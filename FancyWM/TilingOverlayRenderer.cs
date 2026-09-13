using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Windows;

using FancyWM.Layouts.Tiling;
using FancyWM.Models;
using FancyWM.Utilities;
using FancyWM.ViewModels;
using FancyWM.Controls;
using WinMan;
using FancyWM.Windows;
using System.Reactive.Linq;
using System.Reactive.Disposables;
using System.Threading;

namespace FancyWM
{
    /// <summary>
    /// The service-facing overlay surface. Keeping the native WPF host behind
    /// this boundary allows lifecycle and event integration to be exercised
    /// without opening a real overlay window in automated tests.
    /// </summary>
    internal interface ITilingOverlayRenderer : IDisposable
    {
        event EventHandler<PanelNode>? TilingPanelMoveRequested;
        event EventHandler<PanelNode>? TilingPanelMoving;
        event EventHandler<TilingNode>? TilingNodeFocusRequested;
        event EventHandler<TilingNode>? TilingNodePullUpRequested;
        event EventHandler<TilingNode>? TilingNodeCloseRequested;

        event EventHandler<TilingNode>? HorizontalSplitRequested;
        event EventHandler<TilingNode>? VerticalSplitRequested;
        event EventHandler<TilingNode>? StackRequested;
        event EventHandler<TilingNode>? PullUpRequested;
        event EventHandler<WindowNode>? FloatRequested;
        event EventHandler<WindowNode>? IgnoreProcessRequested;
        event EventHandler<WindowNode>? IgnoreClassRequested;
        event EventHandler<WindowNode>? BeginHorizontalWithRequested;
        event EventHandler<WindowNode>? BeginVerticalWithRequested;
        event EventHandler<WindowNode>? BeginStackWithRequested;

        int PanelSpacing { get; set; }
        Thickness PanelPadding { get; set; }
        IReadOnlySet<IWindow> PreviewWindows { get; set; }
        Rectangle? FocusRectangle { get; set; }
        Rectangle? PreviewRectangle { get; set; }
        IWindow? IntentSourceWindow { get; set; }

        void UpdateOverlay(
            IReadOnlyCollection<TilingNode> snapshot,
            IReadOnlyCollection<TilingNode> focusedPath);
        void InvalidateView();
        void InvalidateViewForPadding() => InvalidateView();
        void Show();
        void Hide();
    }

    public class TilingOverlayRenderer : IDisposable, ITilingOverlayRenderer
    {
        public event EventHandler<PanelNode>? TilingPanelMoveRequested;
        public event EventHandler<PanelNode>? TilingPanelMoving;
        public event EventHandler<TilingNode>? TilingNodeFocusRequested;
        public event EventHandler<TilingNode>? TilingNodePullUpRequested;
        public event EventHandler<TilingNode>? TilingNodeCloseRequested;


        public event EventHandler<TilingNode>? HorizontalSplitRequested;
        public event EventHandler<TilingNode>? VerticalSplitRequested;
        public event EventHandler<TilingNode>? StackRequested;
        public event EventHandler<TilingNode>? PullUpRequested;
        public event EventHandler<WindowNode>? FloatRequested;
        public event EventHandler<WindowNode>? IgnoreProcessRequested;
        public event EventHandler<WindowNode>? IgnoreClassRequested;
        public event EventHandler<WindowNode>? BeginHorizontalWithRequested;
        public event EventHandler<WindowNode>? BeginVerticalWithRequested;
        public event EventHandler<WindowNode>? BeginStackWithRequested;

        public int PanelSpacing { get; set; }
        public Thickness PanelPadding { get; set; }

        public IReadOnlySet<IWindow> PreviewWindows
        {
            get => m_previewWindows;
            set
            {
                if (m_previewWindows != value)
                {
                    OnSetPreviewWindows(oldValue: m_previewWindows, newValue: value);
                    m_previewWindows = value;
                }
            }
        }

        public Rectangle? FocusRectangle
        {
            get => m_focusRectangle;
            set
            {
                if (m_focusRectangle != value)
                {
                    OnSetFocusRectangle(oldValue: m_focusRectangle, newValue: value);
                    m_focusRectangle = value;
                }
            }
        }

        public Rectangle? PreviewRectangle
        {
            get => m_previewRectangle;
            set
            {
                if (m_previewRectangle != value)
                {
                    OnSetPreviewRectangle(oldValue: m_previewRectangle, newValue: value);
                    m_previewRectangle = value;
                }
            }
        }

