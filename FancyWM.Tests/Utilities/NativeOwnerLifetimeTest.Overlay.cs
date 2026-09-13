#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using FancyWM.Models;
using FancyWM.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WinMan;

namespace FancyWM.Tests.Utilities;

public partial class NativeOwnerLifetimeTest
{
    [TestMethod]
    public Task OverlayNativeCloseAndStaleCallbacks() => Isolated(nameof(OverlayNativeCloseAndStaleCallbacks), () =>
    {
        foreach (string mode in new[] { "hidden", "shown", "reentrant", "worker" })
        {
            using var owners = new DisplayOwners();
            var host = new OverlayHost(owners.Display); var pair = Pair(host);
            host.Content = new ContentControl(); host.NonHitTestableContent = new ContentControl();
            int closed = 0, anchorCalls = 0;
            host.AnchorSource = () => { anchorCalls++; return IntPtr.Zero; };
            foreach (var window in new[] { pair.Hit, pair.NonHit })
                window.Closed += (_, _) => { closed++; if (mode == "reentrant") host.Close(); };
            if (mode != "hidden")
            {
                host.Show(); Drain();
                Assert.IsNotNull(Field<object?>(Field<object>(host, "m_refreshLoop"), "m_subscription"));
                owners.Raise(); Settings.Emit();
            }
            if (mode == "worker") Complete(Task.Run(host.Close)); else host.Close();
            host.Close(); int callsAtClose = anchorCalls;
            owners.Late(); Settings.Late(); host.Show(); host.Hide(); host.UpdatePositions();
            Complete(Task.Delay(1200)); Drain();
            Assert.AreEqual(callsAtClose, anchorCalls); Assert.AreEqual(2, closed);
            AssertOverlayClosed(host, pair); owners.AssertReleased(); Assert.AreEqual(0, Settings.Active);
            Row(new { scenario = "overlay-close", mode, hit = pair.HitHandle.ToInt64(), nonhit = pair.NonHitHandle.ToInt64(),
                owner = pair.NonHitHandle.ToInt64(), wpfOwner = pair.WpfOwner.ToInt64(), destroyed = true, closed, subscriptions = 0, timers = 0,
                stale = true, dispatcherAlive = Alive });
            Settings.Clear();
        }
    });

    [TestMethod]
    public Task OverlayNativeFailureOrdering() => Isolated(nameof(OverlayNativeFailureOrdering), () =>
    {
        foreach (string mode in new[] { "workspace", "display", "settings", "closed" })
        {
            using var owners = new DisplayOwners();
            var host = new OverlayHost(owners.Display); var pair = Pair(host);
            host.Show(); Drain();
            var error = new InvalidOperationException("owned overlay " + mode + " release failure");
            if (mode == "workspace") { owners.FailureEvent = "cursor"; owners.Failure = error; }
            if (mode == "display") { owners.FailureEvent = "area"; owners.Failure = error; }
            if (mode == "settings") Settings.ReleaseFailure = error;
            int closed = 0;
            pair.Hit.Closed += (_, _) => closed++;
            pair.NonHit.Closed += (_, _) => { closed++; if (mode == "closed") throw error; };
            var errors = Observe(error, host.Close);
            Assert.AreEqual(1, errors.Count, "Exact injected error must cross one real boundary.");
            Assert.AreSame(error, errors.Single());
            host.Close(); owners.Late(); Settings.Late(); Drain();
            AssertOverlayClosed(host, pair); owners.AssertReleased(); Assert.AreEqual(0, Settings.Active);
            Assert.AreEqual(2, closed);
            Row(new { scenario = "overlay-failure", mode, hit = pair.HitHandle.ToInt64(), nonhit = pair.NonHitHandle.ToInt64(),
                owner = pair.NonHitHandle.ToInt64(), wpfOwner = pair.WpfOwner.ToInt64(), destroyed = true, errors = errors.Count, closed,
                subscriptions = 0, timers = 0, originalException = true, dispatcherAlive = Alive });
            Settings.Clear();
        }
    });

    [TestMethod]
    public Task OverlayNativeRetention() => Isolated(nameof(OverlayNativeRetention), () => Retention("overlay", CreateOverlay));

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference[] CreateOverlay()
    {
        using var owners = new DisplayOwners();
        var host = new OverlayHost(owners.Display); var pair = Pair(host);
        host.Content = new ContentControl(); host.NonHitTestableContent = new ContentControl();
        WeakReference[] weak = [new(host), new(pair.Hit), new(pair.NonHit), new(host.Content), new(host.NonHitTestableContent),
            new(HwndSource.FromHwnd(pair.HitHandle)), new(HwndSource.FromHwnd(pair.NonHitHandle))];
        host.Close(); AssertOverlayClosed(host, pair); owners.AssertReleased();
        Assert.AreEqual(0, Settings.Active); Settings.Clear();
        return weak;
    }

