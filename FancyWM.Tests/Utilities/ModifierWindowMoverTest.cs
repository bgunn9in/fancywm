#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Serilog;
using WinMan;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class ModifierWindowMoverTest
    {
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void ConstructorSubscriptionFailureCompensatesOnceAndPreservesOriginalException(bool cleanupFails)
        {
            var failure = new InvalidOperationException("Hook subscription failed after publication.");
            var cleanupFailure = new ApplicationException("Hook cleanup failed after removal.");
            LowLevelMouseHook.ButtonStateChangedEventHandler? handlers = null;
            int additions = 0, removals = 0;
            var observed = Assert.ThrowsException<InvalidOperationException>(() =>
                new ModifierWindowMover(Mock.Of<IWorkspace>(),
                    handler => { additions++; handlers += handler; throw failure; },
                    handler =>
                    {
                        removals++;
                        handlers -= handler;
                        if (cleanupFails) throw cleanupFailure;
                    },
                    _ => throw new AssertFailedException("Construction cannot start a drag."),
                    () => true, () => false, Mock.Of<ILogger>()));
            Assert.AreSame(failure, observed);
            Assert.AreEqual(1, additions);
            Assert.AreEqual(1, removals);
            Assert.IsNull(handlers);
            if (cleanupFails)
                Assert.AreSame(cleanupFailure, observed.Data["ModifierWindowMover.ConstructionCleanupException"]);
        }

        [DataTestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public async Task NormalPressMoveReleasePreservesOffsetSizeAndActivation(bool autoFocus, bool activateModifier)
        {
            using var fixture = new MoverFixture { ActivateModifier = activateModifier };
            fixture.Mover.AutoFocus = autoFocus;
            Assert.IsTrue(fixture.Send(true));
            Assert.AreEqual(new Point(110, 220), fixture.LastFindPoint);
            Assert.AreEqual(1, fixture.Factories);
            Assert.AreEqual(1, fixture.CursorSubscriptions);
            Assert.AreEqual(autoFocus || activateModifier ? 1 : 0, fixture.FocusCalls);
            var capturedCursor = fixture.CaptureCursor();
            capturedCursor(new Point(210, 330));
            capturedCursor(new Point(-20, -50));
            Assert.IsTrue(fixture.Send(false));
            await fixture.Drags.Single().Completion.WaitAsync(Guard);
            Assert.AreEqual(0, fixture.CursorSubscriptions);
            capturedCursor(new Point(800, 900));
            CollectionAssert.AreEqual(new[]
            {
                Rectangle.OffsetAndSize(200, 310, 100, 80),
                Rectangle.OffsetAndSize(-30, -70, 100, 80),
            }, fixture.Writes);
            CollectionAssert.AreEqual(new[] { "start", "end" }, fixture.Notifications);
            Assert.IsFalse(fixture.Send(false));
            fixture.Mover.Dispose();
            await fixture.Mover.Completion.WaitAsync(Guard);
        }

        [TestMethod]
        public async Task DisposeActiveDragBalancesOwnershipAndRejectsLateCallbacksFor100Lifetimes()
        {
            for (int cycle = 0; cycle < 100; cycle++)
            {
                using var fixture = new MoverFixture();
                Assert.IsTrue(fixture.Send(true));
                var capturedHook = fixture.CaptureHook();
                var capturedCursor = fixture.CaptureCursor();
                capturedCursor(new Point(210, 330));
                fixture.Mover.Dispose();
                fixture.Mover.Dispose();
                Assert.AreEqual(1, fixture.HookAdds);
                Assert.AreEqual(1, fixture.HookRemoves);
                Assert.AreEqual(0, fixture.HookSubscriptions);
                Assert.AreEqual(0, fixture.CursorSubscriptions);
                Assert.IsFalse(Send(capturedHook, true));
                Assert.IsFalse(Send(capturedHook, false));
                capturedCursor(new Point(800, 900));
                await fixture.Mover.Completion.WaitAsync(Guard);
                await fixture.Drags.Single().Completion.WaitAsync(Guard);
                Assert.AreEqual(1, fixture.Finds);
                Assert.AreEqual(1, fixture.Factories);
                CollectionAssert.AreEqual(new[] { Rectangle.OffsetAndSize(200, 310, 100, 80) }, fixture.Writes);
                CollectionAssert.AreEqual(new[] { "start", "end" }, fixture.Notifications);
            }
        }

        [TestMethod]
        public async Task DuplicateDisposeAndCapturedHookCannotReopenIdleMover()
        {
            using var fixture = new MoverFixture();
            var captured = fixture.CaptureHook();
            fixture.Mover.Dispose();
            fixture.Mover.Dispose();
            fixture.Mover.IsEnabled = true;
            Assert.IsFalse(Send(captured, true));
            Assert.IsFalse(Send(captured, false));
            await fixture.Mover.Completion.WaitAsync(Guard);
            Assert.AreEqual(1, fixture.HookAdds);
            Assert.AreEqual(1, fixture.HookRemoves);
            Assert.AreEqual(0, fixture.HookSubscriptions);
            Assert.AreEqual(0, fixture.Finds);
            Assert.AreEqual(0, fixture.Factories);
            Assert.AreEqual(0, fixture.FocusCalls);
            Assert.AreEqual(0, fixture.CursorSubscriptions);
        }

        [TestMethod]
        public async Task DisposeDuringBeginDoesNotSubscribeCursorOrApplyLateDrag()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var fixture = new MoverFixture();
            fixture.Mover.AutoFocus = true;
            fixture.OnFocus = () => { entered.Set(); Assert.IsTrue(release.Wait(Guard)); };
            var capturedHook = fixture.CaptureHook();
            var press = Task.Run(() => Send(capturedHook, true));
            bool pending = false;
            try
            {
                Assert.IsTrue(entered.Wait(Guard));
                fixture.Mover.Dispose();
                pending = !fixture.Mover.Completion.IsCompleted;
                Assert.AreEqual(0, fixture.HookSubscriptions);
            }
            finally
            {
                release.Set();
                await press.WaitAsync(Guard);
            }
            Assert.AreEqual(0, fixture.CursorSubscriptions, "Dispose must stop the entered drag before its delayed Begin can subscribe.");
            fixture.RaiseCursor(new Point(800, 900));
            Assert.IsFalse(Send(capturedHook, true));
            Assert.AreEqual(0, fixture.Writes.Count);
            Assert.AreEqual(0, fixture.Notifications.Count);
            Assert.AreEqual(1, fixture.FocusCalls);
            Assert.AreEqual(1, fixture.Factories);
            Assert.IsTrue(pending, "Mover completion must include the already-entered Begin callback.");
            await fixture.Mover.Completion.WaitAsync(Guard);
            await fixture.Drags.Single().Completion.WaitAsync(Guard);
        }

        [DataTestMethod]
        [DataRow("find")]
        [DataRow("factory")]
        public async Task DisposeDuringLookupOrFactoryStopsReturnedDragBeforeBegin(string stage)
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var fixture = new MoverFixture();
            fixture.Mover.AutoFocus = true;
            Action block = () => { entered.Set(); Assert.IsTrue(release.Wait(Guard)); };
            if (stage == "find") fixture.OnFind = block;
            else fixture.OnFactory = block;
            var press = Task.Run(() => fixture.Send(true));
            bool pending = false;
            try
            {
                Assert.IsTrue(entered.Wait(Guard));
                fixture.Mover.Dispose();
                pending = !fixture.Mover.Completion.IsCompleted;
            }
            finally
            {
                release.Set();
                await press.WaitAsync(Guard);
            }
            Assert.AreEqual(0, fixture.CursorSubscriptions);
            Assert.AreEqual(0, fixture.StateReads);
            Assert.AreEqual(0, fixture.FocusCalls);
            Assert.AreEqual(0, fixture.Notifications.Count);
            Assert.AreEqual(stage == "factory" ? 1 : 0, fixture.Factories);
            Assert.IsTrue(pending, "The entered hook callback still owns its window lookup or drag factory until it returns.");
            await fixture.Mover.Completion.WaitAsync(Guard);
            foreach (var drag in fixture.Drags) await drag.Completion.WaitAsync(Guard);
        }

        [TestMethod]
        public async Task DisposeDuringMoveModifierReadPreventsLateLookupAndFactory()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var fixture = new MoverFixture();
            fixture.OnMoveModifier = () => { entered.Set(); Assert.IsTrue(release.Wait(Guard)); };
            var captured = fixture.CaptureHook();
            var press = Task.Run(() => Send(captured, true));
            bool pending = false;
            try
            {
                Assert.IsTrue(entered.Wait(Guard));
                fixture.Mover.Dispose();
                pending = !fixture.Mover.Completion.IsCompleted;
            }
            finally
            {
                release.Set();
                await press.WaitAsync(Guard);
            }
            Assert.IsTrue(pending);
            Assert.IsFalse(await press);
            Assert.IsFalse(Send(captured, true));
            await fixture.Mover.Completion.WaitAsync(Guard);
            Assert.AreEqual(0, fixture.Finds);
            Assert.AreEqual(0, fixture.Factories);
            Assert.AreEqual(0, fixture.HookSubscriptions);
            Assert.AreEqual(0, fixture.CursorSubscriptions);
            Assert.AreEqual(0, fixture.Notifications.Count);
        }

        [TestMethod]
        public async Task DisposeDrainsEnteredCursorWriteAndRejectsCapturedSuccessor()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var fixture = new MoverFixture();
            Assert.IsTrue(fixture.Send(true));
            var captured = fixture.CaptureCursor();
            fixture.OnPosition = () => { entered.Set(); Assert.IsTrue(release.Wait(Guard)); };
            var move = Task.Run(() => captured(new Point(210, 330)));
            bool pending = false;
            try
            {
                Assert.IsTrue(entered.Wait(Guard));
                fixture.Mover.Dispose();
                pending = !fixture.Mover.Completion.IsCompleted;
            }
            finally
            {
                release.Set();
                await move.WaitAsync(Guard);
            }
            fixture.OnPosition = null;
            captured(new Point(800, 900));
            Assert.AreEqual(0, fixture.CursorSubscriptions);
            Assert.IsTrue(pending, "Completion must wait for the already-entered cursor write.");
            await fixture.Mover.Completion.WaitAsync(Guard);
            await fixture.Drags.Single().Completion.WaitAsync(Guard);
            CollectionAssert.AreEqual(new[] { Rectangle.OffsetAndSize(200, 310, 100, 80) }, fixture.Writes);
            CollectionAssert.AreEqual(new[] { "start", "end" }, fixture.Notifications);
        }

        [TestMethod]
        public async Task NextDragStartWaitsForPriorEnteredCursorWriteAndEnd()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var fixture = new MoverFixture();
            var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int starts = 0;
            fixture.OnStarted = () =>
            {
                if (Interlocked.Increment(ref starts) == 2) { secondStarted.TrySetResult(); }
            };

            Assert.IsTrue(fixture.Send(true));
            var first = fixture.Drags.Single();
            var captured = fixture.CaptureCursor();
            int positionEntries = 0;
            fixture.OnPosition = () =>
            {
                if (Interlocked.Increment(ref positionEntries) == 1)
                {
                    entered.Set();
                    Assert.IsTrue(release.Wait(Guard));
                }
            };
            fixture.OnPositionApplied = () =>
            {
                if (fixture.Writes.Count == 2) { secondWrite.TrySetResult(); }
            };
            var move = Task.Run(() => captured(new Point(210, 330)));
            try
            {
                Assert.IsTrue(entered.Wait(Guard));
                Assert.IsTrue(fixture.Send(false));
                Assert.IsTrue(fixture.Send(true));
                Assert.AreEqual(2, fixture.Factories);
                fixture.RaiseCursor(new Point(300, 400));
                fixture.RaiseCursor(new Point(400, 500));
                CollectionAssert.AreEqual(new[] { "start" }, fixture.Notifications,
                    "The next drag start must wait for the prior accepted write and terminal notification.");
                Assert.AreEqual(0, fixture.Writes.Count,
                    "The next drag position must remain buffered while the prior write is still owned.");
            }
            finally
            {
                release.Set();
                await move.WaitAsync(Guard);
            }

            fixture.OnPosition = null;
            await first.Completion.WaitAsync(Guard);
            await secondStarted.Task.WaitAsync(Guard);
            await secondWrite.Task.WaitAsync(Guard);
            CollectionAssert.AreEqual(new[] { "start", "end", "start" }, fixture.Notifications);
            CollectionAssert.AreEqual(new[]
            {
                Rectangle.OffsetAndSize(200, 310, 100, 80),
                Rectangle.OffsetAndSize(390, 480, 100, 80),
            }, fixture.Writes);
            Assert.IsTrue(fixture.Send(false));
            fixture.Mover.Dispose();
            await fixture.Mover.Completion.WaitAsync(Guard);
            foreach (var drag in fixture.Drags) { await drag.Completion.WaitAsync(Guard); }
            CollectionAssert.AreEqual(new[] { "start", "end", "start", "end" }, fixture.Notifications);
        }

        [TestMethod]
        public async Task RepeatedGesturesWhilePriorWriteIsBlockedKeepOneBoundedSuccessor()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var fixture = new MoverFixture();
            Assert.IsTrue(fixture.Send(true));
            var captured = fixture.CaptureCursor();
            fixture.OnPosition = () => { entered.Set(); Assert.IsTrue(release.Wait(Guard)); };
            var move = Task.Run(() => captured(new Point(210, 330)));
            bool completionWasPending = false;
            try
            {
                Assert.IsTrue(entered.Wait(Guard));
                Assert.IsTrue(fixture.Send(false));
                Assert.IsTrue(fixture.Send(true), "One successor remains admissible while the first drag drains.");
                Assert.IsTrue(fixture.Send(false));
                for (int attempt = 0; attempt < 100; attempt++)
                {
                    Assert.IsFalse(fixture.Send(true));
                    Assert.IsFalse(fixture.Send(false));
                }
                fixture.Mover.Dispose();
                completionWasPending = !fixture.Mover.Completion.IsCompleted;
            }
            finally
            {
                release.Set();
                await move.WaitAsync(Guard);
            }

            await fixture.Mover.Completion.WaitAsync(Guard);
            foreach (var drag in fixture.Drags) { await drag.Completion.WaitAsync(Guard); }
            Assert.IsTrue(completionWasPending);
            Assert.AreEqual(2, fixture.Factories);
            Assert.AreEqual(2, fixture.Drags.Count);
            Assert.AreEqual(0, fixture.HookSubscriptions);
            Assert.AreEqual(0, fixture.CursorSubscriptions);
            CollectionAssert.AreEqual(new[] { "start", "end" }, fixture.Notifications);
        }

        [TestMethod]
        public async Task DisposeBeforeQueuedStartSuppressesNotificationsAndDrainsDispatch()
        {
            using var fixture = new MoverFixture { QueuedDispatch = true };
            Assert.IsTrue(fixture.Send(true));
            fixture.Mover.Dispose();
            bool pending = !fixture.Mover.Completion.IsCompleted;
            fixture.Pump();
            Assert.AreEqual(0, fixture.CursorSubscriptions);
            Assert.AreEqual(0, fixture.Notifications.Count);
            Assert.IsTrue(pending, "The accepted dispatcher operation must drain before mover completion.");
            await fixture.Mover.Completion.WaitAsync(Guard);
            await fixture.Drags.Single().Completion.WaitAsync(Guard);
        }

        [TestMethod]
        public void IneligibleInputNeverFindsAWindowOrCreatesADrag()
        {
            using var fixture = new MoverFixture();
            fixture.Mover.IsEnabled = false;
            Assert.IsFalse(fixture.Send(true));
            fixture.Mover.IsEnabled = true;
            Assert.IsFalse(fixture.Send(true, LowLevelMouseHook.MouseButton.Right));
            Assert.IsFalse(fixture.Send(true, LowLevelMouseHook.MouseButton.Middle));
            Assert.IsFalse(fixture.Send(false));
            fixture.MoveModifier = false;
            Assert.IsFalse(fixture.Send(true));
            Assert.AreEqual(0, fixture.Finds);
            Assert.AreEqual(0, fixture.Factories);
            Assert.AreEqual(0, fixture.CursorSubscriptions);
        }

        [TestMethod]
        public async Task ModifierWindowMoverLifetimeCounterScenario()
        {
            const int cycles = 100;
            int lateSubscriptions = 0, lateWrites = 0, pendingCompletions = 0;
            int normalWrites = 0, normalStarts = 0, normalEnds = 0;
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                using (var late = new MoverFixture())
                {
                    late.Mover.AutoFocus = true;
                    late.OnFocus = () =>
                    {
                        late.Mover.Dispose();
                        if (!late.Mover.Completion.IsCompleted) { pendingCompletions++; }
                    };
                    Assert.IsTrue(late.Send(true));
                    lateSubscriptions += late.CursorSubscriptions;
                    late.RaiseCursor(new Point(800, 900));
                    lateWrites += late.Writes.Count;
                    foreach (var drag in late.Drags) { drag.End(); }
                    late.Pump();
                    await late.Mover.Completion.WaitAsync(Guard);
                    foreach (var drag in late.Drags) { await drag.Completion.WaitAsync(Guard); }
                    Assert.AreEqual(0, late.HookSubscriptions);
                    Assert.AreEqual(0, late.CursorSubscriptions);
                }

                using (var normal = new MoverFixture())
                {
                    Assert.IsTrue(normal.Send(true));
                    normal.RaiseCursor(new Point(210, 330));
                    Assert.IsTrue(normal.Send(false));
                    normal.Mover.Dispose();
                    normal.Pump();
                    await normal.Mover.Completion.WaitAsync(Guard);
                    foreach (var drag in normal.Drags) { await drag.Completion.WaitAsync(Guard); }
                    Assert.AreEqual(Rectangle.OffsetAndSize(200, 310, 100, 80), normal.Writes.Single());
                    CollectionAssert.AreEqual(new[] { "start", "end" }, normal.Notifications);
                    Assert.AreEqual(0, normal.HookSubscriptions);
                    Assert.AreEqual(0, normal.CursorSubscriptions);
                    normalWrites += normal.Writes.Count;
                    normalStarts += normal.Notifications.Count(value => value == "start");
                    normalEnds += normal.Notifications.Count(value => value == "end");
                }
            }
            Console.WriteLine($"PERFCOUNTER modifier-drag cycles {cycles}");
            Console.WriteLine($"PERFCOUNTER modifier-drag late-subscriptions {lateSubscriptions}");
            Console.WriteLine($"PERFCOUNTER modifier-drag late-writes {lateWrites}");
            Console.WriteLine($"PERFCOUNTER modifier-drag pending-completions {pendingCompletions}");
            Console.WriteLine($"PERFCOUNTER modifier-drag normal-writes {normalWrites}");
            Console.WriteLine($"PERFCOUNTER modifier-drag normal-starts {normalStarts}");
            Console.WriteLine($"PERFCOUNTER modifier-drag normal-ends {normalEnds}");
        }

        private static bool Send(LowLevelMouseHook.ButtonStateChangedEventHandler handler, bool pressed,
            LowLevelMouseHook.MouseButton button = LowLevelMouseHook.MouseButton.Left)
        {
            var args = new LowLevelMouseHook.ButtonStateChangedEventArgs(button, pressed, 110, 220);
            handler(null, ref args);
            return args.Handled;
        }

        private sealed class MoverFixture : IDisposable
        {
            public Mock<IWindow> Window { get; } = new();
            public Mock<IWorkspace> Workspace { get; } = new();
            public ModifierWindowMover Mover { get; }
            public List<WindowDragger> Drags { get; } = [];
            public List<Rectangle> Writes { get; } = [];
            public List<string> Notifications { get; } = [];
            public bool MoveModifier = true, ActivateModifier, QueuedDispatch;
            public int HookAdds, HookRemoves, Finds, Factories, StateReads, FocusCalls;
            public Point LastFindPoint;
            public Action? OnFind, OnFactory, OnFocus, OnPosition, OnPositionApplied, OnMoveModifier, OnStarted;
            private readonly object m_gate = new();
            private LowLevelMouseHook.ButtonStateChangedEventHandler? m_hookHandlers;
            private EventHandler<CursorLocationChangedEventArgs>? m_cursorHandlers;
            private readonly Queue<(Action Action, TaskCompletionSource Completion)> m_dispatches = new();

            public int HookSubscriptions { get { lock (m_gate) return m_hookHandlers?.GetInvocationList().Length ?? 0; } }
            public int CursorSubscriptions { get { lock (m_gate) return m_cursorHandlers?.GetInvocationList().Length ?? 0; } }

            public MoverFixture()
            {
                Window.SetupGet(value => value.Workspace).Returns(Workspace.Object);
                Window.SetupGet(value => value.Position).Returns(Rectangle.OffsetAndSize(100, 200, 100, 80));
                Window.SetupGet(value => value.State).Returns(() => { StateReads++; return WindowState.Restored; });
                Window.Setup(value => value.SetPosition(It.IsAny<Rectangle>())).Callback<Rectangle>(rectangle =>
                { OnPosition?.Invoke(); Writes.Add(rectangle); OnPositionApplied?.Invoke(); });
                Workspace.SetupGet(value => value.CursorLocation).Returns(new Point(110, 220));
                Workspace.Setup(value => value.FindWindowFromPoint(It.IsAny<Point>())).Returns<Point>(point =>
                { Finds++; LastFindPoint = point; OnFind?.Invoke(); return Window.Object; });
                Workspace.SetupAdd(value => value.CursorLocationChanged += It.IsAny<EventHandler<CursorLocationChangedEventArgs>>())
                    .Callback<EventHandler<CursorLocationChangedEventArgs>>(handler => { lock (m_gate) m_cursorHandlers += handler; });
                Workspace.SetupRemove(value => value.CursorLocationChanged -= It.IsAny<EventHandler<CursorLocationChangedEventArgs>>())
                    .Callback<EventHandler<CursorLocationChangedEventArgs>>(handler => { lock (m_gate) m_cursorHandlers -= handler; });
                Mover = new ModifierWindowMover(Workspace.Object,
                    handler => { lock (m_gate) { HookAdds++; m_hookHandlers += handler; } },
                    handler => { lock (m_gate) { HookRemoves++; m_hookHandlers -= handler; } },
                    window =>
                    {
                        Factories++;
                        OnFactory?.Invoke();
                        var drag = new WindowDragger(window,
                            () => { FocusCalls++; OnFocus?.Invoke(); },
                            () => { Notifications.Add("start"); OnStarted?.Invoke(); },
                            () => Notifications.Add("end"), Dispatch);
                        Drags.Add(drag);
                        return drag;
                    }, () => { OnMoveModifier?.Invoke(); return MoveModifier; }, () => ActivateModifier, Mock.Of<ILogger>())
                { IsEnabled = true };
            }

            public LowLevelMouseHook.ButtonStateChangedEventHandler CaptureHook()
            {
                lock (m_gate) return m_hookHandlers ?? throw new InvalidOperationException("No mouse hook is subscribed.");
            }

            public bool Send(bool pressed, LowLevelMouseHook.MouseButton button = LowLevelMouseHook.MouseButton.Left)
                => ModifierWindowMoverTest.Send(CaptureHook(), pressed, button);

            public Action<Point> CaptureCursor()
            {
                EventHandler<CursorLocationChangedEventArgs>? handlers;
                lock (m_gate) handlers = m_cursorHandlers;
                return point => handlers?.Invoke(Workspace.Object,
                    new CursorLocationChangedEventArgs(Workspace.Object, point, new Point(110, 220)));
            }

            public void RaiseCursor(Point point) => CaptureCursor()(point);

            private Task Dispatch(Action action)
            {
                if (!QueuedDispatch)
                {
                    try { action(); return Task.CompletedTask; }
                    catch (Exception exception) { return Task.FromException(exception); }
                }
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (m_gate) m_dispatches.Enqueue((action, completion));
                return completion.Task;
            }

            public void Pump()
            {
                while (true)
                {
                    (Action Action, TaskCompletionSource Completion) next;
                    lock (m_gate)
                    {
                        if (!m_dispatches.TryDequeue(out next)) return;
                    }
                    try { next.Action(); next.Completion.SetResult(); }
                    catch (Exception exception) { next.Completion.SetException(exception); throw; }
                }
            }

            public void Dispose()
            {
                Mover.Dispose();
                foreach (var drag in Drags) drag.End();
                Pump();
            }
        }
    }
}
