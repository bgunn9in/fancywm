using System;

using FancyWM.Models;

namespace FancyWM.Pages.Settings
{
    /// <summary>
    /// Describes the lightweight settings preview. It deliberately has no
    /// dependency on the production layout tree or layout engine.
    /// </summary>
    internal readonly record struct MasterSatellitePreviewPlan(
        double MasterFraction,
        bool IsMasterFirst,
        bool SatellitesUseRows,
        int ConfiguredSatelliteCount,
        int VisibleSatelliteCount)
    {
        internal const int MaximumVisibleSatellites = 6;

        internal bool IsSatelliteCountTruncated
            => VisibleSatelliteCount < ConfiguredSatelliteCount;

        internal static MasterSatellitePreviewPlan Create(
            double masterRatio,
            MasterSide masterSide,
            SatelliteLayoutOrientation satelliteOrientation,
            int maxSatellites)
        {
            var ratio = double.IsFinite(masterRatio)
                ? Math.Clamp(
                    masterRatio,
                    MasterSatelliteLayoutSettings.MinimumMasterRatio,
                    MasterSatelliteLayoutSettings.MaximumMasterRatio)
                : MasterSatelliteLayoutSettings.DefaultMasterRatio;
            var configuredSatelliteCount = Math.Clamp(
                maxSatellites,
                MasterSatelliteLayoutSettings.MinimumMaxSatellites,
                MasterSatelliteLayoutSettings.MaximumMaxSatellites);

            return new MasterSatellitePreviewPlan(
                ratio,
                masterSide != MasterSide.Right,
                satelliteOrientation != SatelliteLayoutOrientation.Horizontal,
                configuredSatelliteCount,
                Math.Min(configuredSatelliteCount, MaximumVisibleSatellites));
        }
    }
}
