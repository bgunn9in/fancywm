using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reactive.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

using FancyWM.DllImports;

using WinMan;

namespace FancyWM.Windows
{
    partial class OverlayHost
    {
        internal static void CompleteOverlayWindowClose(
            Action notifyClosed,
            Action disposeSettings,
            Action releaseWorkArea,
            Action releaseScaling,
            Action clearContent)
        {
            ExceptionDispatchInfo? failure = null;
            List<Exception>? laterFailures = null;
            void release(Action action)
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

            release(notifyClosed);
            release(disposeSettings);
            release(releaseWorkArea);
            release(releaseScaling);
            release(clearContent);

            if (laterFailures != null)
            {
                AttachLaterOverlayWindowCloseFailures(failure!.SourceException, laterFailures);
            }
            failure?.Throw();
        }

        private static void AttachLaterOverlayWindowCloseFailures(Exception primary, List<Exception> laterFailures)
        {
            try
            {
                const string key = "OverlayWindow.OnClosedExceptions";
                if (primary.Data[key] is AggregateException existing)
                {
                    laterFailures.InsertRange(0, existing.InnerExceptions);
                }
                primary.Data[key] = new AggregateException(laterFailures);
            }
            catch
            {
                // Supplemental close diagnostics must never replace the first
                // error from the ordered close sequence.
            }
        }

        internal static bool TryBeginOverlayWindowClose(ref int closeState)
        {
            return Interlocked.Exchange(ref closeState, 1) == 0;
        }

        internal static void QueueOverlayWindowCallback(
            Func<bool> isClosed,
            Action<Action> post,
            Action first,
            Action? second = null)
        {
            if (isClosed())
            {
                return;
            }
            post(() =>
            {
                if (isClosed())
                {
                    return;
                }
                first();
                if (!isClosed())
                {
                    second?.Invoke();
                }
            });
        }

        internal static void QueueOverlayWindowSettingsCallback(
            Func<bool> isClosed,
            int panelFontSize,
            Action<int> storePanelFontSize,
            Action<Action> post,
            Action updateResources)
        {
            if (isClosed())
            {
                return;
            }
            storePanelFontSize(panelFontSize);
            if (!isClosed())
            {
                QueueOverlayWindowCallback(isClosed, post, updateResources);
            }
        }

        internal static void EnsureOverlayWindowLocation(
            Func<bool> isClosed,
            Func<Rectangle?> getWindowRectangle,
            Func<Rectangle> getWorkArea,
            Action<Rectangle> setWindowPosition)
        {
            if (isClosed())
            {
                return;
            }
            var current = getWindowRectangle();
            if (isClosed())
            {
                return;
            }
            var desired = getWorkArea();
            if (isClosed())
            {
                return;
            }
            if (current != desired)
            {
                setWindowPosition(desired);
            }
        }

        internal static void UpdateOverlayWindowRenderTransform(
            Func<bool> isClosed,
            Action ensureLocation,
            Func<Rectangle> getBounds,
            Func<Rectangle> getWorkArea,
            Func<double> getScaling,
            Action<Transform> setRenderTransform)
        {
            if (isClosed())
            {
                return;
            }
            ensureLocation();
            if (isClosed())
            {
                return;
            }
            var bounds = getBounds();
            if (isClosed())
            {
                return;
            }
            var workArea = getWorkArea();
            if (isClosed())
            {
                return;
            }

            var tg = new TransformGroup();
            tg.Children.Add(new TranslateTransform(
                bounds.Left - workArea.Left,
                bounds.Top - workArea.Top));
            double scaleX = 1 / getScaling();
            if (isClosed())
            {
                return;
            }
            double scaleY = 1 / getScaling();
            if (isClosed())
            {
                return;
            }
            tg.Children.Add(new ScaleTransform(
                scaleX,
                scaleY));
            setRenderTransform(tg);
        }

        internal static void UpdateOverlayWindowResources(
            Func<bool> isClosed,
            Func<IDictionary> getResources,
            Func<int> getPanelFontSize,
            Func<double> getScaling)
        {
            if (isClosed())
            {
                return;
            }
            IDictionary resources = getResources();
            if (isClosed())
            {
                return;
            }
            double scaling = getScaling();
            if (isClosed())
            {
                return;
            }
            resources["DisplayScaling"] = scaling;
            if (isClosed())
            {
                return;
            }

            resources = getResources();
            if (isClosed())
            {
                return;
            }
            int panelFontSize = getPanelFontSize();
            if (isClosed())
            {
                return;
            }
            scaling = getScaling();
            if (isClosed())
            {
                return;
            }
            resources["OverlayFontSize"] = panelFontSize * scaling;
        }

        private class OverlayWindow : Window
        {
            private readonly IDisplay m_display;
            private readonly Border m_contentContainer;
            private readonly IDisposable m_subscription;
            private readonly IntPtr m_hwnd;
            private int m_panelFontSize;
            private int m_closeState;
            private Exception? m_constructionFailure;

            private bool IsClosed => Volatile.Read(ref m_closeState) != 0 || m_constructionFailure != null;

