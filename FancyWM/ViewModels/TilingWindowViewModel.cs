using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;

using WinMan;
using FancyWM.Layouts.Tiling;
using System.Linq;

namespace FancyWM.ViewModels
{
    public class TilingWindowViewModel : TilingNodeViewModel
    {
        private enum RevealState
        {
            /// <summary>
            /// Not enabled, mouse is far away
            /// </summary>
            Hidden,
            /// <summary>
            /// Disabled by mouse movement from the side.
            /// </summary>
            IndicatorVisible,
            /// <summary>
            /// Enabled
            /// </summary>
            Visible,
        }

        [Flags]
        private enum WindowSubscriptions
        {
            None = 0,
            Removed = 1,
            PositionChangeStart = 2,
            PositionChangeEnd = 4,
            TitleChanged = 8,
        }

        public string? Title { get => m_title; set => SetField(ref m_title, value); }

        public Visibility ActionsVisibility { get => m_actionsVisibility; set => SetField(ref m_actionsVisibility, value); }

        public double ActionsHeight { get => m_actionsHeight; set => SetField(ref m_actionsHeight, value); }

        public double RevealHighlightRadius { get => m_revealHighlightRadius; set => SetField(ref m_revealHighlightRadius, value); }

        public double RevealHighlightOpacity { get => m_revealHighlightOpacity; set => SetField(ref m_revealHighlightOpacity, value); }

        public bool IsActionActive { get => m_isActionActive; set => SetField(ref m_isActionActive, value); }
        public bool IsPreviewVisible { get => m_isPreviewVisible; set => SetField(ref m_isPreviewVisible, value); }

        private IWorkspace? m_workspace;
        private string? m_title;
        private Visibility m_actionsVisibility = Visibility.Hidden;
        private double m_actionsHeight = 22;
        private RevealState m_actionsRevealState = RevealState.Hidden;
        private double m_revealHighlightOpacity = 0;
        private double m_revealHighlightRadius = 64;
        private bool m_isMoving = false;
        private bool m_isPreviewVisible = false;
        private bool m_isActionActive = false;
        private WindowNode? m_currentNode;
        private WindowSubscriptions m_windowSubscriptions;
        private bool m_cursorSubscribed;
        private long m_bindingVersion;
        private int m_windowDisposeState;

        public event RoutedEventHandler? BeginHorizontalSplitWith;
        public event RoutedEventHandler? BeginVerticalSplitWith;
        public event RoutedEventHandler? BeginStackWith;

        public event RoutedEventHandler? FloatActionPressed;
        public event RoutedEventHandler? IgnoreProcessPressed;
        public event RoutedEventHandler? IgnoreClassPressed;

        public ICommand BeginHorizontalSplitWithCommand { get; }
        public ICommand BeginVerticalSplitWithCommand { get; }
        public ICommand BeginStackWithCommand { get; }

        public ICommand FloatCommand { get; }
        public ICommand IgnoreProcessCommand { get; }
        public ICommand IgnoreClassCommand { get; }

        public TilingWindowViewModel() : this(null)
        {
        }

        internal TilingWindowViewModel(FancyWM.Utilities.WindowExtensions.IconCache? iconCache) : base(iconCache)
        {
            BeginHorizontalSplitWithCommand = new DelegateCommand(_ => BeginHorizontalSplitWith?.Invoke(this, new RoutedEventArgs()));
            BeginVerticalSplitWithCommand = new DelegateCommand(_ => BeginVerticalSplitWith?.Invoke(this, new RoutedEventArgs()));
            BeginStackWithCommand = new DelegateCommand(_ => BeginStackWith?.Invoke(this, new RoutedEventArgs()));

            FloatCommand = new DelegateCommand(_ => FloatActionPressed?.Invoke(this, new RoutedEventArgs()));
            IgnoreProcessCommand = new DelegateCommand(_ => IgnoreProcessPressed?.Invoke(this, new RoutedEventArgs()));
            IgnoreClassCommand = new DelegateCommand(_ => IgnoreClassPressed?.Invoke(this, new RoutedEventArgs()));
        }

