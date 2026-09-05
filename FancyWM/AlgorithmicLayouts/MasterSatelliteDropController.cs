using System;
using System.Collections.Generic;
using System.Linq;

using FancyWM.Layouts;
using FancyWM.Layouts.Tiling;
using FancyWM.Models;

using WinMan;

namespace FancyWM.AlgorithmicLayouts
{
    internal enum MasterSatelliteDropKind
    {
        ReorderSatellite,
        PromoteSourceSatellite,
        PromoteTargetSatellite,
        ChangeMasterSide,
    }

    /// <summary>
    /// Immutable description of a canonical mouse drop. A plan is produced by
    /// mutating detached tree/state clones, so displaying its preview never changes
    /// the production workspace.
    /// </summary>
    internal sealed record class MasterSatelliteDropPlan
    {
        public bool IsAccepted { get; init; }

        public MasterSatelliteDropKind? Kind { get; init; }

        public IVirtualDesktop Desktop { get; init; } = null!;

        public IWindow? Source { get; init; }

        public IWindow? Target { get; init; }

        public int? FromSatelliteIndex { get; init; }

        public int? ToSatelliteIndex { get; init; }

        public MasterSide? TargetMasterSide { get; init; }

        public long SourceRevision { get; init; }

        public Rectangle? PreviewRectangle { get; init; }

        public IReadOnlyList<IWindow> PreviewWindows { get; init; } = Array.Empty<IWindow>();

        public MasterSatelliteFailureReason FailureReason { get; init; }

        public string? Message { get; init; }

        public MasterSatelliteOperationResult? PreviewOperation { get; init; }
    }

    /// <summary>
    /// Converts pointer locations into the small set of mutations supported by the
    /// canonical Master + Satellites tree. It never invokes generic MoveNode/MoveWindow.
    /// </summary>
    internal sealed class MasterSatelliteDropController
    {
        private readonly MasterSatelliteLayoutEngine m_previewEngine = new();

        public MasterSatelliteDropPlan CreateWindowDropPlan(
            TilingWorkspace backend,
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings,
            IWindow source,
            Point pointer)
        {
            ArgumentNullException.ThrowIfNull(backend);
            ArgumentNullException.ThrowIfNull(desktop);
            ArgumentNullException.ThrowIfNull(runtimeState);
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(source);

            if (!runtimeState.IsActive)
            {
                return Reject(
                    desktop,
                    source,
                    runtimeState.Revision,
                    MasterSatelliteFailureReason.Inactive,
                    "Master + Satellites is not active for this desktop and display.");
            }
            if (!backend.TryGetTreeSnapshot(desktop, out var previewTree))
            {
                return Reject(
                    desktop,
                    source,
                    runtimeState.Revision,
                    MasterSatelliteFailureReason.InvalidCanonicalTree,
                    "The canonical tree could not be copied for drop planning.");
            }

            var previewState = runtimeState.Clone();
            var invariant = m_previewEngine.ValidateInvariant(previewTree, previewState, settings);
            if (!invariant.IsValid)
            {
                return Reject(
                    desktop,
                    source,
                    runtimeState.Revision,
                    MasterSatelliteFailureReason.InvalidCanonicalTree,
                    invariant.Description);
            }

            try
            {
                previewTree.Measure();
                previewTree.Arrange();
            }
            catch (Exception e) when (e is InvalidOperationException or UnsatisfiableFlexConstraintsException)
            {
                return Reject(
                    desktop,
                    source,
                    runtimeState.Revision,
                    MasterSatelliteFailureReason.MinSizeConflict,
                    $"The canonical drop preview could not be arranged ({e.GetType().Name}).");
            }

            var sourceNode = previewTree.FindNode(source);
            if (sourceNode == null || (!WindowsMatch(previewState.Master, source)
                && IndexOfWindow(previewState.Satellites, source) < 0))
            {
                return Reject(
                    desktop,
                    source,
                    runtimeState.Revision,
                    MasterSatelliteFailureReason.WindowNotFound,
                    "Only a window in the canonical layout can be dragged.");
            }
            if (!previewTree.WorkArea.Contains(pointer))
            {
                return Reject(
                    desktop,
                    source,
                    runtimeState.Revision,
                    MasterSatelliteFailureReason.UnsupportedOperation,
                    "Dropping a canonical window outside the layout is not supported.");
            }

            if (WindowsMatch(previewState.Master, source))
            {
                return PlanMasterDrop(
                    desktop,
                    previewTree,
                    previewState,
                    settings,
                    source,
                    pointer,
                    runtimeState.Revision);
            }
            return PlanSatelliteDrop(
                desktop,
                previewTree,
                previewState,
                settings,
                source,
                pointer,
                runtimeState.Revision);
        }

