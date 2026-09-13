using System.Windows.Input;
using System.Windows;
using System.Windows.Media;
using FancyWM.Utilities;

using WinMan;
using FancyWM.Layouts.Tiling;
using System.Windows.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace FancyWM.ViewModels
{
    public abstract class TilingNodeViewModel : ViewModelBase
    {
        private TilingOverlayViewModel? m_overlay;
        private TilingNode? m_node;
        private bool m_hasFocus;
        private Rectangle m_windowBounds;
        private ICommand? m_primaryActionCommand;
        private ICommand? m_secondaryActionCommand;
        private ICommand? m_closeActionCommand;
        private readonly WindowExtensions.IconCache? m_iconCache;
        private IWindow? m_iconWindow;
        private nint m_iconHandle;
        private BitmapSource? m_iconImage;
        private long m_iconExpiresAt;
        private CancellationTokenSource? m_iconRequest;
        private long m_iconVersion;
        private long m_nodeVersion;
        private bool m_iconRemoved;
        private int m_disposeState;

        protected bool IsDisposed => Volatile.Read(ref m_disposeState) != 0;

        public event RoutedEventHandler? HorizontalSplitActionPressed;
        public event RoutedEventHandler? VerticalSplitActionPressed;
        public event RoutedEventHandler? StackActionPressed;
        public event RoutedEventHandler? PullUpActionPressed;

        public ICommand HorizontalSplitCommand { get; }
        public ICommand VerticalSplitCommand { get; }
        public ICommand StackCommand { get; }
        public ICommand PullUpCommand { get; }

        public ImageSource? Icon => GetCachedImageSource();

        public Visibility IconVisibility => Icon != null ? Visibility.Visible : Visibility.Collapsed;

        protected TilingNodeViewModel() : this(null)
        {
        }

        private protected TilingNodeViewModel(WindowExtensions.IconCache? iconCache)
        {
            m_iconCache = iconCache;
            HorizontalSplitCommand = new DelegateCommand(_ => HorizontalSplitActionPressed?.Invoke(this, new RoutedEventArgs()));
            VerticalSplitCommand = new DelegateCommand(_ => VerticalSplitActionPressed?.Invoke(this, new RoutedEventArgs()));
            StackCommand = new DelegateCommand(_ => StackActionPressed?.Invoke(this, new RoutedEventArgs()));
            PullUpCommand = new DelegateCommand(_ => PullUpActionPressed?.Invoke(this, new RoutedEventArgs()));
        }

        public TilingOverlayViewModel? Overlay { get => m_overlay; set => SetField(ref m_overlay, value); }
        public TilingNode? Node
        {
            get => m_node;
            set
            {
                if (IsDisposed) { return; }
                if (ReferenceEquals(m_node, value)) { return; }

                long nodeVersion = Interlocked.Increment(ref m_nodeVersion);
                var expectedNode = m_node;
                ExceptionDispatchInfo? failure = null;
                List<Exception>? laterFailures = null;
                IWindow? previousWindow = null;
                IWindow? candidateWindow = null;
                bool previousReleaseCompleted = false;
                bool candidateAddEntered = false;
                try
                {
                    ResetIcon();
                    if (StopOrRejectObsoleteNodeAssignment(expectedNode, nodeVersion)) { return; }

                    previousWindow = m_iconWindow;
                    m_iconWindow = null;
                    if (previousWindow != null)
                    {
                        previousWindow.Removed -= OnIconWindowRemoved;
                        previousReleaseCompleted = true;
                    }
                    if (StopOrRejectObsoleteNodeAssignment(expectedNode, nodeVersion)) { return; }

                    m_node = value;
                    expectedNode = value;
                    candidateWindow = (value as WindowNode)?.WindowReference;
                    m_iconWindow = candidateWindow;
                    m_iconRemoved = false;
                    if (candidateWindow != null)
                    {
                        m_iconHandle = candidateWindow.Handle;
                        if (StopOrRejectObsoleteNodeAssignment(expectedNode, nodeVersion)) { return; }

                        candidateAddEntered = true;
                        candidateWindow.Removed += OnIconWindowRemoved;
                        if (IsDisposed)
                        {
                            candidateAddEntered = false;
                            TryReleaseIconSubscription(candidateWindow, ref failure, ref laterFailures);
                            ThrowCleanupFailure(failure, laterFailures);
                            return;
                        }
                        RejectObsoleteNodeAssignment(expectedNode, nodeVersion);
                        candidateAddEntered = false;
                    }
                    NotifyPropertyChanged(nameof(Node));
                    if (IsDisposed || !IsNodeAssignmentCurrent(expectedNode, nodeVersion)) { return; }
                    NotifyIconChanged();
                    return;
                }
                catch (Exception error)
                {
                    failure ??= ExceptionDispatchInfo.Capture(error);
                }

                bool compensateCandidate = candidateAddEntered &&
                    (IsDisposed || !IsNodeAssignmentCurrent(expectedNode, nodeVersion) ||
                        !ReferenceEquals(m_iconWindow, candidateWindow));
                TryCleanup(Dispose, ref failure, ref laterFailures);
                if (previousWindow != null && !previousReleaseCompleted)
                {
                    TryReleaseIconSubscription(previousWindow, ref failure, ref laterFailures);
                }
                if (compensateCandidate && candidateWindow != null)
                {
                    TryReleaseIconSubscription(candidateWindow, ref failure, ref laterFailures);
                }
                ThrowCleanupFailure(failure, laterFailures);
            }
        }
        public bool HasFocus { get => m_hasFocus; set => SetField(ref m_hasFocus, value); }
        public Rectangle ComputedBounds { get => m_windowBounds; set => SetField(ref m_windowBounds, value); }

        public ICommand? PrimaryActionCommand { get => m_primaryActionCommand; set => SetField(ref m_primaryActionCommand, value); }

        public ICommand? SecondaryActionCommand { get => m_secondaryActionCommand; set => SetField(ref m_secondaryActionCommand, value); }

        public ICommand? CloseCommand { get => m_closeActionCommand; set => SetField(ref m_closeActionCommand, value); }

#pragma warning disable CA1816 // Dispose methods should call SuppressFinalize
        public override void Dispose()
#pragma warning restore CA1816 // Dispose methods should call SuppressFinalize
        {
            if (Interlocked.Exchange(ref m_disposeState, 1) != 0) { return; }

            var iconWindow = m_iconWindow;
            m_iconWindow = null;
            m_iconRemoved = true;

            ExceptionDispatchInfo? failure = null;
            List<Exception>? laterFailures = null;
            TryCleanup(ResetIcon, ref failure, ref laterFailures);
            if (iconWindow != null)
            {
                TryReleaseIconSubscription(iconWindow, ref failure, ref laterFailures);
            }
            TryCleanup(base.Dispose, ref failure, ref laterFailures);

            HorizontalSplitActionPressed = null;
            VerticalSplitActionPressed = null;
            StackActionPressed = null;
            PullUpActionPressed = null;
            m_primaryActionCommand = null;
            m_secondaryActionCommand = null;
            m_closeActionCommand = null;

            ThrowCleanupFailure(failure, laterFailures);
        }

        protected void ClearNodeReference()
        {
            m_node = null;
        }

        private bool StopOrRejectObsoleteNodeAssignment(TilingNode? expectedNode, long nodeVersion)
        {
            if (IsDisposed) { return true; }
            RejectObsoleteNodeAssignment(expectedNode, nodeVersion);
            return false;
        }

        private bool IsNodeAssignmentCurrent(TilingNode? expectedNode, long nodeVersion)
        {
            return Volatile.Read(ref m_nodeVersion) == nodeVersion && ReferenceEquals(m_node, expectedNode);
        }

        private void RejectObsoleteNodeAssignment(TilingNode? expectedNode, long nodeVersion)
        {
            if (!IsNodeAssignmentCurrent(expectedNode, nodeVersion))
            {
                throw new InvalidOperationException(
                    "The node binding changed while its icon subscription was being acquired.");
            }
        }

        private void TryReleaseIconSubscription(
            IWindow iconWindow,
            ref ExceptionDispatchInfo? failure,
            ref List<Exception>? laterFailures)
        {
            try
            {
                iconWindow.Removed -= OnIconWindowRemoved;
            }
            catch (Exception error)
            {
                if (failure == null) { failure = ExceptionDispatchInfo.Capture(error); }
                else { (laterFailures ??= []).Add(error); }
            }
        }

        protected static void TryCleanup(Action cleanup, ref ExceptionDispatchInfo? failure, ref List<Exception>? laterFailures)
        {
            try
            {
                cleanup();
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

        protected static void ThrowCleanupFailure(ExceptionDispatchInfo? failure, List<Exception>? laterFailures)
        {
            if (failure == null) { return; }
            if (laterFailures != null)
            {
                AttachLaterCleanupFailures(failure.SourceException, laterFailures);
            }
            failure.Throw();
        }

        private static void AttachLaterCleanupFailures(Exception primary, List<Exception> laterFailures)
        {
            try
            {
                const string key = "TilingNodeViewModel.CleanupExceptions";
                if (primary.Data[key] is AggregateException existing)
                {
                    laterFailures.InsertRange(0, existing.InnerExceptions);
                }
                primary.Data[key] = new AggregateException(laterFailures);
            }
            catch
            {
                // Supplemental cleanup diagnostics must never replace the first
                // error from the ordered release sequence.
            }
        }

        private BitmapSource? GetCachedImageSource()
        {
            if (IsDisposed || m_iconRemoved || m_iconWindow is not IWindow window)
            {
                return null;
            }
            if (window.Handle != m_iconHandle)
            {
                ResetIcon();
                m_iconHandle = window.Handle;
            }
            var cache = m_iconCache ?? WindowExtensions.Icons;
            if (cache != null && m_iconRequest == null && cache.Clock.GetTimestamp() >= m_iconExpiresAt)
            {
                var request = new CancellationTokenSource();
                m_iconRequest = request;
                _ = ObserveIconAsync(new WeakReference<TilingNodeViewModel>(this), Dispatcher,
                    cache, cache.GetAsync(window, request.Token), m_iconVersion, request);
            }
            return m_iconImage;
        }

        private static async Task ObserveIconAsync(WeakReference<TilingNodeViewModel> reference,
            Dispatcher dispatcher, WindowExtensions.IconCache cache, Task<WindowExtensions.IconResult> operation, long version,
            CancellationTokenSource request)
        {
            var cancellationToken = request.Token;
            try
            {
                WindowExtensions.IconResult result;
                try
                {
                    result = await operation.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    result = new(null, long.MaxValue);
                }
                catch (ObjectDisposedException)
                {
                    result = new(null, long.MaxValue);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    result = cache.Missing();
                }
                cancellationToken.ThrowIfCancellationRequested();
                await dispatcher.InvokeAsync(() =>
                {
                    if (reference.TryGetTarget(out var model) && !model.IsDisposed &&
                        !model.m_iconRemoved && model.m_iconVersion == version &&
                        ReferenceEquals(model.m_iconRequest, request) &&
                        model.m_iconWindow?.Handle == model.m_iconHandle)
                    {
                        model.m_iconRequest = null;
                        model.m_iconImage = result.Image;
                        model.m_iconExpiresAt = result.RetryAfter;
                        model.NotifyIconChanged();
                    }
                }, DispatcherPriority.Background, cancellationToken).Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException) when (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) { }
            finally
            {
                request.Dispose();
            }
        }

        private void ResetIcon()
        {
            Interlocked.Increment(ref m_iconVersion);
            var request = m_iconRequest;
            m_iconRequest = null;
            m_iconImage = null;
            m_iconExpiresAt = default;
            if (request != null)
            {
                try { request.Cancel(); }
                catch (ObjectDisposedException) { }
                finally { request.Dispose(); }
            }
        }

        private void OnIconWindowRemoved(object? sender, WindowChangedEventArgs e)
        {
            if (Dispatcher.CheckAccess())
            {
                if (IsDisposed || !ReferenceEquals(sender, m_iconWindow)) { return; }
                m_iconRemoved = true;
                ResetIcon();
                NotifyIconChanged();
            }
            else if (sender is IWindow window)
            {
                var reference = new WeakReference<TilingNodeViewModel>(this);
                var removed = new WeakReference<IWindow>(window);
                _ = Dispatcher.InvokeAsync(() =>
                {
                    if (reference.TryGetTarget(out var model) && !model.IsDisposed &&
                        removed.TryGetTarget(out var source) && ReferenceEquals(model.m_iconWindow, source))
                    {
                        model.m_iconRemoved = true;
                        model.ResetIcon();
                        model.NotifyIconChanged();
                    }
                });
            }
        }

        private void NotifyIconChanged()
        {
            NotifyPropertyChanged(nameof(Icon));
            NotifyPropertyChanged(nameof(IconVisibility));
        }
    }

}
