#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Threading;

using FancyWM.AlgorithmicLayouts;
using FancyWM.Models;
using FancyWM.Utilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    public partial class TilingServiceAlgorithmicIntegrationTest
    {
        private const int RuntimeStateCountWarmupQueries = 1_000;
        private const int RuntimeStateCountMeasuredQueries = 10_000;

        [TestMethod]
        public void RuntimeStateCountPreservesCompleteDisplayComparisonOrder()
        {
            var workspace = new ManageabilityAllocationWorkspace();
            var trace = new List<string>();
            var query = RuntimeStateCountDisplay("query", workspace);
            var first = RuntimeStateCountDisplay("first", workspace, trace);
            var middle = RuntimeStateCountDisplay("middle", workspace, trace);
            var last = RuntimeStateCountDisplay("last", workspace, trace);
            first.Equality = other => ReferenceEquals(other, query);
            middle.Equality = _ => false;
            last.Equality = other => ReferenceEquals(other, query);
            var registry = new MasterSatelliteRuntimeRegistry();
            AddRuntimeState(registry, workspace, first);
            AddRuntimeState(registry, workspace, middle);
            AddRuntimeState(registry, workspace, last);
            var session = new MasterSatelliteRuntimeSession(query, registry);
            trace.Clear();

            Assert.AreEqual(2, session.StateCount);
            CollectionAssert.AreEqual(
                new[] { "first.Equals", "middle.Equals", "last.Equals" },
                trace);

            var failure = new InvalidOperationException("late equality failure");
            last.Equality = _ => throw failure;
            trace.Clear();

            var observed = Assert.ThrowsException<InvalidOperationException>(
                () => _ = session.StateCount);
            Assert.AreSame(failure, observed);
            CollectionAssert.AreEqual(
                new[] { "first.Equals", "middle.Equals", "last.Equals" },
                trace);
        }

        [TestMethod]
        public void RuntimeStateCountPreservesAddDuringEnumerationFailure()
        {
            var workspace = new ManageabilityAllocationWorkspace();
            var query = RuntimeStateCountDisplay("query", workspace);
            var first = RuntimeStateCountDisplay("first", workspace);
            var second = RuntimeStateCountDisplay("second", workspace);
            var registry = new MasterSatelliteRuntimeRegistry();
            AddRuntimeState(registry, workspace, first);
            AddRuntimeState(registry, workspace, second);
            var addedKey = new LayoutStateKey(
                new ManageabilityAllocationDesktop(workspace),
                query);
            bool added = false;
            first.Equality = _ =>
            {
                if (!added)
                {
                    added = true;
                    Assert.IsTrue(registry.TryAdd(addedKey, CreateRuntimeState()));
                }
                return true;
            };
            second.Equality = _ => false;
            var session = new MasterSatelliteRuntimeSession(query, registry);

            Assert.ThrowsException<InvalidOperationException>(
                () => _ = session.StateCount);
            Assert.AreEqual(3, registry.Count);

            first.Equality = _ => true;
            Assert.AreEqual(2, session.StateCount);
        }

        [TestMethod]
        public void RuntimeStateCountPreservesRemoveAndClearDuringEnumeration()
        {
            var workspace = new ManageabilityAllocationWorkspace();
            var query = RuntimeStateCountDisplay("query", workspace);
            var first = RuntimeStateCountDisplay("first", workspace);
            var second = RuntimeStateCountDisplay("second", workspace);
            var removeRegistry = new MasterSatelliteRuntimeRegistry();
            AddRuntimeState(removeRegistry, workspace, first);
            var secondKey = AddRuntimeState(removeRegistry, workspace, second);
            first.Equality = _ =>
            {
                Assert.IsTrue(removeRegistry.Remove(secondKey));
                return true;
            };
            second.Equality = _ => true;

            Assert.AreEqual(
                1,
                new MasterSatelliteRuntimeSession(query, removeRegistry).StateCount);
            Assert.AreEqual(1, removeRegistry.Count);

            first = RuntimeStateCountDisplay("clear-first", workspace);
            second = RuntimeStateCountDisplay("clear-second", workspace);
            var clearRegistry = new MasterSatelliteRuntimeRegistry();
            AddRuntimeState(clearRegistry, workspace, first);
            AddRuntimeState(clearRegistry, workspace, second);
            first.Equality = _ =>
            {
                clearRegistry.Clear();
                return true;
            };
            second.Equality = _ => true;

            Assert.AreEqual(
                1,
                new MasterSatelliteRuntimeSession(query, clearRegistry).StateCount);
            Assert.AreEqual(0, clearRegistry.Count);
        }

        [TestMethod]
        public void RuntimeStateCountRetainsCoordinatorThreadDisposeAndLockGuards()
        {
            var workspace = new ManageabilityAllocationWorkspace();
            var query = RuntimeStateCountDisplay("query", workspace);
            var stored = RuntimeStateCountDisplay("stored", workspace);
            using var coordinator = new AlgorithmicLayoutCoordinator(
                workspace,
                Dispatcher.CurrentDispatcher);
            var session = new MasterSatelliteRuntimeSession(query, coordinator);
            bool observedUnderLock = false;
            stored.Equality = other =>
            {
                observedUnderLock = coordinator.IsMutationLockHeldByCurrentThread;
                return ReferenceEquals(other, query);
            };
            Assert.IsTrue(coordinator.TryAdd(
                new LayoutStateKey(
                    new ManageabilityAllocationDesktop(workspace),
                    stored),
                CreateRuntimeState()));

            Assert.AreEqual(1, session.StateCount);
            Assert.IsTrue(observedUnderLock);

            stored.ResetCounters();
            var wrongThread = Task.Run(() =>
                CaptureRuntimeStateCountException(session)).GetAwaiter().GetResult();
            Assert.IsInstanceOfType(wrongThread, typeof(InvalidOperationException));
            Assert.AreEqual(0, stored.EqualityReads);

            coordinator.Dispose();
            coordinator.Dispose();
            Assert.ThrowsException<ObjectDisposedException>(
                () => _ = session.StateCount);

            var wrongThreadAfterDispose = Task.Run(() =>
                CaptureRuntimeStateCountException(session)).GetAwaiter().GetResult();
            Assert.IsInstanceOfType(
                wrongThreadAfterDispose,
                typeof(InvalidOperationException));
        }

        [TestMethod]
        public void RuntimeStateCountLeavesOwnedSnapshotsIsolated()
        {
            var workspace = new ManageabilityAllocationWorkspace();
            var query = RuntimeStateCountDisplay("query", workspace);
            var registry = new MasterSatelliteRuntimeRegistry();
            var firstState = CreateRuntimeState();
            var firstKey = AddRuntimeState(registry, workspace, query, firstState);
            AddRuntimeState(registry, workspace, query);
            var session = new MasterSatelliteRuntimeSession(query, registry);
            var snapshot = session.SnapshotStates();

            Assert.AreEqual(2, snapshot.Count);
            Assert.AreSame(firstState, snapshot[0].State);
            Assert.IsTrue(registry.Remove(firstKey));

            Assert.AreEqual(2, snapshot.Count);
            Assert.AreSame(firstState, snapshot[0].State);
            Assert.AreEqual(1, session.StateCount);
        }

        [TestMethod]
        public void RuntimeStateCountKeepsUnresolvedPlacementFallbackStateAccurate()
        {
            using var fixture = new ServiceFixture(EnabledSettings(false));
            fixture.DrainDispatcher();
            var backend = GetBackend(fixture);
            var tree = backend.GetTree(fixture.Desktop)!;
            var root = tree.Root;
            var focus = backend.GetFocus(fixture.Desktop);
            var newcomer = fixture.CreateWindow("Unresolved count-only placement");
            var placement = BindRuntimeStateCountPlacement(fixture.Service);

            var inactive = InvokeRuntimeStateCountPlacement(
                fixture,
                placement,
                newcomer);

            Assert.AreEqual(
                MasterSatelliteLocalMutationDisposition.NotActive,
                inactive.Result.Disposition);
            Assert.IsFalse(inactive.ActiveLayoutMatched);
            Assert.IsFalse(backend.HasWindow(newcomer));
            Assert.AreSame(root, tree.Root);
            Assert.AreSame(focus, backend.GetFocus(fixture.Desktop));

            var key = new LayoutStateKey(fixture.Desktop, fixture.Display);
            var state = CreateRuntimeState();
            Assert.IsTrue(fixture.Coordinator.TryAdd(key, state));
            long revision = state.Revision;

            var active = InvokeRuntimeStateCountPlacement(
                fixture,
                placement,
                newcomer);

            Assert.AreEqual(
                MasterSatelliteLocalMutationDisposition.Rejected,
                active.Result.Disposition);
            Assert.IsTrue(active.ActiveLayoutMatched);
            Assert.IsNull(active.Result.LayoutKey);
            Assert.IsNull(active.Result.Operation);
            Assert.IsNull(active.Result.Invariant);
            Assert.IsTrue(fixture.Coordinator.TryGet(key, out var retained));
            Assert.AreSame(state, retained);
            Assert.AreEqual(revision, state.Revision);
            Assert.AreEqual(1, fixture.Coordinator.Count);
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            Assert.IsFalse(fixture.Coordinator.FloatingWindows.Contains(newcomer));
            Assert.IsFalse(backend.HasWindow(newcomer));
            Assert.AreSame(root, tree.Root);
            Assert.AreSame(focus, backend.GetFocus(fixture.Desktop));
        }

        [TestMethod]
        public void RuntimeStateCountPlacementUsesFreshStateAfterEarlierSnapshotMutation()
        {
            using var fixture = new ServiceFixture(
                EnabledSettings(false),
                includeSecondDesktop: true);
            fixture.DrainDispatcher();
            var resident = fixture.AddWindow("Resident before snapshot mutation");
            fixture.DrainDispatcher();
            var backend = GetBackend(fixture);
            var tree = backend.GetTree(fixture.Desktop)!;
            var root = tree.Root;
            var residentNode = tree.FindNode(resident);
            Assert.IsNotNull(residentNode);
            var newcomer = fixture.CreateWindow("Snapshot mutation placement");
            var ownKey = new LayoutStateKey(fixture.Desktop, fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryAdd(
                ownKey,
                CreateRuntimeState()));
            Assert.AreEqual(
                1,
                fixture.Coordinator.SnapshotForDisplay(fixture.Display).Count);
            int stableHash = newcomer.Handle.GetHashCode();
            int hashCallbacks = 0;
            bool removedDuringTreeLookup = false;
            Mock.Get(newcomer).Setup(item => item.GetHashCode()).Returns(() =>
            {
                hashCallbacks++;
                if (!removedDuringTreeLookup)
                {
                    removedDuringTreeLookup = fixture.Coordinator.Remove(ownKey);
                }
                return stableHash;
            });

            var result = InvokeRuntimeStateCountPlacement(
                fixture,
                BindRuntimeStateCountPlacement(fixture.Service),
                newcomer);

            Assert.AreEqual(
                MasterSatelliteLocalMutationDisposition.NotActive,
                result.Result.Disposition);
            Assert.IsFalse(result.ActiveLayoutMatched);
            Assert.IsTrue(removedDuringTreeLookup);
            Assert.IsTrue(hashCallbacks > 0);
            Assert.IsFalse(fixture.Coordinator.TryGet(ownKey, out _));
            Assert.AreEqual(0, fixture.Coordinator.Count);
            Assert.IsFalse(backend.HasWindow(newcomer));
            Assert.AreSame(root, tree.Root);
            Assert.AreSame(residentNode, tree.FindNode(resident));
        }

#if !DEBUG
        [DataTestMethod]
        [DataRow("own", 0)]
        [DataRow("own", 1)]
        [DataRow("own", 4)]
        [DataRow("own", 10)]
        [DataRow("own", 50)]
        [DataRow("mixed", 0)]
        [DataRow("mixed", 1)]
        [DataRow("mixed", 4)]
        [DataRow("mixed", 10)]
        [DataRow("mixed", 50)]
        public void RuntimeStateCountDoesNotAllocate(string mode, int stateCount)
        {
            var measurement = MeasureRuntimeStateCount(mode, stateCount);

            Assert.AreEqual(
                0L,
                measurement.AllocatedBytes,
                $"StateCount allocated {measurement.AllocatedBytes} bytes for "
                    + $"{RuntimeStateCountMeasuredQueries} {mode}/{stateCount} "
                    + "queries.");
        }
#endif

        [TestMethod]
        public void RuntimeStateCountCounterScenario()
        {
            foreach (string mode in new[] { "own", "mixed" })
            {
                foreach (int stateCount in new[] { 0, 1, 4, 10, 50 })
                {
                    var measurement = MeasureRuntimeStateCount(mode, stateCount);
                    string scenario = $"runtime-state-count-{mode}-{stateCount}";
                    TestContext.WriteLine(
                        $"PERFCOUNTER {scenario} allocated-bytes {measurement.AllocatedBytes}");
                    TestContext.WriteLine(
                        $"PERFCOUNTER {scenario} elapsed-ticks {measurement.ElapsedTicks}");
                    TestContext.WriteLine(
                        $"PERFCOUNTER {scenario} timestamp-frequency {Stopwatch.Frequency}");
                    TestContext.WriteLine(
                        $"PERFCOUNTER {scenario} queries {RuntimeStateCountMeasuredQueries}");
                    TestContext.WriteLine(
                        $"PERFCOUNTER {scenario} count-sum {measurement.CountSum}");
                    TestContext.WriteLine(
                        $"PERFCOUNTER {scenario} equality-reads {measurement.EqualityReads}");
                }
            }
        }

        private static RuntimeStateCountMeasurement MeasureRuntimeStateCount(
            string mode,
            int stateCount)
        {
            bool mixed = mode switch
            {
                "own" => false,
                "mixed" => true,
                _ => throw new ArgumentOutOfRangeException(nameof(mode)),
            };
            var workspace = new ManageabilityAllocationWorkspace();
            var query = RuntimeStateCountDisplay("query", workspace);
            var storedDisplays = new ManageabilityDisplay[stateCount];
            using var coordinator = new AlgorithmicLayoutCoordinator(
                workspace,
                Dispatcher.CurrentDispatcher);
            for (int index = 0; index < stateCount; index++)
            {
                var stored = mixed
                    ? RuntimeStateCountDisplay($"stored-{index}", workspace)
                    : query;
                if (mixed)
                {
                    bool matches = index % 2 == 0;
                    stored.Equality = other =>
                        matches && ReferenceEquals(other, query);
                }
                storedDisplays[index] = stored;
                Assert.IsTrue(coordinator.TryAdd(
                    new LayoutStateKey(
                        new ManageabilityAllocationDesktop(workspace),
                        stored),
                    CreateRuntimeState()));
            }
            var session = new MasterSatelliteRuntimeSession(query, coordinator);
            long warmupSum = 0;
            for (int queryIndex = 0;
                queryIndex < RuntimeStateCountWarmupQueries;
                queryIndex++)
            {
                warmupSum += session.StateCount;
            }
            for (int index = 0; index < storedDisplays.Length; index++)
            {
                storedDisplays[index].ResetCounters();
            }

            long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            long countSum = 0;
            for (int queryIndex = 0;
                queryIndex < RuntimeStateCountMeasuredQueries;
                queryIndex++)
            {
                countSum += session.StateCount;
            }
            long finish = Stopwatch.GetTimestamp();
            long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;

            long equalityReads = 0;
            for (int index = 0; index < storedDisplays.Length; index++)
            {
                equalityReads += storedDisplays[index].EqualityReads;
            }
            long expectedCount = mixed ? (stateCount + 1L) / 2L : stateCount;
            Assert.AreEqual(
                RuntimeStateCountWarmupQueries * expectedCount,
                warmupSum);
            Assert.AreEqual(
                RuntimeStateCountMeasuredQueries * expectedCount,
                countSum);
            Assert.AreEqual(
                mixed
                    ? RuntimeStateCountMeasuredQueries * (long)stateCount
                    : 0L,
                equalityReads);
            Assert.IsTrue(finish > start);
            GC.KeepAlive(session);
            return new RuntimeStateCountMeasurement(
                allocatedBytes,
                finish - start,
                countSum,
                equalityReads);
        }

        private static ManageabilityDisplay RuntimeStateCountDisplay(
            string name,
            IWorkspace workspace,
            List<string>? trace = null)
        {
            return new ManageabilityDisplay(
                name,
                Rectangle.OffsetAndSize(0, 0, 1920, 1080),
                trace)
            {
                Workspace = workspace,
            };
        }

        private static LayoutStateKey AddRuntimeState(
            MasterSatelliteRuntimeRegistry registry,
            IWorkspace workspace,
            IDisplay display,
            MasterSatelliteRuntimeState? state = null)
        {
            var key = new LayoutStateKey(
                new ManageabilityAllocationDesktop(workspace),
                display);
            Assert.IsTrue(registry.TryAdd(key, state ?? CreateRuntimeState()));
            return key;
        }

        private static MasterSatelliteRuntimeState CreateRuntimeState()
            => new(new MasterSatelliteLayoutSettings(), isActive: true);

        private static Exception? CaptureRuntimeStateCountException(
            MasterSatelliteRuntimeSession session)
        {
            try
            {
                _ = session.StateCount;
                return null;
            }
            catch (Exception error)
            {
                return error;
            }
        }

        private static RuntimeStateCountPlacement BindRuntimeStateCountPlacement(
            TilingService service)
        {
            return typeof(TilingService)
                .GetMethod(
                    "PlaceMasterSatelliteWindowLocked",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<RuntimeStateCountPlacement>(service);
        }

        private static RuntimeStateCountPlacementResult InvokeRuntimeStateCountPlacement(
            ServiceFixture fixture,
            RuntimeStateCountPlacement placement,
            IWindow window)
        {
            var backendLock = fixture.GetServiceField<DebugLock>("m_backendLock");
            using (backendLock.EnterScope())
            {
                var result = placement(
                    window,
                    out bool activeLayoutMatched,
                    observedDesktop: null);
                Assert.IsTrue(fixture.Service.IsAlgorithmicTransferLockHeldByCurrentThread);
                return new RuntimeStateCountPlacementResult(
                    result,
                    activeLayoutMatched);
            }
        }

        private delegate MasterSatelliteLocalMutationResult RuntimeStateCountPlacement(
            IWindow window,
            out bool activeLayoutMatched,
            IVirtualDesktop? observedDesktop);

        private readonly record struct RuntimeStateCountPlacementResult(
            MasterSatelliteLocalMutationResult Result,
            bool ActiveLayoutMatched);

        private readonly record struct RuntimeStateCountMeasurement(
            long AllocatedBytes,
            long ElapsedTicks,
            long CountSum,
            long EqualityReads);
    }
}
