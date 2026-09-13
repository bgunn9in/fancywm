using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using FancyWM.Layouts;
using FancyWM.Layouts.Tiling;

using WinMan;
using WinMan.Windows;

int[] sizes = [1, 4, 10, 25, 50];
const int runCount = 5;
const int warmupOperations = 2_000;

if (args.Contains("--verify-layout"))
{
    LayoutScenarios.Verify();
    return;
}

Console.WriteLine("experiment_id,snapshot_id,scenario,configuration,window_count,metric,unit,run,value,iterations,instrument");

if (args.Contains("--desktop-liveness-only"))
{
    MeasureDesktopLiveness();
    return;
}

if (!args.Contains("--layouts-only"))
using (var idleWorkspace = new Win32Workspace())
{
    var callbackMethod = typeof(Win32Workspace).GetMethod(
        "OnRecentTimerWatch",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(Win32Workspace).FullName, "OnRecentTimerWatch");
    var recentTimerCallback = (Action)callbackMethod.CreateDelegate(typeof(Action), idleWorkspace);
    Warmup(recentTimerCallback, warmupOperations);

    const int idleTickIterations = 250_000;
    for (int run = 1; run <= runCount; run++)
    {
        if ((run & 1) == 1)
        {
            Measure("EXP-WINMAN-RECENT-IDLE-BASELINE", "Win32Workspace.OnRecentTimerWatch, empty recent-window list", 0, run, idleTickIterations,
                recentTimerCallback);
            Measure("EXP-WINMAN-RECENT-IDLE-GUARD-MODEL", "Empty recent-window fast-path guard model", 0, run, idleTickIterations,
                EmptyRecentTimerGuardModel);
        }
        else
        {
            Measure("EXP-WINMAN-RECENT-IDLE-GUARD-MODEL", "Empty recent-window fast-path guard model", 0, run, idleTickIterations,
                EmptyRecentTimerGuardModel);
            Measure("EXP-WINMAN-RECENT-IDLE-BASELINE", "Win32Workspace.OnRecentTimerWatch, empty recent-window list", 0, run, idleTickIterations,
                recentTimerCallback);
        }
    }
}

if (args.Contains("--recent-only")) { return; }

foreach (int size in sizes)
{
    int iterations = Math.Max(5_000, 250_000 / size);

    var stableFlexBaseline = CreateFlex(size);
    var stableFlexGuarded = new Flex(stableFlexBaseline);
    Warmup(() => stableFlexBaseline.SetContainerWidth(3440), warmupOperations);
    Warmup(() => SetContainerWidthGuardModel(stableFlexGuarded, 3440), warmupOperations);

    for (int run = 1; run <= runCount; run++)
    {
        if ((run & 1) == 1)
        {
            Measure("EXP-FLEX-STABLE-BASELINE", "Flex.SetContainerWidth(same width), production", size, run, iterations,
                () => stableFlexBaseline.SetContainerWidth(3440));
            Measure("EXP-FLEX-STABLE-GUARD-MODEL", "Flex.SetContainerWidth(same width), experimental call-site guard model", size, run, iterations,
                () => SetContainerWidthGuardModel(stableFlexGuarded, 3440));
        }
        else
        {
            Measure("EXP-FLEX-STABLE-GUARD-MODEL", "Flex.SetContainerWidth(same width), experimental call-site guard model", size, run, iterations,
                () => SetContainerWidthGuardModel(stableFlexGuarded, 3440));
            Measure("EXP-FLEX-STABLE-BASELINE", "Flex.SetContainerWidth(same width), production", size, run, iterations,
                () => stableFlexBaseline.SetContainerWidth(3440));
        }
    }

    AssertEquivalent(stableFlexBaseline, stableFlexGuarded);

    var tree = CreateFlatTree(size);
    Warmup(() =>
    {
        tree.Measure();
        tree.Arrange();
    }, warmupOperations);

    for (int run = 1; run <= runCount; run++)
    {
        Measure("EXP-LAYOUT-STABLE-PASS", "DesktopTree.Measure+Arrange, unchanged flat tree", size, run, iterations,
            () =>
            {
                tree.Measure();
                tree.Arrange();
            });
    }
}

static Flex CreateFlex(int itemCount)
{
    var flex = new Flex();
    flex.SetContainerWidth(3440);
    for (int i = 0; i < itemCount; i++)
    {
        flex.InsertItem(i, 0, 10_000);
    }
    return flex;
}

static DesktopTree CreateFlatTree(int windowCount)
{
    var root = new SplitPanelNode
    {
        Orientation = PanelOrientation.Horizontal,
        Spacing = 4,
    };
    var tree = new DesktopTree
    {
        Root = root,
        WorkArea = Rectangle.OffsetAndSize(0, 0, 3440, 1440),
    };
    for (int i = 0; i < windowCount; i++)
    {
        root.Attach(new WindowNode(new FakeWindow((nint)(i + 1))));
    }
    tree.Measure();
    tree.Arrange();
    return tree;
}

