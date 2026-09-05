using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using FancyWM.AlgorithmicLayouts;
using FancyWM.Resources;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    [TestClass]
    public class AlgorithmicLayoutEventTest
    {
        [TestMethod]
        public void EventCapturesStableWindowAndTransferContext()
        {
            var display = new Mock<IDisplay>().Object;
            var source = new Mock<IVirtualDesktop>().Object;
            var target = new Mock<IVirtualDesktop>().Object;
            var correlationId = Guid.NewGuid();

            var layoutEvent = new AlgorithmicLayoutEvent(
                AlgorithmicLayoutEventKind.WindowMovedToDesktop,
                display,
                "CapacityReached",
                "AlgorithmicLayout.WindowMovedToDesktop",
                new IntPtr(1234),
                "Editor",
                source,
                target,
                correlationId,
                4,
                4);

            Assert.AreEqual(
                AlgorithmicLayoutEventKind.WindowMovedToDesktop,
                layoutEvent.Kind);
            Assert.AreEqual(new IntPtr(1234), layoutEvent.WindowHandle);
            Assert.AreEqual("Editor", layoutEvent.WindowTitle);
            Assert.AreSame(source, layoutEvent.SourceDesktop);
            Assert.AreSame(target, layoutEvent.TargetDesktop);
            Assert.AreSame(display, layoutEvent.Display);
            Assert.AreEqual(correlationId, layoutEvent.CorrelationId);
            Assert.AreEqual("CapacityReached", layoutEvent.Reason);
            Assert.AreEqual(
                "AlgorithmicLayout.WindowMovedToDesktop",
                layoutEvent.MessageKey);
            Assert.AreEqual(4, layoutEvent.SourceOccupiedSlots);
            Assert.AreEqual(4, layoutEvent.SourceTotalCapacity);
        }

        [TestMethod]
        public void EventRequiresPairedNonnegativeCapacityAndAllowsTransitionOverflow()
        {
            var display = new Mock<IDisplay>().Object;

            Assert.ThrowsException<ArgumentException>(() => new AlgorithmicLayoutEvent(
                AlgorithmicLayoutEventKind.WindowLeftFloating,
                display,
                "NoDestination",
                "AlgorithmicLayout.WindowLeftFloating",
                sourceOccupiedSlots: 1));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new AlgorithmicLayoutEvent(
                AlgorithmicLayoutEventKind.WindowLeftFloating,
                display,
                "NoDestination",
                "AlgorithmicLayout.WindowLeftFloating",
                sourceOccupiedSlots: -1,
                sourceTotalCapacity: 1));
            var transitionEvent = new AlgorithmicLayoutEvent(
                AlgorithmicLayoutEventKind.WindowLeftFloating,
                display,
                "CapacityTransition",
                "AlgorithmicLayout.WindowLeftFloating",
                sourceOccupiedSlots: 2,
                sourceTotalCapacity: 1);
            Assert.AreEqual(2, transitionEvent.SourceOccupiedSlots);
            Assert.AreEqual(1, transitionEvent.SourceTotalCapacity);
        }

        [TestMethod]
        public void EveryUserFacingEventMessageHasANeutralResource()
        {
            var keys = new[]
            {
                "AlgorithmicLayout.LayoutEnabled",
                "AlgorithmicLayout.LayoutDisabled",
                "AlgorithmicLayout.WindowMovedToDesktop",
                "AlgorithmicLayout.WindowLeftFloating",
                "AlgorithmicLayout.OperationRejected",
                "AlgorithmicLayout.LayoutRecovered",
                "AlgorithmicLayout.TransferFailed",
                "AlgorithmicLayout.TransferCancelled",
                "AlgorithmicLayout.CapacityHint",
                "AlgorithmicLayout.CapacityReachedHint",
                "AlgorithmicLayout.CapacityPerDesktopHint",
                "AlgorithmicLayout.NoCapacityHint",
                "AlgorithmicLayout.DesktopIndex",
                "AlgorithmicLayout.UnknownDesktop",
            };

            foreach (var key in keys)
            {
                Assert.IsFalse(
                    string.IsNullOrWhiteSpace(Strings.ResourceManager.GetString(key)),
                    $"Missing neutral resource: {key}");
            }
        }

        [TestMethod]
        public void CommandRejectionsExposeSpecificSafeUserHints()
        {
            StringAssert.Contains(
                TilingService.GetMasterSatelliteCommandUserHint(
                    "PromoteFocusedWindow",
                    MasterSatelliteFailureReason.MinSizeConflict),
                "selected satellite slot");
            StringAssert.Contains(
                TilingService.GetMasterSatelliteCommandUserHint(
                    "ToggleSatelliteOrientation",
                    MasterSatelliteFailureReason.MinSizeConflict),
                "minimum sizes");
            StringAssert.Contains(
                TilingService.GetMasterSatelliteCommandUserHint(
                    "MouseWindowDrop",
                    MasterSatelliteFailureReason.UnsupportedOperation),
                "drop");
        }

        [TestMethod]
        public void OverlayRejectionKeepsOneSafePresentationPathAndRequestsConfiguredSound()
        {
            const string safeHint =
                "The current master does not fit in the selected satellite slot.";
            var exception = new AlgorithmicLayoutCommandException(
                safeHint,
                TilingError.TargetCannotFit);
            var failure = TilingFailedEventArgs.FromException(exception);
            var diagnosticEvent = new AlgorithmicLayoutEvent(
                AlgorithmicLayoutEventKind.OperationRejected,
                new Mock<IDisplay>().Object,
                "MinSizeConflict",
                "AlgorithmicLayout.OperationRejected.CommandHandled");

            Assert.AreEqual(safeHint, failure.PresentationSafeHint);
            Assert.IsTrue(failure.RequestsFailureSound);

            int visiblePresentationPaths = 1;
            if (AlgorithmicLayoutNotificationFormatter.Format(
                    diagnosticEvent,
                    "Window",
                    _ => "Desktop").ShouldShow)
            {
                visiblePresentationPaths++;
            }
            Assert.AreEqual(1, visiblePresentationPaths);
        }

        [TestMethod]
        public void GenericExceptionMessageIsNeverPromotedToPresentationSafeHint()
        {
            const string internalMessage = "internal path and COM diagnostic";
            var failure = TilingFailedEventArgs.FromException(
                new TilingFailedException(
                    internalMessage,
                    TilingError.TargetCannotFit));

            Assert.IsNull(failure.PresentationSafeHint);
            Assert.IsFalse(failure.RequestsFailureSound);
        }

        [TestMethod]
        public void SuccessfulOverflowNotificationNamesBothDesktopsWithoutFailureSound()
        {
            var display = new Mock<IDisplay>().Object;
            var source = new Mock<IVirtualDesktop>().Object;
            var target = new Mock<IVirtualDesktop>().Object;
            var layoutEvent = new AlgorithmicLayoutEvent(
                AlgorithmicLayoutEventKind.WindowMovedToDesktop,
                display,
                "CapacityReached",
                "AlgorithmicLayout.WindowMovedToDesktop",
                new IntPtr(42),
                "Terminal",
                source,
                target,
                Guid.NewGuid(),
                4,
                4);

            var notification = AlgorithmicLayoutNotificationFormatter.Format(
                layoutEvent,
                "Window",
                desktop => ReferenceEquals(desktop, target) ? "Desktop 2" : "Desktop 1");

            Assert.IsTrue(notification.ShouldShow);
            StringAssert.Contains(notification.Message, "Terminal");
            StringAssert.Contains(notification.Message, "Desktop 2");
            Assert.AreEqual("Desktop 1 reached its 4-window limit.", notification.Hint);
            Assert.IsFalse(notification.PlayFailureSound);
        }

        [TestMethod]
        public void FloatingNotificationIncludesReasonAndCapacityAndRequestsFailureSound()
        {
            var layoutEvent = new AlgorithmicLayoutEvent(
                AlgorithmicLayoutEventKind.WindowLeftFloating,
                new Mock<IDisplay>().Object,
                "NoDestination",
                "AlgorithmicLayout.WindowLeftFloating",
                new IntPtr(43),
                "Visual Studio",
                sourceOccupiedSlots: 4,
                sourceTotalCapacity: 4);

            var notification = AlgorithmicLayoutNotificationFormatter.Format(
                layoutEvent,
                "Window",
                _ => "Desktop 1");

            Assert.AreEqual("Visual Studio left floating", notification.Message);
            StringAssert.Contains(
                notification.Hint,
                "Master + Satellites is full on all available desktops.");
            StringAssert.Contains(notification.Hint, "Capacity: 4 windows per desktop.");
            Assert.IsTrue(notification.PlayFailureSound);
        }

        [TestMethod]
        public void SoundOnFailureSettingGatesOnlyAlgorithmicFailureNotifications()
        {
            var display = new Mock<IDisplay>().Object;
            var target = new Mock<IVirtualDesktop>().Object;
            var success = AlgorithmicLayoutNotificationFormatter.Format(
                new AlgorithmicLayoutEvent(
                    AlgorithmicLayoutEventKind.WindowMovedToDesktop,
                    display,
                    "CapacityReached",
                    "AlgorithmicLayout.WindowMovedToDesktop",
                    targetDesktop: target),
                "Window",
                _ => "Desktop 2");
            var rejection = AlgorithmicLayoutNotificationFormatter.Format(
                new AlgorithmicLayoutEvent(
                    AlgorithmicLayoutEventKind.OperationRejected,
                    display,
                    "MinSizeConflict",
                    "AlgorithmicLayout.OperationRejected"),
                "Window",
                _ => "Desktop 1");
            var fallback = AlgorithmicLayoutNotificationFormatter.Format(
                new AlgorithmicLayoutEvent(
                    AlgorithmicLayoutEventKind.WindowLeftFloating,
                    display,
                    "NoDestination",
                    "AlgorithmicLayout.WindowLeftFloating"),
                "Window",
                _ => "Desktop 1");

            Assert.IsFalse(
                AlgorithmicLayoutNotificationFormatter.ShouldPlayFailureSound(
                    success,
                    soundOnFailure: true));
            Assert.IsFalse(
                AlgorithmicLayoutNotificationFormatter.ShouldPlayFailureSound(
                    success,
                    soundOnFailure: false));
            Assert.IsTrue(
                AlgorithmicLayoutNotificationFormatter.ShouldPlayFailureSound(
                    rejection,
                    soundOnFailure: true));
            Assert.IsFalse(
                AlgorithmicLayoutNotificationFormatter.ShouldPlayFailureSound(
                    rejection,
                    soundOnFailure: false));
            Assert.IsTrue(
                AlgorithmicLayoutNotificationFormatter.ShouldPlayFailureSound(
                    fallback,
                    soundOnFailure: true));
            Assert.IsFalse(
                AlgorithmicLayoutNotificationFormatter.ShouldPlayFailureSound(
                    fallback,
                    soundOnFailure: false));
        }

        [TestMethod]
        public void DiagnosticAndCommandHandledEventsDoNotCreateDuplicateNotifications()
        {
            var display = new Mock<IDisplay>().Object;
            var commandHandled = new AlgorithmicLayoutEvent(
                AlgorithmicLayoutEventKind.OperationRejected,
                display,
                "MinSizeConflict",
                "AlgorithmicLayout.OperationRejected.CommandHandled");
            var transferFailed = new AlgorithmicLayoutEvent(
                AlgorithmicLayoutEventKind.TransferFailed,
                display,
                "MoveFailed",
                "AlgorithmicLayout.TransferFailed");

            Assert.IsFalse(AlgorithmicLayoutNotificationFormatter.Format(
                commandHandled,
                "Window",
                _ => "Desktop").ShouldShow);
            Assert.IsFalse(AlgorithmicLayoutNotificationFormatter.Format(
                transferFailed,
                "Window",
                _ => "Desktop").ShouldShow);
        }
    }
}
