using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

using FancyWM.Utilities;

using WinMan;
using FancyWM.Layouts.Tiling;
using FancyWM.Layouts;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using System.Reactive.Linq;
using System.Diagnostics;

using FancyWM.AlgorithmicLayouts;
using FancyWM.Models;

namespace FancyWM
{
    internal partial class TilingService
    {
        private void RestoreOriginalLayout()
        {
            List<(IWindow Window, Rectangle Position)> restores = [];
            using (m_backendLock.EnterScope())
            {
                foreach (var desktop in m_backend.SnapshotDesktops())
                {
                    try
                    {
                        var tree = m_backend.GetTree(desktop);
                        if (tree == null)
                            continue;

                        foreach (var window in tree.Root!.Windows)
                        {
                            var originalPosition = m_backend.GetOriginalPosition(window.WindowReference);
                            restores.Add((window.WindowReference, originalPosition));
                        }
                    }
                    catch (KeyNotFoundException)
                    {
                        continue;
                    }
                    catch (InvalidOperationException e)
                    {
                        m_logger.Warning(e, "Exception thrown while restoring the original window layout!");
                    }
                }
            }

            foreach (var restore in restores)
            {
                try
                {
                    restore.Window.SetPosition(restore.Position);
                }
                catch (InvalidWindowReferenceException)
                {
                    continue;
                }
                catch (InvalidOperationException) when (
                    restore.Window.State != WindowState.Restored)
                {
                    continue;
                }
            }
        }

        private TimeSpan m_lastUpdateLayout = TimeSpan.Zero;
        private readonly ArrangeFailureNotificationTracker m_arrangeFailureNotifications = new();

        private sealed record class ArrangeFailureFallback(
            IWindow Window,
            bool ApplyFloatingFallback,
            MasterSatelliteLocalMutationResult? AlgorithmicRemoval);

        private sealed record class ArrangeTreeAttempt(
            bool Arranged,
            bool RetryAfterFallback,
            IReadOnlyList<ArrangeFailureFallback> Fallbacks,
            string? FailureMessage);

        private bool UpdateTree(DesktopTree tree)
        {
            while (true)
            {
                HashSet<IntPtr> newWindowHandles;
                using (m_newWindowSetLock.EnterScope())
                {
                    newWindowHandles = m_newWindowSet
                        .Select(window => window.Handle)
                        .Where(handle => handle != IntPtr.Zero)
                        .ToHashSet();
                }

                ArrangeTreeAttempt attempt;
                IntPtr[] currentTreeHandles;
                using (m_backendLock.EnterScope())
                {
                    attempt = UpdateTreeLocked(tree, newWindowHandles);
                    currentTreeHandles = tree.Root?.Windows
                        .Select(window => window.WindowReference.Handle)
                        .Where(handle => handle != IntPtr.Zero)
                        .ToArray() ?? [];
                }

                m_arrangeFailureNotifications.ReleaseResolved(currentTreeHandles);
                ApplyArrangeFailureFallbacks(attempt.Fallbacks);

                if (attempt.Arranged)
                {
                    return true;
                }
                if (attempt.FailureMessage != null)
                {
                    m_logger.Warning(attempt.FailureMessage);
                }
                if (!attempt.RetryAfterFallback)
                {
                    return false;
                }
            }

        }

        /// <summary>
        /// Must be called with m_backendLock held. It never raises callbacks and
        /// never mutates the service floating/window/new-window sets.
        /// </summary>
        private ArrangeTreeAttempt UpdateTreeLocked(
            DesktopTree tree,
            IReadOnlySet<IntPtr> newWindowHandles)
        {
            var fallbacks = new List<ArrangeFailureFallback>();
            tree.WorkArea = m_display.WorkArea;

            while (true)
            {
                tree.Measure();
                try
                {
                    tree.Arrange();
                    return new ArrangeTreeAttempt(true, false, fallbacks, null);
                }
                catch (UnsatisfiableFlexConstraintsException)
                {
                    bool algorithmicLayoutActive = IsMasterSatelliteTreeActiveLocked(tree);
                    var windows = tree.Root?.Windows.ToList() ?? [];
                    var decision = MasterSatelliteArrangeFailurePolicy.Decide(
                        algorithmicLayoutActive,
                        windows.Select(window => new ArrangeFailureCandidate(
                            window.WindowReference.Handle,
                            window.GenerationID,
                            newWindowHandles.Contains(window.WindowReference.Handle))));
                    if (!decision.HasCandidate)
                    {
                        string message = algorithmicLayoutActive
                            ? "The arrange pass failed for an active Master + Satellites tree, but no actual new window is available for overflow/floating; the canonical tree was left unchanged."
                            : "The arrange pass failed, but the tree contains no window that can be floated.";
                        return new ArrangeTreeAttempt(false, false, fallbacks, message);
                    }

                    var candidate = windows.First(window =>
                        window.WindowReference.Handle == decision.CandidateHandle);
                    if (!algorithmicLayoutActive)
                    {
                        fallbacks.Add(new ArrangeFailureFallback(
                            candidate.WindowReference,
                            true,
                            null));
                        return new ArrangeTreeAttempt(false, true, fallbacks, null);
                    }

                    var removal = RemoveMasterSatelliteWindowLocked(
                        candidate.WindowReference,
                        preserveOriginalPosition: true);
                    if (!removal.Succeeded)
                    {
                        fallbacks.Add(new ArrangeFailureFallback(
                            candidate.WindowReference,
                            false,
                            removal));
                        return new ArrangeTreeAttempt(
                            false,
                            false,
                            fallbacks,
                            "The arrange pass failed for an active Master + Satellites tree and its new-window fallback could not be removed transactionally; the tree was left unchanged.");
                    }

                    // Canonical removal succeeded atomically. Arrange again before
                    // publishing the fallback so no callback can observe a broken
                    // algorithmic tree.
                    fallbacks.Add(new ArrangeFailureFallback(
                        candidate.WindowReference,
                        true,
                        removal));
                }
            }
        }

        /// <summary>
        /// Must be called with m_backendLock held.
        /// </summary>
        private bool IsMasterSatelliteTreeActiveLocked(DesktopTree tree)
        {
            if (!m_masterSatelliteLifecycleReady || m_masterSatelliteLifecycleDisposed)
            {
                return false;
            }

            foreach (var entry in m_masterSatelliteLifecycle.SnapshotStates())
            {
                try
                {
                    if (ReferenceEquals(
                        m_backend.GetTree(entry.Key.VirtualDesktop),
                        tree))
                    {
                        return true;
                    }
                }
                catch (KeyNotFoundException)
                {
                    continue;
                }
            }
            return false;
        }

        /// <summary>
        /// Called only after UpdateTree has released backend/new-window locks.
        /// PlacementFailed remains the single place that invokes OnWindowFloated.
        /// </summary>
        private void ApplyArrangeFailureFallbacks(
            IReadOnlyList<ArrangeFailureFallback> fallbacks)
        {
            foreach (var fallback in fallbacks)
            {
                if (fallback.AlgorithmicRemoval != null)
                {
                    LogMasterSatelliteRemoval(fallback.Window, fallback.AlgorithmicRemoval);
                }
                if (!m_arrangeFailureNotifications.TryMark(fallback.Window.Handle))
                {
                    continue;
                }
                if (!fallback.ApplyFloatingFallback)
                {
                    m_logger.Warning(
                        "Suppressing floating fallback for window {Window} because its canonical Master + Satellites removal was rejected",
                        fallback.Window.DebugString());
                    continue;
                }

                m_logger.Warning(
                    "The arrange pass failed; applying floating fallback to window {Window}",
                    fallback.Window.DebugString());
                MarkWindowFloating(fallback.Window);
                DetectChanges(fallback.Window);
                PlacementFailed?.Invoke(this, new TilingFailedEventArgs(
                    TilingError.NoValidPlacementExists,
                    fallback.Window));
            }
        }

        private async Task UpdateLayoutAsync()
        {
            if (!Active)
                return;

            if (m_currentInteraction != UserInteraction.None && m_sw.Elapsed - m_lastUpdateLayout <= TimeSpan.FromSeconds(1.0 / m_display.RefreshRate))
            {
                return;
            }
            m_lastUpdateLayout = m_sw.Elapsed;

            IVirtualDesktop desktop = m_workspace.VirtualDesktopManager.CurrentDesktop;

            List<TilingNode> snapshot;
            IReadOnlyCollection<TilingNode> focusedPath;
            TilingNode? focusedNode;
            DesktopTree tree;

            using (m_backendLock.EnterScope())
            {
                try
                {
                    var treeOrNull = m_backend.GetTree(desktop);
                    if (treeOrNull == null)
                        return;
                    tree = treeOrNull;
                }
                catch (KeyNotFoundException)
                {
                    m_logger.Warning($"Current desktop {desktop} is not registered with backend, aborting...");
                    return;
                }

            }

            if (!UpdateTree(tree))
            {
                return;
            }

            using (m_backendLock.EnterScope())
            {
                snapshot = tree.Root!.Nodes.Skip(1).ToList();
                focusedNode = m_backend.GetFocus(desktop);
                focusedPath = (IReadOnlyCollection<TilingNode>?)focusedNode?.PathToRoot?.ToList() ?? [];
            }

            async ValueTask RepositionAsync()
            {
                try
                {
                    Freeze();
                    IList<WindowNode> snapshotWindows;
                    using (m_ignoreRepositionSetLock.EnterScope())
                    {
                        snapshotWindows = snapshot.OfType<WindowNode>().Where(x => !m_ignoreRepositionSet.Contains(x.WindowReference)).ToList();
                    }

                    bool useSmoothing = m_animateWindowMovement && m_currentInteraction != UserInteraction.Resizing;
                    await UpdateWindowPositionsAsync(snapshotWindows, useSmoothing);
                }
                finally
                {
                    Unfreeze();
                }
            }

            m_gui.FocusRectangle = GetFocusRectangle(focusedNode);

            var repositionTask = RepositionAsync();
            try
            {
                m_gui.UpdateOverlay(snapshot, focusedPath);
                m_gui.PreviewRectangle = GetPreviewRectangle();

                if (m_masterSatelliteDropPreviewWindows.Count > 0)
                {
                    m_gui.PreviewWindows = m_masterSatelliteDropPreviewWindows;
                }
                else if (m_showPreviewFocus)
                {
                    HashSet<IWindow> previewWindows;
                    using (m_backendLock.EnterScope())
                    {
                        previewWindows = m_backend.SnapshotDesktops()
                            .Select(candidate => m_backend.GetFocus(candidate))
                            .OfType<WindowNode>()
                            .Select(x => x.WindowReference)
                            .ToHashSet();
                    }
                    m_gui.PreviewWindows = previewWindows;
                }
                else
                {
                    m_gui.PreviewWindows = EmptyWindowSet;
                }
            }
            finally
            {
                // Repositioning starts before overlay work to preserve the
                // existing visual latency, but it must always be observed even
                // when a synchronous preview/renderer call fails.
                await repositionTask;
            }
        }

        private async Task UpdateWindowPositionsAsync(IEnumerable<WindowNode> snapshot, bool useSmoothing)
        {
            var targets = CalculateRepositionTargets(snapshot);
            foreach (var target in targets)
            {
                if (target.OriginalPosition != target.ComputedPosition)
                {
                    m_logger.Information("Relocating window {Window} from {OriginalPosition} to {ComputedPosition}",
                        target.Window.DebugString(),
                        target.OriginalPosition, target.ComputedPosition);
                }
                else
                {
                    m_logger.Information("Window {Window} location is {ComputedPosition}",
                        target.Window.DebugString(),
                        target.ComputedPosition);
                }
            }

            HashSet<IWindow>? newWindows = null;
            using (m_newWindowSetLock.EnterScope())
            {
                if (m_newWindowSet.Count > 0)
                {
                    newWindows = new HashSet<IWindow>(
                        m_newWindowSet,
                        ReferenceEqualityComparer.Instance);
                    m_newWindowSet.Clear();
                }
            }

            if (useSmoothing)
            {
                var focusRectangle = m_gui.FocusRectangle;
                m_gui.FocusRectangle = null;

                TransitionTargetGroup transitionGroup;
                if (newWindows != null)
                {
                    await TransitionTargetGroup.PerformTransitionAsync(targets.Where(x => newWindows!.Contains(x.Window)).ToList());
                    transitionGroup = new TransitionTargetGroup(m_animationThread, targets.Where(x => !newWindows!.Contains(x.Window)));
                }
                else
                {
                    transitionGroup = new TransitionTargetGroup(m_animationThread, targets);
                }
                await transitionGroup.PerformSmoothTransitionAsync(TimeSpan.FromMilliseconds(100));

                m_gui.FocusRectangle = focusRectangle;
            }
            else
            {
                await TransitionTargetGroup.PerformTransitionAsync(targets);
            }
        }

