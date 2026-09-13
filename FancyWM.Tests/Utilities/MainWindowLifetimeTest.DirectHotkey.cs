#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using FancyWM.Models;
using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    public partial class MainWindowLifetimeTest
    {
        [TestMethod]
        public async Task DirectHotkeyCapturedBeforeDisposeRejectsLateAction()
        {
            var probe = DirectHotkeyProbe.WithTilingFailure();
            using var lifetime = new MainWindowLifetime(_ => { });
            Func<Task> captured = () => probe.Invoke(lifetime, BindableAction.RefreshWorkspace);

            lifetime.Dispose();
            await captured().WaitAsync(VirtualDesktopCallbackGuard);

            AssertNoDirectHotkeyEffects(probe);
        }

        [TestMethod]
        public async Task DirectHotkeyRejectsReentrantActionDuringOwnedRelease()
        {
            var probe = DirectHotkeyProbe.WithTilingFailure();
            Task? reentrant = null;
            MainWindowLifetime? lifetime = null;
            lifetime = new MainWindowLifetime(release => release(() =>
                reentrant = probe.Invoke(lifetime!, BindableAction.RefreshWorkspace)));

            lifetime.Dispose();
            Assert.IsNotNull(reentrant);
            await reentrant!.WaitAsync(VirtualDesktopCallbackGuard);

            AssertNoDirectHotkeyEffects(probe);
        }

        [TestMethod]
        public async Task DirectHotkeyRejectsCapturedActionWhileDependentShutdownIsPending()
        {
            var probe = DirectHotkeyProbe.WithTilingFailure();
            var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int dependentReleases = 0;
            var lifetime = new MainWindowLifetime(_ => { }, () => operation.Task,
                release => release(() => dependentReleases++));
            Func<Task> captured = () => probe.Invoke(lifetime, BindableAction.RefreshWorkspace);
            try
            {
                lifetime.Dispose();
                Assert.IsFalse(lifetime.Completion.IsCompleted);
                await captured().WaitAsync(VirtualDesktopCallbackGuard);

                AssertNoDirectHotkeyEffects(probe);
                Assert.AreEqual(0, dependentReleases);
            }
            finally
            {
                operation.TrySetResult();
                lifetime.Dispose();
                await lifetime.Completion.WaitAsync(VirtualDesktopCallbackGuard);
            }
            Assert.AreEqual(1, dependentReleases);
        }

        [TestMethod]
        public async Task DirectHotkeyActiveActionStartsSynchronouslyAndReceivesExactValue()
        {
            var probe = new DirectHotkeyProbe();
            using var lifetime = new MainWindowLifetime(_ => { });

            var running = probe.Invoke(lifetime, BindableAction.MoveToPreviousDisplay);

            Assert.AreEqual(1, probe.Executions);
            Assert.AreEqual(BindableAction.MoveToPreviousDisplay, probe.ObservedAction);
            Assert.AreEqual(0, probe.Presentations);
            Assert.IsTrue(running.IsCompletedSuccessfully);
            await running.WaitAsync(VirtualDesktopCallbackGuard);
            CollectionAssert.AreEqual(new[] { "execute" }, probe.Calls);
        }

        [TestMethod]
        public async Task DirectHotkeyTilingFailurePreservesIdentityFriendlyNameAndAwaitsPresenter()
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failure = new TilingFailedException(TilingError.NoValidPlacementExists);
            var probe = new DirectHotkeyProbe
            {
                ExecutionFailure = failure,
                FriendlyActionName = "move the active window",
                PresentationBody = () => release.Task,
            };
            using var lifetime = new MainWindowLifetime(_ => { });

            var running = probe.Invoke(lifetime, BindableAction.MoveToPreviousDesktop);
            try
            {
                Assert.AreEqual(1, probe.Executions);
                Assert.AreEqual(1, probe.Presentations);
                Assert.AreEqual(0, probe.PresentationCompletions);
                Assert.IsFalse(running.IsCompleted);
                Assert.AreSame(failure, probe.ObservedFailure);
                Assert.AreEqual("move the active window", probe.ObservedFriendlyActionName);
            }
            finally
            {
                release.TrySetResult();
                await running.WaitAsync(VirtualDesktopCallbackGuard);
            }

            Assert.AreEqual(1, probe.PresentationCompletions);
            CollectionAssert.AreEqual(new[] { "execute", "present-start", "present-end" }, probe.Calls);
        }

        [TestMethod]
        public async Task DirectHotkeyNonTilingFailurePreservesIdentityWithoutPresentation()
        {
            var failure = new InvalidOperationException("Direct hotkey action failed.");
            var probe = new DirectHotkeyProbe { ExecutionFailure = failure };
            using var lifetime = new MainWindowLifetime(_ => { });

            var observed = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                probe.Invoke(lifetime, BindableAction.RefreshWorkspace));

            Assert.AreSame(failure, observed);
            Assert.AreEqual(1, probe.Executions);
            Assert.AreEqual(0, probe.Presentations);
            CollectionAssert.AreEqual(new[] { "execute" }, probe.Calls);
        }

        [TestMethod]
        public async Task DirectHotkeyPresenterFailurePreservesIdentity()
        {
            var failure = new ApplicationException("Direct hotkey presentation failed.");
            var probe = DirectHotkeyProbe.WithTilingFailure();
            probe.PresentationBody = () => Task.FromException(failure);
            using var lifetime = new MainWindowLifetime(_ => { });

            var observed = await Assert.ThrowsExceptionAsync<ApplicationException>(() =>
                probe.Invoke(lifetime, BindableAction.RefreshWorkspace));

            Assert.AreSame(failure, observed);
            Assert.AreEqual(1, probe.Executions);
            Assert.AreEqual(1, probe.Presentations);
            Assert.AreEqual(0, probe.PresentationCompletions);
        }

        [TestMethod]
        public async Task DirectHotkeyPresenterCancellationPreservesToken()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var probe = DirectHotkeyProbe.WithTilingFailure();
            probe.PresentationBody = () => Task.FromCanceled(cancellation.Token);
            using var lifetime = new MainWindowLifetime(_ => { });

            try
            {
                await probe.Invoke(lifetime, BindableAction.RefreshWorkspace);
                Assert.Fail("The failure presenter cancellation must remain observable.");
            }
            catch (OperationCanceledException error)
            {
                Assert.AreEqual(cancellation.Token, error.CancellationToken);
            }
            Assert.AreEqual(1, probe.Executions);
            Assert.AreEqual(1, probe.Presentations);
            Assert.AreEqual(0, probe.PresentationCompletions);
        }

        [TestMethod]
        public async Task DirectHotkeyAlreadyAdmittedPresentationFinishesButNewActionIsRejected()
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var admitted = DirectHotkeyProbe.WithTilingFailure();
            admitted.PresentationBody = () => release.Task;
            var late = DirectHotkeyProbe.WithTilingFailure();
            var lifetime = new MainWindowLifetime(_ => { });
            var running = admitted.Invoke(lifetime, BindableAction.RefreshWorkspace);
            try
            {
                Assert.AreEqual(1, admitted.Executions);
                Assert.AreEqual(1, admitted.Presentations);
                lifetime.Dispose();

                await late.Invoke(lifetime, BindableAction.RefreshWorkspace)
                    .WaitAsync(VirtualDesktopCallbackGuard);
                AssertNoDirectHotkeyEffects(late);
                Assert.AreEqual(0, admitted.PresentationCompletions);
            }
            finally
            {
                release.TrySetResult();
                await running.WaitAsync(VirtualDesktopCallbackGuard);
                lifetime.Dispose();
            }
            Assert.AreEqual(1, admitted.PresentationCompletions);
        }

        [TestMethod]
        public async Task DirectHotkeyPreservesOwnedDispatcherAffinityAcrossAwait()
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    var dispatcher = Dispatcher.CurrentDispatcher;
                    ready.TrySetResult(dispatcher);
                    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                    dispatcher.BeginInvoke(new Action(async () =>
                    {
                        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        using var lifetime = new MainWindowLifetime(_ => { });
                        int expectedThread = Environment.CurrentManagedThreadId;
                        var probe = DirectHotkeyProbe.WithTilingFailure();
                        probe.PresentationBody = () => release.Task;
                        try
                        {
                            var running = probe.Invoke(lifetime, BindableAction.RefreshWorkspace);
                            Assert.IsFalse(running.IsCompleted);
                            CollectionAssert.AreEqual(
                                new[] { "execute", "present-start" },
                                probe.Calls);
                            Assert.IsTrue(probe.Threads.TrueForAll(threadId => threadId == expectedThread));
                            Assert.IsTrue(probe.Contexts.TrueForAll(context =>
                                context is DispatcherSynchronizationContext));
                            ThreadPool.QueueUserWorkItem(_ => release.TrySetResult());
                            await running.WaitAsync(VirtualDesktopCallbackGuard);
                            Assert.AreEqual(expectedThread, Environment.CurrentManagedThreadId);
                            CollectionAssert.AreEqual(
                                new[] { "execute", "present-start", "present-end" },
                                probe.Calls);
                            Assert.IsTrue(probe.Threads.TrueForAll(threadId => threadId == expectedThread));
                            Assert.IsTrue(probe.Contexts.TrueForAll(context =>
                                context is DispatcherSynchronizationContext));
                            completed.TrySetResult();
                        }
                        catch (Exception error)
                        {
                            completed.TrySetException(error);
                        }
                        finally
                        {
                            release.TrySetResult();
                            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                        }
                    }));
                    Dispatcher.Run();
                }
                catch (Exception error)
                {
                    ready.TrySetException(error);
                    completed.TrySetException(error);
                }
            })
            {
                IsBackground = true,
                Name = "Direct hotkey Dispatcher affinity",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            try
            {
                await completed.Task.WaitAsync(VirtualDesktopCallbackGuard);
            }
            finally
            {
                if (ready.Task.IsCompletedSuccessfully)
                {
                    ready.Task.Result.BeginInvokeShutdown(DispatcherPriority.Send);
                }
                Assert.IsTrue(thread.Join(VirtualDesktopCallbackGuard),
                    "Owned Dispatcher thread did not stop.");
            }
        }

        [TestMethod]
        public async Task DirectHotkeyLifetimeCounterScenario()
        {
            const int cycles = 100;
            int activeExecutions = 0;
            int activePresentations = 0;
            int lateExecutions = 0;
            int latePresentations = 0;
            int completedInvocations = 0;
            int ownedReleases = 0;
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                var activeProbe = DirectHotkeyProbe.WithTilingFailure();
                using var active = new MainWindowLifetime(_ => { });
                await activeProbe.Invoke(active, BindableAction.RefreshWorkspace)
                    .WaitAsync(VirtualDesktopCallbackGuard);
                activeExecutions += activeProbe.Executions;
                activePresentations += activeProbe.Presentations;
                completedInvocations++;
                active.Dispose();

                var lateProbe = DirectHotkeyProbe.WithTilingFailure();
                using var closing = new MainWindowLifetime(release =>
                    release(() => ownedReleases++));
                Func<Task> captured = () => lateProbe.Invoke(
                    closing,
                    BindableAction.RefreshWorkspace);
                closing.Dispose();
                await captured().WaitAsync(VirtualDesktopCallbackGuard);
                lateExecutions += lateProbe.Executions;
                latePresentations += lateProbe.Presentations;
                completedInvocations++;
            }

            Assert.AreEqual(cycles, activeExecutions);
            Assert.AreEqual(cycles, activePresentations);
            Assert.IsTrue(lateExecutions == 0 || lateExecutions == cycles,
                "A comparison variant must consistently reject or preserve all late actions.");
            Assert.AreEqual(lateExecutions, latePresentations);
            Assert.AreEqual(cycles * 2, completedInvocations);
            Assert.AreEqual(cycles, ownedReleases);
            Console.WriteLine($"PERFCOUNTER direct-hotkey-lifetime cycles {cycles}");
            Console.WriteLine($"PERFCOUNTER direct-hotkey-lifetime active-executions {activeExecutions}");
            Console.WriteLine($"PERFCOUNTER direct-hotkey-lifetime active-presentations {activePresentations}");
            Console.WriteLine($"PERFCOUNTER direct-hotkey-lifetime late-executions {lateExecutions}");
            Console.WriteLine($"PERFCOUNTER direct-hotkey-lifetime late-presentations {latePresentations}");
            Console.WriteLine($"PERFCOUNTER direct-hotkey-lifetime completed-invocations {completedInvocations}");
            Console.WriteLine($"PERFCOUNTER direct-hotkey-lifetime owned-releases {ownedReleases}");
        }

        private static void AssertNoDirectHotkeyEffects(DirectHotkeyProbe probe)
        {
            Assert.AreEqual(0, probe.Executions);
            Assert.AreEqual(0, probe.Presentations);
            Assert.AreEqual(0, probe.PresentationCompletions);
            Assert.AreEqual(0, probe.Calls.Count);
            Assert.AreEqual(0, probe.Threads.Count);
            Assert.AreEqual(0, probe.Contexts.Count);
            Assert.IsNull(probe.ObservedAction);
            Assert.IsNull(probe.ObservedFailure);
            Assert.IsNull(probe.ObservedFriendlyActionName);
        }

        private sealed class DirectHotkeyProbe
        {
            internal int Executions;
            internal int Presentations;
            internal int PresentationCompletions;
            internal BindableAction? ObservedAction;
            internal TilingFailedException? ObservedFailure;
            internal string? ObservedFriendlyActionName;
            internal string? FriendlyActionName;
            internal Exception? ExecutionFailure;
            internal Func<Task> PresentationBody = static () => Task.CompletedTask;
            internal readonly List<string> Calls = [];
            internal readonly List<int> Threads = [];
            internal readonly List<SynchronizationContext?> Contexts = [];

            internal static DirectHotkeyProbe WithTilingFailure()
                => new()
                {
                    FriendlyActionName = "refresh the workspace",
                    ExecutionFailure = new TilingFailedException(TilingError.Failed),
                };

            internal Task Invoke(MainWindowLifetime lifetime, BindableAction action)
                => MainWindow.HandleDirectHotkeyPressedAsync(
                    lifetime,
                    action,
                    Execute,
                    PresentFailureAsync);

            private void Execute(BindableAction action, ref string? friendlyActionName)
            {
                Executions++;
                ObservedAction = action;
                friendlyActionName = FriendlyActionName;
                Record("execute");
                if (ExecutionFailure != null)
                {
                    throw ExecutionFailure;
                }
            }

            private async Task PresentFailureAsync(
                TilingFailedException failure,
                string? friendlyActionName)
            {
                Presentations++;
                ObservedFailure = failure;
                ObservedFriendlyActionName = friendlyActionName;
                Record("present-start");
                await PresentationBody();
                PresentationCompletions++;
                Record("present-end");
            }

            private void Record(string call)
            {
                Calls.Add(call);
                Threads.Add(Environment.CurrentManagedThreadId);
                Contexts.Add(SynchronizationContext.Current);
            }
        }
    }
}
