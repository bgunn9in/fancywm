internal static partial class Program
{
    private static System.IntPtr NativeHeapModule;
    private static StartTrace? NativeHeapSnapshot;
    private static void StartNativeHeap()
    {
        string? path = System.Environment.GetEnvironmentVariable("FWM_NATIVE_HEAP_DLL");
        if (path == null) return;
        NativeHeapModule = System.Runtime.InteropServices.NativeLibrary.Load(path);
        NativeHeapSnapshot = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<StartTrace>(
            System.Runtime.InteropServices.NativeLibrary.GetExport(NativeHeapModule, "HeapSnapshot"));
        string folder = System.IO.Path.Combine(Output, "heap");
        Require(!System.IO.Directory.Exists(folder), "Heap output already exists.");
        System.IO.Directory.CreateDirectory(folder);
        // Warm marshaling and observer before production Startup and workload.
        ObserveNativeHeap("startup");
    }
    private static void ObserveNativeHeap(string label)
    {
        if (NativeHeapSnapshot == null) return;
        if (label == "0") StartHeapTrace();
        HeapModules(label);
        long begin = System.Diagnostics.Stopwatch.GetTimestamp();
        int code = NativeHeapSnapshot(System.IO.Path.Combine(Output, "heap", label + ".bin"));
        Row(new { Kind = "NativeHeapSnapshot", Label = label, Result = code,
            QpcBegin = begin, QpcEnd = System.Diagnostics.Stopwatch.GetTimestamp(),
            DispatcherAlive = !System.Windows.Threading.Dispatcher.CurrentDispatcher.HasShutdownStarted,
            DefaultProcessHeapOnly = true, BlockContentsRecorded = false, AllocationStackClaim = false });
        Require(code == 0, "Default heap native enumeration failed: " + code);
    }
}
