#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    public partial class MainWindowLifetimeTest
    {
        [TestMethod]
        public async Task TilingNotificationCapturedBeforeDisposeRejectsLateCallback()
        {
            var probe = new TilingNotificationProbe();
            using var lifetime = new MainWindowLifetime(_ => { });
            Func<Task> captured = () => probe.Invoke(lifetime);

            lifetime.Dispose();
            await captured().WaitAsync(VirtualDesktopCallbackGuard);

            AssertNoTilingNotificationEffects(probe);
        }

        [TestMethod]
        public async Task TilingNotificationRejectsReentrantCallbackDuringOwnedRelease()
        {
            var probe = new TilingNotificationProbe();
            Task? reentrant = null;
            MainWindowLifetime? lifetime = null;
            lifetime = new MainWindowLifetime(release => release(() => reentrant = probe.Invoke(lifetime!)));

            lifetime.Dispose();
            Assert.IsNotNull(reentrant);
            await reentrant!.WaitAsync(VirtualDesktopCallbackGuard);

            AssertNoTilingNotificationEffects(probe);
        }

        [TestMethod]
        public async Task TilingNotificationRejectsCapturedCallbackWhileDependentShutdownIsPending()
        {
            var probe = new TilingNotificationProbe();
            var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int dependentReleases = 0;
            var lifetime = new MainWindowLifetime(_ => { }, () => operation.Task,
                release => release(() => dependentReleases++));
            Func<Task> captured = () => probe.Invoke(lifetime);
            try
            {
                lifetime.Dispose();
                Assert.IsFalse(lifetime.Completion.IsCompleted);
                await captured().WaitAsync(VirtualDesktopCallbackGuard);

                AssertNoTilingNotificationEffects(probe);
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
        public async Task TilingNotificationActiveCallbackStartsSynchronouslyAndIsAwaitedOnce()
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var probe = new TilingNotificationProbe { Body = () => release.Task };
            using var lifetime = new MainWindowLifetime(_ => { });

            var running = probe.Invoke(lifetime);
            try
            {
                Assert.AreEqual(1, probe.Callbacks);
                Assert.AreEqual(0, probe.CallbackCompletions);
                Assert.IsFalse(running.IsCompleted);
            }
            finally
            {
                release.TrySetResult();
                await running.WaitAsync(VirtualDesktopCallbackGuard);
            }

            Assert.AreEqual(1, probe.Callbacks);
            Assert.AreEqual(1, probe.CallbackCompletions);
        }

        [TestMethod]
        public void TilingNotificationPreservesSynchronousCallbackFailureIdentity()
        {
            var failure = new InvalidOperationException("Tiling notification failed synchronously.");
            using var lifetime = new MainWindowLifetime(_ => { });

            var observed = Assert.ThrowsException<InvalidOperationException>(() =>
                MainWindow.HandleTilingNotificationAsync(lifetime, () => throw failure));

            Assert.AreSame(failure, observed);
        }

        [TestMethod]
        public async Task TilingNotificationPreservesAsynchronousCallbackFailureIdentity()
        {
            var failure = new ApplicationException("Tiling notification failed asynchronously.");
            using var lifetime = new MainWindowLifetime(_ => { });

            var observed = await Assert.ThrowsExceptionAsync<ApplicationException>(() =>
                MainWindow.HandleTilingNotificationAsync(lifetime, () => Task.FromException(failure)));

            Assert.AreSame(failure, observed);
        }

        [TestMethod]
        public async Task TilingNotificationPreservesCallbackCancellationToken()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            using var lifetime = new MainWindowLifetime(_ => { });

            try
            {
                await MainWindow.HandleTilingNotificationAsync(
                    lifetime,
                    () => Task.FromCanceled(cancellation.Token));
                Assert.Fail("The callback cancellation must remain observable to its caller.");
            }
            catch (OperationCanceledException error)
            {
                Assert.AreEqual(cancellation.Token, error.CancellationToken);
            }
        }

        [TestMethod]
        public async Task TilingNotificationAlreadyAdmittedCallbackFinishesButNewCallbackIsRejected()
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var admitted = new TilingNotificationProbe { Body = () => release.Task };
            var late = new TilingNotificationProbe();
            var lifetime = new MainWindowLifetime(_ => { });
            var running = admitted.Invoke(lifetime);
            try
            {
                Assert.AreEqual(1, admitted.Callbacks);
                lifetime.Dispose();

                await late.Invoke(lifetime).WaitAsync(VirtualDesktopCallbackGuard);
                AssertNoTilingNotificationEffects(late);
                Assert.AreEqual(0, admitted.CallbackCompletions);
            }
            finally
            {
                release.TrySetResult();
                await running.WaitAsync(VirtualDesktopCallbackGuard);
                lifetime.Dispose();
            }
            Assert.AreEqual(1, admitted.CallbackCompletions);
        }

        [TestMethod]
        public async Task TilingNotificationPreservesOwnedDispatcherAffinityAcrossAwait()
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
                        try
                        {
                            var probe = new TilingNotificationProbe
                            {
                                Body = async () =>
                                {
                                    Assert.AreEqual(expectedThread, Environment.CurrentManagedThreadId);
                                    Assert.IsInstanceOfType(SynchronizationContext.Current, typeof(DispatcherSynchronizationContext));
                                    await release.Task;
                                    Assert.AreEqual(expectedThread, Environment.CurrentManagedThreadId);
                                    Assert.IsInstanceOfType(SynchronizationContext.Current, typeof(DispatcherSynchronizationContext));
                                }
                            };
                            var running = probe.Invoke(lifetime);
                            Assert.IsFalse(running.IsCompleted);
                            Assert.AreEqual(1, probe.Callbacks);
                            Assert.AreEqual(expectedThread, probe.Threads[0]);
                            ThreadPool.QueueUserWorkItem(_ => release.TrySetResult());
                            await running.WaitAsync(VirtualDesktopCallbackGuard);
                            Assert.AreEqual(expectedThread, Environment.CurrentManagedThreadId);
                            Assert.AreEqual(1, probe.CallbackCompletions);
                            CollectionAssert.AreEqual(new[] { expectedThread, expectedThread }, probe.Threads);
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
                Name = "Tiling notification Dispatcher affinity"
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
                Assert.IsTrue(thread.Join(VirtualDesktopCallbackGuard), "Owned Dispatcher thread did not stop.");
            }
        }

        [TestMethod]
        public async Task TilingNotificationLifetimeCounterScenario()
        {
            const int cycles = 100;
            int activeCallbacks = 0;
            int lateCallbacks = 0;
            int completedInvocations = 0;
            int ownedReleases = 0;
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                var activeProbe = new TilingNotificationProbe();
                using var active = new MainWindowLifetime(_ => { });
                await activeProbe.Invoke(active).WaitAsync(VirtualDesktopCallbackGuard);
                activeCallbacks += activeProbe.Callbacks;
                completedInvocations++;
                active.Dispose();

                var lateProbe = new TilingNotificationProbe();
                using var closing = new MainWindowLifetime(release => release(() => ownedReleases++));
                Func<Task> captured = () => lateProbe.Invoke(closing);
                closing.Dispose();
                await captured().WaitAsync(VirtualDesktopCallbackGuard);
                lateCallbacks += lateProbe.Callbacks;
                completedInvocations++;
            }

            Assert.AreEqual(cycles, activeCallbacks);
            Assert.IsTrue(lateCallbacks == 0 || lateCallbacks == cycles,
                "A comparison variant must consistently reject all late callbacks or preserve all old callbacks.");
            Assert.AreEqual(cycles * 2, completedInvocations);
            Assert.AreEqual(cycles, ownedReleases);
            Console.WriteLine($"PERFCOUNTER tiling-notification-lifetime cycles {cycles}");
            Console.WriteLine($"PERFCOUNTER tiling-notification-lifetime active-callbacks {activeCallbacks}");
            Console.WriteLine($"PERFCOUNTER tiling-notification-lifetime late-callbacks {lateCallbacks}");
            Console.WriteLine($"PERFCOUNTER tiling-notification-lifetime completed-invocations {completedInvocations}");
            Console.WriteLine($"PERFCOUNTER tiling-notification-lifetime owned-releases {ownedReleases}");
        }

        private static void AssertNoTilingNotificationEffects(TilingNotificationProbe probe)
        {
            Assert.AreEqual(0, probe.Callbacks);
            Assert.AreEqual(0, probe.CallbackCompletions);
            Assert.AreEqual(0, probe.Threads.Count);
            Assert.AreEqual(0, probe.Contexts.Count);
        }

        private sealed class TilingNotificationProbe
        {
            internal int Callbacks;
            internal int CallbackCompletions;
            internal readonly List<int> Threads = [];
            internal readonly List<SynchronizationContext?> Contexts = [];
            internal Func<Task> Body = static () => Task.CompletedTask;

            internal Task Invoke(MainWindowLifetime lifetime)
                => MainWindow.HandleTilingNotificationAsync(lifetime, async () =>
                {
                    Callbacks++;
                    RecordContext();
                    await Body();
                    CallbackCompletions++;
                    RecordContext();
                });

            private void RecordContext()
            {
                Threads.Add(Environment.CurrentManagedThreadId);
                Contexts.Add(SynchronizationContext.Current);
            }
        }
    }
}
