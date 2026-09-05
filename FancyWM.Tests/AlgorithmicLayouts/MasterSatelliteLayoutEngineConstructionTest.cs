using System;
using System.Collections.Generic;
using System.Linq;

using FancyWM.AlgorithmicLayouts;
using FancyWM.Layouts.Tiling;
using FancyWM.Models;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    [TestClass]
    public class MasterSatelliteLayoutEngineConstructionTest
    {
        private readonly MasterSatelliteLayoutEngine m_engine = new();
        private readonly UniqueWindowMockFactory m_windows = new();

        [TestMethod]
        public void EmptyLayoutIsValid()
        {
            var settings = CreateSettings();
            var tree = CreateTree();
            var state = m_engine.CreateState(settings, true);

            var operation = m_engine.BuildLayout(tree, state, settings, Array.Empty<IWindow>());

            Assert.IsTrue(operation.Succeeded, operation.Message);
            Assert.IsNull(state.Master);
            Assert.AreEqual(0, state.Satellites.Count);
            Assert.AreEqual(0, tree.Root?.Windows.Count() ?? 0);
            Assert.IsTrue(m_engine.ValidateInvariant(tree, state, settings).IsValid);
        }

        [TestMethod]
        public void OneMasterOccupiesEntireWorkArea()
        {
            var master = m_windows.Create("A");
            var (tree, state, settings) = Build(master);

            var root = AssertRoot(tree);
            Assert.AreEqual(PanelOrientation.Horizontal, root.Orientation);
            Assert.AreEqual(1, root.Children.Count);
            Assert.AreSame(master, state.Master);
            Assert.AreEqual(tree.WorkArea, tree.FindNode(master)!.ComputedRectangle);
            Assert.IsFalse(root.Children.OfType<SplitPanelNode>().Any());
            Assert.IsTrue(m_engine.ValidateInvariant(tree, state, settings).IsValid);
        }

        [TestMethod]
        public void SecondWindowCreatesRequestedSixtyFortySplit()
        {
            var master = m_windows.Create("A");
            var satellite = m_windows.Create("B");
            var (tree, state, _) = Build(master, satellite);

            Assert.AreEqual(600, tree.FindNode(master)!.ComputedRectangle.Width);
            Assert.AreEqual(400, tree.FindNode(satellite)!.ComputedRectangle.Width);
            Assert.AreEqual(0.60, state.RequestedMasterRatio, 0.0001);
            Assert.AreEqual(0.60, state.EffectiveMasterRatio, 0.0001);
        }

        [DataTestMethod]
        [DataRow(MasterSide.Left)]
        [DataRow(MasterSide.Right)]
        public void MasterIsBuiltOnConfiguredSide(MasterSide side)
        {
            var settings = CreateSettings(side: side);
            var master = m_windows.Create("A");
            var satellite = m_windows.Create("B");
            var (tree, state) = Build(settings, master, satellite);
            var root = AssertRoot(tree);

            var expectedMasterIndex = side == MasterSide.Left ? 0 : 1;
            Assert.AreSame(tree.FindNode(master), root.Children[expectedMasterIndex]);
            Assert.AreEqual(side, state.MasterSide);
        }

        [DataTestMethod]
        [DataRow(SatelliteLayoutOrientation.Vertical, PanelOrientation.Vertical)]
        [DataRow(SatelliteLayoutOrientation.Horizontal, PanelOrientation.Horizontal)]
        public void SatellitePanelUsesConfiguredOrientation(
            SatelliteLayoutOrientation orientation,
            PanelOrientation expectedPanelOrientation)
        {
            var settings = CreateSettings(orientation: orientation);
            var windows = CreateWindows(4);
            var (tree, state) = Build(settings, windows);
            var panel = AssertSatellitePanel(tree);

            Assert.AreEqual(expectedPanelOrientation, panel.Orientation);
            CollectionAssert.AreEqual(windows.Skip(1).ToArray(), state.Satellites.ToArray());
            CollectionAssert.AreEqual(windows.Skip(1).ToArray(), WindowsIn(panel));
        }

        [DataTestMethod]
        [DataRow(SatelliteLayoutOrientation.Vertical, PanelOrientation.Vertical)]
        [DataRow(SatelliteLayoutOrientation.Horizontal, PanelOrientation.Horizontal)]
        public void OneSatelliteStillUsesCanonicalPanel(
            SatelliteLayoutOrientation orientation,
            PanelOrientation expectedPanelOrientation)
        {
            var settings = CreateSettings(orientation: orientation);
            var windows = CreateWindows(2);
            var (tree, state) = Build(settings, windows);
            var panel = AssertSatellitePanel(tree);

            Assert.AreEqual(expectedPanelOrientation, panel.Orientation);
            Assert.AreEqual(1, panel.Children.Count);
            Assert.AreSame(windows[1], state.Satellites.Single());
            Assert.AreSame(tree.FindNode(windows[1]), panel.Children[0]);
        }

        [DataTestMethod]
        [DataRow(SatelliteLayoutOrientation.Vertical)]
        [DataRow(SatelliteLayoutOrientation.Horizontal)]
        public void ThreeSatellitesAreInitiallyDistributedEvenly(
            SatelliteLayoutOrientation orientation)
        {
            var settings = CreateSettings(orientation: orientation);
            var windows = CreateWindows(4);
            var (tree, _) = Build(settings, windows);
            var panel = AssertSatellitePanel(tree);

            var lengths = panel.Children
                .Select(x => orientation == SatelliteLayoutOrientation.Vertical
                    ? x.ComputedRectangle.Height
                    : x.ComputedRectangle.Width)
                .ToArray();

            Assert.IsTrue(lengths.Max() - lengths.Min() <= 1,
                $"Expected an even distribution, got [{string.Join(", ", lengths)}].");
        }

        [TestMethod]
        public void SatelliteVisualOrderMatchesInputOrder()
        {
            var windows = CreateWindows(4);
            var (tree, state, _) = Build(windows);

            CollectionAssert.AreEqual(windows.Skip(1).ToArray(), state.Satellites.ToArray());
            CollectionAssert.AreEqual(windows.Skip(1).ToArray(), WindowsIn(AssertSatellitePanel(tree)));
        }

        [TestMethod]
        public void DuplicateInputIsRejectedWithoutPartialTreeMutation()
        {
            var settings = CreateSettings();
            var tree = CreateTree();
            var state = m_engine.CreateState(settings, true);
            var window = m_windows.Create("A");

            var operation = m_engine.BuildLayout(tree, state, settings, new[] { window, window });

            Assert.IsFalse(operation.Succeeded);
            Assert.AreEqual(MasterSatelliteFailureReason.WindowAlreadyPresent, operation.FailureReason);
            Assert.AreEqual(0, tree.Root?.Windows.Count() ?? 0);
            Assert.IsNull(state.Master);
            Assert.AreEqual(0, state.Satellites.Count);
        }

        [TestMethod]
        public void IncrementalPlacementAssignsMasterThenOrderedSatellites()
        {
            var settings = CreateSettings();
            var tree = CreateTree();
            var state = m_engine.CreateState(settings, true);
            var windows = CreateWindows(4);
            var initialize = m_engine.BuildLayout(tree, state, settings, Array.Empty<IWindow>());
            Assert.IsTrue(initialize.Succeeded, initialize.Message);

            var first = m_engine.PlaceWindow(tree, state, settings, windows[0], null);
            var second = m_engine.PlaceWindow(tree, state, settings, windows[1], null);
            var inserted = m_engine.PlaceWindow(tree, state, settings, windows[2], 0);
            var appended = m_engine.PlaceWindow(tree, state, settings, windows[3], null);

            Assert.IsTrue(first.Succeeded, first.Message);
            Assert.AreEqual(MasterSatelliteWindowRole.Master, first.Role);
            Assert.IsTrue(second.Succeeded, second.Message);
            Assert.AreEqual(MasterSatelliteWindowRole.Satellite, second.Role);
            Assert.AreEqual(0, second.SatelliteIndex);
            Assert.IsTrue(inserted.Succeeded, inserted.Message);
            Assert.AreEqual(0, inserted.SatelliteIndex);
            Assert.IsTrue(appended.Succeeded, appended.Message);
            CollectionAssert.AreEqual(
                new[] { windows[2], windows[1], windows[3] },
                state.Satellites.ToArray());
            Assert.IsTrue(m_engine.ValidateInvariant(tree, state, settings).IsValid);
        }

        [TestMethod]
        public void CapacityAndDuplicatePlacementAreRejectedAtomically()
        {
            var settings = CreateSettings(maxSatellites: 1);
            var windows = CreateWindows(3);
            var (tree, state) = Build(settings, windows.Take(2).ToArray());
            var revision = state.Revision;
            var before = WindowsInTree(tree);

            var duplicate = m_engine.PlaceWindow(tree, state, settings, windows[0], null);
            var overflow = m_engine.PlaceWindow(tree, state, settings, windows[2], null);

            Assert.IsFalse(duplicate.Succeeded);
            Assert.AreEqual(MasterSatelliteFailureReason.WindowAlreadyPresent, duplicate.FailureReason);
            Assert.IsFalse(overflow.Succeeded);
            Assert.AreEqual(MasterSatelliteFailureReason.CapacityReached, overflow.FailureReason);
            Assert.AreEqual(revision, state.Revision);
            CollectionAssert.AreEqual(before, WindowsInTree(tree));
        }

        [TestMethod]
        public void CreateStateUsesEnabledSettingUnlessExplicitlyOverridden()
        {
            var disabledSettings = new MasterSatelliteLayoutSettings { Enabled = false };

            var fromSetting = m_engine.CreateState(disabledSettings, null);
            var overridden = m_engine.CreateState(disabledSettings, true);

            Assert.IsFalse(fromSetting.IsActive);
            Assert.IsTrue(overridden.IsActive);
            Assert.AreEqual(disabledSettings.MasterRatio, overridden.RequestedMasterRatio, 0.0001);
            Assert.AreEqual(disabledSettings.DefaultMasterSide, overridden.MasterSide);
            Assert.AreEqual(disabledSettings.DefaultSatelliteOrientation, overridden.SatelliteOrientation);
        }

        private (DesktopTree Tree, MasterSatelliteRuntimeState State, MasterSatelliteLayoutSettings Settings)
            Build(params IWindow[] windows)
        {
            var settings = CreateSettings();
            var (tree, state) = Build(settings, windows);
            return (tree, state, settings);
        }

        private (DesktopTree Tree, MasterSatelliteRuntimeState State) Build(
            MasterSatelliteLayoutSettings settings,
            params IWindow[] windows)
        {
            var tree = CreateTree();
            var state = m_engine.CreateState(settings, true);
            var operation = m_engine.BuildLayout(tree, state, settings, windows);
            Assert.IsTrue(operation.Succeeded, operation.Message);
            return (tree, state);
        }

        private static DesktopTree CreateTree(int width = 1000, int height = 600)
        {
            return new DesktopTree
            {
                WorkArea = Rectangle.OffsetAndSize(0, 0, width, height),
            };
        }

        private static MasterSatelliteLayoutSettings CreateSettings(
            MasterSide side = MasterSide.Left,
            SatelliteLayoutOrientation orientation = SatelliteLayoutOrientation.Vertical,
            int maxSatellites = 3)
        {
            return new MasterSatelliteLayoutSettings
            {
                Enabled = true,
                MasterRatio = 0.60,
                DefaultMasterSide = side,
                DefaultSatelliteOrientation = orientation,
                MaxSatellites = maxSatellites,
            };
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

        private static IWindow[] WindowsInTree(DesktopTree tree)
        {
            return tree.Root?.Windows.Select(x => x.WindowReference).ToArray() ?? Array.Empty<IWindow>();
        }
    }
}
