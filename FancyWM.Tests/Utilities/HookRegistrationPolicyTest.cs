#nullable enable
using System;
using System.Collections.Generic;

using FancyWM.DllImports;
using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    // The actual shared policy runs with fake handle identities. No hook object,
    // message loop, global registration, native input, or native cleanup is used.
    [TestClass]
    public class HookRegistrationPolicyTest
    {
        private static readonly DateTime Epoch = new(2026, 9, 6, 0, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(5);
        private static HHOOK Handle(int value) => new(new IntPtr(value));

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void FailedInstallPreservesTheHandleForNormalExitCleanup(bool throws)
        {
            HHOOK current = Handle(11);
            var releases = new List<HHOOK>();
            var failure = new InvalidOperationException("install failed");
            Func<HHOOK> install = () => throws ? throw failure : default;
            Func<HHOOK, bool> unhook = hook => { releases.Add(hook); return true; };
            if (throws)
                Assert.AreSame(failure, Assert.ThrowsException<InvalidOperationException>(
                    () => HookRegistrationPolicy.Replace(ref current, install, unhook)));
            else HookRegistrationPolicy.Replace(ref current, install, unhook);
            Assert.AreEqual(Handle(11), current);
            Assert.AreEqual(0, releases.Count);
            // Existing owners unhook their stored handle on ordinary loop exit.
            // This asserts its correct target, not the native exit protocol.
            Assert.IsTrue(unhook(current));
            CollectionAssert.AreEqual(new[] { Handle(11) }, releases);
        }

        [TestMethod]
        public void SuccessfulInstallPublishesNewBeforeReleasingPrevious()
        {
            HHOOK current = Handle(11);
            var releases = new List<HHOOK>();
            HookRegistrationPolicy.Replace(ref current,
                () => { Assert.AreEqual(Handle(11), current); return Handle(22); },
                previous =>
                {
                    Assert.AreEqual(Handle(22), current);
                    releases.Add(previous);
                    return true;
                });
            Assert.AreEqual(Handle(22), current);
            CollectionAssert.AreEqual(new[] { Handle(11) }, releases);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void MissingPreviousHandleIsNeverPassedToUnhook(bool succeeds)
        {
            HHOOK current = default;
            int releases = 0;
            HookRegistrationPolicy.Replace(ref current, () => succeeds ? Handle(22) : default,
                _ => { releases++; return false; });
            Assert.AreEqual(succeeds ? Handle(22) : default, current);
            Assert.AreEqual(0, releases);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void FailedOldUnhookDoesNotEraseThePublishedReplacement(bool throws)
        {
            HHOOK current = Handle(11);
            var failure = new InvalidOperationException("unhook failed");
            int releases = 0;
            Func<HHOOK, bool> unhook = previous =>
            {
                Assert.AreEqual(Handle(11), previous);
                Assert.AreEqual(Handle(22), current);
                releases++;
                return throws ? throw failure : false;
            };
            if (throws)
                Assert.AreSame(failure, Assert.ThrowsException<InvalidOperationException>(
                    () => HookRegistrationPolicy.Replace(ref current, () => Handle(22), unhook)));
            else HookRegistrationPolicy.Replace(ref current, () => Handle(22), unhook);
            Assert.AreEqual(Handle(22), current);
            Assert.AreEqual(1, releases);
            // false/throw does not establish whether the old native registration
            // still exists. Native failure/startup/shutdown ownership is separate.
        }

        [TestMethod]
        public void FailedAttemptPreservesTheExistingStrictRetryCadence()
        {
            HHOOK current = Handle(11);
            DateTime last = Epoch;
            DateTime now = Epoch.AddSeconds(6);
            bool fail = true;
            int installs = 0;
            int releases = 0;
            Func<DateTime> clock = () => now;
            Func<HHOOK> install = () => { installs++; return fail ? default : Handle(22); };
            Func<HHOOK, bool> unhook = _ => { releases++; return true; };
            HookRegistrationPolicy.RefreshIfIdle(ref current, ref last, IdleInterval, clock, install, unhook);
            Assert.AreEqual(now, last, "A failed admitted attempt still updates the retry timestamp.");
            now = Epoch.AddSeconds(7);
            HookRegistrationPolicy.RefreshIfIdle(ref current, ref last, IdleInterval, clock, install, unhook);
            now = Epoch.AddSeconds(11);
            HookRegistrationPolicy.RefreshIfIdle(ref current, ref last, IdleInterval, clock, install, unhook);
            Assert.AreEqual(1, installs, "The existing >5 seconds test must not become >=5 or retry every tick.");
            Assert.AreEqual(Handle(11), current);
            Assert.AreEqual(0, releases);
            fail = false;
            now = Epoch.AddSeconds(12);
            HookRegistrationPolicy.RefreshIfIdle(ref current, ref last, IdleInterval, clock, install, unhook);
            Assert.AreEqual(2, installs);
            Assert.AreEqual(1, releases);
            Assert.AreEqual(now, last);
            Assert.AreEqual(Handle(22), current);
        }

        [TestMethod]
        public void AdmittedAttemptTakesTheSecondTimestampBeforeInstall()
        {
            HHOOK current = Handle(11);
            DateTime last = Epoch;
            int reads = 0;
            HookRegistrationPolicy.RefreshIfIdle(ref current, ref last, IdleInterval,
                () => Epoch.AddSeconds(++reads == 1 ? 6 : 7),
                () => { Assert.AreEqual(Epoch.AddSeconds(7), last); return Handle(22); }, _ => true);
            Assert.AreEqual(2, reads);
            Assert.AreEqual(Epoch.AddSeconds(7), last);
        }

        [TestMethod]
        public void RecentActivityAndBackwardClockDoNotCauseExtraInstall()
        {
            HHOOK current = Handle(11);
            DateTime last = Epoch.AddSeconds(10);
            DateTime now = Epoch;
            int installs = 0;
            Func<DateTime> clock = () => now;
            Func<HHOOK> install = () => { installs++; return Handle(22); };
            Func<HHOOK, bool> unhook = _ => true;
            foreach (int seconds in new[] { 9, 10, 14, 15 })
            {
                now = Epoch.AddSeconds(seconds);
                HookRegistrationPolicy.RefreshIfIdle(ref current, ref last, IdleInterval, clock, install, unhook);
            }
            Assert.AreEqual(0, installs);
            Assert.AreEqual(Epoch.AddSeconds(10), last);
            now = Epoch.AddSeconds(16);
            HookRegistrationPolicy.RefreshIfIdle(ref current, ref last, IdleInterval, clock, install, unhook);
            Assert.AreEqual(1, installs);
        }

        [TestMethod]
        public void NotDueTickDoesNotAllocateOrCallNativeAdapters()
        {
            HHOOK current = Handle(11);
            DateTime last = Epoch;
            Func<DateTime> clock = static () => Epoch;
            Func<HHOOK> install = static () => throw new AssertFailedException("Unexpected install");
            Func<HHOOK, bool> unhook = static _ => throw new AssertFailedException("Unexpected unhook");
            for (int warmup = 0; warmup < 1000; warmup++)
                HookRegistrationPolicy.RefreshIfIdle(ref current, ref last, IdleInterval, clock, install, unhook);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int tick = 0; tick < 1000; tick++)
                HookRegistrationPolicy.RefreshIfIdle(ref current, ref last, IdleInterval, clock, install, unhook);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0L, allocated);
            Assert.AreEqual(Handle(11), current);
        }

        [TestMethod]
        public void HookReplacementCounterScenario()
        {
            var native = new FakeRegistrations();
            DateTime now = Epoch;
            DateTime last = Epoch;
            int clockReads = 0;
            int preserved = 0;
            int cleanups = 0;
            Func<DateTime> clock = () => { clockReads++; return now; };
            Func<HHOOK> install = native.Install;
            Func<HHOOK, bool> unhook = native.Unhook;
            for (int cycle = 0; cycle < 100; cycle++)
            {
                HHOOK current = native.CreateInitial();
                HHOOK original = current;
                native.FailInstall = true;
                now = last.AddSeconds(6);
                HookRegistrationPolicy.RefreshIfIdle(ref current, ref last, IdleInterval, clock, install, unhook);
                if (current == original && native.Live.Contains(original)) preserved++;
                now = last.AddSeconds(1);
                HookRegistrationPolicy.RefreshIfIdle(ref current, ref last, IdleInterval, clock, install, unhook);
                native.FailInstall = false;
                now = last.AddSeconds(6);
                HookRegistrationPolicy.RefreshIfIdle(ref current, ref last, IdleInterval, clock, install, unhook);
                Assert.AreNotEqual(default(HHOOK), current);
                Assert.AreEqual(1, native.Live.Count);
                Assert.IsTrue(native.Unhook(current));
                cleanups++;
                Assert.AreEqual(0, native.Live.Count);
            }
            Assert.AreEqual(200, native.Installs);
            Assert.AreEqual(200, native.OwnedUnhooks);
            Assert.AreEqual(500, clockReads);
            // Same counter can describe the mechanical old-policy baseline;
            // the behavioral tests above independently require preservation.
            Emit("cycles", 100);
            Emit("install-calls", native.Installs);
            Emit("preserved-on-failure", preserved);
            Emit("zero-unhook-calls", native.ZeroUnhooks);
            Emit("owned-unhook-calls", native.OwnedUnhooks);
            Emit("fixture-normal-exit-cleanups", cleanups);
            Emit("maximum-fake-live-handles", native.MaximumLive);
            Emit("remaining-fake-live-handles", native.Live.Count);
            Emit("retry-clock-reads", clockReads);
        }

        private static void Emit(string metric, int value) => Console.WriteLine($"PERFCOUNTER hook-replacement {metric} {value}");

        private sealed class FakeRegistrations
        {
            internal readonly HashSet<HHOOK> Live = [];
            internal bool FailInstall;
            internal int Installs;
            internal int OwnedUnhooks;
            internal int ZeroUnhooks;
            internal int MaximumLive;
            private int m_next;

            internal HHOOK CreateInitial()
            {
                HHOOK handle = Handle(++m_next);
                Assert.IsTrue(Live.Add(handle));
                MaximumLive = Math.Max(MaximumLive, Live.Count);
                return handle;
            }
            internal HHOOK Install()
            {
                Installs++;
                return FailInstall ? default : CreateInitial();
            }
            internal bool Unhook(HHOOK handle)
            {
                if (handle == IntPtr.Zero) { ZeroUnhooks++; return false; }
                OwnedUnhooks++;
                return Live.Remove(handle);
            }
        }
    }
}
