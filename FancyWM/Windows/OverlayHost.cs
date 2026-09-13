using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

using FancyWM.DllImports;
using FancyWM.Utilities;

using Microsoft.Extensions.DependencyInjection;

using WinMan;

namespace FancyWM.Windows
{
    /// <summary>
    /// This is basically a workaround for showing overlay content that is ignored by the operating system.
    /// It uses two identially-positioned windows, one with WS_EX_TRANSPARENT and one without.
    /// </summary>
    partial class OverlayHost : DependencyObject
    {

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, GetWindowLongPtr_nIndex nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
        private static extern IntPtr SetWindowLong32(IntPtr hWnd, GetWindowLongPtr_nIndex nIndex, IntPtr dwNewLong);

        private static IntPtr SetWindowLongPtr(IntPtr hWnd, GetWindowLongPtr_nIndex nIndex, IntPtr dwNewLong)
        {
            if (IntPtr.Size == 8)
            {
                return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
            }
            else
            {
                return SetWindowLong32(hWnd, nIndex, dwNewLong);
            }
        }

        [Flags()]
        internal enum SetWindowPosFlags : uint
        {
            IgnoreResize = 0x0001,
            IgnoreMove = 0x0002,
            DoNotActivate = 0x0010,
            DoNotSendChangingEvent = 0x0400,
            AsynchronousWindowPosition = 0x4000,
        }

        public static readonly DependencyProperty NonHitTestableContentProperty = DependencyProperty.Register(
            nameof(NonHitTestableContent),
            typeof(Control),
            typeof(OverlayHost),
            new PropertyMetadata(null));

        public Control? NonHitTestableContent
        {
            get => (Control)GetValue(NonHitTestableContentProperty);
            set => SetValue(NonHitTestableContentProperty, value);
        }

        public static readonly DependencyProperty ContentProperty = DependencyProperty.Register(
            nameof(Content),
            typeof(Control),
            typeof(OverlayHost),
            new PropertyMetadata(null));

        public Control? Content
        {
            get => (Control)GetValue(ContentProperty);
            set => SetValue(ContentProperty, value);
        }

        public Func<IntPtr>? AnchorSource { get; set; }

        private readonly IDisplay m_display;
        private readonly OverlayWindow m_window;
        private readonly OverlayWindow m_nonHitTestableWindow;
        private readonly IntPtr m_hwnd;
        private readonly IntPtr m_nonHitTestableHwnd;
        private readonly LowLevelMouseHook? m_mshk;
        private bool m_isShown;
        private bool m_closed;
        private bool m_showing;
        private long m_cursorGeneration;
        private long m_activeCursorGeneration;
        private readonly OverlayRefreshLoop m_refreshLoop;
        private readonly (Action Invalidate, IDisposable Lifetime) m_cursorInput;

