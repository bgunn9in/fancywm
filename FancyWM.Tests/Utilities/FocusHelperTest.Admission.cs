#nullable enable
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    public partial class FocusHelperTest
    {
        [DataTestMethod]
        [DataRow("queue")]
        [DataRow("focus:1")]
        [DataRow("detach:11:22")]
        public async Task LatestPendingFallbackRunsOnExistingWorkerAfterOlderCompletes(string blockedStage)
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var workers = new ConcurrentQueue<Thread>();
            var requests = AdmissionRequests(workers);
            var older = new SupersessionNative(true)
            {
                OnCall = call =>
                {
                    if (call != blockedStage) return;
                    entered.Set();
                    WaitAdmissionBarrier(release);
                },
            };
            var newer = new SupersessionNative(true);
            Task<bool> first = requests.Enqueue(requests.Begin(), new(1), older);
            Task<bool>? last = null;
            try
            {
                WaitAdmissionBarrier(entered);
                last = requests.Enqueue(requests.Begin(), new(2), newer);
                Assert.AreEqual(1, workers.Count);
                Assert.IsFalse(first.IsCompleted, "Entered cleanup must finish before the worker publishes completion.");
                Assert.IsFalse(last.IsCompleted);
                Assert.IsNull(newer.Worker);
                release.Set();
                Assert.IsFalse(await first.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.IsTrue(await last.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally
            {
                release.Set();
                await first.WaitAsync(TimeSpan.FromSeconds(5));
                if (last != null) await last.WaitAsync(TimeSpan.FromSeconds(5));
                JoinAdmissionWorkers(workers);
            }
            Assert.AreEqual(1, workers.Count);
            Assert.AreSame(older.Worker, newer.Worker);
            Assert.AreEqual(0, older.OwnedAttachments);
            Assert.AreEqual(older.AttachmentCalls, older.DetachmentCalls);
            Assert.AreEqual(blockedStage == "queue" ? 0 : 1, older.FocusAttempts);
            Assert.AreEqual(0, older.AltPresses);
            CollectionAssert.AreEqual(new[]
            {
                "queue", "thread", "foreground", "attach:11:22", "focus:2", "detach:11:22",
            }, newer.Calls);
            AssertCompletedAttachment(newer);
        }

        [DataTestMethod]
        [DataRow("replaced")]
        [DataRow("expired")]
        [DataRow("immediate-success")]
        public async Task RemovedPendingFallbackCompletesBeforeBlockedWorkerReturnsAndNeverRuns(string removal)
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var workers = new ConcurrentQueue<Thread>();
            var requests = AdmissionRequests(workers);
            var older = new SupersessionNative(true)
            {
                OnCall = call =>
                {
                    if (call != "queue") return;
                    entered.Set();
                    WaitAdmissionBarrier(release);
                },
            };
            var removed = new SupersessionNative(true);
            var newest = new SupersessionNative(true);
            Task<bool> first = requests.Enqueue(requests.Begin(), new(1), older);
            Task<bool>? pending = null;
            Task<bool>? last = null;
            try
            {
                WaitAdmissionBarrier(entered);
                object pendingToken = requests.Begin();
                pending = requests.Enqueue(pendingToken, new(2), removed);
                Assert.IsFalse(pending.IsCompleted);
                if (removal == "replaced")
                {
                    last = requests.Enqueue(requests.Begin(), new(3), newest);
                }
                else if (removal == "expired")
                {
                    requests.Expire(pendingToken);
                }
                else
                {
                    Assert.IsTrue(FocusHelper.ForceActivate(new(3), newest, requests));
                }
                Assert.IsFalse(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.IsFalse(release.IsSet);
                Assert.IsFalse(first.IsCompleted);
                Assert.AreEqual(1, workers.Count);
                Assert.IsNull(removed.Worker);
                release.Set();
                Assert.IsFalse(await first.WaitAsync(TimeSpan.FromSeconds(5)));
                if (last != null) Assert.IsTrue(await last.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally
            {
                release.Set();
                await first.WaitAsync(TimeSpan.FromSeconds(5));
                if (pending != null) await pending.WaitAsync(TimeSpan.FromSeconds(5));
                if (last != null) await last.WaitAsync(TimeSpan.FromSeconds(5));
                JoinAdmissionWorkers(workers);
            }
            Assert.AreEqual(0, removed.Calls.Length);
            Assert.AreEqual(0, older.FocusAttempts);
            Assert.AreEqual(0, older.OwnedAttachments);
            Assert.AreEqual(removal == "expired" ? 0 : 1, newest.FocusAttempts);
            Assert.AreEqual(1, workers.Count);
        }

        [TestMethod]
        public async Task OlderCallerTimeoutCannotExpireNewerPendingFallback()
        {
            using var entered = new ManualResetEventSlim();
            using var releaseWorker = new ManualResetEventSlim();
            using var olderWait = new ManualResetEventSlim();
            using var newerWait = new ManualResetEventSlim();
            using var expireOlder = new ManualResetEventSlim();
            var workers = new ConcurrentQueue<Thread>();
            int waits = 0;
            var requests = new FocusHelper.RequestSequence(task =>
            {
                if (Interlocked.Increment(ref waits) == 1)
                {
                    olderWait.Set();
                    WaitAdmissionBarrier(expireOlder);
                    return false;
                }
                newerWait.Set();
                return task.Wait(TimeSpan.FromSeconds(5));
            }, thread =>
            {
                workers.Enqueue(thread);
                thread.Start();
            });
            var older = new SupersessionNative(false, true)
            {
                OnCall = call =>
                {
                    if (call != "queue") return;
                    entered.Set();
                    WaitAdmissionBarrier(releaseWorker);
                },
            };
            var newer = new SupersessionNative(false, true);
            Task<bool> first = Task.Run(() => FocusHelper.ForceActivate(new(1), older, requests));
            Task<bool>? last = null;
            try
            {
                WaitAdmissionBarrier(entered);
                WaitAdmissionBarrier(olderWait);
                last = Task.Run(() => FocusHelper.ForceActivate(new(2), newer, requests));
                WaitAdmissionBarrier(newerWait);
                expireOlder.Set();
                Assert.IsFalse(await first.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.IsFalse(last.IsCompleted, "Only the old request expires at this controlled boundary.");
                Assert.IsNull(newer.Worker);
                Assert.AreEqual(1, workers.Count);
                releaseWorker.Set();
                Assert.IsTrue(await last.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally
            {
                expireOlder.Set();
                releaseWorker.Set();
                await first.WaitAsync(TimeSpan.FromSeconds(5));
                if (last != null) await last.WaitAsync(TimeSpan.FromSeconds(5));
                JoinAdmissionWorkers(workers);
            }
            Assert.AreEqual(1, older.FocusAttempts);
            Assert.AreEqual(0, older.AttachmentCalls);
            Assert.AreEqual(2, newer.FocusAttempts);
            Assert.AreSame(older.Worker, newer.Worker);
            AssertCompletedAttachment(newer);
        }

        [TestMethod]
        public void ReentrantFallbackWithNewerImmediateFailureExpiresWithoutSelfDeadlock()
        {
            var workers = new ConcurrentQueue<Thread>();
            var older = new SupersessionNative(false, true);
            var newer = new SupersessionNative(false, true);
            int reentrantWaits = 0;
            bool? newerResult = null;
            var requests = new FocusHelper.RequestSequence(task =>
            {
                if (ReferenceEquals(Thread.CurrentThread, older.Worker))
                {
                    Interlocked.Increment(ref reentrantWaits);
                    Assert.IsFalse(task.IsCompleted);
                    return false; // The reentrant caller reaches its controlled wait bound.
                }
                return task.Wait(TimeSpan.FromSeconds(5));
            }, thread =>
            {
                workers.Enqueue(thread);
                thread.Start();
            });
            older.OnCall = call =>
            {
                if (call == "queue") newerResult = FocusHelper.ForceActivate(new(2), newer, requests);
            };
            try
            {
                Assert.IsFalse(FocusHelper.ForceActivate(new(1), older, requests));
            }
            finally { JoinAdmissionWorkers(workers); }
            Assert.AreEqual(false, newerResult);
            Assert.AreEqual(1, reentrantWaits);
            Assert.AreEqual(1, workers.Count);
            Assert.AreEqual(1, older.FocusAttempts);
            Assert.AreEqual(1, newer.FocusAttempts);
            Assert.IsNull(newer.Worker);
            Assert.AreEqual(0, older.AttachmentCalls);
            Assert.AreEqual(0, newer.AttachmentCalls);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void FailedWorkerStartSettlesPendingAdmissionAndAllowsFreshFallback(bool reentrantEnqueue)
        {
            var workers = new ConcurrentQueue<Thread>();
            var reentrant = new SupersessionNative(true);
            Task<bool>? reentrantTask = null;
            int startAttempts = 0;
            FocusHelper.RequestSequence? requests = null;
            requests = new FocusHelper.RequestSequence(task => task.Wait(TimeSpan.FromSeconds(5)), thread =>
            {
                if (Interlocked.Increment(ref startAttempts) == 1)
                {
                    if (reentrantEnqueue)
                    {
                        var activeRequests = requests!;
                        reentrantTask = activeRequests.Enqueue(activeRequests.Begin(), new(2), reentrant);
                    }
                    throw new InvalidOperationException("Controlled worker start failure.");
                }
                workers.Enqueue(thread);
                thread.Start();
            });
            var failed = new SupersessionNative(false, true);
            var recovery = new SupersessionNative(false, true);
            try
            {
                Assert.IsFalse(FocusHelper.ForceActivate(new(1), failed, requests));
                Assert.IsNull(failed.Worker);
                if (reentrantEnqueue)
                {
                    Assert.IsNotNull(reentrantTask);
                    Assert.IsTrue(reentrantTask!.IsCompletedSuccessfully);
                    Assert.IsFalse(reentrantTask.Result);
                }
                Assert.IsTrue(FocusHelper.ForceActivate(new(3), recovery, requests));
            }
            finally { JoinAdmissionWorkers(workers); }
            Assert.AreEqual(2, startAttempts);
            Assert.AreEqual(1, workers.Count);
            Assert.AreEqual(1, failed.FocusAttempts);
            Assert.AreEqual(0, failed.AttachmentCalls);
            Assert.AreEqual(0, reentrant.Calls.Length);
            AssertCompletedAttachment(recovery);
        }

        [TestMethod]
        public async Task ThrowAfterStartingWorkerRetainsItsSlotUntilEnteredNativeWorkReturns()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var workers = new ConcurrentQueue<Thread>();
            var older = new SupersessionNative(false, true)
            {
                OnCall = call =>
                {
                    if (call != "queue") return;
                    entered.Set();
                    WaitAdmissionBarrier(release);
                },
            };
            var requests = new FocusHelper.RequestSequence(task => task.Wait(TimeSpan.FromSeconds(5)), thread =>
            {
                workers.Enqueue(thread);
                thread.Start();
                WaitAdmissionBarrier(entered);
                throw new InvalidOperationException("The start callback failed after starting the owned worker.");
            });
            var newer = new SupersessionNative(true);
            Task<bool>? last = null;
            try
            {
                Assert.IsFalse(FocusHelper.ForceActivate(new(1), older, requests));
                Assert.IsFalse(release.IsSet);
                last = requests.Enqueue(requests.Begin(), new(2), newer);
                Assert.AreEqual(1, workers.Count);
                Assert.IsFalse(last.IsCompleted);
                release.Set();
                Assert.IsTrue(await last.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally
            {
                release.Set();
                if (last != null) await last.WaitAsync(TimeSpan.FromSeconds(5));
                JoinAdmissionWorkers(workers);
            }
            Assert.AreEqual(1, older.FocusAttempts);
            Assert.AreEqual(0, older.AttachmentCalls);
            Assert.AreSame(older.Worker, newer.Worker);
            AssertCompletedAttachment(newer);
        }

        [TestMethod]
        public void ReusedRequestSequenceReleasesOneHundredCompletedFallbackLifetimes()
        {
            var workers = new ConcurrentQueue<Thread>();
            var requests = AdmissionRequests(workers);
            for (int cycle = 0; cycle < 100; cycle++)
            {
                var native = new SupersessionNative(false, true);
                try { Assert.IsTrue(FocusHelper.ForceActivate(new(cycle + 1), native, requests)); }
                finally { JoinAdmissionWorkers(workers); }
                AssertCompletedAttachment(native);
                Assert.AreEqual(2, native.FocusAttempts);
                Assert.AreEqual(0, native.AltPresses);
                Assert.AreEqual(cycle + 1, workers.Count, "A completed idle burst releases admission for a fresh worker.");
            }
            Assert.IsTrue(workers.All(worker => !worker.IsAlive));
        }

        [TestMethod]
        public async Task CompletionAndNewAdmissionRaceNeverStrandsLatestFallback()
        {
            var workers = new ConcurrentQueue<Thread>();
            var requests = AdmissionRequests(workers);
            for (int cycle = 0; cycle < 100; cycle++)
            {
                using var cleanupEntered = new ManualResetEventSlim();
                using var race = new Barrier(2);
                var older = new SupersessionNative(true)
                {
                    OnCall = call =>
                    {
                        if (call != "detach:11:22") return;
                        cleanupEntered.Set();
                        if (!race.SignalAndWait(TimeSpan.FromSeconds(5)))
                            throw new TimeoutException("The controlled cleanup/admission race was not released.");
                    },
                };
                var newer = new SupersessionNative(true);
                Task<bool> first = requests.Enqueue(requests.Begin(), new(1), older);
                Task<bool>? last = null;
                try
                {
                    WaitAdmissionBarrier(cleanupEntered);
                    Assert.IsTrue(race.SignalAndWait(TimeSpan.FromSeconds(5)));
                    last = requests.Enqueue(requests.Begin(), new(2), newer);
                    await first.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.IsTrue(await last.WaitAsync(TimeSpan.FromSeconds(5)));
                }
                finally
                {
                    if (race.CurrentPhaseNumber == 0 && race.ParticipantCount == 2)
                        race.RemoveParticipant();
                    await first.WaitAsync(TimeSpan.FromSeconds(5));
                    if (last != null) await last.WaitAsync(TimeSpan.FromSeconds(5));
                    JoinAdmissionWorkers(workers);
                }
                AssertCompletedAttachment(older);
                AssertCompletedAttachment(newer);
                Assert.AreEqual(1, newer.FocusAttempts);
                Assert.AreEqual(0, newer.AltPresses);
            }
            Assert.IsTrue(workers.All(worker => !worker.IsAlive));
        }

        private static FocusHelper.RequestSequence AdmissionRequests(ConcurrentQueue<Thread> workers) =>
            new(task => task.Wait(TimeSpan.FromSeconds(5)), thread =>
            {
                Assert.IsTrue(thread.IsBackground, "The worker must not keep the process alive.");
                workers.Enqueue(thread);
                thread.Start();
            });

        private static void WaitAdmissionBarrier(ManualResetEventSlim barrier)
        {
            if (!barrier.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The controlled focus admission boundary was not reached.");
        }

        private static void JoinAdmissionWorkers(ConcurrentQueue<Thread> workers)
        {
            foreach (var worker in workers)
                Assert.IsTrue(worker.Join(TimeSpan.FromSeconds(5)), "Every started worker must exit before its test adapters are released.");
        }
    }
}
