#nullable enable
using System;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using FancyWM.AlgorithmicLayouts;
using FancyWM.Layouts.Tiling;
using FancyWM.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    public partial class MasterSatelliteDropControllerTest
    {
        [TestMethod]
        public void PreviewCacheManagedCounterScenario()
        {
            foreach (int count in new[] { 1, 4, 10 })
            {
                var windows = Enumerable.Range(0, count).Select(index => new PreviewManagedWindow(index + 1)).ToArray();
                var context = CreateManagedPreviewContext(windows);
                var inputs = Enumerable.Range(0, 1000).Select(index =>
                {
                    if (count == 1)
                    {
                        return (context.Windows[0], RectangleFor(context, context.Windows[0]).Center);
                    }
                    var (source, pointer) = PreviewInput(context, (MasterSatelliteDropKind)(index / 250));
                    return (source, new Point(pointer.X + index % 2, pointer.Y + index % 2));
                }).ToArray();
                var liveTree = context.Workspace.GetTree(context.Desktop)!;
                var liveNodes = context.Windows.Select(liveTree.FindNode).ToArray();
                var before = Snapshot(context);
                var plans = new MasterSatelliteDropPlan[inputs.Length];
                for (int kind = 0; kind < 4; kind++)
                {
                    for (int warmup = 0; warmup < 20; warmup++)
                    {
                        var input = inputs[kind * 250 + warmup];
                        PreviewAt(context, input.Item1, input.Item2);
                    }
                }
                foreach (var window in windows) window.MinimumReads = 0;
                long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                for (int index = 0; index < inputs.Length; index++)
                {
                    var input = inputs[index];
                    plans[index] = PreviewAt(context, input.Item1, input.Item2);
                }
                long ticks = Stopwatch.GetTimestamp() - started;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
                int measuredReads = windows.Sum(window => window.MinimumReads);
                var digest = new StringBuilder();
                for (int index = 0; index < plans.Length; index++)
                {
                    var expected = PreviewAt(context with { Controller = new MasterSatelliteDropController() },
                        inputs[index].Item1, inputs[index].Item2);
                    var actual = plans[index];
                    AssertEquivalentPlan(expected, actual);
                    Assert.AreEqual(count > 1, actual.IsAccepted, actual.Message);
                    digest.Append(actual.IsAccepted).Append('|').Append(actual.Kind).Append('|')
                        .Append(Array.IndexOf(context.Windows, actual.Source)).Append('|')
                        .Append(Array.IndexOf(context.Windows, actual.Target)).Append('|')
                        .Append(actual.FromSatelliteIndex).Append('|').Append(actual.ToSatelliteIndex).Append('|')
                        .Append(actual.TargetMasterSide).Append('|').Append(actual.PreviewRectangle).Append('|');
                    if (actual.PreviewOperation is { } operation)
                    {
                        AppendPreviewSnapshot(digest, context.Windows, operation.Before);
                        AppendPreviewSnapshot(digest, context.Windows, operation.After);
                    }
                    digest.AppendLine();
                }
                Assert.AreEqual(before, Snapshot(context));
                CollectionAssert.AreEqual(liveNodes, context.Windows.Select(liveTree.FindNode).ToArray());
                CollectionAssert.AreEqual(context.Windows.Skip(1).ToArray(), GetState(context).Satellites.ToArray());
                Assert.IsTrue(measuredReads >= count * inputs.Length,
                    "Every pointer must retain the first fresh measurement of each window.");
                Console.WriteLine($"PERFCOUNTER preview-managed-{count} allocated-bytes {allocated}");
                Console.WriteLine($"PERFCOUNTER preview-managed-{count} minimum-reads {measuredReads}");
                Console.WriteLine($"PERFCOUNTER preview-managed-{count} elapsed-ticks {ticks}");
                Console.WriteLine($"PERFCOUNTER preview-managed-{count} timestamp-frequency {Stopwatch.Frequency}");
                Console.WriteLine($"PERFCOUNTER preview-managed-{count} pointers {inputs.Length}");
                Console.WriteLine($"PERFCOUNTER preview-managed-{count} accepted-plans {plans.Count(plan => plan.IsAccepted)}");
                Console.WriteLine($"PERFCOUNTER preview-managed-{count} plan-digest {Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(digest.ToString())))}");
            }
        }

        private ActiveContext CreateManagedPreviewContext(PreviewManagedWindow[] windows)
        {
            var settings = new MasterSatelliteLayoutSettings
            {
                Enabled = true,
                DisplayScope = AlgorithmicLayoutDisplayScope.AllDisplays,
                MasterRatio = 0.60,
                DefaultMasterSide = MasterSide.Left,
                DefaultSatelliteOrientation = SatelliteLayoutOrientation.Vertical,
                MaxSatellites = Math.Max(3, windows.Length - 1),
            };
            var display = new PreviewManagedDisplay(m_workArea);
            var desktop = new PreviewManagedDesktop();
            var lifecycle = new MasterSatelliteRuntimeLifecycle(display);
            var workspace = new TilingWorkspace();
            workspace.RegisterDesktop(desktop, m_workArea, PanelOrientation.Horizontal);
            var root = workspace.GetTree(desktop)!.Root!;
            foreach (var window in windows) workspace.RegisterWindow(window, root);
            var activation = lifecycle.ApplySettings(workspace, settings, display, desktop);
            Assert.IsTrue(activation.Operation!.Succeeded, activation.Operation.Message);
            Assert.IsTrue(lifecycle.TryGetState(desktop, out _));
            return new(workspace, lifecycle, new MasterSatelliteDropController(), desktop, settings, windows);
        }

        private sealed class PreviewManagedDisplay(Rectangle workArea) : IDisplay
        {
            public IWorkspace Workspace => throw UnsupportedPreviewOperation();
            public Rectangle WorkArea { get; } = workArea;
            public Rectangle Bounds => WorkArea;
            public double Scaling => 1.0;
            public int RefreshRate => 60;
            public bool Equals(IDisplay? other) => ReferenceEquals(this, other);
            public event EventHandler<DisplayChangedEventArgs>? Removed
                { add => throw UnsupportedPreviewOperation(); remove => throw UnsupportedPreviewOperation(); }
            public event EventHandler<DisplayRectangleChangedEventArgs>? WorkAreaChanged
                { add => throw UnsupportedPreviewOperation(); remove => throw UnsupportedPreviewOperation(); }
            public event EventHandler<DisplayRectangleChangedEventArgs>? BoundsChanged
                { add => throw UnsupportedPreviewOperation(); remove => throw UnsupportedPreviewOperation(); }
            public event EventHandler<DisplayScalingChangedEventArgs>? ScalingChanged
                { add => throw UnsupportedPreviewOperation(); remove => throw UnsupportedPreviewOperation(); }
            public event EventHandler<DisplayRefreshRateChangedEventArgs>? RefreshRateChanged
                { add => throw UnsupportedPreviewOperation(); remove => throw UnsupportedPreviewOperation(); }
        }

        private sealed class PreviewManagedDesktop : IVirtualDesktop
        {
            public IWorkspace Workspace => throw UnsupportedPreviewOperation();
            public bool IsAlive => true;
            public bool IsCurrent => true;
            public int Index => 0;
            public string Name => "D1";
            public event EventHandler<DesktopChangedEventArgs>? Removed
                { add => throw UnsupportedPreviewOperation(); remove => throw UnsupportedPreviewOperation(); }
            public bool HasWindow(IWindow window) => throw UnsupportedPreviewOperation();
            public void MoveWindow(IWindow window) => throw UnsupportedPreviewOperation();
            public void SwitchTo() => throw UnsupportedPreviewOperation();
            public void SetName(string name) => throw UnsupportedPreviewOperation();
            public void Remove() => throw UnsupportedPreviewOperation();
        }

        private sealed class PreviewManagedWindow(int id) : IWindow
        {
            public int MinimumReads;
            public object SyncRoot { get; } = new();
            public IWorkspace Workspace => throw UnsupportedPreviewOperation();
            public string Title { get; } = ((char)('A' + id - 1)).ToString();
            public Rectangle Position => Rectangle.OffsetAndSize(0, 0, 640, 480);
            public WindowState State => WindowState.Restored;
            public Point? MinSize { get { MinimumReads++; return new Point(0, 0); } }
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
            public IntPtr Handle { get; } = new(id);
            public bool Equals(IWindow? other) => ReferenceEquals(this, other);
            public override bool Equals(object? other) => ReferenceEquals(this, other);
            public override int GetHashCode() => Handle.GetHashCode();
            public Process GetProcess() => throw UnsupportedPreviewOperation();
            public IWindow? GetPreviousWindow() => throw UnsupportedPreviewOperation();
            public IWindow? GetNextWindow() => throw UnsupportedPreviewOperation();
            public void Close() => throw UnsupportedPreviewOperation();
            public void SetPosition(Rectangle position) => throw UnsupportedPreviewOperation();
            public void SetState(WindowState state) => throw UnsupportedPreviewOperation();
            public void SetTopmost(bool topmost) => throw UnsupportedPreviewOperation();
            public void InsertAfter(IWindow other) => throw UnsupportedPreviewOperation();
            public void SendToBack() => throw UnsupportedPreviewOperation();
            public void BringToFront() => throw UnsupportedPreviewOperation();
            public bool RequestFocus() => throw UnsupportedPreviewOperation();
            public event EventHandler<WindowPositionChangedEventArgs>? PositionChangeStart
                { add => throw UnsupportedPreviewOperation(); remove => throw UnsupportedPreviewOperation(); }
            public event EventHandler<WindowPositionChangedEventArgs>? PositionChangeEnd
                { add => throw UnsupportedPreviewOperation(); remove => throw UnsupportedPreviewOperation(); }
            public event EventHandler<WindowPositionChangedEventArgs>? PositionChanged
                { add => throw UnsupportedPreviewOperation(); remove => throw UnsupportedPreviewOperation(); }
            public event EventHandler<WindowStateChangedEventArgs>? StateChanged
                { add => throw UnsupportedPreviewOperation(); remove => throw UnsupportedPreviewOperation(); }
            public event EventHandler<WindowTopmostChangedEventArgs>? TopmostChanged
                { add => throw UnsupportedPreviewOperation(); remove => throw UnsupportedPreviewOperation(); }
            public event EventHandler<WindowFocusChangedEventArgs>? GotFocus
                { add => throw UnsupportedPreviewOperation(); remove => throw UnsupportedPreviewOperation(); }
            public event EventHandler<WindowFocusChangedEventArgs>? LostFocus
                { add => throw UnsupportedPreviewOperation(); remove => throw UnsupportedPreviewOperation(); }
            public event EventHandler<WindowChangedEventArgs>? Added
                { add => throw UnsupportedPreviewOperation(); remove => throw UnsupportedPreviewOperation(); }
            public event EventHandler<WindowChangedEventArgs>? Removed
                { add => throw UnsupportedPreviewOperation(); remove => throw UnsupportedPreviewOperation(); }
            public event EventHandler<WindowChangedEventArgs>? Destroyed
                { add => throw UnsupportedPreviewOperation(); remove => throw UnsupportedPreviewOperation(); }
            public event EventHandler<WindowTitleChangedEventArgs>? TitleChanged
                { add => throw UnsupportedPreviewOperation(); remove => throw UnsupportedPreviewOperation(); }
        }

        private static NotSupportedException UnsupportedPreviewOperation()
            => new("The preview benchmark must not invoke native operations or own external event subscriptions.");
    }
}
