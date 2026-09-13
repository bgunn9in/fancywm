#nullable enable

using System;
using System.Collections.Generic;

using FancyWM.Layouts.Tiling;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    public partial class TilingServiceAlgorithmicIntegrationTest
    {
        [TestMethod]
        public void CanResizePreservesRepeatedProviderReadOrder()
        {
            using var fixture = new ServiceFixture(EnabledSettings(false));
            fixture.Service.Start();
            fixture.DrainDispatcher();
            var window = fixture.AddWindow("Resize read order");
            fixture.DrainDispatcher();
            GetBackend(fixture).SetFocus(window);

            var calls = new List<string>();
            var displays = ConfigureResizeReadProbe(
                fixture,
                window,
                displayCount: 3,
                calls,
                out var counters);

            _ = fixture.Service.CanResize(PanelOrientation.Horizontal, 0.1);

            CollectionAssert.AreEqual(
                new[]
                {
                    "position:1",
                    "snapshot:1",
                    "workarea:0:1",
                    "position:2",
                    "workarea:1:1",
                    "position:3",
                    "workarea:2:1",
                    "position:4",
                    "workarea:2:2",
                    "workarea:2:3",
                },
                calls);
            Assert.AreEqual(4, counters.PositionReads);
            Assert.AreEqual(1, counters.SnapshotReads);
            Assert.AreEqual(5, counters.WorkAreaReads);
            Assert.AreSame(displays, counters.PublishedSnapshot);
        }

        [TestMethod]
        public void CanResizePreservesMutationDuringRepeatedPositionRead()
        {
            using var fixture = new ServiceFixture(EnabledSettings(false));
            fixture.Service.Start();
            fixture.DrainDispatcher();
            var window = fixture.AddWindow("Resize mutation boundary");
            fixture.DrainDispatcher();
            GetBackend(fixture).SetFocus(window);

            var displays = ConfigureResizeReadProbe(
                fixture,
                window,
                displayCount: 2,
                calls: null,
                out var counters);
            var appended = CreateDisplay(
                fixture.Service.Workspace,
                Rectangle.OffsetAndSize(8000, 0, 1000, 1000));
            counters.OnPositionRead = read =>
            {
                if (read == 2)
                {
                    displays.Add(appended);
                }
            };

            Assert.IsTrue(
                fixture.Service.CanResize(PanelOrientation.Horizontal, 0.1));
            Assert.AreEqual(3, counters.PositionReads);
            Assert.AreEqual(1, counters.SnapshotReads);
            Assert.AreEqual(4, counters.WorkAreaReads);
            Assert.AreEqual(3, displays.Count);
        }

        [TestMethod]
        public void ResizePreservesRepeatedProviderReadOrder()
        {
            using var fixture = new ServiceFixture(EnabledSettings(false));
            fixture.Service.Start();
            fixture.DrainDispatcher();
            var window = fixture.AddWindow("Resize command read order");
            fixture.DrainDispatcher();
            GetBackend(fixture).SetFocus(window);

            var calls = new List<string>();
            _ = ConfigureResizeReadProbe(
                fixture,
                window,
                displayCount: 3,
                calls,
                out var counters);

            fixture.Service.Resize(PanelOrientation.Horizontal, 0.1);

            CollectionAssert.AreEqual(
                new[]
                {
                    "position:1",
                    "snapshot:1",
                    "workarea:0:1",
                    "position:2",
                    "workarea:1:1",
                    "position:3",
                    "workarea:2:1",
                    "position:4",
                    "workarea:2:2",
                    "workarea:2:3",
                },
                calls);
            Assert.AreEqual(4, counters.PositionReads);
            Assert.AreEqual(1, counters.SnapshotReads);
            Assert.AreEqual(5, counters.WorkAreaReads);
        }

        [TestMethod]
        public void ResizeOperationSnapshotBoundaryCounterScenario()
        {
            foreach (int displayCount in new[] { 1, 10, 50 })
            {
                using var fixture = new ServiceFixture(EnabledSettings(false));
                fixture.Service.Start();
                fixture.DrainDispatcher();
                var window = fixture.AddWindow($"Resize counter {displayCount}");
                fixture.DrainDispatcher();
                GetBackend(fixture).SetFocus(window);
                _ = ConfigureResizeReadProbe(
                    fixture,
                    window,
                    displayCount,
                    calls: null,
                    out var counters);

                int trueResults = 0;
                const int cycles = 100;
                for (int cycle = 0; cycle < cycles; cycle++)
                {
                    counters.BeginCycle();
                    if (fixture.Service.CanResize(
                            PanelOrientation.Horizontal,
                            0.1))
                    {
                        trueResults++;
                    }
                }

                int expectedPositionReads = cycles * (displayCount + 1);
                int expectedWorkAreaReads = cycles * (displayCount + 2);
                Assert.AreEqual(expectedPositionReads, counters.PositionReads);
                Assert.AreEqual(cycles, counters.SnapshotReads);
                Assert.AreEqual(expectedWorkAreaReads, counters.WorkAreaReads);
                Assert.AreEqual(cycles, counters.CompletedCycles);

                string scenario = $"resize-operation-snapshot-{displayCount}";
                Console.WriteLine($"PERFCOUNTER {scenario} cycles {cycles}");
                Console.WriteLine($"PERFCOUNTER {scenario} display-count {displayCount}");
                Console.WriteLine($"PERFCOUNTER {scenario} position-reads {counters.PositionReads}");
                Console.WriteLine($"PERFCOUNTER {scenario} snapshot-reads {counters.SnapshotReads}");
                Console.WriteLine($"PERFCOUNTER {scenario} workarea-reads {counters.WorkAreaReads}");
                Console.WriteLine($"PERFCOUNTER {scenario} true-results {trueResults}");
                Console.WriteLine($"PERFCOUNTER {scenario} false-results {cycles - trueResults}");
            }
        }

        private static List<IDisplay> ConfigureResizeReadProbe(
            ServiceFixture fixture,
            IWindow window,
            int displayCount,
            List<string>? calls,
            out ResizeReadCounters counters)
        {
            if (displayCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(displayCount));
            }

            counters = new ResizeReadCounters(calls, displayCount);
            var capturedCounters = counters;
            var displays = new List<IDisplay>(displayCount);
            for (int index = 0; index < displayCount; index++)
            {
                int capturedIndex = index;
                var workArea = index == displayCount - 1
                    ? Rectangle.OffsetAndSize(5000, 0, 2000, 1200)
                    : Rectangle.OffsetAndSize(10000 + index * 1200, 0, 1000, 1000);
                displays.Add(CreateDisplay(
                    fixture.Service.Workspace,
                    workArea,
                    () => capturedCounters.RecordWorkArea(capturedIndex)));
            }
            capturedCounters.PublishedSnapshot = displays;

            var displayManager = Mock.Get(fixture.Service.Workspace.DisplayManager);
            displayManager.SetupGet(value => value.Displays).Returns(() =>
            {
                capturedCounters.RecordSnapshot();
                return displays;
            });

            var oldPosition = Rectangle.OffsetAndSize(100, 100, 600, 400);
            var lastPosition = Rectangle.OffsetAndSize(5200, 100, 600, 400);
            Mock.Get(window).SetupGet(value => value.Position).Returns(() =>
            {
                int read = capturedCounters.RecordPosition();
                return read == 1 ? oldPosition : lastPosition;
            });
            return displays;
        }

        private static IDisplay CreateDisplay(
            IWorkspace workspace,
            Rectangle workArea,
            Action? onWorkAreaRead = null)
        {
            var display = new Mock<IDisplay>(MockBehavior.Loose);
            display.SetupGet(value => value.Workspace).Returns(workspace);
            display.SetupGet(value => value.WorkArea).Returns(() =>
            {
                onWorkAreaRead?.Invoke();
                return workArea;
            });
            display.SetupGet(value => value.Bounds).Returns(workArea);
            display.SetupGet(value => value.Scaling).Returns(1.0);
            display.SetupGet(value => value.RefreshRate).Returns(60);
            display.Setup(value => value.Equals(It.IsAny<IDisplay>()))
                .Returns((IDisplay other) => ReferenceEquals(display.Object, other));
            return display.Object;
        }

        private sealed class ResizeReadCounters
        {
            private readonly List<string>? m_calls;
            private readonly int m_displayCount;
            private int m_cyclePositionReads;

            public ResizeReadCounters(List<string>? calls, int displayCount)
            {
                m_calls = calls;
                m_displayCount = displayCount;
            }

            public int PositionReads { get; private set; }
            public int SnapshotReads { get; private set; }
            public int WorkAreaReads { get; private set; }
            public int CompletedCycles { get; private set; }
            public List<IDisplay>? PublishedSnapshot { get; set; }
            public Action<int>? OnPositionRead { get; set; }

            public void BeginCycle()
            {
                m_cyclePositionReads = 0;
            }

            public int RecordPosition()
            {
                PositionReads++;
                m_cyclePositionReads++;
                m_calls?.Add($"position:{PositionReads}");
                OnPositionRead?.Invoke(m_cyclePositionReads);
                if (m_cyclePositionReads == m_displayCount + 1)
                {
                    CompletedCycles++;
                }
                return m_cyclePositionReads;
            }

            public void RecordSnapshot()
            {
                SnapshotReads++;
                m_calls?.Add($"snapshot:{SnapshotReads}");
            }

            public void RecordWorkArea(int displayIndex)
            {
                WorkAreaReads++;
                int displayReads = 0;
                if (m_calls != null)
                {
                    string prefix = $"workarea:{displayIndex}:";
                    foreach (string call in m_calls)
                    {
                        if (call.StartsWith(prefix, StringComparison.Ordinal))
                        {
                            displayReads++;
                        }
                    }
                    m_calls.Add($"{prefix}{displayReads + 1}");
                }
            }
        }
    }
}
