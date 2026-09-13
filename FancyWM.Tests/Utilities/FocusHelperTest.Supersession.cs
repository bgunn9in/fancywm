#nullable enable
using System;
using System.Collections.Generic;
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
        [DataRow(false)]
        [DataRow(true)]
        public void SupersessionDuringImmediateAttemptPreventsFallbackAndStaleSuccess(bool immediateResult)
        {
            var requests = ControlledFocusRequests();
            var newer = new SupersessionNative(true);
            bool? newerFocused = null;
            var older = new SupersessionNative(immediateResult)
            {
                OnCall = call =>
                {
                    if (call == "focus:1")
                    {
                        newerFocused = FocusHelper.ForceActivate(new(2), newer, requests);
                    }
                },
            };

            Assert.IsFalse(FocusHelper.ForceActivate(new(1), older, requests));
            Assert.AreEqual(true, newerFocused);
            Assert.AreEqual(1, older.FocusAttempts);
            Assert.AreEqual(1, newer.FocusAttempts);
            Assert.IsNull(older.Worker, "A superseded immediate attempt must not acquire a fallback worker.");
            Assert.AreEqual(0, older.AltPresses);
            Assert.AreEqual(0, older.AttachmentCalls);
        }

        [DataTestMethod]
        [DataRow("queue", 0)]
        [DataRow("thread", 0)]
        [DataRow("foreground", 0)]
        [DataRow("attach:11:22", 1)]
        public async Task SupersededPausedFallbackStopsBeforeFocusAndDetachesOwnedInput(string pausedCall, int attachments)
        {
            var observation = await ObservePausedSupersession(pausedCall, ControlledFocusRequests());

            Assert.IsFalse(observation.OlderFocused);
            Assert.IsTrue(observation.NewerFocused);
            Assert.AreEqual(1, observation.Older.FocusAttempts, "Only the original immediate attempt is allowed.");
            Assert.AreEqual(1, observation.Newer.FocusAttempts);
            Assert.AreEqual(0, observation.Older.AltPresses);
            Assert.AreEqual(attachments, observation.Older.AttachmentCalls);
            Assert.AreEqual(attachments, observation.Older.DetachmentCalls);
            Assert.AreEqual(0, observation.Older.OwnedAttachments);
            if (attachments != 0)
            {
                Assert.AreEqual("detach:11:22", observation.Older.Calls.Last());
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void SupersessionDuringFallbackResultPreventsLaterEffectsAndStaleSuccess(bool fallbackResult)
        {
            var requests = ControlledFocusRequests();
            var newer = new SupersessionNative(true);
            bool? newerFocused = null;
            var older = new SupersessionNative(false, fallbackResult);
            older.OnCall = call =>
            {
                if (call == "focus:1" && older.FocusAttempts == 2)
                {
                    newerFocused = FocusHelper.ForceActivate(new(2), newer, requests);
                }
            };

            Assert.IsFalse(FocusHelper.ForceActivate(new(1), older, requests));
            Assert.AreEqual(true, newerFocused);
            Assert.AreEqual(2, older.FocusAttempts, "The already entered native call is observed; no later call may start.");
            Assert.AreEqual(0, older.AltPresses);
            AssertCompletedAttachment(older);
        }

        [TestMethod]
        public void SupersessionDuringAltPreventsFinalForegroundAttempt()
        {
            var requests = ControlledFocusRequests();
            var newer = new SupersessionNative(true);
            bool? newerFocused = null;
            var older = new SupersessionNative(false, false, true)
            {
                OnCall = call =>
                {
                    if (call == "alt")
                    {
                        newerFocused = FocusHelper.ForceActivate(new(2), newer, requests);
                    }
                },
            };

            Assert.IsFalse(FocusHelper.ForceActivate(new(1), older, requests));
            Assert.AreEqual(true, newerFocused);
            Assert.AreEqual(2, older.FocusAttempts);
            Assert.AreEqual(1, older.AltPresses, "Already entered input injection is not claimed to be cancellable.");
            AssertCompletedAttachment(older);
        }

        [TestMethod]
        public void SupersessionDuringDetachRejectsPreviouslySuccessfulResult()
        {
            var requests = ControlledFocusRequests();
            var newer = new SupersessionNative(true);
            bool? newerFocused = null;
            var older = new SupersessionNative(false, true)
            {
                OnCall = call =>
                {
                    if (call == "detach:11:22")
                    {
                        newerFocused = FocusHelper.ForceActivate(new(2), newer, requests);
                    }
                },
            };

            Assert.IsFalse(FocusHelper.ForceActivate(new(1), older, requests));
            Assert.AreEqual(true, newerFocused);
            Assert.AreEqual(2, older.FocusAttempts);
            Assert.AreEqual(0, older.AltPresses);
            AssertCompletedAttachment(older);
        }

        [TestMethod]
        public async Task ReusedRequestSequenceAcceptsFreshRequestsAfterSupersededCompletion()
        {
            var requests = ControlledFocusRequests();
            var observation = await ObservePausedSupersession("attach:11:22", requests);
            Assert.IsFalse(observation.OlderFocused);
            Assert.IsTrue(observation.NewerFocused);

            var next = new SupersessionNative(false, true);
            Assert.IsTrue(FocusHelper.ForceActivate(new(3), next, requests));
            CollectionAssert.AreEqual(new[]
            {
                "focus:3", "queue", "thread", "foreground", "attach:11:22", "focus:3", "detach:11:22",
            }, next.Calls);
            AssertCompletedAttachment(next);
        }

        [TestMethod]
        public async Task FocusSupersessionCounterScenario()
        {
            const int cycles = 100;
            var requests = ControlledFocusRequests();
            int oldFocus = 0;
            int newFocus = 0;
            int alt = 0;
            int attachments = 0;
            int detachments = 0;
            int completedWorkers = 0;
            int supersededResults = 0;
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                var observation = await ObservePausedSupersession("queue", requests);
                // These shared fixture assertions hold for both the preserved sequence
                // seam and the candidate. Dedicated regression cases require suppression.
                Assert.IsTrue(observation.NewerFocused);
                Assert.AreEqual(0, observation.Older.OwnedAttachments);
                Assert.AreEqual(observation.Older.AttachmentCalls, observation.Older.DetachmentCalls);
                Assert.AreEqual(0, observation.Newer.AttachmentCalls);
                oldFocus += observation.Older.FocusAttempts;
                newFocus += observation.Newer.FocusAttempts;
                alt += observation.Older.AltPresses + observation.Newer.AltPresses;
                attachments += observation.Older.AttachmentCalls;
                detachments += observation.Older.DetachmentCalls;
                completedWorkers++;
                if (!observation.OlderFocused) supersededResults++;
            }
            Console.WriteLine($"PERFCOUNTER focus-supersession cycles {cycles}");
            Console.WriteLine($"PERFCOUNTER focus-supersession old-focus {oldFocus}");
            Console.WriteLine($"PERFCOUNTER focus-supersession new-focus {newFocus}");
            Console.WriteLine($"PERFCOUNTER focus-supersession alt {alt}");
            Console.WriteLine($"PERFCOUNTER focus-supersession attachments {attachments}");
            Console.WriteLine($"PERFCOUNTER focus-supersession detachments {detachments}");
            Console.WriteLine($"PERFCOUNTER focus-supersession completed-workers {completedWorkers}");
            Console.WriteLine($"PERFCOUNTER focus-supersession superseded-results {supersededResults}");
        }

        private static async Task<FocusSupersessionObservation> ObservePausedSupersession(
            string pausedCall, FocusHelper.RequestSequence requests)
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var older = new SupersessionNative(false, true)
            {
                OnCall = call =>
                {
                    if (call == pausedCall)
                    {
                        entered.Set();
                        if (!release.Wait(TimeSpan.FromSeconds(5)))
                        {
                            throw new TimeoutException("The controlled native stage was not released.");
                        }
                    }
                },
            };
            var newer = new SupersessionNative(true);
            Task<bool> olderRequest = Task.Run(() => FocusHelper.ForceActivate(new(1), older, requests));
            bool newerFocused = false;
            bool olderFocused;
            try
            {
                Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)), "The old request must reach the selected native stage first.");
                newerFocused = FocusHelper.ForceActivate(new(2), newer, requests);
                Assert.IsFalse(olderRequest.IsCompleted, "The old native call remains held until the newer request completes.");
            }
            finally
            {
                release.Set();
                olderFocused = await olderRequest.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.IsNotNull(older.Worker);
            Assert.IsTrue(older.Worker!.Join(TimeSpan.FromSeconds(5)), "The old worker must exit before its native barriers are disposed.");
            return new(older, newer, olderFocused, newerFocused);
        }

        private static void AssertCompletedAttachment(SupersessionNative native)
        {
            Assert.AreEqual(1, native.AttachmentCalls);
            Assert.AreEqual(1, native.DetachmentCalls);
            Assert.AreEqual(0, native.OwnedAttachments);
            Assert.AreEqual("detach:11:22", native.Calls.Last());
            Assert.IsNotNull(native.Worker);
            Assert.IsTrue(native.Worker!.Join(TimeSpan.FromSeconds(5)));
        }

        private sealed record FocusSupersessionObservation(
            SupersessionNative Older, SupersessionNative Newer, bool OlderFocused, bool NewerFocused);

        private sealed class SupersessionNative(params bool[] results) : FocusHelper.INative
        {
            private readonly object m_gate = new();
            private readonly List<string> m_calls = [];
            private readonly Queue<bool> m_results = new(results);
            private int m_focusAttempts;
            private int m_altPresses;
            private int m_attachmentCalls;
            private int m_detachmentCalls;
            private int m_ownedAttachments;

            public Action<string>? OnCall { get; set; }
            public Thread? Worker { get; private set; }
            public int FocusAttempts => Volatile.Read(ref m_focusAttempts);
            public int AltPresses => Volatile.Read(ref m_altPresses);
            public int AttachmentCalls => Volatile.Read(ref m_attachmentCalls);
            public int DetachmentCalls => Volatile.Read(ref m_detachmentCalls);
            public int OwnedAttachments => Volatile.Read(ref m_ownedAttachments);
            public string[] Calls { get { lock (m_gate) return m_calls.ToArray(); } }

            public bool SetForegroundWindow(IntPtr window)
            {
                Interlocked.Increment(ref m_focusAttempts);
                Record($"focus:{window}");
                lock (m_gate) return m_results.Count == 0 || m_results.Dequeue();
            }

            public void EnsureMessageQueue()
            {
                Worker = Thread.CurrentThread;
                Record("queue");
            }

            public uint CurrentThreadId { get { Record("thread"); return 11; } }
            public uint ForegroundThreadId { get { Record("foreground"); return 22; } }

            public bool AttachThreadInput(uint thread, uint foregroundThread, bool attach)
            {
                if (attach)
                {
                    Interlocked.Increment(ref m_attachmentCalls);
                    Interlocked.Increment(ref m_ownedAttachments);
                }
                else
                {
                    Interlocked.Increment(ref m_detachmentCalls);
                    Interlocked.Decrement(ref m_ownedAttachments);
                }
                Record($"{(attach ? "attach" : "detach")}:{thread}:{foregroundThread}");
                return true;
            }

            public void SendAltKeyPresses()
            {
                Interlocked.Increment(ref m_altPresses);
                Record("alt");
            }

            private void Record(string call)
            {
                lock (m_gate) m_calls.Add(call);
                OnCall?.Invoke(call);
            }
        }
    }
}
