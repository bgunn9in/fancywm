using System;
using System.Linq;
using FancyWM.AlgorithmicLayouts;
using FancyWM.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    public partial class MasterSatelliteDropControllerTest
    {
        [DataTestMethod]
        [DataRow(MasterSide.Left)]
        [DataRow(MasterSide.Right)]
        public void MixedDropOnAnyPartOfTargetExchangesExactlyTwoSlots(MasterSide side)
        {
            for (int from = 0; from < 3; from++)
            for (int to = 0; to < 3; to++)
            foreach (bool nearStart in new[] { false, true })
            {
                if (from == to) continue;
                var context = CreateActive(CreateWindows(4));
                var state = GetState(context);
                context.Workspace.SetMasterSatelliteSide(context.Desktop, state, context.Settings, side);
                Assert.IsTrue(context.Workspace.SetMixedSatellites(context.Desktop, state, context.Settings, true).Succeeded);
                var before = state.Satellites.ToArray();
                var slots = before.Select(w => RectangleFor(context, w)).ToArray();
                var target = slots[to];
                var pointer = nearStart ? new Point(target.Left + 1, target.Top + 1)
                    : new Point(target.Right - 1, target.Bottom - 1);
                var source = before[from];
                context.Workspace.SetFocus(source);
                var revision = state.Revision;
                var plan = context.Controller.CreateWindowDropPlan(context.Workspace, context.Desktop,
                    state, context.Settings, source, pointer);
                var repeated = context.Controller.CreateWindowDropPlan(context.Workspace, context.Desktop,
                    state, context.Settings, source, pointer);
                Assert.IsTrue(plan.IsAccepted, plan.Message);
                Assert.AreSame(plan, repeated);
                Assert.AreEqual(to, plan.ToSatelliteIndex);
                Assert.AreEqual(slots[to], plan.PreviewRectangle);
                Assert.AreEqual(revision, state.Revision);
                AssertApplied(context.Controller.ApplyWindowDropAtPointer(context.Workspace, context.Desktop,
                    state, context.Settings, source, pointer));
                (before[from], before[to]) = (before[to], before[from]);
                CollectionAssert.AreEqual(before, state.Satellites.ToArray());
                CollectionAssert.AreEqual(slots, state.Satellites.Select(w => RectangleFor(context, w)).ToArray());
                Assert.AreEqual(plan.PreviewRectangle, RectangleFor(context, source));
                Assert.AreSame(source, ((FancyWM.Layouts.Tiling.WindowNode)context.Workspace.GetFocus(context.Desktop)!).WindowReference);
                AssertInvariant(context);
            }
        }

        [DataTestMethod]
        [DataRow(MasterSide.Left)]
        [DataRow(MasterSide.Right)]
        public void MixedMasterDropsPreserveRoleAndSatelliteDropsPromoteExactSlot(MasterSide side)
        {
            for (int index = 0; index < 3; index++)
            {
                var context = CreateActive(CreateWindows(4));
                var state = GetState(context);
                context.Workspace.SetMasterSatelliteSide(context.Desktop, state, context.Settings, side);
                context.Workspace.SetMixedSatellites(context.Desktop, state, context.Settings, true);
                var before = state.Satellites.ToArray();
                var master = state.Master!;
                var plan = PlanAtWindow(context, master, before[index]);
                Assert.AreEqual(MasterSatelliteDropKind.ChangeMasterSide, plan.Kind);
                AssertApplied(Apply(context, plan));
                Assert.AreSame(master, state.Master);
                CollectionAssert.AreEqual(before, state.Satellites.ToArray());
                Assert.AreEqual(plan.PreviewRectangle, RectangleFor(context, master));
                var slot = RectangleFor(context, before[index]);
                plan = PlanAtWindow(context, before[index], master);
                Assert.AreEqual(MasterSatelliteDropKind.PromoteSourceSatellite, plan.Kind);
                AssertApplied(Apply(context, plan));
                Assert.AreSame(before[index], state.Master);
                before[index] = master;
                CollectionAssert.AreEqual(before, state.Satellites.ToArray());
                Assert.AreEqual(slot, RectangleFor(context, master));
                AssertInvariant(context);
            }
        }

        [TestMethod]
        public void MixedDropRechecksMinimumSizeAndRejectsStaleSettingsPreview()
        {
            var context = CreateActive(CreateWindows(4));
            var state = GetState(context);
            context.Workspace.SetMixedSatellites(context.Desktop, state, context.Settings, true);
            var source = state.Satellites[2];
            var before = state.Satellites.ToArray();
            var plan = PlanAtWindow(context, source, before[0]);
            Assert.IsTrue(plan.IsAccepted);
            Mock.Get(source).SetupGet(w => w.MinSize).Returns(new Point(300, 40));
            var result = Apply(context, plan);
            Assert.AreEqual(MasterSatelliteFailureReason.MinSizeConflict, result.FailureReason);
            CollectionAssert.AreEqual(before, state.Satellites.ToArray());
            Mock.Get(source).SetupGet(w => w.MinSize).Returns(new Point(0, 0));
            context.Workspace.SetMixedSatellites(context.Desktop, state, context.Settings, false);
            Assert.AreEqual(MasterSatelliteFailureReason.InvalidCanonicalTree, Apply(context, plan).FailureReason);
        }
    }
}