        private List<TransitionTarget> CalculateRepositionTargets(IEnumerable<WindowNode> snapshot)
        {
            var targets = new List<TransitionTarget>();
            foreach (var window in snapshot)
            {
                try
                {
                    var currentPosition = window.WindowReference.Position;
                    if (!window.WindowReference.CanResize)
                    {
                        m_logger.Warning("Unresizable window {Window} will be moved only", window.WindowReference.DebugString());
                        var targetRect = ShrinkTo(window.ComputedRectangle, currentPosition.Width, currentPosition.Height);
                        if (targetRect == currentPosition)
                        {
                            continue;
                        }
                        targets.Add(new TransitionTarget(window.WindowReference, currentPosition, targetRect));
                    }
                    else
                    {
                        m_logger.Debug("Updating position of window {Window}", window.WindowReference.DebugString());
                        var rect = window.ComputedRectangle;
                        var frame = window.WindowReference.FrameMargins;
                        var adjustedRect = new Rectangle(
                            left: rect.Left - frame.Left,
                            top: rect.Top - frame.Top,
                            right: rect.Right + frame.Right,
                            bottom: rect.Bottom + frame.Bottom);

                        if (adjustedRect == currentPosition)
                        {
                            continue;
                        }

                        targets.Add(new TransitionTarget(window.WindowReference, currentPosition, adjustedRect));

                        var minSize = window.WindowReference.MinSize;
                        if (minSize.HasValue)
                        {
                            if (minSize.Value.X > adjustedRect.Width)
                            {
                                m_logger.Warning("New width for {Window} is smaller than the value reported by WM_GETMINMAXINFO ({ComputedWidth} < {MinimumWidth})",
                                    window.WindowReference.DebugString(), adjustedRect.Width, minSize.Value.X);
                            }
                            if (minSize.Value.Y > adjustedRect.Height)
                            {
                                m_logger.Warning("New height for {Window} is smaller than the value reported by WM_GETMINMAXINFO ({ComputedHeight} < {MinimumHeight})",
                                    window.WindowReference.DebugString(), adjustedRect.Height, minSize.Value.Y);
                            }
                        }
                    }
                }
                catch (InvalidWindowReferenceException)
                {
                    // Ignore
                }
                catch (Win32Exception e)
                {
                    m_logger.Error(e, "Failed to calculate reposition targets");
                }
            }
            return targets;
        }

        private bool CanShowFocusRectangle()
        {
            return m_showFocus && m_currentInteraction == UserInteraction.None && m_movingPanelNode == null;
        }

        private Rectangle? GetFocusRectangle(TilingNode? focusedNode)
        {
            if (focusedNode is WindowNode focusedWindow && CanShowFocusRectangle())
            {
                return focusedWindow.ComputedRectangle;
            }
            return null;
        }

        private Rectangle? GetPreviewRectangle()
        {
            m_masterSatelliteDropPreviewWindows = EmptyWindowSet;
            if (m_currentInteraction != UserInteraction.Moving
                && m_movingPanelNode == null)
            {
                return null;
            }

            try
            {
                var interactionWindow = m_currentInteraction == UserInteraction.Moving
                    ? m_movingWindow ?? m_workspace.FocusedWindow
                    : null;
                bool previewAlgorithmicWindowDrop = interactionWindow != null
                    && (IsMasterSatelliteWindow(interactionWindow)
                        || (IsCurrentMasterSatelliteLayoutActive()
                            && IsFloatingWindow(interactionWindow)));
                if ((m_currentInteraction == UserInteraction.Moving
                        && (m_delayReposition || previewAlgorithmicWindowDrop))
                    || m_movingPanelNode != null)
                {
                    var isSwapping = IsSwapModifierPressed();
                    var pt = m_workspace.CursorLocation;

                    if (m_movingPanelNode == null)
                    {
                        // Preserve the legacy focused-window preview outside the
                        // algorithmic path. The captured moving window is required
                        // for a canonical drag if focus changes during interaction.
                        var window = previewAlgorithmicWindowDrop
                            ? interactionWindow
                            : m_workspace.FocusedWindow;
                        if (window == null)
                        {
                            return null;
                        }

                        using (m_backendLock.EnterScope())
                        {
                            if (m_backend.HasWindow(window))
                            {
                                if (TryResolveAttachedMasterSatelliteDesktopLocked(
                                        window,
                                        out var desktop))
                                {
                                    if (m_masterSatelliteLifecycle.TryGetState(desktop, out var state))
                                    {
                                        var plan = m_masterSatelliteDrops.CreateWindowDropPlan(
                                            m_backend,
                                            desktop,
                                            state,
                                            m_masterSatelliteLifecycle.SettingsSnapshot,
                                            window,
                                            pt);
                                        if (plan.IsAccepted)
                                        {
                                            m_masterSatelliteDropPreviewWindows = plan.PreviewWindows.ToHashSet();
                                        }
                                        return plan.PreviewRectangle;
                                    }
                                    return null;
                                }
                                return m_backend.MockMoveWindow(window, pt, allowNesting: !isSwapping).preArrange;
                            }
                            if (previewAlgorithmicWindowDrop)
                            {
                                // Floating-window placement is committed through
                                // the ordinary algorithmic placement/overflow seam
                                // on mouse-up. It has no canonical source slot to
                                // simulate as a reorder preview.
                                return null;
                            }
                        }
                    }
                    else
                    {
                        using (m_backendLock.EnterScope())
                        {
                            if (TryResolveAttachedMasterSatelliteDesktopLocked(
                                    m_movingPanelNode,
                                    out var desktop))
                            {
                                // Canonical panels are structural. They never use the
                                // generic arbitrary-nesting preview path.
                                return null;
                            }
                            var rect = m_backend.MockMoveNode(m_movingPanelNode, pt, allowNesting: !isSwapping).preArrange;
                            var padding = GetPanelPaddingRect();
                            var spacing = GetPanelSpacing();
                            return new Rectangle(
                                rect.Left - padding.Left - spacing / 2,
                                rect.Top - padding.Top - spacing / 2,
                                rect.Right + padding.Right + spacing / 2,
                                rect.Bottom + padding.Bottom + spacing / 2);
                        }
                    }
                }
            }
            catch (TilingFailedException exception)
            {
                m_logger.Debug(
                    exception,
                    "Could not create a window-move preview for the current tiling interaction");
            }
            catch (InvalidWindowReferenceException exception)
            {
                m_logger.Debug(
                    exception,
                    "Window-move preview was cancelled because the window is no longer valid");
            }
            return null;
        }

        private void MoveToParentPanel(TilingNode node)
        {
            try
            {
                using (m_backendLock.EnterScope())
                {
                    m_backend.PullUp(node);
                }
                InvalidateLayout();
            }
            catch (TilingFailedException e)
            {
                m_logger.Error(e, "Attempted pull up of {Node} failed", node);
                PlacementFailed?.Invoke(this, new TilingFailedEventArgs(e.FailReason));
            }
        }

        private void WrapInSplitPanel(TilingNode node, bool vertical)
        {
            try
            {
                using (m_backendLock.EnterScope())
                {
                    m_backend.WrapInSplitPanel(node, vertical);
                    m_backend.SetFocus(node);

                    node.Parent!.Padding = GetPanelPaddingRect();
                    node.Parent!.Spacing = GetPanelSpacing();

                    if (m_allocateNewPanelSpace)
                    {
                        node.Parent!.Attach(new PlaceholderNode());
                    }

                    InvalidateLayout();
                }
            }
            catch (TilingFailedException ex)
            {
                m_logger.Error(ex, "Attempted split of {Node} failed", node);
                PlacementFailed?.Invoke(this, new TilingFailedEventArgs(ex.FailReason));
            }
        }

        private void WrapInStackPanel(TilingNode node)
        {
            try
            {
                using (m_backendLock.EnterScope())
                {
                    m_backend.WrapInStackPanel(node);
                    node.Parent!.Padding = GetPanelPaddingRect();
                    node.Parent!.Spacing = GetPanelSpacing();
                    m_backend.SetFocus(node);
                    InvalidateLayout();
                }
            }
            catch (TilingFailedException ex)
            {
                m_logger.Error(ex, "Attempted stack of {Node} failed", node);
                PlacementFailed?.Invoke(this, new TilingFailedEventArgs(ex.FailReason));
            }
        }

        private IntPtr GetOverlayAnchor()
        {
            var desktop = m_workspace.VirtualDesktopManager.CurrentDesktop;
            using (m_backendLock.EnterScope())
            {
                try
                {
                    var focusedNode = m_backend.GetFocus(desktop);
                    if (focusedNode is WindowNode window)
                        return window.WindowReference.Handle;
                }
                catch (ArgumentException)
                {
                    return new IntPtr(0);
                }
            }

            var comparer = m_workspace.CreateSnapshotZOrderComparer();
            using (m_backendLock.EnterScope())
            {
                var tree = m_backend.GetTree(desktop);
                if (tree == null)
                    return new IntPtr(0);
                var topWindow = tree.Root!.Windows
                    .OrderByDescending(x => x.WindowReference, comparer)
                    .FirstOrDefault();

                if (topWindow != null)
                    return topWindow.WindowReference.Handle;

                return new IntPtr(0);
            }
        }

        private void ToggleFloat(IWindow window)
        {
            if (TryGetMasterSatelliteCapacityTransitionDesktop(
                    window,
                    out var transitionDesktop))
            {
                RejectMasterSatelliteCapacityTransitionMutation(
                    "ToggleFloat",
                    transitionDesktop);
            }
            bool floated = !IsFloatingWindow(window);
            if (floated)
            {
                MarkWindowFloating(window);
            }
            else
            {
                MarkWindowTiled(window);
            }
            DetectChanges(
                window,
                allowExistingDesktopOverflow: !floated);
            if (floated)
            {
                OnWindowFloated(window);
            }
            else
            {
                try
                {
                    using (m_backendLock.EnterScope())
                    {
                        if (m_backend.HasWindow(window))
                        {
                            m_backend.SetFocus(window);
                        }
                    }
                }
                catch (Exception ex)
                {
                    m_logger.Debug(
                        ex,
                        "Could not restore backend focus after unfloat for window {Window}",
                        window.DebugString());
                }
            }
        }

        private void OnDisplayScalingChanged(object? sender, DisplayScalingChangedEventArgs e)
        {
            if (DeferToTilingDispatcher(
                () => OnDisplayScalingChanged(sender, e),
                nameof(OnDisplayScalingChanged)))
            {
                return;
            }
            PropagatePanelHeightChange();
            RelayoutMasterSatelliteAfterScaling();
        }

        private void OnPrimaryDisplayChanged(
            object? sender,
            PrimaryDisplayChangedEventArgs e)
        {
            if (DeferToTilingDispatcher(
                () => OnPrimaryDisplayChanged(sender, e),
                nameof(OnPrimaryDisplayChanged)))
            {
                return;
            }

            m_masterSatellitePrimaryDisplay = e.NewPrimaryDisplay;
            MasterSatelliteLayoutSettings settings;
            using (m_backendLock.EnterScope())
            {
                settings = m_masterSatelliteLifecycle.SettingsSnapshot with { };
            }
            OnMasterSatelliteSettingsChanged(settings);
        }

        private void OnPlacementFailed(object? sender, TilingFailedEventArgs e)
        {
            if (e.FailReason == TilingError.NoValidPlacementExists && e.FailSource != null)
            {
                MarkWindowFloating(e.FailSource);
                OnWindowFloated(e.FailSource);
            }
        }

        private void OnWindowFloated(IWindow window)
        {
            Rectangle? originalPosition;
            try
            {
                using (m_backendLock.EnterScope())
                {
                    originalPosition = m_backend.GetOriginalPosition(window);
                }
            }
            catch (Exception exception)
            {
                m_logger.Debug(
                    exception,
                    "Could not restore a floated window's original backend position");
                originalPosition = null;
            }
            try
            {
                originalPosition ??= GetOptimalRestoredSize(window);

                var originalDisplay = m_workspace.DisplayManager.Displays.FirstOrDefault(x => x.Bounds.Contains(originalPosition.Value.Center));
                originalDisplay ??= m_workspace.DisplayManager.PrimaryDisplay;

                var displayBounds = originalDisplay.Bounds;

                var centeredPosition = Rectangle.OffsetAndSize(
                    displayBounds.Left + displayBounds.Width / 2 - originalPosition.Value.Width / 2,
                    displayBounds.Top + displayBounds.Height / 2 - originalPosition.Value.Height / 2,
                    originalPosition.Value.Width,
                    originalPosition.Value.Height);

                window.SetPosition(centeredPosition);
                FocusHelper.ForceActivate(window.Handle);
            }
            catch (Exception exception)
            {
                // Floating is already the safe layout fallback. Geometry restore
                // and activation are best effort and must not suppress its event.
                m_logger.Debug(
                    exception,
                    "Could not restore or activate a floated window");
            }
        }

        private Rectangle GetOptimalRestoredSize(IWindow window)
        {
            var screenSize = m_display.WorkArea.Size;
            var minSize = window.MinSize ?? new Point(0, 0);
            var maxSize = window.MaxSize ?? new Point(screenSize.X, screenSize.Y);
            var pos = window.Position;

            return Rectangle.OffsetAndSize(
                pos.Left,
                pos.Top,
                Math.Max(minSize.X, Math.Min(maxSize.X, Math.Min(screenSize.X, (screenSize.X + minSize.X) / 2))),
                Math.Max(minSize.Y, Math.Min(maxSize.Y, Math.Min(screenSize.Y, (screenSize.Y + minSize.Y) / 2))));
        }


        private void OnCursorLocationChanged(object? sender, CursorLocationChangedEventArgs e)
        {
            QueueTilingDispatcherAction(() =>
            {
                if (PendingIntent is GroupWithIntent gwi)
                {
                    if (Mouse.LeftButton != MouseButtonState.Pressed)
                    {
                        PendingIntent.Cancel();
                        PendingIntent = null;
                        return;
                    }
                    if (!TryGetCurrentMasterSatelliteDesktop(out var desktop))
                    {
                        return;
                    }

                    using (m_backendLock.EnterScope())
                    {
                        if (m_backend.NodeAtPoint(desktop, e.NewLocation) is WindowNode targetNode)
                        {
                            var newSet = new HashSet<IWindow> { gwi.Source.WindowReference, targetNode.WindowReference };
                            if (!m_gui.PreviewWindows.SetEquals(newSet))
                            {
                                m_gui.PreviewWindows = newSet;
                            }
                        }
                    }
                }
            }, nameof(OnCursorLocationChanged));
        }