            public bool AllowClose { get; set; } = false;

            public OverlayWindow(IDisplay display)
            {
                m_display = display;
                try
                {
                    var bounds = display.Bounds;

                    WindowStyle = WindowStyle.None;
                    ResizeMode = ResizeMode.NoResize;
                    Topmost = true;
                    ShowInTaskbar = false;
                    AllowsTransparency = true;
                    Background = null;
                    SnapsToDevicePixels = true;

                    m_hwnd = new WindowInteropHelper(this).EnsureHandle();
                    m_contentContainer = new Border();
                    Content = m_contentContainer;
                    UpdateRenderTransform();

                    m_display.WorkAreaChanged += OnDisplayWorkAreaChanged;
                    m_display.ScalingChanged += OnDisplayScalingChanged;

                    m_subscription = App.Current.AppState.Settings
                        .Select(x => x.PanelFontSize)
                        .DistinctUntilChanged()
                        .Subscribe(OnPanelFontSizeChanged);
                }
                catch (Exception error)
                {
                    AbortConstruction(error);
                    throw;
                }
            }

            internal void PrepareConstructionRollback(Exception primary)
            {
                m_constructionFailure = primary;
                AllowClose = true;
            }

            internal void AbortConstruction(Exception primary)
            {
                PrepareConstructionRollback(primary);
                try { Close(); }
                catch (Exception error) { AttachLaterOverlayWindowCloseFailures(primary, [error]); }
            }

            private void OnPanelFontSizeChanged(int panelFontSize)
            {
                QueueOverlayWindowSettingsCallback(
                    () => IsClosed,
                    panelFontSize,
                    value => m_panelFontSize = value,
                    callback => { _ = Dispatcher.InvokeAsync(callback); },
                    UpdateResources);
            }

            private void UpdateResources()
            {
                UpdateOverlayWindowResources(
                    () => IsClosed,
                    () => Resources,
                    () => m_panelFontSize,
                    () => m_display.Scaling);
            }

            protected override void OnClosing(CancelEventArgs e)
            {
                if (!AllowClose)
                {
                    e.Cancel = true;
                }
            }

            internal void SetContent(Control content)
            {
                m_contentContainer.Child = content;
            }

            private Rectangle? GetWindowRectangle()
            {
                if (PInvoke.GetWindowRect(new(m_hwnd), out RECT current))
                {
                    return new Rectangle(current.left, current.top, current.right, current.bottom);
                }
                return default;
            }

            private void EnsureLocation()
            {
                EnsureOverlayWindowLocation(
                    () => IsClosed,
                    GetWindowRectangle,
                    () => m_display.WorkArea,
                    desired => PInvoke.SetWindowPos(
                        new(m_hwnd),
                        new(),
                        desired.Left,
                        desired.Top,
                        desired.Width,
                        desired.Height,
                        SetWindowPos_uFlags.SWP_NOZORDER | SetWindowPos_uFlags.SWP_NOACTIVATE));
            }

            private void UpdateRenderTransform()
            {
                UpdateOverlayWindowRenderTransform(
                    () => IsClosed,
                    EnsureLocation,
                    () => m_display.Bounds,
                    () => m_display.WorkArea,
                    () => m_display.Scaling,
                    transform => m_contentContainer.RenderTransform = transform);
            }

            private void OnDisplayScalingChanged(object? sender, DisplayScalingChangedEventArgs e)
            {
                QueueOverlayWindowCallback(
                    () => IsClosed,
                    callback => Dispatcher.BeginInvoke(callback),
                    UpdateRenderTransform,
                    UpdateResources);
            }

            private void OnDisplayWorkAreaChanged(object? sender, DisplayRectangleChangedEventArgs e)
            {
                QueueOverlayWindowCallback(
                    () => IsClosed,
                    callback => Dispatcher.BeginInvoke(callback),
                    UpdateRenderTransform);
            }

            protected override void OnLocationChanged(EventArgs e)
            {
                base.OnLocationChanged(e);
                EnsureLocation();
            }

            protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
            {
                base.OnRenderSizeChanged(sizeInfo);
                EnsureLocation();
            }

            protected override void OnClosed(EventArgs e)
            {
                if (!TryBeginOverlayWindowClose(ref m_closeState))
                {
                    return;
                }
                try
                {
                    CompleteOverlayWindowClose(
                        () => base.OnClosed(e),
                        () => m_subscription?.Dispose(),
                        () => m_display.WorkAreaChanged -= OnDisplayWorkAreaChanged,
                        () => m_display.ScalingChanged -= OnDisplayScalingChanged,
                        () => Content = null);
                }
                catch (Exception error) when (m_constructionFailure != null)
                {
                    // WM_DESTROY delivers OnClosed failures through the dispatcher.
                    // During rollback they belong to the still-escaping constructor
                    // error, rather than becoming a second unhandled exception.
                    AttachLaterOverlayWindowCloseFailures(m_constructionFailure, [error]);
                }
            }
        }
    }
}
