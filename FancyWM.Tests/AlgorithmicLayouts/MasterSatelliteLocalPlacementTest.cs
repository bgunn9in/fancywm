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
    public class MasterSatelliteLocalPlacementTest
    {
        private static readonly Rectangle WorkArea = Rectangle.OffsetAndSize(0, 0, 2400, 1200);

        private readonly UniqueWindowMockFactory m_windows = new();

        [DataTestMethod]
        [DataRow(1, 2)]
        [DataRow(3, 4)]
        [DataRow(9, 10)]
        public void LocalCapacityIsMasterPlusConfiguredSatellites(
            int maxSatellites,
            int expectedCapacity)
        {
            var (workspace, lifecycle, desktop, _, settings) = CreateActiveEmpty(maxSatellites);
            var windows = CreateWindows(expectedCapacity + 1);

            for (int i = 0; i < expectedCapacity; i++)
            {
                var placement = lifecycle.PlaceWindow(workspace, desktop, windows[i]);
                AssertLocalSuccess(placement, MasterSatelliteLocalMutationDisposition.Placed);
            }

            Assert.IsTrue(lifecycle.TryGetState(desktop, out var state));
            Assert.AreSame(windows[0], state.Master);
            CollectionAssert.AreEqual(windows.Skip(1).Take(maxSatellites).ToArray(), state.Satellites.ToArray());
            Assert.AreEqual(expectedCapacity, workspace.GetTree(desktop)!.Root!.Windows.Count());
            var capacity = workspace.QueryMasterSatelliteCapacity(desktop, state, settings);
            Assert.AreEqual(expectedCapacity, capacity.TotalCapacity);
            Assert.AreEqual(expectedCapacity, capacity.OccupiedSlots);
            Assert.IsFalse(capacity.CanAcceptWindow);

            long revision = state.Revision;
            var root = workspace.GetTree(desktop)!.Root;
            var nodes = windows.Take(expectedCapacity)
                .Select(window => workspace.GetTree(desktop)!.FindNode(window))
                .ToArray();
            var overflow = lifecycle.PlaceWindow(workspace, desktop, windows[^1]);

            Assert.AreEqual(MasterSatelliteLocalMutationDisposition.Rejected, overflow.Disposition);
            Assert.IsFalse(overflow.Succeeded);
            Assert.AreEqual(MasterSatelliteFailureReason.CapacityReached, overflow.Operation!.FailureReason);
            Assert.AreEqual(revision, state.Revision);
            Assert.AreSame(root, workspace.GetTree(desktop)!.Root);
            CollectionAssert.AreEqual(
                nodes,
                windows.Take(expectedCapacity)
                    .Select(window => workspace.GetTree(desktop)!.FindNode(window))
                    .ToArray());
            Assert.IsNull(workspace.GetTree(desktop)!.FindNode(windows[^1]));
            Assert.IsFalse(workspace.TryGetOriginalPosition(windows[^1], out _));
        }

        [TestMethod]
        public void FirstAndSubsequentPlacementsSetRolesOrderAndFocus()
        {
            var (workspace, lifecycle, desktop, _, _) = CreateActiveEmpty(maxSatellites: 3);
            var windows = CreateWindows(4);

            for (int i = 0; i < windows.Length; i++)
            {
                var placement = lifecycle.PlaceWindow(workspace, desktop, windows[i]);

                AssertLocalSuccess(placement, MasterSatelliteLocalMutationDisposition.Placed);
                Assert.AreSame(
                    windows[i],
                    ((WindowNode)workspace.GetFocus(desktop)!).WindowReference,
                    "Every successful local placement must focus the placed window.");
            }

            Assert.IsTrue(lifecycle.TryGetState(desktop, out var state));
            Assert.AreSame(windows[0], state.Master);
            CollectionAssert.AreEqual(windows.Skip(1).ToArray(), state.Satellites.ToArray());
            CollectionAssert.AreEqual(windows, TreeWindows(workspace, desktop));
            AssertValid(workspace, desktop, state, lifecycle.SettingsSnapshot);
        }

        [TestMethod]
        public void FullLayoutRejectionDoesNotMutateTreeStateFocusOrMetadata()
        {
            var (workspace, lifecycle, desktop, _, settings) = CreateActiveEmpty(maxSatellites: 1);
            var windows = CreateWindows(3);
            AssertLocalSuccess(
                lifecycle.PlaceWindow(workspace, desktop, windows[0]),
                MasterSatelliteLocalMutationDisposition.Placed);
            AssertLocalSuccess(
                lifecycle.PlaceWindow(workspace, desktop, windows[1]),
                MasterSatelliteLocalMutationDisposition.Placed);
            Assert.IsTrue(lifecycle.TryGetState(desktop, out var state));
            var tree = workspace.GetTree(desktop)!;
            var root = tree.Root;
            var nodes = windows.Take(2).Select(tree.FindNode).ToArray();
            var focus = workspace.GetFocus(desktop);
            long revision = state.Revision;

            var rejected = lifecycle.PlaceWindow(workspace, desktop, windows[2]);

            AssertRejectedWithoutMutation(
                rejected,
                MasterSatelliteFailureReason.CapacityReached,
                workspace,
                desktop,
                state,
                settings,
                revision,
                root,
                focus,
                windows.Take(2).ToArray(),
                nodes,
                windows[2]);
        }

        [TestMethod]
        public void MinimumSizeRejectionDoesNotMutateTreeStateFocusOrMetadata()
        {
            var area = Rectangle.OffsetAndSize(0, 0, 1000, 600);
            var (workspace, lifecycle, desktop, _, settings) = CreateActiveEmpty(
                maxSatellites: 3,
                workArea: area);
            var master = m_windows.Create("master", minimumWidth: 700);
            var oversizedSatellite = m_windows.Create("oversized-satellite", minimumWidth: 700);
            AssertLocalSuccess(
                lifecycle.PlaceWindow(workspace, desktop, master),
                MasterSatelliteLocalMutationDisposition.Placed);
            Assert.IsTrue(lifecycle.TryGetState(desktop, out var state));
            var tree = workspace.GetTree(desktop)!;
            var root = tree.Root;
            var node = tree.FindNode(master);
            var focus = workspace.GetFocus(desktop);
            long revision = state.Revision;

            var rejected = lifecycle.PlaceWindow(workspace, desktop, oversizedSatellite);

            AssertRejectedWithoutMutation(
                rejected,
                MasterSatelliteFailureReason.MinSizeConflict,
                workspace,
                desktop,
                state,
                settings,
                revision,
                root,
                focus,
                [master],
                [node],
                oversizedSatellite);
        }

        [TestMethod]
        public void IncomingReservationPreventsUncorrelatedLocalSlotTheft()
        {
            var (workspace, lifecycle, desktop, _, settings) = CreateActiveEmpty(maxSatellites: 3);
            var master = m_windows.Create("master");
            var localCandidate = m_windows.Create("local-candidate");
            AssertLocalSuccess(
                lifecycle.PlaceWindow(workspace, desktop, master),
                MasterSatelliteLocalMutationDisposition.Placed);
            Assert.IsTrue(lifecycle.TryGetState(desktop, out var state));
            var tree = workspace.GetTree(desktop)!;
            var root = tree.Root;
            var focus = workspace.GetFocus(desktop);
            long revision = state.Revision;

            var rejected = lifecycle.PlaceWindow(
                workspace,
                desktop,
                localCandidate,
                nextSlotReserved: true);

            AssertRejectedWithoutMutation(
                rejected,
                MasterSatelliteFailureReason.CapacityReached,
                workspace,
                desktop,
                state,
                settings,
                revision,
                root,
                focus,
                [master],
                [tree.FindNode(master)!],
                localCandidate);
            StringAssert.Contains(rejected.Operation!.Message!, "reserved");
        }

        [TestMethod]
        public void CanonicalRemovalHandlesSatelliteMasterCollapseAndEmptyLayout()
        {
            var (workspace, lifecycle, desktop, _, settings) = CreateActiveEmpty(maxSatellites: 3);
            var windows = CreateWindows(4);
            foreach (var window in windows)
            {
                AssertLocalSuccess(
                    lifecycle.PlaceWindow(workspace, desktop, window),
                    MasterSatelliteLocalMutationDisposition.Placed);
            }
            Assert.IsTrue(lifecycle.TryGetState(desktop, out var state));

            AssertLocalSuccess(
                lifecycle.RemoveWindow(workspace, windows[1], preserveOriginalPosition: false),
                MasterSatelliteLocalMutationDisposition.Removed);
            Assert.AreSame(windows[0], state.Master);
            CollectionAssert.AreEqual(new[] { windows[2], windows[3] }, state.Satellites.ToArray());
            CollectionAssert.AreEqual(new[] { windows[0], windows[2], windows[3] }, TreeWindows(workspace, desktop));
            Assert.IsFalse(workspace.TryGetOriginalPosition(windows[1], out _));
            AssertValid(workspace, desktop, state, settings);

            AssertLocalSuccess(
                lifecycle.RemoveWindow(workspace, windows[0], preserveOriginalPosition: false),
                MasterSatelliteLocalMutationDisposition.Removed);
            Assert.AreSame(windows[2], state.Master, "Removing master must promote the first satellite.");
            CollectionAssert.AreEqual(new[] { windows[3] }, state.Satellites.ToArray());
            CollectionAssert.AreEqual(new[] { windows[2], windows[3] }, TreeWindows(workspace, desktop));
            AssertValid(workspace, desktop, state, settings);

            AssertLocalSuccess(
                lifecycle.RemoveWindow(workspace, windows[3], preserveOriginalPosition: false),
                MasterSatelliteLocalMutationDisposition.Removed);
            Assert.AreSame(windows[2], state.Master);
            Assert.AreEqual(0, state.Satellites.Count);
            CollectionAssert.AreEqual(new[] { windows[2] }, TreeWindows(workspace, desktop));
            Assert.AreEqual(1, workspace.GetTree(desktop)!.Root!.Children.Count);
            AssertValid(workspace, desktop, state, settings);

            AssertLocalSuccess(
                lifecycle.RemoveWindow(workspace, windows[2], preserveOriginalPosition: false),
                MasterSatelliteLocalMutationDisposition.Removed);
            Assert.IsNull(state.Master);
            Assert.AreEqual(0, state.Satellites.Count);
            Assert.AreEqual(0, TreeWindows(workspace, desktop).Length);
            Assert.AreEqual(0, workspace.GetTree(desktop)!.Root!.Children.Count);
            AssertValid(workspace, desktop, state, settings);
        }

        [TestMethod]
        public void MinimizeStyleRemovalPreservesOriginalPositionForRestore()
        {
            var (workspace, lifecycle, desktop, _, settings) = CreateActiveEmpty(maxSatellites: 3);
            var window = m_windows.Create("A");
            AssertLocalSuccess(
                lifecycle.PlaceWindow(workspace, desktop, window),
                MasterSatelliteLocalMutationDisposition.Placed);
            Assert.IsTrue(workspace.TryGetOriginalPosition(window, out var originalPosition));

            var removed = lifecycle.RemoveWindow(
                workspace,
                window,
                preserveOriginalPosition: true);

            AssertLocalSuccess(removed, MasterSatelliteLocalMutationDisposition.Removed);
            Assert.IsNull(workspace.GetTree(desktop)!.FindNode(window));
            Assert.IsTrue(workspace.TryGetOriginalPosition(window, out var preservedPosition));
            Assert.AreEqual(originalPosition, preservedPosition);
            Assert.IsTrue(lifecycle.TryGetState(desktop, out var state));
            AssertValid(workspace, desktop, state, settings);

            var restored = lifecycle.PlaceWindow(workspace, desktop, window);

            AssertLocalSuccess(restored, MasterSatelliteLocalMutationDisposition.Placed);
            Assert.AreSame(window, state.Master);
            Assert.IsTrue(workspace.TryGetOriginalPosition(window, out var restoredOriginalPosition));
            Assert.AreEqual(originalPosition, restoredOriginalPosition);
            Assert.AreSame(window, ((WindowNode)workspace.GetFocus(desktop)!).WindowReference);
            AssertValid(workspace, desktop, state, settings);
        }

        [TestMethod]
        public void DeactivatedDesktopReturnsNotActiveAndAllowsGenericFallback()
        {
            var (workspace, lifecycle, desktop, _, _) = CreateActiveEmpty(maxSatellites: 3);
            var first = m_windows.Create("A");
            var fallback = m_windows.Create("B");
            AssertLocalSuccess(
                lifecycle.PlaceWindow(workspace, desktop, first),
                MasterSatelliteLocalMutationDisposition.Placed);
            var root = workspace.GetTree(desktop)!.Root!;

            Assert.IsTrue(lifecycle.DeactivateDesktop(desktop));
            Assert.IsFalse(lifecycle.TryGetState(desktop, out _));
            Assert.AreSame(root, workspace.GetTree(desktop)!.Root);

            var localPlacement = lifecycle.PlaceWindow(workspace, desktop, fallback);
            Assert.AreEqual(MasterSatelliteLocalMutationDisposition.NotActive, localPlacement.Disposition);
            Assert.IsNull(workspace.GetTree(desktop)!.FindNode(fallback));

            var genericNode = workspace.RegisterWindow(fallback, root);
            Assert.AreSame(genericNode, workspace.GetTree(desktop)!.FindNode(fallback));
            CollectionAssert.AreEqual(new[] { first, fallback }, TreeWindows(workspace, desktop));

            var localRemoval = lifecycle.RemoveWindow(
                workspace,
                first,
                preserveOriginalPosition: false);
            Assert.AreEqual(MasterSatelliteLocalMutationDisposition.NotActive, localRemoval.Disposition);
            workspace.UnregisterWindow(first);
            CollectionAssert.AreEqual(new[] { fallback }, TreeWindows(workspace, desktop));
        }

        [TestMethod]
        public void DisabledSettingsReturnNotActiveWithoutMutation()
        {
            var workspace = new TilingWorkspace();
            var desktop = CreateDesktop("D1");
            var display = CreateDisplay(WorkArea);
            var lifecycle = new MasterSatelliteRuntimeLifecycle(display);
            workspace.RegisterDesktop(desktop, WorkArea, PanelOrientation.Horizontal);
            var existing = m_windows.Create("existing");
            var candidate = m_windows.Create("candidate");
            var existingNode = workspace.RegisterWindow(existing, workspace.GetTree(desktop)!.Root!);
            var root = workspace.GetTree(desktop)!.Root;
            var disabled = CreateSettings(enabled: false, maxSatellites: 3);
            var transition = lifecycle.ApplySettings(workspace, disabled, display, desktop);
            Assert.IsFalse(transition.Eligible);
            Assert.IsFalse(transition.StatePresent);

            var placement = lifecycle.PlaceWindow(workspace, desktop, candidate);
            var removal = lifecycle.RemoveWindow(
                workspace,
                existing,
                preserveOriginalPosition: false);

            Assert.AreEqual(MasterSatelliteLocalMutationDisposition.NotActive, placement.Disposition);
            Assert.AreEqual(MasterSatelliteLocalMutationDisposition.NotActive, removal.Disposition);
            Assert.IsFalse(lifecycle.TryGetState(desktop, out _));
            Assert.AreSame(root, workspace.GetTree(desktop)!.Root);
            Assert.AreSame(existingNode, workspace.GetTree(desktop)!.FindNode(existing));
            Assert.IsNull(workspace.GetTree(desktop)!.FindNode(candidate));
            CollectionAssert.AreEqual(new[] { existing }, TreeWindows(workspace, desktop));
        }

        private (
            TilingWorkspace Workspace,
            MasterSatelliteRuntimeLifecycle Lifecycle,
            IVirtualDesktop Desktop,
            IDisplay Display,
            MasterSatelliteLayoutSettings Settings) CreateActiveEmpty(
                int maxSatellites,
                Rectangle? workArea = null)
        {
            var area = workArea ?? WorkArea;
            var workspace = new TilingWorkspace();
            var desktop = CreateDesktop("D1");
            var display = CreateDisplay(area);
            var lifecycle = new MasterSatelliteRuntimeLifecycle(display);
            var settings = CreateSettings(enabled: true, maxSatellites);
            workspace.RegisterDesktop(desktop, area, PanelOrientation.Horizontal);

            var activation = lifecycle.ApplySettings(workspace, settings, display, desktop);
            Assert.IsTrue(activation.StateAdded, activation.Operation?.Message);
            Assert.IsTrue(activation.Operation!.Succeeded, activation.Operation.Message);
            Assert.IsTrue(activation.Invariant!.IsValid, activation.Invariant.Description);

            return (workspace, lifecycle, desktop, display, settings);
        }

        private static MasterSatelliteLayoutSettings CreateSettings(
            bool enabled,
            int maxSatellites)
        {
            return new MasterSatelliteLayoutSettings
            {
                Enabled = enabled,
                DisplayScope = AlgorithmicLayoutDisplayScope.AllDisplays,
                MaxSatellites = maxSatellites,
            };
        }

        private IWindow[] CreateWindows(int count)
        {
            return Enumerable.Range(0, count)
                .Select(index => m_windows.Create($"W{index}"))
                .ToArray();
        }

        private static IVirtualDesktop CreateDesktop(string name)
        {
            var mock = new Mock<IVirtualDesktop>(MockBehavior.Loose);
            mock.SetupGet(desktop => desktop.Name).Returns(name);
            mock.SetupGet(desktop => desktop.IsAlive).Returns(true);
            return mock.Object;
        }

        private static IDisplay CreateDisplay(Rectangle workArea)
        {
            var mock = new Mock<IDisplay>(MockBehavior.Loose);
            var display = mock.Object;
            mock.SetupGet(candidate => candidate.WorkArea).Returns(workArea);
            mock.Setup(candidate => candidate.Equals(It.IsAny<IDisplay>()))
                .Returns((IDisplay other) => ReferenceEquals(display, other));
            return display;
        }

        private static IWindow[] TreeWindows(
            TilingWorkspace workspace,
            IVirtualDesktop desktop)
        {
            return workspace.GetTree(desktop)!.Root!.Windows
                .Select(node => node.WindowReference)
                .ToArray();
        }

        private static void AssertLocalSuccess(
            MasterSatelliteLocalMutationResult result,
            MasterSatelliteLocalMutationDisposition expectedDisposition)
        {
            Assert.AreEqual(expectedDisposition, result.Disposition);
            Assert.IsTrue(result.Succeeded, result.Operation?.Message);
            Assert.IsTrue(result.Operation!.Succeeded, result.Operation.Message);
            Assert.IsTrue(result.Invariant!.IsValid, result.Invariant.Description);
        }

        private static void AssertValid(
            TilingWorkspace workspace,
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState state,
            MasterSatelliteLayoutSettings settings)
        {
            var invariant = workspace.ValidateMasterSatelliteLayout(desktop, state, settings);
            Assert.IsTrue(invariant.IsValid, invariant.Description);
        }

        private static void AssertRejectedWithoutMutation(
            MasterSatelliteLocalMutationResult rejected,
            MasterSatelliteFailureReason expectedReason,
            TilingWorkspace workspace,
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState state,
            MasterSatelliteLayoutSettings settings,
            long expectedRevision,
            PanelNode expectedRoot,
            TilingNode expectedFocus,
            IWindow[] existingWindows,
            TilingNode[] expectedNodes,
            IWindow rejectedWindow)
        {
            Assert.AreEqual(MasterSatelliteLocalMutationDisposition.Rejected, rejected.Disposition);
            Assert.IsFalse(rejected.Succeeded);
            Assert.IsFalse(rejected.Operation!.Succeeded);
            Assert.AreEqual(expectedReason, rejected.Operation.FailureReason);
            Assert.AreEqual(expectedRevision, state.Revision);
            Assert.AreSame(expectedRoot, workspace.GetTree(desktop)!.Root);
            Assert.AreSame(expectedFocus, workspace.GetFocus(desktop));
            CollectionAssert.AreEqual(existingWindows, TreeWindows(workspace, desktop));
            CollectionAssert.AreEqual(
                expectedNodes,
                existingWindows.Select(window => workspace.GetTree(desktop)!.FindNode(window)).ToArray());
            Assert.IsNull(workspace.GetTree(desktop)!.FindNode(rejectedWindow));
            Assert.IsFalse(workspace.TryGetOriginalPosition(rejectedWindow, out _));
            AssertValid(workspace, desktop, state, settings);
        }
    }
}
