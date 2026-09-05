using System;

using FancyWM.AlgorithmicLayouts;

using WinMan;

namespace FancyWM
{
    internal sealed class AlgorithmicLayoutCommandException : TilingFailedException
    {
        public string UserHint { get; }

        public AlgorithmicLayoutCommandException(string userHint, TilingError reason)
            : base(userHint, reason)
        {
            UserHint = userHint;
        }
    }

    internal partial class TilingService
    {
        private readonly MasterSatelliteCommandController m_masterSatelliteCommands;

        public bool CanToggleMasterSatelliteLayout()
        {
            if (!TryGetCurrentMasterSatelliteDesktop(out var desktop)
                || !TryGetMasterSatellitePrimaryDisplay(out var primaryDisplay)
                || IsMasterSatelliteCapacityTransitionSource(desktop))
            {
                return false;
            }
            using (m_backendLock.EnterScope())
            {
                return m_masterSatelliteCommands.IsActive(desktop)
                    || m_masterSatelliteLifecycle.IsEligible(primaryDisplay);
            }
        }

        public void ToggleMasterSatelliteLayout()
        {
            MasterSatelliteCommandResult result;
            var desktop = GetRequiredMasterSatelliteCommandDesktop();
            var primaryDisplay = GetRequiredMasterSatellitePrimaryDisplay();
            using (m_backendLock.EnterScope())
            {
                result = m_masterSatelliteCommands.Toggle(
                    m_backend,
                    desktop,
                    primaryDisplay);
            }
            CompleteMasterSatelliteCommand("ToggleLayout", desktop, result);
        }

        public bool CanPromoteFocusedWindowToMaster()
        {
            if (!TryGetCurrentMasterSatelliteDesktop(out var desktop))
            {
                return false;
            }
            using (m_backendLock.EnterScope())
            {
                return m_masterSatelliteCommands.CanPromoteFocusedWindow(
                    m_backend,
                    desktop);
            }
        }

        public void PromoteFocusedWindowToMaster()
        {
            MasterSatelliteCommandResult result;
            var desktop = GetRequiredMasterSatelliteCommandDesktop();
            using (m_backendLock.EnterScope())
            {
                result = m_masterSatelliteCommands.PromoteFocusedWindow(
                    m_backend,
                    desktop);
            }
            CompleteMasterSatelliteCommand("PromoteFocusedWindow", desktop, result);
        }

        public bool CanSwapMasterSide() => CanMutateCurrentMasterSatelliteLayout();

        public void SwapMasterSide()
        {
            MasterSatelliteCommandResult result;
            var desktop = GetRequiredMasterSatelliteCommandDesktop();
            using (m_backendLock.EnterScope())
            {
                result = m_masterSatelliteCommands.SwapMasterSide(
                    m_backend,
                    desktop);
            }
            CompleteMasterSatelliteCommand("SwapMasterSide", desktop, result);
        }

        public bool CanToggleSatelliteOrientation() => CanMutateCurrentMasterSatelliteLayout();

        public void ToggleSatelliteOrientation()
        {
            MasterSatelliteCommandResult result;
            var desktop = GetRequiredMasterSatelliteCommandDesktop();
            using (m_backendLock.EnterScope())
            {
                result = m_masterSatelliteCommands.ToggleSatelliteOrientation(
                    m_backend,
                    desktop);
            }
            CompleteMasterSatelliteCommand("ToggleSatelliteOrientation", desktop, result);
        }

        public bool CanResetMasterRatio() => CanMutateCurrentMasterSatelliteLayout();

        public void ResetMasterRatio()
        {
            MasterSatelliteCommandResult result;
            var desktop = GetRequiredMasterSatelliteCommandDesktop();
            using (m_backendLock.EnterScope())
            {
                result = m_masterSatelliteCommands.ResetMasterRatio(
                    m_backend,
                    desktop);
            }
            CompleteMasterSatelliteCommand("ResetMasterRatio", desktop, result);
        }

        public bool CanRebalanceMasterSatelliteLayout() => CanMutateCurrentMasterSatelliteLayout();

        public void RebalanceMasterSatelliteLayout()
        {
            MasterSatelliteCommandResult result;
            var desktop = GetRequiredMasterSatelliteCommandDesktop();
            using (m_backendLock.EnterScope())
            {
                result = m_masterSatelliteCommands.Rebalance(
                    m_backend,
                    desktop);
            }
            CompleteMasterSatelliteCommand("Rebalance", desktop, result);
        }

        private bool IsCurrentMasterSatelliteLayoutActive()
        {
            if (!TryGetCurrentMasterSatelliteDesktop(out var desktop))
            {
                return false;
            }
            using (m_backendLock.EnterScope())
            {
                return m_masterSatelliteCommands.IsActive(desktop);
            }
        }

        private bool CanMutateCurrentMasterSatelliteLayout()
        {
            if (!TryGetCurrentMasterSatelliteDesktop(out var desktop)
                || IsMasterSatelliteCapacityTransitionSource(desktop))
            {
                return false;
            }
            using (m_backendLock.EnterScope())
            {
                return m_masterSatelliteCommands.IsActive(desktop);
            }
        }

        private IVirtualDesktop GetRequiredMasterSatelliteCommandDesktop()
        {
            if (TryGetCurrentMasterSatelliteDesktop(out var desktop))
            {
                return desktop;
            }
            throw new AlgorithmicLayoutCommandException(
                "The current virtual desktop is unavailable.",
                TilingError.InvalidTarget);
        }

