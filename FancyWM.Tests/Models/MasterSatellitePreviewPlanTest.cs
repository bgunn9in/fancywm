using FancyWM.Models;
using FancyWM.Pages.Settings;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Models
{
    [TestClass]
    public class MasterSatellitePreviewPlanTest
    {
        [TestMethod]
        public void CreatesLeftVerticalPreviewFromSettings()
        {
            var plan = MasterSatellitePreviewPlan.Create(
                0.64,
                MasterSide.Left,
                SatelliteLayoutOrientation.Vertical,
                3);

            Assert.AreEqual(0.64, plan.MasterFraction, 0.000001);
            Assert.IsTrue(plan.IsMasterFirst);
            Assert.IsTrue(plan.SatellitesUseRows);
            Assert.AreEqual(3, plan.ConfiguredSatelliteCount);
            Assert.AreEqual(3, plan.VisibleSatelliteCount);
            Assert.IsFalse(plan.IsSatelliteCountTruncated);
        }

        [TestMethod]
        public void CreatesRightHorizontalPreviewAndCapsVisualCount()
        {
            var plan = MasterSatellitePreviewPlan.Create(
                0.72,
                MasterSide.Right,
                SatelliteLayoutOrientation.Horizontal,
                9);

            Assert.IsFalse(plan.IsMasterFirst);
            Assert.IsFalse(plan.SatellitesUseRows);
            Assert.AreEqual(9, plan.ConfiguredSatelliteCount);
            Assert.AreEqual(MasterSatellitePreviewPlan.MaximumVisibleSatellites, plan.VisibleSatelliteCount);
            Assert.IsTrue(plan.IsSatelliteCountTruncated);
        }

        [TestMethod]
        public void InvalidValuesUseTheSameSafeBoundsAsSettings()
        {
            var plan = MasterSatellitePreviewPlan.Create(
                double.NaN,
                (MasterSide)99,
                (SatelliteLayoutOrientation)99,
                100);

            Assert.AreEqual(MasterSatelliteLayoutSettings.DefaultMasterRatio, plan.MasterFraction, 0.000001);
            Assert.IsTrue(plan.IsMasterFirst);
            Assert.IsTrue(plan.SatellitesUseRows);
            Assert.AreEqual(MasterSatelliteLayoutSettings.MaximumMaxSatellites, plan.ConfiguredSatelliteCount);
            Assert.AreEqual(MasterSatellitePreviewPlan.MaximumVisibleSatellites, plan.VisibleSatelliteCount);
        }
    }
}