        public MasterSatelliteDropPlan CreatePanelDropRejection(
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState runtimeState,
            PanelNode panel)
        {
            ArgumentNullException.ThrowIfNull(desktop);
            ArgumentNullException.ThrowIfNull(runtimeState);
            ArgumentNullException.ThrowIfNull(panel);
            return Reject(
                desktop,
                null,
                runtimeState.Revision,
                MasterSatelliteFailureReason.UnsupportedOperation,
                "Canonical panels cannot be moved or nested while Master + Satellites is active.");
        }

        /// <summary>
        /// Replans from the pointer observed at commit time before mutating the
        /// live tree. Preview plans are asynchronous UI artifacts and must not be
        /// reused when mouse-up can occur before the next preview tick.
        /// </summary>
        public MasterSatelliteCommandResult ApplyWindowDropAtPointer(
            TilingWorkspace backend,
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings,
            IWindow source,
            Point pointer)
        {
            var plan = CreateWindowDropPlan(
                backend,
                desktop,
                runtimeState,
                settings,
                source,
                pointer);
            return Apply(
                backend,
                desktop,
                runtimeState,
                settings,
                plan);
        }

        public MasterSatelliteCommandResult Apply(
            TilingWorkspace backend,
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState runtimeState,
            MasterSatelliteLayoutSettings settings,
            MasterSatelliteDropPlan plan)
        {
            ArgumentNullException.ThrowIfNull(backend);
            ArgumentNullException.ThrowIfNull(desktop);
            ArgumentNullException.ThrowIfNull(runtimeState);
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(plan);

            if (!plan.IsAccepted)
            {
                return Rejected(plan.FailureReason, plan.Message, plan.PreviewOperation?.Invariant);
            }
            if (!ReferenceEquals(plan.Desktop, desktop) || plan.SourceRevision != runtimeState.Revision)
            {
                return Rejected(
                    MasterSatelliteFailureReason.InvalidCanonicalTree,
                    "The layout changed after this drop was previewed; the stale drop was ignored.");
            }

            MasterSatelliteOperationResult operation = plan.Kind switch
            {
                MasterSatelliteDropKind.ReorderSatellite => backend.ReorderMasterSatelliteWindow(
                    desktop,
                    runtimeState,
                    settings,
                    plan.FromSatelliteIndex!.Value,
                    plan.ToSatelliteIndex!.Value),
                MasterSatelliteDropKind.PromoteSourceSatellite => backend.PromoteMasterSatelliteWindow(
                    desktop,
                    runtimeState,
                    settings,
                    plan.Source!),
                MasterSatelliteDropKind.PromoteTargetSatellite => backend.PromoteMasterSatelliteWindow(
                    desktop,
                    runtimeState,
                    settings,
                    plan.Target!),
                MasterSatelliteDropKind.ChangeMasterSide => backend.SetMasterSatelliteSide(
                    desktop,
                    runtimeState,
                    settings,
                    plan.TargetMasterSide!.Value),
                _ => throw new InvalidOperationException("The accepted drop plan has no supported mutation."),
            };

            // Every workspace mutation above runs through the engine's atomic
            // commit/rollback boundary and returns the post-mutation invariant.
            // Re-validating after that boundary could only observe a later,
            // unrelated mutation and could not roll it back from this controller.
            var invariant = operation.Invariant;
            if (!operation.Succeeded || !invariant.IsValid)
            {
                return new MasterSatelliteCommandResult(
                    MasterSatelliteCommandDisposition.Rejected,
                    false,
                    false,
                    operation.Succeeded
                        ? MasterSatelliteFailureReason.InvalidCanonicalTree
                        : operation.FailureReason,
                    operation.Succeeded
                        ? invariant.Description
                        : operation.Message,
                    operation,
                    invariant);
            }
            return new MasterSatelliteCommandResult(
                MasterSatelliteCommandDisposition.Applied,
                true,
                operation.Changed,
                MasterSatelliteFailureReason.None,
                operation.Message,
                operation,
                invariant);
        }

