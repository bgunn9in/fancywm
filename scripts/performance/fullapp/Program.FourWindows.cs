using System.Collections;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Reflection;
using FancyWM.Models;
using FancyWM.Utilities;
using FancyWM.ViewModels;
using WinMan;

internal static partial class Program
{
    // One native workload in the existing full-app host. No substituted windows,
    // production methods or settings adapters; counters include host observation.
    private static readonly BindableAction[] FourActions = [BindableAction.SwapRight,
        BindableAction.SwapLeft, BindableAction.SwapDown, BindableAction.SwapUp,
        BindableAction.MoveLeft, BindableAction.MoveRight, BindableAction.ToggleFocusedSatelliteSlot];

    private static KeybindingDictionary FourWindowBindings()
    {
        var bindings = new KeybindingDictionary(false);
        foreach (var action in Enum.GetValues<BindableAction>()) bindings[action] = null;
        for (int i = 0; i < FourActions.Length; i++)
            bindings[FourActions[i]] = new(new HashSet<KeyCode> { KeyCode.LeftCtrl, KeyCode.LeftAlt,
                (KeyCode)((int)KeyCode.F13 + i) }, true);
        return bindings;
    }

    private sealed record Slots(long Master, long[] Satellites, string Side, string Orientation, bool Mixed);
    private static Slots FourSlots()
    {
        var lifecycle = Field(TilingServices().Single(s => Nodes(s).Length == 4), "m_masterSatelliteLifecycle")!;
        var states = ((IEnumerable)lifecycle.GetType().GetMethod("SnapshotStates", Instance)!
            .Invoke(lifecycle, null)!).Cast<object>().Select(entry => Property(entry, "State")!);
        var state = states.Single(s => ((IEnumerable)Property(s, "Satellites")!).Cast<IWindow>().Count() == 3);
        return new(((IWindow)Property(state, "Master")!).Handle.ToInt64(),
            ((IEnumerable)Property(state, "Satellites")!).Cast<IWindow>().Select(w => w.Handle.ToInt64()).ToArray(),
            Property(state, "MasterSide")!.ToString()!, Property(state, "SatelliteOrientation")!.ToString()!,
            (bool)Property(state, "IsMixedLayout")!);
    }

    private static async Task RunFourWindows()
    {
        nint previousFocus = GetForegroundWindow();
        if (!GetCursorPos(out var previousCursor)) throw new InvalidOperationException("Cannot preserve cursor.");
        try
        {
            foreach (string layout in new[] { "Horizontal", "Vertical", "Mixed" })
            {
                await State.Settings.SaveAsync(s => s with { MasterSatelliteLayout = s.MasterSatelliteLayout with
                {
                    UseMixedSatellites = layout == "Mixed",
                    DefaultSatelliteOrientation = layout == "Horizontal" ? SatelliteLayoutOrientation.Horizontal : SatelliteLayoutOrientation.Vertical,
                } });
                await Task.Delay(200);
                var created = await Targets!.Command(new { Operation = "create", Count = 4 });
                await Idle(4);
                var initialGeometry = await VerifiedGeometry(4);
                // Existing active state can retain its orientation across an empty desktop.
                await FocusFour(FourSlots().Master);
                typeof(FancyWM.MainWindow).GetMethod("ExecuteAction", Instance)!.Invoke(MainWindowObject,
                    [layout == "Horizontal" ? BindableAction.CreateHorizontalPanel : BindableAction.CreateVerticalPanel, null]);
                await Idle(4); await VerifiedGeometry(4);
                var initial = FourSlots();
                if (initial.Side != "Left" || initial.Mixed != (layout == "Mixed")
                    || initial.Orientation != (layout == "Horizontal" ? "Horizontal" : "Vertical"))
                    throw new InvalidOperationException("Unexpected initial canonical layout.");
                Observations.Add(new { Kind = "FourWindowsCreated", Layout = layout, Targets = created,
                    Geometry = initialGeometry, Slots = initial, Warmups = 2, MeasuredRepeats = Iterations });
                using var overlay = new FourOverlayCounters();
                for (int repeat = -1; repeat <= Iterations; repeat++)
                {
                    await FourPair(layout, repeat, "satellite-swap", overlay,
                        layout == "Vertical" ? BindableAction.SwapDown : BindableAction.SwapRight,
                        layout == "Vertical" ? BindableAction.SwapUp : BindableAction.SwapLeft, 0, 1);
                    if (layout == "Mixed")
                        await FourPair(layout, repeat, "upper-wide", overlay,
                            BindableAction.ToggleFocusedSatelliteSlot, BindableAction.ToggleFocusedSatelliteSlot, 0, 2);
                    var beforePromotion = FourSlots();
                    await FourOperation(layout, repeat, "promotion", overlay, beforePromotion.Satellites[0], BindableAction.MoveLeft);
                    var promoted = FourSlots();
                    if (promoted.Master != beforePromotion.Satellites[0] || promoted.Satellites[0] != beforePromotion.Master)
                        throw new InvalidOperationException("Promotion did not preserve the vacated slot.");
                    await FourOperation(layout, repeat, "promotion-return", overlay, beforePromotion.Master, BindableAction.MoveLeft);
                    RequireFourSlots(beforePromotion);
                    long master = FourSlots().Master;
                    await FourOperation(layout, repeat, "master-right", overlay, master, BindableAction.MoveRight);
                    if (FourSlots().Master != master || FourSlots().Side != "Right") throw new InvalidOperationException("Master role/side changed incorrectly.");
                    await FourOperation(layout, repeat, "master-left", overlay, master, BindableAction.MoveLeft);
                    RequireFourSlots(beforePromotion);
                    int destination = layout == "Mixed" ? 2 : 1;
                    await FourOperation(layout, repeat, "drag-preview", overlay, FourSlots().Satellites[0], null, destination);
                    await FourOperation(layout, repeat, "drag-preview-return", overlay, FourSlots().Satellites[destination], null, 0);
                    RequireFourSlots(initial);
                }
                await Targets.Command(new { Operation = "close-all" }); await Idle(0);
            }
        }
        finally
        {
            SetCursorPos(previousCursor.X, previousCursor.Y);
            if (previousFocus != 0) SetForegroundWindow(previousFocus);
        }
    }

