using System;
using System.Linq;
using FancyWM.AlgorithmicLayouts;
using FancyWM.Layouts.Tiling;
using FancyWM.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    [TestClass]
    public class MasterSatelliteMixedLayoutTest
    {
        private readonly MasterSatelliteLayoutEngine m_engine = new();
        private readonly UniqueWindowMockFactory m_windows = new();

        [DataTestMethod]
        [DataRow(MasterSide.Left, 0)]
        [DataRow(MasterSide.Right, 0)]
        [DataRow(MasterSide.Left, 12)]
        [DataRow(MasterSide.Right, 12)]
        public void MixedGeometryUsesCanonicalSplitsAndRelativeProportions(MasterSide side, int spacing)
        {
            var (tree, state, settings, windows) = Build(side, spacing);
            var panel = tree.Root!.Children.OfType<SplitPanelNode>().Single();
            var upper = (SplitPanelNode)panel.Children[0];
            Assert.AreEqual(PanelOrientation.Vertical, panel.Orientation);
            Assert.AreEqual(PanelOrientation.Horizontal, upper.Orientation);
            Assert.AreEqual(panel.ContainerLength * 2 / 3, panel.GetChildConstraints(upper).Width, 1);
            var v1 = tree.FindNode(windows[1])!.ComputedRectangle;
            var v2 = tree.FindNode(windows[2])!.ComputedRectangle;
            var h = tree.FindNode(windows[3])!.ComputedRectangle;
            Assert.AreEqual(v1.Top, v2.Top);
            Assert.AreEqual(v1.Bottom, v2.Bottom);
            Assert.AreEqual(v1.Width, v2.Width, 1);
            Assert.IsTrue(v1.Right <= v2.Left - spacing);
            Assert.IsTrue(v1.Bottom <= h.Top - spacing);
            Assert.IsTrue(h.Width > v1.Width);
            var rectangles = windows.Select(w => tree.FindNode(w)!.ComputedRectangle).ToArray();
            foreach (var a in rectangles)
            foreach (var b in rectangles.Where(b => b != a))
                Assert.IsFalse(a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom);
            Assert.AreEqual(0.6, state.RequestedMasterRatio, 0.0001);
            Assert.AreEqual(0.6, state.EffectiveMasterRatio, 0.002);
            Assert.IsTrue(m_engine.ValidateInvariant(tree, state, settings).IsValid);
        }

        [DataTestMethod]
        [DataRow(SatelliteLayoutOrientation.Horizontal, MasterSide.Left)]
        [DataRow(SatelliteLayoutOrientation.Horizontal, MasterSide.Right)]
        [DataRow(SatelliteLayoutOrientation.Vertical, MasterSide.Left)]
        [DataRow(SatelliteLayoutOrientation.Vertical, MasterSide.Right)]
        public void CountTransitionsAndSettingPreserveOrderAndExistingNodes(
            SatelliteLayoutOrientation orientation, MasterSide side)
        {
            var (tree, state, settings, windows) = Build(side, orientation: orientation);
            var nodes = windows.Select(tree.FindNode).ToArray();
            AssertSuccess(m_engine.SetMixedSatellites(tree, state, settings, false));
            Assert.IsFalse(state.IsMixedLayout);
            Assert.IsTrue(tree.Root!.Children.OfType<SplitPanelNode>().Single().Children.All(n => n is WindowNode));
            AssertSuccess(m_engine.SetMixedSatellites(tree, state, settings, true));
            Assert.IsTrue(state.IsMixedLayout);
            CollectionAssert.AreEqual(nodes, windows.Select(tree.FindNode).ToArray());
            var extra = m_windows.Create("extra");
            AssertSuccess(m_engine.PlaceWindow(tree, state, settings, extra).Operation);
            Assert.IsFalse(state.IsMixedLayout);
            CollectionAssert.AreEqual(nodes, windows.Select(tree.FindNode).ToArray());
            AssertSuccess(m_engine.RemoveWindow(tree, state, settings, extra));
            Assert.IsTrue(state.IsMixedLayout);
            AssertSuccess(m_engine.RemoveWindow(tree, state, settings, windows[1]));
            Assert.IsFalse(state.IsMixedLayout);
            CollectionAssert.AreEqual(new[] { windows[2], windows[3] }, state.Satellites.ToArray());
            AssertSuccess(m_engine.PlaceWindow(tree, state, settings, extra).Operation);
            Assert.IsTrue(state.IsMixedLayout);
            CollectionAssert.AreEqual(new[] { windows[2], windows[3], extra }, state.Satellites.ToArray());
            Assert.AreSame(nodes[2], tree.FindNode(windows[2]));
            Assert.AreSame(nodes[3], tree.FindNode(windows[3]));
            AssertSuccess(m_engine.RemoveWindow(tree, state, settings, state.Master!));
            Assert.AreSame(windows[2], state.Master);
            CollectionAssert.AreEqual(new[] { windows[3], extra }, state.Satellites.ToArray());
            Assert.IsFalse(state.IsMixedLayout);
        }

        [TestMethod]
        public void ImpossibleMixedSettingAndCountTransitionAreAtomic()
        {
            var settings = new MasterSatelliteLayoutSettings { Enabled = true };
            var tree = new DesktopTree { WorkArea = Rectangle.OffsetAndSize(0, 0, 1000, 600) };
            var state = m_engine.CreateState(settings);
            var windows = Enumerable.Range(0, 4).Select(i => m_windows.Create($"w{i}", minimumWidth: 350)).ToArray();
            AssertSuccess(m_engine.BuildLayout(tree, state, settings, windows));
            var root = tree.Root;
            var nodes = windows.Select(tree.FindNode).ToArray();
            var revision = state.Revision;
            var result = m_engine.SetMixedSatellites(tree, state, settings, true);
            Assert.AreEqual(MasterSatelliteFailureReason.MinSizeConflict, result.FailureReason);
            Assert.IsFalse(state.UseMixedSatellites);
            Assert.AreEqual(revision, state.Revision);
            Assert.AreSame(root, tree.Root);
            CollectionAssert.AreEqual(nodes, windows.Select(tree.FindNode).ToArray());
            AssertSuccess(m_engine.RemoveWindow(tree, state, settings, windows[3]));
            AssertSuccess(m_engine.SetMixedSatellites(tree, state, settings, true));
            root = tree.Root;
            revision = state.Revision;
            result = m_engine.PlaceWindow(tree, state, settings, windows[3]).Operation;
            Assert.AreEqual(MasterSatelliteFailureReason.MinSizeConflict, result.FailureReason);
            Assert.AreEqual(revision, state.Revision);
            Assert.AreSame(root, tree.Root);
            Assert.IsNull(tree.FindNode(windows[3]));
            Assert.AreEqual(2, state.Satellites.Count);
        }

        [TestMethod]
        public void MixedInvariantRejectsExtraNestingAndWrongSlotOrderAndRecoveryRestoresShape()
        {
            var (tree, state, settings, windows) = Build();
            tree.FindNode(windows[1])!.Embed(new SplitPanelNode());
            Assert.IsFalse(m_engine.ValidateInvariant(tree, state, settings).IsValid);
            AssertSuccess(m_engine.Normalize(tree, state, settings));
            DesktopTree.SwapReferences(tree.FindNode(windows[1])!, tree.FindNode(windows[3])!);
            Assert.IsFalse(m_engine.ValidateInvariant(tree, state, settings).IsValid);
            AssertSuccess(m_engine.Normalize(tree, state, settings));
            CollectionAssert.AreEqual(windows.Skip(1).ToArray(), state.Satellites.ToArray());
            Assert.IsTrue(state.IsMixedLayout);
        }

        private (DesktopTree, MasterSatelliteRuntimeState, MasterSatelliteLayoutSettings, IWindow[]) Build(
            MasterSide side = MasterSide.Left, int spacing = 0,
            SatelliteLayoutOrientation orientation = SatelliteLayoutOrientation.Vertical)
        {
            var settings = new MasterSatelliteLayoutSettings { Enabled = true, UseMixedSatellites = true,
                DefaultMasterSide = side, DefaultSatelliteOrientation = orientation, MaxSatellites = 4 };
            var tree = new DesktopTree { WorkArea = Rectangle.OffsetAndSize(40, 30, 1000, 600),
                Root = new SplitPanelNode { Spacing = spacing, Padding = new Rectangle(0, spacing / 2, 0, 0) } };
            var state = m_engine.CreateState(settings);
            var windows = Enumerable.Range(0, 4).Select(i => m_windows.Create($"w{i}")).ToArray();
            AssertSuccess(m_engine.BuildLayout(tree, state, settings, windows));
            return (tree, state, settings, windows);
        }

        private static void AssertSuccess(MasterSatelliteOperationResult operation)
        {
            Assert.IsTrue(operation.Succeeded, operation.Message);
            Assert.IsTrue(operation.Invariant.IsValid, operation.Invariant.Description);
        }
    }
}
