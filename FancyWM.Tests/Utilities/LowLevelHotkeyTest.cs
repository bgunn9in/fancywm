#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Threading;

using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public partial class LowLevelHotkeyTest
    {
        [TestMethod]
        public void PressRepeatAndDirtyReleasePreserveSuppression()
        {
            RunSta(() =>
            {
                var result = Observe([new([], KeyCode.A)],
                    [new(KeyCode.A, true), new(KeyCode.A, true), new(KeyCode.A, false), new(KeyCode.A, false)]);
                CollectionAssert.AreEqual(new[] { true, true, true, false }, result.Handled);
                CollectionAssert.AreEqual(new[] { new Emission(0, 0), new Emission(1, 0) }, result.Emissions);
            });
        }

        [TestMethod]
        public void ReleaseScanUsesCurrentModifiersAndDoesNotSuppress()
        {
            RunSta(() =>
            {
                var result = Observe([new([KeyCode.LeftCtrl], KeyCode.A, ScanOnRelease: true)],
                    [new(KeyCode.LeftCtrl, true), new(KeyCode.A, true), new(KeyCode.A, false),
                     new(KeyCode.A, true), new(KeyCode.LeftCtrl, false), new(KeyCode.A, false)]);
                CollectionAssert.AreEqual(new bool[6], result.Handled);
                CollectionAssert.AreEqual(new[] { new Emission(2, 0) }, result.Emissions);
            });
        }

        [TestMethod]
        public void ClearOnMissCancelsActivationWithoutClearingAnotherBinding()
        {
            RunSta(() =>
            {
                var result = Observe(
                    [new([KeyCode.LeftCtrl], KeyCode.A, ClearModifiersOnMiss: true),
                     new([KeyCode.LeftCtrl], KeyCode.A, ClearModifiersOnMiss: false)],
                    [new(KeyCode.LeftCtrl, true), new(KeyCode.X, true), new(KeyCode.A, true),
                     new(KeyCode.A, false), new(KeyCode.LeftCtrl, false)]);
                CollectionAssert.AreEqual(new[] { false, false, true, true, false }, result.Handled);
                CollectionAssert.AreEqual(new[] { new Emission(2, 1) }, result.Emissions);
            });
        }

        [TestMethod]
        public void SideAgnosticMainKeyKeepsExistingExactModifierTracking()
        {
            RunSta(() =>
            {
                // Current production remaps the main/input key, but modifier
                // membership is deliberately characterized using the raw key.
                var result = Observe(
                    [new([KeyCode.LeftCtrl], KeyCode.LeftShift, SideAgnostic: true),
                     new([KeyCode.LeftCtrl], KeyCode.LeftShift, SideAgnostic: false)],
                    [new(KeyCode.LeftCtrl, true), new(KeyCode.RightShift, true), new(KeyCode.RightShift, false),
                     new(KeyCode.LeftCtrl, false), new(KeyCode.RightCtrl, true), new(KeyCode.LeftShift, true),
                     new(KeyCode.LeftShift, false), new(KeyCode.RightCtrl, false)]);
                CollectionAssert.AreEqual(new[] { false, true, true, false, false, false, false, false }, result.Handled);
                CollectionAssert.AreEqual(new[] { new Emission(1, 0) }, result.Emissions);
            });
        }

        [TestMethod]
        public void HandledEventStillVisitsEverySubscriberInRegistrationOrder()
        {
            RunSta(() =>
            {
                var result = Observe([new([], KeyCode.A), new([], KeyCode.A)],
                    [new(KeyCode.A, true), new(KeyCode.A, false)]);
                CollectionAssert.AreEqual(new[] { true, true }, result.Handled);
                CollectionAssert.AreEqual(new[] { new Emission(0, 0), new Emission(0, 1) }, result.Emissions);
                var alreadyHandled = Observe([new([], KeyCode.B)], [new(KeyCode.A, true)], initiallyHandled: true);
                CollectionAssert.AreEqual(new[] { true }, alreadyHandled.Handled);
                Assert.AreEqual(0, alreadyHandled.Emissions.Length);
            });
        }

        [DataTestMethod]
        [DataRow(0)]
        [DataRow(10)]
        [DataRow(50)]
        public void SeededCombinedCallbacksMatchIndependentBindingLifetimes(int hotkeyCount)
        {
            RunSta(() =>
            {
                var specifications = CreateSpecifications(hotkeyCount);
                var keys = new[] { KeyCode.A, KeyCode.B, KeyCode.C, KeyCode.Z, KeyCode.F24,
                    KeyCode.LeftCtrl, KeyCode.RightCtrl, KeyCode.LeftShift, KeyCode.RightShift,
                    KeyCode.LeftAlt, KeyCode.RightAlt, KeyCode.LWin, KeyCode.RWin, KeyCode.CapsLock };
                var random = new Random(0x31_2026);
                var inputs = MatchingInputs.Concat(Enumerable.Range(0, 300)
                    .Select(_ => new KeyInput(keys[random.Next(keys.Length)], random.Next(2) == 1))).ToArray();
                var expectedHandled = new bool[inputs.Length];
                var expectedEmissions = new List<Emission>();
                for (int index = 0; index < specifications.Length; index++)
                {
                    // Differential oracle uses independent production instances,
                    // not a second matcher implementation or a timed model.
                    var isolated = Observe([specifications[index]], inputs);
                    for (int input = 0; input < inputs.Length; input++) expectedHandled[input] |= isolated.Handled[input];
                    expectedEmissions.AddRange(isolated.Emissions.Select(emission => emission with { Binding = index }));
                }
                var combined = Observe(specifications, inputs);
                CollectionAssert.AreEqual(expectedHandled, combined.Handled);
                CollectionAssert.AreEqual(expectedEmissions.OrderBy(emission => emission.Input)
                    .ThenBy(emission => emission.Binding).ToArray(), combined.Emissions);
            });
        }

        [TestMethod]
        public void RepeatedDisposeReleasesSubscriptionsAndPendingNotificationsAcross100Dispatchers()
        {
            for (int cycle = 0; cycle < 100; cycle++)
            {
                RunSta(() =>
                {
                    var hook = CreateNativeFreeHook();
                    var hotkey = CreateHotkey(hook, new([], KeyCode.A));
                    int emitted = 0;
                    hotkey.Pressed += (_, _) => emitted++;
                    var input = new LowLevelKeyboardHook.KeyStateChangedEventArgs(KeyCode.A, true);
                    Subscribers(hook)!(hook, ref input);
                    Assert.IsTrue(input.Handled);
                    hotkey.Dispose();
                    hotkey.Dispose();
                    Assert.IsNull(Subscribers(hook));
                    DrainDispatcher();
                    Assert.AreEqual(0, emitted);
                });
            }
        }

        [TestMethod]
        public void AlreadyClaimedNotificationMayCompleteWithoutHoldingTheOwnerGate()
        {
            RunSta(() =>
            {
                var hook = CreateNativeFreeHook();
                using var hotkey = CreateHotkey(hook, new([], KeyCode.A));
                using var disposed = new ManualResetEventSlim();
                Thread? disposer = null;
                bool disposedInsideHandler = false;
                int stages = 0;
                hotkey.Pressed += (_, _) =>
                {
                    stages++;
                    disposer = new Thread(() => { hotkey.Dispose(); disposed.Set(); }) { IsBackground = true };
                    disposer.Start();
                    disposedInsideHandler = disposed.Wait(TimeSpan.FromSeconds(5));
                    // This notification was already claimed. Disposal must not
                    // wait for arbitrary user code or stop its current stack.
                    stages++;
                };
                var input = new LowLevelKeyboardHook.KeyStateChangedEventArgs(KeyCode.A, true);
                Subscribers(hook)!(hook, ref input);
                try { DrainDispatcher(); }
                finally
                {
                    if (disposer != null) Assert.IsTrue(disposer.Join(TimeSpan.FromSeconds(5)));
                }
                Assert.IsTrue(disposedInsideHandler, "Pressed user code must execute outside the owner gate.");
                Assert.AreEqual(2, stages);
                Assert.IsNull(Subscribers(hook));
            });
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void DisposeDuringAnAdmittedPostPreservesEarlierSuppressionAndRejectsNotification(bool initiallyHandled)
        {
            RunSta(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                var hook = CreateNativeFreeHook();
                using var hotkey = CreateHotkey(hook, new([], KeyCode.A));
                using var enteredPost = new ManualResetEventSlim();
                using var releasePost = new ManualResetEventSlim();
                var captured = Subscribers(hook)!;
                var input = new LowLevelKeyboardHook.KeyStateChangedEventArgs(KeyCode.A, true) { Handled = initiallyHandled };
                Exception? failure = null;
                int emitted = 0;
                dispatcher.Hooks.OperationPosted += HoldAdmittedPost;
                var producer = new Thread(() =>
                {
                    try { captured(hook, ref input); }
                    catch (Exception exception) { failure = exception; }
                }) { IsBackground = true };
                producer.SetApartmentState(ApartmentState.STA);
                producer.Start();
                try
                {
                    Assert.IsTrue(enteredPost.Wait(TimeSpan.FromSeconds(5)));
                    // The post was admitted while alive and is now paused in a
                    // real Dispatcher observer. Dispose closes new admission;
                    // it does not wait for this already-admitted post to return.
                    hotkey.Dispose();
                    hotkey.Pressed += (_, _) => emitted++;
                    Assert.IsNull(Subscribers(hook));
                }
                finally
                {
                    releasePost.Set();
                    Assert.IsTrue(producer.Join(TimeSpan.FromSeconds(5)));
                    dispatcher.Hooks.OperationPosted -= HoldAdmittedPost;
                }
                if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
                Assert.AreEqual(initiallyHandled, input.Handled,
                    "The completed in-flight post must not newly suppress input after Dispose returns.");
                DrainDispatcher();
                Assert.AreEqual(0, emitted);
                void HoldAdmittedPost(object? sender, DispatcherHookEventArgs args)
                {
                    enteredPost.Set();
                    if (!releasePost.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Controlled post was not released.");
                }
            });
        }

        [TestMethod]
        public void DisposedOwnerCannotResumeAQueuedNotificationWhenAnObserverAttaches()
        {
            RunSta(() =>
            {
                var hook = CreateNativeFreeHook();
                using var hotkey = CreateHotkey(hook, new([], KeyCode.A));
                var input = new LowLevelKeyboardHook.KeyStateChangedEventArgs(KeyCode.A, true);
                Subscribers(hook)!(hook, ref input);
                hotkey.Dispose();
                int emitted = 0;
                hotkey.Pressed += (_, _) => emitted++;
                DrainDispatcher();
                Assert.AreEqual(0, emitted, "A queued callback belongs to the disposed lifetime, regardless of later subscriptions.");
            });
        }

        [TestMethod]
        public void DisposeReenteredDuringPostingDoesNotSuppressOrNotify()
        {
            RunSta(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                var hook = CreateNativeFreeHook();
                using var hotkey = CreateHotkey(hook, new([], KeyCode.A));
                int emitted = 0;
                dispatcher.Hooks.OperationPosted += DisposeWhilePosting;
                try
                {
                    var input = new LowLevelKeyboardHook.KeyStateChangedEventArgs(KeyCode.A, true);
                    Subscribers(hook)!(hook, ref input);
                    Assert.IsFalse(input.Handled, "An in-flight post cancelled by disposal must not suppress input afterward.");
                }
                finally { dispatcher.Hooks.OperationPosted -= DisposeWhilePosting; }
                DrainDispatcher();
                Assert.AreEqual(0, emitted);
                Assert.IsNull(Subscribers(hook));
                void DisposeWhilePosting(object? sender, DispatcherHookEventArgs args)
                {
                    hotkey.Dispose();
                    hotkey.Pressed += (_, _) => emitted++;
                }
            });
        }

        [TestMethod]
        public void DisposeRejectsAlreadyCapturedSubscriberAcross100Dispatchers()
        {
            for (int cycle = 0; cycle < 100; cycle++)
            {
                RunSta(() =>
                {
                    var dispatcher = Dispatcher.CurrentDispatcher;
                    var hook = CreateNativeFreeHook();
                    using var hotkey = CreateHotkey(hook, new([], KeyCode.A));
                    int emitted = 0, posted = 0;
                    hotkey.Pressed += (_, _) => emitted++;
                    var captured = Subscribers(hook)!;
                    var first = new LowLevelKeyboardHook.KeyStateChangedEventArgs(KeyCode.A, true);
                    captured(hook, ref first);
                    hotkey.Dispose();
                    dispatcher.Hooks.OperationPosted += OnPosted;
                    try
                    {
                        var late = new LowLevelKeyboardHook.KeyStateChangedEventArgs(KeyCode.A, true);
                        captured(hook, ref late);
                        Assert.IsFalse(late.Handled, "A captured callback must not suppress input after its owner is disposed.");
                        Assert.AreEqual(0, posted, "A disposed subscriber must not schedule more UI work.");
                    }
                    finally { dispatcher.Hooks.OperationPosted -= OnPosted; }
                    DrainDispatcher();
                    Assert.AreEqual(0, emitted);
                    Assert.IsNull(Subscribers(hook));
                    void OnPosted(object? sender, DispatcherHookEventArgs args) => posted++;
                });
            }
        }

        private static Observed Observe(Specification[] specifications, KeyInput[] inputs, bool initiallyHandled = false)
        {
            var hook = CreateNativeFreeHook();
            var hotkeys = new List<LowLevelHotkey>();
            var emissions = new List<Emission>();
            var handled = new bool[inputs.Length];
            int currentInput = -1;
            try
            {
                for (int index = 0; index < specifications.Length; index++)
                {
                    int binding = index;
                    var hotkey = CreateHotkey(hook, specifications[index]);
                    hotkey.Pressed += (_, _) => emissions.Add(new(currentInput, binding));
                    hotkeys.Add(hotkey);
                }
                var subscribers = Subscribers(hook);
                for (currentInput = 0; currentInput < inputs.Length; currentInput++)
                {
                    var input = new LowLevelKeyboardHook.KeyStateChangedEventArgs(inputs[currentInput].Key, inputs[currentInput].Pressed)
                        { Handled = initiallyHandled };
                    subscribers?.Invoke(hook, ref input);
                    handled[currentInput] = input.Handled;
                    DrainDispatcher();
                }
                return new(handled, emissions.ToArray());
            }
            finally
            {
                foreach (var hotkey in hotkeys) hotkey.Dispose();
                Assert.IsNull(Subscribers(hook));
            }
        }

        private static LowLevelHotkey CreateHotkey(LowLevelKeyboardHook hook, Specification specification)
            => new(hook, specification.Modifiers, specification.Key)
            {
                ScanOnRelease = specification.ScanOnRelease,
                HideKeyPress = specification.HideKeyPress,
                ClearModifiersOnMiss = specification.ClearModifiersOnMiss,
                SideAgnostic = specification.SideAgnostic,
            };

        private static LowLevelKeyboardHook CreateNativeFreeHook()
        {
            // This object is ONLY an event owner. No constructor, HookProc,
            // native hook, thread, Dispose or finalizer may run in this fixture.
            var hook = (LowLevelKeyboardHook)RuntimeHelpers.GetUninitializedObject(typeof(LowLevelKeyboardHook));
            GC.SuppressFinalize(hook);
            return hook;
        }

        private static readonly FieldInfo SubscriberField = typeof(LowLevelKeyboardHook)
            .GetField("KeyStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;

        private static LowLevelKeyboardHook.KeyStateChangedEventHandler? Subscribers(LowLevelKeyboardHook hook)
            => (LowLevelKeyboardHook.KeyStateChangedEventHandler?)SubscriberField.GetValue(hook);

        private static void DrainDispatcher()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }

        private static void RunSta(Action action)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception exception) { failure = exception; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "Controlled Dispatcher did not complete.");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private sealed record Specification(KeyCode[] Modifiers, KeyCode Key, bool ScanOnRelease = false,
            bool HideKeyPress = true, bool ClearModifiersOnMiss = false, bool SideAgnostic = false);
        private readonly record struct KeyInput(KeyCode Key, bool Pressed);
        private readonly record struct Emission(int Input, int Binding);
        private sealed record Observed(bool[] Handled, Emission[] Emissions);
    }
}
