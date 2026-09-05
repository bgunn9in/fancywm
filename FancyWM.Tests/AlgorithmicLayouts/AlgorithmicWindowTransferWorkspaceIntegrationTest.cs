#nullable enable

using System;
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
    public class AlgorithmicWindowTransferWorkspaceIntegrationTest
    {
        [DataTestMethod]
        [DataRow(false, 0)]
        [DataRow(true, 0)]
        [DataRow(false, 2)]
        [DataRow(true, 2)]
        public void ReservedTransferMaterializesRealCanonicalSlotAndCommits(
            bool destinationFirst,
            int initialTargetWindowCount)
        {
            using var harness = new Harness(maxSatellites: 2);
            var transferred = harness.CreateWindow("transferred");
            var original = Rectangle.OffsetAndSize(17, 23, 417, 323);
            harness.RegisterSourceMaster(transferred, original);

            IWindow? targetMaster = null;
            IWindow? existingSatellite = null;
            if (initialTargetWindowCount == 2)
            {
                targetMaster = harness.CreateWindow("target-master");
                existingSatellite = harness.CreateWindow("target-satellite");
                harness.RegisterTargetMaster(targetMaster);
                harness.RegisterTargetSatellite(existingSatellite, 0);
            }
            harness.PublishTargetCapacity();

            long targetRevisionBefore = harness.TargetState.Revision;
            AlgorithmicTransferArrivalResult? arrival = null;
            bool sourceObserved = false;
            harness.TargetMove = movedWindow =>
            {
                if (destinationFirst)
                {
                    arrival = harness.ObserveDestinationAdded(movedWindow);
                    sourceObserved = harness.RemoveSourceAndObserve(movedWindow);
                }
                else
                {
                    sourceObserved = harness.RemoveSourceAndObserve(movedWindow);
                    arrival = harness.ObserveDestinationAdded(movedWindow);
                }
            };

            var start = harness.Start(transferred, original, useRealPreflight: true);

            Assert.AreEqual(AlgorithmicTransferStartDisposition.MoveRequested, start.Disposition);
            Assert.IsTrue(sourceObserved);
            Assert.IsNotNull(arrival);
            Assert.AreEqual(
                AlgorithmicTransferArrivalDisposition.Materialized,
                arrival!.Disposition,
                arrival.DiagnosticReason);
            Assert.IsNotNull(harness.LastPlacement);
            Assert.IsTrue(harness.LastPlacement!.Succeeded, harness.LastPlacement.Message);
            Assert.AreEqual(1, harness.MaterializationCount);
            Assert.AreEqual(1, harness.ReservationCountAtMaterialization);
            Assert.AreEqual(
                PendingWindowTransferState.DestinationObserved,
                harness.TransferStateAtPhysicalCapacityPublication,
                "The destination must be observed and physical capacity published before the orchestrator commits.");

            Assert.IsNotNull(start.Transfer);
            Assert.IsTrue(harness.Coordinator.TryGetTransfer(
                start.Transfer!.CorrelationId,
                out var committed));
            Assert.AreEqual(PendingWindowTransferState.Committed, committed.State);
            Assert.IsTrue(committed.SourceRemovedObserved);
            Assert.IsTrue(committed.DestinationAddedObserved);
            Assert.AreEqual(0, harness.Coordinator.ReservationCount);
            Assert.AreEqual(0, harness.Coordinator.ActiveTransferCount);

            if (initialTargetWindowCount == 0)
            {
                Assert.AreEqual(ReservedRole.Master, committed.TargetRole);
                Assert.IsNull(committed.TargetSatelliteIndex);
                Assert.AreSame(transferred, harness.TargetState.Master);
                Assert.AreEqual(0, harness.TargetState.Satellites.Count);
            }
            else
            {
                Assert.AreEqual(ReservedRole.Satellite, committed.TargetRole);
                Assert.AreEqual(1, committed.TargetSatelliteIndex);
                Assert.AreSame(targetMaster, harness.TargetState.Master);
                CollectionAssert.AreEqual(
                    new[] { existingSatellite!, transferred },
                    harness.TargetState.Satellites.ToArray());
                Assert.AreSame(transferred, harness.TargetState.Satellites[^1]);
            }

            Assert.IsNull(harness.SourceState.Master);
            Assert.AreEqual(0, harness.SourceState.Satellites.Count);
            Assert.AreEqual(targetRevisionBefore + 1, harness.TargetState.Revision);
            Assert.AreEqual(
                initialTargetWindowCount + 1,
                harness.TargetWindows().Length);
            Assert.AreEqual(
                1,
                harness.TargetWindows().Count(window => ReferenceEquals(window, transferred)));
            Assert.IsNull(harness.Backend.GetTree(harness.Source)!.FindNode(transferred));
            Assert.IsNotNull(harness.Backend.GetTree(harness.Target)!.FindNode(transferred));
            harness.AssertCanonical(harness.Source, harness.SourceState);
            harness.AssertCanonical(harness.Target, harness.TargetState);
            Assert.IsTrue(harness.Backend.TryGetOriginalPosition(transferred, out var restoredOriginal));
            Assert.AreEqual(original, restoredOriginal);

            Assert.IsTrue(harness.Coordinator.TryGetCapacity(
                harness.TargetKey,
                out var committedCapacity));
            Assert.AreEqual(initialTargetWindowCount + 1, committedCapacity.OccupiedSlots);
            Assert.AreEqual(0, committedCapacity.ReservedSlots);

            var targetTree = harness.Backend.GetTree(harness.Target)!;
            var rootAfterCommit = targetTree.Root;
            var snapshotAfterCommit = harness.Engine.CreateSnapshot(
                targetTree,
                harness.TargetState);
            long revisionAfterCommit = harness.TargetState.Revision;
            int materializerCallsForDuplicate = 0;

            var duplicate = harness.Orchestrator.ObserveDestinationAdded(
                transferred,
                harness.Target,
                (_, _) =>
                {
                    materializerCallsForDuplicate++;
                    return AlgorithmicTransferMaterializationResult.Accept();
                });

            Assert.AreEqual(
                AlgorithmicTransferArrivalDisposition.AlreadyTerminal,
                duplicate.Disposition);
            Assert.AreEqual(0, materializerCallsForDuplicate);
            Assert.AreEqual(revisionAfterCommit, harness.TargetState.Revision);
            Assert.AreSame(rootAfterCommit, targetTree.Root);
            Assert.AreEqual(
                snapshotAfterCommit.TreeDescription,
                harness.Engine.CreateSnapshot(targetTree, harness.TargetState).TreeDescription);
            Assert.AreEqual(
                1,
                harness.TargetWindows().Count(window => ReferenceEquals(window, transferred)));
            Assert.AreEqual(0, harness.Coordinator.ReservationCount);
        }

        [TestMethod]
        public void FullSourcePlansAndReservesNewWindowOverflowBeforeMove()
        {
            using var harness = new Harness(maxSatellites: 1);
            var sourceMaster = harness.CreateWindow("source-master");
            var sourceSatellite = harness.CreateWindow("source-satellite");
            var incoming = harness.CreateWindow("incoming");
            harness.RegisterSourceMaster(
                sourceMaster,
                Rectangle.OffsetAndSize(1, 2, 400, 300));
            harness.RegisterSourceSatellite(
                sourceSatellite,
                0,
                Rectangle.OffsetAndSize(3, 4, 400, 300));
            harness.PublishTargetCapacity();
            var sourceTree = harness.Backend.GetTree(harness.Source)!;
            var sourceBefore = harness.Engine.CreateSnapshot(
                sourceTree,
                harness.SourceState);
            long sourceRevision = harness.SourceState.Revision;

            var rejected = harness.Lifecycle.PlaceWindow(
                harness.Backend,
                harness.Source,
                incoming);

            Assert.AreEqual(
                MasterSatelliteLocalMutationDisposition.Rejected,
                rejected.Disposition);
            Assert.AreEqual(
                MasterSatelliteFailureReason.CapacityReached,
                rejected.Operation!.FailureReason);
            Assert.IsNull(sourceTree.FindNode(incoming));
            Assert.AreEqual(sourceRevision, harness.SourceState.Revision);
            Assert.AreEqual(
                sourceBefore.TreeDescription,
                harness.Engine.CreateSnapshot(
                    sourceTree,
                    harness.SourceState).TreeDescription);

            bool reservedBeforeMove = false;
            AlgorithmicTransferArrivalResult? arrival = null;
            harness.TargetMove = movedWindow =>
            {
                reservedBeforeMove = harness.Coordinator.ReservationCount == 1;
                Assert.IsNull(sourceTree.FindNode(incoming));
                Assert.AreEqual(sourceRevision, harness.SourceState.Revision);
                Assert.AreEqual(
                    sourceBefore.TreeDescription,
                    harness.Engine.CreateSnapshot(
                        sourceTree,
                        harness.SourceState).TreeDescription);
                Assert.IsTrue(harness.Orchestrator.ObserveSourceRemoved(
                    movedWindow.Handle,
                    harness.Source,
                    out _));
                arrival = harness.ObserveDestinationAdded(movedWindow);
            };

            var start = harness.Start(
                incoming,
                Rectangle.OffsetAndSize(5, 6, 400, 300),
                useRealPreflight: true);

            Assert.AreEqual(
                AlgorithmicTransferStartDisposition.MoveRequested,
                start.Disposition);
            Assert.IsTrue(reservedBeforeMove);
            Assert.AreEqual(
                AlgorithmicTransferArrivalDisposition.Materialized,
                arrival!.Disposition,
                arrival.DiagnosticReason);
            Assert.AreSame(incoming, harness.TargetState.Master);
            CollectionAssert.AreEqual(
                new[] { sourceMaster, sourceSatellite },
                sourceTree.Root!.Windows
                    .Select(node => node.WindowReference)
                    .ToArray());
            Assert.AreEqual(0, harness.Coordinator.ReservationCount);
            harness.AssertCanonical(harness.Source, harness.SourceState);
            harness.AssertCanonical(harness.Target, harness.TargetState);
        }

        [TestMethod]
        public void NewSatelliteMinSizeFailureOverflowsWithoutPartialSourceMutation()
        {
            using var harness = new Harness(maxSatellites: 3);
            var sourceMaster = harness.CreateWindow(
                "wide-source-master",
                minimumWidth: 700);
            var incoming = harness.CreateWindow(
                "wide-incoming-satellite",
                minimumWidth: 700);
            harness.RegisterSourceMaster(
                sourceMaster,
                Rectangle.OffsetAndSize(11, 12, 700, 400));
            harness.PublishTargetCapacity();
            var sourceTree = harness.Backend.GetTree(harness.Source)!;
            var sourceBefore = harness.Engine.CreateSnapshot(
                sourceTree,
                harness.SourceState);
            long sourceRevision = harness.SourceState.Revision;

            var rejected = harness.Lifecycle.PlaceWindow(
                harness.Backend,
                harness.Source,
                incoming);

            Assert.AreEqual(
                MasterSatelliteLocalMutationDisposition.Rejected,
                rejected.Disposition);
            Assert.AreEqual(
                MasterSatelliteFailureReason.MinSizeConflict,
                rejected.Operation!.FailureReason);
            Assert.IsNull(sourceTree.FindNode(incoming));
            Assert.AreEqual(sourceRevision, harness.SourceState.Revision);
            Assert.AreEqual(
                sourceBefore.TreeDescription,
                harness.Engine.CreateSnapshot(
                    sourceTree,
                    harness.SourceState).TreeDescription);

            AlgorithmicTransferArrivalResult? arrival = null;
            harness.TargetMove = movedWindow =>
            {
                Assert.AreEqual(1, harness.Coordinator.ReservationCount);
                Assert.IsNull(sourceTree.FindNode(incoming));
                Assert.IsTrue(harness.Orchestrator.ObserveSourceRemoved(
                    movedWindow.Handle,
                    harness.Source,
                    out _));
                arrival = harness.ObserveDestinationAdded(movedWindow);
            };

            var start = harness.Start(
                incoming,
                Rectangle.OffsetAndSize(13, 14, 700, 400),
                useRealPreflight: true);

            Assert.AreEqual(
                AlgorithmicTransferStartDisposition.MoveRequested,
                start.Disposition);
            Assert.AreEqual(
                AlgorithmicTransferArrivalDisposition.Materialized,
                arrival!.Disposition,
                arrival.DiagnosticReason);
            Assert.AreSame(sourceMaster, harness.SourceState.Master);
            Assert.AreEqual(0, harness.SourceState.Satellites.Count);
            Assert.AreSame(incoming, harness.TargetState.Master);
            Assert.AreEqual(0, harness.Coordinator.ReservationCount);
            harness.AssertCanonical(harness.Source, harness.SourceState);
            harness.AssertCanonical(harness.Target, harness.TargetState);
        }

        [TestMethod]
        public void RejectedRealMaterializationIsAtomicAndLeavesCanonicalTarget()
        {
            using var harness = new Harness(maxSatellites: 2);
            var transferred = harness.CreateWindow("wide-transfer", minimumWidth: 700);
            var targetMaster = harness.CreateWindow("wide-target-master", minimumWidth: 700);
            var original = Rectangle.OffsetAndSize(31, 47, 451, 351);
            harness.RegisterSourceMaster(transferred, original);
            harness.RegisterTargetMaster(targetMaster);
            harness.PublishTargetCapacity();

            var targetTree = harness.Backend.GetTree(harness.Target)!;
            var rootBefore = targetTree.Root;
            var snapshotBefore = harness.Engine.CreateSnapshot(targetTree, harness.TargetState);
            long revisionBefore = harness.TargetState.Revision;
            AlgorithmicTransferArrivalResult? arrival = null;
            harness.TargetMove = movedWindow =>
                arrival = harness.ObserveDestinationAdded(movedWindow);

            // Reservation/correlation succeeds against physical capacity. The real
            // transactional placement then rejects the two incompatible minima.
            var start = harness.Start(transferred, original, useRealPreflight: false);

            Assert.AreEqual(AlgorithmicTransferStartDisposition.MoveRequested, start.Disposition);
            Assert.IsNotNull(arrival);
            Assert.AreEqual(
                AlgorithmicTransferArrivalDisposition.MaterializationFailed,
                arrival!.Disposition);
            Assert.IsNotNull(harness.LastPlacement);
            Assert.IsFalse(harness.LastPlacement!.Succeeded);
            Assert.AreEqual(
                MasterSatelliteFailureReason.MinSizeConflict,
                harness.LastPlacement.Operation.FailureReason);
            Assert.AreEqual(1, harness.MaterializationCount);
            Assert.AreEqual(1, harness.ReservationCountAtMaterialization);
            Assert.AreEqual(1, harness.PhysicalCapacityPublicationCount,
                "A rejected placement must not publish a fictitious physical mutation.");

            Assert.IsNotNull(start.Transfer);
            Assert.IsTrue(harness.Coordinator.TryGetTransfer(
                start.Transfer!.CorrelationId,
                out var failed));
            Assert.AreEqual(PendingWindowTransferState.Failed, failed.State);
            Assert.AreEqual("DestinationMaterializationRejected", failed.TerminalReason);
            Assert.AreEqual(0, harness.Coordinator.ReservationCount);
            Assert.AreEqual(0, harness.Coordinator.ActiveTransferCount);

            Assert.AreSame(rootBefore, targetTree.Root);
            Assert.AreEqual(revisionBefore, harness.TargetState.Revision);
            Assert.AreEqual(
                snapshotBefore.TreeDescription,
                harness.Engine.CreateSnapshot(targetTree, harness.TargetState).TreeDescription);
            Assert.AreSame(targetMaster, harness.TargetState.Master);
            Assert.AreEqual(0, harness.TargetState.Satellites.Count);
            CollectionAssert.AreEqual(new[] { targetMaster }, harness.TargetWindows());
            Assert.IsNull(targetTree.FindNode(transferred));
            Assert.AreSame(transferred, harness.SourceState.Master);
            Assert.IsNotNull(harness.Backend.GetTree(harness.Source)!.FindNode(transferred));
            harness.AssertCanonical(harness.Source, harness.SourceState);
            harness.AssertCanonical(harness.Target, harness.TargetState);
            Assert.IsTrue(harness.Backend.TryGetOriginalPosition(transferred, out var retainedOriginal));
            Assert.AreEqual(original, retainedOriginal);

            Assert.IsTrue(harness.Coordinator.TryGetCapacity(
                harness.TargetKey,
                out var releasedCapacity));
            Assert.AreEqual(1, releasedCapacity.OccupiedSlots);
            Assert.AreEqual(0, releasedCapacity.ReservedSlots);
            Assert.IsTrue(releasedCapacity.CanAcceptWindow);
        }

        [TestMethod]
        public void OutOfOrderDestinationArrivalsMaterializeExactReservedSlotsInOrder()
        {
            using var harness = new Harness(maxSatellites: 2);
            var windows = new[]
            {
                harness.CreateWindow("master-transfer"),
                harness.CreateWindow("satellite-0-transfer"),
                harness.CreateWindow("satellite-1-transfer"),
            };
            var originals = new[]
            {
                Rectangle.OffsetAndSize(11, 21, 301, 201),
                Rectangle.OffsetAndSize(12, 22, 302, 202),
                Rectangle.OffsetAndSize(13, 23, 303, 203),
            };
            harness.RegisterSourceMaster(windows[0], originals[0]);
            harness.RegisterSourceSatellite(windows[1], 0, originals[1]);
            harness.RegisterSourceSatellite(windows[2], 1, originals[2]);
            harness.PublishTargetCapacity();
            harness.TargetMove = _ => { };

            var starts = windows
                .Select((window, index) => harness.Start(
                    window,
                    originals[index],
                    useRealPreflight: false))
                .ToArray();
            var transfers = starts.Select(start => start.Transfer!).ToArray();

            Assert.IsTrue(starts.All(start =>
                start.Disposition == AlgorithmicTransferStartDisposition.MoveRequested));
            CollectionAssert.AreEqual(
                new[] { ReservedRole.Master, ReservedRole.Satellite, ReservedRole.Satellite },
                transfers.Select(transfer => transfer.TargetRole).ToArray());
            CollectionAssert.AreEqual(
                new int?[] { null, 0, 1 },
                transfers.Select(transfer => transfer.TargetSatelliteIndex).ToArray());
            Assert.AreEqual(3, harness.Coordinator.ReservationCount);

            Assert.IsTrue(harness.RemoveSourceAndObserve(windows[2]));
            Assert.IsTrue(harness.RemoveSourceAndObserve(windows[1]));
            Assert.IsTrue(harness.RemoveSourceAndObserve(windows[0]));

            var satellite1Early = harness.ObserveDestinationAdded(windows[2]);
            var satellite0Early = harness.ObserveDestinationAdded(windows[1]);

            Assert.AreEqual(
                AlgorithmicTransferArrivalDisposition.AwaitingPrecedingSlot,
                satellite1Early.Disposition,
                satellite1Early.DiagnosticReason);
            Assert.AreEqual(
                AlgorithmicTransferArrivalDisposition.AwaitingPrecedingSlot,
                satellite0Early.Disposition,
                satellite0Early.DiagnosticReason);
            Assert.AreEqual(0, harness.MaterializationCount);

            var master = harness.ObserveDestinationAdded(windows[0]);
            var satellite0 = harness.ObserveDestinationAdded(windows[1]);
            var satellite1 = harness.ObserveDestinationAdded(windows[2]);

            Assert.AreEqual(
                AlgorithmicTransferArrivalDisposition.Materialized,
                master.Disposition,
                master.DiagnosticReason);
            Assert.AreEqual(
                AlgorithmicTransferArrivalDisposition.Materialized,
                satellite0.Disposition,
                satellite0.DiagnosticReason);
            Assert.AreEqual(
                AlgorithmicTransferArrivalDisposition.Materialized,
                satellite1.Disposition,
                satellite1.DiagnosticReason);
            Assert.AreEqual(3, harness.MaterializationCount);
            Assert.IsTrue(windows.All(window =>
                harness.MaterializationCountFor(window) == 1));

            Assert.AreSame(windows[0], harness.TargetState.Master);
            CollectionAssert.AreEqual(
                new[] { windows[1], windows[2] },
                harness.TargetState.Satellites.ToArray());
            CollectionAssert.AreEqual(windows, harness.TargetWindows());
            Assert.IsTrue(starts.All(start =>
            {
                Assert.IsNotNull(start.Transfer);
                return harness.Coordinator.TryGetTransfer(
                        start.Transfer!.CorrelationId,
                        out var committed)
                    && committed.State == PendingWindowTransferState.Committed;
            }));
            Assert.AreEqual(0, harness.Coordinator.ReservationCount);
            Assert.AreEqual(0, harness.Coordinator.ActiveTransferCount);
            harness.AssertCanonical(harness.Target, harness.TargetState);
            Assert.AreEqual(
                0,
                harness.Backend.GetTree(harness.Source)!.Root?.Windows.Count() ?? 0);
            Assert.IsNull(harness.SourceState.Master);
            Assert.AreEqual(0, harness.SourceState.Satellites.Count);
            harness.AssertCanonical(harness.Source, harness.SourceState);

            for (int index = 0; index < windows.Length; index++)
            {
                Assert.IsTrue(harness.Backend.TryGetOriginalPosition(
                    windows[index],
                    out var original));
                Assert.AreEqual(originals[index], original);
            }
        }

        [TestMethod]
        public void PredecessorFailureCancelsAlreadyObservedDependentsWithoutMaterializingThem()
        {
            using var harness = new Harness(maxSatellites: 2);
            var windows = new[]
            {
                harness.CreateWindow("failed-master"),
                harness.CreateWindow("cancelled-satellite-0"),
                harness.CreateWindow("cancelled-satellite-1"),
            };
            harness.PublishTargetCapacity();
            harness.TargetMove = _ => { };
            var starts = windows
                .Select(window => harness.Start(
                    window,
                    Rectangle.OffsetAndSize(1, 2, 300, 200),
                    useRealPreflight: false))
                .ToArray();

            var satellite1Early = harness.ObserveDestinationAdded(windows[2]);
            var satellite0Early = harness.ObserveDestinationAdded(windows[1]);
            Assert.AreEqual(
                AlgorithmicTransferArrivalDisposition.AwaitingPrecedingSlot,
                satellite1Early.Disposition);
            Assert.AreEqual(
                AlgorithmicTransferArrivalDisposition.AwaitingPrecedingSlot,
                satellite0Early.Disposition);
            Assert.AreEqual(0, harness.MaterializationCount);

            int predecessorMaterializerCalls = 0;
            var failedMaster = harness.Orchestrator.ObserveDestinationAdded(
                windows[0],
                harness.Target,
                (_, _) =>
                {
                    predecessorMaterializerCalls++;
                    return AlgorithmicTransferMaterializationResult.Reject(
                        "Injected predecessor failure.");
                });

            Assert.AreEqual(
                AlgorithmicTransferArrivalDisposition.MaterializationFailed,
                failedMaster.Disposition);
            Assert.AreEqual(1, predecessorMaterializerCalls);
            Assert.AreEqual(0, harness.MaterializationCount);
            Assert.AreEqual(0, harness.Coordinator.ReservationCount);
            Assert.AreEqual(0, harness.Coordinator.ActiveTransferCount);

            Assert.IsTrue(harness.Coordinator.TryGetTransfer(
                starts[0].Transfer!.CorrelationId,
                out var master));
            Assert.IsTrue(harness.Coordinator.TryGetTransfer(
                starts[1].Transfer!.CorrelationId,
                out var satellite0));
            Assert.IsTrue(harness.Coordinator.TryGetTransfer(
                starts[2].Transfer!.CorrelationId,
                out var satellite1));
            Assert.AreEqual(PendingWindowTransferState.Failed, master.State);
            Assert.AreEqual(
                "DestinationMaterializationRejected",
                master.TerminalReason);
            Assert.AreEqual(PendingWindowTransferState.Cancelled, satellite0.State);
            Assert.AreEqual(PendingWindowTransferState.Cancelled, satellite1.State);
            Assert.AreEqual("PrecedingReservationReleased", satellite0.TerminalReason);
            Assert.AreEqual("PrecedingReservationReleased", satellite1.TerminalReason);
            Assert.IsTrue(satellite0.DestinationAddedObserved);
            Assert.IsTrue(satellite1.DestinationAddedObserved);

            var satellite0Retry = harness.ObserveDestinationAdded(windows[1]);
            var satellite1Retry = harness.ObserveDestinationAdded(windows[2]);
            Assert.AreEqual(
                AlgorithmicTransferArrivalDisposition.AlreadyTerminal,
                satellite0Retry.Disposition);
            Assert.AreEqual(
                AlgorithmicTransferArrivalDisposition.AlreadyTerminal,
                satellite1Retry.Disposition);
            Assert.AreEqual(0, harness.MaterializationCount);
            Assert.IsNull(harness.TargetState.Master);
            Assert.AreEqual(0, harness.TargetState.Satellites.Count);
            harness.AssertCanonical(harness.Target, harness.TargetState);
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
            private readonly System.Collections.Generic.Dictionary<IntPtr, int>
                m_materializationCounts = [];
            private readonly AlgorithmicLayoutDisplayRegistration m_registration;
            private long m_capacityPublicationSequence;

            public MasterSatelliteLayoutEngine Engine { get; }

            public TilingWorkspace Backend { get; }

            public AlgorithmicLayoutCoordinator Coordinator { get; }

            public MasterSatelliteRuntimeLifecycle Lifecycle { get; }

            public AlgorithmicWindowTransferOrchestrator Orchestrator { get; }

            public IVirtualDesktop Source => m_source.Object;

            public IVirtualDesktop Target => m_target.Object;

            public IDisplay Display => m_display.Object;

            public LayoutStateKey TargetKey { get; }

            public MasterSatelliteRuntimeState SourceState { get; }

            public MasterSatelliteRuntimeState TargetState { get; }

            public MasterSatelliteLayoutSettings Settings => Lifecycle.SettingsSnapshot;

            public Action<IWindow>? TargetMove { get; set; }

            public MasterSatellitePlacementResult? LastPlacement { get; private set; }

            public int MaterializationCount { get; private set; }

            public int ReservationCountAtMaterialization { get; private set; }

            public int PhysicalCapacityPublicationCount { get; private set; }

            public PendingWindowTransferState? TransferStateAtPhysicalCapacityPublication
            {
                get;
                private set;
            }

            public Harness(int maxSatellites)
            {
                ConfigureDesktop(m_source, "Source");
                ConfigureDesktop(m_target, "Target");
                m_display.SetupGet(display => display.Workspace).Returns(m_workspace.Object);
                m_display.SetupGet(display => display.WorkArea).Returns(WorkArea);
                var display = Display;
                m_display.Setup(candidate => candidate.Equals(It.IsAny<IDisplay>()))
                    .Returns((IDisplay other) => ReferenceEquals(display, other));

                m_workspace.SetupGet(workspace => workspace.VirtualDesktopManager)
                    .Returns(m_virtualDesktopManager.Object);
                m_virtualDesktopManager.SetupGet(
                        manager => manager.CanManageVirtualDesktops)
                    .Returns(true);
                m_virtualDesktopManager.SetupGet(manager => manager.Desktops)
                    .Returns(new[] { Source, Target });
                m_target.Setup(desktop => desktop.MoveWindow(It.IsAny<IWindow>()))
                    .Callback<IWindow>(window => TargetMove?.Invoke(window));

                Engine = new MasterSatelliteLayoutEngine();
                Backend = new TilingWorkspace(Engine);
                Backend.RegisterDesktop(Source, WorkArea, PanelOrientation.Horizontal);
                Backend.RegisterDesktop(Target, WorkArea, PanelOrientation.Horizontal);

                Coordinator = new AlgorithmicLayoutCoordinator(
                    m_workspace.Object,
                    Dispatcher.CurrentDispatcher);
                Lifecycle = new MasterSatelliteRuntimeLifecycle(Display, Coordinator);
                m_registration = Coordinator.RegisterDisplay(Display, Lifecycle);
                var settings = new MasterSatelliteLayoutSettings
                {
                    Enabled = true,
                    DisplayScope = AlgorithmicLayoutDisplayScope.AllDisplays,
                    MasterRatio = 0.60,
                    DefaultMasterSide = MasterSide.Left,
                    DefaultSatelliteOrientation = SatelliteLayoutOrientation.Vertical,
                    MaxSatellites = maxSatellites,
                };
                var settingsTransition = Lifecycle.CacheSettings(settings, Display);
                Assert.IsTrue(settingsTransition.IsEligible);
                Assert.IsTrue(Lifecycle.DesktopAdded(Backend, Source, Display).StateAdded);
                Assert.IsTrue(Lifecycle.DesktopAdded(Backend, Target, Display).StateAdded);
                Assert.IsTrue(Lifecycle.TryGetState(Source, out var sourceState));
                Assert.IsTrue(Lifecycle.TryGetState(Target, out var targetState));
                SourceState = sourceState;
                TargetState = targetState;
                TargetKey = new LayoutStateKey(Target, Display);
                Assert.IsTrue(Coordinator.TryGet(
                    new LayoutStateKey(Source, Display),
                    out var registrySource));
                Assert.IsTrue(Coordinator.TryGet(TargetKey, out var registryTarget));
                Assert.AreSame(SourceState, registrySource);
                Assert.AreSame(TargetState, registryTarget);

                Orchestrator = new AlgorithmicWindowTransferOrchestrator(
                    Coordinator,
                    Display);
            }

            public IWindow CreateWindow(string title, int minimumWidth = 0)
            {
                return m_windows.Create(title, minimumWidth: minimumWidth);
            }

            public void RegisterSourceMaster(IWindow window, Rectangle originalPosition)
            {
                var placement = Backend.RegisterMaster(
                    Source,
                    SourceState,
                    Settings,
                    window,
                    originalPosition);
                Assert.IsTrue(placement.Succeeded, placement.Message);
            }

            public void RegisterSourceSatellite(
                IWindow window,
                int index,
                Rectangle originalPosition)
            {
                var placement = Backend.RegisterSatellite(
                    Source,
                    SourceState,
                    Settings,
                    window,
                    index,
                    originalPosition);
                Assert.IsTrue(placement.Succeeded, placement.Message);
            }

            public void RegisterTargetMaster(IWindow window)
            {
                var placement = Backend.RegisterMaster(
                    Target,
                    TargetState,
                    Settings,
                    window);
                Assert.IsTrue(placement.Succeeded, placement.Message);
            }

            public void RegisterTargetSatellite(IWindow window, int index)
            {
                var placement = Backend.RegisterSatellite(
                    Target,
                    TargetState,
                    Settings,
                    window,
                    index);
                Assert.IsTrue(placement.Succeeded, placement.Message);
            }

            public AlgorithmicTransferStartResult Start(
                IWindow window,
                Rectangle originalPosition,
                bool useRealPreflight)
            {
                Func<LayoutStateKey, CoordinatorCapacitySnapshot,
                    AlgorithmicDestinationPreflightResult> preflight = useRealPreflight
                        ? Preflight
                        : static (_, _) => AlgorithmicDestinationPreflightResult.Accept();
                m_windowBeingStarted = window;
                try
                {
                    return Orchestrator.StartExistingDesktopTransfer(
                        Guid.NewGuid(),
                        window,
                        Source,
                        originalPosition,
                        preflight);
                }
                finally
                {
                    m_windowBeingStarted = null;
                }
            }

            public bool RemoveSourceAndObserve(IWindow window)
            {
                var removal = Backend.UnregisterMasterSatelliteWindow(
                    Source,
                    SourceState,
                    Settings,
                    window,
                    preserveOriginalPosition: true);
                Assert.IsTrue(removal.Succeeded, removal.Message);
                return Orchestrator.ObserveSourceRemoved(
                    window.Handle,
                    Source,
                    out _);
            }

            public AlgorithmicTransferArrivalResult ObserveDestinationAdded(IWindow window)
            {
                return Orchestrator.ObserveDestinationAdded(
                    window,
                    Target,
                    (transfer, reservation) => Materialize(
                        window,
                        transfer,
                        reservation));
            }

            public void PublishTargetCapacity()
            {
                var physical = Backend.QueryMasterSatelliteCapacity(
                    Target,
                    TargetState,
                    Settings);
                Assert.IsTrue(Coordinator.PublishCapacity(
                    TargetKey,
                    physical,
                    ++m_capacityPublicationSequence));
                PhysicalCapacityPublicationCount++;
            }

            public IWindow[] TargetWindows()
            {
                return Backend.GetTree(Target)!.Root?.Windows
                    .Select(node => node.WindowReference)
                    .ToArray() ?? Array.Empty<IWindow>();
            }

            public int MaterializationCountFor(IWindow window)
            {
                return m_materializationCounts.TryGetValue(window.Handle, out int count)
                    ? count
                    : 0;
            }

            public void AssertCanonical(
                IVirtualDesktop desktop,
                MasterSatelliteRuntimeState state)
            {
                var invariant = Backend.ValidateMasterSatelliteLayout(
                    desktop,
                    state,
                    Settings);
                Assert.IsTrue(invariant.IsValid, invariant.Description);
            }

            public void Dispose()
            {
                m_registration.Dispose();
                Coordinator.Dispose();
            }

            private void ConfigureDesktop(Mock<IVirtualDesktop> desktop, string name)
            {
                desktop.SetupGet(candidate => candidate.Name).Returns(name);
                desktop.SetupGet(candidate => candidate.IsAlive).Returns(true);
                desktop.SetupGet(candidate => candidate.Workspace).Returns(m_workspace.Object);
            }

            private AlgorithmicDestinationPreflightResult Preflight(
                LayoutStateKey key,
                CoordinatorCapacitySnapshot capacity)
            {
                Assert.IsTrue(Lifecycle.TryGetState(key.VirtualDesktop, out var state));
                Assert.IsNotNull(capacity.NextRole);
                var placement = Backend.PreflightMasterSatellitePlacement(
                    key.VirtualDesktop,
                    state,
                    Settings,
                    LastWindowForPreflight(),
                    capacity.NextRole == ReservedRole.Master
                        ? MasterSatelliteWindowRole.Master
                        : MasterSatelliteWindowRole.Satellite,
                    capacity.NextSatelliteIndex);
                return placement.Succeeded
                    ? AlgorithmicDestinationPreflightResult.Accept()
                    : AlgorithmicDestinationPreflightResult.Reject(
                        placement.Message ?? "The real placement preflight was rejected.");
            }

            private IWindow? m_windowBeingStarted;

            private IWindow LastWindowForPreflight()
            {
                return m_windowBeingStarted
                    ?? throw new InvalidOperationException(
                        "The transfer window was not captured for preflight.");
            }

            private AlgorithmicTransferMaterializationResult Materialize(
                IWindow window,
                PendingWindowTransfer transfer,
                AlgorithmicSlotReservation reservation)
            {
                MaterializationCount++;
                m_materializationCounts[window.Handle] =
                    MaterializationCountFor(window) + 1;
                ReservationCountAtMaterialization = Coordinator.ReservationCount;
                LastPlacement = Backend.RegisterReservedWindow(
                    reservation.LayoutKey,
                    TargetState,
                    Settings,
                    window,
                    reservation.ToWorkspaceSlot(),
                    transfer.SourceOriginalPosition);
                if (!LastPlacement.Succeeded)
                {
                    return AlgorithmicTransferMaterializationResult.Reject(
                        LastPlacement.Message
                            ?? "The real reserved placement was rejected.");
                }

                var invariant = Backend.ValidateMasterSatelliteLayout(
                    transfer.TargetDesktop,
                    TargetState,
                    Settings);
                if (!invariant.IsValid)
                {
                    return AlgorithmicTransferMaterializationResult.Reject(
                        invariant.Description);
                }

                PublishTargetCapacity();
                Assert.IsTrue(Coordinator.TryGetTransfer(
                    transfer.CorrelationId,
                    out var transferAtPublication));
                TransferStateAtPhysicalCapacityPublication = transferAtPublication.State;
                return AlgorithmicTransferMaterializationResult.Accept();
            }
        }
    }
}
