using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using FancyWM.Utilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using Serilog;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    public partial class TilingServiceAlgorithmicIntegrationTest
    {
        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void RefreshUsesStableWindowSnapshotWhenStateReadMutatesTrackedWindows(
            bool enabled)
        {
            using var fixture = new ServiceFixture(EnabledSettings(enabled));
            fixture.DrainDispatcher();
            var initiallyTracked = fixture.CreateWindow(
                "Initially tracked",
                canResize: false);
            var addedDuringRefresh = fixture.CreateWindow(
                "Added during refresh",
                canResize: false);
            fixture.AddWindow(initiallyTracked);

            int initiallyTrackedStateReads = 0;
            int addedDuringRefreshStateReads = 0;
            bool mutated = false;
            Mock.Get(addedDuringRefresh)
                .SetupGet(window => window.State)
                .Returns(() =>
                {
                    addedDuringRefreshStateReads++;
                    return WindowState.Restored;
                });
            Mock.Get(initiallyTracked)
                .SetupGet(window => window.State)
                .Returns(() =>
                {
                    initiallyTrackedStateReads++;
                    if (!mutated)
                    {
                        mutated = true;
                        fixture.AddWindow(addedDuringRefresh);
                        // Exclude synchronous WindowAdded work from this Refresh pass.
                        addedDuringRefreshStateReads = 0;
                    }
                    return WindowState.Restored;
                });

            fixture.Service.Refresh();
            Assert.IsTrue(mutated);
            Assert.AreEqual(1, initiallyTrackedStateReads);
            Assert.AreEqual(0, addedDuringRefreshStateReads);

            fixture.Service.Refresh();
            Assert.AreEqual(2, initiallyTrackedStateReads);
            Assert.AreEqual(1, addedDuringRefreshStateReads);
            Assert.IsFalse(GetBackend(fixture).HasWindow(initiallyTracked));
            Assert.IsFalse(GetBackend(fixture).HasWindow(addedDuringRefresh));
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void RefreshPreservesMembershipProbeOrderOutsideLocks(bool enabled)
        {
            using var fixture = new ServiceFixture(
                EnabledSettings(enabled),
                includeSecondDesktop: true);
            fixture.DrainDispatcher();
            fixture.AddWindow("First source window");
            fixture.AddWindow("Target window", fixture.TargetDesktop);
            fixture.AddWindow("Last source window");
            fixture.DrainDispatcher();
            fixture.Service.AutoRegisterWindows = false;

            var backend = GetBackend(fixture);
            var windows = fixture.GetServiceField<HashSet<IWindow>>("m_windowSet").ToArray();
            var memberships = backend.SnapshotDesktops()
                .SelectMany(desktop => windows
                    .Where(window => backend.GetTree(desktop)!.FindNode(window) != null)
                    .Select(window => (
                        Desktop: desktop,
                        Window: window,
                        Node: backend.GetTree(desktop)!.FindNode(window),
                        OriginalPosition: backend.GetOriginalPosition(window))))
                .ToArray();
            Assert.AreEqual(3, windows.Length);
            Assert.AreEqual(3, memberships.Length);

            var expectedTrace = windows.Select(window => $"State:{window.Handle}")
                .Concat(memberships.Select(membership =>
                    $"Owns:{membership.Desktop.Index}:{membership.Window.Handle}"))
                .ToArray();
            var trace = new List<string>();
            var lockSamples = new List<(bool Backend, bool WindowSet)>();
            var backendLock = fixture.GetServiceField<DebugLock>("m_backendLock");
            var windowSetLock = fixture.GetServiceField<DebugLock>("m_windowSetLock");
            foreach (var window in windows)
            {
                Mock.Get(window).SetupGet(item => item.State).Returns(() =>
                {
                    trace.Add($"State:{window.Handle}");
                    lockSamples.Add((backendLock.IsHeldByCurrentThread,
                        windowSetLock.IsHeldByCurrentThread));
                    return WindowState.Restored;
                });
            }
            foreach (var desktopMock in new[] { fixture.DesktopMock, fixture.TargetDesktopMock })
            {
                var desktop = desktopMock.Object;
                desktopMock.Setup(item => item.HasWindow(It.IsAny<IWindow>()))
                    .Returns((IWindow window) =>
                    {
                        trace.Add($"Owns:{desktop.Index}:{window.Handle}");
                        // Refresh catches ownership-probe exceptions. Record lock
                        // ownership here and assert after the call so it cannot
                        // swallow a failed assertion from the adapter callback.
                        lockSamples.Add((backendLock.IsHeldByCurrentThread,
                            windowSetLock.IsHeldByCurrentThread));
                        return ReferenceEquals(fixture.GetWindowDesktop(window), desktop);
                    });
            }

            fixture.Service.Refresh();

            CollectionAssert.AreEqual(expectedTrace, trace);
            Assert.AreEqual(expectedTrace.Length, lockSamples.Count);
            Assert.IsTrue(lockSamples.All(sample => !sample.Backend && !sample.WindowSet),
                "State and ownership adapters must run outside both service locks.");
            foreach (var membership in memberships)
            {
                Assert.AreSame(membership.Node,
                    backend.GetTree(membership.Desktop)!.FindNode(membership.Window));
                Assert.AreEqual(membership.OriginalPosition,
                    backend.GetOriginalPosition(membership.Window));
            }
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
        }

        [TestMethod]
        public void RefreshWindowSnapshotCounterScenario()
        {
            const int warmups = 1000;
            const int passes = 100000;
            foreach (int windowCount in new[] { 0, 1, 10, 50 })
            {
                using var logger = new LoggerConfiguration()
                    .MinimumLevel.Information()
                    .CreateLogger();
                using var fixture = new ManageabilityAllocationFixture(
                    covariantProviderShape: true,
                    discoveryWindowCount: windowCount,
                    logger: logger);
                for (int iteration = 0; iteration < warmups; iteration++)
                {
                    fixture.Service.Refresh();
                }
                fixture.ResetCounters();

                long before = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                for (int iteration = 0; iteration < passes; iteration++)
                {
                    fixture.Service.Refresh();
                }
                long elapsed = Stopwatch.GetTimestamp() - started;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                int stateReads = fixture.DiscoveryWindows.Sum(window => window.StateReads);
                int positionReads = fixture.DiscoveryWindows.Sum(window => window.PositionReads);
                int resizeReads = fixture.DiscoveryWindows.Sum(window => window.ResizeReads);
                int moveReads = fixture.DiscoveryWindows.Sum(window => window.MoveReads);
                Assert.AreEqual(passes * windowCount, stateReads);
                Assert.AreEqual(passes * windowCount, positionReads);
                Assert.AreEqual(passes * windowCount, resizeReads);
                Assert.AreEqual(0, moveReads);
                Assert.AreEqual(0, fixture.DesktopManager.PinReads);
                Assert.AreEqual(0, fixture.DesktopManager.DesktopSnapshotReads);
                Assert.AreEqual(0, fixture.DisplayManager.SnapshotReads);

                string scenario = $"refresh-window-snapshot-{windowCount}";
                Console.WriteLine($"PERFCOUNTER {scenario} allocated-bytes {allocated}");
                Console.WriteLine($"PERFCOUNTER {scenario} elapsed-ticks {elapsed}");
                Console.WriteLine($"PERFCOUNTER {scenario} timestamp-frequency {Stopwatch.Frequency}");
                Console.WriteLine($"PERFCOUNTER {scenario} passes {passes}");
                Console.WriteLine($"PERFCOUNTER {scenario} state-reads {stateReads}");
                Console.WriteLine($"PERFCOUNTER {scenario} position-reads {positionReads}");
                Console.WriteLine($"PERFCOUNTER {scenario} resize-reads {resizeReads}");
                Console.WriteLine($"PERFCOUNTER {scenario} move-reads {moveReads}");
                Console.WriteLine($"PERFCOUNTER {scenario} pin-reads {fixture.DesktopManager.PinReads}");
                Console.WriteLine($"PERFCOUNTER {scenario} desktop-snapshot-reads {fixture.DesktopManager.DesktopSnapshotReads}");
                Console.WriteLine($"PERFCOUNTER {scenario} display-snapshot-reads {fixture.DisplayManager.SnapshotReads}");
                Console.WriteLine($"PERFCOUNTER {scenario} tracked-windows {windowCount}");
            }
        }

#if !DEBUG
        [DataTestMethod]
        [DataRow(0, 128L)]
        [DataRow(1, 160L)]
        [DataRow(10, 232L)]
        [DataRow(50, 552L)]
        public void RefreshWindowSnapshotAvoidsListWrapperAllocation(
            int windowCount,
            long maximumBytesPerPass)
        {
            const int warmups = 100;
            const int passes = 1000;
            using var logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .CreateLogger();
            using var fixture = new ManageabilityAllocationFixture(
                covariantProviderShape: true,
                discoveryWindowCount: windowCount,
                logger: logger);
            for (int iteration = 0; iteration < warmups; iteration++)
            {
                fixture.Service.Refresh();
            }
            fixture.ResetCounters();

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0; iteration < passes; iteration++)
            {
                fixture.Service.Refresh();
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(passes * windowCount,
                fixture.DiscoveryWindows.Sum(window => window.StateReads));
            Assert.AreEqual(passes * windowCount,
                fixture.DiscoveryWindows.Sum(window => window.PositionReads));
            Assert.AreEqual(passes * windowCount,
                fixture.DiscoveryWindows.Sum(window => window.ResizeReads));
            Assert.AreEqual(0,
                fixture.DiscoveryWindows.Sum(window => window.MoveReads));
            Assert.AreEqual(0, fixture.DesktopManager.PinReads);
            Assert.AreEqual(0, fixture.DesktopManager.DesktopSnapshotReads);
            Assert.AreEqual(0, fixture.DisplayManager.SnapshotReads);
            Assert.IsTrue(
                allocated <= maximumBytesPerPass * passes,
                $"Refresh allocated {allocated} bytes for {passes} passes "
                    + $"with {windowCount} tracked windows "
                    + $"({allocated / (double)passes:F3} B/pass), above the "
                    + $"{maximumBytesPerPass} B/pass budget.");
        }
#endif
    }
}
