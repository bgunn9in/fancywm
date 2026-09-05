#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Threading;

using FancyWM.AlgorithmicLayouts;
using FancyWM.Models;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    [TestClass]
    public class AlgorithmicDesktopCreationOrchestratorTest
    {
        [DataTestMethod]
        [DataRow(MasterSatelliteOverflowPolicy.FloatOnCurrentDesktop, false, false)]
        [DataRow(MasterSatelliteOverflowPolicy.MoveToExistingDesktop, true, false)]
        [DataRow(MasterSatelliteOverflowPolicy.MoveToExistingOrCreateDesktop, true, true)]
        public void PolicyCreatesOnlyForExplicitCreateModeAfterNoDestination(
            MasterSatelliteOverflowPolicy policy,
            bool expectsExistingSearch,
            bool expectsCreation)
        {
            Assert.AreEqual(
                expectsExistingSearch,
                AlgorithmicDesktopCreationPolicy.AllowsExistingDesktopOverflow(policy));
            Assert.AreEqual(
                expectsCreation,
                AlgorithmicDesktopCreationPolicy.ShouldCreateAfterSearch(
                    policy,
                    AlgorithmicTransferStartDisposition.NoDestination));
            Assert.IsFalse(AlgorithmicDesktopCreationPolicy.ShouldCreateAfterSearch(
                policy,
                AlgorithmicTransferStartDisposition.MoveRequested));
            Assert.IsFalse(AlgorithmicDesktopCreationPolicy.ShouldCreateAfterSearch(
                policy,
                AlgorithmicTransferStartDisposition.TransferConflict));
            Assert.IsFalse(AlgorithmicDesktopCreationPolicy.ShouldCreateAfterSearch(
                policy,
                AlgorithmicTransferStartDisposition.MoveFailed));
            Assert.AreEqual(
                expectsCreation,
                AlgorithmicDesktopCreationPolicy.ShouldCreateAfterSearch(
                    policy,
                    AlgorithmicDestinationSearchDisposition.NoDestination));
            Assert.IsFalse(AlgorithmicDesktopCreationPolicy.ShouldCreateAfterSearch(
                policy,
                AlgorithmicDestinationSearchDisposition.Reserved));
            Assert.IsFalse(AlgorithmicDesktopCreationPolicy.ShouldCreateAfterSearch(
                policy,
                AlgorithmicDestinationSearchDisposition.TransferConflict));
        }

        [TestMethod]
        public void UnsupportedManagerDoesNotClaimOrCreate()
        {
            using var harness = new Harness(canManage: false);

            var result = harness.Create();

            Assert.AreEqual(
                AlgorithmicDesktopCreationDisposition.Unsupported,
                result.Disposition);
            Assert.IsFalse(result.Succeeded);
            Assert.IsNull(result.ClaimResult);
            Assert.AreEqual(0, harness.CreateCallCount);
            Assert.AreEqual(0, harness.PrepareCallCount);
            Assert.AreEqual(0, harness.Coordinator.SnapshotDesktopCreations().InFlightCount);
        }

        [TestMethod]
        public void CapabilityCheckExceptionIsReturnedWithoutClaiming()
        {
            using var harness = new Harness();
            var expected = new COMException("Capability unavailable");
            harness.Manager.SetupGet(item => item.CanManageVirtualDesktops)
                .Throws(expected);

            var result = harness.Create();

            Assert.AreEqual(
                AlgorithmicDesktopCreationDisposition.CapabilityCheckFailed,
                result.Disposition);
            Assert.AreSame(expected, result.Exception);
            Assert.AreEqual(0, harness.CreateCallCount);
            Assert.AreEqual(0, harness.Coordinator.SnapshotDesktopCreations().InFlightCount);
        }

        [TestMethod]
        public void ZeroLimitDoesNotCallCreateDesktop()
        {
            using var harness = new Harness();

            var result = harness.Create(maximum: 0);

            Assert.AreEqual(
                AlgorithmicDesktopCreationDisposition.LimitReached,
                result.Disposition);
            Assert.AreEqual(
                AlgorithmicDesktopCreationClaimDisposition.LimitReached,
                result.ClaimResult?.Disposition);
            Assert.AreEqual(0, harness.CreateCallCount);
            Assert.AreEqual(0, harness.PrepareCallCount);
            Assert.AreEqual(0, harness.Coordinator.SnapshotDesktopCreations().InFlightCount);
        }

        [TestMethod]
        public void CreateExceptionReleasesClaimAndReturnsException()
        {
            using var harness = new Harness();
            var expected = new COMException("Create failed");
            harness.CreateBehavior = () => throw expected;

            var result = harness.Create();

            Assert.AreEqual(
                AlgorithmicDesktopCreationDisposition.CreationFailed,
                result.Disposition);
            Assert.AreSame(expected, result.Exception);
            Assert.AreEqual(
                AlgorithmicDesktopCreationCompletionDisposition.FailureReleased,
                result.CompletionResult?.Disposition);
            Assert.AreEqual(1, harness.CreateCallCount);
            Assert.AreEqual(0, harness.PrepareCallCount);
            var snapshot = harness.Coordinator.SnapshotDesktopCreations();
            Assert.AreEqual(0, snapshot.SuccessfulCreationCount);
            Assert.AreEqual(0, snapshot.InFlightCount);
        }

        [TestMethod]
        public void NullDesktopReleasesClaimWithoutPreparing()
        {
            using var harness = new Harness();
            harness.CreateBehavior = () => null!;

            var result = harness.Create();

            Assert.AreEqual(
                AlgorithmicDesktopCreationDisposition.CreationReturnedNoDesktop,
                result.Disposition);
            Assert.IsNull(result.CreatedDesktop);
            Assert.AreEqual(
                AlgorithmicDesktopCreationCompletionDisposition.FailureReleased,
                result.CompletionResult?.Disposition);
            Assert.AreEqual(0, harness.PrepareCallCount);
            Assert.AreEqual(0, harness.Coordinator.SnapshotDesktopCreations().InFlightCount);
        }

        [TestMethod]
        public void DeadOrForeignDesktopIsRejectedAndClaimIsReleased()
        {
            using var deadHarness = new Harness();
            deadHarness.CreatedDesktopAlive = false;

            var dead = deadHarness.Create();

            Assert.AreEqual(
                AlgorithmicDesktopCreationDisposition.CreatedDesktopInvalid,
                dead.Disposition);
            StringAssert.Contains(dead.DiagnosticReason, "not alive");
            Assert.AreEqual(0, deadHarness.PrepareCallCount);
            Assert.AreEqual(0, deadHarness.Coordinator.SnapshotDesktopCreations().InFlightCount);

            using var foreignHarness = new Harness();
            var foreignWorkspace = new Mock<IWorkspace>(MockBehavior.Loose).Object;
            foreignHarness.CreatedDesktopWorkspace = foreignWorkspace;

            var foreign = foreignHarness.Create();

            Assert.AreEqual(
                AlgorithmicDesktopCreationDisposition.CreatedDesktopInvalid,
                foreign.Disposition);
            StringAssert.Contains(foreign.DiagnosticReason, "another workspace");
            Assert.AreEqual(0, foreignHarness.PrepareCallCount);
            Assert.AreEqual(0, foreignHarness.Coordinator.SnapshotDesktopCreations().InFlightCount);
        }

        [TestMethod]
        public void CreationIsRecordedBeforePreparationAndBothRunOutsideCoordinatorLock()
        {
            using var harness = new Harness();
            var order = new List<string>();
            harness.CreateBehavior = () =>
            {
                Assert.IsFalse(harness.Coordinator.IsMutationLockHeldByCurrentThread);
                order.Add("create");
                return harness.CreatedDesktop;
            };
            harness.PrepareBehavior = _ =>
            {
                Assert.IsFalse(harness.Coordinator.IsMutationLockHeldByCurrentThread);
                Assert.AreEqual(
                    1,
                    harness.Coordinator.SnapshotDesktopCreations()
                        .SuccessfulCreationCount);
                order.Add("prepare");
            };

            var result = harness.Create();

            Assert.AreEqual(
                AlgorithmicDesktopCreationDisposition.CreatedAndPrepared,
                result.Disposition);
            Assert.IsTrue(result.Succeeded);
            Assert.IsTrue(result.DesktopWasCreated);
            Assert.AreSame(harness.CreatedDesktop, result.CreatedDesktop);
            CollectionAssert.AreEqual(
                new[] { "create", "prepare" },
                order);
            Assert.AreEqual(1, harness.CreateCallCount);
            Assert.AreEqual(1, harness.PrepareCallCount);
            var snapshot = harness.Coordinator.SnapshotDesktopCreations();
            Assert.AreEqual(1, snapshot.SuccessfulCreationCount);
            Assert.AreEqual(0, snapshot.InFlightCount);
        }

        [TestMethod]
        public void PreparationExceptionKeepsCreatedDesktopCountedAndIsReturned()
        {
            using var harness = new Harness();
            var expected = new InvalidOperationException("Preparation failed");
            harness.PrepareBehavior = _ => throw expected;
            var handle = new IntPtr(801);
            var correlation = Guid.NewGuid();

            var result = harness.Create(handle, correlation);
            var duplicate = harness.Create(handle, correlation);

            Assert.AreEqual(
                AlgorithmicDesktopCreationDisposition.PreparationFailed,
                result.Disposition);
            Assert.AreSame(expected, result.Exception);
            Assert.IsTrue(result.DesktopWasCreated);
            Assert.AreSame(harness.CreatedDesktop, result.CreatedDesktop);
            Assert.AreEqual(
                AlgorithmicDesktopCreationDisposition.AlreadyCreated,
                duplicate.Disposition);
            Assert.IsTrue(duplicate.IsDuplicate);
            Assert.AreEqual(1, harness.CreateCallCount);
            Assert.AreEqual(1, harness.PrepareCallCount);
            var snapshot = harness.Coordinator.SnapshotDesktopCreations();
            Assert.AreEqual(1, snapshot.SuccessfulCreationCount);
            Assert.AreEqual(0, snapshot.InFlightCount);
        }

        [TestMethod]
        public void DuplicateReentrantRequestNeverCallsCreateDesktopTwice()
        {
            using var harness = new Harness();
            var handle = new IntPtr(901);
            var correlation = Guid.NewGuid();
            AlgorithmicDesktopCreationResult? nested = null;
            harness.CreateBehavior = () =>
            {
                nested = harness.Create(handle, correlation);
                return harness.CreatedDesktop;
            };

            var result = harness.Create(handle, correlation);

            Assert.AreEqual(
                AlgorithmicDesktopCreationDisposition.CreatedAndPrepared,
                result.Disposition);
            Assert.IsNotNull(nested);
            Assert.AreEqual(
                AlgorithmicDesktopCreationDisposition.DuplicateInFlight,
                nested!.Disposition);
            Assert.IsTrue(nested.IsDuplicate);
            Assert.AreEqual(1, harness.CreateCallCount);
            Assert.AreEqual(1, harness.PrepareCallCount);
            Assert.AreEqual(1, harness.Coordinator.SnapshotDesktopCreations().SuccessfulCreationCount);
        }

        [TestMethod]
        public void InflightClaimConsumesGlobalQuotaDuringReentrantRequest()
        {
            using var harness = new Harness();
            AlgorithmicDesktopCreationResult? nested = null;
            harness.CreateBehavior = () =>
            {
                nested = harness.Create(
                    new IntPtr(1002),
                    Guid.NewGuid(),
                    maximum: 1);
                return harness.CreatedDesktop;
            };

            var result = harness.Create(
                new IntPtr(1001),
                Guid.NewGuid(),
                maximum: 1);

            Assert.AreEqual(
                AlgorithmicDesktopCreationDisposition.CreatedAndPrepared,
                result.Disposition);
            Assert.IsNotNull(nested);
            Assert.AreEqual(
                AlgorithmicDesktopCreationDisposition.LimitReached,
                nested!.Disposition);
            Assert.AreEqual(1, harness.CreateCallCount);
            Assert.AreEqual(1, harness.Coordinator.SnapshotDesktopCreations().SuccessfulCreationCount);
        }

        [TestMethod]
        public void SessionMaximumCountsSuccessfulCreationsAcrossRequests()
        {
            using var harness = new Harness();
            var first = harness.Create(
                new IntPtr(1101),
                Guid.NewGuid(),
                maximum: 1);
            var second = harness.Create(
                new IntPtr(1102),
                Guid.NewGuid(),
                maximum: 1);

            Assert.AreEqual(
                AlgorithmicDesktopCreationDisposition.CreatedAndPrepared,
                first.Disposition);
            Assert.AreEqual(
                AlgorithmicDesktopCreationDisposition.LimitReached,
                second.Disposition);
            Assert.AreEqual(1, harness.CreateCallCount);
            Assert.AreEqual(1, harness.PrepareCallCount);
        }

        [TestMethod]
        public void ReusedDesktopCompletionIsRejectedAndSecondClaimIsReleased()
        {
            using var harness = new Harness();

            var first = harness.Create(
                new IntPtr(1201),
                Guid.NewGuid(),
                maximum: 2);
            var second = harness.Create(
                new IntPtr(1202),
                Guid.NewGuid(),
                maximum: 2);

            Assert.AreEqual(
                AlgorithmicDesktopCreationDisposition.CreatedAndPrepared,
                first.Disposition);
            Assert.AreEqual(
                AlgorithmicDesktopCreationDisposition.CompletionRejected,
                second.Disposition);
            Assert.AreEqual(2, harness.CreateCallCount);
            Assert.AreEqual(1, harness.PrepareCallCount);
            var snapshot = harness.Coordinator.SnapshotDesktopCreations();
            Assert.AreEqual(1, snapshot.SuccessfulCreationCount);
            Assert.AreEqual(0, snapshot.InFlightCount);
        }

        [TestMethod]
        public void InvalidInputsFailBeforeExternalCalls()
        {
            using var harness = new Harness();

            Assert.ThrowsException<ArgumentException>(() => harness.Create(IntPtr.Zero));
            Assert.ThrowsException<ArgumentException>(() =>
                harness.Create(new IntPtr(1301), Guid.Empty));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                harness.Create(maximum: -1));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                harness.Create(maximum: 10));
            Assert.ThrowsException<ArgumentNullException>(() =>
                harness.Orchestrator.CreateAndPrepareDesktop(
                    new IntPtr(1302),
                    Guid.NewGuid(),
                    1,
                    null!));
            Assert.AreEqual(0, harness.CreateCallCount);
        }

        private sealed class Harness : IDisposable
        {
            private readonly Mock<IWorkspace> m_workspace = new(MockBehavior.Loose);
            private readonly Mock<IVirtualDesktopManager> m_manager = new(MockBehavior.Loose);
            private readonly Mock<IVirtualDesktop> m_createdDesktop = new(MockBehavior.Loose);

            public AlgorithmicLayoutCoordinator Coordinator { get; }

            public AlgorithmicDesktopCreationOrchestrator Orchestrator { get; }

            public Mock<IVirtualDesktopManager> Manager => m_manager;

            public IVirtualDesktop CreatedDesktop => m_createdDesktop.Object;

            public Func<IVirtualDesktop> CreateBehavior { get; set; }

            public Action<IVirtualDesktop> PrepareBehavior { get; set; } = _ => { };

            public IWorkspace CreatedDesktopWorkspace { set =>
                m_createdDesktop.SetupGet(item => item.Workspace).Returns(value); }

            public bool CreatedDesktopAlive { set =>
                m_createdDesktop.SetupGet(item => item.IsAlive).Returns(value); }

            public int CreateCallCount { get; private set; }

            public int PrepareCallCount { get; private set; }

            public Harness(bool canManage = true)
            {
                m_workspace.SetupGet(item => item.VirtualDesktopManager)
                    .Returns(m_manager.Object);
                m_manager.SetupGet(item => item.Workspace).Returns(m_workspace.Object);
                m_manager.SetupGet(item => item.CanManageVirtualDesktops)
                    .Returns(canManage);
                m_createdDesktop.SetupGet(item => item.Workspace)
                    .Returns(m_workspace.Object);
                m_createdDesktop.SetupGet(item => item.IsAlive).Returns(true);
                CreateBehavior = () => CreatedDesktop;
                m_manager.Setup(item => item.CreateDesktop())
                    .Returns(() =>
                    {
                        CreateCallCount++;
                        return CreateBehavior();
                    });

                Coordinator = new AlgorithmicLayoutCoordinator(
                    m_workspace.Object,
                    Dispatcher.CurrentDispatcher);
                Orchestrator = new AlgorithmicDesktopCreationOrchestrator(
                    Coordinator,
                    m_manager.Object);
            }

            public AlgorithmicDesktopCreationResult Create(
                IntPtr? handle = null,
                Guid? correlation = null,
                int maximum = 1)
            {
                return Orchestrator.CreateAndPrepareDesktop(
                    handle ?? new IntPtr(101),
                    correlation ?? Guid.NewGuid(),
                    maximum,
                    desktop =>
                    {
                        PrepareCallCount++;
                        PrepareBehavior(desktop);
                    });
            }

            public void Dispose()
            {
                Coordinator.Dispose();
            }
        }
    }
}
