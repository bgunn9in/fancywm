#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using FancyWM.Layouts.Tiling;
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
        [DataRow("Moving")]
        [DataRow("Resizing")]
        public void CadenceGuardedInvalidationPublishesAtNextAllowedFrame(string interaction)
            => RunOnSta(() =>
            {
                using var cadence = new CadenceFixture();
                cadence.Arm(interaction);
                int before = cadence.Fixture.Overlay.UpdateOverlayCount;

                cadence.Invalidate();
                cadence.Fixture.DrainDispatcher();

                Assert.AreEqual(1, cadence.Waits.Count,
                    "A throttled invalidation must retain one trailing frame instead of consuming the only dirty pass.");
                Assert.AreEqual(before, cadence.Fixture.Overlay.UpdateOverlayCount);
                Assert.IsTrue(cadence.Waits[0].Duration > TimeSpan.Zero);
                Assert.IsTrue(cadence.Waits[0].Duration <= cadence.FrameInterval + TimeSpan.FromTicks(1));
                Assert.IsFalse(cadence.Waits[0].Completion.Task.IsCompleted);

                cadence.CompleteAtNextFrame(0);
                cadence.Fixture.DrainMouseLayoutPipeline();

                Assert.AreEqual(before + 1, cadence.Fixture.Overlay.UpdateOverlayCount);
                Assert.AreEqual(1, cadence.Waits.Count);
                cadence.AssertArrangedWindows();
                cadence.Fixture.DrainDispatcher();
                Assert.AreEqual(before + 1, cadence.Fixture.Overlay.UpdateOverlayCount,
                    "Completing the retained frame must not create a second layout loop.");
            });

        [TestMethod]
        public void CadenceRefreshRateChangeCannotRejectAlreadyAdmittedPass()
            => RunOnSta(() =>
            {
                using var cadence = new CadenceFixture();
                cadence.Arm();
                cadence.Now += TimeSpan.FromMilliseconds(20);
                int before = cadence.Fixture.Overlay.UpdateOverlayCount;
                int refreshReads = 0;
                Mock.Get(cadence.Fixture.Display).SetupGet(display => display.RefreshRate)
                    .Returns(() => ++refreshReads == 1 ? 60 : 30);

                cadence.Invalidate();
                cadence.Fixture.DrainMouseLayoutPipeline();

                Assert.IsTrue(refreshReads > 0);
                Assert.AreEqual(0, cadence.Waits.Count,
                    "At 20 ms the pass is already eligible under its observed 60 Hz cadence.");
                Assert.AreEqual(before + 1, cadence.Fixture.Overlay.UpdateOverlayCount,
                    "A later refresh-rate sample must not consume the admitted dirty pass without applying it.");
                cadence.AssertArrangedWindows();
            });

        [TestMethod]
        public void CadenceBurstDuringWaitPublishesLatestWorkAreaOnce()
            => RunOnSta(() =>
            {
                using var cadence = new CadenceFixture();
                cadence.Arm();
                int before = cadence.Fixture.Overlay.UpdateOverlayCount;
                for (int index = 0; index < 100; index++) { cadence.Invalidate(); }
                cadence.Fixture.DrainDispatcher();
                Assert.AreEqual(1, cadence.Waits.Count);

                var latest = Rectangle.OffsetAndSize(0, 0, 2400, 1000);
                Mock.Get(cadence.Fixture.Display).SetupGet(display => display.WorkArea)
                    .Returns(Rectangle.OffsetAndSize(0, 0, 2800, 1200));
                cadence.Invalidate();
                Mock.Get(cadence.Fixture.Display).SetupGet(display => display.WorkArea).Returns(latest);
                for (int index = 0; index < 100; index++) { cadence.Invalidate(); }
                cadence.Fixture.DrainDispatcher();

                Assert.AreEqual(1, cadence.Waits.Count,
                    "New invalidations during an admitted wait must share the same pending cadence operation.");
                Assert.AreEqual(before, cadence.Fixture.Overlay.UpdateOverlayCount);
                cadence.CompleteAtNextFrame(0);
                cadence.Fixture.DrainMouseLayoutPipeline();

                Assert.AreEqual(before + 1, cadence.Fixture.Overlay.UpdateOverlayCount);
                Assert.AreEqual(latest, GetBackend(cadence.Fixture).GetTree(cadence.Fixture.Desktop)!.WorkArea);
                cadence.AssertArrangedWindows();
                Assert.AreEqual(1, cadence.Waits.Count);
            });

        [TestMethod]
        public void CadenceDelayFailureIsObservedAndLaterInvalidationRearms()
            => RunOnSta(() =>
            {
                var logger = new Mock<ILogger>(MockBehavior.Loose);
                using var cadence = new CadenceFixture(logger.Object);
                cadence.Arm();
                int before = cadence.Fixture.Overlay.UpdateOverlayCount;
                cadence.Invalidate();
                cadence.Fixture.DrainDispatcher();
                Assert.AreEqual(1, cadence.Waits.Count);
                var failure = new InvalidOperationException("controlled cadence delay failure");
                Assert.IsTrue(cadence.Waits[0].Completion.TrySetException(failure));
                cadence.Fixture.DrainDispatcher();
                Assert.AreEqual(1, logger.Invocations.Count(invocation =>
                    invocation.Method.Name == "Error"
                    && invocation.Arguments.Any(argument => ReferenceEquals(argument, failure))));
                Assert.AreEqual(before, cadence.Fixture.Overlay.UpdateOverlayCount);

                cadence.Invalidate();
                cadence.Fixture.DrainDispatcher();
                Assert.AreEqual(2, cadence.Waits.Count,
                    "A failed cadence operation must release queue admission for the next invalidation.");
                cadence.CompleteAtNextFrame(1);
                cadence.Fixture.DrainMouseLayoutPipeline();
                Assert.AreEqual(before + 1, cadence.Fixture.Overlay.UpdateOverlayCount);
                cadence.AssertArrangedWindows();
            });

        [TestMethod]
        public void CadenceWaitRechecksFreezeAndRetainsInvalidationUntilUnfreeze()
            => RunOnSta(() =>
            {
                using var cadence = new CadenceFixture();
                cadence.Arm();
                int before = cadence.Fixture.Overlay.UpdateOverlayCount;
                cadence.Invalidate();
                cadence.Fixture.DrainDispatcher();
                Assert.AreEqual(1, cadence.Waits.Count);
                cadence.Invoke("Freeze");
                try
                {
                    cadence.CompleteAtNextFrame(0);
                    cadence.Fixture.DrainDispatcher();
                    Assert.AreEqual(before, cadence.Fixture.Overlay.UpdateOverlayCount,
                        "A previously admitted delay cannot bypass a later freeze.");
                    Assert.IsTrue(cadence.Fixture.LayoutInvalidated);
                }
                finally { cadence.Invoke("Unfreeze"); }
                cadence.Fixture.DrainMouseLayoutPipeline();
                Assert.AreEqual(before + 1, cadence.Fixture.Overlay.UpdateOverlayCount);
                cadence.AssertArrangedWindows();
            });

        [TestMethod]
        public void CadenceWaitCannotPublishAfterStopAndRestartStillApplies()
            => RunOnSta(() =>
            {
                using var cadence = new CadenceFixture();
                cadence.Arm();
                int before = cadence.Fixture.Overlay.UpdateOverlayCount;
                cadence.Invalidate();
                cadence.Fixture.DrainDispatcher();
                Assert.AreEqual(1, cadence.Waits.Count);

                cadence.Fixture.Service.Stop();
                cadence.CompleteAtNextFrame(0);
                cadence.Fixture.DrainDispatcher();
                Assert.AreEqual(before, cadence.Fixture.Overlay.UpdateOverlayCount);
                Assert.IsFalse(cadence.Fixture.Service.Active);

                cadence.Fixture.Service.Start();
                cadence.Fixture.DrainMouseLayoutPipeline();
                Assert.AreEqual(before + 1, cadence.Fixture.Overlay.UpdateOverlayCount);
                cadence.AssertArrangedWindows();
            });

        [TestMethod]
        public void CadenceDisposeCancelsOwnedWaitAndRejectsUncancellableLateCompletion()
            => RunOnSta(() =>
            {
                using var cadence = new CadenceFixture();
                cadence.Arm();
                int before = cadence.Fixture.Overlay.UpdateOverlayCount;
                cadence.Invalidate();
                cadence.Fixture.DrainDispatcher();
                Assert.AreEqual(1, cadence.Waits.Count);
                var wait = cadence.Waits[0];
                Assert.IsTrue(wait.Cancellation.CanBeCanceled);
                cadence.Fixture.Service.Dispose();
                cadence.Fixture.Service.Dispose();
                Assert.IsTrue(wait.Cancellation.IsCancellationRequested,
                    "Disposal must cancel the existing cadence owner's admitted delay.");

                // This adapter deliberately ignores cancellation until released,
                // proving the post-await guard independently of Task.Delay.
                cadence.CompleteAtNextFrame(0);
                cadence.Fixture.DrainDispatcher();
                cadence.Invalidate();
                cadence.Fixture.DrainDispatcher();
                Assert.AreEqual(before, cadence.Fixture.Overlay.UpdateOverlayCount);
                Assert.AreEqual(1, cadence.Waits.Count);
                Assert.IsTrue(cadence.Fixture.Overlay.IsDisposed);
            });

        [TestMethod]
        public void CadenceWaitRechecksInteractionEndWithoutAnotherClockTick()
            => RunOnSta(() =>
            {
                using var cadence = new CadenceFixture();
                cadence.Arm();
                int before = cadence.Fixture.Overlay.UpdateOverlayCount;
                cadence.Invalidate();
                cadence.Fixture.DrainDispatcher();
                Assert.AreEqual(1, cadence.Waits.Count);

                cadence.SetInteraction("None");
                cadence.Invalidate();
                Assert.IsTrue(cadence.Waits[0].Completion.TrySetResult(true));
                cadence.Fixture.DrainMouseLayoutPipeline();
                Assert.AreEqual(before + 1, cadence.Fixture.Overlay.UpdateOverlayCount);
                Assert.AreEqual(1, cadence.Waits.Count,
                    "A finished interaction must not enter a second cadence wait at the old timestamp.");
                cadence.AssertArrangedWindows();
            });

        [TestMethod]
        public void CadenceEarlyWakeRetainsOneWaitUntilTheAllowedFrame()
            => RunOnSta(() =>
            {
                using var cadence = new CadenceFixture();
                cadence.Arm();
                int before = cadence.Fixture.Overlay.UpdateOverlayCount;
                cadence.Invalidate();
                cadence.Fixture.DrainDispatcher();
                Assert.AreEqual(1, cadence.Waits.Count);

                cadence.Now += TimeSpan.FromTicks(cadence.FrameInterval.Ticks / 2);
                Assert.IsTrue(cadence.Waits[0].Completion.TrySetResult(true));
                cadence.Fixture.DrainDispatcher();

                Assert.AreEqual(before, cadence.Fixture.Overlay.UpdateOverlayCount,
                    "An early timer completion must not accelerate the interaction cadence.");
                Assert.AreEqual(2, cadence.Waits.Count);
                Assert.AreEqual(1, cadence.Waits.Count(wait => !wait.Completion.Task.IsCompleted),
                    "Rechecking an early wake must retain one pending operation, not parallel waits.");
                Assert.IsTrue(cadence.Waits[1].Duration > TimeSpan.Zero);
                Assert.IsTrue(cadence.Waits[1].Duration < cadence.Waits[0].Duration);

                cadence.CompleteAtNextFrame(1);
                cadence.Fixture.DrainMouseLayoutPipeline();
                Assert.AreEqual(before + 1, cadence.Fixture.Overlay.UpdateOverlayCount);
                Assert.AreEqual(0, cadence.Waits.Count(wait => !wait.Completion.Task.IsCompleted));
                cadence.AssertArrangedWindows();
            });

        [TestMethod]
        public void CadenceCanceledTaskAfterDisposeDoesNotReportAnUpdateFailure()
            => RunOnSta(() =>
            {
                var logger = new Mock<ILogger>(MockBehavior.Loose);
                using var cadence = new CadenceFixture(logger.Object);
                cadence.Arm();
                int before = cadence.Fixture.Overlay.UpdateOverlayCount;
                cadence.Invalidate();
                cadence.Fixture.DrainDispatcher();
                Assert.AreEqual(1, cadence.Waits.Count);
                int previousErrors = logger.Invocations.Count(invocation => invocation.Method.Name == "Error");
                var wait = cadence.Waits[0];

                cadence.Fixture.Service.Dispose();
                Assert.IsTrue(wait.Cancellation.IsCancellationRequested);
                Assert.IsTrue(wait.Completion.TrySetCanceled(wait.Cancellation));
                cadence.Fixture.DrainDispatcher();
                cadence.Invalidate();
                cadence.Fixture.DrainDispatcher();

                Assert.IsTrue(wait.Completion.Task.IsCanceled);
                Assert.AreEqual(previousErrors,
                    logger.Invocations.Count(invocation => invocation.Method.Name == "Error"),
                    "Normal owner cancellation must not be reported as a failed tiling update.");
                Assert.AreEqual(before, cadence.Fixture.Overlay.UpdateOverlayCount);
                Assert.AreEqual(1, cadence.Waits.Count);
            });

        [TestMethod]
        public void CadenceNewerInvalidationDuringFailedWaitAutomaticallyRearms()
            => RunOnSta(() =>
            {
                var logger = new Mock<ILogger>(MockBehavior.Loose);
                using var cadence = new CadenceFixture(logger.Object);
                cadence.Arm();
                int before = cadence.Fixture.Overlay.UpdateOverlayCount;
                cadence.Invalidate();
                cadence.Fixture.DrainDispatcher();
                Assert.AreEqual(1, cadence.Waits.Count);

                var latest = Rectangle.OffsetAndSize(0, 0, 2200, 900);
                Mock.Get(cadence.Fixture.Display).SetupGet(display => display.WorkArea).Returns(latest);
                cadence.Invalidate();
                var failure = new InvalidOperationException("first cadence wait failed after a newer invalidation");
                Assert.IsTrue(cadence.Waits[0].Completion.TrySetException(failure));
                cadence.Fixture.DrainDispatcher();

                Assert.AreEqual(1, logger.Invocations.Count(invocation =>
                    invocation.Method.Name == "Error"
                    && invocation.Arguments.Any(argument => ReferenceEquals(argument, failure))));
                Assert.AreEqual(before, cadence.Fixture.Overlay.UpdateOverlayCount);
                Assert.AreEqual(2, cadence.Waits.Count,
                    "The queued newer invalidation must rearm without requiring another external event.");
                Assert.AreEqual(1, cadence.Waits.Count(wait => !wait.Completion.Task.IsCompleted));

                cadence.CompleteAtNextFrame(1);
                cadence.Fixture.DrainMouseLayoutPipeline();
                Assert.AreEqual(before + 1, cadence.Fixture.Overlay.UpdateOverlayCount);
                Assert.AreEqual(latest, GetBackend(cadence.Fixture).GetTree(cadence.Fixture.Desktop)!.WorkArea);
                cadence.AssertArrangedWindows();
                Assert.AreEqual(0, cadence.Waits.Count(wait => !wait.Completion.Task.IsCompleted));
            });

        [TestMethod]
        public void CadenceCounterScenario()
        {
            int layouts = 0, delays = 0, pending = 0;
            const int cycles = 100;
            RunOnSta(() =>
            {
                using var cadence = new CadenceFixture();
                int before = cadence.Fixture.Overlay.UpdateOverlayCount;
                for (int cycle = 0; cycle < cycles; cycle++)
                {
                    cadence.Arm();
                    int previousDelays = cadence.Waits.Count;
                    cadence.Invalidate();
                    cadence.Fixture.DrainDispatcher();
                    int newDelays = cadence.Waits.Count - previousDelays;
                    Assert.IsTrue(newDelays is 0 or 1,
                        "The comparison admits at most one bounded wait per invalidation cycle.");
                    cadence.Now += cadence.FrameInterval + TimeSpan.FromTicks(1);
                    if (newDelays == 1)
                    {
                        Assert.IsTrue(cadence.Waits[previousDelays].Completion.TrySetResult(true));
                    }
                    cadence.Fixture.DrainMouseLayoutPipeline();
                    cadence.AssertArrangedWindows();
                }
                layouts = cadence.Fixture.Overlay.UpdateOverlayCount - before;
                delays = cadence.Waits.Count;
                pending = cadence.Waits.Count(wait => !wait.Completion.Task.IsCompleted);
                Assert.AreEqual(0, pending);
                Assert.IsTrue(layouts is 0 or cycles);
                Assert.AreEqual(layouts, delays,
                    "Every admitted comparison wait must produce exactly its retained layout.");
            });
            TestContext.WriteLine($"PERFCOUNTER layout-cadence cycles {cycles}");
            TestContext.WriteLine($"PERFCOUNTER layout-cadence trailing-layouts {layouts}");
            TestContext.WriteLine($"PERFCOUNTER layout-cadence cadence-delays {delays}");
            TestContext.WriteLine($"PERFCOUNTER layout-cadence pending-delays {pending}");
        }

        private sealed class CadenceFixture : IDisposable
        {
            public ServiceFixture Fixture { get; }
            public IWindow First { get; }
            public IWindow Second { get; }
            public TimeSpan Now { get; set; } = TimeSpan.FromSeconds(10);
            public TimeSpan FrameInterval => TimeSpan.FromSeconds(1.0 / Fixture.Display.RefreshRate);
            public List<CadenceWait> Waits { get; } = [];

            public CadenceFixture(ILogger? logger = null)
            {
                Fixture = new ServiceFixture(EnabledSettings(false) with
                {
                    AnimateWindowMovement = false,
                    DelayReposition = false
                }, logger: logger);
                try
                {
                    First = Fixture.AddWindow("Cadence first");
                    Second = Fixture.AddWindow("Cadence second");
                    SetField("m_layoutElapsed", (Func<TimeSpan>)(() => Now));
                    SetField("m_layoutDelay", (Func<TimeSpan, CancellationToken, Task>)Delay);
                    Fixture.Service.Start();
                    Fixture.DrainMouseLayoutPipeline();
                    Assert.AreEqual(0, Waits.Count, "The noninteractive setup must not use cadence delays.");
                    AssertArrangedWindows();
                }
                catch
                {
                    Fixture.Dispose();
                    throw;
                }
            }

            private Task Delay(TimeSpan duration, CancellationToken cancellation)
            {
                var wait = new CadenceWait(duration, cancellation);
                Waits.Add(wait);
                return wait.Completion.Task;
            }

            public void Arm(string interaction = "Resizing")
            {
                SetInteraction(interaction);
                SetField("m_lastUpdateLayout", Now);
            }

            public void SetInteraction(string interaction)
            {
                var field = typeof(TilingService).GetField("m_currentInteraction", BindingFlags.NonPublic | BindingFlags.Instance)!;
                field.SetValue(Fixture.Service, Enum.Parse(field.FieldType, interaction));
            }

            private void SetField(string name, object value) => typeof(TilingService)
                .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(Fixture.Service, value);

            public void Invalidate() => Invoke("InvalidateLayout");

            public void Invoke(string method) => typeof(TilingService)
                .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(Fixture.Service, null);

            public void CompleteAtNextFrame(int index)
            {
                Now += FrameInterval + TimeSpan.FromTicks(1);
                Assert.IsTrue(Waits[index].Completion.TrySetResult(true));
            }

            public void AssertArrangedWindows()
            {
                var tree = GetBackend(Fixture).GetTree(Fixture.Desktop)!;
                CollectionAssert.AreEquivalent(new[] { First, Second },
                    Fixture.Overlay.LastSnapshot.OfType<WindowNode>()
                        .Select(node => node.WindowReference).ToArray());
                foreach (var window in new[] { First, Second })
                {
                    var node = tree.FindNode(window)!;
                    Assert.IsTrue(Fixture.Overlay.LastSnapshot.Any(candidate => ReferenceEquals(candidate, node)));
                    Assert.AreEqual(node.ComputedRectangle, window.Position,
                        "The fake native adapter must receive the final rectangle of the actual arranged node.");
                }
                Assert.AreEqual(0, Fixture.Coordinator.ActiveTransferCount);
                Assert.AreEqual(0, Fixture.Coordinator.ReservationCount);
            }

            public void Dispose()
            {
                Fixture.Dispose();
                foreach (var wait in Waits) { wait.Completion.TrySetResult(true); }
                Fixture.DrainDispatcher();
            }
        }

        private sealed class CadenceWait(TimeSpan duration, CancellationToken cancellation)
        {
            public TimeSpan Duration { get; } = duration;
            public CancellationToken Cancellation { get; } = cancellation;
            public TaskCompletionSource<bool> Completion { get; } = new();
        }
    }
}
