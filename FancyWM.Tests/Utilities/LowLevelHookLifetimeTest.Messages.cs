#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;

using FancyWM.DllImports;
using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    public partial class LowLevelHookLifetimeTest
    {
        private const uint OrdinaryMessage = 0x8001;
        private const int MessageErrorCode = 87;

        [DataTestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public void GetMessageFailurePreservesNativeErrorAndReleasesEveryOwnedResource(
            bool mouse, bool timerOutput)
        {
            var lifetime = NewMessageLifetime();
            HHOOK current = default;
            var calls = new List<string>();
            int reads = 0;
            int Read(out MSG message, out int errorCode)
            {
                calls.Add($"read:{++reads}");
                message = new MSG { message = timerOutput ? Constants.WM_TIMER : OrdinaryMessage };
                errorCode = MessageErrorCode;
                // The second value lets the old boolean loop finish without
                // hanging its regression process after consuming the bad output.
                return reads == 1 ? -1 : 0;
            }

            Exception? observed = Capture(() => Run(mouse, ref current,
                () => { calls.Add("install:11"); return Handle(11); },
                () => { calls.Add("timer:7"); return 7; },
                (ref HHOOK hook) => HookRegistrationPolicy.RunMessageLoop(ref hook, lifetime,
                    Read, (ref HHOOK _) => calls.Add("refresh"),
                    (in MSG _) => calls.Add("dispatch")),
                timer => { calls.Add($"kill:{timer}"); return true; },
                hook => { calls.Add($"unhook:{hook.Value}"); return true; }));

            Assert.IsInstanceOfType(observed, typeof(Win32Exception));
            Assert.AreEqual(MessageErrorCode, ((Win32Exception)observed!).NativeErrorCode);
            StringAssert.Contains(observed.Message, "GetMessage");
            CollectionAssert.AreEqual(new[]
            {
                "install:11", "timer:7", "read:1", "kill:7", "unhook:11",
            }, calls);
            Assert.AreEqual(default(HHOOK), current);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void QuitResultDoesNotDispatchOutputOrReadAgain(bool mouse)
        {
            var lifetime = NewMessageLifetime();
            HHOOK current = default;
            var calls = new List<string>();
            int Read(out MSG message, out int errorCode)
            {
                calls.Add("quit");
                message = new MSG { message = Constants.WM_TIMER };
                errorCode = MessageErrorCode;
                return 0;
            }

            Run(mouse, ref current, () => Handle(21), () => 8,
                (ref HHOOK hook) => HookRegistrationPolicy.RunMessageLoop(ref hook, lifetime,
                    Read, (ref HHOOK _) => calls.Add("refresh"),
                    (in MSG _) => calls.Add("dispatch")),
                timer => { calls.Add($"kill:{timer}"); return true; },
                hook => { calls.Add($"unhook:{hook.Value}"); return true; });

            CollectionAssert.AreEqual(new[] { "quit", "kill:8", "unhook:21" }, calls);
            Assert.AreEqual(default(HHOOK), current);
        }

        [DataTestMethod]
        [DataRow(false, 1, false)]
        [DataRow(false, 1, true)]
        [DataRow(true, 1, false)]
        [DataRow(true, 1, true)]
        [DataRow(false, 0, true)]
        [DataRow(true, 0, true)]
        [DataRow(false, -1, true)]
        [DataRow(true, -1, true)]
        public void StopDuringMessageReadRejectsRetrievedOutput(bool mouse, int result, bool timerOutput)
        {
            int posts = 0;
            var lifetime = new HookRegistrationPolicy.ThreadLifetime(_ => { posts++; return true; });
            Assert.IsTrue(lifetime.PublishQueue(101));
            HHOOK current = default;
            var calls = new List<string>();
            int Read(out MSG message, out int errorCode)
            {
                calls.Add("read");
                lifetime.RequestStop();
                message = new MSG { message = timerOutput ? Constants.WM_TIMER : OrdinaryMessage };
                errorCode = MessageErrorCode;
                return result;
            }

            Exception? observed = Capture(() => Run(mouse, ref current, () => Handle(31), () => 9,
                (ref HHOOK hook) => HookRegistrationPolicy.RunMessageLoop(ref hook, lifetime,
                    Read, (ref HHOOK _) => calls.Add("refresh"),
                    (in MSG _) => calls.Add("dispatch")),
                timer => { calls.Add($"kill:{timer}"); return true; },
                hook => { calls.Add($"unhook:{hook.Value}"); return true; }));

            if (result == -1)
            {
                Assert.IsInstanceOfType(observed, typeof(Win32Exception));
                Assert.AreEqual(MessageErrorCode, ((Win32Exception)observed!).NativeErrorCode,
                    "A stop request must not mask the native read failure.");
            }
            else { Assert.IsNull(observed); }
            Assert.AreEqual(1, posts);
            CollectionAssert.AreEqual(new[] { "read", "kill:9", "unhook:31" }, calls);
            Assert.AreEqual(default(HHOOK), current);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void StopBeforeMessageLoopSkipsReadAndEveryMessageEffect(bool mouse)
        {
            var lifetime = NewMessageLifetime();
            lifetime.RequestStop();
            HHOOK current = default;
            var calls = new List<string>();
            int Read(out MSG message, out int errorCode)
                => throw new AssertFailedException("A stopped owner must not wait for a message.");

            Run(mouse, ref current, () => Handle(41), () => 10,
                (ref HHOOK hook) => HookRegistrationPolicy.RunMessageLoop(ref hook, lifetime,
                    Read, (ref HHOOK _) => calls.Add("refresh"),
                    (in MSG _) => calls.Add("dispatch")),
                timer => { calls.Add($"kill:{timer}"); return true; },
                hook => { calls.Add($"unhook:{hook.Value}"); return true; });

            CollectionAssert.AreEqual(new[] { "kill:10", "unhook:41" }, calls);
            Assert.AreEqual(default(HHOOK), current);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void NormalMessagesPreserveDispatchAndWatchdogOrder(bool mouse)
        {
            var lifetime = NewMessageLifetime();
            HHOOK current = default;
            var calls = new List<string>();
            int reads = 0;
            int Read(out MSG message, out int errorCode)
            {
                calls.Add($"read:{++reads}");
                message = new MSG
                {
                    message = reads == 2 ? Constants.WM_TIMER : OrdinaryMessage + (uint)reads,
                };
                errorCode = MessageErrorCode; // A success must ignore stale error output.
                return reads == 4 ? 0 : reads;
            }

            Run(mouse, ref current,
                () => { calls.Add("install:51"); return Handle(51); },
                () => { calls.Add("timer:11"); return 11; },
                (ref HHOOK hook) => HookRegistrationPolicy.RunMessageLoop(ref hook, lifetime,
                    Read,
                    (ref HHOOK refreshed) =>
                    {
                        calls.Add($"refresh:{refreshed.Value}");
                        HookRegistrationPolicy.Replace(ref refreshed,
                            () => { calls.Add("install:52"); return Handle(52); },
                            previous => { calls.Add($"replace-unhook:{previous.Value}"); return true; });
                    },
                    (in MSG message) => calls.Add($"dispatch:{message.message}")),
                timer => { calls.Add($"kill:{timer}"); return true; },
                hook => { calls.Add($"unhook:{hook.Value}"); return true; });

            CollectionAssert.AreEqual(new[]
            {
                "install:51", "timer:11", "read:1", $"dispatch:{OrdinaryMessage + 1}",
                "read:2", "refresh:51", "install:52", "replace-unhook:51",
                "read:3", $"dispatch:{OrdinaryMessage + 3}", "read:4", "kill:11", "unhook:52",
            }, calls);
            Assert.AreEqual(default(HHOOK), current);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void GetMessageFailureRemainsPrimaryThroughLatestHookCleanupFailures(bool mouse)
        {
            var lifetime = NewMessageLifetime();
            HHOOK current = default;
            var calls = new List<string>();
            var timerFailure = new InvalidOperationException("timer cleanup failed");
            var hookFailure = new ApplicationException("hook cleanup failed");
            int reads = 0;
            int Read(out MSG message, out int errorCode)
            {
                calls.Add($"read:{++reads}");
                message = new MSG { message = reads == 1 ? Constants.WM_TIMER : OrdinaryMessage };
                errorCode = MessageErrorCode;
                return reads == 1 ? 1 : reads == 2 ? -1 : 0;
            }

            Exception? observed = Capture(() => Run(mouse, ref current, () => Handle(61), () => 12,
                (ref HHOOK hook) => HookRegistrationPolicy.RunMessageLoop(ref hook, lifetime, Read,
                    (ref HHOOK refreshed) => HookRegistrationPolicy.Replace(ref refreshed,
                        () => { calls.Add("install:62"); return Handle(62); },
                        previous => { calls.Add($"replace-unhook:{previous.Value}"); return true; }),
                    (in MSG _) => calls.Add("dispatch")),
                timer => { calls.Add($"kill:{timer}"); throw timerFailure; },
                hook => { calls.Add($"unhook:{hook.Value}"); throw hookFailure; }));

            Assert.IsInstanceOfType(observed, typeof(Win32Exception));
            Assert.AreEqual(MessageErrorCode, ((Win32Exception)observed!).NativeErrorCode);
            var cleanup = observed.Data[HookRegistrationPolicy.CleanupExceptionsDataKey]
                as AggregateException ?? throw new AssertFailedException("Cleanup failures were not retained.");
            CollectionAssert.AreEqual(new Exception[] { timerFailure, hookFailure },
                new List<Exception>(cleanup.InnerExceptions));
            CollectionAssert.AreEqual(new[]
            {
                "read:1", "install:62", "replace-unhook:61", "read:2", "kill:12", "unhook:62",
            }, calls);
            Assert.AreEqual(Handle(62), current);
        }

        [DataTestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public void MessageEffectFailurePreservesOriginalErrorAndOwnedCleanup(bool mouse, bool timer)
        {
            var lifetime = NewMessageLifetime();
            HHOOK current = default;
            var calls = new List<string>();
            var primary = new InvalidOperationException("message effect failed");
            int Read(out MSG message, out int errorCode)
            {
                calls.Add("read");
                message = new MSG { message = timer ? Constants.WM_TIMER : OrdinaryMessage };
                errorCode = 0;
                return 1;
            }

            Exception? observed = Capture(() => Run(mouse, ref current, () => Handle(71), () => 13,
                (ref HHOOK hook) => HookRegistrationPolicy.RunMessageLoop(ref hook, lifetime, Read,
                    (ref HHOOK _) => { calls.Add("refresh"); throw primary; },
                    (in MSG _) => { calls.Add("dispatch"); throw primary; }),
                timerId => { calls.Add($"kill:{timerId}"); return true; },
                hook => { calls.Add($"unhook:{hook.Value}"); return true; }));

            Assert.AreSame(primary, observed);
            CollectionAssert.AreEqual(new[]
            {
                "read", timer ? "refresh" : "dispatch", "kill:13", "unhook:71",
            }, calls);
            Assert.AreEqual(default(HHOOK), current);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void OneHundredGetMessageFailuresLeaveNoFakeOwners(bool mouse)
        {
            var live = new HashSet<HHOOK>();
            int effects = 0;
            int timerReleases = 0;
            int hookReleases = 0;
            for (int cycle = 1; cycle <= 100; cycle++)
            {
                var lifetime = NewMessageLifetime();
                HHOOK current = default;
                HHOOK owned = Handle(1000 + cycle);
                int reads = 0;
                int Read(out MSG message, out int errorCode)
                {
                    message = new MSG { message = Constants.WM_TIMER };
                    errorCode = MessageErrorCode;
                    return ++reads == 1 ? -1 : 0;
                }

                Exception? observed = Capture(() => Run(mouse, ref current,
                    () => { Assert.IsTrue(live.Add(owned)); return owned; }, () => (nuint)cycle,
                    (ref HHOOK hook) => HookRegistrationPolicy.RunMessageLoop(ref hook, lifetime, Read,
                        (ref HHOOK _) => effects++, (in MSG _) => effects++),
                    _ => { timerReleases++; return true; },
                    hook => { hookReleases++; return live.Remove(hook); }));

                Assert.IsInstanceOfType(observed, typeof(Win32Exception));
                Assert.AreEqual(MessageErrorCode, ((Win32Exception)observed!).NativeErrorCode);
                Assert.AreEqual(default(HHOOK), current);
            }

            Assert.AreEqual(0, effects);
            Assert.AreEqual(100, timerReleases);
            Assert.AreEqual(100, hookReleases);
            Assert.AreEqual(0, live.Count);
        }

        private static HookRegistrationPolicy.ThreadLifetime NewMessageLifetime()
            => new(_ => throw new AssertFailedException("No native thread ID is published by this fixture."));
    }
}
