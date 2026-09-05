using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using FancyWM.AlgorithmicLayouts;
using FancyWM.Layouts.Tiling;
using FancyWM.Models;

using WinMan;

namespace FancyWM
{
    internal enum WorkspaceLayoutKind
    {
        Empty,
        Canonical,
        Manual,
        CorruptedCanonical,
    }

    internal sealed record class MasterSatelliteCapacitySnapshot(
        WorkspaceLayoutKind LayoutKind,
        bool CanAcceptWindow,
        MasterSatelliteWindowRole? NextRole,
        int? NextSatelliteIndex,
        int OccupiedSlots,
        int TotalCapacity,
        long Revision,
        string? DiagnosticReason);

    internal readonly record struct MasterSatelliteReservedSlot(
        Guid ReservationId,
        LayoutStateKey LayoutKey,
        MasterSatelliteWindowRole Role,
        int? SatelliteIndex);

    internal readonly record struct MasterSatellitePlacementPreflight(
        IWindow Window,
        MasterSatelliteWindowRole Role,
        int? SatelliteIndex);

    internal sealed record MasterSatelliteWorkspaceDesktopRestorePoint(
        IVirtualDesktop Desktop,
        PanelNode? Root,
        IWindow? FocusedWindow,
        MasterSatelliteRuntimeState? RuntimeState,
        MasterSatelliteRuntimeState? RuntimeSnapshot,
        MasterSatelliteLayoutSettings ValidationSettings);

    /// <summary>
    /// Detached restore point for the two backend endpoints touched while moving
    /// an already tiled window. It is captured and consumed under m_backendLock.
    /// </summary>
    internal sealed record MasterSatelliteWorkspaceTransferRestorePoint(
        IWindow Window,
        MasterSatelliteWorkspaceDesktopRestorePoint Source,
        MasterSatelliteWorkspaceDesktopRestorePoint Target,
        bool HadOriginalPosition,
        Rectangle OriginalPosition);

    internal sealed record class WorkspaceLayoutRevisionDiagnostic(
        IVirtualDesktop Desktop,
        string Operation,
        long BeforeRevision,
        long AfterRevision);

    internal partial class TilingWorkspace
    {
        private readonly MasterSatelliteLayoutEngine m_masterSatelliteEngine;
        private readonly Action<WorkspaceLayoutRevisionDiagnostic>? m_revisionDiagnostic;

        public TilingWorkspace()
            : this(new MasterSatelliteLayoutEngine())
        {
        }

        internal TilingWorkspace(
            MasterSatelliteLayoutEngine masterSatelliteEngine,
            Action<WorkspaceLayoutRevisionDiagnostic>? revisionDiagnostic = null)
        {
            m_masterSatelliteEngine = masterSatelliteEngine
                ?? throw new ArgumentNullException(nameof(masterSatelliteEngine));
            m_revisionDiagnostic = revisionDiagnostic;
        }

        /// <summary>
        /// Returns a detached clone of a desktop tree. Mutating the returned tree does
        /// not affect the workspace.
        /// </summary>
        public bool TryGetTreeSnapshot(IVirtualDesktop desktop, out DesktopTree snapshot)
        {
            ArgumentNullException.ThrowIfNull(desktop);
            var state = m_states.GetState(desktop);
            if (state == null)
            {
                snapshot = null!;
                return false;
            }

            try
            {
                snapshot = CloneTree(state.DesktopTree);
                return true;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                Debug.WriteLine($"Unable to clone the desktop tree: {e}");
                Trace.TraceError($"Master + Satellites tree snapshot fallback: {e}");
                snapshot = null!;
                return false;
            }
        }

        /// <summary>
        /// Returns an immutable snapshot built from detached tree and runtime-state
        /// clones. The caller continues to own the supplied runtime state.
        /// </summary>
        public bool TryGetMasterSatelliteSnapshot(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState runtimeState,
            out MasterSatelliteLayoutSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(desktop);
            ArgumentNullException.ThrowIfNull(runtimeState);

            var desktopState = m_states.GetState(desktop);
            if (desktopState == null)
            {
                snapshot = null!;
                return false;
            }

            try
            {
                snapshot = m_masterSatelliteEngine.CreateSnapshot(
                    CloneTree(desktopState.DesktopTree),
                    runtimeState.Clone());
                return true;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                Trace.TraceError($"Master + Satellites immutable snapshot fallback: {e}");
                snapshot = null!;
                return false;
            }
        }

        /// <summary>
        /// Validates detached copies of the desktop tree and caller-owned runtime
        /// state, without exposing or modifying the live tree.
        /// </summary>
        public MasterSatelliteInvariantResult ValidateMasterSatelliteLayout(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings)
        {
            ArgumentNullException.ThrowIfNull(desktop);
            ArgumentNullException.ThrowIfNull(runtimeState);
            ArgumentNullException.ThrowIfNull(settings);

            var desktopState = m_states.GetState(desktop);
            if (desktopState == null)
            {
                const string message = "The virtual desktop is not registered with this workspace.";
                return new MasterSatelliteInvariantResult([message], string.Empty);
            }

            try
            {
                return m_masterSatelliteEngine.ValidateInvariant(
                    CloneTree(desktopState.DesktopTree),
                    runtimeState.Clone(),
                    settings);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                Trace.TraceError($"Master + Satellites validation fallback: {e}");
                return new MasterSatelliteInvariantResult(
                    [$"The canonical layout could not be validated ({e.GetType().Name})."],
                    string.Empty);
            }
        }

        public bool TryGetOriginalPosition(IWindow window, out Rectangle originalPosition)
        {
            ArgumentNullException.ThrowIfNull(window);
            return m_originalPositions.TryGetValue(window, out originalPosition);
        }

