#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using FancyWM.Layouts.Tiling;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using WinMan;

namespace FancyWM.Layouts.Tests.Tiling
{
    [TestClass]
    public class PanelNodeRemovePlaceholdersTest
    {
        [DataTestMethod]
        [DataRow("split")]
        [DataRow("stack")]
        public void ExactPanelsRemoveOnlyDirectPlaceholderInstancesAndPreserveOrder(string panelKind)
        {
            PanelNode panel = CreateExactPanel(panelKind);
            var first = new MarkerNode(1);
            var exact = new PlaceholderNode();
            var spoof = new MarkerNode(2, TilingNodeType.Placeholder);
            var derived = new DerivedPlaceholder();
            var nested = new StackPanelNode();
            var nestedPlaceholder = new PlaceholderNode();
            var last = new MarkerNode(3);
            nested.Attach(nestedPlaceholder);
            foreach (var child in new TilingNode[] { first, exact, spoof, derived, nested, last })
            {
                panel.Attach(child);
            }

            panel.RemovePlaceholders();

            CollectionAssert.AreEqual(new TilingNode[] { first, spoof, nested, last }, panel.Children.ToArray());
            Assert.AreSame(panel, first.Parent);
            Assert.AreSame(panel, spoof.Parent);
            Assert.AreSame(panel, nested.Parent);
            Assert.AreSame(panel, last.Parent);
            Assert.IsNull(exact.Parent);
            Assert.IsNull(derived.Parent);
            Assert.AreSame(nested, nestedPlaceholder.Parent, "Cleanup is limited to direct children.");
            CollectionAssert.AreEqual(new TilingNode[] { nestedPlaceholder }, nested.Children.ToArray());
        }

        [DataTestMethod]
        [DataRow("split")]
        [DataRow("stack")]
        public void ExactPanelsSnapshotAllPlaceholdersBeforeDetachCallbacksMutateChildren(string panelKind)
        {
            PanelNode panel = CreateExactPanel(panelKind);
            var retained = new MarkerNode(1);
            var first = new TrackingPlaceholder("first");
            var second = new TrackingPlaceholder("second");
            var appended = new TrackingPlaceholder("appended");
            panel.Attach(retained);
            panel.Attach(first);
            panel.Attach(second);
            first.ResetReads();
            second.ResetReads();
            appended.ResetReads();
            var events = new List<string>();
            first.OnWindowsRead = () =>
            {
                events.Add("first");
                panel.Attach(appended);
                events.Add("appended");
            };
            second.OnWindowsRead = () => events.Add("second");

            panel.RemovePlaceholders();

            CollectionAssert.AreEqual(new[] { "first", "appended", "second" }, events);
            CollectionAssert.AreEqual(new TilingNode[] { retained, appended }, panel.Children.ToArray());
            Assert.IsNull(first.Parent);
            Assert.IsNull(second.Parent);
            Assert.AreSame(panel, appended.Parent);
            Assert.AreEqual(1L, first.WindowsReads);
            Assert.AreEqual(1L, second.WindowsReads);
            Assert.AreEqual(1L, appended.WindowsReads, "The callback attaches the new placeholder once.");
        }

        [DataTestMethod]
        [DataRow("split")]
        [DataRow("stack")]
        public void ExactPanelsRetainSnapshotFailureWhenCallbackRemovesALaterPlaceholder(string panelKind)
        {
            PanelNode panel = CreateExactPanel(panelKind);
            var retained = new MarkerNode(1);
            var first = new TrackingPlaceholder("first");
            var second = new TrackingPlaceholder("second");
            panel.Attach(retained);
            panel.Attach(first);
            panel.Attach(second);
            first.ResetReads();
            second.ResetReads();
            var events = new List<string>();
            first.OnWindowsRead = () =>
            {
                events.Add("first");
                panel.Detach(second);
            };
            second.OnWindowsRead = () => events.Add("second");

            Assert.ThrowsException<InvalidOperationException>(() => panel.RemovePlaceholders());

            CollectionAssert.AreEqual(new[] { "first", "second", "second" }, events);
            CollectionAssert.AreEqual(new TilingNode[] { retained }, panel.Children.ToArray());
            Assert.AreSame(panel, retained.Parent);
            Assert.IsNull(first.Parent);
            Assert.IsNull(second.Parent);
            Assert.AreEqual(1L, first.WindowsReads);
            Assert.AreEqual(2L, second.WindowsReads);
        }

