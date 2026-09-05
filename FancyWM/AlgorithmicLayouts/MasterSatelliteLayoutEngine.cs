using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

using FancyWM.Layouts;
using FancyWM.Layouts.Tiling;
using FancyWM.Models;

using WinMan;

namespace FancyWM.AlgorithmicLayouts
{
    /// <summary>
    /// Builds and mutates the canonical Master + Satellites tree. The engine has no
    /// dependency on WPF, UI notifications, or a concrete virtual-desktop backend.
    /// </summary>
    internal sealed class MasterSatelliteLayoutEngine
    {
        private readonly Action<string>? m_diagnosticLog;

        public MasterSatelliteLayoutEngine(Action<string>? diagnosticLog = null)
        {
            m_diagnosticLog = diagnosticLog;
        }

        public MasterSatelliteRuntimeState CreateState(
            MasterSatelliteLayoutSettings settings,
            bool? isActive = null)
        {
            ArgumentNullException.ThrowIfNull(settings);
            return new MasterSatelliteRuntimeState(settings, isActive ?? settings.Enabled);
        }

        public MasterSatelliteLayoutSnapshot CreateSnapshot(
            DesktopTree tree,
            MasterSatelliteRuntimeState state)
        {
            ArgumentNullException.ThrowIfNull(tree);
            ArgumentNullException.ThrowIfNull(state);
            return new MasterSatelliteLayoutSnapshot(state, tree.WorkArea, DescribeTree(tree.Root));
        }

        public MasterSatelliteOperationResult BuildLayout(
            DesktopTree tree,
            MasterSatelliteRuntimeState state,
            MasterSatelliteLayoutSettings settings,
            IEnumerable<IWindow> windows)
        {
            ArgumentNullException.ThrowIfNull(tree);
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(windows);

            if (!state.IsActive)
            {
                return Failure(tree, state, settings, MasterSatelliteFailureReason.Inactive,
                    "Master + Satellites is not active for this desktop and display.");
            }

            var orderedWindows = windows.ToList();
            if (orderedWindows.Any(window => window == null))
            {
                return Failure(tree, state, settings, MasterSatelliteFailureReason.InvalidArgument,
                    "The layout cannot contain a null window reference.");
            }
            if (orderedWindows.Distinct().Count() != orderedWindows.Count)
            {
                return Failure(tree, state, settings, MasterSatelliteFailureReason.WindowAlreadyPresent,
                    "The requested layout contains the same window more than once.");
            }
            if (orderedWindows.Count > settings.MaxSatellites + 1)
            {
                return Failure(tree, state, settings, MasterSatelliteFailureReason.CapacityReached,
                    $"The requested layout contains {orderedWindows.Count} windows, but its capacity is {settings.MaxSatellites + 1}.");
            }

            return ExecuteMutation(
                tree,
                state,
                settings,
                (targetTree, targetState) => RebuildCanonicalTree(
                    targetTree,
                    targetState,
                    orderedWindows,
                    targetTree.Root),
                requireValidCurrentTree: false,
                rebalanceSatellites: true);
        }

        public MasterSatellitePlacementResult PlaceWindow(
            DesktopTree tree,
            MasterSatelliteRuntimeState state,
            MasterSatelliteLayoutSettings settings,
            IWindow window,
            int? satelliteIndex = null)
        {
            ArgumentNullException.ThrowIfNull(tree);
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(window);

            if (!state.IsActive)
            {
                return new MasterSatellitePlacementResult(
                    Failure(tree, state, settings, MasterSatelliteFailureReason.Inactive,
                        "Master + Satellites is not active for this desktop and display."),
                    null,
                    null);
            }
            if (ContainsWindow(state, window) || tree.FindNode(window) != null)
            {
                return new MasterSatellitePlacementResult(
                    Failure(tree, state, settings, MasterSatelliteFailureReason.WindowAlreadyPresent,
                        "The window is already present in this layout."),
                    null,
                    null);
            }

            var role = state.Master == null
                ? MasterSatelliteWindowRole.Master
                : MasterSatelliteWindowRole.Satellite;
            int? finalSatelliteIndex = null;
            if (role == MasterSatelliteWindowRole.Satellite)
            {
                if (state.Satellites.Count >= settings.MaxSatellites)
                {
                    return new MasterSatellitePlacementResult(
                        Failure(tree, state, settings, MasterSatelliteFailureReason.CapacityReached,
                            $"The layout already contains its maximum of {settings.MaxSatellites} satellite windows."),
                        null,
                        null);
                }

                finalSatelliteIndex = satelliteIndex ?? state.Satellites.Count;
                if (finalSatelliteIndex < 0 || finalSatelliteIndex > state.Satellites.Count)
                {
                    return new MasterSatellitePlacementResult(
                        Failure(tree, state, settings, MasterSatelliteFailureReason.InvalidSatelliteIndex,
                            $"Satellite index {finalSatelliteIndex} is outside the valid insertion range."),
                        null,
                        null);
                }
            }
            else if (satelliteIndex.HasValue)
            {
                return new MasterSatellitePlacementResult(
                    Failure(tree, state, settings, MasterSatelliteFailureReason.InvalidSatelliteIndex,
                        "A satellite index cannot be specified for the first (master) window."),
                    null,
                    null);
            }

            var operation = ExecuteMutation(
                tree,
                state,
                settings,
                (targetTree, targetState) =>
                {
                    ApplyPlacement(targetTree, targetState, window, finalSatelliteIndex);
                    return true;
                },
                rebalanceSatellites: true);

            return new MasterSatellitePlacementResult(
                operation,
                operation.Succeeded ? role : null,
                operation.Succeeded ? finalSatelliteIndex : null);
        }

