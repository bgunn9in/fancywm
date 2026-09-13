#nullable enable

using System;
using System.Linq;

using FancyWM.Layouts.Tiling;
using FancyWM.Tests.AlgorithmicLayouts;
using FancyWM.Tests.TestUtilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests
{
    [TestClass]
    public class GenericPreviewReuseBoundaryTest
    {
        [TestMethod]
        public void RepeatedPreviewDoesNotReuseCloneAcrossUnversionedCloneCallback()
        {
            var fixture = new Fixture(10);
            fixture.WarmPreviewAndArm();

            var caught = Assert.ThrowsException<InvalidOperationException>(() => fixture.Preview());

            Assert.AreSame(fixture.Probe.Error, caught);
            Assert.AreEqual(1, fixture.Probe.ArmedCloneCalls);
            Assert.AreEqual(0, fixture.MinimumReads);
            fixture.AssertLiveTreeUnchanged();
        }

        [TestMethod]
        public void GenericPreviewCloneBoundaryCounterScenario()
        {
            foreach (int count in new[] { 1, 10, 50 })
            {
                var fixture = new Fixture(count);
                fixture.WarmPreviewAndArm();
                int controlledErrors = 0;
                for (int iteration = 0; iteration < 100; iteration++)
                {
                    try
                    {
                        fixture.Preview();
                        Assert.Fail("A preview reused a clone across an unversioned virtual Clone callback.");
                    }
                    catch (InvalidOperationException error) when (ReferenceEquals(error, fixture.Probe.Error))
                    {
                        controlledErrors++;
                    }
                }

                Assert.AreEqual(100, controlledErrors);
                Assert.AreEqual(100, fixture.Probe.ArmedCloneCalls);
                Assert.AreEqual(0, fixture.MinimumReads);
                fixture.AssertLiveTreeUnchanged();
                Console.WriteLine($"PERFCOUNTER generic-preview-clone-boundary-{count} cycles 100");
                Console.WriteLine($"PERFCOUNTER generic-preview-clone-boundary-{count} controlled-errors {controlledErrors}");
                Console.WriteLine($"PERFCOUNTER generic-preview-clone-boundary-{count} clone-callbacks {fixture.Probe.ArmedCloneCalls}");
                Console.WriteLine($"PERFCOUNTER generic-preview-clone-boundary-{count} minsize-reads {fixture.MinimumReads}");
                Console.WriteLine($"PERFCOUNTER generic-preview-clone-boundary-{count} identity-matches {count * 100}");
                Console.WriteLine($"PERFCOUNTER generic-preview-clone-boundary-{count} registrations {count * 100}");
                Console.WriteLine($"PERFCOUNTER generic-preview-clone-boundary-{count} live-tree-changes 0");
            }
        }

        private sealed class Fixture
        {
            private readonly TilingWorkspace m_workspace = new();
            private readonly DesktopTree m_tree;
            private readonly WindowNode[] m_nodes;
            private readonly IWindow[] m_windows;
            private readonly string m_before;

            public CloneProbe Probe { get; } = new();

            public int MinimumReads { get; private set; }

            public Fixture(int count)
            {
                var factory = new UniqueWindowMockFactory();
                m_windows = Enumerable.Range(0, count).Select(index => factory.Create($"preview-{index}")).ToArray();
                foreach (var window in m_windows)
                {
                    Mock.Get(window).SetupGet(value => value.MinSize).Returns(() =>
                    {
                        MinimumReads++;
                        return new Point(0, 0);
                    });
                }
                var root = new CloneProbePanel(Probe) { Orientation = PanelOrientation.Horizontal };
                m_tree = new DesktopTree
                {
                    Root = root,
                    WorkArea = Rectangle.OffsetAndSize(0, 0, 4096, 1024),
                };
                m_nodes = m_windows.Select(window => new WindowNode(window)).ToArray();
                foreach (var node in m_nodes)
                {
                    root.Attach(node);
                }
                m_tree.Measure();
                m_tree.Arrange();
                m_before = Snapshot();
            }

            public void WarmPreviewAndArm()
            {
                Preview();
                MinimumReads = 0;
                Probe.Armed = true;
                Probe.ArmedCloneCalls = 0;
            }

            public (Rectangle preArrange, Rectangle postArrange) Preview()
            {
                return m_workspace.MockMoveNode(m_nodes[^1], new Point(-100, -100), allowNesting: true);
            }

            public void AssertLiveTreeUnchanged()
            {
                Assert.AreEqual(m_before, Snapshot());
                for (int index = 0; index < m_windows.Length; index++)
                {
                    Assert.AreSame(m_nodes[index], m_tree.FindNode(m_windows[index]));
                    Assert.AreSame(m_windows[index], m_nodes[index].WindowReference);
                }
            }

            private string Snapshot()
            {
                return string.Join(";", m_tree.Root!.Nodes.Select(node =>
                    $"{node.GenerationID}:{node.Parent?.GenerationID}:{node.ComputedRectangle}"));
            }
        }

        private sealed class CloneProbe
        {
            public bool Armed;
            public int ArmedCloneCalls;
            public InvalidOperationException Error { get; } = new("controlled unversioned Clone callback");
        }

        private sealed class CloneProbePanel(CloneProbe probe) : SplitPanelNode
        {
            public override object Clone()
            {
                if (probe.Armed)
                {
                    probe.ArmedCloneCalls++;
                    throw probe.Error;
                }
                return base.Clone();
            }
        }
    }
}