        private void OnPendingIntentChanged(object? sender, EventArgs e)
        {
            if (PendingIntent == null)
            {
                QueueTilingDispatcherAction(
                    () => m_gui.PreviewWindows = EmptyWindowSet,
                    "clearing the pending-intent preview");
            }
            else
            {
                if (App.Current.Services.GetService<LowLevelMouseHook>() is LowLevelMouseHook mshk)
                {
                    var startPt = m_workspace.CursorLocation;
                    bool dispatched = false;
                    void onMouseButtonChanged(object? sender, ref LowLevelMouseHook.ButtonStateChangedEventArgs e)
                    {
                        mshk.ButtonStateChanged -= onMouseButtonChanged;
                        if (e.Button == LowLevelMouseHook.MouseButton.Left && e.IsPressed == false)
                        {
                            var pt = new Point(e.X, e.Y);
                            if (Math.Abs(pt.X - startPt.X) > 5 || Math.Abs(pt.Y - startPt.Y) > 5)
                            {
                                if (!dispatched)
                                {
                                    dispatched = true;
                                    QueueTilingDispatcherAction(
                                        () => HitTestCompletePendingIntent(pt),
                                        nameof(HitTestCompletePendingIntent));
                                }
                            }
                        }
                        else
                        {
                            if (!dispatched)
                            {
                                dispatched = true;
                                QueueTilingDispatcherAction(() =>
                                {
                                    PendingIntent?.Cancel();
                                    PendingIntent = null;
                                }, "cancelling the pending intent");
                            }
                        }
                    }
                    mshk.ButtonStateChanged += onMouseButtonChanged;
                }
            }
        }

        private void HitTestCompletePendingIntent(Point cursorPosition)
        {
            if (m_pendingIntent is GroupWithIntent intent && m_display.Bounds.Contains(cursorPosition))
            {
                PendingIntent = null;

                // Grouping is a manual-tree mutation. Stage 5 routes algorithmic
                // commands explicitly; until then never detach a canonical node
                // through the legacy OnWindowRemoved callback.
                if (IsMasterSatelliteWindow(intent.Source.WindowReference))
                {
                    m_logger.Debug(
                        "Ignoring legacy grouping for Master + Satellites window {Window}",
                        intent.Source.WindowReference.DebugString());
                    intent.Cancel();
                    return;
                }

                WindowNode? sourceNode = null;
                PanelNode? panel = null;
                WindowNode? targetForGrouping = null;
                WindowNode? rejectedAlgorithmicTarget = null;
                IVirtualDesktop? rejectedAlgorithmicDesktop = null;
                MasterSatelliteCommandResult? algorithmicResult = null;
                if (!TryGetCurrentMasterSatelliteDesktop(out var desktop))
                {
                    intent.Cancel();
                    return;
                }
                using (m_backendLock.EnterScope())
                {
                    var node = m_backend.NodeAtPoint(desktop, cursorPosition);
                    if (node is not WindowNode targetNode)
                    {
                        intent.Cancel();
                        return;
                    }
                    if (targetNode.WindowReference.Equals(intent.Source.WindowReference))
                    {
                        intent.Cancel();
                        return;
                    }

                    if (TryResolveAttachedMasterSatelliteDesktopLocked(
                            targetNode,
                            out var algorithmicDesktop))
                    {
                        // A grouping intent may originate in another service or
                        // desktop. Guard the target as well as the source before
                        // intent.Complete can detach anything.
                        intent.Cancel();
                        rejectedAlgorithmicTarget = targetNode;
                        rejectedAlgorithmicDesktop = algorithmicDesktop;
                        algorithmicResult = m_masterSatelliteCommands
                            .RejectLegacyGrouping(algorithmicDesktop);
                    }
                    else
                    {
                        switch (intent.Type)
                        {
                            case GroupWithIntent.GroupType.HorizontalPanel:
                                if (!CanSplit(targetNode))
                                {
                                    intent.Cancel();
                                    PlacementFailed?.Invoke(this, new TilingFailedEventArgs(TilingError.NestingInStackPanel, targetNode.WindowReference));
                                    return;
                                }
                                break;
                            case GroupWithIntent.GroupType.VerticalPanel:
                                if (!CanSplit(targetNode))
                                {
                                    intent.Cancel();
                                    PlacementFailed?.Invoke(this, new TilingFailedEventArgs(TilingError.NestingInStackPanel, targetNode.WindowReference));
                                    return;
                                }
                                break;
                            case GroupWithIntent.GroupType.StackPanel:
                                if (!CanStack(targetNode))
                                {
                                    intent.Cancel();
                                    PlacementFailed?.Invoke(this, new TilingFailedEventArgs(TilingError.NestingInStackPanel, targetNode.WindowReference));
                                    return;
                                }
                                break;
                        }

                        targetForGrouping = targetNode;
                    }
                }

                if (algorithmicResult != null)
                {
                    CompleteMasterSatelliteOverlayCommand(
                        "OverlayGroupWith",
                        rejectedAlgorithmicDesktop!,
                        algorithmicResult,
                        rejectedAlgorithmicTarget!);
                    return;
                }

                // Completing the intent synchronously invokes OnWindowRemoved for
                // the source. Keep that workspace/VDM callback outside the backend
                // lock, then revalidate the captured target before mutating it.
                intent.Complete();
                sourceNode = intent.Source;
                using (m_backendLock.EnterScope())
                {
                    if (targetForGrouping?.Parent == null
                        || targetForGrouping.Desktop != m_backend.GetTree(desktop)
                        || TryResolveAttachedMasterSatelliteDesktopLocked(
                            targetForGrouping,
                            out _))
                    {
                        throw new TilingFailedException(TilingError.InvalidTarget);
                    }
                    switch (intent.Type)
                    {
                        case GroupWithIntent.GroupType.HorizontalPanel:
                            m_backend.WrapInSplitPanel(targetForGrouping, vertical: false);
                            break;
                        case GroupWithIntent.GroupType.VerticalPanel:
                            m_backend.WrapInSplitPanel(targetForGrouping, vertical: true);
                            break;
                        case GroupWithIntent.GroupType.StackPanel:
                            m_backend.WrapInStackPanel(targetForGrouping);
                            break;
                    }
                    panel = targetForGrouping.Parent!;
                    panel.Spacing = GetPanelSpacing();
                    panel.Padding = GetPanelPaddingRect();
                }

                BindEventHandlers(sourceNode!.WindowReference);
                using (m_windowSetLock.EnterScope())
                {
                    m_windowSet.Add(sourceNode.WindowReference);
                }
                if (CanManage(sourceNode.WindowReference))
                {
                    //m_logger.Information("Window {Handle}={ProcessName} can be managed, registering with backend", e.Source.Handle, e.Source.GetCachedProcessName());
                    try
                    {
                        try
                        {
                            using (m_backendLock.EnterScope())
                            {
                                var node = m_backend.RegisterWindow(sourceNode.WindowReference, panel!);
                                m_backend.SetFocus(node);
                            }
                        }
                        catch (NoValidPlacementExistsException)
                        {
                            PlacementFailed?.Invoke(this, new TilingFailedEventArgs(
                                TilingError.NoValidPlacementExists, sourceNode.WindowReference));
                        }
                    }
                    catch
                    {
                        return;
                    }

                    InvalidateLayout();
                }
            }
            else
            {
                m_pendingIntent?.Cancel();
            }
        }

        private void OnBeginHorizontalWithRequestedAsync(object? sender, WindowNode e)
        {
            if (IsMasterSatelliteWindow(e.WindowReference))
            {
                m_logger.Debug(
                    "Ignoring horizontal grouping for Master + Satellites window {Window}",
                    e.WindowReference.DebugString());
                return;
            }
            m_gui.PreviewWindows = new HashSet<IWindow> { e.WindowReference };
            PendingIntent = new GroupWithIntent(GroupWithIntent.GroupType.HorizontalPanel, e,
                complete: () =>
                {
                    m_gui.PreviewWindows = EmptyWindowSet;
                    OnWindowRemoved(this, new WindowChangedEventArgs(e.WindowReference));
                },
                cancel: () =>
                {
                    m_gui.PreviewWindows = EmptyWindowSet;
                });
        }

        private void OnBeginVerticalWithRequested(object? sender, WindowNode e)
        {
            if (IsMasterSatelliteWindow(e.WindowReference))
            {
                m_logger.Debug(
                    "Ignoring vertical grouping for Master + Satellites window {Window}",
                    e.WindowReference.DebugString());
                return;
            }
            m_gui.PreviewWindows = new HashSet<IWindow> { e.WindowReference };
            PendingIntent = new GroupWithIntent(GroupWithIntent.GroupType.VerticalPanel, e,
                complete: () =>
                {
                    m_gui.PreviewWindows = EmptyWindowSet;
                    OnWindowRemoved(this, new WindowChangedEventArgs(e.WindowReference));
                },
                cancel: () =>
                {
                    m_gui.PreviewWindows = EmptyWindowSet;
                });
        }

        private void OnBeginStackWithRequested(object? sender, WindowNode e)
        {
            if (IsMasterSatelliteWindow(e.WindowReference))
            {
                m_logger.Debug(
                    "Ignoring stack grouping for Master + Satellites window {Window}",
                    e.WindowReference.DebugString());
                return;
            }
            m_gui.PreviewWindows = new HashSet<IWindow> { e.WindowReference };
            PendingIntent = new GroupWithIntent(GroupWithIntent.GroupType.StackPanel, e,
                complete: () =>
                {
                    m_gui.PreviewWindows = EmptyWindowSet;
                    OnWindowRemoved(this, new WindowChangedEventArgs(e.WindowReference));
                },
                cancel: () =>
                {
                    m_gui.PreviewWindows = EmptyWindowSet;
                });
        }

        private void OnWindowVerticalSplitRequested(object? sender, TilingNode e)
        {
            CompleteMasterSatelliteOverlaySplit(e, SatelliteLayoutOrientation.Vertical);
        }

        private void OnWindowStackRequested(object? sender, TilingNode e)
        {
            MasterSatelliteCommandResult? algorithmicResult = null;
            IVirtualDesktop? algorithmicDesktop = null;
            using (m_backendLock.EnterScope())
            {
                if (TryResolveAttachedMasterSatelliteDesktopLocked(e, out var desktop))
                {
                    algorithmicDesktop = desktop;
                    if (e is WindowNode window)
                    {
                        m_backend.SetFocus(window);
                    }
                    algorithmicResult = m_masterSatelliteCommands.RejectStackPanel(desktop);
                }
            }
            if (algorithmicResult != null)
            {
                CompleteMasterSatelliteOverlayCommand(
                    "OverlayCreateStackPanel",
                    algorithmicDesktop!,
                    algorithmicResult,
                    e);
            }
            else
            {
                WrapInStackPanel(e);
            }
        }

        private void OnWindowPullUpRequested(object? sender, TilingNode e)
        {
            CompleteMasterSatelliteOverlayPullUp(e);
        }

        private void CompleteMasterSatelliteOverlayPullUp(TilingNode node)
        {
            MasterSatelliteCommandResult? algorithmicResult = null;
            IVirtualDesktop? algorithmicDesktop = null;
            using (m_backendLock.EnterScope())
            {
                if (TryResolveAttachedMasterSatelliteDesktopLocked(node, out var desktop))
                {
                    algorithmicDesktop = desktop;
                    algorithmicResult = m_masterSatelliteCommands.PullUpNode(
                        m_backend,
                        desktop,
                        node);
                }
            }
            if (algorithmicResult != null)
            {
                CompleteMasterSatelliteOverlayCommand(
                    "OverlayPullWindowUp",
                    algorithmicDesktop!,
                    algorithmicResult,
                    node);
            }
            else
            {
                MoveToParentPanel(node);
            }
        }

        private void OnWindowHorizontalSplitRequested(object? sender, TilingNode e)
        {
            CompleteMasterSatelliteOverlaySplit(e, SatelliteLayoutOrientation.Horizontal);
        }

        private void CompleteMasterSatelliteOverlaySplit(
            TilingNode node,
            SatelliteLayoutOrientation orientation)
        {
            MasterSatelliteCommandResult? algorithmicResult = null;
            IVirtualDesktop? algorithmicDesktop = null;
            using (m_backendLock.EnterScope())
            {
                if (TryResolveAttachedMasterSatelliteDesktopLocked(node, out var desktop))
                {
                    algorithmicDesktop = desktop;
                    algorithmicResult = m_masterSatelliteCommands.SetSatelliteOrientationFromNode(
                        m_backend,
                        desktop,
                        node,
                        orientation);
                }
            }
            if (algorithmicResult != null)
            {
                CompleteMasterSatelliteOverlayCommand(
                    "OverlaySetSatelliteOrientation",
                    algorithmicDesktop!,
                    algorithmicResult,
                    node);
            }
            else
            {
                WrapInSplitPanel(
                    node,
                    vertical: orientation == SatelliteLayoutOrientation.Vertical);
            }
        }

        /// <summary>
        /// Overlay commands execute directly on the WPF input route rather than
        /// through MainWindow's command exception boundary. Translate a typed
        /// rejection to the existing PlacementFailed event so the UI presents one
        /// safe toast instead of leaking an exception through Dispatcher.
        /// </summary>
        private void CompleteMasterSatelliteOverlayCommand(
            string command,
            IVirtualDesktop desktop,
            MasterSatelliteCommandResult result,
            TilingNode source)
        {
            try
            {
                CompleteMasterSatelliteCommand(command, desktop, result);
            }
            catch (AlgorithmicLayoutCommandException exception)
            {
                PlacementFailed?.Invoke(
                    this,
                    TilingFailedEventArgs.FromException(
                        exception,
                        (source as WindowNode)?.WindowReference));
            }
        }