        [TestMethod]
        public void CustomChildrenUseOneCapturedNonGenericEnumerationAndDetachTheCompletedSnapshot()
        {
            var panel = new SuppliedChildrenPanel();
            var first = new MarkerNode(1);
            var exact = new PlaceholderNode();
            var spoof = new MarkerNode(2, TilingNodeType.Placeholder);
            var derived = new DerivedPlaceholder();
            foreach (var child in new TilingNode[] { first, exact, spoof, derived }) panel.AddDirect(child);
            var source = new RecordingChildren(panel.BackingChildren.ToArray());
            panel.VisibleChildren = source;
            panel.IsSnapshotDisposed = () => source.Disposed;

            panel.RemovePlaceholders();

            Assert.AreEqual(1, panel.ChildrenReads);
            Assert.AreEqual(0, source.GenericEnumeratorReads);
            Assert.AreEqual(1, source.NonGenericEnumeratorReads);
            Assert.AreEqual(0, source.CountReads);
            Assert.AreEqual(0, source.IndexerReads);
            Assert.IsTrue(source.Disposed);
            Assert.AreEqual(0, panel.DetachesBeforeSnapshotDisposed);
            CollectionAssert.AreEqual(new[] { "detach", "detach" }, panel.DetachCalls);
            CollectionAssert.AreEqual(new TilingNode[] { first, spoof }, panel.BackingChildren.ToArray());
            Assert.IsNull(exact.Parent);
            Assert.IsNull(derived.Parent);
            Assert.AreSame(panel, first.Parent);
            Assert.AreSame(panel, spoof.Parent);
        }

        [TestMethod]
        public void SuppliedExactListUsesACompletedSnapshotWithoutAssumingListOwnership()
        {
            var panel = new SuppliedChildrenPanel();
            var first = new MarkerNode(1);
            var exact = new PlaceholderNode();
            var spoof = new MarkerNode(2, TilingNodeType.Placeholder);
            var derived = new DerivedPlaceholder();
            foreach (var child in new TilingNode[] { first, exact, spoof, derived }) panel.AddDirect(child);
            var supplied = new List<TilingNode> { first, exact, spoof, derived };
            panel.VisibleChildren = supplied;

            panel.RemovePlaceholders();

            Assert.AreEqual(1, panel.ChildrenReads);
            CollectionAssert.AreEqual(new TilingNode[] { first, exact, spoof, derived }, supplied);
            CollectionAssert.AreEqual(new TilingNode[] { first, spoof }, panel.BackingChildren.ToArray());
            CollectionAssert.AreEqual(new[] { "detach", "detach" }, panel.DetachCalls);
            Assert.AreSame(panel, first.Parent);
            Assert.IsNull(exact.Parent);
            Assert.AreSame(panel, spoof.Parent);
            Assert.IsNull(derived.Parent);
        }

        [DataTestMethod]
        [DataRow("split")]
        [DataRow("stack")]
        public void DerivedBuiltInPanelsWithOverriddenChildrenRetainTheCustomFallback(string panelKind)
        {
            OverriddenChildrenPanel control;
            PanelNode panel;
            if (panelKind == "split")
            {
                var split = new OverriddenChildrenSplitPanel();
                control = split;
                panel = split;
            }
            else
            {
                var stack = new OverriddenChildrenStackPanel();
                control = stack;
                panel = stack;
            }
            var first = new MarkerNode(1);
            var placeholder = new PlaceholderNode();
            var last = new MarkerNode(2);
            panel.Attach(first);
            panel.Attach(placeholder);
            panel.Attach(last);
            var source = new RecordingChildren(control.BackingChildren.ToArray());
            control.VisibleChildren = source;
            control.ResetChildrenReads();

            panel.RemovePlaceholders();

            Assert.AreEqual(1, control.ChildrenReads);
            Assert.AreEqual(0, source.GenericEnumeratorReads);
            Assert.AreEqual(1, source.NonGenericEnumeratorReads);
            Assert.IsTrue(source.Disposed);
            CollectionAssert.AreEqual(new TilingNode[] { first, last }, control.BackingChildren.ToArray());
            Assert.AreSame(panel, first.Parent);
            Assert.IsNull(placeholder.Parent);
            Assert.AreSame(panel, last.Parent);
        }

