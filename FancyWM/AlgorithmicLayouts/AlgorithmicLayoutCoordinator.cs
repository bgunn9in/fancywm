using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Threading;

using WinMan;

namespace FancyWM.AlgorithmicLayouts
{
    /// <summary>
    /// Workspace-wide, dispatcher-confined owner of algorithmic runtime state and
    /// cross-desktop transfer bookkeeping. No external callback and no window or
    /// desktop operation is performed while the mutation lock is held.
    /// </summary>
    internal sealed partial class AlgorithmicLayoutCoordinator : IMasterSatelliteRuntimeRegistry, IDisposable
    {
        private static readonly TimeSpan DefaultTransferTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan TerminalRetention = TimeSpan.FromMinutes(2);

        private readonly Dispatcher m_dispatcher;
        private readonly IWorkspace m_workspace;
        private readonly TimeProvider m_timeProvider;
        private readonly TimeSpan m_transferTimeout;
        private readonly object m_mutationLock = new();
        private readonly MasterSatelliteRuntimeRegistry m_runtimeStates = new();
        private readonly AlgorithmicDesktopCreationTracker m_desktopCreations;
        private readonly Dictionary<IDisplay, DisplayParticipantEntry> m_displayParticipants
            = new(DisplayIdentityComparer.Instance);
        private readonly Dictionary<LayoutStateKey, PublishedCapacity> m_capacities
            = new(LayoutStateKeyIdentityComparer.Instance);
        private readonly Dictionary<Guid, AlgorithmicSlotReservation> m_reservations = [];
        private readonly Dictionary<Guid, AlgorithmicSlotReservation> m_committedSlotShadows = [];
        private readonly Dictionary<Guid, PendingWindowTransfer> m_transfers = [];
        private readonly Dictionary<IntPtr, Guid> m_activeTransfersByWindow = [];
        private readonly Dictionary<IntPtr, Guid> m_recentTransfersByWindow = [];
        private readonly Dictionary<Guid, DateTimeOffset> m_retiredCorrelations = [];
        private readonly Queue<PendingWindowTransfer> m_pendingTerminalNotifications = [];
        private readonly DispatcherTimer m_cleanupTimer;
        private DispatcherOperation? m_terminalNotificationDispatch;
        private Action<PendingWindowTransfer>? m_transferTerminated;
        private bool m_disposed;

        public Dispatcher Dispatcher => m_dispatcher;

        public IWorkspace Workspace => m_workspace;

        /// <summary>
        /// Floating-window ownership is shared by every display participant in
        /// this workspace so a manual cross-display move cannot consume a slot.
        /// It uses an independent lock and never participates in coordinator
        /// transfer mutations.
        /// </summary>
        public WorkspaceFloatingWindowRegistry FloatingWindows { get; } = new();

        /// <summary>
        /// Raised asynchronously on <see cref="Dispatcher"/> after a transfer first
        /// enters a terminal state. Notifications are never raised while the
        /// coordinator mutation lock is held.
        /// </summary>
        public event Action<PendingWindowTransfer> TransferTerminated
        {
            add
            {
                ArgumentNullException.ThrowIfNull(value);
                VerifyAccessAndNotDisposed();
                lock (m_mutationLock)
                {
                    m_transferTerminated += value;
                }
            }
            remove
            {
                if (value == null)
                {
                    return;
                }
                m_dispatcher.VerifyAccess();
                lock (m_mutationLock)
                {
                    m_transferTerminated -= value;
                }
            }
        }

        internal bool IsMutationLockHeldByCurrentThread
            => System.Threading.Monitor.IsEntered(m_mutationLock);

        public int Count
        {
            get
            {
                VerifyAccessAndNotDisposed();
                lock (m_mutationLock)
                {
                    return m_runtimeStates.Count;
                }
            }
        }

        public int RegisteredDisplayCount
        {
            get
            {
                VerifyAccessAndNotDisposed();
                lock (m_mutationLock)
                {
                    return m_displayParticipants.Count;
                }
            }
        }

        public int ReservationCount
        {
            get
            {
                VerifyAccessAndNotDisposed();
                lock (m_mutationLock)
                {
                    return m_reservations.Count;
                }
            }
        }

        public int ActiveTransferCount
        {
            get
            {
                VerifyAccessAndNotDisposed();
                lock (m_mutationLock)
                {
                    return m_activeTransfersByWindow.Count;
                }
            }
        }

        public AlgorithmicLayoutCoordinator(
            IWorkspace workspace,
            Dispatcher dispatcher,
            TimeProvider? timeProvider = null,
            TimeSpan? transferTimeout = null)
        {
            m_workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
            m_dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            m_timeProvider = timeProvider ?? TimeProvider.System;
            m_desktopCreations = new AlgorithmicDesktopCreationTracker(m_dispatcher);
            m_transferTimeout = transferTimeout ?? DefaultTransferTimeout;
            if (m_transferTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(transferTimeout),
                    "The transfer timeout must be positive.");
            }
            m_dispatcher.VerifyAccess();
            m_cleanupTimer = new DispatcherTimer(DispatcherPriority.Background, m_dispatcher)
            {
                Interval = TimeSpan.FromSeconds(Math.Clamp(
                    m_transferTimeout.TotalSeconds / 2,
                    1,
                    30)),
            };
            m_cleanupTimer.Tick += OnCleanupTimerTick;
            m_cleanupTimer.Start();
        }

        public AlgorithmicLayoutDisplayRegistration RegisterDisplay(
            IDisplay display,
            object participant)
        {
            ArgumentNullException.ThrowIfNull(display);
            ArgumentNullException.ThrowIfNull(participant);
            VerifyAccessAndNotDisposed();
            ValidateWorkspaceOwner(display.Workspace, nameof(display));
            lock (m_mutationLock)
            {
                if (m_displayParticipants.ContainsKey(display))
                {
                    throw new InvalidOperationException(
                        "An algorithmic layout participant is already registered for this display.");
                }
                var token = Guid.NewGuid();
                m_displayParticipants.Add(
                    display,
                    new DisplayParticipantEntry(token, participant));
                return new AlgorithmicLayoutDisplayRegistration(
                    this,
                    display,
                    participant,
                    token);
            }
        }

        public bool IsDisplayRegistered(IDisplay display, object participant)
        {
            ArgumentNullException.ThrowIfNull(display);
            ArgumentNullException.ThrowIfNull(participant);
            VerifyAccessAndNotDisposed();
            lock (m_mutationLock)
            {
                return m_displayParticipants.TryGetValue(display, out var existing)
                    && ReferenceEquals(existing.Participant, participant);
            }
        }