        /// <summary>
        /// Removes a manual-layout node while retaining the geometry used by the
        /// service's floating fallback. Ordinary UnregisterWindow intentionally
        /// keeps its legacy behavior and clears this metadata.
        /// </summary>
        public void UnregisterWindowPreservingOriginalPosition(IWindow window)
        {
            ArgumentNullException.ThrowIfNull(window);
            bool hadOriginalPosition = m_originalPositions.TryGetValue(
                window,
                out var originalPosition);
            UnregisterWindow(window);
            if (hadOriginalPosition)
            {
                m_originalPositions[window] = originalPosition;
            }
        }

        /// <summary>
        /// Drops retained algorithmic metadata after the window has definitively
        /// left this workspace. Temporary removals and coordinator transfers must
        /// not call this method until their destination has consumed the value.
        /// </summary>
        public bool ForgetMasterSatelliteOriginalPosition(IWindow window)
        {
            ArgumentNullException.ThrowIfNull(window);
            return m_originalPositions.Remove(window);
        }

        public bool TryGetLayoutKind(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState? runtimeState,
            MasterSatelliteLayoutSettings settings,
            out WorkspaceLayoutKind layoutKind)
        {
            ArgumentNullException.ThrowIfNull(desktop);
            ArgumentNullException.ThrowIfNull(settings);

            var desktopState = m_states.GetState(desktop);
            if (desktopState == null)
            {
                layoutKind = WorkspaceLayoutKind.CorruptedCanonical;
                return false;
            }

            layoutKind = ClassifyLayout(desktopState.DesktopTree, runtimeState, settings);
            return true;
        }

        public MasterSatelliteCapacitySnapshot QueryMasterSatelliteCapacity(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState? runtimeState,
            MasterSatelliteLayoutSettings settings)
        {
            ArgumentNullException.ThrowIfNull(desktop);
            ArgumentNullException.ThrowIfNull(settings);

            var desktopState = m_states.GetState(desktop);
            if (desktopState == null)
            {
                return new MasterSatelliteCapacitySnapshot(
                    WorkspaceLayoutKind.CorruptedCanonical,
                    false,
                    null,
                    null,
                    0,
                    settings.MaxSatellites + 1,
                    runtimeState?.Revision ?? 0,
                    "The virtual desktop is not registered with this workspace.");
            }

            var tree = desktopState.DesktopTree;
            var kind = ClassifyLayout(tree, runtimeState, settings);
            int occupiedSlots = TryCountWindows(tree);
            int totalCapacity = settings.MaxSatellites + 1;
            if (!settings.Enabled)
            {
                return new MasterSatelliteCapacitySnapshot(
                    kind,
                    false,
                    null,
                    null,
                    occupiedSlots,
                    totalCapacity,
                    runtimeState?.Revision ?? 0,
                    "Master + Satellites is disabled in settings.");
            }
            if (kind == WorkspaceLayoutKind.Empty)
            {
                return new MasterSatelliteCapacitySnapshot(
                    kind,
                    true,
                    MasterSatelliteWindowRole.Master,
                    null,
                    0,
                    totalCapacity,
                    0,
                    null);
            }
            if (kind != WorkspaceLayoutKind.Canonical || runtimeState == null)
            {
                return new MasterSatelliteCapacitySnapshot(
                    kind,
                    false,
                    null,
                    null,
                    occupiedSlots,
                    totalCapacity,
                    runtimeState?.Revision ?? 0,
                    kind == WorkspaceLayoutKind.Manual
                        ? "A manual layout cannot accept an algorithmic slot."
                        : "The canonical layout is not safe to mutate.");
            }
            if (runtimeState.Master == null)
            {
                return new MasterSatelliteCapacitySnapshot(
                    kind,
                    true,
                    MasterSatelliteWindowRole.Master,
                    null,
                    0,
                    totalCapacity,
                    runtimeState.Revision,
                    null);
            }
            if (runtimeState.Satellites.Count < settings.MaxSatellites)
            {
                return new MasterSatelliteCapacitySnapshot(
                    kind,
                    true,
                    MasterSatelliteWindowRole.Satellite,
                    runtimeState.Satellites.Count,
                    occupiedSlots,
                    totalCapacity,
                    runtimeState.Revision,
                    null);
            }

            return new MasterSatelliteCapacitySnapshot(
                kind,
                false,
                null,
                null,
                occupiedSlots,
                totalCapacity,
                runtimeState.Revision,
                "The canonical layout has no physical satellite slot available.");
        }

        /// <summary>
        /// Rebuilds a registered desktop from an already admitted, complete window
        /// order. Planning overflow is deliberately outside this method.
        /// </summary>
        public MasterSatelliteOperationResult ActivateMasterSatelliteLayout(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings,
            IReadOnlyList<IWindow> orderedWindows)
        {
            ArgumentNullException.ThrowIfNull(desktop);
            ArgumentNullException.ThrowIfNull(runtimeState);
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(orderedWindows);

            if (!TryGetDesktopState(desktop, runtimeState, settings, out var desktopState, out var failure))
            {
                return failure!;
            }

            var tree = desktopState!.DesktopTree;
            if (!settings.Enabled)
            {
                return CreateFailure(tree, runtimeState, settings,
                    MasterSatelliteFailureReason.Inactive,
                    "Master + Satellites is disabled in settings.");
            }
            if (!runtimeState.IsActive)
            {
                return CreateFailure(tree, runtimeState, settings,
                    MasterSatelliteFailureReason.Inactive,
                    "The supplied runtime state is not active.");
            }

            var currentWindows = GetWindows(tree);
            if (orderedWindows.Any(window => window == null)
                || orderedWindows.Distinct().Count() != orderedWindows.Count
                || currentWindows.Count != orderedWindows.Count
                || !currentWindows.ToHashSet().SetEquals(orderedWindows))
            {
                return CreateFailure(tree, runtimeState, settings,
                    MasterSatelliteFailureReason.InvalidArgument,
                    "Activation must contain every current tiled window exactly once; overflow must be admitted before rebuilding.");
            }

            if (!TryCaptureMissingOriginalPositions(orderedWindows, out var originalPositions, out string? error))
            {
                return CreateFailure(tree, runtimeState, settings,
                    MasterSatelliteFailureReason.UnexpectedFailure,
                    error!);
            }

            long beforeRevision = runtimeState.Revision;
            var focusedWindow = CaptureFocusedWindow(desktopState);
            MasterSatelliteOperationResult result;
            try
            {
                result = m_masterSatelliteEngine.BuildLayout(tree, runtimeState, settings, orderedWindows);
            }
            finally
            {
                RemapFocus(desktopState, focusedWindow);
            }
            result = VerifyValidAfterChangedOperation(tree, runtimeState, settings, result);
            if (result.Succeeded)
            {
                foreach (var (window, position) in originalPositions)
                {
                    m_originalPositions.TryAdd(window, position);
                }
            }
            ReportRevision(desktop, "Activate", beforeRevision, runtimeState.Revision);
            return result;
        }

