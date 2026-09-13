#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using FancyWM.Utilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    public partial class TilingServiceAlgorithmicIntegrationTest
    {
        [TestMethod]
        public void ShutdownPreparationWaitsForDirectPlacementBeforeFinalRestore()
            => RunOnSta(() =>
            {
                using var fixture = new ServiceFixture(EnabledSettings(false) with
                {
                    AnimateWindowMovement = false,
                    DelayReposition = false,
                });
                var window = fixture.AddWindow("Owned direct placement");
                var original = window.Position;
                using var entered = new ManualResetEventSlim();
                using var resume = new ManualResetEventSlim();
                var positions = new List<Rectangle>();
                var position = original;
                Mock.Get(window).SetupGet(owner => owner.Position).Returns(() => position);
                Mock.Get(window).Setup(owner => owner.SetPosition(It.IsAny<Rectangle>())).Callback((Rectangle next) =>
                {
                    if (positions.Count == 0)
                    {
                        entered.Set();
                        Assert.IsTrue(resume.Wait(TimeSpan.FromSeconds(10)), "The controlled native placement must be released.");
                    }
                    positions.Add(next);
                    position = next;
                });
                fixture.Service.Start();
                fixture.DrainDispatcher();
                Task? prepared = null;
                try
                {
                    Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
                    var node = GetBackend(fixture).GetTree(fixture.Desktop)!.FindNode(window);
                    prepared = fixture.Service.PrepareForShutdownAsync();
                    Assert.IsFalse(prepared.IsCompleted, "An entered native Task.Run remains part of the layout owner's lifetime.");
                    Assert.IsFalse(fixture.Service.Active);
                    Assert.IsFalse(fixture.Overlay.IsDisposed);
                    Assert.AreSame(node, GetBackend(fixture).GetTree(fixture.Desktop)!.FindNode(window));
                    Assert.AreEqual(original, position);
                    Assert.AreSame(prepared, fixture.Service.PrepareForShutdownAsync());
                }
                finally
                {
                    resume.Set();
                    fixture.DrainMouseLayoutPipeline();
                    prepared?.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                }
                Assert.AreEqual(1, positions.Count);
                fixture.Service.Stop();
                Assert.AreEqual(original, window.Position, "Final restoration must follow the last admitted placement.");
                Assert.AreEqual(original, positions[^1]);
                int writes = positions.Count;
                fixture.DrainDispatcher();
                Assert.AreEqual(writes, positions.Count);
                Assert.IsFalse(fixture.Overlay.IsDisposed, "Preparation and Stop do not dispose native/workspace owners.");
            });

        [TestMethod]
        public void ShutdownPreparationCancelsOwnedCadenceAndRejectsLateStart()
            => RunOnSta(() =>
            {
                using var cadence = new CadenceFixture();
                cadence.Arm();
                int before = cadence.Fixture.Overlay.UpdateOverlayCount;
                cadence.Invalidate();
                cadence.Fixture.DrainDispatcher();
                Assert.AreEqual(1, cadence.Waits.Count);
                var wait = cadence.Waits[0];
                using var cancellation = wait.Cancellation.Register(() => wait.Completion.TrySetCanceled(wait.Cancellation));
                var prepared = cadence.Fixture.Service.PrepareForShutdownAsync();
                try
                {
                    Assert.IsTrue(wait.Cancellation.IsCancellationRequested);
                    Assert.IsFalse(cadence.Fixture.Service.Active);
                    cadence.Fixture.Service.Start();
                    cadence.Invalidate();
                    cadence.Fixture.DrainDispatcher();
                    Assert.IsFalse(cadence.Fixture.Service.Active, "A queued toggle/start cannot reactivate a shutting-down service.");
                    Assert.AreEqual(before, cadence.Fixture.Overlay.UpdateOverlayCount);
                    Assert.AreSame(prepared, cadence.Fixture.Service.PrepareForShutdownAsync());
                }
                finally
                {
                    wait.Completion.TrySetCanceled();
                    cadence.Fixture.DrainDispatcher();
                    prepared.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                }
                Assert.AreEqual(1, cadence.Waits.Count);
            });

        [TestMethod]
        public void ShutdownPreparationCannotAdmitSmoothingAfterAwaitedNewWindowPlacement()
            => RunOnSta(() =>
            {
                var animations = new ShutdownAnimationThread();
                using var fixture = new ServiceFixture(EnabledSettings(false) with
                {
                    AnimateWindowMovement = true,
                    DelayReposition = false,
                }, animationThread: animations);
                fixture.AddWindow("Existing tiled owner");
                fixture.Service.Start();
                fixture.DrainMouseLayoutPipeline();
                var added = fixture.CreateWindow("New direct owner");
                using var entered = new ManualResetEventSlim();
                using var resume = new ManualResetEventSlim();
                var position = added.Position;
                Mock.Get(added).SetupGet(owner => owner.Position).Returns(() => position);
                Mock.Get(added).Setup(owner => owner.SetPosition(It.IsAny<Rectangle>())).Callback((Rectangle next) =>
                {
                    entered.Set();
                    Assert.IsTrue(resume.Wait(TimeSpan.FromSeconds(10)));
                    position = next;
                });
                fixture.AddWindow(added);
                fixture.DrainDispatcher();
                Task? prepared = null;
                int previousStarts = animations.Starts;
                try
                {
                    Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
                    prepared = fixture.Service.PrepareForShutdownAsync();
                    Assert.IsFalse(prepared.IsCompleted);
                }
                finally
                {
                    resume.Set();
                    fixture.DrainMouseLayoutPipeline();
                    prepared?.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                }
                Assert.AreEqual(previousStarts, animations.Starts,
                    "The continuation after direct new-window placement cannot enqueue a later smooth animation after stop admission.");
            });

        [TestMethod]
        public void ShutdownPreparationPublishesStableTaskBeforeCancellationReentrancy()
            => RunOnSta(() =>
            {
                for (int cycle = 0; cycle < 100; cycle++)
                {
                    using var fixture = new ServiceFixture(EnabledSettings(false));
                    fixture.Service.Start();
                    fixture.DrainMouseLayoutPipeline();
                    var cancellation = fixture.GetServiceField<CancellationTokenSource>("m_layoutDelayCancellation");
                    Task? reentrant = null;
                    int cancels = 0;
                    using var callback = cancellation.Token.Register(() =>
                    {
                        cancels++;
                        reentrant = fixture.Service.PrepareForShutdownAsync();
                        fixture.Service.Start();
                    });
                    var prepared = fixture.Service.PrepareForShutdownAsync();
                    prepared.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                    Assert.AreSame(prepared, reentrant);
                    Assert.AreSame(prepared, fixture.Service.PrepareForShutdownAsync());
                    Assert.AreEqual(1, cancels);
                    Assert.IsFalse(fixture.Service.Active);
                    Assert.IsFalse(fixture.Overlay.IsDisposed);
                }
            });

        [TestMethod]
        public void ShutdownPreparationCancellationFailureStillRetainsEnteredLayout()
            => RunOnSta(() =>
            {
                using var fixture = new ServiceFixture(EnabledSettings(false) with { AnimateWindowMovement = false });
                var window = fixture.AddWindow("Cancellation error owner");
                using var entered = new ManualResetEventSlim();
                using var resume = new ManualResetEventSlim();
                Mock.Get(window).Setup(owner => owner.SetPosition(It.IsAny<Rectangle>())).Callback(() =>
                {
                    entered.Set();
                    Assert.IsTrue(resume.Wait(TimeSpan.FromSeconds(10)));
                });
                fixture.Service.Start();
                fixture.DrainDispatcher();
                var failure = new InvalidOperationException("cancellation callback");
                var cancellation = fixture.GetServiceField<CancellationTokenSource>("m_layoutDelayCancellation");
                using var callback = cancellation.Token.Register(() => throw failure);
                Task? prepared = null;
                try
                {
                    Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
                    prepared = fixture.Service.PrepareForShutdownAsync();
                    Assert.IsFalse(prepared.IsCompleted, "A cancellation error cannot release the active native placement.");
                }
                finally
                {
                    resume.Set();
                    fixture.DrainMouseLayoutPipeline();
                }
                var observed = Assert.ThrowsException<AggregateException>(() => prepared!.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
                Assert.IsTrue(observed.Flatten().InnerExceptions.Any(error => ReferenceEquals(error, failure)));
                Assert.IsFalse(fixture.Overlay.IsDisposed);
                Assert.AreSame(prepared, fixture.Service.PrepareForShutdownAsync());
            });

        private sealed class ShutdownAnimationThread : IAnimationThread
        {
            public int Starts { get; private set; }
            public void Start(IAnimationJob job) { Starts++; job.OnCompleted(); }
            public void Dispose() { }
        }
    }
}