        private void OnWindowFloatRequested(object? sender, WindowNode e)
        {
            try
            {
                ToggleFloat(e.WindowReference);
            }
            catch (AlgorithmicLayoutCommandException exception)
            {
                PlacementFailed?.Invoke(
                    this,
                    TilingFailedEventArgs.FromException(
                        exception,
                        e.WindowReference));
            }
        }

        private void OnWindowIgnoreProcessRequested(object? sender, WindowNode e)
        {
            App.Current.AppState.Settings.SaveAsync(x =>
            {
                return x with { ProcessIgnoreList = [.. x.ProcessIgnoreList, e.WindowReference.GetCachedProcessName()] };
            });
        }
        private void OnWindowIgnoreClassRequested(object? sender, WindowNode e)
        {
            App.Current.AppState.Settings.SaveAsync(x =>
            {
                return x with { ClassIgnoreList = [.. x.ClassIgnoreList, ((WinMan.Windows.Win32Window)e.WindowReference).ClassName] };
            });
        }

        private void OnTilingPanelMoving(object? sender, PanelNode panel)
        {
            m_currentInteraction = UserInteraction.Moving;
            m_movingPanelNode = panel;
            InvalidateLayout();
        }

        private void OnTilingPanelMoveRequested(object? sender, PanelNode panel)
        {
            m_logger.Information("Panel {Panel} move ended", panel);
            m_currentInteraction = UserInteraction.None;
            m_movingPanelNode = null;

            try
            {
                var isSwapping = IsSwapModifierPressed();
                var pt = m_workspace.CursorLocation;
                MasterSatelliteCommandResult? algorithmicResult = null;
                IVirtualDesktop? algorithmicDesktop = null;
                bool panelMissing = false;
                using (m_backendLock.EnterScope())
                {
                    // Check that panel hasn't disappeared during the move.
                    if (panel.Desktop == null)
                    {
                        panelMissing = true;
                    }
                    else
                    {
                        if (TryResolveAttachedMasterSatelliteDesktopLocked(
                                panel,
                                out var desktop))
                        {
                            algorithmicDesktop = desktop;
                            if (IsMasterSatelliteCapacityTransitionSource(desktop))
                            {
                                algorithmicResult = m_masterSatelliteCommands
                                    .RejectTransitionInProgress();
                            }
                            else if (m_masterSatelliteLifecycle.TryGetState(desktop, out var state))
                            {
                                var plan = m_masterSatelliteDrops.CreatePanelDropRejection(
                                    desktop,
                                    state,
                                    panel);
                                algorithmicResult = m_masterSatelliteDrops.Apply(
                                    m_backend,
                                    desktop,
                                    state,
                                    m_masterSatelliteLifecycle.SettingsSnapshot,
                                    plan);
                            }
                        }
                        else
                        {
                            m_backend.MoveNode(panel, pt, allowNesting: !isSwapping);
                        }
                    }
                }

                if (panelMissing)
                {
                    return;
                }
                if (algorithmicResult != null)
                {
                    CompleteMasterSatelliteCommand(
                        "MousePanelDrop",
                        algorithmicDesktop!,
                        algorithmicResult);
                }
            }
            catch (InvalidWindowReferenceException)
            {
                return;
            }
            catch (TilingFailedException e)
            {
                PlacementFailed?.Invoke(
                    this,
                    TilingFailedEventArgs.FromException(e));
            }
            finally
            {
                m_masterSatelliteDropPreviewWindows = EmptyWindowSet;
                InvalidateLayout();
            }
        }

        private void OnTilingNodePullUpRequested(object? sender, TilingNode node)
        {
            CompleteMasterSatelliteOverlayPullUp(node);
        }

        private void OnDesktopAdded(object? sender, DesktopChangedEventArgs e)
        {
            if (DeferToTilingDispatcher(
                () => OnDesktopAdded(sender, e),
                nameof(OnDesktopAdded)))
            {
                return;
            }
            m_logger.Information("Desktop {Desktop} added to workspace", e.Source);
            var orientation = m_display.Bounds.Width >= m_display.Bounds.Height ? PanelOrientation.Horizontal : PanelOrientation.Vertical;
            MasterSatelliteLifecycleResult? lifecycleResult;
            using (m_backendLock.EnterScope())
            {
                if (m_backend.GetTree(e.Source) == null)
                {
                    m_backend.RegisterDesktop(e.Source, m_display.WorkArea, orientation);
                }
                lifecycleResult = OnMasterSatelliteDesktopAddedLocked(e.Source);
            }
            LogMasterSatelliteLifecycleResult(lifecycleResult);
        }

        private void OnDesktopRemoved(object? sender, DesktopChangedEventArgs e)
        {
            if (DeferToTilingDispatcher(
                () => OnDesktopRemoved(sender, e),
                nameof(OnDesktopRemoved)))
            {
                return;
            }
            m_logger.Information("Desktop {Desktop} removed from workspace", e.Source);
            bool removedRuntimeState;
            using (m_backendLock.EnterScope())
            {
                removedRuntimeState = OnMasterSatelliteDesktopRemovedLocked(e.Source);
                if (m_backend.GetTree(e.Source) != null)
                {
                    m_backend.UnregisterDesktop(e.Source);
                }
            }
            if (removedRuntimeState)
            {
                m_logger.Debug(
                    "Removed Master + Satellites runtime state for desktop {Desktop} on display {Display}",
                    e.Source,
                    m_display);
            }
        }

        private void OnCurrentDesktopChanged(object? sender, CurrentDesktopChangedEventArgs e)
        {
            if (DeferToTilingDispatcher(
                () => OnCurrentDesktopChanged(sender, e),
                nameof(OnCurrentDesktopChanged)))
            {
                return;
            }
            OnMasterSatelliteCurrentDesktopChanged(e.NewDesktop);
            Refresh();
            InvalidateLayout();
        }

        private void OnWindowGotFocus(object? sender, WindowFocusChangedEventArgs e)
        {
            m_dispatcher.BeginInvoke(() =>
            {
                if (m_disposed
                    || !IsCurrentMasterSatelliteWindowGeneration(e.Source))
                {
                    return;
                }
                m_logger.Information("Got focus on {Window}", e.Source.DebugString());
                try
                {
                    bool hideMaximised = false;
                    using (m_backendLock.EnterScope())
                    {
                        if (m_backend.HasWindow(e.Source))
                        {
                            m_logger.Debug("Window {Window} is managed by backend, need to hide all obstructing windows", e.Source.DebugString());
                            // Focused restored windows that are in the tree cause all maximised windows
                            // to be send to the back
                            hideMaximised = true;
                            m_backend.SetFocus(e.Source);
                        }
                        else
                        {
                            m_logger.Debug("Window {Window} is not managed by backend", e.Source.DebugString());
                            return;
                        }
                    }

                    if (hideMaximised)
                    {
                        m_logger.Debug("Moving all obstructing maximised windows to back");
                        var comparer = m_workspace.CreateSnapshotZOrderComparer();
                        foreach (var maximisedWindow in m_workspace.GetCurrentDesktopSnapshot()
                            .Where(x => x.State == WindowState.Maximized && m_display.Bounds.Contains(x.Position.Center))
                            .OrderBy(x => x, comparer))
                        {
                            m_logger.Information("Moving maximised window {Window} to back", maximisedWindow.DebugString());
                            try
                            {
                                if (maximisedWindow.CanReorder)
                                {
                                    maximisedWindow.SendToBack();
                                }
                            }
                            catch (InvalidWindowReferenceException)
                            {
                                continue;
                            }
                            catch (Win32Exception ex)
                            {
                                m_logger.Error(ex, "Moving window {Window} to back failed ({@Metadata})", maximisedWindow.DebugString(), maximisedWindow.GetMetadata());
                                continue;
                            }
                        }
                    }
                    InvalidateLayout();
                }
                catch (InvalidWindowReferenceException)
                {
                    return;
                }
            }, System.Windows.Threading.DispatcherPriority.DataBind);
        }

        private void OnWindowLostFocus(object? sender, WindowFocusChangedEventArgs e)
        {
            if (DeferToTilingDispatcher(
                () => OnWindowLostFocus(sender, e),
                nameof(OnWindowLostFocus)))
            {
                return;
            }
            if (!IsCurrentMasterSatelliteWindowGeneration(e.Source))
            {
                return;
            }

            // This delay is needed to handle the case where the previously focused window
            // loses focus because another window was just created and the OnWindowAdded event
            // observes the new window as focused.
            //m_logger.Information("Lost focus on {Handle}={ProcessName}", e.Source.Handle, e.Source.GetCachedProcessName());
            //await Task.Delay(250);

            //SilenceExceptionIfDead(() =>
            //{
            //    using (m_backendLock.EnterScope())
            //    {
            //        if (m_backend.HasWindow(e.Source))
            //        {
            //            m_logger.Information("Removing focus from {Handle}={ProcessName}", e.Source.Handle, e.Source.GetCachedProcessName());
            //            m_backend.UnsetFocus(e.Source);
            //            InvalidateLayout();
            //        }
            //    }
            //});
            m_currentInteraction = UserInteraction.None;
        }

