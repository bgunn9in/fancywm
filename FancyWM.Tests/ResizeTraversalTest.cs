using System;
using System.Collections.Generic;
using System.Linq;

using FancyWM.Layouts.Tiling;
using FancyWM.Tests.TestUtilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinMan;

namespace FancyWM.Tests
{
    [TestClass]
    public class ResizeTraversalTest
    {
        [DataTestMethod]
        [DataRow(PanelOrientation.Horizontal, 1)]
        [DataRow(PanelOrientation.Horizontal, 10)]
        [DataRow(PanelOrientation.Vertical, 1)]
        [DataRow(PanelOrientation.Vertical, 10)]
        public void NestedResizeUsesDirectChildWithoutScanningSubtrees(PanelOrientation orientation, int depth)
        {
            var fixture = new Fixture(orientation, depth);
            fixture.Resize(100);
            Assert.AreEqual(0, fixture.VisitedNodes, "The parent chain already identifies the direct child; sibling subtrees are irrelevant.");
            fixture.AssertGeometry(600);
            fixture.Resize(-100);
            fixture.AssertGeometry(500);
        }

        [DataTestMethod]
        [DataRow(PanelOrientation.Horizontal)]
        [DataRow(PanelOrientation.Vertical)]
        public void ResizeWithoutCompatibleAncestorLeavesTreeUnchanged(PanelOrientation orientation)
        {
            var fixture = new Fixture(orientation, 0);
            var previous = fixture.Target.ComputedRectangle;
            var requested = orientation == PanelOrientation.Horizontal
                ? new Rectangle(previous.Left, previous.Top, previous.Right, previous.Bottom + 100)
                : new Rectangle(previous.Left, previous.Top, previous.Right + 100, previous.Bottom);
            new TilingWorkspace().ResizeNode(fixture.Target, requested, previous);
            fixture.Tree.Arrange();
            Assert.AreEqual(previous, fixture.Target.ComputedRectangle);
            fixture.AssertGeometry(500);
        }

        [TestMethod]
        public void ResizeTraversalCounterScenario()
        {
            foreach (var depth in new[] { 1, 10, 25 })
            {
                var fixture = new Fixture(PanelOrientation.Horizontal, depth);
                for (int i = 0; i < 20; i++) { fixture.Resize(10); fixture.Resize(-10); }
                int visits = 0;
                for (int i = 0; i < 100; i++)
                {
                    fixture.Resize(10); visits += fixture.VisitedNodes;
                    fixture.Resize(-10); visits += fixture.VisitedNodes;
                    fixture.AssertGeometry(500);
                }
                Console.WriteLine($"PERFCOUNTER resize-depth-{depth} node-visits {visits}");
                Console.WriteLine($"PERFCOUNTER resize-depth-{depth} resize-calls 200");
            }
        }

        [DataTestMethod]
        [DataRow(PanelOrientation.Horizontal, false)]
        [DataRow(PanelOrientation.Horizontal, true)]
        [DataRow(PanelOrientation.Vertical, false)]
        [DataRow(PanelOrientation.Vertical, true)]
        public void EdgeResizePreservesAdjacentPanelRecursion(PanelOrientation orientation, bool towardsStart)
        {
            var root = new SplitPanelNode { Orientation = orientation };
            var inner = new SplitPanelNode { Orientation = orientation };
            var tree = new DesktopTree { Root = root, WorkArea = new Rectangle(0, 0, 1000, 1000) };
            var factory = new WindowMockFactory();
            var outside = new WindowNode(factory.CreateNotepadWindow());
            if (towardsStart) root.Attach(outside);
            root.Attach(inner);
            if (!towardsStart) root.Attach(outside);
            var first = new WindowNode(factory.CreateNotepadWindow());
            var last = new WindowNode(factory.CreateNotepadWindow());
            inner.Attach(first);
            inner.Attach(last);
            tree.Measure();
            tree.Arrange();
            var target = towardsStart ? first : last;
            var old = target.ComputedRectangle;
            var requested = orientation == PanelOrientation.Horizontal
                ? new Rectangle(old.Left - (towardsStart ? 100 : 0), old.Top, old.Right + (towardsStart ? 0 : 100), old.Bottom)
                : new Rectangle(old.Left, old.Top - (towardsStart ? 100 : 0), old.Right, old.Bottom + (towardsStart ? 0 : 100));
            new TilingWorkspace().ResizeNode(target, requested, old);
            tree.Arrange();
            Rectangle Rect(int start, int end) => orientation == PanelOrientation.Horizontal
                ? new Rectangle(start, 0, end, 1000) : new Rectangle(0, start, 1000, end);
            Assert.AreEqual(Rect(towardsStart ? 0 : 600, towardsStart ? 400 : 1000), outside.ComputedRectangle);
            // The outer sibling yields 100; the inner edge cannot redistribute
            // towards a missing sibling and its Flex transaction rolls back.
            // Both inner children then scale equally inside the 600-wide panel.
            Assert.AreEqual(Rect(towardsStart ? 400 : 0, towardsStart ? 700 : 300), first.ComputedRectangle);
            Assert.AreEqual(Rect(towardsStart ? 700 : 300, 1000 - (towardsStart ? 0 : 400)), last.ComputedRectangle);
            foreach (var node in new[] { outside, first, last }) Assert.AreSame(node, tree.FindNode(node.WindowReference));
            Assert.AreSame(inner, target.Parent);
        }

