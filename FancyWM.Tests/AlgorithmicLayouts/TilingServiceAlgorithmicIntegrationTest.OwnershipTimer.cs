#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    public partial class TilingServiceAlgorithmicIntegrationTest
    {
        [TestMethod]
        public void DrainingIncomingOwnershipWaitsForThePendingProbeAfterFakeFailuresAreConsumed()
        {
            using var fixture = new ServiceFixture(EnabledSettings(true, maxSatellites: 1), includeSecondDesktop: true);
            fixture.DrainDispatcher();
            var master = fixture.AddWindow("Master");
            var satellite = fixture.AddWindow("Satellite");
            fixture.SourceOwnershipProbeFailuresRemaining = 2;
            var overflow = fixture.AddWindow("Overflow");
            Assert.AreEqual(1, ProbeMap(fixture).Count);
            var probe = ProbeMap(fixture)[overflow.Handle]!;
            var timer = (DispatcherTimer)probe.GetType().GetProperty("Timer")!.GetValue(probe)!;
            timer.Stop(); // The fixture, rather than elapsed wall time, delivers the next tick.
            fixture.SourceOwnershipProbeFailuresRemaining = 0;

            fixture.DrainIncomingOwnershipProbes();

            Assert.AreEqual(0, ProbeMap(fixture).Count, "Consumed fake failures do not mean the real pending probe has completed.");
            Assert.IsTrue(fixture.Coordinator.TryGet(new(fixture.Desktop, fixture.Display), out var source));
            Assert.AreSame(master, source.Master);
            CollectionAssert.AreEqual(new[] { satellite }, source.Satellites.ToArray());
            Assert.IsTrue(fixture.Coordinator.TryGet(new(fixture.TargetDesktop, fixture.Display), out var target));
            Assert.AreSame(overflow, target.Master);
            Assert.AreSame(fixture.TargetDesktop, fixture.GetWindowDesktop(overflow));
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
        }

        [TestMethod]
        public void CancellingIncomingOwnershipStopsItsTimerImmediately()
        {
            using var fixture = new ServiceFixture(EnabledSettings(true));
            var priorTimers = DispatcherTimers().ToHashSet();
            try
            {
                var window = fixture.CreateWindow("pending");
                Assert.IsTrue(QueueProbe(fixture, window));
                var timer = NewTimer(priorTimers);
                var probe = ProbeMap(fixture)[window.Handle]!;
                var lateTick = (EventHandler)probe.GetType().GetProperty("Tick")!.GetValue(probe)!;
                Assert.IsTrue(timer.IsEnabled);
                InvokeProbeMethod(fixture, "CancelMasterSatelliteIncomingOwnershipProbe", window, window.Handle);
                Assert.IsFalse(timer.IsEnabled, "Cancellation must stop the owned timer before its deadline.");
                Assert.AreEqual(0, ProbeMap(fixture).Count);
                lateTick(timer, EventArgs.Empty);
                Assert.AreEqual(false, InvokeProbeMethod(fixture, "TryScheduleMasterSatelliteIncomingOwnershipProbe", probe, 5));
                Assert.AreEqual(0, DispatcherTimers().Except(priorTimers).Count(), "A late rearm must not revive cancelled work.");
            }
            finally { StopNewTimers(priorTimers); }
        }

        [TestMethod]
        public void DisposingIncomingOwnershipStopsAllTimersImmediately()
        {
            using var fixture = new ServiceFixture(EnabledSettings(true));
            var priorTimers = DispatcherTimers().ToHashSet();
            try
            {
                Assert.IsTrue(QueueProbe(fixture, fixture.CreateWindow("first")));
                Assert.IsTrue(QueueProbe(fixture, fixture.CreateWindow("second")));
                var timers = DispatcherTimers().Except(priorTimers).ToArray();
                Assert.AreEqual(2, timers.Length);
                fixture.Service.Dispose();
                fixture.Service.Dispose();
                Assert.IsTrue(timers.All(timer => !timer.IsEnabled));
                Assert.AreEqual(0, ProbeMap(fixture).Count);
            }
            finally { StopNewTimers(priorTimers); }
        }

        [TestMethod]
        public void DuplicateOwnershipAdmissionKeepsOneTimerAndReplacementStopsOldGeneration()
        {
            using var fixture = new ServiceFixture(EnabledSettings(true));
            var priorTimers = DispatcherTimers().ToHashSet();
            try
            {
                var old = fixture.CreateWindow("old");
                Assert.IsTrue(QueueProbe(fixture, old));
                var oldTimer = NewTimer(priorTimers);
                for (int index = 0; index < 100; index++) Assert.IsTrue(QueueProbe(fixture, old));
                Assert.AreSame(oldTimer, NewTimer(priorTimers));
                var replacement = fixture.CreateWindow("replacement");
                Mock.Get(replacement).SetupGet(window => window.Handle).Returns(old.Handle);
                Assert.IsTrue(QueueProbe(fixture, replacement));
                Assert.IsFalse(oldTimer.IsEnabled, "Replaced wrapper must lose its pending timer.");
                Assert.AreEqual(1, ProbeMap(fixture).Count);
                var currentTimer = NewTimer(priorTimers);
                Assert.AreNotSame(oldTimer, currentTimer);
                InvokeProbeMethod(fixture, "CancelMasterSatelliteIncomingOwnershipProbe", old, old.Handle);
                Assert.IsTrue(currentTimer.IsEnabled, "Stale cancellation cannot stop the new wrapper's retry.");
                InvokeProbeMethod(fixture, "CancelMasterSatelliteIncomingOwnershipProbe", replacement, replacement.Handle);
                Assert.IsFalse(currentTimer.IsEnabled);
            }
            finally { StopNewTimers(priorTimers); }
        }

        [TestMethod]
        public void IncomingOwnershipRearmRetainsTimerAndOriginalRetryInterval()
        {
            using var fixture = new ServiceFixture(EnabledSettings(true));
            var priorTimers = DispatcherTimers().ToHashSet();
            try
            {
                var window = fixture.CreateWindow("pending");
                Assert.IsTrue(QueueProbe(fixture, window));
                var timer = NewTimer(priorTimers);
                var probe = ProbeMap(fixture)[window.Handle]!;
                for (int remaining = 5; remaining > 0; remaining--)
                {
                    timer.Stop();
                    Assert.AreEqual(true, InvokeProbeMethod(fixture, "TryScheduleMasterSatelliteIncomingOwnershipProbe", probe, remaining));
                    var nextTimer = NewTimer(priorTimers);
                    Assert.AreSame(timer, nextTimer);
                    Assert.AreEqual(TimeSpan.FromMilliseconds(50), nextTimer.Interval);
                }
                InvokeProbeMethod(fixture, "CancelMasterSatelliteIncomingOwnershipProbe", window, window.Handle);
                Assert.IsFalse(timer.IsEnabled);
            }
            finally { StopNewTimers(priorTimers); }
        }

        [TestMethod]
        public void IncomingOwnershipTimerCounterScenario()
        {
            using var fixture = new ServiceFixture(EnabledSettings(true));
            var priorTimers = DispatcherTimers().ToHashSet();
            var created = new HashSet<DispatcherTimer>();
            try
            {
                for (int cycle = 0; cycle < 100; cycle++)
                {
                    var window = fixture.CreateWindow("pending");
                    Assert.IsTrue(QueueProbe(fixture, window));
                    var timer = NewTimer(priorTimers);
                    created.Add(timer);
                    var probe = ProbeMap(fixture)[window.Handle]!;
                    for (int remaining = 5; remaining > 0; remaining--)
                    {
                        timer.Stop();
                        Assert.AreEqual(true, InvokeProbeMethod(fixture, "TryScheduleMasterSatelliteIncomingOwnershipProbe", probe, remaining));
                        timer = NewTimer(priorTimers);
                        created.Add(timer);
                        Assert.AreEqual(TimeSpan.FromMilliseconds(50), timer.Interval);
                    }
                    InvokeProbeMethod(fixture, "CancelMasterSatelliteIncomingOwnershipProbe", window, window.Handle);
                    Assert.AreEqual(0, ProbeMap(fixture).Count);
                    // On the old binary the cancellation does not own the timer. Clean the
                    // fixture explicitly only after observing its state for comparison.
                    timer.Stop();
                }
                TestContext.WriteLine($"PERFCOUNTER ownership-timer timer-instances {created.Count}");
                TestContext.WriteLine("PERFCOUNTER ownership-timer cycles 100");
            }
            finally { StopNewTimers(priorTimers); }
        }

        [DataTestMethod]
        [DataRow(false, false, false)]
        [DataRow(true, false, false)]
        [DataRow(false, true, false)]
        [DataRow(true, true, false)]
        [DataRow(false, false, true)]
        [DataRow(true, false, true)]
        [DataRow(false, true, true)]
        [DataRow(true, true, true)]
        public void IncomingOwnershipTimerStartReentrancyCannotReviveCancelledOwner(bool rearm, bool dispose, bool nestedStart)
        {
            using var fixture = new ServiceFixture(EnabledSettings(true));
            var priorTimers = DispatcherTimers().ToHashSet();
            var window = fixture.CreateWindow("incoming Start callback");
            DispatcherTimer? capturedTimer = null;
            EventHandler? capturedTick = null;
            object? capturedProbe = null;
            bool reentered = false;
            void OnPosted(object? sender, DispatcherHookEventArgs args)
            {
                if (reentered || args.Operation.Priority != DispatcherPriority.Inactive || !ProbeMap(fixture).Contains(window.Handle)) { return; }
                reentered = true;
                capturedProbe = ProbeMap(fixture)[window.Handle]!;
                capturedTimer = (DispatcherTimer)capturedProbe.GetType().GetProperty("Timer")!.GetValue(capturedProbe)!;
                capturedTick = (EventHandler)capturedProbe.GetType().GetProperty("Tick")!.GetValue(capturedProbe)!;
                if (nestedStart)
                {
                    Assert.AreEqual(true, InvokeProbeMethod(fixture, "TryScheduleMasterSatelliteIncomingOwnershipProbe", capturedProbe, 5));
                }
                if (dispose) { fixture.Service.Dispose(); }
                else { InvokeProbeMethod(fixture, "CancelMasterSatelliteIncomingOwnershipProbe", window, window.Handle); }
            }
            try
            {
                if (rearm)
                {
                    Assert.IsTrue(QueueProbe(fixture, window));
                    var timer = NewTimer(priorTimers);
                    var probe = ProbeMap(fixture)[window.Handle]!;
                    timer.Stop();
                    Dispatcher.CurrentDispatcher.Hooks.OperationPosted += OnPosted;
                    InvokeProbeMethod(fixture, "TryScheduleMasterSatelliteIncomingOwnershipProbe", probe, 5);
                }
                else
                {
                    Dispatcher.CurrentDispatcher.Hooks.OperationPosted += OnPosted;
                    QueueProbe(fixture, window);
                }
                Assert.IsTrue(reentered, "The real incoming DispatcherTimer.Start boundary must execute.");
                Assert.IsFalse(capturedTimer!.IsEnabled);
                Assert.AreEqual(0, ProbeMap(fixture).Count);
                Assert.AreEqual(0, DispatcherTimers().Except(priorTimers).Count());
                capturedTick!(capturedTimer, EventArgs.Empty);
                Assert.AreEqual(false, InvokeProbeMethod(fixture, "TryScheduleMasterSatelliteIncomingOwnershipProbe", capturedProbe, 4));
                Assert.AreEqual(0, DispatcherTimers().Except(priorTimers).Count());
            }
            finally
            {
                Dispatcher.CurrentDispatcher.Hooks.OperationPosted -= OnPosted;
                if (capturedTimer != null) { capturedTimer.Start(); capturedTimer.Stop(); }
                StopNewTimers(priorTimers);
            }
        }

        private static bool QueueProbe(ServiceFixture fixture, IWindow window) =>
            (bool)InvokeProbeMethod(fixture, "TryQueueMasterSatelliteIncomingOwnershipProbe", window)!;

        private static object? InvokeProbeMethod(ServiceFixture fixture, string name, params object?[] arguments) =>
            typeof(TilingService).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(fixture.Service, arguments);

        private static IDictionary ProbeMap(ServiceFixture fixture) =>
            (IDictionary)typeof(TilingService).GetField("m_masterSatelliteIncomingOwnershipProbes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Service)!;

        private static DispatcherTimer[] DispatcherTimers() =>
            ((IEnumerable)typeof(Dispatcher).GetField("_timers", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Dispatcher.CurrentDispatcher)!).Cast<DispatcherTimer>().ToArray();

        private static DispatcherTimer NewTimer(HashSet<DispatcherTimer> prior) =>
            DispatcherTimers().Except(prior).Single();

        private static void StopNewTimers(HashSet<DispatcherTimer> prior)
        {
            foreach (var timer in DispatcherTimers().Except(prior)) timer.Stop();
        }
    }
}
