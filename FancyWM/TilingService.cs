using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;

using FancyWM.Layouts.Tiling;
using FancyWM.Utilities;
using WinMan;
using System;
using FancyWM.Models;
using System.Reactive.Linq;
using System.Reactive.Disposables;
using Serilog;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Diagnostics;

using FancyWM.AlgorithmicLayouts;

#if DEBUG
using Lock = FancyWM.Utilities.DebugLock;
#else
using Lock = System.Threading.Lock;
#endif

namespace FancyWM
{
    /// <summary>
    /// Manages the layout of window in the workspace
    /// </summary>
    internal partial class TilingService : ITilingService, IDisposable
    {
        private enum UserInteraction
        {
            None,
            Starting,
            Moving,
            Resizing,
        }

        private class NodeLocation(TilingNode node)
        {
            public PanelNode Parent = node.Parent ?? throw new ArgumentException(nameof(node));
            public int Index = node.Parent.IndexOf(node);
            public Rectangle ComputedRectangle = node.ComputedRectangle;
        }

        public event EventHandler<TilingFailedEventArgs>? PlacementFailed;

        public event EventHandler<AlgorithmicLayoutEvent>? AlgorithmicLayoutChanged;
        public event EventHandler<EventArgs>? PendingIntentChanged;

        /// <summary>
        /// Current active state.
        /// <see cref="Start"/>
        /// <see cref="Stop"/>
        /// </summary>
        public bool Active
        {
            get => m_active;
        }

        public bool AutoRegisterWindows { get; internal set; }

        private bool m_allocateNewPanelSpace;

        private bool m_animateWindowMovement;

        private int m_autoSplitCount = 100;

        private bool m_delayReposition = false;
        private bool m_autoFloatNewWindows = false;

        private void SetAutoCollapse(bool value)
        {
            m_backend.AutoCollapse = value;
        }

        private void SetWindowPadding(int value)
        {
            m_windowPadding = value;
            PropagatePaddingChange();
        }

        private void SetPanelHeight(int value)
        {
            m_panelHeight = value;
            PropagatePanelHeightChange();
        }

        private void SetShowFocus(bool value)
        {
            m_showFocus = value;
            PropagateShowFocusChange();
        }

        public bool ShowPreviewFocus
        {
            get => m_showPreviewFocus;
            set
            {
                m_showPreviewFocus = value;
                PropagateShowPreviewFocusChange();
            }
        }

        public IWorkspace Workspace => m_workspace;

        public IReadOnlyCollection<IWindowMatcher> ExclusionMatchers
        {
            get => m_exclusionMatchers;
            set
            {
                m_exclusionMatchers = [.. value];

                using (m_windowSetLock.EnterScope())
                {
                    foreach (var window in m_windowSet)
                    {
                        if (m_exclusionMatchers.Any(x => x.Matches(window)))
                        {
                            MarkWindowFloating(window);
                        }
                    }
                }
                Refresh();
            }
        }

        public ITilingServiceIntent? PendingIntent
        {
            get => m_pendingIntent;
            set
            {
                if (m_pendingIntent != value)
                {
                    m_pendingIntent = value;
                    PendingIntentChanged?.Invoke(this, new EventArgs());
                }
            }
        }

        private static readonly IReadOnlySet<IWindow> EmptyWindowSet = new HashSet<IWindow>();
        private static readonly TimeSpan LockThreshold = TimeSpan.FromMilliseconds(10);

        /// <summary>
        /// The dispatcher from the thread that created the <see cref="TilingService"/>
        /// </summary>
        private readonly Dispatcher m_dispatcher;
        private readonly IWorkspace m_workspace;
        private readonly ILogger m_logger;
        private IReadOnlyCollection<IWindowMatcher> m_exclusionMatchers = [];

        private readonly ITilingOverlayRenderer m_gui;
        private readonly IDisplay m_display;
        private IDisplay m_masterSatellitePrimaryDisplay;

        private readonly TilingWorkspace m_backend;
        private readonly Utilities.DebugLock m_backendLock = new(LockThreshold);

        private readonly HashSet<IWindow> m_newWindowSet
            = new(ReferenceEqualityComparer.Instance);
        private readonly Utilities.DebugLock m_newWindowSetLock = new(LockThreshold);

        private readonly HashSet<IWindow> m_windowSet
            = new(ReferenceEqualityComparer.Instance);
        private readonly Utilities.DebugLock m_windowSetLock = new(LockThreshold);

        private readonly Dictionary<IWindow, IntPtr> m_windowLifetimeHandles
            = new(ReferenceEqualityComparer.Instance);

        private readonly WorkspaceFloatingWindowRegistry m_workspaceFloatingWindows;

        private readonly HashSet<IWindow> m_ignoreRepositionSet
            = new(ReferenceEqualityComparer.Instance);
        private readonly Utilities.DebugLock m_ignoreRepositionSetLock = new(LockThreshold);

        private readonly Dictionary<IWindow, NodeLocation> m_savedLocations
            = new(ReferenceEqualityComparer.Instance);
        private readonly Utilities.DebugLock m_savedLocationsLock = new(LockThreshold);

