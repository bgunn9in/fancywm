#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;

using FancyWM.DllImports;
using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    // Runs the production keyboard/mouse ownership entrypoints synchronously with
    // fake handles and callbacks. No hook, message queue, HWND, timer or input is used.
    [TestClass]
    public partial class LowLevelHookLifetimeTest
    {
        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void ExceptionalLoopExitAttemptsEveryOwnedCleanupAndPreservesPrimaryFailure(bool mouse)
        {
            HHOOK current = default;
            var calls = new List<string>();
            var primary = new InvalidOperationException("dispatch failed");

            Exception? observed = Capture(() => Run(mouse, ref current,
                () => { calls.Add("install:11"); return Handle(11); },
                () => { calls.Add("timer:7"); return 7; },
                (ref HHOOK hook) =>
                {
                    calls.Add("loop");
                    HookRegistrationPolicy.Replace(ref hook,
                        () => { calls.Add("install:22"); return Handle(22); },
                        previous => { calls.Add($"replace-unhook:{previous.Value}"); return true; });
                    throw primary;
                },
                timer => { calls.Add($"kill:{timer}"); return true; },
                hook => { calls.Add($"unhook:{hook.Value}"); return true; }));

            Assert.AreSame(primary, observed);
            CollectionAssert.AreEqual(new[]
            {
                "install:11", "timer:7", "loop", "install:22", "replace-unhook:11",
                "kill:7", "unhook:22",
            }, calls);
            Assert.AreEqual(default, current);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void ThrowingReplacementReleaseStillCleansPublishedReplacement(bool mouse)
        {
            HHOOK current = default;
            var calls = new List<string>();
            var primary = new InvalidOperationException("old hook release failed");

            Exception? observed = Capture(() => Run(mouse, ref current,
                () => Handle(11), () => 7,
                (ref HHOOK hook) => HookRegistrationPolicy.Replace(ref hook,
                    () => { calls.Add("install:22"); return Handle(22); },
                    previous => { calls.Add($"replace-unhook:{previous.Value}"); throw primary; }),
                timer => { calls.Add($"kill:{timer}"); return true; },
                hook => { calls.Add($"final-unhook:{hook.Value}"); return true; }));

            Assert.AreSame(primary, observed);
            CollectionAssert.AreEqual(new[]
            {
                "install:22", "replace-unhook:11", "kill:7", "final-unhook:22",
            }, calls);
            Assert.AreEqual(default, current);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void TimerCreationFailureReleasesInstalledHookAndPreservesFailure(bool mouse)
        {
            HHOOK current = default;
            var calls = new List<string>();
            var primary = new InvalidOperationException("timer creation failed");

            Exception? observed = Capture(() => Run(mouse, ref current,
                () => { calls.Add("install:31"); return Handle(31); },
                () => { calls.Add("timer"); throw primary; },
                (ref HHOOK _) => calls.Add("loop"),
                timer => { calls.Add($"kill:{timer}"); return true; },
                hook => { calls.Add($"unhook:{hook.Value}"); return true; }));

            Assert.AreSame(primary, observed);
            CollectionAssert.AreEqual(new[] { "install:31", "timer", "unhook:31" }, calls);
            Assert.AreEqual(default, current);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void ZeroTimerFailsWithoutKillingMissingTimerAndReleasesHook(bool mouse)
        {
            HHOOK current = default;
            int loops = 0;
            int kills = 0;
            var releases = new List<HHOOK>();

            Exception? observed = Capture(() => Run(mouse, ref current,
                () => Handle(41), () => 0, (ref HHOOK _) => loops++,
                _ => { kills++; return true; },
                hook => { releases.Add(hook); return true; }));

            Assert.IsInstanceOfType(observed, typeof(Win32Exception));
            StringAssert.Contains(observed!.Message, "watchdog timer");
            Assert.AreEqual(0, loops);
            Assert.AreEqual(0, kills);
            CollectionAssert.AreEqual(new[] { Handle(41) }, releases);
            Assert.AreEqual(default, current);
        }

        [DataTestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public void FailedInitialInstallAcquiresNoOtherOwner(bool mouse, bool throws)
        {
            HHOOK current = default;
            var primary = new InvalidOperationException("install failed");
            int timers = 0;
            int loops = 0;
            int timerReleases = 0;
            int hookReleases = 0;

            Exception? observed = Capture(() => Run(mouse, ref current,
                () => throws ? throw primary : default,
                () => { timers++; return 1; },
                (ref HHOOK _) => loops++,
                _ => { timerReleases++; return true; },
                _ => { hookReleases++; return true; }));

            if (throws) { Assert.AreSame(primary, observed); }
            else { Assert.IsInstanceOfType(observed, typeof(Win32Exception)); }
            Assert.AreEqual(0, timers);
            Assert.AreEqual(0, loops);
            Assert.AreEqual(0, timerReleases);
            Assert.AreEqual(0, hookReleases);
            Assert.AreEqual(default, current);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void CleanupFailuresAreOrderedWithoutMaskingLoopFailure(bool mouse)
        {
            HHOOK current = default;
            var primary = new InvalidOperationException("loop failed");
            var timerFailure = new InvalidOperationException("timer cleanup failed");
            var hookFailure = new InvalidOperationException("hook cleanup failed");
            var calls = new List<string>();

            Exception? observed = Capture(() => Run(mouse, ref current,
                () => Handle(51), () => 9,
                (ref HHOOK _) => throw primary,
                _ => { calls.Add("timer"); throw timerFailure; },
                hook => { calls.Add($"hook:{hook.Value}"); throw hookFailure; }));

            Assert.AreSame(primary, observed);
            CollectionAssert.AreEqual(new[] { "timer", "hook:51" }, calls);
            var cleanup = observed!.Data[HookRegistrationPolicy.CleanupExceptionsDataKey]
                as AggregateException ?? throw new AssertFailedException("Cleanup failures were not retained.");
            Assert.AreEqual(2, cleanup.InnerExceptions.Count);
            Assert.AreSame(timerFailure, cleanup.InnerExceptions[0]);
            Assert.AreSame(hookFailure, cleanup.InnerExceptions[1]);
            Assert.AreEqual(Handle(51), current,
                "A failed unhook must retain the exact unresolved handle identity.");
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void TimerCleanupFailureDoesNotSkipSuccessfulHookRelease(bool mouse)
        {
            HHOOK current = default;
            var timerFailure = new InvalidOperationException("timer cleanup failed");
            var calls = new List<string>();

            Exception? observed = Capture(() => Run(mouse, ref current,
                () => Handle(61), () => 10, (ref HHOOK _) => calls.Add("loop"),
                _ => { calls.Add("timer"); throw timerFailure; },
                hook => { calls.Add($"hook:{hook.Value}"); return true; }));

            Assert.AreSame(timerFailure, observed);
            CollectionAssert.AreEqual(new[] { "loop", "timer", "hook:61" }, calls);
            Assert.AreEqual(default, current);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void FalseCleanupResultsAttemptBothOwnersAndRetainUnresolvedHook(bool mouse)
        {
            HHOOK current = default;
            var calls = new List<string>();

            Exception? observed = Capture(() => Run(mouse, ref current,
                () => Handle(66), () => 11, (ref HHOOK _) => calls.Add("loop"),
                timer => { calls.Add($"timer:{timer}"); return false; },
                hook => { calls.Add($"hook:{hook.Value}"); return false; }));

            Assert.IsInstanceOfType(observed, typeof(Win32Exception));
            StringAssert.Contains(observed!.Message, "watchdog timer");
            CollectionAssert.AreEqual(new[] { "loop", "timer:11", "hook:66" }, calls);
            var cleanup = observed.Data[HookRegistrationPolicy.CleanupExceptionsDataKey]
                as AggregateException ?? throw new AssertFailedException("Later hook cleanup failure was not retained.");
            Assert.AreEqual(1, cleanup.InnerExceptions.Count);
            Assert.IsInstanceOfType(cleanup.InnerExceptions[0], typeof(Win32Exception));
            StringAssert.Contains(cleanup.InnerExceptions[0].Message, mouse ? "WH_MOUSE_LL" : "WH_KEYBOARD_LL");
            Assert.AreEqual(Handle(66), current);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void ThreadInterruptionIsNormalExitAndReleasesEachOwnerOnce(bool mouse)
        {
            HHOOK current = default;
            int timerReleases = 0;
            int hookReleases = 0;

            Run(mouse, ref current, () => Handle(71), () => 12,
                (ref HHOOK _) => throw new ThreadInterruptedException(),
                timer => { Assert.AreEqual((nuint)12, timer); timerReleases++; return true; },
                hook => { Assert.AreEqual(Handle(71), hook); hookReleases++; return true; });

            Assert.AreEqual(1, timerReleases);
            Assert.AreEqual(1, hookReleases);
            Assert.AreEqual(default, current);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void NormalReturnReleasesEachOwnerExactlyOnce(bool mouse)
        {
            HHOOK current = default;
            var calls = new List<string>();

            Run(mouse, ref current,
                () => { calls.Add("install:81"); return Handle(81); },
                () => { calls.Add("timer:13"); return 13; },
                (ref HHOOK hook) => { calls.Add($"loop:{hook.Value}"); },
                timer => { calls.Add($"kill:{timer}"); return true; },
                hook => { calls.Add($"unhook:{hook.Value}"); return true; });

            CollectionAssert.AreEqual(new[]
            {
                "install:81", "timer:13", "loop:81", "kill:13", "unhook:81",
            }, calls);
            Assert.AreEqual(default, current);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void OneHundredExceptionalLoopsLeaveNoFakeRegistrations(bool mouse)
        {
            var live = new HashSet<HHOOK>();
            int timerReleases = 0;
            int hookReleases = 0;

            for (int cycle = 1; cycle <= 100; cycle++)
            {
                HHOOK current = default;
                HHOOK owned = Handle(1000 + cycle);
                var primary = new InvalidOperationException($"loop-{cycle}");
                Exception? observed = Capture(() => Run(mouse, ref current,
                    () => { Assert.IsTrue(live.Add(owned)); return owned; },
                    () => (nuint)cycle,
                    (ref HHOOK _) => throw primary,
                    _ => { timerReleases++; return true; },
                    hook => { hookReleases++; return live.Remove(hook); }));
                Assert.AreSame(primary, observed);
                Assert.AreEqual(default, current);
            }

            Assert.AreEqual(100, timerReleases);
            Assert.AreEqual(100, hookReleases);
            Assert.AreEqual(0, live.Count);
        }

        [TestMethod]
        public void HookLifetimeCounterScenario()
        {
            MeasureOwner(mouse: false, "keyboard");
            MeasureOwner(mouse: true, "mouse");
        }

        private static void MeasureOwner(bool mouse, string owner)
        {
            const int cycles = 100;
            var live = new HashSet<HHOOK>();
            int installedHooks = 0;
            int timerCreations = 0;
            int timerCleanupAttempts = 0;
            int exceptionalUnhookAttempts = 0;
            int originalLoopErrors = 0;
            int normalUnhookAttempts = 0;
            int normalCompletions = 0;
            int duplicateUnhooks = 0;

            for (int cycle = 1; cycle <= cycles; cycle++)
            {
                HHOOK current = default;
                HHOOK owned = Handle((mouse ? 10000 : 20000) + cycle);
                var primary = new InvalidOperationException($"{owner}-exceptional-{cycle}");
                Exception? observed = Capture(() => Run(mouse, ref current,
                    () =>
                    {
                        installedHooks++;
                        Assert.IsTrue(live.Add(owned));
                        return owned;
                    },
                    () => { timerCreations++; return (nuint)cycle; },
                    (ref HHOOK _) => throw primary,
                    _ => { timerCleanupAttempts++; return true; },
                    hook =>
                    {
                        exceptionalUnhookAttempts++;
                        if (!live.Remove(hook)) { duplicateUnhooks++; }
                        return true;
                    }));
                if (ReferenceEquals(primary, observed)) { originalLoopErrors++; }
            }

            int exceptionalRemainingHooks = live.Count;
            for (int cycle = 1; cycle <= cycles; cycle++)
            {
                HHOOK current = default;
                HHOOK owned = Handle((mouse ? 30000 : 40000) + cycle);
                Run(mouse, ref current,
                    () =>
                    {
                        installedHooks++;
                        Assert.IsTrue(live.Add(owned));
                        return owned;
                    },
                    () => { timerCreations++; return (nuint)(cycles + cycle); },
                    (ref HHOOK _) => { },
                    _ => { timerCleanupAttempts++; return true; },
                    hook =>
                    {
                        normalUnhookAttempts++;
                        if (!live.Remove(hook)) { duplicateUnhooks++; }
                        return true;
                    });
                normalCompletions++;
            }

            Assert.AreEqual(cycles * 2, installedHooks);
            Assert.AreEqual(cycles * 2, timerCreations);
            Assert.AreEqual(cycles * 2, timerCleanupAttempts);
            Assert.IsTrue(exceptionalUnhookAttempts == 0 || exceptionalUnhookAttempts == cycles);
            Assert.AreEqual(cycles - exceptionalUnhookAttempts, exceptionalRemainingHooks);
            Assert.AreEqual(cycles, originalLoopErrors);
            Assert.AreEqual(cycles, normalUnhookAttempts);
            Assert.AreEqual(cycles, normalCompletions);
            Assert.AreEqual(exceptionalRemainingHooks, live.Count);
            Assert.AreEqual(0, duplicateUnhooks);

            Counter(owner, "cycles", cycles);
            Counter(owner, "installed-hooks", installedHooks);
            Counter(owner, "timer-creations", timerCreations);
            Counter(owner, "timer-cleanup-attempts", timerCleanupAttempts);
            Counter(owner, "exceptional-unhook-attempts", exceptionalUnhookAttempts);
            Counter(owner, "exceptional-remaining-hooks", exceptionalRemainingHooks);
            Counter(owner, "original-loop-errors", originalLoopErrors);
            Counter(owner, "normal-unhook-attempts", normalUnhookAttempts);
            Counter(owner, "normal-completions", normalCompletions);
            Counter(owner, "duplicate-unhooks", duplicateUnhooks);
        }

        private static void Counter(string owner, string metric, int value)
            => Console.WriteLine($"PERFCOUNTER hook-lifetime-{owner} {metric} {value}");

        private static HHOOK Handle(int value) => new(new IntPtr(value));

        private static Exception? Capture(Action action)
        {
            try
            {
                action();
                return null;
            }
            catch (Exception error)
            {
                return error;
            }
        }

        private static void Run(bool mouse, ref HHOOK current, Func<HHOOK> install,
            Func<nuint> createTimer, HookRegistrationPolicy.MessageLoop runLoop,
            Func<nuint, bool> killTimer, Func<HHOOK, bool> unhook)
        {
            if (mouse)
            {
                LowLevelMouseHook.RunOwnedMessageLoop(ref current, install, createTimer,
                    runLoop, killTimer, unhook);
            }
            else
            {
                LowLevelKeyboardHook.RunOwnedMessageLoop(ref current, install, createTimer,
                    runLoop, killTimer, unhook);
            }
        }
    }
}
