#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;

using FancyWM.AlgorithmicLayouts;
using FancyWM.Layouts.Tiling;
using FancyWM.Models;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    [TestClass]
    public class MasterSatelliteExistingWindowOverflowIntegrationTest
    {
        [TestMethod]
        public void ShrinkMovesTailSatellitesSequentiallyWithoutChangingRetainedRoles()
        {
            using var harness = new Harness();
            var windows = harness.CreateSourceLayout(6);
            var plan = MasterSatelliteCapacityTransitionPlanner.PlanShrink(
                harness.SourceState.Master,
                harness.SourceState.Satellites,
                maxSatellites: 2);
            var moveOrder = new List<IWindow>();

            foreach (var extra in plan.ExtrasFromEnd)
            {
                int sourceCountBeforePlan = harness.SourceWindows().Length;
                var search = harness.Plan(extra, (_, _) =>
                {
                    Assert.AreEqual(sourceCountBeforePlan, harness.SourceWindows().Length);
                    return AlgorithmicDestinationPreflightResult.Accept();
                });
                Assert.IsTrue(search.Succeeded);
                Assert.AreEqual(sourceCountBeforePlan, harness.SourceWindows().Length,
                    "Reservation must not mutate the source tree.");

                var arrival = harness.ExecuteExistingWindowTransfer(extra, search);
                moveOrder.Add(extra);

                Assert.AreEqual(
                    AlgorithmicTransferArrivalDisposition.Materialized,
                    arrival.Disposition,
                    arrival.DiagnosticReason);
                var transitionEvent = new AlgorithmicLayoutEvent(
                    AlgorithmicLayoutEventKind.WindowMovedToDesktop,
                    harness.Display,
                    "CapacityTransition",
                    "AlgorithmicLayout.WindowMovedToDesktop",
                    sourceOccupiedSlots: harness.SourceWindows().Length,
                    sourceTotalCapacity: harness.ReducedSettings.MaxSatellites + 1);
                Assert.AreEqual(
                    harness.SourceWindows().Length,
                    transitionEvent.SourceOccupiedSlots);
                harness.AssertCanonicalWithOldSourceLimit();
                harness.AssertTargetCanonical();
            }

            CollectionAssert.AreEqual(
                new[] { windows[5], windows[4], windows[3] },
                moveOrder.ToArray());
            Assert.AreSame(windows[0], harness.SourceState.Master);
            CollectionAssert.AreEqual(
                new[] { windows[1], windows[2] },
                harness.SourceState.Satellites.ToArray());
            CollectionAssert.AreEqual(
                new[] { windows[0], windows[1], windows[2] },
                harness.SourceWindows());
            CollectionAssert.AreEqual(
                new[] { windows[5], windows[4], windows[3] },
                harness.TargetWindows());
            Assert.IsTrue(harness.Backend.ValidateMasterSatelliteLayout(
                harness.Source,
                harness.SourceState,
                harness.ReducedSettings).IsValid);
            Assert.AreEqual(0, harness.Coordinator.ReservationCount);
            Assert.AreEqual(0, harness.Coordinator.ActiveTransferCount);
        }

        [TestMethod]
        public void RejectedDestinationRestoresBothEndpointsAndExactSourceIndex()
        {
            using var harness = new Harness();
            var windows = harness.CreateSourceLayout(5);
            var extra = windows[^1];
            var sourceDescription = harness.SourceDescription();
            var sourceRevision = harness.SourceState.Revision;
            var targetRevision = harness.TargetState.Revision;
            var search = harness.Plan(
                extra,
                (_, _) => AlgorithmicDestinationPreflightResult.Accept());
            Assert.IsTrue(search.Succeeded);

            var arrival = harness.ExecuteExistingWindowTransfer(
                extra,
                search,
                rejectAfterDetach: true);

            Assert.AreEqual(
                AlgorithmicTransferArrivalDisposition.MaterializationFailed,
                arrival.Disposition);
            CollectionAssert.AreEqual(windows, harness.SourceWindows());
            Assert.AreEqual(0, harness.TargetWindows().Length);
            Assert.AreSame(windows[0], harness.SourceState.Master);
            CollectionAssert.AreEqual(
                windows.Skip(1).ToArray(),
                harness.SourceState.Satellites.ToArray());
            Assert.AreEqual(sourceRevision, harness.SourceState.Revision);
            Assert.AreEqual(targetRevision, harness.TargetState.Revision);
            Assert.AreEqual(sourceDescription, harness.SourceDescription());
            harness.AssertCanonicalWithOldSourceLimit();
            harness.AssertTargetCanonical();
            Assert.AreEqual(0, harness.Coordinator.ReservationCount);
        }

        [TestMethod]
        public void RejectedDestinationRollsBackThenTailFallbackKeepsRetainedPrefixCanonical()
        {
            using var harness = new Harness();
            var windows = harness.CreateSourceLayout(4);
            var extra = windows[^1];
            Assert.IsTrue(harness.Backend.TryGetOriginalPosition(
                extra,
                out var originalPosition));
            var search = harness.Plan(
                extra,
                (_, _) => AlgorithmicDestinationPreflightResult.Accept());
            Assert.IsTrue(search.Succeeded);

            var arrival = harness.ExecuteExistingWindowTransfer(
                extra,
                search,
                rejectAfterDetach: true);
            Assert.AreEqual(
                AlgorithmicTransferArrivalDisposition.MaterializationFailed,
                arrival.Disposition);

            var recovery = harness.Orchestrator.RecoverTerminalTransfer(
                extra,
                search.Transfer!.CorrelationId);
            Assert.AreEqual(
                AlgorithmicTransferRecoveryDisposition.RolledBackToSource,
                recovery.Disposition);
            Assert.IsTrue(recovery.RollbackSucceeded);
            Assert.IsTrue(recovery.RequiresFloating);
            Assert.AreSame(harness.Source, recovery.FloatingDesktop);
            CollectionAssert.AreEqual(windows, harness.SourceWindows(),
                "Endpoint rollback must restore the exact source before fallback.");

            var detach = harness.Backend.UnregisterMasterSatelliteWindow(
                harness.Source,
                harness.SourceState,
                harness.OldSourceSettings,
                extra,
                preserveOriginalPosition: true);

            Assert.IsTrue(detach.Succeeded, detach.Message);
            Assert.AreSame(windows[0], harness.SourceState.Master);
            CollectionAssert.AreEqual(
                new[] { windows[1], windows[2] },
                harness.SourceState.Satellites.ToArray());
            CollectionAssert.AreEqual(
                new[] { windows[0], windows[1], windows[2] },
                harness.SourceWindows());
            Assert.IsTrue(harness.Backend.ValidateMasterSatelliteLayout(
                harness.Source,
                harness.SourceState,
                harness.ReducedSettings).IsValid);
            Assert.IsTrue(harness.Backend.TryGetOriginalPosition(
                extra,
                out var retainedOriginalPosition));
            Assert.AreEqual(originalPosition, retainedOriginalPosition);
            Assert.AreEqual(0, harness.TargetWindows().Length);
            harness.AssertTargetCanonical();
            Assert.AreEqual(0, harness.Coordinator.ReservationCount);
            Assert.AreEqual(0, harness.Coordinator.ActiveTransferCount);
        }

        [TestMethod]
        public void RepeatedDestinationFailuresDrainEveryTailAndKeepPrefixCanonical()
        {
            using var harness = new Harness();
            var windows = harness.CreateSourceLayout(6);
            var plan = MasterSatelliteCapacityTransitionPlanner.PlanShrink(
                harness.SourceState.Master,
                harness.SourceState.Satellites,
                maxSatellites: harness.ReducedSettings.MaxSatellites);
            var floated = new List<IWindow>();

            foreach (var extra in plan.ExtrasFromEnd)
            {
                var search = harness.Plan(
                    extra,
                    (_, _) => AlgorithmicDestinationPreflightResult.Accept());
                Assert.IsTrue(search.Succeeded);
                var arrival = harness.ExecuteExistingWindowTransfer(
                    extra,
                    search,
                    rejectAfterDetach: true);
                Assert.AreEqual(
                    AlgorithmicTransferArrivalDisposition.MaterializationFailed,
                    arrival.Disposition);
                var recovery = harness.Orchestrator.RecoverTerminalTransfer(
                    extra,
                    search.Transfer!.CorrelationId);
                Assert.AreEqual(
                    AlgorithmicTransferRecoveryDisposition.RolledBackToSource,
                    recovery.Disposition);
                var detach = harness.Backend.UnregisterMasterSatelliteWindow(
                    harness.Source,
                    harness.SourceState,
                    harness.OldSourceSettings,
                    extra,
                    preserveOriginalPosition: true);
                Assert.IsTrue(detach.Succeeded, detach.Message);
                floated.Add(extra);

                _ = new AlgorithmicLayoutEvent(
                    AlgorithmicLayoutEventKind.WindowLeftFloating,
                    harness.Display,
                    "DestinationMaterializationRejected",
                    "AlgorithmicLayout.WindowLeftFloating",
                    sourceOccupiedSlots: harness.SourceWindows().Length,
                    sourceTotalCapacity: harness.ReducedSettings.MaxSatellites + 1);
                harness.AssertCanonicalWithOldSourceLimit();
            }

            CollectionAssert.AreEqual(
                new[] { windows[5], windows[4], windows[3] },
                floated.ToArray());
            CollectionAssert.AreEqual(
                new[] { windows[0], windows[1], windows[2] },
                harness.SourceWindows());
            Assert.AreSame(windows[0], harness.SourceState.Master);
            CollectionAssert.AreEqual(
                new[] { windows[1], windows[2] },
                harness.SourceState.Satellites.ToArray());
            Assert.IsTrue(harness.Backend.ValidateMasterSatelliteLayout(
                harness.Source,
                harness.SourceState,
                harness.ReducedSettings).IsValid);
            Assert.AreEqual(0, harness.TargetWindows().Length);
            Assert.AreEqual(0, harness.Coordinator.ReservationCount);
            Assert.AreEqual(0, harness.Coordinator.ActiveTransferCount);
        }

        [TestMethod]
        public void NoDestinationFallbackDrainsEveryTailWithoutReservations()
        {
            using var harness = new Harness
            {
                CanManageVirtualDesktops = false,
            };
            var windows = harness.CreateSourceLayout(6);
            var transition = MasterSatelliteCapacityTransitionPlanner.PlanShrink(
                harness.SourceState.Master,
                harness.SourceState.Satellites,
                maxSatellites: harness.ReducedSettings.MaxSatellites);
            var queue = new Queue<IWindow>(transition.ExtrasFromEnd);
            var floatingDecisions = new List<IWindow>();

            while (queue.Count > 0)
            {
                var extra = queue.Peek();
                var sourceBefore = harness.SourceDescription();
                var search = harness.Plan(
                    extra,
                    (_, _) =>
                    {
                        Assert.Fail(
                            "VDM-unavailable planning must not run target preflight.");
                        return AlgorithmicDestinationPreflightResult.Reject(
                            "Unreachable preflight callback.");
                    });

                Assert.AreEqual(
                    AlgorithmicDestinationSearchDisposition.NoDestination,
                    search.Disposition);
                Assert.IsNull(search.Transfer);
                Assert.AreEqual(0, search.ExaminedCandidates.Count);
                Assert.AreEqual(sourceBefore, harness.SourceDescription(),
                    "Planning failure must not mutate the source tree.");
                Assert.AreEqual(0, harness.Coordinator.ReservationCount);
                Assert.AreEqual(0, harness.Coordinator.ActiveTransferCount);

                var detach = harness.Backend.UnregisterMasterSatelliteWindow(
                    harness.Source,
                    harness.SourceState,
                    harness.OldSourceSettings,
                    extra,
                    preserveOriginalPosition: true);
                Assert.IsTrue(detach.Succeeded, detach.Message);
                floatingDecisions.Add(extra);
                queue.Dequeue();
                harness.AssertCanonicalWithOldSourceLimit();
            }

            Assert.AreEqual(0, queue.Count);
            CollectionAssert.AreEqual(
                new[] { windows[5], windows[4], windows[3] },
                floatingDecisions.ToArray());
            CollectionAssert.AreEqual(
                new[] { windows[0], windows[1], windows[2] },
                harness.SourceWindows());
            Assert.IsTrue(harness.Backend.ValidateMasterSatelliteLayout(
                harness.Source,
                harness.SourceState,
                harness.ReducedSettings).IsValid);
            Assert.AreEqual(0, harness.Coordinator.ReservationCount);
            Assert.AreEqual(0, harness.Coordinator.ActiveTransferCount);
        }

        [TestMethod]
        public void PendingActivationRoutesNewcomerThroughTargetOnlyOverflow()
        {
            using var harness = new Harness();
            var windows = harness.CreateManualSourceLayout(4);
            var activation = MasterSatelliteCapacityTransitionPlanner.PlanActivation(
                windows,
                windows[1],
                harness.ReducedSettings.MaxSatellites);
            var activationState = harness.Engine.CreateState(
                harness.ReducedSettings,
                isActive: true);
            var preflight = harness.Backend.PreflightMasterSatelliteActivation(
                harness.Source,
                activationState,
                harness.ReducedSettings,
                activation.AdmittedWindows);
            Assert.IsTrue(preflight.Succeeded, preflight.Message);
            var plannedExtra = activation.ExtrasFromEnd.Single();
            var extraSearch = harness.Plan(
                plannedExtra,
                (_, _) => AlgorithmicDestinationPreflightResult.Accept());
            Assert.IsTrue(extraSearch.Succeeded);

            var newcomer = harness.CreateSourceOwnedWindow("activation-newcomer");
            var newcomerSearch = harness.Plan(
                newcomer,
                (_, _) => AlgorithmicDestinationPreflightResult.Accept());
            Assert.IsTrue(newcomerSearch.Succeeded);
            var waiting = harness.ExecuteTargetOnlyWindowTransfer(
                newcomer,
                newcomerSearch);

            Assert.AreEqual(
                AlgorithmicTransferArrivalDisposition.AwaitingPrecedingSlot,
                waiting.Disposition);
            CollectionAssert.AreEqual(windows, harness.SourceWindows(),
                "A newcomer must never enter the frozen activation source tree.");

            var extraArrival = harness.ExecuteExistingWindowTransfer(
                plannedExtra,
                extraSearch,
                manualSource: true);
            Assert.AreEqual(
                AlgorithmicTransferArrivalDisposition.Materialized,
                extraArrival.Disposition);
            var newcomerArrival = harness.ObserveTargetOnlyDestination(newcomer);
            Assert.AreEqual(
                AlgorithmicTransferArrivalDisposition.Materialized,
                newcomerArrival.Disposition);

            var committed = harness.Backend.ActivateMasterSatelliteLayout(
                harness.Source,
                activationState,
                harness.ReducedSettings,
                activation.AdmittedWindows);
            Assert.IsTrue(committed.Succeeded, committed.Message);
            CollectionAssert.AreEqual(
                activation.AdmittedWindows.ToArray(),
                harness.SourceWindows());
            Assert.AreSame(activation.Master, activationState.Master);
            Assert.IsTrue(harness.Backend.ValidateMasterSatelliteLayout(
                harness.Source,
                activationState,
                harness.ReducedSettings).IsValid);
            CollectionAssert.AreEqual(
                new[] { plannedExtra, newcomer },
                harness.TargetWindows());
            harness.AssertTargetCanonical();
            Assert.AreEqual(0, harness.Coordinator.ReservationCount);
            Assert.AreEqual(0, harness.Coordinator.ActiveTransferCount);
        }

        private sealed class Harness : IDisposable
        {
            private static readonly Rectangle WorkArea =
                Rectangle.OffsetAndSize(0, 0, 1200, 700);

            private readonly Mock<IWorkspace> m_workspace = new(MockBehavior.Loose);
            private readonly Mock<IVirtualDesktopManager> m_desktops = new(MockBehavior.Loose);
            private readonly Mock<IVirtualDesktop> m_source = new(MockBehavior.Loose);
            private readonly Mock<IVirtualDesktop> m_target = new(MockBehavior.Loose);
            private readonly Mock<IDisplay> m_display = new(MockBehavior.Loose);
            private readonly HashSet<IWindow> m_sourceOwned = [];
            private readonly HashSet<IWindow> m_targetOwned = [];
            private readonly UniqueWindowMockFactory m_windows = new();
            private readonly AlgorithmicLayoutDisplayRegistration m_registration;
            private long m_publicationSequence;

            public TilingWorkspace Backend { get; }

            public MasterSatelliteLayoutEngine Engine { get; }

            public AlgorithmicLayoutCoordinator Coordinator { get; }

            public AlgorithmicWindowTransferOrchestrator Orchestrator { get; }

            public IVirtualDesktop Source => m_source.Object;

            public IVirtualDesktop Target => m_target.Object;

            public IDisplay Display => m_display.Object;

            public MasterSatelliteRuntimeState SourceState { get; }

            public MasterSatelliteRuntimeState TargetState { get; }

            public bool CanManageVirtualDesktops { get; set; } = true;

            public MasterSatelliteLayoutSettings OldSourceSettings { get; } =
                CreateSettings(maxSatellites: 5);

            public MasterSatelliteLayoutSettings ReducedSettings { get; } =
                CreateSettings(maxSatellites: 2);

            public Harness()
            {
                m_workspace.SetupGet(item => item.VirtualDesktopManager)
                    .Returns(m_desktops.Object);
                m_desktops.SetupGet(item => item.CanManageVirtualDesktops)
                    .Returns(() => CanManageVirtualDesktops);
                m_desktops.SetupGet(item => item.Desktops)
                    .Returns(new[] { Source, Target });
                ConfigureDesktop(m_source, "Source", m_sourceOwned);
                ConfigureDesktop(m_target, "Target", m_targetOwned);
                m_source.Setup(desktop => desktop.MoveWindow(It.IsAny<IWindow>()))
                    .Callback((IWindow window) =>
                    {
                        m_targetOwned.Remove(window);
                        m_sourceOwned.Add(window);
                    });
                m_source.SetupGet(item => item.Workspace).Returns(m_workspace.Object);
                m_target.SetupGet(item => item.Workspace).Returns(m_workspace.Object);
                m_display.SetupGet(item => item.Workspace).Returns(m_workspace.Object);
                m_display.SetupGet(item => item.WorkArea).Returns(WorkArea);

                Engine = new MasterSatelliteLayoutEngine();
                Backend = new TilingWorkspace(Engine);
                Backend.RegisterDesktop(Source, WorkArea, PanelOrientation.Horizontal);
                Backend.RegisterDesktop(Target, WorkArea, PanelOrientation.Horizontal);
                SourceState = Engine.CreateState(OldSourceSettings, true);
                TargetState = Engine.CreateState(ReducedSettings, true);
                Assert.IsTrue(Backend.ActivateMasterSatelliteLayout(
                    Target,
                    TargetState,
                    ReducedSettings,
                    Array.Empty<IWindow>()).Succeeded);

                Coordinator = new AlgorithmicLayoutCoordinator(
                    m_workspace.Object,
                    Dispatcher.CurrentDispatcher);
                m_registration = Coordinator.RegisterDisplay(Display, this);
                Orchestrator = new AlgorithmicWindowTransferOrchestrator(
                    Coordinator,
                    Display);
                PublishTargetCapacity();
            }

            public IWindow[] CreateSourceLayout(int count)
            {
                var windows = Enumerable.Range(0, count)
                    .Select(index => m_windows.Create($"source-{index}"))
                    .ToArray();
                foreach (var window in windows)
                {
                    m_sourceOwned.Add(window);
                    Backend.RegisterWindow(window);
                }
                var activation = Backend.ActivateMasterSatelliteLayout(
                    Source,
                    SourceState,
                    OldSourceSettings,
                    windows);
                Assert.IsTrue(activation.Succeeded, activation.Message);
                return windows;
            }

            public IWindow[] CreateManualSourceLayout(int count)
            {
                var windows = Enumerable.Range(0, count)
                    .Select(index => m_windows.Create($"manual-source-{index}"))
                    .ToArray();
                foreach (var window in windows)
                {
                    m_sourceOwned.Add(window);
                    Backend.RegisterWindow(window);
                }
                return windows;
            }

            public IWindow CreateSourceOwnedWindow(string name)
            {
                var window = m_windows.Create(name);
                m_sourceOwned.Add(window);
                return window;
            }

            public AlgorithmicDestinationSearchResult Plan(
                IWindow window,
                Func<LayoutStateKey, CoordinatorCapacitySnapshot,
                    AlgorithmicDestinationPreflightResult> preflight)
            {
                return Orchestrator.PlanExistingDesktopTransfer(
                    Guid.NewGuid(),
                    window,
                    window.Handle,
                    Source,
                    window.Position,
                    preflight);
            }

            public AlgorithmicTransferArrivalResult ExecuteExistingWindowTransfer(
                IWindow window,
                AlgorithmicDestinationSearchResult search,
                bool rejectAfterDetach = false,
                bool manualSource = false)
            {
                AlgorithmicTransferArrivalResult? arrival = null;
                MasterSatelliteWorkspaceTransferRestorePoint? restorePoint = null;
                m_target.Setup(desktop => desktop.MoveWindow(window))
                    .Callback(() =>
                    {
                        m_sourceOwned.Remove(window);
                        m_targetOwned.Add(window);
                        arrival = Orchestrator.ObserveDestinationAdded(
                            window,
                            window.Handle,
                            Target,
                            (transfer, reservation) =>
                            {
                                restorePoint = Backend
                                    .CaptureMasterSatelliteTransferRestorePoint(
                                        Source,
                                        manualSource ? null : SourceState,
                                        OldSourceSettings,
                                        Target,
                                        TargetState,
                                        ReducedSettings,
                                        window);
                                var detach = Backend.DetachMasterSatelliteTransferSource(
                                    restorePoint);
                                Assert.IsTrue(detach.Succeeded, detach.Message);
                                if (rejectAfterDetach)
                                {
                                    Backend.RestoreMasterSatelliteTransfer(restorePoint);
                                    restorePoint = null;
                                    return AlgorithmicTransferMaterializationResult.Reject(
                                        "Injected destination rejection.");
                                }

                                var placement = Backend.RegisterReservedWindow(
                                    new LayoutStateKey(Target, Display),
                                    TargetState,
                                    ReducedSettings,
                                    window,
                                    reservation.ToWorkspaceSlot(),
                                    transfer.SourceOriginalPosition);
                                if (!placement.Succeeded)
                                {
                                    Backend.RestoreMasterSatelliteTransfer(restorePoint);
                                    restorePoint = null;
                                    return AlgorithmicTransferMaterializationResult.Reject(
                                        placement.Message ?? "Placement rejected.");
                                }
                                PublishTargetCapacity();
                                return AlgorithmicTransferMaterializationResult.Accept();
                            },
                            (_, _) =>
                            {
                                if (restorePoint != null)
                                {
                                    Backend.RestoreMasterSatelliteTransfer(restorePoint);
                                    restorePoint = null;
                                }
                            });
                    });

                var start = Orchestrator.ExecuteReservedTransfer(
                    window,
                    window.Handle,
                    search);
                Assert.AreEqual(
                    AlgorithmicTransferStartDisposition.MoveRequested,
                    start.Disposition);
                Assert.IsNotNull(arrival);
                return arrival!;
            }

            public AlgorithmicTransferArrivalResult ExecuteTargetOnlyWindowTransfer(
                IWindow window,
                AlgorithmicDestinationSearchResult search)
            {
                AlgorithmicTransferArrivalResult? arrival = null;
                m_target.Setup(desktop => desktop.MoveWindow(window))
                    .Callback(() =>
                    {
                        m_sourceOwned.Remove(window);
                        m_targetOwned.Add(window);
                        arrival = ObserveTargetOnlyDestination(window);
                    });
                var start = Orchestrator.ExecuteReservedTransfer(
                    window,
                    window.Handle,
                    search);
                Assert.AreEqual(
                    AlgorithmicTransferStartDisposition.MoveRequested,
                    start.Disposition);
                Assert.IsNotNull(arrival);
                return arrival!;
            }

            public AlgorithmicTransferArrivalResult ObserveTargetOnlyDestination(
                IWindow window)
            {
                return Orchestrator.ObserveDestinationAdded(
                    window,
                    window.Handle,
                    Target,
                    (transfer, reservation) =>
                    {
                        var placement = Backend.RegisterReservedWindow(
                            new LayoutStateKey(Target, Display),
                            TargetState,
                            ReducedSettings,
                            window,
                            reservation.ToWorkspaceSlot(),
                            transfer.SourceOriginalPosition);
                        if (!placement.Succeeded)
                        {
                            return AlgorithmicTransferMaterializationResult.Reject(
                                placement.Message ?? "Placement rejected.");
                        }
                        PublishTargetCapacity();
                        return AlgorithmicTransferMaterializationResult.Accept();
                    });
            }

            public IWindow[] SourceWindows() => Windows(Source);

            public IWindow[] TargetWindows() => Windows(Target);

            public string SourceDescription() => Engine.CreateSnapshot(
                Backend.GetTree(Source)!,
                SourceState).TreeDescription;

            public void AssertCanonicalWithOldSourceLimit()
            {
                var invariant = Backend.ValidateMasterSatelliteLayout(
                    Source,
                    SourceState,
                    OldSourceSettings);
                Assert.IsTrue(invariant.IsValid, invariant.Description);
            }

            public void AssertTargetCanonical()
            {
                var invariant = Backend.ValidateMasterSatelliteLayout(
                    Target,
                    TargetState,
                    ReducedSettings);
                Assert.IsTrue(invariant.IsValid, invariant.Description);
            }

            public void Dispose()
            {
                m_registration.Dispose();
                Coordinator.Dispose();
            }

            private void PublishTargetCapacity()
            {
                Assert.IsTrue(Coordinator.PublishCapacity(
                    new LayoutStateKey(Target, Display),
                    Backend.QueryMasterSatelliteCapacity(
                        Target,
                        TargetState,
                        ReducedSettings),
                    ++m_publicationSequence));
            }

            private IWindow[] Windows(IVirtualDesktop desktop)
            {
                return Backend.GetTree(desktop)!.Root?.Windows
                    .Select(node => node.WindowReference)
                    .ToArray() ?? Array.Empty<IWindow>();
            }

            private static MasterSatelliteLayoutSettings CreateSettings(
                int maxSatellites)
            {
                return new MasterSatelliteLayoutSettings
                {
                    Enabled = true,
                    DisplayScope = AlgorithmicLayoutDisplayScope.AllDisplays,
                    MaxSatellites = maxSatellites,
                };
            }

            private static void ConfigureDesktop(
                Mock<IVirtualDesktop> desktop,
                string name,
                ISet<IWindow> ownedWindows)
            {
                desktop.SetupGet(item => item.Name).Returns(name);
                desktop.SetupGet(item => item.IsAlive).Returns(true);
                desktop.Setup(item => item.HasWindow(It.IsAny<IWindow>()))
                    .Returns((IWindow window) => ownedWindows.Contains(window));
            }
        }
    }
}