        private MasterSatelliteDropPlan PlanMasterDrop(
            IVirtualDesktop desktop,
            DesktopTree previewTree,
            MasterSatelliteRuntimeState previewState,
            MasterSatelliteLayoutSettings settings,
            IWindow source,
            Point pointer,
            long sourceRevision)
        {
            var masterNode = previewTree.FindNode(source)!;
            int boundary = previewState.MasterSide == MasterSide.Left
                ? masterNode.ComputedRectangle.Right
                : masterNode.ComputedRectangle.Left;
            int boundaryTolerance = Math.Max(8, previewTree.WorkArea.Width / 50);
            bool crossedBoundary = previewState.MasterSide == MasterSide.Left
                ? pointer.X >= boundary
                : pointer.X <= boundary;
            if (crossedBoundary && Math.Abs(pointer.X - boundary) <= boundaryTolerance)
            {
                var targetSide = previewState.MasterSide == MasterSide.Left
                    ? MasterSide.Right
                    : MasterSide.Left;
                return Simulate(
                    desktop,
                    previewTree,
                    previewState,
                    settings,
                    source,
                    null,
                    MasterSatelliteDropKind.ChangeMasterSide,
                    null,
                    null,
                    targetSide,
                    sourceRevision);
            }

            var target = FindWindowAtPoint(previewTree, previewState.Satellites, pointer);
            if (target != null)
            {
                return Simulate(
                    desktop,
                    previewTree,
                    previewState,
                    settings,
                    source,
                    target,
                    MasterSatelliteDropKind.PromoteTargetSatellite,
                    null,
                    null,
                    null,
                    sourceRevision);
            }

            return Reject(
                desktop,
                source,
                sourceRevision,
                MasterSatelliteFailureReason.UnsupportedOperation,
                "The master can only swap with a satellite or cross the central boundary.");
        }

        private MasterSatelliteDropPlan PlanSatelliteDrop(
            IVirtualDesktop desktop,
            DesktopTree previewTree,
            MasterSatelliteRuntimeState previewState,
            MasterSatelliteLayoutSettings settings,
            IWindow source,
            Point pointer,
            long sourceRevision)
        {
            var masterNode = previewTree.FindNode(previewState.Master!);
            if (masterNode?.ComputedRectangle.Contains(pointer) == true)
            {
                return Simulate(
                    desktop,
                    previewTree,
                    previewState,
                    settings,
                    source,
                    previewState.Master,
                    MasterSatelliteDropKind.PromoteSourceSatellite,
                    null,
                    null,
                    null,
                    sourceRevision);
            }

            int sourceIndex = IndexOfWindow(previewState.Satellites, source);
            int targetIndex = FindWindowIndexAtPoint(
                previewTree,
                previewState.Satellites,
                pointer,
                sourceIndex);
            if (targetIndex < 0)
            {
                return Reject(
                    desktop,
                    source,
                    sourceRevision,
                    MasterSatelliteFailureReason.UnsupportedOperation,
                    "A satellite can only be dropped on the master or another satellite.");
            }

            var target = previewState.Satellites[targetIndex];
            var targetRectangle = previewTree.FindNode(target)!.ComputedRectangle;
            bool before = previewState.SatelliteOrientation == SatelliteLayoutOrientation.Vertical
                ? pointer.Y < targetRectangle.Center.Y
                : pointer.X < targetRectangle.Center.X;
            int destinationIndex = before
                ? targetIndex - (sourceIndex < targetIndex ? 1 : 0)
                : targetIndex + (sourceIndex > targetIndex ? 1 : 0);
            if (destinationIndex == sourceIndex)
            {
                return Reject(
                    desktop,
                    source,
                    sourceRevision,
                    MasterSatelliteFailureReason.UnsupportedOperation,
                    "The satellite is already in the requested slot.");
            }

            return Simulate(
                desktop,
                previewTree,
                previewState,
                settings,
                source,
                target,
                MasterSatelliteDropKind.ReorderSatellite,
                sourceIndex,
                destinationIndex,
                null,
                sourceRevision);
        }

