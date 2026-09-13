using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public partial class FocusHelperTest
    {
        [TestMethod]
        public void ImmediateSuccessDoesNotCreateFallbackWork()
        {
            var native = new Native(true);
            Assert.IsTrue(FocusHelper.ForceActivate(new(99), native));
            CollectionAssert.AreEqual(new[] { "focus:99" }, native.Calls);
            CollectionAssert.AreEqual(new[] { Environment.CurrentManagedThreadId }, native.Threads);
        }

        [TestMethod]
        public void AttachedSuccessDetachesTheExactThreadsWithoutSendingInput()
        {
            var native = new Native(true);
            Assert.IsTrue(FocusHelper.RunFallback(new(99), native));
            CollectionAssert.AreEqual(new[]
            {
                "queue", "thread", "foreground", "attach:11:22", "focus:99", "detach:11:22",
            }, native.Calls);
            Assert.AreEqual(0, native.Attachments);
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void ExistingAltFallbackOrderAndResultArePreserved(bool finalResult)
        {
            var native = new Native(false, finalResult);
            Assert.AreEqual(finalResult, FocusHelper.RunFallback(new(99), native));
            CollectionAssert.AreEqual(new[]
            {
                "queue", "thread", "foreground", "attach:11:22", "focus:99", "alt", "focus:99", "detach:11:22",
            }, native.Calls);
            Assert.AreEqual(0, native.Attachments);
        }

        [TestMethod]
        public void FailedAttachmentDoesNotDetachOrSendInput()
        {
            var native = new Native(true) { AttachSucceeds = false };
            Assert.IsFalse(FocusHelper.RunFallback(new(99), native));
            CollectionAssert.AreEqual(new[] { "queue", "thread", "foreground", "attach:11:22" }, native.Calls);
            Assert.AreEqual(0, native.Attachments);
        }

        [DataTestMethod]
        [DataRow("queue")]
        [DataRow("thread")]
        [DataRow("foreground")]
        [DataRow("attach:11:22")]
        [DataRow("focus:99")]
        [DataRow("alt")]
        [DataRow("detach:11:22")]
        public void NativeExceptionReportsFailureAndReleasesSuccessfulAttachment(string failingCall)
        {
            var native = new Native(false, true) { ThrowAt = failingCall };
            Assert.IsFalse(FocusHelper.RunFallback(new(99), native));
            Assert.AreEqual(0, native.Attachments);
            bool attached = failingCall is "focus:99" or "alt" or "detach:11:22";
            Assert.AreEqual(attached ? 1 : 0, native.Calls.Count(call => call.StartsWith("detach:")));
            Assert.IsFalse(native.Calls.SkipWhile(call => call != failingCall).Skip(1)
                .Any(call => call == "focus:99" || call == "alt"), "No new focus effect follows a failure.");
        }

        [TestMethod]
        public void LastForegroundAttemptExceptionStillDetaches()
        {
            var native = new Native(false, true) { ThrowOnFocusAttempt = 2 };
            Assert.IsFalse(FocusHelper.RunFallback(new(99), native));
            Assert.AreEqual(0, native.Attachments);
            Assert.AreEqual("detach:11:22", native.Calls.Last());
        }

        [TestMethod]
        public void FailedDetachReportsFailureEvenAfterSuccessfulFocus()
        {
            var native = new Native(true) { DetachSucceeds = false };
            Assert.IsFalse(FocusHelper.RunFallback(new(99), native));
            Assert.AreEqual(1, native.Calls.Count(call => call == "detach:11:22"));
            Assert.AreEqual(1, native.Attachments, "The adapter models native detach failure; no false release claim.");
        }

        [TestMethod]
        public async Task WorkerExceptionCompletesAfterDetach()
        {
            var native = new Native(false, true) { ThrowOnFocusAttempt = 2 };
            bool focused = await Task.Run(() => FocusHelper.ForceActivate(new(99), native))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(focused);
            Assert.AreEqual(0, native.Attachments);
            Assert.AreEqual("detach:11:22", native.Calls.Last());
            Assert.AreNotEqual(native.Threads[0], native.Threads[1], "Fallback still runs on its dedicated thread.");
            Assert.IsTrue(native.Worker!.Join(TimeSpan.FromSeconds(5)), "Completed worker must exit.");
        }

        [TestMethod]
        public async Task ControlledNativeCallCompletesAfterReleaseAndDetach()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var native = new Native(false, true) { FocusEntered = entered, FocusRelease = release };
            Task<bool> request = Task.Run(() => FocusHelper.ForceActivate(new(99), native, ControlledFocusRequests()));
            try
            {
                Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
                Assert.IsFalse(request.IsCompleted, "The request is pending while the native adapter is held.");
                Assert.AreEqual(1, native.Attachments);
            }
            finally
            {
                release.Set();
                await request.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.IsTrue(request.Result);
            Assert.AreEqual(0, native.Attachments);
            Assert.IsTrue(native.Worker!.Join(TimeSpan.FromSeconds(5)));
        }

        [TestMethod]
        public void FocusFallbackCounterScenario()
        {
            int attachments = 0;
            int detachments = 0;
            int completedWorkers = 0;
            for (int cycle = 0; cycle < 100; cycle++)
            {
                var native = new Native(false, true) { ThrowOnFocusAttempt = cycle % 2 == 0 ? 2 : 0 };
                Assert.AreEqual(cycle % 2 != 0, FocusHelper.ForceActivate(new(99), native));
                Assert.AreEqual(0, native.Attachments);
                Assert.IsTrue(native.Worker!.Join(TimeSpan.FromSeconds(5)));
                completedWorkers++;
                attachments += native.Calls.Count(call => call == "attach:11:22");
                detachments += native.Calls.Count(call => call == "detach:11:22");
            }
            Assert.AreEqual(100, attachments);
            Assert.AreEqual(attachments, detachments);
            Console.WriteLine($"PERFCOUNTER focus-fallback attachments {attachments}");
            Console.WriteLine($"PERFCOUNTER focus-fallback detachments {detachments}");
            Console.WriteLine($"PERFCOUNTER focus-fallback completed-workers {completedWorkers}");
            Console.WriteLine("PERFCOUNTER focus-fallback cycles 100");
        }

        private sealed class Native(params bool[] results) : FocusHelper.INative
        {
            private readonly Queue<bool> m_results = new(results);
            public List<string> Calls { get; } = [];
            public List<int> Threads { get; } = [];
            public string? ThrowAt { get; init; }
            public int ThrowOnFocusAttempt { get; init; }
            public bool AttachSucceeds { get; init; } = true;
            public bool DetachSucceeds { get; init; } = true;
            public ManualResetEventSlim? FocusEntered { get; init; }
            public ManualResetEventSlim? FocusRelease { get; init; }
            public Thread? Worker { get; private set; }
            public int Attachments { get; private set; }
            public int FocusAttempts { get; private set; }
            public void EnsureMessageQueue() { Worker = Thread.CurrentThread; Record("queue"); }
            public uint CurrentThreadId { get { Record("thread"); return 11; } }
            public uint ForegroundThreadId { get { Record("foreground"); return 22; } }
            public bool SetForegroundWindow(IntPtr window)
            {
                Record($"focus:{window}");
                if (++FocusAttempts == ThrowOnFocusAttempt) throw new InvalidOperationException("focus failed");
                if (FocusAttempts == 2 && FocusEntered != null)
                {
                    FocusEntered.Set();
                    if (!FocusRelease!.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test barrier was not released.");
                }
                return m_results.Dequeue();
            }
            public bool AttachThreadInput(uint thread, uint foregroundThread, bool attach)
            {
                if (!attach && DetachSucceeds) Attachments--;
                Record($"{(attach ? "attach" : "detach")}:{thread}:{foregroundThread}");
                if (attach && AttachSucceeds) Attachments++;
                return attach ? AttachSucceeds : DetachSucceeds;
            }
            public void SendAltKeyPresses() => Record("alt");
            private void Record(string call)
            {
                Calls.Add(call);
                Threads.Add(Environment.CurrentManagedThreadId);
                if (call == ThrowAt) throw new InvalidOperationException($"Native failure: {call}");
            }
        }
    }
}
