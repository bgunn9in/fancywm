#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;

using FancyWM.AlgorithmicLayouts;
using FancyWM.Tests.TestUtilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    [TestClass]
    public class AlgorithmicLayoutDestinationSearchTest
    {
        private readonly Mock<IWorkspace> m_workspace = new(MockBehavior.Loose);
        private readonly Mock<IVirtualDesktopManager> m_virtualDesktopManager = new(MockBehavior.Loose);
        private readonly Dictionary<LayoutStateKey, long> m_publicationSequences
            = new(LayoutStateKeyIdentityComparer.Instance);

        [TestMethod]
        public void ExpiryDuringPreflightRejectsStaleSatelliteSlotAndContinuesInOrder()
        {
            var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
            var source = CreateDesktop("Source");
            var expiredTarget = CreateDesktop("Expired target");
            var destination = CreateDesktop("Destination");
            using var coordinator = CreateCoordinator([source, expiredTarget, destination], time);
            var display = CreateDisplay();
            using var registration = coordinator.RegisterDisplay(display, new object());
            var expiredKey = Publish(coordinator, display, expiredTarget, EmptyCapacity());
            Publish(coordinator, display, destination, EmptyCapacity());
            Assert.IsTrue(coordinator.TryPlanAndReserve(
                Guid.NewGuid(), new IntPtr(701), source, display, expiredKey, out var preceding));
            var notifications = new List<PendingWindowTransfer>();
            bool notificationUnderLock = false;
            coordinator.TransferTerminated += transfer =>
            {
                notificationUnderLock |= coordinator.IsMutationLockHeldByCurrentThread;
                notifications.Add(transfer);
            };
            var preflighted = new List<IVirtualDesktop>();
            var correlationId = Guid.NewGuid();
            var windowHandle = new IntPtr(702);

            var result = coordinator.FindDestinationAndReserve(
                correlationId, windowHandle, source, display, null, (key, capacity) =>
                {
                    Assert.IsFalse(coordinator.IsMutationLockHeldByCurrentThread);
                    preflighted.Add(key.VirtualDesktop);
                    if (ReferenceEquals(key.VirtualDesktop, expiredTarget))
                    {
                        Assert.AreEqual(ReservedRole.Satellite, capacity.NextRole);
                        Assert.AreEqual(0, capacity.NextSatelliteIndex);
                        for (int lookup = 0; lookup < 5; lookup++)
                        {
                            Assert.IsTrue(coordinator.TryGetCapacity(key, out var repeated));
                            Assert.AreEqual(capacity, repeated);
                        }
                        time.Advance(TimeSpan.FromSeconds(30));
                    }
                    return AlgorithmicDestinationPreflightResult.Accept();
                });

            AssertReservedSlot(result, coordinator, correlationId, windowHandle,
                destination, display, ReservedRole.Master, null);
            CollectionAssert.AreEqual(new[] { expiredTarget, destination }, preflighted);
            CollectionAssert.AreEqual(new[] { expiredTarget, destination },
                result.ExaminedCandidates.Select(item => item.Desktop).ToArray());
            Assert.AreEqual(AlgorithmicDestinationCandidateDisposition.ReservationRejected,
                result.ExaminedCandidates[0].Disposition);
            Assert.IsTrue(coordinator.TryGetTransfer(preceding.CorrelationId, out var expired));
            Assert.AreEqual(PendingWindowTransferState.Failed, expired.State);
            Assert.AreEqual("TransferTimedOut", expired.TerminalReason);
            Assert.IsFalse(coordinator.TryGetReservation(preceding.ReservationId!.Value, out _));
            Assert.AreEqual(1, coordinator.ReservationCount);
            Assert.AreEqual(0, notifications.Count);
            Dispatchers.DoEvents();
            Assert.IsFalse(notificationUnderLock);
            Assert.AreEqual(1, notifications.Count);
            Assert.AreEqual(preceding.CorrelationId, notifications[0].CorrelationId);
            Assert.AreEqual(preceding.ReservationId, notifications[0].ReservationId);
        }

        [TestMethod]
        public void MatchingNonemptyCapacityAfterPreflightPreservesExactSatelliteReservation()
        {
            var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
            var source = CreateDesktop("Source");
            var destination = CreateDesktop("Destination");
            using var coordinator = CreateCoordinator([source, destination], time);
            var display = CreateDisplay();
            using var registration = coordinator.RegisterDisplay(display, new object());
            var targetKey = Publish(coordinator, display, destination, EmptyCapacity());
            Assert.IsTrue(coordinator.TryPlanAndReserve(
                Guid.NewGuid(), new IntPtr(711), source, display, targetKey, out var preceding));
            var correlationId = Guid.NewGuid();
            var windowHandle = new IntPtr(712);
            var originalPosition = Rectangle.OffsetAndSize(10, 20, 300, 200);
            int preflightCalls = 0;

            var result = coordinator.FindDestinationAndReserve(
                correlationId, windowHandle, source, display, originalPosition, (key, capacity) =>
                {
                    Assert.IsFalse(coordinator.IsMutationLockHeldByCurrentThread);
                    preflightCalls++;
                    Assert.AreEqual(ReservedRole.Satellite, capacity.NextRole);
                    Assert.AreEqual(0, capacity.NextSatelliteIndex);
                    time.Advance(TimeSpan.FromSeconds(5));
                    Assert.IsTrue(coordinator.TryGetCapacity(key, out var repeated));
                    Assert.AreEqual(capacity, repeated);
                    return AlgorithmicDestinationPreflightResult.Accept();
                });

            AssertReservedSlot(result, coordinator, correlationId, windowHandle,
                destination, display, ReservedRole.Satellite, 0);
            Assert.AreEqual(originalPosition, result.Transfer!.SourceOriginalPosition);
            Assert.AreEqual(time.GetUtcNow() + TimeSpan.FromSeconds(30), result.Transfer.Deadline);
            Assert.AreEqual(1, preflightCalls);
            Assert.AreEqual(1, result.ExaminedCandidates.Count);
            Assert.AreEqual(2, coordinator.ReservationCount);
            Assert.IsTrue(coordinator.TryGetReservation(preceding.ReservationId!.Value, out var retained));
            Assert.AreEqual(preceding.CorrelationId, retained.CorrelationId);
            Assert.AreEqual(ReservedRole.Master, retained.Role);
            Assert.IsNull(retained.SatelliteIndex);
        }

        [TestMethod]
        public void CommittedShadowExpiryDuringPreflightInvalidatesCapacityAndContinues()
        {
            var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
            var source = CreateDesktop("Source");
            var expiredTarget = CreateDesktop("Expired shadow target");
            var destination = CreateDesktop("Destination");
            using var coordinator = CreateCoordinator([source, expiredTarget, destination], time);
            var display = CreateDisplay();
            using var registration = coordinator.RegisterDisplay(display, new object());
            var expiredKey = Publish(coordinator, display, expiredTarget, EmptyCapacity());
            Publish(coordinator, display, destination, EmptyCapacity());
            Assert.IsTrue(coordinator.TryPlanAndReserve(
                Guid.NewGuid(), new IntPtr(721), source, display, expiredKey, out var committed));
            Assert.IsTrue(coordinator.MarkMoving(committed.CorrelationId, committed.WindowHandle));
            Assert.IsTrue(coordinator.ObserveDestination(committed.CorrelationId, committed.WindowHandle));
            Assert.IsTrue(coordinator.Commit(committed.CorrelationId, committed.WindowHandle));
            var correlationId = Guid.NewGuid();
            var windowHandle = new IntPtr(722);
            var preflighted = new List<IVirtualDesktop>();

            var result = coordinator.FindDestinationAndReserve(
                correlationId, windowHandle, source, display, null, (key, capacity) =>
                {
                    Assert.IsFalse(coordinator.IsMutationLockHeldByCurrentThread);
                    preflighted.Add(key.VirtualDesktop);
                    if (ReferenceEquals(key.VirtualDesktop, expiredTarget))
                    {
                        Assert.AreEqual(1, capacity.ReservedSlots);
                        Assert.AreEqual(ReservedRole.Satellite, capacity.NextRole);
                        Assert.AreEqual(0, capacity.NextSatelliteIndex);
                        time.Advance(TimeSpan.FromSeconds(30));
                    }
                    return AlgorithmicDestinationPreflightResult.Accept();
                });

            AssertReservedSlot(result, coordinator, correlationId, windowHandle,
                destination, display, ReservedRole.Master, null);
            CollectionAssert.AreEqual(new[] { expiredTarget, destination }, preflighted);
            CollectionAssert.AreEqual(new[] { expiredTarget, destination },
                result.ExaminedCandidates.Select(item => item.Desktop).ToArray());
            Assert.AreEqual(AlgorithmicDestinationCandidateDisposition.ReservationRejected,
                result.ExaminedCandidates[0].Disposition);
            Assert.IsFalse(coordinator.TryGetCapacity(expiredKey, out _));
            Assert.IsTrue(coordinator.TryGetTransfer(committed.CorrelationId, out var terminal));
            Assert.AreEqual(PendingWindowTransferState.Committed, terminal.State);
            Assert.IsFalse(coordinator.TryGetReservation(committed.ReservationId!.Value, out _));
            Assert.AreEqual(1, coordinator.ReservationCount);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void CapacityChangeDuringPreflightRejectsStaleAcceptance(bool sameFreeSlot)
        {
            var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
            var source = CreateDesktop("Source");
            var changedTarget = CreateDesktop("Changed target");
            var destination = CreateDesktop("Destination");
            using var coordinator = CreateCoordinator([source, changedTarget, destination], time);
            var display = CreateDisplay();
            using var registration = coordinator.RegisterDisplay(display, new object());
            Publish(coordinator, display, changedTarget, EmptyCapacity());
            Publish(coordinator, display, destination, EmptyCapacity());
            var correlationId = Guid.NewGuid();
            var windowHandle = new IntPtr(731);
            var preflighted = new List<IVirtualDesktop>();

            var result = coordinator.FindDestinationAndReserve(
                correlationId, windowHandle, source, display, null, (key, capacity) =>
                {
                    Assert.IsFalse(coordinator.IsMutationLockHeldByCurrentThread);
                    preflighted.Add(key.VirtualDesktop);
                    if (ReferenceEquals(key.VirtualDesktop, changedTarget))
                    {
                        time.Advance(TimeSpan.FromSeconds(1));
                        Publish(coordinator, display, changedTarget,
                            sameFreeSlot ? EmptyCapacity() with { Revision = capacity.Revision + 1 } : FullCapacity());
                        Assert.IsTrue(coordinator.TryGetCapacity(key, out var changed));
                        Assert.AreNotEqual(capacity.Revision, changed.Revision);
                        if (sameFreeSlot)
                        {
                            Assert.AreEqual(capacity.NextRole, changed.NextRole);
                            Assert.AreEqual(capacity.NextSatelliteIndex, changed.NextSatelliteIndex);
                        }
                    }
                    return AlgorithmicDestinationPreflightResult.Accept();
                });

            AssertReservedSlot(result, coordinator, correlationId, windowHandle,
                destination, display, ReservedRole.Master, null);
            CollectionAssert.AreEqual(new[] { changedTarget, destination }, preflighted);
            CollectionAssert.AreEqual(new[] { changedTarget, destination },
                result.ExaminedCandidates.Select(item => item.Desktop).ToArray());
            Assert.AreEqual(AlgorithmicDestinationCandidateDisposition.ReservationRejected,
                result.ExaminedCandidates[0].Disposition);
            Assert.AreEqual(1, coordinator.ReservationCount);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void ReservationPrefixReplacementInvalidatesOnlyItsOwnLayoutPreflight(bool otherDisplay)
        {
            var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
            var source = CreateDesktop("Source");
            var target = CreateDesktop("Target");
            var fallback = CreateDesktop("Fallback");
            using var coordinator = CreateCoordinator([source, target, fallback], time);
            var display = CreateDisplay();
            var independentDisplay = CreateDisplay();
            using var registration = coordinator.RegisterDisplay(display, new object());
            using var independentRegistration = coordinator.RegisterDisplay(independentDisplay, new object());
            var targetKey = Publish(coordinator, display, target, EmptyCapacity());
            Publish(coordinator, display, fallback, EmptyCapacity());
            Assert.IsTrue(coordinator.TryPlanAndReserve(
                Guid.NewGuid(), new IntPtr(741), source, display, targetKey, out var preceding));
            var replacedKey = otherDisplay
                ? Publish(coordinator, independentDisplay, target, EmptyCapacity())
                : targetKey;
            var replaced = preceding;
            if (otherDisplay)
            {
                Assert.IsTrue(coordinator.TryPlanAndReserve(
                    Guid.NewGuid(), new IntPtr(742), source, independentDisplay, replacedKey, out replaced));
            }
            PendingWindowTransfer? replacement = null;
            var correlationId = Guid.NewGuid();
            var windowHandle = new IntPtr(743);
            var preflighted = new List<IVirtualDesktop>();

            var result = coordinator.FindDestinationAndReserve(
                correlationId, windowHandle, source, display, null, (key, capacity) =>
                {
                    Assert.IsFalse(coordinator.IsMutationLockHeldByCurrentThread);
                    preflighted.Add(key.VirtualDesktop);
                    if (ReferenceEquals(key.VirtualDesktop, target))
                    {
                        Assert.AreEqual(ReservedRole.Satellite, capacity.NextRole);
                        Assert.AreEqual(0, capacity.NextSatelliteIndex);
                        time.Advance(TimeSpan.FromSeconds(1));
                        Assert.IsTrue(coordinator.Cancel(
                            replaced.CorrelationId, replaced.WindowHandle, "PrefixReplaced"));
                        Assert.IsTrue(coordinator.TryPlanAndReserve(
                            Guid.NewGuid(), new IntPtr(744), source, replacedKey.Display, replacedKey, out replacement));
                        Assert.IsTrue(coordinator.TryGetCapacity(key, out var changed));
                        Assert.AreEqual(capacity.NextRole, changed.NextRole);
                        Assert.AreEqual(capacity.NextSatelliteIndex, changed.NextSatelliteIndex);
                        Assert.AreEqual(capacity.ReservedSlots, changed.ReservedSlots);
                        Assert.AreEqual(capacity.Revision, changed.Revision);
                        if (otherDisplay) { Assert.AreEqual(capacity, changed); }
                    }
                    return AlgorithmicDestinationPreflightResult.Accept();
                });

            AssertReservedSlot(result, coordinator, correlationId, windowHandle,
                otherDisplay ? target : fallback, display,
                otherDisplay ? ReservedRole.Satellite : ReservedRole.Master,
                otherDisplay ? 0 : null);
            CollectionAssert.AreEqual(otherDisplay ? new[] { target } : new[] { target, fallback }, preflighted);
            CollectionAssert.AreEqual(preflighted,
                result.ExaminedCandidates.Select(item => item.Desktop).ToArray());
            Assert.AreEqual(otherDisplay
                ? AlgorithmicDestinationCandidateDisposition.Reserved
                : AlgorithmicDestinationCandidateDisposition.ReservationRejected,
                result.ExaminedCandidates[0].Disposition);
            Assert.IsTrue(coordinator.TryGetTransfer(replaced.CorrelationId, out var terminal));
            Assert.AreEqual(PendingWindowTransferState.Cancelled, terminal.State);
            Assert.AreEqual("PrefixReplaced", terminal.TerminalReason);
            Assert.IsFalse(coordinator.TryGetReservation(replaced.ReservationId!.Value, out _));
            Assert.IsNotNull(replacement);
            var replacementTransfer = replacement!;
            Assert.IsTrue(coordinator.TryGetReservation(replacementTransfer.ReservationId!.Value, out var current));
            Assert.AreEqual(replacementTransfer.CorrelationId, current.CorrelationId);
            Assert.AreEqual(new IntPtr(744), current.WindowHandle);
            Assert.AreSame(replacedKey.Display, current.LayoutKey.Display);
            Assert.AreEqual(ReservedRole.Master, current.Role);
            Assert.IsNull(current.SatelliteIndex);
            Assert.AreEqual(otherDisplay ? 3 : 2, coordinator.ReservationCount);
        }

        private static void AssertReservedSlot(
            AlgorithmicDestinationSearchResult result,
            AlgorithmicLayoutCoordinator coordinator,
            Guid correlationId,
            IntPtr windowHandle,
            IVirtualDesktop destination,
            IDisplay display,
            ReservedRole role,
            int? satelliteIndex)
        {
            Assert.IsTrue(result.Succeeded, result.DiagnosticReason);
            Assert.AreEqual(AlgorithmicDestinationSearchDisposition.Reserved, result.Disposition);
            Assert.IsNotNull(result.Transfer);
            var transfer = result.Transfer!;
            Assert.AreEqual(correlationId, transfer.CorrelationId);
            Assert.AreEqual(windowHandle, transfer.WindowHandle);
            Assert.AreSame(destination, transfer.TargetDesktop);
            Assert.AreSame(display, transfer.TargetDisplay);
            Assert.AreEqual(role, transfer.TargetRole);
            Assert.AreEqual(satelliteIndex, transfer.TargetSatelliteIndex);
            Assert.AreEqual(PendingWindowTransferState.Reserved, transfer.State);
            Assert.IsTrue(coordinator.TryGetReservation(transfer.ReservationId!.Value, out var reservation));
            Assert.AreEqual(correlationId, reservation.CorrelationId);
            Assert.AreEqual(windowHandle, reservation.WindowHandle);
            Assert.AreSame(destination, reservation.LayoutKey.VirtualDesktop);
            Assert.AreSame(display, reservation.LayoutKey.Display);
            Assert.AreEqual(role, reservation.Role);
            Assert.AreEqual(satelliteIndex, reservation.SatelliteIndex);
        }

        [TestMethod]
        public void WorkspaceVirtualDesktopManagerGetterFailureReturnsNoDestination()
        {
            var source = CreateDesktop("Source");
            var display = CreateDisplay();
            m_workspace.SetupGet(item => item.VirtualDesktopManager)
                .Throws(new InvalidOperationException("sensitive-manager-detail"));
            using var coordinator = new AlgorithmicLayoutCoordinator(
                m_workspace.Object,
                Dispatcher.CurrentDispatcher);
            using var registration = coordinator.RegisterDisplay(display, new object());

            var result = coordinator.FindDestinationAndReserve(
                Guid.NewGuid(),
                new IntPtr(51),
                source,
                display);

            AssertBoundaryUnavailable(
                result,
                coordinator,
                "InvalidOperationException",
                "sensitive-manager-detail");
        }

        [TestMethod]
        public void VirtualDesktopCapabilityGetterFailureReturnsNoDestination()
        {
            var source = CreateDesktop("Source");
            using var coordinator = CreateCoordinator([source]);
            var display = CreateDisplay();
            using var registration = coordinator.RegisterDisplay(display, new object());
            m_virtualDesktopManager.SetupGet(
                    item => item.CanManageVirtualDesktops)
                .Throws(new InvalidOperationException("sensitive-capability-detail"));

            var result = coordinator.FindDestinationAndReserve(
                Guid.NewGuid(),
                new IntPtr(52),
                source,
                display);

            AssertBoundaryUnavailable(
                result,
                coordinator,
                "InvalidOperationException",
                "sensitive-capability-detail");
        }

        [TestMethod]
        public void VirtualDesktopSnapshotFailureReturnsNoDestination()
        {
            var source = CreateDesktop("Source");
            using var coordinator = CreateCoordinator([source]);
            var display = CreateDisplay();
            using var registration = coordinator.RegisterDisplay(display, new object());
            m_virtualDesktopManager.SetupGet(item => item.Desktops)
                .Throws(new InvalidOperationException("sensitive-snapshot-detail"));

            var result = coordinator.FindDestinationAndReserve(
                Guid.NewGuid(),
                new IntPtr(53),
                source,
                display);

            AssertBoundaryUnavailable(
                result,
                coordinator,
                "InvalidOperationException",
                "sensitive-snapshot-detail");
        }

        [TestMethod]
        public void CandidateAvailabilityFailureIsSkippedWithoutLeakingItsMessage()
        {
            var source = CreateDesktop("Source");
            var unavailable = CreateDesktop("Unavailable");
            var destination = CreateDesktop("Destination");
            Mock.Get(unavailable).SetupGet(item => item.IsAlive)
                .Throws(new InvalidOperationException("sensitive-alive-detail"));
            using var coordinator = CreateCoordinator(
                [source, unavailable, destination]);
            var display = CreateDisplay();
            using var registration = coordinator.RegisterDisplay(display, new object());
            Publish(coordinator, display, unavailable, EmptyCapacity());
            Publish(coordinator, display, destination, EmptyCapacity());

            var result = coordinator.FindDestinationAndReserve(
                Guid.NewGuid(),
                new IntPtr(54),
                source,
                display);

            Assert.IsTrue(result.Succeeded, result.DiagnosticReason);
            Assert.AreEqual(2, result.ExaminedCandidates.Count);
            Assert.AreEqual(
                AlgorithmicDestinationCandidateDisposition.Dead,
                result.ExaminedCandidates[0].Disposition);
            StringAssert.Contains(
                result.ExaminedCandidates[0].DiagnosticReason,
                "InvalidOperationException");
            Assert.IsFalse(result.ExaminedCandidates[0].DiagnosticReason.Contains(
                "sensitive-alive-detail",
                StringComparison.Ordinal));
            Assert.AreSame(destination, result.Transfer!.TargetDesktop);
            Assert.AreEqual(1, coordinator.ReservationCount);
        }

        [TestMethod]
        public void SearchStartsWithDesktopImmediatelyAfterSource()
        {
            var source = CreateDesktop("Source");
            var next = CreateDesktop("Next");
            var later = CreateDesktop("Later");
            using var coordinator = CreateCoordinator([source, next, later]);
            var display = CreateDisplay();
            using var registration = coordinator.RegisterDisplay(display, new object());
            Publish(coordinator, display, next, EmptyCapacity());
            Publish(coordinator, display, later, EmptyCapacity());

            var result = coordinator.FindDestinationAndReserve(
                Guid.NewGuid(),
                new IntPtr(101),
                source,
                display);

            Assert.IsTrue(result.Succeeded, result.DiagnosticReason);
            Assert.AreEqual(AlgorithmicDestinationSearchDisposition.Reserved, result.Disposition);
            Assert.AreSame(next, result.Transfer!.TargetDesktop);
            Assert.AreEqual(ReservedRole.Master, result.Transfer.TargetRole);
            Assert.AreEqual(1, result.ExaminedCandidates.Count);
            Assert.AreSame(next, result.ExaminedCandidates[0].Desktop);
            Assert.AreEqual(
                AlgorithmicDestinationCandidateDisposition.Reserved,
                result.ExaminedCandidates[0].Disposition);
        }

        [TestMethod]
        public void SearchWrapsAfterLastDesktopAndExaminesEachCandidateAtMostOnce()
        {
            var wrappedFirst = CreateDesktop("Wrapped first");
            var destination = CreateDesktop("Destination");
            var source = CreateDesktop("Source");
            var afterSource = CreateDesktop("After source");
            using var coordinator = CreateCoordinator(
                [wrappedFirst, destination, source, afterSource]);
            var display = CreateDisplay();
            using var registration = coordinator.RegisterDisplay(display, new object());
            Publish(coordinator, display, afterSource, FullCapacity());
            Publish(coordinator, display, destination, EmptyCapacity());

            var result = coordinator.FindDestinationAndReserve(
                Guid.NewGuid(),
                new IntPtr(201),
                source,
                display);

            Assert.IsTrue(result.Succeeded, result.DiagnosticReason);
            CollectionAssert.AreEqual(
                new[] { afterSource, wrappedFirst, destination },
                result.ExaminedCandidates.Select(item => item.Desktop).ToArray());
            Assert.AreSame(destination, result.Transfer!.TargetDesktop);
            Assert.AreEqual(
                result.ExaminedCandidates.Count,
                result.ExaminedCandidates.Select(item => item.Desktop).Distinct().Count());
        }

        [TestMethod]
        public void SearchSkipsDeadManualAndFullCandidates()
        {
            var source = CreateDesktop("Source");
            var dead = CreateDesktop("Dead", isAlive: false);
            var manual = CreateDesktop("Manual");
            var full = CreateDesktop("Full");
            var destination = CreateDesktop("Destination");
            using var coordinator = CreateCoordinator(
                [source, dead, manual, full, destination]);
            var display = CreateDisplay();
            using var registration = coordinator.RegisterDisplay(display, new object());
            Publish(coordinator, display, manual, ManualCapacity());
            Publish(coordinator, display, full, FullCapacity());
            Publish(coordinator, display, destination, EmptyCapacity());

            var result = coordinator.FindDestinationAndReserve(
                Guid.NewGuid(),
                new IntPtr(301),
                source,
                display);

            Assert.IsTrue(result.Succeeded, result.DiagnosticReason);
            CollectionAssert.AreEqual(
                new[]
                {
                    AlgorithmicDestinationCandidateDisposition.Dead,
                    AlgorithmicDestinationCandidateDisposition.UnsupportedLayout,
                    AlgorithmicDestinationCandidateDisposition.Full,
                    AlgorithmicDestinationCandidateDisposition.Reserved,
                },
                result.ExaminedCandidates.Select(item => item.Disposition).ToArray());
            Assert.AreSame(destination, result.Transfer!.TargetDesktop);
        }

        [TestMethod]
        public void SearchSkipsPlacementPreflightFailureAndPreservesSourceGeometry()
        {
            var source = CreateDesktop("Source");
            var tooSmall = CreateDesktop("Too small");
            var destination = CreateDesktop("Destination");
            using var coordinator = CreateCoordinator([source, tooSmall, destination]);
            var display = CreateDisplay();
            using var registration = coordinator.RegisterDisplay(display, new object());
            Publish(coordinator, display, tooSmall, EmptyCapacity());
            Publish(coordinator, display, destination, EmptyCapacity());
            var sourceGeometry = Rectangle.OffsetAndSize(10, 20, 300, 200);
            var preflighted = new List<IVirtualDesktop>();

            var result = coordinator.FindDestinationAndReserve(
                Guid.NewGuid(),
                new IntPtr(351),
                source,
                display,
                sourceGeometry,
                (key, _) =>
                {
                    preflighted.Add(key.VirtualDesktop);
                    return ReferenceEquals(key.VirtualDesktop, tooSmall)
                        ? AlgorithmicDestinationPreflightResult.Reject(
                            "The window minimum size does not fit this destination.")
                        : AlgorithmicDestinationPreflightResult.Accept();
                });

            Assert.IsTrue(result.Succeeded, result.DiagnosticReason);
            CollectionAssert.AreEqual(
                new[] { tooSmall, destination },
                preflighted);
            Assert.AreEqual(
                AlgorithmicDestinationCandidateDisposition.PlacementRejected,
                result.ExaminedCandidates[0].Disposition);
            StringAssert.Contains(
                result.ExaminedCandidates[0].DiagnosticReason,
                "minimum size");
            Assert.AreSame(destination, result.Transfer!.TargetDesktop);
            Assert.AreEqual(sourceGeometry, result.Transfer.SourceOriginalPosition);
        }

        [TestMethod]
        public void ReservationThatFillsTargetMakesSearchContinueToNextDesktop()
        {
            var source = CreateDesktop("Source");
            var reservedTarget = CreateDesktop("Reserved target");
            var destination = CreateDesktop("Destination");
            using var coordinator = CreateCoordinator([source, reservedTarget, destination]);
            var display = CreateDisplay();
            using var registration = coordinator.RegisterDisplay(display, new object());
            var reservedKey = Publish(
                coordinator,
                display,
                reservedTarget,
                EmptyCapacity(totalCapacity: 1));
            Publish(coordinator, display, destination, EmptyCapacity(totalCapacity: 1));
            Assert.IsTrue(coordinator.TryPlanAndReserve(
                Guid.NewGuid(),
                new IntPtr(401),
                source,
                display,
                reservedKey,
                out _));

            var result = coordinator.FindDestinationAndReserve(
                Guid.NewGuid(),
                new IntPtr(402),
                source,
                display);

            Assert.IsTrue(result.Succeeded, result.DiagnosticReason);
            Assert.AreEqual(2, result.ExaminedCandidates.Count);
            Assert.AreEqual(
                AlgorithmicDestinationCandidateDisposition.Full,
                result.ExaminedCandidates[0].Disposition);
            Assert.AreEqual(1, result.ExaminedCandidates[0].Capacity!.ReservedSlots);
            Assert.AreSame(destination, result.Transfer!.TargetDesktop);
        }

        [TestMethod]
        public void ExistingPlannedTransferIsReservedBeforeSearchReportsSuccess()
        {
            var source = CreateDesktop("Source");
            var destination = CreateDesktop("Destination");
            using var coordinator = CreateCoordinator([source, destination]);
            var display = CreateDisplay();
            using var registration = coordinator.RegisterDisplay(display, new object());
            var destinationKey = Publish(
                coordinator,
                display,
                destination,
                EmptyCapacity());
            var correlationId = Guid.NewGuid();
            var windowHandle = new IntPtr(451);
            Assert.IsTrue(coordinator.TryPlan(
                correlationId,
                windowHandle,
                source,
                display,
                destinationKey,
                out var planned));
            Assert.AreEqual(PendingWindowTransferState.Planned, planned.State);
            Assert.IsNull(planned.ReservationId);
            Assert.AreEqual(0, coordinator.ReservationCount);

            var result = coordinator.FindDestinationAndReserve(
                correlationId,
                windowHandle,
                source,
                display);

            Assert.IsTrue(result.Succeeded, result.DiagnosticReason);
            Assert.AreEqual(
                AlgorithmicDestinationSearchDisposition.ExistingReservation,
                result.Disposition);
            Assert.AreEqual(PendingWindowTransferState.Reserved, result.Transfer!.State);
            var reservationId = result.Transfer.ReservationId;
            Assert.IsNotNull(reservationId);
            Assert.AreEqual(1, coordinator.ReservationCount);
            Assert.IsTrue(coordinator.TryGetReservation(
                reservationId.GetValueOrDefault(),
                out var reservation));
            Assert.AreEqual(correlationId, reservation.CorrelationId);
            Assert.AreEqual(windowHandle, reservation.WindowHandle);
        }

        [TestMethod]
        public void RecentlyCompletedWindowReturnsConflictBeforeDestinationSearch()
        {
            var time = new ManualTimeProvider(
                new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));
            var source = CreateDesktop("Source");
            var destination = CreateDesktop("Destination");
            using var coordinator = CreateCoordinator([source, destination], time);
            var display = CreateDisplay();
            using var registration = coordinator.RegisterDisplay(display, new object());
            Publish(coordinator, display, destination, EmptyCapacity());
            var completedCorrelation = Guid.NewGuid();
            var retryCorrelation = Guid.NewGuid();
            var windowHandle = new IntPtr(471);

            var initial = coordinator.FindDestinationAndReserve(
                completedCorrelation,
                windowHandle,
                source,
                display);
            Assert.IsTrue(initial.Succeeded, initial.DiagnosticReason);
            Assert.IsTrue(coordinator.Fail(
                completedCorrelation,
                windowHandle,
                "MoveWindowFailed"));

            var retry = coordinator.FindDestinationAndReserve(
                retryCorrelation,
                windowHandle,
                source,
                display);

            Assert.AreEqual(
                AlgorithmicDestinationSearchDisposition.TransferConflict,
                retry.Disposition);
            Assert.AreEqual(0, retry.ExaminedCandidates.Count);
            Assert.AreEqual(0, coordinator.ReservationCount);
            Assert.AreEqual(0, coordinator.ActiveTransferCount);
            Assert.IsFalse(coordinator.TryGetTransfer(retryCorrelation, out _));
            StringAssert.Contains(retry.DiagnosticReason, "recently completed");
        }

        [TestMethod]
        public void RetainedHandleRejectsReplacementAndLateEventBeforeExpiryThenAllowsReuse()
        {
            var time = new ManualTimeProvider(
                new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));
            var source = CreateDesktop("Source");
            var destination = CreateDesktop("Destination");
            using var coordinator = CreateCoordinator([source, destination], time);
            var display = CreateDisplay();
            using var registration = coordinator.RegisterDisplay(display, new object());
            Publish(coordinator, display, destination, EmptyCapacity());
            var oldCorrelation = Guid.NewGuid();
            var replacementCorrelation = Guid.NewGuid();
            var windowHandle = new IntPtr(472);

            var initial = coordinator.FindDestinationAndReserve(
                oldCorrelation,
                windowHandle,
                source,
                display);
            Assert.IsTrue(initial.Succeeded, initial.DiagnosticReason);
            Assert.IsTrue(coordinator.Fail(
                oldCorrelation,
                windowHandle,
                "OriginalWrapperClosed"));

            var retainedRetry = coordinator.FindDestinationAndReserve(
                replacementCorrelation,
                windowHandle,
                source,
                display);
            Assert.AreEqual(
                AlgorithmicDestinationSearchDisposition.TransferConflict,
                retainedRetry.Disposition);
            Assert.IsTrue(coordinator.ObserveSourceRemoved(
                windowHandle,
                source,
                display,
                out var lateOldTransfer));
            Assert.AreEqual(oldCorrelation, lateOldTransfer.CorrelationId);
            Assert.IsFalse(coordinator.TryGetTransfer(replacementCorrelation, out _));
            Assert.AreEqual(0, coordinator.ReservationCount);

            time.Advance(TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(1)));
            coordinator.CleanupExpired();

            var replacement = coordinator.FindDestinationAndReserve(
                replacementCorrelation,
                windowHandle,
                source,
                display);
            Assert.AreEqual(
                AlgorithmicDestinationSearchDisposition.Reserved,
                replacement.Disposition,
                replacement.DiagnosticReason);
            Assert.AreEqual(replacementCorrelation, replacement.Transfer!.CorrelationId);
            Assert.AreEqual(1, coordinator.ReservationCount);
            Assert.IsFalse(coordinator.ObserveDestination(oldCorrelation, windowHandle));
            Assert.IsTrue(coordinator.TryGetTransfer(
                replacementCorrelation,
                out var retainedReplacement));
            Assert.AreEqual(PendingWindowTransferState.Reserved, retainedReplacement.State);
        }

        [TestMethod]
        public void SourceDesktopIsNeverExaminedOrSelected()
        {
            var source = CreateDesktop("Only source");
            using var coordinator = CreateCoordinator([source]);
            var display = CreateDisplay();
            using var registration = coordinator.RegisterDisplay(display, new object());
            Publish(coordinator, display, source, EmptyCapacity());

            var result = coordinator.FindDestinationAndReserve(
                Guid.NewGuid(),
                new IntPtr(501),
                source,
                display);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(AlgorithmicDestinationSearchDisposition.NoDestination, result.Disposition);
            Assert.AreEqual(0, result.ExaminedCandidates.Count);
            Assert.AreEqual(0, coordinator.ReservationCount);
            StringAssert.Contains(result.DiagnosticReason, "source");
        }

        [TestMethod]
        public void NoDestinationReturnsOrderedDiagnosticsWithoutRepeatingCandidates()
        {
            var full = CreateDesktop("Full");
            var source = CreateDesktop("Source");
            var dead = CreateDesktop("Dead", isAlive: false);
            var manual = CreateDesktop("Manual");
            using var coordinator = CreateCoordinator([full, source, dead, manual]);
            var display = CreateDisplay();
            using var registration = coordinator.RegisterDisplay(display, new object());
            Publish(coordinator, display, manual, ManualCapacity());
            Publish(coordinator, display, full, FullCapacity());

            var result = coordinator.FindDestinationAndReserve(
                Guid.NewGuid(),
                new IntPtr(601),
                source,
                display);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(AlgorithmicDestinationSearchDisposition.NoDestination, result.Disposition);
            CollectionAssert.AreEqual(
                new[] { dead, manual, full },
                result.ExaminedCandidates.Select(item => item.Desktop).ToArray());
            CollectionAssert.AreEqual(
                new[]
                {
                    AlgorithmicDestinationCandidateDisposition.Dead,
                    AlgorithmicDestinationCandidateDisposition.UnsupportedLayout,
                    AlgorithmicDestinationCandidateDisposition.Full,
                },
                result.ExaminedCandidates.Select(item => item.Disposition).ToArray());
            Assert.AreEqual(
                3,
                result.ExaminedCandidates.Select(item => item.Desktop).Distinct().Count());
            Assert.IsTrue(result.ExaminedCandidates.All(
                item => !string.IsNullOrWhiteSpace(item.DiagnosticReason)));
            StringAssert.Contains(result.DiagnosticReason, "No alive");
            Assert.AreEqual(0, coordinator.ReservationCount);
        }

        private static void AssertBoundaryUnavailable(
            AlgorithmicDestinationSearchResult result,
            AlgorithmicLayoutCoordinator coordinator,
            string expectedExceptionType,
            string sensitiveMessage)
        {
            Assert.AreEqual(
                AlgorithmicDestinationSearchDisposition.NoDestination,
                result.Disposition);
            Assert.IsNull(result.Transfer);
            Assert.AreEqual(0, result.ExaminedCandidates.Count);
            StringAssert.Contains(result.DiagnosticReason, expectedExceptionType);
            Assert.IsFalse(result.DiagnosticReason.Contains(
                sensitiveMessage,
                StringComparison.Ordinal));
            Assert.AreEqual(0, coordinator.ReservationCount);
            Assert.AreEqual(0, coordinator.ActiveTransferCount);
        }

        private AlgorithmicLayoutCoordinator CreateCoordinator(
            IReadOnlyList<IVirtualDesktop> desktops,
            TimeProvider? timeProvider = null)
        {
            m_virtualDesktopManager.SetupGet(
                    item => item.CanManageVirtualDesktops)
                .Returns(true);
            m_virtualDesktopManager.SetupGet(item => item.Desktops).Returns(desktops);
            m_workspace.SetupGet(item => item.VirtualDesktopManager)
                .Returns(m_virtualDesktopManager.Object);
            return new AlgorithmicLayoutCoordinator(
                m_workspace.Object,
                Dispatcher.CurrentDispatcher,
                timeProvider);
        }

        private LayoutStateKey Publish(
            AlgorithmicLayoutCoordinator coordinator,
            IDisplay display,
            IVirtualDesktop desktop,
            MasterSatelliteCapacitySnapshot capacity)
        {
            var key = new LayoutStateKey(desktop, display);
            long publicationSequence = m_publicationSequences.TryGetValue(key, out var current)
                ? current + 1
                : 1;
            m_publicationSequences[key] = publicationSequence;
            Assert.IsTrue(coordinator.PublishCapacity(
                key,
                capacity,
                publicationSequence));
            return key;
        }

        private static MasterSatelliteCapacitySnapshot EmptyCapacity(int totalCapacity = 2)
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

        private static MasterSatelliteCapacitySnapshot ManualCapacity()
        {
            return new MasterSatelliteCapacitySnapshot(
                WorkspaceLayoutKind.Manual,
                false,
                null,
                null,
                1,
                2,
                0,
                "Manual layout");
        }

        private static MasterSatelliteCapacitySnapshot FullCapacity()
        {
            return new MasterSatelliteCapacitySnapshot(
                WorkspaceLayoutKind.Canonical,
                false,
                null,
                null,
                2,
                2,
                2,
                "Full layout");
        }

        private IVirtualDesktop CreateDesktop(string name, bool isAlive = true)
        {
            var mock = new Mock<IVirtualDesktop>(MockBehavior.Loose);
            mock.SetupGet(item => item.Name).Returns(name);
            mock.SetupGet(item => item.IsAlive).Returns(isAlive);
            mock.SetupGet(item => item.Workspace).Returns(m_workspace.Object);
            return mock.Object;
        }

        private IDisplay CreateDisplay()
        {
            var mock = new Mock<IDisplay>(MockBehavior.Loose);
            var display = mock.Object;
            mock.SetupGet(item => item.Workspace).Returns(m_workspace.Object);
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
