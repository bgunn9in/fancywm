using System;

using WinMan;

namespace FancyWM.AlgorithmicLayouts
{
    internal enum ReservedRole
    {
        Master,
        Satellite,
    }

    internal enum PendingWindowTransferState
    {
        Planned,
        Reserved,
        Moving,
        DestinationObserved,
        Committed,
        Failed,
        Cancelled,
    }

    /// <summary>
    /// Immutable coordinator intent used to correlate the source and destination
    /// workspace events emitted by one cross-desktop window move.
    /// </summary>
    internal sealed record PendingWindowTransfer
    {
        public required Guid CorrelationId { get; init; }

        public required IntPtr WindowHandle { get; init; }

        public required IVirtualDesktop SourceDesktop { get; init; }

        public required IDisplay SourceDisplay { get; init; }

        public Rectangle? SourceOriginalPosition { get; init; }

        public required IVirtualDesktop TargetDesktop { get; init; }

        public required IDisplay TargetDisplay { get; init; }

        public required ReservedRole TargetRole { get; init; }

        public int? TargetSatelliteIndex { get; init; }

        public required DateTimeOffset CreatedAt { get; init; }

        public required DateTimeOffset UpdatedAt { get; init; }

        public required DateTimeOffset Deadline { get; init; }

        public required PendingWindowTransferState State { get; init; }

        public Guid? ReservationId { get; init; }

        public bool SourceRemovedObserved { get; init; }

        public bool DestinationAddedObserved { get; init; }

        public string? TerminalReason { get; init; }

        public bool IsTerminal => State is PendingWindowTransferState.Committed
            or PendingWindowTransferState.Failed
            or PendingWindowTransferState.Cancelled;
    }

    internal sealed record AlgorithmicSlotReservation
    {
        public required Guid ReservationId { get; init; }

        public required Guid CorrelationId { get; init; }

        public required IntPtr WindowHandle { get; init; }

        public required LayoutStateKey LayoutKey { get; init; }

        public required ReservedRole Role { get; init; }

        public int? SatelliteIndex { get; init; }

        public required DateTimeOffset CreatedAt { get; init; }

        public required DateTimeOffset Deadline { get; init; }

        public MasterSatelliteReservedSlot ToWorkspaceSlot()
        {
            return new MasterSatelliteReservedSlot(
                ReservationId,
                LayoutKey,
                Role == ReservedRole.Master
                    ? MasterSatelliteWindowRole.Master
                    : MasterSatelliteWindowRole.Satellite,
                SatelliteIndex);
        }
    }

    internal enum AlgorithmicReservationReadinessDisposition
    {
        Ready,
        AwaitingPrecedingSlot,
        Missing,
        DestinationUnavailable,
        SlotAlreadyOccupied,
    }

    internal sealed record AlgorithmicReservationReadiness(
        AlgorithmicReservationReadinessDisposition Disposition,
        string DiagnosticReason)
    {
        public bool IsReady =>
            Disposition == AlgorithmicReservationReadinessDisposition.Ready;

        public bool IsWaiting =>
            Disposition == AlgorithmicReservationReadinessDisposition.AwaitingPrecedingSlot;
    }

    internal sealed record CoordinatorCapacitySnapshot(
        LayoutStateKey LayoutKey,
        WorkspaceLayoutKind LayoutKind,
        bool CanAcceptWindow,
        ReservedRole? NextRole,
        int? NextSatelliteIndex,
        int OccupiedSlots,
        int ReservedSlots,
        int TotalCapacity,
        long Revision,
        string? DiagnosticReason);
}