    private static void RequireFourSlots(Slots expected)
    {
        var actual = FourSlots();
        if (actual.Master != expected.Master || !actual.Satellites.SequenceEqual(expected.Satellites)
            || actual.Side != expected.Side || actual.Orientation != expected.Orientation || actual.Mixed != expected.Mixed)
            throw new InvalidOperationException("Canonical slots were not restored by the inverse operation.");
    }

    private static async Task FourPair(string layout, int repeat, string name, FourOverlayCounters overlay,
        BindableAction forward, BindableAction reverse, int source, int destination)
    {
        var before = FourSlots();
        await FourOperation(layout, repeat, name, overlay, before.Satellites[source], forward);
        var swapped = FourSlots();
        var expected = before.Satellites.ToArray();
        (expected[source], expected[destination]) = (expected[destination], expected[source]);
        if (swapped.Master != before.Master || !swapped.Satellites.SequenceEqual(expected))
            throw new InvalidOperationException("Satellite swap changed an unrelated slot.");
        await FourOperation(layout, repeat, name + "-return", overlay, before.Satellites[source], reverse);
        RequireFourSlots(before);
    }

    private static async Task FocusFour(long hwnd)
    {
        RequireReleasedInput();
        var row = (await VerifiedGeometry(4)).Single(r => r.Hwnd == hwnd);
        var point = new NativePoint((row.Actual.Left + row.Actual.Right) / 2, (row.Actual.Top + row.Actual.Bottom) / 2);
        if (GetWindowProcess(new(hwnd)) != Targets!.ProcessId) throw new InvalidOperationException("Foreign focus target.");
        if (!SetWindowPos(new(hwnd), 0, 0, 0, 0, 0, 0x0213)) throw new InvalidOperationException("Cannot raise owned target.");
        await WaitUntil(() => WindowFromPoint(point) == new nint(hwnd), "owned focus hit-test");
        RequireReleasedInput();
        await SendFourInput([FourMouse(point), Input.Mouse(0, 0, 2), Input.Mouse(0, 0, 4)]);
        await WaitUntil(() => GetForegroundWindow() == new nint(hwnd)
            && Property(Field(MainWindowObject!, "m_workspace")!, "FocusedWindow") is IWindow focused && focused.Handle == new nint(hwnd)
            && (Field(MainWindowObject!, "m_tiling")!.GetType().GetMethod("GetFocus", Instance)!
                .Invoke(Field(MainWindowObject!, "m_tiling"), null) as IWindow)?.Handle == new nint(hwnd), "native and backend focus");
        await Idle(4);
    }

    private static void RequireReleasedInput(bool allowAlt = false, bool allowLeftButton = false)
    {
        for (int key = 1; key < 255; key++)
        {
            if (allowAlt && key is 0x12 or 0xA4 || allowLeftButton && key == 1) continue;
            if ((GetAsyncKeyState(key) & 0x8000) != 0)
                throw new InvalidOperationException($"Input is in use (0x{key:X2}); automatic workload stopped.");
        }
    }

    // Native low-level mouse callbacks synchronously consult the WPF Dispatcher.
    // Keep it pumping while SendInput waits for native hook processing.
    private static Task SendFourInput(Input[] inputs) => Task.Run(() => SendOwnedInput(inputs));

