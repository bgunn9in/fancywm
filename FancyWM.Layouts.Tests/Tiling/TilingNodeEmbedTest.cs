#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;

using FancyWM.Layouts.Tiling;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using WinMan;

namespace FancyWM.Layouts.Tests.Tiling
{
    [TestClass]
    public class TilingNodeEmbedTest
    {
        [DataTestMethod]
        [DataRow("split", "split")]
        [DataRow("split", "stack")]
        [DataRow("stack", "split")]
        [DataRow("stack", "stack")]
        public void EmbedPreservesSlotOrderIdentityAndEveryWindowRegistration(string parentKind, string wrapperKind)
        {
            var windows = CreateWindows(3);
            for (int index = 0; index < windows.Length; index++)
            {
                var fixture = CreateFixture(parentKind, wrapperKind, windows, index);
                long targetGeneration = fixture.Target.GenerationID;
                long rootGeneration = fixture.Root.GenerationID;

                fixture.Target.Embed(fixture.Wrapper);

                var totals = VerifyFixtures(new[] { fixture });
                Assert.AreEqual((long)index, totals.SlotIndexSum);
                Assert.AreEqual(2L, totals.ParentLinks);
                Assert.AreEqual(3L, totals.Registrations);
                Assert.AreEqual(6L, totals.OrderedWindowSum);
                Assert.AreEqual(targetGeneration, fixture.Target.GenerationID);
                Assert.AreEqual(rootGeneration, fixture.Root.GenerationID);
            }
        }

        [TestMethod]
        public void EmbedPreservesEveryRegistrationWhenTheTargetIsAnExistingSubtree()
        {
            var windows = CreateWindows(4);
            var root = new SplitPanelNode();
            var tree = new DesktopTree { Root = root };
            var before = new WindowNode(windows[0]);
            var target = new SplitPanelNode { Orientation = PanelOrientation.Vertical };
            var after = new WindowNode(windows[3]);
            root.Attach(before);
            root.Attach(target);
            root.Attach(after);
            var first = new WindowNode(windows[1]);
            var second = new WindowNode(windows[2]);
            target.Attach(first);
            target.Attach(second);
            WindowNode[] ordered = [before, first, second, after];

            for (int cycle = 0; cycle < 8; cycle++)
            {
                var previousParent = target.Parent!;
                int previousIndex = previousParent.IndexOf(target);
                PanelNode wrapper = CreatePanel((cycle & 1) == 0 ? "stack" : "split");

                target.Embed(wrapper);

                Assert.AreSame(wrapper, previousParent.Children[previousIndex]);
                Assert.AreSame(previousParent, wrapper.Parent);
                Assert.AreSame(wrapper, target.Parent);
                Assert.AreSame(target, wrapper.Children[0]);
                Assert.AreSame(root, tree.Root);
                Assert.AreSame(first, target.Children[0]);
                Assert.AreSame(second, target.Children[1]);
                int visited = 0;
                foreach (var window in root.Windows)
                {
                    Assert.AreSame(ordered[visited], window);
                    Assert.AreSame(window, tree.FindNode(windows[visited]));
                    Assert.AreSame(tree, window.Desktop);
                    visited++;
                }
                Assert.AreEqual(ordered.Length, visited);
            }
        }

        [TestMethod]
        public void EmbedUsesReferenceIdentityWhenWindowNodesOverrideEquals()
        {
            var windows = CreateWindows(3);
            var root = new StackPanelNode();
            var tree = new DesktopTree { Root = root };
            EqualWindowNode[] nodes = [new(windows[0]), new(windows[1]), new(windows[2])];
            foreach (var node in nodes) root.Attach(node);
            var wrapper = new StackPanelNode();

            nodes[1].Embed(wrapper);

            Assert.AreSame(nodes[0], root.Children[0]);
            Assert.AreSame(wrapper, root.Children[1]);
            Assert.AreSame(nodes[2], root.Children[2]);
            Assert.AreSame(nodes[1], wrapper.Children[0]);
            foreach (var node in nodes)
            {
                Assert.AreSame(node, tree.FindNode(node.WindowReference));
                Assert.AreEqual(0, node.EqualityCalls);
            }
        }

