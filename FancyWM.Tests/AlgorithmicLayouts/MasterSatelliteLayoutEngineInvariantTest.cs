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
    public class MasterSatelliteLayoutEngineInvariantTest
    {
        private readonly MasterSatelliteLayoutEngine m_engine = new();
        private readonly UniqueWindowMockFactory m_windows = new();

        [TestMethod]
        public void ValidateInvariantAcceptsCanonicalTree()
        {
            var (tree, state, settings, _) = Build(4);

            var invariant = m_engine.ValidateInvariant(tree, state, settings);

            Assert.IsTrue(invariant.IsValid, invariant.Description);
            Assert.AreEqual(0, invariant.Violations.Count);
            Assert.IsFalse(string.IsNullOrWhiteSpace(invariant.TreeDescription));
        }

        [TestMethod]
        public void ValidateInvariantDetectsWrongRootOrientation()
        {
            var (tree, state, settings, _) = Build(2);
            AssertRoot(tree).Orientation = PanelOrientation.Vertical;

            var invariant = m_engine.ValidateInvariant(tree, state, settings);

            Assert.IsFalse(invariant.IsValid);
            StringAssert.Contains(invariant.Description, "root split must be horizontal");
        }

        [TestMethod]
        public void ValidateInvariantDetectsNestedPanel()
        {
            var (tree, state, settings, _) = Build(3);
            var satellitePanel = AssertSatellitePanel(tree);
            var first = satellitePanel.Children[0];
            satellitePanel.Detach(first);
            var nested = new SplitPanelNode { Orientation = PanelOrientation.Vertical };
            satellitePanel.Attach(0, nested);
            nested.Attach(first);

            var invariant = m_engine.ValidateInvariant(tree, state, settings);

            Assert.IsFalse(invariant.IsValid);
            StringAssert.Contains(invariant.Description, "Nested panels");
        }

        [TestMethod]
        public void ValidateInvariantDetectsStack()
        {
            var (tree, state, settings, _) = Build(3);
            var satellitePanel = AssertSatellitePanel(tree);
            var first = satellitePanel.Children[0];
            satellitePanel.Detach(first);
            var stack = new StackPanelNode();
            satellitePanel.Attach(0, stack);
            stack.Attach(first);

            var invariant = m_engine.ValidateInvariant(tree, state, settings);

            Assert.IsFalse(invariant.IsValid);
            StringAssert.Contains(invariant.Description, "StackPanelNode");
        }

        [TestMethod]
        public void ValidateInvariantDetectsPlaceholder()
        {
            var (tree, state, settings, _) = Build(2);
            AssertSatellitePanel(tree).Attach(new PlaceholderNode());

            var invariant = m_engine.ValidateInvariant(tree, state, settings);

            Assert.IsFalse(invariant.IsValid);
            StringAssert.Contains(invariant.Description, "PlaceholderNode");
        }

        [TestMethod]
        public void ValidateInvariantDetectsDuplicateWindowNode()
        {
            var (tree, state, settings, windows) = Build(2);
            var satellitePanel = AssertSatellitePanel(tree);
            var mutableChildren = (IList<TilingNode>)satellitePanel.Children;
            mutableChildren.Add(new WindowNode(windows[1]));

            var invariant = m_engine.ValidateInvariant(tree, state, settings);

            Assert.IsFalse(invariant.IsValid);
            StringAssert.Contains(invariant.Description, "duplicate WindowNode");
        }

        [TestMethod]
        public void ValidateInvariantDetectsDuplicateRuntimeSatellite()
        {
            var (tree, state, settings, _) = Build(3);
            state.MutableSatellites.Add(state.Satellites[0]);

            var invariant = m_engine.ValidateInvariant(tree, state, settings);

            Assert.IsFalse(invariant.IsValid);
            StringAssert.Contains(invariant.Description, "runtime satellite list contains duplicates");
        }

        [TestMethod]
        public void ValidateInvariantDetectsRuntimeVisualOrderMismatch()
        {
            var (tree, state, settings, _) = Build(4);
            state.MutableSatellites.Reverse();

            var invariant = m_engine.ValidateInvariant(tree, state, settings);

            Assert.IsFalse(invariant.IsValid);
            StringAssert.Contains(invariant.Description, "runtime visual order");
        }

        [TestMethod]
        public void NormalizeRepairsSupportedCanonicalDamageAndPreservesWindows()
        {
            var (tree, state, settings, windows) = Build(4);
            var originalMaster = state.Master;
            var originalSatelliteOrder = state.Satellites.ToArray();
            AssertRoot(tree).Orientation = PanelOrientation.Vertical;

            var operation = m_engine.Normalize(tree, state, settings);

            Assert.IsTrue(operation.Succeeded, operation.Message);
            Assert.IsTrue(operation.Changed);
            Assert.AreEqual(PanelOrientation.Horizontal, AssertRoot(tree).Orientation);
            Assert.AreSame(originalMaster, state.Master);
            CollectionAssert.AreEqual(originalSatelliteOrder, state.Satellites.ToArray());
            CollectionAssert.AreEquivalent(windows, tree.Root!.Windows.Select(x => x.WindowReference).ToArray());
            Assert.IsFalse(state.IsRecovering);
            Assert.IsTrue(m_engine.ValidateInvariant(tree, state, settings).IsValid);
        }

        [TestMethod]
        public void NormalizeLogsBeforeAndAfterTreeSnapshots()
        {
            var (tree, state, settings, _) = Build(2);
            AssertRoot(tree).Orientation = PanelOrientation.Vertical;
            var messages = new List<string>();
            var engine = new MasterSatelliteLayoutEngine(messages.Add);

            var operation = engine.Normalize(tree, state, settings);

            Assert.IsTrue(operation.Succeeded, operation.Message);
            Assert.AreEqual(1, messages.Count);
            StringAssert.Contains(messages[0], "before:");
            StringAssert.Contains(messages[0], "after:");
            StringAssert.Contains(messages[0], nameof(PanelOrientation.Vertical));
            StringAssert.Contains(messages[0], nameof(PanelOrientation.Horizontal));
        }

        [TestMethod]
        public void NormalizeRepairsNestedPanelWithoutLosingWindows()
        {
            var (tree, state, settings, windows) = Build(3);
            var satellitePanel = AssertSatellitePanel(tree);
            var first = satellitePanel.Children[0];
            satellitePanel.Detach(first);
            var nested = new SplitPanelNode { Orientation = PanelOrientation.Horizontal };
            satellitePanel.Attach(0, nested);
            nested.Attach(first);

            var operation = m_engine.Normalize(tree, state, settings);

            Assert.IsTrue(operation.Succeeded, operation.Message);
            CollectionAssert.AreEquivalent(windows, tree.Root!.Windows.Select(x => x.WindowReference).ToArray());
            Assert.IsTrue(m_engine.ValidateInvariant(tree, state, settings).IsValid);
        }

        [TestMethod]
        public void NormalizeRestoresPanelCollapsedByGenericCleanup()
        {
            var (tree, state, settings, windows) = Build(2);
            var panel = AssertSatellitePanel(tree);
            panel.CollapseIfSingle();
            Assert.IsFalse(m_engine.ValidateInvariant(tree, state, settings).IsValid);

            var operation = m_engine.Normalize(tree, state, settings);

            Assert.IsTrue(operation.Succeeded, operation.Message);
            Assert.AreEqual(1, AssertSatellitePanel(tree).Children.Count);
            CollectionAssert.AreEquivalent(windows, tree.Root!.Windows.Select(x => x.WindowReference).ToArray());
            Assert.IsTrue(m_engine.ValidateInvariant(tree, state, settings).IsValid);
        }

        [TestMethod]
        public void NormalizeRestoresRuntimeSatelliteOrderAfterVisualMove()
        {
            var (tree, state, settings, windows) = Build(4);
            AssertSatellitePanel(tree).Move(0, 2);
            Assert.IsFalse(m_engine.ValidateInvariant(tree, state, settings).IsValid);

            var operation = m_engine.Normalize(tree, state, settings);

            Assert.IsTrue(operation.Succeeded, operation.Message);
            CollectionAssert.AreEqual(windows.Skip(1).ToArray(), state.Satellites.ToArray());
            CollectionAssert.AreEqual(
                windows.Skip(1).ToArray(),
                AssertSatellitePanel(tree).Children.Cast<WindowNode>().Select(x => x.WindowReference).ToArray());
        }

        [TestMethod]
        public void RecoveryFailureDisablesOnlyStateAndDoesNotRemoveWindows()
        {
            var settings = CreateSettings();
            var tree = new DesktopTree
            {
                WorkArea = Rectangle.OffsetAndSize(0, 0, 1000, 600),
            };
            var state = m_engine.CreateState(settings, true);
            var windows = new[]
            {
                m_windows.Create("A", minimumWidth: 400, minimumHeight: 10),
                m_windows.Create("B", minimumWidth: 400, minimumHeight: 10),
            };
            var build = m_engine.BuildLayout(tree, state, settings, windows);
            Assert.IsTrue(build.Succeeded, build.Message);
            var originalRoot = tree.Root;
            var revision = state.Revision;
            tree.WorkArea = Rectangle.OffsetAndSize(0, 0, 700, 600);

            var operation = m_engine.Normalize(tree, state, settings);

            Assert.IsFalse(operation.Succeeded);
            Assert.IsTrue(operation.Changed);
            Assert.AreEqual(MasterSatelliteFailureReason.RecoveryFailed, operation.FailureReason);
            Assert.IsFalse(state.IsActive);
            Assert.IsFalse(state.IsRecovering);
            Assert.AreEqual(revision + 1, state.Revision);
            Assert.AreSame(originalRoot, tree.Root);
            CollectionAssert.AreEquivalent(windows, tree.Root!.Windows.Select(x => x.WindowReference).ToArray());
        }

        [TestMethod]
        public void RecoveryCapacityFailureDoesNotDeleteOverflowingTreeWindow()
        {
            var (tree, state, settings, windows) = Build(4);
            var overflow = m_windows.Create("E");
            AssertSatellitePanel(tree).Attach(new WindowNode(overflow));
            var allWindows = windows.Append(overflow).ToArray();
            var originalRoot = tree.Root;

            var operation = m_engine.Normalize(tree, state, settings);

            Assert.IsFalse(operation.Succeeded);
            Assert.AreEqual(MasterSatelliteFailureReason.RecoveryFailed, operation.FailureReason);
            Assert.IsFalse(state.IsActive);
            Assert.AreSame(originalRoot, tree.Root);
            CollectionAssert.AreEquivalent(allWindows, tree.Root!.Windows.Select(x => x.WindowReference).ToArray());
        }

        [TestMethod]
        public void RemovingToOneSatelliteKeepsCanonicalPanel()
        {
            var (tree, state, settings, windows) = Build(3);

            var operation = m_engine.RemoveWindow(tree, state, settings, windows[2]);

            Assert.IsTrue(operation.Succeeded, operation.Message);
            Assert.AreEqual(1, state.Satellites.Count);
            Assert.AreEqual(1, AssertSatellitePanel(tree).Children.Count);
            Assert.IsTrue(m_engine.ValidateInvariant(tree, state, settings).IsValid);
        }

        [TestMethod]
        public void SnapshotCopiesSatelliteListInsteadOfExposingMutableRuntimeState()
        {
            var (tree, state, _, windows) = Build(3);
            var snapshot = m_engine.CreateSnapshot(tree, state);

            state.MutableSatellites.Reverse();

            CollectionAssert.AreEqual(windows.Skip(1).ToArray(), snapshot.Satellites.ToArray());
        }

#if !DEBUG
        [TestMethod]
        public void ReleaseMutationRecoversSupportedDamageBeforeContinuing()
        {
            var (tree, state, settings, _) = Build(3);
            AssertRoot(tree).Orientation = PanelOrientation.Vertical;

            var operation = m_engine.SetMasterSide(tree, state, settings, MasterSide.Right);

            Assert.IsTrue(operation.Succeeded, operation.Message);
            Assert.AreEqual(MasterSide.Right, state.MasterSide);
            Assert.AreEqual(PanelOrientation.Horizontal, AssertRoot(tree).Orientation);
            Assert.IsTrue(m_engine.ValidateInvariant(tree, state, settings).IsValid);
        }
#endif

        private (DesktopTree Tree, MasterSatelliteRuntimeState State, MasterSatelliteLayoutSettings Settings,
            IWindow[] Windows) Build(int count)
        {
            var settings = CreateSettings();
            var tree = new DesktopTree
            {
                WorkArea = Rectangle.OffsetAndSize(0, 0, 1000, 600),
            };
            var state = m_engine.CreateState(settings, true);
            var windows = Enumerable.Range(0, count)
                .Select(x => m_windows.Create(((char)('A' + x)).ToString()))
                .ToArray();
            var operation = m_engine.BuildLayout(tree, state, settings, windows);
            Assert.IsTrue(operation.Succeeded, operation.Message);
            return (tree, state, settings, windows);
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
    }
}
