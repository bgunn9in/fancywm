using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;

internal static partial class Program
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate int DxgiStartDelegate(string path);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DxgiSampleDelegate(int phase, int sample);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DxgiStopDelegate();
    private static IntPtr DxgiModule;
    private static DxgiSampleDelegate? DxgiSampleCall;
    private static DxgiStopDelegate? DxgiStopCall;
    private static void StartDxgi()
    {
        string? path = Environment.GetEnvironmentVariable("FWM_DXGI_MEMORY_DLL");
        if (path == null) return;
        Require(Mode == "graceful", "DXGI observations require the owned graceful graph.");
        DxgiModule = NativeLibrary.Load(path);
        var start = Marshal.GetDelegateForFunctionPointer<DxgiStartDelegate>(NativeLibrary.GetExport(DxgiModule, "DxgiStart"));
        DxgiSampleCall = Marshal.GetDelegateForFunctionPointer<DxgiSampleDelegate>(NativeLibrary.GetExport(DxgiModule, "DxgiSample"));
        DxgiStopCall = Marshal.GetDelegateForFunctionPointer<DxgiStopDelegate>(NativeLibrary.GetExport(DxgiModule, "DxgiStop"));
        long begin = Stopwatch.GetTimestamp();
        int code = start(Path.Combine(Output, "dxgi.jsonl"));
        Row(new { Kind = "DxgiMemoryStarted", Pid = Environment.ProcessId, Code = code, QpcBegin = begin, QpcEnd = Stopwatch.GetTimestamp(), Module = DxgiModule.ToInt64(), NodeIndex = 0, ReservationChanged = false });
        Require(code == 0, "DXGI start failed: " + code);
        ObserveDxgiSynchronously(-1);
    }
    private static void SampleDxgi(int phase, int sample)
    {
        long begin = Stopwatch.GetTimestamp();
        int code = DxgiSampleCall!(phase, sample);
        Row(new { Kind = "DxgiMemorySample", Pid = Environment.ProcessId, Phase = phase, Sample = sample, Code = code,
            QpcBegin = begin, QpcEnd = Stopwatch.GetTimestamp(), DispatcherAlive = !Dispatcher.CurrentDispatcher.HasShutdownStarted });
        Require(code == 0, "DXGI query failed: " + code);
        SampleProcessMemory(phase, sample);
    }
    private static async Task ObserveDxgi(int phase)
    {
        if (DxgiSampleCall == null) return;
        for (int sample = 0; sample < 5; sample++)
        {
            if (sample != 0) await Task.Delay(100);
            SampleDxgi(phase, sample);
        }
    }
    private static void ObserveDxgiSynchronously(int phase)
    {
        if (DxgiSampleCall == null) return;
        for (int sample = 0; sample < 5; sample++)
        {
            if (sample != 0) Thread.Sleep(100);
            SampleDxgi(phase, sample);
        }
    }
    private static void StopDxgi()
    {
        if (DxgiModule == IntPtr.Zero) return;
        long begin = Stopwatch.GetTimestamp();
        int code = DxgiStopCall?.Invoke() ?? -1;
        DxgiSampleCall = null; DxgiStopCall = null;
        NativeLibrary.Free(DxgiModule); DxgiModule = IntPtr.Zero;
        Write("dxgi-stopped.json", new { Pid = Environment.ProcessId, Code = code, QpcBegin = begin, QpcEnd = Stopwatch.GetTimestamp(), ModuleUnloaded = true });
        Require(code == 0, "DXGI cleanup failed: " + code);
    }
}
