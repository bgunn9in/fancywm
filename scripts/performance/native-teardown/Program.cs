using System.Diagnostics;
using System.IO;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FancyWM;
using FancyWM.Models;
using FancyWM.ViewModels;
using FancyWM.Windows;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;

internal static partial class Program
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static StreamWriter Log = null!;
    private static string Output = "", Desktop = "";
    private static App App = null!;
    private static AppState State = null!;
    private static object Mouse = null!, Keyboard = null!;
    private static Exception? Failure;
    private static int Closed;
    private static IntPtr[] FinalHandles = [];

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 3 || Directory.Exists(args[0])) throw new ArgumentException("<fresh-output> <desktop-name> <graceful|failfast|terminate>");
        Output = Path.GetFullPath(args[0]); Desktop = args[1]; string mode = args[2];
        if ((mode != "browser" && !Desktop.StartsWith("FWM_OWNED_", StringComparison.Ordinal)) || DesktopName(GetCurrentThreadId()) != Desktop)
            throw new InvalidOperationException("Native hooks and windows require the fresh owned non-input desktop.");
        if (mode is not ("graceful" or "failfast" or "terminate" or "providers" or "browser")) throw new ArgumentException("mode");
        _ = SetErrorMode(3); // Only this owned process: no critical-error/GP-fault UI.
        Directory.CreateDirectory(Output); Directory.SetCurrentDirectory(Output);
        Log = new StreamWriter(new FileStream("observations.jsonl", FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        Row(new { Kind = "Process", Mode = mode, Pid = Environment.ProcessId, Desktop, StartedUtc = DateTime.UtcNow,
            Runtime = RuntimeInformation.FrameworkDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Scope = "Production App, Startup.ConfigureServices, real file settings and native hooks/windows; no MainWindow or Startup.AppMain" });
        var settings = new Settings { ShowStartupWindow = false, CheckForUpdates = false, RemindToRateReview = false };
        var options = (JsonSerializerOptions)typeof(AppState).GetMethod("CreateSettingsJsonSerializerOptions", Static)!.Invoke(null, null)!;
        Write("settings.json", settings, options);
        var registrations = new ServiceCollection();
        var type = typeof(Startup).GetNestedType("Arguments", BindingFlags.NonPublic)!;
        var arguments = Activator.CreateInstance(type, true)!;
        type.GetProperty("LogLevel")!.SetValue(arguments, LogEventLevel.Information);
        type.GetProperty("HasConsole")!.SetValue(arguments, true);
        typeof(Startup).GetMethod("ConfigureServices", Static)!.Invoke(null, [registrations, arguments]);
        using var provider = registrations.BuildServiceProvider();
        App = new App(provider) { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Same compiled dictionary types as App.xaml, with no MainWindow StartupUri.
        var themeResources = new ModernWpf.ThemeResources { CanBeAccessedAcrossThreads = true };
        App.Resources.MergedDictionaries.Add(themeResources);
        if (mode is "providers" or "browser") { themeResources.BeginInit(); themeResources.EndInit(); }
        App.Resources.MergedDictionaries.Add(new ModernWpf.Controls.XamlControlsResources());
        App.MainWindow = new Window { ShowActivated = false, ShowInTaskbar = false };
        App.Exit += (_, _) =>
        {
            if (mode == "browser") { Row(new { Kind = "BrowserApplicationExit", DispatcherAlive = !App.Dispatcher.HasShutdownStarted, HooksAcquired = false }); return; }
            bool hooks = Completion(Mouse).IsCompletedSuccessfully && Completion(Keyboard).IsCompletedSuccessfully;
            Row(new { Kind = "ApplicationExit", DispatcherAlive = !App.Dispatcher.HasShutdownStarted, HooksCompleted = hooks, Closed });
            Require(hooks, "App shutdown did not wait for native hooks.");
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Write("managed-process-exit.json", new { Pid = Environment.ProcessId, Closed });
        _ = App.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            try
            {
                State = (AppState)typeof(App).GetProperty("AppState", Instance)!.GetValue(App)!;
                _ = State.Settings.FirstAsync().ToTask().GetAwaiter().GetResult();
                if (mode == "graceful")
                {
                    foreach (string owner in new[] { "startup", "about", "error", "message" }) Retention(owner, () => WindowCycle(owner));
                    Retention("native-hooks", HookCycle);
                }
                if (mode == "providers")
                {
                    Retention("workspace", WorkspaceCycle);
                    Retention("theme", ThemeCycle);
                }
                if (mode == "browser") { BrowserRetention(); App.Shutdown(); return; }
                Mouse = Resolve(provider, "FancyWM.Utilities.LowLevelMouseHook");
                Keyboard = Resolve(provider, "FancyWM.Utilities.LowLevelKeyboardHook");
                ObserveHook(Mouse); ObserveHook(Keyboard);
                var windows = new[] { CreateWindow("startup"), CreateWindow("about"), CreateWindow("error"), CreateWindow("message"), CreateWindow("settings") };
                var handles = windows.Select(Ensure).ToArray();
                FinalHandles = handles;
                foreach (var window in windows)
                    window.Closed += (_, _) =>
                    {
                        Closed++; Require(!App.Dispatcher.HasShutdownStarted, "Closed occurred after dispatcher stop.");
                        Row(new { Kind = "Closed", Type = window.GetType().Name, DispatcherAlive = true });
                    };
                if (mode is "graceful" or "providers" or "browser")
                {
                    typeof(App).GetMethod("Terminate", Instance)!.Invoke(App, null);
                }
                else
                {
                    Write("crash-ready.json", new { Pid = Environment.ProcessId, Desktop, Handles = handles.Select(h => h.ToInt64()).ToArray(),
                        HookThreads = new[] { HookThreadId(Mouse), HookThreadId(Keyboard) }, Closed, Mode = mode,
                        Source = typeof(App).Assembly.Location, ProductionSHA256 = Hash(typeof(App).Assembly.Location) });
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
                    timer.Tick += (_, _) =>
                    {
                        if (!File.Exists("crash.go")) return;
                        timer.Stop(); Row(new { Kind = "CrashEntered", Mode = mode, Closed, DispatcherAlive = !App.Dispatcher.HasShutdownStarted });
                        if (mode == "failfast") Environment.FailFast("FWM owned native teardown deliberate FailFast");
                        else
                        {
                            Require(TerminateProcess(Process.GetCurrentProcess().Handle, 0xE000F016), "Self TerminateProcess failed.");
                            throw new InvalidOperationException("TerminateProcess returned without termination.");
                        }
                    };
                    timer.Start();
                }
            }
            catch (Exception error)
            {
                Failure = error; Row(new { Kind = "Failure", Error = error.ToString() });
                foreach (Window window in App.Windows.Cast<Window>().ToArray()) window.Close();
                App.Shutdown(1);
            }
        });
        int code = App.Run();
        if (mode == "browser" && Failure == null)
        {
            provider.Dispose();
            Write("summary.json", new { Verdict = "PASS", Pid = Environment.ProcessId, Mode = mode,
                DispatcherStopped = App.Dispatcher.HasShutdownFinished, ProviderDisposed = true, Hwnds = FinalHandles.Select(h => h.ToInt64()).ToArray(),
                SurvivingHwnds = FinalHandles.Where(IsWindow).Select(h => h.ToInt64()).ToArray(), HooksAcquired = false, FullStartupGraph = false });
            Log.Dispose(); return code;
        }
        if (mode is "graceful" or "providers" or "browser" && Failure == null)
        {
            Require(Closed == 5, "Graceful close coverage.");
            Require(FinalHandles.All(h => !IsWindow(h)), "Graceful HWND survived Application shutdown.");
            foreach (object hook in new[] { Mouse, Keyboard }) AssertHookStopped(hook);
            var termination = (TaskCompletionSource)Field(App, "m_termination")!;
            Require(termination.Task.IsCompletedSuccessfully, "App termination incomplete.");
            provider.Dispose();
            Write("summary.json", new { Verdict = "PASS", Pid = Environment.ProcessId, Mode = mode, Closed,
                DispatcherStopped = App.Dispatcher.HasShutdownFinished, HooksCompleted = true, ProviderDisposed = true,
                Hwnds = FinalHandles.Select(h => h.ToInt64()).ToArray(), SurvivingHwnds = FinalHandles.Where(IsWindow).Select(h => h.ToInt64()).ToArray(),
                ProductionSHA256 = Hash(typeof(App).Assembly.Location), StageAccepted = false,
                FullStartupGraph = false, PerformanceClaim = false, LedgerAppended = false });
        }
        Log.Dispose();
        return Failure == null ? code : 1;
    }

    private static object Resolve(ServiceProvider provider, string name) => provider.GetRequiredService(typeof(App).Assembly.GetType(name)!);
    private static Window CreateWindow(string owner) => owner switch
    {
        "startup" => new StartupWindow(new SettingsViewModel(State.Settings)) { ShowActivated = false, ShowInTaskbar = false },
        "settings" => new SettingsWindow(new SettingsViewModel(State.Settings)) { ShowActivated = false, ShowInTaskbar = false },
        "about" => new AboutWindow { ShowActivated = false, ShowInTaskbar = false },
        "error" => new ErrorMessageBox { ShowActivated = false, ShowInTaskbar = false, IsRestartEnabled = false, IsSubmitLogEnabled = false, ExceptionObject = new InvalidOperationException("owned diagnostic") },
        "message" => new FancyWM.Windows.MessageBox { ShowActivated = false, ShowInTaskbar = false, Message = "owned native lifetime" },
        _ => throw new ArgumentException(owner)
    };
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] WindowCycle(string owner)
    {
        var window = CreateWindow(owner); IntPtr handle = Ensure(window); var source = HwndSource.FromHwnd(handle)!;
        var refs = new List<WeakReference> { new(window), new(source) };
        if (window.DataContext != null && !ReferenceEquals(window.DataContext, window)) refs.Add(new(window.DataContext));
        int closed = 0; window.Closed += (_, _) => { closed++; window.Close(); };
        window.Close(); window.Close(); Drain();
        Require(closed == 1 && !IsWindow(handle) && source.IsDisposed && HwndSource.FromHwnd(handle) == null, "Native auxiliary close failed.");
        Require(!App.Windows.Cast<Window>().Contains(window), "Application retains closed auxiliary window.");
        Row(new { Kind = "WindowLifetime", Owner = owner, Hwnd = handle.ToInt64(), Closed = closed, Destroyed = true, SourceDisposed = source.IsDisposed });
        return refs.ToArray();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] HookCycle()
    {
        var refs = new List<WeakReference>();
        foreach (string name in new[] { "FancyWM.Utilities.LowLevelMouseHook", "FancyWM.Utilities.LowLevelKeyboardHook" })
        {
            var hook = Activator.CreateInstance(typeof(App).Assembly.GetType(name)!)!;
            try
            {
                ObserveHook(hook); refs.Add(new(hook)); refs.Add(new(Field(hook, "m_hookThread")!));
            }
            finally { ((IDisposable)hook).Dispose(); AssertHookStopped(hook); }
        }
        return refs.ToArray();
    }
    private static void ObserveHook(object hook)
    {
        Wait(() => NativeHook(hook) != IntPtr.Zero || Completion(hook).IsCompleted, "Native hook registration");
        Require(NativeHook(hook) != IntPtr.Zero && !Completion(hook).IsCompleted, "Hook install failed.");
        uint tid = HookThreadId(hook); Require(tid != 0 && DesktopName(tid) == Desktop, "Hook escaped the owned desktop.");
        Row(new { Kind = "HookAcquired", Type = hook.GetType().Name, Hook = NativeHook(hook).ToInt64(), Thread = tid, Desktop });
    }
    private static void AssertHookStopped(object hook)
    {
        Wait(() => Completion(hook).IsCompleted, "Native hook completion"); Completion(hook).GetAwaiter().GetResult();
        Require(((Thread)Field(hook, "m_hookThread")!).Join(TimeSpan.FromSeconds(5)), "Native hook thread did not exit.");
        Require(NativeHook(hook) == IntPtr.Zero && HookThreadId(hook) == 0, "Hook owner still published.");
        Row(new { Kind = "HookStopped", Type = hook.GetType().Name, Hook = 0, Thread = 0, Completed = true });
    }
    private static IntPtr NativeHook(object hook) => (IntPtr)Field(Field(hook, "m_hHook")!, "Value")!;
    private static uint HookThreadId(object hook) => (uint)Field(Field(hook, "m_lifetime")!, "m_threadId")!;
    private static Task Completion(object hook) => hook == null ? Task.CompletedTask : (Task)hook.GetType().GetProperty("Completion", Instance)!.GetValue(hook)!;
    private static object? Field(object owner, string name) => owner.GetType().GetField(name, Instance)!.GetValue(owner);
    private static void Retention(string owner, Func<WeakReference[]> create)
    {
        for (int i = 0; i < 10; i++) create();
        Settle(); if (owner == "help-browser") SettleFrameworkViews();
        using var process = Process.GetCurrentProcess();
        int initialUser = GetGuiResources(process.Handle, 1), initialGdi = GetGuiResources(process.Handle, 0);
        var refs = new List<WeakReference>();
        for (int epoch = 1; epoch <= 2; epoch++)
        {
            for (int cycle = 0; cycle < 50; cycle++) refs.AddRange(create());
            Settle();
            if (owner == "help-browser")
            {
                Row(new { Kind = "BeforeFrameworkMaintenance", Epoch = epoch, Cycles = epoch * 50, Live = refs.Count(r => r.IsAlive),
                    LiveIdentities = refs.Select((r, i) => new { Reference = r, Index = i }).Where(r => r.Reference.IsAlive)
                        .Select(r => new { r.Index, Type = r.Reference.Target?.GetType().FullName }).ToArray(), DispatcherAlive = !App.Dispatcher.HasShutdownStarted });
                SettleFrameworkViews();
            }
            int live = refs.Count(r => r.IsAlive), user = GetGuiResources(process.Handle, 1), gdi = GetGuiResources(process.Handle, 0);
            Row(new { Kind = "Retention", Owner = owner, Epoch = epoch, Cycles = epoch * 50, References = refs.Count, Live = live,
                LiveIdentities = refs.Select((r, i) => new { Reference = r, Index = i }).Where(r => r.Reference.IsAlive)
                    .Select(r => new { r.Index, Type = r.Reference.Target?.GetType().FullName }).ToArray(),
                InitialUser = initialUser, User = user, InitialGdi = initialGdi, Gdi = gdi, DispatcherAlive = !App.Dispatcher.HasShutdownStarted });
            if (owner == "help-browser" && live != 0) CaptureRetentionDump();
            Require(live == 0 && initialUser == user && initialGdi == gdi, "Native owner retention/resource growth: " + owner);
        }
    }
    private static void Settle()
    {
        var delay = Task.Delay(1200); Wait(() => delay.IsCompleted, "settling");
        for (int i = 0; i < 3; i++) { Drain(); GC.Collect(); GC.WaitForPendingFinalizers(); } Drain();
    }
    private static IntPtr Ensure(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        uint tid = GetWindowThreadProcessId(hwnd, out uint pid);
        Require(hwnd != IntPtr.Zero && IsWindow(hwnd) && pid == Environment.ProcessId && tid == GetCurrentThreadId(), "Foreign/invalid native HWND.");
        return hwnd;
    }
    private static void Wait(Func<bool> predicate, string operation)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate() && clock.Elapsed < TimeSpan.FromSeconds(10)) { Drain(); Thread.Sleep(1); }
        Require(predicate(), "Timed out: " + operation);
    }
    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Row(object value) { Log.WriteLine(JsonSerializer.Serialize(value)); }
    private static void Write(string name, object value, JsonSerializerOptions? options = null)
    {
        using var f = new FileStream(Path.Combine(Output, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(f, value, options ?? Json); f.Flush(true);
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string DesktopName(uint tid)
    {
        var text = new StringBuilder(256);
        Require(GetUserObjectInformation(GetThreadDesktop(tid), 2, text, text.Capacity * 2, out _), "Cannot identify native desktop.");
        return text.ToString();
    }
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint tid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetUserObjectInformationW")]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder text, int bytes, out int needed);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, uint kind);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint flags);
}
