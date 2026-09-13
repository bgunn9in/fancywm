using System.IO;
using System.Diagnostics;
using System.Windows.Threading;

internal static partial class Program
{
    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private delegate uint PssBeginDelegate(string folder, out uint clonePid);
    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private delegate uint PssRegionsDelegate(string name);
    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private delegate uint PssEndDelegate();
    private static IntPtr PssModule;
    private static PssBeginDelegate? PssBeginCall;
    private static PssRegionsDelegate? PssRegionsCall;
    private static PssEndDelegate? PssEndCall;
    private static void StartPss()
    {
        string? path = Environment.GetEnvironmentVariable("FWM_PSS_DLL");
        if (path == null) return;
        PssModule = System.Runtime.InteropServices.NativeLibrary.Load(path);
        Row(new { Kind = "PssModuleLoaded", Module = PssModule.ToInt64(), Path = path, Pid = Environment.ProcessId });
        PssBeginCall = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<PssBeginDelegate>(System.Runtime.InteropServices.NativeLibrary.GetExport(PssModule, "PssBegin"));
        PssRegionsCall = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<PssRegionsDelegate>(System.Runtime.InteropServices.NativeLibrary.GetExport(PssModule, "PssCloneRegions"));
        PssEndCall = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<PssEndDelegate>(System.Runtime.InteropServices.NativeLibrary.GetExport(PssModule, "PssEnd"));
        Directory.CreateDirectory(Path.Combine(Output, "pss"));
        ObservePss("startup");
    }
    private static void ObservePss(string label)
    {
        if (PssBeginCall == null) return;
        uint clone = 0, capture = uint.MaxValue, first = uint.MaxValue, second = uint.MaxValue, release = uint.MaxValue;
        long begin = Stopwatch.GetTimestamp();
        try
        {
            capture = PssBeginCall(Path.Combine(Output, "pss", label), out clone);
            Row(new { Kind = "PssCreated", Label = label, Pid = Environment.ProcessId, ClonePid = clone, CaptureCode = capture });
            Require(capture == 0, "PSS capture failed: " + capture);
            first = PssRegionsCall!("clone-va-0.bin");
            // Source workers remain live; the clone is never explicitly resumed.
            Thread.Sleep(25);
            second = PssRegionsCall!("clone-va-1.bin");
            Require(first == 0 && second == 0, "PSS clone VA query failed.");
        }
        finally
        {
            release = PssEndCall!();
            Row(new { Kind = "PssReleased", Label = label, Pid = Environment.ProcessId, ClonePid = clone, CaptureCode = capture, FirstQuery = first, SecondQuery = second, ReleaseCode = release,
                QpcBegin = begin, QpcEnd = Stopwatch.GetTimestamp(), DispatcherAlive = !Dispatcher.CurrentDispatcher.HasShutdownStarted,
                HeapBlockEnumeration = false, LogicalOwnerClaim = false, CloneExplicitlyResumed = false });
        }
        Require(release == 0, "PSS release/clone absence failed: " + release);
    }
}