        [DataTestMethod]
        [DataRow(0)]
        [DataRow(2)]
        public void EmbedKeepsCapturedParentAndEnumerationThenReacquiresChildrenBeforeReplacement(int targetIndex)
        {
            var calls = new List<string>();
            var parent = new RecordingPanel("parent", calls);
            var wrapper = new RecordingPanel("wrapper", calls);
            var tree = new DesktopTree { Root = parent };
            var windows = CreateWindows(3);
            WindowNode[] nodes = [new(windows[0]), new(windows[1]), new(windows[2])];
            var target = new RecordingWindowNode(windows[targetIndex]);
            nodes[targetIndex] = target;
            foreach (var node in nodes) parent.Attach(node);
            var actualChildren = parent.Children;
            var scan = new RecordingChildren("scan", nodes, calls);
            var replacement = new RecordingChildren("replacement", nodes, calls) { AllowIndexedAccess = true };
            // The inherited list order differs deliberately from interface enumeration.
            IReadOnlyList<TilingNode> supplied = new DerivedChildrenList(
                new TilingNode[] { nodes[2], nodes[1], nodes[0] }, scan);
            parent.ChildrenSource = () => supplied;
            var otherParent = new StackPanelNode();
            scan.OnCurrent = step =>
            {
                if (step == 0)
                {
                    supplied = replacement;
                    target.Parent = otherParent;
                }
            };
            int windowReads = 0;
            target.OnWindowsRead = () =>
            {
                windowReads++;
                calls.Add($"target.Windows:{windowReads}");
                Assert.AreSame(wrapper, actualChildren[targetIndex]);
                Assert.AreSame(parent, wrapper.Parent);
                Assert.IsNull(target.Parent);
                if (windowReads == 1) Assert.AreSame(target, tree.FindNode(target.WindowReference));
                else Assert.IsNull(tree.FindNode(target.WindowReference));
            };
            wrapper.OnAttach = () =>
            {
                Assert.AreSame(wrapper, target.Parent);
                Assert.AreSame(target, tree.FindNode(target.WindowReference));
            };
            calls.Clear();
            parent.ResetCounts();
            wrapper.ResetCounts();

            target.Embed(wrapper);

            var expected = new List<string> { "parent.Children", "scan.GetEnumerator" };
            for (int i = 0; i <= targetIndex; i++)
            {
                expected.Add($"scan.MoveNext:{i}");
                expected.Add($"scan.Current:{i}");
            }
            expected.AddRange(new[]
            {
                "scan.Dispose", "parent.Children", $"replacement.Indexer:{targetIndex}",
                $"parent.SetReference:{targetIndex}", "target.Windows:1", "wrapper.Children",
                "target.Windows:2", "wrapper.Attach:0",
            });
            CollectionAssert.AreEqual(expected, calls);
            Assert.AreEqual(2, parent.ChildrenReads);
            Assert.AreEqual(1, parent.SetReferenceCalls);
            Assert.AreEqual(1, wrapper.AttachCalls);
            Assert.AreEqual(2, windowReads);
            Assert.AreEqual(0, scan.CountReads + scan.IndexerReads + replacement.CountReads);
            Assert.AreEqual(1, replacement.IndexerReads);
            Assert.AreSame(wrapper, actualChildren[targetIndex]);
            Assert.AreSame(parent, wrapper.Parent);
            Assert.AreSame(wrapper, target.Parent);
            foreach (var node in nodes) Assert.AreSame(node, tree.FindNode(node.WindowReference));
        }

        [TestMethod]
        public void EmbedRejectsAParentlessTargetBeforeInspectingANullWrapper()
        {
            var target = new PlaceholderNode();

            Assert.ThrowsException<InvalidOperationException>(() => target.Embed(null!));

            Assert.IsNull(target.Parent);
            Assert.IsNull(target.Desktop);
        }