        public MasterSatelliteOperationResult PromoteToMaster(
            DesktopTree tree,
            MasterSatelliteRuntimeState state,
            MasterSatelliteLayoutSettings settings,
            IWindow window)
        {
            ArgumentNullException.ThrowIfNull(window);
            if (WindowEquals(state.Master, window))
            {
                return NoOp(tree, state, settings, "The selected window is already the master.");
            }

            int satelliteIndex = IndexOfWindow(state.Satellites, window);
            if (satelliteIndex < 0)
            {
                return Failure(tree, state, settings, MasterSatelliteFailureReason.NotSatellite,
                    "Only a satellite window can be promoted to master.");
            }

            return ExecuteMutation(
                tree,
                state,
                settings,
                (targetTree, targetState) =>
                {
                    var (root, masterNode, satellitePanel) = GetCanonicalNodes(targetTree, targetState);
                    _ = root;
                    var selectedNode = targetTree.FindNode(window)
                        ?? throw new EngineFailureException(
                            MasterSatelliteFailureReason.WindowNotFound,
                            "The selected satellite is not present in the tree.");

                    targetTree.Measure();
                    targetTree.Arrange();
                    masterNode.Measure();
                    var slot = selectedNode.ComputedRectangle;
                    if (masterNode.MinSize.X > slot.Width || masterNode.MinSize.Y > slot.Height)
                    {
                        throw new EngineFailureException(
                            MasterSatelliteFailureReason.MinSizeConflict,
                            $"The current master requires {masterNode.MinSize}, but the selected satellite slot is only {slot.Size}.");
                    }

                    DesktopTree.SwapReferences(masterNode, selectedNode);
                    var oldMaster = targetState.Master!;
                    targetState.Master = window;
                    targetState.MutableSatellites[satelliteIndex] = oldMaster;

                    Debug.Assert(selectedNode.Parent == root);
                    Debug.Assert(masterNode.Parent == satellitePanel);
                    return true;
                });
        }

        public MasterSatelliteOperationResult ReorderSatellite(
            DesktopTree tree,
            MasterSatelliteRuntimeState state,
            MasterSatelliteLayoutSettings settings,
            int fromIndex,
            int toIndex)
        {
            if (fromIndex < 0 || fromIndex >= state.Satellites.Count
                || toIndex < 0 || toIndex >= state.Satellites.Count)
            {
                return Failure(tree, state, settings, MasterSatelliteFailureReason.InvalidSatelliteIndex,
                    $"Cannot move satellite {fromIndex} to {toIndex}; valid indices are 0 through {state.Satellites.Count - 1}.");
            }
            if (fromIndex == toIndex)
            {
                return NoOp(tree, state, settings, "The satellite is already in the requested slot.");
            }

            return ExecuteMutation(
                tree,
                state,
                settings,
                (targetTree, targetState) =>
                {
                    var (_, _, satellitePanel) = GetCanonicalNodes(targetTree, targetState);
                    satellitePanel.Move(fromIndex, toIndex);
                    var moved = targetState.MutableSatellites[fromIndex];
                    targetState.MutableSatellites.RemoveAt(fromIndex);
                    targetState.MutableSatellites.Insert(toIndex, moved);
                    return true;
                });
        }

        public MasterSatelliteOperationResult MoveSatellitePrevious(
            DesktopTree tree,
            MasterSatelliteRuntimeState state,
            MasterSatelliteLayoutSettings settings,
            IWindow window)
        {
            int index = IndexOfWindow(state.Satellites, window);
            return index <= 0
                ? Failure(tree, state, settings, MasterSatelliteFailureReason.InvalidSatelliteIndex,
                    index < 0 ? "The window is not a satellite." : "The first satellite cannot move to a previous slot.")
                : ReorderSatellite(tree, state, settings, index, index - 1);
        }

        public MasterSatelliteOperationResult MoveSatelliteNext(
            DesktopTree tree,
            MasterSatelliteRuntimeState state,
            MasterSatelliteLayoutSettings settings,
            IWindow window)
        {
            int index = IndexOfWindow(state.Satellites, window);
            return index < 0 || index + 1 >= state.Satellites.Count
                ? Failure(tree, state, settings, MasterSatelliteFailureReason.InvalidSatelliteIndex,
                    index < 0 ? "The window is not a satellite." : "The last satellite cannot move to a following slot.")
                : ReorderSatellite(tree, state, settings, index, index + 1);
        }

        public MasterSatelliteOperationResult SetMasterSide(
            DesktopTree tree,
            MasterSatelliteRuntimeState state,
            MasterSatelliteLayoutSettings settings,
            MasterSide side)
        {
            if (!Enum.IsDefined(side))
            {
                return Failure(tree, state, settings, MasterSatelliteFailureReason.InvalidArgument,
                    $"Unknown master side: {side}.");
            }
            if (state.MasterSide == side)
            {
                return NoOp(tree, state, settings, "The master is already on the requested side.");
            }

            return ExecuteMutation(
                tree,
                state,
                settings,
                (targetTree, targetState) =>
                {
                    if (targetState.Master != null)
                    {
                        var (root, masterNode, _) = GetCanonicalNodes(targetTree, targetState, requireSatellite: false);
                        if (targetState.Satellites.Count > 0)
                        {
                            root.Move(root.IndexOf(masterNode), side == MasterSide.Left ? 0 : 1);
                        }
                    }
                    targetState.MasterSide = side;
                    return true;
                });
        }

        public MasterSatelliteOperationResult SwapMasterSide(
            DesktopTree tree,
            MasterSatelliteRuntimeState state,
            MasterSatelliteLayoutSettings settings)
        {
            return SetMasterSide(
                tree,
                state,
                settings,
                state.MasterSide == MasterSide.Left ? MasterSide.Right : MasterSide.Left);
        }

        public MasterSatelliteOperationResult SetSatelliteOrientation(
            DesktopTree tree,
            MasterSatelliteRuntimeState state,
            MasterSatelliteLayoutSettings settings,
            SatelliteLayoutOrientation orientation)
        {
            if (!Enum.IsDefined(orientation))
            {
                return Failure(tree, state, settings, MasterSatelliteFailureReason.InvalidArgument,
                    $"Unknown satellite orientation: {orientation}.");
            }
            if (state.SatelliteOrientation == orientation)
            {
                return NoOp(tree, state, settings, "The satellite panel already uses the requested orientation.");
            }

            return ExecuteMutation(
                tree,
                state,
                settings,
                (targetTree, targetState) =>
                {
                    if (targetState.Satellites.Count > 0)
                    {
                        var (_, _, satellitePanel) = GetCanonicalNodes(targetTree, targetState);
                        satellitePanel.ChangeOrientation(ToPanelOrientation(orientation));
                    }
                    targetState.SatelliteOrientation = orientation;
                    return true;
                },
                rebalanceSatellites: true);
        }

