using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

using FancyWM.AlgorithmicLayouts;
using FancyWM.Models;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    [TestClass]
    [DoNotParallelize]
    public sealed class MasterSatelliteContainsWindowTest
    {
        private const int WarmupCalls = 1000;
        private const int MeasuredCalls = 100000;

        // Resolve reflection once, outside every allocation/timing interval. The
        // delegate also preserves the original exception rather than wrapping it.
        private static readonly Func<MasterSatelliteRuntimeState, IWindow, bool> s_containsWindow =
            typeof(MasterSatelliteLayoutEngine)
                .GetMethod("ContainsWindow", BindingFlags.NonPublic | BindingFlags.Static)!
                .CreateDelegate<Func<MasterSatelliteRuntimeState, IWindow, bool>>();
        private static readonly Func<MasterSatelliteRuntimeState, IWindow, bool> s_enumeratorOracle =
            ContainsWindowEnumeratorOracle;

        public TestContext TestContext { get; set; } = null!;

        [TestMethod]
        public void ContainsWindowChecksMasterFirstAndShortCircuitsWithResidentFirstEquality()
        {
            var order = new List<string>();
            var query = CreateQuery();
            var master = CreateResident(1, "master", query, order, matches: true);
            var satellite = CreateResident(2, "satellite", query, order, matches: true);
            var state = CreateState(master, satellite);

            Assert.IsTrue(s_containsWindow(state, query));

            CollectionAssert.AreEqual(new[] { "master" }, order);
            Assert.AreSame(query, master.LastCompared);
            Assert.AreEqual(0, satellite.EqualityCalls);
            AssertUnusedQueryAndHash(query, master, satellite);
        }

        [DataTestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(-1)]
        public void ContainsWindowChecksSatellitesInOrderAndStopsAtFirstMatch(int matchingIndex)
        {
            var order = new List<string>();
            var query = CreateQuery();
            var master = CreateResident(1, "master", query, order, matches: false);
            var satellites = new EqualityWindow[3];
            for (int index = 0; index < satellites.Length; index++)
            {
                satellites[index] = CreateResident(index + 2, $"satellite-{index}",
                    query, order, matches: index == matchingIndex);
            }
            var state = CreateState(master, satellites);

            Assert.AreEqual(matchingIndex >= 0, s_containsWindow(state, query));

            var expected = new List<string> { "master" };
            int visited = matchingIndex < 0 ? satellites.Length : matchingIndex + 1;
            for (int index = 0; index < visited; index++)
            {
                expected.Add($"satellite-{index}");
                Assert.AreSame(query, satellites[index].LastCompared);
            }
            CollectionAssert.AreEqual(expected, order);
            AssertUnusedQueryAndHash(query, master, satellites[0], satellites[1], satellites[2]);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void ContainsWindowHandlesMissingMasterAndEmptySatellites(bool hasSatellite)
        {
            var query = new EqualityWindow(7);
            var satellite = new EqualityWindow(7);
            var state = CreateState(null);
            if (hasSatellite)
            {
                state.MutableSatellites.Add(satellite);
            }

            Assert.AreEqual(hasSatellite, s_containsWindow(state, query));

            Assert.AreEqual(hasSatellite ? 1 : 0, satellite.EqualityCalls);
            AssertUnusedQueryAndHash(query, satellite);
        }

        [DataTestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(3)]
        public void ContainsWindowPropagatesExactEqualityExceptionWithoutLaterCallbacks(int throwingIndex)
        {
            var order = new List<string>();
            var query = CreateQuery();
            var windows = new EqualityWindow[4];
            for (int index = 0; index < windows.Length; index++)
            {
                windows[index] = CreateResident(index + 1, $"resident-{index}",
                    query, order, matches: false);
            }
            var expectedException = new EqualityProbeException();
            windows[throwingIndex].EqualsCallback = other =>
            {
                Assert.AreSame(query, other);
                order.Add($"resident-{throwingIndex}");
                throw expectedException;
            };
            var state = CreateState(windows[0], windows[1], windows[2], windows[3]);

            var actual = Assert.ThrowsException<EqualityProbeException>(
                () => s_containsWindow(state, query));

            Assert.AreSame(expectedException, actual);
            Assert.AreEqual(throwingIndex + 1, order.Count);
            for (int index = 0; index <= throwingIndex; index++)
            {
                Assert.AreEqual($"resident-{index}", order[index]);
            }
            AssertUnusedQueryAndHash(query, windows);
        }

        [TestMethod]
        public void ContainsWindowSeesMasterCallbackMutationBeforeSatelliteEnumeration()
        {
            var order = new List<string>();
            var query = CreateQuery();
            var master = CreateResident(1, "master", query, order, matches: false);
            var removed = CreateResident(2, "removed", query, order, matches: false);
            var replacement = CreateResident(3, "replacement", query, order, matches: true);
            var state = CreateState(master, removed);
            var view = state.Satellites;
            var list = state.MutableSatellites;
            master.EqualsCallback = other =>
            {
                Assert.AreSame(query, other);
                order.Add("master");
                list.Clear();
                list.Add(replacement);
                return false;
            };

            Assert.IsTrue(s_containsWindow(state, query));

            CollectionAssert.AreEqual(new[] { "master", "replacement" }, order);
            Assert.AreSame(view, state.Satellites);
            Assert.AreSame(list, state.MutableSatellites);
            Assert.AreSame(replacement, view[0]);
            Assert.AreEqual(0, removed.EqualityCalls);
            AssertUnusedQueryAndHash(query, master, removed, replacement);
        }

        [DataTestMethod]
        [DataRow("clear")]
        [DataRow("replace")]
        public void ContainsWindowMutationThenFalseThrowsOnNextMoveNext(string mutation)
        {
            AssertSatelliteMutation(mutation, matches: false);
        }

        [DataTestMethod]
        [DataRow("clear")]
        [DataRow("replace")]
        public void ContainsWindowMutationThenTrueShortCircuitsBeforeNextMoveNext(string mutation)
        {
            AssertSatelliteMutation(mutation, matches: true);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void ContainsWindowPreservesRuntimeStateAndReadOnlyViewIdentity(bool matches)
        {
            var master = new EqualityWindow(1);
            var first = new EqualityWindow(2);
            var last = new EqualityWindow(3);
            var query = new EqualityWindow(matches ? 3 : 4);
            var state = CreateState(master, first, last);
            state.MasterSide = MasterSide.Right;
            state.RequestedMasterRatio = 0.63;
            state.EffectiveMasterRatio = 0.59;
            state.SatelliteOrientation = SatelliteLayoutOrientation.Horizontal;
            state.Revision = 37;
            state.IsRecovering = true;
            var view = state.Satellites;
            var list = state.MutableSatellites;
            using var existingEnumerator = view.GetEnumerator();

            Assert.AreEqual(matches, s_containsWindow(state, query));

            Assert.AreSame(master, state.Master);
            Assert.AreSame(view, state.Satellites);
            Assert.AreSame(list, state.MutableSatellites);
            Assert.AreEqual(2, view.Count);
            Assert.AreSame(first, view[0]);
            Assert.AreSame(last, view[1]);
            Assert.IsTrue(state.IsActive);
            Assert.AreEqual(MasterSide.Right, state.MasterSide);
            Assert.AreEqual(0.63, state.RequestedMasterRatio);
            Assert.AreEqual(0.59, state.EffectiveMasterRatio);
            Assert.AreEqual(SatelliteLayoutOrientation.Horizontal, state.SatelliteOrientation);
            Assert.AreEqual(37L, state.Revision);
            Assert.IsTrue(state.IsRecovering);
            Assert.IsTrue(existingEnumerator.MoveNext());
            Assert.AreSame(first, existingEnumerator.Current);
            Assert.IsTrue(existingEnumerator.MoveNext());
            Assert.AreSame(last, existingEnumerator.Current);
            Assert.IsFalse(existingEnumerator.MoveNext());
            AssertUnusedQueryAndHash(query, master, first, last);
        }

        [TestMethod]
        public void ContainsWindowAvoidsCapturedPredicateAllocation()
        {
            var fixture = new CounterFixture("absent", 4);
            var measurement = Measure(fixture, s_containsWindow);
            AssertCounterSemantics(fixture, measurement);
            var oracle = Measure(fixture, s_enumeratorOracle);
            AssertCounterSemantics(fixture, oracle);

            // The oracle retains the ReadOnlyCollection enumerator. Compare its
            // measured cost rather than assuming a runtime-specific box size;
            // 8 B/call is below the minimum captured-closure object allocation.
            const long allowedDifferencePerCall = 8;
            Assert.IsTrue(measurement.AllocatedBytes <= oracle.AllocatedBytes
                    + allowedDifferencePerCall * MeasuredCalls,
                $"Captured predicate allocation remains: total={measurement.AllocatedBytes} B, "
                + $"per-call={(double)measurement.AllocatedBytes / MeasuredCalls:F3} B, "
                + $"oracle-total={oracle.AllocatedBytes} B, "
                + $"oracle-per-call={(double)oracle.AllocatedBytes / MeasuredCalls:F3} B, "
                + $"allowed-difference={allowedDifferencePerCall} B/call.");
        }

        [TestMethod]
        public void ContainsWindowCounterScenario()
        {
            foreach (int satelliteCount in new[] { 1, 4, 9 })
            {
                foreach (string mode in new[] { "master", "first", "last", "absent" })
                {
                    var fixture = new CounterFixture(mode, satelliteCount);
                    var measurement = Measure(fixture, s_containsWindow);
                    string scenario = $"contains-window-{mode}-{satelliteCount}";
                    WriteCounter(scenario, "allocated-bytes", measurement.AllocatedBytes);
                    WriteCounter(scenario, "elapsed-ticks", measurement.ElapsedTicks);
                    WriteCounter(scenario, "timestamp-frequency", Stopwatch.Frequency);
                    WriteCounter(scenario, "calls", MeasuredCalls);
                    WriteCounter(scenario, "matches", measurement.Matches);
                    WriteCounter(scenario, "equality-calls", measurement.EqualityCalls);
                    WriteCounter(scenario, "hash-calls", measurement.HashCalls);
                    AssertCounterSemantics(fixture, measurement);
                }
            }
        }

        private void WriteCounter(string scenario, string metric, long value)
        {
            TestContext.WriteLine($"PERFCOUNTER {scenario} {metric} "
                + value.ToString(CultureInfo.InvariantCulture));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ContainsWindowEnumeratorOracle(MasterSatelliteRuntimeState state, IWindow window)
        {
            if (EqualityComparer<IWindow?>.Default.Equals(state.Master, window))
            {
                return true;
            }
            foreach (var satellite in state.Satellites)
            {
                if (EqualityComparer<IWindow?>.Default.Equals(satellite, window))
                {
                    return true;
                }
            }
            return false;
        }

        private static Measurement Measure(CounterFixture fixture,
            Func<MasterSatelliteRuntimeState, IWindow, bool> containsWindow)
        {
            for (int index = 0; index < WarmupCalls; index++)
            {
                containsWindow(fixture.State, fixture.Query);
            }
            foreach (var window in fixture.Windows)
            {
                window.EqualityCalls = 0;
                window.HashCalls = 0;
            }
            int matches = 0;
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            for (int index = 0; index < MeasuredCalls; index++)
            {
                if (containsWindow(fixture.State, fixture.Query))
                {
                    matches++;
                }
            }
            long elapsed = Stopwatch.GetTimestamp() - started;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            long equalityCalls = 0;
            long hashCalls = 0;
            foreach (var window in fixture.Windows)
            {
                equalityCalls += window.EqualityCalls;
                hashCalls += window.HashCalls;
            }
            return new Measurement(allocated, elapsed, matches, equalityCalls, hashCalls);
        }

        private static void AssertCounterSemantics(CounterFixture fixture, Measurement measurement)
        {
            Assert.AreEqual(fixture.ExpectedMatches, measurement.Matches);
            Assert.AreEqual(fixture.ExpectedEqualityCalls, measurement.EqualityCalls);
            Assert.AreEqual(0L, measurement.HashCalls);
            Assert.AreEqual(0, fixture.Query.EqualityCalls,
                "Equality must be invoked on resident windows, never on the query.");
        }

        private static void AssertSatelliteMutation(string mutation, bool matches)
        {
            var order = new List<string>();
            var query = CreateQuery();
            var master = CreateResident(1, "master", query, order, matches: false);
            var first = CreateResident(2, "first", query, order, matches: false);
            var last = CreateResident(3, "last", query, order, matches: true);
            var replacement = CreateResident(4, "replacement", query, order, matches: true);
            var state = CreateState(master, first, last);
            var view = state.Satellites;
            var list = state.MutableSatellites;
            first.EqualsCallback = other =>
            {
                Assert.AreSame(query, other);
                order.Add("first");
                if (mutation == "clear")
                {
                    list.Clear();
                }
                else
                {
                    list[1] = replacement;
                }
                return matches;
            };

            if (matches)
            {
                Assert.IsTrue(s_containsWindow(state, query));
            }
            else
            {
                var exception = Assert.ThrowsException<InvalidOperationException>(
                    () => s_containsWindow(state, query));
                Assert.AreEqual(typeof(InvalidOperationException), exception.GetType());
            }

            CollectionAssert.AreEqual(new[] { "master", "first" }, order);
            Assert.AreSame(master, state.Master);
            Assert.AreSame(view, state.Satellites);
            Assert.AreSame(list, state.MutableSatellites);
            Assert.AreEqual(mutation == "clear" ? 0 : 2, view.Count);
            if (mutation == "replace")
            {
                Assert.AreSame(first, view[0]);
                Assert.AreSame(replacement, view[1]);
            }
            Assert.AreEqual(0, last.EqualityCalls);
            Assert.AreEqual(0, replacement.EqualityCalls);
            AssertUnusedQueryAndHash(query, master, first, last, replacement);
        }

        private static MasterSatelliteRuntimeState CreateState(IWindow? master, params IWindow[] satellites)
        {
            var state = new MasterSatelliteRuntimeState(new MasterSatelliteLayoutSettings
            {
                Enabled = true,
                MaxSatellites = MasterSatelliteLayoutSettings.MaximumMaxSatellites,
            }, isActive: true)
            {
                Master = master,
            };
            state.MutableSatellites.AddRange(satellites);
            return state;
        }

        private static EqualityWindow CreateQuery()
        {
            return new EqualityWindow(99)
            {
                EqualsCallback = _ => throw new InvalidOperationException(
                    "Equality direction reversed: the query must not receive Equals."),
            };
        }

        private static EqualityWindow CreateResident(int identity, string label,
            IWindow query, List<string> order, bool matches)
        {
            return new EqualityWindow(identity)
            {
                EqualsCallback = other =>
                {
                    Assert.AreSame(query, other);
                    order.Add(label);
                    return matches;
                },
            };
        }

        private static void AssertUnusedQueryAndHash(EqualityWindow query, params EqualityWindow[] residents)
        {
            Assert.AreEqual(0, query.EqualityCalls);
            Assert.AreEqual(0, query.HashCalls);
            foreach (var resident in residents)
            {
                Assert.AreEqual(0, resident.HashCalls);
            }
        }

        private readonly record struct Measurement(long AllocatedBytes, long ElapsedTicks,
            int Matches, long EqualityCalls, long HashCalls);

        private sealed class CounterFixture
        {
            public MasterSatelliteRuntimeState State { get; }
            public EqualityWindow Query { get; }
            public EqualityWindow[] Windows { get; }
            public int ExpectedMatches { get; }
            public long ExpectedEqualityCalls { get; }

            public CounterFixture(string mode, int satelliteCount)
            {
                Windows = new EqualityWindow[satelliteCount + 2];
                Windows[0] = new EqualityWindow(1);
                State = CreateState(Windows[0]);
                for (int index = 0; index < satelliteCount; index++)
                {
                    Windows[index + 1] = new EqualityWindow(index + 2);
                    State.MutableSatellites.Add(Windows[index + 1]);
                }
                int identity = mode switch
                {
                    "master" => 1,
                    "first" => 2,
                    "last" => satelliteCount + 1,
                    "absent" => satelliteCount + 2,
                    _ => throw new ArgumentOutOfRangeException(nameof(mode)),
                };
                Query = new EqualityWindow(identity);
                Windows[^1] = Query;
                ExpectedMatches = mode == "absent" ? 0 : MeasuredCalls;
                ExpectedEqualityCalls = (long)MeasuredCalls * (mode switch
                {
                    "master" => 1,
                    "first" => 2,
                    _ => satelliteCount + 1,
                });
            }
        }

        private sealed class EqualityProbeException : Exception { }

        // Allocation/counter cases use only field-backed equality and counters.
        // Callback behavior is enabled exclusively by the semantic regressions.
        private sealed class EqualityWindow(int identity) : IWindow
        {
            private readonly int m_identity = identity;
            public int EqualityCalls;
            public int HashCalls;
            public IWindow? LastCompared;
            public Func<IWindow?, bool>? EqualsCallback;

            public bool Equals(IWindow? other)
            {
                EqualityCalls++;
                LastCompared = other;
                return EqualsCallback != null
                    ? EqualsCallback(other)
                    : other is EqualityWindow window && m_identity == window.m_identity;
            }

            public override bool Equals(object? obj) => Equals(obj as IWindow);
            public override int GetHashCode()
            {
                HashCalls++;
                return m_identity;
            }

            public object SyncRoot { get; } = new();
            public IWorkspace Workspace => throw UnsupportedOperation();
            public string Title => "ContainsWindow equality probe";
            public Rectangle Position => default;
            public WindowState State => WindowState.Restored;
            public Point? MinSize => null;
            public Point? MaxSize => null;
            public Rectangle FrameMargins => default;
            public bool CanResize => true;
            public bool CanMove => true;
            public bool CanReorder => true;
            public bool CanMinimize => true;
            public bool CanMaximize => true;
            public bool CanClose => true;
            public bool IsTopmost => false;
            public bool IsFocused => false;
            public bool IsAlive => true;
            public IntPtr Handle => new(m_identity);
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
            public Process GetProcess() => throw UnsupportedOperation();
            public IWindow? GetPreviousWindow() => throw UnsupportedOperation();
            public IWindow? GetNextWindow() => throw UnsupportedOperation();
            public void Close() => throw UnsupportedOperation();
            public void SetPosition(Rectangle newLocation) => throw UnsupportedOperation();
            public void SetState(WindowState state) => throw UnsupportedOperation();
            public void SetTopmost(bool topmost) => throw UnsupportedOperation();
            public void InsertAfter(IWindow other) => throw UnsupportedOperation();
            public void SendToBack() => throw UnsupportedOperation();
            public void BringToFront() => throw UnsupportedOperation();
            public bool RequestFocus() => throw UnsupportedOperation();
            private static NotSupportedException UnsupportedOperation()
                => new("ContainsWindow must not call native/window mutation operations.");
        }
    }
}
