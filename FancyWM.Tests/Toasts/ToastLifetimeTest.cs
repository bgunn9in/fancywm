#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Threading;

using FancyWM.ThemeEngine.Wpf;
using FancyWM.Toasts;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests.Toasts
{
    [TestClass]
    public class ToastLifetimeTest
    {
        [TestMethod]
        public Task EmptyWindowRemainsHiddenUntilFirstContentAndHidesAfterCancellation() => RunOnSta(() =>
        {
            using var fixture = new Fixture();
            Assert.AreEqual(1, fixture.Platform.Initializations);
            Assert.AreEqual(0, fixture.Platform.Visibility.Count, "An empty toast owner must not show its native surface.");
            using var cancellation = new CancellationTokenSource();
            var content = new object();
            fixture.Window.ShowToast(content, cancellation.Token);
            Assert.AreSame(content, fixture.Window.ToastItems.Single().Content);
            CollectionAssert.AreEqual(new[] { true }, fixture.Platform.Visibility);
            cancellation.Cancel();
            Drain();
            Assert.AreEqual(0, fixture.Window.ToastItems.Count);
            CollectionAssert.AreEqual(new[] { true, false }, fixture.Platform.Visibility);
        });

        [TestMethod]
        public Task AlreadyCancelledToastNeverReplacesCurrentContentOrShowsAnEmptySurface() => RunOnSta(() =>
        {
            using var fixture = new Fixture();
            using var current = new CancellationTokenSource();
            using var cancelled = new CancellationTokenSource();
            fixture.Window.ShowToast("current", current.Token);
            int calls = fixture.Platform.Visibility.Count;
            cancelled.Cancel();
            fixture.Window.ShowToast("stale", cancelled.Token);
            Drain();
            Assert.AreEqual("current", fixture.Window.ToastItems.Single().Content);
            Assert.AreEqual(calls, fixture.Platform.Visibility.Count);
        });

        [TestMethod]
        public Task CancellationFromWorkerDoesNotWaitForTheUiThread() => RunOnSta(() =>
        {
            using var fixture = new Fixture();
            using var cancellation = new CancellationTokenSource();
            using var posted = new ManualResetEventSlim();
            fixture.Window.ShowToast("first", cancellation.Token);
            var dispatcher = Dispatcher.CurrentDispatcher;
            void OnPosted(object? sender, DispatcherHookEventArgs args) => posted.Set();
            dispatcher.Hooks.OperationPosted += OnPosted;
            var worker = Task.Run(cancellation.Cancel);
            bool didPost = posted.Wait(TimeSpan.FromSeconds(5));
            bool completedWithoutUi = didPost && worker.Wait(TimeSpan.FromSeconds(1));
            dispatcher.Hooks.OperationPosted -= OnPosted;
            Drain();
            worker.GetAwaiter().GetResult();
            Assert.IsTrue(didPost, "The controlled cancellation must reach its queued UI callback.");
            Assert.IsTrue(completedWithoutUi, "Cancellation must return without synchronously waiting on the UI dispatcher.");
            Assert.AreEqual(0, fixture.Window.ToastItems.Count);
        });

        [TestMethod]
        public Task LateCancellationFromReplacedToastCannotRemoveTheLatestToast() => RunOnSta(() =>
        {
            using var fixture = new Fixture();
            using var previous = new CancellationTokenSource();
            using var latest = new CancellationTokenSource();
            fixture.Window.ShowToast("previous", previous.Token);
            previous.Cancel();
            fixture.Window.ShowToast("latest", latest.Token);
            int calls = fixture.Platform.Visibility.Count;
            Drain();
            Assert.AreEqual("latest", fixture.Window.ToastItems.Single().Content);
            Assert.AreEqual(calls, fixture.Platform.Visibility.Count);
            latest.Cancel();
            Drain();
            Assert.AreEqual(0, fixture.Window.ToastItems.Count);
            Assert.IsFalse(fixture.Platform.Visibility.Last());
        });

        [TestMethod]
        public Task PrimaryDisplaySwitchRebindsScalingExactlyOnceAndIgnoresOldCallbacks() => RunOnSta(() =>
        {
            using var fixture = new Fixture();
            var previousCallback = fixture.First.Handlers!;
            fixture.SwitchPrimary(fixture.Second);
            Drain();
            Assert.AreEqual(0, fixture.First.SubscriptionCount);
            Assert.AreEqual(1, fixture.Second.SubscriptionCount);
            Assert.AreSame(fixture.Second.Object, fixture.Platform.Positions.Last());
            int updates = fixture.Platform.Positions.Count;
            previousCallback(fixture.First.Object, new DisplayScalingChangedEventArgs(fixture.First.Object, 1.5, 1));
            Drain();
            Assert.AreEqual(updates, fixture.Platform.Positions.Count);
            fixture.Second.Scale();
            Drain();
            Assert.AreEqual(updates + 1, fixture.Platform.Positions.Count);
            fixture.SwitchPrimary(fixture.Second);
            Drain();
            Assert.AreEqual(1, fixture.Second.SubscriptionCount);
        });

        [TestMethod]
        public Task RepeatedCreateDisposeReleasesAllSubscriptionsAndIgnoresLateCallbacks() => RunOnSta(() =>
        {
            for (int cycle = 0; cycle < 100; cycle++)
            {
                using var fixture = new Fixture();
                using var cancellation = new CancellationTokenSource();
                using var service = new ToastService(fixture.Window);
                var pending = service.ShowToastAsync(cycle, cancellation.Token);
                Drain();
                var displayCallback = fixture.First.Handlers!;
                var primaryCallback = fixture.PrimaryHandlers!;
                Assert.IsInstanceOfType(fixture.Window, typeof(IDisposable));
                ((IDisposable)fixture.Window).Dispose();
                ((IDisposable)fixture.Window).Dispose();
                Complete(pending);
                Assert.AreEqual(1, fixture.Platform.Closes);
                Assert.AreEqual(0, fixture.First.SubscriptionCount);
                Assert.AreEqual(0, fixture.PrimarySubscriptionCount);
                Assert.AreEqual(0, fixture.Window.ToastItems.Count);
                int calls = fixture.Platform.Positions.Count + fixture.Platform.Visibility.Count;
                cancellation.Cancel();
                displayCallback(fixture.First.Object, new DisplayScalingChangedEventArgs(fixture.First.Object, 2, 1));
                primaryCallback(fixture.Manager.Object, new PrimaryDisplayChangedEventArgs(fixture.Second.Object, fixture.First.Object));
                fixture.Window.ShowToast("late", CancellationToken.None);
                Drain();
                Assert.AreEqual(calls, fixture.Platform.Positions.Count + fixture.Platform.Visibility.Count);
                Assert.AreEqual(0, fixture.Window.ToastItems.Count);
                Assert.AreEqual(0, fixture.Second.SubscriptionCount);
                Assert.AreEqual(IntPtr.Zero, new WindowInteropHelper(fixture.Window).Handle);
            }
        });

        [TestMethod]
        public Task ClosingWindowAlsoReleasesSubscriptionsAndCancellationOwnership() => RunOnSta(() =>
        {
            using var fixture = new Fixture();
            using var cancellation = new CancellationTokenSource();
            using var service = new ToastService(fixture.Window);
            var pending = service.ShowToastAsync("open", cancellation.Token);
            Drain();
            fixture.Window.Close();
            Complete(pending);
            Assert.AreEqual(0, fixture.First.SubscriptionCount);
            Assert.AreEqual(0, fixture.PrimarySubscriptionCount);
            Assert.AreEqual(0, fixture.Window.ToastItems.Count);
            int calls = fixture.Platform.Visibility.Count;
            cancellation.Cancel();
            Drain();
            Assert.AreEqual(calls, fixture.Platform.Visibility.Count);
        });

        [TestMethod]
        public Task ServiceDisposeCompletesUncancelledWaitersAndRejectsQueuedAndLateShows() => RunOnSta(() =>
        {
            using var fixture = new Fixture();
            var service = new ToastService(fixture.Window);
            var active = service.ShowToastAsync("active", CancellationToken.None);
            Drain();
            Assert.IsFalse(active.IsCompleted);
            var queued = service.ShowToastAsync("queued", CancellationToken.None);
            Assert.IsInstanceOfType(service, typeof(IDisposable));
            ((IDisposable)service).Dispose();
            ((IDisposable)service).Dispose();
            Complete(active);
            Complete(queued);
            Complete(service.ShowToastAsync("late", CancellationToken.None));
            Assert.AreEqual(1, fixture.Platform.Closes);
            Assert.AreEqual(0, fixture.Window.ToastItems.Count);
            Assert.AreEqual(0, fixture.PrimarySubscriptionCount);
        });

        [TestMethod]
        public Task ReplacementPreservesEachCallersCancellationLifetime() => RunOnSta(() =>
        {
            using var fixture = new Fixture();
            var service = new ToastService(fixture.Window);
            using var first = new CancellationTokenSource();
            using var second = new CancellationTokenSource();
            var firstTask = service.ShowToastAsync("first", first.Token);
            Drain();
            var secondTask = service.ShowToastAsync("second", second.Token);
            Drain();
            Assert.AreEqual("second", fixture.Window.ToastItems.Single().Content);
            Assert.IsFalse(firstTask.IsCompleted);
            Assert.IsFalse(secondTask.IsCompleted);
            first.Cancel();
            Complete(firstTask);
            Assert.IsFalse(secondTask.IsCompleted);
            Assert.AreEqual("second", fixture.Window.ToastItems.Single().Content);
            second.Cancel();
            Complete(secondTask);
            Drain();
            Assert.AreEqual(0, fixture.Window.ToastItems.Count);
            (service as IDisposable)?.Dispose();
        });

        [DataTestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        public Task ReentrantDisposeDoesNotResurrectContentsOrRegistration(int stage) => RunOnSta(() =>
        {
            using var fixture = new Fixture();
            using var cancellation = new CancellationTokenSource();
            bool invoked = false;
            void DisposeOnce()
            {
                if (invoked) { return; }
                invoked = true;
                fixture.Window.Dispose();
            }
            fixture.Window.ToastItems.CollectionChanged += (_, args) =>
            {
                if (stage == 0 && args.Action == NotifyCollectionChangedAction.Reset
                    || stage == 1 && args.Action == NotifyCollectionChangedAction.Add) { DisposeOnce(); }
            };
            if (stage == 2) { fixture.Platform.VisibilityCallback = DisposeOnce; }
            fixture.Window.ShowToast("outer", cancellation.Token);
            Assert.IsTrue(invoked);
            Assert.AreEqual(0, fixture.Window.ToastItems.Count);
            Assert.AreEqual(default(CancellationTokenRegistration), Registration(fixture.Window));
            Assert.AreEqual(1, fixture.Platform.Closes);
            int calls = fixture.Platform.Visibility.Count;
            cancellation.Cancel();
            Drain();
            Assert.AreEqual(calls, fixture.Platform.Visibility.Count);
        });

        [DataTestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        public Task ReentrantReplacementRetainsOnlyLatestContentsAndRegistration(int stage) => RunOnSta(() =>
        {
            using var fixture = new Fixture();
            using var outer = new CancellationTokenSource();
            using var latest = new CancellationTokenSource();
            bool invoked = false;
            void ReplaceOnce()
            {
                if (invoked) { return; }
                invoked = true;
                fixture.Window.ShowToast("latest", latest.Token);
            }
            fixture.Window.ToastItems.CollectionChanged += (_, args) =>
            {
                if (stage == 0 && args.Action == NotifyCollectionChangedAction.Reset
                    || stage == 1 && args.Action == NotifyCollectionChangedAction.Add) { ReplaceOnce(); }
            };
            if (stage == 2) { fixture.Platform.VisibilityCallback = ReplaceOnce; }
            fixture.Window.ShowToast("outer", outer.Token);
            Assert.IsTrue(invoked);
            Assert.AreEqual("latest", fixture.Window.ToastItems.Single().Content);
            int posted = 0;
            var dispatcher = Dispatcher.CurrentDispatcher;
            void CountPosted(object? sender, DispatcherHookEventArgs args) => posted++;
            dispatcher.Hooks.OperationPosted += CountPosted;
            outer.Cancel();
            dispatcher.Hooks.OperationPosted -= CountPosted;
            Assert.AreEqual(0, posted, "A replaced call must no longer retain a cancellation callback.");
            Drain();
            Assert.AreEqual("latest", fixture.Window.ToastItems.Single().Content);
            latest.Cancel();
            Drain();
            Assert.AreEqual(0, fixture.Window.ToastItems.Count);
            Assert.AreEqual(default(CancellationTokenRegistration), Registration(fixture.Window));
        });

        [TestMethod]
        public Task DisposeClosesNativeOwnerEvenWhenCollectionNotificationThrows() => RunOnSta(() =>
        {
            using var fixture = new Fixture();
            fixture.Window.ShowToast("content", CancellationToken.None);
            fixture.Window.ToastItems.CollectionChanged += (_, _) => throw new InvalidOperationException("Synthetic consumer failure");
            Assert.ThrowsException<InvalidOperationException>(fixture.Window.Dispose);
            Assert.AreEqual(1, fixture.Platform.Closes);
            Assert.AreEqual(0, fixture.PrimarySubscriptionCount);
            Assert.AreEqual(0, fixture.First.SubscriptionCount);
        });

        [TestMethod]
        public Task FailedNativeInitializationClosesBeforeAttachingDisplaySubscriptions() => RunOnSta(() =>
        {
            Fixture? observed = null;
            Assert.ThrowsException<InvalidOperationException>(() => new Fixture(fixture =>
            {
                observed = fixture;
                fixture.Platform.FailInitialization = true;
            }));
            Assert.IsNotNull(observed);
            Assert.AreEqual(1, observed!.Platform.Closes);
            Assert.AreEqual(0, observed!.PrimarySubscriptionCount);
            Assert.AreEqual(0, observed.First.SubscriptionCount);
            Assert.AreEqual(IntPtr.Zero, new WindowInteropHelper(observed.Platform.LastWindow!).Handle);
        });

        [TestMethod]
        public Task FailureAttachingSecondSubscriptionReleasesTheFirstAndCloses() => RunOnSta(() =>
        {
            Fixture? observed = null;
            Assert.ThrowsException<InvalidOperationException>(() => new Fixture(fixture =>
            {
                observed = fixture;
                fixture.First.FailSubscription = true;
            }));
            Assert.IsNotNull(observed);
            Assert.AreEqual(0, observed!.PrimarySubscriptionCount);
            Assert.AreEqual(0, observed.First.SubscriptionCount);
            Assert.AreEqual(1, observed.Platform.Closes);
            Assert.AreEqual(IntPtr.Zero, new WindowInteropHelper(observed.Platform.LastWindow!).Handle);
        });

        [TestMethod]
        public Task ConstructorCompensatesPrimarySubscriptionAfterEffectFailure() => RunOnSta(() =>
        {
            for (int cycle = 0; cycle < 100; cycle++)
            {
                Fixture? observed = null;
                var failure = new InvalidOperationException($"primary add failed after effect {cycle}");
                var actual = Assert.ThrowsException<InvalidOperationException>(() => new Fixture(fixture =>
                {
                    observed = fixture;
                    fixture.PrimarySubscriptionFailureAfterEffect = failure;
                }));
                Assert.AreSame(failure, actual);
                Assert.IsNotNull(observed);
                Assert.AreEqual(0, observed!.PrimarySubscriptionCount);
                Assert.AreEqual(0, observed.First.SubscriptionCount);
                Assert.AreEqual(1, observed.Platform.Closes);
            }
        });

        [TestMethod]
        public Task ConstructorCompensatesScalingSubscriptionAfterEffectFailure() => RunOnSta(() =>
        {
            for (int cycle = 0; cycle < 100; cycle++)
            {
                Fixture? observed = null;
                var failure = new InvalidOperationException($"scaling add failed after effect {cycle}");
                var actual = Assert.ThrowsException<InvalidOperationException>(() => new Fixture(fixture =>
                {
                    observed = fixture;
                    fixture.First.SubscriptionFailureAfterEffect = failure;
                }));
                Assert.AreSame(failure, actual);
                Assert.IsNotNull(observed);
                Assert.AreEqual(0, observed!.PrimarySubscriptionCount);
                Assert.AreEqual(0, observed.First.SubscriptionCount);
                Assert.AreEqual(1, observed.Platform.Closes);
            }
        });

        [TestMethod]
        public Task ConstructorPreservesPrimaryFailureWhenCleanupAlsoFails() => RunOnSta(() =>
        {
            Fixture? observed = null;
            var primary = new InvalidOperationException("primary add failure");
            var cleanup = new InvalidOperationException("cleanup failure");
            var actual = Assert.ThrowsException<InvalidOperationException>(() => new Fixture(fixture =>
            {
                observed = fixture;
                fixture.PrimarySubscriptionFailureAfterEffect = primary;
                fixture.Platform.CloseFailureAfterEffect = cleanup;
            }));
            Assert.AreSame(primary, actual, "Construction failure must not be replaced by a cleanup failure.");
            Assert.IsNotNull(observed);
            Assert.AreEqual(0, observed!.PrimarySubscriptionCount);
            Assert.AreEqual(0, observed.First.SubscriptionCount);
            Assert.AreEqual(1, observed.Platform.Closes);
        });

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public Task ConstructorDisposeDuringSubscriptionDoesNotAcquireTheRemainingTail(bool afterEffect) => RunOnSta(() =>
        {
            Fixture? observed = null;
            Assert.ThrowsException<ObjectDisposedException>(() => new Fixture(fixture =>
            {
                observed = fixture;
                fixture.DisposeDuringPrimarySubscriptionBeforeEffect = !afterEffect;
                fixture.DisposeDuringPrimarySubscriptionAfterEffect = afterEffect;
            }));
            Assert.IsNotNull(observed);
            Assert.AreEqual(0, observed!.PrimarySubscriptionCount);
            Assert.AreEqual(0, observed.First.SubscriptionCount, "Construction must not continue with the scaling subscription after reentrant Dispose.");
            Assert.AreEqual(1, observed.Platform.Closes);
        });

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public Task ConstructorPrimaryFailureAfterReentrantDisposePreservesFailure(bool afterEffect) => RunOnSta(() =>
        {
            Fixture? observed = null;
            var failure = new InvalidOperationException("primary add failed after reentrant Dispose");
            var actual = Assert.ThrowsException<InvalidOperationException>(() => new Fixture(fixture =>
            {
                observed = fixture;
                fixture.DisposeDuringPrimarySubscriptionBeforeEffect = !afterEffect;
                fixture.DisposeDuringPrimarySubscriptionAfterEffect = afterEffect;
                fixture.PrimarySubscriptionFailureAfterEffect = failure;
            }));
            Assert.AreSame(failure, actual);
            Assert.IsNotNull(observed);
            Assert.AreEqual(0, observed!.PrimarySubscriptionCount);
            Assert.AreEqual(0, observed.First.SubscriptionCount);
            Assert.AreEqual(1, observed.Platform.Closes);
        });

        [TestMethod]
        public Task ConstructorCompensatesCapturedScalingOwnerAfterPrimarySwitchAndDispose() => RunOnSta(() =>
        {
            Fixture? observed = null;
            Assert.ThrowsException<ObjectDisposedException>(() => new Fixture(fixture =>
            {
                observed = fixture;
                fixture.First.SubscriptionBeforeEffect = () =>
                {
                    fixture.SwitchPrimary(fixture.Second);
                    fixture.Platform.LastWindow!.Dispose();
                };
            }));
            Assert.IsNotNull(observed);
            Assert.AreEqual(0, observed!.PrimarySubscriptionCount);
            Assert.AreEqual(0, observed.First.SubscriptionCount, "Compensation must detach from the receiver captured before the primary switch.");
            Assert.AreEqual(0, observed.Second.SubscriptionCount);
            Assert.AreEqual(1, observed.Platform.Closes);
        });

        [TestMethod]
        public Task ConstructorPrimaryChangeDuringPrimarySubscriptionDoesNotDuplicateScalingOwnership() => RunOnSta(() =>
        {
            using var fixture = new Fixture(value => value.PrimaryDisplayChangeAfterEffect = value.Second);
            Assert.AreEqual(1, fixture.PrimarySubscriptionCount);
            Assert.AreEqual(0, fixture.First.SubscriptionCount);
            Assert.AreEqual(1, fixture.Second.SubscriptionCount);
            fixture.Window.Dispose();
            Assert.AreEqual(0, fixture.PrimarySubscriptionCount);
            Assert.AreEqual(0, fixture.First.SubscriptionCount);
            Assert.AreEqual(0, fixture.Second.SubscriptionCount);
            Assert.AreEqual(1, fixture.Platform.Closes);
        });

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public Task ConstructorCompensatesScalingSubscriptionAroundReentrantDispose(bool afterEffect) => RunOnSta(() =>
        {
            Fixture? observed = null;
            Assert.ThrowsException<ObjectDisposedException>(() => new Fixture(fixture =>
            {
                observed = fixture;
                if (afterEffect)
                {
                    fixture.First.SubscriptionAfterEffect = () => fixture.Platform.LastWindow!.Dispose();
                }
                else
                {
                    fixture.First.SubscriptionBeforeEffect = () => fixture.Platform.LastWindow!.Dispose();
                }
            }));
            Assert.IsNotNull(observed);
            Assert.AreEqual(0, observed!.PrimarySubscriptionCount);
            Assert.AreEqual(0, observed.First.SubscriptionCount);
            Assert.AreEqual(1, observed.Platform.Closes);
        });

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public Task ConstructorScalingAcquisitionPreservesLatestSubscriptionAcrossDisplayAba(bool afterEffect) => RunOnSta(() =>
        {
            using var fixture = new Fixture(value =>
            {
                Action switchAwayAndBack = () =>
                {
                    value.First.SubscriptionBeforeEffect = null;
                    value.First.SubscriptionAfterEffect = null;
                    value.SwitchPrimary(value.Second);
                    value.SwitchPrimary(value.First);
                };
                if (afterEffect) { value.First.SubscriptionAfterEffect = switchAwayAndBack; }
                else { value.First.SubscriptionBeforeEffect = switchAwayAndBack; }
            });
            Assert.AreEqual(1, fixture.PrimarySubscriptionCount);
            Assert.AreEqual(1, fixture.First.SubscriptionCount);
            Assert.AreEqual(0, fixture.Second.SubscriptionCount);
            int positions = fixture.Platform.Positions.Count;
            fixture.First.Scale();
            Assert.AreEqual(positions + 1, fixture.Platform.Positions.Count, "The latest same-display subscription must remain callable.");
            fixture.Window.Dispose();
            Assert.AreEqual(0, fixture.PrimarySubscriptionCount);
            Assert.AreEqual(0, fixture.First.SubscriptionCount);
            Assert.AreEqual(0, fixture.Second.SubscriptionCount);
        });

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public Task ConstructorScalingFailureAfterDisplayAbaPreservesFailure(bool afterEffect) => RunOnSta(() =>
        {
            Fixture? observed = null;
            var failure = new InvalidOperationException("scaling add failed after an ABA rebind");
            var actual = Assert.ThrowsException<InvalidOperationException>(() => new Fixture(value =>
            {
                observed = value;
                Action switchAwayAndBack = () =>
                {
                    value.First.SubscriptionBeforeEffect = null;
                    value.First.SubscriptionAfterEffect = null;
                    value.SwitchPrimary(value.Second);
                    value.SwitchPrimary(value.First);
                };
                if (afterEffect) { value.First.SubscriptionAfterEffect = switchAwayAndBack; }
                else { value.First.SubscriptionBeforeEffect = switchAwayAndBack; }
                value.First.SubscriptionFailureAfterEffect = failure;
            }));
            Assert.AreSame(failure, actual);
            Assert.IsNotNull(observed);
            Assert.AreEqual(0, observed!.PrimarySubscriptionCount);
            Assert.AreEqual(0, observed.First.SubscriptionCount);
            Assert.AreEqual(0, observed.Second.SubscriptionCount);
            Assert.AreEqual(1, observed.Platform.Closes);
        });

        [TestMethod]
        public Task DisposeReleasesOnlyOwnedDisplaySubscriptions() => RunOnSta(() =>
        {
            int primaryCalls = 0;
            int scalingCalls = 0;
            EventHandler<PrimaryDisplayChangedEventArgs> borrowedPrimary = (_, _) => primaryCalls++;
            EventHandler<DisplayScalingChangedEventArgs> borrowedScaling = (_, _) => scalingCalls++;
            using var fixture = new Fixture(value =>
            {
                value.PrimaryHandlers += borrowedPrimary;
                value.First.Handlers += borrowedScaling;
            });
            Assert.AreEqual(2, fixture.PrimarySubscriptionCount);
            Assert.AreEqual(2, fixture.First.SubscriptionCount);

            fixture.Window.Dispose();
            Assert.AreEqual(1, fixture.PrimarySubscriptionCount, "Disposal must preserve an independently owned primary-display handler.");
            Assert.AreEqual(1, fixture.First.SubscriptionCount, "Disposal must preserve an independently owned scaling handler.");

            fixture.SwitchPrimary(fixture.Second);
            fixture.First.Scale();
            Assert.AreEqual(1, primaryCalls);
            Assert.AreEqual(1, scalingCalls);
            Assert.AreEqual(0, fixture.Second.SubscriptionCount);
        });

        [TestMethod]
        public Task CancellationBeforeQueuedShowPreservesExistingContent() => RunOnSta(() =>
        {
            using var fixture = new Fixture();
            using var service = new ToastService(fixture.Window);
            using var current = new CancellationTokenSource();
            using var cancelled = new CancellationTokenSource();
            var active = service.ShowToastAsync("current", current.Token);
            Drain();
            int visibilityCalls = fixture.Platform.Visibility.Count;
            var queued = service.ShowToastAsync("cancelled", cancelled.Token);
            cancelled.Cancel();
            Complete(queued);
            Assert.AreEqual("current", fixture.Window.ToastItems.Single().Content);
            Assert.AreEqual(visibilityCalls, fixture.Platform.Visibility.Count);
            current.Cancel();
            Complete(active);
        });

        [TestMethod]
        public Task ServiceDisposedFromWorkerClosesOnWindowDispatcher() => RunOnSta(() =>
        {
            using var fixture = new Fixture();
            var service = new ToastService(fixture.Window);
            var pending = service.ShowToastAsync("content", CancellationToken.None);
            Drain();
            Complete(Task.Run(service.Dispose));
            Complete(pending);
            Assert.AreEqual(1, fixture.Platform.Closes);
            Assert.AreEqual(0, fixture.PrimarySubscriptionCount);
        });

        [TestMethod]
        public Task CancellationInNestedCollectionCallbackCannotAddTheCancelledToastAfterwards() => RunOnSta(() =>
        {
            using var fixture = new Fixture();
            using var cancellation = new CancellationTokenSource();
            bool invoked = false;
            fixture.Window.ToastItems.CollectionChanged += (_, args) =>
            {
                if (invoked || args.Action != NotifyCollectionChangedAction.Reset) { return; }
                invoked = true;
                cancellation.Cancel();
                Drain();
            };
            fixture.Window.ShowToast("cancelled before Add", cancellation.Token);
            Assert.IsTrue(invoked);
            Assert.AreEqual(0, fixture.Window.ToastItems.Count);
            Assert.AreEqual(default(CancellationTokenRegistration), Registration(fixture.Window));
            Assert.IsFalse(fixture.Platform.Visibility.Contains(true));
        });

        private static CancellationTokenRegistration Registration(ToastWindow window) =>
            (CancellationTokenRegistration)typeof(ToastWindow).GetField("m_cancellationRegistration", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;

        private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        private static void Complete(Task task)
        {
            if (!task.IsCompleted)
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                var frame = new DispatcherFrame();
                var timeout = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(5) };
                timeout.Tick += (_, _) => frame.Continue = false;
                _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
                timeout.Start();
                Dispatcher.PushFrame(frame);
                timeout.Stop();
            }
            Assert.IsTrue(task.IsCompleted, "The owned toast operation must complete.");
            task.GetAwaiter().GetResult();
        }

        private sealed class Fixture : IDisposable
        {
            public Display First { get; } = new();
            public Display Second { get; } = new();
            public Mock<IDisplayManager> Manager { get; } = new();
            public EventHandler<PrimaryDisplayChangedEventArgs>? PrimaryHandlers;
            public int PrimarySubscriptionCount => PrimaryHandlers?.GetInvocationList().Length ?? 0;
            public Exception? PrimarySubscriptionFailureAfterEffect;
            public bool DisposeDuringPrimarySubscriptionBeforeEffect;
            public bool DisposeDuringPrimarySubscriptionAfterEffect;
            public Display? PrimaryDisplayChangeAfterEffect;
            public Platform Platform { get; } = new();
            public ToastWindow Window { get; }
            private IDisplay m_primary;

            public Fixture(Action<Fixture>? configure = null)
            {
                m_primary = First.Object;
                Manager.SetupGet(manager => manager.PrimaryDisplay).Returns(() => m_primary);
                Manager.SetupAdd(manager => manager.PrimaryDisplayChanged += It.IsAny<EventHandler<PrimaryDisplayChangedEventArgs>>())
                    .Callback<EventHandler<PrimaryDisplayChangedEventArgs>>(handler =>
                    {
                        if (DisposeDuringPrimarySubscriptionBeforeEffect) { Platform.LastWindow!.Dispose(); }
                        PrimaryHandlers += handler;
                        if (PrimaryDisplayChangeAfterEffect != null) { SwitchPrimary(PrimaryDisplayChangeAfterEffect); }
                        if (DisposeDuringPrimarySubscriptionAfterEffect) { Platform.LastWindow!.Dispose(); }
                        if (PrimarySubscriptionFailureAfterEffect != null) { throw PrimarySubscriptionFailureAfterEffect; }
                    });
                Manager.SetupRemove(manager => manager.PrimaryDisplayChanged -= It.IsAny<EventHandler<PrimaryDisplayChangedEventArgs>>())
                    .Callback<EventHandler<PrimaryDisplayChangedEventArgs>>(handler => PrimaryHandlers -= handler);
                var workspace = new Mock<IWorkspace>();
                workspace.SetupGet(value => value.DisplayManager).Returns(Manager.Object);
                configure?.Invoke(this);
                var current = typeof(CssManager).GetField("_current", BindingFlags.NonPublic | BindingFlags.Static)!;
                var previous = current.GetValue(null);
                try
                {
                    current.SetValue(null, new Dictionary<string, CssValue>(new CssToWpfResourceConverter().Convert("<fixture></fixture>", "fixture { color: red; }")));
                    Window = new ToastWindow(workspace.Object, Platform);
                }
                finally { current.SetValue(null, previous); }
                Assert.AreEqual(IntPtr.Zero, new WindowInteropHelper(Window).Handle);
            }

            public void SwitchPrimary(Display display)
            {
                var old = m_primary;
                m_primary = display.Object;
                PrimaryHandlers?.Invoke(Manager.Object, new PrimaryDisplayChangedEventArgs(m_primary, old));
            }

            public void Dispose()
            {
                if (Window is IDisposable owner) { owner.Dispose(); }
                else { Window.Close(); }
                Drain();
                Assert.AreEqual(IntPtr.Zero, new WindowInteropHelper(Window).Handle);
            }
        }

        private sealed class Display
        {
            private readonly Mock<IDisplay> m_display = new();
            public IDisplay Object => m_display.Object;
            public EventHandler<DisplayScalingChangedEventArgs>? Handlers;
            public int SubscriptionCount => Handlers?.GetInvocationList().Length ?? 0;
            public bool FailSubscription;
            public Exception? SubscriptionFailureAfterEffect;
            public Action? SubscriptionBeforeEffect;
            public Action? SubscriptionAfterEffect;
            public Display()
            {
                m_display.SetupAdd(display => display.ScalingChanged += It.IsAny<EventHandler<DisplayScalingChangedEventArgs>>())
                    .Callback<EventHandler<DisplayScalingChangedEventArgs>>(handler =>
                    {
                        if (FailSubscription) { throw new InvalidOperationException("Synthetic subscription failure"); }
                        SubscriptionBeforeEffect?.Invoke();
                        Handlers += handler;
                        SubscriptionAfterEffect?.Invoke();
                        if (SubscriptionFailureAfterEffect != null) { throw SubscriptionFailureAfterEffect; }
                    });
                m_display.SetupRemove(display => display.ScalingChanged -= It.IsAny<EventHandler<DisplayScalingChangedEventArgs>>())
                    .Callback<EventHandler<DisplayScalingChangedEventArgs>>(handler => Handlers -= handler);
            }
            public void Scale() => Handlers?.Invoke(Object, new DisplayScalingChangedEventArgs(Object, 1.5, 1));
        }

        private sealed class Platform : IToastWindowPlatform
        {
            public int Initializations;
            public int Closes;
            public bool FailInitialization;
            public Exception? CloseFailureAfterEffect;
            public ToastWindow? LastWindow;
            public Action? VisibilityCallback;
            public List<bool> Visibility { get; } = [];
            public List<IDisplay> Positions { get; } = [];
            public void Initialize(ToastWindow window)
            {
                window.Dispatcher.VerifyAccess();
                Initializations++;
                LastWindow = window;
                if (FailInitialization) { throw new InvalidOperationException("Synthetic initialization failure"); }
            }
            public void SetPosition(ToastWindow window, IDisplay display) => Positions.Add(display);
            public void SetVisible(ToastWindow window, bool visible) { window.Dispatcher.VerifyAccess(); Visibility.Add(visible); VisibilityCallback?.Invoke(); }
            public void Close(ToastWindow window)
            {
                window.Dispatcher.VerifyAccess();
                Closes++;
                window.Close();
                if (CloseFailureAfterEffect != null) { throw CloseFailureAfterEffect; }
            }
        }

        private static async Task RunOnSta(Action action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { action(); completion.SetResult(); }
                catch (Exception exception) { completion.SetException(exception); }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)), "The owned STA worker must exit."); }
        }
    }
}