static void SetContainerWidthGuardModel(Flex flex, double width)
{
    // Experimental model only: production is not changed by this audit.
    if (flex.ContainerWidth != width)
    {
        flex.SetContainerWidth(width);
    }
}

static void EmptyRecentTimerGuardModel()
{
    // Experimental model only: a real patch must arm/disarm the 10 ms timer
    // around the bounded recent-window observation period.
}

static void MeasureDesktopLiveness()
{
    foreach (int desktopCount in new[] { 1, 4, 10, 50 })
    {
        var fixture = new DesktopLivenessFixture(desktopCount);
        fixture.VerifyReplacementIdentity();
        Warmup(fixture.ReadAlive, 10_000);
        int iterations = Math.Max(10_000, 200_000 / desktopCount);

        for (int run = 1; run <= runCount; run++)
        {
            Measure(
                "EXP-WINMAN-DESKTOP-LIVENESS",
                $"Win32VirtualDesktop.IsAlive, last live wrapper, desktop-count={desktopCount}",
                0,
                run,
                iterations,
                fixture.ReadAlive);
        }
    }
}

static void AssertEquivalent(Flex baseline, Flex candidate)
{
    if (baseline.Count != candidate.Count || baseline.ContainerWidth != candidate.ContainerWidth)
    {
        throw new InvalidOperationException("Guard model changed Flex container semantics.");
    }
    for (int i = 0; i < baseline.Count; i++)
    {
        if (baseline[i].Width != candidate[i].Width
            || baseline[i].MinWidth != candidate[i].MinWidth
            || baseline[i].MaxWidth != candidate[i].MaxWidth)
        {
            throw new InvalidOperationException($"Guard model diverged at item {i}.");
        }
    }
}

static void Warmup(Action operation, int iterations)
{
    for (int i = 0; i < iterations; i++)
    {
        operation();
    }
}

static void Measure(
    string experimentId,
    string scenario,
    int windowCount,
    int run,
    int iterations,
    Action operation)
{
    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();
    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

    long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    int gen0Before = GC.CollectionCount(0);
    int gen1Before = GC.CollectionCount(1);
    int gen2Before = GC.CollectionCount(2);
    long timestampBefore = Stopwatch.GetTimestamp();

    for (int i = 0; i < iterations; i++)
    {
        operation();
    }

    long elapsedTicks = Stopwatch.GetTimestamp() - timestampBefore;
    long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    double nanosecondsPerOperation = elapsedTicks * (1_000_000_000d / Stopwatch.Frequency) / iterations;
    double bytesPerOperation = (double)allocatedBytes / iterations;

    WriteMetric(experimentId, scenario, windowCount, "elapsed", "ns/op", run, nanosecondsPerOperation, iterations, "Stopwatch.GetTimestamp");
    WriteMetric(experimentId, scenario, windowCount, "managed_allocated", "bytes/op", run, bytesPerOperation, iterations, "GC.GetAllocatedBytesForCurrentThread");
    WriteMetric(experimentId, scenario, windowCount, "gen0_collections", "count/run", run, GC.CollectionCount(0) - gen0Before, iterations, "GC.CollectionCount");
    WriteMetric(experimentId, scenario, windowCount, "gen1_collections", "count/run", run, GC.CollectionCount(1) - gen1Before, iterations, "GC.CollectionCount");
    WriteMetric(experimentId, scenario, windowCount, "gen2_collections", "count/run", run, GC.CollectionCount(2) - gen2Before, iterations, "GC.CollectionCount");
}

static void WriteMetric(
    string experimentId,
    string scenario,
    int windowCount,
    string metric,
    string unit,
    int run,
    double value,
    int iterations,
    string instrument)
{
    string snapshotId = Environment.GetEnvironmentVariable("FWM_PERF_SNAPSHOT") ?? "UNSPECIFIED-CURRENT-SOURCE";
    static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
    Console.WriteLine(string.Join(',',
        Csv(experimentId),
        Csv(snapshotId),
        Csv(scenario),
        Csv($"Release; net10.0-windows7.0; x64; no debugger; DOTNET_TieredCompilation={Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default"}"),
        windowCount.ToString(CultureInfo.InvariantCulture),
        Csv(metric),
        Csv(unit),
        run.ToString(CultureInfo.InvariantCulture),
        value.ToString("R", CultureInfo.InvariantCulture),
        iterations.ToString(CultureInfo.InvariantCulture),
        Csv(instrument)));
}

internal sealed class FakeWindow(nint handle) : IWindow
{
#pragma warning disable CS0067
    public event EventHandler<WindowPositionChangedEventArgs>? PositionChangeStart;
    public event EventHandler<WindowPositionChangedEventArgs>? PositionChangeEnd;
    public event EventHandler<WindowPositionChangedEventArgs>? PositionChanged;
    public event EventHandler<WindowStateChangedEventArgs>? StateChanged;
    public event EventHandler<WindowTopmostChangedEventArgs>? TopmostChanged;
    public event EventHandler<WindowFocusChangedEventArgs>? GotFocus;
    public event EventHandler<WindowFocusChangedEventArgs>? LostFocus;
    public event EventHandler<WindowChangedEventArgs>? Added;
    public event EventHandler<WindowChangedEventArgs>? Removed;
    public event EventHandler<WindowChangedEventArgs>? Destroyed;
    public event EventHandler<WindowTitleChangedEventArgs>? TitleChanged;
#pragma warning restore CS0067