        [DataTestMethod]
        [DataRow("children")]
        [DataRow("null-source")]
        [DataRow("get-enumerator")]
        [DataRow("move")]
        [DataRow("current")]
        [DataRow("dispose")]
        [DataRow("move-and-dispose")]
        [DataRow("attached-wrapper")]
        [DataRow("no-desktop")]
        [DataRow("null-wrapper")]
        public void EmbedPreservesFailurePrecedenceAndDoesNotMutateBeforeSearchAndValidationFinish(string stage)
        {
            var calls = new List<string>();
            var parent = new RecordingPanel("parent", calls);
            var wrapper = new RecordingPanel("wrapper", calls);
            var tree = new DesktopTree { Root = parent };
            var windows = CreateWindows(3);
            WindowNode[] nodes = [new(windows[0]), new(windows[1]), new(windows[2])];
            foreach (var node in nodes) parent.Attach(node);
            var actualChildren = parent.Children;
            var scan = new RecordingChildren("scan", nodes, calls);
            var failure = new ControlledEmbedException(stage);
            var disposalFailure = new ControlledEmbedException("dispose supersedes move failure");
            parent.ChildrenSource = () => stage switch
            {
                "children" => throw failure,
                "null-source" => null!,
                _ => scan,
            };
            if (stage == "get-enumerator") scan.OnGetEnumerator = () => throw failure;
            if (stage is "move" or "move-and-dispose") scan.OnMoveNext = step => { if (step == 1) throw failure; };
            if (stage == "current") scan.OnCurrent = step => { if (step == 1) throw failure; };
            if (stage == "dispose") scan.OnDispose = () => throw failure;
            if (stage == "move-and-dispose") scan.OnDispose = () => throw disposalFailure;
            PanelNode? wrapperParent = stage == "attached-wrapper" ? new StackPanelNode() : null;
            wrapper.Parent = wrapperParent;
            if (stage == "no-desktop") parent.Desktop = null;
            calls.Clear();
            parent.ResetCounts();
            wrapper.ResetCounts();

            Action action = () => nodes[1].Embed(stage == "null-wrapper" ? null! : wrapper);
            if (stage == "null-source")
            {
                Assert.AreEqual("source", Assert.ThrowsException<ArgumentNullException>(action).ParamName);
            }
            else if (stage == "attached-wrapper") Assert.ThrowsException<ArgumentException>(action);
            else if (stage == "no-desktop") Assert.ThrowsException<InvalidOperationException>(action);
            else if (stage == "null-wrapper") Assert.ThrowsException<NullReferenceException>(action);
            else
            {
                Assert.AreSame(stage == "move-and-dispose" ? disposalFailure : failure,
                    Assert.ThrowsException<ControlledEmbedException>(action));
            }

            Assert.AreEqual(1, parent.ChildrenReads);
            Assert.AreEqual(0, parent.SetReferenceCalls);
            Assert.AreEqual(0, wrapper.AttachCalls);
            Assert.AreEqual(0, scan.CountReads + scan.IndexerReads);
            int disposals = 0;
            foreach (string call in calls) if (call == "scan.Dispose") disposals++;
            Assert.AreEqual(stage is "children" or "null-source" or "get-enumerator" ? 0 : 1, disposals);
            for (int i = 0; i < nodes.Length; i++)
            {
                Assert.AreSame(nodes[i], actualChildren[i]);
                Assert.AreSame(parent, nodes[i].Parent);
                Assert.AreSame(nodes[i], tree.FindNode(windows[i]));
            }
            Assert.AreSame(wrapperParent, wrapper.Parent);
            Assert.AreEqual(0, wrapper.Children.Count);
        }