        private readonly CompositeDisposable m_subscriptions = [];
        private bool m_guiRegisteredForDisposal;
        private readonly IAnimationThread m_animationThread;
        private int m_panelHeight = 20;
        private int m_windowPadding = 2;
        private bool m_showFocus = false;
        private bool m_showPreviewFocus = false;

        private bool m_active = false;
        private volatile bool m_disposed;
        private bool m_dirty = true;
        private UserInteraction m_currentInteraction = UserInteraction.None;
        private IWindow? m_movingWindow;
        private PanelNode? m_movingPanelNode;
        private ITilingServiceIntent? m_pendingIntent;
        private readonly Counter m_frozen = new();
        private readonly Stopwatch m_sw = new();

        public TilingService(
            IWorkspace workspace,
            IDisplay display,
            IAnimationThread animationThread,
            IObservable<ITilingServiceSettings> settings,
            AlgorithmicLayoutCoordinator algorithmicLayoutCoordinator,
            bool autoRegisterWindows)
            : this(
                workspace,
                display,
                animationThread,
                settings,
                algorithmicLayoutCoordinator,
                autoRegisterWindows,
                App.Current.Logger,
                static (targetDisplay, overlayAnchorSource) =>
                    new TilingOverlayRenderer(targetDisplay, overlayAnchorSource))
        {
        }

