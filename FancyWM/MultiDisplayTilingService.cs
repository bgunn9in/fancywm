using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Windows.Threading;

using FancyWM.Models;
using FancyWM.Utilities;
using FancyWM.AlgorithmicLayouts;

using WinMan;
using FancyWM.Layouts.Tiling;
using Serilog;

namespace FancyWM
{
    class MultiDisplayTilingService : ITilingService, IDisposable
    {
        public event EventHandler<TilingFailedEventArgs>? PlacementFailed;

        public event EventHandler<AlgorithmicLayoutEvent>? AlgorithmicLayoutChanged;
        public event EventHandler<EventArgs>? PendingIntentChanged;

        public Dispatcher Dispatcher { get; }
        public IWorkspace Workspace { get; }
        public IAnimationThread AnimationThread { get; }

        public bool Active => GetPrimaryTilingService().Active;

        public ITilingServiceIntent? PendingIntent
        {
            get => GetActiveTilingService().PendingIntent;
            set => GetActiveTilingService().PendingIntent = value;
        }

        public IReadOnlyCollection<IWindowMatcher> ExclusionMatchers
        {
            get => m_exclusionMatchers;
            set
            {
                ITilingService[] tilingServices;
                lock (m_syncRoot)
                {
                    m_exclusionMatchers = value;
                    tilingServices = [.. m_tilingServices.Values];
                }
                foreach (var tiling in tilingServices)
                {
                    tiling.ExclusionMatchers = value;
                }
            }
        }

        public bool ShowPreviewFocus
        {
            get => m_showPreviewFocus;
            set
            {
                ITilingService[] tilingServices;
                lock (m_syncRoot)
                {
                    m_showPreviewFocus = value;
                    tilingServices = [.. m_tilingServices.Values];
                }
                foreach (var tiling in tilingServices)
                {
                    tiling.ShowPreviewFocus = value;
                }
            }
        }

        private readonly Dictionary<IDisplay, ITilingService> m_tilingServices = [];
        private readonly CompositeDisposable m_subscriptions = [];
        private readonly Subject<Unit> m_focusedWindowLocationChanges = new();
        private readonly ILogger m_logger;
        private IDisplay m_activeDisplay;
        private bool m_showFocus;
        private bool m_showPreviewFocus;
        private bool m_autoCollapse;
        private int m_autoSplitCount;
        private bool m_delayReposition;
        private IReadOnlyCollection<IWindowMatcher> m_exclusionMatchers = [];
        private readonly IObservable<ITilingServiceSettings> m_settings;
        private readonly AlgorithmicLayoutCoordinator m_algorithmicLayoutCoordinator;
        private readonly Func<IDisplay, ITilingService> m_tilingServiceFactory;
        private readonly Action<ITilingService, bool> m_setAutoRegisterWindows;
        private readonly Action m_scheduleGarbageCollection;
        private readonly object m_syncRoot = new();
        private IWindow? m_observedFocusedWindow;
        private bool m_disposed;

        public MultiDisplayTilingService(
            IWorkspace workspace,
            IAnimationThread animationThread,
            IObservable<ITilingServiceSettings> settings,
            AlgorithmicLayoutCoordinator algorithmicLayoutCoordinator)
            : this(
                workspace,
                animationThread,
                settings,
                algorithmicLayoutCoordinator,
                App.Current.Logger,
                display => new TilingService(
                    workspace,
                    display,
                    animationThread,
                    settings,
                    algorithmicLayoutCoordinator,
                    true),
                (service, value) =>
                    ((TilingService)service).AutoRegisterWindows = value,
                GCHelper.ScheduleCollection)
        {
        }