        [TestMethod]
        public void RepeatedEmbedDetachAndAttachCyclesKeepRegistrationBalanced()
        {
            var windows = CreateWindows(3);
            for (int cycle = 0; cycle < 25; cycle++)
            {
                var fixture = CreateFixture((cycle & 1) == 0 ? "split" : "stack", "stack", windows, 1);
                fixture.Target.Embed(fixture.Wrapper);
                VerifyFixtures(new[] { fixture });

                fixture.Wrapper.Detach(fixture.Target);
                Assert.IsNull(fixture.Tree.FindNode(fixture.Target.WindowReference));
                Assert.IsNull(fixture.Target.Parent);
                fixture.Wrapper.Attach(fixture.Target);
                VerifyFixtures(new[] { fixture });

                fixture.Root.Detach(fixture.Wrapper);
                Assert.IsNull(fixture.Tree.FindNode(fixture.Target.WindowReference));
                Assert.IsNull(fixture.Wrapper.Parent);
                Assert.AreSame(fixture.Nodes[0], fixture.Tree.FindNode(windows[0]));
                Assert.AreSame(fixture.Nodes[2], fixture.Tree.FindNode(windows[2]));
                fixture.Root.Attach(1, fixture.Wrapper);
                VerifyFixtures(new[] { fixture });
            }
        }

        [DataTestMethod]
        [DataRow("split", 1)]
        [DataRow("split", 10)]
        [DataRow("split", 50)]
        [DataRow("stack", 1)]
        [DataRow("stack", 10)]
        [DataRow("stack", 50)]
        public void PreparedEmbedDoesNotAllocateForItsIndexLookup(string parentKind, int count)
        {
            var windows = CreateWindows(count);
            WarmEmbed(parentKind, windows);
            const int embeds = 1000;
            var fixtures = CreateFixtures(parentKind, windows, embeds);

            var measurement = MeasureEmbed(fixtures);

            AssertTotals(count, embeds, VerifyFixtures(fixtures));
            Console.WriteLine($"EMBEDINDEX_ALLOC {parentKind} children {count} embeds {embeds} allocated-bytes {measurement.AllocatedBytes}");
            // The real WindowNode unregister/register enumerations and the new stack
            // child storage remain in the budget; only the lookup should allocate zero.
            // JIT escape analysis may eliminate some of those remaining allocations.
            const long maximumBytesPerEmbed = 168;
            Assert.IsTrue(measurement.AllocatedBytes <= maximumBytesPerEmbed * embeds,
                $"The full-Embed ceiling is {maximumBytesPerEmbed} B/call: {parentKind}, N={count}, {embeds} calls allocated {measurement.AllocatedBytes} B.");
        }

        [TestMethod]
        public void TilingNodeEmbedCounterScenario()
        {
            foreach (int count in new[] { 1, 10, 50 })
            {
                var windows = CreateWindows(count);
                WarmEmbed("split", windows);
                const int batchSize = 250;
                const int batches = 40;
                const int embeds = batchSize * batches;
                long allocated = 0;
                long elapsed = 0;
                var totals = new VerificationTotals();
                for (int batch = 0; batch < batches; batch++)
                {
                    // Setup and all structural/registration assertions are outside
                    // the measured regions. Each fixture is embedded exactly once.
                    var fixtures = CreateFixtures("split", windows, batchSize);
                    var measurement = MeasureEmbed(fixtures);
                    allocated += measurement.AllocatedBytes;
                    elapsed += measurement.ElapsedTicks;
                    totals.Add(VerifyFixtures(fixtures));
                }

                AssertTotals(count, embeds, totals);
                Console.WriteLine($"PERFCOUNTER embed-index-{count} allocated-bytes {allocated}");
                Console.WriteLine($"PERFCOUNTER embed-index-{count} elapsed-ticks {elapsed}");
                Console.WriteLine($"PERFCOUNTER embed-index-{count} timestamp-frequency {Stopwatch.Frequency}");
                Console.WriteLine($"PERFCOUNTER embed-index-{count} embeds {embeds}");
                Console.WriteLine($"PERFCOUNTER embed-index-{count} slot-index-sum {totals.SlotIndexSum}");
                Console.WriteLine($"PERFCOUNTER embed-index-{count} parent-links {totals.ParentLinks}");
                Console.WriteLine($"PERFCOUNTER embed-index-{count} registrations {totals.Registrations}");
                Console.WriteLine($"PERFCOUNTER embed-index-{count} ordered-window-sum {totals.OrderedWindowSum}");
            }
        }