        [DataTestMethod]
        [DataRow("get-enumerator")]
        [DataRow("move")]
        [DataRow("current")]
        [DataRow("dispose")]
        [DataRow("move-and-dispose")]
        public void CustomChildrenEnumerationFailurePreventsEveryDetachAndPreservesIdentity(string stage)
        {
            var panel = new SuppliedChildrenPanel();
            var nodes = new TilingNode[] { new PlaceholderNode(), new MarkerNode(1), new PlaceholderNode() };
            foreach (var child in nodes) panel.AddDirect(child);
            var source = new RecordingChildren(nodes);
            panel.VisibleChildren = source;
            var failure = new ControlledCleanupException(stage);
            var disposeFailure = new ControlledCleanupException("dispose wins");
            Exception expected = stage == "move-and-dispose" ? disposeFailure : failure;
            switch (stage)
            {
                case "get-enumerator":
                    source.OnGetEnumerator = () => throw failure;
                    break;
                case "move":
                    source.OnMoveNext = step => { if (step == 1) throw failure; };
                    break;
                case "current":
                    source.OnCurrent = step => { if (step == 1) throw failure; };
                    break;
                case "dispose":
                    source.OnDispose = () => throw failure;
                    break;
                default:
                    source.OnMoveNext = step => { if (step == 1) throw failure; };
                    source.OnDispose = () => throw disposeFailure;
                    break;
            }

            Assert.AreSame(expected,
                Assert.ThrowsException<ControlledCleanupException>(() => panel.RemovePlaceholders()));

            Assert.AreEqual(1, panel.ChildrenReads);
            Assert.AreEqual(0, source.GenericEnumeratorReads);
            Assert.AreEqual(1, source.NonGenericEnumeratorReads);
            Assert.AreEqual(0, source.CountReads);
            Assert.AreEqual(0, source.IndexerReads);
            Assert.AreEqual(0, panel.DetachCalls.Count, "No mutation may begin before snapshot enumeration succeeds.");
            CollectionAssert.AreEqual(nodes, panel.BackingChildren.ToArray());
            foreach (var node in nodes) Assert.AreSame(panel, node.Parent);
        }

        [TestMethod]
        public void CustomChildrenPreserveNullSourceAndGetterFailureContracts()
        {
            var nullPanel = new SuppliedChildrenPanel { VisibleChildren = null! };

            var nullFailure = Assert.ThrowsException<ArgumentNullException>(() => nullPanel.RemovePlaceholders());

            Assert.AreEqual("source", nullFailure.ParamName);
            Assert.AreEqual(1, nullPanel.ChildrenReads);
            var getterFailure = new ControlledCleanupException("getter");
            var throwingPanel = new SuppliedChildrenPanel { ChildrenFailure = getterFailure };

            Assert.AreSame(getterFailure,
                Assert.ThrowsException<ControlledCleanupException>(() => throwingPanel.RemovePlaceholders()));
            Assert.AreEqual(1, throwingPanel.ChildrenReads);
        }

        [DataTestMethod]
        [DataRow("split")]
        [DataRow("stack")]
        public void CloneCleanupKeepsOriginalChildrenAndParentLinksIsolated(string panelKind)
        {
            PanelNode original = CreateExactPanel(panelKind);
            var first = new MarkerNode(1);
            var placeholder = new PlaceholderNode();
            var last = new MarkerNode(2);
            original.Attach(first);
            original.Attach(placeholder);
            original.Attach(last);
            var clone = (PanelNode)original.Clone();

            clone.RemovePlaceholders();

            Assert.AreEqual(3, original.Children.Count);
            Assert.AreEqual(2, clone.Children.Count);
            Assert.AreSame(original, first.Parent);
            Assert.AreSame(original, placeholder.Parent);
            Assert.AreSame(original, last.Parent);
            Assert.IsFalse(clone.Children.Any(child => child is PlaceholderNode));
            Assert.IsTrue(clone.Children.All(child => ReferenceEquals(child.Parent, clone)));
            Assert.IsFalse(clone.Children.Any(child => ReferenceEquals(child, first) || ReferenceEquals(child, last)));
        }

        [DataTestMethod]
        [DataRow("split")]
        [DataRow("stack")]
        public void RepeatedExactCleanupCyclesRetainStableChildrenAndDetachEveryPlaceholder(string panelKind)
        {
            PanelNode panel = CreateExactPanel(panelKind);
            var first = new MarkerNode(1);
            var second = new MarkerNode(2);
            panel.Attach(first);
            panel.Attach(second);
            var placeholder = new CountingPlaceholder();
            placeholder.ResetReads();

            for (int cycle = 0; cycle < 128; cycle++)
            {
                panel.Attach(placeholder);
                panel.RemovePlaceholders();
                Assert.IsNull(placeholder.Parent);
            }

            CollectionAssert.AreEqual(new TilingNode[] { first, second }, panel.Children.ToArray());
            Assert.AreSame(panel, first.Parent);
            Assert.AreSame(panel, second.Parent);
            Assert.AreEqual(256L, placeholder.WindowsReads);
        }