        public IWindow? IntentSourceWindow
        {
            get => m_intentSourceWindow;
            set
            {
                if (m_intentSourceWindow != value)
                {
                    m_intentSourceWindow = value;
                }
            }
        }

        private readonly OverlayHost m_overlay;
        private readonly DelegateCommand<TilingNodeViewModel> m_panelItemPrimaryActionCommand;
        private readonly DelegateCommand<TilingNodeViewModel> m_panelItemSecondaryActionCommand;
        private readonly DelegateCommand<TilingNodeViewModel> m_panelItemCloseActionCommand;
        private readonly IDisplay m_display;
        private readonly CompositeDisposable m_disposables = new();
        private bool m_isOverlayInit = false;
        private readonly TilingOverlayViewModel m_viewModel = new();
        private double m_panelHeight = 22.0;
        private double m_windowPadding = 4.0;
        private int m_panelFontSize = 12;
        private IReadOnlyCollection<TilingNode> m_previousSnapshot = [];
        private readonly Dictionary<TilingNode, TilingNodeViewModel> m_nodeViewModels = [];
        private IReadOnlySet<IWindow> m_previewWindows = new HashSet<IWindow>();
        private Rectangle? m_focusRectangle;
        private Rectangle? m_previewRectangle;
        private IWindow? m_intentSourceWindow;
        private int m_disposeState;
        private int m_invalidationState;
        private int m_invalidationEpoch;
        private bool m_viewRecoveryPending;

        public TilingOverlayRenderer(IDisplay display, Func<IntPtr> overlayAnchorSource)
        {
            m_overlay = new OverlayHost(display)
            {
                AnchorSource = overlayAnchorSource
            };
            m_overlay.Show();

            m_panelItemPrimaryActionCommand = new DelegateCommand<TilingNodeViewModel>(OnOverlayPanelItemClick);
            m_panelItemSecondaryActionCommand = new DelegateCommand<TilingNodeViewModel>(OnOverlayPanelItemSecondaryAction);
            m_panelItemCloseActionCommand = new DelegateCommand<TilingNodeViewModel>(OnOverlayPanelItemCloseAction);
            m_display = display;
            m_display.ScalingChanged += OnDisplayScalingChanged;
            m_disposables.Add(App.Current.AppState.Settings
                .Subscribe(OnSettingsChanged));
        }

        private void OnSettingsChanged(Settings settings)
        {
            if (ApplySettingsIfActive(settings))
            {
                UpdateResources();
            }
        }

        internal bool ApplySettingsIfActive(Settings settings)
        {
            if (Volatile.Read(ref m_disposeState) != 0)
            {
                return false;
            }

            double panelHeight = settings.PanelHeight;
            if (Volatile.Read(ref m_disposeState) != 0) { return false; }
            m_panelHeight = panelHeight;
            if (Volatile.Read(ref m_disposeState) != 0) { return false; }

            double windowPadding = settings.WindowPadding;
            if (Volatile.Read(ref m_disposeState) != 0) { return false; }
            m_windowPadding = windowPadding;
            if (Volatile.Read(ref m_disposeState) != 0) { return false; }

            int panelFontSize = settings.PanelFontSize;
            if (Volatile.Read(ref m_disposeState) != 0) { return false; }
            m_panelFontSize = panelFontSize;
            return Volatile.Read(ref m_disposeState) == 0;
        }

        private void OnDisplayScalingChanged(object? sender, DisplayScalingChangedEventArgs e)
        {
            UpdateResources();
        }

        public void UpdateOverlay(IReadOnlyCollection<TilingNode> snapshot, IReadOnlyCollection<TilingNode> focusedPath)
        {
            int epoch = Volatile.Read(ref m_invalidationEpoch);
            if (!IsUpdateCurrent(epoch))
            {
                return;
            }

            if (m_viewRecoveryPending)
            {
                // A failed pass can leave the dictionary, observable collections
                // and published snapshot at different mutation boundaries. Reuse
                // the existing complete invalidation before accepting another
                // snapshot instead of trying to infer which callbacks mutated.
                InvalidateView();
                m_viewRecoveryPending = false;
                epoch = Volatile.Read(ref m_invalidationEpoch);
                if (!IsUpdateCurrent(epoch))
                {
                    return;
                }
            }

            try
            {
                UpdateViewModels(snapshot, focusedPath);
            }
            catch
            {
                // An obsolete pass may have invalidated and completed a newer
                // reentrant update. Do not tear down that current snapshot.
                if (IsUpdateCurrent(epoch))
                {
                    m_viewRecoveryPending = true;
                }
                throw;
            }
            if (!IsUpdateCurrent(epoch))
            {
                return;
            }

            if (!m_isOverlayInit)
            {
                m_isOverlayInit = true;

                var overlayView = new TilingOverlay
                {
                    ViewModel = m_viewModel
                };
                Draggable.AddDragStartedHandler(overlayView, OnDragStarted);
                Draggable.AddDragCompletedHandler(overlayView, OnDragCompleted);
                Draggable.AddDraggingHandler(overlayView, OnDraggingEvent);
                m_overlay.Content = overlayView;

                m_overlay.NonHitTestableContent = new NonHitTestableTilingOverlay
                {
                    ViewModel = m_viewModel
                };
            }
        }