        internal MultiDisplayTilingService(
            IWorkspace workspace,
            IAnimationThread animationThread,
            IObservable<ITilingServiceSettings> settings,
            AlgorithmicLayoutCoordinator algorithmicLayoutCoordinator,
            ILogger logger,
            Func<IDisplay, ITilingService> tilingServiceFactory,
            Action<ITilingService, bool> setAutoRegisterWindows,
            Action scheduleGarbageCollection)
        {
            Dispatcher = Dispatcher.CurrentDispatcher;
            Workspace = workspace;
            AnimationThread = animationThread;
            m_settings = settings;
            m_algorithmicLayoutCoordinator = algorithmicLayoutCoordinator
                ?? throw new ArgumentNullException(nameof(algorithmicLayoutCoordinator));
            if (!ReferenceEquals(m_algorithmicLayoutCoordinator.Dispatcher, Dispatcher))
            {
                throw new ArgumentException(
                    "The algorithmic layout coordinator must use the multi-display service Dispatcher.",
                    nameof(algorithmicLayoutCoordinator));
            }
            if (!ReferenceEquals(m_algorithmicLayoutCoordinator.Workspace, Workspace))
            {
                throw new ArgumentException(
                    "The algorithmic layout coordinator must belong to the multi-display workspace.",
                    nameof(algorithmicLayoutCoordinator));
            }
            m_logger = logger ?? throw new ArgumentNullException(nameof(logger));
            m_tilingServiceFactory = tilingServiceFactory
                ?? throw new ArgumentNullException(nameof(tilingServiceFactory));
            m_setAutoRegisterWindows = setAutoRegisterWindows
                ?? throw new ArgumentNullException(nameof(setAutoRegisterWindows));
            m_scheduleGarbageCollection = scheduleGarbageCollection
                ?? throw new ArgumentNullException(nameof(scheduleGarbageCollection));
            m_logger.Information($"Using the multi-monitor tiling backend");

            try
            {
                foreach (var display in Workspace.DisplayManager.Displays)
                {
                    ITilingService? tiling = null;
                    try
                    {
                        tiling = m_tilingServiceFactory(display);
                        tiling.ExclusionMatchers = m_exclusionMatchers;
                        tiling.ShowPreviewFocus = m_showPreviewFocus;
                        tiling.PlacementFailed += OnTilingFailed;
                        tiling.AlgorithmicLayoutChanged += OnAlgorithmicLayoutChanged;
                        tiling.PendingIntentChanged += OnPendingIntentChanged;
                        tiling.Start();
                        m_tilingServices.Add(display, tiling);
                        tiling = null;
                    }
                    finally
                    {
                        if (tiling != null)
                        {
                            DisposeTilingService(tiling);
                        }
                    }
                }

                var registeredDisplays = m_tilingServices.Keys.ToArray();
                var initialFocusedWindow = TryGetFocusedWindow();
                m_activeDisplay = MultiDisplayActiveDisplaySelector.Select(
                        registeredDisplays,
                        TryGetWindowCenter(initialFocusedWindow),
                        Workspace.DisplayManager.PrimaryDisplay)
                    ?? Workspace.DisplayManager.PrimaryDisplay;
                m_setAutoRegisterWindows(m_tilingServices[m_activeDisplay], true);
                m_tilingServices[m_activeDisplay].Refresh();

                Workspace.DisplayManager.Added += OnDisplayAdded;
                Workspace.DisplayManager.Removed += OnDisplayRemoved;

                Workspace.FocusedWindowChanged += OnFocusedWindowChanged;
                m_subscriptions.Add(Disposable.Create(
                    () => Workspace.FocusedWindowChanged -= OnFocusedWindowChanged));
                m_observedFocusedWindow = initialFocusedWindow;
                if (m_observedFocusedWindow != null)
                {
                    m_observedFocusedWindow.PositionChanged += OnWindowPositionChanged;
                }

                var focusLocationObservable = m_focusedWindowLocationChanges
                    .Throttle(TimeSpan.FromMilliseconds(100))
                    .Do(_ => QueueDispatcherAction(
                        () => UpdateActiveDisplay(reason: "the focused window was moved"),
                        "updating the active display after a focused-window move"));
                m_subscriptions.Add(focusLocationObservable.Subscribe());
            }
            catch
            {
                RollbackFailedInitialization();
                throw;
            }
        }