        internal TilingService(
            IWorkspace workspace,
            IDisplay display,
            IAnimationThread animationThread,
            IObservable<ITilingServiceSettings> settings,
            AlgorithmicLayoutCoordinator algorithmicLayoutCoordinator,
            bool autoRegisterWindows,
            ILogger logger,
            Func<IDisplay, Func<IntPtr>, ITilingOverlayRenderer> overlayFactory,
            Func<LayoutStateKey, MasterSatelliteCapacitySnapshot, long, bool>?
                algorithmicCapacityPublisher = null,
            AlgorithmicWindowTransferEventTracker?
                algorithmicWindowEventTracker = null)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(overlayFactory);
            m_logger = logger;
            m_logger.Information("Managing display {Display} (Bounds: {Bounds}, Scale: {Scaling})", display, display.Bounds, display.Scaling);
            m_dispatcher = Dispatcher.CurrentDispatcher;
            m_workspace = workspace;
            m_animationThread = animationThread;
            m_display = display;
            m_masterSatellitePrimaryDisplay = display;
            m_algorithmicLayoutCoordinator = algorithmicLayoutCoordinator
                ?? throw new ArgumentNullException(nameof(algorithmicLayoutCoordinator));
            m_masterSatelliteCapacityPublisher = algorithmicCapacityPublisher
                ?? m_algorithmicLayoutCoordinator.PublishCapacity;
            m_workspaceFloatingWindows = m_algorithmicLayoutCoordinator.FloatingWindows;
            if (!ReferenceEquals(m_algorithmicLayoutCoordinator.Dispatcher, m_dispatcher))
            {
                throw new ArgumentException(
                    "The algorithmic layout coordinator must use the tiling service Dispatcher.",
                    nameof(algorithmicLayoutCoordinator));
            }
            if (!ReferenceEquals(m_algorithmicLayoutCoordinator.Workspace, m_workspace))
            {
                throw new ArgumentException(
                    "The algorithmic layout coordinator must belong to the tiling service workspace.",
                    nameof(algorithmicLayoutCoordinator));
            }
            m_algorithmicDisplayRegistration = m_algorithmicLayoutCoordinator.RegisterDisplay(
                display,
                this);
            try
            {
                m_masterSatelliteLifecycle = new MasterSatelliteRuntimeLifecycle(
                    display,
                    m_algorithmicLayoutCoordinator);
                m_masterSatelliteCommands = new MasterSatelliteCommandController(
                    m_masterSatelliteLifecycle,
                    IsMasterSatelliteCapacityTransitionSource);
                m_masterSatelliteDrops = new MasterSatelliteDropController();
                m_algorithmicWindowTransfers = new AlgorithmicWindowTransferOrchestrator(
                    m_algorithmicLayoutCoordinator,
                    display);
                m_algorithmicWindowEvents = algorithmicWindowEventTracker
                    ?? new AlgorithmicWindowTransferEventTracker(m_dispatcher);
                m_algorithmicDesktopCreations = new AlgorithmicDesktopCreationOrchestrator(
                    m_algorithmicLayoutCoordinator,
                    m_workspace.VirtualDesktopManager);
                m_backend = new TilingWorkspace(
                    new MasterSatelliteLayoutEngine(diagnostic =>
                        m_logger.Warning(
                            "Master + Satellites layout diagnostic: {Diagnostic}",
                            diagnostic)),
                    diagnostic => m_logger.Debug(
                        "Master + Satellites workspace mutation {Operation}; desktop={Desktop}; revision={BeforeRevision}->{AfterRevision}",
                        diagnostic.Operation,
                        diagnostic.Desktop,
                        diagnostic.BeforeRevision,
                        diagnostic.AfterRevision));
                m_gui = overlayFactory(display, GetOverlayAnchor);
                ArgumentNullException.ThrowIfNull(m_gui);
                m_gui.PanelSpacing = GetPanelSpacing();
                m_gui.PanelPadding = ToThickness(GetPanelPaddingRect());
                m_subscriptions.Add(m_gui);
                m_guiRegisteredForDisposal = true;
                m_gui.TilingNodeFocusRequested += OnTilingNodeFocusRequested;
                m_gui.TilingNodeCloseRequested += OnTilingNodeCloseRequested;
                m_gui.TilingNodePullUpRequested += OnTilingNodePullUpRequested;
                m_gui.TilingPanelMoving += OnTilingPanelMoving;
                m_gui.TilingPanelMoveRequested += OnTilingPanelMoveRequested;
                m_gui.BeginHorizontalWithRequested += OnBeginHorizontalWithRequestedAsync;
                m_gui.BeginVerticalWithRequested += OnBeginVerticalWithRequested;
                m_gui.BeginStackWithRequested += OnBeginStackWithRequested;
                m_gui.FloatRequested += OnWindowFloatRequested;
                m_gui.HorizontalSplitRequested += OnWindowHorizontalSplitRequested;
                m_gui.VerticalSplitRequested += OnWindowVerticalSplitRequested;
                m_gui.PullUpRequested += OnWindowPullUpRequested;
                m_gui.StackRequested += OnWindowStackRequested;
                m_gui.IgnoreProcessRequested += OnWindowIgnoreProcessRequested;
                m_gui.IgnoreClassRequested += OnWindowIgnoreClassRequested;

                AutoRegisterWindows = autoRegisterWindows;

                try
                {
                    m_masterSatellitePrimaryDisplay =
                        m_workspace.DisplayManager.PrimaryDisplay;
                }
                catch (Exception ex)
                {
                    m_logger.Debug(
                        ex,
                        "Could not snapshot the primary display during tiling-service construction");
                }

                foreach (var d in m_workspace.VirtualDesktopManager.Desktops)
                {
                    OnDesktopAdded(this, new DesktopChangedEventArgs(d));
                }

                m_workspace.VirtualDesktopManager.DesktopAdded += OnDesktopAdded;
                m_workspace.VirtualDesktopManager.DesktopRemoved += OnDesktopRemoved;
                m_workspace.VirtualDesktopManager.CurrentDesktopChanged += OnCurrentDesktopChanged;
                m_workspace.CursorLocationChanged += OnCursorLocationChanged;

                m_display.ScalingChanged += OnDisplayScalingChanged;
                m_display.WorkAreaChanged += OnDisplayWorkAreaChanged;

                m_workspace.WindowAdded += OnWindowAdded;
                m_workspace.WindowRemoved += OnWindowRemoved;
                m_workspace.DisplayManager.PrimaryDisplayChanged +=
                    OnPrimaryDisplayChanged;

                PlacementFailed += OnPlacementFailed;
                PendingIntentChanged += OnPendingIntentChanged;

                m_subscriptions.Add(settings.Subscribe(OnSettingsChanged));

                var currentDesktop = m_workspace.VirtualDesktopManager.CurrentDesktop;
                OnCurrentDesktopChanged(this, new CurrentDesktopChangedEventArgs(currentDesktop, currentDesktop));

                var tree = m_backend.GetTree(currentDesktop)!;
                foreach (var w in m_workspace.GetSnapshot())
                {
                    OnWindowAdded(w, new WindowChangedEventArgs(w));
                    if (m_backend.HasWindow(w))
                    {
                        m_backend.SetFocus(w);
                        UpdateTree(tree);
                    }
                }

                // Initial WindowAdded registrations run at DataBind priority. Queue the
                // first algorithmic rebuild after them so it observes the complete tree.
                m_dispatcher.BeginInvoke(
                    InitializeMasterSatelliteLifecycleSafely,
                    DispatcherPriority.DataBind);

                m_algorithmicLayoutCoordinator.TransferTerminated +=
                    OnAlgorithmicTransferTerminated;
                m_sw.Start();
            }
            catch
            {
                RollbackFailedConstruction(display);
                throw;
            }
        }

