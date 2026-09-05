using System.Collections.Generic;
using System.Collections.ObjectModel;

using FancyWM.Models;

using WinMan;

namespace FancyWM.AlgorithmicLayouts
{
    internal sealed class MasterSatelliteRuntimeState
    {
        private readonly List<IWindow> m_satellites;
        private readonly ReadOnlyCollection<IWindow> m_readOnlySatellites;

        public bool IsActive { get; internal set; }

        public MasterSide MasterSide { get; internal set; }

        public double RequestedMasterRatio { get; internal set; }

        public double EffectiveMasterRatio { get; internal set; }

        public SatelliteLayoutOrientation SatelliteOrientation { get; internal set; }

        public IWindow? Master { get; internal set; }

        public IReadOnlyList<IWindow> Satellites => m_readOnlySatellites;

        public long Revision { get; internal set; }

        public bool IsRecovering { get; internal set; }

        internal List<IWindow> MutableSatellites => m_satellites;

        public MasterSatelliteRuntimeState(
            MasterSatelliteLayoutSettings settings,
            bool isActive)
        {
            IsActive = isActive;
            MasterSide = settings.DefaultMasterSide;
            RequestedMasterRatio = settings.MasterRatio;
            SatelliteOrientation = settings.DefaultSatelliteOrientation;
            m_satellites = [];
            m_readOnlySatellites = m_satellites.AsReadOnly();
        }

        private MasterSatelliteRuntimeState(MasterSatelliteRuntimeState state)
        {
            IsActive = state.IsActive;
            MasterSide = state.MasterSide;
            RequestedMasterRatio = state.RequestedMasterRatio;
            EffectiveMasterRatio = state.EffectiveMasterRatio;
            SatelliteOrientation = state.SatelliteOrientation;
            Master = state.Master;
            Revision = state.Revision;
            IsRecovering = state.IsRecovering;
            m_satellites = [.. state.m_satellites];
            m_readOnlySatellites = m_satellites.AsReadOnly();
        }

        internal MasterSatelliteRuntimeState Clone()
        {
            return new MasterSatelliteRuntimeState(this);
        }

        internal void CopyFrom(MasterSatelliteRuntimeState state)
        {
            IsActive = state.IsActive;
            MasterSide = state.MasterSide;
            RequestedMasterRatio = state.RequestedMasterRatio;
            EffectiveMasterRatio = state.EffectiveMasterRatio;
            SatelliteOrientation = state.SatelliteOrientation;
            Master = state.Master;
            Revision = state.Revision;
            IsRecovering = state.IsRecovering;
            m_satellites.Clear();
            m_satellites.AddRange(state.m_satellites);
        }
    }
}
