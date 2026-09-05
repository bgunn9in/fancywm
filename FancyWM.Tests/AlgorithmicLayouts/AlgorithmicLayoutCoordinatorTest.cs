#nullable enable

using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

using FancyWM.AlgorithmicLayouts;
using FancyWM.Tests.TestUtilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    [TestClass]
    public class AlgorithmicLayoutCoordinatorTest
    {
        [TestMethod]
        public void CoordinatorOwnsOneRuntimeRegistryAcrossRegisteredDisplays()
        {
            using var coordinator = CreateCoordinator();
            var display1 = CreateDisplay();
            var display2 = CreateDisplay();
            var desktop = CreateDesktop("Desktop 1");
            var participant1 = new object();
            var participant2 = new object();
            using var registration1 = coordinator.RegisterDisplay(display1, participant1);
            using var registration2 = coordinator.RegisterDisplay(display2, participant2);
            var state1 = new MasterSatelliteRuntimeState(new(), true);
            var state2 = new MasterSatelliteRuntimeState(new(), true);

            Assert.IsTrue(coordinator.TryAdd(new LayoutStateKey(desktop, display1), state1));
            Assert.IsTrue(coordinator.TryAdd(new LayoutStateKey(desktop, display2), state2));
            Assert.AreEqual(2, coordinator.Count);
            Assert.AreEqual(2, coordinator.RegisteredDisplayCount);
            Assert.AreSame(state1, coordinator.SnapshotForDisplay(display1).Single().State);
            Assert.AreSame(state2, coordinator.SnapshotForDisplay(display2).Single().State);

            registration1.Dispose();

            Assert.AreEqual(1, coordinator.Count);
            Assert.AreEqual(1, coordinator.RegisteredDisplayCount);
            Assert.IsFalse(coordinator.TryGet(new LayoutStateKey(desktop, display1), out _));
            Assert.IsTrue(coordinator.TryGet(new LayoutStateKey(desktop, display2), out var retained));
            Assert.AreSame(state2, retained);
        }

        [TestMethod]
        public void ReservationsConsumeCapacityAndUseUniqueExactSlots()
        {
            using var coordinator = CreateCoordinator();
            var display = CreateDisplay();
            var source = CreateDesktop("Source");
            var target = CreateDesktop("Target");
            using var registration = coordinator.RegisterDisplay(display, new object());
            var key = new LayoutStateKey(target, display);
            coordinator.PublishCapacity(key, EmptyCapacity(totalCapacity: 4), 1);

            Assert.IsTrue(Reserve(coordinator, source, display, key, 101, out var first));
            Assert.AreEqual(ReservedRole.Master, first.TargetRole);
            Assert.IsNull(first.TargetSatelliteIndex);
            Assert.IsTrue(Reserve(coordinator, source, display, key, 102, out var second));
            Assert.AreEqual(ReservedRole.Satellite, second.TargetRole);
            Assert.AreEqual(0, second.TargetSatelliteIndex);
            Assert.IsTrue(Reserve(coordinator, source, display, key, 103, out var third));
            Assert.AreEqual(1, third.TargetSatelliteIndex);
            Assert.IsTrue(Reserve(coordinator, source, display, key, 104, out var fourth));
            Assert.AreEqual(2, fourth.TargetSatelliteIndex);
            Assert.IsFalse(Reserve(coordinator, source, display, key, 105, out _));

            Assert.AreEqual(4, coordinator.ReservationCount);
            Assert.IsTrue(coordinator.TryGetCapacity(key, out var capacity));
            Assert.IsFalse(capacity.CanAcceptWindow);
            Assert.AreEqual(4, capacity.ReservedSlots);
            CollectionAssert.AreEquivalent(
                new[] { "Master:", "Satellite:0", "Satellite:1", "Satellite:2" },
                coordinator.SnapshotReservations()
                    .Select(item => $"{item.Role}:{item.SatelliteIndex}")
                    .ToArray());
        }

        [TestMethod]
        public void ReservationReadinessAdvancesOnlyAfterPrecedingPhysicalCommit()
        {
            using var coordinator = CreateCoordinator();
            var display = CreateDisplay();
            var source = CreateDesktop("Source");
            var target = CreateDesktop("Target");
            using var registration = coordinator.RegisterDisplay(display, new object());
            var key = new LayoutStateKey(target, display);
            coordinator.PublishCapacity(key, EmptyCapacity(totalCapacity: 3), 1);

            Assert.IsTrue(Reserve(coordinator, source, display, key, 111, out var master));
            Assert.IsTrue(Reserve(coordinator, source, display, key, 112, out var satellite0));
            Assert.IsTrue(Reserve(coordinator, source, display, key, 113, out var satellite1));

            AssertReadiness(
                coordinator,
                master,
                AlgorithmicReservationReadinessDisposition.Ready);
            AssertReadiness(
                coordinator,
                satellite0,
                AlgorithmicReservationReadinessDisposition.AwaitingPrecedingSlot);
            AssertReadiness(
                coordinator,
                satellite1,
                AlgorithmicReservationReadinessDisposition.AwaitingPrecedingSlot);

            PublishPhysicalCapacity(coordinator, key, occupiedSlots: 1, sequence: 2);
            Commit(coordinator, master);

            AssertReadiness(
                coordinator,
                satellite0,
                AlgorithmicReservationReadinessDisposition.Ready);
            AssertReadiness(
                coordinator,
                satellite1,
                AlgorithmicReservationReadinessDisposition.AwaitingPrecedingSlot);

            PublishPhysicalCapacity(coordinator, key, occupiedSlots: 2, sequence: 3);
            Commit(coordinator, satellite0);

            AssertReadiness(
                coordinator,
                satellite1,
                AlgorithmicReservationReadinessDisposition.Ready);

            PublishPhysicalCapacity(coordinator, key, occupiedSlots: 3, sequence: 4);
            Commit(coordinator, satellite1);

            Assert.AreEqual(0, coordinator.ReservationCount);
            Assert.AreEqual(0, coordinator.ActiveTransferCount);
        }

        [TestMethod]
        public void ConcurrentPlansRecomputeTheirExactSlotWhenReserved()
        {
            using var coordinator = CreateCoordinator();
            var display = CreateDisplay();
            var source = CreateDesktop("Source");
            var target = CreateDesktop("Target");
            using var registration = coordinator.RegisterDisplay(display, new object());
            var key = new LayoutStateKey(target, display);
            coordinator.PublishCapacity(key, EmptyCapacity(totalCapacity: 2), 1);
            var firstId = Guid.NewGuid();
            var secondId = Guid.NewGuid();

            Assert.IsTrue(coordinator.TryPlan(
                firstId, new IntPtr(201), source, display, key, out var first));
            Assert.IsTrue(coordinator.TryPlan(
                secondId, new IntPtr(202), source, display, key, out var second));
            Assert.AreEqual(PendingWindowTransferState.Planned, first.State);
            Assert.AreEqual(PendingWindowTransferState.Planned, second.State);

            Assert.IsTrue(coordinator.TryReserve(firstId, new IntPtr(201), out first));
            Assert.AreEqual(PendingWindowTransferState.Reserved, first.State);
            Assert.AreEqual(ReservedRole.Master, first.TargetRole);
            Assert.IsTrue(coordinator.TryReserve(secondId, new IntPtr(202), out second));
            Assert.AreEqual(PendingWindowTransferState.Reserved, second.State);
            Assert.AreEqual(ReservedRole.Satellite, second.TargetRole);
            Assert.AreEqual(0, second.TargetSatelliteIndex);
        }

        [TestMethod]
        public void CorrelatedStateMachineIsIdempotentAndCommitReleasesReservation()
        {
            using var coordinator = CreateCoordinator();
            var display = CreateDisplay();
            var source = CreateDesktop("Source");
            var target = CreateDesktop("Target");
            using var registration = coordinator.RegisterDisplay(display, new object());
            var key = new LayoutStateKey(target, display);
            coordinator.PublishCapacity(key, EmptyCapacity(totalCapacity: 2), 1);
            var correlationId = Guid.NewGuid();

            Assert.IsTrue(coordinator.TryPlanAndReserve(
                correlationId,
                new IntPtr(301),
                source,
                display,
                key,
                out var transfer));
            var handle = new IntPtr(301);
            Assert.IsTrue(coordinator.MarkMoving(correlationId, handle));
            Assert.IsTrue(coordinator.MarkMoving(correlationId, handle));
            Assert.IsTrue(coordinator.ObserveDestination(correlationId, handle));
            Assert.IsTrue(coordinator.ObserveDestination(correlationId, handle));
            Assert.IsTrue(coordinator.Commit(correlationId, handle));
            Assert.IsTrue(coordinator.Commit(correlationId, handle));
            Assert.IsTrue(coordinator.TryGetCapacity(key, out var shadowed));
            Assert.AreEqual(1, shadowed.ReservedSlots);
            Assert.AreEqual(ReservedRole.Satellite, shadowed.NextRole);
            Assert.AreEqual(0, shadowed.NextSatelliteIndex);

            Assert.IsTrue(coordinator.PublishCapacity(key, new MasterSatelliteCapacitySnapshot(
                WorkspaceLayoutKind.Canonical,
                true,
                MasterSatelliteWindowRole.Satellite,
                0,
                1,
                2,
                1,
                null), 2));
            Assert.IsTrue(coordinator.TryGetCapacity(key, out var acknowledged));
            Assert.AreEqual(0, acknowledged.ReservedSlots);
            Assert.IsTrue(coordinator.ObserveSourceRemoved(
                handle,
                source,
                display,
                out var lateSource));
            Assert.IsTrue(lateSource.SourceRemovedObserved);

            Assert.AreEqual(0, coordinator.ReservationCount);
            Assert.AreEqual(0, coordinator.ActiveTransferCount);
            Assert.IsTrue(coordinator.TryGetTransfer(correlationId, out transfer));
            Assert.AreEqual(PendingWindowTransferState.Committed, transfer.State);
            Assert.IsFalse(coordinator.Cancel(correlationId, handle, "LateCancel"));
        }

        [TestMethod]
        public void ActiveAndRecentlyCompletedWindowCannotBeReusedUntilQuarantineExpires()
        {
            var time = new ManualTimeProvider(
                new DateTimeOffset(2026, 4, 9, 12, 0, 0, TimeSpan.Zero));
            using var coordinator = CreateCoordinator(time, TimeSpan.FromSeconds(10));
            var display = CreateDisplay();
            var source = CreateDesktop("Source");
            var target = CreateDesktop("Target");
            using var registration = coordinator.RegisterDisplay(display, new object());
            var key = new LayoutStateKey(target, display);
            coordinator.PublishCapacity(key, EmptyCapacity(totalCapacity: 3), 1);
            var firstCorrelation = Guid.NewGuid();

            Assert.IsTrue(coordinator.TryPlanAndReserve(
                firstCorrelation,
                new IntPtr(401),
                source,
                display,
                key,
                out _));
            Assert.IsFalse(coordinator.TryPlanAndReserve(
                Guid.NewGuid(),
                new IntPtr(401),
                source,
                display,
                key,
                out _));
            Assert.IsFalse(coordinator.TryPlanAndReserve(
                firstCorrelation,
                new IntPtr(402),
                source,
                display,
                key,
                out _));

            Assert.IsTrue(coordinator.Fail(
                firstCorrelation,
                new IntPtr(401),
                "MoveWindowFailed"));
            Assert.AreEqual(0, coordinator.ReservationCount);
            Assert.IsFalse(coordinator.TryPlanAndReserve(
                Guid.NewGuid(),
                new IntPtr(401),
                source,
                display,
                key,
                out _));

            time.Advance(TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(1)));
            coordinator.CleanupExpired();

            Assert.IsTrue(coordinator.TryPlanAndReserve(
                Guid.NewGuid(),
                new IntPtr(401),
                source,
                display,
                key,
                out var replacement));
            Assert.AreNotEqual(firstCorrelation, replacement.CorrelationId);
        }

        [TestMethod]
        public void WindowClosedEndsHandleQuarantineForNewGeneration()
        {
            using var coordinator = CreateCoordinator();
            var display = CreateDisplay();
            var source = CreateDesktop("Source");
            var target = CreateDesktop("Target");
            using var registration = coordinator.RegisterDisplay(display, new object());
            var key = new LayoutStateKey(target, display);
            coordinator.PublishCapacity(key, EmptyCapacity(totalCapacity: 3), 1);
            var handle = new IntPtr(425);
            var firstCorrelation = Guid.NewGuid();

            Assert.IsTrue(coordinator.TryPlanAndReserve(
                firstCorrelation,
                handle,
                source,
                display,
                key,
                out _));
            Assert.IsTrue(coordinator.Fail(
                firstCorrelation,
                handle,
                "MoveWindowFailed"));
            Assert.IsTrue(coordinator.TryGetRecentTransfer(handle, out _));

            Assert.IsTrue(coordinator.WindowClosed(handle));
            Assert.IsFalse(coordinator.TryGetRecentTransfer(handle, out _));
            Assert.IsTrue(coordinator.TryPlanAndReserve(
                Guid.NewGuid(),
                handle,
                source,
                display,
                key,
                out var replacement));
            Assert.AreEqual(handle, replacement.WindowHandle);
            Assert.AreNotEqual(firstCorrelation, replacement.CorrelationId);
        }

        [TestMethod]
        public void CapacityChangesCancelOnlyReservationsThatAreNoLongerValid()
        {
            using var coordinator = CreateCoordinator();
            var display = CreateDisplay();
            var source = CreateDesktop("Source");
            var target = CreateDesktop("Target");
            using var registration = coordinator.RegisterDisplay(display, new object());
            var key = new LayoutStateKey(target, display);
            coordinator.PublishCapacity(key, EmptyCapacity(totalCapacity: 4), 1);
            Assert.IsTrue(Reserve(coordinator, source, display, key, 451, out var first));
            Assert.IsTrue(Reserve(coordinator, source, display, key, 452, out var second));
            Assert.IsTrue(Reserve(coordinator, source, display, key, 453, out var third));

            Assert.IsTrue(coordinator.PublishCapacity(
                key,
                EmptyCapacity(totalCapacity: 2),
                2));

            Assert.AreEqual(2, coordinator.ReservationCount);
            Assert.IsTrue(coordinator.TryGetTransfer(first.CorrelationId, out first));
            Assert.IsTrue(coordinator.TryGetTransfer(second.CorrelationId, out second));
            Assert.IsTrue(coordinator.TryGetTransfer(third.CorrelationId, out third));
            Assert.AreEqual(PendingWindowTransferState.Reserved, first.State);
            Assert.AreEqual(PendingWindowTransferState.Reserved, second.State);
            Assert.AreEqual(PendingWindowTransferState.Cancelled, third.State);
            Assert.AreEqual("DestinationCapacityChanged", third.TerminalReason);

            Assert.IsTrue(coordinator.PublishCapacity(key, new MasterSatelliteCapacitySnapshot(
                WorkspaceLayoutKind.Manual,
                false,
                null,
                null,
                0,
                2,
                0,
                "Manual layout"), 3));

            Assert.AreEqual(0, coordinator.ReservationCount);
            Assert.IsTrue(coordinator.TryGetTransfer(first.CorrelationId, out first));
            Assert.IsTrue(coordinator.TryGetTransfer(second.CorrelationId, out second));
            Assert.AreEqual(PendingWindowTransferState.Cancelled, first.State);
            Assert.AreEqual(PendingWindowTransferState.Cancelled, second.State);
        }

        [TestMethod]
        public void TimeoutFailsTransferAndReleasesReservationDeterministically()
        {
            var time = new ManualTimeProvider(new DateTimeOffset(2026, 4, 9, 12, 0, 0, TimeSpan.Zero));
            using var coordinator = CreateCoordinator(time, TimeSpan.FromSeconds(10));
            var display = CreateDisplay();
            var source = CreateDesktop("Source");
            var target = CreateDesktop("Target");
            using var registration = coordinator.RegisterDisplay(display, new object());
            var key = new LayoutStateKey(target, display);
            coordinator.PublishCapacity(key, EmptyCapacity(totalCapacity: 2), 1);
            var correlationId = Guid.NewGuid();
            Assert.IsTrue(coordinator.TryPlanAndReserve(
                correlationId,
                new IntPtr(501),
                source,
                display,
                key,
                out _));

            time.Advance(TimeSpan.FromSeconds(11));

            Assert.AreEqual(1, coordinator.CleanupExpired());
            Assert.AreEqual(0, coordinator.ReservationCount);
            Assert.AreEqual(0, coordinator.ActiveTransferCount);
            Assert.IsTrue(coordinator.TryGetTransfer(correlationId, out var transfer));
            Assert.AreEqual(PendingWindowTransferState.Failed, transfer.State);
            Assert.AreEqual("TransferTimedOut", transfer.TerminalReason);
        }

        [TestMethod]
        public void ExpiredTransferCannotMoveObserveOrCommitBeforeTimerTick()
        {
            var time = new ManualTimeProvider(new DateTimeOffset(2026, 4, 9, 12, 0, 0, TimeSpan.Zero));
            using var coordinator = CreateCoordinator(time, TimeSpan.FromSeconds(5));
            var display = CreateDisplay();
            var source = CreateDesktop("Source");
            var target = CreateDesktop("Target");
            using var registration = coordinator.RegisterDisplay(display, new object());
            var key = new LayoutStateKey(target, display);
            coordinator.PublishCapacity(key, EmptyCapacity(totalCapacity: 2), 1);
            var correlationId = Guid.NewGuid();
            var handle = new IntPtr(551);
            Assert.IsTrue(coordinator.TryPlanAndReserve(
                correlationId, handle, source, display, key, out _));

            time.Advance(TimeSpan.FromSeconds(6));

            Assert.IsFalse(coordinator.MarkMoving(correlationId, handle));
            Assert.IsFalse(coordinator.ObserveDestination(correlationId, handle));
            Assert.IsFalse(coordinator.Commit(correlationId, handle));
            Assert.IsTrue(coordinator.TryGetTransfer(correlationId, out var expired));
            Assert.AreEqual(PendingWindowTransferState.Failed, expired.State);
            Assert.AreEqual(0, coordinator.ReservationCount);
        }

        [TestMethod]
        public void ReleasingEarlierReservationCancelsDependentExactSlots()
        {
            using var coordinator = CreateCoordinator();
            var display = CreateDisplay();
            var source = CreateDesktop("Source");
            var target = CreateDesktop("Target");
            using var registration = coordinator.RegisterDisplay(display, new object());
            var key = new LayoutStateKey(target, display);
            coordinator.PublishCapacity(key, EmptyCapacity(totalCapacity: 4), 1);
            Assert.IsTrue(Reserve(coordinator, source, display, key, 561, out var first));
            Assert.IsTrue(Reserve(coordinator, source, display, key, 562, out var second));
            Assert.IsTrue(Reserve(coordinator, source, display, key, 563, out var third));

            Assert.IsTrue(coordinator.Fail(
                first.CorrelationId,
                first.WindowHandle,
                "MasterMoveFailed"));

            Assert.AreEqual(0, coordinator.ReservationCount);
            Assert.IsTrue(coordinator.TryGetTransfer(second.CorrelationId, out second));
            Assert.IsTrue(coordinator.TryGetTransfer(third.CorrelationId, out third));
            Assert.AreEqual(PendingWindowTransferState.Cancelled, second.State);
            Assert.AreEqual(PendingWindowTransferState.Cancelled, third.State);
            Assert.AreEqual("PrecedingReservationReleased", second.TerminalReason);
            Assert.AreEqual("PrecedingReservationReleased", third.TerminalReason);
        }

        [TestMethod]
        public void MaterializedPhysicalSlotDoesNotDoubleCountOutstandingReservation()
        {
            using var coordinator = CreateCoordinator();
            var display = CreateDisplay();
            var source = CreateDesktop("Source");
            var target = CreateDesktop("Target");
            using var registration = coordinator.RegisterDisplay(display, new object());
            var key = new LayoutStateKey(target, display);
            coordinator.PublishCapacity(key, EmptyCapacity(totalCapacity: 3), 1);
            Assert.IsTrue(Reserve(coordinator, source, display, key, 571, out _));

            Assert.IsTrue(coordinator.PublishCapacity(key, new MasterSatelliteCapacitySnapshot(
                WorkspaceLayoutKind.Canonical,
                true,
                MasterSatelliteWindowRole.Satellite,
                0,
                1,
                3,
                1,
                null), 2));

            Assert.IsTrue(coordinator.TryGetCapacity(key, out var capacity));
            Assert.AreEqual(1, capacity.OccupiedSlots);
            Assert.AreEqual(0, capacity.ReservedSlots);
            Assert.AreEqual(ReservedRole.Satellite, capacity.NextRole);
            Assert.AreEqual(0, capacity.NextSatelliteIndex);
        }

        [TestMethod]
        public void StaleCapacityPublicationCannotReplaceNewerSnapshotAcrossLayoutKinds()
        {
            using var coordinator = CreateCoordinator();
            var display = CreateDisplay();
            var desktop = CreateDesktop("Target");
            using var registration = coordinator.RegisterDisplay(display, new object());
            var key = new LayoutStateKey(desktop, display);
            Assert.IsTrue(coordinator.PublishCapacity(key, new MasterSatelliteCapacitySnapshot(
                WorkspaceLayoutKind.Manual,
                false,
                null,
                null,
                2,
                4,
                1,
                "Manual layout"), 2));

            Assert.IsFalse(coordinator.PublishCapacity(key, new MasterSatelliteCapacitySnapshot(
                WorkspaceLayoutKind.Canonical,
                true,
                MasterSatelliteWindowRole.Satellite,
                0,
                1,
                4,
                99,
                null), 1));

            Assert.IsTrue(coordinator.TryGetCapacity(key, out var capacity));
            Assert.AreEqual(2, capacity.OccupiedSlots);
            Assert.AreEqual(1L, capacity.Revision);
            Assert.AreEqual(WorkspaceLayoutKind.Manual, capacity.LayoutKind);
            Assert.IsFalse(capacity.CanAcceptWindow);
        }

        [TestMethod]
        public void WindowDesktopAndDisplayRemovalCancelRelatedTransfers()
        {
            using var coordinator = CreateCoordinator();
            var display1 = CreateDisplay();
            var display2 = CreateDisplay();
            var source = CreateDesktop("Source");
            var target = CreateDesktop("Target");
            var participant1 = new object();
            var participant2 = new object();
            using var registration1 = coordinator.RegisterDisplay(display1, participant1);
            using var registration2 = coordinator.RegisterDisplay(display2, participant2);
            var key = new LayoutStateKey(target, display2);
            coordinator.PublishCapacity(key, EmptyCapacity(totalCapacity: 4), 1);

            var closeId = Guid.NewGuid();
            Assert.IsTrue(coordinator.TryPlanAndReserve(
                closeId, new IntPtr(601), source, display2, key, out _));
            Assert.IsTrue(coordinator.WindowClosed(new IntPtr(601)));
            Assert.IsTrue(coordinator.TryGetTransfer(closeId, out var closed));
            Assert.AreEqual(PendingWindowTransferState.Cancelled, closed.State);
            Assert.AreEqual("WindowClosed", closed.TerminalReason);

            var desktopId = Guid.NewGuid();
            Assert.IsTrue(coordinator.TryPlanAndReserve(
                desktopId, new IntPtr(602), source, display2, key, out _));
            Assert.IsTrue(coordinator.DesktopRemoved(target) > 0);
            Assert.IsTrue(coordinator.TryGetTransfer(desktopId, out var desktopRemoved));
            Assert.AreEqual("DesktopRemoved", desktopRemoved.TerminalReason);

            coordinator.PublishCapacity(key, EmptyCapacity(totalCapacity: 4), 2);
            var displayId = Guid.NewGuid();
            Assert.IsTrue(coordinator.TryPlanAndReserve(
                displayId, new IntPtr(603), source, display2, key, out _));
            registration2.Dispose();
            Assert.IsTrue(coordinator.TryGetTransfer(displayId, out var displayRemoved));
            Assert.AreEqual("DisplayUnregistered", displayRemoved.TerminalReason);
            Assert.AreEqual(0, coordinator.ReservationCount);
        }

        [TestMethod]
        public void ManualAndCorruptedLayoutsDoNotOfferCapacity()
        {
            using var coordinator = CreateCoordinator();
            var display = CreateDisplay();
            var desktop = CreateDesktop("Target");
            using var registration = coordinator.RegisterDisplay(display, new object());
            var key = new LayoutStateKey(desktop, display);

            coordinator.PublishCapacity(key, new MasterSatelliteCapacitySnapshot(
                WorkspaceLayoutKind.Manual,
                false,
                null,
                null,
                2,
                4,
                0,
                "Manual layout"), 1);
            Assert.IsTrue(coordinator.TryGetCapacity(key, out var manual));
            Assert.IsFalse(manual.CanAcceptWindow);

            coordinator.PublishCapacity(key, new MasterSatelliteCapacitySnapshot(
                WorkspaceLayoutKind.CorruptedCanonical,
                false,
                null,
                null,
                2,
                4,
                3,
                "Broken invariant"), 2);
            Assert.IsTrue(coordinator.TryGetCapacity(key, out var corrupted));
            Assert.IsFalse(corrupted.CanAcceptWindow);
            Assert.AreEqual("Broken invariant", corrupted.DiagnosticReason);
        }

        [TestMethod]
        public void CoordinatorRejectsWrongDispatcherAndConflictingDisplayParticipant()
        {
            using var coordinator = CreateCoordinator();
            var display = CreateDisplay();
            var participant = new object();
            using var registration = coordinator.RegisterDisplay(display, participant);
            Assert.ThrowsException<InvalidOperationException>(() =>
                coordinator.RegisterDisplay(display, participant));
            Assert.ThrowsException<InvalidOperationException>(() =>
                coordinator.RegisterDisplay(display, new object()));

            var exception = Task.Run(() =>
            {
                try
                {
                    _ = coordinator.Count;
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            }).GetAwaiter().GetResult();

            Assert.IsNotNull(exception);
            Assert.IsInstanceOfType(exception, typeof(InvalidOperationException));
        }

        [TestMethod]
        public void CoordinatorRejectsForeignWorkspaceAndCrossDisplayTransfers()
        {
            var workspace = new Mock<IWorkspace>(MockBehavior.Loose).Object;
            var foreignWorkspace = new Mock<IWorkspace>(MockBehavior.Loose).Object;
            using var coordinator = new AlgorithmicLayoutCoordinator(
                workspace,
                Dispatcher.CurrentDispatcher);
            var display = CreateDisplay(workspace);
            var otherDisplay = CreateDisplay(workspace);
            var foreignDisplay = CreateDisplay(foreignWorkspace);
            var source = CreateDesktop("Source", workspace);
            var target = CreateDesktop("Target", workspace);
            var foreignTarget = CreateDesktop("Foreign", foreignWorkspace);
            using var registration = coordinator.RegisterDisplay(display, new object());
            using var otherRegistration = coordinator.RegisterDisplay(otherDisplay, new object());
            coordinator.PublishCapacity(
                new LayoutStateKey(target, display),
                EmptyCapacity(totalCapacity: 2),
                1);

            Assert.ThrowsException<ArgumentException>(() =>
                coordinator.RegisterDisplay(foreignDisplay, new object()));
            Assert.ThrowsException<ArgumentException>(() =>
                coordinator.PublishCapacity(
                    new LayoutStateKey(foreignTarget, display),
                    EmptyCapacity(totalCapacity: 2),
                    1));
            Assert.ThrowsException<ArgumentException>(() =>
                coordinator.TryPlanAndReserve(
                    Guid.NewGuid(),
                    new IntPtr(651),
                    source,
                    otherDisplay,
                    new LayoutStateKey(target, display),
                    out _));
        }

        [TestMethod]
        public void SourceRemovalMustMatchTheCorrelatedSourceLayout()
        {
            using var coordinator = CreateCoordinator();
            var display = CreateDisplay();
            var source = CreateDesktop("Source");
            var unrelatedSource = CreateDesktop("Other source");
            var target = CreateDesktop("Target");
            using var registration = coordinator.RegisterDisplay(display, new object());
            var key = new LayoutStateKey(target, display);
            coordinator.PublishCapacity(key, EmptyCapacity(totalCapacity: 2), 1);
            Assert.IsTrue(Reserve(coordinator, source, display, key, 661, out var transfer));

            Assert.IsFalse(coordinator.ObserveSourceRemoved(
                transfer.WindowHandle,
                unrelatedSource,
                display,
                out _));
            Assert.IsTrue(coordinator.ObserveSourceRemoved(
                transfer.WindowHandle,
                source,
                display,
                out var observed));
            Assert.IsTrue(observed.SourceRemovedObserved);
        }

        [TestMethod]
        public void WorkspaceReservationKeepsOpaqueIdentityAndExactRole()
        {
            var display = CreateDisplay();
            var desktop = CreateDesktop("Target");
            var reservationId = Guid.NewGuid();
            var reservation = new AlgorithmicSlotReservation
            {
                ReservationId = reservationId,
                CorrelationId = Guid.NewGuid(),
                WindowHandle = new IntPtr(701),
                LayoutKey = new LayoutStateKey(desktop, display),
                Role = ReservedRole.Satellite,
                SatelliteIndex = 2,
                CreatedAt = DateTimeOffset.UtcNow,
                Deadline = DateTimeOffset.UtcNow.AddSeconds(30),
            };

            var slot = reservation.ToWorkspaceSlot();

            Assert.AreEqual(reservationId, slot.ReservationId);
            Assert.AreEqual(MasterSatelliteWindowRole.Satellite, slot.Role);
            Assert.AreEqual(2, slot.SatelliteIndex);
            Assert.AreSame(desktop, slot.LayoutKey.VirtualDesktop);
            Assert.AreSame(display, slot.LayoutKey.Display);
        }

        [TestMethod]
        public void ExplicitTerminalTransitionsNotifyExactlyOnceOutsideMutationLock()
        {
            using var coordinator = CreateCoordinator();
            var display = CreateDisplay();
            var source = CreateDesktop("Source");
            var target = CreateDesktop("Target");
            using var registration = coordinator.RegisterDisplay(display, new object());
            var key = new LayoutStateKey(target, display);
            coordinator.PublishCapacity(key, EmptyCapacity(totalCapacity: 4), 1);
            var notifications = new System.Collections.Generic.List<PendingWindowTransfer>();
            int throwingSubscriberCalls = 0;
            bool notificationObservedUnderLock = false;
            bool notificationObservedOffDispatcher = false;
            coordinator.TransferTerminated += transfer =>
            {
                throwingSubscriberCalls++;
                throw new InvalidOperationException("Subscriber failure must be isolated.");
            };
            coordinator.TransferTerminated += transfer =>
            {
                notificationObservedUnderLock |= coordinator.IsMutationLockHeldByCurrentThread;
                notificationObservedOffDispatcher |= !coordinator.Dispatcher.CheckAccess();
                notifications.Add(transfer);
            };

            Assert.IsTrue(Reserve(coordinator, source, display, key, 801, out var failed));
            Assert.IsTrue(coordinator.Fail(failed.CorrelationId, failed.WindowHandle, "MoveFailed"));
            Assert.IsTrue(coordinator.Fail(failed.CorrelationId, failed.WindowHandle, "MoveFailed"));

            Assert.IsTrue(Reserve(coordinator, source, display, key, 802, out var cancelled));
            Assert.IsTrue(coordinator.Cancel(
                cancelled.CorrelationId,
                cancelled.WindowHandle,
                "UserCancelled"));
            Assert.IsTrue(coordinator.Cancel(
                cancelled.CorrelationId,
                cancelled.WindowHandle,
                "UserCancelled"));

            Assert.IsTrue(Reserve(coordinator, source, display, key, 803, out var committed));
            Assert.IsTrue(coordinator.MarkMoving(committed.CorrelationId, committed.WindowHandle));
            Assert.IsTrue(coordinator.ObserveDestination(
                committed.CorrelationId,
                committed.WindowHandle));
            Assert.IsTrue(coordinator.Commit(committed.CorrelationId, committed.WindowHandle));
            Assert.IsTrue(coordinator.Commit(committed.CorrelationId, committed.WindowHandle));

            Assert.AreEqual(0, notifications.Count, "Notifications must be queued, not inline.");
            Dispatchers.DoEvents();

            Assert.AreEqual(3, throwingSubscriberCalls);
            Assert.AreEqual(3, notifications.Count);
            Assert.IsFalse(notificationObservedUnderLock);
            Assert.IsFalse(notificationObservedOffDispatcher);
            CollectionAssert.AreEqual(
                new[]
                {
                    PendingWindowTransferState.Failed,
                    PendingWindowTransferState.Cancelled,
                    PendingWindowTransferState.Committed,
                },
                notifications.Select(item => item.State).ToArray());
            CollectionAssert.AreEqual(
                new string?[] { "MoveFailed", "UserCancelled", null },
                notifications.Select(item => item.TerminalReason).ToArray());
            CollectionAssert.AreEqual(
                new[]
                {
                    failed.CorrelationId,
                    cancelled.CorrelationId,
                    committed.CorrelationId,
                },
                notifications.Select(item => item.CorrelationId).ToArray());
            Assert.AreEqual(failed.ReservationId, notifications[0].ReservationId);
        }

        [TestMethod]
        public void LifecycleRemovalPathsNotifyForEveryCancelledTransfer()
        {
            using var coordinator = CreateCoordinator();
            var display = CreateDisplay();
            var source = CreateDesktop("Source");
            var target = CreateDesktop("Target");
            var registration = coordinator.RegisterDisplay(display, new object());
            var key = new LayoutStateKey(target, display);
            var reasons = new System.Collections.Generic.List<string?>();
            coordinator.TransferTerminated += transfer => reasons.Add(transfer.TerminalReason);
            coordinator.PublishCapacity(key, EmptyCapacity(totalCapacity: 2), 1);

            Assert.IsTrue(Reserve(coordinator, source, display, key, 811, out _));
            Assert.IsTrue(coordinator.WindowClosed(new IntPtr(811)));

            Assert.IsTrue(Reserve(coordinator, source, display, key, 812, out _));
            Assert.IsTrue(coordinator.DesktopRemoved(target) > 0);
            coordinator.PublishCapacity(key, EmptyCapacity(totalCapacity: 2), 1);

            Assert.IsTrue(Reserve(coordinator, source, display, key, 813, out _));
            Assert.IsTrue(coordinator.RemoveCapacity(key));
            coordinator.PublishCapacity(key, EmptyCapacity(totalCapacity: 2), 1);

            Assert.IsTrue(Reserve(coordinator, source, display, key, 814, out _));
            coordinator.RemoveDisplay(display);
            coordinator.PublishCapacity(key, EmptyCapacity(totalCapacity: 2), 1);

            Assert.IsTrue(Reserve(coordinator, source, display, key, 815, out _));
            registration.Dispose();

            Dispatchers.DoEvents();

            CollectionAssert.AreEqual(
                new[]
                {
                    "WindowClosed",
                    "DesktopRemoved",
                    "CapacityRemoved",
                    "DisplayStateRemoved",
                    "DisplayUnregistered",
                },
                reasons.ToArray());
        }

        [TestMethod]
        public void RuntimeRegistryRemovalAndClearNotifyCancelledTransfers()
        {
            using var coordinator = CreateCoordinator();
            var display = CreateDisplay();
            var source = CreateDesktop("Source");
            var target = CreateDesktop("Target");
            using var registration = coordinator.RegisterDisplay(display, new object());
            var key = new LayoutStateKey(target, display);
            var notifications = new System.Collections.Generic.List<PendingWindowTransfer>();
            coordinator.TransferTerminated += notifications.Add;
            coordinator.PublishCapacity(key, EmptyCapacity(totalCapacity: 2), 1);

            Assert.IsTrue(Reserve(coordinator, source, display, key, 821, out _));
            Assert.IsFalse(coordinator.Remove(key));
            coordinator.PublishCapacity(key, EmptyCapacity(totalCapacity: 2), 1);

            Assert.IsTrue(coordinator.TryAdd(
                key,
                new MasterSatelliteRuntimeState(new(), true)));
            Assert.IsTrue(Reserve(coordinator, source, display, key, 822, out _));
            coordinator.Clear();

            Dispatchers.DoEvents();

            Assert.AreEqual(2, notifications.Count);
            CollectionAssert.AreEqual(
                new[] { "LayoutRemoved", "RuntimeStateCleared" },
                notifications.Select(item => item.TerminalReason).ToArray());
            Assert.IsTrue(notifications.All(item =>
                item.State == PendingWindowTransferState.Cancelled));
        }

        [TestMethod]
        public void ExplicitAndImplicitExpiryNotifyFailedTransfers()
        {
            var time = new ManualTimeProvider(
                new DateTimeOffset(2026, 4, 9, 12, 0, 0, TimeSpan.Zero));
            using var coordinator = CreateCoordinator(time, TimeSpan.FromSeconds(5));
            var display = CreateDisplay();
            var source = CreateDesktop("Source");
            var target = CreateDesktop("Target");
            using var registration = coordinator.RegisterDisplay(display, new object());
            var key = new LayoutStateKey(target, display);
            var notifications = new System.Collections.Generic.List<PendingWindowTransfer>();
            coordinator.TransferTerminated += notifications.Add;
            coordinator.PublishCapacity(key, EmptyCapacity(totalCapacity: 2), 1);

            Assert.IsTrue(Reserve(coordinator, source, display, key, 831, out _));
            time.Advance(TimeSpan.FromSeconds(6));
            Assert.AreEqual(1, coordinator.CleanupExpired());

            Assert.IsTrue(Reserve(coordinator, source, display, key, 832, out _));
            time.Advance(TimeSpan.FromSeconds(6));
            Assert.IsFalse(coordinator.TryGetActiveTransfer(new IntPtr(832), out _));

            Dispatchers.DoEvents();

            Assert.AreEqual(2, notifications.Count);
            Assert.IsTrue(notifications.All(item =>
                item.State == PendingWindowTransferState.Failed
                && item.TerminalReason == "TransferTimedOut"));
        }

        [TestMethod]
        public void UnsubscribeAndDisposeSuppressQueuedNotificationDelivery()
        {
            var coordinator = CreateCoordinator();
            var display = CreateDisplay();
            var source = CreateDesktop("Source");
            var target = CreateDesktop("Target");
            var registration = coordinator.RegisterDisplay(display, new object());
            var key = new LayoutStateKey(target, display);
            coordinator.PublishCapacity(key, EmptyCapacity(totalCapacity: 2), 1);
            int calls = 0;
            Action<PendingWindowTransfer> subscriber = _ => calls++;
            coordinator.TransferTerminated += subscriber;

            Assert.IsTrue(Reserve(coordinator, source, display, key, 841, out var first));
            Assert.IsTrue(coordinator.Fail(
                first.CorrelationId,
                first.WindowHandle,
                "Unsubscribed"));
            coordinator.TransferTerminated -= subscriber;
            Dispatchers.DoEvents();
            Assert.AreEqual(0, calls);

            coordinator.TransferTerminated += subscriber;
            Assert.IsTrue(Reserve(coordinator, source, display, key, 842, out var second));
            Assert.IsTrue(coordinator.Fail(
                second.CorrelationId,
                second.WindowHandle,
                "Disposed"));
            coordinator.Dispose();
            coordinator.TransferTerminated -= subscriber;
            Dispatchers.DoEvents();

            Assert.AreEqual(0, calls);
            registration.Dispose();
        }

        private static bool Reserve(
            AlgorithmicLayoutCoordinator coordinator,
            IVirtualDesktop source,
            IDisplay display,
            LayoutStateKey target,
            long handle,
            out PendingWindowTransfer transfer)
        {
            return coordinator.TryPlanAndReserve(
                Guid.NewGuid(),
                new IntPtr(handle),
                source,
                display,
                target,
                out transfer);
        }

        private static void AssertReadiness(
            AlgorithmicLayoutCoordinator coordinator,
            PendingWindowTransfer transfer,
            AlgorithmicReservationReadinessDisposition expected)
        {
            Assert.IsNotNull(transfer.ReservationId);
            var readiness = coordinator.GetReservationReadiness(
                transfer.ReservationId!.Value);
            Assert.AreEqual(expected, readiness.Disposition, readiness.DiagnosticReason);
        }

        private static void Commit(
            AlgorithmicLayoutCoordinator coordinator,
            PendingWindowTransfer transfer)
        {
            Assert.IsTrue(coordinator.MarkMoving(
                transfer.CorrelationId,
                transfer.WindowHandle));
            Assert.IsTrue(coordinator.ObserveDestination(
                transfer.CorrelationId,
                transfer.WindowHandle));
            Assert.IsTrue(coordinator.Commit(
                transfer.CorrelationId,
                transfer.WindowHandle));
        }

        private static void PublishPhysicalCapacity(
            AlgorithmicLayoutCoordinator coordinator,
            LayoutStateKey key,
            int occupiedSlots,
            long sequence)
        {
            int totalCapacity = 3;
            bool canAccept = occupiedSlots < totalCapacity;
            Assert.IsTrue(coordinator.PublishCapacity(
                key,
                new MasterSatelliteCapacitySnapshot(
                    WorkspaceLayoutKind.Canonical,
                    canAccept,
                    canAccept ? MasterSatelliteWindowRole.Satellite : null,
                    canAccept ? occupiedSlots - 1 : null,
                    occupiedSlots,
                    totalCapacity,
                    sequence,
                    null),
                sequence));
        }

        private static AlgorithmicLayoutCoordinator CreateCoordinator(
            TimeProvider? timeProvider = null,
            TimeSpan? timeout = null)
        {
            return new AlgorithmicLayoutCoordinator(
                new Mock<IWorkspace>(MockBehavior.Loose).Object,
                Dispatcher.CurrentDispatcher,
                timeProvider,
                timeout);
        }

        private static MasterSatelliteCapacitySnapshot EmptyCapacity(int totalCapacity)
        {
            return new MasterSatelliteCapacitySnapshot(
                WorkspaceLayoutKind.Empty,
                true,
                MasterSatelliteWindowRole.Master,
                null,
                0,
                totalCapacity,
                0,
                null);
        }

        private static IVirtualDesktop CreateDesktop(
            string name,
            IWorkspace? workspace = null)
        {
            var mock = new Mock<IVirtualDesktop>(MockBehavior.Loose);
            mock.SetupGet(item => item.Name).Returns(name);
            mock.SetupGet(item => item.IsAlive).Returns(true);
            mock.SetupGet(item => item.Workspace).Returns(workspace!);
            return mock.Object;
        }

        private static IDisplay CreateDisplay(IWorkspace? workspace = null)
        {
            var mock = new Mock<IDisplay>(MockBehavior.Loose);
            var display = mock.Object;
            mock.SetupGet(item => item.Workspace).Returns(workspace!);
            mock.Setup(item => item.Equals(It.IsAny<IDisplay>()))
                .Returns((IDisplay other) => ReferenceEquals(display, other));
            return display;
        }

        private sealed class ManualTimeProvider : TimeProvider
        {
            private DateTimeOffset m_now;

            public ManualTimeProvider(DateTimeOffset now)
            {
                m_now = now;
            }

            public override DateTimeOffset GetUtcNow()
            {
                return m_now;
            }

            public void Advance(TimeSpan duration)
            {
                m_now += duration;
            }
        }
    }
}
