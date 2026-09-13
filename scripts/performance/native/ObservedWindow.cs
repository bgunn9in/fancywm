using System.Diagnostics;
using WinMan;
using Rectangle = WinMan.Rectangle;
using Point = WinMan.Point;

namespace FancyWM.AnimationNativeHarness;

// All behavior comes from the real Win32Window. The counters describe IWindow
// calls, not GetWindowRect calls: Win32Window.Position reads its provider cache.
internal sealed class ObservedWindow(IWindow inner, Recording recording, int target) : IWindow
{
    public Rectangle Position
    {
        get
        {
            long start = Stopwatch.GetTimestamp();
            var result = inner.Position;
            recording.Add(Recording.Operation.PositionRead, start, target,
                result.Left, result.Top, result.Width, result.Height);
            return result;
        }
    }

    public void SetPosition(Rectangle position)
    {
        long start = Stopwatch.GetTimestamp();
        inner.SetPosition(position);
        recording.Add(Recording.Operation.SetPosition, start, target,
            position.Left, position.Top, position.Width, position.Height);
    }

    public event EventHandler<WindowPositionChangedEventArgs>? PositionChangeStart { add => inner.PositionChangeStart += value; remove => inner.PositionChangeStart -= value; }
    public event EventHandler<WindowPositionChangedEventArgs>? PositionChangeEnd { add => inner.PositionChangeEnd += value; remove => inner.PositionChangeEnd -= value; }
    public event EventHandler<WindowPositionChangedEventArgs>? PositionChanged { add => inner.PositionChanged += value; remove => inner.PositionChanged -= value; }
    public event EventHandler<WindowStateChangedEventArgs>? StateChanged { add => inner.StateChanged += value; remove => inner.StateChanged -= value; }
    public event EventHandler<WindowTopmostChangedEventArgs>? TopmostChanged { add => inner.TopmostChanged += value; remove => inner.TopmostChanged -= value; }
    public event EventHandler<WindowFocusChangedEventArgs>? GotFocus { add => inner.GotFocus += value; remove => inner.GotFocus -= value; }
    public event EventHandler<WindowFocusChangedEventArgs>? LostFocus { add => inner.LostFocus += value; remove => inner.LostFocus -= value; }
    public event EventHandler<WindowChangedEventArgs>? Added { add => inner.Added += value; remove => inner.Added -= value; }
    public event EventHandler<WindowChangedEventArgs>? Removed { add => inner.Removed += value; remove => inner.Removed -= value; }
    public event EventHandler<WindowChangedEventArgs>? Destroyed { add => inner.Destroyed += value; remove => inner.Destroyed -= value; }
    public event EventHandler<WindowTitleChangedEventArgs>? TitleChanged { add => inner.TitleChanged += value; remove => inner.TitleChanged -= value; }
    public object SyncRoot => inner.SyncRoot;
    public IWorkspace Workspace => inner.Workspace;
    public string Title => inner.Title;
    public WindowState State => inner.State;
    public Point? MinSize => inner.MinSize;
    public Point? MaxSize => inner.MaxSize;
    public Rectangle FrameMargins => inner.FrameMargins;
    public bool CanResize => inner.CanResize;
    public bool CanMove => inner.CanMove;
    public bool CanReorder => inner.CanReorder;
    public bool CanMinimize => inner.CanMinimize;
    public bool CanMaximize => inner.CanMaximize;
    public bool CanClose => inner.CanClose;
    public bool IsTopmost => inner.IsTopmost;
    public bool IsFocused => inner.IsFocused;
    public bool IsAlive => inner.IsAlive;
    public IntPtr Handle => inner.Handle;
    public Process GetProcess() => inner.GetProcess();
    public IWindow? GetPreviousWindow() => inner.GetPreviousWindow();
    public IWindow? GetNextWindow() => inner.GetNextWindow();
    public void Close() => inner.Close();
    public void SetState(WindowState state) => inner.SetState(state);
    public void SetTopmost(bool topmost) => inner.SetTopmost(topmost);
    public void InsertAfter(IWindow other) => inner.InsertAfter(other);
    public void SendToBack() => inner.SendToBack();
    public void BringToFront() => inner.BringToFront();
    public bool RequestFocus() => inner.RequestFocus();
    public bool Equals(IWindow? other) => ReferenceEquals(this, other);
}
