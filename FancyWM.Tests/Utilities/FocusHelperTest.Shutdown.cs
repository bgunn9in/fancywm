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
        [DataRow("queue", 0, 1, 0)]
        [DataRow("attach:11:22", 1, 1, 0)]
        [DataRow("focus:1", 1, 2, 0)]
        [DataRow("alt", 1, 2, 1)]
        [DataRow("detach:11:22", 1, 2, 0)]
        public async Task WindowLifetimeStopInvalidatesEnteredFocusBeforeCallerExpiry(
            string stage, int attachments, int focusAttempts, int altPresses)
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var waiting = new ManualResetEventSlim();
            var workers = new ConcurrentQueue<Thread>();
            var requests = new FocusHelper.RequestSequence(task =>
            {
                waiting.Set();
                return task.Wait(TimeSpan.FromSeconds(5));
            }, thread =>
            {
                thread.Start();
                workers.Enqueue(thread);
            });
            var native = new SupersessionNative(false, stage != "alt", true);
            native.OnCall = call =>
            {
                if (call != stage || (stage == "focus:1" && native.FocusAttempts != 2)) return;
                entered.Set();
                WaitAdmissionBarrier(release);
            };
            int dependentCleanup = 0;
            using var lifetime = new MainWindowLifetime(
                releaseOwned => releaseOwned(requests.Stop),
                releaseDependent: releaseDependent => releaseDependent(() => dependentCleanup++));
            Task<bool> active = Task.Run(() => FocusHelper.ForceActivate(new(1), native, requests));
            try
            {
                WaitAdmissionBarrier(entered);
                WaitAdmissionBarrier(waiting);
                lifetime.Dispose();
                lifetime.Dispose();
                Assert.AreEqual(1, dependentCleanup);
                Assert.IsTrue(lifetime.Completion.IsCompletedSuccessfully,
                    "Revoking focus admission does not wait for an already entered native call.");
                Assert.IsFalse(active.IsCompleted, "An entered native operation still owns its completion until cleanup returns.");
                Assert.IsFalse(release.IsSet);
                // No newer request is admitted before this assertion: only Stop
                // can invalidate the current operation, rather than supersession.
                release.Set();
                Assert.IsFalse(await active.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally
            {
                release.Set();
                await active.WaitAsync(TimeSpan.FromSeconds(5));
                JoinAdmissionWorkers(workers);
            }
            Assert.AreEqual(attachments, native.AttachmentCalls);
            Assert.AreEqual(attachments, native.DetachmentCalls);
            Assert.AreEqual(0, native.OwnedAttachments);
            Assert.AreEqual(focusAttempts, native.FocusAttempts);
            Assert.AreEqual(altPresses, native.AltPresses);
            if (attachments != 0) Assert.AreEqual("detach:11:22", native.Calls.Last());
            var late = new SupersessionNative(true);
            Assert.IsFalse(FocusHelper.ForceActivate(new(2), late, requests));
            Assert.AreEqual(0, late.Calls.Length);
            Assert.AreEqual(1, workers.Count);
        }

        [TestMethod]
        public async Task WindowLifetimeStopCompletesPendingAdmissionWhileActiveAttachmentStillNeedsCleanup()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var workers = new ConcurrentQueue<Thread>();
            var requests = AdmissionRequests(workers);
            var activeNative = new SupersessionNative(true)
            {
                OnCall = call =>
                {
                    if (call != "attach:11:22") return;
                    entered.Set();
                    WaitAdmissionBarrier(release);
                },
            };
            var pendingNative = new SupersessionNative(true);
            var lateNative = new SupersessionNative(true);
            int dependentCleanup = 0;
            using var lifetime = new MainWindowLifetime(
                releaseOwned => releaseOwned(requests.Stop),
                releaseDependent: releaseDependent => releaseDependent(() => dependentCleanup++));
            Task<bool> active = requests.Enqueue(requests.Begin(), new(1), activeNative);
            Task<bool>? pending = null;
            try
            {
                WaitAdmissionBarrier(entered);
                pending = requests.Enqueue(requests.Begin(), new(2), pendingNative);
                Assert.IsFalse(pending.IsCompleted);
                lifetime.Dispose();
                Assert.AreEqual(1, dependentCleanup);
                Assert.IsTrue(pending.IsCompletedSuccessfully,
                    "Stop must publish pending failure immediately, before the blocked worker is released.");
                Assert.IsFalse(pending.Result);
                Assert.IsFalse(active.IsCompleted);
                Assert.AreEqual(1, activeNative.OwnedAttachments);
                Assert.IsFalse(FocusHelper.ForceActivate(new(3), lateNative, requests));
                Assert.AreEqual(0, lateNative.Calls.Length);
                Assert.AreEqual(1, workers.Count);
            }
            finally
            {
                release.Set();
                await active.WaitAsync(TimeSpan.FromSeconds(5));
                if (pending != null) await pending.WaitAsync(TimeSpan.FromSeconds(5));
                JoinAdmissionWorkers(workers);
            }
            Assert.IsFalse(active.Result);
            Assert.AreEqual(0, activeNative.FocusAttempts);
            AssertCompletedAttachment(activeNative);
            Assert.AreEqual(0, pendingNative.Calls.Length);
            Assert.AreEqual(1, workers.Count);
        }

        [DataTestMethod]
        [DataRow("immediate")]
        [DataRow("attachment")]
        public void ReentrantStopFromNativeCallbackIsTerminal(string boundary)
        {
            var workers = new ConcurrentQueue<Thread>();
            var requests = AdmissionRequests(workers);
            int stops = 0;
            var native = new SupersessionNative(boundary == "immediate", true)
            {
                OnCall = call =>
                {
                    if (call != (boundary == "immediate" ? "focus:1" : "attach:11:22")) return;
                    requests.Stop();
                    requests.Stop();
                    stops++;
                },
            };
            try { Assert.IsFalse(FocusHelper.ForceActivate(new(1), native, requests)); }
            finally { JoinAdmissionWorkers(workers); }
            Assert.AreEqual(1, stops);
            Assert.AreEqual(1, native.FocusAttempts);
            Assert.AreEqual(0, native.AltPresses);
            Assert.AreEqual(0, native.OwnedAttachments);
            Assert.AreEqual(boundary == "immediate" ? 0 : 1, native.DetachmentCalls);
            var late = new SupersessionNative(true);
            Assert.IsFalse(FocusHelper.ForceActivate(new(2), late, requests));
            Assert.AreEqual(0, late.Calls.Length);
            Assert.AreEqual(boundary == "immediate" ? 0 : 1, workers.Count);
        }

        [TestMethod]
        public void StopBeforeFirstRequestRejectsBothImmediateAndQueuedAdmission()
        {
            var workers = new ConcurrentQueue<Thread>();
            var requests = AdmissionRequests(workers);
            requests.Stop();
            requests.Stop();
            var immediate = new SupersessionNative(true);
            var queued = new SupersessionNative(true);
            try
            {
                Assert.IsFalse(FocusHelper.ForceActivate(new(1), immediate, requests));
                Task<bool> admission = requests.Enqueue(requests.Begin(), new(2), queued);
                Assert.IsTrue(admission.IsCompletedSuccessfully);
                Assert.IsFalse(admission.Result);
            }
            finally { JoinAdmissionWorkers(workers); }
            Assert.AreEqual(0, immediate.Calls.Length);
            Assert.AreEqual(0, queued.Calls.Length);
            Assert.AreEqual(0, workers.Count);
        }

        [TestMethod]
        public async Task StopDuringUnstartedWorkerAcquisitionRejectsItsNativeWorkAndLaterRequests()
        {
            using var startEntered = new ManualResetEventSlim();
            using var allowStart = new ManualResetEventSlim();
            var workers = new ConcurrentQueue<Thread>();
            int acquiredWorkers = 0;
            var requests = new FocusHelper.RequestSequence(task => task.Wait(TimeSpan.FromSeconds(5)), thread =>
            {
                Interlocked.Increment(ref acquiredWorkers);
                startEntered.Set();
                WaitAdmissionBarrier(allowStart);
                // The launcher was already entered when Stop ran. Its eventual
                // thread must find no admitted native work and exit normally.
                thread.Start();
                workers.Enqueue(thread);
            });
            var native = new SupersessionNative(false, true);
            Task<bool> active = Task.Run(() => FocusHelper.ForceActivate(new(1), native, requests));
            try
            {
                WaitAdmissionBarrier(startEntered);
                requests.Stop();
                requests.Stop();
                Assert.IsFalse(active.IsCompleted);
                allowStart.Set();
                Assert.IsFalse(await active.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally
            {
                allowStart.Set();
                await active.WaitAsync(TimeSpan.FromSeconds(5));
                JoinAdmissionWorkers(workers);
            }
            Assert.AreEqual(1, acquiredWorkers);
            Assert.AreEqual(1, native.FocusAttempts);
            Assert.IsNull(native.Worker);
            Assert.AreEqual(0, native.AttachmentCalls);
            var late = new SupersessionNative(true);
            Assert.IsFalse(FocusHelper.ForceActivate(new(2), late, requests));
            Assert.AreEqual(0, late.Calls.Length);
            Assert.AreEqual(1, acquiredWorkers);
        }

        [TestMethod]
        public void OneHundredWindowLifetimesStopTheirOwnFocusSequenceWithoutRestartingIt()
        {
            int dependentCleanups = 0;
            var workers = new ConcurrentQueue<Thread>();
            for (int cycle = 0; cycle < 100; cycle++)
            {
                var requests = AdmissionRequests(workers);
                var native = new SupersessionNative(false, true);
                using var lifetime = new MainWindowLifetime(
                    releaseOwned => releaseOwned(requests.Stop),
                    releaseDependent: releaseDependent => releaseDependent(() => dependentCleanups++));
                try
                {
                    Assert.IsTrue(FocusHelper.ForceActivate(new(cycle + 1), native, requests));
                    AssertCompletedAttachment(native);
                    lifetime.Dispose();
                    lifetime.Dispose();
                    requests.Stop();
                    var late = new SupersessionNative(true);
                    Assert.IsFalse(FocusHelper.ForceActivate(new(cycle + 101), late, requests));
                    Assert.AreEqual(0, late.Calls.Length);
                    var queued = new SupersessionNative(true);
                    Task<bool> admission = requests.Enqueue(requests.Begin(), new(cycle + 201), queued);
                    Assert.IsTrue(admission.IsCompletedSuccessfully);
                    Assert.IsFalse(admission.Result);
                    Assert.AreEqual(0, queued.Calls.Length);
                    Assert.AreEqual(cycle + 1, dependentCleanups);
                    Assert.AreEqual(cycle + 1, workers.Count);
                }
                finally { JoinAdmissionWorkers(workers); }
            }
            Assert.AreEqual(100, dependentCleanups);
            Assert.IsTrue(workers.All(worker => !worker.IsAlive));
        }
    }
}
