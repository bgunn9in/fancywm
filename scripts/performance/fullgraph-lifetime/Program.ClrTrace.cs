internal static partial class Program
{
    private static System.Diagnostics.Process? ClrCollector;
    private static System.Threading.Tasks.Task? ClrOutput, ClrError;
    private static System.IO.FileStream? ClrStdout, ClrStderr;
    private static string? ClrSession;
    private static void StartClrTrace()
    {
        string? executable = System.Environment.GetEnvironmentVariable("FWM_CLR_HEAP_SOURCE");
        if (executable == null) return;
        Require(Mode == "graceful", "CLR attribution requires graceful owned process.");
        ClrSession = "FWM_OWNED_CLR_" + System.Environment.ProcessId + "_" + System.Guid.NewGuid().ToString("N");
        Require(HeapCommand("logman", "query", ClrSession, "-ets") == 0x80300002, "Own CLR name already present.");
        var start = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(System.IO.Path.Combine(Output, "clr")); start.ArgumentList.Add(System.Environment.ProcessId.ToString()); start.ArgumentList.Add(ClrSession);
        ClrStdout = new(System.IO.Path.Combine(Output, "clr.stdout"), System.IO.FileMode.CreateNew);
        ClrStderr = new(System.IO.Path.Combine(Output, "clr.stderr"), System.IO.FileMode.CreateNew);
        ClrCollector = System.Diagnostics.Process.Start(start)!;
        Write("clr-created.json", new { Pid = System.Environment.ProcessId, CollectorPid = ClrCollector.Id, CollectorStartUtcTicks = ClrCollector.StartTime.ToUniversalTime().Ticks, Session = ClrSession, Executable = executable, Arguments = start.ArgumentList.ToArray(), StartedBeforeProductionStartup = true });
        ClrError = ClrCollector.StandardError.BaseStream.CopyToAsync(ClrStderr);
        var readyTask = ClrCollector.StandardOutput.ReadLineAsync();
        Require(readyTask.Wait(45000), "Own CLR source did not become ready.");
        string line = readyTask.Result ?? throw new System.InvalidOperationException("CLR pipe ended before ready.");
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(line + "\n"); ClrStdout.Write(bytes); ClrStdout.Flush();
        ClrOutput = System.Threading.Tasks.Task.Run(async () => { string rest = await ClrCollector.StandardOutput.ReadToEndAsync(); await ClrStdout.WriteAsync(System.Text.Encoding.UTF8.GetBytes(rest)); });
        using var ready = System.Text.Json.JsonDocument.Parse(line);
        Require(ready.RootElement.GetProperty("Ready").GetBoolean() && ready.RootElement.GetProperty("OwnPid").GetInt32() == System.Environment.ProcessId, "CLR ready identity mismatch.");
        Row(new { Kind = "ClrSourceReady", Pid = System.Environment.ProcessId, CollectorPid = ClrCollector.Id, Session = ClrSession, RundownCompleteQpc = ready.RootElement.GetProperty("RundownCompleteQpc").GetInt64(), Qpc = System.Diagnostics.Stopwatch.GetTimestamp(), StartedBeforeProductionStartup = true });
    }
    private static void StopClrTrace()
    {
        if (ClrCollector == null) return;
        int code;
        try
        {
            if (!ClrCollector.HasExited) { ClrCollector.StandardInput.WriteLine("stop"); ClrCollector.StandardInput.Flush(); }
            if (!ClrCollector.WaitForExit(40000)) { ClrCollector.Kill(); ClrCollector.WaitForExit(); throw new System.TimeoutException("Owned CLR collector failed to stop."); }
            code = ClrCollector.ExitCode;
            if (ClrOutput != null) ClrOutput.GetAwaiter().GetResult();
            if (ClrError != null) ClrError.GetAwaiter().GetResult();
            Write("clr-stopped.json", new { CollectorPid = ClrCollector.Id, ExitCode = code, Exited = ClrCollector.HasExited, Session = ClrSession });
        }
        finally { ClrStdout?.Dispose(); ClrStderr?.Dispose(); ClrCollector.Dispose(); ClrCollector = null; }
        Require(code == 0, "Owned CLR collector returned failure.");
        Require(HeapCommand("logman", "query", ClrSession!, "-ets") == 0x80300002, "Own CLR session survived stop.");
    }
}
