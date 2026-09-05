using System;

using FancyWM.Layouts.Tiling;

using WinMan;

namespace FancyWM.AlgorithmicLayouts
{
    internal enum MasterSatelliteCommandDisposition
    {
        Enabled,
        Disabled,
        Applied,
        Rejected,
    }

    internal sealed record class MasterSatelliteCommandResult(
        MasterSatelliteCommandDisposition Disposition,
        bool Succeeded,
        bool Changed,
        MasterSatelliteFailureReason FailureReason,
        string? Message,
        MasterSatelliteOperationResult? Operation,
        MasterSatelliteInvariantResult? Invariant)
    {
        public bool IsActive => Disposition != MasterSatelliteCommandDisposition.Disabled
            && Succeeded;
    }

    /// <summary>
    /// Headless command boundary. The owning service serializes calls with its
    /// existing backend lock and is responsible for UI notifications.
    /// </summary>
    internal sealed class MasterSatelliteCommandController
    {
        private readonly MasterSatelliteRuntimeLifecycle m_lifecycle;
        private readonly Func<IVirtualDesktop, bool> m_isMutationBlocked;

        public MasterSatelliteCommandController(
            MasterSatelliteRuntimeLifecycle lifecycle,
            Func<IVirtualDesktop, bool>? isMutationBlocked = null)
        {
            m_lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
            m_isMutationBlocked = isMutationBlocked ?? (_ => false);
        }

        public bool IsActive(IVirtualDesktop desktop)
        {
            ArgumentNullException.ThrowIfNull(desktop);
            return m_lifecycle.TryGetState(desktop, out var state) && state.IsActive;
        }

        public MasterSatelliteCommandResult RejectTransitionInProgress()
        {
            return TransitionInProgress();
        }

        public bool CanPromoteFocusedWindow(TilingWorkspace backend, IVirtualDesktop desktop)
        {
            ArgumentNullException.ThrowIfNull(backend);
            ArgumentNullException.ThrowIfNull(desktop);
            if (m_isMutationBlocked(desktop)
                || !m_lifecycle.TryGetState(desktop, out var state)
                || backend.GetFocus(desktop) is not WindowNode focused)
            {
                return false;
            }
            return IndexOfWindow(state.Satellites, focused.WindowReference) >= 0;
        }

        public MasterSatelliteCommandResult Toggle(
            TilingWorkspace backend,
            IVirtualDesktop desktop,
            IDisplay primaryDisplay)
        {
            ArgumentNullException.ThrowIfNull(backend);
            ArgumentNullException.ThrowIfNull(desktop);
            ArgumentNullException.ThrowIfNull(primaryDisplay);

            if (m_isMutationBlocked(desktop))
            {
                return TransitionInProgress();
            }

            if (m_lifecycle.TryGetState(desktop, out _))
            {
                bool removed = m_lifecycle.DeactivateDesktop(desktop);
                return new MasterSatelliteCommandResult(
                    MasterSatelliteCommandDisposition.Disabled,
                    removed,
                    removed,
                    removed ? MasterSatelliteFailureReason.None : MasterSatelliteFailureReason.Inactive,
                    removed ? "Master + Satellites was disabled for this desktop and display." : "The layout was not active.",
                    null,
                    null);
            }

            if (!m_lifecycle.IsEligible(primaryDisplay))
            {
                return Rejected(
                    MasterSatelliteFailureReason.Inactive,
                    "Enable Master + Satellites in settings for this display before using the layout command.");
            }

            var activation = m_lifecycle.CurrentDesktopChanged(backend, desktop, primaryDisplay);
            if (!activation.StatePresent || activation.Operation is { Succeeded: false })
            {
                return Rejected(
                    activation.Operation?.FailureReason ?? MasterSatelliteFailureReason.UnexpectedFailure,
                    activation.Operation?.Message ?? "The layout could not be enabled.",
                    activation.Operation,
                    activation.Invariant);
            }

            return new MasterSatelliteCommandResult(
                MasterSatelliteCommandDisposition.Enabled,
                true,
                activation.StateAdded || activation.Operation?.Changed == true,
                MasterSatelliteFailureReason.None,
                "Master + Satellites was enabled for this desktop and display.",
                activation.Operation,
                activation.Invariant);
        }

