using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Threading;
using FancyWM;
using FancyWM.Layouts.Tiling;
using FancyWM.Models;
using FancyWM.Windows;
using WinMan;

internal static partial class Program
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const string Exclusion = "^(?!FancyWM\\.Perf010Targets$).*";
    private static readonly object LogLock = new();
    private static StreamWriter Log = null!;
    private static string Output = "", Desktop = "", Mode = "";
    private static App App = null!;
    private static MainWindow MainOwner = null!;
    private static AppState State = null!;
    private static Exception? Failure;
    private static object Workspace = null!, Animation = null!, Mouse = null!, Keyboard = null!, Mica = null!;
    private static IntPtr[] Handles = [];
    private static bool OriginalArranging;
    private static int SettingWrites, Closed, ProgramExits, CrashCleanups;


    [STAThread]
    private static int MainEntry(string[] args)
    {
        if (args.Length != 3 || Directory.Exists(args[0])) throw new ArgumentException("<fresh-output> <owned-desktop> <mode>");
        Output = Path.GetFullPath(args[0]); Desktop = args[1]; Mode = args[2];
        Require(Desktop.StartsWith("FWM_OWNED_") && DesktopName(GetCurrentThreadId()) == Desktop, "Full graph requires its owned non-input desktop.");
        Require(Mode is "graceful" or "failfast" or "terminate" or "handler", "Invalid mode.");
        _ = SetErrorMode(3);
        Directory.CreateDirectory(Output);
        Log = new StreamWriter(new FileStream(Path.Combine(Output, "observations.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        StartGuiTrace();
        StartNativeHeap();
        StartClrTrace();
        StartD3D9();
        StartPss();
        StartProcessMemory();
        StartDxgi();
        Environment.SetEnvironmentVariable("FWM_OWNED_STARTUP_ROOT", Path.Combine(Output, "profile"));
        string data = Path.Combine(Output, "profile", "FancyWM"); Directory.CreateDirectory(data);
        bool compactTargets = Environment.GetEnvironmentVariable("FWM_FULLGRAPH_COMPACT_TARGETS") == "1";
        int targetSplitCount = compactTargets ? 50 : 5;
        Row(new { Kind = "GuiTargetLayoutContract", CompactNativeTargets = compactTargets, AutoSplitCount = targetSplitCount, NativeTargetMinimum = compactTargets ? 8 : 0, GlobalDisplayChanged = false });
        var settings = new Settings { AutoFloatNewWindows = true, ProcessIgnoreList = [Exclusion], AutoSplitCount = targetSplitCount,
            ShowStartupWindow = false, CheckForUpdates = false, RemindToRateReview = false, ShowContextHints = false,
            SoundOnFailure = false, NotifyVirtualDesktopServiceIncompatibility = false, ModifierMoveWindow = false };
        var options = (JsonSerializerOptions)typeof(AppState).GetMethod("CreateSettingsJsonSerializerOptions", Static)!.Invoke(null, null)!;
        WriteAbsolute(Path.Combine(data, "settings.json"), settings, options);
        OriginalArranging = FancyWM.Utilities.SystemParameters.Instance.WindowArranging;
        var probe = typeof(FancyWM.Utilities.SystemParameters).GetField("HarnessWindowArrangingWrite", Static);
        Require(probe != null, "The build does not contain the required observed setting adapter.");
        probe!.SetValue(null, (Action<bool>)(value =>
        {
            int call = Interlocked.Increment(ref SettingWrites);
            Row(new { Kind = "WindowArrangingWriteIntercepted", Call = call, Requested = value, NativeValue = FancyWM.Utilities.SystemParameters.Instance.WindowArranging });
        }));
        ObserveStartupEvent("ProgramExit", () => { ProgramExits++; Row(new { Kind = "ProgramExit", Count = ProgramExits, Restart = App?.RestartOnClose }); });
        ObserveStartupEvent("CrashCleanup", () => { CrashCleanups++; Row(new { Kind = "CrashCleanup", Count = CrashCleanups }); });
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Write("managed-process-exit.json", new { Pid = Environment.ProcessId, Closed });
        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.UnhandledException += (_, e) =>
        {
            // Unexpected dispatcher failures make the run red and request owned
            // cleanup. The intentional handler experiment throws on its own
            // worker and enters the actual AppDomain/production handlers.
            Failure = e.Exception; Row(new { Kind = "Failure", Error = e.Exception.ToString() });
            e.Handled = true; App?.GetType().GetMethod("Terminate", Instance)!.Invoke(App, null);
        };
        _ = dispatcher.BeginInvoke(DispatcherPriority.Background, async () =>
        {
            try
            {
                if (Failure != null) throw new InvalidOperationException("Startup already failed", Failure);
                App = (App)Application.Current; MainOwner = (MainWindow)App.MainWindow;
                State = (AppState)typeof(App).GetProperty("AppState", Instance)!.GetValue(App)!;
                await Wait(() => Field(MainOwner, "m_tiling") != null && Services().Length > 0, "full graph initialization");
                await Wait(() => ((IEnumerable)Property(Field(MainOwner, "m_tiling")!, "ExclusionMatchers")!).Cast<object>()
                    .Any(x => x.GetType().Name == "ByProcessNameMatcher" && (string?)Property(x, "ProcessName") == Exclusion), "owned target exclusion");
                Workspace = Field(MainOwner, "m_workspace")!; Animation = Field(MainOwner, "m_animationThread")!;
                Row(new { Kind = "GraphDisplayContract", Displays = ((IWorkspace)Workspace).DisplayManager.Displays.Select(d => new { Device = Property(d, "DeviceID"), d.Bounds, d.WorkArea, d.Scaling, d.RefreshRate }).ToArray() });
                Mouse = Resolve("FancyWM.Utilities.LowLevelMouseHook"); Keyboard = Resolve("FancyWM.Utilities.LowLevelKeyboardHook"); Mica = Resolve("FancyWM.Utilities.IMicaProvider");
                await Wait(() => NativeHook(Mouse) != IntPtr.Zero && NativeHook(Keyboard) != IntPtr.Zero, "native hooks installed");
                var graphWindows = App.Windows.Cast<Window>().ToArray();
                foreach (var window in graphWindows) TrackGraphWindow(window);
                Handles = graphWindows.Select(w => new WindowInteropHelper(w).Handle).Append((IntPtr)Field(Workspace, "m_msgWnd")!).Where(h => h != IntPtr.Zero).Distinct().ToArray();
                Require(Handles.Length >= 4, "Native MainWindow/overlay/workspace HWND coverage.");
                foreach (var hwnd in Handles) Require(IsWindow(hwnd) && GetWindowThreadProcessId(hwnd, out uint pid) != 0 && pid == Environment.ProcessId, "Invalid/foreign graph HWND.");
                foreach (var w in graphWindows) w.Closed += (_, _) => { Closed++; Row(new { Kind = "Closed", Type = w.GetType().FullName, DispatcherAlive = !dispatcher.HasShutdownStarted }); };
                App.Exit += (_, _) => ObserveGraphWindows("exit");
                App.Exit += (_, _) => Row(new { Kind = "ApplicationExit", DispatcherAlive = !dispatcher.HasShutdownStarted, Closed,
                    MainCompleted = Completion(MainOwner, "ShutdownCompletion").IsCompletedSuccessfully,
                    AnimationCompleted = Completion(Animation).IsCompletedSuccessfully,
                    HooksCompleted = Completion(Mouse).IsCompletedSuccessfully && Completion(Keyboard).IsCompletedSuccessfully });
                Row(new { Kind = "GraphReady", Pid = Environment.ProcessId, Desktop, Mode, Hwnds = Handles.Select(h => h.ToInt64()).ToArray(),
                    Services = Services().Select(s => s.GetType().FullName).ToArray(), Workspace = Workspace.GetType().FullName,
                    VirtualDesktopManager = Property(Workspace, "VirtualDesktopManager")!.GetType().FullName,
                    MouseHook = NativeHook(Mouse).ToInt64(), KeyboardHook = NativeHook(Keyboard).ToInt64(),
                    MouseDesktop = DesktopName(HookThread(Mouse)), KeyboardDesktop = DesktopName(HookThread(Keyboard)),
                    AnimationRunning = !Completion(Animation).IsCompleted, MicaRunning = (bool)Property(Mica, "IsWorkerAlive")!,
                    SettingsPath = State.Settings.FullPath, StartupAppMain = true, ControlledSettingWrite = true });
                Require(NativeHook(Mouse) != IntPtr.Zero && NativeHook(Keyboard) != IntPtr.Zero && DesktopName(HookThread(Mouse)) == Desktop && DesktopName(HookThread(Keyboard)) == Desktop, "Hook ownership.");
                Require(SettingWrites == 1 && OriginalArranging == FancyWM.Utilities.SystemParameters.Instance.WindowArranging, "Setting adapter boundary.");
                if (Mode == "graceful")
                {
                    if (Environment.GetEnvironmentVariable("FWM_FULLGRAPH_SHUTDOWN_PROBE") != "1") await RunRetention();
                    else Row(new { Kind = "ShutdownProbe", MicaWorker = (bool)Property(Mica, "IsWorkerAlive")! });
                    Row(new { Kind = "TerminateRequested", DispatcherAlive = !dispatcher.HasShutdownStarted });
                    typeof(App).GetMethod("Terminate", Instance)!.Invoke(App, null);
                }
                else
                {
                    Write("crash-ready.json", new { Pid = Environment.ProcessId, Desktop, Handles = Handles.Select(h => h.ToInt64()).ToArray(), Mode, Closed });
                    while (!File.Exists(Path.Combine(Output, "crash.go"))) await Task.Delay(25);
                    Row(new { Kind = "CrashEntered", Mode, Closed, DispatcherAlive = !dispatcher.HasShutdownStarted });
                    if (Mode == "failfast") Environment.FailFast("FWM owned full graph FailFast");
                    else if (Mode == "terminate") _ = TerminateProcess(Process.GetCurrentProcess().Handle, 0xE000F016);
                    else new Thread(() => throw new OwnedFullGraphException("FWM owned production crash handler")) { Name = "OwnedFullGraphCrash", IsBackground = true }.Start();
                }
            }
            catch (Exception e)
            {
                Failure = e; Row(new { Kind = "Failure", Error = e.ToString() });
                if (Application.Current is App app) typeof(App).GetMethod("Terminate", Instance)!.Invoke(app, null);
                else dispatcher.InvokeShutdown();
            }
        });
        Row(new { Kind = "Process", Pid = Environment.ProcessId, StartedUtc = DateTime.UtcNow, Desktop, Mode,
            Runtime = RuntimeInformation.FrameworkDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(), OriginalArranging });
        int code = Startup.Main(["--owned-fullgraph-harness"]);
        Require(Mode == "graceful", "Hard crash returned from Startup.MainOwner.");
        if (Failure != null) { Log.Dispose(); return 1; }
        Require(Completion(MainOwner, "ShutdownCompletion").IsCompletedSuccessfully && Completion(Animation).IsCompletedSuccessfully, "MainOwner/animation shutdown incomplete.");
        Require(Completion(Mouse).IsCompletedSuccessfully && Completion(Keyboard).IsCompletedSuccessfully && NativeHook(Mouse) == IntPtr.Zero && NativeHook(Keyboard) == IntPtr.Zero, "Native hook survived startup provider disposal.");
        Require(!(bool)Property(Mica, "IsWorkerAlive")! && WorkspaceThreads().All(t => !t.IsAlive), "Provider/worker survived Startup.MainOwner return.");
        Require(Handles.All(h => !IsWindow(h)) && SettingWrites == 2 && FancyWM.Utilities.SystemParameters.Instance.WindowArranging == OriginalArranging, "Native resources/global parameter changed.");
        Require(ProgramExits == 1 && CrashCleanups == 0 && !App.RestartOnClose, "Unexpected startup exit/restart.");
        Write("summary.json", new { Verdict = "PASS", Pid = Environment.ProcessId, Closed, Hwnds = Handles.Select(h => h.ToInt64()).ToArray(), SurvivingHwnds = Handles.Where(IsWindow).Select(h => h.ToInt64()).ToArray(),
            StartupReturned = true, DispatcherStopped = dispatcher.HasShutdownFinished, ProviderDisposed = true, HooksCompleted = true,
            SettingWrites, OriginalArranging, FinalArranging = FancyWM.Utilities.SystemParameters.Instance.WindowArranging,
            MainCompleted = true, WorkspaceWorkersStopped = true, MicaStopped = true, ProgramExits, CrashCleanups, FullGraphWithControlledSetting = true, UnmodifiedStartup = false });
        GuiTraceInventory("shutdown");
        ObserveNativeHeap("shutdown");
        MarkD3D9(99);
        ObservePss("shutdown");
        ObserveDxgiSynchronously(99);
        ObserveGraphWindows("shutdown");
        Log.Dispose(); return code;
    }
    [STAThread]
    private static void Main(string[] args)
    {
        try { Environment.ExitCode = MainEntry(args); }
        finally { try { StopProcessMemory(); } finally { try { StopDxgi(); } finally { try { StopD3D9(); } finally { try { StopHeapTrace(); } finally { try { StopClrTrace(); } finally { StopOwnedEtw(); } } } } } }
    }
    private static object? Field(object owner, string name) => owner.GetType().GetField(name, Instance)!.GetValue(owner);
    private static object? Property(object owner, string name) => owner.GetType().GetProperty(name, Instance)!.GetValue(owner);
    private static Task Completion(object owner, string name = "Completion") => (Task)Property(owner, name)!;
    private static object Resolve(string name) => ((IServiceProvider)typeof(App).GetProperty("Services", Instance)!.GetValue(App)!).GetService(typeof(App).Assembly.GetType(name)!)!;
    private static IntPtr NativeHook(object owner) => (IntPtr)Field(Field(owner, "m_hHook")!, "Value")!;
    private static uint HookThread(object owner) => (uint)Field(Field(owner, "m_lifetime")!, "m_threadId")!;
    private static Thread[] WorkspaceThreads() => new[] { "m_eventLoopThread", "m_processingThread", "m_backgroundProcessingThread" }.Select(n => (Thread)Field(Workspace, n)!).ToArray();
    private static void ObserveStartupEvent(string name, Action callback)
    {
        var field = typeof(Startup).GetField(name, Static)!; field.SetValue(null, (Action?)field.GetValue(null) + callback);
    }
    private static void OnWindowLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Window graphWindow) TrackGraphWindow(graphWindow);
        if (sender is not ErrorMessageBox dialog) return;
        // A test-owned dialog chooses quit. Production's default restart would
        // invoke its broad process-name restart command, which is disallowed.
        var hwnd = new WindowInteropHelper(dialog).Handle;
        Require(GetWindowThreadProcessId(hwnd, out uint pid) != 0 && pid == Environment.ProcessId && DesktopName(GetCurrentThreadId()) == Desktop, "Foreign error dialog.");
        dialog.IsRestartEnabled = false; dialog.IsSubmitLogEnabled = false;
        Row(new { Kind = "ErrorDialog", Hwnd = hwnd.ToInt64(), Mode, OriginalException = dialog.ExceptionObject is OwnedFullGraphException, Desktop, RestartDisabled = true });
        if (Mode != "handler") Failure = dialog.ExceptionObject ?? new InvalidOperationException("Unexpected production error dialog.");
        dialog.Closed += (_, _) => Row(new { Kind = "ErrorDialogClosed", Hwnd = hwnd.ToInt64(), Restart = App?.RestartOnClose });
        _ = dialog.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { dialog.DialogResult = false; dialog.Close(); });
    }
    private sealed class OwnedFullGraphException(string message) : Exception(message);
    private static async Task Wait(Func<bool> predicate, string operation)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate() && watch.Elapsed < TimeSpan.FromSeconds(25)) await Task.Delay(20);
        Require(predicate(), "Timed out: " + operation);
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Row(object value) { lock (LogLock) Log.WriteLine(JsonSerializer.Serialize(value)); }
    private static void Write(string name, object value) => WriteAbsolute(Path.Combine(Output, name), value);
    private static void WriteAbsolute(string path, object value, JsonSerializerOptions? options = null)
    { using var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); JsonSerializer.Serialize(f, value, options); f.Flush(true); }
    private static string DesktopName(uint tid)
    {
        var text = new StringBuilder(256); Require(GetUserObjectInformation(GetThreadDesktop(tid), 2, text, 512, out _), "Desktop identity"); return text.ToString();
    }
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint tid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetUserObjectInformationW")] private static extern bool GetUserObjectInformation(IntPtr h, int i, StringBuilder s, int n, out int r);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr p, uint flag);
}
