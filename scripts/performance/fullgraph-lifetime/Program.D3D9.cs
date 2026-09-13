using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;

internal static partial class Program
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate int D3D9StartDelegate(string path);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int D3D9MarkDelegate(int phase);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int D3D9StopDelegate();
    private static IntPtr D3D9Module;
    private static D3D9MarkDelegate? D3D9MarkCall;
    private static D3D9StopDelegate? D3D9StopCall;
    private static void StartD3D9()
    {
        string? path = Environment.GetEnvironmentVariable("FWM_D3D9_DLL");
        if (path == null) return;
        Require(Mode == "graceful", "D3D9 observer requires graceful owned process.");
        if (Environment.GetEnvironmentVariable("FWM_D3D9_MAP_PSS_DLL") != null)
            Require(Environment.GetEnvironmentVariable("FWM_D3D9_MAP_ROOT") == Path.Combine(Output, "d3d9-mapped-pss"), "Mapped PSS must stay in the owned run output.");
        D3D9Module = NativeLibrary.Load(path);
        var start = Marshal.GetDelegateForFunctionPointer<D3D9StartDelegate>(NativeLibrary.GetExport(D3D9Module, "D3D9Start"));
        D3D9MarkCall = Marshal.GetDelegateForFunctionPointer<D3D9MarkDelegate>(NativeLibrary.GetExport(D3D9Module, "D3D9Mark"));
        D3D9StopCall = Marshal.GetDelegateForFunctionPointer<D3D9StopDelegate>(NativeLibrary.GetExport(D3D9Module, "D3D9Stop"));
        int code = start(Path.Combine(Output, "d3d9.jsonl"));
        Row(new { Kind = "D3D9Started", Pid = Environment.ProcessId, Code = code, Qpc = Stopwatch.GetTimestamp(), PrivateDataOwnersOnly = true });
        Require(code == 0, "D3D9 observer failed: " + code);
        MarkD3D9(-1);
    }
    private static void MarkD3D9(int phase)
    {
        if (D3D9MarkCall == null) return;
        long begin = Stopwatch.GetTimestamp();
        int code = D3D9MarkCall(phase);
        Row(new { Kind = "D3D9Mark", Phase = phase, Code = code, Pid = Environment.ProcessId, QpcBegin = begin, QpcEnd = Stopwatch.GetTimestamp(), DispatcherAlive = !System.Windows.Threading.Dispatcher.CurrentDispatcher.HasShutdownStarted });
        Require(code == 0, "D3D9 checkpoint failed: " + code);
    }
    private static void StopD3D9()
    {
        if (D3D9StopCall == null) return;
        long begin = Stopwatch.GetTimestamp();
        int code = D3D9StopCall();
        Write("d3d9-stopped.json", new { Pid = Environment.ProcessId, Code = code, QpcBegin = begin, QpcEnd = Stopwatch.GetTimestamp(), ModuleUnloaded = false, CompleteResourceDestructionClaim = false });
        // Private IUnknown callbacks can arrive after Startup.Main returns.
        // Keep the observer module loaded; it does not hold resource COM refs.
        Require(code == 0, "D3D9 detach failed: " + code);
    }
}