        public MasterSatelliteCommandResult PromoteFocusedWindow(
            TilingWorkspace backend,
            IVirtualDesktop desktop)
        {
            if (!TryGetActiveState(desktop, out var state, out var failure))
            {
                return failure!;
            }
            if (backend.GetFocus(desktop) is not WindowNode focused)
            {
                return Rejected(
                    MasterSatelliteFailureReason.WindowNotFound,
                    "There is no focused tiled window to promote.");
            }
            return FromOperation(backend.PromoteMasterSatelliteWindow(
                desktop,
                state!,
                m_lifecycle.SettingsSnapshot,
                focused.WindowReference));
        }

        public MasterSatelliteCommandResult SwapMasterSide(
            TilingWorkspace backend,
            IVirtualDesktop desktop)
        {
            if (!TryGetActiveState(desktop, out var state, out var failure))
            {
                return failure!;
            }
            return FromOperation(backend.SwapMasterSatelliteSide(
                desktop,
                state!,
                m_lifecycle.SettingsSnapshot));
        }

        public MasterSatelliteCommandResult ToggleSatelliteOrientation(
            TilingWorkspace backend,
            IVirtualDesktop desktop)
        {
            if (!TryGetActiveState(desktop, out var state, out var failure))
            {
                return failure!;
            }
            var orientation = state!.SatelliteOrientation == Models.SatelliteLayoutOrientation.Vertical
                ? Models.SatelliteLayoutOrientation.Horizontal
                : Models.SatelliteLayoutOrientation.Vertical;
            return FromOperation(backend.SetMasterSatelliteOrientation(
                desktop,
                state,
                m_lifecycle.SettingsSnapshot,
                orientation));
        }

        public MasterSatelliteCommandResult SetSatelliteOrientation(
            TilingWorkspace backend,
            IVirtualDesktop desktop,
            Models.SatelliteLayoutOrientation orientation)
        {
            ArgumentNullException.ThrowIfNull(backend);
            if (!Enum.IsDefined(orientation))
            {
                throw new ArgumentOutOfRangeException(nameof(orientation));
            }
            if (!TryGetActiveState(desktop, out var state, out var failure))
            {
                return failure!;
            }
            return FromOperation(backend.SetMasterSatelliteOrientation(
                desktop,
                state!,
                m_lifecycle.SettingsSnapshot,
                orientation));
        }

        /// <summary>
        /// Handles an overlay split request without allowing a structural panel
        /// to enter the generic nesting path while the canonical layout is active.
        /// Window requests retain their documented meaning of changing the
        /// satellite orientation.
        /// </summary>
        public MasterSatelliteCommandResult SetSatelliteOrientationFromNode(
            TilingWorkspace backend,
            IVirtualDesktop desktop,
            TilingNode node,
            Models.SatelliteLayoutOrientation orientation)
        {
            ArgumentNullException.ThrowIfNull(backend);
            ArgumentNullException.ThrowIfNull(node);
            if (!Enum.IsDefined(orientation))
            {
                throw new ArgumentOutOfRangeException(nameof(orientation));
            }
            if (!TryGetActiveState(desktop, out var state, out var failure))
            {
                return failure!;
            }
            if (!ReferenceEquals(node.Desktop, backend.GetTree(desktop)))
            {
                return Rejected(
                    MasterSatelliteFailureReason.WindowNotFound,
                    "The selected overlay node is no longer part of this desktop.");
            }
            if (node is not WindowNode window)
            {
                return Rejected(
                    MasterSatelliteFailureReason.UnsupportedOperation,
                    "Canonical panels cannot be split or nested while Master + Satellites is active.");
            }

            backend.SetFocus(window);
            return FromOperation(backend.SetMasterSatelliteOrientation(
                desktop,
                state!,
                m_lifecycle.SettingsSnapshot,
                orientation));
        }

        /// <summary>
        /// Maps a window-tab pull-up request to canonical promotion and rejects
        /// structural panel pull-up instead of invoking generic tree mutation.
        /// </summary>
        public MasterSatelliteCommandResult PullUpNode(
            TilingWorkspace backend,
            IVirtualDesktop desktop,
            TilingNode node)
        {
            ArgumentNullException.ThrowIfNull(backend);
            ArgumentNullException.ThrowIfNull(node);
            if (!TryGetActiveState(desktop, out var state, out var failure))
            {
                return failure!;
            }
            if (!ReferenceEquals(node.Desktop, backend.GetTree(desktop)))
            {
                return Rejected(
                    MasterSatelliteFailureReason.WindowNotFound,
                    "The selected overlay node is no longer part of this desktop.");
            }
            if (node is not WindowNode window)
            {
                return Rejected(
                    MasterSatelliteFailureReason.UnsupportedOperation,
                    "Canonical panels cannot be pulled up while Master + Satellites is active.");
            }

            backend.SetFocus(window);
            return FromOperation(backend.PromoteMasterSatelliteWindow(
                desktop,
                state!,
                m_lifecycle.SettingsSnapshot,
                window.WindowReference));
        }