        private static IWindow[] CreateWindows(int count)
        {
            var windows = new IWindow[count];
            for (int i = 0; i < count; i++) windows[i] = new EmbedWindow();
            return windows;
        }

        private static PanelNode CreatePanel(string kind) => kind == "split" ? new SplitPanelNode() : new StackPanelNode();

        private static EmbedFixture CreateFixture(string parentKind, string wrapperKind, IWindow[] windows, int targetIndex)
        {
            var root = CreatePanel(parentKind);
            var tree = new DesktopTree { Root = root };
            var nodes = new WindowNode[windows.Length];
            for (int i = 0; i < windows.Length; i++)
            {
                nodes[i] = new WindowNode(windows[i]);
                root.Attach(nodes[i]);
            }
            return new EmbedFixture(tree, root, CreatePanel(wrapperKind), nodes, targetIndex);
        }

        private static EmbedFixture[] CreateFixtures(string parentKind, IWindow[] windows, int count)
        {
            var fixtures = new EmbedFixture[count];
            for (int i = 0; i < count; i++) fixtures[i] = CreateFixture(parentKind, "stack", windows, windows.Length - 1);
            return fixtures;
        }

        private static void WarmEmbed(string parentKind, IWindow[] windows)
        {
            for (int batch = 0; batch < 4; batch++)
            {
                var fixtures = CreateFixtures(parentKind, windows, 250);
                MeasureEmbed(fixtures);
                AssertTotals(windows.Length, fixtures.Length, VerifyFixtures(fixtures));
            }
        }

        private static (long AllocatedBytes, long ElapsedTicks) MeasureEmbed(EmbedFixture[] fixtures)
        {
            _ = Stopwatch.GetTimestamp();
            long before = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            foreach (var fixture in fixtures) fixture.Target.Embed(fixture.Wrapper);
            long elapsed = Stopwatch.GetTimestamp() - started;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            return (allocated, elapsed);
        }

        private static VerificationTotals VerifyFixtures(EmbedFixture[] fixtures)
        {
            var totals = new VerificationTotals();
            foreach (var fixture in fixtures)
            {
                Assert.AreSame(fixture.Root, fixture.Tree.Root);
                Assert.AreSame(fixture.Tree, fixture.Root.Desktop);
                var children = fixture.Root.Children;
                Assert.AreEqual(fixture.Nodes.Length, children.Count);
                Assert.AreSame(fixture.Wrapper, children[fixture.TargetIndex]);
                totals.SlotIndexSum += fixture.TargetIndex;
                Assert.AreSame(fixture.Root, fixture.Wrapper.Parent);
                Assert.AreSame(fixture.Wrapper, fixture.Target.Parent);
                totals.ParentLinks += 2;
                Assert.AreEqual(1, fixture.Wrapper.Children.Count);
                Assert.AreSame(fixture.Target, fixture.Wrapper.Children[0]);
                for (int i = 0; i < fixture.Nodes.Length; i++)
                {
                    var node = fixture.Nodes[i];
                    if (i != fixture.TargetIndex)
                    {
                        Assert.AreSame(node, children[i]);
                        Assert.AreSame(fixture.Root, node.Parent);
                    }
                    Assert.AreSame(fixture.Tree, node.Desktop);
                    Assert.AreSame(node, fixture.Tree.FindNode(node.WindowReference));
                    totals.Registrations++;
                }
                int orderedIndex = 0;
                foreach (var node in fixture.Root.Windows)
                {
                    Assert.AreSame(fixture.Nodes[orderedIndex], node);
                    totals.OrderedWindowSum += ++orderedIndex;
                }
                Assert.AreEqual(fixture.Nodes.Length, orderedIndex);
            }
            return totals;
        }

        private static void AssertTotals(int count, int embeds, VerificationTotals totals)
        {
            Assert.AreEqual(checked((long)embeds * (count - 1)), totals.SlotIndexSum);
            Assert.AreEqual(checked((long)embeds * 2), totals.ParentLinks);
            Assert.AreEqual(checked((long)embeds * count), totals.Registrations);
            Assert.AreEqual(checked((long)embeds * count * (count + 1) / 2), totals.OrderedWindowSum);
        }