        /// <summary>
        /// Proves that the admitted activation prefix can form a canonical tree
        /// without changing the live manual tree, focus, original-position map, or
        /// runtime state. Windows outside the admitted prefix are intentionally
        /// absent from the detached candidate because they will be overflowed.
        /// </summary>
        public MasterSatelliteOperationResult PreflightMasterSatelliteActivation(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState candidateState,
            MasterSatelliteLayoutSettings settings,
            IReadOnlyList<IWindow> admittedWindows)
        {
            ArgumentNullException.ThrowIfNull(desktop);
            ArgumentNullException.ThrowIfNull(candidateState);
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(admittedWindows);
            if (!TryGetTreeSnapshot(desktop, out var candidateTree))
            {
                return CreateFailure(
                    null,
                    candidateState,
                    settings,
                    MasterSatelliteFailureReason.InvalidArgument,
                    "The virtual desktop is not registered with the tiling workspace.");
            }

            return m_masterSatelliteEngine.BuildLayout(
                candidateTree,
                candidateState.Clone(),
                settings,
                admittedWindows);
        }

        /// <summary>
        /// Captures both trees and both runtime states immediately before an
        /// existing-window source detach. Restoring this token reverses source and
        /// destination mutations together, including focus and original geometry.
        /// </summary>
        public MasterSatelliteWorkspaceTransferRestorePoint
            CaptureMasterSatelliteTransferRestorePoint(
                IVirtualDesktop sourceDesktop,
                MasterSatelliteRuntimeState? sourceRuntimeState,
                MasterSatelliteLayoutSettings sourceValidationSettings,
                IVirtualDesktop targetDesktop,
                MasterSatelliteRuntimeState targetRuntimeState,
                MasterSatelliteLayoutSettings targetValidationSettings,
                IWindow window)
        {
            ArgumentNullException.ThrowIfNull(sourceDesktop);
            ArgumentNullException.ThrowIfNull(sourceValidationSettings);
            ArgumentNullException.ThrowIfNull(targetDesktop);
            ArgumentNullException.ThrowIfNull(targetRuntimeState);
            ArgumentNullException.ThrowIfNull(targetValidationSettings);
            ArgumentNullException.ThrowIfNull(window);
            if (MasterSatelliteDisplayEligibility.DesktopsMatch(
                    sourceDesktop,
                    targetDesktop))
            {
                throw new ArgumentException(
                    "A transfer restore point requires distinct desktops.",
                    nameof(targetDesktop));
            }

            var source = CaptureDesktopRestorePoint(
                sourceDesktop,
                sourceRuntimeState,
                sourceValidationSettings);
            var target = CaptureDesktopRestorePoint(
                targetDesktop,
                targetRuntimeState,
                targetValidationSettings);
            bool hadOriginalPosition = m_originalPositions.TryGetValue(
                window,
                out var originalPosition);
            return new MasterSatelliteWorkspaceTransferRestorePoint(
                window,
                source,
                target,
                hadOriginalPosition,
                originalPosition);
        }

        public MasterSatelliteOperationResult DetachMasterSatelliteTransferSource(
            MasterSatelliteWorkspaceTransferRestorePoint restorePoint)
        {
            ArgumentNullException.ThrowIfNull(restorePoint);
            var sourceState = m_states.GetState(restorePoint.Source.Desktop)
                ?? throw new InvalidOperationException(
                    "The source desktop disappeared before transfer materialization.");
            if (sourceState.DesktopTree.FindNode(restorePoint.Window) == null)
            {
                throw new InvalidOperationException(
                    "The source window disappeared before transfer materialization.");
            }

            if (restorePoint.Source.RuntimeState is MasterSatelliteRuntimeState runtimeState)
            {
                return UnregisterMasterSatelliteWindow(
                    restorePoint.Source.Desktop,
                    runtimeState,
                    restorePoint.Source.ValidationSettings,
                    restorePoint.Window,
                    preserveOriginalPosition: true);
            }

            UnregisterWindowPreservingOriginalPosition(restorePoint.Window);
            var snapshot = new MasterSatelliteLayoutSnapshot(
                new MasterSatelliteRuntimeState(
                    restorePoint.Source.ValidationSettings,
                    isActive: false),
                sourceState.DesktopTree.WorkArea,
                sourceState.DesktopTree.Root?.ToString() ?? "<empty>");
            return MasterSatelliteOperationResult.Success(
                true,
                snapshot,
                snapshot,
                MasterSatelliteInvariantResult.Valid(snapshot.TreeDescription));
        }

        public void RestoreMasterSatelliteTransfer(
            MasterSatelliteWorkspaceTransferRestorePoint restorePoint)
        {
            ArgumentNullException.ThrowIfNull(restorePoint);
            RestoreDesktop(restorePoint.Target);
            RestoreDesktop(restorePoint.Source);
            if (restorePoint.HadOriginalPosition)
            {
                m_originalPositions[restorePoint.Window] = restorePoint.OriginalPosition;
            }
            else
            {
                m_originalPositions.Remove(restorePoint.Window);
            }

            ValidateRestoredDesktop(restorePoint.Target);
            ValidateRestoredDesktop(restorePoint.Source);
        }

