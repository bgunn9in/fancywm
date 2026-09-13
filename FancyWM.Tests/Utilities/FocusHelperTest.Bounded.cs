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
        private static FocusHelper.RequestSequence ControlledFocusRequests()
        {
            // The historical supersession comparison loads its archived production
            // DLL with this same fixture. That DLL already waits until cleanup;
            // newer versions expose an injected wait so barriers do not race100ms.
            var constructor = typeof(FocusHelper.RequestSequence).GetConstructor(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                binder: null, [typeof(Func<Task<bool>, bool>), typeof(Action<Thread>)], modifiers: null);
            return constructor == null ? new() : (FocusHelper.RequestSequence)constructor.Invoke(
                [new Func<Task<bool>, bool>(task => task.Wait(TimeSpan.FromSeconds(5))),
                    new Action<Thread>(thread => thread.Start())]);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task UnconfirmedDetachRetiresWorkerAndFailsPendingWithoutReusingAttachedThread(bool throws)
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var pending = new ManualResetEventSlim();
            int waits = 0;
            var requests = new FocusHelper.RequestSequence(task =>
            {
                if (Interlocked.Increment(ref waits) == 2) pending.Set();
                return task.Wait(TimeSpan.FromSeconds(5));
            }, thread => thread.Start());
            var inner = new SupersessionNative(false, true)
            {
                OnCall = call =>
                {
                    if (call != "detach:11:22") return;
                    entered.Set();
                    Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(5)));
                },
            };
            var older = new DetachFailureNative(inner, throws);
            var newer = new SupersessionNative(false, true);
            Task<bool> first = Task.Run(() => FocusHelper.ForceActivate(new(1), older, requests));
            Task<bool>? second = null;
            try
            {
                Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
                second = Task.Run(() => FocusHelper.ForceActivate(new(2), newer, requests));
                Assert.IsTrue(pending.Wait(TimeSpan.FromSeconds(5)));
            }
            finally
            {
                release.Set();
                await first.WaitAsync(TimeSpan.FromSeconds(5));
                if (second != null) await second.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsTrue(inner.Worker!.Join(TimeSpan.FromSeconds(5)));
            }
            Assert.IsFalse(first.Result);
            Assert.IsNotNull(second);
            Assert.IsFalse(second!.Result);
            Assert.AreEqual(1, inner.DetachmentCalls);
            Assert.IsNull(newer.Worker);
            Assert.AreEqual(1, newer.FocusAttempts);
            var recovery = new SupersessionNative(false, true);
            Assert.IsTrue(FocusHelper.ForceActivate(new(3), recovery, requests));
            AssertCompletedAttachment(recovery);
            Assert.AreNotSame(inner.Worker, recovery.Worker);
        }

        private sealed class DetachFailureNative(SupersessionNative inner, bool throws) : FocusHelper.INative
        {
            public bool SetForegroundWindow(IntPtr window) => inner.SetForegroundWindow(window);
            public void EnsureMessageQueue() => inner.EnsureMessageQueue();
            public uint CurrentThreadId => inner.CurrentThreadId;
            public uint ForegroundThreadId => inner.ForegroundThreadId;
            public void SendAltKeyPresses() => inner.SendAltKeyPresses();
            public bool AttachThreadInput(uint thread, uint foregroundThread, bool attach)
            {
                inner.AttachThreadInput(thread, foregroundThread, attach);
                if (attach) return true;
                if (throws) throw new InvalidOperationException("Controlled detach failure.");
                return false;
            }
        }

        [DataTestMethod]
        [DataRow("queue", 0, 1, 0)]
        [DataRow("thread", 0, 1, 0)]
        [DataRow("foreground", 0, 1, 0)]
        [DataRow("attach:11:22", 1, 1, 0)]
        [DataRow("focus:1", 1, 2, 0)]
        [DataRow("alt", 1, 2, 1)]
        [DataRow("detach:11:22", 1, 2, 0)]
        public async Task ExpiryDuringNativeStageStopsLaterEffectsButRetainsCleanup(
            string stage, int attachments, int focusAttempts, int alt)
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var native = new SupersessionNative(false, stage == "alt" ? false : true, true);
            native.OnCall = call =>
            {
                if (call == stage && (stage != "focus:1" || native.FocusAttempts == 2))
                {
                    entered.Set();
                    Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(5)));
                }
            };
            var requests = new FocusHelper.RequestSequence(task =>
            {
                Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
                return false;
            }, thread => thread.Start());
            try
            {
                Assert.IsFalse(await Task.Run(() => FocusHelper.ForceActivate(new(1), native, requests))
                    .WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.IsFalse(release.IsSet);
            }
            finally
            {
                release.Set();
                Assert.IsNotNull(native.Worker);
                Assert.IsTrue(native.Worker!.Join(TimeSpan.FromSeconds(5)));
            }
            Assert.AreEqual(attachments, native.AttachmentCalls);
            Assert.AreEqual(attachments, native.DetachmentCalls);
            Assert.AreEqual(0, native.OwnedAttachments);
            Assert.AreEqual(focusAttempts, native.FocusAttempts);
            Assert.AreEqual(alt, native.AltPresses);
        }

        [TestMethod]
        public async Task DefaultFallbackTimeoutReturnsWhileNativeRemainsBlocked()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var native = new SupersessionNative(false, true)
            {
                OnCall = call =>
                {
                    if (call != "queue") return;
                    entered.Set();
                    Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(5)));
                },
            };
            Task<bool> caller = Task.Run(() => FocusHelper.ForceActivate(new(1), native));
            try
            {
                Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
                Assert.IsFalse(await caller.WaitAsync(TimeSpan.FromSeconds(2)), "The production100ms wait must not fall through to an unbounded Result.");
                Assert.IsFalse(release.IsSet);
                Assert.IsTrue(native.Worker!.IsBackground);
            }
            finally
            {
                release.Set();
                await caller.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsTrue(native.Worker!.Join(TimeSpan.FromSeconds(5)));
            }
            Assert.AreEqual(1, native.FocusAttempts);
            Assert.AreEqual(0, native.AttachmentCalls);
        }

        [TestMethod]
        public async Task TimedOutFallbackDoesNotMultiplyWorkersOrApplyObsoleteFocus()
        {
            var result = await ObserveBlockedBurst();
            Assert.AreEqual(16, result.ReturnedBeforeRelease, "Every expired caller must return while native work is still blocked.");
            Assert.AreEqual(1, result.StartedWorkers, "Timeout must retain the active slot until the native worker actually exits.");
            Assert.AreEqual(1, result.BackgroundWorkers);
            Assert.AreEqual(0, result.ObsoleteFocus);
        }

        [TestMethod]
        public async Task FocusBoundedCounterScenario()
        {
            // Shared old-policy/candidate fixture: strict improvement assertions
            // belong to the regression above and the comparison runner.
            var result = await ObserveBlockedBurst();
            Console.WriteLine("PERFCOUNTER focus-bounded requests 16");
            Console.WriteLine($"PERFCOUNTER focus-bounded returned-before-release {result.ReturnedBeforeRelease}");
            Console.WriteLine($"PERFCOUNTER focus-bounded started-workers {result.StartedWorkers}");
            Console.WriteLine($"PERFCOUNTER focus-bounded background-workers {result.BackgroundWorkers}");
            Console.WriteLine($"PERFCOUNTER focus-bounded obsolete-focus {result.ObsoleteFocus}");
            Console.WriteLine($"PERFCOUNTER focus-bounded worker-exits {result.StartedWorkers}");
            Console.WriteLine("PERFCOUNTER focus-bounded newest-successes 1");
            Console.WriteLine("PERFCOUNTER focus-bounded recovery-successes 1");
        }

        private static async Task<BlockedBurstResult> ObserveBlockedBurst()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var waited = new AutoResetEvent(false);
            var workers = new List<Thread>();
            var callers = new List<Task<bool>>();
            var natives = new List<SupersessionNative>();
            var requests = new FocusHelper.RequestSequence(task =>
            {
                Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
                waited.Set();
                return false; // Controlled expiry, with the first native call held.
            }, thread =>
            {
                lock (workers) workers.Add(thread);
                entered.Reset();
                thread.Start();
                Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
            });
            int returned;
            int started;
            int background;
            try
            {
                for (int index = 0; index < 16; index++)
                {
                    var native = new SupersessionNative(false, true)
                    {
                        OnCall = call =>
                        {
                            if (call == "queue")
                            {
                                entered.Set();
                                Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(10)));
                            }
                        },
                    };
                    natives.Add(native);
                    callers.Add(Task.Factory.StartNew(() => FocusHelper.ForceActivate(new(1), native, requests),
                        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
                    Assert.IsTrue(waited.WaitOne(TimeSpan.FromSeconds(5)), "Each caller reaches the same expiry seam.");
                    // Older requests may return on supersession. Only the most recent
                    // caller is deliberately held by the legacy unbounded Result.
                }
                // Use an explicit caller completion observation after simulated expiry;
                // baseline is allowed to remain blocked and is always released below.
                await Task.WhenAny(Task.WhenAll(callers), Task.Delay(1000));
                returned = callers.Count(task => task.IsCompleted);
                lock (workers)
                {
                    started = workers.Count;
                    background = workers.Count(thread => thread.IsBackground);
                }
                var newest = new SupersessionNative(true);
                Assert.IsTrue(FocusHelper.ForceActivate(new(2), newest, requests));
                Assert.AreEqual(1, newest.FocusAttempts);
            }
            finally
            {
                release.Set();
                await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(5));
                foreach (var worker in workers)
                    Assert.IsTrue(worker.Join(TimeSpan.FromSeconds(5)), "No blocked test worker may escape the fixture.");
            }
            Assert.IsTrue(callers.All(task => !task.Result));
            Assert.IsTrue(natives.All(native => native.OwnedAttachments == 0));
            var recovery = new SupersessionNative(true);
            Assert.IsTrue(FocusHelper.ForceActivate(new(3), recovery, requests));
            return new(returned, started, background, natives.Sum(native => native.FocusAttempts - 1));
        }

        private sealed record BlockedBurstResult(int ReturnedBeforeRelease, int StartedWorkers,
            int BackgroundWorkers, int ObsoleteFocus);
    }
}
