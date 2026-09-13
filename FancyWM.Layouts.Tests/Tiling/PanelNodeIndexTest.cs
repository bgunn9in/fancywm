#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;

using FancyWM.Layouts.Tiling;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Layouts.Tests.Tiling
{
    [TestClass]
    public class PanelNodeIndexTest
    {
        [DataTestMethod]
        [DataRow("split")]
        [DataRow("stack")]
        public void IndexReturnsFirstReferenceOrTheNumberOfVisitedChildren(string panelKind)
        {
            var (panel, nodes) = CreatePanel(panelKind, 5);

            Assert.AreEqual(0, panel.IndexOf(nodes[0]));
            Assert.AreEqual(2, panel.IndexOf(nodes[2]));
            Assert.AreEqual(4, panel.IndexOf(nodes[4]));
            Assert.AreEqual(5, panel.IndexOf(new PlaceholderNode()));

            panel.Attach(nodes[1]);
            Assert.AreEqual(1, panel.IndexOf(nodes[1]), "The first duplicate reference wins.");
            Assert.AreEqual(6, panel.IndexOf(new PlaceholderNode()));

            var (empty, _) = CreatePanel(panelKind, 0);
            Assert.AreEqual(0, empty.IndexOf(new PlaceholderNode()));
        }

        [DataTestMethod]
        [DataRow("split")]
        [DataRow("stack")]
        public void IndexUsesReferenceIdentityEvenWhenNodesOverrideEquals(string panelKind)
        {
            var (panel, _) = CreatePanel(panelKind, 0);
            var first = new EqualNode();
            var second = new EqualNode();
            var missing = new EqualNode();
            panel.Attach(first);
            panel.Attach(second);

            Assert.AreEqual(1, panel.IndexOf(second));
            Assert.AreEqual(2, panel.IndexOf(missing));
            Assert.AreEqual(0, first.EqualityCalls + second.EqualityCalls + missing.EqualityCalls,
                "IndexOf must not invoke an overridable equality implementation.");
        }

        [DataTestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(-1)]
        public void IndexEnumeratesCustomChildrenOnceAndDisposesAtTheExactStoppingPoint(int matchIndex)
        {
            TilingNode[] nodes = [new PlaceholderNode(), new PlaceholderNode(), new PlaceholderNode()];
            var source = new RecordingChildren(nodes);
            var panel = new SuppliedChildrenPanel(() => source);
            var query = matchIndex < 0 ? new PlaceholderNode() : nodes[matchIndex];

            Assert.AreEqual(matchIndex < 0 ? nodes.Length : matchIndex, panel.IndexOf(query));

            Assert.AreEqual(1, panel.ChildrenReads);
            CollectionAssert.AreEqual(CompletedCalls(nodes.Length, matchIndex), source.Calls);
            AssertNoIndexedAccess(source);
        }

        [TestMethod]
        public void IndexEnumeratesAndDisposesAnEmptyCustomCollection()
        {
            var source = new RecordingChildren(Array.Empty<TilingNode>());
            var panel = new SuppliedChildrenPanel(() => source);

            Assert.AreEqual(0, panel.IndexOf(new PlaceholderNode()));

            Assert.AreEqual(1, panel.ChildrenReads);
            CollectionAssert.AreEqual(new[] { "GetEnumerator", "MoveNext:0", "Dispose" }, source.Calls);
            AssertNoIndexedAccess(source);
        }

        [TestMethod]
        public void IndexRetainsNullQueryAndNullElementReferenceSemantics()
        {
            var source = new RecordingChildren(new TilingNode[] { new PlaceholderNode(), null!, new PlaceholderNode() });
            var panel = new SuppliedChildrenPanel(() => source);

            Assert.AreEqual(1, panel.IndexOf(null!));

            Assert.AreEqual(1, panel.ChildrenReads);
            CollectionAssert.AreEqual(CompletedCalls(3, 1), source.Calls);
            AssertNoIndexedAccess(source);
        }

        [TestMethod]
        public void IndexReadsChildrenOnceAndPreservesTheNullSourceException()
        {
            var panel = new SuppliedChildrenPanel(() => null!);

            var failure = Assert.ThrowsException<ArgumentNullException>(() => panel.IndexOf(new PlaceholderNode()));

            Assert.AreEqual("source", failure.ParamName);
            Assert.AreEqual(1, panel.ChildrenReads);
        }

        [TestMethod]
        public void IndexPreservesChildrenGetterFailureIdentity()
        {
            var failure = new ControlledIndexException("children getter");
            var panel = new SuppliedChildrenPanel(() => throw failure);

            Assert.AreSame(failure,
                Assert.ThrowsException<ControlledIndexException>(() => panel.IndexOf(new PlaceholderNode())));
            Assert.AreEqual(1, panel.ChildrenReads);
        }

        [DataTestMethod]
        [DataRow("get-enumerator")]
        [DataRow("move-first")]
        [DataRow("move-late")]
        [DataRow("current-first")]
        [DataRow("current-late")]
        [DataRow("dispose-hit")]
        [DataRow("dispose-miss")]
        [DataRow("move-and-dispose")]
        public void IndexPreservesEnumerationFailureIdentityAndDisposalOrder(string stage)
        {
            TilingNode[] nodes = [new PlaceholderNode(), new PlaceholderNode(), new PlaceholderNode()];
            var source = new RecordingChildren(nodes);
            var panel = new SuppliedChildrenPanel(() => source);
            var failure = new ControlledIndexException(stage);
            var disposalFailure = new ControlledIndexException("dispose supersedes move failure");
            var expectedFailure = stage == "move-and-dispose" ? disposalFailure : failure;
            var query = stage == "dispose-miss" ? new PlaceholderNode() : nodes[1];
            string[] expectedCalls;

            switch (stage)
            {
                case "get-enumerator":
                    source.OnGetEnumerator = () => throw failure;
                    expectedCalls = ["GetEnumerator"];
                    break;
                case "move-first":
                    source.OnMoveNext = step => { if (step == 0) throw failure; };
                    expectedCalls = ["GetEnumerator", "MoveNext:0", "Dispose"];
                    break;
                case "move-late":
                case "move-and-dispose":
                    source.OnMoveNext = step => { if (step == 1) throw failure; };
                    if (stage == "move-and-dispose") source.OnDispose = () => throw disposalFailure;
                    expectedCalls = ["GetEnumerator", "MoveNext:0", "Current:0", "MoveNext:1", "Dispose"];
                    break;
                case "current-first":
                    source.OnCurrent = step => { if (step == 0) throw failure; };
                    expectedCalls = ["GetEnumerator", "MoveNext:0", "Current:0", "Dispose"];
                    break;
                case "current-late":
                    source.OnCurrent = step => { if (step == 1) throw failure; };
                    expectedCalls = ["GetEnumerator", "MoveNext:0", "Current:0", "MoveNext:1", "Current:1", "Dispose"];
                    break;
                default:
                    source.OnDispose = () => throw failure;
                    expectedCalls = CompletedCalls(nodes.Length, stage == "dispose-hit" ? 1 : -1);
                    break;
            }

            Assert.AreSame(expectedFailure,
                Assert.ThrowsException<ControlledIndexException>(() => panel.IndexOf(query)));
            Assert.AreEqual(1, panel.ChildrenReads);
            CollectionAssert.AreEqual(expectedCalls, source.Calls);
            AssertNoIndexedAccess(source);
        }

        [TestMethod]
        public void IndexKeepsTheAcquiredSequenceAndObservesUnvisitedArrayMutation()
        {
            var query = new PlaceholderNode();
            TilingNode[] nodes = [new PlaceholderNode(), new PlaceholderNode(), new PlaceholderNode()];
            var source = new RecordingChildren(nodes);
            var replacement = new RecordingChildren(new[] { query });
            IReadOnlyList<TilingNode> supplied = source;
            var panel = new SuppliedChildrenPanel(() => supplied);
            source.OnCurrent = step =>
            {
                if (step == 0)
                {
                    nodes[1] = query;
                    supplied = replacement;
                }
            };

            Assert.AreEqual(1, panel.IndexOf(query));

            Assert.AreEqual(1, panel.ChildrenReads);
            Assert.AreSame(query, nodes[1]);
            CollectionAssert.AreEqual(CompletedCalls(nodes.Length, 1), source.Calls);
            Assert.AreEqual(0, replacement.Calls.Count);

            Assert.AreEqual(0, panel.IndexOf(query), "A later call must read the newly supplied children.");

            Assert.AreEqual(2, panel.ChildrenReads);
            CollectionAssert.AreEqual(CompletedCalls(1, 0), replacement.Calls);
            AssertNoIndexedAccess(source);
            AssertNoIndexedAccess(replacement);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void IndexPreservesVersionedEnumeratorMutationAtMissAndEarlyHit(bool firstChildMatches)
        {
            var first = new PlaceholderNode();
            var appended = new PlaceholderNode();
            var nodes = new List<TilingNode> { first, new PlaceholderNode() };
            var source = new RecordingChildren(nodes);
            var panel = new SuppliedChildrenPanel(() => source);
            source.OnCurrent = step => { if (step == 0) nodes.Add(appended); };

            if (firstChildMatches)
            {
                Assert.AreEqual(0, panel.IndexOf(first));
                CollectionAssert.AreEqual(CompletedCalls(2, 0), source.Calls);
            }
            else
            {
                Assert.ThrowsException<InvalidOperationException>(() => panel.IndexOf(appended));
                CollectionAssert.AreEqual(
                    new[] { "GetEnumerator", "MoveNext:0", "Current:0", "MoveNext:1", "Dispose" }, source.Calls);
            }
            Assert.AreEqual(1, panel.ChildrenReads);
            Assert.AreEqual(3, nodes.Count);
            AssertNoIndexedAccess(source);
        }

        [TestMethod]
        public void IndexHonorsDerivedListEnumerationInsteadOfItsUnderlyingOrder()
        {
            TilingNode[] nodes = [new PlaceholderNode(), new PlaceholderNode(), new PlaceholderNode()];
            var enumeration = new RecordingChildren(new[] { nodes[2], nodes[1], nodes[0] });
            var source = new DerivedChildrenList(nodes, enumeration);
            var panel = new SuppliedChildrenPanel(() => source);

            Assert.AreEqual(0, panel.IndexOf(nodes[2]));

            Assert.AreEqual(1, panel.ChildrenReads);
            CollectionAssert.AreEqual(CompletedCalls(nodes.Length, 0), enumeration.Calls);
            AssertNoIndexedAccess(enumeration);
        }

        [DataTestMethod]
        [DataRow("split", 1)]
        [DataRow("split", 10)]
        [DataRow("split", 50)]
        [DataRow("stack", 1)]
        [DataRow("stack", 10)]
        [DataRow("stack", 50)]
        public void RepeatedExactListIndexLookupDoesNotAllocate(string panelKind, int count)
        {
            var (panel, nodes) = CreatePanel(panelKind, count);
            Assert.AreEqual(typeof(List<TilingNode>), panel.Children.GetType());
            var last = nodes[^1];
            var missing = new PlaceholderNode();
            WarmIndexLookup(panel, last, missing, count);

            const int lookups = 1000;
            long lastIndexSum = 0;
            long missingIndexSum = 0;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < lookups; i++)
            {
                if ((i & 1) == 0) lastIndexSum += panel.IndexOf(last);
                else missingIndexSum += panel.IndexOf(missing);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            AssertIndexSums(count, lookups, lastIndexSum, missingIndexSum);
            Console.WriteLine($"PANELINDEX_ALLOC {panelKind} children {count} lookups {lookups} allocated-bytes {allocated}");
            Assert.AreEqual(0L, allocated,
                $"The fixed exact-list budget is 0 B/call: {panelKind}, N={count}, {lookups} lookups allocated {allocated} B.");
        }

        [TestMethod]
        public void PanelNodeIndexCounterScenario()
        {
            foreach (int count in new[] { 1, 10, 50 })
            {
                var (panel, nodes) = CreatePanel("split", count);
                var last = nodes[^1];
                var missing = new PlaceholderNode();
                WarmIndexLookup(panel, last, missing, count);
                _ = Stopwatch.GetTimestamp();

                const int lookups = 100000;
                long lastIndexSum = 0;
                long missingIndexSum = 0;
                long before = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                for (int i = 0; i < lookups; i++)
                {
                    if ((i & 1) == 0) lastIndexSum += panel.IndexOf(last);
                    else missingIndexSum += panel.IndexOf(missing);
                }
                long elapsed = Stopwatch.GetTimestamp() - started;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                AssertIndexSums(count, lookups, lastIndexSum, missingIndexSum);
                Console.WriteLine($"PERFCOUNTER panel-index-{count} allocated-bytes {allocated}");
                Console.WriteLine($"PERFCOUNTER panel-index-{count} elapsed-ticks {elapsed}");
                Console.WriteLine($"PERFCOUNTER panel-index-{count} timestamp-frequency {Stopwatch.Frequency}");
                Console.WriteLine($"PERFCOUNTER panel-index-{count} lookups {lookups}");
                Console.WriteLine($"PERFCOUNTER panel-index-{count} last-index-sum {lastIndexSum}");
                Console.WriteLine($"PERFCOUNTER panel-index-{count} missing-index-sum {missingIndexSum}");
            }
        }

        private static (PanelNode Panel, TilingNode[] Nodes) CreatePanel(string panelKind, int count)
        {
            PanelNode panel = panelKind == "split" ? new SplitPanelNode() : new StackPanelNode();
            var nodes = new TilingNode[count];
            for (int i = 0; i < count; i++)
            {
                nodes[i] = new PlaceholderNode();
                panel.Attach(nodes[i]);
            }
            return (panel, nodes);
        }

        private static void WarmIndexLookup(PanelNode panel, TilingNode last, TilingNode missing, int count)
        {
            const int warmups = 1000;
            long lastIndexSum = 0;
            long missingIndexSum = 0;
            for (int i = 0; i < warmups; i++)
            {
                if ((i & 1) == 0) lastIndexSum += panel.IndexOf(last);
                else missingIndexSum += panel.IndexOf(missing);
            }
            AssertIndexSums(count, warmups, lastIndexSum, missingIndexSum);
        }

        private static void AssertIndexSums(int count, int lookups, long lastIndexSum, long missingIndexSum)
        {
            Assert.AreEqual(checked((long)(lookups / 2) * (count - 1)), lastIndexSum);
            Assert.AreEqual(checked((long)(lookups / 2) * count), missingIndexSum);
        }

        private static string[] CompletedCalls(int count, int matchIndex)
        {
            var calls = new List<string> { "GetEnumerator" };
            int visited = matchIndex < 0 ? count : matchIndex + 1;
            for (int i = 0; i < visited; i++)
            {
                calls.Add($"MoveNext:{i}");
                calls.Add($"Current:{i}");
            }
            if (matchIndex < 0) calls.Add($"MoveNext:{count}");
            calls.Add("Dispose");
            return calls.ToArray();
        }

        private static void AssertNoIndexedAccess(RecordingChildren source)
        {
            Assert.AreEqual(0, source.CountReads);
            Assert.AreEqual(0, source.IndexerReads);
        }

        private sealed class EqualNode : PlaceholderNode
        {
            public int EqualityCalls;
            public override bool Equals(object? obj)
            {
                EqualityCalls++;
                return obj is EqualNode;
            }
            public override int GetHashCode() => 0;
        }

        private sealed class SuppliedChildrenPanel(Func<IReadOnlyList<TilingNode>> children) : SplitPanelNode
        {
            public int ChildrenReads;
            public override IReadOnlyList<TilingNode> Children
            {
                get { ChildrenReads++; return children(); }
            }
        }

        private sealed class ControlledIndexException(string message) : Exception(message) { }

        private sealed class DerivedChildrenList(IEnumerable<TilingNode> nodes, RecordingChildren enumeration)
            : List<TilingNode>(nodes), IEnumerable<TilingNode>
        {
            IEnumerator<TilingNode> IEnumerable<TilingNode>.GetEnumerator() => enumeration.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => enumeration.GetEnumerator();
        }

        private sealed class RecordingChildren(IEnumerable<TilingNode> nodes) : IReadOnlyList<TilingNode>
        {
            public List<string> Calls { get; } = new();
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
                get { IndexerReads++; throw new InvalidOperationException("Unexpected custom indexer read."); }
            }
            public IEnumerator<TilingNode> GetEnumerator()
            {
                Calls.Add("GetEnumerator");
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
                        owner.Calls.Add($"Current:{m_step - 1}");
                        owner.OnCurrent?.Invoke(m_step - 1);
                        return inner.Current;
                    }
                }
                object IEnumerator.Current => Current;
                public bool MoveNext()
                {
                    int step = m_step++;
                    owner.Calls.Add($"MoveNext:{step}");
                    owner.OnMoveNext?.Invoke(step);
                    return inner.MoveNext();
                }
                public void Reset() => throw new NotSupportedException();
                public void Dispose()
                {
                    owner.Calls.Add("Dispose");
                    try { owner.OnDispose?.Invoke(); }
                    finally { inner.Dispose(); }
                }
            }
        }
    }
}