        [DataTestMethod]
        [DataRow("split", 1)]
        [DataRow("split", 10)]
        [DataRow("split", 50)]
        [DataRow("stack", 1)]
        [DataRow("stack", 10)]
        [DataRow("stack", 50)]
        public void ExactPanelCleanupWithoutPlaceholdersDoesNotAllocate(string panelKind, int childCount)
        {
            PanelNode panel = CreateExactPanel(panelKind);
            AddMarkers(panel, childCount);
            RunCleanupOperations(panel, null, 1_000);

            long before = GC.GetAllocatedBytesForCurrentThread();
            RunCleanupOperations(panel, null, 1_000);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            AssertPanelState(panel, childCount);
            Console.WriteLine($"PLACEHOLDER_CLEANUP_ALLOC {panelKind} children {childCount} operations 1000 allocated-bytes {allocated}");
            Assert.AreEqual(0L, allocated,
                $"The exact-panel no-placeholder budget is 0 B/call: {panelKind}, N={childCount}, 1,000 calls allocated {allocated} B.");
        }

        [TestMethod]
        public void PanelNodeRemovePlaceholdersCounterScenario()
        {
            const int operations = 100_000;
            string[] modes =
            [
                "split-absent", "stack-absent", "derived-absent", "split-present", "stack-present",
            ];
            foreach (string mode in modes)
            {
                foreach (int childCount in new[] { 1, 10, 50 })
                {
                    PanelNode panel = mode switch
                    {
                        "split-absent" or "split-present" => new SplitPanelNode(),
                        "stack-absent" or "stack-present" => new StackPanelNode(),
                        _ => new DerivedSplitPanel(),
                    };
                    AddMarkers(panel, childCount);
                    bool present = mode.EndsWith("-present", StringComparison.Ordinal);
                    var placeholder = present ? new CountingPlaceholder() : null;
                    RunCleanupOperations(panel, placeholder, 1_000);
                    placeholder?.ResetReads();
                    _ = Stopwatch.GetTimestamp();

                    long before = GC.GetAllocatedBytesForCurrentThread();
                    long started = Stopwatch.GetTimestamp();
                    RunCleanupOperations(panel, placeholder, operations);
                    long elapsed = Stopwatch.GetTimestamp() - started;
                    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                    var state = ReadPanelState(panel);

                    Assert.AreEqual(childCount, state.RetainedChildren);
                    Assert.AreEqual(childCount, state.ParentLinks);
                    Assert.AreEqual(ExpectedOrderedSlotSum(childCount), state.OrderedSlotSum);
                    Assert.AreEqual(present ? 200_000L : 0L, placeholder?.WindowsReads ?? 0L);
                    Console.WriteLine($"PERFCOUNTER placeholder-cleanup-{mode}-{childCount} allocated-bytes {allocated}");
                    Console.WriteLine($"PERFCOUNTER placeholder-cleanup-{mode}-{childCount} elapsed-ticks {elapsed}");
                    Console.WriteLine($"PERFCOUNTER placeholder-cleanup-{mode}-{childCount} timestamp-frequency {Stopwatch.Frequency}");
                    Console.WriteLine($"PERFCOUNTER placeholder-cleanup-{mode}-{childCount} operations {operations}");
                    Console.WriteLine($"PERFCOUNTER placeholder-cleanup-{mode}-{childCount} retained-children {state.RetainedChildren}");
                    Console.WriteLine($"PERFCOUNTER placeholder-cleanup-{mode}-{childCount} parent-links {state.ParentLinks}");
                    Console.WriteLine($"PERFCOUNTER placeholder-cleanup-{mode}-{childCount} ordered-slot-sum {state.OrderedSlotSum}");
                    Console.WriteLine($"PERFCOUNTER placeholder-cleanup-{mode}-{childCount} placeholder-windows-reads {placeholder?.WindowsReads ?? 0L}");
                }
            }
        }

        private static PanelNode CreateExactPanel(string panelKind)
        {
            return panelKind == "split" ? new SplitPanelNode() : new StackPanelNode();
        }