        public MasterSatelliteOperationResult SetRequestedMasterRatio(
            DesktopTree tree,
            MasterSatelliteRuntimeState state,
            MasterSatelliteLayoutSettings settings,
            double ratio)
        {
            if (!double.IsFinite(ratio))
            {
                return Failure(tree, state, settings, MasterSatelliteFailureReason.InvalidArgument,
                    "The requested master ratio must be finite.");
            }

            double normalized = Math.Clamp(
                ratio,
                MasterSatelliteLayoutSettings.MinimumMasterRatio,
                MasterSatelliteLayoutSettings.MaximumMasterRatio);
            if (state.RequestedMasterRatio == normalized)
            {
                return NoOp(tree, state, settings, "The requested master ratio is unchanged.");
            }

            return ExecuteMutation(
                tree,
                state,
                settings,
                (_, targetState) =>
                {
                    targetState.RequestedMasterRatio = normalized;
                    return true;
                });
        }

        public MasterSatelliteOperationResult ResetMasterRatio(
            DesktopTree tree,
            MasterSatelliteRuntimeState state,
            MasterSatelliteLayoutSettings settings)
        {
            return SetRequestedMasterRatio(
                tree,
                state,
                settings,
                MasterSatelliteLayoutSettings.DefaultMasterRatio);
        }

        public MasterSatelliteOperationResult CaptureCurrentMasterRatio(
            DesktopTree tree,
            MasterSatelliteRuntimeState state,
            MasterSatelliteLayoutSettings settings)
        {
            if (state.Master == null || state.Satellites.Count == 0)
            {
                return NoOp(tree, state, settings, "There is no master/satellite split to capture.");
            }

            return ExecuteMutation(
                tree,
                state,
                settings,
                (targetTree, targetState) =>
                {
                    var (root, masterNode, _) = GetCanonicalNodes(targetTree, targetState);
                    targetTree.Measure();
                    targetTree.Arrange();
                    if (root.ContainerLength <= 0)
                    {
                        throw new EngineFailureException(
                            MasterSatelliteFailureReason.MinSizeConflict,
                            "The root has no usable width from which to capture a ratio.");
                    }
                    targetState.RequestedMasterRatio = root.GetChildConstraints(masterNode).Width / root.ContainerLength;
                    return true;
                });
        }

        public MasterSatelliteOperationResult Relayout(
            DesktopTree tree,
            MasterSatelliteRuntimeState state,
            MasterSatelliteLayoutSettings settings)
        {
            return ExecuteMutation(tree, state, settings, (_, _) => true);
        }

        public MasterSatelliteOperationResult RemoveWindow(
            DesktopTree tree,
            MasterSatelliteRuntimeState state,
            MasterSatelliteLayoutSettings settings,
            IWindow window)
        {
            ArgumentNullException.ThrowIfNull(window);
            if (!ContainsWindow(state, window))
            {
                return Failure(tree, state, settings, MasterSatelliteFailureReason.WindowNotFound,
                    "The window is not present in this layout.");
            }

            return ExecuteMutation(
                tree,
                state,
                settings,
                (targetTree, targetState) =>
                {
                    var (root, masterNode, satellitePanel) = GetCanonicalNodes(
                        targetTree,
                        targetState,
                        requireSatellite: targetState.Satellites.Count > 0);

                    if (WindowEquals(targetState.Master, window))
                    {
                        if (targetState.Satellites.Count == 0)
                        {
                            root.Detach(masterNode);
                            targetState.Master = null;
                        }
                        else
                        {
                            var promotedWindow = targetState.MutableSatellites[0];
                            var promotedNode = targetTree.FindNode(promotedWindow)!;
                            DesktopTree.SwapReferences(masterNode, promotedNode);
                            satellitePanel.Detach(masterNode);
                            targetState.Master = promotedWindow;
                            targetState.MutableSatellites.RemoveAt(0);
                            if (targetState.MutableSatellites.Count == 0)
                            {
                                root.Detach(satellitePanel);
                            }
                        }
                    }
                    else
                    {
                        int index = IndexOfWindow(targetState.Satellites, window);
                        var node = targetTree.FindNode(window)!;
                        satellitePanel.Detach(node);
                        targetState.MutableSatellites.RemoveAt(index);
                        if (targetState.MutableSatellites.Count == 0)
                        {
                            root.Detach(satellitePanel);
                        }
                    }
                    return true;
                });
        }