        private void RollbackFailedConstruction(IDisplay display)
        {
            // A constructor failure does not give the caller an object it can
            // dispose. Make every external subscription and coordinator
            // registration acquired above transactional as well.
            m_disposed = true;
            TryConstructorCleanup(
                () => m_workspace.VirtualDesktopManager.DesktopAdded -= OnDesktopAdded,
                "unsubscribing from desktop-added events");
            TryConstructorCleanup(
                () => m_workspace.VirtualDesktopManager.DesktopRemoved -= OnDesktopRemoved,
                "unsubscribing from desktop-removed events");
            TryConstructorCleanup(
                () => m_workspace.VirtualDesktopManager.CurrentDesktopChanged -= OnCurrentDesktopChanged,
                "unsubscribing from current-desktop events");
            TryConstructorCleanup(
                () => m_workspace.CursorLocationChanged -= OnCursorLocationChanged,
                "unsubscribing from cursor-location events");
            TryConstructorCleanup(
                () => m_display.ScalingChanged -= OnDisplayScalingChanged,
                "unsubscribing from display-scaling events");
            TryConstructorCleanup(
                () => m_display.WorkAreaChanged -= OnDisplayWorkAreaChanged,
                "unsubscribing from display-work-area events");
            TryConstructorCleanup(
                () => m_workspace.WindowAdded -= OnWindowAdded,
                "unsubscribing from window-added events");
            TryConstructorCleanup(
                () => m_workspace.WindowRemoved -= OnWindowRemoved,
                "unsubscribing from window-removed events");
            TryConstructorCleanup(
                () => m_workspace.DisplayManager.PrimaryDisplayChanged -=
                    OnPrimaryDisplayChanged,
                "unsubscribing from primary-display events");
            TryConstructorCleanup(
                () => m_algorithmicLayoutCoordinator.TransferTerminated -=
                    OnAlgorithmicTransferTerminated,
                "unsubscribing from transfer-terminal events");

            IWindow[] trackedWindows;
            using (m_windowSetLock.EnterScope())
            {
                trackedWindows = [.. m_windowSet];
                m_windowSet.Clear();
            }
            foreach (var window in trackedWindows)
            {
                TryConstructorCleanup(
                    () => UnbindEventHandlers(window),
                    "unsubscribing from tracked-window events");
            }
            TryConstructorCleanup(
                UntrackAllWindowLifetimes,
                "unsubscribing from tracked-window lifetime events");

            TryConstructorCleanup(
                m_subscriptions.Dispose,
                "disposing tiling-service subscriptions");
            if (!m_guiRegisteredForDisposal && m_gui != null)
            {
                TryConstructorCleanup(
                    m_gui.Dispose,
                    "disposing the tiling overlay");
            }

            PlacementFailed = null;
            AlgorithmicLayoutChanged = null;
            PendingIntentChanged = null;
            TryConstructorCleanup(
                m_algorithmicDisplayRegistration.Dispose,
                "rolling back the algorithmic display registration");

            void TryConstructorCleanup(Action cleanup, string operation)
            {
                try
                {
                    cleanup();
                }
                catch (Exception ex)
                {
                    m_logger.Error(
                        ex,
                        "Tiling-service construction rollback failed while {Operation} for display {Display}",
                        operation,
                        display);
                }
            }
        }

        private void OnSettingsChanged(ITilingServiceSettings x)
        {
            void ApplySafely()
            {
                if (m_disposed)
                {
                    return;
                }
                try
                {
                    m_allocateNewPanelSpace = x.AllocateNewPanelSpace;
                    m_animateWindowMovement = x.AnimateWindowMovement;
                    m_autoSplitCount = x.AutoSplitCount;
                    m_delayReposition = x.DelayReposition;
                    m_autoFloatNewWindows = x.AutoFloatNewWindows;
                    SetWindowPadding(x.WindowPadding);
                    SetPanelHeight(x.PanelHeight);
                    SetShowFocus(x.ShowFocus);
                    SetAutoCollapse(x.AutoCollapsePanels);
                    OnMasterSatelliteSettingsChanged(x.MasterSatelliteLayout);
                }
                catch (Exception ex)
                {
                    m_logger.Error(ex, "Applying tiling-service settings failed for display {Display}", m_display);
                }
            }

            if (m_dispatcher.CheckAccess())
            {
                ApplySafely();
            }
            else
            {
                // ApplySafely observes and logs every callback exception, so the
                // queued dispatcher operation cannot become an unobserved failure.
                try
                {
                    m_dispatcher.BeginInvoke((Action)ApplySafely);
                }
                catch (Exception ex)
                {
                    m_logger.Error(
                        ex,
                        "Queuing tiling-service settings failed for display {Display}",
                        m_display);
                }
            }
        }

        public void Start()
        {
            m_active = true;
            InvalidateLayout();
            m_gui.Show();
        }

        public void Stop()
        {
            m_active = false;
            RestoreOriginalLayout();
            m_gui.Hide();
        }

        public bool CanMoveFocus(TilingDirection direction)
        {
            return HasFocusAndAdjacentWindow(direction);
        }

        public void MoveFocus(TilingDirection direction)
        {
            var desktop = GetRequiredMasterSatelliteCommandDesktop();
            using (m_backendLock.EnterScope())
            {
                var adjacentWindow = m_backend.GetFocusAdjacentWindow(desktop, direction);
                if (FocusHelper.ForceActivate(adjacentWindow.WindowReference.Handle))
                {
                    m_backend.SetFocus(adjacentWindow);
                }
            }

            InvalidateLayout();
        }

        public bool CanMoveWindow(TilingDirection direction)
        {
            if (!TryGetCurrentMasterSatelliteDesktop(out var desktop))
            {
                return false;
            }
            if (IsMasterSatelliteCapacityTransitionSource(desktop))
            {
                return false;
            }
            using (m_backendLock.EnterScope())
            {
                if (m_masterSatelliteCommands.IsActive(desktop))
                {
                    return m_masterSatelliteCommands.CanMoveFocusedWindow(
                        m_backend,
                        desktop,
                        direction);
                }
                return m_backend.GetFocus(desktop)?.GetAdjacentWindow(direction) != null;
            }
        }

