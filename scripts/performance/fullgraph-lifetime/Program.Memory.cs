using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;

internal static partial class Program
{
    private static IntPtr MemoryModule;
    private static DxgiSampleDelegate? MemorySampleCall;
    private static DxgiStopDelegate? MemoryStopCall;
    private static void StartProcessMemory()
    {
        string? path = Environment.GetEnvironmentVariable("FWM_PROCESS_MEMORY_DLL");
        if (path == null) return;
        Require(Mode == "graceful" && Environment.GetEnvironmentVariable("FWM_DXGI_MEMORY_DLL") != null, "Process memory requires the owned DXGI graph.");
        MemoryModule = NativeLibrary.Load(path);
        var start = Marshal.GetDelegateForFunctionPointer<DxgiStartDelegate>(NativeLibrary.GetExport(MemoryModule, "MemoryStart"));
        MemorySampleCall = Marshal.GetDelegateForFunctionPointer<DxgiSampleDelegate>(NativeLibrary.GetExport(MemoryModule, "MemorySample"));
        MemoryStopCall = Marshal.GetDelegateForFunctionPointer<DxgiStopDelegate>(NativeLibrary.GetExport(MemoryModule, "MemoryStop"));
        long begin = Stopwatch.GetTimestamp(); int code = start(Path.Combine(Output, "memory.jsonl"));
        Row(new { Kind = "ProcessMemoryStarted", Pid = Environment.ProcessId, Code = code, Module = MemoryModule.ToInt64(), QpcBegin = begin, QpcEnd = Stopwatch.GetTimestamp() });
        Require(code == 0, "Process memory start failed: " + code);
    }
    private static void SampleProcessMemory(int phase, int sample)
    {
        if (MemorySampleCall == null) return;
        long begin = Stopwatch.GetTimestamp(); int code = MemorySampleCall(phase, sample);
        Row(new { Kind = "ProcessMemorySample", Pid = Environment.ProcessId, Phase = phase, Sample = sample, Code = code,
            QpcBegin = begin, QpcEnd = Stopwatch.GetTimestamp(), DispatcherAlive = !Dispatcher.CurrentDispatcher.HasShutdownStarted, AtomicWithDxgi = false });
        Require(code == 0, "Process memory query failed: " + code);
    }
    private static void StopProcessMemory()
    {
        if (MemoryModule == IntPtr.Zero) return;
        long begin = Stopwatch.GetTimestamp(); int code = MemoryStopCall?.Invoke() ?? -1;
        MemorySampleCall = null; MemoryStopCall = null;
        NativeLibrary.Free(MemoryModule); MemoryModule = IntPtr.Zero;
        Write("memory-stopped.json", new { Pid = Environment.ProcessId, Code = code, QpcBegin = begin, QpcEnd = Stopwatch.GetTimestamp(), ModuleUnloaded = true });
        Require(code == 0, "Process memory cleanup failed: " + code);
    }
}