        public OverlayHost(IDisplay display)
        {
            m_display = display;
            m_refreshLoop = new OverlayRefreshLoop(TryUpdatePositions, OverlayRefreshLoop.SubscribeTimer);
            int subscriptionAttempts = 0;
            try
            {

                m_window = new OverlayWindow(display)
                {
                    Title = "FancyWMInteractiveOverlay",
                };
                m_nonHitTestableWindow = new OverlayWindow(display)
                {
                    IsHitTestVisible = false,
                    Title = "FancyWMNonInteractiveOverlay",
                };

                m_hwnd = new WindowInteropHelper(m_window).EnsureHandle();
                m_nonHitTestableHwnd = new WindowInteropHelper(m_nonHitTestableWindow).EnsureHandle();

                // Set an owner relationship so that m_hwnd is always rendered on top of m_nonHitTestableHwnd.
                _ = SetWindowLongPtr(new(m_hwnd), GetWindowLongPtr_nIndex.GWLP_HWNDPARENT, m_nonHitTestableHwnd);

                _ = SetWindowLongPtr(new(m_hwnd), GetWindowLongPtr_nIndex.GWL_EXSTYLE,
                    (int)(WINDOWS_EX_STYLE.WS_EX_TOOLWINDOW | WINDOWS_EX_STYLE.WS_EX_NOACTIVATE));
                _ = SetWindowLongPtr(new(m_nonHitTestableHwnd), GetWindowLongPtr_nIndex.GWL_EXSTYLE,
                    (int)(WINDOWS_EX_STYLE.WS_EX_TOOLWINDOW | WINDOWS_EX_STYLE.WS_EX_TRANSPARENT | WINDOWS_EX_STYLE.WS_EX_NOACTIVATE));

                DisableWindow(m_hwnd);
                DisableWindow(m_nonHitTestableHwnd);
                UpdatePositions();

                m_cursorInput = CreateCursorInput(
                    callback => Dispatcher.BeginInvoke(callback, System.Windows.Threading.DispatcherPriority.Background),
                    () => Volatile.Read(ref m_activeCursorGeneration),
                    () =>
                    {
                        var point = m_display.Workspace.CursorLocation;
                        return new(point.X, point.Y);
                    },
                    point => m_window.PointFromScreen(point),
                    point => VisualTreeHelper.HitTest(m_window, point) != null,
                    () => PInvoke.GetWindowLong(new(m_hwnd), GetWindowLongPtr_nIndex.GWL_STYLE),
                    value => _ = SetWindowLongPtr(new(m_hwnd), GetWindowLongPtr_nIndex.GWL_STYLE, value),
                    exception => App.Current.Logger.Warning(exception, "Overlay cursor refresh failed"));

                subscriptionAttempts |= 1;
                display.Workspace.CursorLocationChanged += OnCursorLocationChanged;
                subscriptionAttempts |= 2;
                display.Workspace.FocusedWindowChanged += OnFocusedWindowChanged;
                subscriptionAttempts |= 4;
                display.Workspace.WindowAdded += OnWindowAdded;
                subscriptionAttempts |= 8;
                display.Workspace.WindowRemoved += OnWindowRemoved;

                if (App.Current.Services.GetService<LowLevelMouseHook>() is LowLevelMouseHook mshk)
                {
                    m_mshk = mshk;
                    m_mshk.ButtonStateChanged += OnMouseButtonStateChanged;
                }
            }
            catch (Exception error)
            {
                RollbackConstruction(error, subscriptionAttempts);
                throw;
            }
        }

