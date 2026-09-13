#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;

using FancyWM.Layouts.Tiling;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Layouts.Tests.Tiling
{
    [TestClass]
    public class SplitPanelMeasureTest
    {
        [DataTestMethod]
        [DataRow(PanelOrientation.Horizontal, 64, 33)]
        [DataRow(PanelOrientation.Vertical, 41, 57)]
        public void MeasureCountsOnlyDirectExactAndDerivedWindows(
            PanelOrientation orientation,
            int expectedWidth,
            int expectedHeight)
        {
            var panel = new TestSplitPanel
            {
                Orientation = orientation,
                Spacing = 5,
                Padding = new Rectangle(1, 2, 3, 4),
            };
            panel.AddBacking(new WindowNode(CreateWindow(new Point(10, 20))));
            panel.AddBacking(new DerivedWindowNode(CreateWindow(new Point(30, 5))));
            panel.AddBacking(new SizedNode(new Point(6, 8)));
            var nested = new TestSplitPanel();
            nested.AddBacking(new WindowNode(CreateWindow(new Point(7, 11))));
            panel.AddBacking(nested);

            panel.Measure();

            Assert.AreEqual(new Point(expectedWidth, expectedHeight), panel.ContentMinSize);
            Assert.AreEqual(new Point(short.MaxValue, short.MaxValue), panel.ContentMaxSize);
        }

        [TestMethod]
        public void MeasureUsesBackingWindowsAfterVirtualChildrenDispose()
        {
            var events = new List<string>();
            var panel = new CallbackChildrenPanel
            {
                Orientation = PanelOrientation.Horizontal,
                VisibleChildren =
                [
                    new SizedNode(new Point(11, 13), () => events.Add("measure:first")),
                    new SizedNode(new Point(17, 19), () => events.Add("measure:second")),
                ],
                Events = events,
            };
            int hiddenReads = 0;
            panel.AddBacking(new DerivedWindowNode(CreateWindow(new Point(101, 103), () => hiddenReads++)));
            var added = new WindowNode(CreateWindow(new Point(107, 109), () => hiddenReads++));
            panel.OnDispose = () =>
            {
                panel.AddBacking(added);
                panel.Spacing = 5;
                panel.Padding = new Rectangle(1, 2, 3, 4);
                panel.Orientation = PanelOrientation.Vertical;
            };

            panel.Measure();

            CollectionAssert.AreEqual(new[]
            {
                "children:get", "enumerator:get", "move:0", "current:0", "measure:first",
                "move:1", "current:1", "measure:second", "move:2", "dispose",
            }, events);
            Assert.AreEqual(1, panel.ChildrenReads);
            Assert.AreEqual(1, panel.EnumeratorReads);
            Assert.AreEqual(1, panel.Disposals);
            Assert.AreEqual(0, hiddenReads, "Backing-only windows must not be measured through the virtual view.");
            Assert.AreEqual(2, panel.BackingChildren.Count);
            Assert.AreSame(added, panel.BackingChildren[1]);
            Assert.AreEqual(PanelOrientation.Vertical, panel.Orientation);
            Assert.AreEqual(new Point(39, 32), panel.ContentMinSize,
                "Dimensions come from the original horizontal view; spacing and padding come from post-Dispose state.");
        }

        [TestMethod]
        public void DisposeFailurePreservesIdentityAndPreventsParentSizePublication()
        {
            var events = new List<string>();
            var childFailure = new ControlledMeasureException("child");
            var disposeFailure = new ControlledMeasureException("dispose");
            var first = new SizedNode(new Point(5, 7), () => events.Add("measure:first"));
            var failing = new SizedNode(new Point(11, 13), () =>
            {
                events.Add("measure:failing");
                throw childFailure;
            });
            var panel = new CallbackChildrenPanel
            {
                VisibleChildren = [first, failing],
                Events = events,
                OnDispose = () => throw disposeFailure,
            };
            panel.SeedPublishedSizes(new Point(101, 103), new Point(107, 109));

            var actual = Assert.ThrowsException<ControlledMeasureException>(() => panel.Measure());

            Assert.AreSame(disposeFailure, actual);
            CollectionAssert.AreEqual(new[]
            {
                "children:get", "enumerator:get", "move:0", "current:0", "measure:first",
                "move:1", "current:1", "measure:failing", "dispose",
            }, events);
            Assert.AreEqual(new Point(101, 103), panel.ContentMinSize);
            Assert.AreEqual(new Point(107, 109), panel.ContentMaxSize);
            Assert.AreEqual(new Point(5, 7), first.ContentMinSize);
            Assert.AreEqual(new Point(), failing.ContentMinSize);
        }

        [TestMethod]
        public void MeasureSelectsOrientationBeforeReadingBackingListChildren()
        {
            var events = new List<string>();
            var panel = new BackingChildrenPanel
            {
                Orientation = PanelOrientation.Horizontal,
                Events = events,
            };
            panel.AddBacking(new SizedNode(new Point(11, 13), () => events.Add("measure:first")));
            panel.AddBacking(new SizedNode(new Point(17, 19), () => events.Add("measure:second")));
            panel.OnChildrenRead = () => panel.Orientation = PanelOrientation.Vertical;

            panel.Measure();

            CollectionAssert.AreEqual(new[] { "children:get", "measure:first", "measure:second" }, events);
            Assert.AreEqual(1, panel.ChildrenReads);
            Assert.AreEqual(PanelOrientation.Vertical, panel.Orientation);
            Assert.AreEqual(new Point(28, 19), panel.ContentMinSize,
                "The original horizontal branch and the backing list returned by the one Children read must drive this pass.");
        }

        [TestMethod]
        public void MeasureExactListMutationRetainsVersionFailureAndPreventsParentSizePublication()
        {
            var events = new List<string>();
            var panel = new BackingChildrenPanel { Events = events };
            var added = new SizedNode(new Point(23, 29), () => events.Add("measure:added"));
            panel.AddBacking(new SizedNode(new Point(5, 7), () =>
            {
                events.Add("measure:first");
                panel.AddBacking(added);
            }));
            panel.AddBacking(new SizedNode(new Point(11, 13), () => events.Add("measure:second")));
            panel.SeedPublishedSizes(new Point(101, 103), new Point(107, 109));

            Assert.ThrowsException<InvalidOperationException>(() => panel.Measure());

            CollectionAssert.AreEqual(new[] { "children:get", "measure:first" }, events);
            Assert.AreEqual(3, panel.BackingChildren.Count);
            Assert.AreSame(added, panel.BackingChildren[2]);
            Assert.AreEqual(new Point(101, 103), panel.ContentMinSize);
            Assert.AreEqual(new Point(107, 109), panel.ContentMaxSize);
        }

        [TestMethod]
        public void MeasureExactListChildFailurePreservesIdentityAndPreventsParentSizePublication()
        {
            var events = new List<string>();
            var failure = new ControlledMeasureException("exact-list-child");
            var first = new SizedNode(new Point(5, 7), () => events.Add("measure:first"));
            var failing = new SizedNode(new Point(11, 13), () =>
            {
                events.Add("measure:failing");
                throw failure;
            });
            var panel = new BackingChildrenPanel { Events = events };
            panel.AddBacking(first);
            panel.AddBacking(failing);
            panel.SeedPublishedSizes(new Point(101, 103), new Point(107, 109));

            var actual = Assert.ThrowsException<ControlledMeasureException>(() => panel.Measure());

            Assert.AreSame(failure, actual);
            CollectionAssert.AreEqual(new[] { "children:get", "measure:first", "measure:failing" }, events);
            Assert.AreEqual(new Point(101, 103), panel.ContentMinSize);
            Assert.AreEqual(new Point(107, 109), panel.ContentMaxSize);
            Assert.AreEqual(new Point(5, 7), first.ContentMinSize);
            Assert.AreEqual(new Point(), failing.ContentMinSize);
        }

        [TestMethod]
        public void MeasureDerivedListRetainsInterfaceEnumeration()
        {
            var events = new List<string>();
            var visible = new DerivedChildrenList(events)
            {
                new SizedNode(new Point(5, 7), () => events.Add("measure")),
            };
            var panel = new ListChildrenPanel
            {
                VisibleChildren = visible,
                Events = events,
            };

            panel.Measure();

            CollectionAssert.AreEqual(new[]
            {
                "children:get", "derived:enumerator", "derived:move", "derived:current",
                "measure", "derived:move", "derived:dispose",
            }, events);
            Assert.AreEqual(new Point(5, 7), panel.ContentMinSize);
        }

        [TestMethod]
        public void MeasureNullChildrenRetainsNullReferenceFailureAndPreventsPublication()
        {
            var panel = new ListChildrenPanel { VisibleChildren = null! };
            panel.SeedPublishedSizes(new Point(101, 103), new Point(107, 109));

            Assert.ThrowsException<NullReferenceException>(() => panel.Measure());

            Assert.AreEqual(1, panel.ChildrenReads);
            Assert.AreEqual(new Point(101, 103), panel.ContentMinSize);
            Assert.AreEqual(new Point(107, 109), panel.ContentMaxSize);
        }

        [DataTestMethod]
        [DataRow(false, 1)]
        [DataRow(false, 10)]
        [DataRow(false, 50)]
        [DataRow(true, 1)]
        [DataRow(true, 10)]
        [DataRow(true, 50)]
        public void MeasureAvoidsPostTraversalLinqAllocation(bool hiddenChildren, int childCount)
        {
            TestSplitPanel panel = hiddenChildren ? new HiddenChildrenSplitPanel() : new TestSplitPanel();
            panel.Spacing = 7;
            panel.Padding = new Rectangle(1, 2, 3, 4);
            for (int i = 0; i < childCount; i++) panel.AddBacking(new PlaceholderNode());

            long warmChecksum = MeasureRepeatedly(panel, 1_000);
            Assert.AreEqual(16_000L, warmChecksum);
            long before = GC.GetAllocatedBytesForCurrentThread();
            long checksum = MeasureRepeatedly(panel, 1_000);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(16_000L, checksum);
            long expected = 0;
            Assert.AreEqual(expected, allocated,
                $"{(hiddenChildren ? "Hidden" : "ordinary")} Children with N={childCount} allocated {allocated} B for 1,000 Measure calls.");
        }

        [TestMethod]
        public void SplitPanelMeasureCounterScenario()
        {
            const int measures = 100_000;
            foreach (bool hiddenChildren in new[] { true, false })
            {
                foreach (int childCount in new[] { 1, 10, 50 })
                {
                    TestSplitPanel panel = hiddenChildren ? new HiddenChildrenSplitPanel() : new TestSplitPanel();
                    panel.Spacing = 7;
                    panel.Padding = new Rectangle(1, 2, 3, 4);
                    for (int i = 0; i < childCount; i++) panel.AddBacking(new PlaceholderNode());
                    Assert.AreEqual(16_000L, MeasureRepeatedly(panel, 1_000));
                    _ = Stopwatch.GetTimestamp();

                    long widthSum = 0;
                    long heightSum = 0;
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    long started = Stopwatch.GetTimestamp();
                    for (int i = 0; i < measures; i++)
                    {
                        panel.Measure();
                        widthSum += panel.ContentMinSize.X;
                        heightSum += panel.ContentMinSize.Y;
                    }
                    long elapsed = Stopwatch.GetTimestamp() - started;
                    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                    Assert.AreEqual(700_000L, widthSum);
                    Assert.AreEqual(900_000L, heightSum);
                    string mode = hiddenChildren ? "hidden" : "ordinary";
                    Console.WriteLine($"PERFCOUNTER split-measure-{mode}-{childCount} allocated-bytes {allocated}");
                    Console.WriteLine($"PERFCOUNTER split-measure-{mode}-{childCount} elapsed-ticks {elapsed}");
                    Console.WriteLine($"PERFCOUNTER split-measure-{mode}-{childCount} timestamp-frequency {Stopwatch.Frequency}");
                    Console.WriteLine($"PERFCOUNTER split-measure-{mode}-{childCount} measures {measures}");
                    Console.WriteLine($"PERFCOUNTER split-measure-{mode}-{childCount} backing-children {childCount}");
                    Console.WriteLine($"PERFCOUNTER split-measure-{mode}-{childCount} published-width-sum {widthSum}");
                    Console.WriteLine($"PERFCOUNTER split-measure-{mode}-{childCount} published-height-sum {heightSum}");
                }
            }
        }

        private static long MeasureRepeatedly(SplitPanelNode panel, int count)
        {
            long checksum = 0;
            for (int i = 0; i < count; i++)
            {
                panel.Measure();
                checksum += panel.ContentMinSize.X + panel.ContentMinSize.Y;
            }
            return checksum;
        }

        private static IWindow CreateWindow(Point minimum, Action? onMinimumRead = null)
        {
            var mock = new Mock<IWindow>();
            mock.SetupGet(window => window.MinSize).Returns(() =>
            {
                onMinimumRead?.Invoke();
                return minimum;
            });
            return mock.Object;
        }

        private class TestSplitPanel : SplitPanelNode
        {
            private int m_backingCount;

            public IReadOnlyList<TilingNode> BackingChildren => base.Children;

            public void AddBacking(TilingNode node)
            {
                node.Parent = this;
                base.AttachCore(m_backingCount++, node);
            }

            public void SeedPublishedSizes(Point minimum, Point maximum)
            {
                ContentMinSize = minimum;
                ContentMaxSize = maximum;
            }
        }

        private sealed class HiddenChildrenSplitPanel : TestSplitPanel
        {
            public override IReadOnlyList<TilingNode> Children => Array.Empty<TilingNode>();
        }

        private sealed class CallbackChildrenPanel : TestSplitPanel
        {
            public IReadOnlyList<TilingNode> VisibleChildren { get; init; } = Array.Empty<TilingNode>();
            public List<string> Events { get; init; } = new();
            public Action? OnDispose;
            public int ChildrenReads;
            public int EnumeratorReads;
            public int Disposals;

            public override IReadOnlyList<TilingNode> Children
            {
                get
                {
                    ChildrenReads++;
                    Events.Add("children:get");
                    return new RecordingChildren(this, VisibleChildren);
                }
            }

            private sealed class RecordingChildren(CallbackChildrenPanel owner, IReadOnlyList<TilingNode> nodes)
                : IReadOnlyList<TilingNode>
            {
                public int Count => nodes.Count;
                public TilingNode this[int index] => nodes[index];
                public IEnumerator<TilingNode> GetEnumerator()
                {
                    owner.EnumeratorReads++;
                    owner.Events.Add("enumerator:get");
                    return new Enumerator(owner, nodes);
                }
                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

                private sealed class Enumerator(CallbackChildrenPanel owner, IReadOnlyList<TilingNode> nodes)
                    : IEnumerator<TilingNode>
                {
                    private int m_index = -1;
                    public TilingNode Current
                    {
                        get
                        {
                            owner.Events.Add($"current:{m_index}");
                            return nodes[m_index];
                        }
                    }
                    object IEnumerator.Current => Current;
                    public bool MoveNext()
                    {
                        m_index++;
                        owner.Events.Add($"move:{m_index}");
                        return m_index < nodes.Count;
                    }
                    public void Reset() => throw new NotSupportedException();
                    public void Dispose()
                    {
                        owner.Disposals++;
                        owner.Events.Add("dispose");
                        owner.OnDispose?.Invoke();
                    }
                }
            }
        }

        private sealed class ListChildrenPanel : TestSplitPanel
        {
            public IReadOnlyList<TilingNode> VisibleChildren { get; set; } = new List<TilingNode>();
            public List<string> Events { get; init; } = new();
            public int ChildrenReads;

            public override IReadOnlyList<TilingNode> Children
            {
                get
                {
                    ChildrenReads++;
                    Events.Add("children:get");
                    return VisibleChildren;
                }
            }
        }

        private sealed class BackingChildrenPanel : TestSplitPanel
        {
            public List<string> Events { get; init; } = new();
            public Action? OnChildrenRead;
            public int ChildrenReads;

            public override IReadOnlyList<TilingNode> Children
            {
                get
                {
                    ChildrenReads++;
                    Events.Add("children:get");
                    var children = base.Children;
                    OnChildrenRead?.Invoke();
                    return children;
                }
            }
        }

        private sealed class DerivedChildrenList(List<string> events) : List<TilingNode>, IEnumerable<TilingNode>
        {
            IEnumerator<TilingNode> IEnumerable<TilingNode>.GetEnumerator()
            {
                events.Add("derived:enumerator");
                return new RecordingEnumerator(base.GetEnumerator(), events);
            }

            IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<TilingNode>)this).GetEnumerator();

            private sealed class RecordingEnumerator(IEnumerator<TilingNode> inner, List<string> events)
                : IEnumerator<TilingNode>
            {
                public TilingNode Current
                {
                    get
                    {
                        events.Add("derived:current");
                        return inner.Current;
                    }
                }

                object IEnumerator.Current => Current;

                public bool MoveNext()
                {
                    events.Add("derived:move");
                    return inner.MoveNext();
                }

                public void Reset() => inner.Reset();

                public void Dispose()
                {
                    events.Add("derived:dispose");
                    inner.Dispose();
                }
            }
        }

        private sealed class SizedNode(Point size, Action? onMeasure = null) : TilingNode
        {
            public override TilingNodeType Type => TilingNodeType.Static;
            public override IEnumerable<WindowNode> Windows => Array.Empty<WindowNode>();
            public override IEnumerable<TilingNode> Nodes => new[] { this };
            internal override void ArrangeCore(RectangleF rectangle) { }
            internal override void MeasureCore()
            {
                onMeasure?.Invoke();
                ContentMinSize = size;
            }
        }

        private sealed class DerivedWindowNode(IWindow window) : WindowNode(window) { }

        private sealed class ControlledMeasureException(string message) : Exception(message) { }
    }
}
