using System.Diagnostics;
using System.Text.Json;
using Rectangle = WinMan.Rectangle;

internal static partial class Program
{
    private sealed record RestoreRow(long Hwnd, Rectangle Expected);
    private static bool PendingShutdown;
    private static RestoreRow[] ShutdownRestores = [];
    private static object? ShutdownWorker;
    private static int ShutdownScenario;

    private static async Task PreparePendingShutdown(int scenario)
    {
        ShutdownRestores = TilingServices().SelectMany(service =>
        {
            using (BackendLock(service).EnterScope())
            {
                var backend = Field(service, "m_backend")!;
                var getter = backend.GetType().GetMethod("GetOriginalPosition", Instance)!;
                return Nodes(service).Select(node => new RestoreRow(node.WindowReference.Handle.ToInt64(),
                    (Rectangle)getter.Invoke(backend, [node.WindowReference])!)).ToArray();
            }
        }).ToArray();
        if (ShutdownRestores.Length != 50) throw new InvalidOperationException("Shutdown requires 50 owned restore positions.");
        ShutdownScenario = scenario;
        string eventName = $"Local\\FancyWM-PERF010-{Environment.ProcessId}-{Guid.NewGuid():N}";
        using var signal = new EventWaitHandle(false, EventResetMode.ManualReset, eventName, out bool created);
        if (!created) throw new InvalidOperationException("Native signal name was not fresh.");
        var signaled = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(signal, (_, timedOut) =>
        {
            if (timedOut) signaled.TrySetException(new TimeoutException("No native shutdown signal."));
            else signaled.TrySetResult(Stopwatch.GetTimestamp());
        }, null, TimeSpan.FromSeconds(20), true);
        try
        {
        await Targets!.Command(new { Operation = "scenario", Id = scenario });
        await Targets.Command(new { Operation = "arm-interruption", Index = 0, Action = "notify", EventName = eventName });
        long started = Stopwatch.GetTimestamp();
        var save = State.Settings.SaveAsync(s => s with { WindowPadding = s.WindowPadding == 4 ? 12 : 4 });
        // Observe the durable write without allowing it to delay native shutdown.
        _ = save.ContinueWith(task => { if (task.IsFaulted) _ = task.Exception; }, TaskScheduler.Default);
        long signalReceived;
        var previousContext = SynchronizationContext.Current;
        try
        {
            // Shutdown must be observed before the normal-priority layout
            // continuation releases its pending operation. This is a queued
            // Send-priority request, not a blocked or replaced production job.
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(
                App.Dispatcher, System.Windows.Threading.DispatcherPriority.Send));
            signalReceived = await signaled.Task;
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        var pending = TilingServices().Select(service =>
        {
            var queue = Field(service, "m_layoutInvalidations")!;
            lock (Field(queue, "m_lock")!)
                return new { ActiveCallbacks = (int)Field(queue, "m_activeCallbacks")!,
                    FrozenCount = (int)Property(Field(service, "m_frozen")!, "Count")! };
        }).ToArray();
        ShutdownWorker = Field(MainWindowObject!, "m_animationThread")!;
        var workerCompletion = (Task)Property(ShutdownWorker, "Completion")!;
        long requested = Stopwatch.GetTimestamp();
        bool entered = pending.Any(row => row.ActiveCallbacks > 0 && row.FrozenCount > 0) && !workerCompletion.IsCompleted && signalReceived >= started;
        Observations.Add(new { Kind = "PendingShutdownRequested", Scenario = scenario, StartedQpc = started,
            RequestedQpc = requested, SignalReceivedQpc = signalReceived, EventName = eventName,
            RequestDispatcherPriority = "Send", EnteredNativeOperation = entered, Pending = pending, RestorePositions = ShutdownRestores });
        if (!entered) throw new InvalidOperationException("Shutdown was not observed during an entered native layout operation.");
        PendingShutdown = true;
        typeof(FancyWM.App).GetMethod("Terminate", Instance)!.Invoke(App, null);
        // The caller immediately enters its finally and calls the actual App.Terminate.
        // Keep target HWNDs alive so restoration can be checked after App.Run returns.
        }
        finally { registration.Unregister(null); }
    }

    private static void VerifyPendingShutdown()
    {
        var target = Task.Run(() => Targets!.Command(new { Operation = "snapshot" })).GetAwaiter().GetResult();
        var interruption = target.GetProperty("Interruptions").EnumerateArray()
            .Single(row => row.GetProperty("Scenario").GetInt32() == ShutdownScenario).Clone();
        if (!interruption.GetProperty("DirectSignal").GetBoolean()) throw new InvalidOperationException("Native signal provenance is missing.");
        var completion = (Task)Property(ShutdownWorker!, "Completion")!;
        var thread = (Thread)Field(ShutdownWorker!, "m_thread")!;
        if (!completion.IsCompletedSuccessfully || thread.IsAlive)
            throw new InvalidOperationException("The production animation worker did not finish before App.Run returned.");
        var checks = new List<object>();
        // Allow queued native restore messages to reach the target owner, then
        // independently sample stability after shutdown without a Dispatcher.
        var timeout = Stopwatch.StartNew();
        int consecutive = 0;
        while (consecutive < 3)
        {
            var geometry = ShutdownRestores.Select(row =>
            {
                if (GetWindowProcess(new(row.Hwnd)) != Targets!.ProcessId || !GetWindowRect(new(row.Hwnd), out var r))
                    throw new InvalidOperationException("A shutdown target no longer belongs to the owned process.");
                var actual = new Rectangle(r.Left, r.Top, r.Right, r.Bottom);
                return new { row.Hwnd, row.Expected, Actual = actual, Equal = actual == row.Expected };
            }).ToArray();
            bool equal = geometry.All(row => row.Equal);
            checks.Add(new { Qpc = Stopwatch.GetTimestamp(), Geometry = geometry });
            consecutive = equal ? consecutive + 1 : 0;
            if (timeout.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Native shutdown restoration did not stabilize.");
            if (consecutive < 3) Thread.Sleep(100);
        }
        Observations.Add(new { Kind = "PendingShutdownVerified", Passed = true, WorkerCompleted = true,
            WorkerThreadExited = true, TargetsStillAlive = true, NativeInterruption = interruption, Checks = checks });
    }
}