        public override void Dispose()
        {
            if (Interlocked.Exchange(ref m_windowDisposeState, 1) != 0) { return; }

            ClearNodeReference();

            ExceptionDispatchInfo? failure = null;
            List<Exception>? laterFailures = null;
            TryCleanup(base.Dispose, ref failure, ref laterFailures);

            FloatActionPressed = null;
            IgnoreProcessPressed = null;
            IgnoreClassPressed = null;
            BeginHorizontalSplitWith = null;
            BeginVerticalSplitWith = null;
            BeginStackWith = null;

            ReleaseCurrentSubscriptions(true, ref failure, ref laterFailures);

            ThrowCleanupFailure(failure, laterFailures);
        }

        protected override void NotifyPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            if (propertyName == nameof(Node) && !IsWindowTerminal)
            {
                RebindNode(Node as WindowNode);
            }
            if (!IsWindowTerminal)
            {
                base.NotifyPropertyChanged(propertyName);
            }
        }

        private bool IsWindowTerminal => IsDisposed || Volatile.Read(ref m_windowDisposeState) != 0;

        private void RebindNode(WindowNode? node)
        {
            long bindingVersion = Interlocked.Increment(ref m_bindingVersion);
            var workspace = node?.WindowReference.Workspace;
            bool keepCursor = node != null && m_cursorSubscribed && ReferenceEquals(m_workspace, workspace);
            ExceptionDispatchInfo? failure = null;
            List<Exception>? laterFailures = null;
            ReleaseCurrentSubscriptions(!keepCursor, ref failure, ref laterFailures);
            ThrowCleanupFailure(failure, laterFailures);
            if (IsWindowTerminal || node == null) { return; }

            m_currentNode = node;
            m_workspace = workspace;
            if (!keepCursor && workspace != null && !TryAddCursorSubscription(node, workspace, bindingVersion)) { return; }

            var window = node.WindowReference;
            if (!TryAddWindowSubscription(node, workspace, window, WindowSubscriptions.Removed, bindingVersion)) { return; }
            if (!TryAddWindowSubscription(node, workspace, window, WindowSubscriptions.PositionChangeStart, bindingVersion)) { return; }
            if (!TryAddWindowSubscription(node, workspace, window, WindowSubscriptions.PositionChangeEnd, bindingVersion)) { return; }
            TryAddWindowSubscription(node, workspace, window, WindowSubscriptions.TitleChanged, bindingVersion);
        }

        private void ReleaseCurrentSubscriptions(
            bool releaseWorkspace,
            ref ExceptionDispatchInfo? failure,
            ref List<Exception>? laterFailures)
        {
            var currentNode = m_currentNode;
            var subscriptions = m_windowSubscriptions;
            m_currentNode = null;
            m_windowSubscriptions = WindowSubscriptions.None;

            var workspace = m_workspace;
            bool cursorSubscribed = m_cursorSubscribed;
            if (releaseWorkspace)
            {
                m_workspace = null;
                m_cursorSubscribed = false;
            }

            if (currentNode != null)
            {
                var window = currentNode.WindowReference;
                TryReleaseWindowSubscription(window, subscriptions, WindowSubscriptions.Removed, ref failure, ref laterFailures);
                TryReleaseWindowSubscription(window, subscriptions, WindowSubscriptions.PositionChangeStart, ref failure, ref laterFailures);
                TryReleaseWindowSubscription(window, subscriptions, WindowSubscriptions.PositionChangeEnd, ref failure, ref laterFailures);
                TryReleaseWindowSubscription(window, subscriptions, WindowSubscriptions.TitleChanged, ref failure, ref laterFailures);
            }
            if (releaseWorkspace && cursorSubscribed && workspace != null)
            {
                TryReleaseCursorSubscription(workspace, ref failure, ref laterFailures);
            }
        }

        private bool TryAddCursorSubscription(WindowNode node, IWorkspace workspace, long bindingVersion)
        {
            m_cursorSubscribed = true;
            try
            {
                workspace.CursorLocationChanged += OnCursorLocationChanged;
            }
            catch (Exception error)
            {
                if (IsBindingCurrent(node, workspace, bindingVersion)) { throw; }
                ExceptionDispatchInfo? failure = ExceptionDispatchInfo.Capture(error);
                List<Exception>? laterFailures = null;
                PrepareObsoleteBindingCleanup(node, ref failure, ref laterFailures);
                TryReleaseCursorSubscription(workspace, ref failure, ref laterFailures);
                ThrowCleanupFailure(failure, laterFailures);
                throw;
            }
            if (IsBindingCurrent(node, workspace, bindingVersion)) { return true; }

            ExceptionDispatchInfo? cleanupFailure = null;
            List<Exception>? cleanupLaterFailures = null;
            PrepareObsoleteBindingCleanup(node, ref cleanupFailure, ref cleanupLaterFailures);
            TryReleaseCursorSubscription(workspace, ref cleanupFailure, ref cleanupLaterFailures);
            ThrowCleanupFailure(cleanupFailure, cleanupLaterFailures);
            return false;
        }

