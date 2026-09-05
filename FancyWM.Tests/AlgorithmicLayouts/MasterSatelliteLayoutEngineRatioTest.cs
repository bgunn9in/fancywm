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
    public class MasterSatelliteLayoutEngineRatioTest
    {
        private readonly MasterSatelliteLayoutEngine m_engine = new();
        private readonly UniqueWindowMockFactory m_windows = new();

        [TestMethod]
        public void RatioUsesRootWidthAfterOuterSpacing()
        {
            var settings = CreateSettings();
            var tree = CreateTree(1000, 600);
            tree.Root = new SplitPanelNode
            {
                Orientation = PanelOrientation.Horizontal,
                Spacing = 10,
            };
            var state = m_engine.CreateState(settings, true);
            var windows = CreateWindows();

            var operation = m_engine.BuildLayout(tree, state, settings, windows);

            Assert.IsTrue(operation.Succeeded, operation.Message);
            var root = AssertRoot(tree);
            var masterNode = tree.FindNode(windows[0])!;
            Assert.AreEqual(990, root.ContainerLength, 0.01);
            Assert.AreEqual(594, root.GetChildConstraints(masterNode).Width, 0.01);
            Assert.AreEqual(0.60, state.EffectiveMasterRatio, 0.0001);
        }

        [TestMethod]
        public void RequestedAndEffectiveRatiosRemainSeparateWhenMasterMinimumClamps()
        {
            var master = m_windows.Create("A", minimumWidth: 700, minimumHeight: 10);
            var satellite = m_windows.Create("B", minimumWidth: 10, minimumHeight: 10);
            var (tree, state, settings, operation) = Build(master, satellite);

            Assert.IsTrue(operation.Succeeded, operation.Message);
            Assert.AreEqual(0.60, state.RequestedMasterRatio, 0.0001);
            Assert.AreEqual(0.70, state.EffectiveMasterRatio, 0.0001);
            Assert.AreEqual(700, tree.FindNode(master)!.ComputedRectangle.Width);
            Assert.IsTrue(m_engine.ValidateInvariant(tree, state, settings).IsValid);
        }

        [TestMethod]
        public void SatelliteMinimumClampsEffectiveMasterRatioDown()
        {
            var master = m_windows.Create("A", minimumWidth: 10, minimumHeight: 10);
            var satellite = m_windows.Create("B", minimumWidth: 500, minimumHeight: 10);
            var (_, state, _, operation) = Build(master, satellite);

            Assert.IsTrue(operation.Succeeded, operation.Message);
            Assert.AreEqual(0.60, state.RequestedMasterRatio, 0.0001);
            Assert.AreEqual(0.50, state.EffectiveMasterRatio, 0.0001);
        }

        [TestMethod]
        public void FullyImpossibleLayoutFailsWithoutPartialMutation()
        {
            var settings = CreateSettings();
            var tree = CreateTree(1000, 600);
            var state = m_engine.CreateState(settings, true);
            var master = m_windows.Create("A", minimumWidth: 600, minimumHeight: 10);
            var satellite = m_windows.Create("B", minimumWidth: 500, minimumHeight: 10);

            var operation = m_engine.BuildLayout(tree, state, settings, new[] { master, satellite });

            Assert.IsFalse(operation.Succeeded);
            Assert.AreEqual(MasterSatelliteFailureReason.MinSizeConflict, operation.FailureReason);
            Assert.IsNull(tree.Root);
            Assert.IsNull(state.Master);
            Assert.AreEqual(0, state.Satellites.Count);
            Assert.AreEqual(0, state.Revision);
        }

        [TestMethod]
        public void ImpossibleRebuildLeavesExistingCanonicalLayoutUntouched()
        {
            var windows = CreateWindows();
            var (tree, state, settings, initial) = Build(windows);
            Assert.IsTrue(initial.Succeeded, initial.Message);
            var before = m_engine.CreateSnapshot(tree, state);
            var root = tree.Root;
            var impossibleMaster = m_windows.Create("X", minimumWidth: 600, minimumHeight: 10);
            var impossibleSatellite = m_windows.Create("Y", minimumWidth: 500, minimumHeight: 10);

            var operation = m_engine.BuildLayout(
                tree,
                state,
                settings,
                new[] { impossibleMaster, impossibleSatellite });

            Assert.IsFalse(operation.Succeeded);
            Assert.AreEqual(MasterSatelliteFailureReason.MinSizeConflict, operation.FailureReason);
            Assert.AreSame(root, tree.Root);
            Assert.AreEqual(before.TreeDescription, m_engine.CreateSnapshot(tree, state).TreeDescription);
            Assert.AreSame(windows[0], state.Master);
            CollectionAssert.AreEqual(new[] { windows[1] }, state.Satellites.ToArray());
        }

        [TestMethod]
        public void RatioIsAppliedOnlyWhenSatelliteExists()
        {
            var master = m_windows.Create("A", minimumWidth: 100, minimumHeight: 10);
            var (tree, state, _, operation) = Build(master);

            Assert.IsTrue(operation.Succeeded, operation.Message);
            Assert.AreEqual(0.60, state.RequestedMasterRatio, 0.0001);
            Assert.AreEqual(1.0, state.EffectiveMasterRatio, 0.0001);
            Assert.AreEqual(tree.WorkArea, tree.FindNode(master)!.ComputedRectangle);
        }

        [TestMethod]
        public void SetAndResetRequestedRatioRelayoutCanonicalTree()
        {
            var windows = CreateWindows();
            var (tree, state, settings, operation) = Build(windows);
            Assert.IsTrue(operation.Succeeded, operation.Message);

            var set = m_engine.SetRequestedMasterRatio(tree, state, settings, 0.75);
            Assert.IsTrue(set.Succeeded, set.Message);
            Assert.AreEqual(0.75, state.RequestedMasterRatio, 0.0001);
            Assert.AreEqual(0.75, state.EffectiveMasterRatio, 0.0001);
            Assert.AreEqual(750, AssertRoot(tree).GetChildConstraints(tree.FindNode(windows[0])!).Width, 0.01);

            var reset = m_engine.ResetMasterRatio(tree, state, settings);
            Assert.IsTrue(reset.Succeeded, reset.Message);
            Assert.AreEqual(MasterSatelliteLayoutSettings.DefaultMasterRatio, state.RequestedMasterRatio, 0.0001);
            Assert.AreEqual(0.60, state.EffectiveMasterRatio, 0.0001);
        }

        [TestMethod]
        public void RequestedRatioOutsideSettingRangeIsNormalized()
        {
            var windows = CreateWindows();
            var (tree, state, settings, operation) = Build(windows);
            Assert.IsTrue(operation.Succeeded, operation.Message);

            var high = m_engine.SetRequestedMasterRatio(tree, state, settings, 0.95);
            Assert.IsTrue(high.Succeeded, high.Message);
            Assert.AreEqual(MasterSatelliteLayoutSettings.MaximumMasterRatio, state.RequestedMasterRatio, 0.0001);

            var low = m_engine.SetRequestedMasterRatio(tree, state, settings, 0.20);
            Assert.IsTrue(low.Succeeded, low.Message);
            Assert.AreEqual(MasterSatelliteLayoutSettings.MinimumMasterRatio, state.RequestedMasterRatio, 0.0001);
        }

        [TestMethod]
        public void CaptureCurrentRatioPreservesManualResize()
        {
            var windows = CreateWindows();
            var (tree, state, settings, operation) = Build(windows);
            Assert.IsTrue(operation.Succeeded, operation.Message);
            var root = AssertRoot(tree);
            var masterNode = tree.FindNode(windows[0])!;
            Assert.IsTrue(root.ResizeTo(masterNode, 700, GrowDirection.Both));
            tree.Arrange();

            var capture = m_engine.CaptureCurrentMasterRatio(tree, state, settings);

            Assert.IsTrue(capture.Succeeded, capture.Message);
            Assert.AreEqual(0.70, state.RequestedMasterRatio, 0.0001);
            Assert.AreEqual(0.70, state.EffectiveMasterRatio, 0.0001);
            Assert.AreEqual(700, root.GetChildConstraints(masterNode).Width, 0.01);
        }

        [TestMethod]
        public void WholePixelRoundingDoesNotAccumulateAcrossRelayouts()
        {
            var settings = CreateSettings();
            var tree = CreateTree(1001, 601);
            var state = m_engine.CreateState(settings, true);
            var windows = CreateWindows();
            var build = m_engine.BuildLayout(tree, state, settings, windows);
            Assert.IsTrue(build.Succeeded, build.Message);
            var root = AssertRoot(tree);
            var masterNode = tree.FindNode(windows[0])!;
            var firstWidth = root.GetChildConstraints(masterNode).Width;

            for (int i = 0; i < 20; i++)
            {
                var relayout = m_engine.Relayout(tree, state, settings);
                Assert.IsTrue(relayout.Succeeded, relayout.Message);
            }

            Assert.AreEqual(601, firstWidth, 0.01);
            Assert.AreEqual(firstWidth, root.GetChildConstraints(masterNode).Width, 0.01);
            Assert.AreEqual(firstWidth / root.ContainerLength, state.EffectiveMasterRatio, 0.0000001);
        }

        [TestMethod]
        public void WorkAreaOffsetDoesNotAffectRatioCalculation()
        {
            var settings = CreateSettings();
            var tree = new DesktopTree
            {
                WorkArea = Rectangle.OffsetAndSize(250, 75, 1250, 700),
            };
            var state = m_engine.CreateState(settings, true);
            var windows = CreateWindows();

            var operation = m_engine.BuildLayout(tree, state, settings, windows);

            Assert.IsTrue(operation.Succeeded, operation.Message);
            Assert.AreEqual(750, AssertRoot(tree).GetChildConstraints(tree.FindNode(windows[0])!).Width, 0.01);
            Assert.AreEqual(250, tree.FindNode(windows[0])!.ComputedRectangle.Left);
        }

        [TestMethod]
        public void RelayoutUsesChangedWorkAreaWithoutChangingRequestedRatio()
        {
            var windows = CreateWindows();
            var (tree, state, settings, build) = Build(windows);
            Assert.IsTrue(build.Succeeded, build.Message);
            tree.WorkArea = Rectangle.OffsetAndSize(0, 0, 1200, 700);

            var operation = m_engine.Relayout(tree, state, settings);

            Assert.IsTrue(operation.Succeeded, operation.Message);
            Assert.AreEqual(0.60, state.RequestedMasterRatio, 0.0001);
            Assert.AreEqual(0.60, state.EffectiveMasterRatio, 0.0001);
            Assert.AreEqual(720, AssertRoot(tree).GetChildConstraints(tree.FindNode(windows[0])!).Width, 0.01);
        }

        private (DesktopTree Tree, MasterSatelliteRuntimeState State, MasterSatelliteLayoutSettings Settings,
            MasterSatelliteOperationResult Operation) Build(params IWindow[] windows)
        {
            var settings = CreateSettings();
            var tree = CreateTree(1000, 600);
            var state = m_engine.CreateState(settings, true);
            var operation = m_engine.BuildLayout(tree, state, settings, windows);
            return (tree, state, settings, operation);
        }

        private static MasterSatelliteLayoutSettings CreateSettings()
        {
            return new MasterSatelliteLayoutSettings
            {
                Enabled = true,
                MasterRatio = 0.60,
                DefaultMasterSide = MasterSide.Left,
                DefaultSatelliteOrientation = SatelliteLayoutOrientation.Vertical,
                MaxSatellites = 3,
            };
        }

        private static DesktopTree CreateTree(int width, int height)
        {
            return new DesktopTree
            {
                WorkArea = Rectangle.OffsetAndSize(0, 0, width, height),
            };
        }

        private IWindow[] CreateWindows()
        {
            return new[] { m_windows.Create("A"), m_windows.Create("B") };
        }

        private static SplitPanelNode AssertRoot(DesktopTree tree)
        {
            Assert.IsInstanceOfType(tree.Root, typeof(SplitPanelNode));
            return (SplitPanelNode)tree.Root!;
        }
    }
}