        public MasterSatelliteCommandResult RejectStackPanel(IVirtualDesktop desktop)
        {
            if (!TryGetActiveState(desktop, out _, out var failure))
            {
                return failure!;
            }
            return Rejected(
                MasterSatelliteFailureReason.UnsupportedOperation,
                "Stack panels are not supported while Master + Satellites is active.");
        }

        public MasterSatelliteCommandResult RejectLegacyGrouping(IVirtualDesktop desktop)
        {
            if (!TryGetActiveState(desktop, out _, out var failure))
            {
                return failure!;
            }
            return Rejected(
                MasterSatelliteFailureReason.UnsupportedOperation,
                "Legacy panel grouping cannot target a canonical Master + Satellites window.");
        }

        public bool CanMoveFocusedWindow(
            TilingWorkspace backend,
            IVirtualDesktop desktop,
            TilingDirection direction)
        {
            ArgumentNullException.ThrowIfNull(backend);
            if (m_isMutationBlocked(desktop)
                || !Enum.IsDefined(direction)
                || !m_lifecycle.TryGetState(desktop, out var state)
                || backend.GetFocus(desktop) is not WindowNode focused)
            {
                return false;
            }

            if (WindowsMatch(state.Master, focused.WindowReference))
            {
                return direction switch
                {
                    TilingDirection.Left => state.MasterSide != Models.MasterSide.Left,
                    TilingDirection.Right => state.MasterSide != Models.MasterSide.Right,
                    _ => false,
                };
            }

            int index = IndexOfWindow(state.Satellites, focused.WindowReference);
            if (index < 0)
            {
                return false;
            }
            bool previous = (state.SatelliteOrientation, direction) switch
            {
                (Models.SatelliteLayoutOrientation.Vertical, TilingDirection.Up) => true,
                (Models.SatelliteLayoutOrientation.Horizontal, TilingDirection.Left) => true,
                _ => false,
            };
            bool next = (state.SatelliteOrientation, direction) switch
            {
                (Models.SatelliteLayoutOrientation.Vertical, TilingDirection.Down) => true,
                (Models.SatelliteLayoutOrientation.Horizontal, TilingDirection.Right) => true,
                _ => false,
            };
            if (!previous && !next)
            {
                return false;
            }

            // Reordering reaches the master at the inner edge of a horizontal
            // satellite row. Crossing that real visual neighbour promotes the
            // focused satellite, preserving the old master's exact slot.
            var adjacent = focused.GetAdjacentWindow(direction);
            if (adjacent != null
                && WindowsMatch(state.Master, adjacent.WindowReference))
            {
                return true;
            }
            return previous
                ? index > 0
                : index < state.Satellites.Count - 1;
        }