        private bool TryAddWindowSubscription(
            WindowNode node,
            IWorkspace? workspace,
            IWindow window,
            WindowSubscriptions subscription,
            long bindingVersion)
        {
            m_windowSubscriptions |= subscription;
            try
            {
                switch (subscription)
                {
                    case WindowSubscriptions.Removed:
                        window.Removed += WindowReference_Removed;
                        break;
                    case WindowSubscriptions.PositionChangeStart:
                        window.PositionChangeStart += WindowReference_PositionChangeStart;
                        break;
                    case WindowSubscriptions.PositionChangeEnd:
                        window.PositionChangeEnd += WindowReference_PositionChangeEnd;
                        break;
                    case WindowSubscriptions.TitleChanged:
                        window.TitleChanged += WindowReference_TitleChanged;
                        break;
                }
            }
            catch (Exception error)
            {
                if (IsBindingCurrent(node, workspace, bindingVersion)) { throw; }
                ExceptionDispatchInfo? failure = ExceptionDispatchInfo.Capture(error);
                List<Exception>? laterFailures = null;
                PrepareObsoleteBindingCleanup(node, ref failure, ref laterFailures);
                TryReleaseWindowSubscription(window, subscription, subscription, ref failure, ref laterFailures);
                ThrowCleanupFailure(failure, laterFailures);
                throw;
            }
            if (IsBindingCurrent(node, workspace, bindingVersion)) { return true; }

            ExceptionDispatchInfo? cleanupFailure = null;
            List<Exception>? cleanupLaterFailures = null;
            PrepareObsoleteBindingCleanup(node, ref cleanupFailure, ref cleanupLaterFailures);
            TryReleaseWindowSubscription(window, subscription, subscription, ref cleanupFailure, ref cleanupLaterFailures);
            ThrowCleanupFailure(cleanupFailure, cleanupLaterFailures);
            return false;
        }

        private bool IsBindingCurrent(WindowNode node, IWorkspace? workspace, long bindingVersion)
        {
            return !IsWindowTerminal && Volatile.Read(ref m_bindingVersion) == bindingVersion &&
                ReferenceEquals(m_currentNode, node) && ReferenceEquals(m_workspace, workspace);
        }

        private void PrepareObsoleteBindingCleanup(
            WindowNode expectedNode,
            ref ExceptionDispatchInfo? failure,
            ref List<Exception>? laterFailures)
        {
            bool removedDuringAcquisition = !IsWindowTerminal && m_currentNode == null &&
                ReferenceEquals(Node, expectedNode);
            if (!IsWindowTerminal && !removedDuringAcquisition)
            {
                failure ??= ExceptionDispatchInfo.Capture(new InvalidOperationException(
                    "The window binding changed while its event subscription was being acquired."));
                TryCleanup(Dispose, ref failure, ref laterFailures);
            }
        }

        private void TryReleaseWindowSubscription(
            IWindow window,
            WindowSubscriptions owned,
            WindowSubscriptions subscription,
            ref ExceptionDispatchInfo? failure,
            ref List<Exception>? laterFailures)
        {
            if ((owned & subscription) == 0) { return; }
            try
            {
                switch (subscription)
                {
                    case WindowSubscriptions.Removed:
                        window.Removed -= WindowReference_Removed;
                        break;
                    case WindowSubscriptions.PositionChangeStart:
                        window.PositionChangeStart -= WindowReference_PositionChangeStart;
                        break;
                    case WindowSubscriptions.PositionChangeEnd:
                        window.PositionChangeEnd -= WindowReference_PositionChangeEnd;
                        break;
                    case WindowSubscriptions.TitleChanged:
                        window.TitleChanged -= WindowReference_TitleChanged;
                        break;
                }
            }
            catch (Exception error)
            {
                if (failure == null) { failure = ExceptionDispatchInfo.Capture(error); }
                else { (laterFailures ??= []).Add(error); }
            }
        }