        public MasterSatelliteInvariantResult ValidateInvariant(
            DesktopTree tree,
            MasterSatelliteRuntimeState state,
            MasterSatelliteLayoutSettings settings)
        {
            ArgumentNullException.ThrowIfNull(tree);
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(settings);

            string description = DescribeTree(tree.Root);
            var violations = new List<string>();
            if (!state.IsActive)
            {
                violations.Add("The runtime state is not active.");
            }
            if (tree.Root is not SplitPanelNode root)
            {
                violations.Add("The root must be a SplitPanelNode.");
                return new MasterSatelliteInvariantResult(violations, description);
            }
            if (root.Orientation != PanelOrientation.Horizontal)
            {
                violations.Add("The root split must be horizontal.");
            }

            List<TilingNode> nodes;
            try
            {
                nodes = root.Nodes.ToList();
            }
            catch (Exception e)
            {
                violations.Add($"The tree could not be enumerated: {e.GetType().Name}.");
                return new MasterSatelliteInvariantResult(violations, description);
            }

            if (nodes.Any(node => node is PlaceholderNode))
            {
                violations.Add("PlaceholderNode is not allowed in a canonical tree.");
            }
            if (nodes.Any(node => node is StackPanelNode))
            {
                violations.Add("StackPanelNode is not allowed in a canonical tree.");
            }
            if (nodes.Any(node => node is LayoutFunctionNode || node.Type == TilingNodeType.Static))
            {
                violations.Add("LayoutFunctionNode/static layout nodes are not allowed in a canonical tree.");
            }

            var windowNodes = nodes.OfType<WindowNode>().ToList();
            var duplicateWindows = windowNodes
                .GroupBy(node => node.WindowReference)
                .Where(group => group.Count() > 1)
                .ToList();
            if (duplicateWindows.Count > 0)
            {
                violations.Add("The tree contains duplicate WindowNode references for a logical window.");
            }

            if (state.Satellites.Count > settings.MaxSatellites)
            {
                violations.Add($"The runtime state exceeds the satellite limit of {settings.MaxSatellites}.");
            }
            if (state.Satellites.Distinct().Count() != state.Satellites.Count)
            {
                violations.Add("The runtime satellite list contains duplicates.");
            }
            if (state.Master != null && state.Satellites.Any(window => WindowEquals(window, state.Master)))
            {
                violations.Add("The master also appears in the satellite list.");
            }

            if (state.Master == null)
            {
                if (state.Satellites.Count != 0)
                {
                    violations.Add("Satellites cannot exist without a master.");
                }
                if (root.Children.Count != 0 || windowNodes.Count != 0)
                {
                    violations.Add("An empty runtime state requires an empty root.");
                }
                return new MasterSatelliteInvariantResult(violations, description);
            }

            int expectedWindowCount = state.Satellites.Count + 1;
            if (windowNodes.Count != expectedWindowCount)
            {
                violations.Add($"The tree has {windowNodes.Count} windows; runtime state expects {expectedWindowCount}.");
            }

            var matchingMasterNodes = windowNodes
                .Where(node => WindowEquals(node.WindowReference, state.Master))
                .ToList();
            if (matchingMasterNodes.Count != 1)
            {
                violations.Add("The tree must contain exactly one node for the runtime master.");
            }

            if (state.Satellites.Count == 0)
            {
                if (root.Children.Count != 1
                    || root.Children[0] is not WindowNode onlyMaster
                    || !WindowEquals(onlyMaster.WindowReference, state.Master))
                {
                    violations.Add("A master-only tree must contain exactly the master as the root's sole child.");
                }
                if (nodes.OfType<PanelNode>().Any(panel => !ReferenceEquals(panel, root)))
                {
                    violations.Add("A master-only tree cannot contain a nested panel.");
                }
                return new MasterSatelliteInvariantResult(violations, description);
            }

            if (root.Children.Count != 2)
            {
                violations.Add("A master-plus-satellites tree must have exactly two root children.");
                return new MasterSatelliteInvariantResult(violations, description);
            }

            int masterIndex = state.MasterSide == MasterSide.Left ? 0 : 1;
            int satelliteIndex = 1 - masterIndex;
            if (root.Children[masterIndex] is not WindowNode canonicalMaster
                || !WindowEquals(canonicalMaster.WindowReference, state.Master))
            {
                violations.Add("The master is not in the root slot required by MasterSide.");
            }
            if (root.Children[satelliteIndex] is not SplitPanelNode satellitePanel)
            {
                violations.Add("The non-master root child must be a SplitPanelNode satellite panel.");
                return new MasterSatelliteInvariantResult(violations, description);
            }
            if (satellitePanel.Orientation != ToPanelOrientation(state.SatelliteOrientation))
            {
                violations.Add("The satellite panel orientation does not match runtime state.");
            }
            if (satellitePanel.Children.Count != state.Satellites.Count)
            {
                violations.Add("The satellite panel child count does not match runtime state.");
            }
            if (satellitePanel.Children.Any(child => child is not WindowNode))
            {
                violations.Add("Every satellite panel child must be a direct WindowNode.");
            }

            var extraPanels = nodes.OfType<PanelNode>()
                .Where(panel => !ReferenceEquals(panel, root) && !ReferenceEquals(panel, satellitePanel))
                .ToList();
            if (extraPanels.Count > 0)
            {
                violations.Add("Nested panels are not allowed below the canonical satellite panel.");
            }

            int comparableCount = Math.Min(satellitePanel.Children.Count, state.Satellites.Count);
            for (int i = 0; i < comparableCount; i++)
            {
                if (satellitePanel.Children[i] is not WindowNode satelliteNode
                    || !WindowEquals(satelliteNode.WindowReference, state.Satellites[i]))
                {
                    violations.Add($"Satellite slot {i} does not match runtime visual order.");
                }
            }

            var stateWindows = state.Satellites.Prepend(state.Master).ToList();
            foreach (var treeWindow in windowNodes.Select(node => node.WindowReference))
            {
                if (!stateWindows.Any(stateWindow => WindowEquals(stateWindow, treeWindow)))
                {
                    violations.Add("The tree contains a window that is not tracked by runtime state.");
                    break;
                }
            }

            return new MasterSatelliteInvariantResult(violations, description);
        }

