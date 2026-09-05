#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;

using FancyWM.AlgorithmicLayouts;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    [TestClass]
    public class AlgorithmicWindowTransferOrchestratorTest
    {
        [TestMethod]
        public void StartRunsPreflightAndReservationBeforeMoveWindow()
        {
            using var harness = new Harness(totalCapacity: 2);
            var window = harness.CreateWindow(101);
            var order = new List<string>();
            int reservationCountAtPreflight = -1;
            int reservationCountAtMove = -1;
            PendingWindowTransferState? stateAtMove = null;
            bool coordinatorLockHeldAtMove = true;
            harness.TargetMove = movedWindow =>
            {
                order.Add("move");
                coordinatorLockHeldAtMove = harness.Coordinator.IsMutationLockHeldByCurrentThread;
                reservationCountAtMove = harness.Coordinator.ReservationCount;
                if (harness.Coordinator.TryGetActiveTransfer(
                        movedWindow.Handle,
                        out var active))
                {
                    stateAtMove = active.State;
                }
                harness.MoveToTarget(movedWindow);
            };

            var result = harness.Orchestrator.StartExistingDesktopTransfer(
                Guid.NewGuid(),
                window,
                harness.Source,
                Rectangle.OffsetAndSize(10, 20, 300, 200),
                (_, _) =>
                {
                    order.Add("preflight");
                    reservationCountAtPreflight = harness.Coordinator.ReservationCount;
                    return AlgorithmicDestinationPreflightResult.Accept();
                });

            Assert.AreEqual(AlgorithmicTransferStartDisposition.MoveRequested, result.Disposition);
            CollectionAssert.AreEqual(new[] { "preflight", "move" }, order);
            Assert.AreEqual(0, reservationCountAtPreflight);
            Assert.AreEqual(1, reservationCountAtMove);
            Assert.AreEqual(PendingWindowTransferState.Moving, stateAtMove);
            Assert.IsFalse(coordinatorLockHeldAtMove);
        }

        [TestMethod]
        public void PlanAndExecuteBoundaryDoesNotMoveUntilReservedTransferIsExecuted()
        {
            using var harness = new Harness(totalCapacity: 2);
            var window = harness.CreateWindow(111);
            int moveCount = 0;
            harness.TargetMove = movedWindow =>
            {
                moveCount++;
                harness.MoveToTarget(movedWindow);
            };

            var search = harness.Orchestrator.PlanExistingDesktopTransfer(
                Guid.NewGuid(),
                window,
                window.Handle,
                harness.Source,
                Rectangle.OffsetAndSize(10, 20, 300, 200),
                (_, _) => AlgorithmicDestinationPreflightResult.Accept());

            Assert.IsTrue(search.Succeeded);
            Assert.AreEqual(0, moveCount);
            Assert.AreEqual(1, harness.Coordinator.ReservationCount);
            Assert.AreEqual(
                PendingWindowTransferState.Reserved,
                search.Transfer!.State);

            var result = harness.Orchestrator.ExecuteReservedTransfer(
                window,
                window.Handle,
                search);

            Assert.AreEqual(
                AlgorithmicTransferStartDisposition.MoveRequested,
                result.Disposition);
            Assert.AreEqual(1, moveCount);
        }

        [TestMethod]
        public void CommitRejectionInvokesEndpointRollbackExactlyOnce()
        {
            using var harness = new Harness(totalCapacity: 2);
            var window = harness.CreateWindow(121);
            AlgorithmicTransferArrivalResult? arrival = null;
            int rollbackCount = 0;
            harness.TargetMove = movedWindow =>
            {
                harness.MoveToTarget(movedWindow);
                arrival = harness.Orchestrator.ObserveDestinationAdded(
                    movedWindow,
                    movedWindow.Handle,
                    harness.Target,
                    (transfer, _) =>
                    {
                        Assert.IsTrue(harness.Coordinator.Cancel(
                            transfer.CorrelationId,
                            transfer.WindowHandle,
                            "InjectedCommitRace"));
                        return AlgorithmicTransferMaterializationResult.Accept();
                    },
                    (_, _) => rollbackCount++);
            };

            var start = harness.Start(window);

            Assert.AreEqual(
                AlgorithmicTransferStartDisposition.MoveRequested,
                start.Disposition);
            Assert.IsNotNull(arrival);
            Assert.AreEqual(
                AlgorithmicTransferArrivalDisposition.StateTransitionRejected,
                arrival!.Disposition);
            Assert.AreEqual(1, rollbackCount);
            Assert.AreEqual(0, harness.Coordinator.ReservationCount);
        }

        [TestMethod]
        public void UnavailableVirtualDesktopManagerDoesNotReserveOrMoveWindow()
        {
            using var harness = new Harness(totalCapacity: 1)
            {
                CanManageVirtualDesktops = false,
            };
            var window = harness.CreateWindow(351);
            bool moveCalled = false;
            harness.TargetMove = _ => moveCalled = true;

            var result = harness.Start(window);

            Assert.AreEqual(
                AlgorithmicTransferStartDisposition.NoDestination,
                result.Disposition);
            StringAssert.Contains(
                result.Search.DiagnosticReason,
                "unavailable");
            Assert.AreEqual(0, result.Search.ExaminedCandidates.Count);
            Assert.IsNull(result.Transfer);
            Assert.IsFalse(moveCalled);
            Assert.IsTrue(harness.SourceHas(window));
            Assert.AreEqual(0, harness.Coordinator.ReservationCount);
            Assert.AreEqual(0, harness.Coordinator.ActiveTransferCount);
        }

        [TestMethod]
        public void StartUsesSuppliedStableHandleWithoutRereadingWindowHandle()
        {
            using var harness = new Harness(totalCapacity: 2);
            var window = new Mock<IWindow>(MockBehavior.Loose);
            window.SetupGet(item => item.Handle)
                .Throws(new InvalidWindowReferenceException(new IntPtr(151)));
            window.SetupGet(item => item.IsAlive).Returns(true);

            var result = harness.Orchestrator.StartExistingDesktopTransfer(
                Guid.NewGuid(),
                window.Object,
                new IntPtr(151),
                harness.Source,
                Rectangle.OffsetAndSize(10, 20, 300, 200),
                (_, _) => AlgorithmicDestinationPreflightResult.Accept());

            Assert.AreEqual(
                AlgorithmicTransferStartDisposition.MoveRequested,
                result.Disposition);
            Assert.IsNotNull(result.Transfer);
            Assert.AreEqual(new IntPtr(151), result.Transfer!.WindowHandle);
            window.VerifyGet(item => item.Handle, Times.Never);
        }

        [TestMethod]
        public void DestinationArrivalUsesSuppliedStableHandleWithoutRereadingWindowHandle()
        {
            using var harness = new Harness(totalCapacity: 2);
            var stableHandle = new IntPtr(176);
            var window = new Mock<IWindow>(MockBehavior.Loose);
            window.SetupGet(item => item.Handle)
                .Throws(new InvalidWindowReferenceException(stableHandle));
            window.SetupGet(item => item.IsAlive).Returns(true);
            AlgorithmicTransferArrivalResult? arrival = null;
            harness.TargetMove = movedWindow =>
            {
                arrival = harness.Orchestrator.ObserveDestinationAdded(
                    movedWindow,
                    stableHandle,
                    harness.Target,
                    (_, _) => AlgorithmicTransferMaterializationResult.Accept());
            };

            var result = harness.Orchestrator.StartExistingDesktopTransfer(
                Guid.NewGuid(),
                window.Object,
                stableHandle,
                harness.Source,
                Rectangle.OffsetAndSize(10, 20, 300, 200),
                (_, _) => AlgorithmicDestinationPreflightResult.Accept());

            Assert.AreEqual(
                AlgorithmicTransferStartDisposition.MoveRequested,
                result.Disposition);
            Assert.IsNotNull(arrival);
            Assert.AreEqual(
                AlgorithmicTransferArrivalDisposition.Materialized,
                arrival!.Disposition);
            window.VerifyGet(item => item.Handle, Times.Never);
        }

        [TestMethod]
        public void SynchronousRemovedThenAddedCommitsBothObservations()
        {
            using var harness = new Harness(totalCapacity: 2);
            var window = harness.CreateWindow(201);
            bool sourceObserved = false;
            AlgorithmicTransferArrivalResult? arrival = null;
            harness.TargetMove = movedWindow =>
            {
                harness.RemoveFromSource(movedWindow);
                sourceObserved = harness.Orchestrator.ObserveSourceRemoved(
                    movedWindow.Handle,
                    harness.Source,
                    out _);
                harness.AddToTarget(movedWindow);
                arrival = harness.Orchestrator.ObserveDestinationAdded(
                    movedWindow,
                    harness.Target,
                    (_, _) => AlgorithmicTransferMaterializationResult.Accept());
            };

            var start = harness.Start(window);

            Assert.AreEqual(AlgorithmicTransferStartDisposition.MoveRequested, start.Disposition);
            Assert.IsTrue(sourceObserved);
            Assert.IsNotNull(arrival);
            var completedArrival = arrival!;
            Assert.AreEqual(AlgorithmicTransferArrivalDisposition.Materialized, completedArrival.Disposition);
            Assert.AreEqual(PendingWindowTransferState.Committed, completedArrival.Transfer!.State);
            Assert.IsTrue(completedArrival.Transfer.SourceRemovedObserved);
            Assert.IsTrue(completedArrival.Transfer.DestinationAddedObserved);
            Assert.AreEqual(0, harness.Coordinator.ReservationCount);
            Assert.AreEqual(0, harness.Coordinator.ActiveTransferCount);
        }

        [TestMethod]
        public void SynchronousAddedThenRemovedUsesCommittedCorrelation()
        {
            using var harness = new Harness(totalCapacity: 2);
            var window = harness.CreateWindow(301);
            AlgorithmicTransferArrivalResult? arrival = null;
            bool sourceObservedAfterCommit = false;
            PendingWindowTransfer? observed = null;
            harness.TargetMove = movedWindow =>
            {
                harness.AddToTarget(movedWindow);
                arrival = harness.Orchestrator.ObserveDestinationAdded(
                    movedWindow,
                    harness.Target,
                    (_, _) => AlgorithmicTransferMaterializationResult.Accept());
                harness.RemoveFromSource(movedWindow);
                sourceObservedAfterCommit = harness.Orchestrator.ObserveSourceRemoved(
                    movedWindow.Handle,
                    harness.Source,
                    out observed!);
            };

            var start = harness.Start(window);

            Assert.AreEqual(AlgorithmicTransferStartDisposition.MoveRequested, start.Disposition);
            Assert.AreEqual(AlgorithmicTransferArrivalDisposition.Materialized, arrival!.Disposition);
            Assert.IsTrue(sourceObservedAfterCommit);
            Assert.IsNotNull(observed);
            var observedTransfer = observed!;
            Assert.AreEqual(PendingWindowTransferState.Committed, observedTransfer.State);
            Assert.IsTrue(observedTransfer.SourceRemovedObserved);
            Assert.IsTrue(observedTransfer.DestinationAddedObserved);
            Assert.AreEqual(0, harness.Coordinator.ReservationCount);
        }

        [TestMethod]
        public void DuplicateAddedAndRemovedCallbacksAreIdempotent()
        {
            using var harness = new Harness(totalCapacity: 2);
            var window = harness.CreateWindow(401);
            int materializationCount = 0;
            var arrivals = new List<AlgorithmicTransferArrivalResult>();
            var sourceObservations = new List<bool>();
            harness.TargetMove = movedWindow =>
            {
                harness.AddToTarget(movedWindow);
                for (int i = 0; i < 2; i++)
                {
                    arrivals.Add(harness.Orchestrator.ObserveDestinationAdded(
                        movedWindow,
                        harness.Target,
                        (_, _) =>
                        {
                            materializationCount++;
                            return AlgorithmicTransferMaterializationResult.Accept();
                        }));
                }
                harness.RemoveFromSource(movedWindow);
                for (int i = 0; i < 2; i++)
                {
                    sourceObservations.Add(harness.Orchestrator.ObserveSourceRemoved(
                        movedWindow.Handle,
                        harness.Source,
                        out _));
                }
            };

            harness.Start(window);

            Assert.AreEqual(1, materializationCount);
            CollectionAssert.AreEqual(
                new[]
                {
                    AlgorithmicTransferArrivalDisposition.Materialized,
                    AlgorithmicTransferArrivalDisposition.AlreadyTerminal,
                },
                arrivals.Select(item => item.Disposition).ToArray());
            CollectionAssert.AreEqual(new[] { true, true }, sourceObservations);
            Assert.IsTrue(arrivals[1].Consumed);
            Assert.AreEqual(0, harness.Coordinator.ReservationCount);
        }

        [TestMethod]
        public void FollowPolicyNeverSwitchesBeforeCommitAndHonorsDisabledMode()
        {
            using var harness = new Harness(totalCapacity: 2);
            var window = harness.CreateWindow(451);
            var start = harness.Start(window);
            Assert.IsNotNull(start.Transfer);
            Assert.IsTrue(harness.Coordinator.TryGetTransfer(
                start.Transfer!.CorrelationId,
                out var moving));

            var disabled = harness.Orchestrator.FollowCommittedTransfer(
                moving,
                followRequested: false);
            var premature = harness.Orchestrator.FollowCommittedTransfer(
                moving,
                followRequested: true);

            Assert.IsFalse(disabled.Requested);
            Assert.IsFalse(disabled.Switched);
            Assert.IsTrue(premature.Requested);
            Assert.IsFalse(premature.Switched);
            Mock.Get(harness.Target).Verify(
                desktop => desktop.SwitchTo(),
                Times.Never);

            var arrival = harness.Orchestrator.ObserveDestinationAdded(
                window,
                harness.Target,
                (_, _) => AlgorithmicTransferMaterializationResult.Accept());
            Assert.AreEqual(
                AlgorithmicTransferArrivalDisposition.Materialized,
                arrival.Disposition);

            var followed = harness.Orchestrator.FollowCommittedTransfer(
                arrival.Transfer!,
                followRequested: true);

            Assert.IsTrue(followed.Requested);
            Assert.IsTrue(followed.Switched);
            Assert.IsNull(followed.Exception);
            Mock.Get(harness.Target).Verify(
                desktop => desktop.SwitchTo(),
                Times.Once);
        }

        [TestMethod]
        public void PostMoveReconciliationCommitsWithoutWorkspaceWindowEvents()
        {
            using var harness = new Harness(totalCapacity: 1);
            var window = harness.CreateWindow(475);
            harness.TargetMove = harness.MoveToTarget;

            var start = harness.Start(window);

            Assert.AreEqual(
                AlgorithmicTransferStartDisposition.MoveRequested,
                start.Disposition);
            Assert.IsNotNull(start.Transfer);
            Assert.AreEqual(
                PendingWindowTransferState.Moving,
                harness.GetTransfer(start).State);

            int destinationCallbackCount = 0;
            AlgorithmicTransferArrivalResult? arrival = null;
            var reconciliation = harness.Orchestrator.ReconcileAfterMove(
                window,
                window.Handle,
                start.Transfer!,
                (candidate, stableHandle, desktop) =>
                {
                    destinationCallbackCount++;
                    arrival = harness.Orchestrator.ObserveDestinationAdded(
                        candidate,
                        stableHandle,
                        desktop,
                        (_, _) => AlgorithmicTransferMaterializationResult.Accept());
                    return arrival.Consumed;
                });

            Assert.AreEqual(
                AlgorithmicTransferReconciliationDisposition.DestinationConsumed,
                reconciliation.Disposition,
                reconciliation.DiagnosticReason);
            Assert.IsTrue(reconciliation.TargetOwnershipConfirmed);
            Assert.IsTrue(reconciliation.SourceRemovalObserved);
            Assert.AreEqual(1, destinationCallbackCount);
            Assert.IsNotNull(arrival);
            Assert.AreEqual(
                AlgorithmicTransferArrivalDisposition.Materialized,
                arrival!.Disposition,
                arrival.DiagnosticReason);
            Assert.IsNotNull(arrival.Transfer);
            Assert.AreEqual(
                PendingWindowTransferState.Committed,
                arrival.Transfer!.State);
            Assert.IsTrue(arrival.Transfer.SourceRemovedObserved);
            Assert.IsTrue(arrival.Transfer.DestinationAddedObserved);
            Assert.AreEqual(ReservedRole.Master, arrival.Transfer.TargetRole);
            Assert.IsNull(arrival.Transfer.TargetSatelliteIndex);
            Assert.AreEqual(0, harness.Coordinator.ReservationCount);
            Assert.AreEqual(0, harness.Coordinator.ActiveTransferCount);
        }

        [TestMethod]
        public void PostMoveReconciliationCommitsWhenOwnershipAppearsOnThirdProbe()
        {
            using var harness = new Harness(totalCapacity: 1);
            var window = harness.CreateWindow(476);
            int ownershipProbeCount = 0;
            Mock.Get(harness.Target)
                .Setup(desktop => desktop.HasWindow(window))
                .Returns(() => ++ownershipProbeCount >= 3);

            var start = harness.Start(window);
            Assert.AreEqual(
                AlgorithmicTransferStartDisposition.MoveRequested,
                start.Disposition);
            AlgorithmicTransferArrivalResult? arrival = null;
            AlgorithmicTransferReconciliationResult? reconciliation = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                reconciliation = harness.Orchestrator.ReconcileAfterMove(
                    window,
                    window.Handle,
                    start.Transfer!,
                    (candidate, stableHandle, desktop) =>
                    {
                        arrival = harness.Orchestrator.ObserveDestinationAdded(
                            candidate,
                            stableHandle,
                            desktop,
                            (_, _) =>
                                AlgorithmicTransferMaterializationResult.Accept());
                        return arrival.Consumed;
                    });
                if (attempt < 2)
                {
                    Assert.AreEqual(
                        AlgorithmicTransferReconciliationDisposition.TargetNotObserved,
                        reconciliation.Disposition);
                    Assert.IsTrue(reconciliation.ShouldRetry);
                }
            }

            Assert.AreEqual(3, ownershipProbeCount);
            Assert.IsNotNull(reconciliation);
            Assert.AreEqual(
                AlgorithmicTransferReconciliationDisposition.DestinationConsumed,
                reconciliation!.Disposition,
                reconciliation.DiagnosticReason);
            Assert.IsNotNull(arrival);
            Assert.AreEqual(
                PendingWindowTransferState.Committed,
                arrival!.Transfer!.State);
            Assert.AreEqual(0, harness.Coordinator.ReservationCount);
            Assert.AreEqual(0, harness.Coordinator.ActiveTransferCount);
        }

        [TestMethod]
        public void MaterializerRejectionFailsTransferAndReleasesReservation()
        {
            using var harness = new Harness(totalCapacity: 1);
            var window = harness.CreateWindow(501);
            AlgorithmicTransferArrivalResult? arrival = null;
            harness.TargetMove = movedWindow =>
            {
                harness.MoveToTarget(movedWindow);
                arrival = harness.Orchestrator.ObserveDestinationAdded(
                    movedWindow,
                    harness.Target,
                    (_, _) => AlgorithmicTransferMaterializationResult.Reject(
                        "The reserved slot no longer fits."));
            };

            var start = harness.Start(window);

            Assert.AreEqual(AlgorithmicTransferStartDisposition.MoveRequested, start.Disposition);
            Assert.AreEqual(AlgorithmicTransferArrivalDisposition.MaterializationFailed, arrival!.Disposition);
            StringAssert.Contains(arrival.DiagnosticReason, "no longer fits");
            Assert.AreEqual(0, harness.Coordinator.ReservationCount);
            Assert.IsTrue(harness.Coordinator.TryGetTransfer(
                start.Transfer!.CorrelationId,
                out var failed));
            Assert.AreEqual(PendingWindowTransferState.Failed, failed.State);
            Assert.AreEqual("DestinationMaterializationRejected", failed.TerminalReason);
            Assert.AreEqual(PendingWindowTransferState.Failed, arrival.Transfer!.State);
            Assert.AreEqual("DestinationMaterializationRejected", arrival.Transfer.TerminalReason);
        }

        [TestMethod]
        public void MaterializerExceptionFailsTransferAndReleasesReservation()
        {
            using var harness = new Harness(totalCapacity: 1);
            var window = harness.CreateWindow(601);
            var exception = new InvalidOperationException("materializer failure");
            AlgorithmicTransferArrivalResult? arrival = null;
            harness.TargetMove = movedWindow =>
            {
                harness.MoveToTarget(movedWindow);
                arrival = harness.Orchestrator.ObserveDestinationAdded(
                    movedWindow,
                    harness.Target,
                    (_, _) => throw exception);
            };

            var start = harness.Start(window);

            Assert.AreEqual(AlgorithmicTransferArrivalDisposition.MaterializationFailed, arrival!.Disposition);
            Assert.AreSame(exception, arrival.Exception);
            Assert.AreEqual(0, harness.Coordinator.ReservationCount);
            Assert.IsTrue(harness.Coordinator.TryGetTransfer(
                start.Transfer!.CorrelationId,
                out var failed));
            Assert.AreEqual(PendingWindowTransferState.Failed, failed.State);
            Assert.AreEqual("DestinationMaterializationThrew", failed.TerminalReason);
        }

        [TestMethod]
        public void MoveWindowThrowBeforeMoveDoesNotAttemptUnnecessaryRollback()
        {
            using var harness = new Harness(totalCapacity: 1);
            var window = harness.CreateWindow(701);
            var exception = new InvalidOperationException("move failed before changing ownership");
            harness.TargetMove = _ => throw exception;

            var result = harness.Start(window);

            Assert.AreEqual(AlgorithmicTransferStartDisposition.MoveFailed, result.Disposition);
            Assert.AreSame(exception, result.MoveException);
            Assert.IsFalse(result.RollbackAttempted);
            Assert.IsFalse(result.RollbackSucceeded);
            Assert.AreEqual(0, harness.SourceMoveCount);
            Assert.AreEqual(0, harness.Coordinator.ReservationCount);
            Assert.AreEqual(PendingWindowTransferState.Failed,
                harness.GetTransfer(result).State);
            Assert.IsNotNull(result.Recovery);
            Assert.AreEqual(
                AlgorithmicTransferRecoveryDisposition.AlreadyOnSource,
                result.Recovery!.Disposition);
            Assert.IsTrue(result.Recovery.RequiresFloating);
            Assert.AreSame(harness.Source, result.Recovery.FloatingDesktop);
            Assert.IsTrue(result.Recovery.ShouldNotifyFailure);
        }

        [TestMethod]
        public void MoveWindowThrowAfterSourceLossAttemptsRollback()
        {
            using var harness = new Harness(totalCapacity: 1);
            var window = harness.CreateWindow(801);
            harness.TargetMove = movedWindow =>
            {
                harness.RemoveFromSource(movedWindow);
                throw new InvalidOperationException("move failed after source ownership changed");
            };

            var result = harness.Start(window);

            Assert.AreEqual(AlgorithmicTransferStartDisposition.MoveFailed, result.Disposition);
            Assert.IsTrue(result.RollbackAttempted);
            Assert.IsTrue(result.RollbackSucceeded);
            Assert.IsNull(result.RollbackException);
            Assert.AreEqual(1, harness.SourceMoveCount);
            Assert.IsTrue(harness.SourceHas(window));
            Assert.AreEqual(0, harness.Coordinator.ReservationCount);
            Assert.IsNotNull(result.Recovery);
            Assert.AreEqual(
                AlgorithmicTransferRecoveryDisposition.RolledBackToSource,
                result.Recovery!.Disposition);
            Assert.IsTrue(result.Recovery.RequiresFloating);
            Assert.AreSame(harness.Source, result.Recovery.FloatingDesktop);
            Assert.IsTrue(result.Recovery.ShouldNotifyFailure);
        }

        [TestMethod]
        public void CommittedDuplicateIsClassifiedWithoutCallingMaterializer()
        {
            using var harness = new Harness(totalCapacity: 1);
            var window = harness.CreateWindow(901);
            AlgorithmicTransferArrivalResult? first = null;
            harness.TargetMove = movedWindow =>
            {
                harness.MoveToTarget(movedWindow);
                first = harness.Orchestrator.ObserveDestinationAdded(
                    movedWindow,
                    harness.Target,
                    (_, _) => AlgorithmicTransferMaterializationResult.Accept());
            };
            harness.Start(window);
            int duplicateMaterializerCalls = 0;

            var duplicate = harness.Orchestrator.ObserveDestinationAdded(
                window,
                harness.Target,
                (_, _) =>
                {
                    duplicateMaterializerCalls++;
                    return AlgorithmicTransferMaterializationResult.Accept();
                });

            Assert.AreEqual(AlgorithmicTransferArrivalDisposition.Materialized, first!.Disposition);
            Assert.AreEqual(AlgorithmicTransferArrivalDisposition.AlreadyTerminal, duplicate.Disposition);
            Assert.IsTrue(duplicate.Consumed);
            Assert.AreEqual(0, duplicateMaterializerCalls);
            Assert.AreEqual(PendingWindowTransferState.Committed, duplicate.Transfer!.State);
        }

        [TestMethod]
        public void SourceGeometryIsRetainedThroughDestinationMaterialization()
        {
            using var harness = new Harness(totalCapacity: 1);
            var window = harness.CreateWindow(1001);
            var sourceGeometry = Rectangle.OffsetAndSize(11, 22, 333, 244);
            Rectangle? geometrySeenByMaterializer = null;
            harness.TargetMove = movedWindow =>
            {
                harness.MoveToTarget(movedWindow);
                harness.Orchestrator.ObserveDestinationAdded(
                    movedWindow,
                    harness.Target,
                    (transfer, _) =>
                    {
                        geometrySeenByMaterializer = transfer.SourceOriginalPosition;
                        return AlgorithmicTransferMaterializationResult.Accept();
                    });
            };

            var start = harness.Start(window, sourceGeometry);

            Assert.AreEqual(sourceGeometry, start.Transfer!.SourceOriginalPosition);
            Assert.AreEqual(sourceGeometry, geometrySeenByMaterializer);
            Assert.AreEqual(sourceGeometry,
                harness.GetTransfer(start).SourceOriginalPosition);
        }

        [TestMethod]
        public void ThreeSimultaneousMovesReserveDistinctOrderedSlots()
        {
            using var harness = new Harness(totalCapacity: 3);
            harness.TargetMove = _ => { };
            var windows = new[]
            {
                harness.CreateWindow(1101),
                harness.CreateWindow(1102),
                harness.CreateWindow(1103),
            };

            var starts = windows
                .Select(window => harness.Start(window))
                .ToArray();
            var transfers = starts
                .Select(harness.GetTransfer)
                .ToArray();

            Assert.IsTrue(starts.All(item =>
                item.Disposition == AlgorithmicTransferStartDisposition.MoveRequested));
            Assert.IsTrue(transfers.All(item => item.State == PendingWindowTransferState.Moving));
            CollectionAssert.AreEqual(
                new[] { ReservedRole.Master, ReservedRole.Satellite, ReservedRole.Satellite },
                transfers.Select(item => item.TargetRole).ToArray());
            CollectionAssert.AreEqual(
                new int?[] { null, 0, 1 },
                transfers.Select(item => item.TargetSatelliteIndex).ToArray());
            Assert.AreEqual(3, transfers.Select(item => item.ReservationId).Distinct().Count());
            Assert.AreEqual(3, harness.Coordinator.ReservationCount);
            Assert.AreEqual(3, harness.Coordinator.ActiveTransferCount);
        }

        [TestMethod]
        public void DestinationFailureRecoveryMovesBackOutsideCoordinatorLockAndDeduplicates()
        {
            using var harness = new Harness(totalCapacity: 1);
            var window = harness.CreateWindow(1201);
            AlgorithmicTransferArrivalResult? arrival = null;
            bool coordinatorLockHeldDuringRollback = true;
            harness.TargetMove = movedWindow =>
            {
                harness.MoveToTarget(movedWindow);
                arrival = harness.Orchestrator.ObserveDestinationAdded(
                    movedWindow,
                    harness.Target,
                    (_, _) => AlgorithmicTransferMaterializationResult.Reject(
                        "Destination changed after reservation."));
            };
            harness.SourceMove = movedWindow =>
            {
                coordinatorLockHeldDuringRollback =
                    harness.Coordinator.IsMutationLockHeldByCurrentThread;
                harness.MoveToSource(movedWindow);
            };

            var start = harness.Start(window);
            var first = harness.Orchestrator.RecoverTerminalTransfer(
                window,
                start.Transfer!.CorrelationId);
            var duplicate = harness.Orchestrator.RecoverTerminalTransfer(
                window,
                start.Transfer.CorrelationId);

            Assert.AreEqual(
                AlgorithmicTransferArrivalDisposition.MaterializationFailed,
                arrival!.Disposition);
            Assert.AreEqual(PendingWindowTransferState.Failed, arrival.Transfer!.State);
            Assert.AreEqual(
                AlgorithmicTransferRecoveryDisposition.RolledBackToSource,
                first.Disposition);
            Assert.IsTrue(first.RollbackAttempted);
            Assert.IsTrue(first.RollbackSucceeded);
            Assert.IsTrue(first.RequiresFloating);
            Assert.AreSame(harness.Source, first.FloatingDesktop);
            Assert.IsTrue(first.ShouldNotifyFailure);
            Assert.IsFalse(first.IsDuplicate);
            Assert.IsFalse(coordinatorLockHeldDuringRollback);
            Assert.IsTrue(harness.SourceHas(window));
            Assert.AreEqual(1, harness.SourceMoveCount);

            Assert.AreEqual(first.Disposition, duplicate.Disposition);
            Assert.IsTrue(duplicate.IsDuplicate);
            Assert.IsFalse(duplicate.ShouldNotifyFailure);
            Assert.AreEqual(1, harness.SourceMoveCount);
        }

        [TestMethod]
        public void RecoveryDoesNotRetryRollbackFailureOrDuplicateNotification()
        {
            using var harness = new Harness(totalCapacity: 1);
            var window = harness.CreateWindow(1301);
            harness.TargetMove = harness.MoveToTarget;
            var start = harness.Start(window);
            Assert.IsTrue(harness.Coordinator.Fail(
                start.Transfer!.CorrelationId,
                window.Handle,
                "SettingsChanged"));
            var rollbackException = new InvalidOperationException("rollback failed");
            harness.SourceMove = _ => throw rollbackException;

            var first = harness.Orchestrator.RecoverTerminalTransfer(
                window,
                start.Transfer.CorrelationId);
            var duplicate = harness.Orchestrator.RecoverTerminalTransfer(
                window,
                start.Transfer.CorrelationId);

            Assert.AreEqual(
                AlgorithmicTransferRecoveryDisposition.RollbackFailed,
                first.Disposition);
            Assert.AreSame(rollbackException, first.Exception);
            Assert.IsTrue(first.RequiresFloating);
            Assert.IsTrue(first.ShouldNotifyFailure);
            Assert.AreEqual(1, harness.SourceMoveCount);
            Assert.IsTrue(duplicate.IsDuplicate);
            Assert.IsFalse(duplicate.ShouldNotifyFailure);
            Assert.AreEqual(1, harness.SourceMoveCount);
        }

        [TestMethod]
        public void ManualDestinationCancellationPreservesUserDesktopAndBlocksLaterRollback()
        {
            using var harness = new Harness(totalCapacity: 1);
            var window = harness.CreateWindow(1351);
            harness.TargetMove = harness.MoveToTarget;
            var start = harness.Start(window);
            Assert.IsTrue(harness.Coordinator.Cancel(
                start.Transfer!.CorrelationId,
                window.Handle,
                "UnexpectedDestinationObserved"));

            var first = harness.Orchestrator.RecoverTerminalTransfer(
                window,
                start.Transfer.CorrelationId,
                allowRollbackToSource: false,
                preferredFloatingDesktop: harness.Target);
            var laterTerminalNotification = harness.Orchestrator.RecoverTerminalTransfer(
                window,
                start.Transfer.CorrelationId);

            Assert.AreEqual(
                AlgorithmicTransferRecoveryDisposition.PreservedOnSelectedDesktop,
                first.Disposition);
            Assert.IsFalse(first.RollbackAttempted);
            Assert.IsFalse(first.RollbackSucceeded);
            Assert.IsTrue(first.RequiresFloating);
            Assert.AreSame(harness.Target, first.FloatingDesktop);
            Assert.IsTrue(first.ShouldNotifyFailure);
            Assert.IsFalse(first.IsDuplicate);
            Assert.IsTrue(harness.Target.HasWindow(window));
            Assert.IsFalse(harness.SourceHas(window));
            Assert.AreEqual(0, harness.SourceMoveCount);

            Assert.AreEqual(first.Disposition, laterTerminalNotification.Disposition);
            Assert.IsTrue(laterTerminalNotification.IsDuplicate);
            Assert.IsFalse(laterTerminalNotification.ShouldNotifyFailure);
            Assert.AreEqual(0, harness.SourceMoveCount);
        }

        [TestMethod]
        public void TargetDesktopRemovalCanBeRecoveredToSource()
        {
            using var harness = new Harness(totalCapacity: 1);
            var window = harness.CreateWindow(1401);
            harness.TargetMove = harness.MoveToTarget;
            var start = harness.Start(window);
            harness.TargetDesktopAlive = false;

            Assert.AreEqual(1, harness.Coordinator.DesktopRemoved(harness.Target));
            var recovery = harness.Orchestrator.RecoverTerminalTransfer(
                window,
                start.Transfer!.CorrelationId);

            Assert.AreEqual(
                AlgorithmicTransferRecoveryDisposition.RolledBackToSource,
                recovery.Disposition);
            Assert.AreEqual("DesktopRemoved", recovery.Transfer!.TerminalReason);
            Assert.IsTrue(harness.SourceHas(window));
            Assert.AreEqual(0, harness.Coordinator.ReservationCount);
        }

        [TestMethod]
        public void TargetDisplayRemovalCanBeRecoveredToSource()
        {
            using var harness = new Harness(totalCapacity: 1);
            var window = harness.CreateWindow(1501);
            harness.TargetMove = harness.MoveToTarget;
            var start = harness.Start(window);

            harness.Coordinator.RemoveDisplay(harness.Display);
            var recovery = harness.Orchestrator.RecoverTerminalTransfer(
                window,
                start.Transfer!.CorrelationId);

            Assert.AreEqual(
                AlgorithmicTransferRecoveryDisposition.RolledBackToSource,
                recovery.Disposition);
            Assert.AreEqual("DisplayStateRemoved", recovery.Transfer!.TerminalReason);
            Assert.IsTrue(harness.SourceHas(window));
            Assert.AreEqual(0, harness.Coordinator.ReservationCount);
        }

        [TestMethod]
        public void SettingsChangeCancellationCanBeRecoveredToSource()
        {
            using var harness = new Harness(totalCapacity: 1);
            var window = harness.CreateWindow(1601);
            harness.TargetMove = harness.MoveToTarget;
            var start = harness.Start(window);

            harness.Coordinator.Clear();
            var recovery = harness.Orchestrator.RecoverTerminalTransfer(
                window,
                start.Transfer!.CorrelationId);

            Assert.AreEqual(
                AlgorithmicTransferRecoveryDisposition.RolledBackToSource,
                recovery.Disposition);
            Assert.AreEqual("RuntimeStateCleared", recovery.Transfer!.TerminalReason);
            Assert.IsTrue(harness.SourceHas(window));
            Assert.AreEqual(0, harness.Coordinator.ReservationCount);
        }

        [TestMethod]
        public void ClosedWindowCancellationSkipsRollbackFloatingAndNotification()
        {
            using var harness = new Harness(totalCapacity: 1);
            var window = harness.CreateWindow(1701);
            harness.TargetMove = harness.MoveToTarget;
            var start = harness.Start(window);

            Assert.IsTrue(harness.Coordinator.WindowClosed(window.Handle));
            harness.SetWindowAlive(window, false);
            var recovery = harness.Orchestrator.RecoverTerminalTransfer(
                window,
                start.Transfer!.CorrelationId);

            Assert.AreEqual(
                AlgorithmicTransferRecoveryDisposition.WindowClosed,
                recovery.Disposition);
            Assert.IsFalse(recovery.RollbackAttempted);
            Assert.IsFalse(recovery.RequiresFloating);
            Assert.IsFalse(recovery.ShouldNotifyFailure);
            Assert.AreEqual(0, harness.SourceMoveCount);
            Assert.AreEqual(0, harness.Coordinator.ReservationCount);
        }

        [TestMethod]
        public void RemovedSourceRequestsFloatingWithoutAttemptingRollback()
        {
            using var harness = new Harness(totalCapacity: 1);
            var window = harness.CreateWindow(1751);
            harness.TargetMove = harness.MoveToTarget;
            var start = harness.Start(window);
            harness.SourceDesktopAlive = false;

            Assert.AreEqual(1, harness.Coordinator.DesktopRemoved(harness.Source));
            var recovery = harness.Orchestrator.RecoverTerminalTransfer(
                window,
                start.Transfer!.CorrelationId);

            Assert.AreEqual(
                AlgorithmicTransferRecoveryDisposition.SourceUnavailable,
                recovery.Disposition);
            Assert.AreEqual("DesktopRemoved", recovery.Transfer!.TerminalReason);
            Assert.IsFalse(recovery.RollbackAttempted);
            Assert.IsFalse(recovery.RollbackSucceeded);
            Assert.IsTrue(recovery.RequiresFloating);
            Assert.IsNull(recovery.FloatingDesktop);
            Assert.IsTrue(recovery.ShouldNotifyFailure);
            Assert.AreEqual(0, harness.SourceMoveCount);
            Assert.AreEqual(0, harness.Coordinator.ReservationCount);
        }

        [TestMethod]
        public void ReentrantRecoveryIsSuppressedWhileSourceMoveIsInProgress()
        {
            using var harness = new Harness(totalCapacity: 1);
            var window = harness.CreateWindow(1801);
            harness.TargetMove = harness.MoveToTarget;
            var start = harness.Start(window);
            Assert.IsTrue(harness.Coordinator.Cancel(
                start.Transfer!.CorrelationId,
                window.Handle,
                "DestinationRemoved"));
            AlgorithmicTransferRecoveryResult? reentrant = null;
            harness.SourceMove = movedWindow =>
            {
                reentrant = harness.Orchestrator.RecoverTerminalTransfer(
                    movedWindow,
                    start.Transfer.CorrelationId);
                harness.MoveToSource(movedWindow);
            };

            var recovery = harness.Orchestrator.RecoverTerminalTransfer(
                window,
                start.Transfer.CorrelationId);

            Assert.AreEqual(
                AlgorithmicTransferRecoveryDisposition.RolledBackToSource,
                recovery.Disposition);
            Assert.AreEqual(
                AlgorithmicTransferRecoveryDisposition.RecoveryInProgress,
                reentrant!.Disposition);
            Assert.IsFalse(reentrant.ShouldNotifyFailure);
            Assert.AreEqual(1, harness.SourceMoveCount);
        }

        [TestMethod]
        public void CompletedRecoveryRetentionIsBounded()
        {
            using var harness = new Harness(totalCapacity: 1);
            harness.TargetMove = _ => { };

            for (int i = 0; i < 300; i++)
            {
                var window = harness.CreateWindow(2000 + i);
                var start = harness.Start(window);
                Assert.IsTrue(harness.Coordinator.Cancel(
                    start.Transfer!.CorrelationId,
                    window.Handle,
                    "TestCancellation"));
                var recovery = harness.Orchestrator.RecoverTerminalTransfer(
                    window,
                    start.Transfer.CorrelationId);
                Assert.AreEqual(
                    AlgorithmicTransferRecoveryDisposition.AlreadyOnSource,
                    recovery.Disposition);
            }

            Assert.AreEqual(256, harness.Orchestrator.RetainedRecoveryCount);
        }

        private sealed class Harness : IDisposable
        {
            private readonly Mock<IWorkspace> m_workspace = new(MockBehavior.Loose);
            private readonly Mock<IVirtualDesktopManager> m_virtualDesktopManager = new(MockBehavior.Loose);
            private readonly Mock<IVirtualDesktop> m_source = new(MockBehavior.Loose);
            private readonly Mock<IVirtualDesktop> m_target = new(MockBehavior.Loose);
            private readonly Mock<IDisplay> m_display = new(MockBehavior.Loose);
            private readonly HashSet<IntPtr> m_sourceWindows = [];
            private readonly HashSet<IntPtr> m_targetWindows = [];
            private readonly AlgorithmicLayoutDisplayRegistration m_registration;

            public AlgorithmicLayoutCoordinator Coordinator { get; }

            public AlgorithmicWindowTransferOrchestrator Orchestrator { get; }

            public IVirtualDesktop Source => m_source.Object;

            public IVirtualDesktop Target => m_target.Object;

            public IDisplay Display => m_display.Object;

            public Action<IWindow>? TargetMove { get; set; }

            public Action<IWindow>? SourceMove { get; set; }

            public int SourceMoveCount { get; private set; }

            public bool CanManageVirtualDesktops { get; set; } = true;

            public bool SourceDesktopAlive { get; set; } = true;

            public bool TargetDesktopAlive { get; set; } = true;

            public Harness(int totalCapacity)
            {
                m_workspace.SetupGet(item => item.VirtualDesktopManager)
                    .Returns(m_virtualDesktopManager.Object);
                m_virtualDesktopManager.SetupGet(
                        item => item.CanManageVirtualDesktops)
                    .Returns(() => CanManageVirtualDesktops);
                m_virtualDesktopManager.SetupGet(item => item.Desktops)
                    .Returns(new[] { Source, Target });

                ConfigureDesktop(
                    m_source,
                    "Source",
                    m_sourceWindows,
                    () => SourceDesktopAlive);
                ConfigureDesktop(
                    m_target,
                    "Target",
                    m_targetWindows,
                    () => TargetDesktopAlive);
                m_display.SetupGet(item => item.Workspace).Returns(m_workspace.Object);

                m_target.Setup(item => item.MoveWindow(It.IsAny<IWindow>()))
                    .Callback<IWindow>(window => TargetMove?.Invoke(window));
                m_source.Setup(item => item.MoveWindow(It.IsAny<IWindow>()))
                    .Callback<IWindow>(window =>
                    {
                        SourceMoveCount++;
                        if (SourceMove != null)
                        {
                            SourceMove(window);
                        }
                        else
                        {
                            MoveToSource(window);
                        }
                    });

                Coordinator = new AlgorithmicLayoutCoordinator(
                    m_workspace.Object,
                    Dispatcher.CurrentDispatcher);
                m_registration = Coordinator.RegisterDisplay(m_display.Object, new object());
                Assert.IsTrue(Coordinator.PublishCapacity(
                    new LayoutStateKey(Target, m_display.Object),
                    EmptyCapacity(totalCapacity),
                    1));
                Orchestrator = new AlgorithmicWindowTransferOrchestrator(
                    Coordinator,
                    m_display.Object);
            }

            public IWindow CreateWindow(long handle)
            {
                var mock = new Mock<IWindow>(MockBehavior.Loose);
                mock.SetupGet(item => item.Handle).Returns(new IntPtr(handle));
                mock.SetupGet(item => item.IsAlive).Returns(true);
                var window = mock.Object;
                m_sourceWindows.Add(window.Handle);
                return window;
            }

            public void SetWindowAlive(IWindow window, bool isAlive)
            {
                Mock.Get(window).SetupGet(item => item.IsAlive).Returns(isAlive);
            }

            public AlgorithmicTransferStartResult Start(
                IWindow window,
                Rectangle? sourceGeometry = null)
            {
                return Orchestrator.StartExistingDesktopTransfer(
                    Guid.NewGuid(),
                    window,
                    Source,
                    sourceGeometry ?? Rectangle.OffsetAndSize(1, 2, 300, 200),
                    (_, _) => AlgorithmicDestinationPreflightResult.Accept());
            }

            public PendingWindowTransfer GetTransfer(AlgorithmicTransferStartResult result)
            {
                Assert.IsNotNull(result.Transfer);
                var resultTransfer = result.Transfer!;
                Assert.IsTrue(Coordinator.TryGetTransfer(
                    resultTransfer.CorrelationId,
                    out var transfer));
                return transfer;
            }

            public void MoveToTarget(IWindow window)
            {
                RemoveFromSource(window);
                AddToTarget(window);
            }

            public void RemoveFromSource(IWindow window)
            {
                m_sourceWindows.Remove(window.Handle);
            }

            public void AddToTarget(IWindow window)
            {
                m_targetWindows.Add(window.Handle);
            }

            public void MoveToSource(IWindow window)
            {
                m_targetWindows.Remove(window.Handle);
                m_sourceWindows.Add(window.Handle);
            }

            public bool SourceHas(IWindow window)
            {
                return m_sourceWindows.Contains(window.Handle);
            }

            public bool TargetHas(IWindow window)
            {
                return m_targetWindows.Contains(window.Handle);
            }

            public void Dispose()
            {
                m_registration.Dispose();
                Coordinator.Dispose();
            }

            private void ConfigureDesktop(
                Mock<IVirtualDesktop> desktop,
                string name,
                HashSet<IntPtr> windows,
                Func<bool> isAlive)
            {
                desktop.SetupGet(item => item.Name).Returns(name);
                desktop.SetupGet(item => item.IsAlive).Returns(isAlive);
                desktop.SetupGet(item => item.Workspace).Returns(m_workspace.Object);
                desktop.Setup(item => item.HasWindow(It.IsAny<IWindow>()))
                    .Returns<IWindow>(window => windows.Contains(window.Handle));
            }

            private static MasterSatelliteCapacitySnapshot EmptyCapacity(int totalCapacity)
            {
                return new MasterSatelliteCapacitySnapshot(
                    WorkspaceLayoutKind.Empty,
                    true,
                    MasterSatelliteWindowRole.Master,
                    null,
                    0,
                    totalCapacity,
                    0,
                    null);
            }
        }
    }
}
