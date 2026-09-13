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
        private static readonly TimeSpan VirtualDesktopCallbackGuard = TimeSpan.FromSeconds(10);
        private static readonly string[] VirtualDesktopCallbackStages = ["log", "previous", "probe", "name", "toast"];

        [TestMethod]
        public async Task VirtualDesktopChangedCapturedBeforeDisposeRejectsLateStateAndDesktopReads()
        {
            var probe = new VirtualDesktopCallbackProbe();
            using var lifetime = new MainWindowLifetime(_ => { });
            Func<Task> captured = () => probe.Invoke(lifetime);

            lifetime.Dispose();
            await captured().WaitAsync(VirtualDesktopCallbackGuard);

            AssertNoVirtualDesktopEffects(probe);
        }

        [TestMethod]
        public async Task VirtualDesktopChangedRejectsReentrantCallbackDuringOwnedRelease()
        {
            var probe = new VirtualDesktopCallbackProbe();
            Task? reentrant = null;
            MainWindowLifetime? lifetime = null;
            lifetime = new MainWindowLifetime(release => release(() => reentrant = probe.Invoke(lifetime!)));

            lifetime.Dispose();
            Assert.IsNotNull(reentrant);
            await reentrant!.WaitAsync(VirtualDesktopCallbackGuard);

            AssertNoVirtualDesktopEffects(probe);
        }

        [TestMethod]
        public async Task VirtualDesktopChangedRejectsCapturedCallbackWhileDependentShutdownIsPending()
        {
            var probe = new VirtualDesktopCallbackProbe();
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

                AssertNoVirtualDesktopEffects(probe);
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

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task VirtualDesktopChangedPreservesExactOrderPreviousIdentityAndAwaitsToast(bool oldDesktopAbsent)
        {
            var probe = new VirtualDesktopCallbackProbe(oldDesktopAbsent);
            var toast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            probe.ShowToast = () => toast.Task;
            using var lifetime = new MainWindowLifetime(_ => { });
            var running = probe.Invoke(lifetime);
            try
            {
                Assert.IsFalse(running.IsCompleted, "The callback must await its existing toast operation.");
                AssertActiveVirtualDesktopEffects(probe);
            }
            finally
            {
                toast.TrySetResult();
                await running.WaitAsync(VirtualDesktopCallbackGuard);
            }
            AssertActiveVirtualDesktopEffects(probe);
        }

        [TestMethod]
        public async Task VirtualDesktopChangedNativeTooltipSkipsNameAndToast()
        {
            var probe = new VirtualDesktopCallbackProbe
            {
                HasTooltip = true,
                FailureStage = "name",
                Failure = new InvalidOperationException("Desktop Name must remain unread when Explorer supplies the tooltip.")
            };
            using var lifetime = new MainWindowLifetime(_ => { });

            await probe.Invoke(lifetime).WaitAsync(VirtualDesktopCallbackGuard);

            CollectionAssert.AreEqual(new[] { "log", "previous", "probe" }, probe.Calls);
            Assert.AreSame(probe.OldDesktop, probe.Previous);
            Assert.IsNull(probe.LastToast);
        }

        [DataTestMethod]
        [DataRow("log")]
        [DataRow("previous")]
        [DataRow("probe")]
        [DataRow("name")]
        [DataRow("toast")]
        public async Task VirtualDesktopChangedPreservesSynchronousFailureIdentityAndStopsLaterStages(string stage)
        {
            var failure = new InvalidOperationException($"Virtual desktop {stage} failed.");
            var probe = new VirtualDesktopCallbackProbe { FailureStage = stage, Failure = failure };
            using var lifetime = new MainWindowLifetime(_ => { });

            var observed = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => probe.Invoke(lifetime).WaitAsync(VirtualDesktopCallbackGuard));

            Assert.AreSame(failure, observed);
            int lastStage = Array.IndexOf(VirtualDesktopCallbackStages, stage);
            CollectionAssert.AreEqual(VirtualDesktopCallbackStages[..(lastStage + 1)], probe.Calls);
            Assert.AreSame(lastStage <= 1 ? probe.InitialPrevious : probe.OldDesktop, probe.Previous);
            Assert.IsNull(probe.LastToast);
        }

        [TestMethod]
        public async Task VirtualDesktopChangedPreservesAsynchronousToastFailureIdentity()
        {
            var failure = new ApplicationException("Virtual desktop toast failed asynchronously.");
            var toast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var probe = new VirtualDesktopCallbackProbe { ShowToast = () => toast.Task };
            using var lifetime = new MainWindowLifetime(_ => { });
            var running = probe.Invoke(lifetime);
            try
            {
                Assert.IsFalse(running.IsCompleted);
                AssertActiveVirtualDesktopEffects(probe);
            }
            finally
            {
                toast.TrySetException(failure);
            }

            var observed = await Assert.ThrowsExceptionAsync<ApplicationException>(
                () => running.WaitAsync(VirtualDesktopCallbackGuard));
            Assert.AreSame(failure, observed);
            AssertActiveVirtualDesktopEffects(probe);
        }

        [TestMethod]
        public async Task VirtualDesktopChangedPreservesOwnedDispatcherAffinity()
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
                        var toast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        using var lifetime = new MainWindowLifetime(_ => { });
                        try
                        {
                            int expectedThread = Environment.CurrentManagedThreadId;
                            var probe = new VirtualDesktopCallbackProbe
                            {
                                ShowToast = async () =>
                                {
                                    Assert.AreEqual(expectedThread, Environment.CurrentManagedThreadId);
                                    await toast.Task;
                                    Assert.AreEqual(expectedThread, Environment.CurrentManagedThreadId);
                                    Assert.IsInstanceOfType(SynchronizationContext.Current, typeof(DispatcherSynchronizationContext));
                                }
                            };
                            var running = probe.Invoke(lifetime);
                            Assert.IsFalse(running.IsCompleted);
                            AssertActiveVirtualDesktopEffects(probe);
                            foreach (int callbackThread in probe.Threads)
                            {
                                Assert.AreEqual(expectedThread, callbackThread);
                            }
                            ThreadPool.QueueUserWorkItem(_ => toast.TrySetResult());
                            await running.WaitAsync(VirtualDesktopCallbackGuard);
                            Assert.AreEqual(expectedThread, Environment.CurrentManagedThreadId);
                            AssertActiveVirtualDesktopEffects(probe);
                            completed.TrySetResult();
                        }
                        catch (Exception error)
                        {
                            completed.TrySetException(error);
                        }
                        finally
                        {
                            toast.TrySetResult();
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
                Name = "Virtual desktop callback Dispatcher affinity"
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
        public async Task VirtualDesktopCallbackLifetimeCounterScenario()
        {
            const int cycles = 100;
            int completedRuns = 0;
            var activeCounts = new int[VirtualDesktopCallbackStages.Length];
            var lateCounts = new int[VirtualDesktopCallbackStages.Length];
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                var activeProbe = new VirtualDesktopCallbackProbe();
                using var active = new MainWindowLifetime(_ => { });
                await activeProbe.Invoke(active).WaitAsync(VirtualDesktopCallbackGuard);
                completedRuns++;
                AssertActiveVirtualDesktopEffects(activeProbe);
                active.Dispose();

                var lateProbe = new VirtualDesktopCallbackProbe();
                using var closing = new MainWindowLifetime(_ => { });
                Func<Task> captured = () => lateProbe.Invoke(closing);
                closing.Dispose();
                await captured().WaitAsync(VirtualDesktopCallbackGuard);
                completedRuns++;
                if (lateProbe.Calls.Count == 0)
                {
                    AssertNoVirtualDesktopEffects(lateProbe);
                }
                else
                {
                    // The same fixture measures the sealed old-order seam and
                    // the candidate. Separate regression tests require rejection.
                    AssertActiveVirtualDesktopEffects(lateProbe);
                }

                for (int stage = 0; stage < VirtualDesktopCallbackStages.Length; stage++)
                {
                    activeCounts[stage] += activeProbe.Count(VirtualDesktopCallbackStages[stage]);
                    lateCounts[stage] += lateProbe.Count(VirtualDesktopCallbackStages[stage]);
                }
            }

            Assert.AreEqual(cycles * 2, completedRuns);
            Assert.IsTrue(lateCounts[0] == 0 || lateCounts[0] == cycles,
                "A comparison variant must consistently reject all late callbacks or preserve all old callbacks.");
            Console.WriteLine($"PERFCOUNTER virtual-desktop-callback-lifetime cycles {cycles}");
            for (int stage = 0; stage < VirtualDesktopCallbackStages.Length; stage++)
            {
                Assert.AreEqual(cycles, activeCounts[stage]);
                Assert.AreEqual(lateCounts[0], lateCounts[stage]);
            }
            string[] metrics = ["state-writes", "feature-probes", "name-reads", "toasts"];
            for (int metric = 0; metric < metrics.Length; metric++)
            {
                Console.WriteLine($"PERFCOUNTER virtual-desktop-callback-lifetime active-{metrics[metric]} {activeCounts[metric + 1]}");
                Console.WriteLine($"PERFCOUNTER virtual-desktop-callback-lifetime late-{metrics[metric]} {lateCounts[metric + 1]}");
            }
            Console.WriteLine($"PERFCOUNTER virtual-desktop-callback-lifetime completed-callbacks {completedRuns}");
        }

        private static void AssertNoVirtualDesktopEffects(VirtualDesktopCallbackProbe probe)
        {
            Assert.AreEqual(0, probe.Calls.Count, "Late callbacks must not log, change previous desktop, probe Explorer, read Name, or start a toast.");
            Assert.AreSame(probe.InitialPrevious, probe.Previous);
            Assert.IsNull(probe.LastToast);
        }

        private static void AssertActiveVirtualDesktopEffects(VirtualDesktopCallbackProbe probe)
        {
            CollectionAssert.AreEqual(VirtualDesktopCallbackStages, probe.Calls);
            Assert.AreSame(probe.OldDesktop, probe.Previous);
            Assert.AreEqual("Desktop 2", probe.LastToast);
        }

        private sealed class VirtualDesktopCallbackProbe
        {
            internal readonly object InitialPrevious = new();
            internal readonly object? OldDesktop;
            internal readonly List<string> Calls = [];
            internal readonly List<int> Threads = [];
            internal object? Previous;
            internal bool HasTooltip;
            internal string? LastToast;
            internal string? FailureStage;
            internal Exception? Failure;
            internal Func<Task> ShowToast = static () => Task.CompletedTask;

            internal VirtualDesktopCallbackProbe(bool oldDesktopAbsent = false)
            {
                Previous = InitialPrevious;
                OldDesktop = oldDesktopAbsent ? null : new object();
            }

            internal Task Invoke(MainWindowLifetime lifetime)
                => MainWindow.HandleVirtualDesktopChangedAsync(lifetime,
                    () => Record("log"),
                    () =>
                    {
                        Record("previous");
                        Previous = OldDesktop;
                    },
                    () =>
                    {
                        Record("probe");
                        return HasTooltip;
                    },
                    () =>
                    {
                        Record("name");
                        return "Desktop 2";
                    },
                    name =>
                    {
                        Record("toast");
                        LastToast = name;
                        return ShowToast();
                    });

            internal int Count(string stage)
            {
                int count = 0;
                foreach (string call in Calls)
                {
                    if (call == stage) { count++; }
                }
                return count;
            }

            private void Record(string stage)
            {
                Calls.Add(stage);
                Threads.Add(Environment.CurrentManagedThreadId);
                if (FailureStage == stage) { throw Failure!; }
            }
        }
    }
}