        private void OnWindowAdded(object? sender, WindowChangedEventArgs e)
        {
            if (DeferToTilingDispatcher(
                () => OnWindowAdded(sender, e),
                nameof(OnWindowAdded)))
            {
                return;
            }
            m_logger.Debug("Window {Window} added to workspace", e.Source.DebugString());
            try
            {
                if (IsRetiredMasterSatelliteWindowGeneration(e.Source)
                    || !e.Source.IsAlive)
                {
                    return;
                }
                bool hasStableHandle = TryRememberMasterSatelliteWindowHandle(
                    e.Source,
                    out var stableWindowHandle,
                    out var replacedWindowGeneration,
                    out var previousWindowGeneration);
                if (hasStableHandle && replacedWindowGeneration)
                {
                    ResetReusedMasterSatelliteWindowHandle(
                        e.Source,
                        stableWindowHandle,
                        previousWindowGeneration!);
                }
                if (hasStableHandle
                    && (!m_algorithmicWindowEvents.TryGetWindow(
                            stableWindowHandle,
                            out var currentWindowGeneration)
                        || !ReferenceEquals(currentWindowGeneration, e.Source)))
                {
                    // A duplicate Added for an already retired wrapper must not
                    // reclaim a numeric HWND now owned by another generation.
                    return;
                }

                bool newlyTracked;
                using (m_windowSetLock.EnterScope())
                {
                    newlyTracked = m_windowSet.Add(e.Source);
                }
                if (newlyTracked)
                {
                    BindEventHandlers(e.Source);
                }
                using (m_newWindowSetLock.EnterScope())
                {
                    m_newWindowSet.Add(e.Source);
                }

                if (hasStableHandle
                    && !m_algorithmicWindowEvents.TryGetKnownDesktop(
                        stableWindowHandle,
                        out _)
                    && TryFindMasterSatelliteWindowDesktop(
                        e.Source,
                        out var firstObservedDesktop))
                {
                    // Workspace window events are broadcast to every display
                    // service. Capture the source desktop before CanManage's
                    // geometry filter so a later cross-display Added can be
                    // recognized as a manual move instead of a new-window
                    // overflow candidate.
                    ObserveMasterSatelliteWindowDesktop(
                        e.Source,
                        stableWindowHandle,
                        firstObservedDesktop);
                }
                if (hasStableHandle)
                {
                    TrackWindowLifetime(e.Source, stableWindowHandle);
                }
                bool isCorrelatedForDisplay = hasStableHandle
                    && m_algorithmicLayoutCoordinator.TryGetRecentTransfer(
                        stableWindowHandle,
                        out var correlatedTransfer)
                    && MasterSatelliteDisplayEligibility.DisplaysMatch(
                        correlatedTransfer.TargetDisplay,
                        m_display);

                if (m_exclusionMatchers.Any(x => x.Matches(e.Source)))
                {
                    MarkWindowFloating(e.Source);
                }

                if (!AutoRegisterWindows && !isCorrelatedForDisplay)
                {
                    return;
                }

                if (m_autoFloatNewWindows && !isCorrelatedForDisplay)
                {
                    MarkWindowFloating(e.Source);
                }

                if ((isCorrelatedForDisplay || CanManage(e.Source))
                    && e.Source.State == WindowState.Restored)
                {
                    m_logger.Information("Window {Window} can be managed, registering with backend ({Display})", e.Source.DebugString(), m_display);
                    m_dispatcher.BeginInvoke(() =>
                    {
                        if (m_disposed
                            || IsRetiredMasterSatelliteWindowGeneration(e.Source)
                            || (hasStableHandle
                                && !IsCurrentMasterSatelliteWindowGeneration(
                                    e.Source,
                                    stableWindowHandle)))
                        {
                            return;
                        }
                        bool trackedForQueuedAdd;
                        using (m_windowSetLock.EnterScope())
                        {
                            trackedForQueuedAdd = m_windowSet.Add(e.Source);
                        }
                        if (trackedForQueuedAdd)
                        {
                            BindEventHandlers(e.Source);
                        }
                        using (m_newWindowSetLock.EnterScope())
                        {
                            m_newWindowSet.Add(e.Source);
                        }
                        bool algorithmicLayoutMatched = false;
                        IntPtr queuedWindowHandle = IntPtr.Zero;
                        try
                        {
                            bool hasQueuedHandle;
                            if (hasStableHandle)
                            {
                                queuedWindowHandle = stableWindowHandle;
                                hasQueuedHandle =
                                    m_algorithmicWindowEvents.TryGetWindow(
                                        queuedWindowHandle,
                                        out var queuedWindowGeneration)
                                    && ReferenceEquals(
                                        queuedWindowGeneration,
                                        e.Source);
                                if (!hasQueuedHandle)
                                {
                                    return;
                                }
                            }
                            else
                            {
                                hasQueuedHandle = TryRememberMasterSatelliteWindowHandle(
                                    e.Source,
                                    out queuedWindowHandle);
                            }
                            bool hasActualDesktop = TryFindMasterSatelliteWindowDesktop(
                                e.Source,
                                out var actualDesktop);
                            bool isStillEligible = (isCorrelatedForDisplay
                                    || AutoRegisterWindows)
                                && e.Source.State == WindowState.Restored
                                && CanManage(e.Source);
                            var destinationDecision = hasQueuedHandle && hasActualDesktop
                                ? HandleMasterSatelliteDestinationAdded(
                                    e.Source,
                                    queuedWindowHandle,
                                    actualDesktop,
                                    canMaterialize: isStillEligible)
                                : new MasterSatelliteDestinationAddDecision(false, false);
                            if (destinationDecision.Consumed)
                            {
                                return;
                            }

                            if (hasQueuedHandle && hasActualDesktop)
                            {
                                m_algorithmicWindowEvents.ObservePotentialManualMove(
                                    queuedWindowHandle,
                                    actualDesktop,
                                    out _);
                            }
                            bool manualDesktopMove = hasQueuedHandle
                                && hasActualDesktop
                                && m_algorithmicWindowEvents.TryGetManualMove(
                                    queuedWindowHandle,
                                    actualDesktop,
                                    out _);

                            // Eligibility may have changed while this DataBind-priority
                            // callback was queued (float, pin, exclusion, or state change).
                            if (!isStillEligible)
                            {
                                // A transfer may already have committed through
                                // synchronous reconciliation. Re-run the ordinary
                                // eligibility removal path so an excluded, pinned,
                                // topmost, minimized, or explicitly floating HWND
                                // cannot remain in a canonical target tree.
                                DetectChanges(e.Source);
                                return;
                            }

                            MasterSatelliteLocalMutationResult? algorithmicPlacement = null;
                            bool genericPlacementFailed = false;
                            try
                            {
                                using (m_backendLock.EnterScope())
                                {
                                    if (m_backend.HasWindow(e.Source))
                                    {
                                        // Added-before-Removed manual desktop
                                        // moves leave the marker intact. The
                                        // source-removal callback will migrate
                                        // the existing node explicitly.
                                        return;
                                    }

                                    // When Added is delivered before the matching
                                    // Removed broadcast, this display already tracked
                                    // the HWND before this callback. Keep the manual
                                    // marker so the later Removed path can preserve an
                                    // attached or floating destination. If Removed was
                                    // delivered first, the add is a fresh registration
                                    // and can consume the marker here.
                                    if (manualDesktopMove && newlyTracked)
                                    {
                                        ConsumeManualMasterSatelliteDesktopMove(
                                            queuedWindowHandle,
                                            actualDesktop);
                                    }

                                    algorithmicPlacement = PlaceMasterSatelliteWindowLocked(
                                        e.Source,
                                        out algorithmicLayoutMatched,
                                        hasActualDesktop ? actualDesktop : null);
                                    if (!algorithmicPlacement.Attempted)
                                    {
                                        var node = hasActualDesktop
                                            ? m_backend.RegisterWindow(
                                                e.Source,
                                                actualDesktop,
                                                maxTreeWidth: m_autoSplitCount)
                                            : m_backend.RegisterWindow(
                                                e.Source,
                                                maxTreeWidth: m_autoSplitCount);
                                        node.Parent!.Padding = GetPanelPaddingRect();
                                        node.Parent!.Spacing = GetPanelSpacing();
                                    }
                                }
                            }
                            catch (NoValidPlacementExistsException)
                            {
                                genericPlacementFailed = true;
                            }

                            if (algorithmicPlacement?.Attempted == true)
                            {
                                CompleteMasterSatellitePlacement(
                                    e.Source,
                                    algorithmicPlacement,
                                    allowExistingDesktopOverflow: !manualDesktopMove
                                        && !destinationDecision.SuppressOverflow);
                                return;
                            }
                            if (genericPlacementFailed)
                            {
                                PlacementFailed?.Invoke(this, new TilingFailedEventArgs(
                                    TilingError.NoValidPlacementExists, e.Source));
                                return;
                            }
                        }
                        catch (InvalidWindowReferenceException ex)
                        {
                            m_logger.Debug(
                                ex,
                                "Queued registration skipped because window handle {WindowHandle} is no longer valid",
                                queuedWindowHandle);
                            return;
                        }
                        catch (Exception ex)
                        {
                            m_logger.Error(
                                ex,
                                "Queued registration failed for window handle {WindowHandle} on display {Display}",
                                queuedWindowHandle,
                                m_display);
                            if (algorithmicLayoutMatched)
                            {
                                // The canonical engine is transactional. Keep the
                                // rejected window out of the tree and apply the local
                                // floating fallback until the coordinator owns overflow.
                                PlacementFailed?.Invoke(this, new TilingFailedEventArgs(
                                    TilingError.NoValidPlacementExists,
                                    e.Source));
                            }
                            return;
                        }

                        InvalidateLayout();
                    }, System.Windows.Threading.DispatcherPriority.DataBind);
                }
            }
            catch (InvalidWindowReferenceException)
            {
                return;
            }
        }

        private void OnWindowRemoved(object? sender, WindowChangedEventArgs e)
        {
            if (DeferToTilingDispatcher(
                () => OnWindowRemoved(sender, e),
                nameof(OnWindowRemoved)))
            {
                return;
            }
            bool isAlive;
            try
            {
                isAlive = e.Source.IsAlive;
            }
            catch (InvalidWindowReferenceException)
            {
                isAlive = false;
            }
            bool hasStableHandle =
                m_algorithmicWindowEvents.TryGetRememberedWindowHandle(
                    e.Source,
                    out var stableWindowHandle);
            if (!hasStableHandle && isAlive)
            {
                try
                {
                    // Removal is observational: it must never install an old
                    // wrapper as the current generation for a reused HWND.
                    stableWindowHandle = e.Source.Handle;
                    hasStableHandle = stableWindowHandle != IntPtr.Zero;
                }
                catch (InvalidWindowReferenceException)
                {
                    stableWindowHandle = IntPtr.Zero;
                }
            }
            IWindow currentWindowGeneration = null!;
            bool hasRegisteredWindowGeneration = hasStableHandle
                && m_algorithmicWindowEvents.TryGetWindow(
                    stableWindowHandle,
                    out currentWindowGeneration);
            bool isCurrentWindowGeneration = hasRegisteredWindowGeneration
                && ReferenceEquals(currentWindowGeneration, e.Source);
            IVirtualDesktop actualDesktop = null!;
            bool hasActualDesktop = isAlive
                && TryFindMasterSatelliteWindowDesktop(e.Source, out actualDesktop);
            bool exactWindowPresentInWorkspace = false;
            bool stillPresentInWorkspace = isAlive
                && IsWindowPresentInWorkspaceSnapshot(
                    e.Source,
                    stableWindowHandle,
                    out exactWindowPresentInWorkspace);
            bool exactWindowTrackedByService;
            using (m_windowSetLock.EnterScope())
            {
                exactWindowTrackedByService = m_windowSet.Contains(e.Source);
            }
            // No generation entry is not, by itself, evidence of HWND reuse: the
            // first conclusive ownership event can arrive after tracking was
            // temporarily inconclusive. Only the exact live object retained by
            // both the workspace and this service may bridge that gap. A
            // registered different wrapper always remains stale.
            bool isUnregisteredCurrentWindowGeneration = hasStableHandle
                && !hasRegisteredWindowGeneration
                && exactWindowPresentInWorkspace
                && exactWindowTrackedByService;
            bool canReconcileWindowGeneration = isCurrentWindowGeneration
                || isUnregisteredCurrentWindowGeneration;
            if (hasStableHandle && !canReconcileWindowGeneration)
            {
                m_logger.Debug(
                    "Ignoring delayed removal for retired window generation {Window}; windowHandle={WindowHandle}",
                    e.Source.DebugString(),
                    stableWindowHandle);
                UnbindEventHandlers(e.Source);
                UntrackWindowLifetime(e.Source);
                using (m_savedLocationsLock.EnterScope())
                {
                    m_savedLocations.Remove(e.Source);
                }
                using (m_ignoreRepositionSetLock.EnterScope())
                {
                    m_ignoreRepositionSet.Remove(e.Source);
                }
                using (m_newWindowSetLock.EnterScope())
                {
                    m_newWindowSet.Remove(e.Source);
                }
                using (m_windowSetLock.EnterScope())
                {
                    m_windowSet.Remove(e.Source);
                }
                CancelMasterSatelliteIncomingOwnershipProbe(
                    e.Source,
                    stableWindowHandle);
                m_algorithmicWindowEvents.Forget(e.Source);
                return;
            }

            if (isCurrentWindowGeneration
                && m_algorithmicLayoutCoordinator.TryGetRecentTransfer(
                    stableWindowHandle,
                    out var correlatedTransfer)
                && MasterSatelliteDisplayEligibility.DisplaysMatch(
                    correlatedTransfer.SourceDisplay,
                    m_display))
            {
                bool knownAtTarget = m_algorithmicWindowEvents.TryGetKnownDesktop(
                        stableWindowHandle,
                        out var knownDesktop)
                    && MasterSatelliteDisplayEligibility.DesktopsMatch(
                        knownDesktop,
                        correlatedTransfer.TargetDesktop);
                bool actualAtTarget = hasActualDesktop
                    && MasterSatelliteDisplayEligibility.DesktopsMatch(
                        actualDesktop,
                        correlatedTransfer.TargetDesktop);
                bool attachedAtTarget;
                using (m_backendLock.EnterScope())
                {
                    attachedAtTarget = m_backend.GetTree(
                            correlatedTransfer.TargetDesktop)?
                        .FindNode(e.Source) != null;
                }
                bool confirmedAutomaticSourceRemoval =
                    !correlatedTransfer.IsTerminal
                        ? !hasActualDesktop || actualAtTarget
                        : correlatedTransfer.State
                                == PendingWindowTransferState.Committed
                            && attachedAtTarget
                            && (actualAtTarget
                                || (!hasActualDesktop && knownAtTarget));
                if (stillPresentInWorkspace
                    && confirmedAutomaticSourceRemoval
                    && m_algorithmicWindowTransfers.ObserveSourceRemoved(
                        stableWindowHandle,
                        correlatedTransfer.SourceDesktop,
                        out var observedTransfer))
                {
                    m_logger.Debug(
                        "Observed correlated source removal without clearing destination ownership; correlation={CorrelationId}, windowHandle={WindowHandle}, source={SourceDesktop}, target={TargetDesktop}, destinationObserved={DestinationObserved}",
                        observedTransfer.CorrelationId,
                        stableWindowHandle,
                        observedTransfer.SourceDesktop,
                        observedTransfer.TargetDesktop,
                        observedTransfer.DestinationAddedObserved);
                    return;
                }
                if (!isAlive)
                {
                    m_algorithmicLayoutCoordinator.WindowClosed(stableWindowHandle);
                }
            }

            if (canReconcileWindowGeneration
                && stillPresentInWorkspace
                && hasActualDesktop)
            {
                ObserveMasterSatelliteWindowDesktop(
                    e.Source,
                    stableWindowHandle,
                    actualDesktop);
            }
            if (canReconcileWindowGeneration
                && stillPresentInWorkspace
                && hasActualDesktop
                && TryMigrateManualMasterSatelliteWindow(
                    e.Source,
                    stableWindowHandle,
                    actualDesktop))
            {
                return;
            }
            if (ReferenceEquals(sender, m_workspace)
                && stillPresentInWorkspace
                && hasActualDesktop)
            {
                IVirtualDesktop attachedDesktop = null!;
                bool hasAttachedDesktop;
                using (m_backendLock.EnterScope())
                {
                    hasAttachedDesktop = TryResolveAttachedWindowDesktopLocked(
                        e.Source,
                        out attachedDesktop);
                }
                if (hasAttachedDesktop
                    && MasterSatelliteDisplayEligibility.DesktopsMatch(
                        attachedDesktop,
                        actualDesktop))
                {
                    m_logger.Debug(
                        "Ignoring stale workspace removal for window handle {WindowHandle}; this display already owns the actual destination desktop {Desktop}",
                        stableWindowHandle,
                        actualDesktop);
                    return;
                }
            }
            m_logger.Information("Window {Window} removed from workspace", e.Source.DebugString());

            UnbindEventHandlers(e.Source);
            using (m_savedLocationsLock.EnterScope())
            {
                m_savedLocations.Remove(e.Source);
            }
            using (m_ignoreRepositionSetLock.EnterScope())
            {
                m_ignoreRepositionSet.Remove(e.Source);
            }
            MasterSatelliteLocalMutationResult? algorithmicRemoval = null;
            bool backendChanged = false;
            using (m_backendLock.EnterScope())
            {
                if (m_backend.HasWindow(e.Source))
                {
                    m_logger.Debug("Unregistering window {Window} from backend", e.Source.DebugString());
                    algorithmicRemoval = RemoveMasterSatelliteWindowLocked(
                        e.Source,
                        preserveOriginalPosition: false);
                    if (!algorithmicRemoval.Attempted)
                    {
                        m_backend.UnregisterWindow(e.Source);
                        backendChanged = true;
                    }
                    else
                    {
                        backendChanged = algorithmicRemoval.Succeeded;
                        if (!backendChanged
                            && algorithmicRemoval.Disposition == MasterSatelliteLocalMutationDisposition.Rejected)
                        {
                            backendChanged = RecoverRejectedMasterSatelliteRemovalLocked(
                                e.Source,
                                algorithmicRemoval);
                        }
                    }
                }
                // A window may close after an earlier temporary canonical removal
                // (for example while floating), when it is no longer present in a
                // tree. Final removal must still release retained original geometry.
                m_backend.ForgetMasterSatelliteOriginalPosition(e.Source);
            }
            if (algorithmicRemoval != null)
            {
                LogMasterSatelliteRemoval(e.Source, algorithmicRemoval);
            }
            if (backendChanged)
            {
                InvalidateLayout();
            }
            if (!isAlive)
            {
                RetireMasterSatelliteWindowGeneration(e.Source);
                UntrackWindowLifetime(e.Source);
                if (isCurrentWindowGeneration)
                {
                    ForgetClosedFloatingWindow(e.Source, stableWindowHandle);
                }
            }
            using (m_newWindowSetLock.EnterScope())
            {
                m_newWindowSet.Remove(e.Source);
            }
            using (m_windowSetLock.EnterScope())
            {
                m_windowSet.Remove(e.Source);
            }
            if (hasStableHandle)
            {
                CancelMasterSatelliteIncomingOwnershipProbe(
                    e.Source,
                    stableWindowHandle);
                if (isCurrentWindowGeneration)
                {
                    m_arrangeFailureNotifications.Forget(stableWindowHandle);
                    if (!isAlive)
                    {
                        m_algorithmicLayoutCoordinator.WindowClosed(
                            stableWindowHandle);
                    }
                }
                if (!isAlive || !isCurrentWindowGeneration)
                {
                    // Exact-wrapper cleanup preserves a newer generation that
                    // may already own the same numeric HWND.
                    m_algorithmicWindowEvents.Forget(e.Source);
                }
            }
        }