        public MasterSatelliteOperationResult Normalize(
            DesktopTree tree,
            MasterSatelliteRuntimeState state,
            MasterSatelliteLayoutSettings settings)
        {
            ArgumentNullException.ThrowIfNull(tree);
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(settings);

            var before = CreateSnapshot(tree, state);
            var beforeInvariant = ValidateInvariant(tree, state, settings);
            if (!state.IsActive)
            {
                return Failure(tree, state, settings, MasterSatelliteFailureReason.Inactive,
                    "Master + Satellites is not active for this desktop and display.");
            }

            try
            {
                var visualWindows = tree.Root?.Windows
                    .Select(node => node.WindowReference)
                    .Distinct()
                    .ToList() ?? [];

                var orderedWindows = new List<IWindow>();
                if (state.Master != null && visualWindows.Any(window => WindowEquals(window, state.Master)))
                {
                    orderedWindows.Add(state.Master);
                }
                else if (visualWindows.Count > 0)
                {
                    orderedWindows.Add(visualWindows[0]);
                }

                foreach (var satellite in state.Satellites)
                {
                    if (visualWindows.Any(window => WindowEquals(window, satellite))
                        && !orderedWindows.Any(window => WindowEquals(window, satellite)))
                    {
                        orderedWindows.Add(satellite);
                    }
                }
                foreach (var window in visualWindows)
                {
                    if (!orderedWindows.Any(existing => WindowEquals(existing, window)))
                    {
                        orderedWindows.Add(window);
                    }
                }

                if (orderedWindows.Count > settings.MaxSatellites + 1)
                {
                    return DisableAfterRecoveryFailure(
                        tree,
                        state,
                        settings,
                        before,
                        $"Recovery found {orderedWindows.Count} unique windows, exceeding capacity {settings.MaxSatellites + 1}.");
                }

                var recoveredState = state.Clone();
                recoveredState.IsRecovering = true;
                recoveredState.Revision = state.Revision + 1;
                var plan = new DesktopTree { WorkArea = tree.WorkArea };
                RebuildCanonicalTree(plan, recoveredState, orderedWindows, tree.Root);
                FinalizeLayout(plan, recoveredState, settings, rebalanceSatellites: true);
                recoveredState.IsRecovering = false;
                var planInvariant = ValidateInvariant(plan, recoveredState, settings);
                if (!planInvariant.IsValid)
                {
                    return DisableAfterRecoveryFailure(
                        tree,
                        state,
                        settings,
                        before,
                        $"Recovery produced an invalid canonical tree: {planInvariant.Description}");
                }

                if (beforeInvariant.IsValid
                    && HasEquivalentCanonicalAllocation(
                        tree,
                        state,
                        plan,
                        recoveredState))
                {
                    return MasterSatelliteOperationResult.Success(
                        false,
                        before,
                        before,
                        beforeInvariant,
                        "The canonical layout is already balanced.");
                }

                tree.Root = plan.Root;
                state.CopyFrom(recoveredState);
                var after = CreateSnapshot(tree, state);
                var invariant = ValidateInvariant(tree, state, settings);
                AssertInvariant(invariant);
                m_diagnosticLog?.Invoke($"Master + Satellites recovery before:{Environment.NewLine}{before.TreeDescription}{Environment.NewLine}after:{Environment.NewLine}{after.TreeDescription}");
                return MasterSatelliteOperationResult.Success(true, before, after, invariant, "The canonical layout was recovered.");
            }
            catch (Exception e) when (IsLayoutFailure(e))
            {
                return DisableAfterRecoveryFailure(tree, state, settings, before,
                    $"Recovery could not satisfy the current work area and window constraints: {e.Message}");
            }
            catch (Exception e)
            {
                m_diagnosticLog?.Invoke($"Unexpected Master + Satellites recovery failure: {e}");
                return DisableAfterRecoveryFailure(tree, state, settings, before,
                    "Recovery failed unexpectedly; algorithmic mode was disabled for this desktop and display.");
            }
        }

        private static bool HasEquivalentCanonicalAllocation(
            DesktopTree currentTree,
            MasterSatelliteRuntimeState currentState,
            DesktopTree candidateTree,
            MasterSatelliteRuntimeState candidateState)
        {
            if (!currentTree.WorkArea.Equals(candidateTree.WorkArea)
                || currentState.IsActive != candidateState.IsActive
                || currentState.MasterSide != candidateState.MasterSide
                || !ApproximatelyEqual(
                    currentState.RequestedMasterRatio,
                    candidateState.RequestedMasterRatio)
                || !ApproximatelyEqual(
                    currentState.EffectiveMasterRatio,
                    candidateState.EffectiveMasterRatio)
                || currentState.SatelliteOrientation
                    != candidateState.SatelliteOrientation
                || !WindowEquals(currentState.Master, candidateState.Master)
                || currentState.Satellites.Count
                    != candidateState.Satellites.Count
                || currentState.IsRecovering != candidateState.IsRecovering)
            {
                return false;
            }

            for (int i = 0; i < currentState.Satellites.Count; i++)
            {
                if (!WindowEquals(
                    currentState.Satellites[i],
                    candidateState.Satellites[i]))
                {
                    return false;
                }
            }

            if (currentTree.Root == null || candidateTree.Root == null)
            {
                return currentTree.Root == candidateTree.Root;
            }

            currentTree.Measure();
            currentTree.Arrange();
            return HaveEquivalentAllocation(
                currentTree.Root,
                candidateTree.Root);
        }

