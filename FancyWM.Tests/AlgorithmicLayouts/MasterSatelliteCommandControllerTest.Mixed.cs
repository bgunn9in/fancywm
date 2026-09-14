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
    public partial class MasterSatelliteCommandControllerTest
    {
        [DataTestMethod]
        [DataRow(MasterSide.Left)]
        [DataRow(MasterSide.Right)]
        public void MixedCommandsExchangeOnlyTwoSlotsAndPreserveFocus(MasterSide side)
        {
            var moves = new[] { (0, TilingDirection.Down, 2), (1, TilingDirection.Down, 2),
                (2, TilingDirection.Up, 0), (0, TilingDirection.Right, 1), (1, TilingDirection.Left, 0) };
            foreach (var (from, direction, to) in moves)
            {
                var context = CreateMixed(side);
                var state = GetState(context);
                var before = state.Satellites.ToArray();
                var tree = context.Workspace.GetTree(context.Desktop)!;
                var slots = before.Select(w => tree.FindNode(w)!.ComputedRectangle).ToArray();
                var nodes = context.Windows.Select(tree.FindNode).ToArray();
                var source = before[from];
                context.Workspace.SetFocus(source);
                Assert.IsTrue(context.Controller.CanMoveFocusedWindow(context.Workspace, context.Desktop, direction));
                AssertApplied(context.Controller.MoveFocusedWindow(context.Workspace, context.Desktop, direction));
                (before[from], before[to]) = (before[to], before[from]);
                CollectionAssert.AreEqual(before, state.Satellites.ToArray());
                CollectionAssert.AreEqual(slots, state.Satellites.Select(w => tree.FindNode(w)!.ComputedRectangle).ToArray());
                CollectionAssert.AreEqual(nodes, context.Windows.Select(tree.FindNode).ToArray());
                Assert.AreSame(context.Windows[0], state.Master);
                Assert.AreEqual(side, state.MasterSide);
                Assert.AreEqual(0.6, state.RequestedMasterRatio);
                AssertFocusedWindow(context, source);
            }
        }

        [DataTestMethod]
        [DataRow(MasterSide.Left)]
        [DataRow(MasterSide.Right)]
        public void MixedMasterPromotionUsesImmediateNeighborAndExactFreedSlot(MasterSide side)
        {
            for (int index = 0; index < 3; index++)
            {
                var context = CreateMixed(side);
                var state = GetState(context);
                var master = state.Master!;
                var before = state.Satellites.ToArray();
                var source = before[index];
                var tree = context.Workspace.GetTree(context.Desktop)!;
                var slot = tree.FindNode(source)!.ComputedRectangle;
                var direction = side == MasterSide.Left ? TilingDirection.Left : TilingDirection.Right;
                context.Workspace.SetFocus(source);
                Assert.IsTrue(context.Controller.CanMoveFocusedWindow(context.Workspace, context.Desktop, direction));
                AssertApplied(context.Controller.MoveFocusedWindow(context.Workspace, context.Desktop, direction));
                if (index == 2 || index == (side == MasterSide.Left ? 0 : 1))
                {
                    Assert.AreSame(source, state.Master);
                    before[index] = master;
                    Assert.AreEqual(slot, tree.FindNode(master)!.ComputedRectangle);
                }
                else
                {
                    Assert.AreSame(master, state.Master);
                    (before[0], before[1]) = (before[1], before[0]);
                }
                CollectionAssert.AreEqual(before, state.Satellites.ToArray());
                AssertFocusedWindow(context, source);
                Assert.AreEqual(side, state.MasterSide);
            }
        }

        [DataTestMethod]
        [DataRow(MasterSide.Left)]
        [DataRow(MasterSide.Right)]
        public void MixedMasterChangesSideWithoutChangingInternalSlotsAndEdgesAreUnavailable(MasterSide side)
        {
            var context = CreateMixed(side);
            var state = GetState(context);
            var before = state.Satellites.ToArray();
            var master = state.Master!;
            context.Workspace.SetFocus(master);
            AssertApplied(context.Controller.MoveFocusedWindow(context.Workspace, context.Desktop,
                side == MasterSide.Left ? TilingDirection.Right : TilingDirection.Left));
            Assert.AreSame(master, state.Master);
            CollectionAssert.AreEqual(before, state.Satellites.ToArray());
            AssertFocusedWindow(context, master);
            var edges = new[] { (0, TilingDirection.Up), (1, TilingDirection.Up), (2, TilingDirection.Down),
                (state.MasterSide == MasterSide.Left ? 1 : 0,
                    state.MasterSide == MasterSide.Left ? TilingDirection.Right : TilingDirection.Left) };
            foreach (var (index, direction) in edges)
            {
                context.Workspace.SetFocus(before[index]);
                long revision = state.Revision;
                Assert.IsFalse(context.Controller.CanMoveFocusedWindow(context.Workspace, context.Desktop, direction));
                Assert.IsFalse(context.Controller.MoveFocusedWindow(context.Workspace, context.Desktop, direction).Changed);
                Assert.AreEqual(revision, state.Revision);
            }
        }

        [TestMethod]
        public void SlotToggleMatchesMoveUpAndRejectsOtherLayoutsAndMaster()
        {
            for (int index = 0; index < 3; index++)
            {
                var context = CreateMixed(MasterSide.Left);
                var state = GetState(context);
                var before = state.Satellites.ToArray();
                context.Workspace.SetFocus(before[index]);
                Assert.IsTrue(context.Controller.CanToggleFocusedSatelliteSlot(context.Workspace, context.Desktop));
                AssertApplied(context.Controller.ToggleFocusedSatelliteSlot(context.Workspace, context.Desktop));
                int target = index == 2 ? 0 : 2;
                (before[index], before[target]) = (before[target], before[index]);
                CollectionAssert.AreEqual(before, state.Satellites.ToArray());
                context.Workspace.SetFocus(state.Master!);
                Assert.IsFalse(context.Controller.CanToggleFocusedSatelliteSlot(context.Workspace, context.Desktop));
                Assert.IsFalse(context.Controller.ToggleFocusedSatelliteSlot(context.Workspace, context.Desktop).Changed);
                context.Workspace.SetMixedSatellites(context.Desktop, state, context.Settings, false);
                context.Workspace.SetFocus(before[0]);
                Assert.IsFalse(context.Controller.CanToggleFocusedSatelliteSlot(context.Workspace, context.Desktop));
                Assert.IsFalse(context.Controller.ToggleFocusedSatelliteSlot(context.Workspace, context.Desktop).Changed);
            }
        }

        [TestMethod]
        public void MixedExchangeRejectsMinimumSizeConflictWithoutPartialMutation()
        {
            var context = CreateMixed(MasterSide.Right);
            var state = GetState(context);
            var before = state.Satellites.ToArray();
            var tree = context.Workspace.GetTree(context.Desktop)!;
            Mock.Get(before[2]).SetupGet(w => w.MinSize).Returns(new Point(300, 50));
            context.Workspace.RelayoutMasterSatelliteLayout(context.Desktop, state, context.Settings);
            var root = tree.Root;
            var revision = state.Revision;
            context.Workspace.SetFocus(before[2]);
            Assert.IsFalse(context.Controller.CanMoveFocusedWindow(context.Workspace, context.Desktop, TilingDirection.Up));
            Assert.IsFalse(context.Controller.CanToggleFocusedSatelliteSlot(context.Workspace, context.Desktop));
            var result = context.Controller.ToggleFocusedSatelliteSlot(context.Workspace, context.Desktop);
            Assert.AreEqual(MasterSatelliteFailureReason.MinSizeConflict, result.FailureReason);
            Assert.AreEqual(revision, state.Revision);
            Assert.AreSame(root, tree.Root);
            CollectionAssert.AreEqual(before, state.Satellites.ToArray());
            AssertFocusedWindow(context, before[2]);
        }

        [TestMethod]
        public void WideSlotMoveUpUsesCloserUpperWindowWhenTheirWidthsDiffer()
        {
            var context = CreateMixed(MasterSide.Left);
            var state = GetState(context);
            var tree = context.Workspace.GetTree(context.Desktop)!;
            var source = state.Satellites[2];
            var before = state.Satellites.ToArray();
            var upper = (SplitPanelNode)tree.FindNode(before[0])!.Parent!;
            Assert.IsTrue(upper.ResizeTo(upper.Children[0], 100, FancyWM.Layouts.GrowDirection.Both));
            tree.Arrange();
            context.Workspace.SetFocus(source);
            Assert.IsTrue(context.Controller.CanToggleFocusedSatelliteSlot(context.Workspace, context.Desktop));
            AssertApplied(context.Controller.ToggleFocusedSatelliteSlot(context.Workspace, context.Desktop));
            CollectionAssert.AreEqual(new[] { before[0], before[2], before[1] }, state.Satellites.ToArray());
        }

        private ActiveContext CreateMixed(MasterSide side)
        {
            var context = CreateActive(CreateWindows(4));
            var state = GetState(context);
            Assert.IsTrue(context.Workspace.SetMasterSatelliteSide(context.Desktop, state, context.Settings, side).Succeeded);
            AssertApplied(new MasterSatelliteCommandController(context.Lifecycle).ToggleSatelliteOrientation(
                context.Workspace, context.Desktop));
            Assert.IsTrue(context.Workspace.SetMixedSatellites(context.Desktop, state, context.Settings, true).Succeeded);
            return context;
        }
    }
}