        private void UpdateResources()
        {
            UpdateResourcesCore(
                () => m_overlay.Dispatcher.Thread == Thread.CurrentThread,
                callback => m_overlay.Dispatcher.BeginInvoke(callback));
        }

        internal void UpdateResourcesCore(Func<bool> checkAccess, Action<Action> post)
        {
            if (Volatile.Read(ref m_disposeState) != 0)
            {
                return;
            }

            bool hasAccess = checkAccess();
            if (Volatile.Read(ref m_disposeState) != 0)
            {
                return;
            }

            if (!hasAccess)
            {
                post(() => UpdateResourcesCore(checkAccess, post));
                return;
            }

            double scaling = m_display.Scaling;
            if (Volatile.Read(ref m_disposeState) != 0) { return; }
            m_viewModel.DisplayScaling = scaling;
            if (Volatile.Read(ref m_disposeState) != 0) { return; }

            scaling = m_display.Scaling;
            if (Volatile.Read(ref m_disposeState) != 0) { return; }
            m_viewModel.FontSize = scaling * m_panelFontSize;
            if (Volatile.Read(ref m_disposeState) != 0) { return; }

            scaling = m_display.Scaling;
            if (Volatile.Read(ref m_disposeState) != 0) { return; }
            m_viewModel.IconSize = scaling * m_panelFontSize;
            if (Volatile.Read(ref m_disposeState) != 0) { return; }

            scaling = m_display.Scaling;
            if (Volatile.Read(ref m_disposeState) != 0) { return; }
            m_viewModel.TabWidth = 175 * scaling * m_panelFontSize / 12;
        }

        private Rectangle AdjustForDisplay(Rectangle rectangle)
        {
            var bounds = m_display.Bounds;
            return new Rectangle(rectangle.Left - bounds.Left, rectangle.Top - bounds.Top, rectangle.Right - bounds.Left, rectangle.Bottom - bounds.Top);
        }

        private void UpdateViewModels(IReadOnlyCollection<TilingNode> snapshot, IReadOnlyCollection<TilingNode> focusedPath)
        {
            m_viewUpdateDepth++;
            try { UpdateViewModelsCore(snapshot, focusedPath); }
            finally { m_viewUpdateDepth--; }
        }

        private int m_viewUpdateDepth;

