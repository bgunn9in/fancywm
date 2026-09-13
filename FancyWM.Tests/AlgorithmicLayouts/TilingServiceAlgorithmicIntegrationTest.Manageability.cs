#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reactive.Subjects;

using FancyWM.AlgorithmicLayouts;
using FancyWM.Models;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using Serilog;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    public partial class TilingServiceAlgorithmicIntegrationTest
    {
        [TestMethod]
        public void CanManageShortCircuitsTopmostFloatingAndCurrentDisplayBeforeSnapshot()
        {
            using var fixture = new ServiceFixture(EnabledSettings(false));
            var canManage = BindCanManage(fixture.Service);
            var displayManager = Mock.Get(fixture.Service.Workspace.DisplayManager);
            var window = fixture.CreateWindow("Manageability gates");
            var windowMock = Mock.Get(window);
            int positionReads = 0;
            var insideCurrentDisplay = Rectangle.OffsetAndSize(100, 100, 800, 600);
            windowMock.SetupGet(item => item.Position).Returns(() =>
            {
                positionReads++;
                return insideCurrentDisplay;
            });
            displayManager.SetupGet(item => item.Displays)
                .Throws(new InvalidOperationException("The current-display path read the display snapshot."));

            windowMock.SetupGet(item => item.IsTopmost).Returns(true);
            Assert.IsFalse(canManage(window, false));
            Assert.AreEqual(0, positionReads);

            windowMock.SetupGet(item => item.IsTopmost).Returns(false);
            Assert.IsTrue(fixture.Coordinator.FloatingWindows.Add(window));
            Assert.IsFalse(canManage(window, false));
            Assert.AreEqual(0, positionReads);

            Assert.IsTrue(canManage(window, true));
            Assert.AreEqual(1, positionReads);
            displayManager.VerifyGet(item => item.Displays, Times.Never);
        }

        [DataTestMethod]
        [DataRow(-1)]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        public void CanManagePreservesOrderedForeignDisplayProbesAndFirstMatch(int matchIndex)
        {
            using var fixture = new ServiceFixture(EnabledSettings(false));
            var canManage = BindCanManage(fixture.Service);
            var displayManager = Mock.Get(fixture.Service.Workspace.DisplayManager);
            var trace = new List<string>();
            var outsideCurrentDisplay = Rectangle.OffsetAndSize(5000, 200, 400, 300);
            var hitBounds = Rectangle.OffsetAndSize(4900, 0, 1000, 1000);
            var missBounds = Rectangle.OffsetAndSize(10000, 0, 1000, 1000);
            var alias = new ManageabilityDisplay("self", missBounds, trace)
            {
                Equality = other => ReferenceEquals(other, fixture.Display)
            };
            var foreign = new[]
            {
                new ManageabilityDisplay("foreign-0", matchIndex == 0 ? hitBounds : missBounds, trace),
                new ManageabilityDisplay("foreign-1", matchIndex == 1 ? hitBounds : missBounds, trace),
                new ManageabilityDisplay("foreign-2", matchIndex == 2 ? hitBounds : missBounds, trace),
            };
            displayManager.SetupGet(item => item.Displays)
                .Returns(new IDisplay[] { alias, foreign[0], foreign[1], foreign[2] });
            var window = ManageabilityWindow(outsideCurrentDisplay, out var positionReads);

            Assert.AreEqual(matchIndex < 0, canManage(window.Object, true));
            Assert.AreEqual(1, positionReads());
            Assert.AreEqual(0, alias.BoundsReads);
            var expected = new List<string> { "self.Equals" };
            int visitedForeign = matchIndex < 0 ? foreign.Length : matchIndex + 1;
            for (int index = 0; index < visitedForeign; index++)
            {
                expected.Add($"foreign-{index}.Equals");
                expected.Add($"foreign-{index}.Bounds");
            }
            CollectionAssert.AreEqual(expected, trace);
            for (int index = visitedForeign; index < foreign.Length; index++)
            {
                Assert.AreEqual(0, foreign[index].BoundsReads);
            }
            displayManager.VerifyGet(item => item.Displays, Times.Once);
        }

        [TestMethod]
        public void CanManagePreservesResizeMoveAndPinnedGateOrder()
        {
            using var fixture = new ServiceFixture(EnabledSettings(false));
            var canManage = BindCanManage(fixture.Service);
            var displayManager = Mock.Get(fixture.Service.Workspace.DisplayManager);
            displayManager.SetupGet(item => item.Displays).Returns(Array.Empty<IDisplay>());
            var trace = new List<string>();
            var window = Mock.Get(fixture.CreateWindow("Manageability gate order"));
            window.SetupGet(item => item.IsTopmost).Returns(() =>
            {
                trace.Add("topmost");
                return false;
            });
            window.SetupGet(item => item.Position).Returns(() =>
            {
                trace.Add("position");
                return Rectangle.OffsetAndSize(5000, 100, 200, 200);
            });
            window.SetupGet(item => item.CanResize).Returns(() =>
            {
                trace.Add("resize");
                return false;
            });
            window.SetupGet(item => item.CanMove).Returns(() =>
            {
                trace.Add("move");
                return true;
            });

            Assert.IsFalse(canManage(window.Object, true));
            CollectionAssert.AreEqual(new[] { "topmost", "position", "resize" }, trace);

            trace.Clear();
            window.SetupGet(item => item.CanResize).Returns(() =>
            {
                trace.Add("resize");
                return true;
            });
            window.SetupGet(item => item.CanMove).Returns(() =>
            {
                trace.Add("move");
                return false;
            });
            Assert.IsFalse(canManage(window.Object, true));
            CollectionAssert.AreEqual(new[] { "topmost", "position", "resize", "move" }, trace);

            trace.Clear();
            window.SetupGet(item => item.CanMove).Returns(() =>
            {
                trace.Add("move");
                return true;
            });
            fixture.Pin(window.Object);
            Assert.IsFalse(canManage(window.Object, true));
            CollectionAssert.AreEqual(new[] { "topmost", "position", "resize", "move" }, trace);
            fixture.VirtualDesktopManagerMock.Verify(
                item => item.IsWindowPinned(window.Object), Times.Once);
        }

        [TestMethod]
        public void CanManagePreservesEnumeratorDisposalAndPredicateExceptionIdentity()
        {
            using var fixture = new ServiceFixture(EnabledSettings(false));
            var canManage = BindCanManage(fixture.Service);
            var displayManager = Mock.Get(fixture.Service.Workspace.DisplayManager);
            var failure = new InvalidOperationException("Synthetic display equality failure");
            var throwing = new ManageabilityDisplay(
                "throwing",
                Rectangle.OffsetAndSize(10000, 0, 100, 100))
            {
                Equality = _ => throw failure
            };
            var snapshot = new TrackingDisplaySnapshot(throwing);
            displayManager.SetupGet(item => item.Displays).Returns(snapshot);
            var window = ManageabilityWindow(
                Rectangle.OffsetAndSize(5000, 100, 200, 200),
                out _);

            var actual = Assert.ThrowsException<InvalidOperationException>(
                () => canManage(window.Object, true));
            Assert.AreSame(failure, actual);
            Assert.AreEqual(1, snapshot.EnumeratorCreations);
            Assert.AreEqual(1, snapshot.Yields);
            Assert.AreEqual(1, snapshot.Disposals);
            Assert.AreEqual(0, snapshot.IndexReads,
                "The ordinary enumerable path must not be changed to index-based traversal.");
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void CanManageDisposesCustomEnumerationOnHitAndMiss(bool hit)
        {
            using var fixture = new ServiceFixture(EnabledSettings(false));
            var canManage = BindCanManage(fixture.Service);
            var display = new ManageabilityDisplay(
                "foreign",
                hit
                    ? Rectangle.OffsetAndSize(4900, 0, 1000, 1000)
                    : Rectangle.OffsetAndSize(10000, 0, 1000, 1000));
            var snapshot = new TrackingDisplaySnapshot(display);
            Mock.Get(fixture.Service.Workspace.DisplayManager)
                .SetupGet(item => item.Displays).Returns(snapshot);
            var window = ManageabilityWindow(
                Rectangle.OffsetAndSize(5000, 100, 200, 200),
                out _);

            Assert.AreEqual(!hit, canManage(window.Object, true));
            Assert.AreEqual(1, snapshot.EnumeratorCreations);
            Assert.AreEqual(1, snapshot.Yields);
            Assert.AreEqual(1, snapshot.Disposals);
            Assert.AreEqual(0, snapshot.IndexReads);
        }

        [TestMethod]
        public void CanManagePreservesBoundsFailureIdentityAndDisposesEnumerator()
        {
            using var fixture = new ServiceFixture(EnabledSettings(false));
            var canManage = BindCanManage(fixture.Service);
            var failure = new InvalidOperationException("Synthetic display bounds failure");
            var display = new ManageabilityDisplay(
                "foreign",
                Rectangle.OffsetAndSize(10000, 0, 1000, 1000))
            {
                BoundsRead = () => throw failure
            };
            var snapshot = new TrackingDisplaySnapshot(display);
            Mock.Get(fixture.Service.Workspace.DisplayManager)
                .SetupGet(item => item.Displays).Returns(snapshot);
            var window = ManageabilityWindow(
                Rectangle.OffsetAndSize(5000, 100, 200, 200),
                out _);

            var actual = Assert.ThrowsException<InvalidOperationException>(
                () => canManage(window.Object, true));
            Assert.AreSame(failure, actual);
            Assert.AreEqual(1, display.EqualityReads);
            Assert.AreEqual(1, display.BoundsReads);
            Assert.AreEqual(1, snapshot.Disposals);
        }

        [TestMethod]
        public void CanManagePreservesEnumeratorDisposeFailureIdentity()
        {
            using var fixture = new ServiceFixture(EnabledSettings(false));
            var canManage = BindCanManage(fixture.Service);
            var failure = new InvalidOperationException("Synthetic enumerator disposal failure");
            var snapshot = new TrackingDisplaySnapshot(
                new ManageabilityDisplay(
                    "foreign",
                    Rectangle.OffsetAndSize(10000, 0, 1000, 1000)))
            {
                DisposeFailure = failure
            };
            Mock.Get(fixture.Service.Workspace.DisplayManager)
                .SetupGet(item => item.Displays).Returns(snapshot);
            var window = ManageabilityWindow(
                Rectangle.OffsetAndSize(5000, 100, 200, 200),
                out _);

            var actual = Assert.ThrowsException<InvalidOperationException>(
                () => canManage(window.Object, true));
            Assert.AreSame(failure, actual);
            Assert.AreEqual(1, snapshot.Disposals);
        }

        [TestMethod]
        public void CanManagePreservesNullDisplaySnapshotLinqContract()
        {
            using var fixture = new ServiceFixture(EnabledSettings(false));
            var canManage = BindCanManage(fixture.Service);
            Mock.Get(fixture.Service.Workspace.DisplayManager)
                .SetupGet(item => item.Displays)
                .Returns((IReadOnlyList<IDisplay>)null!);
            var window = ManageabilityWindow(
                Rectangle.OffsetAndSize(5000, 100, 200, 200),
                out _);

            var actual = Assert.ThrowsException<ArgumentNullException>(
                () => canManage(window.Object, true));
            Assert.AreEqual("source", actual.ParamName);
        }

        [TestMethod]
        public void CanManageObservesArrayReplacementBeforeTheElementIsVisited()
        {
            using var fixture = new ServiceFixture(EnabledSettings(false));
            var canManage = BindCanManage(fixture.Service);
            var trace = new List<string>();
            var hitBounds = Rectangle.OffsetAndSize(4900, 0, 1000, 1000);
            var missBounds = Rectangle.OffsetAndSize(10000, 0, 1000, 1000);
            var replacement = new ManageabilityDisplay("replacement", hitBounds, trace);
            var displays = new IDisplay[2];
            var first = new ManageabilityDisplay("first", missBounds, trace)
            {
                Equality = _ =>
                {
                    displays[1] = replacement;
                    return false;
                }
            };
            displays[0] = first;
            displays[1] = new ManageabilityDisplay("stale", missBounds, trace);
            Mock.Get(fixture.Service.Workspace.DisplayManager)
                .SetupGet(item => item.Displays).Returns(displays);
            var window = ManageabilityWindow(
                Rectangle.OffsetAndSize(5000, 100, 200, 200),
                out _);

            Assert.IsFalse(canManage(window.Object, true));
            CollectionAssert.AreEqual(
                new[]
                {
                    "first.Equals", "first.Bounds",
                    "replacement.Equals", "replacement.Bounds"
                },
                trace);
        }

        [TestMethod]
        public void CanManageRetainsExactListLinqMutationBehavior()
        {
            using var fixture = new ServiceFixture(EnabledSettings(false));
            var canManage = BindCanManage(fixture.Service);
            var trace = new List<string>();
            var missBounds = Rectangle.OffsetAndSize(10000, 0, 1000, 1000);
            var displays = new List<IDisplay>();
            var appended = new ManageabilityDisplay("appended", missBounds, trace);
            displays.Add(new ManageabilityDisplay("first", missBounds, trace)
            {
                Equality = _ =>
                {
                    displays.Add(appended);
                    return false;
                }
            });
            displays.Add(new ManageabilityDisplay("second", missBounds, trace));
            Mock.Get(fixture.Service.Workspace.DisplayManager)
                .SetupGet(item => item.Displays).Returns(displays);
            var window = ManageabilityWindow(
                Rectangle.OffsetAndSize(5000, 100, 200, 200),
                out _);

            Assert.IsTrue(canManage(window.Object, true));
            CollectionAssert.AreEqual(
                new[] { "first.Equals", "first.Bounds", "second.Equals", "second.Bounds" },
                trace);
            Assert.AreEqual(3, displays.Count);
            Assert.AreEqual(0, appended.BoundsReads,
                "The List<T> LINQ specialization captures the original span length.");
        }

        [TestMethod]
        public void CanManageRetainsDerivedExactListLinqSpecialization()
        {
            using var fixture = new ServiceFixture(EnabledSettings(false));
            var canManage = BindCanManage(fixture.Service);
            var displays = new DerivedExactDisplayList
            {
                new ManageabilityDisplay(
                    "foreign",
                    Rectangle.OffsetAndSize(10000, 0, 1000, 1000))
            };
            Mock.Get(fixture.Service.Workspace.DisplayManager)
                .SetupGet(item => item.Displays).Returns(displays);
            var window = ManageabilityWindow(
                Rectangle.OffsetAndSize(5000, 100, 200, 200),
                out _);

            Assert.IsTrue(canManage(window.Object, true));
            Assert.AreEqual(0, displays.EnumeratorReads,
                "LINQ specializes List<IDisplay> subclasses without calling a reimplemented enumerator.");
        }

        [TestMethod]
        public void CanManageRetainsCovariantProviderListVersionFailure()
        {
            using var fixture = new ServiceFixture(EnabledSettings(false));
            var canManage = BindCanManage(fixture.Service);
            var trace = new List<string>();
            var missBounds = Rectangle.OffsetAndSize(10000, 0, 1000, 1000);
            var displays = new List<ManageabilityDisplay>();
            displays.Add(new ManageabilityDisplay("first", missBounds, trace)
            {
                Equality = _ =>
                {
                    displays.Add(new ManageabilityDisplay("appended", missBounds, trace));
                    return false;
                }
            });
            displays.Add(new ManageabilityDisplay("second", missBounds, trace));
            IReadOnlyList<IDisplay> covariantSnapshot = displays;
            Mock.Get(fixture.Service.Workspace.DisplayManager)
                .SetupGet(item => item.Displays).Returns(covariantSnapshot);
            var window = ManageabilityWindow(
                Rectangle.OffsetAndSize(5000, 100, 200, 200),
                out _);

            Assert.ThrowsException<InvalidOperationException>(
                () => canManage(window.Object, true));
            CollectionAssert.AreEqual(
                new[] { "first.Equals", "first.Bounds" },
                trace);
        }

        [TestMethod]
        public void CanManageReentrantProbeRetainsEachPositionAndSnapshot()
        {
            using var fixture = new ServiceFixture(EnabledSettings(false));
            var canManage = BindCanManage(fixture.Service);
            var trace = new List<string>();
            var outerWindow = ManageabilityWindow(
                Rectangle.OffsetAndSize(5000, 100, 200, 200),
                out var outerPositionReads);
            var innerWindow = ManageabilityWindow(
                Rectangle.OffsetAndSize(6000, 100, 200, 200),
                out var innerPositionReads);
            var innerDisplay = new ManageabilityDisplay(
                "inner",
                Rectangle.OffsetAndSize(5900, 0, 1000, 1000),
                trace);
            bool insideReentrantCall = false;
            bool? innerResult = null;
            var outerDisplay = new ManageabilityDisplay(
                "outer",
                Rectangle.OffsetAndSize(10000, 0, 1000, 1000),
                trace)
            {
                Equality = _ =>
                {
                    insideReentrantCall = true;
                    try
                    {
                        innerResult = canManage(innerWindow.Object, true);
                    }
                    finally
                    {
                        insideReentrantCall = false;
                    }
                    return false;
                }
            };
            var displayManager = Mock.Get(fixture.Service.Workspace.DisplayManager);
            displayManager.SetupGet(item => item.Displays).Returns(() =>
                insideReentrantCall
                    ? new IDisplay[] { innerDisplay }
                    : new IDisplay[] { outerDisplay });

            Assert.IsTrue(canManage(outerWindow.Object, true));
            Assert.AreEqual(false, innerResult);
            Assert.AreEqual(1, outerPositionReads());
            Assert.AreEqual(1, innerPositionReads());
            CollectionAssert.AreEqual(
                new[]
                {
                    "outer.Equals", "inner.Equals", "inner.Bounds", "outer.Bounds"
                },
                trace);
            displayManager.VerifyGet(item => item.Displays, Times.Exactly(2));
        }

        [DataTestMethod]
        [DataRow(false, 0L)]
        [DataRow(true, 64L)]
        public void CanManageDisplayScanAvoidsCapturingLinqAllocations(
            bool covariantProviderShape,
            long maximumBytesPerCall)
        {
            const int iterations = 1000;
            using var fixture = new ManageabilityAllocationFixture(
                covariantProviderShape);
            for (int iteration = 0; iteration < 100; iteration++)
            {
                Assert.IsTrue(fixture.CanManage(fixture.Window, true));
            }
            fixture.ResetCounters();

            long before = GC.GetAllocatedBytesForCurrentThread();
            int accepted = 0;
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                if (fixture.CanManage(fixture.Window, true))
                {
                    accepted++;
                }
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(iterations, accepted);
            Assert.AreEqual(iterations, fixture.Window.PositionReads);
            Assert.AreEqual(iterations, fixture.DisplayManager.SnapshotReads);
            Assert.AreEqual(iterations, fixture.ForeignDisplay.EqualityReads);
            Assert.AreEqual(iterations, fixture.ForeignDisplay.BoundsReads);
            Assert.AreEqual(iterations, fixture.Window.ResizeReads);
            Assert.AreEqual(iterations, fixture.Window.MoveReads);
            Assert.AreEqual(iterations, fixture.DesktopManager.PinReads);
            Assert.IsTrue(
                allocated <= maximumBytesPerCall * iterations,
                $"The display scan allocated {allocated} bytes for {iterations} calls "
                    + $"({allocated / (double)iterations:F3} B/call), above the "
                    + $"{maximumBytesPerCall} B/call budget.");
        }

        [TestMethod]
        public void CanManageDisplayCounterScenario()
        {
            const int warmups = 1000;
            const int lookups = 100000;
            foreach (int displayCount in new[] { 1, 2, 4 })
            {
                using var fixture = new ManageabilityAllocationFixture(
                    covariantProviderShape: true,
                    displayCount,
                    lastContainsResident: true);
                for (int iteration = 0; iteration < warmups; iteration++)
                {
                    var window = (iteration & 1) == 0
                        ? fixture.ResidentWindow
                        : fixture.Window;
                    _ = fixture.CanManage(window, true);
                }
                fixture.ResetCounters();

                long before = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                int manageable = 0;
                for (int iteration = 0; iteration < lookups; iteration++)
                {
                    var window = (iteration & 1) == 0
                        ? fixture.ResidentWindow
                        : fixture.Window;
                    if (fixture.CanManage(window, true))
                    {
                        manageable++;
                    }
                }
                long elapsed = Stopwatch.GetTimestamp() - started;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                int equalityReads = 0;
                int boundsReads = 0;
                foreach (var display in fixture.ForeignDisplays)
                {
                    equalityReads += display.EqualityReads;
                    boundsReads += display.BoundsReads;
                }
                int positionReads = fixture.Window.PositionReads
                    + fixture.ResidentWindow.PositionReads;
                int resizeReads = fixture.Window.ResizeReads
                    + fixture.ResidentWindow.ResizeReads;
                int moveReads = fixture.Window.MoveReads
                    + fixture.ResidentWindow.MoveReads;

                Assert.AreEqual(lookups / 2, manageable);
                Assert.AreEqual(lookups, positionReads);
                Assert.AreEqual(lookups, fixture.DisplayManager.SnapshotReads);
                Assert.AreEqual(lookups * displayCount, equalityReads);
                Assert.AreEqual(lookups * displayCount, boundsReads);
                Assert.AreEqual(lookups / 2, resizeReads);
                Assert.AreEqual(lookups / 2, moveReads);
                Assert.AreEqual(lookups / 2, fixture.DesktopManager.PinReads);

                string scenario = $"display-manage-provider-{displayCount}";
                Console.WriteLine($"PERFCOUNTER {scenario} allocated-bytes {allocated}");
                Console.WriteLine($"PERFCOUNTER {scenario} elapsed-ticks {elapsed}");
                Console.WriteLine($"PERFCOUNTER {scenario} timestamp-frequency {Stopwatch.Frequency}");
                Console.WriteLine($"PERFCOUNTER {scenario} lookups {lookups}");
                Console.WriteLine($"PERFCOUNTER {scenario} manageable-results {manageable}");
                Console.WriteLine($"PERFCOUNTER {scenario} foreign-results {lookups - manageable}");
                Console.WriteLine($"PERFCOUNTER {scenario} position-reads {positionReads}");
                Console.WriteLine($"PERFCOUNTER {scenario} snapshot-reads {fixture.DisplayManager.SnapshotReads}");
                Console.WriteLine($"PERFCOUNTER {scenario} equality-reads {equalityReads}");
                Console.WriteLine($"PERFCOUNTER {scenario} bounds-reads {boundsReads}");
                Console.WriteLine($"PERFCOUNTER {scenario} resize-reads {resizeReads}");
                Console.WriteLine($"PERFCOUNTER {scenario} move-reads {moveReads}");
                Console.WriteLine($"PERFCOUNTER {scenario} pin-reads {fixture.DesktopManager.PinReads}");
            }
        }

        [TestMethod]
        public void DiscoveryWindowSnapshotCounterScenario()
        {
            const int warmups = 1000;
            const int passes = 100000;
            foreach (int windowCount in new[] { 0, 1, 10, 50 })
            {
                using var fixture = new ManageabilityAllocationFixture(
                    covariantProviderShape: true,
                    discoveryWindowCount: windowCount);
                for (int iteration = 0; iteration < warmups; iteration++)
                {
                    _ = fixture.Service.DiscoverWindows();
                }
                fixture.ResetCounters();

                long before = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                int changedPasses = 0;
                for (int iteration = 0; iteration < passes; iteration++)
                {
                    if (fixture.Service.DiscoverWindows())
                    {
                        changedPasses++;
                    }
                }
                long elapsed = Stopwatch.GetTimestamp() - started;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                int stateReads = 0;
                int positionReads = 0;
                int resizeReads = 0;
                int moveReads = 0;
                foreach (var window in fixture.DiscoveryWindows)
                {
                    stateReads += window.StateReads;
                    positionReads += window.PositionReads;
                    resizeReads += window.ResizeReads;
                    moveReads += window.MoveReads;
                }

                Assert.AreEqual(0, changedPasses);
                Assert.AreEqual(passes * windowCount, stateReads);
                Assert.AreEqual(passes * windowCount, positionReads);
                Assert.AreEqual(passes * windowCount, resizeReads);
                Assert.AreEqual(0, moveReads);
                Assert.AreEqual(0, fixture.DesktopManager.PinReads);
                Assert.AreEqual(0, fixture.DesktopManager.DesktopSnapshotReads);
                Assert.AreEqual(0, fixture.DisplayManager.SnapshotReads);

                string scenario = $"discovery-window-snapshot-{windowCount}";
                Console.WriteLine($"PERFCOUNTER {scenario} allocated-bytes {allocated}");
                Console.WriteLine($"PERFCOUNTER {scenario} elapsed-ticks {elapsed}");
                Console.WriteLine($"PERFCOUNTER {scenario} timestamp-frequency {Stopwatch.Frequency}");
                Console.WriteLine($"PERFCOUNTER {scenario} passes {passes}");
                Console.WriteLine($"PERFCOUNTER {scenario} changed-passes {changedPasses}");
                Console.WriteLine($"PERFCOUNTER {scenario} state-reads {stateReads}");
                Console.WriteLine($"PERFCOUNTER {scenario} position-reads {positionReads}");
                Console.WriteLine($"PERFCOUNTER {scenario} resize-reads {resizeReads}");
                Console.WriteLine($"PERFCOUNTER {scenario} move-reads {moveReads}");
                Console.WriteLine($"PERFCOUNTER {scenario} pin-reads {fixture.DesktopManager.PinReads}");
                Console.WriteLine($"PERFCOUNTER {scenario} desktop-snapshot-reads {fixture.DesktopManager.DesktopSnapshotReads}");
                Console.WriteLine($"PERFCOUNTER {scenario} display-snapshot-reads {fixture.DisplayManager.SnapshotReads}");
            }
        }

#if !DEBUG
        [DataTestMethod]
        [DataRow(0, 0L)]
        [DataRow(1, 32L)]
        [DataRow(10, 104L)]
        [DataRow(50, 424L)]
        public void DiscoveryWindowSnapshotAvoidsListWrapperAllocation(
            int windowCount,
            long maximumBytesPerPass)
        {
            const int warmups = 100;
            const int passes = 1000;
            using var fixture = new ManageabilityAllocationFixture(
                covariantProviderShape: true,
                discoveryWindowCount: windowCount);
            for (int iteration = 0; iteration < warmups; iteration++)
            {
                Assert.IsFalse(fixture.Service.DiscoverWindows());
            }
            fixture.ResetCounters();

            long before = GC.GetAllocatedBytesForCurrentThread();
            int changedPasses = 0;
            for (int iteration = 0; iteration < passes; iteration++)
            {
                if (fixture.Service.DiscoverWindows())
                {
                    changedPasses++;
                }
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0, changedPasses);
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
                $"DiscoverWindows allocated {allocated} bytes for {passes} passes "
                    + $"with {windowCount} tracked windows "
                    + $"({allocated / (double)passes:F3} B/pass), above the "
                    + $"{maximumBytesPerPass} B/pass budget.");
        }
#endif

        private static Func<IWindow, bool, bool> BindCanManage(TilingService service)
            => typeof(TilingService)
                .GetMethod("CanManage", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Func<IWindow, bool, bool>>(service);

        private static Mock<IWindow> ManageabilityWindow(
            Rectangle position,
            out Func<int> positionReads)
        {
            int reads = 0;
            var window = new Mock<IWindow>(MockBehavior.Loose);
            window.SetupGet(item => item.IsTopmost).Returns(false);
            window.SetupGet(item => item.Position).Returns(() =>
            {
                reads++;
                return position;
            });
            window.SetupGet(item => item.CanResize).Returns(true);
            window.SetupGet(item => item.CanMove).Returns(true);
            positionReads = () => reads;
            return window;
        }

        private sealed class ManageabilityDisplay : IDisplay
        {
            private readonly string m_name;
            private readonly List<string>? m_trace;
            private Rectangle m_bounds;

            public ManageabilityDisplay(
                string name,
                Rectangle bounds,
                List<string>? trace = null)
            {
                m_name = name;
                m_bounds = bounds;
                m_trace = trace;
            }

            public Func<IDisplay, bool>? Equality { get; set; }
            public Func<Rectangle>? BoundsRead { get; set; }
            public IWorkspace Workspace { get; set; } = null!;
            public Rectangle WorkArea => m_bounds;
            public Rectangle Bounds
            {
                get
                {
                    BoundsReads++;
                    m_trace?.Add($"{m_name}.Bounds");
                    return BoundsRead?.Invoke() ?? m_bounds;
                }
                set => m_bounds = value;
            }
            public double Scaling => 1;
            public int RefreshRate => 60;
            public int EqualityReads { get; private set; }
            public int BoundsReads { get; private set; }
            public event EventHandler<DisplayChangedEventArgs>? Removed { add { } remove { } }
            public event EventHandler<DisplayRectangleChangedEventArgs>? WorkAreaChanged { add { } remove { } }
            public event EventHandler<DisplayRectangleChangedEventArgs>? BoundsChanged { add { } remove { } }
            public event EventHandler<DisplayScalingChangedEventArgs>? ScalingChanged { add { } remove { } }
            public event EventHandler<DisplayRefreshRateChangedEventArgs>? RefreshRateChanged { add { } remove { } }
            public bool Equals(IDisplay? other)
            {
                EqualityReads++;
                m_trace?.Add($"{m_name}.Equals");
                return Equality?.Invoke(other!) ?? ReferenceEquals(this, other);
            }
            public override bool Equals(object? obj) =>
                obj is IDisplay display && Equals(display);
            public override int GetHashCode() =>
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
            public void ResetCounters()
            {
                EqualityReads = 0;
                BoundsReads = 0;
            }
        }

        private sealed class TrackingDisplaySnapshot : IReadOnlyList<IDisplay>
        {
            private readonly IDisplay[] m_items;

            public TrackingDisplaySnapshot(params IDisplay[] items)
            {
                m_items = items;
            }

            public int EnumeratorCreations { get; private set; }
            public int Yields { get; private set; }
            public int Disposals { get; private set; }
            public int IndexReads { get; private set; }
            public Exception? DisposeFailure { get; set; }
            public int Count => m_items.Length;
            public IDisplay this[int index]
            {
                get
                {
                    IndexReads++;
                    throw new InvalidOperationException("The enumerable snapshot indexer must not be used.");
                }
            }
            public IEnumerator<IDisplay> GetEnumerator()
            {
                EnumeratorCreations++;
                return Enumerate().GetEnumerator();
            }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            private IEnumerable<IDisplay> Enumerate()
            {
                try
                {
                    foreach (var item in m_items)
                    {
                        Yields++;
                        yield return item;
                    }
                }
                finally
                {
                    Disposals++;
                    if (DisposeFailure != null)
                    {
                        throw DisposeFailure;
                    }
                }
            }
        }

        private sealed class DerivedExactDisplayList :
            List<IDisplay>,
            IEnumerable<IDisplay>
        {
            public int EnumeratorReads { get; private set; }
            IEnumerator<IDisplay> IEnumerable<IDisplay>.GetEnumerator()
            {
                EnumeratorReads++;
                throw new InvalidOperationException(
                    "The List<IDisplay> LINQ specialization must not call this enumerator.");
            }
            IEnumerator IEnumerable.GetEnumerator() =>
                ((IEnumerable<IDisplay>)this).GetEnumerator();
        }

        private sealed class ManageabilityAllocationFixture : IDisposable
        {
            private readonly BehaviorSubject<ITilingServiceSettings> m_settings;
            private readonly AlgorithmicLayoutCoordinator m_coordinator;
            private readonly TilingService m_service;

            public ManageabilityAllocationWorkspace Workspace { get; }
            public ManageabilityAllocationDisplayManager DisplayManager =>
                Workspace.DisplayManagerAdapter;
            public ManageabilityAllocationDesktopManager DesktopManager =>
                Workspace.DesktopManagerAdapter;
            public IReadOnlyList<ManageabilityDisplay> ForeignDisplays { get; }
            public ManageabilityDisplay ForeignDisplay => ForeignDisplays[0];
            public ManageabilityAllocationWindow Window { get; }
            public ManageabilityAllocationWindow ResidentWindow { get; }
            public IReadOnlyList<ManageabilityAllocationWindow> DiscoveryWindows { get; }
            public TilingService Service => m_service;
            public Func<IWindow, bool, bool> CanManage { get; }

            public ManageabilityAllocationFixture(
                bool covariantProviderShape,
                int displayCount = 1,
                bool lastContainsResident = false,
                int? discoveryWindowCount = null,
                ILogger? logger = null)
            {
                if (displayCount <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(displayCount));
                }
                if (discoveryWindowCount < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(discoveryWindowCount));
                }
                Workspace = new ManageabilityAllocationWorkspace();
                var foreignDisplays = new List<ManageabilityDisplay>(displayCount);
                for (int index = 0; index < displayCount; index++)
                {
                    bool containsResident = lastContainsResident
                        && index == displayCount - 1;
                    foreignDisplays.Add(new ManageabilityDisplay(
                        $"foreign-{index}",
                        containsResident
                            ? Rectangle.OffsetAndSize(4900, 0, 1000, 1000)
                            : Rectangle.OffsetAndSize(10000 + index * 2000, 0, 1000, 1000))
                    {
                        Workspace = Workspace
                    });
                }
                ForeignDisplays = foreignDisplays;
                Workspace.DisplayManagerAdapter.Snapshot = covariantProviderShape
                    ? foreignDisplays
                    : foreignDisplays.ToArray();
                Window = new ManageabilityAllocationWindow(
                    Workspace,
                    Rectangle.OffsetAndSize(
                        lastContainsResident ? 8000 : 5000,
                        100,
                        200,
                        200));
                ResidentWindow = new ManageabilityAllocationWindow(
                    Workspace,
                    Rectangle.OffsetAndSize(5000, 100, 200, 200));
                var discoveryWindows = new ManageabilityAllocationWindow[
                    discoveryWindowCount ?? 0];
                for (int index = 0; index < discoveryWindows.Length; index++)
                {
                    discoveryWindows[index] = new ManageabilityAllocationWindow(
                        Workspace,
                        Rectangle.OffsetAndSize(100, 100, 200, 200),
                        canResize: false,
                        handle: 0x300 + index);
                }
                DiscoveryWindows = discoveryWindows;
                if (discoveryWindowCount.HasValue)
                {
                    Workspace.Snapshot = discoveryWindows;
                    Workspace.DisplayManagerAdapter.Snapshot = new IDisplay[]
                    {
                        Workspace.DisplayManagerAdapter.Primary
                    };
                }
                m_settings = new BehaviorSubject<ITilingServiceSettings>(
                    EnabledSettings(false));
                m_coordinator = new AlgorithmicLayoutCoordinator(
                    Workspace,
                    System.Windows.Threading.Dispatcher.CurrentDispatcher);
                m_service = new TilingService(
                    Workspace,
                    Workspace.DisplayManagerAdapter.Primary,
                    new FakeAnimationThread(),
                    m_settings,
                    m_coordinator,
                    autoRegisterWindows: true,
                    logger ?? new Mock<ILogger>(MockBehavior.Loose).Object,
                    (_, _) => new FakeTilingOverlayRenderer());
                CanManage = BindCanManage(m_service);
            }

            public void ResetCounters()
            {
                DisplayManager.SnapshotReads = 0;
                DesktopManager.PinReads = 0;
                foreach (var display in ForeignDisplays)
                {
                    display.ResetCounters();
                }
                Window.ResetCounters();
                ResidentWindow.ResetCounters();
                foreach (var window in DiscoveryWindows)
                {
                    window.ResetCounters();
                }
                DesktopManager.DesktopSnapshotReads = 0;
                DisplayManager.Primary.ResetCounters();
            }

            public void Dispose()
            {
                m_service.Dispose();
                m_coordinator.Dispose();
                m_settings.Dispose();
            }
        }

        private sealed class ManageabilityAllocationWorkspace : IWorkspace
        {
            public ManageabilityAllocationWorkspace()
            {
                DisplayManagerAdapter = new ManageabilityAllocationDisplayManager(this);
                DesktopManagerAdapter = new ManageabilityAllocationDesktopManager(this);
            }

            public ManageabilityAllocationDisplayManager DisplayManagerAdapter { get; }
            public ManageabilityAllocationDesktopManager DesktopManagerAdapter { get; }
            public IReadOnlyList<IWindow> Snapshot { get; set; } = Array.Empty<IWindow>();
            public bool IsOpen => true;
            public Point CursorLocation => new(0, 0);
            public IWindow? FocusedWindow => null;
            public IDisplayManager DisplayManager => DisplayManagerAdapter;
            public IVirtualDesktopManager VirtualDesktopManager => DesktopManagerAdapter;
            public TimeSpan WatchInterval { get; set; }
            public event EventHandler<CursorLocationChangedEventArgs>? CursorLocationChanged { add { } remove { } }
            public event EventHandler<FocusedWindowChangedEventArgs>? FocusedWindowChanged { add { } remove { } }
            public event EventHandler<WindowChangedEventArgs>? WindowManaging { add { } remove { } }
            public event EventHandler<WindowChangedEventArgs>? WindowAdded { add { } remove { } }
            public event EventHandler<WindowChangedEventArgs>? WindowRemoved { add { } remove { } }
            public event UnhandledExceptionEventHandler? UnhandledException { add { } remove { } }
            public void Open() { }
            public IWindow? FindWindow(IntPtr windowHandle) => null;
            public IWindow? FindWindowFromPoint(Point pt) => null;
            public IWindow UnsafeCreateFromHandle(IntPtr windowHandle) =>
                throw UnsupportedManageabilityOperation();
            public IReadOnlyList<IWindow> GetSnapshot() => Snapshot;
            public IComparer<IWindow> CreateSnapshotZOrderComparer() =>
                throw UnsupportedManageabilityOperation();
            public void RefreshConfiguration() { }
            public void Dispose() { }
        }

        private sealed class ManageabilityAllocationDisplayManager : IDisplayManager
        {
            public ManageabilityAllocationDisplayManager(IWorkspace workspace)
            {
                Workspace = workspace;
                Primary = new ManageabilityDisplay(
                    "current",
                    Rectangle.OffsetAndSize(0, 0, 3440, 1400))
                {
                    Workspace = workspace
                };
                Snapshot = new IDisplay[] { Primary };
            }

            public ManageabilityDisplay Primary { get; }
            public IReadOnlyList<IDisplay> Snapshot { get; set; }
            public int SnapshotReads;
            public IWorkspace Workspace { get; }
            public Rectangle VirtualDisplayBounds => Primary.Bounds;
            public IDisplay PrimaryDisplay => Primary;
            public IReadOnlyList<IDisplay> Displays
            {
                get
                {
                    SnapshotReads++;
                    return Snapshot;
                }
            }
            public event EventHandler<DisplayChangedEventArgs>? Added { add { } remove { } }
            public event EventHandler<DisplayChangedEventArgs>? Removed { add { } remove { } }
            public event EventHandler<DisplayRectangleChangedEventArgs>? VirtualDisplayBoundsChanged { add { } remove { } }
            public event EventHandler<PrimaryDisplayChangedEventArgs>? PrimaryDisplayChanged { add { } remove { } }
        }

        private sealed class ManageabilityAllocationDesktopManager : IVirtualDesktopManager
        {
            private readonly ManageabilityAllocationDesktop m_desktop;
            private readonly IReadOnlyList<IVirtualDesktop> m_desktops;

            public ManageabilityAllocationDesktopManager(IWorkspace workspace)
            {
                Workspace = workspace;
                m_desktop = new ManageabilityAllocationDesktop(workspace);
                m_desktops = new[] { m_desktop };
            }

            public int PinReads;
            public int DesktopSnapshotReads;
            public IWorkspace Workspace { get; }
            public bool CanManageVirtualDesktops => true;
            public IReadOnlyList<IVirtualDesktop> Desktops
            {
                get
                {
                    DesktopSnapshotReads++;
                    return m_desktops;
                }
            }
            public IVirtualDesktop CurrentDesktop => m_desktop;
            public event EventHandler<DesktopChangedEventArgs>? DesktopAdded { add { } remove { } }
            public event EventHandler<DesktopChangedEventArgs>? DesktopRemoved { add { } remove { } }
            public event EventHandler<CurrentDesktopChangedEventArgs>? CurrentDesktopChanged { add { } remove { } }
            public IVirtualDesktop CreateDesktop() => throw UnsupportedManageabilityOperation();
            public void PinWindow(IWindow window) => throw UnsupportedManageabilityOperation();
            public void UnpinWindow(IWindow window) => throw UnsupportedManageabilityOperation();
            public bool IsWindowPinned(IWindow window)
            {
                PinReads++;
                return false;
            }
        }

        private sealed class ManageabilityAllocationDesktop : IVirtualDesktop
        {
            public ManageabilityAllocationDesktop(IWorkspace workspace)
            {
                Workspace = workspace;
            }

            public IWorkspace Workspace { get; }
            public bool IsAlive => true;
            public bool IsCurrent => true;
            public int Index => 0;
            public string Name => "Manageability desktop";
            public event EventHandler<DesktopChangedEventArgs>? Removed { add { } remove { } }
            public void MoveWindow(IWindow window) => throw UnsupportedManageabilityOperation();
            public bool HasWindow(IWindow window) => false;
            public void SwitchTo() => throw UnsupportedManageabilityOperation();
            public void SetName(string newName) => throw UnsupportedManageabilityOperation();
            public void Remove() => throw UnsupportedManageabilityOperation();
        }

        private sealed class ManageabilityAllocationWindow : IWindow
        {
            private readonly Rectangle m_position;
            private readonly bool m_canResize;
            private readonly int m_handle;

            public ManageabilityAllocationWindow(
                IWorkspace workspace,
                Rectangle position,
                bool canResize = true,
                int handle = 0x221)
            {
                Workspace = workspace;
                m_position = position;
                m_canResize = canResize;
                m_handle = handle;
            }

            public int StateReads;
            public int PositionReads;
            public int ResizeReads;
            public int MoveReads;
            public object SyncRoot { get; } = new();
            public IWorkspace Workspace { get; }
            public string Title => "Manageability allocation window";
            public Rectangle Position
            {
                get
                {
                    PositionReads++;
                    return m_position;
                }
            }
            public WindowState State
            {
                get
                {
                    StateReads++;
                    return WindowState.Restored;
                }
            }
            public Point? MinSize => new Point(0, 0);
            public Point? MaxSize => null;
            public Rectangle FrameMargins => new();
            public bool CanResize
            {
                get
                {
                    ResizeReads++;
                    return m_canResize;
                }
            }
            public bool CanMove
            {
                get
                {
                    MoveReads++;
                    return true;
                }
            }
            public bool CanReorder => true;
            public bool CanMinimize => true;
            public bool CanMaximize => true;
            public bool CanClose => true;
            public bool IsTopmost => false;
            public bool IsFocused => false;
            public bool IsAlive => true;
            public IntPtr Handle => new(m_handle);
            public event EventHandler<WindowPositionChangedEventArgs>? PositionChangeStart { add { } remove { } }
            public event EventHandler<WindowPositionChangedEventArgs>? PositionChangeEnd { add { } remove { } }
            public event EventHandler<WindowPositionChangedEventArgs>? PositionChanged { add { } remove { } }
            public event EventHandler<WindowStateChangedEventArgs>? StateChanged { add { } remove { } }
            public event EventHandler<WindowTopmostChangedEventArgs>? TopmostChanged { add { } remove { } }
            public event EventHandler<WindowFocusChangedEventArgs>? GotFocus { add { } remove { } }
            public event EventHandler<WindowFocusChangedEventArgs>? LostFocus { add { } remove { } }
            public event EventHandler<WindowChangedEventArgs>? Added { add { } remove { } }
            public event EventHandler<WindowChangedEventArgs>? Removed { add { } remove { } }
            public event EventHandler<WindowChangedEventArgs>? Destroyed { add { } remove { } }
            public event EventHandler<WindowTitleChangedEventArgs>? TitleChanged { add { } remove { } }
            public bool Equals(IWindow? other) => ReferenceEquals(this, other);
            public override bool Equals(object? obj) => ReferenceEquals(this, obj);
            public override int GetHashCode() => m_handle;
            public Process GetProcess() => throw UnsupportedManageabilityOperation();
            public IWindow? GetPreviousWindow() => throw UnsupportedManageabilityOperation();
            public IWindow? GetNextWindow() => throw UnsupportedManageabilityOperation();
            public void Close() => throw UnsupportedManageabilityOperation();
            public void SetPosition(Rectangle newLocation) => throw UnsupportedManageabilityOperation();
            public void SetState(WindowState state) => throw UnsupportedManageabilityOperation();
            public void SetTopmost(bool topmost) => throw UnsupportedManageabilityOperation();
            public void InsertAfter(IWindow other) => throw UnsupportedManageabilityOperation();
            public void SendToBack() => throw UnsupportedManageabilityOperation();
            public void BringToFront() => throw UnsupportedManageabilityOperation();
            public bool RequestFocus() => throw UnsupportedManageabilityOperation();
            public void ResetCounters()
            {
                StateReads = 0;
                PositionReads = 0;
                ResizeReads = 0;
                MoveReads = 0;
            }
        }

        private static NotSupportedException UnsupportedManageabilityOperation()
            => new("The manageability fixture must not perform native or desktop mutations.");
    }
}
