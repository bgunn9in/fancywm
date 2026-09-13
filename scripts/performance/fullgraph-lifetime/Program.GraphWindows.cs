internal static partial class Program
{
    private sealed class GraphWindowGeneration(int id, System.Windows.Window window)
    {
        public int Id = id;
        public string Type = window.GetType().FullName!;
        public System.WeakReference Owner = new(window);
        public System.IntPtr Hwnd;
        public bool Closed;
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<System.Windows.Window, GraphWindowGeneration> GraphWindowIds = new();
    private static readonly System.Collections.Generic.List<GraphWindowGeneration> GraphGenerations = [];
    private static bool IsGraphWindow(System.Windows.Window window) => window is FancyWM.MainWindow ||
        window.GetType().FullName == "FancyWM.Toasts.ToastWindow" ||
        window.GetType().FullName == "FancyWM.Windows.OverlayHost+OverlayWindow";
    private static void TrackGraphWindow(System.Windows.Window window)
    {
        if ((NativeHeapSnapshot == null && PssBeginCall == null && DxgiSampleCall == null) || !IsGraphWindow(window) || GraphWindowIds.TryGetValue(window, out _)) return;
        var entry = new GraphWindowGeneration(GraphGenerations.Count + 1, window);
        GraphWindowIds.Add(window, entry); GraphGenerations.Add(entry);
        void SourceReady()
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            uint pid = 0;
            Require(hwnd != System.IntPtr.Zero && IsWindow(hwnd) && GetWindowThreadProcessId(hwnd, out pid) != 0 && pid == System.Environment.ProcessId, "Graph generation native identity.");
            entry.Hwnd = hwnd;
            Row(new { Kind = "GraphWindowAcquired", entry.Id, entry.Type, Hwnd = hwnd.ToInt64(), Pid = pid, DispatcherAlive = !window.Dispatcher.HasShutdownStarted });
        }
        if (new System.Windows.Interop.WindowInteropHelper(window).Handle != System.IntPtr.Zero) SourceReady();
        else window.SourceInitialized += (_, _) => SourceReady();
        window.Closed += (_, _) =>
        {
            Require(!entry.Closed && entry.Hwnd != System.IntPtr.Zero, "Missing/duplicate graph generation close.");
            entry.Closed = true;
            Row(new { Kind = "GraphWindowClosed", entry.Id, entry.Type, Hwnd = entry.Hwnd.ToInt64(), NativeAliveDuringClosedEvent = IsWindow(entry.Hwnd), DispatcherAlive = !window.Dispatcher.HasShutdownStarted });
        };
    }
    private static void ObserveGraphWindows(string label)
    {
        if (NativeHeapSnapshot == null && PssBeginCall == null && DxgiSampleCall == null) return;
        var current = System.Windows.Application.Current?.Windows.Cast<System.Windows.Window>().Where(IsGraphWindow).ToArray() ?? [];
        foreach (var window in current) TrackGraphWindow(window);
        var active = GraphGenerations.Where(g => !g.Closed).ToArray();
        Require(current.Length == active.Length && current.All(w => active.Any(g => System.Object.ReferenceEquals(g.Owner.Target, w))), "Untracked graph window generation.");
        var handles = GraphGenerations.Where(g => g.Hwnd != System.IntPtr.Zero).Select(g => g.Hwnd).Distinct().ToArray();
        Row(new { Kind = "GraphWindowCheckpoint", Label = label, Acquired = GraphGenerations.Count,
            Closed = GraphGenerations.Count(g => g.Closed), ApplicationGraphWindows = current.Length,
            Active = active.Select(g => new { g.Id, g.Type, Hwnd = g.Hwnd.ToInt64(), NativeAlive = IsWindow(g.Hwnd) }).ToArray(),
            SurvivingNativeHwnds = handles.Where(IsWindow).Select(h => h.ToInt64()).ToArray(),
            Displays = Workspace is WinMan.IWorkspace workspace ? workspace.DisplayManager.Displays.Select(d => new { Device = Property(d, "DeviceID"), d.Bounds, d.WorkArea, d.Scaling, d.RefreshRate }).ToArray() : null,
            DispatcherAlive = !System.Windows.Threading.Dispatcher.CurrentDispatcher.HasShutdownStarted });
        if (label is "exit" or "shutdown") Require(active.Length == 0 && handles.All(h => !IsWindow(h)), "Graph generation survived graceful shutdown.");
    }
}
