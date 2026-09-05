using System;
using System.Linq;

using FancyWM.AlgorithmicLayouts;
using FancyWM.Layouts.Tiling;
using FancyWM.Models;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    [TestClass]
    public class MasterSatelliteCommandControllerTest
    {
        private readonly UniqueWindowMockFactory m_windows = new();
        private readonly Rectangle m_workArea = Rectangle.OffsetAndSize(0, 0, 1000, 600);

        [TestMethod]
        public void ToggleDisablesWithoutTreeMutationAndReenablesFromCurrentFocus()
        {
            var context = CreateActive(CreateWindows(3));
            var tree = context.Workspace.GetTree(context.Desktop)!;
            context.Workspace.SetFocus(context.Windows[1]);
            var root = tree.Root;
            var nodes = context.Windows.Select(tree.FindNode).ToArray();
            var originals = context.Windows.ToDictionary(
                window => window,
                window => context.Workspace.GetOriginalPosition(window));

            var disabled = context.Controller.Toggle(
                context.Workspace,
                context.Desktop,
                context.Display);

            Assert.AreEqual(MasterSatelliteCommandDisposition.Disabled, disabled.Disposition);
            Assert.IsTrue(disabled.Succeeded);
            Assert.IsTrue(disabled.Changed);
            Assert.IsFalse(context.Controller.IsActive(context.Desktop));
            Assert.AreSame(root, tree.Root);
            CollectionAssert.AreEqual(nodes, context.Windows.Select(tree.FindNode).ToArray());
            AssertFocusedWindow(context, context.Windows[1]);
            AssertOriginalPositions(context, originals);

            var enabled = context.Controller.Toggle(
                context.Workspace,
                context.Desktop,
                context.Display);

            Assert.AreEqual(MasterSatelliteCommandDisposition.Enabled, enabled.Disposition);
            Assert.IsTrue(enabled.Succeeded, enabled.Message);
            Assert.IsTrue(enabled.Changed);
            Assert.IsTrue(context.Controller.IsActive(context.Desktop));
            Assert.IsTrue(context.Lifecycle.TryGetState(context.Desktop, out var state));
            Assert.AreSame(context.Windows[1], state.Master);
            CollectionAssert.AreEqual(
                new[] { context.Windows[0], context.Windows[2] },
                state.Satellites.ToArray());
            AssertFocusedWindow(context, context.Windows[1]);
            AssertOriginalPositions(context, originals);
            Assert.IsTrue(enabled.Invariant!.IsValid, enabled.Invariant.Description);
        }

        [TestMethod]
        public void ToggleRejectsDisabledSettingWithoutTouchingManualTree()
        {
            var display = CreateDisplay();
            var lifecycle = new MasterSatelliteRuntimeLifecycle(display);
            var controller = new MasterSatelliteCommandController(lifecycle);
            var workspace = new TilingWorkspace();
            var desktop = CreateDesktop();
            workspace.RegisterDesktop(desktop, m_workArea, PanelOrientation.Horizontal);
            var window = m_windows.Create("manual");
            workspace.RegisterWindow(window, workspace.GetTree(desktop)!.Root!);
            var tree = workspace.GetTree(desktop)!;
            var root = tree.Root;
            var node = tree.FindNode(window);
            lifecycle.CacheSettings(new MasterSatelliteLayoutSettings { Enabled = false }, display);

            var result = controller.Toggle(workspace, desktop, display);

            Assert.AreEqual(MasterSatelliteCommandDisposition.Rejected, result.Disposition);
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(MasterSatelliteFailureReason.Inactive, result.FailureReason);
            Assert.IsFalse(controller.IsActive(desktop));
            Assert.AreSame(root, tree.Root);
            Assert.AreSame(node, tree.FindNode(window));
        }

        [TestMethod]
        public void PromoteFocusedSatellitePlacesOldMasterInItsExactSlot()
        {
            var context = CreateActive(CreateWindows(4));
            var state = GetState(context);
            var originalMaster = state.Master;
            var side = state.MasterSide;
            var orientation = state.SatelliteOrientation;
            var requestedRatio = state.RequestedMasterRatio;
            context.Workspace.SetFocus(context.Windows[2]);

            Assert.IsTrue(context.Controller.CanPromoteFocusedWindow(
                context.Workspace,
                context.Desktop));
            var result = context.Controller.PromoteFocusedWindow(
                context.Workspace,
                context.Desktop);

            AssertApplied(result);
            Assert.AreSame(context.Windows[2], state.Master);
            CollectionAssert.AreEqual(
                new[] { context.Windows[1], originalMaster, context.Windows[3] },
                state.Satellites.ToArray());
            Assert.AreEqual(side, state.MasterSide);
            Assert.AreEqual(orientation, state.SatelliteOrientation);
            Assert.AreEqual(requestedRatio, state.RequestedMasterRatio, 0.0001);
            Assert.IsTrue(result.Invariant!.IsValid, result.Invariant.Description);
            AssertFocusedWindow(context, context.Windows[2]);
            Assert.IsFalse(context.Controller.CanPromoteFocusedWindow(
                context.Workspace,
                context.Desktop));
        }

        [TestMethod]
        public void SideOrientationAndRatioCommandsPreserveRolesAndOrder()
        {
            var context = CreateActive(CreateWindows(4));
            var state = GetState(context);
            var master = state.Master;
            var satellites = state.Satellites.ToArray();
            var nodes = context.Windows.Select(
                context.Workspace.GetTree(context.Desktop)!.FindNode).ToArray();
            Assert.IsTrue(context.Workspace.SetMasterSatelliteRatio(
                context.Desktop,
                state,
                context.Settings,
                0.72).Succeeded);

            var side = context.Controller.SwapMasterSide(
                context.Workspace,
                context.Desktop);
            var orientation = context.Controller.ToggleSatelliteOrientation(
                context.Workspace,
                context.Desktop);
            var ratio = context.Controller.ResetMasterRatio(
                context.Workspace,
                context.Desktop);

            AssertApplied(side);
            AssertApplied(orientation);
            AssertApplied(ratio);
            Assert.AreEqual(MasterSide.Right, state.MasterSide);
            Assert.AreEqual(SatelliteLayoutOrientation.Horizontal, state.SatelliteOrientation);
            Assert.AreEqual(
                MasterSatelliteLayoutSettings.DefaultMasterRatio,
                state.RequestedMasterRatio,
                0.0001);
            Assert.AreSame(master, state.Master);
            CollectionAssert.AreEqual(satellites, state.Satellites.ToArray());
            CollectionAssert.AreEqual(
                nodes,
                context.Windows.Select(context.Workspace.GetTree(context.Desktop)!.FindNode).ToArray());
            var invariant = context.Workspace.ValidateMasterSatelliteLayout(
                context.Desktop,
                state,
                context.Settings);
            Assert.IsTrue(invariant.IsValid, invariant.Description);
        }

        [TestMethod]
        public void ExistingPanelCommandsSetOrientationAndRejectStack()
        {
            var context = CreateActive(CreateWindows(3));
            var state = GetState(context);

            var alreadyVertical = context.Controller.SetSatelliteOrientation(
                context.Workspace,
                context.Desktop,
                SatelliteLayoutOrientation.Vertical);
            var horizontal = context.Controller.SetSatelliteOrientation(
                context.Workspace,
                context.Desktop,
                SatelliteLayoutOrientation.Horizontal);
            var stack = context.Controller.RejectStackPanel(context.Desktop);

            Assert.AreEqual(MasterSatelliteCommandDisposition.Applied, alreadyVertical.Disposition);
            Assert.IsTrue(alreadyVertical.Succeeded, alreadyVertical.Message);
            Assert.IsFalse(alreadyVertical.Changed);
            AssertApplied(horizontal);
            Assert.AreEqual(SatelliteLayoutOrientation.Horizontal, state.SatelliteOrientation);
            Assert.AreEqual(MasterSatelliteCommandDisposition.Rejected, stack.Disposition);
            Assert.IsFalse(stack.Succeeded);
            Assert.AreEqual(MasterSatelliteFailureReason.UnsupportedOperation, stack.FailureReason);
            Assert.IsTrue(stack.Message!.Contains("Stack", StringComparison.Ordinal));
            var invariant = context.Workspace.ValidateMasterSatelliteLayout(
                context.Desktop,
                state,
                context.Settings);
            Assert.IsTrue(invariant.IsValid, invariant.Description);
        }

        [TestMethod]
        public void OverlayStructuralActionsAreRejectedAndTabPullUpPromotesCanonically()
        {
            var context = CreateActive(CreateWindows(4));
            var state = GetState(context);
            var tree = context.Workspace.GetTree(context.Desktop)!;
            var root = tree.Root!;
            var satellitePanel = root.Children.OfType<SplitPanelNode>().Single();
            var originalMaster = state.Master!;
            var originalSatellites = state.Satellites.ToArray();
            var originalNodes = context.Windows.Select(tree.FindNode).ToArray();
            var revision = state.Revision;

            var splitPanel = context.Controller.SetSatelliteOrientationFromNode(
                context.Workspace,
                context.Desktop,
                satellitePanel,
                SatelliteLayoutOrientation.Horizontal);
            var pullPanel = context.Controller.PullUpNode(
                context.Workspace,
                context.Desktop,
                satellitePanel);
            var legacyGrouping = context.Controller.RejectLegacyGrouping(
                context.Desktop);

            Assert.AreEqual(MasterSatelliteCommandDisposition.Rejected, splitPanel.Disposition);
            Assert.AreEqual(MasterSatelliteFailureReason.UnsupportedOperation, splitPanel.FailureReason);
            Assert.AreEqual(MasterSatelliteCommandDisposition.Rejected, pullPanel.Disposition);
            Assert.AreEqual(MasterSatelliteFailureReason.UnsupportedOperation, pullPanel.FailureReason);
            Assert.AreEqual(
                MasterSatelliteCommandDisposition.Rejected,
                legacyGrouping.Disposition);
            Assert.AreEqual(
                MasterSatelliteFailureReason.UnsupportedOperation,
                legacyGrouping.FailureReason);
            Assert.AreEqual(revision, state.Revision);
            Assert.AreSame(root, tree.Root);
            Assert.AreSame(originalMaster, state.Master);
            CollectionAssert.AreEqual(originalSatellites, state.Satellites.ToArray());
            CollectionAssert.AreEqual(originalNodes, context.Windows.Select(tree.FindNode).ToArray());

            var selected = originalSatellites[1];
            var selectedNode = tree.FindNode(selected)!;
            var pullTab = context.Controller.PullUpNode(
                context.Workspace,
                context.Desktop,
                selectedNode);

            AssertApplied(pullTab);
            Assert.AreSame(selected, state.Master);
            CollectionAssert.AreEqual(
                new[] { originalSatellites[0], originalMaster, originalSatellites[2] },
                state.Satellites.ToArray());
            Assert.IsTrue(context.Workspace.ValidateMasterSatelliteLayout(
                context.Desktop,
                state,
                context.Settings).IsValid);
        }

        [TestMethod]
        public void ExistingMoveCommandSetsMasterSideAndReordersOnActiveAxis()
        {
            var context = CreateActive(CreateWindows(4));
            var state = GetState(context);
            context.Workspace.SetFocus(state.Master!);

            Assert.IsFalse(context.Controller.CanMoveFocusedWindow(
                context.Workspace,
                context.Desktop,
                TilingDirection.Left));
            Assert.IsTrue(context.Controller.CanMoveFocusedWindow(
                context.Workspace,
                context.Desktop,
                TilingDirection.Right));
            AssertApplied(context.Controller.MoveFocusedWindow(
                context.Workspace,
                context.Desktop,
                TilingDirection.Right));
            Assert.AreEqual(MasterSide.Right, state.MasterSide);

            context.Workspace.SetFocus(context.Windows[2]);
            Assert.IsTrue(context.Controller.CanMoveFocusedWindow(
                context.Workspace,
                context.Desktop,
                TilingDirection.Up));
            AssertApplied(context.Controller.MoveFocusedWindow(
                context.Workspace,
                context.Desktop,
                TilingDirection.Up));
            CollectionAssert.AreEqual(
                new[] { context.Windows[2], context.Windows[1], context.Windows[3] },
                state.Satellites.ToArray());

            Assert.IsFalse(context.Controller.CanMoveFocusedWindow(
                context.Workspace,
                context.Desktop,
                TilingDirection.Left));
            var perpendicular = context.Controller.MoveFocusedWindow(
                context.Workspace,
                context.Desktop,
                TilingDirection.Left);
            Assert.IsFalse(perpendicular.Succeeded);
            Assert.AreEqual(
                MasterSatelliteFailureReason.UnsupportedOperation,
                perpendicular.FailureReason);
            CollectionAssert.AreEqual(
                new[] { context.Windows[2], context.Windows[1], context.Windows[3] },
                state.Satellites.ToArray());
            Assert.IsTrue(context.Workspace.ValidateMasterSatelliteLayout(
                context.Desktop,
                state,
                context.Settings).IsValid);
        }

        [DataTestMethod]
        [DataRow(2, MasterSide.Left, TilingDirection.Left, 0)]
        [DataRow(2, MasterSide.Right, TilingDirection.Right, 0)]
        [DataRow(4, MasterSide.Left, TilingDirection.Left, 0)]
        [DataRow(4, MasterSide.Right, TilingDirection.Right, 2)]
        public void MoveSatelliteAcrossAdjacentMasterPromotesOnActiveAxis(
            int windowCount,
            MasterSide masterSide,
            TilingDirection direction,
            int satelliteIndex)
        {
            var context = CreateActive(CreateWindows(windowCount));
            var state = GetState(context);
            var originalMaster = state.Master!;
            if (masterSide == MasterSide.Right)
            {
                AssertApplied(context.Controller.SwapMasterSide(
                    context.Workspace,
                    context.Desktop));
            }
            Assert.AreEqual(masterSide, state.MasterSide);
            var selected = state.Satellites[satelliteIndex];
            context.Workspace.SetFocus(selected);

            // The master is spatially adjacent in this direction, but promotion
            // through Move remains limited to the active satellite axis.
            Assert.IsFalse(context.Controller.CanMoveFocusedWindow(
                context.Workspace,
                context.Desktop,
                direction));
            AssertApplied(context.Controller.ToggleSatelliteOrientation(
                context.Workspace,
                context.Desktop));
            Assert.AreEqual(
                SatelliteLayoutOrientation.Horizontal,
                state.SatelliteOrientation);

            var outerDirection = direction == TilingDirection.Left
                ? TilingDirection.Right
                : TilingDirection.Left;
            var outerSatellite = direction == TilingDirection.Left
                ? state.Satellites[^1]
                : state.Satellites[0];
            context.Workspace.SetFocus(outerSatellite);
            Assert.IsFalse(context.Controller.CanMoveFocusedWindow(
                context.Workspace,
                context.Desktop,
                outerDirection));
            var outerMove = context.Controller.MoveFocusedWindow(
                context.Workspace,
                context.Desktop,
                outerDirection);
            Assert.IsFalse(outerMove.Succeeded);
            Assert.AreEqual(
                MasterSatelliteFailureReason.InvalidSatelliteIndex,
                outerMove.FailureReason);

            context.Workspace.SetFocus(selected);
            Assert.IsTrue(context.Controller.CanMoveFocusedWindow(
                context.Workspace,
                context.Desktop,
                direction));

            var result = context.Controller.MoveFocusedWindow(
                context.Workspace,
                context.Desktop,
                direction);

            AssertApplied(result);
            Assert.AreSame(selected, state.Master);
            Assert.AreSame(originalMaster, state.Satellites[satelliteIndex]);
            Assert.AreEqual(masterSide, state.MasterSide);
            AssertFocusedWindow(context, selected);
            var invariant = context.Workspace.ValidateMasterSatelliteLayout(
                context.Desktop,
                state,
                context.Settings);
            Assert.IsTrue(invariant.IsValid, invariant.Description);
        }

        [TestMethod]
        public void ExistingSwapCommandPromotesTheMasterSatelliteCounterpart()
        {
            var context = CreateActive(CreateWindows(4));
            var state = GetState(context);
            var oldMaster = state.Master!;
            context.Workspace.SetFocus(oldMaster);
            var focusedNode = (WindowNode)context.Workspace.GetFocus(context.Desktop)!;
            var selected = focusedNode.GetAdjacentWindow(TilingDirection.Right);
            Assert.IsNotNull(selected);
            int selectedIndex = Array.IndexOf(
                state.Satellites.ToArray(),
                selected.WindowReference);
            Assert.IsTrue(selectedIndex >= 0);

            Assert.IsTrue(context.Controller.CanSwapFocusedWindow(
                context.Workspace,
                context.Desktop,
                TilingDirection.Right));
            var result = context.Controller.SwapFocusedWindow(
                context.Workspace,
                context.Desktop,
                TilingDirection.Right);

            AssertApplied(result);
            Assert.AreSame(selected.WindowReference, state.Master);
            Assert.AreSame(oldMaster, state.Satellites[selectedIndex]);
            Assert.IsTrue(context.Workspace.ValidateMasterSatelliteLayout(
                context.Desktop,
                state,
                context.Settings).IsValid);
        }

        [TestMethod]
        public void ExistingResizeCommandChangesOnlyFocusedMasterWidthRatio()
        {
            var context = CreateActive(CreateWindows(3));
            var state = GetState(context);
            context.Workspace.SetFocus(state.Master!);
            var beforeRatio = state.RequestedMasterRatio;

            Assert.IsTrue(context.Controller.CanResizeFocusedMaster(
                context.Workspace,
                context.Desktop,
                PanelOrientation.Horizontal,
                0.05));
            var resized = context.Controller.ResizeFocusedMaster(
                context.Workspace,
                context.Desktop,
                PanelOrientation.Horizontal,
                0.05);

            AssertApplied(resized);
            Assert.IsTrue(state.RequestedMasterRatio > beforeRatio);
            Assert.IsTrue(state.EffectiveMasterRatio > beforeRatio);

            var revision = state.Revision;
            var vertical = context.Controller.ResizeFocusedMaster(
                context.Workspace,
                context.Desktop,
                PanelOrientation.Vertical,
                0.05);
            Assert.IsFalse(vertical.Succeeded);
            Assert.AreEqual(MasterSatelliteFailureReason.UnsupportedOperation, vertical.FailureReason);
            Assert.AreEqual(revision, state.Revision);

            context.Workspace.SetFocus(state.Satellites[0]);
            Assert.IsFalse(context.Controller.CanResizeFocusedMaster(
                context.Workspace,
                context.Desktop,
                PanelOrientation.Horizontal,
                0.05));
            var satellite = context.Controller.ResizeFocusedMaster(
                context.Workspace,
                context.Desktop,
                PanelOrientation.Horizontal,
                0.05);
            Assert.IsFalse(satellite.Succeeded);
            Assert.AreEqual(MasterSatelliteFailureReason.UnsupportedOperation, satellite.FailureReason);
            Assert.AreEqual(revision, state.Revision);
            Assert.IsTrue(context.Workspace.ValidateMasterSatelliteLayout(
                context.Desktop,
                state,
                context.Settings).IsValid);
        }

        [TestMethod]
        public void NativeMasterResizeCapturesRatioAndRejectsSatelliteMutation()
        {
            var context = CreateActive(CreateWindows(3));
            var state = GetState(context);
            var tree = context.Workspace.GetTree(context.Desktop)!;
            tree.Measure();
            tree.Arrange();

            var resized = context.Controller.ResizeWindowByPixels(
                context.Workspace,
                context.Desktop,
                state.Master!,
                100);

            AssertApplied(resized);
            Assert.AreEqual(0.70, state.RequestedMasterRatio, 0.001);
            var capturedRatio = state.RequestedMasterRatio;
            var relayout = context.Workspace.RelayoutMasterSatelliteLayout(
                context.Desktop,
                state,
                context.Settings);
            Assert.IsTrue(relayout.Succeeded, relayout.Message);
            Assert.AreEqual(capturedRatio, state.RequestedMasterRatio, 0.0001);
            Assert.AreEqual(capturedRatio, state.EffectiveMasterRatio, 0.001);

            var revision = state.Revision;
            var satellite = context.Controller.ResizeWindowByPixels(
                context.Workspace,
                context.Desktop,
                state.Satellites[0],
                50);
            Assert.AreEqual(MasterSatelliteCommandDisposition.Rejected, satellite.Disposition);
            Assert.AreEqual(
                MasterSatelliteFailureReason.UnsupportedOperation,
                satellite.FailureReason);
            Assert.AreEqual(revision, state.Revision);
            Assert.IsTrue(context.Workspace.ValidateMasterSatelliteLayout(
                context.Desktop,
                state,
                context.Settings).IsValid);
        }

        [TestMethod]
        public void ImpossibleOrientationToggleIsRejectedAtomically()
        {
            var windows = new[]
            {
                m_windows.Create("master", minimumWidth: 100, minimumHeight: 100),
                m_windows.Create("satellite-1", minimumWidth: 350, minimumHeight: 50),
                m_windows.Create("satellite-2", minimumWidth: 350, minimumHeight: 50),
                m_windows.Create("satellite-3", minimumWidth: 350, minimumHeight: 50),
            };
            var context = CreateActive(windows);
            var state = GetState(context);
            var tree = context.Workspace.GetTree(context.Desktop)!;
            var root = tree.Root;
            var nodes = windows.Select(tree.FindNode).ToArray();
            var revision = state.Revision;
            Assert.IsTrue(context.Workspace.TryGetMasterSatelliteSnapshot(
                context.Desktop,
                state,
                out var before));

            var result = context.Controller.ToggleSatelliteOrientation(
                context.Workspace,
                context.Desktop);

            Assert.AreEqual(MasterSatelliteCommandDisposition.Rejected, result.Disposition);
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(MasterSatelliteFailureReason.MinSizeConflict, result.FailureReason);
            Assert.AreEqual(SatelliteLayoutOrientation.Vertical, state.SatelliteOrientation);
            Assert.AreEqual(revision, state.Revision);
            Assert.AreSame(root, tree.Root);
            CollectionAssert.AreEqual(nodes, windows.Select(tree.FindNode).ToArray());
            Assert.IsTrue(context.Workspace.TryGetMasterSatelliteSnapshot(
                context.Desktop,
                state,
                out var after));
            Assert.AreEqual(before.TreeDescription, after.TreeDescription);
        }

        [TestMethod]
        public void RebalanceAlreadyBalancedCanonicalTreeIsNoOp()
        {
            var context = CreateActive(CreateWindows(4));
            var state = GetState(context);
            var tree = context.Workspace.GetTree(context.Desktop)!;
            var root = tree.Root;
            var nodes = context.Windows.Select(tree.FindNode).ToArray();
            long revision = state.Revision;

            var result = context.Controller.Rebalance(
                context.Workspace,
                context.Desktop);

            Assert.IsTrue(result.Succeeded, result.Message);
            Assert.IsFalse(result.Changed);
            Assert.AreEqual(revision, state.Revision);
            Assert.AreSame(root, tree.Root);
            CollectionAssert.AreEqual(nodes, context.Windows.Select(tree.FindNode).ToArray());
            Assert.IsTrue(result.Invariant!.IsValid, result.Invariant.Description);
            Assert.IsNull(TilingService.CreateMasterSatelliteCommandChangedEvent(
                "Rebalance",
                result,
                context.Display,
                context.Desktop));
        }

        [TestMethod]
        public void RebalanceRecoversCanonicalTreeAndPreservesRuntimeChoices()
        {
            var context = CreateActive(CreateWindows(4));
            var state = GetState(context);
            AssertApplied(context.Controller.SwapMasterSide(
                context.Workspace,
                context.Desktop));
            AssertApplied(context.Controller.ToggleSatelliteOrientation(
                context.Workspace,
                context.Desktop));
            Assert.IsTrue(context.Workspace.SetMasterSatelliteRatio(
                context.Desktop,
                state,
                context.Settings,
                0.71).Succeeded);
            context.Workspace.SetFocus(context.Windows[2]);
            var master = state.Master;
            var satellites = state.Satellites.ToArray();
            var originals = context.Windows.ToDictionary(
                window => window,
                window => context.Workspace.GetOriginalPosition(window));
            var revision = state.Revision;
            ((SplitPanelNode)context.Workspace.GetTree(context.Desktop)!.Root!).Orientation
                = PanelOrientation.Vertical;

            var result = context.Controller.Rebalance(
                context.Workspace,
                context.Desktop);

            AssertApplied(result);
            Assert.AreEqual(revision + 1, state.Revision);
            Assert.AreSame(master, state.Master);
            CollectionAssert.AreEqual(satellites, state.Satellites.ToArray());
            Assert.AreEqual(MasterSide.Right, state.MasterSide);
            Assert.AreEqual(SatelliteLayoutOrientation.Horizontal, state.SatelliteOrientation);
            Assert.AreEqual(0.71, state.RequestedMasterRatio, 0.0001);
            AssertFocusedWindow(context, context.Windows[2]);
            AssertOriginalPositions(context, originals);
            Assert.IsTrue(result.Invariant!.IsValid, result.Invariant.Description);
            var layoutEvent = TilingService.CreateMasterSatelliteCommandChangedEvent(
                "Rebalance",
                result,
                context.Display,
                context.Desktop);
            Assert.IsNotNull(layoutEvent);
            Assert.AreEqual(
                AlgorithmicLayoutEventKind.LayoutRecovered,
                layoutEvent.Kind);
            Assert.AreSame(context.Display, layoutEvent.Display);
            Assert.AreSame(context.Desktop, layoutEvent.SourceDesktop);
            Assert.AreEqual("Rebalance", layoutEvent.Reason);
            Assert.AreEqual(
                "AlgorithmicLayout.LayoutRecovered",
                layoutEvent.MessageKey);
            var notification = AlgorithmicLayoutNotificationFormatter.Format(
                layoutEvent,
                "Window",
                _ => "Desktop 1");
            Assert.IsTrue(notification.ShouldShow);
            Assert.IsFalse(notification.PlayFailureSound);

            var tree = context.Workspace.GetTree(context.Desktop)!;
            tree.Measure();
            tree.Arrange();
            var panel = tree.Root!.Children.OfType<SplitPanelNode>().Single();
            var widths = panel.Children.Select(child => child.ComputedRectangle.Width).ToArray();
            Assert.IsTrue(widths.Max() - widths.Min() <= 1,
                $"Expected evenly rebalanced satellites, got [{string.Join(", ", widths)}].");
        }

        [TestMethod]
        public void LayoutCommandsRejectInactiveDesktopWithoutMutation()
        {
            var display = CreateDisplay();
            var lifecycle = new MasterSatelliteRuntimeLifecycle(display);
            var controller = new MasterSatelliteCommandController(lifecycle);
            var workspace = new TilingWorkspace();
            var desktop = CreateDesktop();
            workspace.RegisterDesktop(desktop, m_workArea, PanelOrientation.Horizontal);
            var window = m_windows.Create("manual");
            workspace.RegisterWindow(window, workspace.GetTree(desktop)!.Root!);
            var tree = workspace.GetTree(desktop)!;
            var root = tree.Root;
            var node = tree.FindNode(window);

            var results = new[]
            {
                controller.PromoteFocusedWindow(workspace, desktop),
                controller.SwapMasterSide(workspace, desktop),
                controller.ToggleSatelliteOrientation(workspace, desktop),
                controller.ResetMasterRatio(workspace, desktop),
                controller.Rebalance(workspace, desktop),
                controller.RejectStackPanel(desktop),
            };

            Assert.IsFalse(controller.CanPromoteFocusedWindow(workspace, desktop));
            Assert.IsTrue(results.All(result => !result.Succeeded));
            Assert.IsTrue(results.All(result =>
                result.Disposition == MasterSatelliteCommandDisposition.Rejected));
            Assert.IsTrue(results.All(result =>
                result.FailureReason == MasterSatelliteFailureReason.Inactive));
            Assert.AreSame(root, tree.Root);
            Assert.AreSame(node, tree.FindNode(window));
        }

        private ActiveContext CreateActive(IWindow[] windows)
        {
            var settings = new MasterSatelliteLayoutSettings
            {
                Enabled = true,
                DisplayScope = AlgorithmicLayoutDisplayScope.AllDisplays,
                MasterRatio = 0.60,
                DefaultMasterSide = MasterSide.Left,
                DefaultSatelliteOrientation = SatelliteLayoutOrientation.Vertical,
                MaxSatellites = Math.Max(3, windows.Length - 1),
            };
            var display = CreateDisplay();
            var lifecycle = new MasterSatelliteRuntimeLifecycle(display);
            var controller = new MasterSatelliteCommandController(lifecycle);
            var workspace = new TilingWorkspace();
            var desktop = CreateDesktop();
            workspace.RegisterDesktop(desktop, m_workArea, PanelOrientation.Horizontal);
            var root = workspace.GetTree(desktop)!.Root!;
            foreach (var window in windows)
            {
                workspace.RegisterWindow(window, root);
            }
            var activation = lifecycle.ApplySettings(workspace, settings, display, desktop);
            Assert.IsTrue(activation.StateAdded);
            Assert.IsTrue(activation.Operation!.Succeeded, activation.Operation.Message);
            Assert.IsTrue(activation.Invariant!.IsValid, activation.Invariant.Description);
            return new ActiveContext(
                workspace,
                lifecycle,
                controller,
                desktop,
                display,
                settings,
                windows);
        }

        private IWindow[] CreateWindows(int count)
        {
            return Enumerable.Range(0, count)
                .Select(index => m_windows.Create(((char)('A' + index)).ToString()))
                .ToArray();
        }

        private IDisplay CreateDisplay()
        {
            var mock = new Mock<IDisplay>(MockBehavior.Loose);
            var display = mock.Object;
            mock.SetupGet(candidate => candidate.WorkArea).Returns(m_workArea);
            mock.SetupGet(candidate => candidate.Scaling).Returns(1.0);
            mock.Setup(candidate => candidate.Equals(It.IsAny<IDisplay>()))
                .Returns((IDisplay other) => ReferenceEquals(display, other));
            return display;
        }

        private static IVirtualDesktop CreateDesktop()
        {
            var mock = new Mock<IVirtualDesktop>(MockBehavior.Loose);
            mock.SetupGet(desktop => desktop.Name).Returns("D1");
            mock.SetupGet(desktop => desktop.IsAlive).Returns(true);
            return mock.Object;
        }

        private static MasterSatelliteRuntimeState GetState(ActiveContext context)
        {
            Assert.IsTrue(context.Lifecycle.TryGetState(context.Desktop, out var state));
            return state;
        }

        private static void AssertApplied(MasterSatelliteCommandResult result)
        {
            Assert.AreEqual(MasterSatelliteCommandDisposition.Applied, result.Disposition);
            Assert.IsTrue(result.Succeeded, result.Message);
            Assert.IsTrue(result.Changed);
            Assert.IsTrue(result.Invariant!.IsValid, result.Invariant.Description);
        }

        private static void AssertFocusedWindow(ActiveContext context, IWindow expected)
        {
            Assert.IsInstanceOfType(
                context.Workspace.GetFocus(context.Desktop),
                typeof(WindowNode));
            Assert.AreSame(
                expected,
                ((WindowNode)context.Workspace.GetFocus(context.Desktop)!).WindowReference);
        }

        private static void AssertOriginalPositions(
            ActiveContext context,
            System.Collections.Generic.IReadOnlyDictionary<IWindow, Rectangle> expected)
        {
            foreach (var pair in expected)
            {
                Assert.IsTrue(context.Workspace.TryGetOriginalPosition(pair.Key, out var actual));
                Assert.AreEqual(pair.Value, actual);
            }
        }

        private sealed record class ActiveContext(
            TilingWorkspace Workspace,
            MasterSatelliteRuntimeLifecycle Lifecycle,
            MasterSatelliteCommandController Controller,
            IVirtualDesktop Desktop,
            IDisplay Display,
            MasterSatelliteLayoutSettings Settings,
            IWindow[] Windows);
    }
}
