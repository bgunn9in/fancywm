#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

using FancyWM.Controls;
using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class KeyPressBoxTest
    {
        [TestMethod]
        public void PublicDisplayConstructionDoesNotRequireFancyWmAppOrHook() => KeyPatternTestSupport.RunSta(() =>
        {
            Assert.IsFalse(Application.Current is FancyWM.App, "The fixture must not own a real FancyWM application.");
            var control = new KeyPressBox();
            try
            {
                Assert.AreEqual("<None>", Input(control).Text);
                control.InitialPattern = "LeftCtrl,A";
                Assert.AreEqual(new[] { KeyCode.LeftCtrl, KeyCode.A }.ToPrettyString(), Input(control).Text);
                Assert.IsNull(PresentationSource.FromVisual(control));
            }
            finally { Unload(control); }
        });

        [TestMethod]
        public void DisplayUpdatesAndRepeatedUnloadsDoNotAcquireRecordingListener() => KeyPatternTestSupport.RunSta(() =>
        {
            using var fixture = new ControlFixture();
            var control = fixture.Control;
            int notifications = 0;
            control.PatternChanged += (_, _) => notifications++;
            control.InitialPattern = "RightShift,B";
            Assert.AreEqual(new[] { KeyCode.RightShift, KeyCode.B }.ToPrettyString(), Input(control).Text);
            control.Pattern = new HashSet<KeyCode> { KeyCode.LeftCtrl, KeyCode.A };
            Assert.AreEqual(new[] { KeyCode.LeftCtrl, KeyCode.A }.OrderByDescending(key => (int)key).ToPrettyString(), Input(control).Text);
            control.Pattern = null;
            control.InitialPattern = "";
            control.Background = Brushes.AliceBlue;
            control.Measure(new Size(220, 32));
            control.Arrange(new Rect(0, 0, 220, 32));
            Assert.AreEqual("<None>", Input(control).Text);
            Assert.AreEqual(0, notifications);
            Assert.AreEqual(0, fixture.Created.Count);
            Unload(control);
            Unload(control);
            Assert.AreEqual(0, fixture.Created.Count);
            Assert.AreEqual(0, KeyPatternTestSupport.SubscriberCount(fixture.Hook));
        });

        [TestMethod]
        public void FirstFocusCapturesNormalizedPatternWithOriginalSender() => KeyPatternTestSupport.RunSta(() =>
        {
            using var fixture = new ControlFixture();
            int notifications = 0;
            fixture.Control.PatternChanged += (sender, e) =>
            {
                Assert.AreSame(fixture.Created.Single(), sender);
                Assert.AreSame(fixture.Control.Pattern, e.Keys);
                CollectionAssert.AreEquivalent(new[] { KeyCode.LeftCtrl, KeyCode.LeftShift, KeyCode.A }, e.Keys.ToArray());
                notifications++;
            };
            fixture.SetFocus(true);
            Assert.AreEqual(1, fixture.Created.Count);
            Assert.IsTrue(fixture.Created[0].IsListening);
            Assert.AreEqual(1, KeyPatternTestSupport.SubscriberCount(fixture.Hook));
            KeyPatternTestSupport.Send(fixture.Hook, KeyCode.RightCtrl, true);
            KeyPatternTestSupport.Send(fixture.Hook, KeyCode.RightShift, true);
            KeyPatternTestSupport.Send(fixture.Hook, KeyCode.A, true);
            KeyPatternTestSupport.Send(fixture.Hook, KeyCode.A, false);
            KeyPatternTestSupport.Send(fixture.Hook, KeyCode.RightCtrl, false);
            KeyPatternTestSupport.Send(fixture.Hook, KeyCode.RightShift, false);
            KeyPatternTestSupport.DrainDispatcher();
            Assert.AreEqual(1, notifications);
            Assert.AreEqual(fixture.Control.Pattern!.OrderByDescending(key => (int)key).ToPrettyString(), Input(fixture.Control).Text);
        });

        [TestMethod]
        public void ManualClearPublishesEmptyPatternWithBoxSender() => KeyPatternTestSupport.RunSta(() =>
        {
            using var fixture = new ControlFixture();
            fixture.Control.Pattern = new HashSet<KeyCode> { KeyCode.A };
            int notifications = 0;
            fixture.Control.PatternChanged += (sender, e) =>
            {
                Assert.AreSame(fixture.Control, sender);
                Assert.AreEqual(0, e.Keys.Count);
                Assert.IsNull(fixture.Control.Pattern);
                notifications++;
            };
            Input(fixture.Control).Text = "";
            Assert.AreEqual(1, notifications);
            Assert.AreEqual("<None>", Input(fixture.Control).Text);
        });

        [DataTestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public void ManualClearCannotOverwriteOrNotifyAReentrantLifetime(bool duringTextUpdate, bool reload) => KeyPatternTestSupport.RunSta(() =>
        {
            using var fixture = new ControlFixture();
            fixture.SetFocus(true);
            fixture.Control.Pattern = new HashSet<KeyCode> { KeyCode.A };
            int oldNotifications = 0;
            int newNotifications = 0;
            bool reentered = false;
            fixture.Control.PatternChanged += (_, _) => oldNotifications++;
            void ReplaceLifetime()
            {
                if (reentered) return;
                reentered = true;
                Unload(fixture.Control);
                if (reload) Load(fixture.Control);
                fixture.Control.PatternChanged += (sender, e) =>
                {
                    Assert.AreSame(fixture.Control, sender);
                    Assert.AreEqual(0, e.Keys.Count);
                    newNotifications++;
                };
                fixture.Control.Pattern = new HashSet<KeyCode> { KeyCode.B };
            }
            var descriptor = DependencyPropertyDescriptor.FromProperty(KeyPressBox.PatternProperty, typeof(KeyPressBox))!;
            EventHandler onPattern = (_, _) => { if (fixture.Control.Pattern == null) ReplaceLifetime(); };
            TextChangedEventHandler onText = (_, _) => { if (Input(fixture.Control).Text == "<None>") ReplaceLifetime(); };
            if (duringTextUpdate) Input(fixture.Control).TextChanged += onText;
            else descriptor.AddValueChanged(fixture.Control, onPattern);
            try
            {
                Input(fixture.Control).Text = "";
                Assert.IsTrue(reentered);
                Assert.AreEqual(0, oldNotifications, "The old clear crossed its unload boundary.");
                Assert.AreEqual(0, newNotifications, "A replacement lifetime received an obsolete clear.");
                Assert.IsNotNull(fixture.Control.Pattern);
                CollectionAssert.AreEqual(new[] { KeyCode.B }, fixture.Control.Pattern!.ToArray());
                Assert.AreEqual(new[] { KeyCode.B }.ToPrettyString(), Input(fixture.Control).Text);
                Assert.AreEqual(reload ? 1 : 0, KeyPatternTestSupport.SubscriberCount(fixture.Hook));
                if (!reload) Load(fixture.Control);
                Input(fixture.Control).Text = "";
                Assert.AreEqual(1, oldNotifications);
                Assert.AreEqual(1, newNotifications);
                Assert.IsNull(fixture.Control.Pattern);
                Assert.AreEqual("<None>", Input(fixture.Control).Text);
            }
            finally
            {
                Input(fixture.Control).TextChanged -= onText;
                descriptor.RemoveValueChanged(fixture.Control, onPattern);
            }
        });

        [TestMethod]
        public void LoadedFocusedControlReplacesItsUnloadedListener() => KeyPatternTestSupport.RunSta(() =>
        {
            using var fixture = new ControlFixture();
            fixture.SetFocus(true);
            var previous = fixture.Created.Single();
            Unload(fixture.Control);
            Assert.IsFalse(previous.IsListening);
            Assert.AreEqual(0, KeyPatternTestSupport.SubscriberCount(fixture.Hook));
            // The injected reader reports the same focused state on Loaded.
            Load(fixture.Control);
            Assert.AreEqual(2, fixture.Created.Count);
            Assert.AreNotSame(previous, fixture.Created[1]);
            Assert.IsTrue(fixture.Created[1].IsListening);
            Assert.AreEqual(1, KeyPatternTestSupport.SubscriberCount(fixture.Hook));
            int notifications = 0;
            fixture.Control.PatternChanged += (_, e) =>
            {
                CollectionAssert.AreEqual(new[] { KeyCode.B }, e.Keys.ToArray());
                notifications++;
            };
            KeyPatternTestSupport.Send(fixture.Hook, KeyCode.B, true);
            KeyPatternTestSupport.Send(fixture.Hook, KeyCode.B, false);
            KeyPatternTestSupport.DrainDispatcher();
            Assert.AreEqual(1, notifications);
        });

        [TestMethod]
        public void FactoryResultCanceledByReentrantUnloadIsDisposedBeforeReturn() => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            var created = new List<KeyPatternListener>();
            bool focused = false;
            bool armed = false;
            int unloadsInsideFactory = 0;
            var control = new KeyPressBox(input =>
            {
                var listener = new KeyPatternListener(input, hook);
                created.Add(listener);
                if (armed)
                {
                    listener.SynchronizeFocus(true);
                    Unload(Owner(input));
                    unloadsInsideFactory++;
                }
                return listener;
            }, () => focused);
            try
            {
                armed = true;
                focused = true;
                control.SynchronizeRecordingFocus();
                Assert.AreEqual(1, unloadsInsideFactory);
                Assert.AreEqual(1, created.Count);
                Assert.IsFalse(created[0].IsListening);
                Assert.AreEqual(0, KeyPatternTestSupport.SubscriberCount(hook));
                control.SynchronizeRecordingFocus();
                Assert.AreEqual(1, created.Count, "An unloaded control must not revive capture.");
                armed = false;
                Load(control);
                Assert.AreEqual(2, created.Count);
                Assert.IsTrue(created[1].IsListening);
            }
            finally
            {
                Unload(control);
                foreach (var listener in created) listener.Dispose();
            }
        });

        [TestMethod]
        public void ReentrantFocusDuringFactoryAcquiresOnlyOneListener() => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            var created = new List<KeyPatternListener>();
            bool focused = false;
            bool reentered = false;
            var control = new KeyPressBox(input =>
            {
                if (!reentered)
                {
                    reentered = true;
                    Owner(input).SynchronizeRecordingFocus();
                }
                var listener = new KeyPatternListener(input, hook);
                created.Add(listener);
                return listener;
            }, () => focused);
            try
            {
                focused = true;
                control.SynchronizeRecordingFocus();
                Assert.IsTrue(reentered);
                Assert.AreEqual(1, created.Count);
                Assert.AreEqual(1, KeyPatternTestSupport.SubscriberCount(hook));
            }
            finally
            {
                Unload(control);
                foreach (var listener in created) listener.Dispose();
            }
        });

        [DataTestMethod]
        [DataRow(1)]
        [DataRow(3)]
        public void ReentrantReloadDuringFactoryReconcilesLatestFocusWithoutAnotherFocusEvent(int invalidations) => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            var created = new List<KeyPatternListener>();
            bool focused = false;
            bool armed = false;
            int canceled = 0;
            LowLevelKeyboardHook.KeyStateChangedEventHandler? obsolete = null;
            var control = new KeyPressBox(input =>
            {
                var listener = new KeyPatternListener(input, hook);
                created.Add(listener);
                if (armed && canceled < invalidations)
                {
                    canceled++;
                    listener.SynchronizeFocus(true);
                    obsolete ??= KeyPatternTestSupport.Subscribers(hook);
                    KeyPatternTestSupport.Send(hook, KeyCode.A, true);
                    Unload(Owner(input));
                    Load(Owner(input));
                }
                return listener;
            }, () => focused);
            try
            {
                armed = true;
                focused = true;
                control.SynchronizeRecordingFocus();
                KeyPatternTestSupport.DrainDispatcher();
                Assert.AreEqual(invalidations, canceled);
                Assert.AreEqual(invalidations + 1, created.Count);
                Assert.IsTrue(created.Take(invalidations).All(listener => !listener.IsListening));
                Assert.IsTrue(created[^1].IsListening);
                Assert.AreEqual(1, KeyPatternTestSupport.SubscriberCount(hook));
                var late = new LowLevelKeyboardHook.KeyStateChangedEventArgs(KeyCode.A, false);
                obsolete!(hook, ref late);
                Assert.IsFalse(late.Handled);
                int notifications = 0;
                control.PatternChanged += (sender, e) =>
                {
                    Assert.AreSame(created[^1], sender);
                    CollectionAssert.AreEqual(new[] { KeyCode.B }, e.Keys.ToArray());
                    notifications++;
                };
                KeyPatternTestSupport.Send(hook, KeyCode.B, true);
                KeyPatternTestSupport.Send(hook, KeyCode.B, false);
                KeyPatternTestSupport.DrainDispatcher();
                Assert.AreEqual(1, notifications);
            }
            finally
            {
                armed = false;
                Unload(control);
                foreach (var listener in created) listener.Dispose();
                KeyPatternTestSupport.DrainDispatcher();
            }
        });

        [TestMethod]
        public void RepeatedReentrantReloadKeepsOnePendingReconciliationAndUnloadInvalidatesIt() => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            var created = new List<KeyPatternListener>();
            var pending = new List<DispatcherOperation>();
            bool focused = false;
            bool armed = false;
            int canceled = 0;
            var control = new KeyPressBox(input =>
            {
                var listener = new KeyPatternListener(input, hook);
                created.Add(listener);
                if (armed)
                {
                    canceled++;
                    listener.SynchronizeFocus(true);
                    Unload(Owner(input));
                    Load(Owner(input));
                }
                return listener;
            }, () => focused);
            KeyPatternTestSupport.DrainDispatcher();
            var dispatcher = Dispatcher.CurrentDispatcher;
            DispatcherHookEventHandler observePosted = (_, e) =>
            {
                if (e.Operation.Priority == DispatcherPriority.Background) pending.Add(e.Operation);
            };
            dispatcher.Hooks.OperationPosted += observePosted;
            try
            {
                focused = true;
                armed = true;
                control.SynchronizeRecordingFocus();
                Assert.AreEqual(1, canceled);
                for (int turn = 0; turn < 3; turn++)
                {
                    // FIFO sentinel at the same priority returns after one
                    // already-pending reconciliation, even if it re-invalidates.
                    var frame = new DispatcherFrame();
                    dispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
                    Dispatcher.PushFrame(frame);
                    Assert.AreEqual(turn + 2, canceled);
                    Assert.AreEqual(1, pending.Count(operation => operation.Status == DispatcherOperationStatus.Pending));
                    Assert.AreEqual(0, KeyPatternTestSupport.SubscriberCount(hook));
                    Assert.IsTrue(created.All(listener => !listener.IsListening));
                }
                Unload(control);
                armed = false;
                KeyPatternTestSupport.DrainDispatcher();
                Assert.AreEqual(4, canceled);
                Assert.AreEqual(4, created.Count);
                Assert.AreEqual(0, pending.Count(operation => operation.Status == DispatcherOperationStatus.Pending));
                Load(control);
                Assert.AreEqual(5, created.Count);
                Assert.IsTrue(created[^1].IsListening);
                Assert.AreEqual(1, KeyPatternTestSupport.SubscriberCount(hook));
            }
            finally
            {
                dispatcher.Hooks.OperationPosted -= observePosted;
                armed = false;
                Unload(control);
                foreach (var listener in created) listener.Dispose();
                KeyPatternTestSupport.DrainDispatcher();
            }
        });

        [TestMethod]
        public void PatternMutationCannotPublishOldCaptureToReloadedLifetimeSubscribers() => KeyPatternTestSupport.RunSta(() =>
        {
            using var fixture = new ControlFixture();
            fixture.SetFocus(true);
            var previous = fixture.Created.Single();
            int oldNotifications = 0;
            int newNotifications = 0;
            fixture.Control.PatternChanged += (_, _) => oldNotifications++;
            var descriptor = DependencyPropertyDescriptor.FromProperty(KeyPressBox.PatternProperty, typeof(KeyPressBox))!;
            bool reentered = false;
            EventHandler replaceLifetime = (_, _) =>
            {
                if (reentered) return;
                reentered = true;
                Unload(fixture.Control);
                Load(fixture.Control);
                fixture.Control.PatternChanged += (sender, _) =>
                {
                    Assert.AreNotSame(previous, sender);
                    newNotifications++;
                };
                fixture.Control.Pattern = new HashSet<KeyCode> { KeyCode.B };
            };
            descriptor.AddValueChanged(fixture.Control, replaceLifetime);
            try
            {
                KeyPatternTestSupport.Send(fixture.Hook, KeyCode.A, true);
                KeyPatternTestSupport.Send(fixture.Hook, KeyCode.A, false);
                KeyPatternTestSupport.DrainDispatcher();
                Assert.IsTrue(reentered);
                Assert.AreEqual(0, oldNotifications);
                Assert.AreEqual(0, newNotifications);
                CollectionAssert.AreEqual(new[] { KeyCode.B }, fixture.Control.Pattern!.ToArray());
                KeyPatternTestSupport.Send(fixture.Hook, KeyCode.B, true);
                KeyPatternTestSupport.Send(fixture.Hook, KeyCode.B, false);
                KeyPatternTestSupport.DrainDispatcher();
                Assert.AreEqual(1, oldNotifications);
                Assert.AreEqual(1, newNotifications);
            }
            finally { descriptor.RemoveValueChanged(fixture.Control, replaceLifetime); }
        });

        [TestMethod]
        public void FocusLossDuringFactoryReleasesItsUnpublishedListener() => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            bool focused = false;
            bool cancelFirstRecording = true;
            var created = new List<KeyPatternListener>();
            var control = new KeyPressBox(input =>
            {
                var listener = new KeyPatternListener(input, hook);
                created.Add(listener);
                if (focused && cancelFirstRecording)
                {
                    cancelFirstRecording = false;
                    focused = false;
                    Owner(input).SynchronizeRecordingFocus();
                }
                return listener;
            }, () => focused);
            try
            {
                focused = true;
                control.SynchronizeRecordingFocus();
                Assert.IsFalse(focused);
                Assert.AreEqual(1, created.Count);
                Assert.IsFalse(created[0].IsListening);
                Assert.AreEqual(0, KeyPatternTestSupport.SubscriberCount(hook));
                focused = true;
                control.SynchronizeRecordingFocus();
                Assert.AreEqual(2, created.Count);
                Assert.IsTrue(created[1].IsListening);
            }
            finally
            {
                Unload(control);
                foreach (var listener in created) listener.Dispose();
            }
        });

        [TestMethod]
        public void FailedFirstRecordingAcquisitionLeavesNormalRetryAvailable() => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            bool focused = false;
            int attempts = 0;
            var failure = new InvalidOperationException("Injected first recording acquisition failure.");
            var created = new List<KeyPatternListener>();
            var control = new KeyPressBox(input =>
            {
                if (++attempts == 1) throw failure;
                var listener = new KeyPatternListener(input, hook);
                created.Add(listener);
                return listener;
            }, () => focused);
            try
            {
                Assert.AreEqual(0, attempts);
                focused = true;
                Assert.AreSame(failure, Assert.ThrowsException<InvalidOperationException>(control.SynchronizeRecordingFocus));
                Assert.AreEqual(0, KeyPatternTestSupport.SubscriberCount(hook));
                control.SynchronizeRecordingFocus();
                Assert.AreEqual(2, attempts);
                Assert.AreEqual(1, created.Count);
                Assert.AreEqual(1, KeyPatternTestSupport.SubscriberCount(hook));
            }
            finally
            {
                Unload(control);
                foreach (var listener in created) listener.Dispose();
            }
        });

        [TestMethod]
        public void UnloadedQueuedRecordingCannotOverwriteReloadedControl() => KeyPatternTestSupport.RunSta(() =>
        {
            using var fixture = new ControlFixture();
            fixture.SetFocus(true);
            var previous = fixture.Created.Single();
            var oldCallback = KeyPatternTestSupport.Subscribers(fixture.Hook)!;
            KeyPatternTestSupport.Send(fixture.Hook, KeyCode.A, true);
            KeyPatternTestSupport.Send(fixture.Hook, KeyCode.A, false);
            Unload(fixture.Control);
            Load(fixture.Control);
            int notifications = 0;
            fixture.Control.PatternChanged += (sender, e) =>
            {
                Assert.AreNotSame(previous, sender);
                CollectionAssert.AreEqual(new[] { KeyCode.B }, e.Keys.ToArray());
                notifications++;
            };
            var late = new LowLevelKeyboardHook.KeyStateChangedEventArgs(KeyCode.A, true);
            oldCallback(fixture.Hook, ref late);
            Assert.IsFalse(late.Handled);
            KeyPatternTestSupport.Send(fixture.Hook, KeyCode.B, true);
            KeyPatternTestSupport.Send(fixture.Hook, KeyCode.B, false);
            KeyPatternTestSupport.DrainDispatcher();
            Assert.AreEqual(1, notifications);
        });

        [TestMethod]
        public void PatternInducedBlurDoesNotDiscardItsAdmittedNotification() => KeyPatternTestSupport.RunSta(() =>
        {
            using var fixture = new ControlFixture();
            var descriptor = DependencyPropertyDescriptor.FromProperty(KeyPressBox.PatternProperty, typeof(KeyPressBox))!;
            EventHandler blurWhenPatternChanges = (_, _) => fixture.SetFocus(false);
            descriptor.AddValueChanged(fixture.Control, blurWhenPatternChanges);
            try
            {
                int notifications = 0;
                fixture.Control.PatternChanged += (_, _) => notifications++;
                fixture.SetFocus(true);
                KeyPatternTestSupport.Send(fixture.Hook, KeyCode.A, true);
                KeyPatternTestSupport.Send(fixture.Hook, KeyCode.A, false);
                KeyPatternTestSupport.DrainDispatcher();
                Assert.AreEqual(1, notifications);
                CollectionAssert.AreEqual(new[] { KeyCode.A }, fixture.Control.Pattern!.ToArray());
                Assert.AreEqual(0, KeyPatternTestSupport.SubscriberCount(fixture.Hook));
            }
            finally { descriptor.RemoveValueChanged(fixture.Control, blurWhenPatternChanges); }
        });

        [TestMethod]
        public void HundredLoadRecordUnloadCyclesHaveOneOwnedListenerPerCycle() => KeyPatternTestSupport.RunSta(() =>
        {
            using var fixture = new ControlFixture();
            int notifications = 0;
            fixture.Control.PatternChanged += (_, _) => notifications++;
            for (int cycle = 0; cycle < 100; cycle++)
            {
                fixture.Focused = true;
                Load(fixture.Control);
                fixture.Control.SynchronizeRecordingFocus();
                Assert.AreEqual(cycle + 1, fixture.Created.Count);
                Assert.AreEqual(1, KeyPatternTestSupport.SubscriberCount(fixture.Hook));
                KeyPatternTestSupport.Send(fixture.Hook, KeyCode.RightCtrl, true);
                KeyPatternTestSupport.Send(fixture.Hook, KeyCode.A, true);
                KeyPatternTestSupport.Send(fixture.Hook, KeyCode.A, false);
                KeyPatternTestSupport.DrainDispatcher();
                CollectionAssert.AreEquivalent(new[] { KeyCode.LeftCtrl, KeyCode.A }, fixture.Control.Pattern!.ToArray());
                fixture.SetFocus(false);
                Unload(fixture.Control);
                Unload(fixture.Control);
                Assert.AreEqual(0, KeyPatternTestSupport.SubscriberCount(fixture.Hook));
                Assert.IsTrue(fixture.Created.All(listener => !listener.IsListening));
            }
            Assert.AreEqual(100, notifications);
        });

        [TestMethod]
        public void UnloadedControlDoesNotRetainItsDisposedListener() => KeyPatternTestSupport.RunSta(() =>
        {
            var observed = CreateUnloadedControl();
            // Retention proof is a standalone regression, never part of the
            // display benchmark or a periodic production collection.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.IsFalse(observed.Listener.IsAlive);
            GC.KeepAlive(observed.Control);
        });

        [TestMethod]
        public void HundredReentrantLifetimesReleaseControlsAndListenersAfterDispatcherDrains() => KeyPatternTestSupport.RunSta(() =>
        {
            var observed = new List<WeakReference>();
            for (int cycle = 0; cycle < 100; cycle++) observed.AddRange(CreateReentrantUnloadedControl());
            KeyPatternTestSupport.DrainDispatcher();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.IsTrue(observed.All(reference => !reference.IsAlive));
        });

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference[] CreateReentrantUnloadedControl()
        {
            var hook = KeyPatternTestSupport.CreateHook();
            var listeners = new List<KeyPatternListener>();
            bool focused = false;
            bool armed = false;
            bool invalidated = false;
            var control = new KeyPressBox(input =>
            {
                var listener = new KeyPatternListener(input, hook);
                listeners.Add(listener);
                if (armed && !invalidated)
                {
                    invalidated = true;
                    Unload(Owner(input));
                    Load(Owner(input));
                }
                return listener;
            }, () => focused);
            try
            {
                armed = true;
                focused = true;
                control.SynchronizeRecordingFocus();
                KeyPatternTestSupport.DrainDispatcher();
                Assert.IsTrue(invalidated);
                Assert.AreEqual(2, listeners.Count);
                Assert.AreEqual(1, KeyPatternTestSupport.SubscriberCount(hook));
                Unload(control);
                KeyPatternTestSupport.DrainDispatcher();
                Assert.AreEqual(0, KeyPatternTestSupport.SubscriberCount(hook));
                return new[] { new WeakReference(control) }.Concat(listeners.Select(listener => new WeakReference(listener))).ToArray();
            }
            finally
            {
                Unload(control);
                foreach (var listener in listeners) listener.Dispose();
                KeyPatternTestSupport.DrainDispatcher();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static (KeyPressBox Control, WeakReference Listener) CreateUnloadedControl()
        {
            var hook = KeyPatternTestSupport.CreateHook();
            WeakReference? listenerReference = null;
            bool focused = false;
            var control = new KeyPressBox(input =>
            {
                var listener = new KeyPatternListener(input, hook);
                listenerReference = new WeakReference(listener);
                return listener;
            }, () => focused);
            focused = true;
            control.SynchronizeRecordingFocus();
            Assert.IsNotNull(listenerReference);
            Unload(control);
            Assert.AreEqual(0, KeyPatternTestSupport.SubscriberCount(hook));
            return (control, listenerReference!);
        }

        [TestMethod]
        public void KeyPressDisplayCounterScenario() => KeyPatternTestSupport.RunSta(() =>
        {
            Assert.IsFalse(Application.Current is FancyWM.App);
            const int warmup = 20;
            const int iterations = 100;
            foreach (int count in new[] { 1, 10, 50 })
            {
                var hook = KeyPatternTestSupport.CreateHook();
                int creations = 0;
                Func<UIElement, KeyPatternListener> factory = input =>
                {
                    creations++;
                    return new KeyPatternListener(input, hook);
                };
                var patterns = Enumerable.Range(0, count).Select(index => index % 2 == 0
                    ? new HashSet<KeyCode> { KeyCode.LeftCtrl, KeyCode.A }
                    : new HashSet<KeyCode> { KeyCode.RightShift, KeyCode.B }).ToArray();
                var expectedText = patterns.Select(pattern => pattern.OrderByDescending(key => (int)key).ToPrettyString()).ToArray();
                for (int run = 0; run < warmup; run++) DisplayPass(factory, patterns, expectedText);
                creations = 0;
                long checksum = 0;
                long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                for (int run = 0; run < iterations; run++) checksum += DisplayPass(factory, patterns, expectedText);
                long ticks = Stopwatch.GetTimestamp() - start;
                long bytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
                // Hook retention inspection and output occur outside both timing
                // and allocation regions. Display/layout assertions are inside
                // the common method and their cost is included in both versions.
                int subscribers = KeyPatternTestSupport.SubscriberCount(hook);
                Assert.AreEqual(0, subscribers);
                Assert.AreEqual(iterations * TextChecksum(expectedText), checksum);
                string scenario = "key-press-display-" + count.ToString(CultureInfo.InvariantCulture);
                Counter(scenario, "created-controls", iterations * count);
                Counter(scenario, "listener-creations", creations);
                Counter(scenario, "live-hook-subscribers", subscribers);
                Counter(scenario, "display-checksum", checksum);
                Counter(scenario, "allocated-bytes", bytes);
                Counter(scenario, "elapsed-ticks", ticks);
                Counter(scenario, "timestamp-frequency", Stopwatch.Frequency);
            }
        });

        private static long DisplayPass(Func<UIElement, KeyPatternListener> factory, HashSet<KeyCode>[] patterns, string[] expectedText)
        {
            var controls = new KeyPressBox[patterns.Length];
            try
            {
                for (int index = 0; index < controls.Length; index++)
                {
                    var control = new KeyPressBox(factory) { Pattern = patterns[index] };
                    controls[index] = control;
                    control.Measure(new Size(220, 32));
                    control.Arrange(new Rect(0, 0, 220, 32));
                    Assert.AreEqual(expectedText[index], Input(control).Text);
                    Assert.AreEqual(220d, control.ActualWidth);
                    Assert.AreEqual(32d, control.ActualHeight);
                    Assert.IsNull(PresentationSource.FromVisual(control));
                }
                return TextChecksum(controls.Select(control => Input(control).Text).ToArray());
            }
            finally
            {
                foreach (var control in controls)
                {
                    if (control != null) Unload(control);
                }
            }
        }

        private static long TextChecksum(IReadOnlyList<string> strings)
        {
            long result = 0;
            for (int index = 0; index < strings.Count; index++)
            {
                long text = 1;
                foreach (char character in strings[index]) text = (text * 31 + character) % 1_000_000_007;
                result += text * (index + 1);
            }
            return result;
        }

        private static void Counter(string scenario, string metric, long value)
            => Console.WriteLine("KEYPRESSDISPLAY|" + scenario + "|" + metric + "|" + value.ToString(CultureInfo.InvariantCulture));

        private static TextBox Input(KeyPressBox control) => (TextBox)control.FindName("InputBox");
        private static void Load(KeyPressBox control) => control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        private static void Unload(KeyPressBox control) => control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));

        private static KeyPressBox Owner(UIElement input)
        {
            DependencyObject? current = input;
            while (current != null)
            {
                if (current is KeyPressBox control) return control;
                current = LogicalTreeHelper.GetParent(current);
            }
            throw new AssertFailedException("The actual XAML input must have its KeyPressBox logical owner.");
        }

        private sealed class ControlFixture : IDisposable
        {
            internal readonly LowLevelKeyboardHook Hook = KeyPatternTestSupport.CreateHook();
            internal readonly List<KeyPatternListener> Created = new();
            internal readonly KeyPressBox Control;
            internal bool Focused;

            internal ControlFixture()
            {
                Control = new KeyPressBox(input =>
                {
                    var listener = new KeyPatternListener(input, Hook);
                    Created.Add(listener);
                    return listener;
                }, () => Focused);
            }

            internal void SetFocus(bool focused)
            {
                Focused = focused;
                Control.SynchronizeRecordingFocus();
            }

            public void Dispose()
            {
                Unload(Control);
                foreach (var listener in Created) listener.Dispose();
                KeyPatternTestSupport.DrainDispatcher();
            }
        }
    }
}
