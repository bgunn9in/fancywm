#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WinMan;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class WindowDraggerTest
    {
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

        [TestMethod]
        public async Task NormalDragPreservesWindowOffsetSizeAndBalancedNotificationsFor100Lifetimes()
        {
            for (int cycle = 0; cycle < 100; cycle++)
            {
                var fixture = new DragFixture { State = WindowState.Maximized, QueuedDispatch = true };
                var drag = fixture.Drag;
                Assert.AreSame(fixture.Window.Object, drag.Window);
                drag.Begin(activateWindow: true);
                Assert.AreEqual(1, fixture.Subscriptions);
                fixture.Pump();
                fixture.Raise(new Point(210, 330));
                fixture.Raise(new Point(-20, -50));
                CollectionAssert.AreEqual(new[]
                {
                    Rectangle.OffsetAndSize(200, 310, 100, 80),
                    Rectangle.OffsetAndSize(-30, -70, 100, 80),
                }, fixture.Writes);
                drag.End();
                await fixture.PumpUntilAsync(drag.Completion);
                Assert.AreEqual(WindowState.Restored, fixture.State);
                Assert.AreEqual(1, fixture.Restores);
                Assert.AreEqual(1, fixture.FocusCalls);
                Assert.AreEqual(0, fixture.Subscriptions);
                CollectionAssert.AreEqual(new[] { "start", "end" }, fixture.Notifications);
            }
        }

        [TestMethod]
        public async Task PumpUntilCompletionWaitsForEndClaimedBeforePhysicalEnqueue()
        {
            using var dispatchEntered = new ManualResetEventSlim();
            using var releaseDispatch = new ManualResetEventSlim();
            var enqueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fixture = new DragFixture { QueuedDispatch = true };
            fixture.Drag.Begin(false);
            fixture.Pump();
            fixture.DispatchOverride = action =>
            {
                // Advance has claimed End, but its dispatcher submission has not
                // physically reached the fake queue yet.
                dispatchEntered.Set();
                Assert.IsTrue(releaseDispatch.Wait(Guard));
                Task dispatched = fixture.EnqueueDispatch(action);
                enqueued.SetResult();
                return dispatched;
            };
            Task ending = Task.Run(fixture.Drag.End);
            Task? pumping = null;
            try
            {
                Assert.IsTrue(dispatchEntered.Wait(Guard));
                fixture.Drag.End();
                fixture.Pump();
                Assert.AreEqual(0, fixture.Ends);
                Assert.IsFalse(fixture.Drag.Completion.IsCompleted);

                pumping = fixture.PumpUntilAsync(fixture.Drag.Completion);
                Assert.IsFalse(pumping.IsCompleted,
                    "An empty queue is not terminal while the owner has a claimed dispatch.");
                releaseDispatch.Set();
                await enqueued.Task.WaitAsync(Guard);
                await ending.WaitAsync(Guard);
                await pumping;

                Assert.AreEqual(0, fixture.Subscriptions);
                CollectionAssert.AreEqual(new[] { "start", "end" }, fixture.Notifications);
            }
            finally
            {
                releaseDispatch.Set();
                await ending.WaitAsync(Guard);
                await enqueued.Task.WaitAsync(Guard);
                fixture.Pump();
                await fixture.Drag.Completion.WaitAsync(Guard);
                // On the expected old-pump red, retain its failure from the
                // await above while leaving no queued callback or pending task.
                if (pumping != null)
                {
                    try { await pumping; }
                    catch (TimeoutException) { }
                }
            }
        }

        [TestMethod]
        public async Task RepeatedBeginEndAndBeginAfterEndCannotDuplicateOrReopenDrag()
        {
            var fixture = new DragFixture();
            var drag = fixture.Drag;
            drag.Begin(true);
            drag.Begin(true);
            Assert.AreEqual(1, fixture.Subscriptions);
            Assert.AreEqual(1, fixture.FocusCalls);
            Assert.AreEqual(1, fixture.Starts);
            drag.End();
            drag.End();
            drag.Begin(true);
            await drag.Completion.WaitAsync(Guard);
            Assert.AreEqual(0, fixture.Subscriptions);
            Assert.AreEqual(1, fixture.FocusCalls);
            Assert.AreEqual(1, fixture.Starts);
            Assert.AreEqual(1, fixture.Ends);
        }

        [TestMethod]
        public async Task CompletionWaitsForBlockedUnsubscribeDuringDuplicateEnd()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var fixture = new DragFixture();
            fixture.OnRemoving = () =>
            {
                entered.Set();
                Assert.IsTrue(release.Wait(Guard));
            };
            fixture.Drag.Begin(false);
            var end = Task.Run(fixture.Drag.End);
            try
            {
                Assert.IsTrue(entered.Wait(Guard));
                fixture.Drag.End();
                Assert.AreEqual(1, fixture.Subscriptions);
                Assert.AreEqual(1, fixture.RemovalCalls);
                Assert.IsFalse(fixture.Drag.Completion.IsCompleted,
                    "Completion must retain workspace ownership until the accepted remove accessor returns.");
            }
            finally
            {
                release.Set();
                await end.WaitAsync(Guard);
            }
            await fixture.Drag.Completion.WaitAsync(Guard);
            Assert.AreEqual(0, fixture.Subscriptions);
            Assert.AreEqual(1, fixture.RemovalCalls);
            CollectionAssert.AreEqual(new[] { "start", "end" }, fixture.Notifications);
        }

        [TestMethod]
        public async Task UnsubscribeFailureIsPreservedAndCapturedCallbackIsRejected()
        {
            var fixture = new DragFixture();
            var failure = new InvalidOperationException("The workspace rejected cursor unsubscription.");
            fixture.Drag.Begin(false);
            var captured = fixture.Capture();
            fixture.OnRemoving = () => throw failure;
            fixture.Drag.End();

            var observed = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => fixture.Drag.Completion.WaitAsync(Guard));
            Assert.AreSame(failure, observed);
            Assert.AreEqual(1, fixture.RemovalCalls);
            Assert.AreEqual(1, fixture.Subscriptions,
                "The fixture keeps the physical subscription when its remove accessor throws.");
            captured(new Point(700, 800));
            Assert.AreEqual(0, fixture.Writes.Count,
                "A callback captured before failed cleanup must still lose logical admission.");
            CollectionAssert.AreEqual(new[] { "start", "end" }, fixture.Notifications);
        }

        [TestMethod]
        public async Task EndBeforeBeginClosesAdmissionWithoutNativeOrNotificationEffects()
        {
            var fixture = new DragFixture { State = WindowState.Maximized };
            fixture.Drag.End();
            fixture.Drag.Begin(true);
            fixture.Drag.End();
            await fixture.Drag.Completion.WaitAsync(Guard);
            Assert.AreEqual(0, fixture.StateReads);
            Assert.AreEqual(0, fixture.Restores);
            Assert.AreEqual(0, fixture.FocusCalls);
            Assert.AreEqual(0, fixture.Subscriptions);
            Assert.AreEqual(0, fixture.Starts);
            Assert.AreEqual(0, fixture.Ends);
        }

        [TestMethod]
        public async Task EndBeforeNotificationPredecessorSettlesCannotReleaseSuccessorEarly()
        {
            var predecessor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fixture = new DragFixture();
            fixture.Drag.SetNotificationPredecessor(predecessor.Task);
            fixture.Drag.End();
            Assert.IsFalse(fixture.Drag.Completion.IsCompleted);
            fixture.Drag.Begin(false);
            predecessor.SetResult();
            await fixture.Drag.Completion.WaitAsync(Guard);
            Assert.AreEqual(0, fixture.Subscriptions);
            Assert.AreEqual(0, fixture.Starts);
            Assert.AreEqual(0, fixture.Ends);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task FailedOrCanceledNotificationPredecessorOnlyDelaysNextDrag(bool canceled)
        {
            var predecessor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fixture = new DragFixture { OnStarted = started.SetResult };
            fixture.Drag.SetNotificationPredecessor(predecessor.Task);
            fixture.Drag.Begin(false);
            Assert.AreEqual(0, fixture.Starts);

            if (canceled) { predecessor.SetCanceled(new CancellationToken(canceled: true)); }
            else { predecessor.SetException(new InvalidOperationException("The prior drag failed.")); }
            await started.Task.WaitAsync(Guard);
            fixture.Drag.End();
            await fixture.Drag.Completion.WaitAsync(Guard);
            CollectionAssert.AreEqual(new[] { "start", "end" }, fixture.Notifications);
            Assert.AreEqual(0, fixture.Subscriptions);
        }

        [DataTestMethod]
        [DataRow("state")]
        [DataRow("restore")]
        [DataRow("focus")]
        public async Task EndInsideBeginNativeStagePreventsLaterSubscriptionAndEffects(string stage)
        {
            var fixture = new DragFixture { State = WindowState.Maximized };
            var drag = fixture.Drag;
            if (stage == "state") fixture.OnStateRead = drag.End;
            if (stage == "restore") fixture.OnRestore = drag.End;
            if (stage == "focus") fixture.OnFocus = drag.End;
            drag.Begin(true);
            await drag.Completion.WaitAsync(Guard);
            Assert.AreEqual(0, fixture.Subscriptions);
            Assert.AreEqual(0, fixture.Starts);
            Assert.AreEqual(0, fixture.Ends);
            Assert.AreEqual(stage == "state" ? 0 : 1, fixture.Restores);
            Assert.AreEqual(stage == "focus" ? 1 : 0, fixture.FocusCalls);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task EndDuringCursorSubscriptionAcquisitionReleasesReturnedOwnership(bool afterPublication)
        {
            var fixture = new DragFixture();
            var drag = fixture.Drag;
            if (afterPublication) fixture.OnAdded = drag.End;
            else fixture.OnAdding = drag.End;
            drag.Begin(false);
            await drag.Completion.WaitAsync(Guard);
            fixture.Raise(new Point(200, 300));
            Assert.AreEqual(0, fixture.Subscriptions);
            Assert.AreEqual(0, fixture.Writes.Count);
            Assert.AreEqual(0, fixture.Starts);
            Assert.AreEqual(0, fixture.Ends);
        }

        [TestMethod]
        public async Task EndBeforeQueuedStartSuppressesBothObsoleteNotifications()
        {
            var fixture = new DragFixture { QueuedDispatch = true };
            fixture.Drag.Begin(false);
            fixture.Drag.End();
            fixture.Pump();
            await fixture.Drag.Completion.WaitAsync(Guard);
            Assert.AreEqual(0, fixture.Starts);
            Assert.AreEqual(0, fixture.Ends);
            Assert.AreEqual(0, fixture.Subscriptions);
        }

        [TestMethod]
        public async Task QueuedStartAfterCursorWriteStillBalancesNotificationsAfterEnd()
        {
            var fixture = new DragFixture { QueuedDispatch = true };
            fixture.Drag.Begin(false);
            var captured = fixture.Capture();
            captured(new Point(210, 330));
            fixture.Drag.End();
            Assert.AreEqual(0, fixture.Starts);
            Assert.AreEqual(0, fixture.Subscriptions);
            Assert.IsFalse(fixture.Drag.Completion.IsCompleted);
            captured(new Point(800, 900));
            fixture.Pump();
            await fixture.Drag.Completion.WaitAsync(Guard);
            CollectionAssert.AreEqual(new[] { Rectangle.OffsetAndSize(200, 310, 100, 80) }, fixture.Writes);
            CollectionAssert.AreEqual(new[] { "start", "end" }, fixture.Notifications);
            Assert.AreEqual(1, fixture.Starts);
            Assert.AreEqual(1, fixture.Ends);
        }

        [DataTestMethod]
        [DataRow("start", "throw")]
        [DataRow("start", "fault")]
        [DataRow("start", "cancel")]
        [DataRow("end", "throw")]
        [DataRow("end", "fault")]
        [DataRow("end", "cancel")]
        public async Task FailedOrCanceledDispatchSettlesCompletionAndReleasesCursorOwnership(string stage, string mode)
        {
            var fixture = new DragFixture();
            var failure = new InvalidOperationException("The drag dispatcher rejected the notification.");
            var canceledToken = new CancellationToken(canceled: true);
            if (stage == "end")
            {
                fixture.Drag.Begin(false);
                fixture.Raise(new Point(210, 330));
            }
            var captured = fixture.Capture();
            fixture.DispatchOverride = _ => mode switch
            {
                "throw" => throw failure,
                "fault" => Task.FromException(failure),
                "cancel" => Task.FromCanceled(canceledToken),
                _ => throw new AssertFailedException("Unknown dispatch mode."),
            };
            if (stage == "start") fixture.Drag.Begin(false);
            fixture.Drag.End();
            if (mode == "cancel")
            {
                var observed = await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => fixture.Drag.Completion.WaitAsync(Guard));
                Assert.AreEqual(canceledToken, observed.CancellationToken);
            }
            else
            {
                var observed = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => fixture.Drag.Completion.WaitAsync(Guard));
                Assert.AreSame(failure, observed);
            }
            Assert.AreEqual(0, fixture.Subscriptions);
            captured(new Point(800, 900));
            Assert.AreEqual(stage == "end" ? 1 : 0, fixture.Writes.Count);
            if (stage == "end")
                Assert.AreEqual(Rectangle.OffsetAndSize(200, 310, 100, 80), fixture.Writes.Single());
            Assert.AreEqual(stage == "end" ? 1 : 0, fixture.Starts);
            Assert.AreEqual(0, fixture.Ends, "A dispatcher that never entered its callback cannot emit an end notification.");
        }

        [TestMethod]
        public async Task ReentrantEndDuringStartWaitsForStartedCallbackAndBalancesOnce()
        {
            var fixture = new DragFixture();
            bool pendingInsideStart = false;
            bool endInsideStart = false;
            fixture.OnStarted = () =>
            {
                fixture.Drag.End();
                pendingInsideStart = !fixture.Drag.Completion.IsCompleted;
                endInsideStart = fixture.Ends != 0;
            };
            fixture.Drag.Begin(false);
            await fixture.Drag.Completion.WaitAsync(Guard);
            Assert.IsTrue(pendingInsideStart);
            Assert.IsFalse(endInsideStart, "The terminal notification must follow the entered start callback.");
            CollectionAssert.AreEqual(new[] { "start", "end" }, fixture.Notifications);
            Assert.AreEqual(0, fixture.Subscriptions);
        }

        [TestMethod]
        public async Task EndWhileFocusIsBlockedReturnsWithoutAdmittingLateSubscription()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var fixture = new DragFixture();
            fixture.OnFocus = () => { entered.Set(); Assert.IsTrue(release.Wait(Guard)); };
            var begin = Task.Run(() => fixture.Drag.Begin(true));
            bool pending = false;
            try
            {
                Assert.IsTrue(entered.Wait(Guard));
                fixture.Drag.End();
                pending = !fixture.Drag.Completion.IsCompleted;
            }
            finally
            {
                release.Set();
                await begin.WaitAsync(Guard);
            }
            Assert.IsTrue(pending);
            await fixture.Drag.Completion.WaitAsync(Guard);
            Assert.AreEqual(0, fixture.Subscriptions);
            Assert.AreEqual(0, fixture.Starts);
            Assert.AreEqual(0, fixture.Ends);
        }

        [TestMethod]
        public async Task EndWhileCursorWriteIsBlockedDrainsEnteredWriteAndRejectsCapturedCallback()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var fixture = new DragFixture();
            int writesObservedAtEnd = -1;
            fixture.OnEnded = () => writesObservedAtEnd = fixture.Writes.Count;
            fixture.Drag.Begin(false);
            var captured = fixture.Capture();
            fixture.OnPosition = () => { entered.Set(); Assert.IsTrue(release.Wait(Guard)); };
            var move = Task.Run(() => captured(new Point(200, 300)));
            bool pending = false;
            try
            {
                Assert.IsTrue(entered.Wait(Guard));
                fixture.Drag.End();
                pending = !fixture.Drag.Completion.IsCompleted;
                Assert.AreEqual(0, fixture.Ends,
                    "The terminal notification must not overtake an already-entered cursor write.");
            }
            finally
            {
                release.Set();
                await move.WaitAsync(Guard);
            }
            fixture.OnPosition = null;
            captured(new Point(700, 800));
            await fixture.Drag.Completion.WaitAsync(Guard);
            Assert.IsTrue(pending);
            Assert.AreEqual(1, fixture.Writes.Count);
            Assert.AreEqual(Rectangle.OffsetAndSize(190, 280, 100, 80), fixture.Writes.Single());
            Assert.AreEqual(1, writesObservedAtEnd,
                "The terminal observer must see the final accepted rectangle write.");
            CollectionAssert.AreEqual(new[] { "start", "end" }, fixture.Notifications);
            Assert.AreEqual(0, fixture.Subscriptions);
        }

        [TestMethod]
        public async Task CursorFailureEndsDragAndRejectsCapturedCallbacks()
        {
            var fixture = new DragFixture();
            fixture.Drag.Begin(false);
            var captured = fixture.Capture();
            var failure = new InvalidWindowReferenceException(new IntPtr(10));
            fixture.OnPosition = () => throw failure;
            captured(new Point(200, 300));
            fixture.OnPosition = null;
            captured(new Point(400, 500));
            await fixture.Drag.Completion.WaitAsync(Guard);
            Assert.AreEqual(0, fixture.Writes.Count);
            Assert.AreEqual(0, fixture.Subscriptions);
            CollectionAssert.AreEqual(new[] { "start", "end" }, fixture.Notifications);
        }

        [TestMethod]
        public async Task TerminalNotificationPrecedesNextDragStartOnSharedDispatcher()
        {
            var pending = new Queue<(Action Action, TaskCompletionSource Completion)>();
            Task Dispatch(Action action)
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                pending.Enqueue((action, completion));
                return completion.Task;
            }
            void Pump()
            {
                while (pending.TryDequeue(out var next))
                {
                    try { next.Action(); next.Completion.SetResult(); }
                    catch (Exception error) { next.Completion.SetException(error); throw; }
                }
            }

            var order = new List<string>();
            var first = new DragFixture { DispatchOverride = Dispatch };
            var second = new DragFixture { DispatchOverride = Dispatch };
            first.OnStarted = () => order.Add("start-a");
            first.OnEnded = () => order.Add("end-a");
            second.OnStarted = () => order.Add("start-b");
            second.OnEnded = () => order.Add("end-b");

            first.Drag.Begin(false);
            first.Raise(new Point(210, 330));
            first.Drag.End();
            second.Drag.Begin(false);
            Pump();
            CollectionAssert.AreEqual(new[] { "start-a", "end-a", "start-b" }, order);

            second.Drag.End();
            Pump();
            await Task.WhenAll(first.Drag.Completion, second.Drag.Completion).WaitAsync(Guard);
            CollectionAssert.AreEqual(new[] { "start-a", "end-a", "start-b", "end-b" }, order);
        }

        [TestMethod]
        public async Task EndCannotSubmitBeforeBlockedStartDispatchSubmission()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var pending = new Queue<(Action Action, TaskCompletionSource Completion)>();
            var queueGate = new object();
            int submissions = 0;
            Task Dispatch(Action action)
            {
                if (Interlocked.Increment(ref submissions) == 1)
                {
                    entered.Set();
                    Assert.IsTrue(release.Wait(Guard));
                }
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (queueGate) { pending.Enqueue((action, completion)); }
                return completion.Task;
            }

            var fixture = new DragFixture { DispatchOverride = Dispatch };
            var begin = Task.Run(() => fixture.Drag.Begin(false));
            try
            {
                Assert.IsTrue(entered.Wait(Guard));
                fixture.Raise(new Point(210, 330));
                fixture.Drag.End();
            }
            finally
            {
                release.Set();
                await begin.WaitAsync(Guard);
            }

            while (true)
            {
                (Action Action, TaskCompletionSource Completion) next;
                lock (queueGate)
                {
                    if (!pending.TryDequeue(out next)) { break; }
                }
                try { next.Action(); next.Completion.SetResult(); }
                catch (Exception error) { next.Completion.SetException(error); throw; }
            }
            await fixture.Drag.Completion.WaitAsync(Guard);
            CollectionAssert.AreEqual(new[] { "start", "end" }, fixture.Notifications);
            Assert.AreEqual(2, submissions);
            Assert.AreEqual(0, fixture.Subscriptions);
        }

        [DataTestMethod]
        [DataRow("throw")]
        [DataRow("fault")]
        [DataRow("cancel")]
        public async Task RejectedStartAfterCursorWriteCannotEmitUnmatchedEnd(string mode)
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var failure = new InvalidOperationException("start rejected");
            var canceledToken = new CancellationToken(canceled: true);
            var fixture = new DragFixture
            {
                DispatchOverride = _ =>
                {
                    entered.Set();
                    Assert.IsTrue(release.Wait(Guard));
                    return mode switch
                    {
                        "throw" => throw failure,
                        "fault" => Task.FromException(failure),
                        "cancel" => Task.FromCanceled(canceledToken),
                        _ => throw new AssertFailedException("Unknown mode."),
                    };
                },
            };
            var begin = Task.Run(() => fixture.Drag.Begin(false));
            try
            {
                Assert.IsTrue(entered.Wait(Guard));
                fixture.Raise(new Point(210, 330));
            }
            finally
            {
                release.Set();
                await begin.WaitAsync(Guard);
            }

            Exception? observed = null;
            try { await fixture.Drag.Completion.WaitAsync(Guard); }
            catch (Exception error) { observed = error; }
            Assert.IsNotNull(observed);
            if (mode == "cancel") { Assert.IsInstanceOfType(observed, typeof(TaskCanceledException)); }
            else { Assert.AreSame(failure, observed); }
            Assert.AreEqual(1, fixture.Writes.Count);
            Assert.AreEqual(0, fixture.Starts);
            Assert.AreEqual(0, fixture.Ends);
            Assert.AreEqual(0, fixture.Subscriptions);
        }

        [TestMethod]
        public async Task WindowDraggerLifetimeCounterScenario()
        {
            const int cycles = 100;
            int lateSubscriptions = 0, lateWrites = 0, normalWrites = 0, normalStarts = 0, normalEnds = 0;
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                var late = new DragFixture();
                late.OnFocus = late.Drag.End;
                late.Drag.Begin(true);
                lateSubscriptions += late.Subscriptions;
                late.Raise(new Point(200, 300));
                lateWrites += late.Writes.Count;
                if (late.Writes.Count != 0)
                    Assert.AreEqual(Rectangle.OffsetAndSize(190, 280, 100, 80), late.Writes.Single());
                late.Drag.End();
                await late.Drag.Completion.WaitAsync(Guard);
                Assert.AreEqual(0, late.Subscriptions);

                var normal = new DragFixture();
                normal.Drag.Begin(false);
                normal.Raise(new Point(200, 300));
                normal.Drag.End();
                await normal.Drag.Completion.WaitAsync(Guard);
                Assert.AreEqual(Rectangle.OffsetAndSize(190, 280, 100, 80), normal.Writes.Single());
                CollectionAssert.AreEqual(new[] { "start", "end" }, normal.Notifications);
                Assert.AreEqual(0, normal.Subscriptions);
                normalWrites += normal.Writes.Count;
                normalStarts += normal.Starts;
                normalEnds += normal.Ends;
            }
            Console.WriteLine($"PERFCOUNTER window-dragger cycles {cycles}");
            Console.WriteLine($"PERFCOUNTER window-dragger late-subscriptions {lateSubscriptions}");
            Console.WriteLine($"PERFCOUNTER window-dragger late-writes {lateWrites}");
            Console.WriteLine($"PERFCOUNTER window-dragger normal-writes {normalWrites}");
            Console.WriteLine($"PERFCOUNTER window-dragger normal-starts {normalStarts}");
            Console.WriteLine($"PERFCOUNTER window-dragger normal-ends {normalEnds}");
        }

        private sealed class DragFixture
        {
            public Mock<IWindow> Window { get; } = new();
            public Mock<IWorkspace> Workspace { get; } = new();
            public WindowDragger Drag { get; }
            public WindowState State = WindowState.Restored;
            public int StateReads, Restores, FocusCalls, Starts, Ends;
            public bool QueuedDispatch;
            public List<Rectangle> Writes { get; } = [];
            public List<string> Notifications { get; } = [];
            public Action? OnStateRead, OnRestore, OnFocus, OnAdding, OnAdded, OnRemoving, OnPosition, OnStarted, OnEnded;
            public Func<Action, Task>? DispatchOverride;
            public int RemovalCalls;
            private readonly object m_gate = new();
            private EventHandler<CursorLocationChangedEventArgs>? m_handlers;
            private readonly Queue<(Action Action, TaskCompletionSource Completion)> m_dispatches = new();
            private TaskCompletionSource m_dispatchAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Subscriptions { get { lock (m_gate) return m_handlers?.GetInvocationList().Length ?? 0; } }

            public DragFixture()
            {
                Window.SetupGet(value => value.Workspace).Returns(Workspace.Object);
                Window.SetupGet(value => value.Position).Returns(Rectangle.OffsetAndSize(100, 200, 100, 80));
                Workspace.SetupGet(value => value.CursorLocation).Returns(new Point(110, 220));
                Window.SetupGet(value => value.State).Returns(() => { StateReads++; OnStateRead?.Invoke(); return State; });
                Window.Setup(value => value.SetState(It.IsAny<WindowState>())).Callback<WindowState>(state =>
                { Restores++; State = state; OnRestore?.Invoke(); });
                Window.Setup(value => value.SetPosition(It.IsAny<Rectangle>())).Callback<Rectangle>(rectangle =>
                { OnPosition?.Invoke(); Writes.Add(rectangle); });
                Workspace.SetupAdd(value => value.CursorLocationChanged += It.IsAny<EventHandler<CursorLocationChangedEventArgs>>())
                    .Callback<EventHandler<CursorLocationChangedEventArgs>>(handler =>
                    { OnAdding?.Invoke(); lock (m_gate) m_handlers += handler; OnAdded?.Invoke(); });
                Workspace.SetupRemove(value => value.CursorLocationChanged -= It.IsAny<EventHandler<CursorLocationChangedEventArgs>>())
                    .Callback<EventHandler<CursorLocationChangedEventArgs>>(handler =>
                    { RemovalCalls++; OnRemoving?.Invoke(); lock (m_gate) m_handlers -= handler; });
                Drag = new WindowDragger(Window.Object,
                    () => { FocusCalls++; OnFocus?.Invoke(); },
                    () => { Starts++; Notifications.Add("start"); OnStarted?.Invoke(); },
                    () => { Ends++; Notifications.Add("end"); OnEnded?.Invoke(); }, Dispatch);
            }

            public Action<Point> Capture()
            {
                EventHandler<CursorLocationChangedEventArgs>? handlers;
                lock (m_gate) handlers = m_handlers;
                return point => handlers?.Invoke(Workspace.Object,
                    new CursorLocationChangedEventArgs(Workspace.Object, point, new Point(110, 220)));
            }
            public void Raise(Point point) => Capture()(point);

            private Task Dispatch(Action action)
            {
                if (DispatchOverride != null) return DispatchOverride(action);
                if (!QueuedDispatch)
                {
                    try { action(); return Task.CompletedTask; }
                    catch (Exception exception) { return Task.FromException(exception); }
                }
                return EnqueueDispatch(action);
            }

            public Task EnqueueDispatch(Action action)
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (m_gate)
                {
                    m_dispatches.Enqueue((action, completion));
                    m_dispatchAvailable.TrySetResult();
                }
                return completion.Task;
            }

            public async Task PumpUntilAsync(Task completion)
            {
                using var deadline = new CancellationTokenSource(Guard);
                try
                {
                    while (!completion.IsCompleted)
                    {
                        Pump(deadline.Token);
                        if (completion.IsCompleted) { break; }
                        Task available;
                        lock (m_gate)
                        {
                            if (m_dispatches.Count != 0) { continue; }
                            if (m_dispatchAvailable.Task.IsCompleted)
                            {
                                m_dispatchAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
                            }
                            available = m_dispatchAvailable.Task;
                        }
                        // Reset and enqueue share the gate, so a submission
                        // arriving after the empty check cannot lose its wakeup.
                        await Task.WhenAny(available, completion).WaitAsync(deadline.Token);
                    }
                    await completion;
                }
                catch (OperationCanceledException error) when (error.CancellationToken == deadline.Token)
                {
                    throw new TimeoutException("The fake dispatcher did not complete the owned operation before its deadline.", error);
                }
            }

            public void Pump() => Pump(CancellationToken.None);

            private void Pump(CancellationToken deadline)
            {
                while (true)
                {
                    deadline.ThrowIfCancellationRequested();
                    (Action Action, TaskCompletionSource Completion) next;
                    lock (m_gate)
                    {
                        if (!m_dispatches.TryDequeue(out next)) return;
                    }
                    try { next.Action(); next.Completion.SetResult(); }
                    catch (Exception exception) { next.Completion.SetException(exception); throw; }
                }
            }
        }
    }
}
