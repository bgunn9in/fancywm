using System;
using System.Reactive.Disposables;
using System.Threading;
using System.Windows.Media;

using FancyWM.Utilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.DependencyInjection;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class FauxMicaProviderTest
    {
        public TestContext TestContext { get; set; }

        [TestMethod]
        public void ServiceProviderDisposalStopsResolvedMicaWorker()
        {
            for (int cycle = 0; cycle < 100; cycle++)
            {
                using var started = new ManualResetEventSlim();
                int subscriptions = 0;
                var services = new ServiceCollection();
                services.AddSingleton<IMicaProvider>(_ => new FauxMicaProvider(TimeSpan.FromHours(1),
                    () => { started.Set(); return new SystemWallpaper(); },
                    _ => DateTime.UnixEpoch, () => Colors.Transparent, _ => { },
                    subscribeChanges: _ =>
                    {
                        subscriptions++;
                        return Disposable.Create(() => Interlocked.Decrement(ref subscriptions));
                    }));
                FauxMicaProvider mica;
                using (var provider = services.BuildServiceProvider())
                {
                    mica = (FauxMicaProvider)provider.GetRequiredService<IMicaProvider>();
                    Assert.AreSame(mica, provider.GetRequiredService<IMicaProvider>());
                    Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)));
                    Assert.AreEqual(1, subscriptions);
                }
                Assert.IsFalse(mica.IsWorkerAlive);
                Assert.AreEqual(0, subscriptions);
            }
        }

        [TestMethod]
        public void WallpaperNotificationsInvalidateSameTimestampAndUnsubscribeAcrossCycles()
        {
            int active = 0;
            for (int cycle = 0; cycle < 100; cycle++)
            {
                Action invalidate = null;
                int captures = 0;
                using var provider = new FauxMicaProvider(TimeSpan.FromSeconds(1),
                    () => new SystemWallpaper { WallpaperPath = "same-image" }, _ => DateTime.UnixEpoch,
                    () => ++captures == 1 ? Colors.Red : Colors.Blue, exception => Assert.Fail(exception.ToString()),
                    startThread: false, subscribeChanges: callback =>
                    {
                        invalidate = callback;
                        active++;
                        return Disposable.Create(() => active--);
                    });
                Assert.AreEqual(1, active);
                provider.Refresh();
                Assert.AreEqual(Colors.Red, provider.PrimaryColor);
                provider.Refresh();
                Assert.AreEqual(1, captures);
                for (int notification = 0; notification < 1000; notification++) { invalidate(); }
                provider.Refresh();
                Assert.AreEqual(2, captures);
                Assert.AreEqual(Colors.Blue, provider.PrimaryColor);
                provider.Dispose();
                provider.Dispose();
                invalidate();
                provider.Refresh();
                Assert.AreEqual(2, captures);
                Assert.AreEqual(0, active);
            }
        }

        [TestMethod]
        public void InvalidationDuringCaptureRejectsStaleColorAndKeepsNextRefresh()
        {
            Action invalidate = null;
            int captures = 0;
            using var provider = new FauxMicaProvider(TimeSpan.FromSeconds(1),
                () => new SystemWallpaper { WallpaperPath = "same-image" }, _ => DateTime.UnixEpoch,
                () =>
                {
                    if (++captures == 1) { invalidate(); return Colors.Red; }
                    return Colors.Blue;
                }, exception => Assert.Fail(exception.ToString()), startThread: false,
                subscribeChanges: callback => { invalidate = callback; return Disposable.Empty; });
            int changes = 0;
            provider.PrimaryColorChanged += (_, _) => changes++;
            provider.Refresh();
            Assert.AreEqual(Colors.Transparent, provider.PrimaryColor);
            Assert.AreEqual(0, changes);
            provider.Refresh();
            Assert.AreEqual(Colors.Blue, provider.PrimaryColor);
            Assert.AreEqual(1, changes);
            provider.Refresh();
            Assert.AreEqual(2, captures);
        }

        [TestMethod]
        public void WorkerRetriesFailureOnlyAfterScheduledWait()
        {
            using var waiting = new ManualResetEventSlim();
            using var advance = new ManualResetEventSlim();
            int reads = 0;
            int waits = 0;
            int errors = 0;
            TimeSpan observedInterval = default;
            using var provider = new FauxMicaProvider(TimeSpan.FromHours(1), () =>
            {
                if (Interlocked.Increment(ref reads) == 1) { throw new InvalidOperationException("transient"); }
                return new SystemWallpaper { RGB = new byte[] { 10, 20, 30 } };
            }, _ => DateTime.UnixEpoch, () => Colors.Transparent,
                _ => Interlocked.Increment(ref errors), waitForNextCheck: interval =>
                {
                    observedInterval = interval;
                    if (Interlocked.Increment(ref waits) == 1)
                    {
                        waiting.Set();
                        return !advance.Wait(TimeSpan.FromSeconds(5));
                    }
                    return true;
                });
            try
            {
                Assert.IsTrue(waiting.Wait(TimeSpan.FromSeconds(5)));
                Assert.AreEqual(1, reads);
                Assert.AreEqual(1, errors);
                Assert.AreEqual(TimeSpan.FromHours(1), observedInterval);
            }
            finally
            {
                advance.Set();
            }
            Assert.IsTrue(SpinWait.SpinUntil(() => !provider.IsWorkerAlive, TimeSpan.FromSeconds(5)));
            Assert.AreEqual(2, reads);
            Assert.AreEqual(2, waits);
            Assert.AreEqual(1, errors);
            Assert.AreEqual(Color.FromRgb(10, 20, 30), provider.PrimaryColor);
        }

        [TestMethod]
        public void FailedSubscriptionDoesNotStartAWorkerOrLeaveAnActiveCallback()
        {
            Action staleCallback = null;
            int reads = 0;
            Assert.ThrowsException<InvalidOperationException>(() => new FauxMicaProvider(TimeSpan.FromSeconds(1),
                () => { Interlocked.Increment(ref reads); return new SystemWallpaper(); },
                _ => DateTime.UnixEpoch, () => Colors.Transparent, _ => { },
                subscribeChanges: callback =>
                {
                    staleCallback = callback;
                    throw new InvalidOperationException("subscription failure");
                }));
            staleCallback();
            Assert.AreEqual(0, reads);
        }

        [TestMethod]
        public void DisposeDuringBlockedCaptureSuppressesLateCompletionAndWorkerExits()
        {
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            int changes = 0;
            Exception failure = null;
            using var provider = new FauxMicaProvider(TimeSpan.FromMilliseconds(50),
                () => new SystemWallpaper { WallpaperPath = "fake-image" }, _ => DateTime.UnixEpoch,
                () =>
                {
                    started.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(5))) { throw new TimeoutException(); }
                    return Colors.Red;
                }, exception => failure = exception);
            provider.PrimaryColorChanged += (_, _) => Interlocked.Increment(ref changes);
            try
            {
                Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)));
                provider.Dispose();
                Assert.IsTrue(provider.IsWorkerAlive);
            }
            finally
            {
                release.Set();
            }
            Assert.IsTrue(SpinWait.SpinUntil(() => !provider.IsWorkerAlive, TimeSpan.FromSeconds(5)));
            Assert.AreEqual(0, changes);
            Assert.AreEqual(Colors.Transparent, provider.PrimaryColor);
            Assert.IsNull(failure);
        }

        [TestMethod]
        public void ZeroIntervalCannotStartAnIdleSpinLoop()
        {
            Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                new FauxMicaProvider(TimeSpan.Zero));
        }

        [TestMethod]
        public void SubMillisecondIntervalCannotStartAnIdleSpinLoop()
        {
            Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            {
                using var provider = new FauxMicaProvider(TimeSpan.FromTicks(1),
                    () => new SystemWallpaper(), _ => DateTime.UnixEpoch,
                    () => Colors.Transparent, _ => { }, startThread: false);
            });
        }

        [TestMethod]
        public void UnchangedSolidColorDoesNotPublishAndTransitionsRemainVisible()
        {
            var wallpaper = new SystemWallpaper { RGB = new byte[] { 10, 20, 30 } };
            using var provider = new FauxMicaProvider(TimeSpan.FromSeconds(1), () => wallpaper,
                _ => throw new AssertFailedException(), () => throw new AssertFailedException(),
                exception => Assert.Fail(exception.ToString()), startThread: false);
            int changes = 0;
            provider.PrimaryColorChanged += (_, _) => changes++;
            for (int tick = 0; tick < 600; tick++) { provider.Refresh(); }
            Assert.AreEqual(1, changes);
            TestContext.WriteLine("MICA_COUNTER,solid,refresh_calls,600");
            TestContext.WriteLine($"MICA_COUNTER,solid,color_notifications,{changes}");
            Assert.AreEqual(Color.FromRgb(10, 20, 30), provider.PrimaryColor);
            wallpaper = new SystemWallpaper { RGB = new byte[] { 30, 20, 10 } };
            provider.Refresh();
            Assert.AreEqual(2, changes);
            wallpaper = new SystemWallpaper();
            provider.Refresh();
            Assert.AreEqual(3, changes);
            Assert.AreEqual(Colors.Transparent, provider.PrimaryColor);
            provider.Refresh();
            Assert.AreEqual(3, changes);
        }

        [TestMethod]
        public void FailedCaptureRetriesAndTimestampChangesInvalidateOnlyTheCurrentImage()
        {
            var wallpaper = new SystemWallpaper { WallpaperPath = "fake-image-a" };
            var timestamp = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc);
            int captures = 0;
            using var provider = new FauxMicaProvider(TimeSpan.FromSeconds(1), () => wallpaper,
                _ => timestamp, () => ++captures == 1 ? throw new InvalidOperationException() : Colors.Blue,
                exception => Assert.Fail(exception.ToString()), startThread: false);
            int changes = 0;
            provider.PrimaryColorChanged += (_, _) => changes++;
            Assert.ThrowsException<InvalidOperationException>(() => provider.Refresh());
            provider.Refresh();
            Assert.AreEqual(2, captures);
            Assert.AreEqual(1, changes);
            for (int tick = 0; tick < 600; tick++) { provider.Refresh(); }
            Assert.AreEqual(2, captures);
            TestContext.WriteLine("MICA_COUNTER,image,stable_refresh_calls,600");
            TestContext.WriteLine($"MICA_COUNTER,image,total_capture_attempts,{captures}");
            TestContext.WriteLine("MICA_COUNTER,image,stable_capture_attempts,0");
            timestamp = timestamp.AddSeconds(-1);
            provider.Refresh();
            Assert.AreEqual(3, captures);
            Assert.AreEqual(1, changes);
            wallpaper = new SystemWallpaper { WallpaperPath = "fake-image-b" };
            provider.Refresh();
            Assert.AreEqual(4, captures);
            provider.Dispose();
            provider.Refresh();
            Assert.AreEqual(4, captures);
        }

        [TestMethod]
        public void WorkerIsInitializedBeforeFirstCallbackAndRepeatedDisposeStopsIt()
        {
            for (int cycle = 0; cycle < 100; cycle++)
            {
                using var observed = new ManualResetEventSlim();
                int reads = 0;
                Exception failure = null;
                using var provider = new FauxMicaProvider(TimeSpan.FromHours(1), () =>
                {
                    Interlocked.Increment(ref reads);
                    observed.Set();
                    return new SystemWallpaper();
                }, _ => throw new AssertFailedException(), () => throw new AssertFailedException(),
                exception => failure = exception);
                Assert.IsTrue(observed.Wait(TimeSpan.FromSeconds(5)), "Worker did not start");
                provider.Dispose();
                provider.Dispose();
                Assert.IsFalse(provider.IsWorkerAlive);
                Assert.AreEqual(1, reads);
                Assert.IsNull(failure);
            }
        }

        [TestMethod]
        public void DisposeDuringCaptureCannotPublishLateColor()
        {
            FauxMicaProvider provider = null;
            using (provider = new FauxMicaProvider(TimeSpan.FromSeconds(1),
                () => new SystemWallpaper { WallpaperPath = "fake-image" }, _ => DateTime.UnixEpoch,
                () => { provider.Dispose(); return Colors.Red; },
                exception => Assert.Fail(exception.ToString()), startThread: false))
            {
                int changes = 0;
                provider.PrimaryColorChanged += (_, _) => changes++;
                provider.Refresh();
                Assert.AreEqual(0, changes);
                Assert.AreEqual(Colors.Transparent, provider.PrimaryColor);
            }
        }
    }
}
