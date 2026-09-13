using System.Windows.Interop;

using FancyWM.DllImports;

using WinMan;

namespace FancyWM.Toasts
{
    internal interface IToastWindowPlatform
    {
        void Initialize(ToastWindow window);
        void SetPosition(ToastWindow window, IDisplay display);
        void SetVisible(ToastWindow window, bool visible);
        void Close(ToastWindow window);
    }

    internal sealed class NativeToastWindowPlatform : IToastWindowPlatform
    {
        public void Initialize(ToastWindow window)
        {
            window.ShowActivated = false;
            var hwnd = new HWND(new WindowInteropHelper(window).EnsureHandle());
            _ = PInvoke.SetWindowLong(hwnd, GetWindowLongPtr_nIndex.GWL_STYLE, PInvoke.GetWindowLong(hwnd, GetWindowLongPtr_nIndex.GWL_STYLE) | (int)WINDOWS_STYLE.WS_DISABLED);
            _ = PInvoke.SetWindowLong(hwnd, GetWindowLongPtr_nIndex.GWL_EXSTYLE,
                (int)(WINDOWS_EX_STYLE.WS_EX_TOOLWINDOW | WINDOWS_EX_STYLE.WS_EX_TOPMOST | WINDOWS_EX_STYLE.WS_EX_NOACTIVATE));
        }

        public void SetPosition(ToastWindow window, IDisplay display)
        {
            var workArea = display.WorkArea;
            PInvoke.SetWindowPos(new(new WindowInteropHelper(window).EnsureHandle()), new(), workArea.Left, workArea.Top, workArea.Width, workArea.Height,
                SetWindowPos_uFlags.SWP_NOZORDER | SetWindowPos_uFlags.SWP_NOACTIVATE);
        }

        public void SetVisible(ToastWindow window, bool visible)
        {
            if (window.IsDisposed || visible != (window.ToastItems.Count > 0)) { return; }
            // WPF must attach and lay out the visual tree on its first actual toast.
            if (visible) { window.Show(); }
            else { window.Hide(); }
            if (window.IsDisposed || visible != (window.ToastItems.Count > 0)) { return; }
            var flags = SetWindowPos_uFlags.SWP_NOACTIVATE | SetWindowPos_uFlags.SWP_NOMOVE | SetWindowPos_uFlags.SWP_NOSIZE | SetWindowPos_uFlags.SWP_NOSENDCHANGING;
            flags |= visible ? SetWindowPos_uFlags.SWP_SHOWWINDOW : SetWindowPos_uFlags.SWP_HIDEWINDOW;
            PInvoke.SetWindowPos(new(new WindowInteropHelper(window).EnsureHandle()), new(-1), 0, 0, 0, 0, flags);
        }

        public void Close(ToastWindow window) => window.Close();
    }
}