        private sealed class Fixture
        {
            private readonly PanelOrientation m_orientation;
            private readonly List<CountingPanel> m_panels = [];
            private readonly TilingWorkspace m_workspace = new();
            private readonly TilingNode m_resized;
            private readonly WindowNode m_other;
            public DesktopTree Tree { get; }
            public WindowNode Target { get; }
            public int VisitedNodes { get; private set; }

            public Fixture(PanelOrientation orientation, int depth)
            {
                m_orientation = orientation;
                var root = new CountingPanel { Orientation = orientation };
                m_panels.Add(root);
                Tree = new DesktopTree { Root = root, WorkArea = new Rectangle(0, 0, 1000, 1000) };
                PanelNode parent = root;
                for (int i = 0; i < depth; i++)
                {
                    var branch = new CountingPanel { Orientation = orientation == PanelOrientation.Horizontal ? PanelOrientation.Vertical : PanelOrientation.Horizontal };
                    m_panels.Add(branch);
                    parent.Attach(branch);
                    parent = branch;
                }
                Target = new WindowNode(new WindowMockFactory().CreateNotepadWindow());
                parent.Attach(Target);
                m_resized = root.Children[0];
                m_other = new WindowNode(new WindowMockFactory().CreateNotepadWindow());
                root.Attach(m_other);
                Tree.Measure();
                Tree.Arrange();
                AssertGeometry(500);
            }

            public void Resize(int delta)
            {
                var previous = Target.ComputedRectangle;
                var requested = m_orientation == PanelOrientation.Horizontal
                    ? new Rectangle(previous.Left, previous.Top, previous.Right + delta, previous.Bottom)
                    : new Rectangle(previous.Left, previous.Top, previous.Right, previous.Bottom + delta);
                foreach (var panel in m_panels) panel.VisitedNodes = 0;
                m_workspace.ResizeNode(Target, requested, previous);
                VisitedNodes = m_panels.Sum(panel => panel.VisitedNodes);
                Tree.Arrange();
            }

            public void AssertGeometry(int length)
            {
                var expected = m_orientation == PanelOrientation.Horizontal
                    ? new Rectangle(0, 0, length, 1000) : new Rectangle(0, 0, 1000, length);
                var remainder = m_orientation == PanelOrientation.Horizontal
                    ? new Rectangle(length, 0, 1000, 1000) : new Rectangle(0, length, 1000, 1000);
                Assert.AreEqual(expected, Target.ComputedRectangle);
                Assert.AreEqual(expected, m_resized.ComputedRectangle);
                Assert.AreEqual(remainder, m_other.ComputedRectangle);
                Assert.AreSame(Target, Tree.FindNode(Target.WindowReference));
                Assert.AreSame(m_other, Tree.FindNode(m_other.WindowReference));
                Assert.AreSame(m_resized, ((PanelNode)Tree.Root).Children[0]);
            }
        }

        private sealed class CountingPanel : SplitPanelNode
        {
            public int VisitedNodes;
            public override IEnumerable<TilingNode> Nodes
            {
                get
                {
                    foreach (var node in base.Nodes) { VisitedNodes++; yield return node; }
                }
            }
        }
    }
}