        private void RollbackConstruction(Exception primary, int subscriptionAttempts)
        {
            // An event add can acquire its handler and then throw. Admit rollback
            // before calling providers; preserve the constructor's original error.
            m_closed = true;
            m_cursorGeneration++;
            Volatile.Write(ref m_activeCursorGeneration, 0);
            List<Exception>? failures = null;
            void release(Action action)
            {
                try { action(); }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
            release(() => m_cursorInput.Lifetime?.Dispose());
            release(m_refreshLoop.Dispose);
            if ((subscriptionAttempts & 1) != 0) release(() => m_display.Workspace.CursorLocationChanged -= OnCursorLocationChanged);
            if ((subscriptionAttempts & 2) != 0) release(() => m_display.Workspace.FocusedWindowChanged -= OnFocusedWindowChanged);
            if ((subscriptionAttempts & 4) != 0) release(() => m_display.Workspace.WindowAdded -= OnWindowAdded);
            if ((subscriptionAttempts & 8) != 0) release(() => m_display.Workspace.WindowRemoved -= OnWindowRemoved);
            if (m_mshk != null) release(() => m_mshk.ButtonStateChanged -= OnMouseButtonStateChanged);
            // Destroying the native owner may synchronously destroy its owned
            // surface first. Both must join rollback before either HWND closes.
            m_window?.PrepareConstructionRollback(primary);
            m_nonHitTestableWindow?.PrepareConstructionRollback(primary);
            release(() => m_nonHitTestableWindow?.AbortConstruction(primary));
            release(() => m_window?.AbortConstruction(primary));
            if (failures != null) AttachLaterCloseFailures(primary, failures);
        }

        public void Show()
        {
            Dispatcher.VerifyAccess();
            if (m_closed || m_isShown || m_showing) { return; }
            long generation = ++m_cursorGeneration;
            m_showing = true;
            try
            {
                m_nonHitTestableWindow.Show();
                if (m_closed || generation != m_cursorGeneration) { return; }
                // Hit-testable window goes on top, because it after an interaction,
                // it will be raised above the non-hit testable window anyway.
                m_window.Show();
                if (m_closed || generation != m_cursorGeneration) { return; }
                m_isShown = true;
                Volatile.Write(ref m_activeCursorGeneration, generation);

                TryUpdatePositions();
                if (generation != Volatile.Read(ref m_activeCursorGeneration)) { return; }
                m_refreshLoop.Start();
                if (generation == Volatile.Read(ref m_activeCursorGeneration)) { m_cursorInput.Invalidate(); }
            }
            finally
            {
                if (generation == m_cursorGeneration) { m_showing = false; }
            }
        }

        internal void Hide()
        {
            Dispatcher.VerifyAccess();
            if (m_closed) { return; }
            long generation = ++m_cursorGeneration;
            Volatile.Write(ref m_activeCursorGeneration, 0);
            m_showing = false;
            m_isShown = false;
            m_refreshLoop.Stop();
            if (m_closed || generation != m_cursorGeneration) { return; }
            m_nonHitTestableWindow.Hide();
            if (m_closed || generation != m_cursorGeneration) { return; }
            m_window.Hide();
        }

        public void Close()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(Close);
                return;
            }
            CompleteClose(
                ref m_closed,
                () =>
                {
                    m_cursorGeneration++;
                    Volatile.Write(ref m_activeCursorGeneration, 0);
                    m_showing = false;
                    m_isShown = false;
                },
                m_cursorInput.Lifetime.Dispose,
                m_refreshLoop.Dispose,
                () => AnchorSource = null,
                release =>
                {
                    release(() => m_display.Workspace.CursorLocationChanged -= OnCursorLocationChanged);
                    release(() => m_display.Workspace.FocusedWindowChanged -= OnFocusedWindowChanged);
                    release(() => m_display.Workspace.WindowAdded -= OnWindowAdded);
                    release(() => m_display.Workspace.WindowRemoved -= OnWindowRemoved);
                    if (m_mshk != null)
                    {
                        release(() => m_mshk.ButtonStateChanged -= OnMouseButtonStateChanged);
                    }
                },
                release => Dispatcher.Invoke(() =>
                {
                    release(() => m_nonHitTestableWindow.Visibility = Visibility.Collapsed);
                    release(() => m_nonHitTestableWindow.AllowClose = true);
                    release(m_nonHitTestableWindow.Close);
                    release(() => m_window.Visibility = Visibility.Collapsed);
                    release(() => m_window.AllowClose = true);
                    release(m_window.Close);
                }));
        }

        internal static void CompleteClose(
            ref bool closed,
            Action invalidateState,
            Action disposeCursorInput,
            Action disposeRefreshLoop,
            Action clearAnchor,
            Action<Action<Action>> releaseSubscriptions,
            Action<Action<Action>> closeWindows)
        {
            if (closed) { return; }
            closed = true;
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

            release(invalidateState);
            release(disposeCursorInput);
            release(disposeRefreshLoop);
            release(clearAnchor);
            release(() => releaseSubscriptions(release));
            release(() => closeWindows(release));

            if (laterFailures != null)
            {
                AttachLaterCloseFailures(failure!.SourceException, laterFailures);
            }
            failure?.Throw();
        }