        private void UpdateViewModelsCore(IReadOnlyCollection<TilingNode> snapshot, IReadOnlyCollection<TilingNode> focusedPath)
        {
            int epoch = Volatile.Read(ref m_invalidationEpoch);
            if (!IsUpdateCurrent(epoch)) { return; }
            (var addList, var removeList, var persistList) = m_previousSnapshot.Changes(snapshot);
            if (!IsUpdateCurrent(epoch)) { return; }
            m_previousSnapshot = snapshot;
            HashSet<TilingNode> focusedNodes = [];
            TilingNode? focusedNode = null;
            bool isFirstFocusedNode = true;
            foreach (var node in focusedPath)
            {
                if (isFirstFocusedNode)
                {
                    focusedNode = node;
                    isFirstFocusedNode = false;
                }
                focusedNodes.Add(node);
            }
            if (!IsUpdateCurrent(epoch)) { return; }

            foreach (var removedNode in removeList)
            {
                if (m_nodeViewModels.TryGetValue(removedNode, out var vm))
                {
                    m_nodeViewModels.Remove(removedNode);
                    try
                    {
                        switch (vm)
                        {
                            case TilingPanelViewModel panelViewModel:
                                ChangeViewCollection(m_viewModel.PanelElements, panelViewModel, add: false);
                                break;
                            case TilingWindowViewModel windowViewModel:
                                ChangeViewCollection(m_viewModel.WindowElements, windowViewModel, add: false);
                                break;
                            default:
                                continue;
                        }
                    }
                    catch (Exception error)
                    {
                        ReleaseAfterFailure(
                            vm.Dispose,
                            error,
                            "TilingOverlayRenderer.UpdateViewModelsExceptions");
                        throw;
                    }
                    vm.Dispose();
                    if (!IsUpdateCurrent(epoch)) { return; }
                }
            }

            foreach (var addedNode in addList)
            {
                var vm = CreateViewModel(addedNode, focusedNodes, focusedNode, epoch);
                if (!IsUpdateCurrent(epoch)) { return; }
                switch (vm)
                {
                    case TilingPanelViewModel panelViewModel:
                        ChangeViewCollection(m_viewModel.PanelElements, panelViewModel, add: true);
                        break;
                    case TilingWindowViewModel windowViewModel:
                        ChangeViewCollection(m_viewModel.WindowElements, windowViewModel, add: true);
                        break;
                    default:
                        continue;
                }
                // Observable collection notifications can synchronously clear
                // or dispose this owner. Do not resume the obsolete add pass.
                if (!IsUpdateCurrent(epoch)) { return; }
            }

            foreach (var persistedNode in persistList.Concat(addList))
            {
                var vm = GetViewModel(persistedNode);
                switch (vm)
                {
                    case TilingPanelViewModel panelViewModel:
                        UpdateViewModel(panelViewModel, (PanelNode)persistedNode, focusedNodes, focusedNode);
                        break;
                    case TilingWindowViewModel windowViewModel:
                        UpdateViewModel(windowViewModel, (WindowNode)persistedNode, focusedNodes);
                        break;
                    default:
                        continue;
                }
                if (!IsUpdateCurrent(epoch)) { return; }
            }
        }

        private int m_collectionChangeDepth;
        private bool m_deferredViewInvalidation;

        private void ChangeViewCollection<T>(ObservableCollection<T> collection, T model, bool add)
        {
            m_collectionChangeDepth++;
            Exception? failure = null;
            try
            {
                if (add) { collection.Add(model); }
                else { collection.Remove(model); }
            }
            catch (Exception error)
            {
                failure = error;
                throw;
            }
            finally
            {
                m_collectionChangeDepth--;
                if (failure == null) { CompleteDeferredViewInvalidation(); }
                else
                {
                    ReleaseAfterFailure(
                        CompleteDeferredViewInvalidation,
                        failure,
                        "TilingOverlayRenderer.UpdateViewModelsExceptions");
                }
            }
        }

        private void CompleteDeferredViewInvalidation()
        {
            if (m_collectionChangeDepth != 0 || !m_deferredViewInvalidation) { return; }
            m_deferredViewInvalidation = false;
            try { InvalidateViewCore(); }
            catch
            {
                // A listener may reject Reset after mutation. Retry the complete
                // reset before accepting another snapshot, including empty ones.
                m_viewRecoveryPending = true;
                throw;
            }
        }

        private bool IsUpdateCurrent(int epoch) =>
            !m_deferredViewInvalidation &&
            Volatile.Read(ref m_disposeState) == 0 &&
            Volatile.Read(ref m_invalidationState) == 0 &&
            Volatile.Read(ref m_invalidationEpoch) == epoch;

        private TilingNodeViewModel? GetViewModel(TilingNode node)
        {
            if (m_nodeViewModels.TryGetValue(node, out var vm))
            {
                return vm;
            }
            return null;
        }

