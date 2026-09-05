using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

using FancyWM.Models;

using WinMan;

namespace FancyWM.AlgorithmicLayouts
{
    internal enum MasterSatelliteWindowRole
    {
        Master,
        Satellite,
    }

    internal enum MasterSatelliteFailureReason
    {
        None,
        Inactive,
        InvalidArgument,
        WindowAlreadyPresent,
        WindowNotFound,
        NotSatellite,
        CapacityReached,
        InvalidSatelliteIndex,
        MinSizeConflict,
        InvalidCanonicalTree,
        RecoveryFailed,
        UnsupportedOperation,
        UnexpectedFailure,
    }

    internal sealed record class MasterSatelliteLayoutSnapshot
    {
        public bool IsActive { get; }

        public MasterSide MasterSide { get; }

        public double RequestedMasterRatio { get; }

        public double EffectiveMasterRatio { get; }

        public SatelliteLayoutOrientation SatelliteOrientation { get; }

        public IWindow? Master { get; }

        public IReadOnlyList<IWindow> Satellites { get; }

        public long Revision { get; }

        public bool IsRecovering { get; }

        public Rectangle WorkArea { get; }

        public string TreeDescription { get; }

        public MasterSatelliteLayoutSnapshot(
            MasterSatelliteRuntimeState state,
            Rectangle workArea,
            string treeDescription)
        {
            ArgumentNullException.ThrowIfNull(state);

            IsActive = state.IsActive;
            MasterSide = state.MasterSide;
            RequestedMasterRatio = state.RequestedMasterRatio;
            EffectiveMasterRatio = state.EffectiveMasterRatio;
            SatelliteOrientation = state.SatelliteOrientation;
            Master = state.Master;
            Satellites = Array.AsReadOnly(state.Satellites.ToArray());
            Revision = state.Revision;
            IsRecovering = state.IsRecovering;
            WorkArea = workArea;
            TreeDescription = treeDescription ?? string.Empty;
        }
    }

    internal sealed record class MasterSatelliteInvariantResult
    {
        public bool IsValid => Violations.Count == 0;

        public IReadOnlyList<string> Violations { get; }

        public string TreeDescription { get; }

        public string Description => IsValid
            ? "The master-satellite invariant is valid."
            : string.Join(Environment.NewLine, Violations);

        public MasterSatelliteInvariantResult(
            IEnumerable<string> violations,
            string treeDescription)
        {
            ArgumentNullException.ThrowIfNull(violations);

            Violations = new ReadOnlyCollection<string>([.. violations]);
            TreeDescription = treeDescription ?? string.Empty;
        }

        public static MasterSatelliteInvariantResult Valid(string treeDescription)
        {
            return new MasterSatelliteInvariantResult([], treeDescription);
        }
    }

    internal sealed record class MasterSatelliteOperationResult(
        bool Succeeded,
        bool Changed,
        MasterSatelliteFailureReason FailureReason,
        string? Message,
        MasterSatelliteLayoutSnapshot Before,
        MasterSatelliteLayoutSnapshot After,
        MasterSatelliteInvariantResult Invariant)
    {
        public static MasterSatelliteOperationResult Success(
            bool changed,
            MasterSatelliteLayoutSnapshot before,
            MasterSatelliteLayoutSnapshot after,
            MasterSatelliteInvariantResult invariant,
            string? message = null)
        {
            return new MasterSatelliteOperationResult(
                true,
                changed,
                MasterSatelliteFailureReason.None,
                message,
                before,
                after,
                invariant);
        }

        public static MasterSatelliteOperationResult Failure(
            MasterSatelliteFailureReason reason,
            string message,
            MasterSatelliteLayoutSnapshot snapshot,
            MasterSatelliteInvariantResult invariant)
        {
            if (reason == MasterSatelliteFailureReason.None)
            {
                throw new ArgumentOutOfRangeException(nameof(reason));
            }

            return new MasterSatelliteOperationResult(
                false,
                false,
                reason,
                message,
                snapshot,
                snapshot,
                invariant);
        }
    }

    internal sealed record class MasterSatellitePlacementResult(
        MasterSatelliteOperationResult Operation,
        MasterSatelliteWindowRole? Role,
        int? SatelliteIndex)
    {
        public bool Succeeded => Operation.Succeeded;

        public bool Changed => Operation.Changed;

        public MasterSatelliteFailureReason FailureReason => Operation.FailureReason;

        public string? Message => Operation.Message;
    }
}
