using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using WinMan.Windows;

internal static partial class Program
{
    private static int ThemeCycles;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] WorkspaceCycle()
    {
        var workspace = new Win32Workspace();
        var errors = new ConcurrentQueue<Exception>();
        workspace.UnhandledException += (_, e) => errors.Enqueue((Exception)e.ExceptionObject);
        var weak = new List<WeakReference> { new(workspace) };
        IntPtr hwnd = IntPtr.Zero;
        Thread[] threads = [];
        try
        {
            workspace.Open();
            Wait(() => (IntPtr)Field(workspace, "m_msgWnd")! != IntPtr.Zero, "Workspace native message HWND");
            hwnd = (IntPtr)Field(workspace, "m_msgWnd")!;
            uint tid = GetWindowThreadProcessId(hwnd, out uint pid);
            Require(IsWindow(hwnd) && pid == Environment.ProcessId && DesktopName(tid) == Desktop, "Workspace acquired a foreign HWND.");
            threads = new[] { "m_eventLoopThread", "m_processingThread", "m_backgroundProcessingThread" }.Select(n => (Thread)Field(workspace, n)!).ToArray();
            weak.AddRange(threads.Select(t => new WeakReference(t)));
            var displays = workspace.DisplayManager;
            Require(displays.Displays.Count > 0, "No native displays."); weak.Add(new(displays));
            // Exercise the normal reconciliation timer while the message loop lives.
            var settled = Task.Delay(300); Wait(() => settled.IsCompleted, "Workspace native timer epoch");
            Row(new { Kind = "WorkspaceAcquired", Hwnd = hwnd.ToInt64(), Thread = tid, Desktop, DisplayCount = displays.Displays.Count,
                VirtualDesktopManager = Field(workspace, "m_virtualDesktops")?.GetType().FullName, Workers = threads.Length });
        }
        finally { workspace.Dispose(); workspace.Dispose(); }
        Require(threads.All(t => t.Join(TimeSpan.FromSeconds(5))), "Workspace worker survived Dispose.");
        Require(!IsWindow(hwnd) && (IntPtr)Field(workspace, "m_msgWnd")! == IntPtr.Zero, "Workspace HWND survived Dispose.");
        Require(errors.IsEmpty, "Workspace native errors: " + string.Join("\n", errors));
        Row(new { Kind = "WorkspaceClosed", Hwnd = hwnd.ToInt64(), Destroyed = true, WorkersAlive = threads.Count(t => t.IsAlive), Errors = errors.Count });
        return weak.ToArray();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ThemeCycle()
    {
        string dir = Path.Combine(Output, "themes", (++ThemeCycles).ToString("D4"));
        Require(!Directory.Exists(dir), "Theme fixture path already exists.");
        var type = typeof(FancyWM.App).Assembly.GetType("FancyWM.Utilities.ThemeEngineManager")!;
        var manager = type.GetMethod("Initialize", Static)!.Invoke(null, [dir, "owned.css"])!;
        object? watcher = Field(manager, "m_watcher"); Require(watcher != null, "Native watcher was not acquired.");
        WeakReference[] weak = [new(manager), new(watcher)];
        try
        {
            Wait(() => Completion(manager).IsCompleted, "Theme initialization"); Completion(manager).GetAwaiter().GetResult();
            var initial = File.ReadAllBytes(Path.Combine(dir, "owned.css"));
            using (var copy = new FileStream(Path.Combine(dir, "before.css"), FileMode.CreateNew)) copy.Write(initial);
            File.AppendAllText(Path.Combine(dir, "owned.css"), "\n/* owned native watcher change */\n");
            Wait(() => ((string)Field(manager, "m_lastAppliedCss")!).Contains("owned native watcher change"), "Native watcher reload application");
            Wait(() => Completion(manager).IsCompleted, "Theme file reload"); Completion(manager).GetAwaiter().GetResult();
        }
        finally { ((IDisposable)manager).Dispose(); ((IDisposable)manager).Dispose(); }
        Wait(() => Completion(manager).IsCompleted, "Theme disposal"); Completion(manager).GetAwaiter().GetResult();
        Require(Field(manager, "m_watcher") == null && Field(manager, "m_defaultSubscription") == null && Field(manager, "m_timer") == null,
            "Theme manager retained a watcher/subscription/timer.");
        Require(!(bool)Field(manager, "m_workerRunning")!, "Theme worker survived disposal.");
        long generation = (long)Field(manager, "m_generation")!;
        File.AppendAllText(Path.Combine(dir, "owned.css"), "\n/* owned post-disposal notification */\n");
        var staleDelay = Task.Delay(350); Wait(() => staleDelay.IsCompleted, "Post-disposal file notification");
        Require(generation == (long)Field(manager, "m_generation")! && !(bool)Field(manager, "m_workerRunning")!, "Closed watcher admitted a stale callback.");
        Row(new { Kind = "ThemeClosed", Cycle = ThemeCycles, WatcherAcquired = true, ReloadApplied = true, StaleNotificationInert = true,
            WatcherReleased = true, Subscriptions = 0, Timers = 0, WorkerRunning = false });
        return weak;
    }
}
