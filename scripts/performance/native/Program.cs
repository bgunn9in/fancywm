using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FancyWM.AnimationNativeHarness;
using WinMan;
using WinMan.Windows;
using Forms = System.Windows.Forms;

if (args.Length is < 2 or > 3 || !int.TryParse(args[1], out int iterations) || iterations is < 1 or > 20)
    throw new ArgumentException("Usage: FancyWM.AnimationNativeHarness <new-output-directory> <iterations 1..20> [owner-limit 1|5|50]");
int ownerLimit = args.Length == 3 ? int.Parse(args[2]) : 1;
if (ownerLimit is not (1 or 5 or 50)) throw new ArgumentException("Owner limit must be 1, 5 or 50.");
if (Directory.Exists(args[0])) throw new IOException("Output directory already exists.");
Directory.CreateDirectory(args[0]);
if (!Environment.Is64BitProcess) throw new InvalidOperationException("An x64 process is required.");
Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
var recording = new Recording();
var production = Assembly.Load("FancyWM");
var animationType = production.GetType("FancyWM.Utilities.AnimationThread", true)!;
var compositor = animationType.GetNestedType("Compositor", BindingFlags.NonPublic)!;
var nativeWait = compositor.GetMethod("Wait", BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate<Func<uint, uint>>();
var nativeBoost = compositor.GetMethod("BoostClock", BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate<Func<bool, int>>();
var targetType = production.GetType("FancyWM.Utilities.TransitionTarget", true)!;
var groupType = production.GetType("FancyWM.Utilities.TransitionTargetGroup", true)!;
var transitionMethod = groupType.GetMethod("PerformSmoothTransitionAsync")!;
const int durationMs = 250, warmups = 2;
int targetFrameRate = 0;
var process = Process.GetCurrentProcess();
var results = new List<object>();
var displays = new List<object>();
int transitionId = 0;
bool cleanupPassed = false;
string? runError = null;

try
{
    foreach (int count in new[] { 1, 10, 50 })
    {
        using var windows = new NativeWindows(count, recording, ownerLimit);
        using var workspace = new Win32Workspace();
        workspace.Open();
        int refreshRate = workspace.DisplayManager.Displays.Max(display => display.RefreshRate);
        if (refreshRate <= 0 || (targetFrameRate != 0 && targetFrameRate != refreshRate))
            throw new InvalidOperationException("The production display refresh rate changed or is invalid.");
        targetFrameRate = refreshRate; // Same selection as MainWindow's production constructor.
        var actual = windows.Handles.Select(handle => (Win32Window)workspace.UnsafeCreateFromHandle(handle)).ToArray();
        foreach (var window in actual)
        {
            window.RaisePositionChangeEnd(); // Real native state initialization, outside samples.
            if (window.State != WindowState.Restored || !window.CanMove)
                throw new InvalidOperationException("An owned target is not movable/restored.");
        }
        var observed = actual.Select((window, index) => new ObservedWindow(window, recording, index)).ToArray();
        displays.Add(new { Count = count, windows.Dpi, windows.WorkArea,
            Handles = windows.Handles.Select(handle => handle.ToInt64()).ToArray(),
            OwnerLimit = ownerLimit, windows.OwnerThreadIds, windows.OwnerProcessIds,
            windows.ExtendedStylesBeforeVisibilityFix,
            windows.Originals, windows.Destinations, ForegroundBefore = windows.ForegroundBefore.ToInt64() });

        Func<uint, uint> wait = timeout =>
        {
            long start = Stopwatch.GetTimestamp();
            uint result = nativeWait(timeout);
            Interlocked.Increment(ref recording.Frame);
            recording.Add(Recording.Operation.Frame, start, x: unchecked((int)result));
            if (unchecked((int)result) < 0)
                throw new InvalidOperationException($"Native compositor clock unavailable: NTSTATUS=0x{result:X8}. The display/cadence prerequisite is not satisfied.");
            return result;
        };
        Func<bool, int> boost = enabled =>
        {
            long start = Stopwatch.GetTimestamp();
            int result = nativeBoost(enabled);
            recording.Add(Recording.Operation.Boost, start, x: enabled ? 1 : 0, y: result);
            return result;
        };
        var animation = (IDisposable)Activator.CreateInstance(animationType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [targetFrameRate, boost, wait], null)!;
        try
        {
            for (int iteration = 0; iteration < warmups + iterations; iteration++)
            {
                int id = ++transitionId;
                var expected = (iteration & 1) == 0 ? windows.Destinations : windows.Originals;
                var targets = Array.CreateInstance(targetType, count);
                for (int i = 0; i < count; i++)
                    targets.SetValue(Activator.CreateInstance(targetType, observed[i], actual[i].Position, expected[i]), i);
                var group = Activator.CreateInstance(groupType, animation, targets)!;
                windows.PrepareVisibility();
                await Task.Delay(100);
                var visibilityBefore = windows.VerifyVisibility();
                process.Refresh();
                double cpuStart = process.TotalProcessorTime.TotalMilliseconds;
                long allocatedStart = GC.GetTotalAllocatedBytes();
                Volatile.Write(ref recording.Transition, id);
                long start = Stopwatch.GetTimestamp();
                AnimationEvents.Log.Boundary(id, 1, start);
                var task = (Task)transitionMethod.Invoke(group, [TimeSpan.FromMilliseconds(durationMs)])!;
                await task.WaitAsync(TimeSpan.FromSeconds(10));
                long completed = Stopwatch.GetTimestamp();
                AnimationEvents.Log.Boundary(id, 2, completed);
                process.Refresh();
                double cpu = process.TotalProcessorTime.TotalMilliseconds - cpuStart;
                long allocated = GC.GetTotalAllocatedBytes() - allocatedStart;
                await windows.VerifyAsync(expected);
                long nativeVerified = Stopwatch.GetTimestamp();
                AnimationEvents.Log.Boundary(id, 3, nativeVerified);
                process.Refresh();
                double nativeCpu = process.TotalProcessorTime.TotalMilliseconds - cpuStart;
                Marshal.ThrowExceptionForHR(NativeWindows.DwmFlush());
                long flushed = Stopwatch.GetTimestamp();
                AnimationEvents.Log.Boundary(id, 4, flushed);
                process.Refresh();
                double flushedCpu = process.TotalProcessorTime.TotalMilliseconds - cpuStart;
                var visibilityAfter = windows.VerifyVisibility();
                recording.ThrowIfCapacityExceeded();
                results.Add(new { Transition = id, Count = count, Iteration = iteration - warmups + 1,
                    Measured = iteration >= warmups, StartQpc = start, CompletedQpc = completed,
                    NativeVerifiedQpc = nativeVerified, DwmFlushedQpc = flushed,
                    CompletionMs = (completed - start) * 1000.0 / Stopwatch.Frequency,
                    NativeVerifiedMs = (nativeVerified - start) * 1000.0 / Stopwatch.Frequency,
                    DwmFlushBoundaryMs = (flushed - start) * 1000.0 / Stopwatch.Frequency,
                    CompletionProcessCpuMs = cpu, NativeVerifiedProcessCpuMs = nativeCpu,
                    ProcessCpuMs = flushedCpu, CompletionProcessAllocatedBytes = allocated,
                    VisibilityBefore = visibilityBefore, VisibilityAfter = visibilityAfter,
                    NativeRectanglesPassed = true });
                Volatile.Write(ref recording.Transition, 0);
            }
        }
        finally
        {
            animation.Dispose();
            var completion = (Task)animationType.GetProperty("Completion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(animation)!;
            await completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
    cleanupPassed = true;
}
catch (Exception error)
{
    runError = error.ToString();
    throw;
}
finally
{
    recording.Save(Path.Combine(args[0], "calls.csv"));
    var assemblies = new[] { production, typeof(Win32Window).Assembly, typeof(IWindow).Assembly, Assembly.GetExecutingAssembly() }
        .Select(assembly => new { assembly.Location, SHA256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) }).ToArray();
    var summary = new { ProcessId = Environment.ProcessId, StartedUtc = process.StartTime.ToUniversalTime(),
        FinishedUtc = DateTime.UtcNow, StopwatchFrequency = Stopwatch.Frequency,
        Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Runtime = RuntimeInformation.FrameworkDescription,
        DurationMs = durationMs, Warmups = warmups, Iterations = iterations, TargetFrameRate = targetFrameRate,
        EtwProviderEnabled = AnimationEvents.Log.IsEnabled(), CleanupPassed = cleanupPassed,
        NativeTargetOwnerLimit = ownerLimit,
        RunError = runError, RecordingAttemptedSamples = recording.AttemptedSamples,
        RecordingCapacityExceeded = recording.CapacityExceeded,
        VisibilityProtocol = "native-topmost-and-five-point-hit-tests",
        Assemblies = assemblies, Displays = displays, Transitions = results,
        Limitations = "Instrumented native HWND fixture running the unmodified production assemblies; process CPU includes fixture and WinMan workspace, not the complete FancyWM application. PositionRead is the real provider cache read with IsWindow liveness, not a GetWindowRect count. Frame timestamps span the real compositor wait and precede UpdateFrame. DwmFlush is a separately timed API boundary, not per-window physical presentation latency. No A/B speedup or GPU/energy claim." };
    File.WriteAllText(Path.Combine(args[0], "summary.json"), JsonSerializer.Serialize(summary,
        new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
}
Console.WriteLine($"Native run passed: {iterations * 3} measured transitions; owned HWNDs and animation workers closed.");
