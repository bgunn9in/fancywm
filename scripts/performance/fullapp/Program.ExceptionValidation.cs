using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

internal static partial class Program
{
    private sealed record ValidationException(long Qpc, uint NativeThreadId, string Type, string Stack);
    private static readonly ConcurrentQueue<ValidationException> ValidationExceptions = new();
    private static int ValidationExceptionCount;

    private static void ObserveValidationException(object? sender, FirstChanceExceptionEventArgs args)
    {
        if (args.Exception is not (WinMan.InvalidWindowReferenceException or InvalidOperationException or TaskCanceledException)) return;
        string stack = args.Exception.StackTrace ?? "";
        if (!stack.Contains("FancyWM.Utilities.TransitionTargetGroup", StringComparison.Ordinal)
            && !stack.Contains("FancyWM.Utilities.Tasks.WhenAllIgnoreCancelled", StringComparison.Ordinal)
            && !stack.Contains("WinMan.Windows.Win32Window", StringComparison.Ordinal)) return;
        if (Interlocked.Increment(ref ValidationExceptionCount) <= 256)
            ValidationExceptions.Enqueue(new(Stopwatch.GetTimestamp(), GetCurrentThreadId(), args.Exception.GetType().FullName!, stack));
    }
}