        private bool IsWindowPresentInWorkspaceSnapshot(
            IWindow window,
            IntPtr stableWindowHandle,
            out bool exactWindowPresent)
        {
            exactWindowPresent = false;
            try
            {
                foreach (var candidate in m_workspace.GetSnapshot())
                {
                    if (ReferenceEquals(candidate, window))
                    {
                        exactWindowPresent = true;
                        return true;
                    }
                    if (stableWindowHandle == IntPtr.Zero)
                    {
                        continue;
                    }
                    try
                    {
                        if (candidate.Handle == stableWindowHandle)
                        {
                            return true;
                        }
                    }
                    catch (InvalidWindowReferenceException)
                    {
                        // A different snapshot entry died while it was inspected.
                    }
                }
            }
            catch (Exception ex)
            {
                m_logger.Debug(
                    ex,
                    "Could not snapshot workspace membership for removed window handle {WindowHandle}",
                    stableWindowHandle);
            }
            return false;
        }

        private void DoWindowMove(IWindow window)
        {
            if (TryGetMasterSatelliteCapacityTransitionDesktop(
                    window,
                    out var transitionDesktop))
            {
                RejectMasterSatelliteCapacityTransitionMutation(
                    "MouseWindowDrop",
                    transitionDesktop);
            }
            var isSwapping = IsSwapModifierPressed();
            var pt = m_workspace.CursorLocation;
            bool canTileFloatingWindow = IsFloatingWindow(window)
                && m_display.WorkArea.Contains(pt)
                && !m_exclusionMatchers.Any(matcher => matcher.Matches(window))
                && CanManage(window, ignoreFloating: true);
            IVirtualDesktop floatingDesktop = null!;
            bool hasFloatingDesktop = canTileFloatingWindow
                && TryFindMasterSatelliteWindowDesktop(
                    window,
                    out floatingDesktop);
            MasterSatelliteCommandResult? algorithmicResult = null;
            IVirtualDesktop? algorithmicDesktop = null;
            bool attemptAlgorithmicFloatingDrop = false;
            using (m_backendLock.EnterScope())
            {
                if (m_backend.HasWindow(window))
                {
                    m_logger.Debug("Window {Window} size is unchanged, attempting to insert window at {Position}", window.DebugString(), pt);
                    if (TryResolveAttachedMasterSatelliteDesktopLocked(
                            window,
                            out var desktop))
                    {
                        algorithmicDesktop = desktop;
                        if (m_masterSatelliteLifecycle.TryGetState(desktop, out var state))
                        {
                            algorithmicResult = m_masterSatelliteDrops
                                .ApplyWindowDropAtPointer(
                                    m_backend,
                                    desktop,
                                    state,
                                    m_masterSatelliteLifecycle.SettingsSnapshot,
                                    window,
                                    pt);
                            if (algorithmicResult.Succeeded)
                            {
                                m_backend.SetFocus(window);
                            }
                        }
                    }
                    else
                    {
                        m_backend.MoveWindow(window, pt, allowNesting: !isSwapping);
                        m_backend.SetFocus(window);
                    }
                }
                else
                {
                    attemptAlgorithmicFloatingDrop = hasFloatingDesktop
                        && m_masterSatelliteCommands.IsActive(floatingDesktop);
                }
            }
            if (algorithmicResult != null)
            {
                CompleteMasterSatelliteCommand(
                    "MouseWindowDrop",
                    algorithmicDesktop!,
                    algorithmicResult);
            }
            else if (attemptAlgorithmicFloatingDrop)
            {
                bool wasFloating = MarkWindowTiled(window);

                if (wasFloating)
                {
                    try
                    {
                        MasterSatelliteLocalMutationResult placement;
                        using (m_backendLock.EnterScope())
                        {
                            placement = PlaceMasterSatelliteWindowLocked(
                                window,
                                out _,
                                floatingDesktop);
                        }
                        if (placement.Attempted)
                        {
                            CompleteMasterSatellitePlacement(
                                window,
                                placement,
                                allowExistingDesktopOverflow: true);
                        }
                        else
                        {
                            MarkWindowFloating(window);
                        }
                    }
                    catch
                    {
                        MarkWindowFloating(window);
                        throw;
                    }
                }
            }
        }

        private void OnWindowPositionChangeEnd(object? sender, WindowPositionChangedEventArgs e)
        {
            if (!m_active)
                return;

            if (DeferToTilingDispatcher(
                () => OnWindowPositionChangeEnd(sender, e),
                nameof(OnWindowPositionChangeEnd)))
            {
                return;
            }
            if (!IsCurrentMasterSatelliteWindowGeneration(e.Source))
            {
                return;
            }

            try
            {
                bool shouldApplyMove = m_delayReposition
                    || IsMasterSatelliteWindow(e.Source)
                    || (IsCurrentMasterSatelliteLayoutActive()
                        && IsFloatingWindow(e.Source));
                if (shouldApplyMove
                    && m_currentInteraction == UserInteraction.Moving)
                {
                    try
                    {
                        DoWindowMove(e.Source);
                    }
                    catch (InvalidWindowReferenceException exception)
                    {
                        m_logger.Debug(
                            exception,
                            "Window move ended after its window reference became invalid");
                    }
                    catch (TilingFailedException ex)
                    {
                        PlacementFailed?.Invoke(
                            this,
                            TilingFailedEventArgs.FromException(ex, e.Source));
                    }
                }

                m_logger.Information("Window {Window} move ended", e.Source.DebugString());
            }
            finally
            {
                m_masterSatelliteDropPreviewWindows = EmptyWindowSet;
                InvalidateLayout();
                using (m_ignoreRepositionSetLock.EnterScope())
                {
                    m_ignoreRepositionSet.Remove(e.Source);
                }
                m_movingWindow = null;
                m_currentInteraction = UserInteraction.None;
            }
        }

        private TimeSpan m_lastPlacementFailed = TimeSpan.Zero;
        private TimeSpan m_lastWindowPositionChanged = TimeSpan.Zero;

        private void OnWindowPositionChanged(object? sender, WindowPositionChangedEventArgs e)
        {
            if (!m_active)
                return;

            if (DeferToTilingDispatcher(
                () => OnWindowPositionChanged(sender, e),
                nameof(OnWindowPositionChanged)))
            {
                return;
            }
            if (!IsCurrentMasterSatelliteWindowGeneration(e.Source))
            {
                return;
            }

            if (m_sw.Elapsed - m_lastPlacementFailed <= TimeSpan.FromMilliseconds(100))
            {
                return;
            }

            if (m_currentInteraction != UserInteraction.None && m_sw.Elapsed - m_lastWindowPositionChanged <= TimeSpan.FromSeconds(1.0 / m_display.RefreshRate))
            {
                return;
            }
            m_lastWindowPositionChanged = m_sw.Elapsed;

            using (m_ignoreRepositionSetLock.EnterScope())
            {
                if (!m_ignoreRepositionSet.Contains(e.Source))
                {
                    // Some other event might have resulted in the movement of the window.
                    // Do not call DetectChanges under the lock, to avoid deadlock.
                    QueueTilingDispatcherAction(
                        () => DetectChanges(e.Source),
                        "detecting changes after a window position event");
                    return;
                }
            }

            bool backendHasWindow;
            using (m_backendLock.EnterScope())
            {
                backendHasWindow = m_backend.HasWindow(e.Source);
            }
            if (!backendHasWindow
                && (!IsFloatingWindow(e.Source)
                    || !IsCurrentMasterSatelliteLayoutActive()))
            {
                return;
            }

            if (m_currentInteraction == UserInteraction.Starting)
            {
                if (e.OldPosition.Size == e.NewPosition.Size)
                {
                    m_currentInteraction = UserInteraction.Moving;
                }
                else
                {
                    m_currentInteraction = UserInteraction.Resizing;
                }
            }

            try
            {
                DetectChanges(e.Source);

                if (e.NewPosition == e.OldPosition)
                {
                    return;
                }

                if (e.NewPosition.Width == e.OldPosition.Width && e.NewPosition.Height == e.OldPosition.Height)
                {
                    if (!m_delayReposition && !IsCurrentMasterSatelliteLayoutActive())
                    {
                        DoWindowMove(e.Source);
                    }
                }
                else
                {
                    DesktopTree? treeToUpdate = null;
                    MasterSatelliteCommandResult? algorithmicResize = null;
                    IVirtualDesktop? algorithmicResizeDesktop = null;
                    using (m_backendLock.EnterScope())
                    {
                        if (m_backend.HasWindow(e.Source))
                        {
                            var node = m_backend.FindWindow(e.Source);
                            var oldPosition = node!.ComputedContentRectangle;
                            var frame = e.Source.FrameMargins;
                            var adjustedRect = new Rectangle(
                                left: oldPosition.Left - frame.Left,
                                top: oldPosition.Top - frame.Top,
                                right: oldPosition.Right + frame.Right,
                                bottom: oldPosition.Bottom + frame.Bottom);

                            m_logger.Debug("Window {Window} size is different, attempting to resize window from {OldPosition} to {NewPosition}", e.Source.DebugString(), adjustedRect, e.NewPosition);
                            if (TryResolveAttachedMasterSatelliteDesktopLocked(
                                    e.Source,
                                    out var desktop))
                            {
                                algorithmicResizeDesktop = desktop;
                                algorithmicResize = m_masterSatelliteCommands.ResizeWindowByPixels(
                                    m_backend,
                                    desktop,
                                    e.Source,
                                    e.NewPosition.Width - adjustedRect.Width);
                            }
                            else
                            {
                                m_backend.ResizeWindow(e.Source, e.NewPosition, adjustedRect);
                                treeToUpdate = node.Desktop!;
                            }
                        }
                    }
                    if (algorithmicResize != null)
                    {
                        CompleteMasterSatelliteCommand(
                            "MouseResizeMaster",
                            algorithmicResizeDesktop!,
                            algorithmicResize);
                    }
                    else if (treeToUpdate != null)
                    {
                        UpdateTree(treeToUpdate);
                    }
                }
                InvalidateLayout();
            }
            catch (InvalidWindowReferenceException)
            {
                return;
            }
            catch (TilingFailedException ex)
            {
                if (m_sw.Elapsed - m_lastPlacementFailed <= TimeSpan.FromSeconds(1))
                {
                    return;
                }
                m_lastPlacementFailed = m_sw.Elapsed;
                PlacementFailed?.Invoke(
                    this,
                    TilingFailedEventArgs.FromException(ex, e.Source));
            }
            finally
            {
                Unfreeze();
            }
        }

        private void OnWindowTopmostChanged(object? sender, WindowTopmostChangedEventArgs e)
        {
            if (!m_active)
                return;

            if (DeferToTilingDispatcher(
                () => OnWindowTopmostChanged(sender, e),
                nameof(OnWindowTopmostChanged)))
            {
                return;
            }
            if (!IsCurrentMasterSatelliteWindowGeneration(e.Source))
            {
                return;
            }

            try
            {
                m_logger.Verbose("Changed topmost of window {Window}", e.Source.DebugString());
                DetectChanges(e.Source);
            }
            catch (InvalidWindowReferenceException)
            {
                return;
            }
        }