        private static void AddMarkers(PanelNode panel, int count)
        {
            for (int index = 0; index < count; index++) panel.Attach(new MarkerNode(index + 1));
        }

        private static void RunCleanupOperations(PanelNode panel, CountingPlaceholder? placeholder, int count)
        {
            for (int operation = 0; operation < count; operation++)
            {
                if (placeholder != null) panel.Attach(placeholder);
                panel.RemovePlaceholders();
            }
        }

        private static void AssertPanelState(PanelNode panel, int expectedCount)
        {
            var state = ReadPanelState(panel);
            Assert.AreEqual(expectedCount, state.RetainedChildren);
            Assert.AreEqual(expectedCount, state.ParentLinks);
            Assert.AreEqual(ExpectedOrderedSlotSum(expectedCount), state.OrderedSlotSum);
        }

        private static (int RetainedChildren, int ParentLinks, long OrderedSlotSum) ReadPanelState(PanelNode panel)
        {
            int retained = 0;
            int parentLinks = 0;
            long orderedSlotSum = 0;
            foreach (var child in panel.Children)
            {
                retained++;
                if (ReferenceEquals(child.Parent, panel)) parentLinks++;
                var marker = (MarkerNode)child;
                orderedSlotSum += checked((long)retained * marker.Id);
            }
            return (retained, parentLinks, orderedSlotSum);
        }

        private static long ExpectedOrderedSlotSum(int childCount)
        {
            return checked((long)childCount * (childCount + 1) * (2L * childCount + 1) / 6);
        }

        private class MarkerNode(int id, TilingNodeType type = TilingNodeType.Static) : TilingNode
        {
            public int Id { get; } = id;
            public override TilingNodeType Type => type;
            public override IEnumerable<WindowNode> Windows => Array.Empty<WindowNode>();
            public override IEnumerable<TilingNode> Nodes => new[] { this };
            internal override void ArrangeCore(RectangleF rectangle) { }
            internal override void MeasureCore() { }
        }

        private sealed class DerivedPlaceholder : PlaceholderNode { }

        private class CountingPlaceholder : PlaceholderNode
        {
            public long WindowsReads;
            public override IEnumerable<WindowNode> Windows
            {
                get
                {
                    WindowsReads++;
                    return Array.Empty<WindowNode>();
                }
            }
            public void ResetReads() => WindowsReads = 0;
        }

        private sealed class TrackingPlaceholder(string name) : CountingPlaceholder
        {
            public Action? OnWindowsRead;
            public override IEnumerable<WindowNode> Windows
            {
                get
                {
                    var windows = base.Windows;
                    OnWindowsRead?.Invoke();
                    return windows;
                }
            }
            public override string ToString() => name;
        }

        private sealed class DerivedSplitPanel : SplitPanelNode { }

        private sealed class SuppliedChildrenPanel : PanelNode
        {
            private readonly List<TilingNode> m_children = new();

            public IReadOnlyList<TilingNode> VisibleChildren { get; set; } = Array.Empty<TilingNode>();
            public Exception? ChildrenFailure;
            public int ChildrenReads;
            public Func<bool>? IsSnapshotDisposed;
            public int DetachesBeforeSnapshotDisposed;
            public List<string> DetachCalls { get; } = new();
            public IReadOnlyList<TilingNode> BackingChildren => m_children;
            public override TilingNodeType Type => TilingNodeType.Stack;
            public override IReadOnlyList<TilingNode> Children
            {
                get
                {
                    ChildrenReads++;
                    if (ChildrenFailure != null) throw ChildrenFailure;
                    return VisibleChildren;
                }
            }

            public void AddDirect(TilingNode node)
            {
                node.Parent = this;
                m_children.Add(node);
            }

            internal override void SetReference(int index, TilingNode node) => m_children[index] = node;
            protected override void AttachCore(int index, TilingNode node) => m_children.Insert(index, node);
            protected override void DetachCore(TilingNode node)
            {
                if (IsSnapshotDisposed != null && !IsSnapshotDisposed()) DetachesBeforeSnapshotDisposed++;
                if (!m_children.Remove(node)) throw new InvalidOperationException();
                DetachCalls.Add("detach");
            }
            public override Point GetMaxChildSize(TilingNode node) => new();
            public override Point GetMaxSizeForInsert(TilingNode node) => new();
            public override void Move(int fromIndex, int toIndex)
            {
                var node = m_children[fromIndex];
                m_children.RemoveAt(fromIndex);
                m_children.Insert(toIndex, node);
            }
            internal override void ArrangeCore(RectangleF rectangle) { }
            internal override void MeasureCore() { }
        }

