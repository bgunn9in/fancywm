#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Threading;

using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class KeyPatternListenerTest
    {
        [TestMethod]
        public void FocusSynchronizationIsIdempotentAndPreservesPendingKeys() => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            using var listener = new KeyPatternListener(new TextBox(), hook);
            var patterns = new List<KeyCode[]>();
            listener.PatternChanged += (_, e) => patterns.Add(e.Keys.ToArray());
            listener.SynchronizeFocus(true);
            Assert.AreEqual(1, KeyPatternTestSupport.SubscriberCount(hook));
            Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.LeftCtrl, true));
            KeyPatternTestSupport.DrainDispatcher();
            listener.SynchronizeFocus(true);
            Assert.AreEqual(1, KeyPatternTestSupport.SubscriberCount(hook));
            KeyPatternTestSupport.Send(hook, KeyCode.A, true);
            KeyPatternTestSupport.Send(hook, KeyCode.A, false);
            KeyPatternTestSupport.DrainDispatcher();
            Assert.AreEqual(1, patterns.Count);
            CollectionAssert.AreEquivalent(new[] { KeyCode.LeftCtrl, KeyCode.A }, patterns[0]);
            listener.SynchronizeFocus(false);
            listener.SynchronizeFocus(false);
            Assert.AreEqual(0, KeyPatternTestSupport.SubscriberCount(hook));
        });

        [TestMethod]
        public void FirstReleasePublishesPressedSetOnceWithListenerSender() => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            using var listener = new KeyPatternListener(new TextBox(), hook);
            int notifications = 0;
            listener.PatternChanged += (sender, e) =>
            {
                Assert.AreSame(listener, sender);
                Assert.AreSame(listener.Pattern, e.Keys);
                CollectionAssert.AreEquivalent(new[] { KeyCode.RightCtrl, KeyCode.A }, e.Keys.ToArray());
                notifications++;
            };
            Assert.IsFalse(KeyPatternTestSupport.Send(hook, KeyCode.B, true));
            listener.SynchronizeFocus(true);
            Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.RightCtrl, true));
            Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.A, true));
            Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.A, false));
            Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.RightCtrl, false));
            KeyPatternTestSupport.DrainDispatcher();
            Assert.AreEqual(1, notifications);
        });

        [TestMethod]
        public void BlurDiscardsQueuedPatternBeforeItsDispatcherTurn() => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            using var listener = new KeyPatternListener(new TextBox(), hook);
            int notifications = 0;
            listener.PatternChanged += (_, _) => notifications++;
            listener.SynchronizeFocus(true);
            KeyPatternTestSupport.Send(hook, KeyCode.A, true);
            KeyPatternTestSupport.Send(hook, KeyCode.A, false);
            listener.SynchronizeFocus(false);
            KeyPatternTestSupport.DrainDispatcher();
            Assert.AreEqual(0, notifications);
            Assert.IsNull(listener.Pattern);
        });

        [TestMethod]
        public void QueuedOldRecordingCannotContributeKeysToNewRecording() => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            using var listener = new KeyPatternListener(new TextBox(), hook);
            var patterns = new List<KeyCode[]>();
            listener.PatternChanged += (_, e) => patterns.Add(e.Keys.ToArray());
            listener.SynchronizeFocus(true);
            KeyPatternTestSupport.Send(hook, KeyCode.A, true);
            listener.SynchronizeFocus(false);
            listener.SynchronizeFocus(true);
            KeyPatternTestSupport.Send(hook, KeyCode.B, true);
            KeyPatternTestSupport.Send(hook, KeyCode.B, false);
            KeyPatternTestSupport.DrainDispatcher();
            Assert.AreEqual(1, patterns.Count);
            CollectionAssert.AreEqual(new[] { KeyCode.B }, patterns[0]);
        });

        [TestMethod]
        public void DisposeRejectsQueuedNotificationsAndLaterFocusRevival() => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            var listener = new KeyPatternListener(new TextBox(), hook);
            int notifications = 0;
            listener.PatternChanged += (_, _) => notifications++;
            listener.SynchronizeFocus(true);
            KeyPatternTestSupport.Send(hook, KeyCode.A, true);
            KeyPatternTestSupport.Send(hook, KeyCode.A, false);
            listener.Dispose();
            listener.Dispose();
            KeyPatternTestSupport.DrainDispatcher();
            Assert.AreEqual(0, notifications);
            Assert.IsNull(listener.Pattern);
            listener.SynchronizeFocus(true);
            Assert.IsFalse(listener.IsListening);
            Assert.AreEqual(0, KeyPatternTestSupport.SubscriberCount(hook));
        });

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void CapturedHookCallbackCannotNewlySuppressAfterDispose(bool alreadyHandled) => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            var listener = new KeyPatternListener(new TextBox(), hook);
            listener.SynchronizeFocus(true);
            var captured = KeyPatternTestSupport.Subscribers(hook)!;
            listener.Dispose();
            var input = new LowLevelKeyboardHook.KeyStateChangedEventArgs(KeyCode.A, true) { Handled = alreadyHandled };
            captured(hook, ref input);
            Assert.AreEqual(alreadyHandled, input.Handled);
            KeyPatternTestSupport.DrainDispatcher();
        });

        [TestMethod]
        public void CapturedHookCallbackCannotSuppressAfterBlur() => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            using var listener = new KeyPatternListener(new TextBox(), hook);
            listener.SynchronizeFocus(true);
            var captured = KeyPatternTestSupport.Subscribers(hook)!;
            listener.SynchronizeFocus(false);
            var input = new LowLevelKeyboardHook.KeyStateChangedEventArgs(KeyCode.A, true);
            captured(hook, ref input);
            Assert.IsFalse(input.Handled);
            KeyPatternTestSupport.DrainDispatcher();
        });

        [TestMethod]
        public void CapturedPreviousRecordingCallbackCannotEnterRefocusedGeneration() => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            using var listener = new KeyPatternListener(new TextBox(), hook);
            var patterns = new List<KeyCode[]>();
            listener.PatternChanged += (_, e) => patterns.Add(e.Keys.ToArray());
            listener.SynchronizeFocus(true);
            var captured = KeyPatternTestSupport.Subscribers(hook)!;
            listener.SynchronizeFocus(false);
            listener.SynchronizeFocus(true);
            var stale = new LowLevelKeyboardHook.KeyStateChangedEventArgs(KeyCode.A, true);
            captured(hook, ref stale);
            Assert.IsFalse(stale.Handled);
            KeyPatternTestSupport.Send(hook, KeyCode.B, true);
            KeyPatternTestSupport.Send(hook, KeyCode.B, false);
            KeyPatternTestSupport.DrainDispatcher();
            Assert.AreEqual(1, patterns.Count);
            CollectionAssert.AreEqual(new[] { KeyCode.B }, patterns[0]);
        });

        [TestMethod]
        public void CapturedWorkerCallbackReleasedAfterUiDisposeIsRejected() => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            var listener = new KeyPatternListener(new TextBox(), hook);
            listener.SynchronizeFocus(true);
            var captured = KeyPatternTestSupport.Subscribers(hook)!;
            using var capturedOnWorker = new ManualResetEventSlim();
            using var releaseWorker = new ManualResetEventSlim();
            Exception? failure = null;
            bool handled = true;
            var worker = new Thread(() =>
            {
                try
                {
                    capturedOnWorker.Set();
                    Assert.IsTrue(releaseWorker.Wait(TimeSpan.FromSeconds(10)));
                    var input = new LowLevelKeyboardHook.KeyStateChangedEventArgs(KeyCode.A, true);
                    captured(hook, ref input);
                    handled = input.Handled;
                }
                catch (Exception exception) { failure = exception; }
            }) { IsBackground = true };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            try
            {
                Assert.IsTrue(capturedOnWorker.Wait(TimeSpan.FromSeconds(10)));
                listener.Dispose();
            }
            finally
            {
                listener.Dispose();
                releaseWorker.Set();
                Assert.IsTrue(worker.Join(TimeSpan.FromSeconds(10)), "Owned captured callback worker did not finish.");
            }
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
            Assert.IsFalse(handled);
            Assert.AreEqual(0, KeyPatternTestSupport.SubscriberCount(hook));
            KeyPatternTestSupport.DrainDispatcher();
        });

        [TestMethod]
        public void DispatcherPostingMayDisposeWithoutPublishingTheQueuedPattern() => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            var listener = new KeyPatternListener(new TextBox(), hook);
            int notifications = 0;
            listener.PatternChanged += (_, _) => notifications++;
            listener.SynchronizeFocus(true);
            KeyPatternTestSupport.Send(hook, KeyCode.A, true);
            KeyPatternTestSupport.DrainDispatcher();
            bool disposedDuringPost = false;
            DispatcherHookEventHandler onPosted = (_, _) =>
            {
                if (!disposedDuringPost)
                {
                    disposedDuringPost = true;
                    listener.Dispose();
                }
            };
            Dispatcher.CurrentDispatcher.Hooks.OperationPosted += onPosted;
            try { Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.A, false)); }
            finally { Dispatcher.CurrentDispatcher.Hooks.OperationPosted -= onPosted; }
            Assert.IsTrue(disposedDuringPost);
            KeyPatternTestSupport.DrainDispatcher();
            Assert.AreEqual(0, notifications);
            Assert.AreEqual(0, KeyPatternTestSupport.SubscriberCount(hook));
        });

        [TestMethod]
        public void AlreadyAdmittedNotificationMayDisposeAndFinishItsDelegateList() => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            var listener = new KeyPatternListener(new TextBox(), hook);
            int notifications = 0;
            listener.PatternChanged += (_, _) => { listener.Dispose(); notifications++; };
            listener.PatternChanged += (_, _) => notifications++;
            listener.SynchronizeFocus(true);
            KeyPatternTestSupport.Send(hook, KeyCode.A, true);
            KeyPatternTestSupport.Send(hook, KeyCode.A, false);
            KeyPatternTestSupport.DrainDispatcher();
            Assert.AreEqual(2, notifications);
            Assert.AreEqual(0, KeyPatternTestSupport.SubscriberCount(hook));
        });

        [TestMethod]
        public void ThrowingSubscriberDoesNotHoldGateOrCorruptNextRecording() => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            using var listener = new KeyPatternListener(new TextBox(), hook);
            var expectedFailure = new InvalidOperationException("Injected PatternChanged failure.");
            using var peerCompleted = new ManualResetEventSlim();
            Exception? peerFailure = null;
            Thread? peer = null;
            DispatcherOperation? releaseOperation = null;
            var dispatcher = Dispatcher.CurrentDispatcher;
            int unhandled = 0;
            DispatcherUnhandledExceptionEventHandler observeUnhandled = (_, e) =>
            {
                unhandled++;
                e.Handled = true;
            };
            KeyPatternChangedEventHandler throwing = (_, _) =>
            {
                peer = new Thread(() =>
                {
                    try { Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.B, true)); }
                    catch (Exception exception) { peerFailure = exception; }
                    finally { peerCompleted.Set(); }
                }) { IsBackground = true };
                peer.SetApartmentState(ApartmentState.STA);
                peer.Start();
                // A callback on the hook thread must not wait on the gate held
                // by arbitrary UI subscriber code. No timing sleep is used.
                Assert.IsTrue(peerCompleted.Wait(TimeSpan.FromSeconds(10)), "PatternChanged held the hook admission gate.");
                throw expectedFailure;
            };
            listener.PatternChanged += throwing;
            dispatcher.UnhandledException += observeUnhandled;
            try
            {
                listener.SynchronizeFocus(true);
                KeyPatternTestSupport.Send(hook, KeyCode.A, true);
                KeyPatternTestSupport.DrainDispatcher();
                DispatcherHookEventHandler capturePosted = (_, e) => releaseOperation = e.Operation;
                dispatcher.Hooks.OperationPosted += capturePosted;
                try { KeyPatternTestSupport.Send(hook, KeyCode.A, false); }
                finally { dispatcher.Hooks.OperationPosted -= capturePosted; }
                KeyPatternTestSupport.DrainDispatcher();
                Assert.IsNotNull(releaseOperation);
                // InvokeAsync preserves its existing task-fault behavior; the
                // fixture observes that exact task instead of leaking a fault.
                Assert.IsTrue(releaseOperation!.Task.IsFaulted);
                Assert.AreSame(expectedFailure, releaseOperation.Task.Exception!.GetBaseException());
                Assert.AreEqual(0, unhandled);
                if (peerFailure != null) ExceptionDispatchInfo.Capture(peerFailure).Throw();
                CollectionAssert.AreEqual(new[] { KeyCode.A }, listener.Pattern!.ToArray());
                listener.PatternChanged -= throwing;
                listener.SynchronizeFocus(false);
                listener.SynchronizeFocus(true);
                int notifications = 0;
                listener.PatternChanged += (_, e) =>
                {
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
                listener.PatternChanged -= throwing;
                dispatcher.UnhandledException -= observeUnhandled;
                listener.Dispose();
                if (peer != null) Assert.IsTrue(peer.Join(TimeSpan.FromSeconds(10)), "Owned exception-probe worker did not finish.");
            }
        });

        [TestMethod]
        public void HundredListenerLifetimesLeaveBorrowedHookReusable() => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            int notifications = 0;
            for (int cycle = 0; cycle < 100; cycle++)
            {
                using var listener = new KeyPatternListener(new TextBox(), hook);
                Assert.AreEqual(0, KeyPatternTestSupport.SubscriberCount(hook));
                listener.PatternChanged += (_, e) =>
                {
                    CollectionAssert.AreEqual(new[] { KeyCode.A }, e.Keys.ToArray());
                    notifications++;
                };
                listener.SynchronizeFocus(true);
                Assert.AreEqual(1, KeyPatternTestSupport.SubscriberCount(hook));
                KeyPatternTestSupport.Send(hook, KeyCode.A, true);
                KeyPatternTestSupport.Send(hook, KeyCode.A, false);
                KeyPatternTestSupport.DrainDispatcher();
                listener.SynchronizeFocus(false);
                listener.Dispose();
                Assert.AreEqual(0, KeyPatternTestSupport.SubscriberCount(hook));
            }
            Assert.AreEqual(100, notifications);
        });
    }

    internal static class KeyPatternTestSupport
    {
        private static readonly FieldInfo SubscriberField = typeof(LowLevelKeyboardHook)
            .GetField("KeyStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;

        internal static LowLevelKeyboardHook CreateHook()
        {
            // The existing hook is used ONLY as a managed event owner. Its
            // constructor, HookProc, Dispose and finalizer must never execute.
            var hook = (LowLevelKeyboardHook)RuntimeHelpers.GetUninitializedObject(typeof(LowLevelKeyboardHook));
            GC.SuppressFinalize(hook);
            return hook;
        }

        internal static LowLevelKeyboardHook.KeyStateChangedEventHandler? Subscribers(LowLevelKeyboardHook hook)
            => (LowLevelKeyboardHook.KeyStateChangedEventHandler?)SubscriberField.GetValue(hook);

        internal static int SubscriberCount(LowLevelKeyboardHook hook)
            => Subscribers(hook)?.GetInvocationList().Length ?? 0;

        internal static bool Send(LowLevelKeyboardHook hook, KeyCode key, bool pressed)
        {
            var input = new LowLevelKeyboardHook.KeyStateChangedEventArgs(key, pressed);
            Subscribers(hook)?.Invoke(hook, ref input);
            return input.Handled;
        }

        internal static void DrainDispatcher()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }

        internal static void RunSta(Action action)
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
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "Controlled KeyPressBox dispatcher did not finish.");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