        public void MoveWindow(TilingDirection direction)
        {
            MasterSatelliteCommandResult? algorithmicResult = null;
            var desktop = GetRequiredMasterSatelliteCommandDesktop();
            using (m_backendLock.EnterScope())
            {
                if (m_masterSatelliteCommands.IsActive(desktop))
                {
                    algorithmicResult = m_masterSatelliteCommands.MoveFocusedWindow(
                        m_backend,
                        desktop,
                        direction);
                }
                else
                {
                    var focusedNode = m_backend.GetFocus(desktop) ?? throw new TilingFailedException(TilingError.MissingTarget);
                    WindowNode? adjacentWindow = focusedNode.GetAdjacentWindow(direction) ?? throw new TilingFailedException(TilingError.MissingAdjacentWindow);
                    var focusedParent = focusedNode.Parent ?? throw new TilingFailedException(TilingError.InvalidTarget);

                    if (adjacentWindow.Parent == focusedParent)
                    {
                        var focusedNodeIndex = focusedParent.IndexOf(focusedNode);
                        var adjacentNodeIndex = focusedParent.IndexOf(adjacentWindow);
                        focusedParent.Move(focusedNodeIndex, adjacentNodeIndex);
                    }
                    else
                    {
                        if (direction == TilingDirection.Left || direction == TilingDirection.Up)
                        {
                            m_backend.MoveAfter(focusedNode, adjacentWindow);
                        }
                        else
                        {
                            m_backend.MoveBefore(focusedNode, adjacentWindow);
                        }
                    }
                }
            }

            if (algorithmicResult != null)
            {
                CompleteMasterSatelliteCommand(
                    "MoveFocusedWindow",
                    desktop,
                    algorithmicResult);
            }
            else
            {
                InvalidateLayout();
            }
        }

        public bool CanSwapFocus(TilingDirection direction)
        {
            if (!TryGetCurrentMasterSatelliteDesktop(out var desktop))
            {
                return false;
            }
            using (m_backendLock.EnterScope())
            {
                if (m_masterSatelliteCommands.IsActive(desktop))
                {
                    return m_masterSatelliteCommands.CanSwapFocusedWindow(
                        m_backend,
                        desktop,
                        direction);
                }
                return m_backend.GetFocus(desktop)?.GetAdjacentWindow(direction) != null;
            }
        }

        public void SwapFocus(TilingDirection direction)
        {
            MasterSatelliteCommandResult? algorithmicResult = null;
            var desktop = GetRequiredMasterSatelliteCommandDesktop();
            using (m_backendLock.EnterScope())
            {
                if (m_masterSatelliteCommands.IsActive(desktop))
                {
                    algorithmicResult = m_masterSatelliteCommands.SwapFocusedWindow(
                        m_backend,
                        desktop,
                        direction);
                }
                else
                {
                    (var currentWindow, var adjacentWindow) = m_backend.GetFocusAndAdjacentWindow(desktop, direction);
                    currentWindow!.Swap(adjacentWindow);
                }
            }
            if (algorithmicResult != null)
            {
                CompleteMasterSatelliteCommand(
                    "SwapFocusedWindow",
                    desktop,
                    algorithmicResult);
            }
            else
            {
                InvalidateLayout();
            }
        }

