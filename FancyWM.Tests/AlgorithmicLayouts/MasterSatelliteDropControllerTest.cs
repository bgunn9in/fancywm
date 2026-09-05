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
    public class MasterSatelliteDropControllerTest
    {
        private readonly UniqueWindowMockFactory m_windows = new();
        private readonly Rectangle m_workArea = Rectangle.OffsetAndSize(0, 0, 1000, 600);

        [TestMethod]
        public void VerticalSatelliteDropPlansOnCloneThenReordersAfterTarget()
        {
            var context = CreateActive(CreateWindows(4));
            var state = GetState(context);
            var beforeTree = Snapshot(context);
            long beforeRevision = state.Revision;
            var source = context.Windows[1];
            var targetRectangle = RectangleFor(context, context.Windows[3]);

            var plan = context.Controller.CreateWindowDropPlan(
                context.Workspace,
                context.Desktop,
                state,
                context.Settings,
                source,
                new Point(targetRectangle.Center.X, targetRectangle.Bottom - 1));

            Assert.IsTrue(plan.IsAccepted, plan.Message);
            Assert.AreEqual(MasterSatelliteDropKind.ReorderSatellite, plan.Kind);
            Assert.AreEqual(0, plan.FromSatelliteIndex);
            Assert.AreEqual(2, plan.ToSatelliteIndex);
            Assert.IsNotNull(plan.PreviewRectangle);
            CollectionAssert.AreEqual(
                new[] { source, context.Windows[3] },
                plan.PreviewWindows.ToArray());
            Assert.AreEqual(beforeRevision, state.Revision, "Preview changed live state.");
            Assert.AreEqual(beforeTree, Snapshot(context), "Preview changed live tree.");
            CollectionAssert.AreEqual(
                context.Windows.Skip(1).ToArray(),
                state.Satellites.ToArray());
            var expectedPreviewRectangle = plan.PreviewRectangle;

            var result = context.Controller.Apply(
                context.Workspace,
                context.Desktop,
                state,
                context.Settings,
                plan);

            AssertApplied(result);
            CollectionAssert.AreEqual(
                new[] { context.Windows[2], context.Windows[3], source },
                state.Satellites.ToArray());
            Assert.AreEqual(expectedPreviewRectangle, RectangleFor(context, source));
            AssertInvariant(context);
        }

        [TestMethod]
        public void CommitReplansAtCurrentPointerInsteadOfApplyingStalePreview()
        {
            var context = CreateActive(CreateWindows(4));
            var state = GetState(context);
            var originalMaster = state.Master!;
            var source = context.Windows[1];
            var previewPoint = RectangleFor(context, originalMaster).Center;
            var currentTarget = RectangleFor(context, context.Windows[3]);
            var currentPoint = new Point(
                currentTarget.Center.X,
                currentTarget.Bottom - 1);

            // The last rendered preview targets A (promotion). Mouse-up then
            // observes B before another asynchronous preview/layout tick runs.
            var stalePreview = context.Controller.CreateWindowDropPlan(
                context.Workspace,
                context.Desktop,
                state,
                context.Settings,
                source,
                previewPoint);
            Assert.IsTrue(stalePreview.IsAccepted, stalePreview.Message);
            Assert.AreEqual(
                MasterSatelliteDropKind.PromoteSourceSatellite,
                stalePreview.Kind);

            var result = context.Controller.ApplyWindowDropAtPointer(
                context.Workspace,
                context.Desktop,
                state,
                context.Settings,
                source,
                currentPoint);

            AssertApplied(result);
            Assert.AreSame(originalMaster, state.Master);
            CollectionAssert.AreEqual(
                new[] { context.Windows[2], context.Windows[3], source },
                state.Satellites.ToArray());
            AssertInvariant(context);
        }

        [TestMethod]
        public void HorizontalSatelliteDropUsesHorizontalAxisAndMovesBeforeTarget()
        {
            var context = CreateActive(CreateWindows(4));
            var state = GetState(context);
            Assert.IsTrue(context.Workspace.SetMasterSatelliteOrientation(
                context.Desktop,
                state,
                context.Settings,
                SatelliteLayoutOrientation.Horizontal).Succeeded);
            var source = context.Windows[3];
            var targetRectangle = RectangleFor(context, context.Windows[1]);

            var plan = context.Controller.CreateWindowDropPlan(
                context.Workspace,
                context.Desktop,
                state,
                context.Settings,
                source,
                new Point(targetRectangle.Left + 1, targetRectangle.Center.Y));
            var result = context.Controller.Apply(
                context.Workspace,
                context.Desktop,
                state,
                context.Settings,
                plan);

            Assert.IsTrue(plan.IsAccepted, plan.Message);
            Assert.AreEqual(2, plan.FromSatelliteIndex);
            Assert.AreEqual(0, plan.ToSatelliteIndex);
            AssertApplied(result);
            CollectionAssert.AreEqual(
                new[] { source, context.Windows[1], context.Windows[2] },
                state.Satellites.ToArray());
            AssertInvariant(context);
        }

        [TestMethod]
        public void SatelliteDroppedOnMasterIsPromotedAndOldMasterUsesExactSlot()
        {
            var context = CreateActive(CreateWindows(4));
            var state = GetState(context);
            var source = context.Windows[2];
            var oldMaster = state.Master;
            var plan = PlanAtWindow(context, source, oldMaster!);

            Assert.AreEqual(MasterSatelliteDropKind.PromoteSourceSatellite, plan.Kind);
            Assert.AreSame(oldMaster, state.Master, "Preview must not promote the live state.");
            var expectedPreviewRectangle = plan.PreviewRectangle;
            var result = Apply(context, plan);

            AssertApplied(result);
            Assert.AreSame(source, state.Master);
            CollectionAssert.AreEqual(
                new[] { context.Windows[1], oldMaster, context.Windows[3] },
                state.Satellites.ToArray());
            Assert.AreEqual(expectedPreviewRectangle, RectangleFor(context, source));
            AssertInvariant(context);
        }

        [TestMethod]
        public void MasterDroppedOnSatellitePromotesTargetAndMovesMasterToThatSlot()
        {
            var context = CreateActive(CreateWindows(4));
            var state = GetState(context);
            var oldMaster = state.Master!;
            var target = context.Windows[2];
            var plan = PlanAtWindow(context, oldMaster, target);

            Assert.AreEqual(MasterSatelliteDropKind.PromoteTargetSatellite, plan.Kind);
            var expectedPreviewRectangle = plan.PreviewRectangle;
            var result = Apply(context, plan);

            AssertApplied(result);
            Assert.AreSame(target, state.Master);
            CollectionAssert.AreEqual(
                new[] { context.Windows[1], oldMaster, context.Windows[3] },
                state.Satellites.ToArray());
            Assert.AreEqual(expectedPreviewRectangle, RectangleFor(context, oldMaster));
            AssertInvariant(context);
        }

        [TestMethod]
        public void MasterCrossingCentralBoundaryChangesSideAndKeepsOrderAndRatio()
        {
            var context = CreateActive(CreateWindows(4));
            var state = GetState(context);
            var source = state.Master!;
            var masterRectangle = RectangleFor(context, source);
            var satellites = state.Satellites.ToArray();
            double ratio = state.RequestedMasterRatio;
            var plan = context.Controller.CreateWindowDropPlan(
                context.Workspace,
                context.Desktop,
                state,
                context.Settings,
                source,
                new Point(masterRectangle.Right + 1, masterRectangle.Center.Y));

            Assert.IsTrue(plan.IsAccepted, plan.Message);
            Assert.AreEqual(MasterSatelliteDropKind.ChangeMasterSide, plan.Kind);
            Assert.AreEqual(MasterSide.Right, plan.TargetMasterSide);
            Assert.AreEqual(MasterSide.Left, state.MasterSide, "Preview mutated live side.");
            var expectedPreviewRectangle = plan.PreviewRectangle;
            var result = Apply(context, plan);

            AssertApplied(result);
            Assert.AreEqual(MasterSide.Right, state.MasterSide);
            Assert.AreEqual(ratio, state.RequestedMasterRatio, 0.0001);
            CollectionAssert.AreEqual(satellites, state.Satellites.ToArray());
            Assert.AreEqual(expectedPreviewRectangle, RectangleFor(context, source));
            AssertInvariant(context);
        }

        [TestMethod]
        public void OutsideAndPanelDropsAreRejectedWithoutMutatingCanonicalTree()
        {
            var context = CreateActive(CreateWindows(3));
            var state = GetState(context);
            var before = Snapshot(context);
            long revision = state.Revision;

            var outside = context.Controller.CreateWindowDropPlan(
                context.Workspace,
                context.Desktop,
                state,
                context.Settings,
                state.Master!,
                new Point(m_workArea.Right + 50, m_workArea.Bottom + 50));
            var outsideResult = Apply(context, outside);
            var satellitePanel = context.Workspace.GetTree(context.Desktop)!.Root!
                .Children.OfType<SplitPanelNode>().Single();
            var panel = context.Controller.CreatePanelDropRejection(
                context.Desktop,
                state,
                satellitePanel);
            var panelResult = Apply(context, panel);
            var ownRectangle = RectangleFor(context, context.Windows[1]);
            var ownSlot = context.Controller.CreateWindowDropPlan(
                context.Workspace,
                context.Desktop,
                state,
                context.Settings,
                context.Windows[1],
                ownRectangle.Center);
            var ownSlotResult = Apply(context, ownSlot);

            Assert.IsFalse(outside.IsAccepted);
            Assert.IsFalse(outsideResult.Succeeded);
            Assert.AreEqual(MasterSatelliteFailureReason.UnsupportedOperation, outsideResult.FailureReason);
            Assert.IsFalse(panel.IsAccepted);
            Assert.IsFalse(panelResult.Succeeded);
            Assert.AreEqual(MasterSatelliteFailureReason.UnsupportedOperation, panelResult.FailureReason);
            Assert.IsFalse(ownSlot.IsAccepted);
            Assert.IsFalse(ownSlotResult.Succeeded);
            Assert.AreEqual(MasterSatelliteFailureReason.UnsupportedOperation, ownSlotResult.FailureReason);
            Assert.AreEqual(revision, state.Revision);
            Assert.AreEqual(before, Snapshot(context));
            AssertInvariant(context);
        }

        [TestMethod]
        public void StalePreviewIsRejectedWithoutApplyingToNewRevision()
        {
            var context = CreateActive(CreateWindows(4));
            var state = GetState(context);
            var source = context.Windows[1];
            var plan = PlanAtWindow(context, source, context.Windows[3], afterCenter: true);
            Assert.IsTrue(plan.IsAccepted, plan.Message);
            Assert.IsTrue(context.Workspace.SetMasterSatelliteSide(
                context.Desktop,
                state,
                context.Settings,
                MasterSide.Right).Succeeded);
            var beforeApply = Snapshot(context);
            long revision = state.Revision;

            var result = Apply(context, plan);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(MasterSatelliteFailureReason.InvalidCanonicalTree, result.FailureReason);
            Assert.AreEqual(revision, state.Revision);
            Assert.AreEqual(beforeApply, Snapshot(context));
            CollectionAssert.AreEqual(
                context.Windows.Skip(1).ToArray(),
                state.Satellites.ToArray());
            AssertInvariant(context);
        }

        [TestMethod]
        public void ImpossiblePromotionPreviewIsRejectedAndLeavesOriginalSlot()
        {
            var windows = new[]
            {
                m_windows.Create("A", minimumWidth: 100, minimumHeight: 500),
                m_windows.Create("B", minimumWidth: 100, minimumHeight: 10),
                m_windows.Create("C", minimumWidth: 100, minimumHeight: 10),
                m_windows.Create("D", minimumWidth: 100, minimumHeight: 10),
            };
            var context = CreateActive(windows);
            var state = GetState(context);
            var before = Snapshot(context);
            long revision = state.Revision;
            var plan = PlanAtWindow(context, windows[2], windows[0]);

            Assert.IsFalse(plan.IsAccepted);
            Assert.AreEqual(MasterSatelliteFailureReason.MinSizeConflict, plan.FailureReason);
            Assert.IsNotNull(plan.PreviewOperation);
            Assert.IsFalse(plan.PreviewOperation.Succeeded);
            var result = Apply(context, plan);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(MasterSatelliteFailureReason.MinSizeConflict, result.FailureReason);
            Assert.AreEqual(revision, state.Revision);
            Assert.AreSame(windows[0], state.Master);
            CollectionAssert.AreEqual(windows.Skip(1).ToArray(), state.Satellites.ToArray());
            Assert.AreEqual(before, Snapshot(context));
            AssertInvariant(context);
        }

        [TestMethod]
        public void CommitFailureAfterAcceptedPreviewRollsBackOriginalSlots()
        {
            var context = CreateActive(CreateWindows(4));
            var state = GetState(context);
            var oldMaster = state.Master!;
            var source = context.Windows[2];
            var plan = PlanAtWindow(context, source, oldMaster);
            Assert.IsTrue(plan.IsAccepted, plan.Message);
            var before = Snapshot(context);
            long revision = state.Revision;

            // Simulate WM_GETMINMAXINFO changing between pointer preview and mouse-up.
            Mock.Get(oldMaster).SetupGet(window => window.MinSize)
                .Returns(new Point(100, 500));
            var result = Apply(context, plan);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(MasterSatelliteFailureReason.MinSizeConflict, result.FailureReason);
            Assert.AreEqual(revision, state.Revision);
            Assert.AreSame(oldMaster, state.Master);
            CollectionAssert.AreEqual(
                context.Windows.Skip(1).ToArray(),
                state.Satellites.ToArray());
            Assert.AreEqual(before, Snapshot(context));
            AssertInvariant(context);
        }

        private MasterSatelliteDropPlan PlanAtWindow(
            ActiveContext context,
            IWindow source,
            IWindow target,
            bool afterCenter = false)
        {
            var rectangle = RectangleFor(context, target);
            var state = GetState(context);
            var point = state.SatelliteOrientation == SatelliteLayoutOrientation.Vertical
                ? new Point(rectangle.Center.X, afterCenter ? rectangle.Bottom - 1 : rectangle.Center.Y)
                : new Point(afterCenter ? rectangle.Right - 1 : rectangle.Center.X, rectangle.Center.Y);
            return context.Controller.CreateWindowDropPlan(
                context.Workspace,
                context.Desktop,
                state,
                context.Settings,
                source,
                point);
        }

        private MasterSatelliteCommandResult Apply(
            ActiveContext context,
            MasterSatelliteDropPlan plan)
        {
            return context.Controller.Apply(
                context.Workspace,
                context.Desktop,
                GetState(context),
                context.Settings,
                plan);
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
            var displayMock = new Mock<IDisplay>(MockBehavior.Loose);
            var display = displayMock.Object;
            displayMock.SetupGet(candidate => candidate.WorkArea).Returns(m_workArea);
            displayMock.SetupGet(candidate => candidate.Scaling).Returns(1.0);
            displayMock.Setup(candidate => candidate.Equals(It.IsAny<IDisplay>()))
                .Returns((IDisplay other) => ReferenceEquals(display, other));
            var desktopMock = new Mock<IVirtualDesktop>(MockBehavior.Loose);
            var desktop = desktopMock.Object;
            desktopMock.SetupGet(candidate => candidate.Name).Returns("D1");
            desktopMock.SetupGet(candidate => candidate.IsAlive).Returns(true);

            var lifecycle = new MasterSatelliteRuntimeLifecycle(display);
            var workspace = new TilingWorkspace();
            workspace.RegisterDesktop(desktop, m_workArea, PanelOrientation.Horizontal);
            var root = workspace.GetTree(desktop)!.Root!;
            foreach (var window in windows)
            {
                workspace.RegisterWindow(window, root);
            }
            var activation = lifecycle.ApplySettings(workspace, settings, display, desktop);
            Assert.IsTrue(activation.Operation!.Succeeded, activation.Operation.Message);
            Assert.IsTrue(lifecycle.TryGetState(desktop, out _));
            return new ActiveContext(
                workspace,
                lifecycle,
                new MasterSatelliteDropController(),
                desktop,
                settings,
                windows);
        }

        private static MasterSatelliteRuntimeState GetState(ActiveContext context)
        {
            Assert.IsTrue(context.Lifecycle.TryGetState(context.Desktop, out var state));
            return state;
        }

        private static Rectangle RectangleFor(ActiveContext context, IWindow window)
        {
            var tree = context.Workspace.GetTree(context.Desktop)!;
            tree.Measure();
            tree.Arrange();
            return tree.FindNode(window)!.ComputedRectangle;
        }

        private static string Snapshot(ActiveContext context)
        {
            Assert.IsTrue(context.Workspace.TryGetMasterSatelliteSnapshot(
                context.Desktop,
                GetState(context),
                out var snapshot));
            return snapshot.TreeDescription;
        }

        private static void AssertApplied(MasterSatelliteCommandResult result)
        {
            Assert.IsTrue(result.Succeeded, result.Message);
            Assert.IsTrue(result.Changed);
            Assert.AreEqual(MasterSatelliteCommandDisposition.Applied, result.Disposition);
            Assert.IsTrue(result.Invariant!.IsValid, result.Invariant.Description);
        }

        private static void AssertInvariant(ActiveContext context)
        {
            var invariant = context.Workspace.ValidateMasterSatelliteLayout(
                context.Desktop,
                GetState(context),
                context.Settings);
            Assert.IsTrue(invariant.IsValid, invariant.Description);
        }

        private IWindow[] CreateWindows(int count)
        {
            return Enumerable.Range(0, count)
                .Select(index => m_windows.Create(((char)('A' + index)).ToString()))
                .ToArray();
        }

        private sealed record class ActiveContext(
            TilingWorkspace Workspace,
            MasterSatelliteRuntimeLifecycle Lifecycle,
            MasterSatelliteDropController Controller,
            IVirtualDesktop Desktop,
            MasterSatelliteLayoutSettings Settings,
            IWindow[] Windows);
    }
}