        public MasterSatellitePlacementResult PreflightMasterSatellitePlacement(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings,
            IWindow window,
            MasterSatelliteWindowRole expectedRole,
            int? satelliteIndex = null)
        {
            return PreflightMasterSatellitePlacements(
                desktop,
                runtimeState,
                settings,
                [new MasterSatellitePlacementPreflight(
                    window,
                    expectedRole,
                    satelliteIndex)]);
        }

        /// <summary>
        /// Preflights an ordered reservation prefix plus a candidate against one
        /// detached tree/state pair. This proves min-size feasibility for a future
        /// satellite even while preceding exact slots are reserved but not yet
        /// physically materialized.
        /// </summary>
        public MasterSatellitePlacementResult PreflightMasterSatellitePlacements(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings,
            IReadOnlyList<MasterSatellitePlacementPreflight> placements)
        {
            ArgumentNullException.ThrowIfNull(placements);
            if (!TryGetDesktopState(desktop, runtimeState, settings, out var desktopState, out var failure))
            {
                return new MasterSatellitePlacementResult(failure!, null, null);
            }
            if (placements.Count == 0)
            {
                var operation = CreateFailure(
                    desktopState!.DesktopTree,
                    runtimeState,
                    settings,
                    MasterSatelliteFailureReason.InvalidArgument,
                    "At least one placement is required for preflight.");
                return new MasterSatellitePlacementResult(operation, null, null);
            }

            try
            {
                var candidateTree = CloneTree(desktopState!.DesktopTree);
                var candidateState = runtimeState.Clone();
                MasterSatellitePlacementResult? result = null;
                foreach (var placement in placements)
                {
                    ArgumentNullException.ThrowIfNull(placement.Window);
                    if (!TryValidatePlacementRequest(
                        candidateTree,
                        candidateState,
                        settings,
                        placement.Role,
                        placement.SatelliteIndex,
                        out failure))
                    {
                        return new MasterSatellitePlacementResult(
                            failure!,
                            null,
                            null);
                    }
                    result = m_masterSatelliteEngine.PlaceWindow(
                        candidateTree,
                        candidateState,
                        settings,
                        placement.Window,
                        placement.SatelliteIndex);
                    if (!result.Succeeded)
                    {
                        return result;
                    }
                }
                return result!;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                Trace.TraceError($"Master + Satellites placement preflight fallback: {e}");
                var operation = CreateFailure(
                    desktopState!.DesktopTree,
                    runtimeState,
                    settings,
                    MasterSatelliteFailureReason.UnexpectedFailure,
                    $"Placement preflight could not clone the current tree: {e.Message}");
                return new MasterSatellitePlacementResult(operation, null, null);
            }
        }

        public MasterSatellitePlacementResult RegisterMaster(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings,
            IWindow window,
            Rectangle? originalPosition = null)
        {
            return RegisterAlgorithmicWindow(
                desktop,
                runtimeState,
                settings,
                window,
                MasterSatelliteWindowRole.Master,
                null,
                originalPosition,
                "RegisterMaster");
        }

        public MasterSatellitePlacementResult RegisterSatellite(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings,
            IWindow window,
            int satelliteIndex,
            Rectangle? originalPosition = null)
        {
            return RegisterAlgorithmicWindow(
                desktop,
                runtimeState,
                settings,
                window,
                MasterSatelliteWindowRole.Satellite,
                satelliteIndex,
                originalPosition,
                "RegisterSatellite");
        }

        /// <summary>
        /// Materializes a coordinator-owned reservation. Reservations are bound to
        /// an opaque identity and layout key, not a tree revision, so multiple
        /// reservations may remain outstanding while earlier ones commit. The
        /// coordinator owns exactly-once consumption and must queue a future
        /// satellite index until all preceding indices have materialized.
        /// </summary>
        public MasterSatellitePlacementResult RegisterReservedWindow(
            LayoutStateKey layoutKey,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings,
            IWindow window,
            MasterSatelliteReservedSlot reservedSlot,
            Rectangle? transferredOriginalPosition = null)
        {
            ArgumentNullException.ThrowIfNull(runtimeState);
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(window);
            if (!settings.Enabled)
            {
                var failure = CreateFailure(
                    null,
                    runtimeState,
                    settings,
                    MasterSatelliteFailureReason.Inactive,
                    "Master + Satellites is disabled in settings.");
                return new MasterSatellitePlacementResult(failure, null, null);
            }
            if (reservedSlot.ReservationId == Guid.Empty)
            {
                var failure = CreateFailure(
                    null,
                    runtimeState,
                    settings,
                    MasterSatelliteFailureReason.InvalidArgument,
                    "The reservation identity must not be empty.");
                return new MasterSatellitePlacementResult(failure, null, null);
            }
            if (layoutKey.VirtualDesktop == null
                || layoutKey.Display == null
                || reservedSlot.LayoutKey.VirtualDesktop == null
                || reservedSlot.LayoutKey.Display == null
                || !LayoutKeysMatch(reservedSlot.LayoutKey, layoutKey))
            {
                var failure = CreateFailure(
                    null,
                    runtimeState,
                    settings,
                    MasterSatelliteFailureReason.InvalidArgument,
                    "The reservation is not bound to the supplied desktop/display layout key.");
                return new MasterSatellitePlacementResult(failure, null, null);
            }
            if (!Enum.IsDefined(reservedSlot.Role)
                || (reservedSlot.Role == MasterSatelliteWindowRole.Master && reservedSlot.SatelliteIndex.HasValue)
                || (reservedSlot.Role == MasterSatelliteWindowRole.Satellite && !reservedSlot.SatelliteIndex.HasValue))
            {
                var failure = CreateFailureForDesktop(
                    layoutKey.VirtualDesktop,
                    runtimeState,
                    settings,
                    MasterSatelliteFailureReason.InvalidArgument,
                    "The reserved slot role and satellite index are inconsistent.");
                return new MasterSatellitePlacementResult(failure, null, null);
            }

            return RegisterAlgorithmicWindow(
                layoutKey.VirtualDesktop,
                runtimeState,
                settings,
                window,
                reservedSlot.Role,
                reservedSlot.SatelliteIndex,
                transferredOriginalPosition,
                "RegisterReservedWindow",
                preferSuppliedOriginalPosition: true);
        }