    public object SyncRoot { get; } = new();
    public IWorkspace Workspace => throw new NotSupportedException();
    public string Title => "performance-fake";
    public Rectangle Position => Rectangle.OffsetAndSize(0, 0, 800, 600);
    public WindowState State => WindowState.Restored;
    public Point? MinSize { get; set; }
    public Point? MaxSize => null;
    public Rectangle FrameMargins => new();
    public bool CanResize => true;
    public bool CanMove => true;
    public bool CanReorder => true;
    public bool CanMinimize => true;
    public bool CanMaximize => true;
    public bool CanClose => true;
    public bool IsTopmost => false;
    public bool IsFocused => false;
    public bool IsAlive => true;
    public nint Handle { get; } = handle;

    public Process GetProcess() => throw new NotSupportedException();
    public IWindow? GetPreviousWindow() => null;
    public IWindow? GetNextWindow() => null;
    public void Close() => throw new NotSupportedException();
    public void SetPosition(Rectangle newLocation) => throw new NotSupportedException();
    public void SetState(WindowState state) => throw new NotSupportedException();
    public void SetTopmost(bool topmost) => throw new NotSupportedException();
    public void InsertAfter(IWindow other) => throw new NotSupportedException();
    public void SendToBack() => throw new NotSupportedException();
    public void BringToFront() => throw new NotSupportedException();
    public bool RequestFocus() => throw new NotSupportedException();
    public bool Equals(IWindow? other) => ReferenceEquals(this, other);
}

internal sealed class DesktopLivenessFixture
{
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo DescriptorField = typeof(Win32VirtualDesktop)
        .GetField("m_desktop", InstanceFields)!;
    private static readonly Type DescriptorType = DescriptorField.FieldType;

    private readonly object m_syncRoot = new();
    private readonly List<Win32VirtualDesktop> m_desktops = new();
    private readonly Win32Workspace m_workspace;
    private readonly Guid m_targetGuid;

    public Win32VirtualDesktopManager Manager { get; }
    public Win32VirtualDesktop Target { get; }

    public DesktopLivenessFixture(int desktopCount)
    {
        m_workspace = Uninitialized<Win32Workspace>();
        Manager = Uninitialized<Win32VirtualDesktopManager>();
        SetField(Manager, "m_syncRoot", m_syncRoot);
        SetField(Manager, "m_desktops", m_desktops);
        SetField(m_workspace, "m_eventLoopThread", Thread.CurrentThread);
        SetField(m_workspace, "m_virtualDesktops", Manager);

        Win32VirtualDesktop? target = null;
        Guid targetGuid = default;
        for (int index = 0; index < desktopCount; index++)
        {
            var guid = Guid.NewGuid();
            var desktop = CreateDesktop(guid);
            m_desktops.Add(desktop);
            if (index == desktopCount - 1)
            {
                targetGuid = guid;
                target = desktop;
            }
        }
        Target = target ?? throw new ArgumentOutOfRangeException(nameof(desktopCount));
        m_targetGuid = targetGuid;
    }

    public void VerifyReplacementIdentity()
    {
        var snapshot = Manager.Desktops;
        if (!Target.IsAlive || !Remove(Target) || Target.IsAlive)
        {
            throw new InvalidOperationException("Desktop removal liveness diverged.");
        }

        var replacement = CreateDesktop(m_targetGuid);
        Add(replacement);
        if (Target.IsAlive || !replacement.IsAlive || snapshot.Count != m_desktops.Count
            || !ReferenceEquals(snapshot[^1], Target))
        {
            throw new InvalidOperationException("Same-GUID replacement or snapshot identity diverged.");
        }

        if (!Remove(replacement))
        {
            throw new InvalidOperationException("Replacement desktop was not published.");
        }
        Add(Target);
        ReadAlive();
    }

    public void ReadAlive()
    {
        if (!Target.IsAlive)
        {
            throw new InvalidOperationException("Published desktop became dead during measurement.");
        }
    }

    private Win32VirtualDesktop CreateDesktop(Guid guid)
    {
        var desktop = Uninitialized<Win32VirtualDesktop>();
        SetField(desktop, "m_workspace", m_workspace);
        DescriptorField.SetValue(desktop, Activator.CreateInstance(DescriptorType, guid)!);
        return desktop;
    }

    private void Add(Win32VirtualDesktop desktop)
    {
        lock (m_syncRoot) { m_desktops.Add(desktop); }
    }

    private bool Remove(Win32VirtualDesktop desktop)
    {
        lock (m_syncRoot) { return m_desktops.Remove(desktop); }
    }

    private static T Uninitialized<T>() where T : class =>
        (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static void SetField(object owner, string name, object value) =>
        owner.GetType().GetField(name, InstanceFields)!.SetValue(owner, value);
}