        private void RollbackFailedInitialization()
        {
            m_disposed = true;
            TryInitializationCleanup(
                () => Workspace.DisplayManager.Added -= OnDisplayAdded,
                "unsubscribing from display-added events");
            TryInitializationCleanup(
                () => Workspace.DisplayManager.Removed -= OnDisplayRemoved,
                "unsubscribing from display-removed events");
            TryInitializationCleanup(
                () => Workspace.FocusedWindowChanged -= OnFocusedWindowChanged,
                "unsubscribing from focused-window events");

            if (m_observedFocusedWindow != null)
            {
                TryInitializationCleanup(
                    () => m_observedFocusedWindow.PositionChanged -= OnWindowPositionChanged,
                    "unsubscribing from focused-window position events");
                m_observedFocusedWindow = null;
            }

            TryInitializationCleanup(
                m_subscriptions.Dispose,
                "disposing multi-display subscriptions");

            var initializedServices = m_tilingServices.Values.ToArray();
            m_tilingServices.Clear();
            foreach (var tiling in initializedServices)
            {
                DisposeTilingService(tiling);
            }

            TryInitializationCleanup(
                m_focusedWindowLocationChanges.OnCompleted,
                "completing focused-window location events");
            TryInitializationCleanup(
                m_focusedWindowLocationChanges.Dispose,
                "disposing focused-window location events");
        }

        private void TryInitializationCleanup(Action cleanup, string operation)
        {
            try
            {
                cleanup();
            }
            catch (Exception exception)
            {
                m_logger.Error(
                    exception,
                    "Multi-display initialization rollback failed while {Operation}",
                    operation);
            }
        }

        private void QueueDispatcherAction(Action action, string operation)
        {
            ArgumentNullException.ThrowIfNull(action);
            ArgumentException.ThrowIfNullOrWhiteSpace(operation);

            void ApplySafely()
            {
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    m_logger.Error(
                        exception,
                        "Multi-display dispatcher action failed while {Operation}",
                        operation);
                }
            }

            try
            {
                Dispatcher.BeginInvoke((Action)ApplySafely);
            }
            catch (Exception exception)
            {
                m_logger.Error(
                    exception,
                    "Could not queue multi-display dispatcher action while {Operation}",
                    operation);
            }
        }

        private void OnDisplayRemoved(object? sender, DisplayChangedEventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                QueueDispatcherAction(
                    () => OnDisplayRemoved(sender, e),
                    "removing a display");
                return;
            }

            ITilingService? removedTiling = null;
            bool updateActiveDisplay = false;
            lock (m_syncRoot)
            {
                if (m_disposed)
                {
                    return;
                }
                updateActiveDisplay = m_activeDisplay.Equals(e.Source);
                if (m_tilingServices.Remove(e.Source, out var tiling))
                {
                    removedTiling = tiling;
                }
            }