        private TilingNodeViewModel? CreateViewModel(
            TilingNode node,
            IReadOnlySet<TilingNode> focusedPath,
            TilingNode? focusedNode,
            int epoch)
        {
            TilingNodeViewModel viewModel;
            switch (node)
            {
                case PanelNode panelNode:
                    var panelViewModel = new TilingPanelViewModel();
                    viewModel = panelViewModel;
                    try
                    {
                        panelViewModel.HorizontalSplitActionPressed += WindowViewModel_HorizontalSplitActionPressed;
                        panelViewModel.VerticalSplitActionPressed += WindowViewModel_VerticalSplitActionPressed;
                        panelViewModel.PullUpActionPressed += WindowViewModel_PullUpActionPressed;
                        panelViewModel.StackActionPressed += WindowViewModel_StackActionPressed;
                        UpdateViewModel(panelViewModel, panelNode, focusedPath, focusedNode);
                    }
                    catch (Exception error)
                    {
                        ReleaseAfterFailure(
                            panelViewModel.Dispose,
                            error,
                            "TilingOverlayRenderer.CreateViewModelExceptions");
                        throw;
                    }
                    break;
                case WindowNode windowNode:
                    var windowViewModel = new TilingWindowViewModel();
                    viewModel = windowViewModel;
                    try
                    {
                        windowViewModel.BeginHorizontalSplitWith += WindowViewModel_BeginHorizontalSplitWith;
                        windowViewModel.BeginVerticalSplitWith += WindowViewModel_BeginVerticalSplitWith;
                        windowViewModel.BeginStackWith += WindowViewModel_BeginStackWith;
                        windowViewModel.FloatActionPressed += WindowViewModel_FloatActionPressed;
                        windowViewModel.HorizontalSplitActionPressed += WindowViewModel_HorizontalSplitActionPressed;
                        windowViewModel.VerticalSplitActionPressed += WindowViewModel_VerticalSplitActionPressed;
                        windowViewModel.PullUpActionPressed += WindowViewModel_PullUpActionPressed;
                        windowViewModel.StackActionPressed += WindowViewModel_StackActionPressed;
                        windowViewModel.IgnoreClassPressed += WindowViewModel_IgnoreClassPressed;
                        windowViewModel.IgnoreProcessPressed += WindowViewModel_IgnoreProcessPressed;
                        UpdateViewModel(windowViewModel, windowNode, focusedPath);
                        // PreviewWindows can retain the same set instance across
                        // recovery, so its setter will not revisit rebuilt models.
                        windowViewModel.IsPreviewVisible =
                            m_previewWindows.Contains(windowNode.WindowReference);
                    }
                    catch (Exception error)
                    {
                        ReleaseAfterFailure(
                            windowViewModel.Dispose,
                            error,
                            "TilingOverlayRenderer.CreateViewModelExceptions");
                        throw;
                    }
                    break;
                default:
                    return null;
            }

            // Initialization callbacks may synchronously invalidate or dispose
            // this renderer before the model becomes owned by the dictionary.
            if (!IsUpdateCurrent(epoch))
            {
                viewModel.Dispose();
                return null;
            }

            try
            {
                m_nodeViewModels.Add(node, viewModel);
            }
            catch (Exception error)
            {
                ReleaseAfterFailure(
                    viewModel.Dispose,
                    error,
                    "TilingOverlayRenderer.CreateViewModelExceptions");
                throw;
            }
            return viewModel;
        }

        private void WindowViewModel_BeginHorizontalSplitWith(object sender, RoutedEventArgs e)
        {
            BeginHorizontalWithRequested?.Invoke(this, (WindowNode)((TilingWindowViewModel)sender!).Node!);
        }

        private void WindowViewModel_BeginVerticalSplitWith(object sender, RoutedEventArgs e)
        {
            BeginVerticalWithRequested?.Invoke(this, (WindowNode)((TilingWindowViewModel)sender!).Node!);
        }

        private void WindowViewModel_BeginStackWith(object sender, RoutedEventArgs e)
        {
            BeginStackWithRequested?.Invoke(this, (WindowNode)((TilingWindowViewModel)sender!).Node!);
        }

        internal void Show()
        {
            m_overlay.Show();
        }

        void ITilingOverlayRenderer.Show() => Show();

        internal void Hide()
        {
            m_overlay.Hide();
        }

        void ITilingOverlayRenderer.Hide() => Hide();

        private void WindowViewModel_StackActionPressed(object sender, RoutedEventArgs e)
        {
            StackRequested?.Invoke(this, ((TilingNodeViewModel)sender!).Node!);
        }

        private void WindowViewModel_PullUpActionPressed(object sender, RoutedEventArgs e)
        {
            PullUpRequested?.Invoke(this, ((TilingNodeViewModel)sender!).Node!);
        }

        private void WindowViewModel_VerticalSplitActionPressed(object sender, RoutedEventArgs e)
        {
            VerticalSplitRequested?.Invoke(this, ((TilingNodeViewModel)sender!).Node!);
        }

        private void WindowViewModel_HorizontalSplitActionPressed(object sender, RoutedEventArgs e)
        {
            HorizontalSplitRequested?.Invoke(this, ((TilingNodeViewModel)sender!).Node!);
        }

        private void WindowViewModel_FloatActionPressed(object sender, RoutedEventArgs e)
        {
            FloatRequested?.Invoke(this, (WindowNode)((TilingWindowViewModel)sender!).Node!);
        }

        private void WindowViewModel_IgnoreProcessPressed(object sender, RoutedEventArgs e)
        {
            IgnoreProcessRequested?.Invoke(this, ((WindowNode)((TilingWindowViewModel)sender!).Node!));
        }

