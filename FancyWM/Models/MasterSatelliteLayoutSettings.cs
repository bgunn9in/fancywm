using System;

namespace FancyWM.Models
{
    public enum MasterSide
    {
        Left,
        Right,
    }

    public enum SatelliteLayoutOrientation
    {
        Vertical,
        Horizontal,
    }

    public enum MasterSatelliteOverflowPolicy
    {
        FloatOnCurrentDesktop,
        MoveToExistingDesktop,
        MoveToExistingOrCreateDesktop,
    }

    public enum AlgorithmicLayoutDisplayScope
    {
        PrimaryDisplay,
        UltrawideDisplays,
        AllDisplays,
    }

    public record class MasterSatelliteLayoutSettings
    {
        public const double MinimumMasterRatio = 0.50;
        public const double MaximumMasterRatio = 0.80;
        public const double DefaultMasterRatio = 0.60;

        public const int MinimumMaxSatellites = 1;
        public const int MaximumMaxSatellites = 9;
        public const int DefaultMaxSatellites = 3;

        public const int MinimumMaxAutoCreatedDesktops = 0;
        public const int MaximumMaxAutoCreatedDesktops = 9;
        public const int DefaultMaxAutoCreatedDesktops = 1;

        private double m_masterRatio = DefaultMasterRatio;
        private MasterSide m_defaultMasterSide = MasterSide.Left;
        private SatelliteLayoutOrientation m_defaultSatelliteOrientation = SatelliteLayoutOrientation.Vertical;
        private int m_maxSatellites = DefaultMaxSatellites;
        private MasterSatelliteOverflowPolicy m_overflowPolicy = MasterSatelliteOverflowPolicy.MoveToExistingDesktop;
        private AlgorithmicLayoutDisplayScope m_displayScope = AlgorithmicLayoutDisplayScope.PrimaryDisplay;
        private int m_maxAutoCreatedDesktops = DefaultMaxAutoCreatedDesktops;

        public bool Enabled { get; init; } = false;

        public double MasterRatio
        {
            get => m_masterRatio;
            init => m_masterRatio = double.IsFinite(value)
                ? Math.Clamp(value, MinimumMasterRatio, MaximumMasterRatio)
                : DefaultMasterRatio;
        }

        public MasterSide DefaultMasterSide
        {
            get => m_defaultMasterSide;
            init => m_defaultMasterSide = value switch
            {
                MasterSide.Left or MasterSide.Right => value,
                _ => MasterSide.Left,
            };
        }

        public SatelliteLayoutOrientation DefaultSatelliteOrientation
        {
            get => m_defaultSatelliteOrientation;
            init => m_defaultSatelliteOrientation = value switch
            {
                SatelliteLayoutOrientation.Vertical or SatelliteLayoutOrientation.Horizontal => value,
                _ => SatelliteLayoutOrientation.Vertical,
            };
        }

        public int MaxSatellites
        {
            get => m_maxSatellites;
            init => m_maxSatellites = Math.Clamp(value, MinimumMaxSatellites, MaximumMaxSatellites);
        }

        public MasterSatelliteOverflowPolicy OverflowPolicy
        {
            get => m_overflowPolicy;
            init => m_overflowPolicy = value switch
            {
                MasterSatelliteOverflowPolicy.FloatOnCurrentDesktop
                    or MasterSatelliteOverflowPolicy.MoveToExistingDesktop
                    or MasterSatelliteOverflowPolicy.MoveToExistingOrCreateDesktop => value,
                _ => MasterSatelliteOverflowPolicy.MoveToExistingDesktop,
            };
        }

        public AlgorithmicLayoutDisplayScope DisplayScope
        {
            get => m_displayScope;
            init => m_displayScope = value switch
            {
                AlgorithmicLayoutDisplayScope.PrimaryDisplay
                    or AlgorithmicLayoutDisplayScope.UltrawideDisplays
                    or AlgorithmicLayoutDisplayScope.AllDisplays => value,
                _ => AlgorithmicLayoutDisplayScope.PrimaryDisplay,
            };
        }

        public int MaxAutoCreatedDesktops
        {
            get => m_maxAutoCreatedDesktops;
            init => m_maxAutoCreatedDesktops = Math.Clamp(
                value,
                MinimumMaxAutoCreatedDesktops,
                MaximumMaxAutoCreatedDesktops);
        }

        public bool FollowOverflowWindow { get; init; } = false;
    }
}
