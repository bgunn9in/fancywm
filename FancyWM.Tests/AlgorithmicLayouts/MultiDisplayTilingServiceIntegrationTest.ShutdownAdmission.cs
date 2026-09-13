#nullable enable

using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    public partial class MultiDisplayTilingServiceIntegrationTest
    {
        [TestMethod]
        public void ShutdownDuringDisplayStartRetainsReturnedChildUntilItsPreparationCompletes()
        {
            using var fixture = new ServiceFixture(includeSecondDisplay: false);
            var pending = NewShutdownCompletion();
            Task? prepared = null;
            FakeTilingService? added = null;
            fixture.OnServiceCreated = child =>
            {
                added = child;
                child.OnPrepareForShutdown = () => pending.Task;
                child.OnStart = () => prepared = fixture.Service.PrepareForShutdownAsync();
            };
            fixture.AddDisplay(fixture.SecondDisplay);
            try
            {
                Assert.IsNotNull(added);
                Assert.IsNotNull(prepared);
                Assert.IsFalse(prepared!.IsCompleted, "The entered Start owns the newly returned child even before Add completes.");
                Assert.AreEqual(1, added!.PrepareForShutdownCount);
                Assert.AreEqual(0, added.StopCount);
                Assert.AreEqual(0, added.DisposeCount);
            }
            finally
            {
                pending.TrySetResult();
                if (prepared != null) { PumpShutdown(prepared); }
            }
            fixture.Service.Stop();
            fixture.Service.Dispose();
            Assert.AreEqual(1, added!.StopCount);
            Assert.AreEqual(1, added.DisposeCount);
        }

        [TestMethod]
        public void ShutdownDuringDisplayRemovalWaitsForDetachedChildBeforeWorkspaceCanClose()
        {
            using var fixture = new ServiceFixture(includeSecondDisplay: true);
            var removed = fixture.GetService(fixture.SecondDisplay);
            var pending = NewShutdownCompletion();
            removed.OnPrepareForShutdown = () => pending.Task;
            fixture.FocusDisplay(fixture.SecondDisplay);
            fixture.Service.CanToggleMasterSatelliteLayout();
            fixture.FocusDisplay(fixture.PrimaryDisplay);
            Task? prepared = null;
            fixture.OnAutoRegisterWindows = _ => prepared ??= fixture.Service.PrepareForShutdownAsync();
            fixture.RemoveDisplay(fixture.SecondDisplay);
            try
            {
                Assert.IsNotNull(prepared);
                Assert.IsFalse(prepared!.IsCompleted, "A removed child remains owned while its direct placement drains.");
                Assert.AreEqual(1, removed.PrepareForShutdownCount);
                Assert.AreEqual(0, removed.StopCount);
                Assert.AreEqual(0, removed.DisposeCount);
                Assert.AreEqual(0, fixture.GarbageCollectionRequests);
            }
            finally
            {
                pending.TrySetResult();
                if (prepared != null) { PumpShutdown(prepared); }
            }
            Assert.AreEqual(1, removed.StopCount);
            Assert.AreEqual(1, removed.DisposeCount);
            Assert.AreEqual(1, fixture.GarbageCollectionRequests);
            fixture.Service.Stop();
            fixture.Service.Dispose();
            Assert.AreEqual(1, removed.StopCount);
            Assert.AreEqual(1, removed.DisposeCount);
        }

        private static void PumpShutdown(Task completion)
        {
            if (completion.IsCompleted) { completion.GetAwaiter().GetResult(); return; }
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            var timeout = new DispatcherTimer(DispatcherPriority.Send, dispatcher)
            {
                Interval = TimeSpan.FromSeconds(10),
            };
            void StopOnTimeout(object? sender, EventArgs args) => frame.Continue = false;
            timeout.Tick += StopOnTimeout;
            _ = completion.ContinueWith(_ => dispatcher.BeginInvoke(DispatcherPriority.Send,
                new Action(() => frame.Continue = false)), TaskScheduler.Default);
            try { timeout.Start(); Dispatcher.PushFrame(frame); }
            finally { timeout.Stop(); timeout.Tick -= StopOnTimeout; }
            Assert.IsTrue(completion.IsCompleted, "The owned display shutdown must finish after its controlled child is released.");
            completion.GetAwaiter().GetResult();
        }

        [TestMethod]
        public void ShutdownWaitsForRetiringOwnerAndPreservesEveryCleanupFailure()
        {
            using var fixture = new ServiceFixture(includeSecondDisplay: true);
            var removed = fixture.GetService(fixture.SecondDisplay);
            var pending = NewShutdownCompletion();
            var stopFailure = new InvalidOperationException("retiring stop");
            var disposeFailure = new InvalidOperationException("retiring dispose");
            removed.OnPrepareForShutdown = () => pending.Task;
            removed.OnStop = () => throw stopFailure;
            removed.OnDispose = () => throw disposeFailure;
            fixture.FocusDisplay(fixture.SecondDisplay);
            fixture.Service.CanToggleMasterSatelliteLayout();
            fixture.FocusDisplay(fixture.PrimaryDisplay);
            Task? prepared = null;
            fixture.OnAutoRegisterWindows = _ => prepared ??= fixture.Service.PrepareForShutdownAsync();
            fixture.RemoveDisplay(fixture.SecondDisplay);
            AggregateException? observed = null;
            try
            {
                Assert.IsNotNull(prepared);
                Assert.IsFalse(prepared!.IsCompleted);
                Assert.AreEqual(0, removed.StopCount);
                Assert.AreEqual(0, removed.DisposeCount);
            }
            finally
            {
                pending.TrySetResult();
                if (prepared != null)
                {
                    try { PumpShutdown(prepared); }
                    catch (AggregateException error) { observed = error; }
                }
            }
            Assert.IsNotNull(observed);
            CollectionAssert.AreEqual(new Exception[] { stopFailure, disposeFailure }, observed!.Flatten().InnerExceptions.ToArray());
            Assert.AreEqual(1, removed.StopCount);
            Assert.AreEqual(1, removed.DisposeCount);
            Assert.AreEqual(1, fixture.GarbageCollectionRequests);
        }
    }
}
