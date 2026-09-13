#nullable enable

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

using FancyWM.Layouts.Tiling;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    public partial class MultiDisplayTilingServiceIntegrationTest
    {
        [TestMethod]
        public void DisplayRemovalForcedGcCounterScenario()
        {
            foreach (int duplicateCount in new[] { 1, 10, 50 })
            {
                MeasureDisplayRemovalForcedGc(duplicateCount);
            }
        }

        private static void MeasureDisplayRemovalForcedGc(int duplicateCount)
        {
            const int cycles = 3;
            const int churnBytesPerEvent = 64 * 1024;
            long gcPauseTicks = 0;
            int fullGcPasses = 0;
            int removedOwners = 0;
            int routingChecks = 0;
            long checksum = 0;

            ForceFullCollection();
            long managedBytesBefore = GC.GetTotalMemory(forceFullCollection: false);
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            int gen0Before = GC.CollectionCount(0);
            int gen1Before = GC.CollectionCount(1);
            int gen2Before = GC.CollectionCount(2);
            long started = Stopwatch.GetTimestamp();
            int collectionRequests = 0;

            for (int cycle = 0; cycle < cycles; cycle++)
            {
                using var fixture = new ServiceFixture(
                    includeSecondDisplay: true,
                    onGarbageCollectionRequest: () =>
                    {
                        collectionRequests++;
                        long gcStarted = Stopwatch.GetTimestamp();
                        ForceFullCollection();
                        gcPauseTicks += Stopwatch.GetTimestamp() - gcStarted;
                        fullGcPasses += 2;
                    });
                var removed = fixture.GetService(fixture.SecondDisplay);
                int requestsBeforeCycle = collectionRequests;

                checksum += AllocateGarbage(churnBytesPerEvent, cycle);
                fixture.RemoveDisplay(fixture.SecondDisplay);
                for (int duplicate = 0; duplicate < duplicateCount; duplicate++)
                {
                    checksum += AllocateGarbage(churnBytesPerEvent, duplicate + cycle + 1);
                    fixture.RemoveDisplay(fixture.SecondDisplay);
                }

                Assert.AreEqual(1, removed.StopCount);
                Assert.AreEqual(1, removed.DisposeCount);
                Assert.AreEqual(collectionRequests - requestsBeforeCycle, fixture.GarbageCollectionRequests);
                Assert.IsTrue(fixture.GarbageCollectionRequests >= 1);
                fixture.Service.ToggleMasterSatelliteLayout();
                CollectionAssert.AreEqual(
                    new[] { nameof(ITilingService.ToggleMasterSatelliteLayout) },
                    fixture.GetService(fixture.PrimaryDisplay).CommandCalls);
                removedOwners++;
                routingChecks++;
            }

            long elapsedTicks = Stopwatch.GetTimestamp() - started;
            long allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            long managedBytesBeforeFinalCollection = GC.GetTotalMemory(forceFullCollection: false);
            int gen0Collections = GC.CollectionCount(0) - gen0Before;
            int gen1Collections = GC.CollectionCount(1) - gen1Before;
            int gen2Collections = GC.CollectionCount(2) - gen2Before;
            ForceFullCollection();
            long settledManagedBytes = GC.GetTotalMemory(forceFullCollection: false);

            Assert.AreEqual(collectionRequests * 2, fullGcPasses);
            Assert.AreEqual(cycles, removedOwners);
            Assert.AreEqual(cycles, routingChecks);
            Assert.IsTrue(elapsedTicks >= gcPauseTicks);
            Assert.AreNotEqual(0L, checksum);

            string scenario = $"display-removal-gc-pause-{duplicateCount}";
            Counter(scenario, "collection-requests", collectionRequests);
            Counter(scenario, "full-gc-passes", fullGcPasses);
            Counter(scenario, "gen0-collections", gen0Collections);
            Counter(scenario, "gen1-collections", gen1Collections);
            Counter(scenario, "gen2-collections", gen2Collections);
            Counter(scenario, "allocated-bytes", allocatedBytes);
            Counter(scenario, "elapsed-ticks", elapsedTicks);
            Counter(scenario, "gc-pause-ticks", gcPauseTicks);
            Counter(scenario, "non-gc-ticks", elapsedTicks - gcPauseTicks);
            Counter(scenario, "timestamp-frequency", Stopwatch.Frequency);
            Counter(scenario, "managed-bytes-before", managedBytesBefore);
            Counter(scenario, "managed-bytes-before-final-collection", managedBytesBeforeFinalCollection);
            Counter(scenario, "settled-managed-bytes", settledManagedBytes);
            Counter(scenario, "removed-owners", removedOwners);
            Counter(scenario, "duplicate-events", duplicateCount * cycles);
            Counter(scenario, "event-calls", (duplicateCount + 1) * cycles);
            Counter(scenario, "cycles", cycles);
            Counter(scenario, "churn-bytes", (long)(duplicateCount + 1) * cycles * churnBytesPerEvent);
            Counter(scenario, "routing-checks", routingChecks);
            Counter(scenario, "checksum", checksum);
        }

        private static void ForceFullCollection()
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static long AllocateGarbage(int bytes, int seed)
        {
            byte[] garbage = GC.AllocateUninitializedArray<byte>(bytes);
            garbage[0] = (byte)seed;
            garbage[^1] = (byte)(seed * 31);
            return garbage[0] + garbage[^1] + garbage.Length;
        }

        private static void Counter(string scenario, string metric, long value)
        {
            Console.WriteLine($"PERFCOUNTER {scenario} {metric} {value}");
        }
    }
}
