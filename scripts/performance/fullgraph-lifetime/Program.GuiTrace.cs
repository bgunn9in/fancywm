internal static partial class Program
{
    private const uint GrGdiObjects = 0, GrUserObjects = 1;
    private static System.IntPtr GuiTraceModule;
    private static System.IntPtr OwnedEtwModule;
    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private delegate int StartTrace([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string path);
    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private delegate int TraceOperation();
    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private delegate int TraceMarker(int value);
    private static T Export<T>(string name) where T : System.Delegate => System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<T>(System.Runtime.InteropServices.NativeLibrary.GetExport(GuiTraceModule, name));
    private static void StartGuiTrace()
    {
        Row(new { Kind = "GuiCounterContract", GdiFlag = GrGdiObjects, UserFlag = GrUserObjects, Api = "user32!GetGuiResources", Schema = 2 });
        string? path = Environment.GetEnvironmentVariable("FWM_GUI_TRACE_DLL");
        if (path == null) return;
        string? etw = Environment.GetEnvironmentVariable("FWM_GUI_ETW_DLL");
        if (etw != null)
        {
            OwnedEtwModule = System.Runtime.InteropServices.NativeLibrary.Load(etw);
            var start = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<StartTrace>(System.Runtime.InteropServices.NativeLibrary.GetExport(OwnedEtwModule, "OwnedEtwStart"));
            int code = start(System.IO.Path.Combine(Output, "realtime"));
            if (code != 0) OwnedEtwModule = System.IntPtr.Zero; // Native Start already closed a failed admission.
            Require(code == 0, "Owned realtime ETW admission failed: " + code);
        }
        GuiTraceModule = System.Runtime.InteropServices.NativeLibrary.Load(path);
        Require(Export<StartTrace>("GuiTraceStart")(System.IO.Path.Combine(Output, "gui-api.jsonl")) == 0, "Native GUI instrumentation admission failed.");
        int control = Export<TraceOperation>("GuiTraceControl")();
        Row(new { Kind = "GuiCounterControl", Result = control, Bitmaps = 24, Menus = 24, Icons = 24, GdiFlag = GrGdiObjects, UserFlag = GrUserObjects });
        Require(control == 0, "Native GUI counter/create-destroy control failed: " + control);
        GuiMark(-1);
    }
    private static void StopOwnedEtw()
    {
        if (OwnedEtwModule == System.IntPtr.Zero) return;
        var stop = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<TraceOperation>(System.Runtime.InteropServices.NativeLibrary.GetExport(OwnedEtwModule, "OwnedEtwStop"));
        int code = stop(); OwnedEtwModule = System.IntPtr.Zero;
        Require(code == 0, "Owned ETW drain/loss/cleanup failed: " + code);
    }
    private static void GuiMark(int phase)
    {
        if (GuiTraceModule != System.IntPtr.Zero)
            Require(Export<TraceMarker>("GuiTraceMark")(phase) == 0, "Native GUI trace lost a write.");
    }
    private static void GuiTraceInventory(string label)
    {
        if (GuiTraceModule == System.IntPtr.Zero) return;
        GuiMark(label == "shutdown" ? 999 : int.Parse(label) * 10 + 9);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        Write("gui-modules-" + label + ".json", process.Modules.Cast<System.Diagnostics.ProcessModule>()
            .Select(m => new { Path = m.FileName, Base = m.BaseAddress.ToInt64(), Bytes = m.ModuleMemorySize }).ToArray());
        Write("gui-threads-" + label + ".json", new {
            QpcBegin = System.Diagnostics.Stopwatch.GetTimestamp(),
            Threads = process.Threads.Cast<System.Diagnostics.ProcessThread>().Select(t => new { Id = t.Id }).ToArray(),
            QpcEnd = System.Diagnostics.Stopwatch.GetTimestamp(),
            ManagedPoolThreads = System.Threading.ThreadPool.ThreadCount,
            PendingWork = System.Threading.ThreadPool.PendingWorkItemCount
        });
        if (label == "shutdown") Require(Export<TraceOperation>("GuiTraceClose")() == 0, "Native GUI trace failed to close.");
    }
}
