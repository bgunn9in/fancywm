internal static partial class Program
{
    private static object[] Services()
    {
        var tiling = Field(MainOwner, "m_tiling")!;
        return tiling.GetType().Name == "TilingService" ? [tiling] : ((System.Collections.IDictionary)Field(tiling, "m_tilingServices")!).Values.Cast<object>().ToArray();
    }
    private static FancyWM.Layouts.Tiling.WindowNode[] Nodes(object tiling)
    {
        var gate = Field(tiling, "m_backendLock")!;
        using ((gate as System.Threading.Lock ?? (System.Threading.Lock)Field(gate, "m_lock")!).EnterScope())
            return ((System.Collections.IEnumerable)Property(Field(tiling, "m_backend")!, "Trees")!).Cast<FancyWM.Layouts.Tiling.DesktopTree>().SelectMany(t => t.Root?.Windows ?? []).ToArray();
    }
    private static object[] ViewModels() => Services().SelectMany(s => ((System.Collections.IEnumerable)Property(Field(Field(s, "m_gui")!, "m_viewModel")!, "WindowElements")!).Cast<object>()).ToArray();
    private static async Task Idle(int count, int pid)
    {
        int stable = 0;
        try { await Wait(() =>
        {
            var nodes = Services().SelectMany(Nodes).ToArray();
            bool idle = nodes.Length == count && ViewModels().Length == count && nodes.All(n => GetWindowThreadProcessId(n.WindowReference.Handle, out uint owner) != 0 && owner == pid)
                && Services().All(s => { var q = Field(s, "m_layoutInvalidations")!; lock (Field(q, "m_lock")!) return !(bool)Field(q, "m_dirty")! && !(bool)Field(q, "m_scheduled")! && (int)Field(q, "m_activeCallbacks")! == 0 && !(bool)Field(s, "m_dirty")! && (int)Property(Field(s, "m_frozen")!, "Count")! == 0; });
            stable = idle ? stable + 1 : 0; return stable >= 3;
        }, "owned target layout idle " + count); }
        catch
        {
            var ws = (WinMan.IWorkspace)Workspace;
            Row(new { Kind = "IdleFailure", Expected = count, Nodes = Services().SelectMany(Nodes).Select(n => n.WindowReference.Handle.ToInt64()).ToArray(), ViewModels = ViewModels().Length,
                Windows = ws.GetSnapshot().Select(w => new { Hwnd = w.Handle.ToInt64(), w.CanResize, MinWidth = w.MinSize?.X, MinHeight = w.MinSize?.Y, w.Position, Desktops = ws.VirtualDesktopManager.Desktops.Select(d => new { d.Name, HasWindow = d.HasWindow(w) }).ToArray() }).ToArray(),
                Services = Services().Select(s => new { Active = Field(s, "m_active"), Dirty = Field(s, "m_dirty"), Frozen = Property(Field(s, "m_frozen")!, "Count") }).ToArray() });
            throw;
        }
    }
    private sealed record WeakOwner(string Kind, WeakReference Weak);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakOwner[] CaptureOwners() => Services().SelectMany(Nodes).SelectMany(n => new[] { new WeakOwner("WindowNode", new(n)), new WeakOwner("WindowReference", new(n.WindowReference)) })
        .Concat(ViewModels().Select(v => new WeakOwner("TilingWindowViewModel", new(v)))).ToArray();
    private static async Task Settle()
    {
        await Task.Delay(1200);
        // Apply the same public WPF view-cache activity established by the
        // earlier HelpPage evidence. Record passive and post-maintenance values.
        for (int i = 0; i < 3; i++)
        {
            _ = System.Windows.Data.CollectionViewSource.GetDefaultView(new System.Collections.ObjectModel.ObservableCollection<int>());
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
    private static async Task RunRetention()
    {
        using var targets = new TargetClient(System.IO.Path.Combine(System.AppContext.BaseDirectory, "targets", "FancyWM.Perf010Targets.exe"));
        bool membership = Environment.GetEnvironmentVariable("FWM_OWNED_MEMBERSHIP") == "1";
        int membershipCalls = 0;
        var membershipField = Workspace.GetType().Assembly.GetType("WinMan.Windows.Win32VirtualDesktop")!.GetField("HarnessWindowMembership", Static);
        if (membership)
        {
            Require(membershipField != null, "Membership adapter missing.");
            int targetPid = targets.ProcessId;
            membershipField!.SetValue(null, (Func<WinMan.IWindow, bool?>)(window =>
            {
                uint thread = GetWindowThreadProcessId(window.Handle, out uint pid);
                if (pid != targetPid) return null;
                Require(thread != 0 && DesktopName(thread) == Desktop, "Membership must reference own native target.");
                Interlocked.Increment(ref membershipCalls); return true;
            }));
            Row(new { Kind = "MembershipAdapter", TargetPid = targetPid, Desktop, NativeCurrentDesktop = true, NativeWindowMembership = false });
        }
        await State.Settings.SaveAsync(s => s with { AutoFloatNewWindows = false });
        await Task.Delay(300);
        var all = new List<WeakOwner>();
        for (int epoch = 0; epoch < 3; epoch++)
        {
            GuiMark(epoch * 10);
            MarkD3D9(epoch * 10);
            int warmup = int.Parse(Environment.GetEnvironmentVariable("FWM_FULLGRAPH_WARMUP") ?? "10");
            Require(warmup is 10 or 50, "Invalid warmup.");
            int cycles = epoch == 0 ? warmup : 50;
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                int count = new[] { 1, 10, 50 }[cycle % 3];
                var response = await targets.Command(new { Operation = "create", Count = count });
                var hwnds = response.GetProperty("Windows").EnumerateArray().Select(w => new IntPtr(w.GetProperty("Hwnd").GetInt64())).ToArray();
                Require(hwnds.Length == count && hwnds.All(h => IsWindow(h) && GetWindowThreadProcessId(h, out uint p) != 0 && p == targets.ProcessId), "Target native identity.");
                var workspace = (WinMan.IWorkspace)Workspace;
                await Wait(() => hwnds.All(h => workspace.FindWindow(h) != null), "native workspace discovery");
                if (membership) await Idle(count, targets.ProcessId);
                var owners = membership ? CaptureOwners() : hwnds.Select(h => new WeakOwner("WindowReference", new(workspace.FindWindow(h)!))).ToArray();
                all.AddRange(owners);
                var settingsOwners = await SettingsLifetime(); all.AddRange(settingsOwners);
                await State.Settings.SaveAsync(s => s with { WindowPadding = cycle % 2 == 0 ? 4 : 12 });
                if (membership) await Idle(count, targets.ProcessId);
                await targets.Command(new { Operation = "close-all" });
                await Wait(() => hwnds.All(h => workspace.FindWindow(h) == null), "workspace removal");
                await Idle(0, targets.ProcessId);
                Require(hwnds.All(h => !IsWindow(h)), "Target HWND survived close.");
                Row(new { Kind = "Lifetime", Epoch = epoch, Cycle = cycle, Count = count, TargetPid = targets.ProcessId, Hwnds = hwnds.Select(h => h.ToInt64()).ToArray(), Captured = owners.Length + settingsOwners.Length, BackendNodes = Services().SelectMany(Nodes).Count(), ControlledMembership = membership, MembershipCalls = membershipCalls, VirtualDesktopLayoutAvailable = false, SurvivingHwnds = hwnds.Where(IsWindow).Select(h => h.ToInt64()).ToArray(), DispatcherAlive = !App.Dispatcher.HasShutdownStarted });
            }
            await Task.Delay(1200); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Row(new { Kind = "PassiveRetention", Epoch = epoch, Retained = all.Where(w => w.Weak.IsAlive).GroupBy(w => w.Kind).ToDictionary(g => g.Key, g => g.Count()) });
            await Settle();
            for (int pass = 0; pass < 5; pass++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                await Task.Delay(200);
                Row(new { Kind = "SettlingPass", Epoch = epoch, Pass = pass, USER = GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, GrUserObjects), GDI = GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, GrGdiObjects) });
            }
            int quiescence = int.Parse(Environment.GetEnvironmentVariable("FWM_FULLGRAPH_GUI_QUIESCENCE") ?? "0");
            Require(quiescence is 0 or 45, "Invalid GUI quiescence observation duration.");
            for (int second = 0; second < quiescence; second++)
            {
                await Task.Delay(1000);
                Require(!App.Dispatcher.HasShutdownStarted, "Dispatcher stopped during GUI quiescence.");
                Row(new { Kind = "GuiQuiescence", Epoch = epoch, Second = second + 1, Seconds = quiescence,
                    USER = GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, GrUserObjects),
                    GDI = GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, GrGdiObjects),
                    ManagedPoolThreads = System.Threading.ThreadPool.ThreadCount,
                    PendingWork = System.Threading.ThreadPool.PendingWorkItemCount, DispatcherAlive = true });
            }
            int stableWindow = int.Parse(Environment.GetEnvironmentVariable("FWM_FULLGRAPH_GUI_STABLE_WINDOW") ?? "0");
            Require(stableWindow is 0 or 30, "Invalid bounded GUI stability observation.");
            if (stableWindow != 0)
            {
                Require(quiescence == 45, "Stability observation requires the same 45-second quiescence for every epoch.");
                var stable = new System.Collections.Generic.Queue<(int User, int Gdi, long Qpc)>();
                int observed = 0;
                while (stable.Count < 20 && observed < stableWindow * 10)
                {
                    await Task.Delay(100);
                    int user = GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, GrUserObjects);
                    int gdi = GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, GrGdiObjects);
                    long qpc = System.Diagnostics.Stopwatch.GetTimestamp();
                    Require(!App.Dispatcher.HasShutdownStarted, "Dispatcher stopped during stability observation.");
                    Row(new { Kind = "GuiStabilitySample", Epoch = epoch, Sample = observed++, USER = user, GDI = gdi, Qpc = qpc, DispatcherAlive = true });
                    if (stable.Count != 0 && (stable.Peek().User != user || stable.Peek().Gdi != gdi)) stable.Clear();
                    stable.Enqueue((user, gdi, qpc));
                }
                Require(stable.Count == 20, "No stable 20-sample GUI window within 30 seconds.");
                Row(new { Kind = "GuiStabilityAdmitted", Epoch = epoch, Observations = observed, MaxObservations = 300, Consecutive = 20, FirstStablePlateau = true, MinimumSelection = false });
                int sample = 0;
                foreach (var observation in stable)
                    Row(new { Kind = "GuiSample", Epoch = epoch, Sample = sample++, USER = observation.User, GDI = observation.Gdi, observation.Qpc, DispatcherAlive = true });
            }
            for (int sample = 0; stableWindow == 0 && sample < 20; sample++)
            {
                await Task.Delay(100);
                Row(new { Kind = "GuiSample", Epoch = epoch, Sample = sample, USER = GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, GrUserObjects), GDI = GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, GrGdiObjects), DispatcherAlive = !App.Dispatcher.HasShutdownStarted });
            }
            var retained = all.Where(w => w.Weak.IsAlive).GroupBy(w => w.Kind).ToDictionary(g => g.Key, g => g.Count());
            Row(new { Kind = "NativeInventory", Epoch = epoch, Windows = App.Windows.Cast<System.Windows.Window>().Select(w => new { Type = w.GetType().FullName, Hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle.ToInt64(), w.IsVisible }).ToArray(), WorkspaceWindows = ((WinMan.IWorkspace)Workspace).GetSnapshot().Select(w => w.Handle.ToInt64()).ToArray() });
            Row(new { Kind = "Epoch", Epoch = epoch, Cycles = cycles, Captured = all.Count, Retained = retained, USER = GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, GrUserObjects), GDI = GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, GrGdiObjects), DispatcherAlive = !App.Dispatcher.HasShutdownStarted, FixedStartupOwnersIntentionallyAlive = true, ViewMaintenance = true });
            GuiTraceInventory(epoch.ToString());
            ObserveNativeHeap(epoch.ToString());
            MarkD3D9(epoch * 10 + 1);
            ObservePss(epoch.ToString());
            await ObserveDxgi(epoch);
            ObserveGraphWindows(epoch.ToString());
            if (retained.Count != 0 && membership)
            {
                using var dump = new System.IO.FileStream(System.IO.Path.Combine(Output, "retention.dmp"), System.IO.FileMode.CreateNew);
                Require(MiniDumpWriteDump(System.Diagnostics.Process.GetCurrentProcess().Handle, Environment.ProcessId, dump.SafeFileHandle.DangerousGetHandle(), 2, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero), "Owned retention dump failed.");
                Row(new { Kind = "OwnedRetentionDump", Epoch = epoch, Pid = Environment.ProcessId });
            }
            Require(retained.Count == 0, "Retained workload owner after live dispatcher settling.");
        }
        // Dispose closes stdin once. The target reader then requests its own
        // loop exit; a second explicit shutdown races its control HWND.
        Row(new { Kind = "TargetEofShutdownRequested", Pid = targets.ProcessId });
        if (membership) { membershipField!.SetValue(null, null); Row(new { Kind = "MembershipAdapterRemoved", Calls = membershipCalls }); }
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<WeakOwner[]> SettingsLifetime()
    {
        var viewModel = new FancyWM.ViewModels.SettingsViewModel(State.Settings);
        var window = new FancyWM.Windows.SettingsWindow(viewModel) { ShowActivated = false };
        window.Show();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        Require(IsWindow(hwnd) && GetWindowThreadProcessId(hwnd, out uint pid) != 0 && pid == Environment.ProcessId, "Settings native HWND");
        var refs = new List<WeakOwner> { new("SettingsWindow", new(window)), new("SettingsViewModel", new(viewModel)), new("PageNavigation", new(Field(window, "m_pages")!)) };
        if (Environment.GetEnvironmentVariable("FWM_OWNED_MEMBERSHIP") == "1")
        {
            await Wait(() => ((WinMan.IWorkspace)Workspace).FindWindow(hwnd) != null, "native settings workspace owner");
            refs.Add(new("SettingsWorkspaceReference", new(((WinMan.IWorkspace)Workspace).FindWindow(hwnd)!)));
        }
        window.Close();
        Require(!IsWindow(hwnd) && System.Windows.Interop.HwndSource.FromHwnd(hwnd) == null && !App.Windows.Cast<System.Windows.Window>().Contains(window), "Settings native close");
        Row(new { Kind = "SettingsClosed", Hwnd = hwnd.ToInt64(), Destroyed = true, DispatcherAlive = !App.Dispatcher.HasShutdownStarted });
        return refs.ToArray();
    }
    [System.Runtime.InteropServices.DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(IntPtr process, int pid, IntPtr file, uint flags, IntPtr exception, IntPtr user, IntPtr callbacks);
    private sealed class TargetClient : IDisposable
    {
        private readonly System.Diagnostics.Process process;
        private readonly System.IO.StreamWriter protocol;
        private readonly Task<string> stderr;
        public int ProcessId => process.Id;
        public TargetClient(string exe)
        {
            var start = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(System.IO.Path.Combine(Output, "targets"));
            protocol = new(new System.IO.FileStream(System.IO.Path.Combine(Output, "target-protocol.jsonl"), System.IO.FileMode.CreateNew)) { AutoFlush = true };
            process = System.Diagnostics.Process.Start(start)!; stderr = process.StandardError.ReadToEndAsync();
            string line = process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult()!;
            protocol.WriteLine(line); using var json = System.Text.Json.JsonDocument.Parse(line); var ready = json.RootElement;
            Require(ready.GetProperty("Ready").GetBoolean() && ready.GetProperty("ProcessId").GetInt32() == process.Id && DesktopName(ready.GetProperty("OwnerThreadId").GetUInt32()) == Desktop, "Target inherited private desktop.");
            Row(new { Kind = "TargetReady", Pid = process.Id, Desktop });
        }
        public async Task<System.Text.Json.JsonElement> Command(object command)
        {
            string request = System.Text.Json.JsonSerializer.Serialize(command); protocol.WriteLine(request);
            await process.StandardInput.WriteLineAsync(request); await process.StandardInput.FlushAsync();
            string response = (await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)))!; protocol.WriteLine(response);
            using var json = System.Text.Json.JsonDocument.Parse(response); Require(json.RootElement.GetProperty("Success").GetBoolean(), response); return json.RootElement.Clone();
        }
        public void Dispose()
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(20000)) { process.Kill(true); process.WaitForExit(); throw new TimeoutException("Owned target failed to stop."); }
            Write("target-exit.json", new { Pid = process.Id, ExitCode = process.ExitCode, Stderr = stderr.GetAwaiter().GetResult() });
            Require(process.ExitCode == 0, "Target failure."); protocol.Dispose(); process.Dispose();
        }
    }
}
