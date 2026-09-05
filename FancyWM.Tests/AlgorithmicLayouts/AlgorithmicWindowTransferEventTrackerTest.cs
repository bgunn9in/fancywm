#nullable enable

using System;
using System.Windows.Threading;

using FancyWM.AlgorithmicLayouts;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    [TestClass]
    public class AlgorithmicWindowTransferEventTrackerTest
    {
        [TestMethod]
        public void CachedHandleSurvivesZeroAndInvalidWindowReads()
        {
            var tracker = CreateTracker();
            var window = new Mock<IWindow>(MockBehavior.Loose);
            var currentHandle = new IntPtr(101);
            Exception? handleException = null;
            window.SetupGet(item => item.Handle).Returns(() =>
            {
                if (handleException != null)
                {
                    throw handleException;
                }
                return currentHandle;
            });

            Assert.IsTrue(tracker.TryGetStableWindowHandle(window.Object, out var live));
            currentHandle = IntPtr.Zero;
            Assert.IsTrue(tracker.TryGetStableWindowHandle(window.Object, out var zero));
            handleException = new InvalidWindowReferenceException(new IntPtr(101));
            Assert.IsTrue(tracker.TryGetStableWindowHandle(window.Object, out var invalid));

            Assert.AreEqual(new IntPtr(101), live);
            Assert.AreEqual(live, zero);
            Assert.AreEqual(live, invalid);
            window.VerifyGet(item => item.Handle, Times.Once);
        }

        [TestMethod]
        public void DistinctEqualWrappersDoNotShareStableHandle()
        {
            var tracker = CreateTracker();
            var first = new Mock<IWindow>(MockBehavior.Loose);
            var second = new Mock<IWindow>(MockBehavior.Loose);
            first.SetupGet(item => item.Handle).Returns(new IntPtr(201));
            second.SetupGet(item => item.Handle).Returns(IntPtr.Zero);
            first.Setup(item => item.Equals(second.Object)).Returns(true);
            second.Setup(item => item.Equals(first.Object)).Returns(true);

            Assert.IsTrue(tracker.TryGetStableWindowHandle(first.Object, out var firstHandle));
            Assert.IsFalse(tracker.TryGetStableWindowHandle(second.Object, out var secondHandle));

            Assert.AreEqual(new IntPtr(201), firstHandle);
            Assert.AreEqual(IntPtr.Zero, secondHandle);
        }

        [TestMethod]
        public void LatestWrapperCanBeResolvedByStableHandle()
        {
            var tracker = CreateTracker();
            var first = CreateWindow(251);
            var replacement = CreateWindow(251);

            Assert.IsTrue(tracker.TryGetStableWindowHandle(first, out var handle));
            Assert.IsTrue(tracker.TryGetWindow(handle, out var firstResolved));
            Assert.AreSame(first, firstResolved);

            Assert.IsTrue(tracker.TryGetStableWindowHandle(replacement, out _));
            Assert.IsTrue(tracker.TryGetWindow(handle, out var replacementResolved));
            Assert.AreSame(replacement, replacementResolved);

            Assert.IsTrue(tracker.Forget(first));
            Assert.IsTrue(tracker.TryGetWindow(handle, out var retainedReplacement));
            Assert.AreSame(replacement, retainedReplacement);
            Assert.IsTrue(tracker.Forget(handle));
            Assert.IsFalse(tracker.TryGetWindow(handle, out _));
        }

        [TestMethod]
        public void RememberedHandleLookupDoesNotReclaimReusedHandleGeneration()
        {
            var tracker = CreateTracker();
            var oldWindow = CreateWindow(252);
            var replacement = CreateWindow(252);
            var source = CreateDesktop("Source");
            var target = CreateDesktop("Target");

            Assert.IsTrue(tracker.TryGetStableWindowHandle(oldWindow, out var handle));
            tracker.RememberDesktop(handle, source);
            Assert.IsTrue(tracker.ObservePotentialManualMove(
                handle,
                target,
                out _));
            Assert.IsTrue(tracker.TryGetStableWindowHandle(
                replacement,
                out _,
                out var replacedGeneration));
            Assert.IsTrue(replacedGeneration);
            Assert.IsFalse(tracker.TryGetKnownDesktop(handle, out _));
            Assert.IsFalse(tracker.TryGetManualMove(handle, target, out _));

            Assert.IsTrue(tracker.TryGetRememberedWindowHandle(
                oldWindow,
                out var remembered));
            Assert.AreEqual(handle, remembered);
            Assert.IsTrue(tracker.TryGetStableWindowHandle(oldWindow, out _));
            Assert.IsTrue(tracker.TryGetWindow(handle, out var current));
            Assert.AreSame(replacement, current);
        }

        [TestMethod]
        public void RemovedThenAddedManualMarkerIsConsumedOnce()
        {
            var tracker = CreateTracker();
            var source = CreateDesktop("Source");
            var target = CreateDesktop("Target");
            var handle = new IntPtr(301);
            tracker.RememberDesktop(handle, source);

            Assert.IsTrue(tracker.ObservePotentialManualMove(handle, target, out var recorded));
            Assert.AreSame(source, recorded.SourceDesktop);
            Assert.AreSame(target, recorded.TargetDesktop);
            Assert.IsTrue(tracker.TryConsumeManualMove(handle, target, out var consumed));
            Assert.AreSame(recorded, consumed);
            Assert.IsFalse(tracker.TryConsumeManualMove(handle, target, out _));
            Assert.IsTrue(tracker.TryGetKnownDesktop(handle, out var known));
            Assert.AreSame(target, known);
        }

        [TestMethod]
        public void AddedThenRemovedDuplicateObservationDoesNotRecreateConsumedMarker()
        {
            var tracker = CreateTracker();
            var source = CreateDesktop("Source");
            var target = CreateDesktop("Target");
            var handle = new IntPtr(401);
            tracker.RememberDesktop(handle, source);

            Assert.IsTrue(tracker.ObservePotentialManualMove(handle, target, out _));
            Assert.IsTrue(tracker.TryConsumeManualMove(handle, target, out _));
            Assert.IsFalse(tracker.ObservePotentialManualMove(handle, target, out _));
            Assert.IsFalse(tracker.TryConsumeManualMove(handle, target, out _));
        }

        [TestMethod]
        public void PeekingManualMarkerDoesNotConsumeIt()
        {
            var time = new ManualTimeProvider(
                new DateTimeOffset(2026, 4, 9, 12, 0, 0, TimeSpan.Zero));
            var tracker = new AlgorithmicWindowTransferEventTracker(
                Dispatcher.CurrentDispatcher,
                time);
            var source = CreateDesktop("Source");
            var target = CreateDesktop("Target");
            var window = CreateWindow(123);
            Assert.IsTrue(tracker.TryGetStableWindowHandle(window, out var handle));
            tracker.RememberDesktop(handle, source);

            Assert.IsTrue(tracker.ObservePotentialManualMove(handle, target, out var recorded));
            Assert.IsTrue(tracker.TryGetManualMove(handle, target, out var peeked));
            Assert.AreEqual(recorded, peeked);
            Assert.IsTrue(tracker.TryGetManualMove(handle, target, out _));
            Assert.IsTrue(tracker.TryConsumeManualMove(handle, target, out var consumed));
            Assert.AreEqual(recorded, consumed);
            Assert.IsFalse(tracker.TryGetManualMove(handle, target, out _));
        }

        [TestMethod]
        public void SameDesktopObservationDoesNotCreateMarker()
        {
            var tracker = CreateTracker();
            var desktop = CreateDesktop("Same");
            var handle = new IntPtr(501);
            tracker.RememberDesktop(handle, desktop);

            Assert.IsFalse(tracker.ObservePotentialManualMove(handle, desktop, out _));
            Assert.IsFalse(tracker.RecordManualMove(handle, desktop, desktop, out _));
            Assert.IsFalse(tracker.TryConsumeManualMove(handle, desktop, out _));
        }

        [TestMethod]
        public void ManualMarkerExpiresAfterThirtySeconds()
        {
            var time = new ManualTimeProvider(
                new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));
            var tracker = CreateTracker(time);
            var source = CreateDesktop("Source");
            var target = CreateDesktop("Target");
            var handle = new IntPtr(601);
            tracker.RememberDesktop(handle, source);
            Assert.IsTrue(tracker.ObservePotentialManualMove(handle, target, out var marker));
            Assert.AreEqual(time.GetUtcNow().AddSeconds(30), marker.Deadline);

            time.Advance(TimeSpan.FromSeconds(29));
            Assert.AreEqual(0, tracker.CleanupExpired());
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.AreEqual(1, tracker.CleanupExpired());
            Assert.IsFalse(tracker.TryConsumeManualMove(handle, target, out _));
        }

        [TestMethod]
        public void ForgetRemovesHandleDesktopAndMarkerState()
        {
            var tracker = CreateTracker();
            var source = CreateDesktop("Source");
            var target = CreateDesktop("Target");
            var handleValue = new IntPtr(701);
            var currentHandle = handleValue;
            var window = new Mock<IWindow>(MockBehavior.Loose);
            window.SetupGet(item => item.Handle).Returns(() => currentHandle);
            Assert.IsTrue(tracker.TryGetStableWindowHandle(window.Object, out var handle));
            tracker.RememberDesktop(handle, source);
            Assert.IsTrue(tracker.ObservePotentialManualMove(handle, target, out _));

            Assert.IsTrue(tracker.Forget(window.Object));
            currentHandle = IntPtr.Zero;
            Assert.IsFalse(tracker.TryGetStableWindowHandle(window.Object, out _));
            Assert.IsFalse(tracker.TryGetKnownDesktop(handle, out _));
            Assert.IsFalse(tracker.TryConsumeManualMove(handle, target, out _));
        }

        [TestMethod]
        public void DestroyedGenerationDoesNotLeakStateToReusedHandle()
        {
            var tracker = CreateTracker();
            var source = CreateDesktop("Source");
            var target = CreateDesktop("Target");
            var oldWindow = CreateWindow(751);
            var newWindow = CreateWindow(751);
            Assert.IsTrue(tracker.TryGetStableWindowHandle(oldWindow, out var handle));
            tracker.RememberDesktop(handle, source);
            Assert.IsTrue(tracker.ObservePotentialManualMove(handle, target, out _));

            Assert.IsTrue(tracker.Forget(handle));
            Assert.IsTrue(tracker.TryGetStableWindowHandle(newWindow, out var reused));

            Assert.AreEqual(handle, reused);
            Assert.IsFalse(tracker.TryGetKnownDesktop(reused, out _));
            Assert.IsFalse(tracker.TryGetManualMove(reused, target, out _));
        }

        [TestMethod]
        public void ClearRemovesAllTrackedState()
        {
            var tracker = CreateTracker();
            var source = CreateDesktop("Source");
            var target = CreateDesktop("Target");
            var first = CreateWindow(801);
            var second = CreateWindow(802);
            Assert.IsTrue(tracker.TryGetStableWindowHandle(first, out var firstHandle));
            Assert.IsTrue(tracker.TryGetStableWindowHandle(second, out var secondHandle));
            tracker.RememberDesktop(firstHandle, source);
            tracker.RememberDesktop(secondHandle, source);
            tracker.ObservePotentialManualMove(firstHandle, target, out _);

            tracker.Clear();

            Assert.IsFalse(tracker.TryGetKnownDesktop(firstHandle, out _));
            Assert.IsFalse(tracker.TryGetKnownDesktop(secondHandle, out _));
            Assert.IsFalse(tracker.TryConsumeManualMove(firstHandle, target, out _));
            Assert.IsTrue(tracker.Forget(firstHandle) is false);
        }

        private static AlgorithmicWindowTransferEventTracker CreateTracker(
            TimeProvider? timeProvider = null)
        {
            return new AlgorithmicWindowTransferEventTracker(
                Dispatcher.CurrentDispatcher,
                timeProvider);
        }

        private static IVirtualDesktop CreateDesktop(string name)
        {
            var desktop = new Mock<IVirtualDesktop>(MockBehavior.Loose);
            desktop.SetupGet(item => item.Name).Returns(name);
            return desktop.Object;
        }

        private static IWindow CreateWindow(long handle)
        {
            var window = new Mock<IWindow>(MockBehavior.Loose);
            window.SetupGet(item => item.Handle).Returns(new IntPtr(handle));
            return window.Object;
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

            public void Advance(TimeSpan elapsed)
            {
                m_now += elapsed;
            }
        }
    }
}