        private void TryReleaseCursorSubscription(
            IWorkspace workspace,
            ref ExceptionDispatchInfo? failure,
            ref List<Exception>? laterFailures)
        {
            try
            {
                workspace.CursorLocationChanged -= OnCursorLocationChanged;
            }
            catch (Exception error)
            {
                if (failure == null) { failure = ExceptionDispatchInfo.Capture(error); }
                else { (laterFailures ??= []).Add(error); }
            }
        }

        private void WindowReference_TitleChanged(object? sender, WindowTitleChangedEventArgs e)
        {
            if (!IsCurrentWindow(sender)) { return; }
            Title = e.NewTitle;
        }

        private void WindowReference_Removed(object? sender, WindowChangedEventArgs e)
        {
            if (!IsCurrentWindow(sender)) { return; }
            Interlocked.Increment(ref m_bindingVersion);
            ExceptionDispatchInfo? failure = null;
            List<Exception>? laterFailures = null;
            ReleaseCurrentSubscriptions(true, ref failure, ref laterFailures);
            ThrowCleanupFailure(failure, laterFailures);
        }

        private void WindowReference_PositionChangeEnd(object? sender, WindowPositionChangedEventArgs e)
        {
            if (!IsCurrentWindow(sender)) { return; }
            m_isMoving = false;
        }

        private void WindowReference_PositionChangeStart(object? sender, WindowPositionChangedEventArgs e)
        {
            if (!IsCurrentWindow(sender)) { return; }
            m_isMoving = true;
        }

        private void OnCursorLocationChanged(object? sender, CursorLocationChangedEventArgs e)
        {
            if (IsDisposed || Volatile.Read(ref m_windowDisposeState) != 0 || !ReferenceEquals(sender, m_workspace)) { return; }
            if (Node is WindowNode node)
            {
                if (m_isActionActive)
                {
                    RevealHighlightOpacity = 0;
                    ActionsVisibility = Visibility.Visible;
                    m_actionsRevealState = RevealState.Visible;
                    return;
                }

                if (node.WindowReference.Workspace.FocusedWindow != node.WindowReference)
                {
                    RevealHighlightOpacity = 0;
                    ActionsVisibility = Visibility.Collapsed;
                    m_actionsRevealState = RevealState.Hidden;
                    return;
                }

                var windowPos = node.WindowReference.Position;

                var x = e.NewLocation.X - windowPos.Left;
                var y = e.NewLocation.Y - windowPos.Top;

                var isInBoundsX = 0 <= x && x <= windowPos.Width;

                var dpi = node.WindowReference.Workspace.DisplayManager.Displays.FirstOrDefault(x => x.Bounds.Contains(windowPos.Center))?.Scaling ?? 1.0;
                var revealHighlightRadius = RevealHighlightRadius * dpi;

                if (isInBoundsX && -(revealHighlightRadius / 2) < y && y <= 0)
                {
                    if (m_actionsRevealState == RevealState.IndicatorVisible)
                    {
                        m_actionsRevealState = RevealState.Visible;
                    }
                }
                else if (isInBoundsX && 0 <= y && y < revealHighlightRadius)
                {
                    if (m_actionsRevealState == RevealState.Hidden)
                    {
                        m_actionsRevealState = RevealState.IndicatorVisible;
                    }
                    RevealHighlightOpacity = 1 - Math.Pow(-y / revealHighlightRadius, 2);
                }
                else
                {
                    m_actionsRevealState = RevealState.Hidden;
                    RevealHighlightOpacity = 0;
                }

                if (m_actionsRevealState != RevealState.IndicatorVisible)
                {
                    RevealHighlightOpacity = 0;
                }

                ActionsVisibility = m_actionsRevealState == RevealState.Visible
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                if (m_isMoving)
                {
                    RevealHighlightOpacity = 0;
                    ActionsVisibility = Visibility.Collapsed;
                }
            }
        }

        private bool IsCurrentWindow(object? sender)
        {
            return !IsDisposed && Volatile.Read(ref m_windowDisposeState) == 0 &&
                m_currentNode is WindowNode node && ReferenceEquals(sender, node.WindowReference);
        }
    }
}
