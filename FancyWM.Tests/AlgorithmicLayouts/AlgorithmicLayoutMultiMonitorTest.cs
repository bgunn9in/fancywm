#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;

using FancyWM.AlgorithmicLayouts;
using FancyWM.Models;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    [TestClass]
    public class AlgorithmicLayoutMultiMonitorTest
    {
        [TestMethod]
        public void OneCoordinatorKeepsDesktopDisplayStateAndCapacityIndependent()
        {
            var workspace = new Mock<IWorkspace>(MockBehavior.Loose);
            using var coordinator = new AlgorithmicLayoutCoordinator(
                workspace.Object,
                Dispatcher.CurrentDispatcher);
            var primary = CreateDisplay(
                workspace.Object,
                Rectangle.OffsetAndSize(0, 0, 1920, 1080));
            var secondary = CreateDisplay(
                workspace.Object,
                Rectangle.OffsetAndSize(1920, 0, 3440, 1440));
            var desktop1 = CreateDesktop(workspace.Object, "Desktop 1");
            var desktop2 = CreateDesktop(workspace.Object, "Desktop 2");
            using var primaryRegistration = coordinator.RegisterDisplay(primary, new object());
            using var secondaryRegistration = coordinator.RegisterDisplay(secondary, new object());

            var primaryDesktop1 = CreateState(
                0.55,
                MasterSide.Left,
                SatelliteLayoutOrientation.Vertical);
            var secondaryDesktop1 = CreateState(
                0.72,
                MasterSide.Right,
                SatelliteLayoutOrientation.Horizontal);
            var primaryDesktop2 = CreateState(
                0.63,
                MasterSide.Right,
                SatelliteLayoutOrientation.Vertical);
            var secondaryDesktop2 = CreateState(
                0.78,
                MasterSide.Left,
                SatelliteLayoutOrientation.Horizontal);
            var states = new[]
            {
                (Key: new LayoutStateKey(desktop1, primary), State: primaryDesktop1),
                (Key: new LayoutStateKey(desktop1, secondary), State: secondaryDesktop1),
                (Key: new LayoutStateKey(desktop2, primary), State: primaryDesktop2),
                (Key: new LayoutStateKey(desktop2, secondary), State: secondaryDesktop2),
            };

            foreach (var entry in states)
            {
                Assert.IsTrue(coordinator.TryAdd(entry.Key, entry.State));
            }

            Assert.AreEqual(4, coordinator.Count);
            Assert.AreEqual(2, coordinator.SnapshotForDisplay(primary).Count);
            Assert.AreEqual(2, coordinator.SnapshotForDisplay(secondary).Count);
            foreach (var entry in states)
            {
                Assert.IsTrue(coordinator.TryGet(entry.Key, out var actual));
                Assert.AreSame(entry.State, actual);
            }

            AssertState(
                primaryDesktop1,
                0.55,
                MasterSide.Left,
                SatelliteLayoutOrientation.Vertical);
            AssertState(
                secondaryDesktop1,
                0.72,
                MasterSide.Right,
                SatelliteLayoutOrientation.Horizontal);
            AssertState(
                primaryDesktop2,
                0.63,
                MasterSide.Right,
                SatelliteLayoutOrientation.Vertical);
            AssertState(
                secondaryDesktop2,
                0.78,
                MasterSide.Left,
                SatelliteLayoutOrientation.Horizontal);

            var primaryKey = new LayoutStateKey(desktop1, primary);
            var secondaryKey = new LayoutStateKey(desktop1, secondary);
            Assert.IsTrue(coordinator.PublishCapacity(
                primaryKey,
                FullCapacity(totalCapacity: 4),
                publicationSequence: 1));
            Assert.IsTrue(coordinator.PublishCapacity(
                secondaryKey,
                EmptyCapacity(totalCapacity: 4),
                publicationSequence: 1));

            Assert.IsTrue(coordinator.TryGetCapacity(primaryKey, out var primaryCapacity));
            Assert.IsTrue(coordinator.TryGetCapacity(secondaryKey, out var secondaryCapacity));
            Assert.IsFalse(primaryCapacity.CanAcceptWindow);
            Assert.AreEqual(4, primaryCapacity.OccupiedSlots);
            Assert.AreSame(primary, primaryCapacity.LayoutKey.Display);
            Assert.IsTrue(secondaryCapacity.CanAcceptWindow);
            Assert.AreEqual(0, secondaryCapacity.OccupiedSlots);
            Assert.AreEqual(ReservedRole.Master, secondaryCapacity.NextRole);
            Assert.AreSame(secondary, secondaryCapacity.LayoutKey.Display);
        }

        [TestMethod]
        public void DestinationSearchPreservesSourceDisplayAndIgnoresOtherDisplayCapacity()
        {
            var workspace = new Mock<IWorkspace>(MockBehavior.Loose);
            var desktopManager = new Mock<IVirtualDesktopManager>(MockBehavior.Loose);
            workspace.SetupGet(item => item.VirtualDesktopManager).Returns(desktopManager.Object);
            desktopManager.SetupGet(item => item.CanManageVirtualDesktops).Returns(true);
            var source = CreateDesktop(workspace.Object, "Source");
            var destination = CreateDesktop(workspace.Object, "Destination");
            desktopManager.SetupGet(item => item.Desktops)
                .Returns(new[] { source, destination });
            using var coordinator = new AlgorithmicLayoutCoordinator(
                workspace.Object,
                Dispatcher.CurrentDispatcher);
            var sourceDisplay = CreateDisplay(
                workspace.Object,
                Rectangle.OffsetAndSize(0, 0, 1920, 1080));
            var otherDisplay = CreateDisplay(
                workspace.Object,
                Rectangle.OffsetAndSize(1920, 0, 3440, 1440));
            using var sourceRegistration = coordinator.RegisterDisplay(sourceDisplay, new object());
            using var otherRegistration = coordinator.RegisterDisplay(otherDisplay, new object());
            var sourceDisplayKey = new LayoutStateKey(destination, sourceDisplay);
            var otherDisplayKey = new LayoutStateKey(destination, otherDisplay);

            Assert.IsTrue(coordinator.PublishCapacity(
                otherDisplayKey,
                EmptyCapacity(totalCapacity: 4),
                publicationSequence: 1));

            var unavailable = coordinator.FindDestinationAndReserve(
                Guid.NewGuid(),
                new IntPtr(9101),
                source,
                sourceDisplay);

            Assert.AreEqual(
                AlgorithmicDestinationSearchDisposition.NoDestination,
                unavailable.Disposition);
            Assert.AreEqual(1, unavailable.ExaminedCandidates.Count);
            Assert.AreEqual(
                AlgorithmicDestinationCandidateDisposition.CapacityUnavailable,
                unavailable.ExaminedCandidates[0].Disposition);
            Assert.AreSame(sourceDisplay, unavailable.ExaminedCandidates[0].LayoutKey.Display);
            Assert.AreEqual(0, coordinator.ReservationCount);
            Assert.IsTrue(coordinator.TryGetCapacity(otherDisplayKey, out var untouchedOther));
            Assert.AreEqual(0, untouchedOther.ReservedSlots);

            Assert.IsTrue(coordinator.PublishCapacity(
                sourceDisplayKey,
                EmptyCapacity(totalCapacity: 4),
                publicationSequence: 1));
            var reserved = coordinator.FindDestinationAndReserve(
                Guid.NewGuid(),
                new IntPtr(9102),
                source,
                sourceDisplay);

            Assert.IsTrue(reserved.Succeeded, reserved.DiagnosticReason);
            Assert.AreSame(sourceDisplay, reserved.Transfer!.SourceDisplay);
            Assert.AreSame(sourceDisplay, reserved.Transfer.TargetDisplay);
            Assert.AreSame(destination, reserved.Transfer.TargetDesktop);
            Assert.IsNotNull(reserved.Transfer.ReservationId);
            Assert.IsTrue(coordinator.TryGetReservation(
                reserved.Transfer.ReservationId.GetValueOrDefault(),
                out var reservation));
            Assert.AreSame(sourceDisplay, reservation.LayoutKey.Display);
            Assert.IsTrue(coordinator.TryGetCapacity(sourceDisplayKey, out var sourceCapacity));
            Assert.AreEqual(1, sourceCapacity.ReservedSlots);
            Assert.IsTrue(coordinator.TryGetCapacity(otherDisplayKey, out untouchedOther));
            Assert.AreEqual(0, untouchedOther.ReservedSlots);
        }

        [TestMethod]
        public void DisplayScopesUseIdentityAndWorkAreaAspectRatioOnly()
        {
            var workspace = new Mock<IWorkspace>(MockBehavior.Loose);
            var primary = CreateDisplay(
                workspace.Object,
                Rectangle.OffsetAndSize(0, 0, 1920, 1080),
                bounds: Rectangle.OffsetAndSize(0, 0, 7680, 4320),
                scaling: 2.0);
            var thresholdUltrawide = CreateDisplay(
                workspace.Object,
                Rectangle.OffsetAndSize(1920, 30, 2100, 900),
                bounds: Rectangle.OffsetAndSize(1920, 0, 1920, 1080),
                scaling: 0.75);
            var belowThreshold = CreateDisplay(
                workspace.Object,
                Rectangle.OffsetAndSize(4020, 30, 2099, 900),
                bounds: Rectangle.OffsetAndSize(3840, 0, 3440, 1440),
                scaling: 1.5);
            var primaryOnly = Settings(AlgorithmicLayoutDisplayScope.PrimaryDisplay);
            var all = Settings(AlgorithmicLayoutDisplayScope.AllDisplays);
            var ultrawide = Settings(AlgorithmicLayoutDisplayScope.UltrawideDisplays);

            Assert.IsTrue(MasterSatelliteDisplayEligibility.IsEligible(
                primaryOnly,
                primary,
                primary));
            Assert.IsFalse(MasterSatelliteDisplayEligibility.IsEligible(
                primaryOnly,
                thresholdUltrawide,
                primary));
            Assert.IsTrue(MasterSatelliteDisplayEligibility.IsEligible(all, primary, primary));
            Assert.IsTrue(MasterSatelliteDisplayEligibility.IsEligible(
                all,
                thresholdUltrawide,
                primary));
            Assert.IsTrue(MasterSatelliteDisplayEligibility.IsEligible(
                all,
                belowThreshold,
                primary));

            Assert.AreEqual(
                21d / 9d,
                MasterSatelliteDisplayEligibility.UltrawideMinimumAspectRatio,
                0.000001);
            Assert.IsFalse(MasterSatelliteDisplayEligibility.IsEligible(
                ultrawide,
                primary,
                primary),
                "A high-resolution Bounds rectangle must not override a 16:9 WorkArea.");
            Assert.IsTrue(MasterSatelliteDisplayEligibility.IsEligible(
                ultrawide,
                thresholdUltrawide,
                primary),
                "Exactly 21:9 in WorkArea must be eligible even with 16:9 Bounds.");
            Assert.IsFalse(MasterSatelliteDisplayEligibility.IsEligible(
                ultrawide,
                belowThreshold,
                primary),
                "Ultrawide Bounds must not override a WorkArea below the 21:9 threshold.");
        }

        [TestMethod]
        public void DisplayRemovalRemovesOnlyRelatedStatesCapacityAndReservations()
        {
            var workspace = new Mock<IWorkspace>(MockBehavior.Loose);
            using var coordinator = new AlgorithmicLayoutCoordinator(
                workspace.Object,
                Dispatcher.CurrentDispatcher);
            var removedDisplay = CreateDisplay(
                workspace.Object,
                Rectangle.OffsetAndSize(0, 0, 1920, 1080));
            var retainedDisplay = CreateDisplay(
                workspace.Object,
                Rectangle.OffsetAndSize(1920, 0, 3440, 1440));
            var source = CreateDesktop(workspace.Object, "Source");
            var target = CreateDesktop(workspace.Object, "Target");
            using var removedRegistration = coordinator.RegisterDisplay(removedDisplay, new object());
            using var retainedRegistration = coordinator.RegisterDisplay(retainedDisplay, new object());

            foreach (var desktop in new[] { source, target })
            {
                Assert.IsTrue(coordinator.TryAdd(
                    new LayoutStateKey(desktop, removedDisplay),
                    CreateState(0.57, MasterSide.Left, SatelliteLayoutOrientation.Vertical)));
                Assert.IsTrue(coordinator.TryAdd(
                    new LayoutStateKey(desktop, retainedDisplay),
                    CreateState(0.74, MasterSide.Right, SatelliteLayoutOrientation.Horizontal)));
            }
            var removedKey = new LayoutStateKey(target, removedDisplay);
            var retainedKey = new LayoutStateKey(target, retainedDisplay);
            Assert.IsTrue(coordinator.PublishCapacity(
                removedKey,
                EmptyCapacity(totalCapacity: 4),
                publicationSequence: 1));
            Assert.IsTrue(coordinator.PublishCapacity(
                retainedKey,
                EmptyCapacity(totalCapacity: 4),
                publicationSequence: 1));
            Assert.IsTrue(coordinator.TryPlanAndReserve(
                Guid.NewGuid(),
                new IntPtr(9201),
                source,
                removedDisplay,
                removedKey,
                out var removedTransfer));
            Assert.IsTrue(coordinator.TryPlanAndReserve(
                Guid.NewGuid(),
                new IntPtr(9202),
                source,
                retainedDisplay,
                retainedKey,
                out var retainedTransfer));

            Assert.AreEqual(2, coordinator.RemoveDisplay(removedDisplay));

            Assert.AreEqual(2, coordinator.Count);
            Assert.AreEqual(0, coordinator.SnapshotForDisplay(removedDisplay).Count);
            Assert.AreEqual(2, coordinator.SnapshotForDisplay(retainedDisplay).Count);
            Assert.IsFalse(coordinator.TryGetCapacity(removedKey, out _));
            Assert.IsTrue(coordinator.TryGetCapacity(retainedKey, out var retainedCapacity));
            Assert.AreEqual(1, retainedCapacity.ReservedSlots);
            Assert.IsTrue(coordinator.TryGetTransfer(
                removedTransfer.CorrelationId,
                out removedTransfer));
            Assert.AreEqual(PendingWindowTransferState.Cancelled, removedTransfer.State);
            Assert.AreEqual("DisplayStateRemoved", removedTransfer.TerminalReason);
            Assert.IsTrue(coordinator.TryGetTransfer(
                retainedTransfer.CorrelationId,
                out retainedTransfer));
            Assert.AreEqual(PendingWindowTransferState.Reserved, retainedTransfer.State);
            Assert.AreEqual(1, coordinator.ReservationCount);
            Assert.AreEqual(1, coordinator.ActiveTransferCount);
            var reservation = coordinator.SnapshotReservations().Single();
            Assert.AreEqual(retainedTransfer.CorrelationId, reservation.CorrelationId);
            Assert.AreSame(retainedDisplay, reservation.LayoutKey.Display);
        }

        private static MasterSatelliteRuntimeState CreateState(
            double ratio,
            MasterSide side,
            SatelliteLayoutOrientation orientation)
        {
            return new MasterSatelliteRuntimeState(new MasterSatelliteLayoutSettings
            {
                Enabled = true,
                DisplayScope = AlgorithmicLayoutDisplayScope.AllDisplays,
                MasterRatio = ratio,
                DefaultMasterSide = side,
                DefaultSatelliteOrientation = orientation,
            }, true);
        }

        private static void AssertState(
            MasterSatelliteRuntimeState state,
            double ratio,
            MasterSide side,
            SatelliteLayoutOrientation orientation)
        {
            Assert.AreEqual(ratio, state.RequestedMasterRatio, 0.000001);
            Assert.AreEqual(side, state.MasterSide);
            Assert.AreEqual(orientation, state.SatelliteOrientation);
        }

        private static MasterSatelliteLayoutSettings Settings(
            AlgorithmicLayoutDisplayScope displayScope)
        {
            return new MasterSatelliteLayoutSettings
            {
                Enabled = true,
                DisplayScope = displayScope,
            };
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

        private static MasterSatelliteCapacitySnapshot FullCapacity(int totalCapacity)
        {
            return new MasterSatelliteCapacitySnapshot(
                WorkspaceLayoutKind.Canonical,
                false,
                null,
                null,
                totalCapacity,
                totalCapacity,
                totalCapacity,
                "Full layout");
        }

        private static IVirtualDesktop CreateDesktop(IWorkspace workspace, string name)
        {
            var mock = new Mock<IVirtualDesktop>(MockBehavior.Loose);
            mock.SetupGet(item => item.Name).Returns(name);
            mock.SetupGet(item => item.IsAlive).Returns(true);
            mock.SetupGet(item => item.Workspace).Returns(workspace);
            return mock.Object;
        }

        private static IDisplay CreateDisplay(
            IWorkspace workspace,
            Rectangle workArea,
            Rectangle? bounds = null,
            double scaling = 1.0)
        {
            var mock = new Mock<IDisplay>(MockBehavior.Loose);
            var display = mock.Object;
            mock.SetupGet(item => item.Workspace).Returns(workspace);
            mock.SetupGet(item => item.WorkArea).Returns(workArea);
            mock.SetupGet(item => item.Bounds).Returns(bounds ?? workArea);
            mock.SetupGet(item => item.Scaling).Returns(scaling);
            mock.Setup(item => item.Equals(It.IsAny<IDisplay>()))
                .Returns((IDisplay other) => ReferenceEquals(display, other));
            return display;
        }
    }
}