        public bool DiscoverWindows()
        {
            if (!AutoRegisterWindows)
            {
                return false;
            }

            List<IWindow> windows;
            using (m_windowSetLock.EnterScope())
            {
                windows = [.. m_windowSet];
            }

            bool anyChanges = false;
            foreach (var window in windows)
            {
                MasterSatelliteLocalMutationResult? algorithmicPlacement = null;
                MasterSatelliteLocalMutationResult? cleanupRemoval = null;
                bool backendChanged = false;
                bool placementFailed = false;
                bool algorithmicLayoutMatched = false;
                bool suppressOverflow = false;
                bool manualDesktopMove = false;
                Exception? algorithmicPlacementException = null;
                try
                {
                    if (window.State != WindowState.Restored || !CanManage(window))
                    {
                        continue;
                    }

                    bool hasActualDesktop = TryFindMasterSatelliteWindowDesktop(
                        window,
                        out var actualDesktop);
                    bool hasStableHandle = TryRememberMasterSatelliteWindowHandle(
                        window,
                        out var stableWindowHandle);
                    if (hasStableHandle && hasActualDesktop)
                    {
                        var destinationDecision = HandleMasterSatelliteDestinationAdded(
                            window,
                            stableWindowHandle,
                            actualDesktop);
                        if (destinationDecision.Consumed)
                        {
                            anyChanges = true;
                            continue;
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
                                anyChanges = true;
                                continue;
                            }
                        }
                    }

                    using (m_backendLock.EnterScope())
                    {
                        try
                        {
                            if (!m_backend.HasWindow(window))
                            {
                                m_logger.Debug("Discovered window {Window}", window.DebugString());
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
                    placementFailed = true;
                }
                catch (InvalidWindowReferenceException)
                {
                    using (m_backendLock.EnterScope())
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
                        allowExistingDesktopOverflow: !manualDesktopMove
                            && !suppressOverflow);
                }
                if (algorithmicPlacementException != null)
                {
                    m_logger.Error(
                        algorithmicPlacementException,
                        "Master + Satellites discovery placement failed unexpectedly for window {Window}",
                        window.DebugString());
                    PlacementFailed?.Invoke(this, new TilingFailedEventArgs(
                        TilingError.NoValidPlacementExists,
                        window));
                }
                if (placementFailed)
                {
                    PlacementFailed?.Invoke(this, new TilingFailedEventArgs(
                        TilingError.NoValidPlacementExists, window));
                }
                if (backendChanged
                    && algorithmicPlacement?.Disposition != MasterSatelliteLocalMutationDisposition.Placed)
                {
                    InvalidateLayout();
                }
                anyChanges |= backendChanged;
            }

            return anyChanges;
        }

        public void Refresh()
        {
            List<IWindow> windows;
            using (m_windowSetLock.EnterScope())
            {
                windows = [.. m_windowSet];
            }

            bool anyChanges = false;
            foreach (var window in windows)
            {
                if (DetectChanges(
                    window,
                    allowExistingDesktopOverflow: true))
                {
                    anyChanges = true;
                }
            }

            if (anyChanges)
            {
                InvalidateLayout();
            }

            List<IWindow> movedWindows = [];
            List<(IVirtualDesktop Desktop, IWindow Window)> memberships = [];
            using (m_backendLock.EnterScope())
            {
                foreach (var desktop in m_backend.SnapshotDesktops())
                {
                    var tree = m_backend.GetTree(desktop);
                    if (tree == null)
                        continue;
                    foreach (var window in windows)
                    {
                        if (tree.FindNode(window) != null)
                        {
                            memberships.Add((desktop, window));
                        }
                    }
                }
            }

            foreach (var membership in memberships)
            {
                try
                {
                    if (!membership.Desktop.HasWindow(membership.Window))
                    {
                        movedWindows.Add(membership.Window);
                    }
                }
                catch (Exception ex)
                {
                    m_logger.Debug(
                        ex,
                        "Could not verify desktop ownership while refreshing window handle");
                }
            }

            foreach (var movedWindow in movedWindows)
            {
                OnWindowRemoved(movedWindow, new WindowChangedEventArgs(movedWindow));
                OnWindowAdded(movedWindow, new WindowChangedEventArgs(movedWindow));
            }
        }

        private static bool CanSplit(TilingNode node)
        {
            return !node.PathToRoot.OfType<StackPanelNode>().Any();
        }

        public bool CanSplit(bool vertical)
        {
            if (!TryGetCurrentMasterSatelliteDesktop(out var desktop))
            {
                return false;
            }
            if (IsMasterSatelliteCapacityTransitionSource(desktop))
            {
                return false;
            }
            using (m_backendLock.EnterScope())
            {
                if (m_masterSatelliteCommands.IsActive(desktop))
                {
                    return true;
                }
                var focusedNode = m_backend.GetFocus(desktop);
                return focusedNode != null && CanSplit(focusedNode);
            }
        }

        public void Split(bool vertical)
        {
            MasterSatelliteCommandResult? algorithmicResult = null;
            var desktop = GetRequiredMasterSatelliteCommandDesktop();
            using (m_backendLock.EnterScope())
            {
                if (m_masterSatelliteCommands.IsActive(desktop))
                {
                    algorithmicResult = m_masterSatelliteCommands.SetSatelliteOrientation(
                        m_backend,
                        desktop,
                        vertical
                            ? SatelliteLayoutOrientation.Vertical
                            : SatelliteLayoutOrientation.Horizontal);
                }
                else
                {
                    var focusedNode = m_backend.GetFocus(desktop) ?? throw new TilingFailedException(TilingError.MissingTarget);
                    WrapInSplitPanel(focusedNode, vertical);
                    m_backend.SetFocus(focusedNode);
                }
            }
            if (algorithmicResult != null)
            {
                CompleteMasterSatelliteCommand(
                    "SetSatelliteOrientation",
                    desktop,
                    algorithmicResult);
            }
        }

        public bool CanFloat()
        {
            var window = m_workspace.FocusedWindow;
            return window != null
                && (!TryGetCurrentMasterSatelliteDesktop(out var desktop)
                    || !IsMasterSatelliteCapacityTransitionSource(desktop))
                && CanManage(window, ignoreFloating: true);
        }

        public void Float()
        {
            var window = m_workspace.FocusedWindow ?? throw new TilingFailedException(TilingError.MissingTarget);
            ToggleFloat(window);
        }

        private static bool CanStack(TilingNode node)
        {
            return !node.PathToRoot.OfType<StackPanelNode>().Any();
        }

        public bool CanStack()
        {
            if (!TryGetCurrentMasterSatelliteDesktop(out var desktop))
            {
                return false;
            }
            using (m_backendLock.EnterScope())
            {
                if (m_masterSatelliteCommands.IsActive(desktop))
                {
                    return false;
                }
                var focusedNode = m_backend.GetFocus(desktop);
                return focusedNode != null && CanStack(focusedNode);
            }
        }

        public void Stack()
        {
            MasterSatelliteCommandResult? algorithmicResult = null;
            var desktop = GetRequiredMasterSatelliteCommandDesktop();
            using (m_backendLock.EnterScope())
            {
                if (m_masterSatelliteCommands.IsActive(desktop))
                {
                    algorithmicResult = m_masterSatelliteCommands.RejectStackPanel(desktop);
                }
                else
                {
                    var focusedNode = m_backend.GetFocus(desktop);
                    if (focusedNode == null || focusedNode.Parent == null)
                        throw new TilingFailedException(TilingError.MissingTarget);

                    WrapInStackPanel(focusedNode);
                }
            }
            if (algorithmicResult != null)
            {
                CompleteMasterSatelliteCommand(
                    "CreateStackPanel",
                    desktop,
                    algorithmicResult);
            }
        }

        public bool CanPullUp()
        {
            if (!TryGetCurrentMasterSatelliteDesktop(out var desktop))
            {
                return false;
            }
            using (m_backendLock.EnterScope())
            {
                if (m_masterSatelliteCommands.IsActive(desktop))
                {
                    return m_masterSatelliteCommands.CanPromoteFocusedWindow(
                        m_backend,
                        desktop);
                }
                var focusedNode = m_backend.GetFocus(desktop);
                return focusedNode != null && focusedNode.Parent != focusedNode.Desktop!.Root;
            }
        }

        public void PullUp()
        {
            MasterSatelliteCommandResult? algorithmicResult = null;
            var desktop = GetRequiredMasterSatelliteCommandDesktop();
            using (m_backendLock.EnterScope())
            {
                if (m_masterSatelliteCommands.IsActive(desktop))
                {
                    algorithmicResult = m_masterSatelliteCommands.PromoteFocusedWindow(
                        m_backend,
                        desktop);
                }
                else
                {
                    var focusedNode = m_backend.GetFocus(desktop) ?? throw new TilingFailedException(TilingError.MissingTarget);
                    MoveToParentPanel(focusedNode);
                    m_backend.SetFocus(focusedNode);
                }
            }
            if (algorithmicResult != null)
            {
                CompleteMasterSatelliteCommand(
                    "PullWindowUp",
                    desktop,
                    algorithmicResult);
            }
        }

        public async void ToggleDesktop()
        {
            if (m_active)
            {
                Stop();
                await Task.Delay(50);
                foreach (var window in m_workspace.GetCurrentDesktopSnapshot())
                {
                    try
                    {
                        if (window.CanMinimize)
                            window.SetState(WindowState.Minimized);
                    }
                    catch (Exception e) when (e is Win32Exception || e is InvalidWindowReferenceException)
                    {
                        // Ignore
                    }
                }
            }
            else
            {
                foreach (var window in m_workspace.GetCurrentDesktopSnapshot())
                {
                    try
                    {
                        if (window.CanMinimize)
                            window.SetState(WindowState.Restored);
                    }
                    catch (Exception e) when (e is Win32Exception || e is InvalidWindowReferenceException)
                    {
                        // Ignore
                    }
                }
                await Task.Delay(50);
                Start();
                Refresh();
            }
        }

        public void Dispose()
        {
            if (m_disposed)
            {
                return;
            }
            RecoverMasterSatelliteTransfersBeforeDisposal();
            m_disposed = true;
            m_logger.Information("No longer managing display {Display}", m_display);

            m_active = false;
            m_subscriptions.Dispose();

            PlacementFailed = null;
            AlgorithmicLayoutChanged = null;

            m_workspace.VirtualDesktopManager.DesktopAdded -= OnDesktopAdded;
            m_workspace.VirtualDesktopManager.DesktopRemoved -= OnDesktopRemoved;
            m_workspace.VirtualDesktopManager.CurrentDesktopChanged -= OnCurrentDesktopChanged;
            m_workspace.CursorLocationChanged -= OnCursorLocationChanged;

            m_workspace.WindowAdded -= OnWindowAdded;
            m_workspace.WindowRemoved -= OnWindowRemoved;
            m_workspace.DisplayManager.PrimaryDisplayChanged -=
                OnPrimaryDisplayChanged;

            m_display.ScalingChanged -= OnDisplayScalingChanged;
            m_display.WorkAreaChanged -= OnDisplayWorkAreaChanged;
            m_algorithmicLayoutCoordinator.TransferTerminated -=
                OnAlgorithmicTransferTerminated;
            DisposeMasterSatelliteLifecycle();

            // There is still the possibility that OnWindowAdded gets called, but hopefully that does not happen too often.
            using (m_windowSetLock.EnterScope())
            {
                foreach (var window in m_windowSet)
                {
                    UnbindEventHandlers(window);
                }
            }
            UntrackAllWindowLifetimes();
        }

        public bool CanResize(PanelOrientation orientation, double displayPercentage)
        {
            if (!TryGetCurrentMasterSatelliteDesktop(out var desktop))
            {
                return false;
            }
            using (m_backendLock.EnterScope())
            {
                if (m_masterSatelliteCommands.IsActive(desktop))
                {
                    return m_masterSatelliteCommands.CanResizeFocusedMaster(
                        m_backend,
                        desktop,
                        orientation,
                        displayPercentage);
                }
                var focusedNode = m_backend.GetFocus(desktop);
                if (focusedNode is not WindowNode focusedWindow)
                    return false;

                var window = focusedWindow.WindowReference;
                var oldSize = window.Position;
                var display = m_workspace.DisplayManager.Displays.FirstOrDefault(x => x.WorkArea.Contains(window.Position.Center));
                if (display == null)
                    return false;

                var verticalDelta = (int)(display.WorkArea.Height * displayPercentage);
                var horizontalDelta = (int)(display.WorkArea.Width * displayPercentage);

                var grandparent = focusedNode.Ancestors
                        .Select(x => x as GridLikeNode)
                        .Where(x => x != null)
                        .FirstOrDefault(x => x!.CanResizeInOrientation(orientation));
                if (grandparent != null)
                {
                    double newSize;
                    switch (orientation)
                    {
                        case PanelOrientation.Horizontal:
                            newSize = oldSize.Width + horizontalDelta / 2;
                            return focusedNode.Parent!.GetMaxChildSize(focusedNode).X > newSize;
                        case PanelOrientation.Vertical:
                            newSize = oldSize.Height + verticalDelta / 2;
                            return focusedNode.Parent!.GetMaxChildSize(focusedNode).Y > newSize;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(orientation));
                    }
                }

                return false;
            }
        }