        private static void AttachLaterCloseFailures(Exception primary, List<Exception> laterFailures)
        {
            try
            {
                const string key = "OverlayHost.CloseExceptions";
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

        private void TryUpdatePositions()
        {
            try
            {
                UpdatePositions();
            }
            catch (Exception exception)
            {
                App.Current.Logger.Warning(exception, "Overlay position refresh failed");
            }
        }

        public void UpdatePositions()
        {
            if (m_closed) { return; }
            var anchor = AnchorSource != null
                ? AnchorSource()
                : new IntPtr(-1);

            // Reorder front .. back (front is drawn on top)
            // Goal: beforeAnchor -> m_hwnd -> m_nonHitTestableHwnd -> anchor
            var beforeAnchor = PInvoke.GetWindow(new(anchor), GetWindow_uCmdFlags.GW_HWNDPREV);
            if (PInvoke.GetWindow(new(m_hwnd), GetWindow_uCmdFlags.GW_HWNDNEXT).Value == beforeAnchor)
            {
                return;
            }

            var swpFlags = SetWindowPos_uFlags.SWP_NOMOVE | SetWindowPos_uFlags.SWP_NOSIZE | SetWindowPos_uFlags.SWP_NOACTIVATE | SetWindowPos_uFlags.SWP_NOSENDCHANGING;
            if (beforeAnchor.Value == default)
            {
                if (PInvoke.IsWindow(new(anchor)))
                {
                    PInvoke.SetWindowPos(new(m_hwnd), new(anchor), 0, 0, 0, 0, swpFlags);
                    PInvoke.SetWindowPos(new(anchor), new(m_hwnd), 0, 0, 0, 0, swpFlags);
                }
                else
                {
                    PInvoke.SetWindowPos(new(m_hwnd), new(), 0, 0, 0, 0, swpFlags);
                }
            }
            else
            {
                PInvoke.SetWindowPos(new(m_hwnd), new(beforeAnchor), 0, 0, 0, 0, swpFlags);
            }
        }

        public void SetResource(string key, object? value)
        {
            m_window.Resources[key] = value;
            m_nonHitTestableWindow.Resources[key] = value;
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == ContentProperty)
            {
                m_window.SetContent(Content!);
            }
            else if (e.Property == NonHitTestableContentProperty)
            {
                m_nonHitTestableWindow.SetContent(NonHitTestableContent!);
            }
        }

        private static void EnableWindow(IntPtr hwnd)
        {
            var oldValue = PInvoke.GetWindowLong(new(hwnd), GetWindowLongPtr_nIndex.GWL_STYLE);
            var newValue = oldValue & ~(int)WINDOWS_STYLE.WS_DISABLED;
            if (oldValue != newValue)
            {
                _ = SetWindowLongPtr(new(hwnd), GetWindowLongPtr_nIndex.GWL_STYLE, newValue);
            }
        }

        private static void DisableWindow(IntPtr hwnd)
        {
            var oldValue = PInvoke.GetWindowLong(new(hwnd), GetWindowLongPtr_nIndex.GWL_STYLE);
            var newValue = oldValue | (int)WINDOWS_STYLE.WS_DISABLED;
            if (oldValue != newValue)
            {
                _ = SetWindowLongPtr(new(hwnd), GetWindowLongPtr_nIndex.GWL_STYLE, newValue);
            }
        }

        private void OnWindowRemoved(object? sender, WindowChangedEventArgs e)
        {
            DispatchUpdatePositions();
        }

        private void OnWindowAdded(object? sender, WindowChangedEventArgs e)
        {
            DispatchUpdatePositions();
        }

        private void OnFocusedWindowChanged(object? sender, FocusedWindowChangedEventArgs e)
        {
            DispatchUpdatePositions();
        }

        private void OnMouseButtonStateChanged(object? sender, ref LowLevelMouseHook.ButtonStateChangedEventArgs e)
        {
            DispatchUpdatePositions();
        }

        private void DispatchUpdatePositions()
        {
            if (m_isShown)
            {
                Dispatcher.BeginInvoke(() => UpdatePositions());
            }
        }

        private void OnCursorLocationChanged(object? sender, CursorLocationChangedEventArgs e)
        {
            m_cursorInput.Invalidate();
        }
    }
}