        private bool TryGetMasterSatellitePrimaryDisplay(out IDisplay primaryDisplay)
        {
            try
            {
                primaryDisplay = m_workspace.DisplayManager.PrimaryDisplay;
                return primaryDisplay != null;
            }
            catch (Exception ex)
            {
                m_logger.Debug(
                    ex,
                    "Could not resolve the primary display for a Master + Satellites command");
                primaryDisplay = null!;
                return false;
            }
        }

        private IDisplay GetRequiredMasterSatellitePrimaryDisplay()
        {
            if (TryGetMasterSatellitePrimaryDisplay(out var primaryDisplay))
            {
                return primaryDisplay;
            }
            throw new AlgorithmicLayoutCommandException(
                "The primary display is unavailable.",
                TilingError.InvalidTarget);
        }

        private void CompleteMasterSatelliteCommand(
            string command,
            IVirtualDesktop desktop,
            MasterSatelliteCommandResult result)
        {
            ArgumentNullException.ThrowIfNull(desktop);
            if (!result.Succeeded)
            {
                m_logger.Debug(
                    "Master + Satellites command {Command} rejected: {Reason} ({Message})",
                    command,
                    result.FailureReason,
                    result.Message);
                RaiseMasterSatelliteEvent(new AlgorithmicLayoutEvent(
                    AlgorithmicLayoutEventKind.OperationRejected,
                    m_display,
                    result.FailureReason.ToString(),
                    "AlgorithmicLayout.OperationRejected.CommandHandled",
                    sourceDesktop: desktop));
                throw new AlgorithmicLayoutCommandException(
                    GetMasterSatelliteCommandUserHint(
                        command,
                        result.FailureReason),
                    MapMasterSatelliteFailure(result.FailureReason));
            }

            m_logger.Debug(
                "Master + Satellites command {Command} completed; disposition={Disposition}, changed={Changed}, revision={Revision}",
                command,
                result.Disposition,
                result.Changed,
                result.Operation?.After.Revision);
            if (result.Changed)
            {
                using (m_backendLock.EnterScope())
                {
                    PublishMasterSatelliteCapacityLocked(
                        desktop);
                }
                var layoutEvent = CreateMasterSatelliteCommandChangedEvent(
                    command,
                    result,
                    m_display,
                    desktop);
                if (layoutEvent != null)
                {
                    RaiseMasterSatelliteEvent(layoutEvent);
                }
                InvalidateLayout();
            }
        }

        internal static AlgorithmicLayoutEvent?
            CreateMasterSatelliteCommandChangedEvent(
                string command,
                MasterSatelliteCommandResult result,
                IDisplay display,
                IVirtualDesktop desktop)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(command);
            ArgumentNullException.ThrowIfNull(result);
            ArgumentNullException.ThrowIfNull(display);
            ArgumentNullException.ThrowIfNull(desktop);
            if (!result.Succeeded || !result.Changed)
            {
                return null;
            }

            if (result.Disposition is MasterSatelliteCommandDisposition.Enabled
                or MasterSatelliteCommandDisposition.Disabled)
            {
                bool enabled = result.Disposition
                    == MasterSatelliteCommandDisposition.Enabled;
                return new AlgorithmicLayoutEvent(
                    enabled
                        ? AlgorithmicLayoutEventKind.LayoutEnabled
                        : AlgorithmicLayoutEventKind.LayoutDisabled,
                    display,
                    command,
                    enabled
                        ? "AlgorithmicLayout.LayoutEnabled"
                        : "AlgorithmicLayout.LayoutDisabled",
                    sourceDesktop: desktop);
            }

            return command == "Rebalance"
                ? new AlgorithmicLayoutEvent(
                    AlgorithmicLayoutEventKind.LayoutRecovered,
                    display,
                    command,
                    "AlgorithmicLayout.LayoutRecovered",
                    sourceDesktop: desktop)
                : null;
        }

        private static TilingError MapMasterSatelliteFailure(MasterSatelliteFailureReason reason)
        {
            return reason switch
            {
                MasterSatelliteFailureReason.WindowNotFound => TilingError.MissingTarget,
                MasterSatelliteFailureReason.MinSizeConflict => TilingError.TargetCannotFit,
                MasterSatelliteFailureReason.UnsupportedOperation => TilingError.UnsupportedInAlgorithmicLayout,
                MasterSatelliteFailureReason.Inactive => TilingError.InvalidTarget,
                _ => TilingError.InvalidTarget,
            };
        }

        internal static string GetMasterSatelliteCommandUserHint(
            string command,
            MasterSatelliteFailureReason reason)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(command);
            if (reason == MasterSatelliteFailureReason.MinSizeConflict)
            {
                if (command.Contains("Promote", StringComparison.Ordinal)
                    || command.Contains("PullWindowUp", StringComparison.Ordinal)
                    || command.Contains("MouseWindowDrop", StringComparison.Ordinal))
                {
                    return "The current master does not fit in the selected satellite slot.";
                }
                if (command.Contains("Orientation", StringComparison.Ordinal)
                    || command.Contains("SetSatellite", StringComparison.Ordinal))
                {
                    return "The requested satellite orientation cannot fit the current windows at their minimum sizes.";
                }
                return "The requested layout cannot fit the current windows at their minimum sizes.";
            }
            if (command.Contains("Mouse", StringComparison.Ordinal))
            {
                return "That drop is not supported by the active Master + Satellites layout.";
            }
            if (command.Contains("Stack", StringComparison.Ordinal))
            {
                return "Stack panels are not supported while Master + Satellites is active.";
            }
            return reason switch
            {
                MasterSatelliteFailureReason.WindowNotFound =>
                    "Select a tiled satellite window before running this command.",
                MasterSatelliteFailureReason.Inactive =>
                    "Master + Satellites is not active for this desktop and display.",
                _ => "The requested operation is not supported by the active Master + Satellites layout.",
            };
        }
    }
}
