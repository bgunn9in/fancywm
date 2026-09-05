using System;
using System.Collections.Generic;
using System.Linq;

using WinMan;

namespace FancyWM.AlgorithmicLayouts
{
    internal enum AlgorithmicDestinationCandidateDisposition
    {
        Dead,
        CapacityUnavailable,
        UnsupportedLayout,
        Full,
        PlacementRejected,
        ReservationRejected,
        Reserved,
    }

    internal enum AlgorithmicDestinationSearchDisposition
    {
        Reserved,
        ExistingReservation,
        NoDestination,
        SourceDesktopNotFound,
        TransferConflict,
    }

    internal sealed record AlgorithmicDestinationCandidate(
        IVirtualDesktop Desktop,
        LayoutStateKey LayoutKey,
        AlgorithmicDestinationCandidateDisposition Disposition,
        CoordinatorCapacitySnapshot? Capacity,
        string DiagnosticReason);

    internal sealed record AlgorithmicDestinationPreflightResult(
        bool Succeeded,
        string DiagnosticReason)
    {
        public static AlgorithmicDestinationPreflightResult Accept()
        {
            return new AlgorithmicDestinationPreflightResult(true, "The destination placement preflight succeeded.");
        }

        public static AlgorithmicDestinationPreflightResult Reject(string diagnosticReason)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticReason);
            return new AlgorithmicDestinationPreflightResult(false, diagnosticReason);
        }
    }

    internal sealed record AlgorithmicDestinationSearchResult(
        AlgorithmicDestinationSearchDisposition Disposition,
        PendingWindowTransfer? Transfer,
        IReadOnlyList<AlgorithmicDestinationCandidate> ExaminedCandidates,
        string DiagnosticReason)
    {
        public bool Succeeded => Disposition is AlgorithmicDestinationSearchDisposition.Reserved
            or AlgorithmicDestinationSearchDisposition.ExistingReservation;
    }

    internal sealed partial class AlgorithmicLayoutCoordinator
    {
        /// <summary>
        /// Examines one immutable virtual-desktop snapshot in next-first cyclic
        /// order and atomically reserves the first eligible destination. It does
        /// not create desktops, move windows, or call a service participant.
        /// </summary>
        public AlgorithmicDestinationSearchResult FindDestinationAndReserve(
            Guid correlationId,
            IntPtr windowHandle,
            IVirtualDesktop sourceDesktop,
            IDisplay display,
            Rectangle? sourceOriginalPosition = null,
            Func<LayoutStateKey, CoordinatorCapacitySnapshot,
                AlgorithmicDestinationPreflightResult>? placementPreflight = null)
        {
            ArgumentNullException.ThrowIfNull(sourceDesktop);
            ArgumentNullException.ThrowIfNull(display);
            VerifyAccessAndNotDisposed();
            ValidateTransferIdentity(correlationId, windowHandle);

            CleanupExpired();
            if (TryGetTransfer(correlationId, out var existing))
            {
                if (existing.WindowHandle == windowHandle
                    && MasterSatelliteDisplayEligibility.DesktopsMatch(
                        existing.SourceDesktop,
                        sourceDesktop)
                    && MasterSatelliteDisplayEligibility.DisplaysMatch(
                        existing.SourceDisplay,
                        display)
                    && MasterSatelliteDisplayEligibility.DisplaysMatch(
                        existing.TargetDisplay,
                        display)
                    && !existing.IsTerminal)
                {
                    if (TryReserve(correlationId, windowHandle, out var reserved)
                        && reserved.ReservationId is Guid reservationId
                        && TryGetReservation(reservationId, out var reservation)
                        && reservation.CorrelationId == correlationId
                        && reservation.WindowHandle == windowHandle)
                    {
                        return Result(
                            AlgorithmicDestinationSearchDisposition.ExistingReservation,
                            reserved,
                            [],
                            "The existing correlated destination reservation was reused.");
                    }

                    return Result(
                        AlgorithmicDestinationSearchDisposition.TransferConflict,
                        null,
                        [],
                        "The existing transfer has no live destination reservation.");
                }

                return Result(
                    AlgorithmicDestinationSearchDisposition.TransferConflict,
                    null,
                    [],
                    "The correlation identity already belongs to a different or terminal transfer.");
            }
            if (TryGetRecentTransfer(windowHandle, out var blockingTransfer))
            {
                return Result(
                    AlgorithmicDestinationSearchDisposition.TransferConflict,
                    null,
                    [],
                    blockingTransfer.IsTerminal
                        ? "The window handle is retained by a recently completed transfer."
                        : "The window already has an active transfer.");
            }

            IVirtualDesktopManager virtualDesktopManager;
            try
            {
                virtualDesktopManager = m_workspace.VirtualDesktopManager;
            }
            catch (Exception exception)
            {
                return ExternalBoundaryUnavailable(
                    "The virtual desktop manager could not be obtained",
                    exception);
            }
            if (virtualDesktopManager == null)
            {
                return Result(
                    AlgorithmicDestinationSearchDisposition.NoDestination,
                    null,
                    [],
                    "The virtual desktop manager is unavailable.");
            }

            bool canManageVirtualDesktops;
            try
            {
                canManageVirtualDesktops =
                    virtualDesktopManager.CanManageVirtualDesktops;
            }
            catch (Exception exception)
            {
                return ExternalBoundaryUnavailable(
                    "Virtual desktop capability could not be determined",
                    exception);
            }
            if (!canManageVirtualDesktops)
            {
                return Result(
                    AlgorithmicDestinationSearchDisposition.NoDestination,
                    null,
                    [],
                    "Virtual desktop manipulation is unavailable in this environment.");
            }

            // IWorkspace is an external boundary. Capture its desktop list while
            // no coordinator mutation lock is held, then use only this snapshot.
            IVirtualDesktop[] desktops;
            try
            {
                desktops = virtualDesktopManager.Desktops.ToArray();
            }
            catch (Exception exception)
            {
                return ExternalBoundaryUnavailable(
                    "The virtual desktop snapshot could not be captured",
                    exception);
            }

            int sourceIndex = IndexOfDesktop(desktops, sourceDesktop);
            if (sourceIndex < 0)
            {
                return Result(
                    AlgorithmicDestinationSearchDisposition.SourceDesktopNotFound,
                    null,
                    [],
                    "The source desktop is not present in the virtual-desktop snapshot.");
            }

            var examined = new List<AlgorithmicDestinationCandidate>(
                Math.Max(0, desktops.Length - 1));
            for (int offset = 1; offset < desktops.Length; offset++)
            {
                var candidate = desktops[(sourceIndex + offset) % desktops.Length];
                var layoutKey = new LayoutStateKey(candidate, display);
                bool isAlive;
                try
                {
                    isAlive = candidate.IsAlive;
                }
                catch (Exception exception)
                {
                    examined.Add(Candidate(
                        candidate,
                        layoutKey,
                        AlgorithmicDestinationCandidateDisposition.Dead,
                        null,
                        $"The desktop availability check failed ({exception.GetType().Name})."));
                    continue;
                }
                if (!isAlive)
                {
                    examined.Add(Candidate(
                        candidate,
                        layoutKey,
                        AlgorithmicDestinationCandidateDisposition.Dead,
                        null,
                        "The desktop is no longer alive."));
                    continue;
                }
                if (!TryGetCapacity(layoutKey, out var capacity))
                {
                    examined.Add(Candidate(
                        candidate,
                        layoutKey,
                        AlgorithmicDestinationCandidateDisposition.CapacityUnavailable,
                        null,
                        "No coordinator capacity snapshot is available for this desktop and display."));
                    continue;
                }
                if (capacity.LayoutKind is not (WorkspaceLayoutKind.Empty
                    or WorkspaceLayoutKind.Canonical))
                {
                    examined.Add(Candidate(
                        candidate,
                        layoutKey,
                        AlgorithmicDestinationCandidateDisposition.UnsupportedLayout,
                        capacity,
                        capacity.DiagnosticReason
                            ?? "Only empty or canonical layouts can accept an algorithmic transfer."));
                    continue;
                }
                if (!capacity.CanAcceptWindow)
                {
                    examined.Add(Candidate(
                        candidate,
                        layoutKey,
                        AlgorithmicDestinationCandidateDisposition.Full,
                        capacity,
                        capacity.DiagnosticReason
                            ?? "All physical and reserved layout slots are occupied."));
                    continue;
                }
                if (placementPreflight != null)
                {
                    // The coordinator mutation lock is not held while consulting
                    // the display endpoint. The endpoint may therefore take its
                    // backend lock and run the clone-only min-size preflight.
                    var preflight = placementPreflight(layoutKey, capacity)
                        ?? throw new InvalidOperationException(
                            "The destination placement preflight returned no result.");
                    if (!preflight.Succeeded)
                    {
                        examined.Add(Candidate(
                            candidate,
                            layoutKey,
                            AlgorithmicDestinationCandidateDisposition.PlacementRejected,
                            capacity,
                            preflight.DiagnosticReason));
                        continue;
                    }
                }
                if (!TryPlanAndReserve(
                    correlationId,
                    windowHandle,
                    sourceDesktop,
                    display,
                    layoutKey,
                    sourceOriginalPosition,
                    out var transfer))
                {
                    examined.Add(Candidate(
                        candidate,
                        layoutKey,
                        AlgorithmicDestinationCandidateDisposition.ReservationRejected,
                        capacity,
                        "The destination changed or the atomic reservation was rejected."));
                    continue;
                }

                examined.Add(Candidate(
                    candidate,
                    layoutKey,
                    AlgorithmicDestinationCandidateDisposition.Reserved,
                    capacity,
                    "The destination slot was reserved."));
                return Result(
                    AlgorithmicDestinationSearchDisposition.Reserved,
                    transfer,
                    examined,
                    "A destination slot was reserved.");
            }

            return Result(
                AlgorithmicDestinationSearchDisposition.NoDestination,
                null,
                examined,
                desktops.Length <= 1
                    ? "No desktop other than the source exists."
                    : "No alive empty or canonical destination has available capacity on this display.");
        }

        private static AlgorithmicDestinationSearchResult ExternalBoundaryUnavailable(
            string diagnosticReason,
            Exception exception)
        {
            return Result(
                AlgorithmicDestinationSearchDisposition.NoDestination,
                null,
                [],
                $"{diagnosticReason} ({exception.GetType().Name}).");
        }

        private static int IndexOfDesktop(
            IReadOnlyList<IVirtualDesktop> desktops,
            IVirtualDesktop desktop)
        {
            for (int i = 0; i < desktops.Count; i++)
            {
                if (MasterSatelliteDisplayEligibility.DesktopsMatch(desktops[i], desktop))
                {
                    return i;
                }
            }
            return -1;
        }

        private static AlgorithmicDestinationCandidate Candidate(
            IVirtualDesktop desktop,
            LayoutStateKey layoutKey,
            AlgorithmicDestinationCandidateDisposition disposition,
            CoordinatorCapacitySnapshot? capacity,
            string diagnosticReason)
        {
            return new AlgorithmicDestinationCandidate(
                desktop,
                layoutKey,
                disposition,
                capacity,
                diagnosticReason);
        }

        private static AlgorithmicDestinationSearchResult Result(
            AlgorithmicDestinationSearchDisposition disposition,
            PendingWindowTransfer? transfer,
            IEnumerable<AlgorithmicDestinationCandidate> examined,
            string diagnosticReason)
        {
            return new AlgorithmicDestinationSearchResult(
                disposition,
                transfer,
                Array.AsReadOnly(examined.ToArray()),
                diagnosticReason);
        }
    }
}
