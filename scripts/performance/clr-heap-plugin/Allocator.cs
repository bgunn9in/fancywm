using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
namespace OwnedHeapPlugin;
public static class Allocator
{
    [DllImport("kernel32.dll")] private static extern nint GetProcessHeap();
    [DllImport("kernel32.dll")] private static extern nint HeapAlloc(nint heap, uint flags, nuint bytes);
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static nint Allocate(int size) { nint pointer = HeapAlloc(GetProcessHeap(), 8, (nuint)size); GC.KeepAlive(size); return pointer; }
}