        private void WindowViewModel_IgnoreClassPressed(object sender, RoutedEventArgs e)
        {
            IgnoreClassRequested?.Invoke(this, ((WindowNode)((TilingWindowViewModel)sender!).Node!));
        }

        private void OnSetPreviewWindows(IReadOnlySet<IWindow> oldValue, IReadOnlySet<IWindow> newValue)
        {
            List<TilingWindowViewModel>? newlyVisible = null;
            foreach (var (node, vm) in m_nodeViewModels)
            {
                if (node is WindowNode window && vm is TilingWindowViewModel windowVm)
                {
                    bool isPreview = newValue.Contains(window.WindowReference);
                    if (isPreview)
                    {
                        if (!windowVm.IsPreviewVisible) { (newlyVisible ??= []).Add(windowVm); }
                    }
                    else if (oldValue.Contains(window.WindowReference))
                    {
                        windowVm.IsPreviewVisible = false;
                    }
                }
            }
            if (newlyVisible != null)
            {
                foreach (var vm in newlyVisible) { vm.IsPreviewVisible = true; }
            }
        }

        private void OnSetFocusRectangle(Rectangle? oldValue, Rectangle? newValue)
        {
            m_viewModel.FocusRectangle = newValue.HasValue ? AdjustForDisplay(newValue.Value) : new Rectangle();
        }

        private void OnSetPreviewRectangle(Rectangle? oldValue, Rectangle? newValue)
        {
            m_viewModel.PreviewRectangle = newValue.HasValue ? AdjustForDisplay(newValue.Value) : new Rectangle();
        }


        private void UpdateViewModel(TilingWindowViewModel vm, WindowNode node, IReadOnlySet<TilingNode> focusedPath)
        {
            vm.Overlay = m_viewModel;
            vm.Node = node;
            vm.Title = node.WindowReference.Title;
            vm.HasFocus = focusedPath.Contains(node);
            vm.ComputedBounds = AdjustForDisplay(node.ComputedRectangle);
            vm.PrimaryActionCommand = m_panelItemPrimaryActionCommand;
            vm.SecondaryActionCommand = m_panelItemSecondaryActionCommand;
            vm.CloseCommand = m_panelItemCloseActionCommand;
            vm.ActionsHeight = m_panelHeight + 4;
            vm.RevealHighlightRadius = (16 + m_panelHeight + m_windowPadding) * 2;
        }

        private void UpdateViewModel(TilingPanelViewModel vm, PanelNode node, IReadOnlySet<TilingNode> focusedPath, TilingNode? focusedNode)
        {
            vm.Overlay = m_viewModel;
            vm.Node = node;
            vm.HasFocus = focusedPath.Contains(node);
            vm.ChildHasDirectFocus = vm.ChildNodes.Select(x => x.Node).Contains(focusedNode);
            vm.ComputedBounds = AdjustForDisplay(node.ComputedRectangle);
            vm.PrimaryActionCommand = m_panelItemPrimaryActionCommand;
            vm.SecondaryActionCommand = m_panelItemSecondaryActionCommand;
            vm.CloseCommand = m_panelItemCloseActionCommand;

            vm.HeaderBounds = Rectangle.OffsetAndSize(
                (int)(vm.ComputedBounds.Left - node.Padding.Left + PanelSpacing / 2),
                (int)(vm.ComputedBounds.Top - node.Padding.Top + PanelSpacing / 2),
                (int)(vm.ComputedBounds.Width - PanelSpacing),
                (int)(PanelPadding.Top - PanelSpacing));

            vm.IsHeaderVisible = !IsObscured(node, focusedPath);

            if (HaveChildrenChanged(vm, node))
            {
                vm.ChildNodes.Clear();
                foreach (var child in node.Children)
                {
                    var childViewModel = GetViewModel(child);
                    if (childViewModel == null)
                    {
                        continue;
                    }
                    vm.ChildNodes.Add(childViewModel);
                }
            }
        }

        private bool HaveChildrenChanged(TilingPanelViewModel vm, PanelNode node)
        {
            if (vm.ChildNodes.Count != node.Children.Count)
            {
                return true;
            }
            int i = 0;
            foreach (var child in node.Children)
            {
                var childViewModel = GetViewModel(child);
                if (childViewModel == null)
                {
                    continue;
                }
                if (childViewModel != vm.ChildNodes[i])
                {
                    return true;
                }
                i++;
            }
            return false;
        }