        private sealed record EmbedFixture(DesktopTree Tree, PanelNode Root, PanelNode Wrapper, WindowNode[] Nodes, int TargetIndex)
        {
            public WindowNode Target => Nodes[TargetIndex];
        }

        private struct VerificationTotals
        {
            public long SlotIndexSum;
            public long ParentLinks;
            public long Registrations;
            public long OrderedWindowSum;
            public void Add(VerificationTotals value)
            {
                SlotIndexSum += value.SlotIndexSum;
                ParentLinks += value.ParentLinks;
                Registrations += value.Registrations;
                OrderedWindowSum += value.OrderedWindowSum;
            }
        }

        private sealed class EqualWindowNode(IWindow window) : WindowNode(window)
        {
            public int EqualityCalls;
            public override bool Equals(object? obj)
            {
                EqualityCalls++;
                return obj is EqualWindowNode;
            }
            public override int GetHashCode() => 0;
        }

        private sealed class RecordingWindowNode(IWindow window) : WindowNode(window)
        {
            public Action? OnWindowsRead;
            public override IEnumerable<WindowNode> Windows
            {
                get { OnWindowsRead?.Invoke(); return base.Windows; }
            }
        }

        private sealed class RecordingPanel(string name, List<string> calls) : StackPanelNode
        {
            public Func<IReadOnlyList<TilingNode>>? ChildrenSource;
            public Action? OnAttach;
            public int ChildrenReads;
            public int SetReferenceCalls;
            public int AttachCalls;
            public override IReadOnlyList<TilingNode> Children
            {
                get
                {
                    ChildrenReads++;
                    calls.Add($"{name}.Children");
                    return ChildrenSource == null ? base.Children : ChildrenSource();
                }
            }
            internal override void SetReference(int index, TilingNode node)
            {
                SetReferenceCalls++;
                calls.Add($"{name}.SetReference:{index}");
                base.SetReference(index, node);
            }
            protected override void AttachCore(int index, TilingNode node)
            {
                AttachCalls++;
                calls.Add($"{name}.Attach:{index}");
                OnAttach?.Invoke();
                base.AttachCore(index, node);
            }
            public void ResetCounts() { ChildrenReads = SetReferenceCalls = AttachCalls = 0; }
        }

        private sealed class ControlledEmbedException(string message) : Exception(message) { }