        private static bool HaveEquivalentAllocation(
            TilingNode current,
            TilingNode candidate)
        {
            if (current.Type != candidate.Type
                || !current.Padding.Equals(candidate.Padding)
                || !current.ComputedRectangle.Equals(candidate.ComputedRectangle))
            {
                return false;
            }

            if (current is WindowNode currentWindow
                && candidate is WindowNode candidateWindow)
            {
                return WindowEquals(
                    currentWindow.WindowReference,
                    candidateWindow.WindowReference);
            }

            if (current is not SplitPanelNode currentPanel
                || candidate is not SplitPanelNode candidatePanel
                || currentPanel.Orientation != candidatePanel.Orientation
                || currentPanel.Spacing != candidatePanel.Spacing
                || currentPanel.Children.Count != candidatePanel.Children.Count
                || !ApproximatelyEqual(
                    currentPanel.ContainerLength,
                    candidatePanel.ContainerLength))
            {
                return false;
            }

            for (int i = 0; i < currentPanel.Children.Count; i++)
            {
                var currentChild = currentPanel.Children[i];
                var candidateChild = candidatePanel.Children[i];
                var currentConstraints = currentPanel.GetChildConstraints(currentChild);
                var candidateConstraints = candidatePanel.GetChildConstraints(candidateChild);
                if (!ApproximatelyEqual(
                        currentConstraints.Width,
                        candidateConstraints.Width)
                    || !ApproximatelyEqual(
                        currentConstraints.MinWidth,
                        candidateConstraints.MinWidth)
                    || !ApproximatelyEqual(
                        currentConstraints.MaxWidth,
                        candidateConstraints.MaxWidth)
                    || !HaveEquivalentAllocation(currentChild, candidateChild))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool ApproximatelyEqual(double left, double right)
        {
            return Math.Abs(left - right) <= 0.000001;
        }

        private MasterSatelliteOperationResult ExecuteMutation(
            DesktopTree tree,
            MasterSatelliteRuntimeState state,
            MasterSatelliteLayoutSettings settings,
            Func<DesktopTree, MasterSatelliteRuntimeState, bool> mutation,
            bool requireValidCurrentTree = true,
            bool rebalanceSatellites = false)
        {
            ArgumentNullException.ThrowIfNull(tree);
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(mutation);

            var before = CreateSnapshot(tree, state);
            if (!state.IsActive)
            {
                return Failure(tree, state, settings, MasterSatelliteFailureReason.Inactive,
                    "Master + Satellites is not active for this desktop and display.");
            }

            var currentInvariant = ValidateInvariant(tree, state, settings);
            if (requireValidCurrentTree && !currentInvariant.IsValid)
            {
#if DEBUG
                Debug.Assert(false, $"{currentInvariant.Description}{Environment.NewLine}{currentInvariant.TreeDescription}");
                return MasterSatelliteOperationResult.Failure(
                    MasterSatelliteFailureReason.InvalidCanonicalTree,
                    currentInvariant.Description,
                    before,
                    currentInvariant);
#else
                var recovery = Normalize(tree, state, settings);
                if (!recovery.Succeeded)
                {
                    return recovery;
                }
                before = CreateSnapshot(tree, state);
                currentInvariant = ValidateInvariant(tree, state, settings);
#endif
            }

            try
            {
                var candidateTree = CloneTree(tree);
                var candidateState = state.Clone();
                bool candidateChanged = mutation(candidateTree, candidateState);
                if (!candidateChanged)
                {
                    return MasterSatelliteOperationResult.Success(false, before, before, currentInvariant);
                }

                candidateState.Revision = state.Revision + 1;
                FinalizeLayout(candidateTree, candidateState, settings, rebalanceSatellites);
                var candidateInvariant = ValidateInvariant(candidateTree, candidateState, settings);
                if (!candidateInvariant.IsValid)
                {
                    throw new EngineFailureException(
                        MasterSatelliteFailureReason.InvalidCanonicalTree,
                        candidateInvariant.Description);
                }
            }
            catch (EngineFailureException e)
            {
                return MasterSatelliteOperationResult.Failure(e.Reason, e.Message, before, currentInvariant);
            }
            catch (Exception e) when (IsLayoutFailure(e))
            {
                return MasterSatelliteOperationResult.Failure(
                    MasterSatelliteFailureReason.MinSizeConflict,
                    $"The operation cannot satisfy the current work area and window minimum sizes: {e.Message}",
                    before,
                    currentInvariant);
            }
            catch (Exception e)
            {
                m_diagnosticLog?.Invoke($"Unexpected Master + Satellites preflight failure: {e}");
                return MasterSatelliteOperationResult.Failure(
                    MasterSatelliteFailureReason.UnexpectedFailure,
                    "The operation failed during preflight without changing the live layout.",
                    before,
                    currentInvariant);
            }

            PanelNode? rollbackRoot = tree.Root == null ? null : (PanelNode)tree.Root.Clone();
            var rollbackState = state.Clone();
            try
            {
                var committedState = state.Clone();
                bool changed = mutation(tree, committedState);
                if (!changed)
                {
                    throw new InvalidOperationException("A mutation changed during commit after a successful preflight.");
                }

                committedState.Revision = state.Revision + 1;
                FinalizeLayout(tree, committedState, settings, rebalanceSatellites);
                var invariant = ValidateInvariant(tree, committedState, settings);
                if (!invariant.IsValid)
                {
                    throw new EngineFailureException(
                        MasterSatelliteFailureReason.InvalidCanonicalTree,
                        invariant.Description);
                }

                state.CopyFrom(committedState);
                AssertInvariant(invariant);
                var after = CreateSnapshot(tree, state);
                return MasterSatelliteOperationResult.Success(true, before, after, invariant);
            }
            catch (Exception e)
            {
                tree.Root = rollbackRoot;
                state.CopyFrom(rollbackState);
                var restoredInvariant = ValidateInvariant(tree, state, settings);
                m_diagnosticLog?.Invoke($"Master + Satellites commit was rolled back: {e}");
                var reason = e is EngineFailureException engineFailure
                    ? engineFailure.Reason
                    : IsLayoutFailure(e)
                        ? MasterSatelliteFailureReason.MinSizeConflict
                        : MasterSatelliteFailureReason.UnexpectedFailure;
                return MasterSatelliteOperationResult.Failure(
                    reason,
                    "The operation failed during commit and the original layout was restored.",
                    CreateSnapshot(tree, state),
                    restoredInvariant);
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

        private static bool RebuildCanonicalTree(
            DesktopTree tree,
            MasterSatelliteRuntimeState state,
            IReadOnlyList<IWindow> orderedWindows,
            PanelNode? styleAndNodeSource)
        {
            var sourceRoot = styleAndNodeSource;
            var root = new SplitPanelNode
            {
                Orientation = PanelOrientation.Horizontal,
                Padding = sourceRoot?.Padding ?? new Rectangle(),
                Spacing = sourceRoot?.Spacing ?? 0,
            };
            tree.Root = root;

            state.Master = orderedWindows.Count == 0 ? null : orderedWindows[0];
            state.MutableSatellites.Clear();
            state.MutableSatellites.AddRange(orderedWindows.Skip(1));

            if (state.Master == null)
            {
                return true;
            }

            var masterNode = CloneOrCreateWindowNode(sourceRoot, state.Master);
            root.Attach(masterNode);
            if (state.MutableSatellites.Count == 0)
            {
                return true;
            }

            var previousSatellitePanel = FindLikelySatellitePanel(sourceRoot, state.MasterSide);
            var satellitePanel = new SplitPanelNode
            {
                Orientation = ToPanelOrientation(state.SatelliteOrientation),
                Padding = previousSatellitePanel?.Padding ?? sourceRoot?.Padding ?? new Rectangle(),
                Spacing = previousSatellitePanel?.Spacing ?? sourceRoot?.Spacing ?? 0,
            };
            root.Attach(state.MasterSide == MasterSide.Left ? 1 : 0, satellitePanel);
            foreach (var satellite in state.MutableSatellites)
            {
                satellitePanel.Attach(CloneOrCreateWindowNode(sourceRoot, satellite));
            }
            return true;
        }

        private static WindowNode CloneOrCreateWindowNode(PanelNode? sourceRoot, IWindow window)
        {
            var existing = sourceRoot?.Windows.FirstOrDefault(node => WindowEquals(node.WindowReference, window));
            return existing == null ? new WindowNode(window) : (WindowNode)existing.Clone();
        }

        private static SplitPanelNode? FindLikelySatellitePanel(PanelNode? sourceRoot, MasterSide side)
        {
            if (sourceRoot is not SplitPanelNode split || split.Children.Count != 2)
            {
                return null;
            }
            int expectedIndex = side == MasterSide.Left ? 1 : 0;
            return split.Children[expectedIndex] as SplitPanelNode;
        }

        private static void ApplyPlacement(
            DesktopTree tree,
            MasterSatelliteRuntimeState state,
            IWindow window,
            int? satelliteIndex)
        {
            if (tree.Root is not SplitPanelNode root)
            {
                throw new EngineFailureException(
                    MasterSatelliteFailureReason.InvalidCanonicalTree,
                    "The canonical root is missing.");
            }

            if (state.Master == null)
            {
                root.Attach(new WindowNode(window));
                state.Master = window;
                return;
            }

            SplitPanelNode satellitePanel;
            if (state.Satellites.Count == 0)
            {
                satellitePanel = new SplitPanelNode
                {
                    Orientation = ToPanelOrientation(state.SatelliteOrientation),
                    Padding = root.Padding,
                    Spacing = root.Spacing,
                };
                root.Attach(state.MasterSide == MasterSide.Left ? 1 : 0, satellitePanel);
            }
            else
            {
                (_, _, satellitePanel) = GetCanonicalNodes(tree, state);
            }

            int index = satelliteIndex ?? state.Satellites.Count;
            satellitePanel.Attach(index, new WindowNode(window));
            state.MutableSatellites.Insert(index, window);
        }

        private static void FinalizeLayout(
            DesktopTree tree,
            MasterSatelliteRuntimeState state,
            MasterSatelliteLayoutSettings settings,
            bool rebalanceSatellites = false)
        {
            if (tree.Root == null)
            {
                throw new EngineFailureException(
                    MasterSatelliteFailureReason.InvalidCanonicalTree,
                    "The canonical root is null.");
            }
            if (tree.WorkArea.Width <= 0 || tree.WorkArea.Height <= 0)
            {
                throw new EngineFailureException(
                    MasterSatelliteFailureReason.MinSizeConflict,
                    $"The work area must have a positive size, but is {tree.WorkArea}.");
            }

            tree.Measure();
            tree.Arrange();

            if (state.Master == null)
            {
                state.EffectiveMasterRatio = 0;
                return;
            }
            if (state.Satellites.Count == 0)
            {
                state.EffectiveMasterRatio = 1;
                return;
            }

            var (root, masterNode, satellitePanel) = GetCanonicalNodes(tree, state);
            if (rebalanceSatellites)
            {
                satellitePanel.DistributeChildrenEvenly();
                tree.Arrange();
            }
            double usefulWidth = root.ContainerLength;
            var masterConstraints = root.GetChildConstraints(masterNode);
            var satelliteConstraints = root.GetChildConstraints(satellitePanel);
            double minimumMasterWidth = Math.Max(
                masterConstraints.MinWidth,
                usefulWidth - satelliteConstraints.MaxWidth);
            double maximumMasterWidth = Math.Min(
                masterConstraints.MaxWidth,
                usefulWidth - satelliteConstraints.MinWidth);
            if (minimumMasterWidth > maximumMasterWidth || usefulWidth < 1)
            {
                throw new EngineFailureException(
                    MasterSatelliteFailureReason.MinSizeConflict,
                    $"No master width satisfies [{minimumMasterWidth}, {maximumMasterWidth}] within {usefulWidth} useful pixels.");
            }

            double clampedRatio = Math.Clamp(
                state.RequestedMasterRatio,
                minimumMasterWidth / usefulWidth,
                maximumMasterWidth / usefulWidth);
            double minimumRoundedWidth = Math.Ceiling(minimumMasterWidth);
            double maximumRoundedWidth = Math.Floor(maximumMasterWidth);
            if (minimumRoundedWidth > maximumRoundedWidth)
            {
                throw new EngineFailureException(
                    MasterSatelliteFailureReason.MinSizeConflict,
                    "The feasible master-width interval contains no whole pixel.");
            }

            double targetWidth = Math.Round(usefulWidth * clampedRatio, MidpointRounding.AwayFromZero);
            targetWidth = Math.Clamp(targetWidth, minimumRoundedWidth, maximumRoundedWidth);
            if (!root.ResizeTo(masterNode, targetWidth, GrowDirection.Both))
            {
                throw new EngineFailureException(
                    MasterSatelliteFailureReason.MinSizeConflict,
                    "The root Flex container rejected the requested master width.");
            }

            tree.Arrange();
            state.EffectiveMasterRatio = root.GetChildConstraints(masterNode).Width / root.ContainerLength;
        }

        private MasterSatelliteOperationResult DisableAfterRecoveryFailure(
            DesktopTree tree,
            MasterSatelliteRuntimeState state,
            MasterSatelliteLayoutSettings settings,
            MasterSatelliteLayoutSnapshot before,
            string message)
        {
            state.IsActive = false;
            state.IsRecovering = false;
            state.Revision++;
            var after = CreateSnapshot(tree, state);
            var invariant = ValidateInvariant(tree, state, settings);
            m_diagnosticLog?.Invoke($"Master + Satellites recovery failed: {message}{Environment.NewLine}{before.TreeDescription}");
            return new MasterSatelliteOperationResult(
                false,
                true,
                MasterSatelliteFailureReason.RecoveryFailed,
                message,
                before,
                after,
                invariant);
        }

        private MasterSatelliteOperationResult NoOp(
            DesktopTree tree,
            MasterSatelliteRuntimeState state,
            MasterSatelliteLayoutSettings settings,
            string message)
        {
            var snapshot = CreateSnapshot(tree, state);
            var invariant = ValidateInvariant(tree, state, settings);
            if (!state.IsActive)
            {
                return MasterSatelliteOperationResult.Failure(
                    MasterSatelliteFailureReason.Inactive,
                    "Master + Satellites is not active for this desktop and display.",
                    snapshot,
                    invariant);
            }
            if (!invariant.IsValid)
            {
                return MasterSatelliteOperationResult.Failure(
                    MasterSatelliteFailureReason.InvalidCanonicalTree,
                    invariant.Description,
                    snapshot,
                    invariant);
            }
            return MasterSatelliteOperationResult.Success(false, snapshot, snapshot, invariant, message);
        }

        private MasterSatelliteOperationResult Failure(
            DesktopTree tree,
            MasterSatelliteRuntimeState state,
            MasterSatelliteLayoutSettings settings,
            MasterSatelliteFailureReason reason,
            string message)
        {
            var snapshot = CreateSnapshot(tree, state);
            return MasterSatelliteOperationResult.Failure(
                reason,
                message,
                snapshot,
                ValidateInvariant(tree, state, settings));
        }

        private static (SplitPanelNode Root, WindowNode Master, SplitPanelNode SatellitePanel) GetCanonicalNodes(
            DesktopTree tree,
            MasterSatelliteRuntimeState state,
            bool requireSatellite = true)
        {
            if (tree.Root is not SplitPanelNode root || state.Master == null)
            {
                throw new EngineFailureException(
                    MasterSatelliteFailureReason.InvalidCanonicalTree,
                    "The canonical root or master is missing.");
            }
            var masterNode = tree.FindNode(state.Master)
                ?? throw new EngineFailureException(
                    MasterSatelliteFailureReason.InvalidCanonicalTree,
                    "The runtime master is not registered in the tree.");

            SplitPanelNode? satellitePanel = null;
            if (state.Satellites.Count > 0)
            {
                int satellitePanelIndex = state.MasterSide == MasterSide.Left ? 1 : 0;
                if (root.Children.Count <= satellitePanelIndex
                    || root.Children[satellitePanelIndex] is not SplitPanelNode panel)
                {
                    throw new EngineFailureException(
                        MasterSatelliteFailureReason.InvalidCanonicalTree,
                        "The canonical satellite panel is missing.");
                }
                satellitePanel = panel;
            }
            else if (requireSatellite)
            {
                throw new EngineFailureException(
                    MasterSatelliteFailureReason.InvalidCanonicalTree,
                    "The operation requires at least one satellite.");
            }

            return (root, masterNode, satellitePanel!);
        }

        private static bool ContainsWindow(MasterSatelliteRuntimeState state, IWindow window)
        {
            return WindowEquals(state.Master, window)
                || state.Satellites.Any(satellite => WindowEquals(satellite, window));
        }

        private static int IndexOfWindow(IReadOnlyList<IWindow> windows, IWindow window)
        {
            for (int i = 0; i < windows.Count; i++)
            {
                if (WindowEquals(windows[i], window))
                {
                    return i;
                }
            }
            return -1;
        }

        private static bool WindowEquals(IWindow? left, IWindow? right)
        {
            return EqualityComparer<IWindow?>.Default.Equals(left, right);
        }

        private static PanelOrientation ToPanelOrientation(SatelliteLayoutOrientation orientation)
        {
            return orientation == SatelliteLayoutOrientation.Vertical
                ? PanelOrientation.Vertical
                : PanelOrientation.Horizontal;
        }

        private static bool IsLayoutFailure(Exception exception)
        {
            return exception is UnsatisfiableFlexConstraintsException
                || exception is EngineFailureException { Reason: MasterSatelliteFailureReason.MinSizeConflict };
        }

        [Conditional("DEBUG")]
        private static void AssertInvariant(MasterSatelliteInvariantResult invariant)
        {
            Debug.Assert(invariant.IsValid, $"{invariant.Description}{Environment.NewLine}{invariant.TreeDescription}");
        }

        private static string DescribeTree(PanelNode? root)
        {
            if (root == null)
            {
                return "<null root>";
            }

            var result = new StringBuilder();
            var visited = new HashSet<TilingNode>(ReferenceEqualityComparer.Instance);
            void append(TilingNode node, int depth)
            {
                result.Append(' ', depth * 2);
                result.Append(node.GetType().Name);
                result.Append('#');
                result.Append(node.GenerationID);
                if (node is SplitPanelNode split)
                {
                    result.Append('(');
                    result.Append(split.Orientation);
                    result.Append(')');
                }
                else if (node is WindowNode window)
                {
                    result.Append("[window@");
                    result.Append(RuntimeHelpers.GetHashCode(window.WindowReference));
                    result.Append(']');
                }
                result.AppendLine();

                if (!visited.Add(node))
                {
                    result.Append(' ', (depth + 1) * 2);
                    result.AppendLine("<cycle>");
                    return;
                }
                if (node is PanelNode panel)
                {
                    foreach (var child in panel.Children)
                    {
                        append(child, depth + 1);
                    }
                }
            }

            append(root, 0);
            return result.ToString().TrimEnd();
        }

        private sealed class EngineFailureException : Exception
        {
            public MasterSatelliteFailureReason Reason { get; }

            public EngineFailureException(MasterSatelliteFailureReason reason, string message)
                : base(message)
            {
                Reason = reason;
            }
        }
    }
}
