#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

using FancyWM.AlgorithmicLayouts;
using FancyWM.Models;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    [TestClass]
    public class MasterSatelliteCapacityTransitionPlannerTest
    {
        [TestMethod]
        public void ActivationKeepsFocusedMasterAndProcessesExtrasFromVisualEnd()
        {
            var windows = Enumerable.Range(0, 6)
                .Select(CreateWindow)
                .ToArray();

            var plan = MasterSatelliteCapacityTransitionPlanner.PlanActivation(
                windows,
                windows[2],
                maxSatellites: 2);

            Assert.AreSame(windows[2], plan.Master);
            CollectionAssert.AreEqual(
                new[] { windows[0], windows[1] },
                plan.RetainedSatellites.ToArray());
            CollectionAssert.AreEqual(
                new[] { windows[5], windows[4], windows[3] },
                plan.ExtrasFromEnd.ToArray());
            CollectionAssert.AreEqual(
                new[] { windows[2], windows[0], windows[1] },
                plan.AdmittedWindows.ToArray());
        }

        [TestMethod]
        public void ActivationFocusFallsBackToWorkspaceOnlyForATiledWindow()
        {
            var windows = Enumerable.Range(0, 3)
                .Select(CreateWindow)
                .ToArray();
            var staleLayoutFocus = CreateWindow(10);
            var unrelatedWorkspaceFocus = CreateWindow(11);

            var focused = MasterSatelliteCapacityTransitionPlanner
                .ResolveActivationFocus(
                    windows,
                    staleLayoutFocus,
                    windows[1]);
            var ignored = MasterSatelliteCapacityTransitionPlanner
                .ResolveActivationFocus(
                    windows,
                    staleLayoutFocus,
                    unrelatedWorkspaceFocus);

            Assert.AreSame(windows[1], focused);
            Assert.IsNull(ignored);
            var plan = MasterSatelliteCapacityTransitionPlanner.PlanActivation(
                windows,
                focused,
                maxSatellites: 2);
            Assert.AreSame(windows[1], plan.Master);
        }

        [TestMethod]
        public void ShrinkNeverChangesMasterOrRetainedSatelliteOrder()
        {
            var master = CreateWindow(10);
            var satellites = Enumerable.Range(11, 5)
                .Select(CreateWindow)
                .ToArray();

            var plan = MasterSatelliteCapacityTransitionPlanner.PlanShrink(
                master,
                satellites,
                maxSatellites: 2);

            Assert.AreSame(master, plan.Master);
            CollectionAssert.AreEqual(
                satellites.Take(2).ToArray(),
                plan.RetainedSatellites.ToArray());
            CollectionAssert.AreEqual(
                satellites.Skip(2).Reverse().ToArray(),
                plan.ExtrasFromEnd.ToArray());
        }

        [TestMethod]
        public void IncreaseProducesNoExtrasAndDoesNotBackfillAnything()
        {
            var master = CreateWindow(20);
            var satellites = new[] { CreateWindow(21), CreateWindow(22) };

            var plan = MasterSatelliteCapacityTransitionPlanner.PlanShrink(
                master,
                satellites,
                maxSatellites: 7);

            Assert.IsFalse(plan.RequiresOverflow);
            CollectionAssert.AreEqual(
                new[] { master, satellites[0], satellites[1] },
                plan.AdmittedWindows.ToArray());
        }

        [TestMethod]
        public void ClosedWindowCompletesCapacityWorkWithoutRestoringIt()
        {
            var sourceDesktop = new Mock<IVirtualDesktop>().Object;
            var targetDesktop = new Mock<IVirtualDesktop>().Object;
            var display = new Mock<IDisplay>().Object;
            var master = CreateWindow(30);
            var closedExtra = CreateWindow(31);
            var remainingExtra = CreateWindow(32);
            var transition = MasterSatelliteCapacityTransitionPlanner.PlanShrink(
                master,
                new[] { remainingExtra, closedExtra },
                maxSatellites: 0);
            var sourcePlan = new TilingService.MasterSatelliteCapacitySourcePlan(
                new LayoutStateKey(sourceDesktop, display),
                SourceState: null,
                new MasterSatelliteLayoutSettings(),
                transition,
                IsActivation: true);
            var closedWork = new TilingService.MasterSatelliteCapacityTransitionWorkItem(
                sourcePlan,
                closedExtra);
            var remainingWork = new TilingService.MasterSatelliteCapacityTransitionWorkItem(
                sourcePlan,
                remainingExtra);
            var correlationId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var transfer = new PendingWindowTransfer
            {
                CorrelationId = correlationId,
                WindowHandle = closedExtra.Handle,
                SourceDesktop = sourceDesktop,
                SourceDisplay = display,
                TargetDesktop = targetDesktop,
                TargetDisplay = display,
                TargetRole = ReservedRole.Master,
                CreatedAt = now,
                UpdatedAt = now,
                Deadline = now.AddSeconds(1),
                State = PendingWindowTransferState.Cancelled,
                TerminalReason = "WindowClosed",
            };
            var context = new TilingService.MasterSatelliteExistingWindowTransferContext(
                closedWork,
                transfer);
            var contexts = new Dictionary<Guid,
                TilingService.MasterSatelliteExistingWindowTransferContext>
            {
                [correlationId] = context,
            };
            var queue = new Queue<TilingService.MasterSatelliteCapacityTransitionWorkItem>();
            queue.Enqueue(closedWork);
            queue.Enqueue(remainingWork);

            Assert.IsTrue(TilingService.CompleteClosedMasterSatelliteCapacityTransfer(
                transfer,
                contexts));

            Assert.IsTrue(closedWork.Completed);
            Assert.IsNull(context.RestorePoint);
            Assert.AreEqual(0, contexts.Count);
            while (queue.Count > 0 && queue.Peek().Completed)
            {
                queue.Dequeue();
            }
            Assert.AreSame(remainingWork, queue.Peek());
        }

        private static IWindow CreateWindow(int handle)
        {
            var window = new Mock<IWindow>(MockBehavior.Loose);
            window.SetupGet(item => item.Handle).Returns(new System.IntPtr(handle));
            return window.Object;
        }
    }
}