        internal bool UnregisterDisplay(
            IDisplay display,
            object participant,
            Guid registrationToken)
        {
            ArgumentNullException.ThrowIfNull(display);
            ArgumentNullException.ThrowIfNull(participant);
            m_dispatcher.VerifyAccess();
            lock (m_mutationLock)
            {
                if (m_disposed)
                {
                    return false;
                }
                if (!m_displayParticipants.TryGetValue(display, out var existing)
                    || existing.RegistrationToken != registrationToken
                    || !ReferenceEquals(existing.Participant, participant))
                {
                    return false;
                }
                m_displayParticipants.Remove(display);
                RemoveDisplayCore(display, "DisplayUnregistered");
                return true;
            }
        }

        public bool PublishCapacity(
            LayoutStateKey layoutKey,
            MasterSatelliteCapacitySnapshot capacity,
            long publicationSequence)
        {
            ArgumentNullException.ThrowIfNull(layoutKey.VirtualDesktop);
            ArgumentNullException.ThrowIfNull(layoutKey.Display);
            ArgumentNullException.ThrowIfNull(capacity);
            if (publicationSequence <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(publicationSequence),
                    "The capacity publication sequence must be positive.");
            }
            VerifyAccessAndNotDisposed();
            ValidateWorkspaceOwner(layoutKey.VirtualDesktop.Workspace, nameof(layoutKey));
            ValidateWorkspaceOwner(layoutKey.Display.Workspace, nameof(layoutKey));
            lock (m_mutationLock)
            {
                if (!m_displayParticipants.ContainsKey(layoutKey.Display))
                {
                    throw new InvalidOperationException(
                        "Capacity cannot be published for an unregistered display.");
                }
                if (m_capacities.TryGetValue(layoutKey, out var current))
                {
                    if (publicationSequence < current.PublicationSequence)
                    {
                        return false;
                    }
                    if (publicationSequence == current.PublicationSequence)
                    {
                        return current.Snapshot == capacity;
                    }
                }
                m_capacities[layoutKey] = new PublishedCapacity(
                    capacity,
                    publicationSequence);
                ReconcileCapacityCore(
                    layoutKey,
                    capacity,
                    m_timeProvider.GetUtcNow());
                return true;
            }
        }

        public bool TryGetCapacity(
            LayoutStateKey layoutKey,
            out CoordinatorCapacitySnapshot capacity)
        {
            ArgumentNullException.ThrowIfNull(layoutKey.VirtualDesktop);
            ArgumentNullException.ThrowIfNull(layoutKey.Display);
            VerifyAccessAndNotDisposed();
            lock (m_mutationLock)
            {
                CleanupExpiredCore(m_timeProvider.GetUtcNow());
                return TryGetCapacityCore(layoutKey, out capacity);
            }
        }

        public bool RemoveCapacity(LayoutStateKey layoutKey)
        {
            ArgumentNullException.ThrowIfNull(layoutKey.VirtualDesktop);
            ArgumentNullException.ThrowIfNull(layoutKey.Display);
            VerifyAccessAndNotDisposed();
            lock (m_mutationLock)
            {
                bool removed = m_capacities.Remove(layoutKey);
                RemoveCommittedShadowsForLayoutCore(layoutKey);
                CancelTransfersForLayoutCore(layoutKey, "CapacityRemoved");
                return removed;
            }
        }

        public bool TryPlanAndReserve(
            Guid correlationId,
            IntPtr windowHandle,
            IVirtualDesktop sourceDesktop,
            IDisplay sourceDisplay,
            LayoutStateKey targetLayout,
            out PendingWindowTransfer transfer)
        {
            return TryPlanAndReserve(
                correlationId,
                windowHandle,
                sourceDesktop,
                sourceDisplay,
                targetLayout,
                null,
                out transfer);
        }

        public bool TryPlanAndReserve(
            Guid correlationId,
            IntPtr windowHandle,
            IVirtualDesktop sourceDesktop,
            IDisplay sourceDisplay,
            LayoutStateKey targetLayout,
            Rectangle? sourceOriginalPosition,
            out PendingWindowTransfer transfer)
        {
            ArgumentNullException.ThrowIfNull(sourceDesktop);
            ArgumentNullException.ThrowIfNull(sourceDisplay);
            ArgumentNullException.ThrowIfNull(targetLayout.VirtualDesktop);
            ArgumentNullException.ThrowIfNull(targetLayout.Display);
            VerifyAccessAndNotDisposed();
            ValidateTransferIdentity(correlationId, windowHandle);
            ValidateTransferOwnership(sourceDesktop, sourceDisplay, targetLayout);

            lock (m_mutationLock)
            {
                var now = m_timeProvider.GetUtcNow();
                CleanupExpiredCore(now);

                if (m_retiredCorrelations.ContainsKey(correlationId))
                {
                    transfer = null!;
                    return false;
                }
                if (m_transfers.TryGetValue(correlationId, out var existing))
                {
                    if (MatchesRequest(
                        existing,
                        windowHandle,
                        sourceDesktop,
                        sourceDisplay,
                        targetLayout))
                    {
                        transfer = existing;
                        return existing.State is PendingWindowTransferState.Reserved
                            or PendingWindowTransferState.Moving
                            or PendingWindowTransferState.DestinationObserved;
                    }
                    transfer = null!;
                    return false;
                }
                if (m_activeTransfersByWindow.ContainsKey(windowHandle)
                    || m_recentTransfersByWindow.ContainsKey(windowHandle)
                    || MasterSatelliteDisplayEligibility.DesktopsMatch(
                        sourceDesktop,
                        targetLayout.VirtualDesktop)
                    || !m_displayParticipants.ContainsKey(sourceDisplay)
                    || !m_displayParticipants.ContainsKey(targetLayout.Display)
                    || !TryGetCapacityCore(targetLayout, out var capacity)
                    || !capacity.CanAcceptWindow
                    || capacity.NextRole == null)
                {
                    transfer = null!;
                    return false;
                }

                var deadline = now + m_transferTimeout;
                var reservationId = Guid.NewGuid();
                var reservation = new AlgorithmicSlotReservation
                {
                    ReservationId = reservationId,
                    CorrelationId = correlationId,
                    WindowHandle = windowHandle,
                    LayoutKey = targetLayout,
                    Role = capacity.NextRole.Value,
                    SatelliteIndex = capacity.NextSatelliteIndex,
                    CreatedAt = now,
                    Deadline = deadline,
                };
                if (IsSlotReservedCore(
                    targetLayout,
                    reservation.Role,
                    reservation.SatelliteIndex))
                {
                    transfer = null!;
                    return false;
                }

                transfer = new PendingWindowTransfer
                {
                    CorrelationId = correlationId,
                    WindowHandle = windowHandle,
                    SourceDesktop = sourceDesktop,
                    SourceDisplay = sourceDisplay,
                    SourceOriginalPosition = sourceOriginalPosition,
                    TargetDesktop = targetLayout.VirtualDesktop,
                    TargetDisplay = targetLayout.Display,
                    TargetRole = reservation.Role,
                    TargetSatelliteIndex = reservation.SatelliteIndex,
                    CreatedAt = now,
                    UpdatedAt = now,
                    Deadline = deadline,
                    State = PendingWindowTransferState.Reserved,
                    ReservationId = reservationId,
                };
                m_reservations.Add(reservationId, reservation);
                m_transfers.Add(correlationId, transfer);
                m_activeTransfersByWindow.Add(windowHandle, correlationId);
                m_recentTransfersByWindow[windowHandle] = correlationId;
                return true;
            }
        }

        public bool TryPlan(
            Guid correlationId,
            IntPtr windowHandle,
            IVirtualDesktop sourceDesktop,
            IDisplay sourceDisplay,
            LayoutStateKey targetLayout,
            out PendingWindowTransfer transfer)
        {
            ArgumentNullException.ThrowIfNull(sourceDesktop);
            ArgumentNullException.ThrowIfNull(sourceDisplay);
            ArgumentNullException.ThrowIfNull(targetLayout.VirtualDesktop);
            ArgumentNullException.ThrowIfNull(targetLayout.Display);
            VerifyAccessAndNotDisposed();
            ValidateTransferIdentity(correlationId, windowHandle);
            ValidateTransferOwnership(sourceDesktop, sourceDisplay, targetLayout);

            lock (m_mutationLock)
            {
                var now = m_timeProvider.GetUtcNow();
                CleanupExpiredCore(now);
                if (m_retiredCorrelations.ContainsKey(correlationId))
                {
                    transfer = null!;
                    return false;
                }
                if (m_transfers.TryGetValue(correlationId, out var existing))
                {
                    transfer = existing;
                    return MatchesRequest(
                            existing,
                            windowHandle,
                            sourceDesktop,
                            sourceDisplay,
                            targetLayout)
                        && !existing.IsTerminal;
                }
                if (m_activeTransfersByWindow.ContainsKey(windowHandle)
                    || m_recentTransfersByWindow.ContainsKey(windowHandle)
                    || MasterSatelliteDisplayEligibility.DesktopsMatch(
                        sourceDesktop,
                        targetLayout.VirtualDesktop)
                    || !m_displayParticipants.ContainsKey(sourceDisplay)
                    || !m_displayParticipants.ContainsKey(targetLayout.Display)
                    || !TryGetCapacityCore(targetLayout, out var capacity)
                    || !capacity.CanAcceptWindow
                    || capacity.NextRole == null)
                {
                    transfer = null!;
                    return false;
                }

                transfer = new PendingWindowTransfer
                {
                    CorrelationId = correlationId,
                    WindowHandle = windowHandle,
                    SourceDesktop = sourceDesktop,
                    SourceDisplay = sourceDisplay,
                    TargetDesktop = targetLayout.VirtualDesktop,
                    TargetDisplay = targetLayout.Display,
                    TargetRole = capacity.NextRole.Value,
                    TargetSatelliteIndex = capacity.NextSatelliteIndex,
                    CreatedAt = now,
                    UpdatedAt = now,
                    Deadline = now + m_transferTimeout,
                    State = PendingWindowTransferState.Planned,
                };
                m_transfers.Add(correlationId, transfer);
                m_activeTransfersByWindow.Add(windowHandle, correlationId);
                m_recentTransfersByWindow[windowHandle] = correlationId;
                return true;
            }
        }

        public bool TryReserve(
            Guid correlationId,
            IntPtr windowHandle,
            out PendingWindowTransfer transfer)
        {
            ValidateTransferIdentity(correlationId, windowHandle);
            VerifyAccessAndNotDisposed();
            lock (m_mutationLock)
            {
                var now = m_timeProvider.GetUtcNow();
                CleanupExpiredCore(now);
                if (!m_transfers.TryGetValue(correlationId, out var existing))
                {
                    transfer = null!;
                    return false;
                }
                if (existing.WindowHandle != windowHandle)
                {
                    transfer = existing;
                    return false;
                }
                if (existing.State is PendingWindowTransferState.Reserved
                    or PendingWindowTransferState.Moving
                    or PendingWindowTransferState.DestinationObserved)
                {
                    transfer = existing;
                    return true;
                }
                var targetLayout = new LayoutStateKey(
                    existing.TargetDesktop,
                    existing.TargetDisplay);
                if (existing.State != PendingWindowTransferState.Planned
                    || !TryGetCapacityCore(targetLayout, out var capacity)
                    || !capacity.CanAcceptWindow
                    || IsSlotReservedCore(
                        targetLayout,
                        capacity.NextRole!.Value,
                        capacity.NextSatelliteIndex))
                {
                    transfer = existing;
                    return false;
                }

                var reservationId = Guid.NewGuid();
                m_reservations.Add(reservationId, new AlgorithmicSlotReservation
                {
                    ReservationId = reservationId,
                    CorrelationId = existing.CorrelationId,
                    WindowHandle = existing.WindowHandle,
                    LayoutKey = targetLayout,
                    Role = capacity.NextRole!.Value,
                    SatelliteIndex = capacity.NextSatelliteIndex,
                    CreatedAt = now,
                    Deadline = existing.Deadline,
                });
                transfer = existing with
                {
                    State = PendingWindowTransferState.Reserved,
                    ReservationId = reservationId,
                    TargetRole = capacity.NextRole.Value,
                    TargetSatelliteIndex = capacity.NextSatelliteIndex,
                    UpdatedAt = now,
                };
                m_transfers[correlationId] = transfer;
                return true;
            }
        }

        public bool TryGetTransfer(Guid correlationId, out PendingWindowTransfer transfer)
        {
            VerifyAccessAndNotDisposed();
            lock (m_mutationLock)
            {
                return m_transfers.TryGetValue(correlationId, out transfer!);
            }
        }

        public bool TryGetActiveTransfer(IntPtr windowHandle, out PendingWindowTransfer transfer)
        {
            if (windowHandle == IntPtr.Zero)
            {
                throw new ArgumentException(
                    "The transferred window handle must not be zero.",
                    nameof(windowHandle));
            }
            VerifyAccessAndNotDisposed();
            lock (m_mutationLock)
            {
                CleanupExpiredCore(m_timeProvider.GetUtcNow());
                if (m_activeTransfersByWindow.TryGetValue(windowHandle, out var correlationId)
                    && m_transfers.TryGetValue(correlationId, out transfer!))
                {
                    return true;
                }
                transfer = null!;
                return false;
            }
        }

        public bool TryGetRecentTransfer(IntPtr windowHandle, out PendingWindowTransfer transfer)
        {
            if (windowHandle == IntPtr.Zero)
            {
                throw new ArgumentException(
                    "The transferred window handle must not be zero.",
                    nameof(windowHandle));
            }
            VerifyAccessAndNotDisposed();
            lock (m_mutationLock)
            {
                CleanupExpiredCore(m_timeProvider.GetUtcNow());
                if ((m_activeTransfersByWindow.TryGetValue(windowHandle, out var correlationId)
                        || m_recentTransfersByWindow.TryGetValue(windowHandle, out correlationId))
                    && m_transfers.TryGetValue(correlationId, out transfer!))
                {
                    return true;
                }
                transfer = null!;
                return false;
            }
        }

        public bool TryGetReservation(Guid reservationId, out AlgorithmicSlotReservation reservation)
        {
            VerifyAccessAndNotDisposed();
            lock (m_mutationLock)
            {
                CleanupExpiredCore(m_timeProvider.GetUtcNow());
                return m_reservations.TryGetValue(reservationId, out reservation!);
            }
        }

        /// <summary>
        /// Determines whether an exact reservation is the next physical slot that
        /// can be materialized. Several transfers may reserve master/S0/S1 before
        /// Windows reports their destination events; later arrivals must wait for
        /// all preceding canonical slots instead of failing or changing roles.
        /// </summary>
        public AlgorithmicReservationReadiness GetReservationReadiness(
            Guid reservationId)
        {
            if (reservationId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The reservation identity must not be empty.",
                    nameof(reservationId));
            }
            VerifyAccessAndNotDisposed();
            lock (m_mutationLock)
            {
                CleanupExpiredCore(m_timeProvider.GetUtcNow());
                if (!m_reservations.TryGetValue(reservationId, out var reservation))
                {
                    return new AlgorithmicReservationReadiness(
                        AlgorithmicReservationReadinessDisposition.Missing,
                        "The exact reservation is no longer active.");
                }
                if (!m_capacities.TryGetValue(
                        reservation.LayoutKey,
                        out var publication))
                {
                    return new AlgorithmicReservationReadiness(
                        AlgorithmicReservationReadinessDisposition.DestinationUnavailable,
                        "The destination no longer has a published capacity snapshot.");
                }

                var physical = publication.Snapshot;
                if (physical.LayoutKind is not (WorkspaceLayoutKind.Empty
                    or WorkspaceLayoutKind.Canonical))
                {
                    return new AlgorithmicReservationReadiness(
                        AlgorithmicReservationReadinessDisposition.DestinationUnavailable,
                        "The destination no longer has a supported canonical layout.");
                }

                int ordinal = GetSlotOrdinal(
                    reservation.Role,
                    reservation.SatelliteIndex);
                if (ordinal < physical.OccupiedSlots)
                {
                    return new AlgorithmicReservationReadiness(
                        AlgorithmicReservationReadinessDisposition.SlotAlreadyOccupied,
                        "The exact reserved slot is already physically occupied.");
                }
                if (ordinal > physical.OccupiedSlots)
                {
                    return new AlgorithmicReservationReadiness(
                        AlgorithmicReservationReadinessDisposition.AwaitingPrecedingSlot,
                        "The exact reservation is waiting for a preceding canonical slot.");
                }
                if (!physical.CanAcceptWindow
                    || ordinal >= physical.TotalCapacity)
                {
                    return new AlgorithmicReservationReadiness(
                        AlgorithmicReservationReadinessDisposition.DestinationUnavailable,
                        physical.DiagnosticReason
                            ?? "The destination can no longer materialize the exact reservation.");
                }

                return new AlgorithmicReservationReadiness(
                    AlgorithmicReservationReadinessDisposition.Ready,
                    "The exact reservation is the next physical canonical slot.");
            }
        }

        public bool MarkMoving(Guid correlationId, IntPtr windowHandle)
        {
            return Transition(
                correlationId,
                windowHandle,
                PendingWindowTransferState.Moving,
                PendingWindowTransferState.Reserved,
                PendingWindowTransferState.Moving);
        }

        public bool ObserveDestination(Guid correlationId, IntPtr windowHandle)
        {
            VerifyAccessAndNotDisposed();
            ValidateTransferIdentity(correlationId, windowHandle);
            lock (m_mutationLock)
            {
                var now = m_timeProvider.GetUtcNow();
                CleanupExpiredCore(now);
                if (!m_transfers.TryGetValue(correlationId, out var transfer)
                    || transfer.WindowHandle != windowHandle)
                {
                    return false;
                }
                if (transfer.State == PendingWindowTransferState.DestinationObserved
                    && transfer.DestinationAddedObserved)
                {
                    return true;
                }
                if (transfer.IsTerminal
                    || transfer.State is not (PendingWindowTransferState.Reserved
                        or PendingWindowTransferState.Moving
                        or PendingWindowTransferState.DestinationObserved))
                {
                    return false;
                }
                m_transfers[correlationId] = transfer with
                {
                    State = PendingWindowTransferState.DestinationObserved,
                    DestinationAddedObserved = true,
                    UpdatedAt = now,
                };
                return true;
            }
        }

        public bool ObserveSourceRemoved(
            IntPtr windowHandle,
            IVirtualDesktop sourceDesktop,
            IDisplay sourceDisplay,
            out PendingWindowTransfer transfer)
        {
            ArgumentNullException.ThrowIfNull(sourceDesktop);
            ArgumentNullException.ThrowIfNull(sourceDisplay);
            if (windowHandle == IntPtr.Zero)
            {
                throw new ArgumentException(
                    "The transferred window handle must not be zero.",
                    nameof(windowHandle));
            }
            VerifyAccessAndNotDisposed();
            ValidateWorkspaceOwner(sourceDesktop.Workspace, nameof(sourceDesktop));
            ValidateWorkspaceOwner(sourceDisplay.Workspace, nameof(sourceDisplay));
            lock (m_mutationLock)
            {
                CleanupExpiredCore(m_timeProvider.GetUtcNow());
                if ((!m_activeTransfersByWindow.TryGetValue(windowHandle, out var correlationId)
                        && !m_recentTransfersByWindow.TryGetValue(windowHandle, out correlationId))
                    || !m_transfers.TryGetValue(correlationId, out transfer!))
                {
                    transfer = null!;
                    return false;
                }
                if (!MasterSatelliteDisplayEligibility.DesktopsMatch(
                        transfer.SourceDesktop,
                        sourceDesktop)
                    || !MasterSatelliteDisplayEligibility.DisplaysMatch(
                        transfer.SourceDisplay,
                        sourceDisplay))
                {
                    transfer = null!;
                    return false;
                }
                if (transfer.SourceRemovedObserved)
                {
                    return true;
                }
                transfer = transfer with
                {
                    SourceRemovedObserved = true,
                    UpdatedAt = m_timeProvider.GetUtcNow(),
                };
                m_transfers[correlationId] = transfer;
                return true;
            }
        }

        public bool Commit(Guid correlationId, IntPtr windowHandle)
        {
            return CompleteTransfer(
                correlationId,
                windowHandle,
                PendingWindowTransferState.Committed,
                null);
        }

        public bool Fail(Guid correlationId, IntPtr windowHandle, string reason)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            return CompleteTransfer(
                correlationId,
                windowHandle,
                PendingWindowTransferState.Failed,
                reason);
        }

        public bool Cancel(Guid correlationId, IntPtr windowHandle, string reason)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            return CompleteTransfer(
                correlationId,
                windowHandle,
                PendingWindowTransferState.Cancelled,
                reason);
        }

        public bool WindowClosed(IntPtr windowHandle)
        {
            VerifyAccessAndNotDisposed();
            m_desktopCreations.ForgetWindow(windowHandle);
            lock (m_mutationLock)
            {
                bool changed = false;
                if (m_activeTransfersByWindow.TryGetValue(
                        windowHandle,
                        out var correlationId))
                {
                    changed = CompleteTransferCore(
                        correlationId,
                        PendingWindowTransferState.Cancelled,
                        "WindowClosed",
                        m_timeProvider.GetUtcNow());
                }

                // Destruction is an explicit HWND-generation boundary. Retain
                // the correlation record/tombstone for diagnostics and late
                // correlation-id rejection, but never associate a newly created
                // window that reuses this native handle with the old endpoint.
                changed |= m_recentTransfersByWindow.Remove(windowHandle);
                return changed;
            }
        }

        public int DesktopRemoved(IVirtualDesktop desktop)
        {
            ArgumentNullException.ThrowIfNull(desktop);
            VerifyAccessAndNotDisposed();
            m_desktopCreations.DesktopRemoved(desktop);
            lock (m_mutationLock)
            {
                int affected = 0;
                var affectedKeys = m_capacities.Keys
                    .Where(key => MasterSatelliteDisplayEligibility.DesktopsMatch(
                        key.VirtualDesktop,
                        desktop))
                    .Concat(m_displayParticipants.Keys.Select(display =>
                        new LayoutStateKey(desktop, display)))
                    .Distinct(LayoutStateKeyIdentityComparer.Instance)
                    .ToArray();
                foreach (var key in affectedKeys)
                {
                    m_capacities.Remove(key);
                    RemoveCommittedShadowsForLayoutCore(key);
                    if (m_runtimeStates.Remove(key))
                    {
                        affected++;
                    }
                }
                foreach (var transfer in m_transfers.Values
                    .Where(item => !item.IsTerminal
                        && (MasterSatelliteDisplayEligibility.DesktopsMatch(
                                item.SourceDesktop,
                                desktop)
                            || MasterSatelliteDisplayEligibility.DesktopsMatch(
                                item.TargetDesktop,
                                desktop)))
                    .ToArray())
                {
                    if (CompleteTransferCore(
                        transfer.CorrelationId,
                        PendingWindowTransferState.Cancelled,
                        "DesktopRemoved",
                        m_timeProvider.GetUtcNow()))
                    {
                        affected++;
                    }
                }
                return affected;
            }
        }

        public int CleanupExpired()
        {
            VerifyAccessAndNotDisposed();
            lock (m_mutationLock)
            {
                return CleanupExpiredCore(m_timeProvider.GetUtcNow());
            }
        }

        public IReadOnlyList<PendingWindowTransfer> SnapshotTransfers()
        {
            VerifyAccessAndNotDisposed();
            lock (m_mutationLock)
            {
                return Array.AsReadOnly(m_transfers.Values
                    .OrderBy(item => item.CreatedAt)
                    .Select(item => item with { })
                    .ToArray());
            }
        }

        public IReadOnlyList<AlgorithmicSlotReservation> SnapshotReservations()
        {
            VerifyAccessAndNotDisposed();
            lock (m_mutationLock)
            {
                return Array.AsReadOnly(m_reservations.Values
                    .OrderBy(item => item.CreatedAt)
                    .Select(item => item with { })
                    .ToArray());
            }
        }

        public AlgorithmicDesktopCreationAvailability CanCreateDesktop(
            int maxAutoCreatedDesktops)
        {
            VerifyAccessAndNotDisposed();
            return m_desktopCreations.CanCreate(maxAutoCreatedDesktops);
        }

        public AlgorithmicDesktopCreationClaimResult TryClaimDesktopCreation(
            IntPtr windowHandle,
            Guid correlationId,
            int maxAutoCreatedDesktops)
        {
            VerifyAccessAndNotDisposed();
            return m_desktopCreations.TryClaim(
                windowHandle,
                correlationId,
                maxAutoCreatedDesktops);
        }

        public AlgorithmicDesktopCreationCompletionResult CompleteDesktopCreation(
            AlgorithmicDesktopCreationClaim claim,
            IVirtualDesktop desktop)
        {
            VerifyAccessAndNotDisposed();
            return m_desktopCreations.CompleteSuccess(claim, desktop);
        }

        public AlgorithmicDesktopCreationCompletionResult FailDesktopCreation(
            AlgorithmicDesktopCreationClaim claim)
        {
            VerifyAccessAndNotDisposed();
            return m_desktopCreations.CompleteFailure(claim);
        }

        public AlgorithmicDesktopCreationSnapshot SnapshotDesktopCreations()
        {
            VerifyAccessAndNotDisposed();
            return m_desktopCreations.GetSnapshot();
        }

        public bool TryAdd(LayoutStateKey key, MasterSatelliteRuntimeState state)
        {
            ArgumentNullException.ThrowIfNull(state);
            VerifyAccessAndNotDisposed();
            lock (m_mutationLock)
            {
                return m_runtimeStates.TryAdd(key, state);
            }
        }

        public bool TryGet(LayoutStateKey key, out MasterSatelliteRuntimeState state)
        {
            VerifyAccessAndNotDisposed();
            lock (m_mutationLock)
            {
                return m_runtimeStates.TryGet(key, out state!);
            }
        }

        public bool Remove(LayoutStateKey key)
        {
            VerifyAccessAndNotDisposed();
            lock (m_mutationLock)
            {
                m_capacities.Remove(key);
                RemoveCommittedShadowsForLayoutCore(key);
                CancelTransfersForLayoutCore(key, "LayoutRemoved");
                return m_runtimeStates.Remove(key);
            }
        }

        public int RemoveDisplay(IDisplay display)
        {
            ArgumentNullException.ThrowIfNull(display);
            VerifyAccessAndNotDisposed();
            lock (m_mutationLock)
            {
                return RemoveDisplayCore(display, "DisplayStateRemoved");
            }
        }

        public IReadOnlyList<MasterSatelliteRuntimeEntry> SnapshotForDisplay(IDisplay display)
        {
            ArgumentNullException.ThrowIfNull(display);
            VerifyAccessAndNotDisposed();
            lock (m_mutationLock)
            {
                return m_runtimeStates.SnapshotForDisplay(display);
            }
        }

        public void Clear()
        {
            VerifyAccessAndNotDisposed();
            lock (m_mutationLock)
            {
                foreach (var transfer in m_transfers.Values.Where(item => !item.IsTerminal).ToArray())
                {
                    CompleteTransferCore(
                        transfer.CorrelationId,
                        PendingWindowTransferState.Cancelled,
                        "RuntimeStateCleared",
                        m_timeProvider.GetUtcNow());
                }
                m_runtimeStates.Clear();
                m_capacities.Clear();
                m_committedSlotShadows.Clear();
            }
        }

        public void Dispose()
        {
            m_dispatcher.VerifyAccess();
            DispatcherOperation? pendingNotificationDispatch;
            lock (m_mutationLock)
            {
                if (m_disposed)
                {
                    return;
                }
                m_disposed = true;
                m_cleanupTimer.Stop();
                m_cleanupTimer.Tick -= OnCleanupTimerTick;
                m_displayParticipants.Clear();
                m_runtimeStates.Clear();
                m_capacities.Clear();
                m_reservations.Clear();
                m_committedSlotShadows.Clear();
                m_transfers.Clear();
                m_activeTransfersByWindow.Clear();
                m_recentTransfersByWindow.Clear();
                m_retiredCorrelations.Clear();
                m_desktopCreations.Clear();
                m_pendingTerminalNotifications.Clear();
                m_transferTerminated = null;
                pendingNotificationDispatch = m_terminalNotificationDispatch;
                m_terminalNotificationDispatch = null;
            }
            pendingNotificationDispatch?.Abort();
            FloatingWindows.Clear();
        }

        private void OnCleanupTimerTick(object? sender, EventArgs e)
        {
            if (m_disposed)
            {
                return;
            }
            CleanupExpired();
        }

        private bool Transition(
            Guid correlationId,
            IntPtr windowHandle,
            PendingWindowTransferState nextState,
            params PendingWindowTransferState[] allowedStates)
        {
            ValidateTransferIdentity(correlationId, windowHandle);
            VerifyAccessAndNotDisposed();
            lock (m_mutationLock)
            {
                var now = m_timeProvider.GetUtcNow();
                CleanupExpiredCore(now);
                if (!m_transfers.TryGetValue(correlationId, out var transfer)
                    || transfer.WindowHandle != windowHandle)
                {
                    return false;
                }
                if (transfer.State == nextState)
                {
                    return true;
                }
                if (transfer.IsTerminal || !allowedStates.Contains(transfer.State))
                {
                    return false;
                }
                m_transfers[correlationId] = transfer with
                {
                    State = nextState,
                    UpdatedAt = now,
                };
                return true;
            }
        }

        private bool CompleteTransfer(
            Guid correlationId,
            IntPtr windowHandle,
            PendingWindowTransferState terminalState,
            string? reason)
        {
            ValidateTransferIdentity(correlationId, windowHandle);
            VerifyAccessAndNotDisposed();
            lock (m_mutationLock)
            {
                var now = m_timeProvider.GetUtcNow();
                CleanupExpiredCore(now);
                if (!m_transfers.TryGetValue(correlationId, out var transfer)
                    || transfer.WindowHandle != windowHandle)
                {
                    return false;
                }
                return CompleteTransferCore(
                    correlationId,
                    terminalState,
                    reason,
                    now);
            }
        }

        private bool CompleteTransferCore(
            Guid correlationId,
            PendingWindowTransferState terminalState,
            string? reason,
            DateTimeOffset now)
        {
            if (!m_transfers.TryGetValue(correlationId, out var transfer))
            {
                return false;
            }
            if (transfer.State == terminalState)
            {
                return true;
            }
            if (transfer.IsTerminal)
            {
                return false;
            }
            if (terminalState == PendingWindowTransferState.Committed
                && transfer.State != PendingWindowTransferState.DestinationObserved)
            {
                return false;
            }

            if (transfer.ReservationId is Guid reservationId)
            {
                if (m_reservations.Remove(reservationId, out var releasedReservation)
                    && terminalState == PendingWindowTransferState.Committed
                    && !IsPhysicalSlotOccupiedCore(releasedReservation))
                {
                    m_committedSlotShadows[reservationId] = releasedReservation with
                    {
                        Deadline = now + m_transferTimeout,
                    };
                }
                else if (releasedReservation != null
                    && terminalState != PendingWindowTransferState.Committed)
                {
                    CancelDependentReservationsCore(
                        releasedReservation,
                        now);
                }
            }
            if (m_activeTransfersByWindow.TryGetValue(
                    transfer.WindowHandle,
                    out var activeCorrelation)
                && activeCorrelation == correlationId)
            {
                m_activeTransfersByWindow.Remove(transfer.WindowHandle);
            }
            m_recentTransfersByWindow[transfer.WindowHandle] = correlationId;
            m_retiredCorrelations[correlationId] = now + TerminalRetention;
            var terminalTransfer = transfer with
            {
                State = terminalState,
                UpdatedAt = now,
                TerminalReason = reason,
            };
            m_transfers[correlationId] = terminalTransfer;
            QueueTerminalNotificationCore(terminalTransfer);
            return true;
        }

        /// <summary>
        /// Must be called with <see cref="m_mutationLock"/> held. Dispatcher
        /// callbacks are queued, rather than invoked inline, so even terminal
        /// transitions nested inside reservation reconciliation cannot call an
        /// external subscriber while coordinator state is being mutated.
        /// </summary>
        private void QueueTerminalNotificationCore(PendingWindowTransfer transfer)
        {
            m_pendingTerminalNotifications.Enqueue(transfer with { });
            if (m_terminalNotificationDispatch != null
                || m_disposed
                || m_dispatcher.HasShutdownStarted
                || m_dispatcher.HasShutdownFinished)
            {
                return;
            }

            try
            {
                m_terminalNotificationDispatch = m_dispatcher.BeginInvoke(
                    DispatcherPriority.Normal,
                    new Action(DispatchTerminalNotifications));
            }
            catch (InvalidOperationException e)
            {
                m_pendingTerminalNotifications.Clear();
                Trace.TraceError(
                    $"Unable to schedule an algorithmic transfer terminal notification: {e}");
            }
        }

        private void DispatchTerminalNotifications()
        {
            m_dispatcher.VerifyAccess();
            PendingWindowTransfer[] notifications;
            lock (m_mutationLock)
            {
                m_terminalNotificationDispatch = null;
                if (m_disposed)
                {
                    m_pendingTerminalNotifications.Clear();
                    return;
                }
                notifications = m_pendingTerminalNotifications.ToArray();
                m_pendingTerminalNotifications.Clear();
            }

            foreach (var notification in notifications)
            {
                Delegate[] subscribers;
                lock (m_mutationLock)
                {
                    if (m_disposed)
                    {
                        return;
                    }
                    subscribers = m_transferTerminated?.GetInvocationList() ?? [];
                }

                foreach (var subscriber in subscribers)
                {
                    lock (m_mutationLock)
                    {
                        if (m_disposed)
                        {
                            return;
                        }
                    }
                    try
                    {
                        ((Action<PendingWindowTransfer>)subscriber)(notification);
                    }
                    catch (Exception e)
                    {
                        Trace.TraceError(
                            $"Algorithmic transfer terminal notification subscriber failed: {e}");
                    }
                }
            }
        }

        private bool TryGetCapacityCore(
            LayoutStateKey layoutKey,
            out CoordinatorCapacitySnapshot capacity)
        {
            if (!m_capacities.TryGetValue(layoutKey, out var publication))
            {
                capacity = null!;
                return false;
            }
            var physical = publication.Snapshot;

            var occupiedOrdinals = new HashSet<int>(Enumerable.Range(
                0,
                Math.Max(0, physical.OccupiedSlots)));
            foreach (var reservation in m_reservations.Values
                .Concat(m_committedSlotShadows.Values)
                .Where(item => LayoutKeysMatch(item.LayoutKey, layoutKey)))
            {
                occupiedOrdinals.Add(GetSlotOrdinal(
                    reservation.Role,
                    reservation.SatelliteIndex));
            }
            int reserved = occupiedOrdinals.Count - Math.Max(0, physical.OccupiedSlots);
            int nextOrdinal = Enumerable.Range(0, Math.Max(0, physical.TotalCapacity))
                .FirstOrDefault(ordinal => !occupiedOrdinals.Contains(ordinal), -1);
            int occupied = occupiedOrdinals.Count;
            bool kindSupported = physical.LayoutKind is WorkspaceLayoutKind.Empty
                or WorkspaceLayoutKind.Canonical;
            bool canAccept = kindSupported
                && physical.CanAcceptWindow
                && nextOrdinal >= 0;
            ReservedRole? role = canAccept
                ? nextOrdinal == 0
                    ? ReservedRole.Master
                    : ReservedRole.Satellite
                : null;
            int? satelliteIndex = role == ReservedRole.Satellite
                ? nextOrdinal - 1
                : null;
            string? reason = canAccept
                ? null
                : physical.DiagnosticReason
                    ?? (occupied >= physical.TotalCapacity
                        ? "All physical and reserved layout slots are occupied."
                        : "The destination layout cannot safely accept a reservation.");
            capacity = new CoordinatorCapacitySnapshot(
                layoutKey,
                physical.LayoutKind,
                canAccept,
                role,
                satelliteIndex,
                physical.OccupiedSlots,
                reserved,
                physical.TotalCapacity,
                physical.Revision,
                reason);
            return true;
        }

        private bool IsSlotReservedCore(
            LayoutStateKey layoutKey,
            ReservedRole role,
            int? satelliteIndex)
        {
            return m_reservations.Values.Any(item =>
                LayoutKeysMatch(item.LayoutKey, layoutKey)
                && item.Role == role
                && item.SatelliteIndex == satelliteIndex);
        }

        private int CleanupExpiredCore(DateTimeOffset now)
        {
            int cleaned = 0;
            foreach (var transfer in m_transfers.Values
                .Where(item => !item.IsTerminal && item.Deadline <= now)
                .ToArray())
            {
                if (CompleteTransferCore(
                    transfer.CorrelationId,
                    PendingWindowTransferState.Failed,
                    "TransferTimedOut",
                    now))
                {
                    cleaned++;
                }
            }
            foreach (var transfer in m_transfers.Values
                .Where(item => item.IsTerminal && item.UpdatedAt + TerminalRetention <= now)
                .ToArray())
            {
                m_transfers.Remove(transfer.CorrelationId);
                if (m_recentTransfersByWindow.TryGetValue(
                        transfer.WindowHandle,
                        out var recentCorrelation)
                    && recentCorrelation == transfer.CorrelationId)
                {
                    m_recentTransfersByWindow.Remove(transfer.WindowHandle);
                }
            }
            foreach (var correlationId in m_retiredCorrelations
                .Where(item => item.Value <= now)
                .Select(item => item.Key)
                .ToArray())
            {
                m_retiredCorrelations.Remove(correlationId);
            }
            foreach (var shadow in m_committedSlotShadows.Values
                .Where(item => item.Deadline <= now)
                .ToArray())
            {
                m_committedSlotShadows.Remove(shadow.ReservationId);
                m_capacities.Remove(shadow.LayoutKey);
            }
            return cleaned;
        }

        private int RemoveDisplayCore(IDisplay display, string reason)
        {
            int removedStates = m_runtimeStates.RemoveDisplay(display);
            foreach (var key in m_capacities.Keys
                .Where(key => MasterSatelliteDisplayEligibility.DisplaysMatch(
                    key.Display,
                    display))
                .ToArray())
            {
                m_capacities.Remove(key);
                RemoveCommittedShadowsForLayoutCore(key);
            }
            foreach (var transfer in m_transfers.Values
                .Where(item => !item.IsTerminal
                    && (MasterSatelliteDisplayEligibility.DisplaysMatch(
                            item.SourceDisplay,
                            display)
                        || MasterSatelliteDisplayEligibility.DisplaysMatch(
                            item.TargetDisplay,
                            display)))
                .ToArray())
            {
                CompleteTransferCore(
                    transfer.CorrelationId,
                    PendingWindowTransferState.Cancelled,
                    reason,
                    m_timeProvider.GetUtcNow());
            }
            return removedStates;
        }

        private void CancelTransfersForLayoutCore(LayoutStateKey layoutKey, string reason)
        {
            foreach (var transfer in m_transfers.Values
                .Where(item => !item.IsTerminal
                    && (LayoutKeysMatch(
                            new LayoutStateKey(item.SourceDesktop, item.SourceDisplay),
                            layoutKey)
                        || LayoutKeysMatch(
                            new LayoutStateKey(item.TargetDesktop, item.TargetDisplay),
                            layoutKey)))
                .ToArray())
            {
                CompleteTransferCore(
                    transfer.CorrelationId,
                    PendingWindowTransferState.Cancelled,
                    reason,
                    m_timeProvider.GetUtcNow());
            }
        }

        private void CancelDependentReservationsCore(
            AlgorithmicSlotReservation released,
            DateTimeOffset now)
        {
            int releasedOrdinal = GetSlotOrdinal(
                released.Role,
                released.SatelliteIndex);
            var dependentCorrelations = m_reservations.Values
                .Where(item => LayoutKeysMatch(item.LayoutKey, released.LayoutKey)
                    && GetSlotOrdinal(item.Role, item.SatelliteIndex) > releasedOrdinal)
                .OrderByDescending(item => GetSlotOrdinal(item.Role, item.SatelliteIndex))
                .Select(item => item.CorrelationId)
                .ToArray();
            foreach (var correlationId in dependentCorrelations)
            {
                CompleteTransferCore(
                    correlationId,
                    PendingWindowTransferState.Cancelled,
                    "PrecedingReservationReleased",
                    now);
            }
        }

        private void ReconcileCapacityCore(
            LayoutStateKey layoutKey,
            MasterSatelliteCapacitySnapshot physical,
            DateTimeOffset now)
        {
            bool supported = physical.LayoutKind is WorkspaceLayoutKind.Empty
                or WorkspaceLayoutKind.Canonical;
            foreach (var shadow in m_committedSlotShadows.Values
                .Where(item => LayoutKeysMatch(item.LayoutKey, layoutKey))
                .ToArray())
            {
                int ordinal = GetSlotOrdinal(shadow.Role, shadow.SatelliteIndex);
                if (!supported
                    || ordinal < physical.OccupiedSlots
                    || ordinal >= physical.TotalCapacity)
                {
                    m_committedSlotShadows.Remove(shadow.ReservationId);
                }
            }

            var invalidReservations = m_reservations.Values
                .Where(item => LayoutKeysMatch(item.LayoutKey, layoutKey))
                .Where(item =>
                {
                    int ordinal = GetSlotOrdinal(item.Role, item.SatelliteIndex);
                    return !supported
                        || ordinal >= physical.TotalCapacity
                        || (!physical.CanAcceptWindow
                            && ordinal >= physical.OccupiedSlots);
                })
                .OrderBy(item => GetSlotOrdinal(item.Role, item.SatelliteIndex))
                .ToArray();
            foreach (var reservation in invalidReservations)
            {
                if (m_reservations.ContainsKey(reservation.ReservationId))
                {
                    CompleteTransferCore(
                        reservation.CorrelationId,
                        PendingWindowTransferState.Cancelled,
                        "DestinationCapacityChanged",
                        now);
                }
            }
        }

        private bool IsPhysicalSlotOccupiedCore(AlgorithmicSlotReservation reservation)
        {
            return m_capacities.TryGetValue(reservation.LayoutKey, out var publication)
                && publication.Snapshot.LayoutKind == WorkspaceLayoutKind.Canonical
                && GetSlotOrdinal(reservation.Role, reservation.SatelliteIndex)
                    < publication.Snapshot.OccupiedSlots;
        }

        private void RemoveCommittedShadowsForLayoutCore(LayoutStateKey layoutKey)
        {
            foreach (var reservationId in m_committedSlotShadows
                .Where(item => LayoutKeysMatch(item.Value.LayoutKey, layoutKey))
                .Select(item => item.Key)
                .ToArray())
            {
                m_committedSlotShadows.Remove(reservationId);
            }
        }

        private static int GetSlotOrdinal(ReservedRole role, int? satelliteIndex)
        {
            return role switch
            {
                ReservedRole.Master when satelliteIndex == null => 0,
                ReservedRole.Satellite when satelliteIndex >= 0 => satelliteIndex.Value + 1,
                _ => throw new InvalidOperationException(
                    "The reservation role and satellite index are inconsistent."),
            };
        }

        private void VerifyAccessAndNotDisposed()
        {
            m_dispatcher.VerifyAccess();
            ObjectDisposedException.ThrowIf(m_disposed, this);
        }

        private void ValidateTransferOwnership(
            IVirtualDesktop sourceDesktop,
            IDisplay sourceDisplay,
            LayoutStateKey targetLayout)
        {
            ValidateWorkspaceOwner(sourceDesktop.Workspace, nameof(sourceDesktop));
            ValidateWorkspaceOwner(sourceDisplay.Workspace, nameof(sourceDisplay));
            ValidateWorkspaceOwner(targetLayout.VirtualDesktop.Workspace, nameof(targetLayout));
            ValidateWorkspaceOwner(targetLayout.Display.Workspace, nameof(targetLayout));
            if (!MasterSatelliteDisplayEligibility.DisplaysMatch(
                sourceDisplay,
                targetLayout.Display))
            {
                throw new ArgumentException(
                    "An overflow transfer must preserve its source display.",
                    nameof(targetLayout));
            }
        }

        private void ValidateWorkspaceOwner(IWorkspace? owner, string parameterName)
        {
            if (owner != null && !ReferenceEquals(owner, m_workspace))
            {
                throw new ArgumentException(
                    "The algorithmic layout object belongs to a different workspace.",
                    parameterName);
            }
        }

        private static void ValidateTransferIdentity(Guid correlationId, IntPtr windowHandle)
        {
            if (correlationId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The transfer correlation identity must not be empty.",
                    nameof(correlationId));
            }
            if (windowHandle == IntPtr.Zero)
            {
                throw new ArgumentException(
                    "The transferred window handle must not be zero.",
                    nameof(windowHandle));
            }
        }

        private static bool MatchesRequest(
            PendingWindowTransfer transfer,
            IntPtr windowHandle,
            IVirtualDesktop sourceDesktop,
            IDisplay sourceDisplay,
            LayoutStateKey targetLayout)
        {
            return transfer.WindowHandle == windowHandle
                && MasterSatelliteDisplayEligibility.DesktopsMatch(
                    transfer.SourceDesktop,
                    sourceDesktop)
                && MasterSatelliteDisplayEligibility.DisplaysMatch(
                    transfer.SourceDisplay,
                    sourceDisplay)
                && MasterSatelliteDisplayEligibility.DesktopsMatch(
                    transfer.TargetDesktop,
                    targetLayout.VirtualDesktop)
                && MasterSatelliteDisplayEligibility.DisplaysMatch(
                    transfer.TargetDisplay,
                    targetLayout.Display);
        }

        private static bool LayoutKeysMatch(LayoutStateKey left, LayoutStateKey right)
        {
            return LayoutStateKeyIdentityComparer.Instance.Equals(left, right);
        }
    }

    internal readonly record struct PublishedCapacity(
        MasterSatelliteCapacitySnapshot Snapshot,
        long PublicationSequence);

    internal sealed class AlgorithmicLayoutDisplayRegistration : IDisposable
    {
        private AlgorithmicLayoutCoordinator? m_coordinator;
        private readonly object m_participant;
        private readonly Guid m_registrationToken;

        public IDisplay Display { get; }

        internal AlgorithmicLayoutDisplayRegistration(
            AlgorithmicLayoutCoordinator coordinator,
            IDisplay display,
            object participant,
            Guid registrationToken)
        {
            m_coordinator = coordinator;
            Display = display;
            m_participant = participant;
            m_registrationToken = registrationToken;
        }

        public void Dispose()
        {
            var coordinator = m_coordinator;
            if (coordinator == null)
            {
                return;
            }
            coordinator.UnregisterDisplay(
                Display,
                m_participant,
                m_registrationToken);
            m_coordinator = null;
        }
    }

    internal readonly record struct DisplayParticipantEntry(
        Guid RegistrationToken,
        object Participant);

    internal sealed class DisplayIdentityComparer : IEqualityComparer<IDisplay>
    {
        public static DisplayIdentityComparer Instance { get; } = new();

        private DisplayIdentityComparer()
        {
        }

        public bool Equals(IDisplay? x, IDisplay? y)
        {
            return MasterSatelliteDisplayEligibility.DisplaysMatch(x, y);
        }

        public int GetHashCode(IDisplay obj)
        {
            return EqualityComparer<IDisplay>.Default.GetHashCode(obj);
        }
    }
}
