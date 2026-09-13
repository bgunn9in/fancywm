using System.Collections;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FancyWM;
using FancyWM.Layouts.Tiling;
using FancyWM.Models;
using FancyWM.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;
using Rectangle = WinMan.Rectangle;
using Application = System.Windows.Application;

internal static partial class Program
{
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const string Exclusion = "^(?!FancyWM\\.Perf010Targets$).*";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    private static readonly List<object> Observations = [];
    private static App App = null!;
    private static AppState State = null!;
    private static TargetClient? Targets;
    private static Exception? Failure;
    private static string Output = "";
    private static int Iterations;
    private static bool Validation;
    private static object? MainWindowObject;

    [STAThread]
    private static int MainEntry(string[] args)
    {
        if (args.Length != 4 || Directory.Exists(args[0])) throw new ArgumentException("Usage: <fresh-output> <target-exe> <iterations> <validation|measurement>");
        Output = Path.GetFullPath(args[0]); var targetExe = Path.GetFullPath(args[1]);
        if (!AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), new(-4)))
            throw new InvalidOperationException("Launch the archived apphost EXE with the production PerMonitorV2 manifest; the current DPI context differs.");
        Iterations = int.Parse(args[2]); Validation = args[3] == "validation";
        if (args[3] is not ("validation" or "measurement")) throw new ArgumentException("Unknown run mode.");
        if (Iterations is < 1 or > 20 || !File.Exists(targetExe)) throw new ArgumentException("Invalid controls.");
        Directory.CreateDirectory(Output); Directory.SetCurrentDirectory(Output);
        var options = (JsonSerializerOptions)typeof(AppState).GetMethod("CreateSettingsJsonSerializerOptions", Static)!.Invoke(null, null)!;
        var settings = new Settings { AutoFloatNewWindows = true, ProcessIgnoreList = [Exclusion], AutoSplitCount = 5,
            ShowStartupWindow = false, CheckForUpdates = false, RemindToRateReview = false,
            ShowContextHints = false, SoundOnFailure = false, NotifyVirtualDesktopServiceIncompatibility = false };
        if (Validation) settings = settings with { Keybindings = ValidationBindings() };
        File.WriteAllText("settings.json", JsonSerializer.Serialize(settings, options));
        var services = new ServiceCollection(); var argumentsType = typeof(Startup).GetNestedType("Arguments", BindingFlags.NonPublic)!;
        var arguments = Activator.CreateInstance(argumentsType, true)!;
        argumentsType.GetProperty("LogLevel")!.SetValue(arguments, LogEventLevel.Information);
        argumentsType.GetProperty("HasConsole")!.SetValue(arguments, false);
        typeof(Startup).GetMethod("ConfigureServices", Static)!.Invoke(null, [services, arguments]);
        using var provider = services.BuildServiceProvider();
        typeof(Startup).GetMethod("ConfigureThreadPriority", Static)!.Invoke(null, [provider.GetRequiredService<ILogger>()]);
        App = new App(provider); App.InitializeComponent();
        // A managed EXE has an entry assembly, so WPF forbids assigning
        // Application.ResourceAssembly. Resolve the unchanged production BAML
        // through its assembly-qualified component URI instead.
        // Match InitializeComponent's generated URI, including assembly version
        // and resource casing, so WPF recognizes the in-progress BAML load.
        App.StartupUri = new Uri($"/FancyWM;V{typeof(MainWindow).Assembly.GetName().Version};component/mainwindow.xaml", UriKind.Relative);
        App.DispatcherUnhandledException += (_, error) =>
        {
            Failure = Failure is null ? error.Exception : new AggregateException(Failure, error.Exception);
            error.Handled = true;
            typeof(App).GetMethod("Terminate", Instance)!.Invoke(App, null);
        };
        if (Validation) AppDomain.CurrentDomain.FirstChanceException += ObserveValidationException;
        App.Startup += (_, _) => App.Dispatcher.BeginInvoke(DispatcherPriority.Background, async () =>
        {
            try
            {
                await WaitUntil(() => App.MainWindow is MainWindow && Field(App.MainWindow, "m_tiling") is not null, "production MainWindow initialization");
                MainWindowObject = App.MainWindow; State = (AppState)typeof(App).GetProperty("AppState", Instance)!.GetValue(App)!;
                var tiling = Field(MainWindowObject, "m_tiling")!;
                await WaitUntil(() => ((IEnumerable)Property(tiling, "ExclusionMatchers")!).Cast<object>()
                    .Any(x => x.GetType().Name == "ByProcessNameMatcher" && (string?)Property(x, "ProcessName") == Exclusion), "owned-process exclusion publication");
                await Idle(0);
                await State.Settings.SaveAsync(s => s with { AutoFloatNewWindows = false });
                Targets = new TargetClient(targetExe, Path.Combine(Output, "targets"));
                await Run();
            }
            catch (Exception error) { Failure = Failure is null ? error : new AggregateException(Failure, error); }
            finally
            {
                try { if (Targets is not null && !PendingShutdown) await Targets.Command(new { Operation = "close-all" }); }
                catch (Exception error) { Failure = new AggregateException(Failure ?? error, error); }
                typeof(App).GetMethod("Terminate", Instance)!.Invoke(App, null);
            }
        });
        int exitCode;
        try { exitCode = App.Run(); }
        finally
        {
            if (Validation)
            {
                AppDomain.CurrentDomain.FirstChanceException -= ObserveValidationException;
                File.WriteAllText("validation-exceptions.json", JsonSerializer.Serialize(ValidationExceptions.ToArray(), Json));
                if (ValidationExceptionCount > 256) Failure = new InvalidOperationException("Validation exception observation capacity exceeded.");
            }
            try { if (PendingShutdown) VerifyPendingShutdown(); }
            catch (Exception error) { Failure = new AggregateException(Failure ?? error, error); }
            try { Targets?.Dispose(); } catch (Exception error) { Failure = new AggregateException(Failure ?? error, error); }
            var termination = Field(App, "m_termination") as TaskCompletionSource;
            var shutdown = MainWindowObject is null ? null : Property(MainWindowObject, "ShutdownCompletion") as Task;
            File.WriteAllText("fullapp-summary.json", JsonSerializer.Serialize(new {
                ProcessId = Environment.ProcessId, StartedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime(),
                FinishedUtc = DateTime.UtcNow, Stopwatch.Frequency, Iterations, Validation,
                ProcessPath = Environment.ProcessPath, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Runtime = RuntimeInformation.FrameworkDescription, EtwProviderEnabled = FullAppEvents.Log.IsEnabled(),
                OwnershipWaits,
                DispatcherNativeThreadId = GetCurrentThreadId(),
                PerMonitorV2 = AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), new(-4)),
                Assemblies = new[] { typeof(App).Assembly, typeof(WinMan.Windows.Win32Window).Assembly,
                    typeof(WinMan.IWindow).Assembly, typeof(Program).Assembly }
                    .Select(assembly => new { assembly.Location, SHA256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) }).ToArray(),
                RealApplication = typeof(App).Assembly.Location, ActualAnimationDurationMs = 100,
                FixtureAutoSplitCount = 5, FixtureLayoutPreparation = "SettledEvenFlexAndTargetOrder",
                TargetMinimumSize = new { Width = 48, Height = 48 },
                TargetStyle = "Captionless WinForms with native SIZEBOX/MINIMIZEBOX/MAXIMIZEBOX; no fabricated WM_GETMINMAXINFO result",
                IsPackaged = false, IsolatedSettingsPath = State?.Settings.FullPath,
                ProductionExclusionPattern = Exclusion, Observations,
                MainWindowShutdownCompleted = shutdown?.IsCompletedSuccessfully,
                AppTerminationCompleted = termination?.Task.IsCompletedSuccessfully,
                TargetProcessExited = Targets?.Exited, Error = Failure?.ToString(),
                StageAccepted = false,
                Limits = "Instrumented host of production App/MainWindow/DI in an isolated working directory. Layout idle is an observation, not animation Task completion. Physical presentation requires subsequent raw DWM/Dxg verification. Native DPI is the actual display DPI; no untested DPI configuration is claimed."
            }, Json));
        }
        if (Failure is not null) { Console.Error.WriteLine(Failure); return 1; }
        return exitCode;
    }

    private static async Task Run()
    {
        int scenario = 0;
        foreach (int count in new[] { 1, 10, 50 })
        {
            var created = await Targets!.Command(new { Operation = "create", Count = count });
            await Idle(count);
            await PreparePanelAllocations(count, created);
            Observations.Add(new { Kind = "Created", Count = count, Targets = created, Geometry = Geometry() });
            for (int iteration = -1; iteration <= Iterations; iteration++)
            {
                int id = ++scenario;
                await Targets.Command(new { Operation = "scenario", Id = id });
                await Task.Delay(100);
                var process = Process.GetCurrentProcess(); process.Refresh();
                double cpuStart = process.TotalProcessorTime.TotalMilliseconds;
                long start = Stopwatch.GetTimestamp(); FullAppEvents.Log.Boundary(id, 1, start);
                var save = State.Settings.SaveAsync(s => s with { WindowPadding = (iteration & 1) == 0 ? 4 : 12 });
                long published = Stopwatch.GetTimestamp(); FullAppEvents.Log.Boundary(id, 2, published);
                await Idle(count);
                long idle = Stopwatch.GetTimestamp(); FullAppEvents.Log.Boundary(id, 3, idle);
                // The real animation Task can complete before the HWND owner
                // applies its queued SetWindowPos calls. Keep layout-idle and
                // independently observed final native geometry separate.
                var geometry = await VerifiedGeometry(count);
                long native = Stopwatch.GetTimestamp(); FullAppEvents.Log.Boundary(id, 4, native);
                Marshal.ThrowExceptionForHR(DwmFlush());
                long flushed = Stopwatch.GetTimestamp(); FullAppEvents.Log.Boundary(id, 5, flushed);
                process.Refresh(); double cpu = process.TotalProcessorTime.TotalMilliseconds - cpuStart;
                Observations.Add(new { Kind = "Transition", Scenario = id, Count = count, Iteration = iteration,
                    Measured = iteration > 0, StartQpc = start, SettingsPublishedQpc = published,
                    LayoutIdleObservedQpc = idle, NativeVerifiedQpc = native, DwmFlushedQpc = flushed,
                    ProcessCpuMs = cpu, Geometry = geometry });
                await save;
            }
            if (Validation && count == 10)
            {
                try { await ValidateInteractiveFocus(); }
                catch (Exception error)
                {
                    Failure = Failure is null ? error : new AggregateException(Failure, error);
                    Observations.Add(new { Kind = "InteractiveFailure", Error = error.ToString() });
                }
                await ValidateConcurrentSettings();
                await ValidateNativeInterruption(++scenario, "minimize", 9);
                await Targets.Command(new { Operation = "restore", Index = 9 }); await Idle(10);
                var restoredGeometry = await VerifiedGeometry(10);
                Observations.Add(new { Kind = "NativeRestoreVerified", Passed = true, Count = 10,
                    Qpc = Stopwatch.GetTimestamp(), Geometry = restoredGeometry });
                await ValidateNativeInterruption(++scenario, "close", 9);
            }
            if (Validation && count == 50)
            {
                await PreparePendingShutdown(++scenario);
                return;
            }
            await Targets.Command(new { Operation = "close-all" }); await Idle(0);
        }
    }

    private static object? Field(object value, string name) => value.GetType().GetField(name, Instance)?.GetValue(value);
    private static object? Property(object value, string name) => value.GetType().GetProperty(name, Instance)?.GetValue(value);
    private static object[] TilingServices()
    {
        var tiling = Field(MainWindowObject!, "m_tiling")!;
        if (tiling.GetType().Name == "TilingService") return [tiling];
        return ((IDictionary)Field(tiling, "m_tilingServices")!).Values.Cast<object>().ToArray();
    }
    private static WindowNode[] Nodes(object tiling)
    {
        using (BackendLock(tiling).EnterScope())
        {
            var backend = Field(tiling, "m_backend")!;
            return ((IEnumerable)Property(backend, "Trees")!).Cast<DesktopTree>()
                .SelectMany(t => t.Root?.Windows ?? []).ToArray();
        }
    }
    private static System.Threading.Lock BackendLock(object tiling)
    {
        var value = Field(tiling, "m_backendLock")!;
        return value as System.Threading.Lock ?? (System.Threading.Lock)Field(value, "m_lock")!;
    }
    private static async Task Idle(int count, CancellationToken cancellationToken = default)
    {
        int consecutive = 0;
        await WaitUntil(() =>
        {
            var services = TilingServices(); var nodes = services.SelectMany(Nodes).ToArray();
            var owners = nodes.Select(n => GetWindowProcess(n.WindowReference.Handle)).ToArray();
            if (!WindowOwnersReady(owners, Targets?.ProcessId))
            {
                OwnershipWaits.Add(new { Qpc = Stopwatch.GetTimestamp(), ExpectedCount = count,
                    Handles = nodes.Select(n => n.WindowReference.Handle.ToInt64()).ToArray(), ProcessIds = owners });
                consecutive = 0;
                return false;
            }
            bool idle = nodes.Length == count && services.All(s =>
            {
                var queue = Field(s, "m_layoutInvalidations")!;
                lock (Field(queue, "m_lock")!)
                    return !(bool)Field(queue, "m_dirty")! && !(bool)Field(queue, "m_scheduled")!
                        && (int)Field(queue, "m_activeCallbacks")! == 0 && !(bool)Field(s, "m_dirty")!
                        && (int)Property(Field(s, "m_frozen")!, "Count")! == 0;
            });
            consecutive = idle ? consecutive + 1 : 0;
            return consecutive >= 3;
        }, $"layout idle with {count} owned windows", cancellationToken);
    }
    private sealed record GeometryRow(long Hwnd, uint OwnerThreadId, uint Dpi, Rectangle Expected, Rectangle Actual,
        bool NativeEqualsExpected, Rectangle ExtendedFrame, Rectangle ClientScreen, bool Visible, int Cloaked);
    private static async Task<GeometryRow[]> VerifiedGeometry(int count)
    {
        GeometryRow[] geometry = [];
        try
        {
            await WaitUntil(() =>
            {
                geometry = Geometry();
                return geometry.Length == count && geometry.All(row => row.NativeEqualsExpected);
            }, $"final native geometry for {count} owned windows");
        }
        catch
        {
            Observations.Add(new { Kind = "NativeGeometryFailure", Count = count, Qpc = Stopwatch.GetTimestamp(), Geometry = geometry });
            throw;
        }
        return geometry;
    }
    private static GeometryRow[] Geometry() => TilingServices().SelectMany(Nodes).Select(n =>
    {
        var window = n.WindowReference; var rect = n.ComputedRectangle; var margin = window.FrameMargins;
        var expected = new Rectangle(rect.Left - margin.Left, rect.Top - margin.Top, rect.Right + margin.Right, rect.Bottom + margin.Bottom);
        if (!GetWindowRect(window.Handle, out var actualRect)) throw new System.ComponentModel.Win32Exception();
        var actual = new Rectangle(actualRect.Left, actualRect.Top, actualRect.Right, actualRect.Bottom);
        Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(window.Handle, 9, out Rect frame, Marshal.SizeOf<Rect>()));
        Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(window.Handle, 14, out int cloaked, sizeof(int)));
        if (!GetClientRect(window.Handle, out Rect client)) throw new System.ComponentModel.Win32Exception();
        var origin = new NativePoint();
        if (!ClientToScreen(window.Handle, ref origin)) throw new System.ComponentModel.Win32Exception();
        return new GeometryRow(window.Handle.ToInt64(), GetWindowThreadProcessId(window.Handle, out _), GetDpiForWindow(window.Handle), expected, actual, expected == actual,
            new(frame.Left, frame.Top, frame.Right, frame.Bottom),
            new(origin.X, origin.Y, origin.X + client.Right, origin.Y + client.Bottom), IsWindowVisible(window.Handle), cloaked);
    }).ToArray();
    private static async Task WaitUntil(Func<bool> ready, string reason, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ready()) return;
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException(reason);
            await Task.Delay(2, cancellationToken);
        }
    }
    private static uint GetWindowProcess(nint hwnd) { GetWindowThreadProcessId(hwnd, out uint pid); return pid; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(nint left, nint right);

    [STAThread]
    private static int Main(string[] args) => MainEntry(args);

    private sealed class TargetClient : IDisposable
    {
        private readonly Process m_process;
        private readonly StreamWriter m_log;
        private readonly Task<string> m_stderr;
        public int ProcessId => m_process.Id;
        public bool Exited { get; private set; }
        public TargetClient(string executable, string output)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(output);
            m_log = new StreamWriter(new FileStream(Path.Combine(Output, "target-protocol.jsonl"), FileMode.CreateNew));
            m_process = Process.Start(start)!; m_stderr = m_process.StandardError.ReadToEndAsync();
            string line = m_process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult()!;
            m_log.WriteLine(line); m_log.Flush();
            if (!JsonDocument.Parse(line).RootElement.GetProperty("Ready").GetBoolean()) throw new InvalidOperationException(line);
        }
        public async Task<JsonElement> Command(object command)
        {
            string request = JsonSerializer.Serialize(command); m_log.WriteLine(request); m_log.Flush();
            await m_process.StandardInput.WriteLineAsync(request); await m_process.StandardInput.FlushAsync();
            string response = (await m_process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)))!;
            m_log.WriteLine(response); m_log.Flush(); using var json = JsonDocument.Parse(response);
            if (!json.RootElement.GetProperty("Success").GetBoolean()) throw new InvalidOperationException(response);
            return json.RootElement.Clone();
        }
        public void Dispose()
        {
            m_process.StandardInput.Close();
            if (!m_process.WaitForExit(20000)) { m_process.Kill(true); m_process.WaitForExit(); throw new TimeoutException("Owned target process failed to stop."); }
            Exited = true; File.WriteAllText(Path.Combine(Output, "target.stderr"), m_stderr.GetAwaiter().GetResult());
            m_log.Dispose(); m_process.Dispose();
        }
    }
}

[EventSource(Name = "FancyWM-Perf010-FullApp", Guid = "a5bbdb9a-4198-4e10-b6d9-8a50ef9e7656")]
internal sealed class FullAppEvents : EventSource
{
    public static readonly FullAppEvents Log = new();
    [Event(1, Level = EventLevel.Informational)] public void Boundary(long scenario, long phase, long qpc) => WriteEvent(1, scenario, phase, qpc);
}
