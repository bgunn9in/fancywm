internal static partial class Program
{
    private static string? HeapTraceSession;
    private static bool HeapTraceStarted;
    private static int HeapTraceCommand;
    private static uint HeapCommand(string executable, params string[] arguments)
    {
        string name = "heap-command-" + HeapTraceCommand++;
        using var stdout = new System.IO.FileStream(System.IO.Path.Combine(Output, name + ".stdout"), System.IO.FileMode.CreateNew);
        using var stderr = new System.IO.FileStream(System.IO.Path.Combine(Output, name + ".stderr"), System.IO.FileMode.CreateNew);
        var start = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        long before = System.Diagnostics.Stopwatch.GetTimestamp();
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.BaseStream.CopyToAsync(stdout);
        var error = process.StandardError.BaseStream.CopyToAsync(stderr);
        if (!process.WaitForExit(60000)) { process.Kill(); process.WaitForExit(); throw new System.TimeoutException("Owned heap command timed out."); }
        System.Threading.Tasks.Task.WaitAll(output, error);
        uint code = unchecked((uint)process.ExitCode);
        Write(name + ".json", new { Executable = executable, Arguments = arguments, ProcessId = process.Id, ExitCode = code, ExitHex = "0x" + code.ToString("X8"), QpcBegin = before, QpcEnd = System.Diagnostics.Stopwatch.GetTimestamp() });
        return code;
    }
    private static void StartHeapTrace()
    {
        if (System.Environment.GetEnvironmentVariable("FWM_NATIVE_HEAP_TRACE") != "1") return;
        Require(HeapTraceSession == null, "Heap trace already initialized.");
        string executable = System.Environment.GetEnvironmentVariable("FWM_NATIVE_HEAP_XPERF")!;
        HeapTraceSession = "FWM_OWNED_GRAPH_HEAP_" + System.Environment.ProcessId + "_" + System.Guid.NewGuid().ToString("N");
        Require(HeapCommand("logman", "query", HeapTraceSession, "-ets") == 0x80300002, "Own heap session name is not absent.");
        Write("heap-session.json", new { Pid = System.Environment.ProcessId, Session = HeapTraceSession, Executable = executable, OwnPidOnly = true, NoRegistryChange = true, AttachedAfterWarmup = true });
        string etl = System.IO.Path.Combine(Output, "heap.etl");
        Require(!System.IO.File.Exists(etl), "Heap ETL already exists.");
        uint code = HeapCommand(executable, "-start", HeapTraceSession, "-heap", "-Pids", System.Environment.ProcessId.ToString(), "-BufferSize", "1024", "-MinBuffers", "64", "-MaxBuffers", "64", "-stackwalk", "HeapAlloc+HeapRealloc", "-f", etl);
        HeapTraceStarted = code == 0;
        Row(new { Kind = "NativeHeapTraceStart", Session = HeapTraceSession, Result = code, Pid = System.Environment.ProcessId, DispatcherAlive = !System.Windows.Threading.Dispatcher.CurrentDispatcher.HasShutdownStarted, AttachedAfterWarmup = true });
        Require(HeapTraceStarted, "Native heap tracing failed.");
    }
    private static void StopHeapTrace()
    {
        if (!HeapTraceStarted) return;
        string executable = System.Environment.GetEnvironmentVariable("FWM_NATIVE_HEAP_XPERF")!;
        uint code = HeapCommand(executable, "-stop", HeapTraceSession!);
        if (code == 0) HeapTraceStarted = false;
        Write("heap-session-stop.json", new { Session = HeapTraceSession, Result = code, Pid = System.Environment.ProcessId });
        Require(code == 0, "Own heap trace failed to stop.");
        Require(HeapCommand("logman", "query", HeapTraceSession!, "-ets") == 0x80300002, "Own heap session survived stop.");
    }
    private static void HeapModules(string label)
    {
        if (System.Environment.GetEnvironmentVariable("FWM_NATIVE_HEAP_TRACE") != "1") return;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        Write("heap-modules-" + label + ".json", new { Pid = process.Id, Qpc = System.Diagnostics.Stopwatch.GetTimestamp(), Modules = process.Modules.Cast<System.Diagnostics.ProcessModule>().Select(m => new { Path = m.FileName, Base = m.BaseAddress.ToInt64(), Bytes = m.ModuleMemorySize }).ToArray() });
    }
}
