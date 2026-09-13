#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    public partial class MainWindowLifetimeTest
    {
        [TestMethod]
        public async Task UpdateCheckRepeatedLoadedBeforeDispatchQueuesOnceAndKeepsFirstRun()
        {
            var probe = new UpdateCheckLoopProbe();
            using var lifetime = new MainWindowLifetime(_ => { });
            var slot = probe.Create(lifetime);
            var calls = new List<string>();
            try
            {
                slot.Start(() => { calls.Add("first"); return Task.CompletedTask; });
                slot.Start(() => { calls.Add("replacement"); return Task.CompletedTask; });

                Assert.AreEqual(1, probe.Posts);
                Assert.AreEqual(1, probe.Pending.Count);
                Assert.AreEqual(0, calls.Count, "Loaded must retain the existing deferred Dispatcher boundary.");
                Assert.IsFalse(slot.Completion.IsCompleted, "The queued Dispatcher operation is still owned.");
            }
            finally
            {
                await probe.DrainAsync();
            }
            CollectionAssert.AreEqual(new[] { "first" }, calls);
            Assert.AreEqual(0, probe.Reported.Count);
        }

        [TestMethod]
        public async Task UpdateCheckRepeatedLoadedWhileRunningStartsOnce()
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var probe = new UpdateCheckLoopProbe { RunBody = () => release.Task };
            using var lifetime = new MainWindowLifetime(_ => { });
            var slot = probe.Create(lifetime);
            slot.Start(probe.Run);
            var completion = slot.Completion;
            var running = probe.Pending.Dequeue()();
            try
            {
                Assert.AreEqual(1, probe.Runs);
                Assert.IsFalse(running.IsCompleted);
                Assert.IsFalse(completion.IsCompleted);

                slot.Start(probe.Run);

                Assert.AreEqual(1, probe.Posts);
                Assert.AreEqual(0, probe.Pending.Count);
                Assert.AreEqual(1, probe.Runs);
                Assert.IsFalse(running.IsCompleted);
            }
            finally
            {
                release.TrySetResult();
                await running.WaitAsync(VirtualDesktopCallbackGuard);
                await probe.DrainAsync();
            }
            await completion.WaitAsync(VirtualDesktopCallbackGuard);
            Assert.AreEqual(0, probe.Reported.Count);
        }

        [TestMethod]
        public async Task UpdateCheckQueuedBeforeDisposeDoesNotStart()
        {
            var probe = new UpdateCheckLoopProbe();
            using var lifetime = new MainWindowLifetime(_ => { });
            var slot = probe.Create(lifetime);
            slot.Start(probe.Run);
            Assert.AreEqual(1, probe.Posts);
            Assert.AreEqual(0, probe.Runs);

            lifetime.Dispose();
            await probe.DrainAsync();

            Assert.AreEqual(0, probe.Runs);
            Assert.AreEqual(0, probe.Reported.Count);
            Assert.IsTrue(lifetime.Completion.IsCompletedSuccessfully);
        }

        [TestMethod]
        public async Task UpdateCheckCapturedAfterDisposeDoesNotPost()
        {
            var probe = new UpdateCheckLoopProbe();
            using var lifetime = new MainWindowLifetime(_ => { });
            var slot = probe.Create(lifetime);
            Action captured = () => slot.Start(probe.Run);
            lifetime.Dispose();
            try
            {
                captured();

                Assert.AreEqual(0, probe.Posts);
                Assert.AreEqual(0, probe.Pending.Count);
            }
            finally
            {
                await probe.DrainAsync();
            }
            Assert.AreEqual(0, probe.Runs);
            Assert.AreEqual(0, probe.Reported.Count);
        }

        [TestMethod]
        public async Task UpdateCheckCompletionAllowsLaterLoadedWithNewRun()
        {
            var probe = new UpdateCheckLoopProbe();
            using var lifetime = new MainWindowLifetime(_ => { });
            var slot = probe.Create(lifetime);
            var calls = new List<string>();

            slot.Start(() => { calls.Add("first"); return Task.CompletedTask; });
            await probe.DrainAsync();
            slot.Start(() => { calls.Add("second"); return Task.CompletedTask; });
            Assert.AreEqual(2, probe.Posts);
            CollectionAssert.AreEqual(new[] { "first" }, calls);
            await probe.DrainAsync();

            CollectionAssert.AreEqual(new[] { "first", "second" }, calls);
            Assert.AreEqual(0, probe.Reported.Count);
        }

        [TestMethod]
        public async Task UpdateCheckPostFailureReleasesSlotAndPreservesException()
        {
            var failure = new InvalidOperationException("Update check Dispatcher post failed.");
            var probe = new UpdateCheckLoopProbe { PostFailure = failure };
            using var lifetime = new MainWindowLifetime(_ => { });
            var slot = probe.Create(lifetime);

            var observed = Assert.ThrowsException<InvalidOperationException>(() => slot.Start(probe.Run));

            Assert.AreSame(failure, observed);
            Assert.AreEqual(1, probe.Posts);
            Assert.AreEqual(0, probe.Pending.Count);
            Assert.AreEqual(0, probe.Runs);
            Assert.AreEqual(0, probe.Reported.Count);
            probe.PostFailure = null;
            slot.Start(probe.Run);
            await probe.DrainAsync();
            Assert.AreEqual(2, probe.Posts);
            Assert.AreEqual(1, probe.Runs);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task UpdateCheckRunFailureIsReportedOnceAndAllowsLaterLoaded(bool synchronousFailure)
        {
            var failure = new ApplicationException("Update check loop failed.");
            var probe = new UpdateCheckLoopProbe
            {
                RunBody = () => synchronousFailure ? throw failure : Task.FromException(failure),
            };
            using var lifetime = new MainWindowLifetime(_ => { });
            var slot = probe.Create(lifetime);

            slot.Start(probe.Run);
            await probe.DrainAsync();

            Assert.AreEqual(1, probe.Runs);
            Assert.AreEqual(1, probe.Reported.Count);
            Assert.AreSame(failure, probe.Reported[0]);
            probe.RunBody = static () => Task.CompletedTask;
            slot.Start(probe.Run);
            await probe.DrainAsync();
            Assert.AreEqual(2, probe.Posts);
            Assert.AreEqual(2, probe.Runs);
            Assert.AreEqual(1, probe.Reported.Count);
        }

        [TestMethod]
        public async Task UpdateCheckReporterFailureRemainsObservableAndReleasesSlot()
        {
            var loopFailure = new ApplicationException("Update check loop failed.");
            var reportFailure = new InvalidOperationException("Update check logger failed.");
            var probe = new UpdateCheckLoopProbe
            {
                RunBody = () => Task.FromException(loopFailure),
                ReportBody = _ => throw reportFailure,
            };
            using var lifetime = new MainWindowLifetime(_ => { });
            var slot = probe.Create(lifetime);
            slot.Start(probe.Run);
            var completion = slot.Completion;

            var observed = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => probe.Pending.Dequeue()().WaitAsync(VirtualDesktopCallbackGuard));
            var completionFailure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => completion.WaitAsync(VirtualDesktopCallbackGuard));

            Assert.AreSame(reportFailure, observed);
            Assert.AreSame(reportFailure, completionFailure);
            Assert.AreEqual(1, probe.Reported.Count);
            Assert.AreSame(loopFailure, probe.Reported[0]);
            probe.RunBody = static () => Task.CompletedTask;
            probe.ReportBody = null;
            slot.Start(probe.Run);
            await probe.DrainAsync();
            Assert.AreEqual(2, probe.Posts);
            Assert.AreEqual(2, probe.Runs);
            Assert.AreEqual(1, probe.Reported.Count);
        }

        [TestMethod]
        public async Task UpdateCheckReporterCancellationPreservesTokenAndReleasesSlot()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var loopFailure = new ApplicationException("Update check loop failed before reporter cancellation.");
            var probe = new UpdateCheckLoopProbe
            {
                RunBody = () => Task.FromException(loopFailure),
                ReportBody = _ => throw new OperationCanceledException(cancellation.Token),
            };
            using var lifetime = new MainWindowLifetime(_ => { });
            var slot = probe.Create(lifetime);
            slot.Start(probe.Run);
            var completion = slot.Completion;
            try
            {
                await probe.Pending.Dequeue()().WaitAsync(VirtualDesktopCallbackGuard);
                Assert.Fail("Reporter cancellation must remain observable from the Dispatcher callback.");
            }
            catch (OperationCanceledException error)
            {
                Assert.AreEqual(cancellation.Token, error.CancellationToken);
            }
            try
            {
                await completion.WaitAsync(VirtualDesktopCallbackGuard);
                Assert.Fail("Reporter cancellation must remain observable from owned completion.");
            }
            catch (OperationCanceledException error)
            {
                Assert.AreEqual(cancellation.Token, error.CancellationToken);
            }
            Assert.IsTrue(completion.IsCanceled);
            Assert.AreEqual(1, probe.Reported.Count);
            Assert.AreSame(loopFailure, probe.Reported[0]);
            probe.RunBody = static () => Task.CompletedTask;
            probe.ReportBody = null;
            slot.Start(probe.Run);
            await probe.DrainAsync();
            Assert.AreEqual(2, probe.Posts);
            Assert.AreEqual(2, probe.Runs);
            Assert.AreEqual(1, probe.Reported.Count);
        }

        [TestMethod]
        public async Task UpdateCheckReporterCancellationAfterShutdownRemainsObservable()
        {
            using var shutdown = new CancellationTokenSource();
            using var reporterCancellation = new CancellationTokenSource();
            reporterCancellation.Cancel();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var loopFailure = new ApplicationException("Update check failed after shutdown started.");
            var probe = new UpdateCheckLoopProbe
            {
                IsShutdownRequested = () => shutdown.IsCancellationRequested,
                RunBody = async () =>
                {
                    entered.TrySetResult();
                    await release.Task;
                },
                ReportBody = _ => throw new OperationCanceledException(reporterCancellation.Token),
            };
            using var lifetime = new MainWindowLifetime(releaseOwned => releaseOwned(shutdown.Cancel));
            var slot = probe.Create(lifetime);
            slot.Start(probe.Run);
            var completion = slot.Completion;
            var running = probe.Pending.Dequeue()();
            try
            {
                await entered.Task.WaitAsync(VirtualDesktopCallbackGuard);
                lifetime.Dispose();
                release.TrySetException(loopFailure);
                try
                {
                    await running.WaitAsync(VirtualDesktopCallbackGuard);
                    Assert.Fail("Reporter cancellation must remain observable after shutdown starts.");
                }
                catch (OperationCanceledException error)
                {
                    Assert.AreEqual(reporterCancellation.Token, error.CancellationToken);
                }
                try
                {
                    await completion.WaitAsync(VirtualDesktopCallbackGuard);
                    Assert.Fail("Owned completion must retain reporter cancellation after shutdown starts.");
                }
                catch (OperationCanceledException error)
                {
                    Assert.AreEqual(reporterCancellation.Token, error.CancellationToken);
                }
                Assert.IsTrue(completion.IsCanceled);
                Assert.AreEqual(1, probe.Reported.Count);
                Assert.AreSame(loopFailure, probe.Reported[0]);
            }
            finally
            {
                shutdown.Cancel();
                release.TrySetException(loopFailure);
                try { await running.WaitAsync(VirtualDesktopCallbackGuard); }
                catch (OperationCanceledException) { }
                lifetime.Dispose();
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task UpdateCheckAbortedPostCompletesOwnershipAndAllowsLaterLoaded(bool canceled)
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var failure = new ApplicationException("Queued update check Dispatcher operation failed.");
            var probe = new UpdateCheckLoopProbe();
            using var lifetime = new MainWindowLifetime(_ => { });
            var slot = probe.Create(lifetime);
            slot.Start(probe.Run);
            var completion = slot.Completion;
            try
            {
                Assert.IsFalse(completion.IsCompleted);
                Assert.AreEqual(0, probe.Runs);
                // Simulate Dispatcher aborting its operation before invoking the
                // queued delegate. No callback is executed or silently retried.
                probe.Pending.Clear();
                if (canceled)
                {
                    probe.PostOperations[0].TrySetCanceled(cancellation.Token);
                    try
                    {
                        await completion.WaitAsync(VirtualDesktopCallbackGuard);
                        Assert.Fail("The aborted operation's cancellation must remain observable.");
                    }
                    catch (OperationCanceledException error)
                    {
                        Assert.AreEqual(cancellation.Token, error.CancellationToken);
                    }
                    Assert.IsTrue(completion.IsCanceled);
                }
                else
                {
                    probe.PostOperations[0].TrySetException(failure);
                    var observed = await Assert.ThrowsExceptionAsync<ApplicationException>(
                        () => completion.WaitAsync(VirtualDesktopCallbackGuard));
                    Assert.AreSame(failure, observed);
                }
                Assert.AreEqual(0, probe.Runs);
                Assert.AreEqual(0, probe.Reported.Count,
                    "An operation failure must not be reported again as a loop-body failure.");

                slot.Start(probe.Run);
                await probe.DrainAsync();
                Assert.AreEqual(2, probe.Posts);
                Assert.AreEqual(1, probe.Runs);
                Assert.IsTrue(slot.Completion.IsCompletedSuccessfully);
            }
            finally
            {
                foreach (var operation in probe.PostOperations) { operation.TrySetResult(); }
                probe.Pending.Clear();
            }
        }

        [TestMethod]
        public async Task UpdateCheckQueuedShutdownCancellationCompletesOwnershipWithoutFailureReport()
        {
            using var shutdown = new CancellationTokenSource();
            var probe = new UpdateCheckLoopProbe
            {
                IsShutdownRequested = () => shutdown.IsCancellationRequested,
            };
            using var lifetime = new MainWindowLifetime(release => release(shutdown.Cancel));
            var slot = probe.Create(lifetime);
            slot.Start(probe.Run);
            var completion = slot.Completion;
            try
            {
                Assert.IsFalse(completion.IsCompleted);
                lifetime.Dispose();
                probe.Pending.Clear();
                probe.PostOperations[0].TrySetCanceled(shutdown.Token);
                await completion.WaitAsync(VirtualDesktopCallbackGuard);

                Assert.IsTrue(completion.IsCompletedSuccessfully);
                Assert.AreEqual(0, probe.Runs);
                Assert.AreEqual(0, probe.Reported.Count);
                slot.Start(probe.Run);
                Assert.AreEqual(1, probe.Posts);
            }
            finally
            {
                foreach (var operation in probe.PostOperations) { operation.TrySetResult(); }
                probe.Pending.Clear();
            }
        }

        [TestMethod]
        public async Task UpdateCheckShutdownCancellationFinishesAdmittedLoopWithoutFailureReport()
        {
            using var shutdown = new CancellationTokenSource();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int exits = 0;
            var probe = new UpdateCheckLoopProbe
            {
                IsShutdownRequested = () => shutdown.IsCancellationRequested,
                RunBody = async () =>
                {
                    using var registration = shutdown.Token.Register(() => release.TrySetCanceled(shutdown.Token));
                    try { await release.Task; }
                    finally { exits++; }
                },
            };
            using var lifetime = new MainWindowLifetime(releaseOwned => releaseOwned(shutdown.Cancel));
            var slot = probe.Create(lifetime);
            slot.Start(probe.Run);
            var running = probe.Pending.Dequeue()();
            try
            {
                Assert.AreEqual(1, probe.Runs);
                Assert.IsFalse(running.IsCompleted);
                Assert.AreEqual(0, exits);

                lifetime.Dispose();
                await running.WaitAsync(VirtualDesktopCallbackGuard);

                Assert.AreEqual(1, exits);
                Assert.AreEqual(0, probe.Reported.Count);
                Assert.AreEqual(0, probe.Pending.Count);
            }
            finally
            {
                shutdown.Cancel();
                await running.WaitAsync(VirtualDesktopCallbackGuard);
            }
        }

        [TestMethod]
        public async Task UpdateCheckAdmittedPostRemainsOwnedAcrossConcurrentShutdown()
        {
            using var postEntered = new ManualResetEventSlim();
            using var releasePost = new ManualResetEventSlim();
            var postReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int dependentReleases = 0;
            LoadedUpdateCheck? slot = null;
            var probe = new UpdateCheckLoopProbe
            {
                PostBody = () =>
                {
                    postEntered.Set();
                    if (!releasePost.Wait(VirtualDesktopCallbackGuard))
                    {
                        throw new TimeoutException("Admitted update-check post was not released.");
                    }
                },
            };
            using var lifetime = new MainWindowLifetime(
                _ => { },
                () => slot!.Completion,
                release => release(() => dependentReleases++));
            slot = probe.Create(lifetime);
            var thread = new Thread(() =>
            {
                try
                {
                    slot.Start(probe.Run);
                    postReturned.TrySetResult();
                }
                catch (Exception error)
                {
                    postReturned.TrySetException(error);
                }
            })
            {
                IsBackground = true,
                Name = "Admitted update-check post",
            };
            thread.Start();
            try
            {
                Assert.IsTrue(postEntered.Wait(VirtualDesktopCallbackGuard), "Update-check post did not start.");
                lifetime.Dispose();

                Assert.IsFalse(lifetime.Completion.IsCompleted);
                Assert.AreEqual(0, dependentReleases);
                Assert.AreEqual(0, probe.Pending.Count);

                releasePost.Set();
                await postReturned.Task.WaitAsync(VirtualDesktopCallbackGuard);
                Assert.AreEqual(1, probe.Pending.Count);
                Assert.IsFalse(lifetime.Completion.IsCompleted);
                await probe.DrainAsync();
                await lifetime.Completion.WaitAsync(VirtualDesktopCallbackGuard);

                Assert.AreEqual(1, dependentReleases);
                Assert.AreEqual(0, probe.Runs);
                Assert.AreEqual(0, probe.Reported.Count);
            }
            finally
            {
                releasePost.Set();
                Assert.IsTrue(thread.Join(VirtualDesktopCallbackGuard), "Update-check post thread did not stop.");
                lifetime.Dispose();
            }
        }

        [TestMethod]
        public async Task UpdateCheckAdmittedRunDelaysDependentShutdownUntilCancellationReturns()
        {
            using var shutdown = new CancellationTokenSource();
            var runEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int dependentReleases = 0;
            LoadedUpdateCheck? slot = null;
            var probe = new UpdateCheckLoopProbe
            {
                IsShutdownRequested = () => shutdown.IsCancellationRequested,
                RunBody = async () =>
                {
                    runEntered.TrySetResult();
                    await releaseRun.Task;
                },
            };
            using var lifetime = new MainWindowLifetime(
                release => release(shutdown.Cancel),
                () => slot!.Completion,
                release => release(() => dependentReleases++));
            slot = probe.Create(lifetime);
            slot.Start(probe.Run);
            var running = probe.Pending.Dequeue()();
            try
            {
                await runEntered.Task.WaitAsync(VirtualDesktopCallbackGuard);
                lifetime.Dispose();

                Assert.IsTrue(shutdown.IsCancellationRequested);
                Assert.IsFalse(running.IsCompleted);
                Assert.IsFalse(lifetime.Completion.IsCompleted);
                Assert.AreEqual(0, dependentReleases);

                releaseRun.TrySetCanceled(shutdown.Token);
                await running.WaitAsync(VirtualDesktopCallbackGuard);
                await lifetime.Completion.WaitAsync(VirtualDesktopCallbackGuard);

                Assert.AreEqual(1, dependentReleases);
                Assert.AreEqual(0, probe.Reported.Count);
            }
            finally
            {
                shutdown.Cancel();
                releaseRun.TrySetCanceled(shutdown.Token);
                await running.WaitAsync(VirtualDesktopCallbackGuard);
                lifetime.Dispose();
            }
        }

        [TestMethod]
        public async Task UpdateCheckUnrelatedCancellationIsReportedAndAllowsLaterLoaded()
        {
            using var unrelated = new CancellationTokenSource();
            unrelated.Cancel();
            var cancellation = new OperationCanceledException(unrelated.Token);
            var probe = new UpdateCheckLoopProbe { RunBody = () => Task.FromException(cancellation) };
            using var lifetime = new MainWindowLifetime(_ => { });
            var slot = probe.Create(lifetime);

            slot.Start(probe.Run);
            await probe.DrainAsync();

            Assert.AreEqual(1, probe.Reported.Count);
            Assert.AreSame(cancellation, probe.Reported[0]);
            Assert.AreEqual(unrelated.Token, ((OperationCanceledException)probe.Reported[0]).CancellationToken);
            probe.RunBody = static () => Task.CompletedTask;
            slot.Start(probe.Run);
            await probe.DrainAsync();
            Assert.AreEqual(2, probe.Runs);
            Assert.AreEqual(1, probe.Reported.Count);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task UpdateCheckReentrantLoadedDuringPostOrRunDoesNotDuplicate(bool duringPost)
        {
            var probe = new UpdateCheckLoopProbe();
            using var lifetime = new MainWindowLifetime(_ => { });
            var slot = probe.Create(lifetime);
            bool reentered = false;
            int nestedRuns = 0;
            void ReenterOnce()
            {
                if (reentered) { return; }
                reentered = true;
                slot.Start(() => { nestedRuns++; return Task.CompletedTask; });
            }
            if (duringPost) { probe.PostBody = ReenterOnce; }
            else { probe.RunBody = () => { ReenterOnce(); return Task.CompletedTask; }; }

            slot.Start(probe.Run);
            await probe.DrainAsync();

            Assert.IsTrue(reentered);
            Assert.AreEqual(1, probe.Posts);
            Assert.AreEqual(1, probe.Runs);
            Assert.AreEqual(0, nestedRuns);
            Assert.AreEqual(0, probe.Reported.Count);
            probe.PostBody = null;
            probe.RunBody = static () => Task.CompletedTask;
            slot.Start(probe.Run);
            await probe.DrainAsync();
            Assert.AreEqual(2, probe.Posts);
            Assert.AreEqual(2, probe.Runs);
        }

        [TestMethod]
        public async Task UpdateCheckLoopLifetimeCounterScenario()
        {
            const int cycles = 100;
            int activePosts = 0;
            int activeRuns = 0;
            int burstPosts = 0;
            int burstRuns = 0;
            int latePosts = 0;
            int lateRuns = 0;
            int completedStarts = 0;
            int ownedReleases = 0;
            int pendingCallbacks = 0;
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                var probe = new UpdateCheckLoopProbe();
                using var lifetime = new MainWindowLifetime(release => release(() => ownedReleases++));
                var slot = probe.Create(lifetime);

                slot.Start(probe.Run);
                completedStarts++;
                await probe.DrainAsync();
                activePosts += probe.Posts;
                activeRuns += probe.Runs;
                Assert.AreEqual(1, probe.Posts);
                Assert.AreEqual(1, probe.Runs);

                int postsBeforeBurst = probe.Posts;
                int runsBeforeBurst = probe.Runs;
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                probe.RunBody = () => release.Task;
                var running = new List<Task>();
                try
                {
                    slot.Start(probe.Run);
                    completedStarts++;
                    slot.Start(probe.Run);
                    completedStarts++;
                    while (probe.Pending.Count != 0) { running.Add(probe.Pending.Dequeue()()); }
                    Assert.IsTrue(running.Count > 0);
                    Assert.IsTrue(running.TrueForAll(task => !task.IsCompleted));

                    slot.Start(probe.Run);
                    completedStarts++;
                    while (probe.Pending.Count != 0) { running.Add(probe.Pending.Dequeue()()); }
                    Assert.IsTrue(running.TrueForAll(task => !task.IsCompleted));
                }
                finally
                {
                    release.TrySetResult();
                    await Task.WhenAll(running).WaitAsync(VirtualDesktopCallbackGuard);
                    await probe.DrainAsync();
                }
                burstPosts += probe.Posts - postsBeforeBurst;
                burstRuns += probe.Runs - runsBeforeBurst;

                probe.RunBody = static () => Task.CompletedTask;
                int postsBeforeClose = probe.Posts;
                int runsBeforeClose = probe.Runs;
                lifetime.Dispose();
                slot.Start(probe.Run);
                completedStarts++;
                await probe.DrainAsync();
                latePosts += probe.Posts - postsBeforeClose;
                lateRuns += probe.Runs - runsBeforeClose;
                pendingCallbacks += probe.Pending.Count;
                Assert.AreEqual(0, probe.Reported.Count);
                Assert.IsTrue(lifetime.Completion.IsCompletedSuccessfully);
            }

            Assert.AreEqual(cycles, activePosts);
            Assert.AreEqual(cycles, activeRuns);
            Assert.IsTrue(burstPosts == cycles || burstPosts == cycles * 3,
                "A comparison variant must consistently coalesce or preserve all three burst starts.");
            Assert.AreEqual(burstPosts, burstRuns);
            Assert.IsTrue(latePosts == 0 || latePosts == cycles,
                "A comparison variant must consistently reject or preserve all late starts.");
            Assert.AreEqual(latePosts, lateRuns);
            // Completed starts count returned Start calls, including coalesced
            // and rejected calls; they do not count executed update checks.
            Assert.AreEqual(cycles * 5, completedStarts);
            Assert.AreEqual(cycles, ownedReleases);
            Assert.AreEqual(0, pendingCallbacks);
            Console.WriteLine($"PERFCOUNTER loaded-update-check-lifetime cycles {cycles}");
            Console.WriteLine($"PERFCOUNTER loaded-update-check-lifetime active-posts {activePosts}");
            Console.WriteLine($"PERFCOUNTER loaded-update-check-lifetime active-runs {activeRuns}");
            Console.WriteLine($"PERFCOUNTER loaded-update-check-lifetime burst-posts {burstPosts}");
            Console.WriteLine($"PERFCOUNTER loaded-update-check-lifetime burst-runs {burstRuns}");
            Console.WriteLine($"PERFCOUNTER loaded-update-check-lifetime late-posts {latePosts}");
            Console.WriteLine($"PERFCOUNTER loaded-update-check-lifetime late-runs {lateRuns}");
            Console.WriteLine($"PERFCOUNTER loaded-update-check-lifetime completed-starts {completedStarts}");
            Console.WriteLine($"PERFCOUNTER loaded-update-check-lifetime owned-releases {ownedReleases}");
            Console.WriteLine($"PERFCOUNTER loaded-update-check-lifetime pending-callbacks {pendingCallbacks}");
        }

        private sealed class UpdateCheckLoopProbe
        {
            internal int Posts;
            internal int Runs;
            internal Exception? PostFailure;
            internal Action? PostBody;
            internal Action<Exception>? ReportBody;
            internal Func<Task> RunBody = static () => Task.CompletedTask;
            internal Func<bool> IsShutdownRequested = static () => false;
            internal readonly Queue<Func<Task>> Pending = new();
            internal readonly List<Exception> Reported = [];
            internal readonly List<TaskCompletionSource> PostOperations = [];
            private LoadedUpdateCheck? m_slot;

            internal LoadedUpdateCheck Create(MainWindowLifetime lifetime)
                => m_slot = new(lifetime, Post, Report, () => IsShutdownRequested());

            internal Task Run()
            {
                Runs++;
                return RunBody();
            }

            internal async Task DrainAsync()
            {
                while (Pending.Count != 0)
                {
                    await Pending.Dequeue()().WaitAsync(VirtualDesktopCallbackGuard);
                }
                if (m_slot != null) { await m_slot.Completion.WaitAsync(VirtualDesktopCallbackGuard); }
            }

            private Task Post(Func<Task> callback)
            {
                Posts++;
                if (PostFailure != null) { throw PostFailure; }
                PostBody?.Invoke();
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                PostOperations.Add(completion);
                Pending.Enqueue(async () =>
                {
                    try
                    {
                        await callback();
                        completion.TrySetResult();
                    }
                    catch (OperationCanceledException error)
                    {
                        completion.TrySetCanceled(error.CancellationToken);
                        throw;
                    }
                    catch (Exception error)
                    {
                        completion.TrySetException(error);
                        throw;
                    }
                });
                return completion.Task;
            }

            private void Report(Exception error)
            {
                Reported.Add(error);
                ReportBody?.Invoke(error);
            }
        }
    }
}