        public MasterSatelliteOperationResult UnregisterMasterSatelliteWindow(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings,
            IWindow window,
            bool preserveOriginalPosition = false)
        {
            ArgumentNullException.ThrowIfNull(window);
            var result = ExecuteAlgorithmicOperation(
                desktop,
                runtimeState,
                settings,
                "UnregisterWindow",
                tree => m_masterSatelliteEngine.RemoveWindow(tree, runtimeState, settings, window));
            if (result.Succeeded && !preserveOriginalPosition)
            {
                m_originalPositions.Remove(window);
            }
            return result;
        }

        public MasterSatelliteOperationResult PromoteMasterSatelliteWindow(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings,
            IWindow window)
        {
            return ExecuteAlgorithmicOperation(
                desktop,
                runtimeState,
                settings,
                "PromoteToMaster",
                tree => m_masterSatelliteEngine.PromoteToMaster(tree, runtimeState, settings, window));
        }

        public MasterSatelliteOperationResult ReorderMasterSatelliteWindow(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings,
            int fromIndex,
            int toIndex)
        {
            return ExecuteAlgorithmicOperation(
                desktop,
                runtimeState,
                settings,
                "ReorderSatellite",
                tree => m_masterSatelliteEngine.ReorderSatellite(tree, runtimeState, settings, fromIndex, toIndex));
        }

        public MasterSatelliteOperationResult MoveMasterSatelliteWindowPrevious(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings,
            IWindow window)
        {
            return ExecuteAlgorithmicOperation(
                desktop,
                runtimeState,
                settings,
                "MoveSatellitePrevious",
                tree => m_masterSatelliteEngine.MoveSatellitePrevious(tree, runtimeState, settings, window));
        }

        public MasterSatelliteOperationResult MoveMasterSatelliteWindowNext(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings,
            IWindow window)
        {
            return ExecuteAlgorithmicOperation(
                desktop,
                runtimeState,
                settings,
                "MoveSatelliteNext",
                tree => m_masterSatelliteEngine.MoveSatelliteNext(tree, runtimeState, settings, window));
        }

        public MasterSatelliteOperationResult SetMasterSatelliteSide(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings,
            MasterSide side)
        {
            return ExecuteAlgorithmicOperation(
                desktop,
                runtimeState,
                settings,
                "SetMasterSide",
                tree => m_masterSatelliteEngine.SetMasterSide(tree, runtimeState, settings, side));
        }

        public MasterSatelliteOperationResult SwapMasterSatelliteSide(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings)
        {
            return ExecuteAlgorithmicOperation(
                desktop,
                runtimeState,
                settings,
                "SwapMasterSide",
                tree => m_masterSatelliteEngine.SwapMasterSide(tree, runtimeState, settings));
        }

        public MasterSatelliteOperationResult SetMasterSatelliteOrientation(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings,
            SatelliteLayoutOrientation orientation)
        {
            return ExecuteAlgorithmicOperation(
                desktop,
                runtimeState,
                settings,
                "SetSatelliteOrientation",
                tree => m_masterSatelliteEngine.SetSatelliteOrientation(tree, runtimeState, settings, orientation));
        }

        public MasterSatelliteOperationResult SetMasterSatelliteRatio(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings,
            double ratio)
        {
            return ExecuteAlgorithmicOperation(
                desktop,
                runtimeState,
                settings,
                "SetMasterRatio",
                tree => m_masterSatelliteEngine.SetRequestedMasterRatio(tree, runtimeState, settings, ratio));
        }

        public MasterSatelliteOperationResult ResetMasterSatelliteRatio(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings)
        {
            return ExecuteAlgorithmicOperation(
                desktop,
                runtimeState,
                settings,
                "ResetMasterRatio",
                tree => m_masterSatelliteEngine.ResetMasterRatio(tree, runtimeState, settings));
        }

        public MasterSatelliteOperationResult CaptureMasterSatelliteRatio(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings)
        {
            return ExecuteAlgorithmicOperation(
                desktop,
                runtimeState,
                settings,
                "CaptureMasterRatio",
                tree => m_masterSatelliteEngine.CaptureCurrentMasterRatio(tree, runtimeState, settings));
        }

        public MasterSatelliteOperationResult RelayoutMasterSatelliteLayout(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings)
        {
            return ExecuteAlgorithmicOperation(
                desktop,
                runtimeState,
                settings,
                "Relayout",
                tree => m_masterSatelliteEngine.Relayout(tree, runtimeState, settings));
        }

        public MasterSatelliteOperationResult NormalizeMasterSatelliteLayout(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings)
        {
            return ExecuteAlgorithmicOperation(
                desktop,
                runtimeState,
                settings,
                "Normalize",
                tree =>
                {
                    var invariant = m_masterSatelliteEngine.ValidateInvariant(tree, runtimeState, settings);
                    if (invariant.IsValid)
                    {
                        var snapshot = m_masterSatelliteEngine.CreateSnapshot(tree, runtimeState);
                        return MasterSatelliteOperationResult.Success(
                            false,
                            snapshot,
                            snapshot,
                            invariant,
                            "The canonical layout does not require normalization.");
                    }
                    return m_masterSatelliteEngine.Normalize(tree, runtimeState, settings);
                },
                requireCanonicalInput: false);
        }