        public MasterSatelliteCommandResult MoveFocusedWindow(
            TilingWorkspace backend,
            IVirtualDesktop desktop,
            TilingDirection direction)
        {
            ArgumentNullException.ThrowIfNull(backend);
            if (!Enum.IsDefined(direction))
            {
                return Rejected(MasterSatelliteFailureReason.InvalidArgument, "The move direction is invalid.");
            }
            if (!TryGetActiveState(desktop, out var state, out var failure))
            {
                return failure!;
            }
            if (backend.GetFocus(desktop) is not WindowNode focused)
            {
                return Rejected(
                    MasterSatelliteFailureReason.WindowNotFound,
                    "There is no focused tiled window to move.");
            }

            if (WindowsMatch(state!.Master, focused.WindowReference))
            {
                var side = direction switch
                {
                    TilingDirection.Left => Models.MasterSide.Left,
                    TilingDirection.Right => Models.MasterSide.Right,
                    _ => (Models.MasterSide?)null,
                };
                if (side == null)
                {
                    return Rejected(
                        MasterSatelliteFailureReason.UnsupportedOperation,
                        "The master can only be moved left or right.");
                }
                return FromOperation(backend.SetMasterSatelliteSide(
                    desktop,
                    state,
                    m_lifecycle.SettingsSnapshot,
                    side.Value));
            }

            int index = IndexOfWindow(state.Satellites, focused.WindowReference);
            if (index < 0)
            {
                return Rejected(
                    MasterSatelliteFailureReason.WindowNotFound,
                    "The focused window is not part of the canonical layout.");
            }
            bool previous = (state.SatelliteOrientation, direction) switch
            {
                (Models.SatelliteLayoutOrientation.Vertical, TilingDirection.Up) => true,
                (Models.SatelliteLayoutOrientation.Horizontal, TilingDirection.Left) => true,
                _ => false,
            };
            bool next = (state.SatelliteOrientation, direction) switch
            {
                (Models.SatelliteLayoutOrientation.Vertical, TilingDirection.Down) => true,
                (Models.SatelliteLayoutOrientation.Horizontal, TilingDirection.Right) => true,
                _ => false,
            };
            if (!previous && !next)
            {
                return Rejected(
                    MasterSatelliteFailureReason.UnsupportedOperation,
                    "Satellites can only be moved along the active satellite axis.");
            }
            var adjacent = focused.GetAdjacentWindow(direction);
            if (adjacent != null
                && WindowsMatch(state.Master, adjacent.WindowReference))
            {
                return FromOperation(backend.PromoteMasterSatelliteWindow(
                    desktop,
                    state,
                    m_lifecycle.SettingsSnapshot,
                    focused.WindowReference));
            }
            return FromOperation(previous
                ? backend.MoveMasterSatelliteWindowPrevious(
                    desktop,
                    state,
                    m_lifecycle.SettingsSnapshot,
                    focused.WindowReference)
                : backend.MoveMasterSatelliteWindowNext(
                    desktop,
                    state,
                    m_lifecycle.SettingsSnapshot,
                    focused.WindowReference));
        }

        public bool CanSwapFocusedWindow(
            TilingWorkspace backend,
            IVirtualDesktop desktop,
            TilingDirection direction)
        {
            ArgumentNullException.ThrowIfNull(backend);
            if (m_isMutationBlocked(desktop)
                || !Enum.IsDefined(direction)
                || !m_lifecycle.TryGetState(desktop, out var state)
                || backend.GetFocus(desktop) is not WindowNode focused)
            {
                return false;
            }
            var adjacent = focused.GetAdjacentWindow(direction);
            if (adjacent == null)
            {
                return false;
            }
            bool focusedMaster = WindowsMatch(state.Master, focused.WindowReference);
            bool adjacentMaster = WindowsMatch(state.Master, adjacent.WindowReference);
            int focusedIndex = IndexOfWindow(state.Satellites, focused.WindowReference);
            int adjacentIndex = IndexOfWindow(state.Satellites, adjacent.WindowReference);
            return (focusedMaster && adjacentIndex >= 0)
                || (adjacentMaster && focusedIndex >= 0)
                || (focusedIndex >= 0 && adjacentIndex >= 0 && focusedIndex != adjacentIndex);
        }

        public MasterSatelliteCommandResult SwapFocusedWindow(
            TilingWorkspace backend,
            IVirtualDesktop desktop,
            TilingDirection direction)
        {
            ArgumentNullException.ThrowIfNull(backend);
            if (!Enum.IsDefined(direction))
            {
                return Rejected(MasterSatelliteFailureReason.InvalidArgument, "The swap direction is invalid.");
            }
            if (!TryGetActiveState(desktop, out var state, out var failure))
            {
                return failure!;
            }
            if (backend.GetFocus(desktop) is not WindowNode focused)
            {
                return Rejected(
                    MasterSatelliteFailureReason.WindowNotFound,
                    "There is no focused tiled window to swap.");
            }
            var adjacent = focused.GetAdjacentWindow(direction);
            if (adjacent == null)
            {
                return Rejected(
                    MasterSatelliteFailureReason.InvalidArgument,
                    "There is no adjacent tiled window in that direction.");
            }

            bool focusedMaster = WindowsMatch(state!.Master, focused.WindowReference);
            bool adjacentMaster = WindowsMatch(state.Master, adjacent.WindowReference);
            int focusedIndex = IndexOfWindow(state.Satellites, focused.WindowReference);
            int adjacentIndex = IndexOfWindow(state.Satellites, adjacent.WindowReference);
            if (focusedMaster && adjacentIndex >= 0)
            {
                return FromOperation(backend.PromoteMasterSatelliteWindow(
                    desktop,
                    state,
                    m_lifecycle.SettingsSnapshot,
                    adjacent.WindowReference));
            }
            if (adjacentMaster && focusedIndex >= 0)
            {
                return FromOperation(backend.PromoteMasterSatelliteWindow(
                    desktop,
                    state,
                    m_lifecycle.SettingsSnapshot,
                    focused.WindowReference));
            }
            if (focusedIndex >= 0 && adjacentIndex >= 0 && focusedIndex != adjacentIndex)
            {
                return FromOperation(backend.ReorderMasterSatelliteWindow(
                    desktop,
                    state,
                    m_lifecycle.SettingsSnapshot,
                    focusedIndex,
                    adjacentIndex));
            }
            return Rejected(
                MasterSatelliteFailureReason.UnsupportedOperation,
                "Only canonical master and satellite windows can be swapped.");
        }

