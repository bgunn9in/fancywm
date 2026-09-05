using System;
using System.Collections.Generic;

using WinMan;

namespace FancyWM.AlgorithmicLayouts
{
    internal enum AlgorithmicTransferStartDisposition
    {
        MoveRequested,
        NoDestination,
        TransferConflict,
        MoveFailed,
    }

    internal sealed record AlgorithmicTransferStartResult(
        AlgorithmicTransferStartDisposition Disposition,
        AlgorithmicDestinationSearchResult Search,
        PendingWindowTransfer? Transfer,
        Exception? MoveException,
        bool RollbackAttempted,
        bool RollbackSucceeded,
        Exception? RollbackException)
    {
        public bool MoveRequested => Disposition == AlgorithmicTransferStartDisposition.MoveRequested;

        /// <summary>
        /// Describes the one-shot recovery performed after MoveWindow threw. It
        /// also carries the deduplicated notification decision for the caller.
        /// </summary>
        public AlgorithmicTransferRecoveryResult? Recovery { get; init; }
    }

    internal sealed record AlgorithmicTransferMaterializationResult(
        bool Succeeded,
        string DiagnosticReason)
    {
        public static AlgorithmicTransferMaterializationResult Accept()
        {
            return new AlgorithmicTransferMaterializationResult(
                true,
                "The exact reserved destination slot was materialized.");
        }

        public static AlgorithmicTransferMaterializationResult Reject(string diagnosticReason)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticReason);
            return new AlgorithmicTransferMaterializationResult(false, diagnosticReason);
        }
    }

    internal enum AlgorithmicTransferArrivalDisposition
    {
        NotCorrelated,
        WrongDestination,
        AlreadyTerminal,
        AwaitingPrecedingSlot,
        Materialized,
        MaterializationFailed,
        StateTransitionRejected,
    }

    internal sealed record AlgorithmicTransferArrivalResult(
        AlgorithmicTransferArrivalDisposition Disposition,
        PendingWindowTransfer? Transfer,
        string DiagnosticReason,
        Exception? Exception = null)
    {
        public bool Consumed => Disposition is AlgorithmicTransferArrivalDisposition.AlreadyTerminal
            or AlgorithmicTransferArrivalDisposition.AwaitingPrecedingSlot
            or AlgorithmicTransferArrivalDisposition.Materialized
            or AlgorithmicTransferArrivalDisposition.MaterializationFailed
            or AlgorithmicTransferArrivalDisposition.StateTransitionRejected;
    }

    internal enum AlgorithmicTransferRecoveryDisposition
    {
        TransferNotFound,
        NotTerminal,
        Committed,
        RecoveryInProgress,
        WindowClosed,
        PreservedOnSelectedDesktop,
        SourceUnavailable,
        AlreadyOnSource,
        RolledBackToSource,
        RollbackFailed,
    }

    /// <summary>
    /// Result of consuming a failed/cancelled transfer exactly once. Moving the
    /// window is the orchestrator's responsibility; marking it floating and
    /// presenting a failure notification remain explicit TilingService actions.
    /// </summary>
    internal sealed record AlgorithmicTransferRecoveryResult(
        AlgorithmicTransferRecoveryDisposition Disposition,
        PendingWindowTransfer? Transfer,
        bool RollbackAttempted,
        bool RollbackSucceeded,
        bool RequiresFloating,
        IVirtualDesktop? FloatingDesktop,
        bool ShouldNotifyFailure,
        bool IsDuplicate,
        Exception? Exception = null);

    internal sealed record AlgorithmicTransferFollowResult(
        bool Requested,
        bool Switched,
        string DiagnosticReason,
        Exception? Exception = null);

    internal enum AlgorithmicTransferReconciliationDisposition
    {
        TargetNotObserved,
        DestinationConsumed,
        DestinationNotConsumed,
        AlreadyTerminal,
        OwnershipProbeFailed,
        DestinationCallbackFailed,
    }

    internal sealed record AlgorithmicTransferReconciliationResult(
        AlgorithmicTransferReconciliationDisposition Disposition,
        PendingWindowTransfer Transfer,
        bool TargetOwnershipConfirmed,
        bool SourceRemovalObserved,
        string DiagnosticReason,
        Exception? Exception = null)
    {
        public bool ShouldRetry => Disposition is
            AlgorithmicTransferReconciliationDisposition.TargetNotObserved
            or AlgorithmicTransferReconciliationDisposition.OwnershipProbeFailed;
    }

    /// <summary>
    /// Executes the external part of a correlated transfer. Coordinator calls are
    /// completed before invoking endpoint callbacks or IVirtualDesktop methods, so
    /// no coordinator mutation lock is held across a backend or OS boundary.
    /// The owning TilingService must call these methods without its internal locks.
    /// </summary>
    internal sealed class AlgorithmicWindowTransferOrchestrator
    {
        private const int MaximumRetainedRecoveries = 256;

        private readonly AlgorithmicLayoutCoordinator m_coordinator;
        private readonly IDisplay m_display;
        private readonly Dictionary<Guid, AlgorithmicTransferRecoveryResult?> m_recoveries = [];
        private readonly Queue<Guid> m_completedRecoveryOrder = [];

        internal int RetainedRecoveryCount => m_recoveries.Count;

        public AlgorithmicWindowTransferOrchestrator(
            AlgorithmicLayoutCoordinator coordinator,
            IDisplay display)
        {
            m_coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            m_display = display ?? throw new ArgumentNullException(nameof(display));
        }

        public AlgorithmicTransferStartResult StartExistingDesktopTransfer(
            Guid correlationId,
            IWindow window,
            IVirtualDesktop sourceDesktop,
            Rectangle sourceOriginalPosition,
            Func<LayoutStateKey, CoordinatorCapacitySnapshot,
                AlgorithmicDestinationPreflightResult> placementPreflight)
        {
            ArgumentNullException.ThrowIfNull(window);
            return StartExistingDesktopTransfer(
                correlationId,
                window,
                window.Handle,
                sourceDesktop,
                sourceOriginalPosition,
                placementPreflight);
        }

        public AlgorithmicTransferStartResult StartExistingDesktopTransfer(
            Guid correlationId,
            IWindow window,
            IntPtr stableWindowHandle,
            IVirtualDesktop sourceDesktop,
            Rectangle sourceOriginalPosition,
            Func<LayoutStateKey, CoordinatorCapacitySnapshot,
                AlgorithmicDestinationPreflightResult> placementPreflight)
        {
            ArgumentNullException.ThrowIfNull(window);
            ArgumentNullException.ThrowIfNull(sourceDesktop);
            ArgumentNullException.ThrowIfNull(placementPreflight);
            if (stableWindowHandle == IntPtr.Zero)
            {
                throw new ArgumentException(
                    "The transferred window handle must not be zero.",
                    nameof(stableWindowHandle));
            }

            var search = PlanExistingDesktopTransfer(
                correlationId,
                window,
                stableWindowHandle,
                sourceDesktop,
                sourceOriginalPosition,
                placementPreflight);
            if (!search.Succeeded || search.Transfer == null)
            {
                return new AlgorithmicTransferStartResult(
                    search.Disposition == AlgorithmicDestinationSearchDisposition.TransferConflict
                        ? AlgorithmicTransferStartDisposition.TransferConflict
                        : AlgorithmicTransferStartDisposition.NoDestination,
                    search,
                    search.Transfer,
                    null,
                    false,
                    false,
                    null);
            }

            return ExecuteReservedTransfer(window, stableWindowHandle, search);
        }

        /// <summary>
        /// Completes destination preflight and reserves the exact target slot but
        /// deliberately does not mutate either endpoint or call MoveWindow. This
        /// split boundary lets an existing tiled-window transition retain its
        /// source tree until destination planning has succeeded.
        /// </summary>
        public AlgorithmicDestinationSearchResult PlanExistingDesktopTransfer(
            Guid correlationId,
            IWindow window,
            IntPtr stableWindowHandle,
            IVirtualDesktop sourceDesktop,
            Rectangle sourceOriginalPosition,
            Func<LayoutStateKey, CoordinatorCapacitySnapshot,
                AlgorithmicDestinationPreflightResult> placementPreflight)
        {
            ArgumentNullException.ThrowIfNull(window);
            ArgumentNullException.ThrowIfNull(sourceDesktop);
            ArgumentNullException.ThrowIfNull(placementPreflight);
            if (stableWindowHandle == IntPtr.Zero)
            {
                throw new ArgumentException(
                    "The transferred window handle must not be zero.",
                    nameof(stableWindowHandle));
            }

            return m_coordinator.FindDestinationAndReserve(
                correlationId,
                stableWindowHandle,
                sourceDesktop,
                m_display,
                sourceOriginalPosition,
                placementPreflight);
        }

        /// <summary>
        /// Executes a previously planned exact reservation. MoveWindow remains
        /// outside every coordinator/service lock; callers may keep an existing
        /// source tree intact until destination ownership is observed.
        /// </summary>
        public AlgorithmicTransferStartResult ExecuteReservedTransfer(
            IWindow window,
            IntPtr stableWindowHandle,
            AlgorithmicDestinationSearchResult search)
        {
            ArgumentNullException.ThrowIfNull(window);
            ArgumentNullException.ThrowIfNull(search);
            if (stableWindowHandle == IntPtr.Zero)
            {
                throw new ArgumentException(
                    "The transferred window handle must not be zero.",
                    nameof(stableWindowHandle));
            }
            if (!search.Succeeded
                || search.Transfer is not PendingWindowTransfer transfer
                || transfer.WindowHandle != stableWindowHandle
                || !MasterSatelliteDisplayEligibility.DisplaysMatch(
                    transfer.SourceDisplay,
                    m_display)
                || !MasterSatelliteDisplayEligibility.DisplaysMatch(
                    transfer.TargetDisplay,
                    m_display))
            {
                return new AlgorithmicTransferStartResult(
                    AlgorithmicTransferStartDisposition.TransferConflict,
                    search,
                    search.Transfer,
                    null,
                    false,
                    false,
                    null);
            }

            if (!m_coordinator.MarkMoving(transfer.CorrelationId, transfer.WindowHandle))
            {
                m_coordinator.Cancel(
                    transfer.CorrelationId,
                    transfer.WindowHandle,
                    "MoveTransitionRejected");
                return new AlgorithmicTransferStartResult(
                    AlgorithmicTransferStartDisposition.TransferConflict,
                    search,
                    transfer,
                    null,
                    false,
                    false,
                    null);
            }

            try
            {
                // Deliberately outside coordinator/service locks. A mock or the OS
                // may synchronously emit either workspace event from this call.
                transfer.TargetDesktop.MoveWindow(window);
                m_coordinator.TryGetTransfer(
                    transfer.CorrelationId,
                    out var latestTransfer);
                return new AlgorithmicTransferStartResult(
                    AlgorithmicTransferStartDisposition.MoveRequested,
                    search,
                    latestTransfer ?? transfer,
                    null,
                    false,
                    false,
                    null);
            }
            catch (Exception moveException)
            {
                if (m_coordinator.TryGetTransfer(transfer.CorrelationId, out var observed)
                    && observed.State == PendingWindowTransferState.Committed)
                {
                    return new AlgorithmicTransferStartResult(
                        AlgorithmicTransferStartDisposition.MoveRequested,
                        search,
                        observed,
                        null,
                        false,
                        false,
                        null);
                }

                m_coordinator.Fail(
                    transfer.CorrelationId,
                    transfer.WindowHandle,
                    "MoveWindowFailed");
                var recovery = RecoverTerminalTransfer(
                    window,
                    transfer.CorrelationId);
                return new AlgorithmicTransferStartResult(
                    AlgorithmicTransferStartDisposition.MoveFailed,
                    search,
                    recovery.Transfer ?? transfer,
                    moveException,
                    recovery.RollbackAttempted,
                    recovery.RollbackSucceeded,
                    recovery.Exception)
                {
                    Recovery = recovery,
                };
            }
        }

        public AlgorithmicTransferArrivalResult ObserveDestinationAdded(
            IWindow window,
            IVirtualDesktop actualDesktop,
            Func<PendingWindowTransfer, AlgorithmicSlotReservation,
                AlgorithmicTransferMaterializationResult> materialize)
        {
            ArgumentNullException.ThrowIfNull(window);
            return ObserveDestinationAdded(
                window,
                window.Handle,
                actualDesktop,
                materialize);
        }

        public AlgorithmicTransferArrivalResult ObserveDestinationAdded(
            IWindow window,
            IntPtr stableWindowHandle,
            IVirtualDesktop actualDesktop,
            Func<PendingWindowTransfer, AlgorithmicSlotReservation,
                AlgorithmicTransferMaterializationResult> materialize,
            Action<PendingWindowTransfer, AlgorithmicSlotReservation>?
                rollbackMaterialization = null)
        {
            ArgumentNullException.ThrowIfNull(window);
            ArgumentNullException.ThrowIfNull(actualDesktop);
            ArgumentNullException.ThrowIfNull(materialize);
            if (stableWindowHandle == IntPtr.Zero)
            {
                throw new ArgumentException(
                    "The transferred window handle must not be zero.",
                    nameof(stableWindowHandle));
            }

            if (!m_coordinator.TryGetRecentTransfer(stableWindowHandle, out var transfer))
            {
                return new AlgorithmicTransferArrivalResult(
                    AlgorithmicTransferArrivalDisposition.NotCorrelated,
                    null,
                    "The window has no active or retained transfer correlation.");
            }
            if (!MasterSatelliteDisplayEligibility.DisplaysMatch(
                    transfer.TargetDisplay,
                    m_display)
                || !MasterSatelliteDisplayEligibility.DesktopsMatch(
                    transfer.TargetDesktop,
                    actualDesktop))
            {
                return new AlgorithmicTransferArrivalResult(
                    AlgorithmicTransferArrivalDisposition.WrongDestination,
                    transfer,
                    "The window event does not belong to this transfer destination.");
            }
            if (transfer.IsTerminal)
            {
                return new AlgorithmicTransferArrivalResult(
                    AlgorithmicTransferArrivalDisposition.AlreadyTerminal,
                    transfer,
                    "The correlated transfer was already completed.");
            }
            if (transfer.ReservationId is not Guid reservationId
                || !m_coordinator.TryGetReservation(reservationId, out var reservation)
                || reservation.CorrelationId != transfer.CorrelationId
                || reservation.WindowHandle != transfer.WindowHandle)
            {
                m_coordinator.Fail(
                    transfer.CorrelationId,
                    transfer.WindowHandle,
                    "ReservationMissingAtDestination");
                return new AlgorithmicTransferArrivalResult(
                    AlgorithmicTransferArrivalDisposition.MaterializationFailed,
                    GetLatestTransfer(transfer),
                    "The correlated destination reservation is no longer live.");
            }

            if (!m_coordinator.ObserveDestination(
                    transfer.CorrelationId,
                    transfer.WindowHandle))
            {
                m_coordinator.Fail(
                    transfer.CorrelationId,
                    transfer.WindowHandle,
                    "DestinationObservationRejected");
                return new AlgorithmicTransferArrivalResult(
                    AlgorithmicTransferArrivalDisposition.StateTransitionRejected,
                    GetLatestTransfer(transfer),
                    "The destination event could not be recorded.");
            }

            var readiness = m_coordinator.GetReservationReadiness(reservationId);
            if (readiness.IsWaiting)
            {
                return new AlgorithmicTransferArrivalResult(
                    AlgorithmicTransferArrivalDisposition.AwaitingPrecedingSlot,
                    GetLatestTransfer(transfer),
                    readiness.DiagnosticReason);
            }
            if (!readiness.IsReady)
            {
                m_coordinator.Fail(
                    transfer.CorrelationId,
                    transfer.WindowHandle,
                    "DestinationReservationNotMaterializable");
                return new AlgorithmicTransferArrivalResult(
                    AlgorithmicTransferArrivalDisposition.MaterializationFailed,
                    GetLatestTransfer(transfer),
                    readiness.DiagnosticReason);
            }

            AlgorithmicTransferMaterializationResult materialization;
            try
            {
                // Deliberately outside the coordinator mutation lock. The endpoint
                // may acquire m_backendLock and perform one transactional mutation.
                materialization = materialize(transfer, reservation)
                    ?? throw new InvalidOperationException(
                        "The destination materializer returned no result.");
            }
            catch (Exception exception)
            {
                m_coordinator.Fail(
                    transfer.CorrelationId,
                    transfer.WindowHandle,
                    "DestinationMaterializationThrew");
                return new AlgorithmicTransferArrivalResult(
                    AlgorithmicTransferArrivalDisposition.MaterializationFailed,
                    GetLatestTransfer(transfer),
                    "The destination materializer threw an exception.",
                    exception);
            }
            if (!materialization.Succeeded)
            {
                m_coordinator.Fail(
                    transfer.CorrelationId,
                    transfer.WindowHandle,
                    "DestinationMaterializationRejected");
                return new AlgorithmicTransferArrivalResult(
                    AlgorithmicTransferArrivalDisposition.MaterializationFailed,
                    GetLatestTransfer(transfer),
                    materialization.DiagnosticReason);
            }
            if (!m_coordinator.Commit(
                    transfer.CorrelationId,
                    transfer.WindowHandle))
            {
                m_coordinator.Fail(
                    transfer.CorrelationId,
                    transfer.WindowHandle,
                    "DestinationCommitRejected");
                Exception? rollbackException = null;
                if (rollbackMaterialization != null)
                {
                    try
                    {
                        // Commit has already returned and therefore no coordinator
                        // mutation lock is held while the endpoint restores its
                        // source/target transaction.
                        rollbackMaterialization(transfer, reservation);
                    }
                    catch (Exception exception)
                    {
                        rollbackException = exception;
                    }
                }
                return new AlgorithmicTransferArrivalResult(
                    AlgorithmicTransferArrivalDisposition.StateTransitionRejected,
                    GetLatestTransfer(transfer),
                    rollbackException == null
                        ? "The materialized destination could not be committed."
                        : "The materialized destination could not be committed and its endpoint rollback failed.",
                    rollbackException);
            }

            m_coordinator.TryGetTransfer(transfer.CorrelationId, out var committed);
            return new AlgorithmicTransferArrivalResult(
                AlgorithmicTransferArrivalDisposition.Materialized,
                committed,
                "The reserved destination slot was materialized and committed.");
        }

        public bool ObserveSourceRemoved(
            IntPtr cachedWindowHandle,
            IVirtualDesktop sourceDesktop,
            out PendingWindowTransfer transfer)
        {
            ArgumentNullException.ThrowIfNull(sourceDesktop);
            return m_coordinator.ObserveSourceRemoved(
                cachedWindowHandle,
                sourceDesktop,
                m_display,
                out transfer);
        }

        /// <summary>
        /// Applies the optional user-follow policy only to an already committed
        /// transfer. The desktop switch is an external OS boundary and this method
        /// is called after all coordinator and service locks have been released.
        /// </summary>
        public AlgorithmicTransferFollowResult FollowCommittedTransfer(
            PendingWindowTransfer transfer,
            bool followRequested)
        {
            ArgumentNullException.ThrowIfNull(transfer);
            if (!followRequested)
            {
                return new AlgorithmicTransferFollowResult(
                    false,
                    false,
                    "Following the overflow window is disabled.");
            }
            if (transfer.State != PendingWindowTransferState.Committed)
            {
                return new AlgorithmicTransferFollowResult(
                    true,
                    false,
                    "The overflow destination cannot be followed before commit.");
            }

            try
            {
                transfer.TargetDesktop.SwitchTo();
                return new AlgorithmicTransferFollowResult(
                    true,
                    true,
                    "The committed overflow destination was activated.");
            }
            catch (Exception exception)
            {
                return new AlgorithmicTransferFollowResult(
                    true,
                    false,
                    "The committed overflow destination could not be activated.",
                    exception);
            }
        }

        /// <summary>
        /// Reconciles the synchronous result of IVirtualDesktop.MoveWindow. WinMan
        /// changes COM ownership but does not emit workspace WindowAdded/Removed
        /// solely for a virtual-desktop move, so the caller must confirm ownership
        /// and enter the same idempotent destination path explicitly.
        /// </summary>
        public AlgorithmicTransferReconciliationResult ReconcileAfterMove(
            IWindow window,
            IntPtr stableWindowHandle,
            PendingWindowTransfer transfer,
            Func<IWindow, IntPtr, IVirtualDesktop, bool> consumeDestination)
        {
            ArgumentNullException.ThrowIfNull(window);
            ArgumentNullException.ThrowIfNull(transfer);
            ArgumentNullException.ThrowIfNull(consumeDestination);
            if (stableWindowHandle == IntPtr.Zero)
            {
                throw new ArgumentException(
                    "The transferred window handle must not be zero.",
                    nameof(stableWindowHandle));
            }
            if (transfer.WindowHandle != stableWindowHandle
                || !MasterSatelliteDisplayEligibility.DisplaysMatch(
                    transfer.TargetDisplay,
                    m_display))
            {
                throw new ArgumentException(
                    "The transfer does not belong to this window/display endpoint.",
                    nameof(transfer));
            }
            if (transfer.IsTerminal)
            {
                return new AlgorithmicTransferReconciliationResult(
                    AlgorithmicTransferReconciliationDisposition.AlreadyTerminal,
                    transfer,
                    transfer.State == PendingWindowTransferState.Committed,
                    transfer.SourceRemovedObserved,
                    "The transfer already reached a terminal state.");
            }

            bool targetOwnsWindow;
            try
            {
                targetOwnsWindow = transfer.TargetDesktop.IsAlive
                    && transfer.TargetDesktop.HasWindow(window);
            }
            catch (Exception exception)
            {
                return new AlgorithmicTransferReconciliationResult(
                    AlgorithmicTransferReconciliationDisposition.OwnershipProbeFailed,
                    GetLatestTransfer(transfer),
                    false,
                    false,
                    "The target desktop ownership probe failed.",
                    exception);
            }
            if (!targetOwnsWindow)
            {
                return new AlgorithmicTransferReconciliationResult(
                    AlgorithmicTransferReconciliationDisposition.TargetNotObserved,
                    GetLatestTransfer(transfer),
                    false,
                    false,
                    "The target desktop does not yet report ownership of the window.");
            }

            bool sourceObserved = ObserveSourceRemoved(
                stableWindowHandle,
                transfer.SourceDesktop,
                out _);
            try
            {
                bool consumed = consumeDestination(
                    window,
                    stableWindowHandle,
                    transfer.TargetDesktop);
                return new AlgorithmicTransferReconciliationResult(
                    consumed
                        ? AlgorithmicTransferReconciliationDisposition.DestinationConsumed
                        : AlgorithmicTransferReconciliationDisposition.DestinationNotConsumed,
                    GetLatestTransfer(transfer),
                    true,
                    sourceObserved,
                    consumed
                        ? "The confirmed target ownership was consumed by the destination path."
                        : "The destination path did not consume the confirmed target ownership.");
            }
            catch (Exception exception)
            {
                return new AlgorithmicTransferReconciliationResult(
                    AlgorithmicTransferReconciliationDisposition.DestinationCallbackFailed,
                    GetLatestTransfer(transfer),
                    true,
                    sourceObserved,
                    "The destination reconciliation callback failed.",
                    exception);
            }
        }

        /// <summary>
        /// Consumes a failed or cancelled transfer and, when possible, requests
        /// one move back to its source desktop. The coordinator lookup and all
        /// state bookkeeping finish before IVirtualDesktop.MoveWindow is called.
        /// Reentrant or duplicate calls for the same correlation never issue a
        /// second move or request a second failure notification.
        /// </summary>
        public AlgorithmicTransferRecoveryResult RecoverTerminalTransfer(
            IWindow window,
            Guid correlationId)
        {
            return RecoverTerminalTransfer(
                window,
                correlationId,
                allowRollbackToSource: true,
                preferredFloatingDesktop: null);
        }

        /// <summary>
        /// Consumes a failed or cancelled transfer exactly once. A caller that
        /// has positively identified a user-selected destination can disable the
        /// automatic source rollback; the window then remains floating on that
        /// desktop and duplicate terminal notifications cannot undo the choice.
        /// </summary>
        public AlgorithmicTransferRecoveryResult RecoverTerminalTransfer(
            IWindow window,
            Guid correlationId,
            bool allowRollbackToSource,
            IVirtualDesktop? preferredFloatingDesktop)
        {
            ArgumentNullException.ThrowIfNull(window);
            if (correlationId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The transfer correlation ID must not be empty.",
                    nameof(correlationId));
            }
            if (!allowRollbackToSource)
            {
                ArgumentNullException.ThrowIfNull(preferredFloatingDesktop);
            }

            if (!m_coordinator.TryGetTransfer(correlationId, out var transfer))
            {
                return new AlgorithmicTransferRecoveryResult(
                    AlgorithmicTransferRecoveryDisposition.TransferNotFound,
                    null,
                    false,
                    false,
                    false,
                    null,
                    false,
                    false);
            }
            if (!transfer.IsTerminal)
            {
                return new AlgorithmicTransferRecoveryResult(
                    AlgorithmicTransferRecoveryDisposition.NotTerminal,
                    transfer,
                    false,
                    false,
                    false,
                    null,
                    false,
                    false);
            }
            if (transfer.State == PendingWindowTransferState.Committed)
            {
                return new AlgorithmicTransferRecoveryResult(
                    AlgorithmicTransferRecoveryDisposition.Committed,
                    transfer,
                    false,
                    false,
                    false,
                    transfer.TargetDesktop,
                    false,
                    false);
            }

            if (m_recoveries.TryGetValue(correlationId, out var retained))
            {
                if (retained == null)
                {
                    return new AlgorithmicTransferRecoveryResult(
                        AlgorithmicTransferRecoveryDisposition.RecoveryInProgress,
                        transfer,
                        false,
                        false,
                        false,
                        null,
                        false,
                        true);
                }
                return retained with
                {
                    ShouldNotifyFailure = false,
                    IsDuplicate = true,
                };
            }

            // Install the sentinel before touching an external endpoint. A mock
            // or WinMan may synchronously re-enter through source Added.
            m_recoveries.Add(correlationId, null);
            AlgorithmicTransferRecoveryResult result;
            try
            {
                result = RecoverTerminalTransferCore(
                    window,
                    transfer,
                    allowRollbackToSource,
                    preferredFloatingDesktop);
            }
            catch (Exception exception)
            {
                // Endpoint properties are allowed to fail. Convert unexpected
                // failures into an actionable floating result and retain it so
                // event replay cannot create an unbounded retry loop.
                result = new AlgorithmicTransferRecoveryResult(
                    AlgorithmicTransferRecoveryDisposition.RollbackFailed,
                    transfer,
                    false,
                    false,
                    true,
                    null,
                    true,
                    false,
                    exception);
            }

            RetainCompletedRecovery(correlationId, result);
            return result;
        }

        private AlgorithmicTransferRecoveryResult RecoverTerminalTransferCore(
            IWindow window,
            PendingWindowTransfer transfer,
            bool allowRollbackToSource,
            IVirtualDesktop? preferredFloatingDesktop)
        {
            if (transfer.TerminalReason == "WindowClosed" || !window.IsAlive)
            {
                return new AlgorithmicTransferRecoveryResult(
                    AlgorithmicTransferRecoveryDisposition.WindowClosed,
                    transfer,
                    false,
                    false,
                    false,
                    null,
                    false,
                    false);
            }
            if (!allowRollbackToSource)
            {
                return new AlgorithmicTransferRecoveryResult(
                    AlgorithmicTransferRecoveryDisposition.PreservedOnSelectedDesktop,
                    transfer,
                    false,
                    false,
                    true,
                    preferredFloatingDesktop,
                    true,
                    false);
            }
            if (!transfer.SourceDesktop.IsAlive)
            {
                return new AlgorithmicTransferRecoveryResult(
                    AlgorithmicTransferRecoveryDisposition.SourceUnavailable,
                    transfer,
                    false,
                    false,
                    true,
                    null,
                    true,
                    false);
            }
            if (transfer.SourceDesktop.HasWindow(window))
            {
                return new AlgorithmicTransferRecoveryResult(
                    AlgorithmicTransferRecoveryDisposition.AlreadyOnSource,
                    transfer,
                    false,
                    false,
                    true,
                    transfer.SourceDesktop,
                    true,
                    false);
            }

            try
            {
                // Deliberately outside the coordinator mutation lock. The call
                // may synchronously emit source Added or target Removed.
                transfer.SourceDesktop.MoveWindow(window);
                return new AlgorithmicTransferRecoveryResult(
                    AlgorithmicTransferRecoveryDisposition.RolledBackToSource,
                    transfer,
                    true,
                    true,
                    true,
                    transfer.SourceDesktop,
                    true,
                    false);
            }
            catch (Exception exception)
            {
                return new AlgorithmicTransferRecoveryResult(
                    AlgorithmicTransferRecoveryDisposition.RollbackFailed,
                    transfer,
                    true,
                    false,
                    true,
                    null,
                    true,
                    false,
                    exception);
            }
        }

        private PendingWindowTransfer GetLatestTransfer(PendingWindowTransfer fallback)
        {
            return m_coordinator.TryGetTransfer(fallback.CorrelationId, out var latest)
                ? latest
                : fallback;
        }

        private void RetainCompletedRecovery(
            Guid correlationId,
            AlgorithmicTransferRecoveryResult result)
        {
            m_recoveries[correlationId] = result;
            m_completedRecoveryOrder.Enqueue(correlationId);
            while (m_completedRecoveryOrder.Count > MaximumRetainedRecoveries)
            {
                var expired = m_completedRecoveryOrder.Dequeue();
                m_recoveries.Remove(expired);
            }
        }
    }
}