        /// <summary>
        /// Atomically rebuilds the canonical tree from its current logical roles.
        /// Unlike normalization, this intentionally runs for an already-valid tree
        /// so satellite flex values are distributed evenly.
        /// </summary>
        public MasterSatelliteOperationResult RebalanceMasterSatelliteLayout(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings)
        {
            return ExecuteAlgorithmicOperation(
                desktop,
                runtimeState,
                settings,
                "Rebalance",
                tree => m_masterSatelliteEngine.Normalize(tree, runtimeState, settings),
                requireCanonicalInput: false);
        }

        private MasterSatellitePlacementResult RegisterAlgorithmicWindow(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings,
            IWindow window,
            MasterSatelliteWindowRole expectedRole,
            int? satelliteIndex,
            Rectangle? suppliedOriginalPosition,
            string operationName,
            bool preferSuppliedOriginalPosition = false)
        {
            ArgumentNullException.ThrowIfNull(window);
            if (!TryGetDesktopState(desktop, runtimeState, settings, out var desktopState, out var failure))
            {
                return new MasterSatellitePlacementResult(failure!, null, null);
            }
            if (!TryValidatePlacementRequest(
                desktopState!.DesktopTree,
                runtimeState,
                settings,
                expectedRole,
                satelliteIndex,
                out failure))
            {
                return new MasterSatellitePlacementResult(failure!, null, null);
            }
            if (!TryResolveOriginalPosition(
                window,
                suppliedOriginalPosition,
                preferSuppliedOriginalPosition,
                out var originalPosition,
                out string? error))
            {
                var operation = CreateFailure(
                    desktopState.DesktopTree,
                    runtimeState,
                    settings,
                    MasterSatelliteFailureReason.UnexpectedFailure,
                    error!);
                return new MasterSatellitePlacementResult(operation, null, null);
            }

            long beforeRevision = runtimeState.Revision;
            var focusedWindow = CaptureFocusedWindow(desktopState);
            MasterSatellitePlacementResult placement;
            try
            {
                placement = m_masterSatelliteEngine.PlaceWindow(
                    desktopState.DesktopTree,
                    runtimeState,
                    settings,
                    window,
                    satelliteIndex);
            }
            finally
            {
                RemapFocus(desktopState, focusedWindow);
            }

            var operationResult = VerifyValidAfterChangedOperation(
                desktopState.DesktopTree,
                runtimeState,
                settings,
                placement.Operation);
            if (operationResult.Succeeded)
            {
                if (preferSuppliedOriginalPosition && suppliedOriginalPosition.HasValue)
                {
                    m_originalPositions[window] = originalPosition;
                }
                else
                {
                    m_originalPositions.TryAdd(window, originalPosition);
                }
            }
            ReportRevision(desktop, operationName, beforeRevision, runtimeState.Revision);

            return new MasterSatellitePlacementResult(
                operationResult,
                operationResult.Succeeded ? expectedRole : null,
                operationResult.Succeeded && expectedRole == MasterSatelliteWindowRole.Satellite
                    ? satelliteIndex
                    : null);
        }

        private MasterSatelliteOperationResult ExecuteAlgorithmicOperation(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings,
            string operationName,
            Func<DesktopTree, MasterSatelliteOperationResult> operation,
            bool requireCanonicalInput = true)
        {
            ArgumentNullException.ThrowIfNull(operation);
            if (!TryGetDesktopState(desktop, runtimeState, settings, out var desktopState, out var failure))
            {
                return failure!;
            }

            var tree = desktopState!.DesktopTree;
            if (!settings.Enabled)
            {
                return CreateFailure(
                    tree,
                    runtimeState,
                    settings,
                    MasterSatelliteFailureReason.Inactive,
                    "Master + Satellites is disabled in settings.");
            }
            if (!runtimeState.IsActive)
            {
                return CreateFailure(
                    tree,
                    runtimeState,
                    settings,
                    MasterSatelliteFailureReason.Inactive,
                    "The supplied runtime state is not active.");
            }
            if (requireCanonicalInput)
            {
                var inputInvariant = m_masterSatelliteEngine.ValidateInvariant(tree, runtimeState, settings);
                if (!inputInvariant.IsValid)
                {
                    return MasterSatelliteOperationResult.Failure(
                        MasterSatelliteFailureReason.InvalidCanonicalTree,
                        inputInvariant.Description,
                        m_masterSatelliteEngine.CreateSnapshot(tree, runtimeState),
                        inputInvariant);
                }
            }

            long beforeRevision = runtimeState.Revision;
            var focusedWindow = CaptureFocusedWindow(desktopState);
            try
            {
                var result = operation(tree);
                result = VerifyValidAfterChangedOperation(tree, runtimeState, settings, result);
                ReportRevision(desktop, operationName, beforeRevision, runtimeState.Revision);
                return result;
            }
            finally
            {
                RemapFocus(desktopState, focusedWindow);
            }
        }