        private void OnWindowStateChanged(object? sender, WindowStateChangedEventArgs e)
        {
            if (!m_active)
                return;

            if (DeferToTilingDispatcher(
                () => OnWindowStateChanged(sender, e),
                nameof(OnWindowStateChanged)))
            {
                return;
            }
            if (!IsCurrentMasterSatelliteWindowGeneration(e.Source))
            {
                return;
            }

            void UnregisterAndSaveLocation()
            {
                MasterSatelliteLocalMutationResult? algorithmicRemoval = null;
                bool backendChanged = false;
                using (m_backendLock.EnterScope())
                {
                    var window = m_backend.FindWindow(e.Source);
                    if (window != null)
                    {
                        algorithmicRemoval = RemoveMasterSatelliteWindowLocked(
                            e.Source,
                            preserveOriginalPosition: true);
                        if (!algorithmicRemoval.Attempted)
                        {
                            using (m_savedLocationsLock.EnterScope())
                            {
                                // Be resilient to multiple OnWindowStateChanged events happening one after the other
                                m_savedLocations[e.Source] = new NodeLocation(window);
                            }
                            m_backend.UnregisterWindow(e.Source);
                            backendChanged = true;
                        }
                        else
                        {
                            backendChanged = algorithmicRemoval.Succeeded;
                            if (!backendChanged
                                && algorithmicRemoval.Disposition == MasterSatelliteLocalMutationDisposition.Rejected)
                            {
                                backendChanged = RecoverRejectedMasterSatelliteRemovalLocked(
                                    e.Source,
                                    algorithmicRemoval);
                            }
                        }
                    }
                }
                if (algorithmicRemoval != null)
                {
                    LogMasterSatelliteRemoval(e.Source, algorithmicRemoval);
                }
                if (backendChanged)
                {
                    InvalidateLayout();
                }
                DetectChanges(e.Source);
            }

            void RegisterAndRestoreLocation()
            {
                NodeLocation? savedLocation;
                using (m_savedLocationsLock.EnterScope())
                {
                    if (m_savedLocations.TryGetValue(e.Source, out savedLocation))
                    {
                        m_savedLocations.Remove(e.Source);
                    }
                }

                bool hasActualDesktop = TryFindMasterSatelliteWindowDesktop(
                    e.Source,
                    out var actualDesktop);

                void RegisterInTopLevelPanel()
                {
                    try
                    {
                        var window = hasActualDesktop
                            ? m_backend.RegisterWindow(
                                e.Source,
                                actualDesktop,
                                maxTreeWidth: m_autoSplitCount)
                            : m_backend.RegisterWindow(
                                e.Source,
                                maxTreeWidth: m_autoSplitCount);
                        window.Parent!.Padding = GetPanelPaddingRect();
                        window.Parent!.Spacing = GetPanelSpacing();
                    }
                    catch (WindowAlreadyRegisteredException)
                    {
                        // Window might be already registered!
                        var registered = m_backend.FindWindow(e.Source);
                        if (registered == null)
                        {
                            throw;
                        }
                        // This is clearly a race condition with DetectChanges dirty checking.
                    }
                }

                void RegisterInSavedPanel(NodeLocation location)
                {
                    WindowNode window;
                    try
                    {
                        window = m_backend.RegisterWindow(e.Source, location.Parent);
                        window.Parent!.Padding = GetPanelPaddingRect();
                        window.Parent!.Spacing = GetPanelSpacing();
                    }
                    catch (WindowAlreadyRegisteredException)
                    {
                        // Window might be already registered!
                        var registered = m_backend.FindWindow(e.Source);
                        if (registered == null)
                        {
                            throw;
                        }
                        // This is clearly a race condition with DetectChanges dirty checking.
                        window = registered;
                    }

                    window.Parent!.Detach(window);
                    int childCount = location.Parent.Children.Count;
                    int index = Math.Min(location.Index, childCount);
                    location.Parent.Attach(index, window);

                    // Restore size
                    if (window.Parent is GridLikeNode gridNode)
                    {
                        if (window.Desktop is DesktopTree tree)
                        {
                            // Assign ComputedRectangle to that Resize will work.
                            try
                            {
                                tree.Measure();
                                tree.Arrange();
                            }
                            catch (UnsatisfiableFlexConstraintsException)
                            {
                            }
                            if (gridNode.CanResizeInOrientation(PanelOrientation.Horizontal))
                            {
                                gridNode.ResizeTo(window, location.ComputedRectangle.Width, GrowDirection.Both);
                            }
                            else
                            {
                                gridNode.ResizeTo(window, location.ComputedRectangle.Height, GrowDirection.Both);
                            }
                        }
                    }
                }

                MasterSatelliteLocalMutationResult? algorithmicPlacement = null;
                bool algorithmicLayoutMatched = false;
                Exception? algorithmicPlacementException = null;
                bool genericPlacementFailed = false;
                try
                {
                    using (m_backendLock.EnterScope())
                    {
                        algorithmicPlacement = PlaceMasterSatelliteWindowLocked(
                            e.Source,
                            out algorithmicLayoutMatched,
                            hasActualDesktop ? actualDesktop : null);
                        bool savedParentMatchesActualDesktop =
                            savedLocation?.Parent?.Desktop != null
                            && (!hasActualDesktop
                                || ReferenceEquals(
                                    savedLocation.Parent.Desktop,
                                    m_backend.GetTree(actualDesktop)));
                        if (!algorithmicPlacement.Attempted
                            && savedParentMatchesActualDesktop
                            && savedLocation is NodeLocation location)
                        {
                            try
                            {
                                RegisterInSavedPanel(location);
                            }
                            catch (NoValidPlacementExistsException)
                            {
                                RegisterInTopLevelPanel();
                            }
                        }
                        else if (!algorithmicPlacement.Attempted)
                        {
                            RegisterInTopLevelPanel();
                        }
                    }
                }
                catch (NoValidPlacementExistsException)
                {
                    genericPlacementFailed = true;
                }
                catch (Exception ex) when (algorithmicLayoutMatched)
                {
                    algorithmicPlacementException = ex;
                }
                if (algorithmicPlacement?.Attempted == true)
                {
                    CompleteMasterSatellitePlacement(e.Source, algorithmicPlacement);
                }
                if (algorithmicPlacementException != null)
                {
                    m_logger.Error(
                        algorithmicPlacementException,
                        "Master + Satellites restore placement failed unexpectedly for window {Window}",
                        e.Source.DebugString());
                    PlacementFailed?.Invoke(this, new TilingFailedEventArgs(
                        TilingError.NoValidPlacementExists,
                        e.Source));
                }
                if (genericPlacementFailed)
                {
                    PlacementFailed?.Invoke(this, new TilingFailedEventArgs(
                        TilingError.NoValidPlacementExists,
                        e.Source));
                }
                DetectChanges(e.Source);
            }

            try
            {
                m_logger.Information("Changed state of window {Window} to {NewState}", e.Source.DebugString(), e.NewState);

                try
                {
                    // Window is now minimized or maximized but was restored
                    if ((e.NewState == WindowState.Maximized || e.NewState == WindowState.Minimized)
                        && e.OldState == WindowState.Restored)
                    {
                        UnregisterAndSaveLocation();
                    }
                    // Window is now restored
                    else if (e.NewState == WindowState.Restored
                        && (e.OldState == WindowState.Maximized || e.OldState == WindowState.Minimized))
                    {
                        if (!CanManage(e.Source))
                        {
                            return;
                        }
                        RegisterAndRestoreLocation();
                    }
                    else
                    {
                        DetectChanges(e.Source);
                    }
                }
                catch (InvalidWindowReferenceException)
                {
                    return;
                }
                catch (WindowAlreadyRegisteredException)
                {
                    return;
                }

                if (Equals(m_workspace.FocusedWindow, sender))
                {
                    m_logger.Debug("Window {Window} is also focused, calling OnWindowGotFocus", e.Source.DebugString());
                    // This is to update focus when a maximised window is restored.
                    OnWindowGotFocus(e.Source, new WindowFocusChangedEventArgs(e.Source, true));
                }
            }
            catch (InvalidWindowReferenceException)
            {
                return;
            }
        }

        private void OnWindowPositionChangeStart(object? sender, WindowPositionChangedEventArgs e)
        {
            if (!m_active)
                return;

            if (DeferToTilingDispatcher(
                () => OnWindowPositionChangeStart(sender, e),
                nameof(OnWindowPositionChangeStart)))
            {
                return;
            }
            if (!IsCurrentMasterSatelliteWindowGeneration(e.Source))
            {
                return;
            }

            using (m_ignoreRepositionSetLock.EnterScope())
            {
                m_ignoreRepositionSet.Add(e.Source);
            }
            m_masterSatelliteDropPreviewWindows = EmptyWindowSet;
            m_movingWindow = e.Source;
            m_currentInteraction = UserInteraction.Starting;
        }

        private void OnTilingNodeFocusRequested(object? sender, TilingNode e)
        {
            using (m_backendLock.EnterScope())
            {
                var windowNode = e.Windows.FirstOrDefault();
                try
                {
                    if (windowNode != null)
                    {
                        if (FocusHelper.ForceActivate(windowNode.WindowReference.Handle))
                        {
                            m_backend.SetFocus(windowNode.WindowReference);
                        }
                    }
                }
                catch (InvalidWindowReferenceException)
                {
                    return;
                }
            }
        }

        private void OnTilingNodeCloseRequested(object? sender, TilingNode e)
        {
            foreach (var window in e.Windows.ToList())
            {
                try
                {
                    if (window.WindowReference.CanClose)
                    {
                        window.WindowReference.Close();
                    }
                }
                catch (InvalidWindowReferenceException)
                {
                    // Ignore
                }
                catch (Win32Exception)
                {
                    // Ignore
                    // TODO: Show toast
                }
            }
        }

        private void BindEventHandlers(IWindow window)
        {
            window.StateChanged += OnWindowStateChanged;
            window.PositionChangeStart += OnWindowPositionChangeStart;
            window.PositionChangeEnd += OnWindowPositionChangeEnd;
            window.PositionChanged += OnWindowPositionChanged;
            window.GotFocus += OnWindowGotFocus;
            window.LostFocus += OnWindowLostFocus;
            window.TopmostChanged += OnWindowTopmostChanged;
        }

        private void TrackWindowLifetime(IWindow window, IntPtr stableWindowHandle)
        {
            if (stableWindowHandle == IntPtr.Zero
                || m_windowLifetimeHandles.ContainsKey(window))
            {
                return;
            }
            window.Destroyed += OnTrackedWindowDestroyed;
            if (!m_windowLifetimeHandles.TryAdd(window, stableWindowHandle))
            {
                window.Destroyed -= OnTrackedWindowDestroyed;
            }
        }

        private void UntrackWindowLifetime(IWindow window)
        {
            if (m_windowLifetimeHandles.Remove(window))
            {
                window.Destroyed -= OnTrackedWindowDestroyed;
            }
        }

        private void UntrackAllWindowLifetimes()
        {
            foreach (var window in m_windowLifetimeHandles.Keys.ToArray())
            {
                window.Destroyed -= OnTrackedWindowDestroyed;
            }
            m_windowLifetimeHandles.Clear();
        }

        private void OnTrackedWindowDestroyed(
            object? sender,
            WindowChangedEventArgs e)
        {
            if (DeferToTilingDispatcher(
                () => OnTrackedWindowDestroyed(sender, e),
                nameof(OnTrackedWindowDestroyed)))
            {
                return;
            }

            var window = e.Source;
            if (!m_windowLifetimeHandles.TryGetValue(
                    window,
                    out var stableWindowHandle)
                && sender is IWindow senderWindow)
            {
                window = senderWindow;
                m_windowLifetimeHandles.TryGetValue(
                    window,
                    out stableWindowHandle);
            }
            UntrackWindowLifetime(window);
            if (stableWindowHandle == IntPtr.Zero)
            {
                return;
            }

            bool isCurrentWindowGeneration =
                m_algorithmicWindowEvents.TryGetWindow(
                    stableWindowHandle,
                    out var currentWindow)
                && ReferenceEquals(currentWindow, window);
            CancelMasterSatelliteIncomingOwnershipProbe(
                window,
                stableWindowHandle);
            if (!isCurrentWindowGeneration)
            {
                // Retain the exact wrapper-to-handle entry until a delayed
                // WindowRemoved callback can identify this as the retired HWND
                // generation without consulting the now-reused native handle.
                return;
            }

            RetireMasterSatelliteWindowGeneration(window);
            // Keep the exact wrapper-to-handle entry until WindowRemoved or a
            // replacement Added arrives. If Windows reuses the HWND between
            // those events, the replacement must still discover and purge the
            // dead canonical node before registering itself.
            m_workspaceFloatingWindows.Remove(stableWindowHandle);
            m_arrangeFailureNotifications.Forget(stableWindowHandle);
            m_algorithmicLayoutCoordinator.WindowClosed(stableWindowHandle);
        }

        private void UnbindEventHandlers(IWindow window)
        {
            window.StateChanged -= OnWindowStateChanged;
            window.PositionChangeStart -= OnWindowPositionChangeStart;
            window.PositionChangeEnd -= OnWindowPositionChangeEnd;
            window.PositionChanged -= OnWindowPositionChanged;
            window.GotFocus -= OnWindowGotFocus;
            window.LostFocus -= OnWindowLostFocus;
            window.TopmostChanged -= OnWindowTopmostChanged;
        }

        private bool IsSwapModifierPressed()
        {
            static bool GetState() => Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
            if (m_dispatcher.CheckAccess())
            {
                return GetState();
            }
            else
            {
                return m_dispatcher.Invoke(GetState, System.Windows.Threading.DispatcherPriority.Send);
            }
        }

