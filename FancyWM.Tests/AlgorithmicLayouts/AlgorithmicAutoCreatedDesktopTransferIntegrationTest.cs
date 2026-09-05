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
    public class AlgorithmicAutoCreatedDesktopTransferIntegrationTest
    {
        [TestMethod]
        public void CreatedDesktopIsPreparedBeforeExactMasterTransferAndCommitsOnce()
        {
            using var harness = new Harness();
            var overflowWindow = harness.CreateWindow("overflow");
            var correlationId = Guid.NewGuid();
            var originalPosition = Rectangle.OffsetAndSize(37, 41, 503, 307);

            Assert.AreEqual(1, harness.Desktops.Count);
            Assert.AreSame(harness.Source, harness.Desktops[0]);
            Assert.IsNull(harness.Backend.GetTree(harness.Target));
            Assert.IsFalse(harness.SourceCapacity.CanAcceptWindow);
            Assert.AreEqual(2, harness.SourceCapacity.OccupiedSlots);
            Assert.AreEqual(2, harness.SourceCapacity.TotalCapacity);

            var creation = harness.CreationOrchestrator.CreateAndPrepareDesktop(
                overflowWindow.Handle,
                correlationId,
                1,
                harness.PrepareCreatedDesktop);

            Assert.AreEqual(
                AlgorithmicDesktopCreationDisposition.CreatedAndPrepared,
                creation.Disposition,
                creation.DiagnosticReason);
            Assert.AreSame(harness.Target, creation.CreatedDesktop);
            Assert.AreEqual(1, harness.CreateDesktopCallCount);
            Assert.AreEqual(1, harness.PrepareCallCount);
            Assert.IsTrue(harness.PreparationCompleted);
            Assert.AreEqual(2, harness.Desktops.Count);
            Assert.IsNotNull(harness.TargetState);
            Assert.IsTrue(harness.Coordinator.TryGetCapacity(
                harness.TargetKey,
                out var emptyTargetCapacity));
            Assert.AreEqual(WorkspaceLayoutKind.Canonical, emptyTargetCapacity.LayoutKind);
            Assert.AreEqual(ReservedRole.Master, emptyTargetCapacity.NextRole);
            Assert.AreEqual(0, emptyTargetCapacity.OccupiedSlots);

            AlgorithmicTransferArrivalResult? arrival = null;
            bool sourceRemovalObserved = false;
            int materializationCount = 0;
            harness.TargetMove = movedWindow =>
            {
                Assert.IsTrue(harness.PreparationCompleted);
                Assert.IsFalse(harness.Coordinator.IsMutationLockHeldByCurrentThread);
                sourceRemovalObserved = harness.TransferOrchestrator.ObserveSourceRemoved(
                    movedWindow.Handle,
                    harness.Source,
                    out _);
                arrival = harness.TransferOrchestrator.ObserveDestinationAdded(
                    movedWindow,
                    harness.Target,
                    (transfer, reservation) =>
                    {
                        materializationCount++;
                        return harness.MaterializeMaster(
                            movedWindow,
                            transfer,
                            reservation);
                    });
            };

            var start = harness.TransferOrchestrator.StartExistingDesktopTransfer(
                correlationId,
                overflowWindow,
                harness.Source,
                originalPosition,
                (key, capacity) => harness.Preflight(
                    overflowWindow,
                    key,
                    capacity));

            Assert.AreEqual(
                AlgorithmicTransferStartDisposition.MoveRequested,
                start.Disposition);
            Assert.IsTrue(sourceRemovalObserved);
            Assert.IsNotNull(arrival);
            Assert.AreEqual(
                AlgorithmicTransferArrivalDisposition.Materialized,
                arrival!.Disposition,
                arrival.DiagnosticReason);
            Assert.AreEqual(1, materializationCount);
            Assert.IsNotNull(start.Transfer);
            Assert.IsTrue(harness.Coordinator.TryGetTransfer(
                correlationId,
                out var committed));
            Assert.AreEqual(PendingWindowTransferState.Committed, committed.State);
            Assert.IsTrue(committed.SourceRemovedObserved);
            Assert.IsTrue(committed.DestinationAddedObserved);
            Assert.AreEqual(ReservedRole.Master, committed.TargetRole);
            Assert.IsNull(committed.TargetSatelliteIndex);
            Assert.AreSame(harness.Target, committed.TargetDesktop);
            Assert.AreSame(overflowWindow, harness.TargetState!.Master);
            Assert.AreEqual(0, harness.TargetState.Satellites.Count);
            Assert.AreEqual(0, harness.Coordinator.ReservationCount);
            Assert.AreEqual(0, harness.Coordinator.ActiveTransferCount);

            var targetInvariant = harness.Backend.ValidateMasterSatelliteLayout(
                harness.Target,
                harness.TargetState,
                harness.Settings);
            Assert.IsTrue(targetInvariant.IsValid, targetInvariant.Description);
            Assert.AreEqual(
                1,
                harness.Backend.GetTree(harness.Target)!.Root!.Windows.Count());
            Assert.IsNotNull(
                harness.Backend.GetTree(harness.Target)!.FindNode(overflowWindow));
            Assert.IsTrue(harness.Backend.TryGetOriginalPosition(
                overflowWindow,
                out var restoredOriginal));
            Assert.AreEqual(originalPosition, restoredOriginal);

            // The overflow candidate was never admitted to the already-full
            // source tree, so the source remains canonical and full throughout.
            var sourceAfter = harness.Backend.QueryMasterSatelliteCapacity(
                harness.Source,
                harness.SourceState,
                harness.Settings);
            Assert.IsFalse(sourceAfter.CanAcceptWindow);
            Assert.AreEqual(2, sourceAfter.OccupiedSlots);
            var sourceInvariant = harness.Backend.ValidateMasterSatelliteLayout(
                harness.Source,
                harness.SourceState,
                harness.Settings);
            Assert.IsTrue(sourceInvariant.IsValid, sourceInvariant.Description);

            Assert.IsTrue(harness.Coordinator.TryGetCapacity(
                harness.TargetKey,
                out var committedTargetCapacity));
            Assert.AreEqual(1, committedTargetCapacity.OccupiedSlots);
            Assert.AreEqual(0, committedTargetCapacity.ReservedSlots);

            int duplicateMaterializerCalls = 0;
            var duplicateArrival = harness.TransferOrchestrator.ObserveDestinationAdded(
                overflowWindow,
                harness.Target,
                (_, _) =>
                {
                    duplicateMaterializerCalls++;
                    return AlgorithmicTransferMaterializationResult.Accept();
                });

            Assert.AreEqual(
                AlgorithmicTransferArrivalDisposition.AlreadyTerminal,
                duplicateArrival.Disposition);
            Assert.AreEqual(0, duplicateMaterializerCalls);
            Assert.AreEqual(1, materializationCount);
            Assert.AreEqual(
                1,
                harness.Backend.GetTree(harness.Target)!.Root!.Windows.Count());
            Assert.AreEqual(
                1,
                harness.Coordinator.SnapshotTransfers()
                    .Count(transfer => transfer.State == PendingWindowTransferState.Committed));
        }

        [TestMethod]
        public void DuplicateCreationCorrelationAndSiblingOrchestratorCannotBypassGlobalLimit()
        {
            using var harness = new Harness();
            var firstWindow = harness.CreateWindow("first-overflow");
            var secondWindow = harness.CreateWindow("second-overflow");
            var correlationId = Guid.NewGuid();
            var sibling = new AlgorithmicDesktopCreationOrchestrator(
                harness.Coordinator,
                harness.VirtualDesktopManager);
            AlgorithmicDesktopCreationResult? reentrantDuplicate = null;
            int duplicatePreparationCalls = 0;
            harness.DuringCreateDesktop = () =>
            {
                reentrantDuplicate = sibling.CreateAndPrepareDesktop(
                    firstWindow.Handle,
                    correlationId,
                    1,
                    _ => duplicatePreparationCalls++);
            };

            var first = harness.CreationOrchestrator.CreateAndPrepareDesktop(
                firstWindow.Handle,
                correlationId,
                1,
                harness.PrepareCreatedDesktop);
            var completedDuplicate = sibling.CreateAndPrepareDesktop(
                firstWindow.Handle,
                correlationId,
                1,
                _ => duplicatePreparationCalls++);
            var overLimit = sibling.CreateAndPrepareDesktop(
                secondWindow.Handle,
                Guid.NewGuid(),
                1,
                _ => duplicatePreparationCalls++);

            Assert.AreEqual(
                AlgorithmicDesktopCreationDisposition.CreatedAndPrepared,
                first.Disposition);
            Assert.IsNotNull(reentrantDuplicate);
            Assert.AreEqual(
                AlgorithmicDesktopCreationDisposition.DuplicateInFlight,
                reentrantDuplicate!.Disposition);
            Assert.AreEqual(
                AlgorithmicDesktopCreationDisposition.AlreadyCreated,
                completedDuplicate.Disposition);
            Assert.AreEqual(
                AlgorithmicDesktopCreationDisposition.LimitReached,
                overLimit.Disposition);
            Assert.AreEqual(1, harness.CreateDesktopCallCount);
            Assert.AreEqual(1, harness.PrepareCallCount);
            Assert.AreEqual(0, duplicatePreparationCalls);
            Assert.AreEqual(2, harness.Desktops.Count);

            var snapshot = harness.Coordinator.SnapshotDesktopCreations();
            Assert.AreEqual(1, snapshot.SuccessfulCreationCount);
            Assert.AreEqual(0, snapshot.InFlightCount);
            Assert.AreEqual(1, snapshot.TrackedCreatedDesktopCount);
        }

        private sealed class Harness : IDisposable
        {
            private static readonly Rectangle WorkArea =
                Rectangle.OffsetAndSize(0, 0, 1000, 600);

            private readonly Mock<IWorkspace> m_workspace = new(MockBehavior.Loose);
            private readonly Mock<IVirtualDesktopManager> m_virtualDesktopManager =
                new(MockBehavior.Loose);
            private readonly Mock<IVirtualDesktop> m_source = new(MockBehavior.Loose);
            private readonly Mock<IVirtualDesktop> m_target = new(MockBehavior.Loose);
            private readonly Mock<IDisplay> m_display = new(MockBehavior.Loose);
            private readonly UniqueWindowMockFactory m_windows = new();
            private readonly List<IVirtualDesktop> m_desktops = [];
            private readonly AlgorithmicLayoutDisplayRegistration m_registration;
            private long m_capacityPublicationSequence;

            public TilingWorkspace Backend { get; }

            public AlgorithmicLayoutCoordinator Coordinator { get; }

            public MasterSatelliteRuntimeLifecycle Lifecycle { get; }

            public AlgorithmicDesktopCreationOrchestrator CreationOrchestrator { get; }

            public AlgorithmicWindowTransferOrchestrator TransferOrchestrator { get; }

            public IVirtualDesktopManager VirtualDesktopManager =>
                m_virtualDesktopManager.Object;

            public IReadOnlyList<IVirtualDesktop> Desktops => m_desktops;

            public IVirtualDesktop Source => m_source.Object;

            public IVirtualDesktop Target => m_target.Object;

            public IDisplay Display => m_display.Object;

            public MasterSatelliteLayoutSettings Settings { get; }

            public MasterSatelliteRuntimeState SourceState { get; }

            public MasterSatelliteRuntimeState? TargetState { get; private set; }

            public LayoutStateKey TargetKey => new(Target, Display);

            public MasterSatelliteCapacitySnapshot SourceCapacity { get; }

            public int CreateDesktopCallCount { get; private set; }

            public int PrepareCallCount { get; private set; }

            public bool PreparationCompleted { get; private set; }

            public Action? DuringCreateDesktop { get; set; }

            public Action<IWindow>? TargetMove { get; set; }

            public Harness()
            {
                ConfigureDesktop(m_source, "Source");
                ConfigureDesktop(m_target, "Auto-created");
                m_desktops.Add(Source);

                m_workspace.SetupGet(workspace => workspace.VirtualDesktopManager)
                    .Returns(m_virtualDesktopManager.Object);
                m_virtualDesktopManager.SetupGet(manager => manager.Workspace)
                    .Returns(m_workspace.Object);
                m_virtualDesktopManager.SetupGet(manager => manager.CanManageVirtualDesktops)
                    .Returns(true);
                m_virtualDesktopManager.SetupGet(manager => manager.Desktops)
                    .Returns(() => m_desktops.ToArray());
                m_virtualDesktopManager.Setup(manager => manager.CreateDesktop())
                    .Returns(() =>
                    {
                        CreateDesktopCallCount++;
                        if (!m_desktops.Contains(Target))
                        {
                            m_desktops.Add(Target);
                        }
                        DuringCreateDesktop?.Invoke();
                        return Target;
                    });

                m_display.SetupGet(display => display.Workspace)
                    .Returns(m_workspace.Object);
                m_display.SetupGet(display => display.WorkArea).Returns(WorkArea);
                var display = Display;
                m_display.Setup(candidate => candidate.Equals(It.IsAny<IDisplay>()))
                    .Returns((IDisplay other) => ReferenceEquals(display, other));
                m_target.Setup(desktop => desktop.MoveWindow(It.IsAny<IWindow>()))
                    .Callback<IWindow>(window => TargetMove?.Invoke(window));

                Backend = new TilingWorkspace(new MasterSatelliteLayoutEngine());
                Backend.RegisterDesktop(
                    Source,
                    WorkArea,
                    PanelOrientation.Horizontal);

                Coordinator = new AlgorithmicLayoutCoordinator(
                    m_workspace.Object,
                    Dispatcher.CurrentDispatcher);
                Lifecycle = new MasterSatelliteRuntimeLifecycle(Display, Coordinator);
                m_registration = Coordinator.RegisterDisplay(Display, Lifecycle);
                Settings = new MasterSatelliteLayoutSettings
                {
                    Enabled = true,
                    DisplayScope = AlgorithmicLayoutDisplayScope.AllDisplays,
                    MasterRatio = 0.60,
                    DefaultMasterSide = MasterSide.Left,
                    DefaultSatelliteOrientation = SatelliteLayoutOrientation.Vertical,
                    MaxSatellites = 1,
                    MaxAutoCreatedDesktops = 1,
                };
                var transition = Lifecycle.CacheSettings(Settings, Display);
                Assert.IsTrue(transition.IsEligible);
                var sourceLifecycle = Lifecycle.DesktopAdded(
                    Backend,
                    Source,
                    Display);
                Assert.IsTrue(sourceLifecycle.StateAdded);
                Assert.IsTrue(Lifecycle.TryGetState(Source, out var sourceState));
                SourceState = sourceState;

                var sourceMaster = CreateWindow("source-master");
                var sourceSatellite = CreateWindow("source-satellite");
                var masterPlacement = Backend.RegisterMaster(
                    Source,
                    SourceState,
                    Settings,
                    sourceMaster);
                var satellitePlacement = Backend.RegisterSatellite(
                    Source,
                    SourceState,
                    Settings,
                    sourceSatellite,
                    0);
                Assert.IsTrue(masterPlacement.Succeeded, masterPlacement.Message);
                Assert.IsTrue(satellitePlacement.Succeeded, satellitePlacement.Message);
                SourceCapacity = Backend.QueryMasterSatelliteCapacity(
                    Source,
                    SourceState,
                    Settings);
                Assert.IsTrue(Coordinator.PublishCapacity(
                    new LayoutStateKey(Source, Display),
                    SourceCapacity,
                    NextCapacitySequence()));

                CreationOrchestrator = new AlgorithmicDesktopCreationOrchestrator(
                    Coordinator,
                    VirtualDesktopManager);
                TransferOrchestrator = new AlgorithmicWindowTransferOrchestrator(
                    Coordinator,
                    Display);
            }

            public IWindow CreateWindow(string title)
            {
                return m_windows.Create(title);
            }

            public void PrepareCreatedDesktop(IVirtualDesktop desktop)
            {
                PrepareCallCount++;
                Assert.AreSame(Target, desktop);
                Assert.IsFalse(Coordinator.IsMutationLockHeldByCurrentThread);
                Assert.IsNull(Backend.GetTree(Target));
                Assert.IsTrue(m_desktops.Contains(Target));

                Backend.RegisterDesktop(
                    Target,
                    WorkArea,
                    PanelOrientation.Horizontal);
                var lifecycle = Lifecycle.DesktopAdded(
                    Backend,
                    Target,
                    Display);
                Assert.IsTrue(lifecycle.StateAdded);
                Assert.IsTrue(Lifecycle.TryGetState(Target, out var targetState));
                TargetState = targetState;
                PublishTargetCapacity();
                PreparationCompleted = true;
            }

            public AlgorithmicDestinationPreflightResult Preflight(
                IWindow window,
                LayoutStateKey key,
                CoordinatorCapacitySnapshot capacity)
            {
                Assert.IsTrue(PreparationCompleted);
                Assert.AreSame(Target, key.VirtualDesktop);
                Assert.AreSame(Display, key.Display);
                Assert.AreEqual(ReservedRole.Master, capacity.NextRole);
                Assert.IsNull(capacity.NextSatelliteIndex);
                Assert.IsNotNull(TargetState);
                var placement = Backend.PreflightMasterSatellitePlacement(
                    Target,
                    TargetState!,
                    Settings,
                    window,
                    MasterSatelliteWindowRole.Master);
                return placement.Succeeded
                    ? AlgorithmicDestinationPreflightResult.Accept()
                    : AlgorithmicDestinationPreflightResult.Reject(
                        placement.Message
                            ?? "The real target preflight was rejected.");
            }

            public AlgorithmicTransferMaterializationResult MaterializeMaster(
                IWindow window,
                PendingWindowTransfer transfer,
                AlgorithmicSlotReservation reservation)
            {
                Assert.IsFalse(Coordinator.IsMutationLockHeldByCurrentThread);
                Assert.IsNotNull(TargetState);
                Assert.AreEqual(ReservedRole.Master, reservation.Role);
                Assert.IsNull(reservation.SatelliteIndex);
                Assert.AreEqual(transfer.CorrelationId, reservation.CorrelationId);
                Assert.AreEqual(window.Handle, reservation.WindowHandle);
                Assert.AreEqual(1, Coordinator.ReservationCount);

                var placement = Backend.RegisterReservedWindow(
                    reservation.LayoutKey,
                    TargetState!,
                    Settings,
                    window,
                    reservation.ToWorkspaceSlot(),
                    transfer.SourceOriginalPosition);
                if (!placement.Succeeded)
                {
                    return AlgorithmicTransferMaterializationResult.Reject(
                        placement.Message
                            ?? "The exact master placement was rejected.");
                }
                Assert.AreEqual(MasterSatelliteWindowRole.Master, placement.Role);
                Assert.IsNull(placement.SatelliteIndex);

                var invariant = Backend.ValidateMasterSatelliteLayout(
                    Target,
                    TargetState!,
                    Settings);
                if (!invariant.IsValid)
                {
                    return AlgorithmicTransferMaterializationResult.Reject(
                        invariant.Description);
                }
                PublishTargetCapacity();
                return AlgorithmicTransferMaterializationResult.Accept();
            }

            public void Dispose()
            {
                m_registration.Dispose();
                Coordinator.Dispose();
            }

            private void PublishTargetCapacity()
            {
                Assert.IsNotNull(TargetState);
                var capacity = Backend.QueryMasterSatelliteCapacity(
                    Target,
                    TargetState,
                    Settings);
                Assert.IsTrue(Coordinator.PublishCapacity(
                    TargetKey,
                    capacity,
                    NextCapacitySequence()));
            }

            private long NextCapacitySequence()
            {
                return ++m_capacityPublicationSequence;
            }

            private void ConfigureDesktop(
                Mock<IVirtualDesktop> desktop,
                string name)
            {
                desktop.SetupGet(candidate => candidate.Name).Returns(name);
                desktop.SetupGet(candidate => candidate.IsAlive).Returns(true);
                desktop.SetupGet(candidate => candidate.Workspace)
                    .Returns(m_workspace.Object);
                var instance = desktop.Object;
                desktop.Setup(candidate => candidate.Equals(It.IsAny<IVirtualDesktop>()))
                    .Returns((IVirtualDesktop other) => ReferenceEquals(instance, other));
            }
        }
    }
}