        private bool TryValidatePlacementRequest(
            DesktopTree tree,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings,
            MasterSatelliteWindowRole expectedRole,
            int? satelliteIndex,
            out MasterSatelliteOperationResult? failure)
        {
            failure = null;
            if (!settings.Enabled)
            {
                failure = CreateFailure(tree, runtimeState, settings,
                    MasterSatelliteFailureReason.Inactive,
                    "Master + Satellites is disabled in settings.");
                return false;
            }
            if (!runtimeState.IsActive)
            {
                failure = CreateFailure(tree, runtimeState, settings,
                    MasterSatelliteFailureReason.Inactive,
                    "The supplied runtime state is not active.");
                return false;
            }
            if (!Enum.IsDefined(expectedRole))
            {
                failure = CreateFailure(tree, runtimeState, settings,
                    MasterSatelliteFailureReason.InvalidArgument,
                    "The requested placement role is invalid.");
                return false;
            }

            var invariant = m_masterSatelliteEngine.ValidateInvariant(tree, runtimeState, settings);
            if (!invariant.IsValid)
            {
                failure = MasterSatelliteOperationResult.Failure(
                    MasterSatelliteFailureReason.InvalidCanonicalTree,
                    invariant.Description,
                    m_masterSatelliteEngine.CreateSnapshot(tree, runtimeState),
                    invariant);
                return false;
            }

            var actualRole = runtimeState.Master == null
                ? MasterSatelliteWindowRole.Master
                : MasterSatelliteWindowRole.Satellite;
            if (expectedRole != actualRole)
            {
                failure = CreateFailure(tree, runtimeState, settings,
                    MasterSatelliteFailureReason.InvalidArgument,
                    $"The next physical slot is {actualRole}, not {expectedRole}.");
                return false;
            }
            if (expectedRole == MasterSatelliteWindowRole.Master && satelliteIndex.HasValue)
            {
                failure = CreateFailure(tree, runtimeState, settings,
                    MasterSatelliteFailureReason.InvalidSatelliteIndex,
                    "A master placement cannot specify a satellite index.");
                return false;
            }
            if (expectedRole == MasterSatelliteWindowRole.Satellite
                && (!satelliteIndex.HasValue
                    || satelliteIndex.Value < 0
                    || satelliteIndex.Value > runtimeState.Satellites.Count))
            {
                failure = CreateFailure(tree, runtimeState, settings,
                    MasterSatelliteFailureReason.InvalidSatelliteIndex,
                    "The requested satellite insertion index is outside the current panel.");
                return false;
            }
            return true;
        }

        private MasterSatelliteOperationResult VerifyValidAfterChangedOperation(
            DesktopTree tree,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings,
            MasterSatelliteOperationResult result)
        {
            if (!result.Succeeded || !result.Changed)
            {
                return result;
            }

            var invariant = m_masterSatelliteEngine.ValidateInvariant(tree, runtimeState, settings);
            bool revisionAdvancedOnce = runtimeState.Revision == result.Before.Revision + 1;
            if (invariant.IsValid && revisionAdvancedOnce)
            {
                return result;
            }

            string message = !invariant.IsValid
                ? $"An engine operation returned success with an invalid tree: {invariant.Description}"
                : $"An engine operation advanced revision from {result.Before.Revision} to {runtimeState.Revision}; exactly one increment was expected.";
            Debug.Fail(message);
            Trace.TraceError($"Master + Satellites postcondition failure: {message}");
            return result;
        }

        private bool TryGetDesktopState(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings,
            out DesktopState? desktopState,
            out MasterSatelliteOperationResult? failure)
        {
            ArgumentNullException.ThrowIfNull(desktop);
            ArgumentNullException.ThrowIfNull(runtimeState);
            ArgumentNullException.ThrowIfNull(settings);

            desktopState = m_states.GetState(desktop);
            if (desktopState != null)
            {
                failure = null;
                return true;
            }

            failure = CreateFailure(
                null,
                runtimeState,
                settings,
                MasterSatelliteFailureReason.InvalidArgument,
                "The virtual desktop is not registered with this workspace.");
            return false;
        }

        private MasterSatelliteOperationResult CreateFailureForDesktop(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings,
            MasterSatelliteFailureReason reason,
            string message)
        {
            ArgumentNullException.ThrowIfNull(desktop);
            ArgumentNullException.ThrowIfNull(runtimeState);
            ArgumentNullException.ThrowIfNull(settings);
            return CreateFailure(m_states.GetState(desktop)?.DesktopTree, runtimeState, settings, reason, message);
        }

        private MasterSatelliteOperationResult CreateFailure(
            DesktopTree? tree,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings,
            MasterSatelliteFailureReason reason,
            string message)
        {
            if (tree != null)
            {
                var snapshot = m_masterSatelliteEngine.CreateSnapshot(tree, runtimeState);
                return MasterSatelliteOperationResult.Failure(
                    reason,
                    message,
                    snapshot,
                    m_masterSatelliteEngine.ValidateInvariant(tree, runtimeState, settings));
            }

            const string description = "<desktop is not registered>";
            var detachedSnapshot = new MasterSatelliteLayoutSnapshot(
                runtimeState,
                Rectangle.Empty,
                description);
            return MasterSatelliteOperationResult.Failure(
                reason,
                message,
                detachedSnapshot,
                new MasterSatelliteInvariantResult([message], description));
        }

        private WorkspaceLayoutKind ClassifyLayout(
            DesktopTree tree,
            MasterSatelliteRuntimeState? runtimeState,
            MasterSatelliteLayoutSettings settings)
        {
            if (runtimeState != null)
            {
                var invariant = m_masterSatelliteEngine.ValidateInvariant(tree, runtimeState, settings);
                return runtimeState.IsActive && invariant.IsValid
                    ? WorkspaceLayoutKind.Canonical
                    : WorkspaceLayoutKind.CorruptedCanonical;
            }

            try
            {
                if (tree.Root != null
                    && !tree.Root.Windows.Any()
                    && !tree.Root.Nodes.Skip(1).Any())
                {
                    return WorkspaceLayoutKind.Empty;
                }
                return WorkspaceLayoutKind.Manual;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                Debug.WriteLine($"Unable to classify the desktop tree: {e}");
                Trace.TraceError($"Master + Satellites classification fallback: {e}");
                return WorkspaceLayoutKind.CorruptedCanonical;
            }
        }

