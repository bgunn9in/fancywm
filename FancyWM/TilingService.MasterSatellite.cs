using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

using FancyWM.AlgorithmicLayouts;
using FancyWM.Models;
using FancyWM.Utilities;

using WinMan;

namespace FancyWM
{
    internal partial class TilingService
    {
        private const int MasterSatelliteDeferredOwnershipProbeLimit = 6;
        private const int MasterSatelliteIncomingOwnershipProbeLimit = 6;
        private static readonly TimeSpan MasterSatelliteIncomingOwnershipProbeInterval
            = TimeSpan.FromMilliseconds(50);

        private readonly AlgorithmicLayoutCoordinator m_algorithmicLayoutCoordinator;
        private readonly AlgorithmicLayoutDisplayRegistration m_algorithmicDisplayRegistration;
        private readonly AlgorithmicWindowTransferOrchestrator m_algorithmicWindowTransfers;
        private readonly AlgorithmicWindowTransferEventTracker m_algorithmicWindowEvents;
        private readonly AlgorithmicDesktopCreationOrchestrator m_algorithmicDesktopCreations;
        private readonly MasterSatelliteRuntimeLifecycle m_masterSatelliteLifecycle;
        private readonly MasterSatelliteDropController m_masterSatelliteDrops;
        private IReadOnlySet<IWindow> m_masterSatelliteDropPreviewWindows = EmptyWindowSet;
        private long m_masterSatelliteCapacityPublicationSequence;
        private bool m_masterSatelliteLifecycleReady;
        private bool m_masterSatelliteLifecycleDisposed;
        private bool m_drainingAlgorithmicDestinationArrivals;
        private readonly Queue<MasterSatelliteCapacityTransitionWorkItem>
            m_masterSatelliteCapacityTransitions = [];
        private readonly Dictionary<Guid, MasterSatelliteExistingWindowTransferContext>
            m_masterSatelliteExistingWindowTransfers = [];
        private readonly Dictionary<Guid, MasterSatelliteWorkspaceTransferRestorePoint>
            m_masterSatelliteDestinationRestorePoints = [];
        private readonly Dictionary<IntPtr, MasterSatelliteIncomingOwnershipProbe>
            m_masterSatelliteIncomingOwnershipProbes = [];
        private readonly ConditionalWeakTable<IWindow, RetiredWindowGenerationMarker>
            m_retiredMasterSatelliteWindowGenerations = new();
        private readonly Func<LayoutStateKey, MasterSatelliteCapacitySnapshot, long, bool>
            m_masterSatelliteCapacityPublisher;
        private bool m_processingMasterSatelliteCapacityTransitions;
        private MasterSatelliteLayoutSettings? m_deferredMasterSatelliteSettings;
        private List<MasterSatelliteLifecycleResult>? m_capacityTransitionLifecycleResults;
        private MasterSatelliteSettingsTransition? m_capacityTransitionSettingsTransition;
        private IReadOnlyList<MasterSatelliteCapacitySourcePlan>?
            m_capacityTransitionSourcePlans;
        private IVirtualDesktop? m_capacityTransitionCurrentDesktop;
        private SatelliteLayoutOrientation? m_capacityTransitionPendingOrientation;
        private IVirtualDesktop? m_capacityTransitionOrientationDesktop;

        private sealed class RetiredWindowGenerationMarker
        {
        }

        internal bool IsAlgorithmicTransferLockHeldByCurrentThread
            => m_backendLock.IsHeldByCurrentThread
                || m_windowSetLock.IsHeldByCurrentThread
                || m_newWindowSetLock.IsHeldByCurrentThread
                || m_ignoreRepositionSetLock.IsHeldByCurrentThread
                || m_savedLocationsLock.IsHeldByCurrentThread
                || m_workspaceFloatingWindows.IsLockHeldByCurrentThread
                || m_algorithmicLayoutCoordinator.IsMutationLockHeldByCurrentThread;

        private bool DeferToTilingDispatcher(Action action, string operation)
        {
            if (m_dispatcher.CheckAccess())
            {
                return false;
            }
            QueueTilingDispatcherAction(action, operation);
            return true;
        }

        private void QueueTilingDispatcherAction(
            Action action,
            string operation,
            DispatcherPriority priority = DispatcherPriority.DataBind)
        {
            ArgumentNullException.ThrowIfNull(action);
            ArgumentException.ThrowIfNullOrWhiteSpace(operation);
            try
            {
                m_dispatcher.BeginInvoke(() =>
                {
                    if (m_disposed)
                    {
                        return;
                    }
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        m_logger.Error(
                            ex,
                            "Deferred tiling event {Operation} failed for display {Display}",
                            operation,
                            m_display);
                    }
                }, priority);
            }
            catch (Exception ex)
            {
                m_logger.Debug(
                    ex,
                    "Dropping tiling event {Operation} because its Dispatcher is shutting down",
                    operation);
            }
        }

        private bool TryRememberMasterSatelliteWindowHandle(
            IWindow window,
            out IntPtr windowHandle)
        {
            ArgumentNullException.ThrowIfNull(window);
            windowHandle = IntPtr.Zero;
            if (IsRetiredMasterSatelliteWindowGeneration(window)
                || !m_algorithmicWindowEvents.TryGetStableWindowHandle(
                    window,
                    out windowHandle,
                    out var replacedWindowGeneration,
                    out var previousWindowGeneration))
            {
                return false;
            }
            if (replacedWindowGeneration)
            {
                ResetReusedMasterSatelliteWindowHandle(
                    window,
                    windowHandle,
                    previousWindowGeneration!);
            }
            return IsCurrentMasterSatelliteWindowGeneration(
                window,
                windowHandle);
        }

        private bool TryRememberMasterSatelliteWindowHandle(
            IWindow window,
            out IntPtr windowHandle,
            out bool replacedWindowGeneration,
            out IWindow? previousWindowGeneration)
        {
            ArgumentNullException.ThrowIfNull(window);
            return m_algorithmicWindowEvents.TryGetStableWindowHandle(
                window,
                out windowHandle,
                out replacedWindowGeneration,
                out previousWindowGeneration);
        }

        private bool IsRetiredMasterSatelliteWindowGeneration(IWindow window)
        {
            ArgumentNullException.ThrowIfNull(window);
            return m_retiredMasterSatelliteWindowGenerations.TryGetValue(
                window,
                out _);
        }

        private void RetireMasterSatelliteWindowGeneration(IWindow window)
        {
            ArgumentNullException.ThrowIfNull(window);
            m_retiredMasterSatelliteWindowGenerations.GetValue(
                window,
                static _ => new RetiredWindowGenerationMarker());
        }

        private bool IsCurrentMasterSatelliteWindowGeneration(IWindow window)
        {
            ArgumentNullException.ThrowIfNull(window);
            if (IsRetiredMasterSatelliteWindowGeneration(window)
                || !m_algorithmicWindowEvents.TryGetRememberedWindowHandle(
                    window,
                    out var stableWindowHandle))
            {
                return false;
            }
            return IsCurrentMasterSatelliteWindowGeneration(
                window,
                stableWindowHandle);
        }

        private bool IsCurrentMasterSatelliteWindowGeneration(
            IWindow window,
            IntPtr stableWindowHandle)
        {
            ArgumentNullException.ThrowIfNull(window);
            return stableWindowHandle != IntPtr.Zero
                && !IsRetiredMasterSatelliteWindowGeneration(window)
                && m_algorithmicWindowEvents.TryGetWindow(
                    stableWindowHandle,
                    out var currentWindowGeneration)
                && ReferenceEquals(currentWindowGeneration, window);
        }

        private void ResetReusedMasterSatelliteWindowHandle(
            IWindow window,
            IntPtr windowHandle,
            IWindow previousWindowGeneration)
        {
            // The replacement wrapper is now the event tracker's current HWND
            // generation. Purge the exact previous wrapper from reference-keyed
            // service state and from the equality-keyed backend before the new
            // wrapper can be registered. Late old events are ignored separately.
            RetireMasterSatelliteWindowGeneration(previousWindowGeneration);
            CancelMasterSatelliteIncomingOwnershipProbe(
                previousWindowGeneration,
                windowHandle);
            UnbindEventHandlers(previousWindowGeneration);
            UntrackWindowLifetime(previousWindowGeneration);
            using (m_newWindowSetLock.EnterScope())
            {
                m_newWindowSet.Remove(previousWindowGeneration);
            }
            using (m_windowSetLock.EnterScope())
            {
                m_windowSet.Remove(previousWindowGeneration);
            }
            using (m_savedLocationsLock.EnterScope())
            {
                m_savedLocations.Remove(previousWindowGeneration);
            }
            using (m_ignoreRepositionSetLock.EnterScope())
            {
                m_ignoreRepositionSet.Remove(previousWindowGeneration);
            }
            if (ReferenceEquals(m_movingWindow, previousWindowGeneration))
            {
                m_movingWindow = null;
                m_currentInteraction = UserInteraction.None;
                m_masterSatelliteDropPreviewWindows = EmptyWindowSet;
            }

            MasterSatelliteLocalMutationResult? removal = null;
            bool backendChanged = false;
            using (m_backendLock.EnterScope())
            {
                var previousNode = m_backend.FindWindow(previousWindowGeneration);
                if (ReferenceEquals(
                    previousNode?.WindowReference,
                    previousWindowGeneration))
                {
                    removal = RemoveMasterSatelliteWindowLocked(
                        previousWindowGeneration,
                        preserveOriginalPosition: false);
                    if (!removal.Attempted)
                    {
                        m_backend.UnregisterWindow(previousWindowGeneration);
                        backendChanged = true;
                    }
                    else
                    {
                        backendChanged = removal.Succeeded;
                        if (!backendChanged
                            && removal.Disposition
                                == MasterSatelliteLocalMutationDisposition.Rejected)
                        {
                            backendChanged = RecoverRejectedMasterSatelliteRemovalLocked(
                                previousWindowGeneration,
                                removal);
                        }
                    }
                }
                m_backend.ForgetMasterSatelliteOriginalPosition(
                    previousWindowGeneration);
            }
            if (removal != null)
            {
                LogMasterSatelliteRemoval(previousWindowGeneration, removal);
            }
            // Keep the exact old wrapper-to-handle entry until its delayed
            // Removed callback arrives. This prevents a duplicate old Added from
            // reclaiming the current HWND generation after native handle reuse.
            m_workspaceFloatingWindows.Remove(windowHandle);
            m_arrangeFailureNotifications.Forget(windowHandle);
            m_algorithmicLayoutCoordinator.WindowClosed(windowHandle);
            if (backendChanged)
            {
                InvalidateLayout();
            }
            m_logger.Debug(
                "Reset Master + Satellites state for replacement window generation {Window}; previousWindow={PreviousWindow}, windowHandle={WindowHandle}",
                window.DebugString(),
                previousWindowGeneration.DebugString(),
                windowHandle);
        }

