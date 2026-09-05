using System;
using System.Linq;
using System.Threading.Tasks;

using FancyWM.AlgorithmicLayouts;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    [TestClass]
    public class MasterSatelliteArrangeFailureTest
    {
        [TestMethod]
        public void AlgorithmicFailure_SelectsNewestActualNewWindow()
        {
            var decision = MasterSatelliteArrangeFailurePolicy.Decide(
                algorithmicLayoutActive: true,
                [
                    new(new IntPtr(10), 100, false),
                    new(new IntPtr(20), 50, true),
                    new(new IntPtr(30), 75, true),
                ]);

            Assert.AreEqual(
                ArrangeFailureDisposition.AlgorithmicOverflowCandidate,
                decision.Disposition);
            Assert.AreEqual(new IntPtr(30), decision.CandidateHandle);
        }

        [TestMethod]
        public void AlgorithmicFailure_DoesNotSelectExistingWindow()
        {
            var decision = MasterSatelliteArrangeFailurePolicy.Decide(
                algorithmicLayoutActive: true,
                [
                    new(new IntPtr(10), 100, false),
                    new(new IntPtr(20), 200, false),
                ]);

            Assert.AreEqual(ArrangeFailureDisposition.NoCandidate, decision.Disposition);
            Assert.IsFalse(decision.HasCandidate);
        }

        [TestMethod]
        public void LegacyFailure_PreservesNewestGenerationFallback()
        {
            var decision = MasterSatelliteArrangeFailurePolicy.Decide(
                algorithmicLayoutActive: false,
                [
                    new(new IntPtr(10), 100, false),
                    new(new IntPtr(20), 200, false),
                ]);

            Assert.AreEqual(
                ArrangeFailureDisposition.LegacyFloatingCandidate,
                decision.Disposition);
            Assert.AreEqual(new IntPtr(20), decision.CandidateHandle);
        }

        [TestMethod]
        public void NotificationTracker_DeduplicatesUntilHandleLeavesTree()
        {
            var tracker = new ArrangeFailureNotificationTracker();
            var handle = new IntPtr(42);

            Assert.IsTrue(tracker.TryMark(handle));
            Assert.IsFalse(tracker.TryMark(handle));

            tracker.ReleaseResolved([handle]);
            Assert.IsFalse(tracker.TryMark(handle));

            tracker.ReleaseResolved([]);
            Assert.IsTrue(tracker.TryMark(handle));
        }

        [TestMethod]
        public void NotificationTracker_ForgetAllowsReusedHandle()
        {
            var tracker = new ArrangeFailureNotificationTracker();
            var handle = new IntPtr(42);

            Assert.IsTrue(tracker.TryMark(handle));
            tracker.Forget(handle);

            Assert.IsTrue(tracker.TryMark(handle));
        }

        [TestMethod]
        public void NotificationTracker_ConcurrentTicksPublishOnlyOnce()
        {
            var tracker = new ArrangeFailureNotificationTracker();
            var handle = new IntPtr(42);
            var accepted = new bool[32];

            Parallel.For(0, accepted.Length, index =>
                accepted[index] = tracker.TryMark(handle));

            Assert.AreEqual(1, accepted.Count(value => value));
        }
    }
}
