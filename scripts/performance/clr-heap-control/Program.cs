using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;

internal static class Program
{
    private static string Output = "", Role = "";
    private static readonly List<object> Rows = [];
    private static StreamWriter? Journal;
    private static Snapshot? TakeSnapshot;
    private delegate int Snapshot([MarshalAs(UnmanagedType.LPWStr)] string path);
    private sealed record Buffer(int Index, string Kind, nint Pointer, int Size);
    private static void Row(object value) { Rows.Add(value); Journal!.WriteLine(JsonSerializer.Serialize(value)); Journal.Flush(); }
    private static void Snap(string label)
    {
        string path = Path.Combine(Output, label + ".bin"); if (File.Exists(path)) throw new IOException("Snapshot exists.");
        long before = Stopwatch.GetTimestamp(); int result = TakeSnapshot!(path);
        if (result != 0) throw new InvalidOperationException("Native heap snapshot failed.");
        Row(new { Kind = "Snapshot", Label = label, Before = before, After = Stopwatch.GetTimestamp(), Result = result });
    }
    private static void Main(string[] args)
    {
        if (args.Length != 4 || Directory.Exists(args[0])) throw new ArgumentException("<fresh-output> <role> <plugin> <heap-observer>");
        Output = Path.GetFullPath(args[0]); Role = args[1]; Directory.CreateDirectory(Output);
        using var journal = new StreamWriter(new FileStream(Path.Combine(Output, "journal.jsonl"), FileMode.CreateNew)); Journal = journal;
        nint library = NativeLibrary.Load(Path.GetFullPath(args[3])); TakeSnapshot = Marshal.GetDelegateForFunctionPointer<Snapshot>(NativeLibrary.GetExport(library, "HeapSnapshot"));
        var active = new List<Buffer>();
        try
        {
            nint warm = WarmAllocator.Allocate(72); if (!Native.Free(Native.GetProcessHeap(), 0, warm)) throw new InvalidOperationException("Warm free.");
            Row(new { Kind = "Process", Pid = Environment.ProcessId, Role, Runtime = Environment.Version.ToString(), Environment.Is64BitProcess, QpcFrequency = Stopwatch.Frequency, ModuleMvid = typeof(Program).Module.ModuleVersionId });
            Console.WriteLine(JsonSerializer.Serialize(new { Ready = true, Pid = Environment.ProcessId, Role })); Console.Out.Flush();
            if (Console.ReadLine() != "run") throw new InvalidOperationException("Missing own run command.");
            for (int epoch = 0; epoch != 2; epoch++)
            {
                Snap(epoch + "-baseline");
                for (int i = 0; i != 16; i++)
                {
                    int size = 4096 + i * 256; long before = Stopwatch.GetTimestamp();
                    nint pointer = i < 8 ? WarmAllocator.Allocate(size) : ColdAllocator.Allocate(size);
                    if (pointer == 0) throw new OutOfMemoryException();
                    string kind = i < 8 ? "Warm" : "Cold"; active.Add(new(i, kind, pointer, size));
                    Row(new { Kind = "Allocate", Epoch = epoch, Index = i, Owner = Role + ":" + epoch + ":" + i, Allocator = kind, Pointer = pointer.ToInt64(), Size = size, Before = before, After = Stopwatch.GetTimestamp(), Tid = Native.GetCurrentThreadId() });
                }
                WeakReference weak = PluginRound(Path.GetFullPath(args[2]), epoch, active);
                for (int i = 0; i != 40 && weak.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(25); }
                Row(new { Kind = "CollectibleContext", Epoch = epoch, Collected = !weak.IsAlive, Qpc = Stopwatch.GetTimestamp() });
                if (weak.IsAlive) throw new InvalidOperationException("Owned collectible context retained.");
                Snap(epoch + "-held-after-code-unload");
                for (int i = 0; i != active.Count; i++)
                {
                    Buffer old = active[i]; int size = 16384 + old.Index * 256; long before = Stopwatch.GetTimestamp();
                    nint next = ColdAllocator.Reallocate(old.Pointer, size); if (next == 0) throw new OutOfMemoryException();
                    active[i] = old with { Pointer = next, Size = size };
                    Row(new { Kind = "Reallocate", Epoch = epoch, old.Index, Owner = Role + ":" + epoch + ":" + old.Index, OldPointer = old.Pointer.ToInt64(), OldSize = old.Size, Pointer = next.ToInt64(), Size = size, Before = before, After = Stopwatch.GetTimestamp(), Tid = Native.GetCurrentThreadId() });
                }
                Snap(epoch + "-resized");
                while (active.Count != 0)
                {
                    var buffer = active[^1];
                    long before = Stopwatch.GetTimestamp(); bool freed = Native.Free(Native.GetProcessHeap(), 0, buffer.Pointer);
                    if (freed) active.RemoveAt(active.Count - 1);
                    Row(new { Kind = "Free", Epoch = epoch, buffer.Index, Pointer = buffer.Pointer.ToInt64(), Before = before, After = Stopwatch.GetTimestamp(), Success = freed, Tid = Native.GetCurrentThreadId() });
                    if (!freed) throw new InvalidOperationException("Owned native free failed.");
                }
                active.Clear(); Snap(epoch + "-released");
            }
            using (var file = new FileStream(Path.Combine(Output, "observations.json"), FileMode.CreateNew)) JsonSerializer.Serialize(file, Rows);
            Console.WriteLine(JsonSerializer.Serialize(new { Done = true, Pid = Environment.ProcessId, Role, Epochs = 2, Allocations = 48, Reallocations = 48, Frees = 48, Qpc = Stopwatch.GetTimestamp() })); Console.Out.Flush();
            if (Console.ReadLine() != "exit") throw new InvalidOperationException("Missing own exit command.");
        }
        catch (Exception e) { Row(new { Kind = "Failure", Error = e.ToString(), Qpc = Stopwatch.GetTimestamp() }); throw; }
        finally { foreach (var b in active) { bool freed = Native.Free(Native.GetProcessHeap(), 0, b.Pointer); Row(new { Kind = "EmergencyFree", Pointer = b.Pointer.ToInt64(), Success = freed }); } NativeLibrary.Free(library); }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference PluginRound(string path, int epoch, List<Buffer> active)
    {
        var context = new AssemblyLoadContext("FWMOwned_" + Role + "_" + epoch, true); Assembly assembly = context.LoadFromAssemblyPath(path);
        MethodInfo method = assembly.GetType("OwnedHeapPlugin.Allocator")!.GetMethod("Allocate")!;
        Row(new { Kind = "Plugin", Epoch = epoch, Context = context.Name, Mvid = method.Module.ModuleVersionId, Token = method.MetadataToken, Type = method.DeclaringType!.FullName, Method = method.Name, Qpc = Stopwatch.GetTimestamp() });
        for (int i = 16; i != 24; i++)
        {
            int size = 4096 + i * 256; long before = Stopwatch.GetTimestamp(); nint pointer = (nint)method.Invoke(null, [size])!;
            if (pointer == 0) throw new OutOfMemoryException(); active.Add(new(i, "Plugin", pointer, size));
            Row(new { Kind = "Allocate", Epoch = epoch, Index = i, Owner = Role + ":" + epoch + ":" + i, Allocator = "Plugin", Pointer = pointer.ToInt64(), Size = size, Before = before, After = Stopwatch.GetTimestamp(), Tid = Native.GetCurrentThreadId() });
        }
        var weak = new WeakReference(context); context.Unload(); return weak;
    }
}
internal static class Native
{
    [DllImport("kernel32.dll")] internal static extern nint GetProcessHeap();
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", EntryPoint = "HeapAlloc")] internal static extern nint Alloc(nint heap, uint flags, nuint bytes);
    [DllImport("kernel32.dll", EntryPoint = "HeapReAlloc")] internal static extern nint Realloc(nint heap, uint flags, nint old, nuint bytes);
    [DllImport("kernel32.dll", EntryPoint = "HeapFree")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool Free(nint heap, uint flags, nint pointer);
}
internal static class WarmAllocator
{
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)] internal static nint Allocate(int size) { nint p = Native.Alloc(Native.GetProcessHeap(), 8, (nuint)size); GC.KeepAlive(size); return p; }
}
internal static class ColdAllocator
{
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)] internal static nint Allocate(int size) { nint p = Native.Alloc(Native.GetProcessHeap(), 8, (nuint)size); GC.KeepAlive(size); return p; }
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)] internal static nint Reallocate(nint pointer, int size) { nint p = Native.Realloc(Native.GetProcessHeap(), 8, pointer, (nuint)size); GC.KeepAlive(size); return p; }
}
