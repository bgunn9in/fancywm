#nullable enable

using System;
using System.Linq;
using System.Reflection;

using FancyWM.Layouts.Tiling;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    public partial class TilingServiceAlgorithmicIntegrationTest
    {
        [TestMethod]
        public void ShutdownPreparationHidesOverlayBeforeOwnedLayoutDrain()
            => RunOnSta(() =>
            {
                using var cadence = new CadenceFixture();
                cadence.Arm();
                cadence.Invalidate();
                cadence.Fixture.DrainDispatcher();
                Assert.AreEqual(1, cadence.Waits.Count);
                var wait = cadence.Waits[0];
                int hides = cadence.Fixture.Overlay.HideCount;
                var prepared = cadence.Fixture.Service.PrepareForShutdownAsync();
                try
                {
                    Assert.IsFalse(prepared.IsCompleted);
                    Assert.AreEqual(hides + 1, cadence.Fixture.Overlay.HideCount,
                        "Interactive overlays must hide while their owner is retained for the pending layout.");
                    Assert.IsFalse(cadence.Fixture.Overlay.IsDisposed);
                    Assert.AreSame(prepared, cadence.Fixture.Service.PrepareForShutdownAsync());
                    Assert.AreEqual(hides + 1, cadence.Fixture.Overlay.HideCount);
                }
                finally
                {
                    wait.Completion.TrySetCanceled();
                    cadence.Fixture.DrainDispatcher();
                    prepared.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                }
            });

        [TestMethod]
        public void PreparedServiceRejectsCapturedOverlayCloseBeforeAccessingWindow()
            => RunOnSta(() =>
            {
                using var fixture = new ServiceFixture(EnabledSettings(false));
                var window = fixture.AddWindow("Late overlay close");
                Mock.Get(window).SetupGet(owner => owner.CanClose).Returns(true);
                var close = CaptureShutdownOverlayCallback<TilingNode>(fixture.Service, "OnTilingNodeCloseRequested");
                var node = new WindowNode(window);
                fixture.Service.PrepareForShutdownAsync().GetAwaiter().GetResult();
                Mock.Get(window).Invocations.Clear();
                close(null, node);
                Mock.Get(window).Verify(owner => owner.Close(), Times.Never);
                Mock.Get(window).VerifyGet(owner => owner.CanClose, Times.Never);
            });

        [TestMethod]
        public void PreparedServiceRejectsCapturedOverlayFloatWithoutChangingRegistration()
            => RunOnSta(() =>
            {
                using var fixture = new ServiceFixture(EnabledSettings(false));
                var window = fixture.AddWindow("Late overlay float");
                fixture.Service.Start();
                fixture.DrainMouseLayoutPipeline();
                var tree = GetBackend(fixture).GetTree(fixture.Desktop)!;
                var node = tree.FindNode(window)!;
                var callback = CaptureShutdownOverlayCallback<WindowNode>(fixture.Service, "OnWindowFloatRequested");
                fixture.Service.PrepareForShutdownAsync().GetAwaiter().GetResult();
                callback(null, node);
                Assert.AreSame(node, tree.FindNode(window), "A captured input callback cannot change registration during shutdown.");
            });

        [TestMethod]
        public void PreparedServiceRejectsCapturedOverlayGroupingWithoutNewIntent()
            => RunOnSta(() =>
            {
                using var fixture = new ServiceFixture(EnabledSettings(false));
                var window = fixture.AddWindow("Late overlay group");
                var callback = CaptureShutdownOverlayCallback<WindowNode>(fixture.Service, "OnBeginHorizontalWithRequestedAsync");
                fixture.Service.PrepareForShutdownAsync().GetAwaiter().GetResult();
                try { callback(null, new WindowNode(window)); }
                catch (NullReferenceException error) when (App.Current == null
                    && error.StackTrace?.Contains("TilingService.OnPendingIntentChanged", StringComparison.Ordinal) == true)
                {
                    // The previous callback mutated PendingIntent before trying
                    // to use App's mouse hook. Keep the regression about that
                    // mutation without creating a real Application/native hook.
                }
                Assert.IsNull(fixture.Service.PendingIntent);
                Assert.AreEqual(0, fixture.Overlay.PreviewWindows.Count);
            });

        [TestMethod]
        public void OverlayHideFailureStillCancelsAndWaitsForOwnedLayout()
            => RunOnSta(() =>
            {
                using var cadence = new CadenceFixture();
                cadence.Arm();
                cadence.Invalidate();
                cadence.Fixture.DrainDispatcher();
                var wait = cadence.Waits.Single();
                var failure = new InvalidOperationException("overlay hide");
                cadence.Fixture.Overlay.OnHide = () => throw failure;
                var prepared = cadence.Fixture.Service.PrepareForShutdownAsync();
                AggregateException? observed = null;
                try
                {
                    Assert.IsTrue(wait.Cancellation.IsCancellationRequested);
                    Assert.IsFalse(prepared.IsCompleted, "A failed Hide cannot release an entered layout owner.");
                    Assert.IsFalse(cadence.Fixture.Overlay.IsDisposed);
                    cadence.Fixture.Service.Start();
                    Assert.IsFalse(cadence.Fixture.Service.Active);
                }
                finally
                {
                    cadence.Fixture.Overlay.OnHide = null;
                    wait.Completion.TrySetCanceled();
                    cadence.Fixture.DrainDispatcher();
                    try { prepared.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
                    catch (AggregateException error) { observed = error; }
                }
                Assert.IsNotNull(observed);
                CollectionAssert.AreEqual(new Exception[] { failure }, observed!.Flatten().InnerExceptions.ToArray());
            });

        [TestMethod]
        public void ShutdownRejectsQueuedWindowFocusBeforeNativeReordering()
            => RunOnSta(() =>
            {
                using var fixture = new ServiceFixture(EnabledSettings(false));
                var focused = fixture.AddWindow("Queued focus owner");
                var maximized = fixture.AddWindow("Obstructing maximized owner");
                fixture.Service.Start();
                fixture.DrainMouseLayoutPipeline();
                Mock.Get(maximized).SetupGet(window => window.State).Returns(WindowState.Maximized);
                Mock.Get(maximized).SetupGet(window => window.CanReorder).Returns(true);
                var callback = CaptureShutdownOverlayCallback<WindowFocusChangedEventArgs>(fixture.Service, "OnWindowGotFocus");
                callback(focused, new WindowFocusChangedEventArgs(focused, true));
                fixture.Service.PrepareForShutdownAsync().GetAwaiter().GetResult();
                Mock.Get(maximized).Invocations.Clear();
                fixture.DrainDispatcher();
                Mock.Get(maximized).Verify(window => window.SendToBack(), Times.Never);
                Mock.Get(maximized).VerifyGet(window => window.State, Times.Never);
            });

        private static Action<object?, TNode> CaptureShutdownOverlayCallback<TNode>(TilingService service, string method)
            => typeof(TilingService).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Action<object?, TNode>>(service);
    }
}