    private static Input FourMouse(NativePoint point) => Input.Mouse(
        (int)((long)(point.X - GetSystemMetrics(76)) * 65535 / (GetSystemMetrics(78) - 1)),
        (int)((long)(point.Y - GetSystemMetrics(77)) * 65535 / (GetSystemMetrics(79) - 1)), 0xC001);

    private static async Task FourOperation(string layout, int repeat, string name, FourOverlayCounters overlay,
        long hwnd, BindableAction? action, int destination = -1)
    {
        await FocusFour(hwnd);
        var before = FourSlots();
        var renderer = Field(TilingServices().Single(s => Nodes(s).Length == 4), "m_gui")!;
        var viewModel = (TilingOverlayViewModel)Field(renderer, "m_viewModel")!;
        var overlayHost = Field(renderer, "m_overlay")!;
        var previewControl = (System.Windows.Controls.UserControl)Property(overlayHost, "NonHitTestableContent")!;
        var previewElement = ((System.Windows.Controls.Canvas)previewControl.Content).Children
            .OfType<System.Windows.Shapes.Rectangle>().Last();
        var previewHwnd = (nint)Field(overlayHost, "m_nonHitTestableHwnd")!;
        var geometry = await VerifiedGeometry(4);
        long acknowledged = 0;
        int notifications = 0;
        EventInfo? pressed = null; object? hook = null;
        EventHandler<EventArgs> handler = (_, _) => { notifications++; acknowledged = Stopwatch.GetTimestamp(); };
        if (action.HasValue)
        {
            int index = Array.IndexOf(FourActions, action.Value);
            hook = ((Array)Field(MainWindowObject!, "m_directHks")!).Cast<object>()
                .Single(h => (KeyCode)Property(h, "Key")! == (KeyCode)((int)KeyCode.F13 + index));
            pressed = hook.GetType().GetEvent("Pressed")!; pressed.AddEventHandler(hook, handler);
        }
        using var process = Process.GetCurrentProcess();
        double cpuStart = process.TotalProcessorTime.TotalMilliseconds;
        int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
        long allocatedStart = GC.GetTotalAllocatedBytes(false);
        var overlayStart = overlay.Snapshot();
        long start = Stopwatch.GetTimestamp();
        bool previewObserved = false;
        try
        {
            if (GetForegroundWindow() != new nint(hwnd)) throw new InvalidOperationException("Foreign foreground before input.");
            RequireReleasedInput();
            if (action.HasValue)
            {
                ushort key = (ushort)((int)KeyCode.F13 + Array.IndexOf(FourActions, action.Value));
                await SendFourInput([Input.Key(0xA2, false), Input.Key(0xA4, false), Input.Key(key, false),
                    Input.Key(key, true), Input.Key(0xA4, true), Input.Key(0xA2, true)]);
                await WaitUntil(() => notifications == 1, "automatic direct command callback");
            }
            else
            {
                var source = geometry.Single(g => g.Hwnd == hwnd).Actual;
                var target = geometry.Single(g => g.Hwnd == before.Satellites[destination]).Actual;
                int direction = Array.IndexOf(before.Satellites, hwnd) < destination ? 1 : -1;
                var from = new NativePoint((source.Left + source.Right) / 2, (source.Top + source.Bottom) / 2);
                var to = new NativePoint((target.Left + target.Right) / 2, (target.Top + target.Bottom) / 2);
                if (!before.Mixed) to = before.Orientation == "Horizontal"
                    ? to with { X = to.X + direction * target.Width / 4 } : to with { Y = to.Y + direction * target.Height / 4 };
                bool altOwned = false, buttonOwned = false;
                var lastPointer = from;
                void CheckDragInput()
                {
                    RequireReleasedInput(allowAlt: altOwned, allowLeftButton: buttonOwned);
                    if (GetWindowProcess(new(hwnd)) != Targets!.ProcessId || GetForegroundWindow() != new nint(hwnd)
                        || !GetCursorPos(out var pointer) || Math.Abs(pointer.X - lastPointer.X) > 2 || Math.Abs(pointer.Y - lastPointer.Y) > 2)
                        throw new InvalidOperationException("Owned drag input was interrupted; automatic workload stopped.");
                }
                try
                {
                    await SendFourInput([FourMouse(from)]);
                    await SendFourInput([Input.Key(0xA4, false)]);
                    altOwned = true;
                    await Task.Delay(30);
                    CheckDragInput();
                    await SendFourInput([Input.Mouse(0, 0, 2)]);
                    buttonOwned = true;
                    await WaitUntil(() =>
                    {
                        CheckDragInput();
                        return Field(TilingServices().Single(s => Nodes(s).Length == 4), "m_currentInteraction")!.ToString() is "Starting" or "Moving";
                    }, "modifier drag start");
                    for (int step = 1; step <= 6; step++)
                    {
                        CheckDragInput();
                        lastPointer = new(from.X + (to.X - from.X) * step / 6, from.Y + (to.Y - from.Y) * step / 6);
                        await SendFourInput([FourMouse(lastPointer)]);
                        await Task.Delay(16);
                    }
                    await WaitUntil(() =>
                    {
                        CheckDragInput();
                        return viewModel.IsPreviewRectangleVisible && previewElement.IsVisible
                            && previewElement.Opacity > 0 && IsWindowVisible(previewHwnd);
                    }, "preview element on a visible native overlay");
                    previewObserved = true; // WPF element/native HWND state, not a frame/pixel timestamp.
                    await Task.Delay(100);
                    CheckDragInput();
                }
                finally
                {
                    // Each successful press is owned across calls/awaits. Attempt
                    // the Alt release even if releasing the mouse button fails.
                    try { if (buttonOwned) await SendFourInput([Input.Mouse(0, 0, 4)]); }
                    finally { if (altOwned) await SendFourInput([Input.Key(0xA4, true)]); }
                }
                acknowledged = Stopwatch.GetTimestamp(); // Input submission end, not command-handler time.
            }
            await Idle(4);
            var afterGeometry = await VerifiedGeometry(4);
            long end = Stopwatch.GetTimestamp();
            long allocated = GC.GetTotalAllocatedBytes(false) - allocatedStart;
            int collections0 = GC.CollectionCount(0) - gc0, collections1 = GC.CollectionCount(1) - gc1, collections2 = GC.CollectionCount(2) - gc2;
            process.Refresh(); double cpu = process.TotalProcessorTime.TotalMilliseconds - cpuStart;
            var overlayEnd = overlay.Snapshot();
            var after = FourSlots();
            if (GetForegroundWindow() != new nint(hwnd)) throw new InvalidOperationException("Operation changed native focus.");
            if (!action.HasValue && (after.Master != before.Master || after.Satellites[destination] != hwnd
                || viewModel.IsPreviewRectangleVisible)) throw new InvalidOperationException("Drag commit/preview cleanup differs.");
            Observations.Add(new { Kind = "FourWindowOperation", Layout = layout, Repeat = repeat, Measured = repeat > 0,
                Operation = name, Action = action?.ToString(), Input = "automatic SendInput", InputCount = action.HasValue ? 6 : 11,
                StartQpc = start, CallbackOrDragInputEndQpc = acknowledged, NativeGeometryObservedQpc = end,
                ProcessCpuMs = cpu, ProcessAllocatedBytes = allocated, Gen0 = collections0, Gen1 = collections1, Gen2 = collections2,
                NewWindowModels = overlayEnd[0] - overlayStart[0], NewPanelModels = overlayEnd[1] - overlayStart[1],
                ChildCollectionChanges = overlayEnd[2] - overlayStart[2], ChildCollectionResets = overlayEnd[3] - overlayStart[3],
                PreviewObserved = previewObserved, Notifications = notifications, Before = before, After = after, Geometry = afterGeometry });
            Console.WriteLine($"{layout} repeat {repeat}: {name} CPU={cpu:F3}ms allocation={allocated}B");
        }
        finally { pressed?.RemoveEventHandler(hook, handler); }
    }

