#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Threading;

using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class LowLevelKeyPatternListenerTest
    {
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

        [TestMethod]
        public void QueuedRecordingsPublishIndependentSnapshotsWithListenerSender() => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            using var listener = new LowLevelKeyPatternListener(hook);
            var notifications = new List<(object Sender, IReadOnlySet<KeyCode> Keys)>();
            listener.PatternChanged += (sender, e) => notifications.Add((sender, e.Keys));
            Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.A, true));
            Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.A, false));
            Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.B, true));
            Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.B, false));
            Assert.AreEqual(0, notifications.Count);

            KeyPatternTestSupport.DrainDispatcher();

            Assert.AreEqual(2, notifications.Count);
            Assert.AreSame(listener, notifications[0].Sender);
            Assert.AreSame(listener, notifications[1].Sender);
            CollectionAssert.AreEqual(new[] { KeyCode.A }, notifications[0].Keys.ToArray());
            CollectionAssert.AreEqual(new[] { KeyCode.B }, notifications[1].Keys.ToArray());
            Assert.AreNotSame(notifications[0].Keys, notifications[1].Keys);
            CollectionAssert.AreEqual(new[] { KeyCode.B }, listener.Pattern!.ToArray());
            listener.Dispose();
            Assert.AreEqual(0, KeyPatternTestSupport.SubscriberCount(hook));
        });

        [TestMethod]
        public void DisposeRejectsQueuedNotification() => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            using var listener = new LowLevelKeyPatternListener(hook);
            int notifications = 0;
            listener.PatternChanged += (_, _) => notifications++;
            KeyPatternTestSupport.Send(hook, KeyCode.A, true);
            KeyPatternTestSupport.Send(hook, KeyCode.A, false);
            Assert.AreEqual(0, notifications);

            listener.Dispose();
            listener.Dispose();
            KeyPatternTestSupport.DrainDispatcher();

            Assert.AreEqual(0, notifications);
            Assert.IsFalse(listener.IsListening);
            Assert.AreEqual(0, KeyPatternTestSupport.SubscriberCount(hook));
        });

        [DataTestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public void CapturedCallbackAfterDisposeCannotRecordOrSuppressUnrelatedInput(bool draining, bool alreadyHandled) => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            using var listener = new LowLevelKeyPatternListener(hook);
            int notifications = 0;
            listener.PatternChanged += (_, _) => notifications++;
            var captured = KeyPatternTestSupport.Subscribers(hook)!;
            if (draining) Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.A, true));
            listener.Dispose();
            Assert.AreEqual(draining ? 1 : 0, KeyPatternTestSupport.SubscriberCount(hook));
            var down = new LowLevelKeyboardHook.KeyStateChangedEventArgs(KeyCode.B, true) { Handled = alreadyHandled };
            var up = new LowLevelKeyboardHook.KeyStateChangedEventArgs(KeyCode.B, false) { Handled = alreadyHandled };

            captured(hook, ref down);
            captured(hook, ref up);
            if (draining) Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.A, false));
            KeyPatternTestSupport.DrainDispatcher();

            Assert.AreEqual(alreadyHandled, down.Handled, "A stale callback changed unrelated key-down suppression.");
            Assert.AreEqual(alreadyHandled, up.Handled, "A stale callback changed unrelated key-up suppression.");
            Assert.AreEqual(0, notifications, "A stale callback published a recording after disposal.");
            Assert.IsNull(listener.Pattern, "Unrelated stale input became a recorded pattern.");
            Assert.IsFalse(listener.IsListening);
            Assert.AreEqual(0, KeyPatternTestSupport.SubscriberCount(hook));
        });

        [TestMethod]
        public void HeldReleaseAfterDisposeStaysSuppressedAndDetaches() => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            using var listener = new LowLevelKeyPatternListener(hook);
            int notifications = 0;
            listener.PatternChanged += (_, _) => notifications++;
            Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.A, true));

            listener.Dispose();
            listener.Dispose();
            Assert.IsFalse(listener.IsListening);
            Assert.AreEqual(1, KeyPatternTestSupport.SubscriberCount(hook));
            Assert.IsFalse(KeyPatternTestSupport.Send(hook, KeyCode.B, true));
            Assert.IsFalse(KeyPatternTestSupport.Send(hook, KeyCode.B, false));
            Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.A, false));
            Assert.AreEqual(0, KeyPatternTestSupport.SubscriberCount(hook));
            Assert.IsFalse(KeyPatternTestSupport.Send(hook, KeyCode.A, false));
            KeyPatternTestSupport.DrainDispatcher();
            Assert.AreEqual(0, notifications);
            Assert.IsNull(listener.Pattern);
        });

        [TestMethod]
        public void CapturedHeldReleaseAfterDisposeCompletesSuppressionOwnership() => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            using var listener = new LowLevelKeyPatternListener(hook);
            int notifications = 0;
            listener.PatternChanged += (_, _) => notifications++;
            var captured = KeyPatternTestSupport.Subscribers(hook)!;
            Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.A, true));
            listener.Dispose();
            Assert.AreEqual(1, KeyPatternTestSupport.SubscriberCount(hook));
            var release = new LowLevelKeyboardHook.KeyStateChangedEventArgs(KeyCode.A, false);

            captured(hook, ref release);
            KeyPatternTestSupport.DrainDispatcher();
            int remainingSubscriptions = KeyPatternTestSupport.SubscriberCount(hook);
            var remainingPattern = listener.Pattern;
            // Clean up an incorrectly retained old cleanup callback only after
            // observing the captured release's complete postconditions.
            KeyPatternTestSupport.Send(hook, KeyCode.A, false);

            Assert.IsTrue(release.Handled);
            Assert.AreEqual(0, notifications);
            Assert.AreEqual(0, remainingSubscriptions, "Captured key-up retained its cleanup subscription.");
            Assert.IsNull(remainingPattern, "The captured draining release recorded a new pattern.");
        });

        [TestMethod]
        public void ChordFirstReleasePublishesPressedSetOnceAndSuppressesRemainingRelease() => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            using var listener = new LowLevelKeyPatternListener(hook);
            var patterns = new List<KeyCode[]>();
            listener.PatternChanged += (_, e) => patterns.Add(e.Keys.ToArray());
            Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.LeftCtrl, true));
            Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.A, true));
            Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.A, false));

            KeyPatternTestSupport.DrainDispatcher();
            Assert.AreEqual(1, patterns.Count);
            CollectionAssert.AreEquivalent(new[] { KeyCode.LeftCtrl, KeyCode.A }, patterns[0]);
            Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.LeftCtrl, false));
            KeyPatternTestSupport.DrainDispatcher();

            Assert.AreEqual(1, patterns.Count);
            listener.Dispose();
            Assert.AreEqual(0, KeyPatternTestSupport.SubscriberCount(hook));
        });

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void UnknownActiveReleasePublishesPressedSetAndPreservesHandled(bool alreadyHandled) => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            using var listener = new LowLevelKeyPatternListener(hook);
            var patterns = new List<KeyCode[]>();
            listener.PatternChanged += (_, e) => patterns.Add(e.Keys.ToArray());
            Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.A, true));
            var unknownRelease = new LowLevelKeyboardHook.KeyStateChangedEventArgs(KeyCode.B, false) { Handled = alreadyHandled };

            KeyPatternTestSupport.Subscribers(hook)!(hook, ref unknownRelease);
            KeyPatternTestSupport.DrainDispatcher();

            Assert.AreEqual(alreadyHandled, unknownRelease.Handled);
            Assert.AreEqual(1, patterns.Count);
            CollectionAssert.AreEqual(new[] { KeyCode.A }, patterns[0]);
            Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.A, false));
            KeyPatternTestSupport.DrainDispatcher();
            Assert.AreEqual(1, patterns.Count);
            listener.Dispose();
            Assert.AreEqual(0, KeyPatternTestSupport.SubscriberCount(hook));
        });

        [TestMethod]
        public void MultipleHeldKeysDrainOnlyOnTheirLastOwnedRelease() => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            using var listener = new LowLevelKeyPatternListener(hook);
            int notifications = 0;
            listener.PatternChanged += (_, _) => notifications++;
            Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.LeftCtrl, true));
            Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.A, true));
            listener.Dispose();
            listener.Dispose();

            bool firstRepeat = KeyPatternTestSupport.Send(hook, KeyCode.LeftCtrl, true);
            int subscriptionsAfterRepeat = KeyPatternTestSupport.SubscriberCount(hook);
            bool unknownRelease = KeyPatternTestSupport.Send(hook, KeyCode.B, false);
            bool firstOwnedRelease = KeyPatternTestSupport.Send(hook, KeyCode.A, false);
            int subscriptionsAfterFirstRelease = KeyPatternTestSupport.SubscriberCount(hook);
            bool secondRepeat = KeyPatternTestSupport.Send(hook, KeyCode.LeftCtrl, true);
            var alreadyHandledRelease = new LowLevelKeyboardHook.KeyStateChangedEventArgs(KeyCode.B, false) { Handled = true };
            KeyPatternTestSupport.Subscribers(hook)?.Invoke(hook, ref alreadyHandledRelease);
            bool lastOwnedRelease = KeyPatternTestSupport.Send(hook, KeyCode.LeftCtrl, false);
            KeyPatternTestSupport.DrainDispatcher();

            Assert.IsTrue(firstRepeat);
            Assert.AreEqual(1, subscriptionsAfterRepeat);
            Assert.IsFalse(unknownRelease);
            Assert.IsTrue(firstOwnedRelease);
            Assert.AreEqual(1, subscriptionsAfterFirstRelease, "A different held key still owns its release.");
            Assert.IsTrue(secondRepeat);
            Assert.IsTrue(alreadyHandledRelease.Handled);
            Assert.IsTrue(lastOwnedRelease);
            Assert.AreEqual(0, KeyPatternTestSupport.SubscriberCount(hook));
            Assert.AreEqual(0, notifications);
            Assert.IsNull(listener.Pattern);
        });

        [TestMethod]
        public void RepeatDownWhileDrainingDoesNotConsumeReleaseOwnership() => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            using var listener = new LowLevelKeyPatternListener(hook);
            int notifications = 0;
            listener.PatternChanged += (_, _) => notifications++;
            Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.A, true));
            listener.Dispose();

            bool firstRepeat = KeyPatternTestSupport.Send(hook, KeyCode.A, true);
            int subscriptionsAfterRepeat = KeyPatternTestSupport.SubscriberCount(hook);
            bool secondRepeat = KeyPatternTestSupport.Send(hook, KeyCode.A, true);
            bool released = KeyPatternTestSupport.Send(hook, KeyCode.A, false);
            KeyPatternTestSupport.DrainDispatcher();

            Assert.IsTrue(firstRepeat);
            Assert.AreEqual(1, subscriptionsAfterRepeat, "Repeat-down consumed the owned key-up.");
            Assert.IsTrue(secondRepeat);
            Assert.IsTrue(released, "The owned key-up escaped after repeat-down.");
            Assert.AreEqual(0, KeyPatternTestSupport.SubscriberCount(hook));
            Assert.AreEqual(0, notifications);
            Assert.IsNull(listener.Pattern);
        });

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void CapturedWorkerCallbackReleasedAfterDisposeIsRejected(bool alreadyHandled) => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            using var listener = new LowLevelKeyPatternListener(hook);
            int notifications = 0;
            listener.PatternChanged += (_, _) => notifications++;
            using var callbackCaptured = new ManualResetEventSlim();
            using var releaseCallback = new ManualResetEventSlim();
            Exception? failure = null;
            bool downHandled = !alreadyHandled;
            bool upHandled = !alreadyHandled;
            var worker = new Thread(() =>
            {
                try
                {
                    var captured = KeyPatternTestSupport.Subscribers(hook)!;
                    callbackCaptured.Set();
                    if (!releaseCallback.Wait(Guard)) throw new TimeoutException("Captured callback was not released.");
                    var down = new LowLevelKeyboardHook.KeyStateChangedEventArgs(KeyCode.B, true) { Handled = alreadyHandled };
                    var up = new LowLevelKeyboardHook.KeyStateChangedEventArgs(KeyCode.B, false) { Handled = alreadyHandled };
                    captured(hook, ref down);
                    captured(hook, ref up);
                    downHandled = down.Handled;
                    upHandled = up.Handled;
                }
                catch (Exception exception) { failure = exception; }
            }) { IsBackground = true };
            worker.Start();
            try
            {
                Assert.IsTrue(callbackCaptured.Wait(Guard), "Worker did not capture the live callback.");
                listener.Dispose();
            }
            finally
            {
                listener.Dispose();
                releaseCallback.Set();
                Assert.IsTrue(worker.Join(Guard), "Owned captured callback worker did not finish.");
            }
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
            KeyPatternTestSupport.DrainDispatcher();

            Assert.AreEqual(alreadyHandled, downHandled);
            Assert.AreEqual(alreadyHandled, upHandled);
            Assert.AreEqual(0, notifications);
            Assert.IsNull(listener.Pattern);
            Assert.AreEqual(0, KeyPatternTestSupport.SubscriberCount(hook));
        });

        [TestMethod]
        public void DispatcherPostDoesNotHoldTheLifetimeGate() => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            using var listener = new LowLevelKeyPatternListener(hook);
            Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.A, true));
            using var peerCompleted = new ManualResetEventSlim();
            Exception? failure = null;
            Thread? peer = null;
            bool completedDuringPost = false;
            DispatcherHookEventHandler onPosted = (_, _) =>
            {
                if (peer != null) return;
                peer = new Thread(() =>
                {
                    try { listener.Dispose(); }
                    catch (Exception exception) { failure = exception; }
                    finally { peerCompleted.Set(); }
                }) { IsBackground = true };
                peer.Start();
                // A same-thread Dispose would let a reentrant Monitor falsely
                // pass. The peer must finish while OperationPosted is active.
                completedDuringPost = peerCompleted.Wait(Guard);
            };
            listener.Dispatcher.Hooks.OperationPosted += onPosted;
            try { Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.A, false)); }
            finally
            {
                listener.Dispatcher.Hooks.OperationPosted -= onPosted;
                if (peer != null) Assert.IsTrue(peer.Join(Guard), "Owned posting probe worker did not finish.");
                listener.Dispose();
            }
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
            KeyPatternTestSupport.DrainDispatcher();

            Assert.IsNotNull(peer, "The recording did not post a notification.");
            Assert.IsTrue(completedDuringPost, "Dispatcher posting held the lifetime gate.");
            Assert.IsFalse(listener.IsListening);
            Assert.AreEqual(0, KeyPatternTestSupport.SubscriberCount(hook));
        });

        [TestMethod]
        public void SubscriberInvocationDoesNotHoldTheLifetimeGate() => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            using var listener = new LowLevelKeyPatternListener(hook);
            using var peerCompleted = new ManualResetEventSlim();
            Exception? failure = null;
            Thread? peer = null;
            bool completedDuringNotification = false;
            bool peerHandled = false;
            int notifications = 0;
            KeyPatternChangedEventHandler handler = (_, _) =>
            {
                notifications++;
                peer = new Thread(() =>
                {
                    try { peerHandled = KeyPatternTestSupport.Send(hook, KeyCode.B, true); }
                    catch (Exception exception) { failure = exception; }
                    finally { peerCompleted.Set(); }
                }) { IsBackground = true };
                peer.Start();
                completedDuringNotification = peerCompleted.Wait(Guard);
            };
            listener.PatternChanged += handler;
            try
            {
                KeyPatternTestSupport.Send(hook, KeyCode.A, true);
                KeyPatternTestSupport.Send(hook, KeyCode.A, false);
                KeyPatternTestSupport.DrainDispatcher();
            }
            finally
            {
                listener.PatternChanged -= handler;
                if (peer != null) Assert.IsTrue(peer.Join(Guard), "Owned subscriber probe worker did not finish.");
                // Balance the peer's admitted key-down before disposing.
                KeyPatternTestSupport.Send(hook, KeyCode.B, false);
                KeyPatternTestSupport.DrainDispatcher();
                listener.Dispose();
            }
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();

            Assert.AreEqual(1, notifications);
            Assert.IsTrue(completedDuringNotification, "PatternChanged held the hook admission gate.");
            Assert.IsTrue(peerHandled);
            Assert.AreEqual(0, KeyPatternTestSupport.SubscriberCount(hook));
        });

        [TestMethod]
        public void AlreadyAdmittedNotificationMayDisposeAndFinishItsDelegateList() => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            using var listener = new LowLevelKeyPatternListener(hook);
            int notifications = 0;
            listener.PatternChanged += (_, _) => { listener.Dispose(); notifications++; };
            listener.PatternChanged += (_, _) => notifications++;
            KeyPatternTestSupport.Send(hook, KeyCode.A, true);
            KeyPatternTestSupport.Send(hook, KeyCode.A, false);

            KeyPatternTestSupport.DrainDispatcher();

            Assert.AreEqual(2, notifications);
            Assert.IsFalse(listener.IsListening);
            Assert.AreEqual(0, KeyPatternTestSupport.SubscriberCount(hook));
        });

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void HundredNormalOrDrainingLifetimesLeaveBorrowedHookReusable(bool draining) => KeyPatternTestSupport.RunSta(() =>
        {
            var hook = KeyPatternTestSupport.CreateHook();
            var patterns = new List<KeyCode[]>();
            for (int cycle = 0; cycle < 100; cycle++)
            {
                using var listener = new LowLevelKeyPatternListener(hook);
                Assert.IsTrue(listener.IsListening);
                Assert.AreEqual(1, KeyPatternTestSupport.SubscriberCount(hook));
                listener.PatternChanged += (_, e) => patterns.Add(e.Keys.ToArray());
                Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.A, true));
                if (draining)
                {
                    listener.Dispose();
                    listener.Dispose();
                    Assert.AreEqual(1, KeyPatternTestSupport.SubscriberCount(hook));
                    Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.A, false));
                    KeyPatternTestSupport.DrainDispatcher();
                }
                else
                {
                    Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.A, false));
                    KeyPatternTestSupport.DrainDispatcher();
                    CollectionAssert.AreEqual(new[] { KeyCode.A }, patterns[cycle]);
                    listener.Dispose();
                    listener.Dispose();
                }
                Assert.IsFalse(listener.IsListening);
                Assert.AreEqual(0, KeyPatternTestSupport.SubscriberCount(hook), $"Cycle {cycle} retained a subscription.");
            }
            Assert.AreEqual(draining ? 0 : 100, patterns.Count);
        });

        [TestMethod]
        public void LowLevelPatternLifetimeCounterScenario() => KeyPatternTestSupport.RunSta(() =>
        {
            const int cycles = 100;
            var hook = KeyPatternTestSupport.CreateHook();
            int normalNotifications = 0, queuedNotifications = 0, incorrectQueuedSnapshots = 0;
            int lateNotifications = 0, staleUnrelatedSuppressions = 0, lostOwnedReleases = 0;
            int retainedDrainSubscriptions = 0, remainingSubscriptions = 0;

            void ObserveDetached()
            {
                int count = KeyPatternTestSupport.SubscriberCount(hook);
                remainingSubscriptions += count;
                Assert.AreEqual(0, count, "The next lifetime must borrow a hook without stale subscriptions.");
            }

            for (int cycle = 0; cycle < cycles; cycle++)
            {
                // Every cycle uses six actual listener lifetimes on the same
                // native-free hook event owner. Both DLLs run identical inputs.
                using (var normal = new LowLevelKeyPatternListener(hook))
                {
                    var patterns = new List<IReadOnlySet<KeyCode>>();
                    normal.PatternChanged += (_, e) => patterns.Add(e.Keys);
                    Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.LeftCtrl, true));
                    Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.A, true));
                    Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.A, false));
                    KeyPatternTestSupport.DrainDispatcher();
                    Assert.AreEqual(1, patterns.Count);
                    CollectionAssert.AreEquivalent(new[] { KeyCode.LeftCtrl, KeyCode.A }, patterns[0].ToArray());
                    Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.LeftCtrl, false));
                    KeyPatternTestSupport.DrainDispatcher();
                    Assert.AreEqual(1, patterns.Count);
                    normalNotifications += patterns.Count;
                    normal.Dispose();
                }
                ObserveDetached();

                using (var queued = new LowLevelKeyPatternListener(hook))
                {
                    var patterns = new List<IReadOnlySet<KeyCode>>();
                    queued.PatternChanged += (_, e) => patterns.Add(e.Keys);
                    Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.A, true));
                    Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.A, false));
                    Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.B, true));
                    Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.B, false));
                    Assert.AreEqual(0, patterns.Count);
                    KeyPatternTestSupport.DrainDispatcher();
                    Assert.AreEqual(2, patterns.Count);
                    queuedNotifications += patterns.Count;
                    if (!patterns[0].SetEquals(new[] { KeyCode.A })) incorrectQueuedSnapshots++;
                    if (!patterns[1].SetEquals(new[] { KeyCode.B })) incorrectQueuedSnapshots++;
                    queued.Dispose();
                }
                ObserveDetached();

                using (var stopped = new LowLevelKeyPatternListener(hook))
                {
                    int notifications = 0;
                    stopped.PatternChanged += (_, _) => notifications++;
                    Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.A, true));
                    Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.A, false));
                    stopped.Dispose();
                    stopped.Dispose();
                    KeyPatternTestSupport.DrainDispatcher();
                    Assert.IsTrue(notifications is 0 or 1);
                    lateNotifications += notifications;
                }
                ObserveDetached();

                using (var stale = new LowLevelKeyPatternListener(hook))
                {
                    int notifications = 0;
                    stale.PatternChanged += (_, _) => notifications++;
                    var captured = KeyPatternTestSupport.Subscribers(hook)!;
                    stale.Dispose();
                    stale.Dispose();
                    var down = new LowLevelKeyboardHook.KeyStateChangedEventArgs(KeyCode.B, true);
                    var up = new LowLevelKeyboardHook.KeyStateChangedEventArgs(KeyCode.B, false);
                    captured(hook, ref down);
                    captured(hook, ref up);
                    if (down.Handled) staleUnrelatedSuppressions++;
                    if (up.Handled) staleUnrelatedSuppressions++;
                    KeyPatternTestSupport.DrainDispatcher();
                    Assert.IsTrue(notifications is 0 or 1);
                    lateNotifications += notifications;
                }
                ObserveDetached();

                using (var held = new LowLevelKeyPatternListener(hook))
                {
                    int notifications = 0;
                    held.PatternChanged += (_, _) => notifications++;
                    var captured = KeyPatternTestSupport.Subscribers(hook)!;
                    Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.A, true));
                    held.Dispose();
                    held.Dispose();
                    Assert.AreEqual(1, KeyPatternTestSupport.SubscriberCount(hook));
                    var up = new LowLevelKeyboardHook.KeyStateChangedEventArgs(KeyCode.A, false);
                    captured(hook, ref up);
                    Assert.IsTrue(up.Handled);
                    KeyPatternTestSupport.DrainDispatcher();
                    Assert.IsTrue(notifications is 0 or 1);
                    lateNotifications += notifications;
                    int retained = KeyPatternTestSupport.SubscriberCount(hook);
                    Assert.IsTrue(retained is 0 or 1);
                    retainedDrainSubscriptions += retained;
                    // After recording all outcomes, the same extra release in
                    // both variants detaches R0's obsolete cleanup callback.
                    // This fixture cleanup is outside the observed counters.
                    Assert.IsFalse(KeyPatternTestSupport.Send(hook, KeyCode.A, false));
                }
                ObserveDetached();

                using (var repeated = new LowLevelKeyPatternListener(hook))
                {
                    int notifications = 0;
                    repeated.PatternChanged += (_, _) => notifications++;
                    Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.A, true));
                    repeated.Dispose();
                    repeated.Dispose();
                    Assert.IsTrue(KeyPatternTestSupport.Send(hook, KeyCode.A, true));
                    if (!KeyPatternTestSupport.Send(hook, KeyCode.A, false)) lostOwnedReleases++;
                    KeyPatternTestSupport.DrainDispatcher();
                    Assert.AreEqual(0, notifications);
                }
                ObserveDetached();
            }

            Assert.AreEqual(cycles, normalNotifications);
            Assert.AreEqual(2 * cycles, queuedNotifications);
            Assert.AreEqual(0, remainingSubscriptions);
            Console.WriteLine($"PERFCOUNTER lowlevel-pattern-lifetime cycles {cycles}");
            Console.WriteLine($"PERFCOUNTER lowlevel-pattern-lifetime normal-notifications {normalNotifications}");
            Console.WriteLine($"PERFCOUNTER lowlevel-pattern-lifetime queued-notifications {queuedNotifications}");
            Console.WriteLine($"PERFCOUNTER lowlevel-pattern-lifetime incorrect-queued-snapshots {incorrectQueuedSnapshots}");
            Console.WriteLine($"PERFCOUNTER lowlevel-pattern-lifetime late-notifications {lateNotifications}");
            Console.WriteLine($"PERFCOUNTER lowlevel-pattern-lifetime stale-unrelated-suppressions {staleUnrelatedSuppressions}");
            Console.WriteLine($"PERFCOUNTER lowlevel-pattern-lifetime lost-owned-releases {lostOwnedReleases}");
            Console.WriteLine($"PERFCOUNTER lowlevel-pattern-lifetime retained-drain-subscriptions {retainedDrainSubscriptions}");
            Console.WriteLine($"PERFCOUNTER lowlevel-pattern-lifetime remaining-subscriptions {remainingSubscriptions}");
        });
    }
}