        // Stable dictionary keys without mocking-framework callbacks or native work
        // inside the measured unregister/register path.
        private sealed class EmbedWindow : IWindow
        {
            public bool Equals(IWindow? other) => ReferenceEquals(this, other);
            public object SyncRoot => this;
            public IWorkspace Workspace => throw new NotSupportedException();
            public string Title => "Embed fixture";
            public Rectangle Position => throw new NotSupportedException();
            public WindowState State => WindowState.Restored;
            public Point? MinSize => null;
            public Point? MaxSize => null;
            public Rectangle FrameMargins => default;
            public bool CanResize => true;
            public bool CanMove => true;
            public bool CanReorder => true;
            public bool CanMinimize => true;
            public bool CanMaximize => true;
            public bool CanClose => true;
            public bool IsTopmost => false;
            public bool IsFocused => false;
            public bool IsAlive => true;
            public IntPtr Handle => throw new NotSupportedException();
            public Process GetProcess() => throw new NotSupportedException();
            public IWindow? GetPreviousWindow() => throw new NotSupportedException();
            public IWindow? GetNextWindow() => throw new NotSupportedException();
            public void Close() => throw new NotSupportedException();
            public void SetPosition(Rectangle newLocation) => throw new NotSupportedException();
            public void SetState(WindowState state) => throw new NotSupportedException();
            public void SetTopmost(bool topmost) => throw new NotSupportedException();
            public void InsertAfter(IWindow other) => throw new NotSupportedException();
            public void SendToBack() => throw new NotSupportedException();
            public void BringToFront() => throw new NotSupportedException();
            public bool RequestFocus() => throw new NotSupportedException();
            public event EventHandler<WindowPositionChangedEventArgs>? PositionChangeStart { add => throw new NotSupportedException(); remove => throw new NotSupportedException(); }
            public event EventHandler<WindowPositionChangedEventArgs>? PositionChangeEnd { add => throw new NotSupportedException(); remove => throw new NotSupportedException(); }
            public event EventHandler<WindowPositionChangedEventArgs>? PositionChanged { add => throw new NotSupportedException(); remove => throw new NotSupportedException(); }
            public event EventHandler<WindowStateChangedEventArgs>? StateChanged { add => throw new NotSupportedException(); remove => throw new NotSupportedException(); }
            public event EventHandler<WindowTopmostChangedEventArgs>? TopmostChanged { add => throw new NotSupportedException(); remove => throw new NotSupportedException(); }
            public event EventHandler<WindowFocusChangedEventArgs>? GotFocus { add => throw new NotSupportedException(); remove => throw new NotSupportedException(); }
            public event EventHandler<WindowFocusChangedEventArgs>? LostFocus { add => throw new NotSupportedException(); remove => throw new NotSupportedException(); }
            public event EventHandler<WindowChangedEventArgs>? Added { add => throw new NotSupportedException(); remove => throw new NotSupportedException(); }
            public event EventHandler<WindowChangedEventArgs>? Removed { add => throw new NotSupportedException(); remove => throw new NotSupportedException(); }
            public event EventHandler<WindowChangedEventArgs>? Destroyed { add => throw new NotSupportedException(); remove => throw new NotSupportedException(); }
            public event EventHandler<WindowTitleChangedEventArgs>? TitleChanged { add => throw new NotSupportedException(); remove => throw new NotSupportedException(); }
        }

        private sealed class DerivedChildrenList(IEnumerable<TilingNode> nodes, RecordingChildren enumeration)
            : List<TilingNode>(nodes), IEnumerable<TilingNode>
        {
            IEnumerator<TilingNode> IEnumerable<TilingNode>.GetEnumerator() => enumeration.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => enumeration.GetEnumerator();
        }

        private sealed class RecordingChildren(string name, IReadOnlyList<TilingNode> nodes, List<string> calls)
            : IReadOnlyList<TilingNode>
        {
            public bool AllowIndexedAccess;
            public int CountReads;
            public int IndexerReads;
            public Action? OnGetEnumerator;
            public Action<int>? OnMoveNext;
            public Action<int>? OnCurrent;
            public Action? OnDispose;
            public int Count
            {
                get { CountReads++; throw new InvalidOperationException("Unexpected custom Count read."); }
            }
            public TilingNode this[int index]
            {
                get
                {
                    IndexerReads++;
                    calls.Add($"{name}.Indexer:{index}");
                    if (!AllowIndexedAccess) throw new InvalidOperationException("Unexpected scan indexer read.");
                    return nodes[index];
                }
            }
            public IEnumerator<TilingNode> GetEnumerator()
            {
                calls.Add($"{name}.GetEnumerator");
                OnGetEnumerator?.Invoke();
                return new RecordingEnumerator(this, nodes.GetEnumerator());
            }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            private sealed class RecordingEnumerator(RecordingChildren owner, IEnumerator<TilingNode> inner)
                : IEnumerator<TilingNode>
            {
                private int m_step;
                public TilingNode Current
                {
                    get
                    {
                        owner.Log($"Current:{m_step - 1}");
                        owner.OnCurrent?.Invoke(m_step - 1);
                        return inner.Current;
                    }
                }
                object IEnumerator.Current => Current;
                public bool MoveNext()
                {
                    int step = m_step++;
                    owner.Log($"MoveNext:{step}");
                    owner.OnMoveNext?.Invoke(step);
                    return inner.MoveNext();
                }
                public void Reset() => throw new NotSupportedException();
                public void Dispose()
                {
                    owner.Log("Dispose");
                    try { owner.OnDispose?.Invoke(); }
                    finally { inner.Dispose(); }
                }
            }
            private void Log(string operation) => calls.Add($"{name}.{operation}");
        }
    }
}
