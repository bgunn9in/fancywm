using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;

internal static partial class Program
{
    private sealed record SettingsRequest(int Ordinal, uint NativeThreadId, long StartedQpc, long CompletedQpc);

    private static object[] SettingsVersions() => TilingServices().Select(service => (object)new
    {
        Requested = (long)Field(service, "m_settingsRequestVersion")!,
        Applied = (long)Field(service, "m_settingsAppliedVersion")!,
        WindowPadding = (int)Field(service, "m_windowPadding")!,
        NativeThreadId = GetCurrentThreadId()
    }).ToArray();

    private static async Task ValidateConcurrentSettings()
    {
        var before = SettingsVersions(); var calls = new ConcurrentBag<SettingsRequest>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = Enumerable.Range(0, 12).Select(i => Task.Run(async () =>
        {
            await release.Task;
            uint thread = GetCurrentThreadId(); long start = Stopwatch.GetTimestamp();
            await State.Settings.SaveAsync(s => s with { WindowPadding = 4 + i % 3 * 4 });
            calls.Add(new(i, thread, start, Stopwatch.GetTimestamp()));
        })).ToArray();
        release.SetResult(); await Task.WhenAll(updates).WaitAsync(TimeSpan.FromSeconds(20));
        await App.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Idle(10); var geometry = await VerifiedGeometry(10);
        var recorded = calls.OrderBy(c => c.StartedQpc).ToArray();
        if (recorded.Length != 12 || recorded[1].StartedQpc >= recorded[0].CompletedQpc)
            throw new InvalidOperationException("Concurrent settings calls did not overlap.");
        foreach (var service in TilingServices())
            if ((long)Field(service, "m_settingsRequestVersion")! != (long)Field(service, "m_settingsAppliedVersion")!)
                throw new InvalidOperationException("The final requested settings version was not applied.");
        Observations.Add(new { Kind = "ConcurrentSettings", Passed = true, Requests = recorded,
            VersionsBefore = before, VersionsAfter = SettingsVersions(), Geometry = geometry });
    }

    private static async Task ValidateNativeInterruption(int scenario, string action, int remaining)
    {
        await Targets!.Command(new { Operation = "scenario", Id = scenario });
        await Targets.Command(new { Operation = "arm-interruption", Index = 9, Action = action });
        long started = Stopwatch.GetTimestamp(); FullAppEvents.Log.Boundary(scenario, 1, started);
        var saved = State.Settings.SaveAsync(s => s with { WindowPadding = s.WindowPadding == 4 ? 12 : 4 });
        using var stopObservation = new CancellationTokenSource();
        async Task<long> ObserveIdle()
        {
            await Idle(remaining, stopObservation.Token);
            return Stopwatch.GetTimestamp();
        }
        var idle = ObserveIdle(); JsonElement interruption;
        var timeout = Stopwatch.StartNew();
        try
        {
        while (true)
        {
            var response = await Targets.Command(new { Operation = "snapshot" });
            var found = response.GetProperty("Interruptions").EnumerateArray()
                .Where(row => row.GetProperty("Scenario").GetInt32() == scenario).ToArray();
            if (found.Length == 1) { interruption = found[0].Clone(); break; }
            if (timeout.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("No native movement triggered the interruption.");
            await Task.Delay(4);
        }
        long idleObserved = await idle;
        var geometry = await VerifiedGeometry(remaining); long verified = Stopwatch.GetTimestamp();
        await saved;
        long triggered = interruption.GetProperty("NativePositionMessageQpc").GetInt64();
        long applied = interruption.GetProperty("AppliedQpc").GetInt64();
        if (triggered < started || applied < triggered || applied >= idleObserved)
            throw new InvalidOperationException("The interruption was not bracketed by the native movement and layout observation.");
        Observations.Add(new { Kind = "NativeMovementInterruption", Action = action, Scenario = scenario, Passed = true,
            StartedQpc = started, LayoutIdleObservedQpc = idleObserved, NativeVerifiedQpc = verified,
            NativeInterruption = interruption, RemainingWindows = remaining, Geometry = geometry,
            Meaning = "Real target minimize/close posted by its native position callback; surviving layout and application remain live. Individual internal animation Task status is not instrumented." });
        }
        finally
        {
            stopObservation.Cancel();
            await idle.ContinueWith(task => { if (task.IsFaulted) _ = task.Exception; },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