        private static bool IsObscured(PanelNode node, IReadOnlySet<TilingNode> focusedPath)
        {
            if (focusedPath.Contains(node))
                return false;

            var stackAncestors = node.Ancestors
                .OfType<StackPanelNode>();

            if (!stackAncestors.Any())
                return false;

            return stackAncestors
                .All(x => focusedPath.Contains(x));
        }

        private void OnOverlayPanelItemClick(TilingNodeViewModel viewModel)
        {
            TilingNodeFocusRequested?.Invoke(this, viewModel.Node!);
        }

        private void OnOverlayPanelItemSecondaryAction(TilingNodeViewModel viewModel)
        {
            TilingNodePullUpRequested?.Invoke(this, viewModel.Node!);
        }

        private void OnOverlayPanelItemCloseAction(TilingNodeViewModel viewModel)
        {
            TilingNodeCloseRequested?.Invoke(this, viewModel.Node!);
        }

        private void OnDragStarted(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is TilingPanel view)
            {
                if (view.ViewModel != null)
                {
                    view.ViewModel.IsMoving = true;
                }
            }
        }

        private void OnDragCompleted(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is TilingPanel view)
            {
                if (view.ViewModel != null)
                {
                    view.ViewModel.IsMoving = false;
                    TilingPanelMoveRequested?.Invoke(this, (PanelNode)view.ViewModel.Node!);
                }
            }
        }

        private void OnDraggingEvent(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is TilingPanel view)
            {
                if (view.ViewModel != null)
                {
                    TilingPanelMoving?.Invoke(this, (PanelNode)view.ViewModel.Node!);
                }
            }
        }

        void ITilingOverlayRenderer.InvalidateViewForPadding()
        {
            if (!IsUpdateCurrent(Volatile.Read(ref m_invalidationEpoch))) { return; }
            if (m_viewUpdateDepth != 0 || m_viewRecoveryPending)
            {
                // An interrupted or failed pass may have published only part of
                // its snapshot. Retain complete cleanup and stale-pass cancellation.
                InvalidateView();
                return;
            }

            // The next layout updates bounds/header geometry on the existing
            // models. Preserve templates, subscriptions and preview transitions.
            Interlocked.Increment(ref m_invalidationEpoch);
        }

        public void InvalidateView()
        {
            if (Volatile.Read(ref m_disposeState) != 0)
            {
                return;
            }
            InvalidateViewCore();
        }

        private void InvalidateViewCore()
        {
            if (m_collectionChangeDepth != 0)
            {
                // ObservableCollection cannot be cleared while its add/remove
                // notification is reaching other listeners (including WPF).
                // Cancel immediately; finish cleanup synchronously once they
                // unwind, before the suspended mutation returns to the update.
                if (!m_deferredViewInvalidation)
                {
                    m_deferredViewInvalidation = true;
                    Interlocked.Increment(ref m_invalidationEpoch);
                }
                return;
            }
            CompleteInvalidation(
                ref m_invalidationState,
                () =>
                {
                    // Retain the cancellation signal after synchronous Clear
                    // callbacks return and invalidation admission is released.
                    Interlocked.Increment(ref m_invalidationEpoch);
                    var releases = new List<Action>(m_nodeViewModels.Count);
                    foreach (var model in m_nodeViewModels.Values)
                    {
                        releases.Add(model.Dispose);
                    }
                    return releases;
                },
                m_viewModel.PanelElements.Clear,
                m_viewModel.WindowElements.Clear,
                m_nodeViewModels.Clear,
                () => m_previousSnapshot = []);
        }

        internal static void CompleteInvalidation(
            ref int invalidationState,
            Func<IReadOnlyList<Action>> captureModelReleases,
            Action clearPanels,
            Action clearWindows,
            Action clearModels,
            Action clearSnapshot)
        {
            if (Interlocked.Exchange(ref invalidationState, 1) != 0)
            {
                return;
            }

            try
            {
                IReadOnlyList<Action> modelReleases = Array.Empty<Action>();
                ExceptionDispatchInfo? failure = null;
                List<Exception>? laterFailures = null;

                Release(() => modelReleases = captureModelReleases(), ref failure, ref laterFailures);
                Release(clearPanels, ref failure, ref laterFailures);
                Release(clearWindows, ref failure, ref laterFailures);
                foreach (var releaseModel in modelReleases)
                {
                    Release(releaseModel, ref failure, ref laterFailures);
                }
                Release(clearModels, ref failure, ref laterFailures);
                Release(clearSnapshot, ref failure, ref laterFailures);

                if (laterFailures != null)
                {
                    AttachLaterFailures(
                        failure!.SourceException,
                        laterFailures,
                        "TilingOverlayRenderer.InvalidateViewExceptions");
                }
                failure?.Throw();
            }
            finally
            {
                Volatile.Write(ref invalidationState, 0);
            }
        }

        internal static void CompleteDispose(
            ref int disposeState,
            Action captureContent,
            Action releaseDragHandlers,
            Action closeOverlay,
            Action disposeViewModel,
            Action releaseDisplay,
            Action disposeSubscriptions,
            Action invalidateView,
            Action clearEvents)
        {
            if (Interlocked.Exchange(ref disposeState, 1) != 0)
            {
                return;
            }

            ExceptionDispatchInfo? failure = null;
            List<Exception>? laterFailures = null;
            Release(captureContent, ref failure, ref laterFailures);
            Release(releaseDragHandlers, ref failure, ref laterFailures);
            Release(closeOverlay, ref failure, ref laterFailures);
            Release(disposeViewModel, ref failure, ref laterFailures);
            Release(releaseDisplay, ref failure, ref laterFailures);
            Release(disposeSubscriptions, ref failure, ref laterFailures);
            Release(invalidateView, ref failure, ref laterFailures);
            Release(clearEvents, ref failure, ref laterFailures);

            if (laterFailures != null)
            {
                AttachLaterFailures(
                    failure!.SourceException,
                    laterFailures,
                    "TilingOverlayRenderer.DisposeExceptions");
            }
            failure?.Throw();
        }

        private static void Release(
            Action action,
            ref ExceptionDispatchInfo? failure,
            ref List<Exception>? laterFailures)
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                if (failure == null)
                {
                    failure = ExceptionDispatchInfo.Capture(error);
                }
                else
                {
                    (laterFailures ??= []).Add(error);
                }
            }
        }

        private static void ReleaseAfterFailure(Action release, Exception primary, string key)
        {
            ExceptionDispatchInfo? failure = ExceptionDispatchInfo.Capture(primary);
            List<Exception>? laterFailures = null;
            Release(release, ref failure, ref laterFailures);
            if (laterFailures != null)
            {
                AttachLaterFailures(primary, laterFailures, key);
            }
        }

        private static void AttachLaterFailures(
            Exception primary,
            List<Exception> laterFailures,
            string key)
        {
            try
            {
                if (primary.Data[key] is AggregateException existing)
                {
                    laterFailures.InsertRange(0, existing.InnerExceptions);
                }
                primary.Data[key] = new AggregateException(laterFailures);
            }
            catch
            {
                // Supplemental disposal diagnostics must never replace the
                // first error from the ordered release sequence.
            }
        }

        internal static void ReleaseDragHandlers(
            UIElement content,
            RoutedEventHandler dragStarted,
            RoutedEventHandler dragCompleted,
            RoutedEventHandler dragging)
        {
            Draggable.RemoveDragStartedHandler(content, dragStarted);
            Draggable.RemoveDragCompletedHandler(content, dragCompleted);
            Draggable.RemoveDraggingdHandler(content, dragging);
        }

        internal void ClearEventHandlers()
        {
            TilingPanelMoveRequested = null;
            TilingPanelMoving = null;
            TilingNodeFocusRequested = null;
            TilingNodeCloseRequested = null;
            TilingNodePullUpRequested = null;

            HorizontalSplitRequested = null;
            VerticalSplitRequested = null;
            StackRequested = null;
            PullUpRequested = null;
            FloatRequested = null;
            IgnoreProcessRequested = null;
            IgnoreClassRequested = null;
            BeginHorizontalWithRequested = null;
            BeginVerticalWithRequested = null;
            BeginStackWithRequested = null;
        }

#pragma warning disable CA1816 // Dispose methods should call SuppressFinalize
        public void Dispose()
#pragma warning restore CA1816 // Dispose methods should call SuppressFinalize
        {
            UIElement? content = null;
            DisposeCore(
                () => content = m_overlay.Content,
                () =>
                {
                    if (content != null)
                    {
                        ReleaseDragHandlers(content, OnDragStarted, OnDragCompleted, OnDraggingEvent);
                    }
                },
                m_overlay.Close);
        }

        internal void DisposeCore(Action captureContent, Action releaseDragHandlers, Action closeOverlay)
        {
            CompleteDispose(
                ref m_disposeState,
                captureContent,
                releaseDragHandlers,
                closeOverlay,
                m_viewModel.Dispose,
                () => m_display.ScalingChanged -= OnDisplayScalingChanged,
                m_disposables.Dispose,
                InvalidateViewCore,
                ClearEventHandlers);
        }
    }
}
