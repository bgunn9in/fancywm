#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using WinMan;
using WinMan.Windows;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class Win32DisplayManagerSnapshotTest
    {
        [TestMethod]
        public void DisplaysReturnsFreshStableSortedConcreteLists()
        {
            var fixture = new DisplayManagerFixture("b", "a", "a", "c");

            var first = fixture.Manager.Displays;
            var second = fixture.Manager.Displays;

            Assert.IsInstanceOfType(first, typeof(List<Win32Display>));
            Assert.IsInstanceOfType(second, typeof(List<Win32Display>));
            Assert.AreNotSame(first, second);
            CollectionAssert.AreEqual(
                new[] { fixture.Displays[1], fixture.Displays[2], fixture.Displays[0], fixture.Displays[3] },
                first.Cast<Win32Display>().ToArray());
            ((List<Win32Display>)first).RemoveAt(0);
            Assert.AreEqual(4, second.Count);
            Assert.AreSame(fixture.Displays[1], second[0]);
            Assert.AreSame(fixture.Displays[2], second[1],
                "Equal DeviceID values must retain source order.");
        }

        [TestMethod]
        public void DisplaysRefreshesOrderForCallerCulture()
        {
            CultureInfo originalCulture = CultureInfo.CurrentCulture;
            try
            {
                var fixture = new DisplayManagerFixture("z", "ä", "a");
                foreach (string cultureName in new[] { "en-US", "sv-SE", "en-US" })
                {
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
                    string[] expected = fixture.DeviceIds
                        .OrderBy(value => value)
                        .ToArray();
                    string[] actual = fixture.Manager.Displays
                        .Cast<Win32Display>()
                        .Select(fixture.GetDeviceId)
                        .ToArray();
                    CollectionAssert.AreEqual(expected, actual, cultureName);
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
            }
        }

        [TestMethod]
        public void PublishedDisplayUpdateIsAtomicWithSnapshotReads()
        {
            var fixture = new DisplayManagerFixture("b", "a");
            var oldSnapshot = fixture.Manager.Displays;

            fixture.Replace("d", "c", "b");
            var newSnapshot = fixture.Manager.Displays;

            CollectionAssert.AreEqual(
                new[] { "a", "b" },
                oldSnapshot.Cast<Win32Display>().Select(fixture.GetDeviceId).ToArray());
            CollectionAssert.AreEqual(
                new[] { "b", "c", "d" },
                newSnapshot.Cast<Win32Display>().Select(fixture.GetDeviceId).ToArray());

            int invalidSnapshots = 0;
            using var start = new ManualResetEventSlim();
            var reader = Task.Run(() =>
            {
                start.Wait();
                for (int iteration = 0; iteration < 1000; iteration++)
                {
                    string joined = string.Join(",", fixture.Manager.Displays
                        .Cast<Win32Display>()
                        .Select(fixture.GetDeviceId));
                    if (joined != "b,c,d" && joined != "e,f")
                    {
                        Interlocked.Increment(ref invalidSnapshots);
                    }
                }
            });
            start.Set();
            for (int iteration = 0; iteration < 100; iteration++)
            {
                fixture.Replace(iteration % 2 == 0
                    ? new[] { "f", "e" }
                    : new[] { "d", "c", "b" });
            }
            Assert.IsTrue(reader.Wait(TimeSpan.FromSeconds(10)));
            Assert.AreEqual(0, invalidSnapshots);
        }

        [DataTestMethod]
        [DataRow(1)]
        [DataRow(10)]
        [DataRow(50)]
        public void DisplaysAvoidsRepeatedSortingPipelineAllocation(int displayCount)
        {
            long allocated = MeasureSnapshotReads(
                displayCount,
                10000,
                out _,
                out _,
                out _);
            long bytesPerCallBudget = displayCount switch
            {
                1 => 80,
                10 => 160,
                50 => 480,
                _ => throw new ArgumentOutOfRangeException(nameof(displayCount)),
            };
            Assert.IsTrue(
                allocated <= 10000 * bytesPerCallBudget,
                $"The display snapshot rebuilt its sorting pipeline for {displayCount} displays: {allocated} bytes.");
        }

        [DataTestMethod]
        [DataRow(1)]
        [DataRow(10)]
        [DataRow(50)]
        public void DisplaysSnapshotAllocationCounterScenario(int displayCount)
        {
            const int iterations = 10000;
            long allocated = MeasureSnapshotReads(
                displayCount,
                iterations,
                out long elapsed,
                out long checksum,
                out int freshLists);

            Assert.IsTrue(allocated > 0);
            Assert.IsTrue(elapsed > 0);
            Console.WriteLine($"PERFCOUNTER display-provider-snapshot-{displayCount} calls {iterations}");
            Console.WriteLine($"PERFCOUNTER display-provider-snapshot-{displayCount} allocated-bytes {allocated}");
            Console.WriteLine($"PERFCOUNTER display-provider-snapshot-{displayCount} elapsed-ticks {elapsed}");
            Console.WriteLine($"PERFCOUNTER display-provider-snapshot-{displayCount} timestamp-frequency {Stopwatch.Frequency}");
            Console.WriteLine($"PERFCOUNTER display-provider-snapshot-{displayCount} returned-items {iterations * displayCount}");
            Console.WriteLine($"PERFCOUNTER display-provider-snapshot-{displayCount} fresh-lists {freshLists}");
            Console.WriteLine($"PERFCOUNTER display-provider-snapshot-{displayCount} checksum {checksum}");
        }

        private static long MeasureSnapshotReads(
            int displayCount,
            int iterations,
            out long elapsed,
            out long checksum,
            out int freshLists)
        {
            string[] ids = Enumerable.Range(0, displayCount)
                .Reverse()
                .Select(index => $"display-{index:D3}")
                .ToArray();
            var fixture = new DisplayManagerFixture(ids);
            checksum = 0;
            freshLists = 0;
            for (int iteration = 0; iteration < 1000; iteration++)
            {
                checksum ^= fixture.Manager.Displays.Count;
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            object? previous = null;
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                var snapshot = fixture.Manager.Displays;
                if (!ReferenceEquals(previous, snapshot))
                {
                    freshLists++;
                }
                previous = snapshot;
                checksum = unchecked(checksum
                    + snapshot.Count * 17L
                    + fixture.GetDeviceId((Win32Display)snapshot[0]).Length);
            }
            elapsed = Stopwatch.GetTimestamp() - started;
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        private sealed class DisplayManagerFixture
        {
            private static readonly FieldInfo DisplaysField = typeof(Win32DisplayManager)
                .GetField("m_displays", BindingFlags.Instance | BindingFlags.NonPublic)!;
            private static readonly FieldInfo DeviceIdField = typeof(Win32Display)
                .GetField("m_deviceID", BindingFlags.Instance | BindingFlags.NonPublic)!;
            private static readonly MethodInfo? RefreshSnapshotMethod =
                typeof(Win32DisplayManager).GetMethod(
                    "RefreshDisplaySnapshotUnderLock",
                    BindingFlags.Instance | BindingFlags.NonPublic);

            private List<Win32Display> m_displays;

            public Win32DisplayManager Manager { get; }
            public IReadOnlyList<Win32Display> Displays => m_displays;
            public IReadOnlyList<string> DeviceIds => m_displays.Select(GetDeviceId).ToArray();

            public DisplayManagerFixture(params string[] deviceIds)
            {
                Manager = (Win32DisplayManager)RuntimeHelpers.GetUninitializedObject(
                    typeof(Win32DisplayManager));
                m_displays = deviceIds.Select(CreateDisplay).ToList();
                DisplaysField.SetValue(Manager, m_displays);
                RefreshSnapshot();
            }

            public string GetDeviceId(Win32Display display) =>
                (string)DeviceIdField.GetValue(display)!;

            public void Replace(params string[] deviceIds)
            {
                lock (m_displays)
                {
                    m_displays.Clear();
                    m_displays.AddRange(deviceIds.Select(CreateDisplay));
                    RefreshSnapshot();
                }
            }

            private void RefreshSnapshot()
            {
                RefreshSnapshotMethod?.Invoke(Manager, null);
            }

            private static Win32Display CreateDisplay(string deviceId)
            {
                var display = (Win32Display)RuntimeHelpers.GetUninitializedObject(
                    typeof(Win32Display));
                DeviceIdField.SetValue(display, deviceId);
                return display;
            }
        }
    }
}
