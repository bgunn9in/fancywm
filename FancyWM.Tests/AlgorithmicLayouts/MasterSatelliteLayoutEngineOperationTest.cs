using System;
using System.Linq;

using FancyWM.AlgorithmicLayouts;
using FancyWM.Layouts;
using FancyWM.Layouts.Tiling;
using FancyWM.Models;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    [TestClass]
    public class MasterSatelliteLayoutEngineOperationTest
    {
        private readonly MasterSatelliteLayoutEngine m_engine = new();
        private readonly UniqueWindowMockFactory m_windows = new();

        [DataTestMethod]
        [DataRow(0, "A,C,D")]
        [DataRow(1, "B,A,D")]
        [DataRow(2, "B,C,A")]
        public void PromotionPutsOldMasterInSelectedSatelliteSlot(
            int satelliteIndex,
            string expectedSatelliteTitles)
        {
            var windows = CreateWindows(4);
            var (tree, state, settings) = Build(windows);
            var selected = windows[satelliteIndex + 1];
            var oldMasterNode = tree.FindNode(windows[0]);
            var promotedNode = tree.FindNode(selected);
            var side = state.MasterSide;
            var requestedRatio = state.RequestedMasterRatio;
            var ratio = state.EffectiveMasterRatio;
            var orientation = state.SatelliteOrientation;

            var operation = m_engine.PromoteToMaster(tree, state, settings, selected);

            Assert.IsTrue(operation.Succeeded, operation.Message);
            Assert.IsTrue(operation.Changed);
            Assert.AreSame(selected, state.Master);
            CollectionAssert.AreEqual(
                expectedSatelliteTitles.Split(','),
                state.Satellites.Select(x => x.Title).ToArray());
            Assert.AreSame(oldMasterNode, tree.FindNode(windows[0]),
                "Promotion must swap existing node references, not unregister/register windows.");
            Assert.AreSame(promotedNode, tree.FindNode(selected));
            Assert.AreSame(tree.FindNode(windows[0]), AssertSatellitePanel(tree).Children[satelliteIndex]);
            Assert.AreEqual(side, state.MasterSide);
            Assert.AreEqual(requestedRatio, state.RequestedMasterRatio, 0.0001);
            Assert.AreEqual(ratio, state.EffectiveMasterRatio, 0.0001);
            Assert.AreEqual(orientation, state.SatelliteOrientation);
            Assert.IsTrue(m_engine.ValidateInvariant(tree, state, settings).IsValid);
        }

        [TestMethod]
        public void PromotingCurrentMasterIsSafeNoOp()
        {
            var windows = CreateWindows(3);
            var (tree, state, settings) = Build(windows);
            var revision = state.Revision;
            var before = m_engine.CreateSnapshot(tree, state);

            var operation = m_engine.PromoteToMaster(tree, state, settings, windows[0]);

            Assert.IsTrue(operation.Succeeded, operation.Message);
            Assert.IsFalse(operation.Changed);
            Assert.AreEqual(revision, state.Revision);
            Assert.AreEqual(before.TreeDescription, operation.After.TreeDescription);
            CollectionAssert.AreEqual(windows.Skip(1).ToArray(), state.Satellites.ToArray());
        }

        [TestMethod]
        public void PromotionMinSizeConflictIsRejectedAtomically()
        {
            var master = m_windows.Create("A", minimumWidth: 100, minimumHeight: 500);
            var satellites = new[]
            {
                m_windows.Create("B", minimumWidth: 100, minimumHeight: 10),
                m_windows.Create("C", minimumWidth: 100, minimumHeight: 10),
                m_windows.Create("D", minimumWidth: 100, minimumHeight: 10),
            };
            var all = new[] { master }.Concat(satellites).ToArray();
            var (tree, state, settings) = Build(all);
            var revision = state.Revision;
            var beforeTree = m_engine.CreateSnapshot(tree, state).TreeDescription;

            var operation = m_engine.PromoteToMaster(tree, state, settings, satellites[1]);

            Assert.IsFalse(operation.Succeeded);
            Assert.AreEqual(MasterSatelliteFailureReason.MinSizeConflict, operation.FailureReason);
            Assert.AreEqual(revision, state.Revision);
            Assert.AreSame(master, state.Master);
            CollectionAssert.AreEqual(satellites, state.Satellites.ToArray());
            Assert.AreEqual(beforeTree, m_engine.CreateSnapshot(tree, state).TreeDescription);
        }

        [DataTestMethod]
        [DataRow(0, 2, "C,D,B")]
        [DataRow(2, 0, "D,B,C")]
        [DataRow(1, 0, "C,B,D")]
        public void ReorderSatellitePreservesWindowSetAndMaster(
            int fromIndex,
            int toIndex,
            string expectedTitles)
        {
            var windows = CreateWindows(4);
            var (tree, state, settings) = Build(windows);
            var masterNode = tree.FindNode(windows[0]);

            var operation = m_engine.ReorderSatellite(
                tree,
                state,
                settings,
                fromIndex,
                toIndex);

            Assert.IsTrue(operation.Succeeded, operation.Message);
            Assert.AreSame(windows[0], state.Master);
            Assert.AreSame(masterNode, tree.FindNode(windows[0]));
            CollectionAssert.AreEqual(
                expectedTitles.Split(','),
                state.Satellites.Select(x => x.Title).ToArray());
            CollectionAssert.AreEquivalent(windows, tree.Root!.Windows.Select(x => x.WindowReference).ToArray());
        }

        [TestMethod]
        public void ReorderCarriesIndividualFlexAllocationWithWindow()
        {
            var windows = CreateWindows(4);
            var (tree, state, settings) = Build(windows);
            var panel = AssertSatellitePanel(tree);
            var firstNode = tree.FindNode(windows[1])!;
            Assert.IsTrue(panel.ResizeTo(firstNode, 300, GrowDirection.Both));
            var firstLength = panel.GetChildConstraints(firstNode).Width;

            var operation = m_engine.ReorderSatellite(tree, state, settings, 0, 2);

            Assert.IsTrue(operation.Succeeded, operation.Message);
            Assert.AreSame(firstNode, panel.Children[2]);
            Assert.AreEqual(firstLength, panel.GetChildConstraints(firstNode).Width, 0.01);
        }

        [TestMethod]
        public void MovePreviousAndNextRespectSatelliteBounds()
        {
            var windows = CreateWindows(4);
            var (tree, state, settings) = Build(windows);

            var previousAtStart = m_engine.MoveSatellitePrevious(tree, state, settings, windows[1]);
            Assert.IsFalse(previousAtStart.Succeeded);
            Assert.AreEqual(MasterSatelliteFailureReason.InvalidSatelliteIndex, previousAtStart.FailureReason);

            var moveNext = m_engine.MoveSatelliteNext(tree, state, settings, windows[1]);
            Assert.IsTrue(moveNext.Succeeded, moveNext.Message);
            CollectionAssert.AreEqual(new[] { windows[2], windows[1], windows[3] }, state.Satellites.ToArray());

            var movePrevious = m_engine.MoveSatellitePrevious(tree, state, settings, windows[1]);
            Assert.IsTrue(movePrevious.Succeeded, movePrevious.Message);
            CollectionAssert.AreEqual(new[] { windows[1], windows[2], windows[3] }, state.Satellites.ToArray());

            var nextAtEnd = m_engine.MoveSatelliteNext(tree, state, settings, windows[3]);
            Assert.IsFalse(nextAtEnd.Succeeded);
            Assert.AreEqual(MasterSatelliteFailureReason.InvalidSatelliteIndex, nextAtEnd.FailureReason);
        }

        [TestMethod]
        public void InvalidReorderIndexIsRejectedWithoutMutation()
        {
            var windows = CreateWindows(3);
            var (tree, state, settings) = Build(windows);
            var revision = state.Revision;

            var operation = m_engine.ReorderSatellite(tree, state, settings, 0, 2);

            Assert.IsFalse(operation.Succeeded);
            Assert.AreEqual(MasterSatelliteFailureReason.InvalidSatelliteIndex, operation.FailureReason);
            Assert.AreEqual(revision, state.Revision);
            CollectionAssert.AreEqual(windows.Skip(1).ToArray(), state.Satellites.ToArray());
        }

        [TestMethod]
        public void SetAndSwapMasterSidePreserveRatioOrderAndNodeIdentity()
        {
            var windows = CreateWindows(4);
            var (tree, state, settings) = Build(windows);
            var nodes = windows.Select(tree.FindNode).ToArray();
            var ratio = state.EffectiveMasterRatio;

            var set = m_engine.SetMasterSide(tree, state, settings, MasterSide.Right);
            var repeated = m_engine.SetMasterSide(tree, state, settings, MasterSide.Right);
            var swap = m_engine.SwapMasterSide(tree, state, settings);

            Assert.IsTrue(set.Succeeded, set.Message);
            Assert.IsTrue(set.Changed);
            Assert.IsTrue(repeated.Succeeded);
            Assert.IsFalse(repeated.Changed);
            Assert.IsTrue(swap.Succeeded, swap.Message);
            Assert.AreEqual(MasterSide.Left, state.MasterSide);
            Assert.AreEqual(ratio, state.EffectiveMasterRatio, 0.0001);
            CollectionAssert.AreEqual(windows.Skip(1).ToArray(), state.Satellites.ToArray());
            CollectionAssert.AreEqual(nodes, windows.Select(tree.FindNode).ToArray());
        }

        [TestMethod]
        public void OrientationSwitchPreservesOrderAndResetsFlex()
        {
            var windows = CreateWindows(4);
            var (tree, state, settings) = Build(windows);
            var panel = AssertSatellitePanel(tree);
            Assert.IsTrue(panel.ResizeTo(tree.FindNode(windows[1])!, 300, GrowDirection.Both));

            var operation = m_engine.SetSatelliteOrientation(
                tree,
                state,
                settings,
                SatelliteLayoutOrientation.Horizontal);

            Assert.IsTrue(operation.Succeeded, operation.Message);
            Assert.AreEqual(PanelOrientation.Horizontal, panel.Orientation);
            CollectionAssert.AreEqual(windows.Skip(1).ToArray(), state.Satellites.ToArray());
            var widths = panel.Children.Select(x => panel.GetChildConstraints(x).Width).ToArray();
            Assert.IsTrue(widths.Max() - widths.Min() < 0.01,
                $"Expected reset/even flex, got [{string.Join(", ", widths)}].");
        }

        [TestMethod]
        public void ImpossibleOrientationSwitchIsRejectedAtomically()
        {
            var master = m_windows.Create("A", minimumWidth: 100, minimumHeight: 100);
            var satellites = new[]
            {
                m_windows.Create("B", minimumWidth: 350, minimumHeight: 50),
                m_windows.Create("C", minimumWidth: 350, minimumHeight: 50),
                m_windows.Create("D", minimumWidth: 350, minimumHeight: 50),
            };
            var (tree, state, settings) = Build(new[] { master }.Concat(satellites).ToArray());
            var before = m_engine.CreateSnapshot(tree, state);

            var operation = m_engine.SetSatelliteOrientation(
                tree,
                state,
                settings,
                SatelliteLayoutOrientation.Horizontal);

            Assert.IsFalse(operation.Succeeded);
            Assert.AreEqual(MasterSatelliteFailureReason.MinSizeConflict, operation.FailureReason);
            Assert.AreEqual(SatelliteLayoutOrientation.Vertical, state.SatelliteOrientation);
            Assert.AreEqual(before.TreeDescription, m_engine.CreateSnapshot(tree, state).TreeDescription);
            CollectionAssert.AreEqual(satellites, state.Satellites.ToArray());
        }

        [TestMethod]
        public void RemovingSatellitePreservesRemainingCanonicalPanel()
        {
            var windows = CreateWindows(4);
            var (tree, state, settings) = Build(windows);

            var operation = m_engine.RemoveWindow(tree, state, settings, windows[2]);

            Assert.IsTrue(operation.Succeeded, operation.Message);
            CollectionAssert.AreEqual(new[] { windows[1], windows[3] }, state.Satellites.ToArray());
            CollectionAssert.AreEqual(new[] { windows[1], windows[3] }, WindowsIn(AssertSatellitePanel(tree)));
            Assert.IsTrue(m_engine.ValidateInvariant(tree, state, settings).IsValid);
        }

        [TestMethod]
        public void RemovingLastSatelliteRemovesPanelAndExpandsMaster()
        {
            var windows = CreateWindows(2);
            var (tree, state, settings) = Build(windows);

            var operation = m_engine.RemoveWindow(tree, state, settings, windows[1]);

            Assert.IsTrue(operation.Succeeded, operation.Message);
            Assert.AreEqual(0, state.Satellites.Count);
            Assert.AreEqual(1, AssertRoot(tree).Children.Count);
            Assert.AreEqual(tree.WorkArea, tree.FindNode(windows[0])!.ComputedRectangle);
            Assert.IsFalse(AssertRoot(tree).Children.OfType<PanelNode>().Any());
        }

        [TestMethod]
        public void RemovingMasterPromotesFirstSatelliteAndPreservesOrder()
        {
            var windows = CreateWindows(4);
            var (tree, state, settings) = Build(windows);

            var operation = m_engine.RemoveWindow(tree, state, settings, windows[0]);

            Assert.IsTrue(operation.Succeeded, operation.Message);
            Assert.AreSame(windows[1], state.Master);
            CollectionAssert.AreEqual(new[] { windows[2], windows[3] }, state.Satellites.ToArray());
            Assert.IsNull(tree.FindNode(windows[0]));
            Assert.IsTrue(m_engine.ValidateInvariant(tree, state, settings).IsValid);
        }

        [TestMethod]
        public void RemovingOnlyWindowProducesValidEmptyLayout()
        {
            var window = m_windows.Create("A");
            var (tree, state, settings) = Build(window);

            var operation = m_engine.RemoveWindow(tree, state, settings, window);

            Assert.IsTrue(operation.Succeeded, operation.Message);
            Assert.IsNull(state.Master);
            Assert.AreEqual(0, state.Satellites.Count);
            Assert.AreEqual(0, tree.Root?.Windows.Count() ?? 0);
            Assert.IsTrue(m_engine.ValidateInvariant(tree, state, settings).IsValid);
        }

        private (DesktopTree Tree, MasterSatelliteRuntimeState State, MasterSatelliteLayoutSettings Settings)
            Build(params IWindow[] windows)
        {
            var settings = new MasterSatelliteLayoutSettings
            {
                Enabled = true,
                MasterRatio = 0.60,
                DefaultMasterSide = MasterSide.Left,
                DefaultSatelliteOrientation = SatelliteLayoutOrientation.Vertical,
                MaxSatellites = 3,
            };
            var tree = new DesktopTree
            {
                WorkArea = Rectangle.OffsetAndSize(0, 0, 1000, 600),
            };
            var state = m_engine.CreateState(settings, true);
            var operation = m_engine.BuildLayout(tree, state, settings, windows);
            Assert.IsTrue(operation.Succeeded, operation.Message);
            return (tree, state, settings);
        }

        private IWindow[] CreateWindows(int count)
        {
            return Enumerable.Range(0, count)
                .Select(x => m_windows.Create(((char)('A' + x)).ToString()))
                .ToArray();
        }

        private static SplitPanelNode AssertRoot(DesktopTree tree)
        {
            Assert.IsInstanceOfType(tree.Root, typeof(SplitPanelNode));
            return (SplitPanelNode)tree.Root!;
        }

        private static SplitPanelNode AssertSatellitePanel(DesktopTree tree)
        {
            var panel = AssertRoot(tree).Children.OfType<SplitPanelNode>().SingleOrDefault();
            Assert.IsNotNull(panel);
            return panel;
        }

        private static IWindow[] WindowsIn(PanelNode panel)
        {
            return panel.Children.Cast<WindowNode>().Select(x => x.WindowReference).ToArray();
        }
    }
}
