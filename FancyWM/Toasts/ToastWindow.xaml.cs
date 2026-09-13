using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Windows;

using FancyWM.ViewModels;

using WinMan;

namespace FancyWM.Toasts
{
    /// <summary>
    /// Interaction logic for ToastWindow.xaml
    /// </summary>
    public partial class ToastWindow : Window, IDisposable
    {
        public class ToastItem(object content, Action? extraAction = null) : ViewModelBase
        {
            public object Content { get; } = content ?? throw new ArgumentNullException(nameof(content));

            public Action? ExtraAction { get; } = extraAction;
        }

        public ObservableCollection<ToastItem> ToastItems { get; set; } = [];

        private readonly IWorkspace m_workspace;
        private readonly IToastWindowPlatform m_platform;
        private IDisplay m_display;
        private CancellationTokenRegistration m_cancellationRegistration;
        private ToastItem? m_currentItem;
        private long m_generation;
        private bool m_primarySubscribed;
        private ScalingSubscription? m_scalingSubscription;
        private bool m_disposed;

        internal bool IsDisposed => m_disposed;

        public ToastWindow(IWorkspace workspace)
            : this(workspace, new NativeToastWindowPlatform())
        {
        }

        internal ToastWindow(IWorkspace workspace, IToastWindowPlatform platform)
        {
            m_workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
            m_platform = platform ?? throw new ArgumentNullException(nameof(platform));

            InitializeComponent();
            DataContext = this;

            m_display = m_workspace.DisplayManager.PrimaryDisplay;
            try
            {
                m_platform.Initialize(this);
                ObjectDisposedException.ThrowIf(m_disposed, this);
                UpdatePosition(m_display);
                ObjectDisposedException.ThrowIf(m_disposed, this);
                SubscribeToPrimaryDisplayChanges();
                EnsureScalingSubscription();
            }
            catch
            {
                try { Dispose(); }
                catch { }
                throw;
            }
        }

        private void SubscribeToPrimaryDisplayChanges()
        {
            m_primarySubscribed = true;
            try
            {
                m_workspace.DisplayManager.PrimaryDisplayChanged += OnPrimaryDisplayChanged;
            }
            catch
            {
                if (m_disposed) { CompensatePrimarySubscriptionAfterDispose(); }
                throw;
            }
            if (!m_disposed) { return; }
            CompensatePrimarySubscriptionAfterDispose();
            throw new ObjectDisposedException(nameof(ToastWindow));
        }

        private void EnsureScalingSubscription()
        {
            if (ReferenceEquals(m_scalingSubscription?.Display, m_display)) { return; }
            ReleaseScalingSubscription();
            SubscribeToScalingChanges(m_display);
        }

        private void SubscribeToScalingChanges(IDisplay owner)
        {
            var subscription = new ScalingSubscription(this, owner);
            m_scalingSubscription = subscription;
            try
            {
                owner.ScalingChanged += subscription.Handler;
            }
            catch
            {
                if (m_disposed || !ReferenceEquals(m_scalingSubscription, subscription))
                {
                    CompensateScalingSubscription(subscription);
                }
                throw;
            }
            if (ReferenceEquals(m_scalingSubscription, subscription) && !m_disposed) { return; }
            if (m_disposed)
            {
                CompensateScalingSubscription(subscription);
                throw new ObjectDisposedException(nameof(ToastWindow));
            }
            owner.ScalingChanged -= subscription.Handler;
        }

        private void CompensatePrimarySubscriptionAfterDispose()
        {
            m_primarySubscribed = true;
            try { ReleasePrimarySubscription(); }
            catch { }
        }

        private static void CompensateScalingSubscription(ScalingSubscription subscription)
        {
            try { subscription.Display.ScalingChanged -= subscription.Handler; }
            catch { }
        }

        private void OnPrimaryDisplayChanged(object? sender, PrimaryDisplayChangedEventArgs e)
        {
            Dispatch(() =>
            {
                if (m_disposed) { return; }
                if (!ReferenceEquals(m_display, e.NewPrimaryDisplay))
                {
                    ReleaseScalingSubscription();
                    if (m_disposed) { return; }
                    m_display = e.NewPrimaryDisplay;
                    EnsureScalingSubscription();
                    if (m_disposed) { return; }
                }
                UpdatePosition(m_display);
            });
        }

        private void OnDisplayScalingChanged(object? sender, DisplayScalingChangedEventArgs e)
        {
            Dispatch(() =>
            {
                if (m_disposed || !ReferenceEquals(e.Source, m_display)) { return; }
                UpdatePosition(m_display);
            });
        }