        private MasterSatelliteWorkspaceDesktopRestorePoint CaptureDesktopRestorePoint(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState? runtimeState,
            MasterSatelliteLayoutSettings validationSettings)
        {
            var desktopState = m_states.GetState(desktop)
                ?? throw new InvalidOperationException(
                    "The transfer endpoint desktop is not registered with this workspace.");
            return new MasterSatelliteWorkspaceDesktopRestorePoint(
                desktop,
                desktopState.DesktopTree.Root == null
                    ? null
                    : (PanelNode)desktopState.DesktopTree.Root.Clone(),
                CaptureFocusedWindow(desktopState),
                runtimeState,
                runtimeState?.Clone(),
                validationSettings with { });
        }

        private void RestoreDesktop(
            MasterSatelliteWorkspaceDesktopRestorePoint restorePoint)
        {
            var desktopState = m_states.GetState(restorePoint.Desktop)
                ?? throw new InvalidOperationException(
                    "The transfer endpoint desktop disappeared during rollback.");
            desktopState.DesktopTree.Root = restorePoint.Root == null
                ? null
                : (PanelNode)restorePoint.Root.Clone();
            if (restorePoint.RuntimeState != null
                && restorePoint.RuntimeSnapshot != null)
            {
                restorePoint.RuntimeState.CopyFrom(restorePoint.RuntimeSnapshot);
            }
            desktopState.FocusedNode = restorePoint.FocusedWindow == null
                ? null
                : desktopState.DesktopTree.FindNode(restorePoint.FocusedWindow);
        }

        private void ValidateRestoredDesktop(
            MasterSatelliteWorkspaceDesktopRestorePoint restorePoint)
        {
            if (restorePoint.RuntimeState == null)
            {
                return;
            }
            var desktopState = m_states.GetState(restorePoint.Desktop)
                ?? throw new InvalidOperationException(
                    "The restored transfer endpoint desktop is unavailable.");
            var invariant = m_masterSatelliteEngine.ValidateInvariant(
                desktopState.DesktopTree,
                restorePoint.RuntimeState,
                restorePoint.ValidationSettings);
            if (!invariant.IsValid)
            {
                throw new InvalidOperationException(
                    $"The restored transfer endpoint is not canonical: {invariant.Description}");
            }
        }

        private static DesktopTree CloneTree(DesktopTree tree)
        {
            return new DesktopTree
            {
                WorkArea = tree.WorkArea,
                Root = tree.Root == null ? null : (PanelNode)tree.Root.Clone(),
            };
        }

        private static bool LayoutKeysMatch(LayoutStateKey left, LayoutStateKey right)
        {
            return (ReferenceEquals(left.VirtualDesktop, right.VirtualDesktop)
                    || EqualityComparer<IVirtualDesktop>.Default.Equals(left.VirtualDesktop, right.VirtualDesktop))
                && (ReferenceEquals(left.Display, right.Display)
                    || EqualityComparer<IDisplay>.Default.Equals(left.Display, right.Display));
        }

        private static IReadOnlyList<IWindow> GetWindows(DesktopTree tree)
        {
            return tree.Root?.Windows.Select(node => node.WindowReference).ToList() ?? [];
        }

        private static int TryCountWindows(DesktopTree tree)
        {
            try
            {
                return tree.Root?.Windows.Count() ?? 0;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                Debug.WriteLine($"Unable to count windows in the desktop tree: {e}");
                Trace.TraceError($"Master + Satellites window-count fallback: {e}");
                return 0;
            }
        }

        private static IWindow? CaptureFocusedWindow(DesktopState desktopState)
        {
            return (desktopState.FocusedNode as WindowNode)?.WindowReference;
        }

        private static void RemapFocus(DesktopState desktopState, IWindow? focusedWindow)
        {
            if (focusedWindow != null)
            {
                desktopState.FocusedNode = desktopState.DesktopTree.FindNode(focusedWindow);
            }
            else if (desktopState.FocusedNode?.Desktop == null)
            {
                desktopState.FocusedNode = null;
            }
        }

        private bool TryCaptureMissingOriginalPositions(
            IEnumerable<IWindow> windows,
            out List<(IWindow Window, Rectangle Position)> positions,
            out string? error)
        {
            positions = [];
            foreach (var window in windows)
            {
                if (m_originalPositions.ContainsKey(window))
                {
                    continue;
                }
                try
                {
                    positions.Add((window, window.Position));
                }
                catch (Exception e)
                {
                    Trace.TraceError($"Master + Satellites original-position capture fallback: {e}");
                    error = $"The original position for a window could not be read ({e.GetType().Name}: {e.Message}).";
                    positions = [];
                    return false;
                }
            }
            error = null;
            return true;
        }

        private bool TryResolveOriginalPosition(
            IWindow window,
            Rectangle? suppliedPosition,
            bool preferSuppliedPosition,
            out Rectangle originalPosition,
            out string? error)
        {
            if (preferSuppliedPosition && suppliedPosition.HasValue)
            {
                originalPosition = suppliedPosition.Value;
                error = null;
                return true;
            }
            if (m_originalPositions.TryGetValue(window, out originalPosition))
            {
                error = null;
                return true;
            }
            if (suppliedPosition.HasValue)
            {
                originalPosition = suppliedPosition.Value;
                error = null;
                return true;
            }
            try
            {
                originalPosition = window.Position;
                error = null;
                return true;
            }
            catch (Exception e)
            {
                Trace.TraceError($"Master + Satellites original-position read fallback: {e}");
                originalPosition = Rectangle.Empty;
                error = $"The original position for the window could not be read ({e.GetType().Name}: {e.Message}).";
                return false;
            }
        }

        private void ReportRevision(
            IVirtualDesktop desktop,
            string operation,
            long beforeRevision,
            long afterRevision)
        {
            if (beforeRevision == afterRevision || m_revisionDiagnostic == null)
            {
                return;
            }

            try
            {
                m_revisionDiagnostic(new WorkspaceLayoutRevisionDiagnostic(
                    desktop,
                    operation,
                    beforeRevision,
                    afterRevision));
            }
            catch (Exception e)
            {
                Trace.TraceError($"Master + Satellites revision diagnostic failed: {e}");
            }
        }
    }
}