        private interface OverriddenChildrenPanel
        {
            IReadOnlyList<TilingNode> BackingChildren { get; }
            IReadOnlyList<TilingNode>? VisibleChildren { get; set; }
            int ChildrenReads { get; }
            void ResetChildrenReads();
        }

        private sealed class OverriddenChildrenSplitPanel : SplitPanelNode, OverriddenChildrenPanel
        {
            private int m_childrenReads;
            private IReadOnlyList<TilingNode>? m_visibleChildren;
            public override IReadOnlyList<TilingNode> Children
            {
                get
                {
                    m_childrenReads++;
                    return m_visibleChildren ?? base.Children;
                }
            }
            public IReadOnlyList<TilingNode> BackingChildren => base.Children;
            public IReadOnlyList<TilingNode>? VisibleChildren
            {
                get => m_visibleChildren;
                set => m_visibleChildren = value;
            }
            public int ChildrenReads => m_childrenReads;
            public void ResetChildrenReads() => m_childrenReads = 0;
        }

        private sealed class OverriddenChildrenStackPanel : StackPanelNode, OverriddenChildrenPanel
        {
            private int m_childrenReads;
            private IReadOnlyList<TilingNode>? m_visibleChildren;
            public override IReadOnlyList<TilingNode> Children
            {
                get
                {
                    m_childrenReads++;
                    return m_visibleChildren ?? base.Children;
                }
            }
            public IReadOnlyList<TilingNode> BackingChildren => base.Children;
            public IReadOnlyList<TilingNode>? VisibleChildren
            {
                get => m_visibleChildren;
                set => m_visibleChildren = value;
            }
            public int ChildrenReads => m_childrenReads;
            public void ResetChildrenReads() => m_childrenReads = 0;
        }

        private sealed class RecordingChildren(IReadOnlyList<TilingNode> nodes) : IReadOnlyList<TilingNode>
        {
            public List<string> Calls { get; } = new();
            public int GenericEnumeratorReads;
            public int NonGenericEnumeratorReads;
            public int CountReads;
            public int IndexerReads;
            public bool Disposed;
            public Action? OnGetEnumerator;
            public Action<int>? OnMoveNext;
            public Action<int>? OnCurrent;
            public Action? OnDispose;

            public int Count
            {
                get
                {
                    CountReads++;
                    throw new InvalidOperationException("Unexpected custom Count read.");
                }
            }
            public TilingNode this[int index]
            {
                get
                {
                    IndexerReads++;
                    throw new InvalidOperationException("Unexpected custom indexer read.");
                }
            }

            IEnumerator<TilingNode> IEnumerable<TilingNode>.GetEnumerator()
            {
                GenericEnumeratorReads++;
                return CreateEnumerator("generic");
            }
            IEnumerator IEnumerable.GetEnumerator()
            {
                NonGenericEnumeratorReads++;
                return CreateEnumerator("non-generic");
            }

            private IEnumerator<TilingNode> CreateEnumerator(string kind)
            {
                Calls.Add($"{kind}:GetEnumerator");
                OnGetEnumerator?.Invoke();
                return new RecordingEnumerator(this, kind, nodes.GetEnumerator());
            }

            private sealed class RecordingEnumerator(
                RecordingChildren owner,
                string kind,
                IEnumerator<TilingNode> inner) : IEnumerator<TilingNode>
            {
                private int m_step;

                public TilingNode Current => ReadCurrent();
                object IEnumerator.Current => ReadCurrent();

                private TilingNode ReadCurrent()
                {
                    int step = m_step - 1;
                    owner.Calls.Add($"{kind}:Current:{step}");
                    owner.OnCurrent?.Invoke(step);
                    return inner.Current;
                }

                public bool MoveNext()
                {
                    int step = m_step++;
                    owner.Calls.Add($"{kind}:MoveNext:{step}");
                    owner.OnMoveNext?.Invoke(step);
                    return inner.MoveNext();
                }
                public void Reset() => throw new NotSupportedException();
                public void Dispose()
                {
                    owner.Calls.Add($"{kind}:Dispose");
                    owner.Disposed = true;
                    try { owner.OnDispose?.Invoke(); }
                    finally { inner.Dispose(); }
                }
            }
        }

        private sealed class ControlledCleanupException(string message) : Exception(message) { }
    }
}