        public void Resize(PanelOrientation orientation, double displayPercentage)
        {
            MasterSatelliteCommandResult? algorithmicResult = null;
            var desktop = GetRequiredMasterSatelliteCommandDesktop();
            using (m_backendLock.EnterScope())
            {
                if (m_masterSatelliteCommands.IsActive(desktop))
                {
                    algorithmicResult = m_masterSatelliteCommands.ResizeFocusedMaster(
                        m_backend,
                        desktop,
                        orientation,
                        displayPercentage);
                }
                else
                {
                    var focusedNode = m_backend.GetFocus(desktop);
                    if (focusedNode is not WindowNode focusedWindow)
                        throw new TilingFailedException(TilingError.MissingTarget);

                    var window = focusedWindow.WindowReference;
                    var oldSize = window.Position;
                    var display = m_workspace.DisplayManager.Displays.FirstOrDefault(x => x.WorkArea.Contains(window.Position.Center)) ?? throw new TilingFailedException(TilingError.Failed);
                    var verticalDelta = (int)(display.WorkArea.Height * displayPercentage);
                    var horizontalDelta = (int)(display.WorkArea.Width * displayPercentage);
                    var newSize = orientation switch
                    {
                        PanelOrientation.Horizontal => new Rectangle(oldSize.Left - horizontalDelta / 2, oldSize.Top, oldSize.Right + horizontalDelta / 2, oldSize.Bottom),
                        PanelOrientation.Vertical => new Rectangle(oldSize.Left, oldSize.Top - verticalDelta / 2, oldSize.Right, oldSize.Bottom + verticalDelta / 2),
                        _ => throw new ArgumentOutOfRangeException(nameof(orientation)),
                    };
                    m_backend.ResizeWindow(window, newSize, oldSize);
                }
            }
            if (algorithmicResult != null)
            {
                CompleteMasterSatelliteCommand(
                    "ResizeMaster",
                    desktop,
                    algorithmicResult);
            }
            else
            {
                InvalidateLayout();
            }
        }

        public IWindow? GetFocus()
        {
            if (!TryGetCurrentMasterSatelliteDesktop(out var desktop))
            {
                return null;
            }
            using (m_backendLock.EnterScope())
            {
                var focusedNode = m_backend.GetFocus(desktop);
                if (focusedNode is not WindowNode focusedWindow)
                    return null;

                return focusedWindow.WindowReference;
            }
        }

        public Rectangle GetBounds()
        {
            return m_display.Bounds;
        }

        public IWindow? FindClosest(Point center)
        {
            static double Distance(Point point1, Point point2)
            {
                return Math.Pow(point1.X - point2.X, 2) + Math.Pow(point1.Y - point2.Y, 2);
            }

            var desktop = m_workspace.VirtualDesktopManager.CurrentDesktop;
            using (m_backendLock.EnterScope())
            {
                var tree = m_backend.GetTree(desktop);
                var closestNode = tree!.Root!.Windows
                    .OrderBy(x => Distance(center, x.ComputedRectangle.Center))
                    .FirstOrDefault();

                if (closestNode != null)
                {
                    return closestNode.WindowReference;
                }
            }

            return null;
        }
    }
}