        private void Dispatch(Action action)
        {
            if (Dispatcher.CheckAccess()) { action(); }
            else if (!Dispatcher.HasShutdownStarted) { _ = Dispatcher.InvokeAsync(action); }
        }

        private void UpdatePosition(IDisplay display)
        {
            m_platform.SetPosition(this, display);
        }

        internal void ShowToast(object content, CancellationToken token)
        {
            ShowToast(content, null, token);
        }

        internal void ShowToast(object content, Action? extraAction, CancellationToken token)
        {
            Dispatcher.VerifyAccess();
            if (m_disposed || token.IsCancellationRequested) { return; }
            var item = new ToastItem(content, extraAction);
            long generation = ++m_generation;
            var previousRegistration = m_cancellationRegistration;
            m_cancellationRegistration = default;
            previousRegistration.Dispose();
            if (!IsCurrent(generation)) { return; }
            m_currentItem = item;
            var registration = token.Register(() =>
            {
                // Never block a cancelling worker while the UI disposes its registration.
                if (!Dispatcher.HasShutdownStarted) { _ = Dispatcher.InvokeAsync(() => RemoveItem(item, generation)); }
            });
            if (!IsCurrent(generation))
            {
                registration.Dispose();
                return;
            }
            // Own the registration before collection/visibility callbacks can replace or close us.
            m_cancellationRegistration = registration;
            ToastItems.Clear();
            if (!IsCurrent(generation)) { return; }
            ToastItems.Add(item);
            if (!IsCurrent(generation)) { return; }
            UpdateVisibility(true);
        }

        private bool IsCurrent(long generation) => !m_disposed && generation == m_generation;

        private void RemoveItem(ToastItem item, long generation)
        {
            if (!IsCurrent(generation) || !ReferenceEquals(m_currentItem, item)) { return; }
            long removalGeneration = ++m_generation;
            m_currentItem = null;
            var registration = m_cancellationRegistration;
            m_cancellationRegistration = default;
            registration.Dispose();
            if (!IsCurrent(removalGeneration)) { return; }
            if (!ToastItems.Remove(item) && IsCurrent(removalGeneration))
            {
                // Cancellation can run through a nested dispatcher frame before Add.
                ToastItems.Clear();
            }
            if (IsCurrent(removalGeneration) && ToastItems.Count == 0)
            {
                UpdateVisibility(false);
            }
        }

        private void UpdateVisibility(bool show)
        {
            m_platform.SetVisible(this, show);
        }

        private void ReleaseResources()
        {
            if (m_disposed) { return; }
            m_disposed = true;
            m_generation++;
            m_currentItem = null;
            try
            {
                ReleasePrimarySubscription();
            }
            finally
            {
                try
                {
                    ReleaseScalingSubscription();
                }
                finally
                {
                    var registration = m_cancellationRegistration;
                    m_cancellationRegistration = default;
                    try { registration.Dispose(); }
                    finally { ToastItems.Clear(); }
                }
            }
        }

        private void ReleasePrimarySubscription()
        {
            if (!m_primarySubscribed) { return; }
            m_primarySubscribed = false;
            m_workspace.DisplayManager.PrimaryDisplayChanged -= OnPrimaryDisplayChanged;
        }

        private void ReleaseScalingSubscription()
        {
            var subscription = m_scalingSubscription;
            if (subscription == null) { return; }
            m_scalingSubscription = null;
            subscription.Display.ScalingChanged -= subscription.Handler;
        }

        private sealed class ScalingSubscription
        {
            private readonly ToastWindow m_window;

            internal IDisplay Display { get; }
            internal EventHandler<DisplayScalingChangedEventArgs> Handler { get; }

            internal ScalingSubscription(ToastWindow window, IDisplay display)
            {
                m_window = window;
                Display = display;
                Handler = OnScalingChanged;
            }

            private void OnScalingChanged(object? sender, DisplayScalingChangedEventArgs e) =>
                m_window.OnDisplayScalingChanged(sender, e);
        }

        protected override void OnClosed(EventArgs e)
        {
            try { ReleaseResources(); }
            finally { base.OnClosed(e); }
        }

        public void Dispose()
        {
            Dispatcher.VerifyAccess();
            if (m_disposed) { return; }
            try { ReleaseResources(); }
            finally
            {
                try { m_platform.Close(this); }
                finally { GC.SuppressFinalize(this); }
            }
        }
    }
}