        private bool TryFindMasterSatelliteWindowDesktop(
            IWindow window,
            out IVirtualDesktop desktop)
        {
            IReadOnlyList<IVirtualDesktop> candidates;
            try
            {
                candidates = m_workspace.VirtualDesktopManager.Desktops.ToArray();
            }
            catch (Exception ex)
            {
                m_logger.Debug(
                    ex,
                    "Could not snapshot virtual desktops while resolving window ownership");
                desktop = null!;
                return false;
            }

            foreach (var candidate in candidates)
            {
                try
                {
                    if (candidate.IsAlive && candidate.HasWindow(window))
                    {
                        desktop = candidate;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    m_logger.Debug(
                        ex,
                        "Could not resolve window ownership on candidate desktop {Desktop}",
                        candidate);
                }
            }
            desktop = null!;
            return false;
        }

        private bool TryGetCurrentMasterSatelliteDesktop(
            out IVirtualDesktop desktop)
        {
            try
            {
                desktop = m_workspace.VirtualDesktopManager.CurrentDesktop;
                return desktop != null;
            }
            catch (Exception ex)
            {
                m_logger.Debug(
                    ex,
                    "Could not resolve the current virtual desktop for Master + Satellites");
                desktop = null!;
                return false;
            }
        }

        private void RememberMasterSatelliteWindowDesktop(
            IntPtr windowHandle,
            IVirtualDesktop desktop)
        {
            if (windowHandle != IntPtr.Zero)
            {
                m_algorithmicWindowEvents.RememberDesktop(windowHandle, desktop);
            }
        }

        /// <summary>
        /// Reconciles the first usable ownership observation with the backend
        /// attachment. An initial WinMan ownership probe can be temporarily
        /// inconclusive even though the queued add registered the window on the
        /// current desktop. If the next event already reports another desktop,
        /// that difference is positive manual-move evidence rather than the
        /// window's initial ownership.
        /// </summary>
        private void ObserveMasterSatelliteWindowDesktop(
            IWindow window,
            IntPtr windowHandle,
            IVirtualDesktop observedDesktop)
        {
            if (m_algorithmicWindowEvents.TryGetKnownDesktop(
                    windowHandle,
                    out _))
            {
                m_algorithmicWindowEvents.ObservePotentialManualMove(
                    windowHandle,
                    observedDesktop,
                    out _);
                return;
            }

            IVirtualDesktop attachedDesktop = null!;
            bool hasAttachedDesktop;
            using (m_backendLock.EnterScope())
            {
                hasAttachedDesktop = TryResolveAttachedWindowDesktopLocked(
                    window,
                    out attachedDesktop);
            }
            if (hasAttachedDesktop
                && !MasterSatelliteDisplayEligibility.DesktopsMatch(
                    attachedDesktop,
                    observedDesktop))
            {
                m_algorithmicWindowEvents.RecordManualMove(
                    windowHandle,
                    attachedDesktop,
                    observedDesktop,
                    out _);
                return;
            }

            RememberMasterSatelliteWindowDesktop(windowHandle, observedDesktop);
        }

        private bool ConsumeManualMasterSatelliteDesktopMove(
            IntPtr windowHandle,
            IVirtualDesktop targetDesktop)
        {
            return m_algorithmicWindowEvents.TryConsumeManualMove(
                windowHandle,
                targetDesktop,
                out _);
        }

        /// <summary>
        /// Reconciles the Added-before-Removed ordering of a user-initiated
        /// desktop move. The Added callback leaves its marker untouched while the
        /// window is still attached to the source tree; the matching Removed
        /// callback then detaches the source and places only on the explicit user
        /// target. It never starts another overflow transfer.
        /// </summary>
        private bool TryMigrateManualMasterSatelliteWindow(
            IWindow window,
            IntPtr windowHandle,
            IVirtualDesktop actualDesktop)
        {
            try
            {
                if (!CanManage(window, ignoreFloating: true))
                {
                    // The window moved to another physical display as well. This
                    // service owns only source cleanup; the geometrically matching
                    // display service will perform target placement.
                    return false;
                }
            }
            catch (Exception ex)
            {
                m_logger.Debug(
                    ex,
                    "Could not confirm display ownership for manual desktop move of window handle {WindowHandle}",
                    windowHandle);
                return false;
            }
            if (!m_algorithmicWindowEvents.TryGetManualMove(
                    windowHandle,
                    actualDesktop,
                    out var marker))
            {
                return false;
            }

            bool wasFloating = IsFloatingWindow(window);

            if (m_algorithmicLayoutCoordinator.TryGetRecentTransfer(
                    windowHandle,
                    out var activeTransfer)
                && !activeTransfer.IsTerminal)
            {
                // The matching destination Added callback owns cancellation and
                // recovery of an in-flight automatic transfer. Do not compete
                // with its reservation transaction here.
                return false;
            }

            bool forceFloating = activeTransfer != null
                && activeTransfer.State != PendingWindowTransferState.Committed
                && !MasterSatelliteDisplayEligibility.DesktopsMatch(
                    activeTransfer.TargetDesktop,
                    actualDesktop);

            MasterSatelliteLocalMutationResult? removal = null;
            MasterSatelliteLocalMutationResult? placement = null;
            MasterSatelliteLifecycleResult? targetLifecycleResult = null;
            bool detached = false;
            bool genericPlacementFailed = false;
            bool genericPlacementSucceeded = false;
            Exception? unexpectedFailure = null;
            try
            {
                using (m_backendLock.EnterScope())
                {
                    var sourceTree = m_backend.GetTree(marker.SourceDesktop);
                    if (sourceTree?.FindNode(window) == null)
                    {
                        bool alreadyMaterialized = m_backend.GetTree(actualDesktop)?
                            .FindNode(window) != null;
                        if (alreadyMaterialized || wasFloating)
                        {
                            // Retain the marker for the short-lived duplicate
                            // source-removal events emitted by some shell builds.
                            return true;
                        }
                        return false;
                    }
                    if (m_backend.GetTree(actualDesktop) == null)
                    {
                        var orientation = m_display.Bounds.Width >= m_display.Bounds.Height
                            ? FancyWM.Layouts.Tiling.PanelOrientation.Horizontal
                            : FancyWM.Layouts.Tiling.PanelOrientation.Vertical;
                        m_backend.RegisterDesktop(
                            actualDesktop,
                            m_display.WorkArea,
                            orientation);
                        targetLifecycleResult = OnMasterSatelliteDesktopAddedLocked(
                            actualDesktop);
                    }

                    removal = RemoveMasterSatelliteWindowLocked(
                        window,
                        preserveOriginalPosition: true);
                    if (removal.Attempted)
                    {
                        detached = removal.Succeeded;
                        if (!detached
                            && removal.Disposition
                                == MasterSatelliteLocalMutationDisposition.Rejected)
                        {
                            m_masterSatelliteLifecycle.DeactivateDesktop(
                                marker.SourceDesktop);
                            if (m_backend.HasWindow(window))
                            {
                                m_backend.UnregisterWindowPreservingOriginalPosition(
                                    window);
                            }
                            detached = !m_backend.HasWindow(window);
                        }
                    }
                    else
                    {
                        m_backend.UnregisterWindowPreservingOriginalPosition(window);
                        detached = true;
                    }

                    if (!detached)
                    {
                        return false;
                    }

                    if (!forceFloating)
                    {
                        placement = PlaceMasterSatelliteWindowLocked(
                            window,
                            out _,
                            actualDesktop);
                        if (!placement.Attempted)
                        {
                            try
                            {
                                var node = m_backend.RegisterWindow(
                                    window,
                                    actualDesktop,
                                    maxTreeWidth: m_autoSplitCount);
                                node.Parent!.Padding = GetPanelPaddingRect();
                                node.Parent.Spacing = GetPanelSpacing();
                                genericPlacementSucceeded = true;
                            }
                            catch (NoValidPlacementExistsException)
                            {
                                genericPlacementFailed = true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                unexpectedFailure = ex;
            }

            if (!detached)
            {
                return false;
            }

            LogMasterSatelliteLifecycleResult(targetLifecycleResult);
            if (removal != null)
            {
                LogMasterSatelliteRemoval(window, removal);
            }
            if (forceFloating)
            {
                NotifyMasterSatellitePlacementFailedOnce(
                    window,
                    windowHandle,
                    activeTransfer!.CorrelationId,
                    removal?.LayoutKey);
                InvalidateLayout();
                return true;
            }
            if (unexpectedFailure != null)
            {
                m_logger.Error(
                    unexpectedFailure,
                    "Manual desktop-move placement failed for window handle {WindowHandle} on desktop {Desktop}",
                    windowHandle,
                    actualDesktop);
                PlacementFailed?.Invoke(this, new TilingFailedEventArgs(
                    TilingError.NoValidPlacementExists,
                    window));
                InvalidateLayout();
                return true;
            }
            if (placement?.Attempted == true)
            {
                CompleteMasterSatellitePlacement(
                    window,
                    placement,
                    allowExistingDesktopOverflow: false);
                return true;
            }
            if (genericPlacementFailed)
            {
                PlacementFailed?.Invoke(this, new TilingFailedEventArgs(
                    TilingError.NoValidPlacementExists,
                    window));
                InvalidateLayout();
                return true;
            }
            if (genericPlacementSucceeded)
            {
                m_arrangeFailureNotifications.Forget(windowHandle);
                InvalidateLayout();
                return true;
            }

            PlacementFailed?.Invoke(this, new TilingFailedEventArgs(
                TilingError.NoValidPlacementExists,
                window));
            InvalidateLayout();
            return true;
        }

        private void OnMasterSatelliteSettingsChanged(MasterSatelliteLayoutSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            if (m_masterSatelliteCapacityTransitions.Count > 0
                || m_capacityTransitionSourcePlans != null)
            {
                // A real MoveWindow normally reconciles synchronously. If the OS
                // publishes ownership later, serialize the next settings snapshot
                // behind the exact source transaction instead of overlapping two
                // capacity reductions for the same canonical tree.
                m_deferredMasterSatelliteSettings = settings with { };
                return;
            }

            var results = new List<MasterSatelliteLifecycleResult>();
            MasterSatelliteSettingsTransition? settingsTransition = null;
            bool publishSettingsTransition = false;
            bool defaultOrientationChanged = false;
            List<MasterSatelliteCapacitySourcePlan> capacityPlans = [];
            SatelliteLayoutOrientation? pendingOrientation = null;
            IVirtualDesktop? pendingOrientationDesktop = null;
            bool hasCurrentDesktop = TryGetCurrentMasterSatelliteDesktop(
                out var currentDesktop);
            var primaryDisplay = m_masterSatellitePrimaryDisplay;
            using (m_backendLock.EnterScope())
            {
                if (m_masterSatelliteLifecycleDisposed)
                {
                    return;
                }
                if (!m_masterSatelliteLifecycleReady)
                {
                    settingsTransition = m_masterSatelliteLifecycle.CacheSettings(settings, primaryDisplay);
                }
                else if (hasCurrentDesktop)
                {
                    var previousSettings = m_masterSatelliteLifecycle.SettingsSnapshot
                        with { };
                    defaultOrientationChanged =
                        previousSettings.DefaultSatelliteOrientation
                            != settings.DefaultSatelliteOrientation;
                    capacityPlans = BuildMasterSatelliteCapacityPlansLocked(
                        settings,
                        primaryDisplay,
                        currentDesktop);
                    settingsTransition = m_masterSatelliteLifecycle.CacheSettings(settings, primaryDisplay);
                    publishSettingsTransition = settingsTransition.Value.WasEligible
                        != settingsTransition.Value.IsEligible;
                    results.AddRange(InitializeMasterSatelliteDesktopsLocked(
                        currentDesktop,
                        primaryDisplay,
                        capacityPlans.Select(plan => plan.SourceKey.VirtualDesktop)));
                    if (settingsTransition.Value.IsEligible
                        && defaultOrientationChanged)
                    {
                        var currentCapacityPlan = capacityPlans.FirstOrDefault(plan =>
                            MasterSatelliteDisplayEligibility.DesktopsMatch(
                                plan.SourceKey.VirtualDesktop,
                                currentDesktop));
                        if (currentCapacityPlan?.SourceState != null)
                        {
                            // Prove the final retained prefix before moving any
                            // tail window. Applying the orientation to the old,
                            // over-capacity set can reject geometry that is valid
                            // after the shrink has completed.
                            var candidateState = currentCapacityPlan.SourceState.Clone();
                            candidateState.SatelliteOrientation =
                                settings.DefaultSatelliteOrientation;
                            var preflight = m_backend
                                .PreflightMasterSatelliteActivation(
                                    currentDesktop,
                                    candidateState,
                                    settings,
                                    currentCapacityPlan.Transition.AdmittedWindows);
                            if (preflight.Succeeded)
                            {
                                pendingOrientation =
                                    settings.DefaultSatelliteOrientation;
                                pendingOrientationDesktop = currentDesktop;
                            }
                            else
                            {
                                results.Add(new MasterSatelliteLifecycleResult(
                                    currentCapacityPlan.SourceKey,
                                    true,
                                    true,
                                    false,
                                    false,
                                    "SettingsOrientationRejected",
                                    preflight,
                                    preflight.Invariant));
                            }
                        }
                        else if (currentCapacityPlan == null)
                        {
                            var orientationResult = m_masterSatelliteLifecycle
                                .ApplySatelliteOrientationSetting(
                                    m_backend,
                                    currentDesktop,
                                    settings,
                                    settings.DefaultSatelliteOrientation);
                            if (orientationResult != null)
                            {
                                results.Add(orientationResult);
                            }
                        }
                        // A first activation with surplus generic windows has no
                        // runtime state yet; final activation naturally creates it
                        // with the new default orientation.
                    }
                }
                else
                {
                    // Cache the setting change without guessing which desktop is
                    // current. A later desktop event will initialize the matching
                    // state; active canonical trees remain untouched meanwhile.
                    var previousSettings = m_masterSatelliteLifecycle.SettingsSnapshot
                        with { };
                    defaultOrientationChanged =
                        previousSettings.DefaultSatelliteOrientation
                            != settings.DefaultSatelliteOrientation;
                    settingsTransition = m_masterSatelliteLifecycle.CacheSettings(
                        settings,
                        primaryDisplay);
                    publishSettingsTransition = settingsTransition.Value.WasEligible
                        != settingsTransition.Value.IsEligible;
                }
                if (defaultOrientationChanged
                    && settingsTransition is { IsEligible: true })
                {
                    foreach (var entry in m_masterSatelliteLifecycle.SnapshotStates())
                    {
                        // Empty states are pre-created only for capacity
                        // publication. Keep them aligned with the persisted
                        // activation default, while preserving intentional
                        // per-desktop orientation for non-empty layouts.
                        if (entry.State.Master != null
                            || entry.State.Satellites.Count != 0
                            || entry.State.SatelliteOrientation
                                == settings.DefaultSatelliteOrientation)
                        {
                            continue;
                        }
                        var orientationResult = m_masterSatelliteLifecycle
                            .ApplySatelliteOrientationSetting(
                                m_backend,
                                entry.Key.VirtualDesktop,
                                settings,
                                settings.DefaultSatelliteOrientation);
                        if (orientationResult != null)
                        {
                            results.Add(orientationResult);
                        }
                    }
                }
                PublishAllMasterSatelliteCapacitiesLocked();
            }

            if (settingsTransition is { RemovedStateCount: > 0 } transition)
            {
                m_logger.Debug(
                    "Master + Satellites settings removed {StateCount} display-scoped runtime states before lifecycle initialization",
                    transition.RemovedStateCount);
            }
            if (capacityPlans.Count > 0)
            {
                foreach (var plan in capacityPlans)
                {
                    foreach (var window in plan.Transition.ExtrasFromEnd)
                    {
                        m_masterSatelliteCapacityTransitions.Enqueue(
                            new MasterSatelliteCapacityTransitionWorkItem(
                                plan,
                                window));
                    }
                }
                m_capacityTransitionLifecycleResults = results;
                m_capacityTransitionSettingsTransition = publishSettingsTransition
                    ? settingsTransition
                    : null;
                m_capacityTransitionSourcePlans = capacityPlans;
                m_capacityTransitionCurrentDesktop = currentDesktop;
                m_capacityTransitionPendingOrientation = pendingOrientation;
                m_capacityTransitionOrientationDesktop = pendingOrientationDesktop;
                ProcessMasterSatelliteCapacityTransitions();
                return;
            }

            LogMasterSatelliteLifecycleResults(results);
            PublishMasterSatelliteSettingsTransition(
                publishSettingsTransition ? settingsTransition : null,
                results,
                hasCurrentDesktop ? currentDesktop : null);
            if (results.Count > 0)
            {
                InvalidateLayout();
            }
        }

        /// <summary>
        /// Computes every source reduction without mutating a tree. Active
        /// desktops use their exact logical role order; first activation uses the
        /// focused tiled window followed by visual order. The detached activation
        /// preflight proves the final retained prefix before any overflow planning.
        /// </summary>
        private List<MasterSatelliteCapacitySourcePlan>
            BuildMasterSatelliteCapacityPlansLocked(
                MasterSatelliteLayoutSettings settings,
                IDisplay primaryDisplay,
                IVirtualDesktop currentDesktop)
        {
            var plans = new List<MasterSatelliteCapacitySourcePlan>();
            if (!MasterSatelliteDisplayEligibility.IsEligible(
                    settings,
                    m_display,
                    primaryDisplay))
            {
                return plans;
            }

            var sourceSettings = m_masterSatelliteLifecycle.SettingsSnapshot with { };
            foreach (var entry in m_masterSatelliteLifecycle.SnapshotStates())
            {
                if (entry.State.Master == null
                    || entry.State.Satellites.Count <= settings.MaxSatellites)
                {
                    continue;
                }
                var invariant = m_backend.ValidateMasterSatelliteLayout(
                    entry.Key.VirtualDesktop,
                    entry.State,
                    sourceSettings);
                if (!invariant.IsValid)
                {
                    m_logger.Warning(
                        "Skipping Master + Satellites capacity reduction for corrupted source {Desktop} on display {Display}: {Invariant}",
                        entry.Key.VirtualDesktop,
                        m_display,
                        invariant.Description);
                    continue;
                }
                plans.Add(new MasterSatelliteCapacitySourcePlan(
                    entry.Key,
                    entry.State,
                    sourceSettings,
                    MasterSatelliteCapacityTransitionPlanner.PlanShrink(
                        entry.State.Master,
                        entry.State.Satellites,
                        settings.MaxSatellites),
                    IsActivation: false));
            }

            if (plans.Any(plan =>
                    MasterSatelliteDisplayEligibility.DesktopsMatch(
                        plan.SourceKey.VirtualDesktop,
                        currentDesktop))
                || m_masterSatelliteLifecycle.TryGetState(currentDesktop, out _)
                || !m_backend.TryGetTreeSnapshot(currentDesktop, out var currentTree))
            {
                return plans;
            }

            var visualOrder = currentTree.Root?.Windows
                .Select(node => node.WindowReference)
                .ToArray() ?? [];
            if (visualOrder.Length <= settings.MaxSatellites + 1)
            {
                return plans;
            }
            var focusedWindow = MasterSatelliteCapacityTransitionPlanner
                .ResolveActivationFocus(
                    visualOrder,
                    (m_backend.GetFocus(currentDesktop) as
                        FancyWM.Layouts.Tiling.WindowNode)?.WindowReference,
                    m_workspace.FocusedWindow);
            var activation = MasterSatelliteCapacityTransitionPlanner.PlanActivation(
                visualOrder,
                focusedWindow,
                settings.MaxSatellites);
            var candidateState = new MasterSatelliteRuntimeState(settings, true);
            var preflight = m_backend.PreflightMasterSatelliteActivation(
                currentDesktop,
                candidateState,
                settings,
                activation.AdmittedWindows);
            if (!preflight.Succeeded)
            {
                m_logger.Warning(
                    "Master + Satellites activation prefix preflight rejected before overflow planning for desktop {Desktop} on display {Display}: {Reason} ({Message})",
                    currentDesktop,
                    m_display,
                    preflight.FailureReason,
                    preflight.Message);
                return plans;
            }
            plans.Add(new MasterSatelliteCapacitySourcePlan(
                new LayoutStateKey(currentDesktop, m_display),
                null,
                sourceSettings,
                activation,
                IsActivation: true));
            return plans;
        }

        private IReadOnlyList<MasterSatelliteLifecycleResult>
            InitializeMasterSatelliteDesktopsLocked(
                IVirtualDesktop currentDesktop,
                IDisplay primaryDisplay,
                IEnumerable<IVirtualDesktop> excludedDesktops)
        {
            var excluded = excludedDesktops.ToArray();
            var ordered = new List<IVirtualDesktop> { currentDesktop };
            foreach (var desktop in m_backend.SnapshotDesktops())
            {
                if (!ordered.Any(existing =>
                    MasterSatelliteDisplayEligibility.DesktopsMatch(existing, desktop)))
                {
                    ordered.Add(desktop);
                }
            }

            var results = new List<MasterSatelliteLifecycleResult>();
            foreach (var desktop in ordered)
            {
                if (excluded.Any(candidate =>
                    MasterSatelliteDisplayEligibility.DesktopsMatch(candidate, desktop)))
                {
                    continue;
                }
                results.Add(MasterSatelliteDisplayEligibility.DesktopsMatch(
                        desktop,
                        currentDesktop)
                    ? m_masterSatelliteLifecycle.CurrentDesktopChanged(
                        m_backend,
                        desktop,
                        primaryDisplay)
                    : m_masterSatelliteLifecycle.DesktopAdded(
                        m_backend,
                        desktop,
                        primaryDisplay));
            }
            return results;
        }

        private void PublishMasterSatelliteSettingsTransition(
            MasterSatelliteSettingsTransition? settingsTransition,
            IReadOnlyList<MasterSatelliteLifecycleResult> results,
            IVirtualDesktop? currentDesktop)
        {
            if (settingsTransition is not { } published)
            {
                return;
            }
            bool currentLayoutPresent = currentDesktop != null
                && results.Any(result =>
                MasterSatelliteDisplayEligibility.DesktopsMatch(
                    result.Key.VirtualDesktop,
                    currentDesktop)
                && result.StatePresent);
            if (!published.IsEligible || currentLayoutPresent)
            {
                RaiseMasterSatelliteEvent(new AlgorithmicLayoutEvent(
                    published.IsEligible
                        ? AlgorithmicLayoutEventKind.LayoutEnabled
                        : AlgorithmicLayoutEventKind.LayoutDisabled,
                    m_display,
                    "SettingsChanged",
                    published.IsEligible
                        ? "AlgorithmicLayout.LayoutEnabled"
                        : "AlgorithmicLayout.LayoutDisabled",
                    sourceDesktop: currentDesktop));
                return;
            }

            var rejection = currentDesktop == null
                ? null
                : results.FirstOrDefault(result =>
                    MasterSatelliteDisplayEligibility.DesktopsMatch(
                        result.Key.VirtualDesktop,
                        currentDesktop));
            RaiseMasterSatelliteEvent(new AlgorithmicLayoutEvent(
                AlgorithmicLayoutEventKind.OperationRejected,
                m_display,
                rejection?.Operation?.FailureReason.ToString()
                    ?? rejection?.Action
                    ?? "ActivationRejected",
                "AlgorithmicLayout.OperationRejected",
                sourceDesktop: currentDesktop));
        }

        private void ProcessMasterSatelliteCapacityTransitions()
        {
            if (m_masterSatelliteCapacityTransitions.Count == 0
                || m_processingMasterSatelliteCapacityTransitions
                || m_masterSatelliteLifecycleDisposed)
            {
                return;
            }

            m_processingMasterSatelliteCapacityTransitions = true;
            try
            {
                while (m_masterSatelliteCapacityTransitions.Count > 0)
                {
                    var work = m_masterSatelliteCapacityTransitions.Peek();
                    if (work.Completed)
                    {
                        m_masterSatelliteCapacityTransitions.Dequeue();
                        continue;
                    }

                    if (!IsMasterSatelliteCapacitySourceWindowPresent(work))
                    {
                        work.Completed = true;
                        continue;
                    }

                    var settings = m_masterSatelliteLifecycle.SettingsSnapshot;
                    AlgorithmicDestinationSearchResult? search = null;
                    if (AlgorithmicDesktopCreationPolicy.AllowsExistingDesktopOverflow(
                        settings.OverflowPolicy))
                    {
                        search = PlanMasterSatelliteExistingWindowOverflow(work);
                        if (!search.Succeeded
                            && AlgorithmicDesktopCreationPolicy.ShouldCreateAfterSearch(
                                settings.OverflowPolicy,
                                search.Disposition))
                        {
                            search = TryCreateDesktopAndPlanCapacityOverflow(work)
                                ?? search;
                        }
                    }

                    if (search?.Succeeded == true
                        && search.Transfer is PendingWindowTransfer transfer)
                    {
                        work.CorrelationId = transfer.CorrelationId;
                        var context = new MasterSatelliteExistingWindowTransferContext(
                            work,
                            transfer);
                        m_masterSatelliteExistingWindowTransfers.Add(
                            transfer.CorrelationId,
                            context);
                        var start = m_algorithmicWindowTransfers.ExecuteReservedTransfer(
                            work.Window,
                            transfer.WindowHandle,
                            search);
                        if (start.MoveRequested)
                        {
                            ReconcileMasterSatelliteTransferAfterMove(
                                work.Window,
                                transfer.WindowHandle,
                                start.Transfer ?? transfer,
                                remainingDeferredProbes:
                                    MasterSatelliteDeferredOwnershipProbeLimit);
                            if (!work.Completed)
                            {
                                // Preserve strict last-to-first sequencing. The
                                // next extra starts only after this correlation
                                // commits or reaches its floating fallback.
                                return;
                            }
                            continue;
                        }

                        var recovery = start.Recovery
                            ?? m_algorithmicWindowTransfers.RecoverTerminalTransfer(
                                work.Window,
                                transfer.CorrelationId);
                        CompleteMasterSatelliteExistingWindowFailure(
                            context,
                            recovery,
                            transfer.WindowHandle);
                        continue;
                    }

                    // Planning has conclusively found no usable exact slot. Only
                    // now may the source layout be reduced to its admitted prefix.
                    bool plannedFloatingFallback = search == null
                        || search.Disposition is
                            AlgorithmicDestinationSearchDisposition.NoDestination
                            or AlgorithmicDestinationSearchDisposition.SourceDesktopNotFound;
                    if (plannedFloatingFallback)
                    {
                        if (!FloatMasterSatelliteCapacityWindow(work))
                        {
                            AbortMasterSatelliteCapacitySourcePlan(work.Plan);
                        }
                    }
                    else
                    {
                        // A correlation/preflight infrastructure failure is not a
                        // successful overflow plan. Preserve the exact source tree;
                        // final lifecycle validation may safely deactivate only
                        // this desktop rather than partially evicting it.
                        m_logger.Error(
                            "Capacity transition planning was inconclusive for window {Window}; disposition={Disposition}, reason={Reason}. The source tree was not mutated.",
                            work.Window.DebugString(),
                            search!.Disposition,
                            search.DiagnosticReason);
                        AbortMasterSatelliteCapacitySourcePlan(work.Plan);
                    }
                    work.Completed = true;
                }
            }
            finally
            {
                m_processingMasterSatelliteCapacityTransitions = false;
            }

            if (m_masterSatelliteCapacityTransitions.Count == 0)
            {
                FinalizeMasterSatelliteCapacityTransition();
            }
        }

        private AlgorithmicDestinationSearchResult
            PlanMasterSatelliteExistingWindowOverflow(
                MasterSatelliteCapacityTransitionWorkItem work)
        {
            if (!TryRememberMasterSatelliteWindowHandle(
                    work.Window,
                    out var stableWindowHandle))
            {
                return new AlgorithmicDestinationSearchResult(
                    AlgorithmicDestinationSearchDisposition.TransferConflict,
                    null,
                    Array.Empty<AlgorithmicDestinationCandidate>(),
                    "The existing source window no longer has a stable handle.");
            }

            Rectangle sourceOriginalPosition;
            using (m_backendLock.EnterScope())
            {
                if (!m_backend.TryGetOriginalPosition(
                    work.Window,
                    out sourceOriginalPosition))
                {
                    sourceOriginalPosition = Rectangle.Empty;
                }
            }
            if (sourceOriginalPosition == Rectangle.Empty)
            {
                try
                {
                    // External window property: deliberately outside backend and
                    // coordinator locks.
                    sourceOriginalPosition = work.Window.Position;
                }
                catch (Exception ex)
                {
                    m_logger.Error(
                        ex,
                        "Could not capture original geometry for capacity transition window {Window}",
                        work.Window.DebugString());
                    return new AlgorithmicDestinationSearchResult(
                        AlgorithmicDestinationSearchDisposition.TransferConflict,
                        null,
                        Array.Empty<AlgorithmicDestinationCandidate>(),
                        "The existing source geometry is unavailable.");
                }
            }

            var correlationId = Guid.NewGuid();
            var search = m_algorithmicWindowTransfers.PlanExistingDesktopTransfer(
                correlationId,
                work.Window,
                stableWindowHandle,
                work.Plan.SourceKey.VirtualDesktop,
                sourceOriginalPosition,
                (targetKey, capacity) => PreflightOverflowDestination(
                    work.Window,
                    targetKey,
                    capacity));
            work.CorrelationId = correlationId;
            work.SourceOriginalPosition = sourceOriginalPosition;
            work.StableWindowHandle = stableWindowHandle;
            return search;
        }

        private AlgorithmicDestinationSearchResult?
            TryCreateDesktopAndPlanCapacityOverflow(
                MasterSatelliteCapacityTransitionWorkItem work)
        {
            var settings = m_masterSatelliteLifecycle.SettingsSnapshot;
            var creation = m_algorithmicDesktopCreations.CreateAndPrepareDesktop(
                work.StableWindowHandle,
                work.CorrelationId,
                settings.MaxAutoCreatedDesktops,
                EnsureMasterSatelliteDesktopReady);
            if (!creation.Succeeded || creation.CreatedDesktop == null)
            {
                if (creation.Exception != null)
                {
                    m_logger.Error(
                        creation.Exception,
                        "Capacity transition could not create an overflow desktop; correlation={CorrelationId}, display={Display}, reason={Reason}",
                        work.CorrelationId,
                        m_display,
                        creation.DiagnosticReason);
                }
                return null;
            }

            return m_algorithmicWindowTransfers.PlanExistingDesktopTransfer(
                work.CorrelationId,
                work.Window,
                work.StableWindowHandle,
                work.Plan.SourceKey.VirtualDesktop,
                work.SourceOriginalPosition,
                (targetKey, capacity) => PreflightOverflowDestination(
                    work.Window,
                    targetKey,
                    capacity));
        }

        private bool IsMasterSatelliteCapacitySourceWindowPresent(
            MasterSatelliteCapacityTransitionWorkItem work)
        {
            using (m_backendLock.EnterScope())
            {
                return m_backend.GetTree(work.Plan.SourceKey.VirtualDesktop)?
                    .FindNode(work.Window) != null;
            }
        }

        private bool FloatMasterSatelliteCapacityWindow(
            MasterSatelliteCapacityTransitionWorkItem work,
            bool presentFallback = true)
        {
            bool detached = false;
            try
            {
                using (m_backendLock.EnterScope())
                {
                    if (m_backend.GetTree(work.Plan.SourceKey.VirtualDesktop)?
                        .FindNode(work.Window) == null)
                    {
                        detached = true;
                    }
                    else if (work.Plan.SourceState != null)
                    {
                        var operation = m_backend.UnregisterMasterSatelliteWindow(
                            work.Plan.SourceKey.VirtualDesktop,
                            work.Plan.SourceState,
                            work.Plan.SourceValidationSettings,
                            work.Window,
                            preserveOriginalPosition: true);
                        detached = operation.Succeeded;
                        if (!detached)
                        {
                            m_logger.Error(
                                "Capacity transition could not detach canonical source window {Window}: {Reason} ({Message})",
                                work.Window.DebugString(),
                                operation.FailureReason,
                                operation.Message);
                        }
                    }
                    else
                    {
                        m_backend.UnregisterWindowPreservingOriginalPosition(
                            work.Window);
                        detached = true;
                    }
                    PublishMasterSatelliteCapacityLocked(
                        work.Plan.SourceKey.VirtualDesktop);
                }
            }
            catch (Exception ex)
            {
                m_logger.Error(
                    ex,
                    "Capacity transition source detach failed for window {Window} on desktop {Desktop}",
                    work.Window.DebugString(),
                    work.Plan.SourceKey.VirtualDesktop);
            }

            if (detached && presentFallback)
            {
                PresentMasterSatelliteCapacityFloatingFallback(work);
            }
            return detached;
        }

        private void PresentMasterSatelliteCapacityFloatingFallback(
            MasterSatelliteCapacityTransitionWorkItem work)
        {
            try
            {
                NotifyMasterSatellitePlacementFailedOnce(
                    work.Window,
                    work.StableWindowHandle,
                    work.CorrelationId,
                    work.Plan.SourceKey);
                InvalidateLayout();
            }
            catch (Exception ex)
            {
                // Presentation and best-effort window repositioning must never
                // strand an already completed source mutation or reservation.
                m_logger.Error(
                    ex,
                    "Capacity transition floating fallback presentation failed for window {Window}",
                    work.Window.DebugString());
            }
        }

        private void AbortMasterSatelliteCapacitySourcePlan(
            MasterSatelliteCapacitySourcePlan plan)
        {
            plan.Aborted = true;
            foreach (var pending in m_masterSatelliteCapacityTransitions)
            {
                if (ReferenceEquals(pending.Plan, plan))
                {
                    pending.Completed = true;
                }
            }
        }

        private bool IsMasterSatelliteCapacityTransitionSource(
            IVirtualDesktop desktop)
        {
            ArgumentNullException.ThrowIfNull(desktop);
            return m_capacityTransitionSourcePlans?.Any(plan =>
                MasterSatelliteDisplayEligibility.DesktopsMatch(
                    plan.SourceKey.VirtualDesktop,
                    desktop)) == true;
        }

        private bool TryGetMasterSatelliteCapacityTransitionDesktop(
            IWindow window,
            out IVirtualDesktop desktop)
        {
            ArgumentNullException.ThrowIfNull(window);
            using (m_backendLock.EnterScope())
            {
                if (TryResolveAttachedMasterSatelliteDesktopLocked(
                        window,
                        out var attachedDesktop)
                    && IsMasterSatelliteCapacityTransitionSource(attachedDesktop))
                {
                    desktop = attachedDesktop;
                    return true;
                }
            }
            if (TryFindMasterSatelliteWindowDesktop(window, out var actualDesktop)
                && IsMasterSatelliteCapacityTransitionSource(actualDesktop))
            {
                desktop = actualDesktop;
                return true;
            }
            desktop = null!;
            return false;
        }

        private void RejectMasterSatelliteCapacityTransitionMutation(
            string command,
            IVirtualDesktop desktop)
        {
            CompleteMasterSatelliteCommand(
                command,
                desktop,
                m_masterSatelliteCommands.RejectTransitionInProgress());
        }

        private void FinalizeMasterSatelliteCapacityTransition()
        {
            var results = m_capacityTransitionLifecycleResults
                ?? new List<MasterSatelliteLifecycleResult>();
            bool hasCurrentDesktop = TryGetCurrentMasterSatelliteDesktop(
                out var observedCurrentDesktop);
            IVirtualDesktop? currentDesktop = hasCurrentDesktop
                ? observedCurrentDesktop
                : m_capacityTransitionCurrentDesktop;
            IVirtualDesktop? transitionDesktop =
                m_capacityTransitionCurrentDesktop ?? currentDesktop;
            var primaryDisplay = m_masterSatellitePrimaryDisplay;
            using (m_backendLock.EnterScope())
            {
                if (!m_masterSatelliteLifecycleDisposed)
                {
                    IReadOnlyList<MasterSatelliteCapacitySourcePlan> sourcePlans =
                        m_capacityTransitionSourcePlans
                        ?? Array.Empty<MasterSatelliteCapacitySourcePlan>();
                    var abortedPlans = sourcePlans
                        .Where(plan => plan.Aborted)
                        .ToArray();
                    foreach (var plan in abortedPlans.Where(
                        plan => plan.SourceState != null))
                    {
                        results.Add(
                            m_masterSatelliteLifecycle
                                .DeactivateCapacityTransitionSource(
                                    plan.SourceKey.VirtualDesktop,
                                    primaryDisplay));
                    }
                    var completedActivationPlans = sourcePlans
                        .Where(plan => plan.IsActivation && !plan.Aborted)
                        .ToArray();
                    foreach (var plan in completedActivationPlans)
                    {
                        // Activation was explicitly requested while this desktop
                        // was current. Finish that transaction even if the user
                        // switched away while its tail transfers were awaiting OS
                        // ownership; background initialization intentionally does
                        // not convert non-empty manual trees.
                        results.Add(
                            m_masterSatelliteLifecycle.CurrentDesktopChanged(
                                m_backend,
                                plan.SourceKey.VirtualDesktop,
                                primaryDisplay));
                    }
                    var desktops = m_backend.SnapshotDesktops();
                    currentDesktop ??= desktops.FirstOrDefault();
                    if (currentDesktop != null)
                    {
                        results.AddRange(
                            m_masterSatelliteLifecycle.InitializeExistingDesktops(
                                m_backend,
                                desktops,
                                currentDesktop,
                                primaryDisplay,
                                abortedPlans.Select(plan =>
                                        plan.SourceKey.VirtualDesktop)
                                    .Concat(completedActivationPlans.Select(plan =>
                                        plan.SourceKey.VirtualDesktop))));
                    }
                    if (m_capacityTransitionPendingOrientation is { } orientation
                        && m_capacityTransitionOrientationDesktop is { } orientationDesktop
                        && !abortedPlans.Any(plan =>
                            MasterSatelliteDisplayEligibility.DesktopsMatch(
                                plan.SourceKey.VirtualDesktop,
                                orientationDesktop)))
                    {
                        var orientationResult = m_masterSatelliteLifecycle
                            .ApplySatelliteOrientationSetting(
                                m_backend,
                                orientationDesktop,
                                m_masterSatelliteLifecycle.SettingsSnapshot,
                                orientation);
                        if (orientationResult != null)
                        {
                            results.Add(orientationResult);
                        }
                    }
                    PublishAllMasterSatelliteCapacitiesLocked();
                }
            }

            LogMasterSatelliteLifecycleResults(results);
            PublishMasterSatelliteSettingsTransition(
                m_capacityTransitionSettingsTransition,
                results,
                transitionDesktop);
            m_capacityTransitionLifecycleResults = null;
            m_capacityTransitionSettingsTransition = null;
            m_capacityTransitionSourcePlans = null;
            m_capacityTransitionCurrentDesktop = null;
            m_capacityTransitionPendingOrientation = null;
            m_capacityTransitionOrientationDesktop = null;
            InvalidateLayout();

            if (m_deferredMasterSatelliteSettings is { } deferred)
            {
                m_deferredMasterSatelliteSettings = null;
                OnMasterSatelliteSettingsChanged(deferred);
            }
        }

        private void InitializeMasterSatelliteLifecycle()
        {
            MasterSatelliteLayoutSettings settings;
            using (m_backendLock.EnterScope())
            {
                if (m_masterSatelliteLifecycleDisposed)
                {
                    return;
                }
                m_masterSatelliteLifecycleReady = true;
                settings = m_masterSatelliteLifecycle.SettingsSnapshot with { };
            }

            // Re-enter the normal settings-transition path now that the complete
            // startup window tree is available. This is what gives first
            // activation the same clone-preflight and tail-overflow guarantees as
            // a later UI toggle.
            OnMasterSatelliteSettingsChanged(settings);
        }

        private void InitializeMasterSatelliteLifecycleSafely()
        {
            try
            {
                InitializeMasterSatelliteLifecycle();
            }
            catch (Exception ex)
            {
                m_logger.Error(
                    ex,
                    "Initializing Master + Satellites lifecycle failed for display {Display}",
                    m_display);
            }
        }

        private MasterSatelliteLifecycleResult? OnMasterSatelliteDesktopAddedLocked(
            IVirtualDesktop desktop)
        {
            if (!m_masterSatelliteLifecycleReady)
            {
                return null;
            }
            var result = m_masterSatelliteLifecycle.DesktopAdded(
                m_backend,
                desktop,
                m_masterSatellitePrimaryDisplay);
            PublishMasterSatelliteCapacityLocked(desktop);
            return result;
        }

        private bool OnMasterSatelliteDesktopRemovedLocked(IVirtualDesktop desktop)
        {
            bool removed = m_masterSatelliteLifecycle.DesktopRemoved(desktop);
            m_algorithmicLayoutCoordinator.DesktopRemoved(desktop);
            return removed;
        }

        private void OnMasterSatelliteCurrentDesktopChanged(IVirtualDesktop desktop)
        {
            MasterSatelliteLifecycleResult? result = null;
            using (m_backendLock.EnterScope())
            {
                if (m_masterSatelliteLifecycleReady && !m_masterSatelliteLifecycleDisposed)
                {
                    bool capacityTransitionOwnsDesktop =
                        IsMasterSatelliteCapacityTransitionSource(desktop);
                    if (!capacityTransitionOwnsDesktop)
                    {
                        result = m_masterSatelliteLifecycle.CurrentDesktopChanged(
                            m_backend,
                            desktop,
                            m_masterSatellitePrimaryDisplay);
                    }
                    else
                    {
                        m_logger.Debug(
                            "Deferred Master + Satellites desktop refresh while a capacity transition owns source desktop {Desktop} on display {Display}",
                            desktop,
                            m_display);
                    }
                    PublishAllMasterSatelliteCapacitiesLocked();
                }
            }
            LogMasterSatelliteLifecycleResult(result);
        }

        private void OnDisplayWorkAreaChanged(object? sender, DisplayRectangleChangedEventArgs e)
        {
            if (DeferToTilingDispatcher(
                () => OnDisplayWorkAreaChanged(sender, e),
                nameof(OnDisplayWorkAreaChanged)))
            {
                return;
            }
            IReadOnlyList<MasterSatelliteLifecycleResult> results;
            using (m_backendLock.EnterScope())
            {
                if (m_masterSatelliteLifecycleDisposed)
                {
                    return;
                }
                results = m_masterSatelliteLifecycle.WorkAreaChanged(
                    m_backend,
                    m_display.WorkArea);
                PublishAllMasterSatelliteCapacitiesLocked();
            }
            LogMasterSatelliteLifecycleResults(results);
            InvalidateLayout();
        }

        private void RelayoutMasterSatelliteAfterScaling()
        {
            IReadOnlyList<MasterSatelliteLifecycleResult>? results = null;
            using (m_backendLock.EnterScope())
            {
                if (m_masterSatelliteLifecycleReady && !m_masterSatelliteLifecycleDisposed)
                {
                    results = m_masterSatelliteLifecycle.ScalingChanged(
                        m_backend,
                        GetPanelPaddingRect(),
                        GetPanelSpacing());
                    PublishAllMasterSatelliteCapacitiesLocked();
                }
            }
            LogMasterSatelliteLifecycleResults(results);
        }

        private void DisposeMasterSatelliteLifecycle()
        {
            using (m_backendLock.EnterScope())
            {
                m_masterSatelliteLifecycleReady = false;
                m_masterSatelliteLifecycleDisposed = true;
                m_masterSatelliteLifecycle.Clear();
                m_masterSatelliteCapacityTransitions.Clear();
                m_masterSatelliteExistingWindowTransfers.Clear();
                m_masterSatelliteDestinationRestorePoints.Clear();
                m_masterSatelliteIncomingOwnershipProbes.Clear();
                m_retiredMasterSatelliteWindowGenerations.Clear();
                m_capacityTransitionLifecycleResults = null;
                m_capacityTransitionSettingsTransition = null;
                m_capacityTransitionSourcePlans = null;
                m_capacityTransitionCurrentDesktop = null;
                m_capacityTransitionPendingOrientation = null;
                m_capacityTransitionOrientationDesktop = null;
                m_deferredMasterSatelliteSettings = null;
                m_masterSatelliteDropPreviewWindows = EmptyWindowSet;
            }
            m_algorithmicDisplayRegistration.Dispose();
            m_algorithmicWindowEvents.Clear();
        }

        private void RecoverMasterSatelliteTransfersBeforeDisposal()
        {
            PendingWindowTransfer[] transfers;
            try
            {
                transfers = m_algorithmicLayoutCoordinator.SnapshotTransfers()
                    .Where(transfer =>
                        transfer.State != PendingWindowTransferState.Committed
                        && (MasterSatelliteDisplayEligibility.DisplaysMatch(
                                transfer.SourceDisplay,
                                m_display)
                            || MasterSatelliteDisplayEligibility.DisplaysMatch(
                                transfer.TargetDisplay,
                                m_display)))
                    .ToArray();
            }
            catch (Exception ex)
            {
                m_logger.Error(
                    ex,
                    "Could not snapshot pending Master + Satellites transfers while disposing display {Display}",
                    m_display);
                return;
            }

            foreach (var transfer in transfers)
            {
                try
                {
                    if (!m_algorithmicWindowEvents.TryGetWindow(
                            transfer.WindowHandle,
                            out var window))
                    {
                        if (!transfer.IsTerminal)
                        {
                            m_algorithmicLayoutCoordinator.Cancel(
                                transfer.CorrelationId,
                                transfer.WindowHandle,
                                "DisplayServiceDisposed");
                        }
                        if (m_algorithmicLayoutCoordinator.TryGetTransfer(
                                transfer.CorrelationId,
                                out var terminalTransfer))
                        {
                            CompleteClosedMasterSatelliteCapacityTransfer(
                                terminalTransfer,
                                m_masterSatelliteExistingWindowTransfers);
                        }
                        continue;
                    }

                    if (!transfer.IsTerminal)
                    {
                        m_algorithmicLayoutCoordinator.Cancel(
                            transfer.CorrelationId,
                            transfer.WindowHandle,
                            "DisplayServiceDisposed");
                    }

                    var recovery = RecoverMasterSatelliteTerminalTransfer(
                        window,
                        transfer);
                    if (m_masterSatelliteExistingWindowTransfers.TryGetValue(
                            transfer.CorrelationId,
                            out var context))
                    {
                        CompleteMasterSatelliteExistingWindowFailure(
                            context,
                            recovery,
                            transfer.WindowHandle);
                    }
                    else
                    {
                        ApplyMasterSatelliteTransferRecovery(
                            window,
                            recovery,
                            transfer.WindowHandle);
                    }
                }
                catch (Exception ex)
                {
                    m_logger.Error(
                        ex,
                        "Could not recover Master + Satellites transfer {CorrelationId} while disposing display {Display}",
                        transfer.CorrelationId,
                        m_display);
                }
            }
        }

        private AlgorithmicTransferRecoveryResult RecoverMasterSatelliteTerminalTransfer(
            IWindow window,
            PendingWindowTransfer transfer)
        {
            // A manual move can win the race after the coordinator has started a
            // transfer but before its terminal notification reaches this service.
            // Observe OS ownership before recovery and never move such a window
            // back to the stale source desktop.
            bool hasActualDesktop = TryFindMasterSatelliteWindowDesktop(
                window,
                out var actualDesktop);
            bool actualIsSource = hasActualDesktop
                && MasterSatelliteDisplayEligibility.DesktopsMatch(
                    actualDesktop,
                    transfer.SourceDesktop);
            bool actualIsReservedTarget = hasActualDesktop
                && MasterSatelliteDisplayEligibility.DesktopsMatch(
                    actualDesktop,
                    transfer.TargetDesktop);
            bool hasManualMarker = hasActualDesktop
                && m_algorithmicWindowEvents.TryGetManualMove(
                    transfer.WindowHandle,
                    actualDesktop,
                    out _);
            bool preserveObservedDesktop = hasActualDesktop
                && !actualIsSource
                && (!actualIsReservedTarget
                    || hasManualMarker
                    || IsFloatingWindow(window));
            if (preserveObservedDesktop)
            {
                RememberMasterSatelliteWindowDesktop(
                    transfer.WindowHandle,
                    actualDesktop);
                return m_algorithmicWindowTransfers.RecoverTerminalTransfer(
                    window,
                    transfer.CorrelationId,
                    allowRollbackToSource: false,
                    preferredFloatingDesktop: actualDesktop);
            }

            return m_algorithmicWindowTransfers.RecoverTerminalTransfer(
                window,
                transfer.CorrelationId);
        }

        /// <summary>
        /// Must be called while holding m_backendLock. A NotActive result means the
        /// caller must continue through the unchanged generic FancyWM path.
        /// </summary>
        private MasterSatelliteLocalMutationResult PlaceMasterSatelliteWindowLocked(
            IWindow window,
            out bool activeLayoutMatched,
            IVirtualDesktop? observedDesktop = null)
        {
            if (observedDesktop != null
                && TryGetPendingMasterSatelliteActivationSource(
                    observedDesktop,
                    window,
                    out var activationPlan))
            {
                // The source prefix is frozen until all pre-planned tail work has
                // completed. Treat a newcomer as an algorithmic overflow without
                // ever registering it in the manual source tree; the ordinary
                // target-only transfer path runs after m_backendLock is released.
                activeLayoutMatched = true;
                return new MasterSatelliteLocalMutationResult(
                    MasterSatelliteLocalMutationDisposition.Rejected,
                    activationPlan.SourceKey,
                    null,
                    null);
            }

            IVirtualDesktop? desktop = observedDesktop;
            if (desktop == null
                && TryResolveAttachedMasterSatelliteDesktopLocked(
                    window,
                    out var attachedDesktop))
            {
                desktop = attachedDesktop;
            }

            if (desktop != null
                && m_masterSatelliteLifecycle.TryGetState(desktop, out _))
            {
                activeLayoutMatched = true;
                var layoutKey = new LayoutStateKey(desktop, m_display);
                bool nextSlotReserved = m_algorithmicLayoutCoordinator.TryGetCapacity(
                        layoutKey,
                        out var coordinatedCapacity)
                    && coordinatedCapacity.ReservedSlots > 0;
                var result = m_masterSatelliteLifecycle.PlaceWindow(
                    m_backend,
                    desktop,
                    window,
                    nextSlotReserved);
                PublishMasterSatelliteCapacityLocked(desktop);
                return result;
            }

            // When desktop ownership is temporarily unavailable, an unregistered
            // window must not fall through to generic placement and corrupt any
            // active canonical tree. Keep it untiled/floating until a later event
            // can identify its desktop safely outside m_backendLock.
            if (desktop == null
                && m_masterSatelliteLifecycle.SnapshotStates().Count > 0)
            {
                activeLayoutMatched = true;
                return new MasterSatelliteLocalMutationResult(
                    MasterSatelliteLocalMutationDisposition.Rejected,
                    null,
                    null,
                    null);
            }

            activeLayoutMatched = false;
            return new MasterSatelliteLocalMutationResult(
                MasterSatelliteLocalMutationDisposition.NotActive,
                null,
                null,
                null);
        }

        private bool TryGetPendingMasterSatelliteActivationSource(
            IVirtualDesktop observedDesktop,
            IWindow window,
            out MasterSatelliteCapacitySourcePlan plan)
        {
            plan = m_capacityTransitionSourcePlans?.FirstOrDefault(candidate =>
                candidate.IsActivation
                && !candidate.Aborted
                && MasterSatelliteDisplayEligibility.DesktopsMatch(
                    candidate.SourceKey.VirtualDesktop,
                    observedDesktop)
                && !candidate.Transition.AdmittedWindows.Contains(window)
                && !candidate.Transition.ExtrasFromEnd.Contains(window))!;
            return plan != null;
        }

        /// <summary>
        /// Resolves the explicit desktop associated with a local window event. This
        /// deliberately does not assume CurrentDesktop: WindowAdded may describe a
        /// background desktop, and the coordinator will later reuse the same
        /// explicit-desktop lifecycle boundary for transfers.
        /// </summary>
        private bool TryResolveAttachedMasterSatelliteDesktopLocked(
            IWindow window,
            out IVirtualDesktop desktop)
        {
            foreach (var entry in m_masterSatelliteLifecycle.SnapshotStates())
            {
                var tree = m_backend.GetTree(entry.Key.VirtualDesktop);
                if (tree?.FindNode(window) != null)
                {
                    desktop = entry.Key.VirtualDesktop;
                    return true;
                }
            }

            desktop = null!;
            return false;
        }

        private bool TryResolveAttachedWindowDesktopLocked(
            IWindow window,
            out IVirtualDesktop desktop)
        {
            foreach (var candidate in m_backend.SnapshotDesktops())
            {
                if (m_backend.GetTree(candidate)?.FindNode(window) != null)
                {
                    desktop = candidate;
                    return true;
                }
            }
            desktop = null!;
            return false;
        }

        /// <summary>
        /// Resolves an attached overlay node by its owning tree. Structural
        /// panels have no single window identity, so using the tree association is
        /// required to keep their UI actions out of generic mutation paths.
        /// Must be called while holding m_backendLock.
        /// </summary>
        private bool TryResolveAttachedMasterSatelliteDesktopLocked(
            FancyWM.Layouts.Tiling.TilingNode node,
            out IVirtualDesktop desktop)
        {
            var owner = node.Desktop;
            if (owner != null)
            {
                foreach (var entry in m_masterSatelliteLifecycle.SnapshotStates())
                {
                    if (ReferenceEquals(
                        m_backend.GetTree(entry.Key.VirtualDesktop),
                        owner))
                    {
                        desktop = entry.Key.VirtualDesktop;
                        return true;
                    }
                }
            }

            desktop = null!;
            return false;
        }

        private bool IsMasterSatelliteWindow(IWindow window)
        {
            try
            {
                using (m_backendLock.EnterScope())
                {
                    return m_backend.HasWindow(window)
                        && TryResolveAttachedMasterSatelliteDesktopLocked(
                            window,
                            out _);
                }
            }
            catch (InvalidWindowReferenceException)
            {
                return false;
            }
        }

        /// <summary>
        /// Must be called while holding m_backendLock.
        /// </summary>
        private MasterSatelliteLocalMutationResult RemoveMasterSatelliteWindowLocked(
            IWindow window,
            bool preserveOriginalPosition)
        {
            var transitionPlan = m_capacityTransitionSourcePlans?
                .FirstOrDefault(plan =>
                {
                    if (plan.Aborted || plan.SourceState == null)
                    {
                        return false;
                    }
                    var node = m_backend.GetTree(
                            plan.SourceKey.VirtualDesktop)?
                        .FindNode(window);
                    return ReferenceEquals(node?.WindowReference, window);
                });
            var result = transitionPlan?.SourceState is { } transitionState
                ? m_masterSatelliteLifecycle.RemoveWindow(
                    m_backend,
                    transitionPlan.SourceKey.VirtualDesktop,
                    transitionState,
                    transitionPlan.SourceValidationSettings,
                    window,
                    preserveOriginalPosition)
                : m_masterSatelliteLifecycle.RemoveWindow(
                    m_backend,
                    window,
                    preserveOriginalPosition);
            if (result.LayoutKey is LayoutStateKey layoutKey)
            {
                PublishMasterSatelliteCapacityLocked(layoutKey.VirtualDesktop);
            }
            return result;
        }

        /// <summary>
        /// Last-resort recovery for a rejected canonical removal. The affected
        /// runtime state is disabled and the ordinary backend unregister path is
        /// used so a closed or unmanaged HWND cannot remain in the tree. Callers
        /// must hold m_backendLock.
        /// </summary>
        private bool RecoverRejectedMasterSatelliteRemovalLocked(
            IWindow window,
            MasterSatelliteLocalMutationResult result)
        {
            if (result.Disposition != MasterSatelliteLocalMutationDisposition.Rejected
                || result.LayoutKey is not LayoutStateKey layoutKey)
            {
                return false;
            }

            m_masterSatelliteLifecycle.DeactivateDesktop(layoutKey.VirtualDesktop);
            if (m_backend.HasWindow(window))
            {
                m_backend.UnregisterWindow(window);
                PublishMasterSatelliteCapacityLocked(layoutKey.VirtualDesktop);
                return true;
            }
            return false;
        }

        private void CompleteMasterSatellitePlacement(
            IWindow window,
            MasterSatelliteLocalMutationResult result,
            bool allowExistingDesktopOverflow = false,
            bool allowIncomingOwnershipProbe = true)
        {
            if (result.Disposition == MasterSatelliteLocalMutationDisposition.Placed)
            {
                if (TryRememberMasterSatelliteWindowHandle(
                        window,
                        out var placedWindowHandle))
                {
                    // A later independent failure for the same HWND must be able
                    // to establish floating state and publish one new event.
                    m_arrangeFailureNotifications.Forget(placedWindowHandle);
                }
                m_logger.Debug(
                    "Placed window {Window} in Master + Satellites layout {LayoutKey} at revision {Revision}",
                    window.DebugString(),
                    result.LayoutKey,
                    result.Operation?.After.Revision);
                InvalidateLayout();
                return;
            }
            if (result.Disposition != MasterSatelliteLocalMutationDisposition.Rejected)
            {
                return;
            }

            if (allowExistingDesktopOverflow
                && allowIncomingOwnershipProbe
                && result.LayoutKey == null
                && result.Operation == null
                && TryQueueMasterSatelliteIncomingOwnershipProbe(window))
            {
                return;
            }

            if (allowExistingDesktopOverflow
                && TryStartExistingDesktopOverflow(window, result))
            {
                return;
            }

            m_logger.Debug(
                "Master + Satellites local placement rejected for window {Window}: {Reason} ({Message}); using floating fallback for policy {Policy}",
                window.DebugString(),
                result.Operation?.FailureReason,
                result.Operation?.Message,
                m_masterSatelliteLifecycle.SettingsSnapshot.OverflowPolicy);
            NotifyMasterSatellitePlacementFailedOnce(
                window,
                sourceLayoutKey: result.LayoutKey);
        }

        /// <summary>
        /// A Win32 WindowAdded notification can precede the virtual-desktop COM
        /// ownership update by a few milliseconds. Keep the new window neutral
        /// during a bounded retry window; otherwise the first inconclusive probe
        /// permanently marks it floating before overflow can identify its source.
        /// The generation object deduplicates repeated workspace notifications
        /// without allowing a stale callback to affect a new wrapper that reuses
        /// the same HWND.
        /// </summary>
        private bool TryQueueMasterSatelliteIncomingOwnershipProbe(IWindow window)
        {
            if (!TryRememberMasterSatelliteWindowHandle(window, out var windowHandle))
            {
                return false;
            }
            if (m_masterSatelliteIncomingOwnershipProbes.TryGetValue(
                    windowHandle,
                    out var existingProbe)
                && ReferenceEquals(existingProbe.Window, window))
            {
                return true;
            }

            var probe = new MasterSatelliteIncomingOwnershipProbe(
                window,
                windowHandle);
            m_masterSatelliteIncomingOwnershipProbes[windowHandle] = probe;

            m_logger.Debug(
                "Deferring Master + Satellites placement for window {Window} until virtual-desktop ownership becomes observable",
                window.DebugString());
            if (TryScheduleMasterSatelliteIncomingOwnershipProbe(
                    probe,
                    MasterSatelliteIncomingOwnershipProbeLimit))
            {
                return true;
            }

            RemoveMasterSatelliteIncomingOwnershipProbe(probe);
            return false;
        }

        private bool TryScheduleMasterSatelliteIncomingOwnershipProbe(
            MasterSatelliteIncomingOwnershipProbe probe,
            int remainingProbes)
        {
            try
            {
                var timer = new DispatcherTimer(
                    DispatcherPriority.Background,
                    m_dispatcher)
                {
                    Interval = MasterSatelliteIncomingOwnershipProbeInterval,
                };
                EventHandler? tick = null;
                tick = (_, _) =>
                {
                    timer.Stop();
                    timer.Tick -= tick;
                    try
                    {
                        RetryMasterSatelliteIncomingOwnership(
                            probe,
                            remainingProbes);
                    }
                    catch (Exception ex)
                    {
                        if (!RemoveMasterSatelliteIncomingOwnershipProbe(probe))
                        {
                            return;
                        }
                        m_logger.Error(
                            ex,
                            "Deferred Master + Satellites ownership processing failed for window handle {WindowHandle}",
                            probe.WindowHandle);
                        NotifyMasterSatellitePlacementFailedOnce(
                            probe.Window,
                            stableWindowHandle: probe.WindowHandle);
                    }
                };
                timer.Tick += tick;
                timer.Start();
                return true;
            }
            catch (Exception ex)
            {
                m_logger.Debug(
                    ex,
                    "Could not schedule a deferred virtual-desktop ownership probe for window handle {WindowHandle}",
                    probe.WindowHandle);
                return false;
            }
        }

        private void RetryMasterSatelliteIncomingOwnership(
            MasterSatelliteIncomingOwnershipProbe probe,
            int remainingProbes)
        {
            if (!IsCurrentMasterSatelliteIncomingOwnershipProbe(probe))
            {
                return;
            }
            if (m_disposed)
            {
                RemoveMasterSatelliteIncomingOwnershipProbe(probe);
                return;
            }

            bool stillTracked;
            using (m_windowSetLock.EnterScope())
            {
                stillTracked = m_windowSet.Contains(probe.Window);
            }
            if (!stillTracked
                || !m_algorithmicWindowEvents.TryGetWindow(
                    probe.WindowHandle,
                    out var currentWindow)
                || !ReferenceEquals(currentWindow, probe.Window))
            {
                RemoveMasterSatelliteIncomingOwnershipProbe(probe);
                return;
            }

            try
            {
                if (!probe.Window.IsAlive)
                {
                    RemoveMasterSatelliteIncomingOwnershipProbe(probe);
                    return;
                }
            }
            catch (Exception ex)
            {
                m_logger.Debug(
                    ex,
                    "Deferred virtual-desktop ownership probe could not inspect window handle {WindowHandle}",
                    probe.WindowHandle);
            }

            if (TryFindMasterSatelliteWindowDesktop(
                    probe.Window,
                    out var actualDesktop))
            {
                DetectChanges(
                    probe.Window,
                    allowExistingDesktopOverflow: true,
                    observedDesktop: actualDesktop);
                RemoveMasterSatelliteIncomingOwnershipProbe(probe);
                return;
            }

            if (remainingProbes > 1
                && TryScheduleMasterSatelliteIncomingOwnershipProbe(
                    probe,
                    remainingProbes - 1))
            {
                return;
            }

            if (!RemoveMasterSatelliteIncomingOwnershipProbe(probe))
            {
                return;
            }
            m_logger.Information(
                "Master + Satellites could not resolve virtual-desktop ownership for window {Window} after {ProbeCount} deferred probes; using floating fallback",
                probe.Window.DebugString(),
                MasterSatelliteIncomingOwnershipProbeLimit);
            NotifyMasterSatellitePlacementFailedOnce(
                probe.Window,
                stableWindowHandle: probe.WindowHandle);
        }

        private bool IsCurrentMasterSatelliteIncomingOwnershipProbe(
            MasterSatelliteIncomingOwnershipProbe probe)
        {
            return m_masterSatelliteIncomingOwnershipProbes.TryGetValue(
                    probe.WindowHandle,
                    out var currentProbe)
                && ReferenceEquals(currentProbe, probe);
        }

        private bool RemoveMasterSatelliteIncomingOwnershipProbe(
            MasterSatelliteIncomingOwnershipProbe probe)
        {
            return IsCurrentMasterSatelliteIncomingOwnershipProbe(probe)
                && m_masterSatelliteIncomingOwnershipProbes.Remove(
                    probe.WindowHandle);
        }

        private void CancelMasterSatelliteIncomingOwnershipProbe(
            IWindow window,
            IntPtr windowHandle)
        {
            if (m_masterSatelliteIncomingOwnershipProbes.TryGetValue(
                    windowHandle,
                    out var probe)
                && ReferenceEquals(probe.Window, window))
            {
                m_masterSatelliteIncomingOwnershipProbes.Remove(windowHandle);
            }
        }

        private bool TryStartExistingDesktopOverflow(
            IWindow window,
            MasterSatelliteLocalMutationResult rejectedPlacement)
        {
            var settings = m_masterSatelliteLifecycle.SettingsSnapshot;
            if (!AlgorithmicDesktopCreationPolicy.AllowsExistingDesktopOverflow(
                    settings.OverflowPolicy)
                || rejectedPlacement.LayoutKey is not LayoutStateKey sourceKey
                || !MasterSatelliteDisplayEligibility.DisplaysMatch(sourceKey.Display, m_display))
            {
                return false;
            }

            Rectangle sourceOriginalPosition;
            try
            {
                sourceOriginalPosition = window.Position;
            }
            catch (Exception ex)
            {
                m_logger.Debug(
                    ex,
                    "Master + Satellites overflow could not capture source geometry for window {Window}",
                    window.DebugString());
                return false;
            }

            AlgorithmicTransferStartResult start;
            Guid correlationId = Guid.NewGuid();
            IntPtr stableWindowHandle;
            try
            {
                if (!TryRememberMasterSatelliteWindowHandle(
                    window,
                    out stableWindowHandle))
                {
                    return false;
                }
                start = m_algorithmicWindowTransfers.StartExistingDesktopTransfer(
                    correlationId,
                    window,
                    stableWindowHandle,
                    sourceKey.VirtualDesktop,
                    sourceOriginalPosition,
                    (targetKey, capacity) => PreflightOverflowDestination(
                        window,
                        targetKey,
                        capacity));
                if (!start.MoveRequested
                    && AlgorithmicDesktopCreationPolicy.ShouldCreateAfterSearch(
                        settings.OverflowPolicy,
                        start.Disposition))
                {
                    start = TryCreateDesktopAndStartOverflow(
                            window,
                            stableWindowHandle,
                            correlationId,
                            sourceKey,
                            sourceOriginalPosition)
                        ?? start;
                }
            }
            catch (Exception ex)
            {
                m_logger.Error(
                    ex,
                    "Master + Satellites destination planning failed unexpectedly for window {Window}",
                    window.DebugString());
                return false;
            }

            if (start.MoveRequested)
            {
                var selectedCapacity = start.Search.ExaminedCandidates
                    .LastOrDefault(candidate => candidate.Disposition
                        == AlgorithmicDestinationCandidateDisposition.Reserved)
                    ?.Capacity;
                ReconcileMasterSatelliteTransferAfterMove(
                    window,
                    stableWindowHandle,
                    start.Transfer!,
                    remainingDeferredProbes:
                        MasterSatelliteDeferredOwnershipProbeLimit);
                m_logger.Information(
                    "Master + Satellites overflow move requested for window {Window}; correlation={CorrelationId}, reservation={ReservationId}, source={SourceDesktop}, target={TargetDesktop}, display={Display}, role={Role}, satelliteIndex={SatelliteIndex}, targetOccupied={TargetOccupied}, targetReserved={TargetReserved}, targetCapacity={TargetCapacity}",
                    window.DebugString(),
                    start.Transfer?.CorrelationId,
                    start.Transfer?.ReservationId,
                    sourceKey.VirtualDesktop,
                    start.Transfer?.TargetDesktop,
                    m_display,
                    start.Transfer?.TargetRole,
                    start.Transfer?.TargetSatelliteIndex,
                    selectedCapacity?.OccupiedSlots,
                    selectedCapacity?.ReservedSlots,
                    selectedCapacity?.TotalCapacity);
                return true;
            }

            if (start.MoveException != null)
            {
                m_logger.Error(
                    start.MoveException,
                    "Master + Satellites MoveWindow failed; correlation={CorrelationId}, source={SourceDesktop}, target={TargetDesktop}, display={Display}",
                    start.Transfer?.CorrelationId,
                    sourceKey.VirtualDesktop,
                    start.Transfer?.TargetDesktop,
                    m_display);
            }
            if (start.RollbackException != null)
            {
                m_logger.Error(
                    start.RollbackException,
                    "Master + Satellites overflow rollback failed; correlation={CorrelationId}, source={SourceDesktop}, display={Display}",
                    start.Transfer?.CorrelationId,
                    sourceKey.VirtualDesktop,
                    m_display);
            }
            if (start.Recovery != null)
            {
                ApplyMasterSatelliteTransferRecovery(window, start.Recovery);
                return true;
            }
            string candidateDiagnostics = string.Join(
                " | ",
                start.Search.ExaminedCandidates.Select((candidate, index) =>
                    $"#{index + 1}:{candidate.Disposition}:{candidate.DiagnosticReason}"));
            m_logger.Information(
                "Master + Satellites overflow did not move window {Window}; disposition={Disposition}, reason={Reason}, candidates={Candidates}, rollbackAttempted={RollbackAttempted}, rollbackSucceeded={RollbackSucceeded}",
                window.DebugString(),
                start.Disposition,
                start.Search.DiagnosticReason,
                candidateDiagnostics,
                start.RollbackAttempted,
                start.RollbackSucceeded);
            return false;
        }

        private void ReconcileMasterSatelliteTransferAfterMove(
            IWindow window,
            IntPtr stableWindowHandle,
            PendingWindowTransfer transfer,
            int remainingDeferredProbes)
        {
            if (!IsCurrentMasterSatelliteWindowGeneration(
                    window,
                    stableWindowHandle))
            {
                // A replacement wrapper may already own this numeric HWND. The
                // reset/removal path terminalizes the old correlation; never ask
                // COM about ownership through the retired wrapper.
                return;
            }
            bool isAlive;
            try
            {
                isAlive = window.IsAlive;
            }
            catch (InvalidWindowReferenceException)
            {
                isAlive = false;
            }
            if (!isAlive)
            {
                // Treat death as terminal before any HasWindow call can observe a
                // new native window that reused the old numeric HWND.
                m_algorithmicLayoutCoordinator.WindowClosed(stableWindowHandle);
                return;
            }
            var reconciliation = m_algorithmicWindowTransfers.ReconcileAfterMove(
                window,
                stableWindowHandle,
                transfer,
                (candidate, handle, desktop) =>
                    HandleMasterSatelliteDestinationAdded(
                        candidate,
                        handle,
                        desktop).Consumed);
            if (reconciliation.Exception != null)
            {
                m_logger.Error(
                    reconciliation.Exception,
                    "Master + Satellites post-move reconciliation failed; correlation={CorrelationId}, window={Window}, target={TargetDesktop}, display={Display}, disposition={Disposition}",
                    transfer.CorrelationId,
                    window.DebugString(),
                    transfer.TargetDesktop,
                    m_display,
                    reconciliation.Disposition);
            }
            else
            {
                m_logger.Debug(
                    "Master + Satellites post-move reconciliation; correlation={CorrelationId}, target={TargetDesktop}, display={Display}, disposition={Disposition}, sourceObserved={SourceObserved}",
                    transfer.CorrelationId,
                    transfer.TargetDesktop,
                    m_display,
                    reconciliation.Disposition,
                    reconciliation.SourceRemovalObserved);
            }

            if (!reconciliation.ShouldRetry
                || transfer.IsTerminal
                || m_disposed)
            {
                return;
            }

            if (remainingDeferredProbes <= 0
                || DateTimeOffset.UtcNow >= transfer.Deadline)
            {
                FailMasterSatellitePostMoveReconciliation(
                    window,
                    stableWindowHandle,
                    transfer,
                    "PostMoveOwnershipNotObserved");
                return;
            }

            try
            {
                var timer = new DispatcherTimer(
                    DispatcherPriority.Background,
                    m_dispatcher)
                {
                    // A dispatcher yield alone is shorter than the Windows VDM
                    // COM propagation window on some builds. Use real elapsed
                    // time while retaining a strict fixed probe budget.
                    Interval = MasterSatelliteIncomingOwnershipProbeInterval,
                };
                EventHandler? tick = null;
                tick = (_, _) =>
                {
                    timer.Stop();
                    timer.Tick -= tick;
                    try
                    {
                        if (m_disposed
                            || !m_algorithmicLayoutCoordinator.TryGetTransfer(
                                transfer.CorrelationId,
                                out var latest)
                            || latest.IsTerminal)
                        {
                            return;
                        }
                        if (DateTimeOffset.UtcNow >= latest.Deadline)
                        {
                            FailMasterSatellitePostMoveReconciliation(
                                window,
                                stableWindowHandle,
                                latest,
                                "PostMoveOwnershipDeadlineExpired");
                        }
                        else
                        {
                            ReconcileMasterSatelliteTransferAfterMove(
                                window,
                                stableWindowHandle,
                                latest,
                                remainingDeferredProbes - 1);
                        }
                    }
                    catch (Exception ex)
                    {
                        m_logger.Error(
                            ex,
                            "Master + Satellites deferred post-move reconciliation failed; correlation={CorrelationId}",
                            transfer.CorrelationId);
                        FailMasterSatellitePostMoveReconciliation(
                            window,
                            stableWindowHandle,
                            transfer,
                            "PostMoveReconciliationThrew");
                    }
                };
                timer.Tick += tick;
                timer.Start();
            }
            catch (InvalidOperationException ex)
            {
                m_logger.Error(
                    ex,
                    "Master + Satellites could not queue deferred post-move reconciliation; correlation={CorrelationId}",
                    transfer.CorrelationId);
                FailMasterSatellitePostMoveReconciliation(
                    window,
                    stableWindowHandle,
                    transfer,
                    "PostMoveReconciliationSchedulingFailed");
            }
        }

        private void FailMasterSatellitePostMoveReconciliation(
            IWindow window,
            IntPtr stableWindowHandle,
            PendingWindowTransfer transfer,
            string reason)
        {
            if (m_disposed
                || transfer.IsTerminal
                || !m_algorithmicLayoutCoordinator.Fail(
                    transfer.CorrelationId,
                    stableWindowHandle,
                    reason))
            {
                return;
            }

            m_logger.Warning(
                "Master + Satellites post-move ownership did not become observable within the bounded retry window; correlation={CorrelationId}, window={Window}, target={TargetDesktop}, reason={Reason}",
                transfer.CorrelationId,
                window.DebugString(),
                transfer.TargetDesktop,
                reason);
        }

        private AlgorithmicTransferStartResult? TryCreateDesktopAndStartOverflow(
            IWindow window,
            IntPtr stableWindowHandle,
            Guid correlationId,
            LayoutStateKey sourceKey,
            Rectangle sourceOriginalPosition)
        {
            var settings = m_masterSatelliteLifecycle.SettingsSnapshot;
            var creation = m_algorithmicDesktopCreations.CreateAndPrepareDesktop(
                stableWindowHandle,
                correlationId,
                settings.MaxAutoCreatedDesktops,
                EnsureMasterSatelliteDesktopReady);
            if (!creation.Succeeded || creation.CreatedDesktop == null)
            {
                if (creation.Exception != null)
                {
                    m_logger.Error(
                        creation.Exception,
                        "Master + Satellites could not create and prepare an overflow desktop; correlation={CorrelationId}, display={Display}, disposition={Disposition}, reason={Reason}",
                        correlationId,
                        m_display,
                        creation.Disposition,
                        creation.DiagnosticReason);
                }
                else
                {
                    m_logger.Debug(
                        "Master + Satellites desktop creation did not proceed; correlation={CorrelationId}, display={Display}, disposition={Disposition}, reason={Reason}",
                        correlationId,
                        m_display,
                        creation.Disposition,
                        creation.DiagnosticReason);
                }
                return null;
            }

            var createdDesktop = creation.CreatedDesktop;
            m_logger.Information(
                "Master + Satellites created overflow desktop {Desktop}; correlation={CorrelationId}, display={Display}, sessionCreatedCount={CreatedCount}",
                createdDesktop,
                correlationId,
                m_display,
                m_algorithmicLayoutCoordinator.SnapshotDesktopCreations()
                    .SuccessfulCreationCount);
            return m_algorithmicWindowTransfers.StartExistingDesktopTransfer(
                correlationId,
                window,
                stableWindowHandle,
                sourceKey.VirtualDesktop,
                sourceOriginalPosition,
                (targetKey, capacity) => PreflightOverflowDestination(
                    window,
                    targetKey,
                    capacity));
        }

        private void EnsureMasterSatelliteDesktopReady(IVirtualDesktop desktop)
        {
            MasterSatelliteLifecycleResult? lifecycleResult;
            using (m_backendLock.EnterScope())
            {
                if (m_backend.GetTree(desktop) == null)
                {
                    var orientation = m_display.Bounds.Width >= m_display.Bounds.Height
                        ? FancyWM.Layouts.Tiling.PanelOrientation.Horizontal
                        : FancyWM.Layouts.Tiling.PanelOrientation.Vertical;
                    m_backend.RegisterDesktop(
                        desktop,
                        m_display.WorkArea,
                        orientation);
                }
                lifecycleResult = OnMasterSatelliteDesktopAddedLocked(desktop);
                PublishMasterSatelliteCapacityLocked(desktop);
            }
            LogMasterSatelliteLifecycleResult(lifecycleResult);
        }

        private AlgorithmicDestinationPreflightResult PreflightOverflowDestination(
            IWindow window,
            LayoutStateKey targetKey,
            CoordinatorCapacitySnapshot capacity)
        {
            try
            {
                using (m_backendLock.EnterScope())
                {
                    if (!m_masterSatelliteLifecycle.TryGetState(
                            targetKey.VirtualDesktop,
                            out var targetState)
                        || capacity.NextRole is not ReservedRole role)
                    {
                        return AlgorithmicDestinationPreflightResult.Reject(
                            "The destination runtime state or exact role is unavailable.");
                    }

                    int candidateOrdinal = role == ReservedRole.Master
                        ? 0
                        : capacity.NextSatelliteIndex is int candidateIndex
                            ? candidateIndex + 1
                            : -1;
                    if (candidateOrdinal < 0)
                    {
                        return AlgorithmicDestinationPreflightResult.Reject(
                            "The destination candidate has an inconsistent exact role/index.");
                    }

                    var placements = new List<MasterSatellitePlacementPreflight>();
                    foreach (var reservation in m_algorithmicLayoutCoordinator
                        .SnapshotReservations()
                        .Where(reservation =>
                            LayoutStateKeyIdentityComparer.Instance.Equals(
                                reservation.LayoutKey,
                                targetKey))
                        .Select(reservation => new
                        {
                            Reservation = reservation,
                            Ordinal = reservation.Role == ReservedRole.Master
                                ? 0
                                : (reservation.SatelliteIndex ?? int.MaxValue) + 1,
                        })
                        .Where(item => item.Ordinal >= capacity.OccupiedSlots
                            && item.Ordinal < candidateOrdinal)
                        .OrderBy(item => item.Ordinal))
                    {
                        if (!m_algorithmicWindowEvents.TryGetWindow(
                            reservation.Reservation.WindowHandle,
                            out var reservedWindow))
                        {
                            return AlgorithmicDestinationPreflightResult.Reject(
                                "A preceding reserved window is no longer available for destination preflight.");
                        }
                        placements.Add(new MasterSatellitePlacementPreflight(
                            reservedWindow,
                            reservation.Reservation.Role == ReservedRole.Master
                                ? MasterSatelliteWindowRole.Master
                                : MasterSatelliteWindowRole.Satellite,
                            reservation.Reservation.SatelliteIndex));
                    }
                    if (placements.Count != candidateOrdinal - capacity.OccupiedSlots)
                    {
                        return AlgorithmicDestinationPreflightResult.Reject(
                            "The exact reservation prefix is incomplete for destination preflight.");
                    }
                    placements.Add(new MasterSatellitePlacementPreflight(
                        window,
                        role == ReservedRole.Master
                            ? MasterSatelliteWindowRole.Master
                            : MasterSatelliteWindowRole.Satellite,
                        capacity.NextSatelliteIndex));

                    var preflight = m_backend.PreflightMasterSatellitePlacements(
                        targetKey.VirtualDesktop,
                        targetState,
                        m_masterSatelliteLifecycle.SettingsSnapshot,
                        placements);
                    return preflight.Succeeded
                        ? AlgorithmicDestinationPreflightResult.Accept()
                        : AlgorithmicDestinationPreflightResult.Reject(
                            preflight.Operation.Message
                                ?? "The destination placement preflight was rejected.");
                }
            }
            catch (Exception ex)
            {
                m_logger.Error(
                    ex,
                    "Master + Satellites destination preflight failed for window {Window}, desktop {Desktop}, display {Display}",
                    window.DebugString(),
                    targetKey.VirtualDesktop,
                    targetKey.Display);
                return AlgorithmicDestinationPreflightResult.Reject(
                    "The destination placement preflight failed unexpectedly.");
            }
        }

        private AlgorithmicTransferMaterializationResult MaterializeOverflowDestination(
            IWindow window,
            PendingWindowTransfer transfer,
            AlgorithmicSlotReservation reservation)
        {
            m_masterSatelliteExistingWindowTransfers.TryGetValue(
                transfer.CorrelationId,
                out var existingWindowContext);
            try
            {
                using (m_backendLock.EnterScope())
                {
                    if (!m_masterSatelliteLifecycle.TryGetState(
                            transfer.TargetDesktop,
                            out var targetState))
                    {
                        return AlgorithmicTransferMaterializationResult.Reject(
                            "The destination runtime state is unavailable.");
                    }

                    bool windowAlreadyRegistered = m_backend.HasWindow(window);
                    if (existingWindowContext == null
                        && !windowAlreadyRegistered
                        && !m_masterSatelliteDestinationRestorePoints.ContainsKey(
                            transfer.CorrelationId))
                    {
                        m_masterSatelliteLifecycle.TryGetState(
                            transfer.SourceDesktop,
                            out var sourceState);
                        m_masterSatelliteDestinationRestorePoints.Add(
                            transfer.CorrelationId,
                            m_backend.CaptureMasterSatelliteTransferRestorePoint(
                                transfer.SourceDesktop,
                                sourceState,
                                m_masterSatelliteLifecycle.SettingsSnapshot,
                                transfer.TargetDesktop,
                                targetState,
                                m_masterSatelliteLifecycle.SettingsSnapshot,
                                window));
                    }

                    if (existingWindowContext != null
                        && m_backend.GetTree(
                            existingWindowContext.Work.Plan.SourceKey.VirtualDesktop)?
                            .FindNode(window) != null)
                    {
                        var restorePoint =
                            m_backend.CaptureMasterSatelliteTransferRestorePoint(
                                existingWindowContext.Work.Plan.SourceKey.VirtualDesktop,
                                existingWindowContext.Work.Plan.SourceState,
                                existingWindowContext.Work.Plan.SourceValidationSettings,
                                transfer.TargetDesktop,
                                targetState,
                                m_masterSatelliteLifecycle.SettingsSnapshot,
                                window);
                        existingWindowContext.RestorePoint = restorePoint;
                        var detach = m_backend.DetachMasterSatelliteTransferSource(
                            restorePoint);
                        if (!detach.Succeeded)
                        {
                            RestoreMasterSatelliteMaterializationLocked(
                                existingWindowContext,
                                transfer);
                            return AlgorithmicTransferMaterializationResult.Reject(
                                detach.Message
                                    ?? "The existing source window could not be detached transactionally.");
                        }
                    }

                    if (m_backend.HasWindow(window))
                    {
                        bool exactMatch = reservation.Role == ReservedRole.Master
                            ? Equals(targetState.Master, window)
                            : reservation.SatelliteIndex is int index
                                && index >= 0
                                && index < targetState.Satellites.Count
                                && Equals(targetState.Satellites[index], window);
                        if (!exactMatch)
                        {
                            RestoreMasterSatelliteMaterializationLocked(
                                existingWindowContext,
                                transfer);
                            return AlgorithmicTransferMaterializationResult.Reject(
                                "The window is already registered outside its exact reserved slot.");
                        }
                    }
                    else
                    {
                        var placement = m_backend.RegisterReservedWindow(
                            reservation.LayoutKey,
                            targetState,
                            m_masterSatelliteLifecycle.SettingsSnapshot,
                            window,
                            reservation.ToWorkspaceSlot(),
                            transfer.SourceOriginalPosition);
                        if (!placement.Succeeded)
                        {
                            RestoreMasterSatelliteMaterializationLocked(
                                existingWindowContext,
                                transfer);
                            return AlgorithmicTransferMaterializationResult.Reject(
                                placement.Operation.Message
                                    ?? "The exact reserved destination slot could not be materialized.");
                        }
                    }

                    var invariant = m_backend.ValidateMasterSatelliteLayout(
                        transfer.TargetDesktop,
                        targetState,
                        m_masterSatelliteLifecycle.SettingsSnapshot);
                    if (!invariant.IsValid)
                    {
                        RestoreMasterSatelliteMaterializationLocked(
                            existingWindowContext,
                            transfer);
                        return AlgorithmicTransferMaterializationResult.Reject(
                            invariant.Description);
                    }
                    if (existingWindowContext != null)
                    {
                        PublishMasterSatelliteCapacityLocked(
                            existingWindowContext.Work.Plan.SourceKey.VirtualDesktop);
                    }
                    PublishMasterSatelliteCapacityLocked(transfer.TargetDesktop);
                    return AlgorithmicTransferMaterializationResult.Accept();
                }
            }
            catch (Exception ex)
            {
                try
                {
                    using (m_backendLock.EnterScope())
                    {
                        RestoreMasterSatelliteMaterializationLocked(
                            existingWindowContext,
                            transfer);
                    }
                }
                catch (Exception rollbackException)
                {
                    m_logger.Error(
                        rollbackException,
                        "Master + Satellites endpoint rollback failed; correlation={CorrelationId}",
                        transfer.CorrelationId);
                }
                m_logger.Error(
                    ex,
                    "Master + Satellites destination materialization failed for window {Window}; correlation={CorrelationId}, target={TargetDesktop}, display={Display}",
                    window.DebugString(),
                    transfer.CorrelationId,
                    transfer.TargetDesktop,
                    transfer.TargetDisplay);
                throw;
            }
        }

        /// <summary>
        /// Restores every endpoint captured before destination mutation. Existing
        /// tiled-window transfers keep their source/target token on their work
        /// context; new-window overflow keeps the same token by correlation until
        /// coordinator commit. The caller must hold <c>m_backendLock</c>.
        /// </summary>
        private void RestoreMasterSatelliteMaterializationLocked(
            MasterSatelliteExistingWindowTransferContext? existingContext,
            PendingWindowTransfer transfer)
        {
            if (existingContext?.RestorePoint != null)
            {
                RestoreMasterSatelliteExistingWindowTransferLocked(existingContext);
                return;
            }
            if (!m_masterSatelliteDestinationRestorePoints.TryGetValue(
                    transfer.CorrelationId,
                    out var restorePoint))
            {
                return;
            }

            m_backend.RestoreMasterSatelliteTransfer(restorePoint);
            m_masterSatelliteDestinationRestorePoints.Remove(
                transfer.CorrelationId);
            PublishMasterSatelliteCapacityLocked(transfer.SourceDesktop);
            PublishMasterSatelliteCapacityLocked(transfer.TargetDesktop);
        }

        private void RestoreMasterSatelliteExistingWindowTransferLocked(
            MasterSatelliteExistingWindowTransferContext? context)
        {
            if (context?.RestorePoint is not { } restorePoint)
            {
                return;
            }
            m_backend.RestoreMasterSatelliteTransfer(restorePoint);
            context.RestorePoint = null;
            PublishMasterSatelliteCapacityLocked(
                context.Work.Plan.SourceKey.VirtualDesktop);
            PublishMasterSatelliteCapacityLocked(context.Transfer.TargetDesktop);
        }

        private void RollbackMasterSatelliteMaterialization(
            IWindow window,
            PendingWindowTransfer transfer,
            AlgorithmicSlotReservation reservation)
        {
            using (m_backendLock.EnterScope())
            {
                m_masterSatelliteExistingWindowTransfers.TryGetValue(
                    transfer.CorrelationId,
                    out var context);
                bool hadCapturedRestorePoint = context?.RestorePoint != null
                    || m_masterSatelliteDestinationRestorePoints.ContainsKey(
                        transfer.CorrelationId);
                RestoreMasterSatelliteMaterializationLocked(context, transfer);
                if (hadCapturedRestorePoint)
                {
                    return;
                }

                if (!m_masterSatelliteLifecycle.TryGetState(
                        transfer.TargetDesktop,
                        out var targetState))
                {
                    throw new InvalidOperationException(
                        "The destination runtime state disappeared before endpoint rollback.");
                }
                bool exactReservedSlot = reservation.Role == ReservedRole.Master
                    ? Equals(targetState.Master, window)
                    : reservation.SatelliteIndex is int index
                        && index >= 0
                        && index < targetState.Satellites.Count
                        && Equals(targetState.Satellites[index], window);
                if (!exactReservedSlot
                    || m_backend.GetTree(transfer.TargetDesktop)?
                        .FindNode(window) == null)
                {
                    throw new InvalidOperationException(
                        "The materialized destination no longer owns the exact reserved slot.");
                }

                var rollback = m_backend.UnregisterMasterSatelliteWindow(
                    transfer.TargetDesktop,
                    targetState,
                    m_masterSatelliteLifecycle.SettingsSnapshot,
                    window,
                    preserveOriginalPosition: true);
                if (!rollback.Succeeded)
                {
                    throw new InvalidOperationException(
                        rollback.Message
                            ?? "The materialized destination could not be rolled back.");
                }
                PublishMasterSatelliteCapacityLocked(transfer.TargetDesktop);
            }
        }

        private MasterSatelliteDestinationAddDecision HandleMasterSatelliteDestinationAdded(
            IWindow window,
            IntPtr windowHandle,
            IVirtualDesktop actualDesktop,
            bool canMaterialize = true)
        {
            if (windowHandle == IntPtr.Zero
                || !m_algorithmicLayoutCoordinator.TryGetRecentTransfer(
                    windowHandle,
                    out var transfer))
            {
                return new MasterSatelliteDestinationAddDecision(false, false);
            }
            // Explicit/fallback floating ownership is workspace-wide. A manual
            // float or cross-display move that races a retained transfer must win
            // over destination materialization.
            canMaterialize &= !IsFloatingWindow(window);
            if (!MasterSatelliteDisplayEligibility.DisplaysMatch(
                    transfer.TargetDisplay,
                    m_display))
            {
                return new MasterSatelliteDestinationAddDecision(false, true);
            }
            if (!MasterSatelliteDisplayEligibility.DesktopsMatch(
                    transfer.TargetDesktop,
                    actualDesktop))
            {
                if (!transfer.IsTerminal
                    && MasterSatelliteDisplayEligibility.DesktopsMatch(
                        transfer.SourceDesktop,
                        actualDesktop))
                {
                    // MoveWindow may have returned before VDM COM ownership stops
                    // reporting the source. A duplicate Added/Discover pass must
                    // not reinterpret that stale source observation as a manual
                    // move and cancel the still-reserved automatic transfer.
                    return new MasterSatelliteDestinationAddDecision(
                        true,
                        true,
                        true);
                }
                if (transfer.IsTerminal
                    && transfer.State != PendingWindowTransferState.Committed)
                {
                    // A failed transfer may synchronously return to its source.
                    // Keep it floating there and consume the correlated add so it
                    // cannot be registered and overflowed again.
                    m_algorithmicWindowEvents.RecordManualMove(
                        windowHandle,
                        transfer.SourceDesktop,
                        actualDesktop,
                        out _);
                    bool returnedToSource = MasterSatelliteDisplayEligibility.DesktopsMatch(
                        transfer.SourceDesktop,
                        actualDesktop);
                    var recovery = returnedToSource
                        ? m_algorithmicWindowTransfers.RecoverTerminalTransfer(
                            window,
                            transfer.CorrelationId)
                        : m_algorithmicWindowTransfers.RecoverTerminalTransfer(
                            window,
                            transfer.CorrelationId,
                            allowRollbackToSource: false,
                            preferredFloatingDesktop: actualDesktop);
                    if (m_masterSatelliteExistingWindowTransfers.TryGetValue(
                            transfer.CorrelationId,
                            out var existingContext))
                    {
                        CompleteMasterSatelliteExistingWindowFailure(
                            existingContext,
                            recovery,
                            windowHandle);
                        ProcessMasterSatelliteCapacityTransitions();
                    }
                    else
                    {
                        ApplyMasterSatelliteTransferRecovery(
                            window,
                            recovery,
                            windowHandle);
                    }
                    return new MasterSatelliteDestinationAddDecision(true, true);
                }
                if (!transfer.IsTerminal)
                {
                    m_algorithmicLayoutCoordinator.Cancel(
                        transfer.CorrelationId,
                        transfer.WindowHandle,
                        "UnexpectedDestinationObserved");
                    m_algorithmicWindowEvents.RecordManualMove(
                        windowHandle,
                        transfer.SourceDesktop,
                        actualDesktop,
                        out _);
                    var recovery = m_algorithmicWindowTransfers.RecoverTerminalTransfer(
                        window,
                        transfer.CorrelationId,
                        allowRollbackToSource: false,
                        preferredFloatingDesktop: actualDesktop);
                    if (m_masterSatelliteExistingWindowTransfers.TryGetValue(
                            transfer.CorrelationId,
                            out var existingContext))
                    {
                        CompleteMasterSatelliteExistingWindowFailure(
                            existingContext,
                            recovery,
                            windowHandle);
                        ProcessMasterSatelliteCapacityTransitions();
                    }
                    else
                    {
                        ApplyMasterSatelliteTransferRecovery(
                            window,
                            recovery,
                            windowHandle);
                    }
                    return new MasterSatelliteDestinationAddDecision(true, true);
                }

                // A later manual move after a committed transfer must continue
                // through the explicit manual-move path, but never overflow.
                return new MasterSatelliteDestinationAddDecision(false, true);
            }

            if (transfer.State == PendingWindowTransferState.Committed)
            {
                bool knownAtRetainedTarget =
                    m_algorithmicWindowEvents.TryGetKnownDesktop(
                        windowHandle,
                        out var knownDesktop)
                    && MasterSatelliteDisplayEligibility.DesktopsMatch(
                        knownDesktop,
                        transfer.TargetDesktop);
                bool attachedAtRetainedTarget;
                using (m_backendLock.EnterScope())
                {
                    attachedAtRetainedTarget = m_backend.GetTree(
                            transfer.TargetDesktop)?
                        .FindNode(window) != null;
                }
                if (!knownAtRetainedTarget || !attachedAtRetainedTarget)
                {
                    // The coordinator retains completed correlations briefly so
                    // duplicate OS events are idempotent. A user can move the
                    // same HWND away and back during that interval; such an Add
                    // is a new manual move, not a duplicate transfer arrival.
                    return new MasterSatelliteDestinationAddDecision(false, true);
                }
            }

            if (!canMaterialize)
            {
                if (transfer.State == PendingWindowTransferState.Committed)
                {
                    // The caller will run DetectChanges and remove any already
                    // materialized node through the normal eligibility path.
                    return new MasterSatelliteDestinationAddDecision(false, true);
                }
                if (!transfer.IsTerminal)
                {
                    m_algorithmicLayoutCoordinator.Cancel(
                        transfer.CorrelationId,
                        transfer.WindowHandle,
                        "DestinationWindowNoLongerEligible");
                }
                RememberMasterSatelliteWindowDesktop(windowHandle, actualDesktop);
                var recovery = RecoverMasterSatelliteTerminalTransfer(
                    window,
                    transfer);
                if (m_masterSatelliteExistingWindowTransfers.TryGetValue(
                        transfer.CorrelationId,
                        out var existingContext))
                {
                    CompleteMasterSatelliteExistingWindowFailure(
                        existingContext,
                        recovery,
                        windowHandle);
                    ProcessMasterSatelliteCapacityTransitions();
                }
                else
                {
                    ApplyMasterSatelliteTransferRecovery(
                        window,
                        recovery,
                        windowHandle);
                }
                return new MasterSatelliteDestinationAddDecision(true, true);
            }

            if (transfer.IsTerminal
                && transfer.State != PendingWindowTransferState.Committed)
            {
                RememberMasterSatelliteWindowDesktop(windowHandle, actualDesktop);
                var recovery = RecoverMasterSatelliteTerminalTransfer(
                    window,
                    transfer);
                if (m_masterSatelliteExistingWindowTransfers.TryGetValue(
                        transfer.CorrelationId,
                        out var existingContext))
                {
                    CompleteMasterSatelliteExistingWindowFailure(
                        existingContext,
                        recovery,
                        windowHandle);
                    ProcessMasterSatelliteCapacityTransitions();
                }
                else
                {
                    ApplyMasterSatelliteTransferRecovery(
                        window,
                        recovery,
                        windowHandle);
                }
                return new MasterSatelliteDestinationAddDecision(true, true);
            }

            var arrival = m_algorithmicWindowTransfers.ObserveDestinationAdded(
                window,
                windowHandle,
                actualDesktop,
                (pending, reservation) => MaterializeOverflowDestination(
                    window,
                    pending,
                    reservation),
                (pending, reservation) => RollbackMasterSatelliteMaterialization(
                    window,
                    pending,
                    reservation));
            if (arrival.Disposition
                == AlgorithmicTransferArrivalDisposition.AwaitingPrecedingSlot)
            {
                m_logger.Debug(
                    "Master + Satellites destination arrival is waiting for a preceding reserved slot; correlation={CorrelationId}, window={Window}, target={TargetDesktop}, display={Display}, role={Role}, satelliteIndex={SatelliteIndex}",
                    arrival.Transfer?.CorrelationId,
                    window.DebugString(),
                    actualDesktop,
                    m_display,
                    arrival.Transfer?.TargetRole,
                    arrival.Transfer?.TargetSatelliteIndex);
                return new MasterSatelliteDestinationAddDecision(
                    true,
                    true,
                    true);
            }
            if (arrival.Disposition == AlgorithmicTransferArrivalDisposition.Materialized)
            {
                if (arrival.Transfer != null)
                {
                    // The coordinator has committed and released its reservation.
                    // Retire the exact source work before logging, notification,
                    // follow policy, or any other presentation callback can fail.
                    CompleteMasterSatelliteExistingWindowSuccess(
                        arrival.Transfer.CorrelationId);
                    using (m_backendLock.EnterScope())
                    {
                        m_masterSatelliteDestinationRestorePoints.Remove(
                            arrival.Transfer.CorrelationId);
                    }
                }
                ProcessMasterSatelliteCapacityTransitions();
                MarkWindowTiled(window);
                m_arrangeFailureNotifications.Forget(windowHandle);
                RememberMasterSatelliteWindowDesktop(windowHandle, actualDesktop);
                m_algorithmicWindowEvents.TryConsumeManualMove(
                    windowHandle,
                    actualDesktop,
                    out _);
                m_logger.Information(
                    "Master + Satellites overflow committed; correlation={CorrelationId}, window={Window}, source={SourceDesktop}, target={TargetDesktop}, display={Display}, role={Role}, satelliteIndex={SatelliteIndex}",
                    arrival.Transfer?.CorrelationId,
                    window.DebugString(),
                    arrival.Transfer?.SourceDesktop,
                    actualDesktop,
                    m_display,
                    arrival.Transfer?.TargetRole,
                    arrival.Transfer?.TargetSatelliteIndex);
                if (arrival.Transfer != null)
                {
                    RaiseMasterSatelliteTransferEvent(
                        AlgorithmicLayoutEventKind.WindowMovedToDesktop,
                        window,
                        windowHandle,
                        arrival.Transfer,
                        "CapacityReached",
                        "AlgorithmicLayout.WindowMovedToDesktop");
                }
                InvalidateLayout();

                var follow = m_algorithmicWindowTransfers.FollowCommittedTransfer(
                    arrival.Transfer!,
                    m_masterSatelliteLifecycle.SettingsSnapshot.FollowOverflowWindow);
                if (follow.Exception != null)
                {
                    m_logger.Error(
                        follow.Exception,
                        "Master + Satellites could not follow committed overflow to desktop {Desktop}",
                        actualDesktop);
                }
                DrainMasterSatelliteDestinationArrivals(actualDesktop);
                return new MasterSatelliteDestinationAddDecision(true, true);
            }

            if (arrival.Disposition == AlgorithmicTransferArrivalDisposition.AlreadyTerminal
                && arrival.Transfer?.State == PendingWindowTransferState.Committed)
            {
                RememberMasterSatelliteWindowDesktop(windowHandle, actualDesktop);
                return new MasterSatelliteDestinationAddDecision(true, true);
            }
            if (arrival.Disposition == AlgorithmicTransferArrivalDisposition.AlreadyTerminal)
            {
                // The first terminal observation already applied its fallback and
                // notification. A duplicate workspace event is correlation noise.
                return new MasterSatelliteDestinationAddDecision(true, true);
            }

            if (arrival.Exception != null)
            {
                m_logger.Error(
                    arrival.Exception,
                    "Master + Satellites destination event failed; correlation={CorrelationId}, window={Window}, disposition={Disposition}",
                    arrival.Transfer?.CorrelationId,
                    window.DebugString(),
                    arrival.Disposition);
            }
            m_logger.Warning(
                "Master + Satellites destination event was not materialized; correlation={CorrelationId}, window={Window}, disposition={Disposition}, reason={Reason}",
                arrival.Transfer?.CorrelationId,
                window.DebugString(),
                arrival.Disposition,
                arrival.DiagnosticReason);
            if (arrival.Transfer is { IsTerminal: true } failedTransfer)
            {
                var recovery = m_algorithmicWindowTransfers.RecoverTerminalTransfer(
                    window,
                    failedTransfer.CorrelationId);
                if (m_masterSatelliteExistingWindowTransfers.TryGetValue(
                        failedTransfer.CorrelationId,
                        out var context))
                {
                    CompleteMasterSatelliteExistingWindowFailure(
                        context,
                        recovery,
                        windowHandle);
                    ProcessMasterSatelliteCapacityTransitions();
                }
                else
                {
                    ApplyMasterSatelliteTransferRecovery(window, recovery, windowHandle);
                }
            }
            else
            {
                NotifyMasterSatellitePlacementFailedOnce(
                    window,
                    windowHandle,
                    arrival.Transfer?.CorrelationId ?? Guid.Empty);
            }
            return new MasterSatelliteDestinationAddDecision(true, true);
        }

        private void CompleteMasterSatelliteExistingWindowSuccess(Guid correlationId)
        {
            if (!m_masterSatelliteExistingWindowTransfers.Remove(
                    correlationId,
                    out var context))
            {
                return;
            }
            // A committed target owns the live mutation; dropping the restore
            // point is the endpoint commit of the source/target transaction.
            context.RestorePoint = null;
            context.Work.Completed = true;
        }

        private void CompleteMasterSatelliteExistingWindowFailure(
            MasterSatelliteExistingWindowTransferContext context,
            AlgorithmicTransferRecoveryResult recovery,
            IntPtr stableWindowHandle)
        {
            if (context.Work.Completed)
            {
                return;
            }

            if (context.RestorePoint != null)
            {
                try
                {
                    using (m_backendLock.EnterScope())
                    {
                        RestoreMasterSatelliteExistingWindowTransferLocked(context);
                    }
                }
                catch (Exception ex)
                {
                    m_logger.Error(
                        ex,
                        "Could not restore existing Master + Satellites source after failed transfer; correlation={CorrelationId}",
                        context.Transfer.CorrelationId);
                }
            }

            // Recovery first restores OS ownership (or positively reports that it
            // cannot). The deliberate capacity fallback then removes the exact
            // tail satellite and leaves that HWND floating wherever it survived.
            bool detached = FloatMasterSatelliteCapacityWindow(
                context.Work,
                presentFallback: false);
            if (!detached)
            {
                AbortMasterSatelliteCapacitySourcePlan(context.Work.Plan);
            }
            m_masterSatelliteExistingWindowTransfers.Remove(
                context.Transfer.CorrelationId);
            context.Work.Completed = true;
            if (detached)
            {
                // Terminal bookkeeping is complete before any user-facing event,
                // restore positioning, logging callback, or layout invalidation.
                PresentMasterSatelliteCapacityFloatingFallback(context.Work);
                ApplyMasterSatelliteTransferRecovery(
                    context.Work.Window,
                    recovery,
                    stableWindowHandle);
            }
        }

        /// <summary>
        /// Materializes destination events that arrived before an earlier exact
        /// reservation. The coordinator exposes only immutable transfer snapshots;
        /// window references come from the stable HWND tracker owned by this
        /// service. A guard turns nested commits into one deterministic drain loop.
        /// </summary>
        private void DrainMasterSatelliteDestinationArrivals(
            IVirtualDesktop targetDesktop)
        {
            if (m_drainingAlgorithmicDestinationArrivals)
            {
                return;
            }

            m_drainingAlgorithmicDestinationArrivals = true;
            try
            {
                while (true)
                {
                    var waiting = m_algorithmicLayoutCoordinator.SnapshotTransfers()
                        .Where(transfer =>
                            transfer.State
                                == PendingWindowTransferState.DestinationObserved
                            && MasterSatelliteDisplayEligibility.DisplaysMatch(
                                transfer.TargetDisplay,
                                m_display)
                            && MasterSatelliteDisplayEligibility.DesktopsMatch(
                                transfer.TargetDesktop,
                                targetDesktop))
                        .OrderBy(transfer => transfer.TargetRole == ReservedRole.Master
                            ? 0
                            : (transfer.TargetSatelliteIndex ?? int.MaxValue) + 1)
                        .ThenBy(transfer => transfer.CreatedAt)
                        .FirstOrDefault();
                    if (waiting == null
                        || !m_algorithmicWindowEvents.TryGetWindow(
                            waiting.WindowHandle,
                            out var waitingWindow))
                    {
                        return;
                    }

                    bool stillOwnedByTarget;
                    try
                    {
                        stillOwnedByTarget = waitingWindow.IsAlive
                            && waiting.TargetDesktop.IsAlive
                            && waiting.TargetDesktop.HasWindow(waitingWindow);
                    }
                    catch (Exception ex) when (ex is InvalidWindowReferenceException
                        or InvalidVirtualDesktopReferenceException
                        or System.Runtime.InteropServices.ExternalException)
                    {
                        m_logger.Debug(
                            ex,
                            "Could not verify deferred Master + Satellites destination ownership; correlation={CorrelationId}, windowHandle={WindowHandle}, target={TargetDesktop}",
                            waiting.CorrelationId,
                            waiting.WindowHandle,
                            waiting.TargetDesktop);
                        return;
                    }
                    if (!stillOwnedByTarget)
                    {
                        bool isAlive;
                        try
                        {
                            isAlive = waitingWindow.IsAlive;
                        }
                        catch (InvalidWindowReferenceException)
                        {
                            isAlive = false;
                        }
                        if (!isAlive)
                        {
                            m_algorithmicLayoutCoordinator.WindowClosed(
                                waiting.WindowHandle);
                            continue;
                        }
                        if (!TryFindMasterSatelliteWindowDesktop(
                                waitingWindow,
                                out var actualDesktop))
                        {
                            // Ownership can be briefly unobservable while the OS
                            // publishes a desktop move. Leave the intent pending;
                            // the next workspace event or timeout will reconcile it.
                            return;
                        }

                        m_algorithmicLayoutCoordinator.Cancel(
                            waiting.CorrelationId,
                            waiting.WindowHandle,
                            "ManualMoveDuringDeferredDestinationArrival");
                        RememberMasterSatelliteWindowDesktop(
                            waiting.WindowHandle,
                            actualDesktop);
                        var recovery = m_algorithmicWindowTransfers.RecoverTerminalTransfer(
                            waitingWindow,
                            waiting.CorrelationId,
                            allowRollbackToSource: false,
                            preferredFloatingDesktop: actualDesktop);
                        if (m_masterSatelliteExistingWindowTransfers.TryGetValue(
                                waiting.CorrelationId,
                                out var existingContext))
                        {
                            CompleteMasterSatelliteExistingWindowFailure(
                                existingContext,
                                recovery,
                                waiting.WindowHandle);
                            ProcessMasterSatelliteCapacityTransitions();
                        }
                        else
                        {
                            ApplyMasterSatelliteTransferRecovery(
                                waitingWindow,
                                recovery,
                                waiting.WindowHandle);
                        }
                        continue;
                    }

                    var decision = HandleMasterSatelliteDestinationAdded(
                        waitingWindow,
                        waiting.WindowHandle,
                        waiting.TargetDesktop);
                    if (!decision.Consumed || decision.Deferred)
                    {
                        return;
                    }
                }
            }
            finally
            {
                m_drainingAlgorithmicDestinationArrivals = false;
            }
        }

        private void ApplyMasterSatelliteTransferRecovery(
            IWindow window,
            AlgorithmicTransferRecoveryResult recovery,
            IntPtr stableWindowHandle = default)
        {
            if (recovery.Exception != null)
            {
                m_logger.Error(
                    recovery.Exception,
                    "Master + Satellites transfer recovery failed; correlation={CorrelationId}, window={Window}, disposition={Disposition}",
                    recovery.Transfer?.CorrelationId,
                    window.DebugString(),
                    recovery.Disposition);
            }
            m_logger.Debug(
                "Master + Satellites transfer recovery completed; correlation={CorrelationId}, disposition={Disposition}, rollbackAttempted={RollbackAttempted}, rollbackSucceeded={RollbackSucceeded}, requiresFloating={RequiresFloating}, floatingDesktop={FloatingDesktop}, duplicate={Duplicate}",
                recovery.Transfer?.CorrelationId,
                recovery.Disposition,
                recovery.RollbackAttempted,
                recovery.RollbackSucceeded,
                recovery.RequiresFloating,
                recovery.FloatingDesktop,
                recovery.IsDuplicate);
            if (recovery.RequiresFloating && recovery.ShouldNotifyFailure)
            {
                NotifyMasterSatellitePlacementFailedOnce(
                    window,
                    stableWindowHandle,
                    recovery.Transfer?.CorrelationId ?? Guid.Empty);
            }
        }

        private void OnAlgorithmicTransferTerminated(
            PendingWindowTransfer transfer)
        {
            if (m_disposed
                || transfer.State == PendingWindowTransferState.Committed
                || (!MasterSatelliteDisplayEligibility.DisplaysMatch(
                        transfer.SourceDisplay,
                        m_display)
                    && !MasterSatelliteDisplayEligibility.DisplaysMatch(
                        transfer.TargetDisplay,
                        m_display)))
            {
                return;
            }

            if (transfer.TerminalReason == "WindowClosed")
            {
                // HWND reuse can install a replacement wrapper before this
                // Dispatcher-delayed notification is delivered. Complete the old
                // correlation by identity and never resolve or notify through the
                // replacement generation merely because its numeric handle matches.
                if (CompleteClosedMasterSatelliteCapacityTransfer(
                    transfer,
                    m_masterSatelliteExistingWindowTransfers))
                {
                    ProcessMasterSatelliteCapacityTransitions();
                }
                return;
            }

            if (!m_algorithmicWindowEvents.TryGetWindow(
                    transfer.WindowHandle,
                    out var window))
            {
                // WindowRemoved forgets a dead HWND after asking the coordinator
                // to terminate its transfer. The terminal notification is
                // intentionally asynchronous, so a capacity transition must be
                // completed by correlation even though no live IWindow remains.
                // Restoring or floating the captured endpoint would reintroduce a
                // closed window; OnWindowRemoved has already removed it from the
                // backend and released its original-position metadata.
                if (CompleteClosedMasterSatelliteCapacityTransfer(
                    transfer,
                    m_masterSatelliteExistingWindowTransfers))
                {
                    ProcessMasterSatelliteCapacityTransitions();
                }
                return;
            }

            RaiseMasterSatelliteTransferEvent(
                transfer.State == PendingWindowTransferState.Failed
                    ? AlgorithmicLayoutEventKind.TransferFailed
                    : AlgorithmicLayoutEventKind.TransferCancelled,
                window,
                transfer.WindowHandle,
                transfer,
                transfer.TerminalReason ?? transfer.State.ToString(),
                transfer.State == PendingWindowTransferState.Failed
                    ? "AlgorithmicLayout.TransferFailed"
                    : "AlgorithmicLayout.TransferCancelled");

            var recovery = RecoverMasterSatelliteTerminalTransfer(
                window,
                transfer);
            if (m_masterSatelliteExistingWindowTransfers.TryGetValue(
                    transfer.CorrelationId,
                    out var context))
            {
                CompleteMasterSatelliteExistingWindowFailure(
                    context,
                    recovery,
                    transfer.WindowHandle);
                ProcessMasterSatelliteCapacityTransitions();
            }
            else
            {
                ApplyMasterSatelliteTransferRecovery(
                    window,
                    recovery,
                    transfer.WindowHandle);
            }
        }

        private void NotifyMasterSatellitePlacementFailedOnce(
            IWindow window,
            IntPtr stableWindowHandle = default,
            Guid expectedCorrelationId = default,
            LayoutStateKey? sourceLayoutKey = null)
        {
            if (stableWindowHandle == IntPtr.Zero
                && !TryRememberMasterSatelliteWindowHandle(
                    window,
                    out stableWindowHandle))
            {
                // A dead window has no useful floating fallback or user message.
                return;
            }
            bool newlyFloated = MarkWindowFloating(window);
            if (newlyFloated)
            {
                OnWindowFloated(window);
            }

            // Notification deduplication must never suppress the state-changing
            // floating fallback itself.
            if (!m_arrangeFailureNotifications.TryMark(stableWindowHandle))
            {
                return;
            }

            PendingWindowTransfer? transfer = null;
            if (expectedCorrelationId != Guid.Empty
                && m_algorithmicLayoutCoordinator.TryGetRecentTransfer(
                    stableWindowHandle,
                    out var correlated)
                && correlated.CorrelationId == expectedCorrelationId
                && correlated.State != PendingWindowTransferState.Committed)
            {
                transfer = correlated;
            }
            RaiseMasterSatelliteTransferEvent(
                AlgorithmicLayoutEventKind.WindowLeftFloating,
                window,
                stableWindowHandle,
                transfer,
                transfer?.TerminalReason ?? "NoOverflowDestination",
                "AlgorithmicLayout.WindowLeftFloating",
                sourceLayoutKey);
        }

        private void RaiseMasterSatelliteTransferEvent(
            AlgorithmicLayoutEventKind kind,
            IWindow window,
            IntPtr windowHandle,
            PendingWindowTransfer? transfer,
            string reason,
            string messageKey,
            LayoutStateKey? sourceLayoutKey = null)
        {
            CoordinatorCapacitySnapshot? sourceCapacity = null;
            var capacityKey = transfer != null
                ? new LayoutStateKey(
                    transfer.SourceDesktop,
                    transfer.SourceDisplay)
                : sourceLayoutKey;
            if (capacityKey is LayoutStateKey key)
            {
                try
                {
                    m_algorithmicLayoutCoordinator.TryGetCapacity(
                        key,
                        out sourceCapacity!);
                }
                catch (InvalidOperationException ex)
                {
                    m_logger.Debug(
                        ex,
                        "Could not capture source capacity for algorithmic event {EventKind}",
                        kind);
                }
            }

            IVirtualDesktop? observedDesktop = null;
            if (!m_algorithmicWindowEvents.TryGetKnownDesktop(
                    windowHandle,
                    out observedDesktop!)
                && kind == AlgorithmicLayoutEventKind.WindowLeftFloating
                && TryFindMasterSatelliteWindowDesktop(window, out var actualDesktop))
            {
                observedDesktop = actualDesktop;
                RememberMasterSatelliteWindowDesktop(
                    windowHandle,
                    actualDesktop);
            }
            RaiseMasterSatelliteEvent(new AlgorithmicLayoutEvent(
                kind,
                transfer?.TargetDisplay ?? sourceLayoutKey?.Display ?? m_display,
                reason,
                messageKey,
                windowHandle,
                CaptureMasterSatelliteWindowTitle(window),
                transfer?.SourceDesktop ?? sourceLayoutKey?.VirtualDesktop,
                kind == AlgorithmicLayoutEventKind.WindowLeftFloating
                    ? observedDesktop
                        ?? transfer?.SourceDesktop
                        ?? sourceLayoutKey?.VirtualDesktop
                    : transfer?.TargetDesktop,
                transfer?.CorrelationId ?? Guid.Empty,
                sourceCapacity?.OccupiedSlots,
                sourceCapacity?.TotalCapacity));
        }

        private void RaiseMasterSatelliteEvent(AlgorithmicLayoutEvent layoutEvent)
        {
            foreach (var subscriber in AlgorithmicLayoutChanged?
                .GetInvocationList() ?? [])
            {
                try
                {
                    ((EventHandler<AlgorithmicLayoutEvent>)subscriber)(
                        this,
                        layoutEvent);
                }
                catch (Exception ex)
                {
                    m_logger.Error(
                        ex,
                        "Algorithmic layout event subscriber failed; kind={EventKind}, correlation={CorrelationId}",
                        layoutEvent.Kind,
                        layoutEvent.CorrelationId);
                }
            }
        }

        private static string? CaptureMasterSatelliteWindowTitle(IWindow window)
        {
            try
            {
                return window.Title;
            }
            catch (InvalidWindowReferenceException)
            {
                return null;
            }
        }

        private void LogMasterSatelliteRemoval(
            IWindow window,
            MasterSatelliteLocalMutationResult result)
        {
            if (!result.Attempted)
            {
                return;
            }
            if (result.Succeeded)
            {
                m_logger.Debug(
                    "Removed window {Window} from Master + Satellites layout {LayoutKey} at revision {Revision}",
                    window.DebugString(),
                    result.LayoutKey,
                    result.Operation?.After.Revision);
                return;
            }
            m_logger.Debug(
                "Master + Satellites removal rejected for window {Window}: {Reason} ({Message})",
                window.DebugString(),
                result.Operation?.FailureReason,
                result.Operation?.Message);
        }

        private void LogMasterSatelliteLifecycleResults(
            IReadOnlyList<MasterSatelliteLifecycleResult>? results)
        {
            if (results == null)
            {
                return;
            }
            foreach (var result in results)
            {
                LogMasterSatelliteLifecycleResult(result);
            }
        }

        private void LogMasterSatelliteLifecycleResult(MasterSatelliteLifecycleResult? result)
        {
            if (result == null)
            {
                return;
            }

            if (result.Operation is { Succeeded: false } failed)
            {
                m_logger.Debug(
                    "Master + Satellites lifecycle {Action} rejected for desktop {Desktop} on display {Display}: {Reason} ({Message})",
                    result.Action,
                    result.Key.VirtualDesktop,
                    result.Key.Display,
                    failed.FailureReason,
                    failed.Message);
                if (result.Action == "SettingsOrientationRejected")
                {
                    RaiseMasterSatelliteEvent(new AlgorithmicLayoutEvent(
                        AlgorithmicLayoutEventKind.OperationRejected,
                        result.Key.Display,
                        failed.FailureReason.ToString(),
                        "AlgorithmicLayout.OrientationRejected",
                        sourceDesktop: result.Key.VirtualDesktop));
                }
                return;
            }

            m_logger.Debug(
                "Master + Satellites lifecycle {Action} for desktop {Desktop} on display {Display}; eligible={Eligible}, present={Present}, added={Added}, removed={Removed}, revision={Revision}",
                result.Action,
                result.Key.VirtualDesktop,
                result.Key.Display,
                result.Eligible,
                result.StatePresent,
                result.StateAdded,
                result.StateRemoved,
                result.Operation?.After.Revision);
            if (result.Action == "Rebuilt"
                && result.Operation is { Succeeded: true, Changed: true })
            {
                RaiseMasterSatelliteEvent(new AlgorithmicLayoutEvent(
                    AlgorithmicLayoutEventKind.LayoutRecovered,
                    result.Key.Display,
                    result.Action,
                    "AlgorithmicLayout.LayoutRecovered",
                    sourceDesktop: result.Key.VirtualDesktop));
            }
        }

        /// <summary>
        /// Publishes a physical backend snapshot while the caller holds
        /// m_backendLock. The coordinator only copies immutable data and never
        /// invokes a service callback while holding its mutation lock.
        /// </summary>
        private void PublishMasterSatelliteCapacityLocked(IVirtualDesktop desktop)
        {
            if (m_masterSatelliteLifecycleDisposed
                || m_backend.GetTree(desktop) == null)
            {
                return;
            }

            m_masterSatelliteLifecycle.TryGetState(desktop, out var state);
            var settings = m_masterSatelliteLifecycle.SettingsSnapshot;
            if (!m_masterSatelliteLifecycle.IsEligible(
                m_masterSatellitePrimaryDisplay))
            {
                settings = settings with { Enabled = false };
                state = null!;
            }
            var capacity = m_backend.QueryMasterSatelliteCapacity(
                desktop,
                state,
                settings);
            m_masterSatelliteCapacityPublisher(
                new LayoutStateKey(desktop, m_display),
                capacity,
                System.Threading.Interlocked.Increment(
                    ref m_masterSatelliteCapacityPublicationSequence));
        }

        private void PublishAllMasterSatelliteCapacitiesLocked()
        {
            foreach (var desktop in m_backend.SnapshotDesktops())
            {
                PublishMasterSatelliteCapacityLocked(desktop);
            }
        }

        internal static bool CompleteClosedMasterSatelliteCapacityTransfer(
            PendingWindowTransfer transfer,
            IDictionary<Guid, MasterSatelliteExistingWindowTransferContext>
                contexts)
        {
            ArgumentNullException.ThrowIfNull(transfer);
            ArgumentNullException.ThrowIfNull(contexts);
            if (!transfer.IsTerminal
                || transfer.State == PendingWindowTransferState.Committed
                || !contexts.Remove(transfer.CorrelationId, out var context))
            {
                return false;
            }

            context.RestorePoint = null;
            context.Work.Completed = true;
            return true;
        }

        internal sealed record MasterSatelliteCapacitySourcePlan(
            LayoutStateKey SourceKey,
            MasterSatelliteRuntimeState? SourceState,
            MasterSatelliteLayoutSettings SourceValidationSettings,
            MasterSatelliteCapacityTransitionPlan Transition,
            bool IsActivation)
        {
            public bool Aborted { get; set; }
        }

        internal sealed class MasterSatelliteCapacityTransitionWorkItem
        {
            public MasterSatelliteCapacitySourcePlan Plan { get; }

            public IWindow Window { get; }

            public Guid CorrelationId { get; set; }

            public IntPtr StableWindowHandle { get; set; }

            public Rectangle SourceOriginalPosition { get; set; }

            public bool Completed { get; set; }

            public MasterSatelliteCapacityTransitionWorkItem(
                MasterSatelliteCapacitySourcePlan plan,
                IWindow window)
            {
                Plan = plan;
                Window = window;
            }
        }

        internal sealed class MasterSatelliteExistingWindowTransferContext
        {
            public MasterSatelliteCapacityTransitionWorkItem Work { get; }

            public PendingWindowTransfer Transfer { get; }

            public MasterSatelliteWorkspaceTransferRestorePoint? RestorePoint { get; set; }

            public MasterSatelliteExistingWindowTransferContext(
                MasterSatelliteCapacityTransitionWorkItem work,
                PendingWindowTransfer transfer)
            {
                Work = work;
                Transfer = transfer;
            }
        }

        private sealed class MasterSatelliteIncomingOwnershipProbe
        {
            public IWindow Window { get; }

            public IntPtr WindowHandle { get; }

            public MasterSatelliteIncomingOwnershipProbe(
                IWindow window,
                IntPtr windowHandle)
            {
                Window = window;
                WindowHandle = windowHandle;
            }
        }

        private readonly record struct MasterSatelliteDestinationAddDecision(
            bool Consumed,
            bool SuppressOverflow,
            bool Deferred = false);
    }
}