        private bool DetectChanges(
            IWindow window,
            bool allowExistingDesktopOverflow = false,
            IVirtualDesktop? observedDesktop = null)
        {
            if (IsRetiredMasterSatelliteWindowGeneration(window))
            {
                return false;
            }
            try
            {
                m_logger.Verbose("Dirty checking for changes with window {Window}", window.DebugString());
                if (window.State == WindowState.Restored && CanManage(window))
                {
                    if (!AutoRegisterWindows)
                    {
                        return false;
                    }

                    IVirtualDesktop actualDesktop = null!;
                    bool hasActualDesktop;
                    if (observedDesktop != null)
                    {
                        actualDesktop = observedDesktop;
                        hasActualDesktop = true;
                    }
                    else
                    {
                        hasActualDesktop = TryFindMasterSatelliteWindowDesktop(
                            window,
                            out actualDesktop);
                    }
                    bool hasStableHandle = TryRememberMasterSatelliteWindowHandle(
                        window,
                        out var stableWindowHandle);
                    bool suppressOverflow = false;
                    bool manualDesktopMove = false;
                    if (hasStableHandle && hasActualDesktop)
                    {
                        var destinationDecision = HandleMasterSatelliteDestinationAdded(
                            window,
                            stableWindowHandle,
                            actualDesktop);
                        if (destinationDecision.Consumed)
                        {
                            return true;
                        }
                        suppressOverflow = destinationDecision.SuppressOverflow;

                        m_algorithmicWindowEvents.ObservePotentialManualMove(
                            stableWindowHandle,
                            actualDesktop,
                            out _);
                        manualDesktopMove = m_algorithmicWindowEvents.TryGetManualMove(
                            stableWindowHandle,
                            actualDesktop,
                            out _);

                        IVirtualDesktop attachedDesktop = null!;
                        bool hasAttachedDesktop;
                        using (m_backendLock.EnterScope())
                        {
                            hasAttachedDesktop = TryResolveAttachedWindowDesktopLocked(
                                window,
                                out attachedDesktop);
                        }
                        bool belongsToPendingAutomaticTransfer =
                            m_algorithmicLayoutCoordinator.TryGetRecentTransfer(
                                stableWindowHandle,
                                out var transfer)
                            && !transfer.IsTerminal
                            && MasterSatelliteDisplayEligibility.DesktopsMatch(
                                transfer.TargetDesktop,
                                actualDesktop);
                        if (hasAttachedDesktop
                            && !belongsToPendingAutomaticTransfer
                            && !MasterSatelliteDisplayEligibility.DesktopsMatch(
                                attachedDesktop,
                                actualDesktop))
                        {
                            m_algorithmicWindowEvents.RecordManualMove(
                                stableWindowHandle,
                                attachedDesktop,
                                actualDesktop,
                                out _);
                            manualDesktopMove = true;
                            if (TryMigrateManualMasterSatelliteWindow(
                                    window,
                                    stableWindowHandle,
                                    actualDesktop))
                            {
                                return true;
                            }
                        }
                    }

                    MasterSatelliteLocalMutationResult? algorithmicPlacement = null;
                    MasterSatelliteLocalMutationResult? cleanupRemoval = null;
                    bool backendChanged = false;
                    bool genericPlacementFailed = false;
                    bool algorithmicLayoutMatched = false;
                    Exception? algorithmicPlacementException = null;
                    try
                    {
                        using (m_backendLock.EnterScope())
                        {
                            try
                            {
                                if (!m_backend.HasWindow(window))
                                {
                                    m_logger.Debug("Window {Window} can be managed, but is not registered with backend, registering now", window.DebugString());
                                    algorithmicPlacement = PlaceMasterSatelliteWindowLocked(
                                        window,
                                        out algorithmicLayoutMatched,
                                        hasActualDesktop ? actualDesktop : null);
                                    if (!algorithmicPlacement.Attempted)
                                    {
                                        var newNode = hasActualDesktop
                                            ? m_backend.RegisterWindow(
                                                window,
                                                actualDesktop,
                                                maxTreeWidth: m_autoSplitCount)
                                            : m_backend.RegisterWindow(
                                                window,
                                                maxTreeWidth: m_autoSplitCount);
                                        newNode.Parent!.Padding = GetPanelPaddingRect();
                                        newNode.Parent!.Spacing = GetPanelSpacing();
                                        backendChanged = true;
                                    }
                                    else
                                    {
                                        backendChanged = algorithmicPlacement.Succeeded;
                                    }
                                }
                            }
                            catch (InvalidWindowReferenceException)
                            {
                                if (m_backend.HasWindow(window))
                                {
                                    cleanupRemoval = RemoveMasterSatelliteWindowLocked(
                                        window,
                                        preserveOriginalPosition: false);
                                    if (!cleanupRemoval.Attempted)
                                    {
                                        m_backend.UnregisterWindow(window);
                                        backendChanged = true;
                                    }
                                    else if (cleanupRemoval.Succeeded)
                                    {
                                        backendChanged = true;
                                    }
                                    else if (cleanupRemoval.Disposition
                                        == MasterSatelliteLocalMutationDisposition.Rejected)
                                    {
                                        backendChanged = RecoverRejectedMasterSatelliteRemovalLocked(
                                            window,
                                            cleanupRemoval);
                                    }
                                }
                            }
                        }
                    }
                    catch (NoValidPlacementExistsException)
                    {
                        genericPlacementFailed = true;
                    }
                    catch (Exception ex) when (algorithmicLayoutMatched)
                    {
                        algorithmicPlacementException = ex;
                    }

                    if (cleanupRemoval != null)
                    {
                        LogMasterSatelliteRemoval(window, cleanupRemoval);
                    }
                    if (algorithmicPlacement?.Attempted == true)
                    {
                        CompleteMasterSatellitePlacement(
                            window,
                            algorithmicPlacement,
                            allowExistingDesktopOverflow
                                && !manualDesktopMove
                                && !suppressOverflow,
                            allowIncomingOwnershipProbe: observedDesktop == null);
                    }
                    if (algorithmicPlacementException != null)
                    {
                        m_logger.Error(
                            algorithmicPlacementException,
                            "Master + Satellites dirty-check placement failed unexpectedly for window {Window}",
                            window.DebugString());
                        PlacementFailed?.Invoke(this, new TilingFailedEventArgs(
                            TilingError.NoValidPlacementExists,
                            window));
                    }
                    if (genericPlacementFailed)
                    {
                        PlacementFailed?.Invoke(this, new TilingFailedEventArgs(
                            TilingError.NoValidPlacementExists, window));
                    }
                    if (backendChanged
                        && algorithmicPlacement?.Disposition != MasterSatelliteLocalMutationDisposition.Placed)
                    {
                        InvalidateLayout();
                    }
                    return backendChanged;
                }
                else
                {
                    MasterSatelliteLocalMutationResult? algorithmicRemoval = null;
                    bool backendChanged = false;
                    using (m_backendLock.EnterScope())
                    {
                        if (m_backend.HasWindow(window))
                        {
                            m_logger.Verbose("Window {Window} can no longer be managed, but is registered with backend, unregistering now", window.DebugString());
                            algorithmicRemoval = RemoveMasterSatelliteWindowLocked(
                                window,
                                preserveOriginalPosition: true);
                            if (!algorithmicRemoval.Attempted)
                            {
                                m_backend.UnregisterWindow(window);
                                backendChanged = true;
                            }
                            else if (algorithmicRemoval.Succeeded)
                            {
                                backendChanged = true;
                            }
                            else if (algorithmicRemoval.Disposition
                                == MasterSatelliteLocalMutationDisposition.Rejected)
                            {
                                backendChanged = RecoverRejectedMasterSatelliteRemovalLocked(
                                    window,
                                    algorithmicRemoval);
                            }
                        }
                    }
                    if (algorithmicRemoval != null)
                    {
                        LogMasterSatelliteRemoval(window, algorithmicRemoval);
                    }
                    if (backendChanged)
                    {
                        InvalidateLayout();
                    }
                    return backendChanged;
                }
            }
            catch (WindowAlreadyRegisteredException)
            {
                return false;
            }
            // TODO: Is the following catch block necessary?
            catch (InvalidOperationException)
            {
                return false;
            }
            return false;
        }

        private bool CanManage(IWindow x, bool ignoreFloating = false)
        {
            bool IsOnCurrentDisplay()
            {
                var pos = x.Position.Center;
                if (m_display.Bounds.Contains(pos))
                    return true;

                // Check if on any other displays
                return !m_workspace.DisplayManager.Displays
                    .Where(d => !d.Equals(m_display) && d.Bounds.Contains(pos))
                    .Any();
            }
            bool IsFloating()
            {
                return IsFloatingWindow(x);
            }

            // Cheap boolean read
            if (x.IsTopmost)
            {
                return false;
            }

            // Set lookup
            if (!ignoreFloating && IsFloating())
            {
                return false;
            }

            // GetWindowPos + Lookup
            if (!IsOnCurrentDisplay())
            {
                return false;
            }

            // GetWindowStyle + OpenProcess
            if (!x.CanResize)
            {
                return false;
            }

            // OpenProcess (expensive)
            if (!x.CanMove)
            {
                return false;
            }

            // Virtual Desktop stuff is very expensive
            if (m_workspace.VirtualDesktopManager.IsWindowPinned(x))
            {
                return false;
            }

            return true;
        }

        private bool IsFloatingWindow(IWindow window)
        {
            return m_workspaceFloatingWindows.Contains(window);
        }

        private bool MarkWindowFloating(IWindow window)
        {
            return m_workspaceFloatingWindows.Add(window);
        }

        private bool MarkWindowTiled(IWindow window)
        {
            return m_workspaceFloatingWindows.Remove(window);
        }

        private void ForgetClosedFloatingWindow(
            IWindow window,
            IntPtr stableWindowHandle)
        {
            if (!m_workspaceFloatingWindows.Remove(stableWindowHandle))
            {
                m_workspaceFloatingWindows.Remove(window);
            }
        }

        private void InvalidateLayout()
        {
            if (!m_active)
            {
                return;
            }

            m_dirty = true;
            if (m_frozen.IsPositive())
            {
                return;
            }
            try
            {
                m_dispatcher.BeginInvoke(
                    (Action)ApplyInvalidatedLayoutAsync,
                    System.Windows.Threading.DispatcherPriority.DataBind);
            }
            catch (InvalidOperationException exception)
            {
                m_logger.Debug(
                    exception,
                    "Dropping layout invalidation because the tiling Dispatcher is shutting down");
            }
        }

        /// <summary>
        /// Dispatcher callback for a coalesced invalidation. Every asynchronous
        /// failure is observed here so a transient workspace/VDM/preview error
        /// cannot escape through an abandoned Task.
        /// </summary>
        private async void ApplyInvalidatedLayoutAsync()
        {
            if (!m_dirty || m_frozen.IsPositive())
            {
                return;
            }
            m_dirty = false;
            try
            {
                await UpdateLayoutAsync();
            }
            catch (Exception exception)
            {
                m_logger.Error(
                    exception,
                    "Asynchronous tiling layout update failed for display {Display}",
                    m_display);
            }
        }

        private void Freeze()
        {
            m_frozen.Increment();
        }

        private void Unfreeze()
        {
            if (m_frozen.DecrementIfPositive())
            {
                if (m_dirty)
                {
                    InvalidateLayout();
                }
            }
        }

        private static Rectangle ShrinkTo(Rectangle container, int width, int height)
        {
            int wdiff = container.Width - width;
            int hdiff = container.Height - height;
            return new Rectangle(
                container.Left + wdiff / 2,
                container.Top + hdiff / 2,
                container.Right - wdiff / 2,
                container.Height - wdiff / 2
            );
        }

        private int GetPanelSpacing()
        {
            double scaling = m_display.Scaling;
            return (int)(m_windowPadding * scaling);
        }

        private Rectangle GetPanelPaddingRect()
        {
            double scaling = m_display.Scaling;
            return new Rectangle(0, (int)((m_panelHeight + m_windowPadding) * scaling), 0, 0);
        }

        private static System.Windows.Thickness ToThickness(Rectangle rc)
        {
            return new System.Windows.Thickness(rc.Left, rc.Top, rc.Right, rc.Bottom);
        }

        private void UpdateGuiNodeOptions()
        {
            m_dispatcher.Invoke(() =>
            {
                m_gui.PanelSpacing = GetPanelSpacing();
                m_gui.PanelPadding = ToThickness(GetPanelPaddingRect());
                m_gui.InvalidateView();
                InvalidateLayout();
            });
        }

        private void PropagatePaddingChange()
        {
            using (m_backendLock.EnterScope())
            {
                foreach (var panel in m_backend.Trees.SelectMany(x => x.Root!.Nodes).OfType<PanelNode>())
                {
                    panel.Spacing = GetPanelSpacing();
                }
            }
            UpdateGuiNodeOptions();
        }

        private void PropagatePanelHeightChange()
        {
            using (m_backendLock.EnterScope())
            {
                foreach (var panel in m_backend.Trees.SelectMany(x => x.Root!.Nodes).OfType<PanelNode>())
                {
                    panel.Padding = GetPanelPaddingRect();
                    panel.Spacing = GetPanelSpacing();
                }
            }
            UpdateGuiNodeOptions();
        }

        private void PropagateShowFocusChange()
        {
            InvalidateLayout();
        }

        private void PropagateShowPreviewFocusChange()
        {
            InvalidateLayout();
        }

        bool HasFocusAndAdjacentWindow(TilingDirection direction)
        {
            var desktop = m_workspace.VirtualDesktopManager.CurrentDesktop;
            try
            {
                using (m_backendLock.EnterScope())
                {
                    m_backend.GetFocusAndAdjacentWindow(desktop, direction);
                    return true;
                }
            }
            catch (TilingFailedException e) when (e.FailReason == TilingError.MissingTarget || e.FailReason == TilingError.MissingAdjacentWindow)
            {
                return false;
            }
        }
    }
}