    private sealed record WindowPair(Window Hit, Window NonHit, IntPtr HitHandle, IntPtr NonHitHandle, IntPtr WpfOwner);
    private static WindowPair Pair(OverlayHost host)
    {
        var hit = Field<Window>(host, "m_window"); var nonhit = Field<Window>(host, "m_nonHitTestableWindow");
        var h = Handle(hit); var n = Handle(nonhit);
        Assert.AreNotEqual(h, n); Assert.AreEqual(n, GetWindow(h, 4), "GW_OWNER must point to noninteractive HWND.");
        // ShowInTaskbar=false gives the lower surface WPF's hidden owner HWND.
        // The explicit production relationship is hit -> nonhit -> WPF owner.
        IntPtr lowerOwner = GetWindow(n, 4);
        Assert.AreNotEqual(IntPtr.Zero, lowerOwner);
        uint ownerThread = GetWindowThreadProcessId(lowerOwner, out uint ownerPid);
        Assert.AreEqual((uint)Environment.ProcessId, ownerPid);
        Assert.AreEqual(GetCurrentThreadId(), ownerThread);
        Assert.AreEqual(2, Settings.Active);
        return new(hit, nonhit, h, n, lowerOwner);
    }
    private static void AssertOverlayClosed(OverlayHost host, WindowPair pair)
    {
        AssertClosed(pair.Hit, pair.HitHandle); AssertClosed(pair.NonHit, pair.NonHitHandle);
        Assert.IsNull(pair.Hit.Content); Assert.IsNull(pair.NonHit.Content); Assert.IsNull(host.AnchorSource);
        Assert.IsTrue(Field<bool>(host, "m_closed")); Assert.AreEqual(0L, Field<long>(host, "m_activeCursorGeneration"));
        object loop = Field<object>(host, "m_refreshLoop");
        Assert.IsTrue(Field<bool>(loop, "m_disposed")); Assert.IsNull(Field<object?>(loop, "m_subscription"));
    }

    private sealed class Entity : IObservableFileEntity<Settings>
    {
        private Settings m_value = new();
        private readonly List<IObserver<Settings>> m_active = [];
        private readonly List<IObserver<Settings>> m_late = [];
        public int Active => m_active.Count;
        public int Releases, Flushes;
        public Exception? ReleaseFailure;
        public string FullPath => "controlled-native-owners-settings.json";
        public IObservable<Settings> Value => this;
        public IDisposable Subscribe(IObserver<Settings> observer)
        {
            m_active.Add(observer); m_late.Add(observer); observer.OnNext(m_value);
            return Disposable.Create(() =>
            {
                Assert.IsTrue(m_active.Remove(observer)); Releases++;
                var error = ReleaseFailure; ReleaseFailure = null;
                if (error != null) throw error;
            });
        }
        public Task SaveAsync(Func<Settings, Settings> update) { m_value = update(m_value); Emit(); return Task.CompletedTask; }
        public Task FlushAsync() { Flushes++; return Task.CompletedTask; }
        public void Emit() { m_value = m_value with { PanelFontSize = m_value.PanelFontSize + 1 }; foreach (var observer in m_active.ToArray()) observer.OnNext(m_value); }
        public void Late() { foreach (var observer in m_late) observer.OnNext(m_value with { PanelFontSize = 30 }); }
        public void Clear() { Assert.AreEqual(0, Active); m_late.Clear(); ReleaseFailure = null; }
    }

