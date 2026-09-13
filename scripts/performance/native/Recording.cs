using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace FancyWM.AnimationNativeHarness;

// The sidecar uses the same QPC clock as ETW. Recording does not allocate per call.
internal sealed class Recording
{
    internal enum Operation { Frame, PositionRead, SetPosition, NativeApplied, Boost }
    internal readonly record struct Sample(long Qpc, long Duration, int ThreadId,
        int Transition, int Frame, Operation Operation, int Target,
        int X, int Y, int Width, int Height);

    private readonly Sample[] m_samples = new Sample[500_000];
    private int m_count;
    public int AttemptedSamples => Volatile.Read(ref m_count);
    public bool CapacityExceeded => AttemptedSamples > m_samples.Length;
    public int Transition;
    public int Frame;

    public void Add(Operation operation, long start, int target = -1,
        int x = 0, int y = 0, int width = 0, int height = 0)
    {
        long end = Stopwatch.GetTimestamp();
        int slot = Interlocked.Increment(ref m_count) - 1;
        // Never throw from an HWND callback: WinForms may show a modal error
        // dialog and prevent owned-thread shutdown. The controller rejects the
        // run at its next boundary and retains this valid prefix plus the count.
        if (slot >= m_samples.Length) return;
        m_samples[slot] = new(start, end - start, (int)GetCurrentThreadId(),
            Volatile.Read(ref Transition), Volatile.Read(ref Frame), operation, target, x, y, width, height);
    }

    // Preserve the valid prefix after a capacity failure. Do not mask the
    // original failure with an out-of-range exception during final cleanup.
    public Sample[] Snapshot() => m_samples.AsSpan(0, Math.Min(Volatile.Read(ref m_count), m_samples.Length)).ToArray();

    public void ThrowIfCapacityExceeded()
    {
        if (CapacityExceeded) throw new InvalidOperationException($"Recording capacity exceeded: {AttemptedSamples} attempted samples.");
    }

    public void Save(string path)
    {
        using var writer = new StreamWriter(path, false);
        writer.WriteLine("qpc,duration_ticks,thread_id,transition,frame,operation,target,x,y,width,height");
        foreach (var sample in Snapshot())
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{sample.Qpc},{sample.Duration},{sample.ThreadId},{sample.Transition},{sample.Frame},{sample.Operation},{sample.Target},{sample.X},{sample.Y},{sample.Width},{sample.Height}"));
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}

[EventSource(Name = "FancyWM-AnimationNativeHarness", Guid = "c72441c8-a777-4d33-8d80-794f778a01ca")]
internal sealed class AnimationEvents : EventSource
{
    public static readonly AnimationEvents Log = new();

    // phase: 1 = submit, 2 = task complete, 3 = native rectangles verified,
    // 4 = DwmFlush returned. These are different completion boundaries.
    [Event(1, Level = EventLevel.Informational)]
    // Match the three-Int64 WriteEvent overload exactly, including the manifest.
    public void Boundary(long transition, long phase, long qpc) => WriteEvent(1, transition, phase, qpc);
}
