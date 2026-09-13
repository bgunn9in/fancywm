#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Threading;

using FancyWM.AlgorithmicLayouts;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    public partial class TilingServiceAlgorithmicIntegrationTest
    {
        [TestMethod]
        public void PostMoveRetryRetainsOneTimerAndSixProbeBudget()
        {
            using var fixture = CreatePostMoveTimerFixture();
            var priorTimers = DispatcherTimers().ToHashSet();
            try
            {
                var (window, transfer) = StartPostMoveTimer(fixture);
                int ownershipReads = 0;
                fixture.TargetDesktopMock.Setup(desktop => desktop.HasWindow(window)).Returns(() => { ownershipReads++; return false; });
                var first = PostMoveTimer(priorTimers);
                for (int probe = 0; probe < 6; probe++)
                {
                    var timer = PostMoveTimer(priorTimers);
                    Assert.AreSame(first, timer, "Every retry of this correlation must retain its owned timer.");
                    Assert.AreEqual(TimeSpan.FromMilliseconds(50), timer.Interval);
                    PostMoveTick(timer)(timer, EventArgs.Empty);
                }
                Assert.AreEqual(6, ownershipReads, "The six deferred ownership probes must all execute.");
                Assert.IsFalse(first.IsEnabled);
                Assert.AreEqual(0, PostMoveTimers(priorTimers).Length);
                Assert.IsTrue(fixture.Coordinator.TryGetTransfer(transfer.CorrelationId, out var failed));
                Assert.AreEqual(PendingWindowTransferState.Failed, failed.State);
                Assert.AreEqual("PostMoveOwnershipNotObserved", failed.TerminalReason);
                Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            }
            finally { StopNewTimers(priorTimers); }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void PostMoveTerminalNotificationStopsTimerBeforeRecovery(bool commit)
        {
            using var fixture = CreatePostMoveTimerFixture();
            var priorTimers = DispatcherTimers().ToHashSet();
            try
            {
                var (window, transfer) = StartPostMoveTimer(fixture);
                var timer = PostMoveTimer(priorTimers);
                var lateTick = PostMoveTick(timer);
                if (commit)
                {
                    Assert.IsTrue(fixture.Coordinator.ObserveDestination(transfer.CorrelationId, window.Handle));
                    Assert.IsTrue(fixture.Coordinator.Commit(transfer.CorrelationId, window.Handle));
                }
                else
                {
                    Assert.IsTrue(fixture.Coordinator.Cancel(transfer.CorrelationId, window.Handle, "Controlled cancellation"));
                }
                Assert.IsTrue(fixture.Coordinator.TryGetTransfer(transfer.CorrelationId, out var terminal));
                fixture.Service.AlgorithmicLayoutChanged += (_, _) => Assert.IsFalse(timer.IsEnabled,
                    "Terminal timer ownership must end before user-facing recovery callbacks.");
                InvokeProbeMethod(fixture, "OnAlgorithmicTransferTerminated", terminal);
                Assert.IsFalse(timer.IsEnabled);
                int ownershipReads = 0;
                fixture.TargetDesktopMock.Setup(desktop => desktop.HasWindow(window)).Returns(() => { ownershipReads++; return false; });
                lateTick(timer, EventArgs.Empty);
                Assert.AreEqual(0, ownershipReads);
                Assert.AreEqual(0, PostMoveTimers(priorTimers).Length);
            }
            finally { StopNewTimers(priorTimers); }
        }

        [TestMethod]
        public void PostMoveServiceDisposalStopsTimerAndSuppressesLateCallback()
        {
            using var fixture = CreatePostMoveTimerFixture();
            var priorTimers = DispatcherTimers().ToHashSet();
            try
            {
                var (window, _) = StartPostMoveTimer(fixture);
                var timer = PostMoveTimer(priorTimers);
                var lateTick = PostMoveTick(timer);
                fixture.Service.Dispose();
                fixture.Service.Dispose();
                Assert.IsFalse(timer.IsEnabled, "Disposed display service must release the pending timer without waiting for its deadline.");
                int ownershipReads = 0;
                fixture.TargetDesktopMock.Setup(desktop => desktop.HasWindow(window)).Returns(() => { ownershipReads++; return false; });
                lateTick(timer, EventArgs.Empty);
                Assert.AreEqual(0, ownershipReads);
                Assert.AreEqual(0, PostMoveTimers(priorTimers).Length);
            }
            finally { StopNewTimers(priorTimers); }
        }

        [TestMethod]
        public void PostMoveHwndReplacementStopsOldTimerWithoutCancellingNewProbe()
        {
            using var fixture = CreatePostMoveTimerFixture();
            var priorTimers = DispatcherTimers().ToHashSet();
            try
            {
                var (old, _) = StartPostMoveTimer(fixture);
                var oldTimer = PostMoveTimer(priorTimers);
                var lateTick = PostMoveTick(oldTimer);
                var replacement = fixture.CreateWindowWithHandle("replacement", old.Handle);
                Assert.IsTrue(QueueProbe(fixture, replacement));
                Assert.IsFalse(oldTimer.IsEnabled, "Retiring the wrapper must release its timer immediately.");
                var newTimer = PostMoveTimer(priorTimers);
                Assert.AreNotSame(oldTimer, newTimer);
                int ownershipReads = 0;
                fixture.TargetDesktopMock.Setup(desktop => desktop.HasWindow(old)).Returns(() => { ownershipReads++; return false; });
                lateTick(oldTimer, EventArgs.Empty);
                Assert.AreEqual(0, ownershipReads);
                Assert.IsTrue(newTimer.IsEnabled, "Old correlation cleanup cannot cancel the replacement wrapper's incoming ownership probe.");
                InvokeProbeMethod(fixture, "CancelMasterSatelliteIncomingOwnershipProbe", replacement, replacement.Handle);
            }
            finally { StopNewTimers(priorTimers); }
        }

        [TestMethod]
        public void PostMoveDeadWindowTickDoesNotProbeReusedHandle()
        {
            using var fixture = CreatePostMoveTimerFixture();
            var priorTimers = DispatcherTimers().ToHashSet();
            try
            {
                var (window, transfer) = StartPostMoveTimer(fixture);
                var timer = PostMoveTimer(priorTimers);
                fixture.SetWindowAlive(window, false);
                int ownershipReads = 0;
                fixture.TargetDesktopMock.Setup(desktop => desktop.HasWindow(window)).Returns(() => { ownershipReads++; return false; });
                PostMoveTick(timer)(timer, EventArgs.Empty);
                Assert.AreEqual(0, ownershipReads);
                Assert.IsFalse(timer.IsEnabled);
                Assert.IsTrue(fixture.Coordinator.TryGetTransfer(transfer.CorrelationId, out var terminal));
                Assert.AreEqual(PendingWindowTransferState.Cancelled, terminal.State);
                Assert.AreEqual("WindowClosed", terminal.TerminalReason);
            }
            finally { StopNewTimers(priorTimers); }
        }

        [TestMethod]
        public void PostMoveDeadlineUsesLatestTransferBeforeOwnershipRead()
        {
            using var fixture = CreatePostMoveTimerFixture();
            var priorTimers = DispatcherTimers().ToHashSet();
            try
            {
                var (window, transfer) = StartPostMoveTimer(fixture);
                var timer = PostMoveTimer(priorTimers);
                var retained = (IDictionary)typeof(AlgorithmicLayoutCoordinator)
                    .GetField("m_transfers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Coordinator)!;
                retained[transfer.CorrelationId] = transfer with { Deadline = DateTimeOffset.MinValue };
                int ownershipReads = 0;
                fixture.TargetDesktopMock.Setup(desktop => desktop.HasWindow(window)).Returns(() => { ownershipReads++; return false; });
                PostMoveTick(timer)(timer, EventArgs.Empty);
                Assert.AreEqual(0, ownershipReads, "The tick must inspect the latest deadline before native ownership.");
                Assert.IsFalse(timer.IsEnabled);
                Assert.IsTrue(fixture.Coordinator.TryGetTransfer(transfer.CorrelationId, out var terminal));
                Assert.IsTrue(terminal.IsTerminal);
            }
            finally { StopNewTimers(priorTimers); }
        }

        [TestMethod]
        public void PostMoveProbeExceptionStopsTimerAndKeepsFailureReason()
        {
            using var fixture = CreatePostMoveTimerFixture();
            var priorTimers = DispatcherTimers().ToHashSet();
            try
            {
                var (window, transfer) = StartPostMoveTimer(fixture);
                var timer = PostMoveTimer(priorTimers);
                Mock.Get(window).SetupGet(candidate => candidate.IsAlive).Throws(new InvalidOperationException("Controlled endpoint failure"));
                PostMoveTick(timer)(timer, EventArgs.Empty);
                Assert.IsFalse(timer.IsEnabled);
                Assert.IsTrue(fixture.Coordinator.TryGetTransfer(transfer.CorrelationId, out var terminal));
                Assert.AreEqual(PendingWindowTransferState.Failed, terminal.State);
                Assert.AreEqual("PostMoveReconciliationThrew", terminal.TerminalReason);
                Mock.Get(window).SetupGet(candidate => candidate.IsAlive).Returns(true);
            }
            finally { StopNewTimers(priorTimers); }
        }

        [DataTestMethod]
        [DataRow(false, "cancel")]
        [DataRow(false, "dispose")]
        [DataRow(false, "replace")]
        [DataRow(true, "cancel")]
        [DataRow(true, "dispose")]
        [DataRow(true, "replace")]
        public void PostMoveReentrantEndpointCannotReviveOldOwner(bool duringOwnershipRead, string action)
        {
            using var fixture = CreatePostMoveTimerFixture();
            var priorTimers = DispatcherTimers().ToHashSet();
            try
            {
                var (window, transfer) = StartPostMoveTimer(fixture);
                var timer = PostMoveTimer(priorTimers);
                var lateTick = PostMoveTick(timer);
                bool reentered = false;
                int ownershipReads = 0;
                IWindow? replacement = null;
                DispatcherTimer? replacementTimer = null;
                void Reenter()
                {
                    if (reentered) { return; }
                    reentered = true;
                    switch (action)
                    {
                        case "cancel":
                            Assert.IsTrue(fixture.Coordinator.Cancel(transfer.CorrelationId, window.Handle, "Reentrant cancellation"));
                            Assert.IsTrue(fixture.Coordinator.TryGetTransfer(transfer.CorrelationId, out var terminal));
                            InvokeProbeMethod(fixture, "OnAlgorithmicTransferTerminated", terminal);
                            break;
                        case "dispose":
                            fixture.Service.Dispose();
                            break;
                        case "replace":
                            replacement = fixture.CreateWindowWithHandle("replacement during endpoint read", window.Handle);
                            Assert.IsTrue(QueueProbe(fixture, replacement));
                            replacementTimer = PostMoveTimer(priorTimers);
                            break;
                        default:
                            Assert.Fail("Unknown controlled reentrant action");
                            break;
                    }
                }
                fixture.TargetDesktopMock.Setup(desktop => desktop.HasWindow(window)).Returns(() =>
                {
                    ownershipReads++;
                    if (duringOwnershipRead) { Reenter(); }
                    return false;
                });
                Mock.Get(window).SetupGet(candidate => candidate.IsAlive).Returns(() =>
                {
                    if (!duringOwnershipRead) { Reenter(); }
                    return true;
                });
                lateTick(timer, EventArgs.Empty);
                Assert.IsTrue(reentered);
                Assert.AreEqual(duringOwnershipRead ? 1 : 0, ownershipReads,
                    "No old endpoint ownership query may begin after IsAlive retires or cancels its owner.");
                Assert.IsFalse(timer.IsEnabled);
                Assert.AreEqual(0, PostMoveProbeMap(fixture).Count);
                lateTick(timer, EventArgs.Empty);
                Assert.AreEqual(duringOwnershipRead ? 1 : 0, ownershipReads);
                if (replacement != null)
                {
                    Assert.IsTrue(replacementTimer!.IsEnabled);
                    InvokeProbeMethod(fixture, "CancelMasterSatelliteIncomingOwnershipProbe", replacement, replacement.Handle);
                }
                Assert.AreEqual(0, PostMoveTimers(priorTimers).Length);
            }
            finally { StopNewTimers(priorTimers); }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void PostMoveRetiredLivenessResultCannotCloseReplacementCorrelation(bool throwInvalidReference)
        {
            using var fixture = CreatePostMoveTimerFixture();
            var priorTimers = DispatcherTimers().ToHashSet();
            try
            {
                var (old, _) = StartPostMoveTimer(fixture);
                var oldTimer = PostMoveTimer(priorTimers);
                var lateTick = PostMoveTick(oldTimer);
                bool reentered = false;
                PendingWindowTransfer? replacementTransfer = null;
                DispatcherTimer? replacementTimer = null;
                Mock.Get(old).SetupGet(window => window.IsAlive).Returns(() =>
                {
                    if (!reentered)
                    {
                        reentered = true;
                        var replacement = fixture.CreateWindowWithHandle("new correlation", old.Handle);
                        Assert.IsTrue(QueueProbe(fixture, replacement));
                        InvokeProbeMethod(fixture, "CancelMasterSatelliteIncomingOwnershipProbe", replacement, replacement.Handle);
                        fixture.SetWindowDesktop(replacement, fixture.Desktop);
                        var correlation = Guid.NewGuid();
                        Assert.IsTrue(fixture.Coordinator.TryPlanAndReserve(correlation, replacement.Handle, fixture.Desktop, fixture.Display,
                            new LayoutStateKey(fixture.TargetDesktop, fixture.Display), out _));
                        Assert.IsTrue(fixture.Coordinator.MarkMoving(correlation, replacement.Handle));
                        Assert.IsTrue(fixture.Coordinator.TryGetTransfer(correlation, out replacementTransfer));
                        InvokeProbeMethod(fixture, "ReconcileMasterSatelliteTransferAfterMove", replacement, replacement.Handle, replacementTransfer, 6);
                        replacementTimer = PostMoveTimer(priorTimers);
                    }
                    if (throwInvalidReference) { throw new InvalidWindowReferenceException(old.Handle); }
                    return false;
                });
                lateTick(oldTimer, EventArgs.Empty);
                Assert.IsTrue(reentered);
                Assert.IsFalse(oldTimer.IsEnabled);
                Assert.IsTrue(replacementTimer!.IsEnabled);
                Assert.IsTrue(fixture.Coordinator.TryGetTransfer(replacementTransfer!.CorrelationId, out var current));
                Assert.AreEqual(PendingWindowTransferState.Moving, current.State,
                    "A dead result from the retired wrapper cannot call WindowClosed on the replacement correlation.");
                Assert.AreEqual(1, fixture.Coordinator.ActiveTransferCount);
                Assert.AreEqual(1, fixture.Coordinator.ReservationCount);
                lateTick(oldTimer, EventArgs.Empty);
                Assert.IsTrue(replacementTimer.IsEnabled);
                Assert.AreEqual(1, PostMoveProbeMap(fixture).Count);
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
        public void PostMoveTimerStartReentrancyCannotResurrectCancelledOwner(bool rearm, bool dispose, bool nestedStart)
        {
            using var fixture = CreatePostMoveTimerFixture();
            var priorTimers = DispatcherTimers().ToHashSet();
            IWindow? window = null;
            PendingWindowTransfer? transfer = null;
            DispatcherTimer? capturedTimer = null;
            EventHandler? capturedTick = null;
            bool reentered = false;
            void OnPosted(object? sender, DispatcherHookEventArgs args)
            {
                if (reentered || args.Operation.Priority != DispatcherPriority.Inactive) { return; }
                reentered = true;
                var probe = PostMoveProbeMap(fixture)[window!.Handle]!;
                capturedTimer = (DispatcherTimer)probe.GetType().GetProperty("Timer")!.GetValue(probe)!;
                capturedTick = PostMoveTick(capturedTimer);
                if (nestedStart)
                {
                    InvokeProbeMethod(fixture, "ReconcileMasterSatelliteTransferAfterMove", window, window.Handle, transfer, 5);
                }
                if (dispose)
                {
                    fixture.Service.Dispose();
                }
                else
                {
                    Assert.IsTrue(fixture.Coordinator.Cancel(transfer!.CorrelationId, window.Handle, "Cancel during timer Start"));
                    Assert.IsTrue(fixture.Coordinator.TryGetTransfer(transfer.CorrelationId, out var terminal));
                    InvokeProbeMethod(fixture, "OnAlgorithmicTransferTerminated", terminal);
                }
            }
            try
            {
                if (rearm)
                {
                    (window, transfer) = StartPostMoveTimer(fixture);
                    var timer = PostMoveTimer(priorTimers);
                    Dispatcher.CurrentDispatcher.Hooks.OperationPosted += OnPosted;
                    PostMoveTick(timer)(timer, EventArgs.Empty);
                }
                else
                {
                    StartPostMoveTimer(fixture, (candidate, pending) =>
                    {
                        window = candidate;
                        transfer = pending;
                        Dispatcher.CurrentDispatcher.Hooks.OperationPosted += OnPosted;
                    });
                }
                Assert.IsTrue(reentered, "The test must reach the real DispatcherTimer.Start callback boundary.");
                Assert.IsFalse(capturedTimer!.IsEnabled);
                Assert.AreEqual(0, PostMoveProbeMap(fixture).Count);
                Assert.AreEqual(0, PostMoveTimers(priorTimers).Length);
                int ownershipReads = 0;
                fixture.TargetDesktopMock.Setup(desktop => desktop.HasWindow(window!)).Returns(() => { ownershipReads++; return false; });
                capturedTick!(capturedTimer, EventArgs.Empty);
                Assert.AreEqual(0, ownershipReads);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.Hooks.OperationPosted -= OnPosted;
                // An old binary can leave a disabled WPF timer registered after
                // reentrant Stop. Recover only the test-owned detached timer.
                if (capturedTimer != null) { capturedTimer.Start(); capturedTimer.Stop(); }
                StopNewTimers(priorTimers);
            }
        }

        [TestMethod]
        public void PostMoveTimerCounterScenario()
        {
            using var fixture = CreatePostMoveTimerFixture();
            var priorTimers = DispatcherTimers().ToHashSet();
            var created = new HashSet<DispatcherTimer>();
            int deferredProbes = 0;
            try
            {
                for (int cycle = 0; cycle < 100; cycle++)
                {
                    var (window, transfer) = StartPostMoveTimer(fixture);
                    fixture.TargetDesktopMock.Setup(desktop => desktop.HasWindow(window)).Returns(() => { deferredProbes++; return false; });
                    for (int probe = 0; probe < 6; probe++)
                    {
                        var timer = PostMoveTimer(priorTimers);
                        created.Add(timer);
                        Assert.AreEqual(TimeSpan.FromMilliseconds(50), timer.Interval);
                        PostMoveTick(timer)(timer, EventArgs.Empty);
                    }
                    Assert.AreEqual(0, PostMoveTimers(priorTimers).Length);
                    Assert.IsTrue(fixture.Coordinator.TryGetTransfer(transfer.CorrelationId, out var terminal));
                    Assert.AreEqual(PendingWindowTransferState.Failed, terminal.State);
                    Assert.AreEqual("PostMoveOwnershipNotObserved", terminal.TerminalReason);
                    Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
                    // The archived baseline has no owner map; when present it
                    // must release every completed endpoint before the next cycle.
                    if (typeof(TilingService).GetField("m_masterSatellitePostMoveOwnershipProbes", BindingFlags.Instance | BindingFlags.NonPublic) != null)
                    {
                        Assert.AreEqual(0, PostMoveProbeMap(fixture).Count);
                    }
                    fixture.DrainDispatcher();
                }
                Assert.AreEqual(600, deferredProbes);
                TestContext.WriteLine($"PERFCOUNTER postmove-timer timer-instances {created.Count}");
                TestContext.WriteLine($"PERFCOUNTER postmove-timer deferred-probes {deferredProbes}");
                TestContext.WriteLine("PERFCOUNTER postmove-timer cycles 100");
            }
            finally { StopNewTimers(priorTimers); }
        }

        private static ServiceFixture CreatePostMoveTimerFixture()
        {
            var fixture = new ServiceFixture(EnabledSettings(true), includeSecondDesktop: true);
            fixture.DrainDispatcher();
            return fixture;
        }

        private static (IWindow Window, PendingWindowTransfer Transfer) StartPostMoveTimer(
            ServiceFixture fixture,
            Action<IWindow, PendingWindowTransfer>? beforeSchedule = null)
        {
            var window = fixture.CreateWindow("pending transfer");
            fixture.SetWindowDesktop(window, fixture.Desktop);
            Assert.IsTrue(fixture.EventTracker.TryGetStableWindowHandle(window, out var handle));
            var correlation = Guid.NewGuid();
            Assert.IsTrue(fixture.Coordinator.TryPlanAndReserve(correlation, handle, fixture.Desktop, fixture.Display,
                new LayoutStateKey(fixture.TargetDesktop, fixture.Display), out _));
            Assert.IsTrue(fixture.Coordinator.MarkMoving(correlation, handle));
            Assert.IsTrue(fixture.Coordinator.TryGetTransfer(correlation, out var transfer));
            beforeSchedule?.Invoke(window, transfer);
            InvokeProbeMethod(fixture, "ReconcileMasterSatelliteTransferAfterMove", window, handle, transfer, 6);
            return (window, transfer);
        }

        private static DispatcherTimer[] PostMoveTimers(HashSet<DispatcherTimer> prior) =>
            DispatcherTimers().Except(prior).Where(timer => timer.Interval == TimeSpan.FromMilliseconds(50)).ToArray();

        private static DispatcherTimer PostMoveTimer(HashSet<DispatcherTimer> prior) => PostMoveTimers(prior).Single();

        private static EventHandler PostMoveTick(DispatcherTimer timer) =>
            (EventHandler)typeof(DispatcherTimer).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(field => field.FieldType == typeof(EventHandler)).GetValue(timer)!;

        private static IDictionary PostMoveProbeMap(ServiceFixture fixture) =>
            (IDictionary)typeof(TilingService).GetField("m_masterSatellitePostMoveOwnershipProbes", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(fixture.Service)!;
    }
}