        private MasterSatelliteDropPlan Simulate(
            IVirtualDesktop desktop,
            DesktopTree previewTree,
            MasterSatelliteRuntimeState previewState,
            MasterSatelliteLayoutSettings settings,
            IWindow source,
            IWindow? target,
            MasterSatelliteDropKind kind,
            int? fromIndex,
            int? toIndex,
            MasterSide? targetSide,
            long sourceRevision)
        {
            MasterSatelliteOperationResult operation = kind switch
            {
                MasterSatelliteDropKind.ReorderSatellite => m_previewEngine.ReorderSatellite(
                    previewTree,
                    previewState,
                    settings,
                    fromIndex!.Value,
                    toIndex!.Value),
                MasterSatelliteDropKind.PromoteSourceSatellite => m_previewEngine.PromoteToMaster(
                    previewTree,
                    previewState,
                    settings,
                    source),
                MasterSatelliteDropKind.PromoteTargetSatellite => m_previewEngine.PromoteToMaster(
                    previewTree,
                    previewState,
                    settings,
                    target!),
                MasterSatelliteDropKind.ChangeMasterSide => m_previewEngine.SetMasterSide(
                    previewTree,
                    previewState,
                    settings,
                    targetSide!.Value),
                _ => throw new InvalidOperationException("Unknown canonical drop mutation."),
            };
            if (!operation.Succeeded || !operation.Changed)
            {
                return Reject(
                    desktop,
                    source,
                    sourceRevision,
                    operation.Succeeded
                        ? MasterSatelliteFailureReason.UnsupportedOperation
                        : operation.FailureReason,
                    operation.Message ?? "The drop would not change the canonical layout.",
                    operation);
            }

            previewTree.Measure();
            previewTree.Arrange();
            var previewRectangle = previewTree.FindNode(source)?.ComputedRectangle;
            var previewWindows = target == null || WindowsMatch(source, target)
                ? new[] { source }
                : new[] { source, target };
            return new MasterSatelliteDropPlan
            {
                IsAccepted = true,
                Kind = kind,
                Desktop = desktop,
                Source = source,
                Target = target,
                FromSatelliteIndex = fromIndex,
                ToSatelliteIndex = toIndex,
                TargetMasterSide = targetSide,
                SourceRevision = sourceRevision,
                PreviewRectangle = previewRectangle,
                PreviewWindows = Array.AsReadOnly(previewWindows),
                FailureReason = MasterSatelliteFailureReason.None,
                Message = operation.Message,
                PreviewOperation = operation,
            };
        }

        private static MasterSatelliteDropPlan Reject(
            IVirtualDesktop desktop,
            IWindow? source,
            long sourceRevision,
            MasterSatelliteFailureReason reason,
            string? message,
            MasterSatelliteOperationResult? previewOperation = null)
        {
            return new MasterSatelliteDropPlan
            {
                IsAccepted = false,
                Desktop = desktop,
                Source = source,
                SourceRevision = sourceRevision,
                FailureReason = reason,
                Message = message,
                PreviewOperation = previewOperation,
            };
        }

        private static MasterSatelliteCommandResult Rejected(
            MasterSatelliteFailureReason reason,
            string? message,
            MasterSatelliteInvariantResult? invariant = null)
        {
            return new MasterSatelliteCommandResult(
                MasterSatelliteCommandDisposition.Rejected,
                false,
                false,
                reason == MasterSatelliteFailureReason.None
                    ? MasterSatelliteFailureReason.UnsupportedOperation
                    : reason,
                message,
                null,
                invariant);
        }

        private static IWindow? FindWindowAtPoint(
            DesktopTree tree,
            IReadOnlyList<IWindow> windows,
            Point pointer)
        {
            return windows.FirstOrDefault(window =>
                tree.FindNode(window)?.ComputedRectangle.Contains(pointer) == true);
        }

        private static int FindWindowIndexAtPoint(
            DesktopTree tree,
            IReadOnlyList<IWindow> windows,
            Point pointer,
            int excludedIndex)
        {
            for (int i = 0; i < windows.Count; i++)
            {
                if (i != excludedIndex
                    && tree.FindNode(windows[i])?.ComputedRectangle.Contains(pointer) == true)
                {
                    return i;
                }
            }
            return -1;
        }

        private static int IndexOfWindow(IReadOnlyList<IWindow> windows, IWindow window)
        {
            for (int i = 0; i < windows.Count; i++)
            {
                if (WindowsMatch(windows[i], window))
                {
                    return i;
                }
            }
            return -1;
        }

        private static bool WindowsMatch(IWindow? first, IWindow? second)
        {
            if (ReferenceEquals(first, second))
            {
                return first != null;
            }
            if (first == null || second == null)
            {
                return false;
            }
            try
            {
                return first.Handle != IntPtr.Zero && first.Handle == second.Handle;
            }
            catch (InvalidWindowReferenceException)
            {
                return false;
            }
        }
    }
}