        public bool CanResizeFocusedMaster(
            TilingWorkspace backend,
            IVirtualDesktop desktop,
            PanelOrientation orientation,
            double displayPercentage)
        {
            ArgumentNullException.ThrowIfNull(backend);
            if (m_isMutationBlocked(desktop)
                || orientation != PanelOrientation.Horizontal
                || !double.IsFinite(displayPercentage)
                || displayPercentage == 0
                || !m_lifecycle.TryGetState(desktop, out var state)
                || state.Satellites.Count == 0
                || backend.GetFocus(desktop) is not WindowNode focused
                || !WindowsMatch(state.Master, focused.WindowReference))
            {
                return false;
            }
            double target = Math.Clamp(
                state.EffectiveMasterRatio + displayPercentage,
                Models.MasterSatelliteLayoutSettings.MinimumMasterRatio,
                Models.MasterSatelliteLayoutSettings.MaximumMasterRatio);
            return target != state.RequestedMasterRatio;
        }

        public MasterSatelliteCommandResult ResizeFocusedMaster(
            TilingWorkspace backend,
            IVirtualDesktop desktop,
            PanelOrientation orientation,
            double displayPercentage)
        {
            ArgumentNullException.ThrowIfNull(backend);
            if (orientation != PanelOrientation.Horizontal
                || !double.IsFinite(displayPercentage)
                || displayPercentage == 0)
            {
                return Rejected(
                    MasterSatelliteFailureReason.UnsupportedOperation,
                    "Only master width can be resized in Master + Satellites mode.");
            }
            if (!TryGetActiveState(desktop, out var state, out var failure))
            {
                return failure!;
            }
            if (backend.GetFocus(desktop) is not WindowNode focused)
            {
                return Rejected(
                    MasterSatelliteFailureReason.WindowNotFound,
                    "There is no focused tiled window to resize.");
            }
            if (!WindowsMatch(state!.Master, focused.WindowReference))
            {
                return Rejected(
                    MasterSatelliteFailureReason.UnsupportedOperation,
                    "Only the master width can change the master ratio.");
            }
            if (state.Satellites.Count == 0)
            {
                return Rejected(
                    MasterSatelliteFailureReason.UnsupportedOperation,
                    "A master ratio requires at least one satellite.");
            }
            return FromOperation(backend.SetMasterSatelliteRatio(
                desktop,
                state,
                m_lifecycle.SettingsSnapshot,
                state.EffectiveMasterRatio + displayPercentage));
        }