    private sealed class FourOverlayCounters : IDisposable
    {
        private readonly List<TilingOverlayViewModel> views = [];
        private readonly HashSet<TilingPanelViewModel> panels = [];
        private long windowsAdded, panelsAdded, childChanges, childResets;
        public FourOverlayCounters()
        {
            foreach (var service in TilingServices())
            {
                var view = (TilingOverlayViewModel)Field(Field(service, "m_gui")!, "m_viewModel")!;
                views.Add(view);
                view.WindowElements.CollectionChanged += WindowsChanged;
                view.PanelElements.CollectionChanged += PanelsChanged;
                foreach (var panel in view.PanelElements) Subscribe(panel);
            }
        }
        private void Subscribe(TilingPanelViewModel panel)
        { if (panels.Add(panel)) panel.ChildNodes.CollectionChanged += ChildrenChanged; }
        private void WindowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => windowsAdded += e.NewItems?.Count ?? 0;
        private void PanelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            panelsAdded += e.NewItems?.Count ?? 0;
            if (e.NewItems != null) foreach (TilingPanelViewModel panel in e.NewItems) Subscribe(panel);
        }
        private void ChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
        { childChanges++; if (e.Action == NotifyCollectionChangedAction.Reset) childResets++; }
        public long[] Snapshot() => [windowsAdded, panelsAdded, childChanges, childResets];
        public void Dispose()
        {
            foreach (var view in views)
            { view.WindowElements.CollectionChanged -= WindowsChanged; view.PanelElements.CollectionChanged -= PanelsChanged; }
            foreach (var panel in panels) panel.ChildNodes.CollectionChanged -= ChildrenChanged;
        }
    }
}