            try
            {
                if (updateActiveDisplay)
                {
                    UpdateActiveDisplay($"the active display {e.Source} was removed");
                }
            }
            catch (Exception exception)
            {
                m_logger.Error(
                    exception,
                    "Could not reroute the active display after removing {Display}",
                    e.Source);
            }
            finally
            {
                if (removedTiling != null)
                {
                    DisposeTilingService(removedTiling);
                }
                m_scheduleGarbageCollection();
            }
        }

        private void OnDisplayAdded(object? sender, DisplayChangedEventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                QueueDispatcherAction(
                    () => OnDisplayAdded(sender, e),
                    "adding a display");
                return;
            }

            bool showPreviewFocus;
            IReadOnlyCollection<IWindowMatcher> exclusionMatchers;
            lock (m_syncRoot)
            {
                if (m_disposed || m_tilingServices.ContainsKey(e.Source))
                {
                    return;
                }
                showPreviewFocus = m_showPreviewFocus;
                exclusionMatchers = m_exclusionMatchers;
            }

            var tiling = m_tilingServiceFactory(e.Source);
            tiling.ShowPreviewFocus = showPreviewFocus;
            tiling.ExclusionMatchers = exclusionMatchers;
            tiling.PlacementFailed += OnTilingFailed;
            tiling.AlgorithmicLayoutChanged += OnAlgorithmicLayoutChanged;
            tiling.PendingIntentChanged += OnPendingIntentChanged;
            try
            {
                tiling.Start();
            }
            catch
            {
                DisposeTilingService(tiling);
                throw;
            }

            bool registered;
            lock (m_syncRoot)
            {
                registered = !m_disposed && m_tilingServices.TryAdd(e.Source, tiling);
            }
            if (!registered)
            {
                DisposeTilingService(tiling);
                return;
            }

            UpdateActiveDisplay(reason: $"display {e.Source} was added");
        }

        private void OnPendingIntentChanged(object? sender, EventArgs e)
        {
            ITilingService[] tilingServices;
            lock (m_syncRoot)
            {
                if (m_disposed)
                {
                    return;
                }
                tilingServices = [.. m_tilingServices.Values];
            }
            PendingIntentChanged?.Invoke(this, e);
            foreach (var tiling in tilingServices)
            {
                tiling.PendingIntent = ((ITilingService)sender!).PendingIntent;
            }
        }

        private void OnWindowPositionChanged(object? sender, WindowPositionChangedEventArgs e)
        {
            lock (m_syncRoot)
            {
                if (m_disposed)
                {
                    return;
                }
            }
            try
            {
                m_focusedWindowLocationChanges.OnNext(Unit.Default);
            }
            catch (ObjectDisposedException)
            {
                // Dispose can complete between the guarded state check and the
                // notification when WinMan raises PositionChanged concurrently.
                m_logger.Debug(
                    "Ignored a focused-window position notification during multi-display disposal");
            }
        }

        private void OnFocusedWindowChanged(object? sender, FocusedWindowChangedEventArgs e)
        {
            var focusedWindow = e.NewFocusedWindow;
            lock (m_syncRoot)
            {
                if (m_disposed)
                {
                    return;
                }
                if (m_observedFocusedWindow != null)
                {
                    m_observedFocusedWindow.PositionChanged -= OnWindowPositionChanged;
                }
                m_observedFocusedWindow = focusedWindow;
                if (focusedWindow == null)
                {
                    return;
                }
                focusedWindow.PositionChanged += OnWindowPositionChanged;
            }

            QueueDispatcherAction(
                () => UpdateActiveDisplay($"the focused window has changed to {focusedWindow.DebugString()}"),
                "updating the active display after a focus change");
        }

        private void UpdateActiveDisplay(string? reason = null)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => UpdateActiveDisplay(reason));
                return;
            }

            IDisplay[] registeredDisplays;
            IDisplay previousDisplay;
            lock (m_syncRoot)
            {
                if (m_disposed || m_tilingServices.Count == 0)
                {
                    return;
                }
                registeredDisplays = [.. m_tilingServices.Keys];
                previousDisplay = m_activeDisplay;
            }

            var primaryDisplay = Workspace.DisplayManager.PrimaryDisplay;
            var selectedDisplay = MultiDisplayActiveDisplaySelector.Select(
                registeredDisplays,
                TryGetFocusedWindowCenter(),
                primaryDisplay);

            ITilingService? oldTiling;
            ITilingService newTiling;
            IDisplay newActiveDisplay;
            lock (m_syncRoot)
            {
                if (m_disposed || m_tilingServices.Count == 0)
                {
                    return;
                }
                newActiveDisplay = selectedDisplay != null
                    && m_tilingServices.ContainsKey(selectedDisplay)
                        ? selectedDisplay
                        : m_tilingServices.ContainsKey(primaryDisplay)
                            ? primaryDisplay
                            : m_tilingServices.Keys.First();
                if (newActiveDisplay.Equals(m_activeDisplay)
                    && m_tilingServices.ContainsKey(m_activeDisplay))
                {
                    return;
                }

                m_tilingServices.TryGetValue(m_activeDisplay, out oldTiling);
                m_activeDisplay = newActiveDisplay;
                newTiling = m_tilingServices[newActiveDisplay];
            }

            m_logger.Information($"Active display changed from {previousDisplay} to {newActiveDisplay}");
            if (oldTiling != null)
            {
                m_setAutoRegisterWindows(oldTiling, true);
            }
            m_setAutoRegisterWindows(newTiling, true);
            newTiling.Refresh();
            m_logger.Verbose($"Check triggered because {reason}.");
        }

        private ITilingService GetActiveTilingService()
        {
            if (!Dispatcher.CheckAccess())
            {
                return Dispatcher.Invoke(GetActiveTilingService);
            }

            UpdateActiveDisplay(reason: "an operation requested the active display");
            lock (m_syncRoot)
            {
                return m_tilingServices[m_activeDisplay];
            }
        }

        private Point? TryGetFocusedWindowCenter()
        {
            return TryGetWindowCenter(TryGetFocusedWindow());
        }

        private IWindow? TryGetFocusedWindow()
        {
            try
            {
                return Workspace.FocusedWindow;
            }
            catch (InvalidWindowReferenceException ex)
            {
                m_logger.Debug(
                    ex,
                    "The focused window became invalid while selecting the active display");
                return null;
            }
        }

        private Point? TryGetWindowCenter(IWindow? window)
        {
            try
            {
                return window?.Position.Center;
            }
            catch (InvalidWindowReferenceException ex)
            {
                m_logger.Debug(
                    ex,
                    "The focused window became invalid while selecting the active display");
                return null;
            }
        }

        private ITilingService GetPrimaryTilingService()
        {
            lock (m_syncRoot)
            {
                return m_tilingServices[Workspace.DisplayManager.PrimaryDisplay];
            }
        }

        private void OnTilingFailed(object? sender, TilingFailedEventArgs e)
        {
            lock (m_syncRoot)
            {
                if (m_disposed)
                {
                    return;
                }
            }
            PlacementFailed?.Invoke(this, e);
        }

        private void OnAlgorithmicLayoutChanged(
            object? sender,
            AlgorithmicLayoutEvent e)
        {
            lock (m_syncRoot)
            {
                if (m_disposed)
                {
                    return;
                }
            }
            foreach (var subscriber in AlgorithmicLayoutChanged?
                .GetInvocationList() ?? [])
            {
                try
                {
                    ((EventHandler<AlgorithmicLayoutEvent>)subscriber)(this, e);
                }
                catch (Exception ex)
                {
                    m_logger.Error(
                        ex,
                        "Algorithmic layout event subscriber failed; kind={EventKind}, correlation={CorrelationId}",
                        e.Kind,
                        e.CorrelationId);
                }
            }
        }

        public void Dispose()
        {
            List<ITilingService> tilingServices;
            IWindow? observedFocusedWindow;
            lock (m_syncRoot)
            {
                if (m_disposed)
                {
                    return;
                }
                m_disposed = true;
                tilingServices = [.. m_tilingServices.Values];
                m_tilingServices.Clear();
                observedFocusedWindow = m_observedFocusedWindow;
                m_observedFocusedWindow = null;
            }

            Workspace.DisplayManager.Added -= OnDisplayAdded;
            Workspace.DisplayManager.Removed -= OnDisplayRemoved;
            Workspace.FocusedWindowChanged -= OnFocusedWindowChanged;

            if (observedFocusedWindow != null)
            {
                observedFocusedWindow.PositionChanged -= OnWindowPositionChanged;
            }

            foreach (var tiling in tilingServices)
            {
                DisposeTilingService(tiling);
            }

            m_focusedWindowLocationChanges.OnCompleted();
            m_subscriptions.Dispose();
            m_focusedWindowLocationChanges.Dispose();

            PlacementFailed = null;
            AlgorithmicLayoutChanged = null;
            PendingIntentChanged = null;
        }

        private void DisposeTilingService(ITilingService tiling)
        {
            tiling.PlacementFailed -= OnTilingFailed;
            tiling.AlgorithmicLayoutChanged -= OnAlgorithmicLayoutChanged;
            tiling.PendingIntentChanged -= OnPendingIntentChanged;
            try
            {
                tiling.Stop();
            }
            catch (Exception ex)
            {
                m_logger.Error(ex, "Stopping a display tiling service during disposal failed");
            }
            try
            {
                tiling.Dispose();
            }
            catch (Exception ex)
            {
                m_logger.Error(ex, "Disposing a display tiling service failed");
            }
        }

        public void Stack()
        {
            GetActiveTilingService().Stack();
        }

        public bool DiscoverWindows()
        {
            bool anyChanges = false;
            ITilingService[] tilingServices;
            lock (m_syncRoot)
            {
                tilingServices = [.. m_tilingServices.Values];
            }
            foreach (var tiling in tilingServices)
            {
                anyChanges = anyChanges || tiling.DiscoverWindows();
            }
            return anyChanges;
        }

        public void Refresh()
        {
            ITilingService[] tilingServices;
            lock (m_syncRoot)
            {
                tilingServices = [.. m_tilingServices.Values];
            }
            foreach (var tiling in tilingServices)
            {
                tiling.Refresh();
            }
        }

        public void Float()
        {
            GetActiveTilingService().Float();
        }

        public void MoveFocus(TilingDirection direction)
        {
            var tiling = GetActiveTilingService();
            if (tiling.CanMoveFocus(direction))
            {
                tiling.MoveFocus(direction);
                return;
            }

            var closest = SnapshotTilingServices()
                .Where(x => x != tiling)
                .OrderBy(x => SqrDistanceInDirection(tiling.GetBounds().Center, x.GetBounds().Center, direction))
                .FirstOrDefault();
            if (closest == null)
            {
                return;
            }

            IWindow? focusedWindow = tiling.GetFocus();
            if (focusedWindow == null)
            {
                return;
            }

            var closestWindow = closest.FindClosest(focusedWindow.Position.Center);
            if (closestWindow != null)
            {
                FocusHelper.ForceActivate(closestWindow.Handle);
            }
        }

        public void PullUp()
        {
            GetActiveTilingService().PullUp();
        }

        public void SwapFocus(TilingDirection direction)
        {
            GetActiveTilingService().SwapFocus(direction);
        }

        public void Split(bool vertical)
        {
            GetActiveTilingService().Split(vertical);
        }

        public void Stop()
        {
            ITilingService[] tilingServices;
            lock (m_syncRoot)
            {
                tilingServices = [.. m_tilingServices.Values];
            }
            foreach (var tiling in tilingServices)
            {
                tiling.Stop();
            }
        }

        public void Start()
        {
            ITilingService[] tilingServices;
            lock (m_syncRoot)
            {
                tilingServices = [.. m_tilingServices.Values];
            }
            foreach (var tiling in tilingServices)
            {
                tiling.Start();
            }
        }

        public bool CanSplit(bool vertical)
        {
            return GetActiveTilingService().CanSplit(vertical);
        }

        public bool CanStack()
        {
            return GetActiveTilingService().CanStack();
        }

        public bool CanFloat()
        {
            return GetActiveTilingService().CanFloat();
        }

        public bool CanMoveFocus(TilingDirection direction)
        {
            if (GetActiveTilingService().CanMoveFocus(direction))
            {
                return true;
            }

            var tiling = GetActiveTilingService();
            var closest = SnapshotTilingServices()
                .OrderBy(x => SqrDistanceInDirection(tiling.GetBounds().Center, x.GetBounds().Center, direction))
                .FirstOrDefault();
            if (closest == null)
            {
                return false;
            }

            IWindow? focusedWindow = tiling.GetFocus();
            if (focusedWindow == null)
            {
                return false;
            }

            return closest.FindClosest(focusedWindow.Position.Center) != null;
        }

        public bool CanPullUp()
        {
            return GetActiveTilingService().CanPullUp();
        }

        public bool CanSwapFocus(TilingDirection direction)
        {
            return GetActiveTilingService().CanSwapFocus(direction);
        }

        private ITilingService[] SnapshotTilingServices()
        {
            lock (m_syncRoot)
            {
                return [.. m_tilingServices.Values];
            }
        }

        public bool CanMoveWindow(TilingDirection direction)
        {
            return GetActiveTilingService().CanMoveWindow(direction);
        }

        public void MoveWindow(TilingDirection direction)
        {
            GetActiveTilingService().MoveWindow(direction);
        }

        public bool CanResize(PanelOrientation orientation, double displayPercentage)
        {
            return GetActiveTilingService().CanResize(orientation, displayPercentage);
        }

        public void Resize(PanelOrientation orientation, double displayPercentage)
        {
            GetActiveTilingService().Resize(orientation, displayPercentage);
        }

        public bool CanToggleMasterSatelliteLayout()
        {
            return GetActiveTilingService().CanToggleMasterSatelliteLayout();
        }

        public void ToggleMasterSatelliteLayout()
        {
            GetActiveTilingService().ToggleMasterSatelliteLayout();
        }

        public bool CanPromoteFocusedWindowToMaster()
        {
            return GetActiveTilingService().CanPromoteFocusedWindowToMaster();
        }

        public void PromoteFocusedWindowToMaster()
        {
            GetActiveTilingService().PromoteFocusedWindowToMaster();
        }

        public bool CanSwapMasterSide()
        {
            return GetActiveTilingService().CanSwapMasterSide();
        }

        public void SwapMasterSide()
        {
            GetActiveTilingService().SwapMasterSide();
        }

        public bool CanToggleSatelliteOrientation()
        {
            return GetActiveTilingService().CanToggleSatelliteOrientation();
        }

        public void ToggleSatelliteOrientation()
        {
            GetActiveTilingService().ToggleSatelliteOrientation();
        }

        public bool CanResetMasterRatio()
        {
            return GetActiveTilingService().CanResetMasterRatio();
        }

        public void ResetMasterRatio()
        {
            GetActiveTilingService().ResetMasterRatio();
        }

        public bool CanRebalanceMasterSatelliteLayout()
        {
            return GetActiveTilingService().CanRebalanceMasterSatelliteLayout();
        }

        public void RebalanceMasterSatelliteLayout()
        {
            GetActiveTilingService().RebalanceMasterSatelliteLayout();
        }

        public void ToggleDesktop()
        {
            GetActiveTilingService().ToggleDesktop();
        }

        public IWindow? GetFocus()
        {
            return GetActiveTilingService().GetFocus();
        }

        public Rectangle GetBounds()
        {
            throw new NotSupportedException();
        }

        public IWindow? FindClosest(Point center)
        {
            throw new NotSupportedException();
        }

        private static double SqrDistanceInDirection(Point from, Point to, TilingDirection direction)
        {
            switch (direction)
            {
                case TilingDirection.Left:
                    if (from.X > to.X)
                        return double.PositiveInfinity;
                    break;
                case TilingDirection.Right:
                    if (from.X < to.X)
                        return double.PositiveInfinity;
                    break;
                case TilingDirection.Up:
                    if (from.Y < to.Y)
                        return double.PositiveInfinity;
                    break;
                case TilingDirection.Down:
                    if (from.Y > to.Y)
                        return double.PositiveInfinity;
                    break;
            }

            return (from.X - to.X) * (from.X - to.X) + (from.Y - to.Y) * (from.Y - to.Y);
        }
    }
}