        /// <summary>
        /// Converts the native horizontal resize delta of a specific canonical
        /// master into the persisted runtime ratio. Satellite and vertical-only
        /// native resize gestures are rejected so they cannot mutate canonical
        /// Flex outside the engine transaction boundary.
        /// </summary>
        public MasterSatelliteCommandResult ResizeWindowByPixels(
            TilingWorkspace backend,
            IVirtualDesktop desktop,
            IWindow window,
            int widthDelta)
        {
            ArgumentNullException.ThrowIfNull(backend);
            ArgumentNullException.ThrowIfNull(window);
            if (!TryGetActiveState(desktop, out var state, out var failure))
            {
                return failure!;
            }
            if (!WindowsMatch(state!.Master, window))
            {
                return Rejected(
                    MasterSatelliteFailureReason.UnsupportedOperation,
                    "Only the master width can be resized in Master + Satellites mode.");
            }
            if (state.Satellites.Count == 0 || widthDelta == 0)
            {
                return Rejected(
                    MasterSatelliteFailureReason.UnsupportedOperation,
                    "A horizontal master/satellite split is required for resizing.");
            }

            var tree = backend.GetTree(desktop);
            if (tree?.Root is not SplitPanelNode root
                || !ReferenceEquals(tree.FindNode(window)?.Desktop, tree))
            {
                return Rejected(
                    MasterSatelliteFailureReason.WindowNotFound,
                    "The master is no longer attached to this desktop.");
            }
            tree.Measure();
            tree.Arrange();
            if (root.ContainerLength <= 0)
            {
                return Rejected(
                    MasterSatelliteFailureReason.MinSizeConflict,
                    "The layout has no usable width for resizing the master.");
            }

            return FromOperation(backend.SetMasterSatelliteRatio(
                desktop,
                state,
                m_lifecycle.SettingsSnapshot,
                state.EffectiveMasterRatio + widthDelta / (double)root.ContainerLength));
        }

        public MasterSatelliteCommandResult ResetMasterRatio(
            TilingWorkspace backend,
            IVirtualDesktop desktop)
        {
            if (!TryGetActiveState(desktop, out var state, out var failure))
            {
                return failure!;
            }
            return FromOperation(backend.ResetMasterSatelliteRatio(
                desktop,
                state!,
                m_lifecycle.SettingsSnapshot));
        }

        public MasterSatelliteCommandResult Rebalance(
            TilingWorkspace backend,
            IVirtualDesktop desktop)
        {
            if (!TryGetActiveState(desktop, out var state, out var failure))
            {
                return failure!;
            }
            return FromOperation(backend.RebalanceMasterSatelliteLayout(
                desktop,
                state!,
                m_lifecycle.SettingsSnapshot));
        }

        private bool TryGetActiveState(
            IVirtualDesktop desktop,
            out MasterSatelliteRuntimeState? state,
            out MasterSatelliteCommandResult? failure)
        {
            ArgumentNullException.ThrowIfNull(desktop);
            if (m_isMutationBlocked(desktop))
            {
                state = null;
                failure = TransitionInProgress();
                return false;
            }
            if (!m_lifecycle.TryGetState(desktop, out state!) || !state.IsActive)
            {
                state = null;
                failure = Rejected(
                    MasterSatelliteFailureReason.Inactive,
                    "Master + Satellites is not active for this desktop and display.");
                return false;
            }
            failure = null;
            return true;
        }

        private static MasterSatelliteCommandResult FromOperation(
            MasterSatelliteOperationResult operation)
        {
            ArgumentNullException.ThrowIfNull(operation);
            return operation.Succeeded
                ? new MasterSatelliteCommandResult(
                    MasterSatelliteCommandDisposition.Applied,
                    true,
                    operation.Changed,
                    MasterSatelliteFailureReason.None,
                    operation.Message,
                    operation,
                    operation.Invariant)
                : Rejected(
                    operation.FailureReason,
                    operation.Message ?? "The command was rejected.",
                    operation,
                    operation.Invariant);
        }

        private static MasterSatelliteCommandResult Rejected(
            MasterSatelliteFailureReason reason,
            string message,
            MasterSatelliteOperationResult? operation = null,
            MasterSatelliteInvariantResult? invariant = null)
        {
            return new MasterSatelliteCommandResult(
                MasterSatelliteCommandDisposition.Rejected,
                false,
                false,
                reason,
                message,
                operation,
                invariant);
        }

        private static MasterSatelliteCommandResult TransitionInProgress()
        {
            return Rejected(
                MasterSatelliteFailureReason.UnsupportedOperation,
                "Wait for the current Master + Satellites capacity transition to finish before changing this layout.");
        }

        private static int IndexOfWindow(
            System.Collections.Generic.IReadOnlyList<IWindow> windows,
            IWindow window)
        {
            for (int i = 0; i < windows.Count; i++)
            {
                if (ReferenceEquals(windows[i], window)
                    || System.Collections.Generic.EqualityComparer<IWindow>.Default.Equals(windows[i], window))
                {
                    return i;
                }
            }
            return -1;
        }

        private static bool WindowsMatch(IWindow? first, IWindow? second)
        {
            return first != null
                && second != null
                && (ReferenceEquals(first, second)
                    || System.Collections.Generic.EqualityComparer<IWindow>.Default.Equals(first, second));
        }
    }
}