    private sealed class DisplayOwners : IDisposable
    {
        private readonly Mock<IDisplay> m_display = new();
        private readonly Mock<IWorkspace> m_workspace = new();
        private readonly Dictionary<string, Delegate?> m_active = [], m_late = [];
        public string? FailureEvent;
        public Exception? Failure;
        public Exception? RemovalFailure;
        public string? ConstructionBoundary;
        public IntPtr[] AcquiredHandles = [];
        public IDisplay Display => m_display.Object;
        public DisplayOwners()
        {
            // Small transparent fixture surface: native location is owned, no desktop
            // changes, foreign HWND movement, injected input or real hook subscription.
            var area = new WinMan.Rectangle(-30000, -30000, -29968, -29968);
            m_display.SetupGet(x => x.Bounds).Returns(area);
            m_display.SetupGet(x => x.WorkArea).Returns(() => { FailConstruction("position"); return area; });
            m_display.SetupGet(x => x.Scaling).Returns(1); m_display.SetupGet(x => x.Workspace).Returns(m_workspace.Object);
            m_workspace.SetupGet(x => x.CursorLocation).Returns(new WinMan.Point(-29999, -29999));
            m_display.SetupAdd(x => x.WorkAreaChanged += It.IsAny<EventHandler<DisplayRectangleChangedEventArgs>>())
                .Callback<EventHandler<DisplayRectangleChangedEventArgs>>(d => Add("area", d));
            m_display.SetupRemove(x => x.WorkAreaChanged -= It.IsAny<EventHandler<DisplayRectangleChangedEventArgs>>())
                .Callback<EventHandler<DisplayRectangleChangedEventArgs>>(d => Remove("area", d));
            m_display.SetupAdd(x => x.ScalingChanged += It.IsAny<EventHandler<DisplayScalingChangedEventArgs>>())
                .Callback<EventHandler<DisplayScalingChangedEventArgs>>(d => Add("scale", d));
            m_display.SetupRemove(x => x.ScalingChanged -= It.IsAny<EventHandler<DisplayScalingChangedEventArgs>>())
                .Callback<EventHandler<DisplayScalingChangedEventArgs>>(d => Remove("scale", d));
            m_workspace.SetupAdd(x => x.CursorLocationChanged += It.IsAny<EventHandler<CursorLocationChangedEventArgs>>())
                .Callback<EventHandler<CursorLocationChangedEventArgs>>(d => Add("cursor", d));
            m_workspace.SetupRemove(x => x.CursorLocationChanged -= It.IsAny<EventHandler<CursorLocationChangedEventArgs>>())
                .Callback<EventHandler<CursorLocationChangedEventArgs>>(d => Remove("cursor", d));
            m_workspace.SetupAdd(x => x.FocusedWindowChanged += It.IsAny<EventHandler<FocusedWindowChangedEventArgs>>())
                .Callback<EventHandler<FocusedWindowChangedEventArgs>>(d => Add("focus", d));
            m_workspace.SetupRemove(x => x.FocusedWindowChanged -= It.IsAny<EventHandler<FocusedWindowChangedEventArgs>>())
                .Callback<EventHandler<FocusedWindowChangedEventArgs>>(d => Remove("focus", d));
            m_workspace.SetupAdd(x => x.WindowAdded += It.IsAny<EventHandler<WindowChangedEventArgs>>())
                .Callback<EventHandler<WindowChangedEventArgs>>(d => Add("added", d));
            m_workspace.SetupRemove(x => x.WindowAdded -= It.IsAny<EventHandler<WindowChangedEventArgs>>())
                .Callback<EventHandler<WindowChangedEventArgs>>(d => Remove("added", d));
            m_workspace.SetupAdd(x => x.WindowRemoved += It.IsAny<EventHandler<WindowChangedEventArgs>>())
                .Callback<EventHandler<WindowChangedEventArgs>>(d => Add("removed", d));
            m_workspace.SetupRemove(x => x.WindowRemoved -= It.IsAny<EventHandler<WindowChangedEventArgs>>())
                .Callback<EventHandler<WindowChangedEventArgs>>(d => Remove("removed", d));
        }
        private void Add(string key, Delegate value)
        {
            m_active.TryGetValue(key, out var current); m_active[key] = Delegate.Combine(current, value);
            m_late[key] = m_active[key];
            FailConstruction(key + "-add");
        }
        private void FailConstruction(string boundary)
        {
            if (ConstructionBoundary == "scale-second-add" && boundary == "scale-add"
                && m_active["scale"]!.GetInvocationList().Length == 2) boundary = "scale-second-add";
            if (ConstructionBoundary != boundary) return;
            ConstructionBoundary = null;
            AcquiredHandles = Application.Current.Windows.Cast<Window>()
                .Where(w => w.GetType().Name == "OverlayWindow")
                .Select(w => new WindowInteropHelper(w).Handle).Where(h => h != IntPtr.Zero).ToArray();
            throw Failure!;
        }
        private void Remove(string key, Delegate value)
        {
            // Ordinary .NET events allow removing an absent handler, including
            // rollback before a constructor reached the corresponding add.
            m_active.TryGetValue(key, out var current);
            m_active[key] = Delegate.Remove(current, value);
            if (FailureEvent == key) { FailureEvent = null; throw RemovalFailure ?? Failure!; }
        }
        public void Raise() => Invoke(m_active);
        public void Late() => Invoke(m_late);
        private void Invoke(Dictionary<string, Delegate?> callbacks)
        {
            foreach (var pair in callbacks)
                pair.Value?.DynamicInvoke(pair.Key is "area" or "scale" ? m_display.Object : m_workspace.Object, null);
        }
        public void AssertReleased()
        {
            Assert.IsTrue(m_active.Values.All(x => x == null), "Display/workspace owns a closed window or host.");
            m_late.Clear(); m_display.Invocations.Clear(); m_workspace.Invocations.Clear();
        }
        // Every successful path calls AssertReleased explicitly. Do not replace
        // the primary native failure with a second assertion during unwind.
        public void Dispose() { m_late.Clear(); }
    }
}
