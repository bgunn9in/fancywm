using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using FancyWM.Layouts.Tiling;
using FancyWM.Utilities;
using FancyWM.ViewModels;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class WindowIconCacheTest
    {
        private sealed class Clock : TimeProvider
        {
            private long m_ticks = DateTimeOffset.UnixEpoch.Ticks;
            public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref m_ticks), TimeSpan.Zero);
            public override long TimestampFrequency => TimeSpan.TicksPerSecond;
            public override long GetTimestamp() => Interlocked.Read(ref m_ticks) - DateTimeOffset.UnixEpoch.Ticks;
            internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref m_ticks, elapsed.Ticks);
        }

        private sealed class Resolver : WindowExtensions.IIconResolver
        {
            internal Func<WindowExtensions.IconRequest, CancellationToken, ValueTask<WindowExtensions.IconIdentity>> Identify;
            internal Func<WindowExtensions.IconIdentity, CancellationToken, ValueTask<BitmapSource>> Load;
            internal Func<WindowExtensions.IconIdentity, bool> Current = _ => true;
            internal int Probes;
            internal int Loads;
            internal readonly ConcurrentQueue<int> Threads = new();

            internal Resolver()
            {
                Identify = (request, _) => ValueTask.FromResult(new WindowExtensions.IconIdentity(request.Handle, request.Handle, request.Handle));
                Load = (_, _) => ValueTask.FromResult(Pixel(42, frozen: false));
            }

            public ValueTask<WindowExtensions.IconIdentity> IdentifyAsync(WindowExtensions.IconRequest request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Probes);
                Threads.Enqueue(Environment.CurrentManagedThreadId);
                return Identify(request, cancellationToken);
            }

            public ValueTask<BitmapSource> LoadAsync(WindowExtensions.IconIdentity identity, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Loads);
                Threads.Enqueue(Environment.CurrentManagedThreadId);
                return Load(identity, cancellationToken);
            }

            public bool IsCurrent(WindowExtensions.IconIdentity identity)
            {
                Threads.Enqueue(Environment.CurrentManagedThreadId);
                return Current(identity);
            }
        }

        [TestMethod]
        public void IconBindingReturnsBeforeResolverAndPublishesFrozenResult()
        {
            RunSta(() =>
            {
                int uiThread = Environment.CurrentManagedThreadId;
                var resolver = new Resolver();
                var started = Signal();
                var release = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
                resolver.Load = (_, _) => { started.TrySetResult(); return new(release.Task); };
                using var cache = new WindowExtensions.IconCache(resolver);
                using var model = new TilingWindowViewModel(cache) { Node = new WindowNode(Window(1)) };
                var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                model.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(model.Icon) && model.Icon != null)
                    {
                        Assert.AreEqual(uiThread, Environment.CurrentManagedThreadId);
                        changed.TrySetResult();
                    }
                };

                Assert.IsNull(model.Icon, "A cold binding read returns the placeholder before any asynchronous result is applied.");
                Assert.AreEqual(Visibility.Collapsed, model.IconVisibility);
                PumpUntil(started.Task);
                bool uiCallback = false;
                Dispatcher.CurrentDispatcher.BeginInvoke(() => uiCallback = true);
                Drain();
                Assert.IsTrue(uiCallback, "The Dispatcher remains available while discovery is blocked.");
                Assert.IsFalse(changed.Task.IsCompleted);
                release.SetResult(Pixel(42));
                PumpUntil(changed.Task);
                Assert.IsNotNull(model.Icon);
                Assert.IsTrue(model.Icon.IsFrozen);
                Assert.AreEqual(Visibility.Visible, model.IconVisibility);
                Assert.AreEqual(1, resolver.Loads);
                foreach (int thread in resolver.Threads) { Assert.AreNotEqual(uiThread, thread); }
            });
        }

        [TestMethod]
        public void VisibilityBindingAlsoReturnsAnImmediatePlaceholder()
        {
            RunSta(() =>
            {
                using var cache = new WindowExtensions.IconCache(new Resolver());
                using var model = new TilingWindowViewModel(cache) { Node = new WindowNode(Window(1)) };
                Assert.AreEqual(Visibility.Collapsed, model.IconVisibility);
            });
        }

        [TestMethod]
        public void ExpiredNegativeIconReadRetriesForUnchangedNode()
        {
            RunSta(() =>
            {
                var clock = new Clock();
                var resolver = new Resolver();
                resolver.Load = (_, _) => ValueTask.FromResult(resolver.Loads == 1 ? null : Pixel(91));
                using var cache = new WindowExtensions.IconCache(resolver, clock);
                using var model = new TilingWindowViewModel(cache) { Node = new WindowNode(Window(1)) };
                var first = NextIconChange(model);
                Assert.IsNull(model.Icon);
                PumpUntil(first);
                for (int read = 0; read < 100; read++)
                {
                    Assert.IsNull(model.Icon);
                    Assert.AreEqual(Visibility.Collapsed, model.IconVisibility);
                }
                Assert.AreEqual(1, resolver.Loads);
                clock.Advance(TimeSpan.FromSeconds(10));
                var second = NextIconChange(model);
                Assert.IsNull(model.Icon);
                PumpUntil(second);
                Assert.AreEqual(91, Red(model.Icon));
                Assert.AreEqual(2, resolver.Loads);
            });
        }

        [TestMethod]
        public void NodeReplacementAndDisposeRejectLateCompletion()
        {
            RunSta(() =>
            {
                var oldStarted = Signal();
                var oldRelease = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
                var resolver = new Resolver();
                resolver.Load = (identity, _) => identity.Handle == 1
                    ? Hold(oldStarted, oldRelease.Task) : ValueTask.FromResult(Pixel(92));
                using var cache = new WindowExtensions.IconCache(resolver);
                using var model = new TilingWindowViewModel(cache) { Node = new WindowNode(Window(1)) };
                Assert.IsNull(model.Icon);
                PumpUntil(oldStarted.Task);
                model.Node = new WindowNode(Window(2));
                var current = NextIconChange(model);
                Assert.IsNull(model.Icon);
                PumpUntil(current);
                Assert.AreEqual(92, Red(model.Icon));
                oldRelease.SetResult(Pixel(41));
                cache.Dispose();
                PumpUntil(cache.Completion);
                Drain();
                Assert.AreEqual(92, Red(model.Icon));
                model.Dispose();
                model.Dispose();
                Assert.IsNull(model.Node);
                Assert.IsNull(model.Icon);
                Assert.AreEqual(Visibility.Collapsed, model.IconVisibility);
            });
        }

        [TestMethod]
        public void RemovedWindowClearsIconAndDoesNotRestartDiscovery()
        {
            RunSta(() =>
            {
                var resolver = new Resolver();
                var window = Window(1);
                using var cache = new WindowExtensions.IconCache(resolver);
                using var model = new TilingWindowViewModel(cache) { Node = new WindowNode(window) };
                var ready = NextIconChange(model);
                Assert.IsNull(model.Icon);
                PumpUntil(ready);
                Assert.IsNotNull(model.Icon);
                Mock.Get(window).Raise(value => value.Removed += null, new WindowChangedEventArgs(window));
                for (int read = 0; read < 100; read++) { Assert.IsNull(model.Icon); }
                Assert.AreEqual(1, resolver.Probes);
            });
        }

        [TestMethod]
        public void RemovedCallbackForPreviousNodeCannotInvalidateItsReplacement()
        {
            RunSta(() =>
            {
                var first = Window(1);
                using var cache = new WindowExtensions.IconCache(new Resolver());
                using var model = new TilingWindowViewModel(cache) { Node = new WindowNode(first) };
                var ready = NextIconChange(model);
                Assert.IsNull(model.Icon);
                PumpUntil(ready);
                Task.Run(() => Mock.Get(first).Raise(value => value.Removed += null,
                    new WindowChangedEventArgs(first))).GetAwaiter().GetResult();
                model.Node = new WindowNode(Window(2));
                var replacement = NextIconChange(model);
                Assert.IsNull(model.Icon);
                PumpUntil(replacement);
                Assert.IsNotNull(model.Icon);
                Assert.AreEqual(Visibility.Visible, model.IconVisibility);
            });
        }

        [TestMethod]
        public void MutableHandleReadRejectsThePreviousWrapperResult()
        {
            RunSta(() =>
            {
                int handle = 1;
                var window = Window(handle);
                Mock.Get(window).SetupGet(value => value.Handle).Returns(() => new IntPtr(handle));
                var resolver = new Resolver { Load = (identity, _) => ValueTask.FromResult(Pixel((byte)identity.Handle)) };
                using var cache = new WindowExtensions.IconCache(resolver);
                using var model = new TilingWindowViewModel(cache) { Node = new WindowNode(window) };
                var first = NextIconChange(model);
                Assert.IsNull(model.Icon);
                PumpUntil(first);
                Assert.AreEqual(1, Red(model.Icon));
                handle = 2;
                var second = NextIconChange(model);
                Assert.IsNull(model.Icon);
                PumpUntil(second);
                Assert.AreEqual(2, Red(model.Icon));
                Assert.AreEqual(2, resolver.Loads);
            });
        }

        [TestMethod]
        public async Task SameWindowRequestsShareOneAdmittedJob()
        {
            var started = Signal();
            var release = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
            var resolver = new Resolver { Load = (_, _) => Hold(started, release.Task) };
            using var cache = new WindowExtensions.IconCache(resolver);
            var window = Window(1);
            var requests = Enumerable.Range(0, 100).Select(_ => cache.GetAsync(window, CancellationToken.None)).ToArray();
            await Bound(started.Task);
            Assert.AreEqual(1, resolver.Probes);
            Assert.AreEqual(1, resolver.Loads);
            var image = Pixel(41);
            release.SetResult(image);
            var results = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10));
            foreach (var result in results) { Assert.AreSame(image, result.Image); }
            Assert.AreEqual(0, cache.PendingCount);
        }

        [TestMethod]
        public async Task SharedResourceFinishesAfterFirstObserverCancels()
        {
            var started = Signal();
            var identified = Signal();
            var release = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
            int probes = 0;
            var resolver = new Resolver
            {
                Identify = (request, _) =>
                {
                    if (Interlocked.Increment(ref probes) == 2) { identified.SetResult(); }
                    return ValueTask.FromResult(new WindowExtensions.IconIdentity(request.Handle, "process-42-generation-7-package-A", request.Handle));
                },
                Load = (_, _) => Hold(started, release.Task),
            };
            using var cache = new WindowExtensions.IconCache(resolver);
            using var firstCancellation = new CancellationTokenSource();
            var first = cache.GetAsync(Window(1), firstCancellation.Token);
            await Bound(started.Task);
            var second = cache.GetAsync(Window(2), CancellationToken.None);
            await Bound(identified.Task);
            firstCancellation.Cancel();
            await Cancelled(first);
            Assert.IsFalse(second.IsCompleted);
            var image = Pixel(66);
            release.SetResult(image);
            Assert.AreSame(image, (await second.WaitAsync(TimeSpan.FromSeconds(10))).Image);
            Assert.AreEqual(1, resolver.Loads);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task DisposeBoundsIgnoringNativeWorkAndCompletesAllObservers(bool blockLoad)
        {
            var entered = Signal();
            var release = Signal();
            int active = 0;
            var resolver = new Resolver();
            async ValueTask Wait()
            {
                if (Interlocked.Increment(ref active) == 2) { entered.TrySetResult(); }
                await release.Task.ConfigureAwait(false);
            }
            resolver.Identify = async (request, _) =>
            {
                if (!blockLoad) { await Wait(); }
                return new WindowExtensions.IconIdentity(request.Handle, request.Handle, request.Handle);
            };
            resolver.Load = async (_, _) =>
            {
                if (blockLoad) { await Wait(); }
                return Pixel(22);
            };
            using var cache = new WindowExtensions.IconCache(resolver, workerCount: 2, queueCapacity: 3);
            var windows = Enumerable.Range(1, 20).Select(Window).ToArray();
            var requests = new List<Task<WindowExtensions.IconResult>>
            {
                cache.GetAsync(windows[0], CancellationToken.None),
                cache.GetAsync(windows[1], CancellationToken.None),
            };
            await Bound(entered.Task);
            for (int index = 2; index < windows.Length; index++)
            {
                requests.Add(cache.GetAsync(windows[index], CancellationToken.None));
            }
            Assert.AreEqual(3, cache.PendingCount);
            cache.Dispose();
            cache.Dispose();
            await Bound(Task.WhenAll(requests.Select(Cancelled)));
            Assert.AreEqual(0, cache.PendingCount);
            Assert.AreEqual(0, cache.CacheCount);
            Assert.AreEqual(2, active);
            Assert.IsFalse(cache.Completion.IsCompleted, "An unresponsive call keeps its original bounded worker slot.");
            await Cancelled(cache.GetAsync(Window(100), CancellationToken.None));
            release.SetResult();
            await Bound(cache.Completion);
            Assert.AreEqual(2, resolver.Probes);
            Assert.AreEqual(blockLoad ? 2 : 0, resolver.Loads);
        }

        [TestMethod]
        public async Task QueueAdmissionCancellationDoesNotWaitForHungWorkers()
        {
            var entered = Signal();
            var release = Signal();
            var resolver = new Resolver
            {
                Identify = async (request, _) =>
                {
                    entered.TrySetResult();
                    await release.Task.ConfigureAwait(false);
                    return new WindowExtensions.IconIdentity(request.Handle, request.Handle, request.Handle);
                },
            };
            using var cache = new WindowExtensions.IconCache(resolver, workerCount: 1, queueCapacity: 1);
            using var cancellation = new CancellationTokenSource();
            var first = cache.GetAsync(Window(1), cancellation.Token);
            await Bound(entered.Task);
            var queued = cache.GetAsync(Window(2), cancellation.Token);
            var overflow = cache.GetAsync(Window(3), cancellation.Token);
            Assert.AreEqual(1, cache.PendingCount);
            cancellation.Cancel();
            await Bound(Task.WhenAll(new[] { first, queued, overflow }.Select(Cancelled)));
            Assert.AreEqual(0, cache.PendingCount);
            Assert.AreEqual(1, resolver.Probes);
            cache.Dispose();
            release.SetResult();
            await Bound(cache.Completion);
            Assert.AreEqual(0, resolver.Loads);
        }

        [TestMethod]
        public async Task CacheLimitAndOriginalNegativeExpiryArePreserved()
        {
            var clock = new Clock();
            var resolver = new Resolver();
            using var cache = new WindowExtensions.IconCache(resolver, clock, cacheCapacity: 3);
            for (int handle = 1; handle <= 5; handle++)
            {
                await cache.GetAsync(Window(handle), CancellationToken.None);
                Assert.IsTrue(cache.CacheCount <= 3);
            }
            Assert.AreEqual(5, resolver.Loads);
            await cache.GetAsync(Window(5), CancellationToken.None);
            Assert.AreEqual(5, resolver.Loads);
            await cache.GetAsync(Window(1), CancellationToken.None);
            Assert.AreEqual(6, resolver.Loads);

            resolver.Load = (_, _) => ValueTask.FromResult<BitmapSource>(null);
            var negative = await cache.GetAsync(Window(100), CancellationToken.None);
            clock.Advance(TimeSpan.FromSeconds(9));
            var laterObserver = await cache.GetAsync(Window(100), CancellationToken.None);
            Assert.AreEqual(negative.RetryAfter, laterObserver.RetryAfter);
            Assert.AreEqual(7, resolver.Loads);
            clock.Advance(TimeSpan.FromSeconds(1));
            resolver.Load = (_, _) => ValueTask.FromResult(Pixel(44));
            Assert.IsNotNull((await cache.GetAsync(Window(100), CancellationToken.None)).Image);
            Assert.AreEqual(8, resolver.Loads);
        }

        [TestMethod]
        public async Task DefaultCacheStopsAt256Entries()
        {
            var resolver = new Resolver();
            using var cache = new WindowExtensions.IconCache(resolver);
            for (int handle = 1; handle <= 300; handle++)
            {
                Assert.IsNotNull((await cache.GetAsync(Window(handle), CancellationToken.None)).Image);
            }
            Assert.AreEqual(256, cache.CacheCount);
            Assert.AreEqual(300, resolver.Loads);
        }

        [TestMethod]
        public async Task ReusedHandleAndChangedPackageGenerationRejectOldResults()
        {
            int generation = 1;
            var started = Signal();
            var oldRelease = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
            var resolver = new Resolver
            {
                Identify = (request, _) => ValueTask.FromResult(new WindowExtensions.IconIdentity(
                    request.Handle, ("package", generation), generation)),
                Load = (identity, _) => (int)identity.State == 1
                    ? Hold(started, oldRelease.Task) : ValueTask.FromResult(Pixel(88)),
                Current = identity => (int)identity.State == generation,
            };
            using var cache = new WindowExtensions.IconCache(resolver);
            var first = cache.GetAsync(Window(1), CancellationToken.None);
            await Bound(started.Task);
            generation = 2;
            var second = await cache.GetAsync(Window(1), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(88, Red(second.Image));
            oldRelease.SetResult(Pixel(11));
            Assert.IsNull((await first.WaitAsync(TimeSpan.FromSeconds(10))).Image);
            Assert.AreEqual(2, resolver.Loads);
            Assert.AreEqual(1, cache.CacheCount);
        }

        [TestMethod]
        public async Task SharedLoadFailureCompletesFollowersAndExpiresForRetry()
        {
            var clock = new Clock();
            var entered = Signal();
            var release = Signal();
            var identified = Signal();
            int probes = 0;
            var resolver = new Resolver
            {
                Identify = (request, _) =>
                {
                    if (Interlocked.Increment(ref probes) == 2) { identified.SetResult(); }
                    return ValueTask.FromResult(new WindowExtensions.IconIdentity(request.Handle, "shared", request.Handle));
                },
                Load = async (_, _) =>
                {
                    entered.TrySetResult();
                    await release.Task.ConfigureAwait(false);
                    throw new UnauthorizedAccessException("package denied");
                },
            };
            using var cache = new WindowExtensions.IconCache(resolver, clock);
            var first = cache.GetAsync(Window(1), CancellationToken.None);
            await Bound(entered.Task);
            var second = cache.GetAsync(Window(2), CancellationToken.None);
            await Bound(identified.Task);
            release.SetResult();
            var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(results.All(result => result.Image == null));
            Assert.AreEqual(1, resolver.Loads);
            clock.Advance(TimeSpan.FromSeconds(10));
            resolver.Load = (_, _) => ValueTask.FromResult(Pixel(48));
            Assert.AreEqual(48, Red((await cache.GetAsync(Window(2), CancellationToken.None)).Image));
            Assert.AreEqual(2, resolver.Loads);
        }

        [TestMethod]
        public void AnUnfrozenForeignThreadImageIsNeverPublishedOrCachedAsSuccess()
        {
            RunSta(() =>
            {
                var foreign = Pixel(55, frozen: false);
                var clock = new Clock();
                var resolver = new Resolver { Load = (_, _) => ValueTask.FromResult(foreign) };
                using var cache = new WindowExtensions.IconCache(resolver, clock);
                var first = cache.GetAsync(Window(1), CancellationToken.None);
                PumpUntil(first);
                Assert.IsNull(first.Result.Image);
                var second = cache.GetAsync(Window(1), CancellationToken.None);
                PumpUntil(second);
                Assert.IsNull(second.Result.Image);
                Assert.AreEqual(1, resolver.Loads);
                clock.Advance(TimeSpan.FromSeconds(10));
                resolver.Load = (_, _) => ValueTask.FromResult(Pixel(56));
                var retry = cache.GetAsync(Window(1), CancellationToken.None);
                PumpUntil(retry);
                Assert.AreEqual(56, Red(retry.Result.Image));
            });
        }

        [TestMethod]
        public async Task UnknownProcessGenerationDoesNotShareIconsAcrossWrappers()
        {
            var resolver = new Resolver
            {
                Identify = (request, _) => ValueTask.FromResult(new WindowExtensions.IconIdentity(request.Handle, null, request.Handle)),
                Load = (_, _) => ValueTask.FromResult(Pixel(41)),
            };
            using var cache = new WindowExtensions.IconCache(resolver);
            Assert.IsNotNull((await cache.GetAsync(Window(1), CancellationToken.None)).Image);
            Assert.IsNotNull((await cache.GetAsync(Window(1), CancellationToken.None)).Image);
            Assert.AreEqual(2, resolver.Loads);
            Assert.AreEqual(0, cache.CacheCount);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ProtectedProcessMetadataAndMissingPackagePreserveBorrowedClassIcon(bool missingPackage)
        {
            using var owned = (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
            var parent = new WindowExtensions.NativeWindowIdentity(0, 42, 1, null, null, null, owned.Handle);
            var state = new WindowExtensions.NativeIconState(parent, null,
                missingPackage ? Path.Combine(Path.GetTempPath(), "FancyWM-missing-" + Guid.NewGuid().ToString("N")) : null,
                missingPackage ? 1 : 0);
            var resolver = new WindowExtensions.NativeIconResolver();
            var image = await Task.Run(async () =>
            {
                var result = await resolver.LoadAsync(new WindowExtensions.IconIdentity(0, null, state), CancellationToken.None);
                result.Freeze();
                return result;
            });
            Assert.AreEqual(owned.Width, image.PixelWidth);
            Assert.AreEqual(owned.Height, image.PixelHeight);
            using var borrowedAgain = System.Drawing.Icon.FromHandle(owned.Handle).ToBitmap();
            Assert.AreEqual(owned.Width, borrowedAgain.Width);
        }

        [TestMethod]
        public async Task NativeShellWorkerScopePreservesFrozenPixelsAfterHandoff()
        {
            string iconPath = Path.Combine(Path.GetTempPath(), "FancyWM-shell-" + Guid.NewGuid().ToString("N") + ".ico");
            try
            {
                using (var output = File.Create(iconPath)) { System.Drawing.SystemIcons.Application.Save(output); }
                var parent = new WindowExtensions.NativeWindowIdentity(0, 42, 1, null, iconPath, null, 0);
                var identity = new WindowExtensions.IconIdentity(0, null,
                    new WindowExtensions.NativeIconState(parent, null, null, 0));
                var image = await Task.Run(async () =>
                {
                    var resolver = new WindowExtensions.NativeIconResolver();
                    BitmapSource last = null;
                    for (int iteration = 0; iteration < 20; iteration++)
                    {
                        last = await resolver.LoadAsync(identity, CancellationToken.None);
                        last.Freeze();
                        Assert.IsTrue(last.PixelWidth > 0);
                    }
                    return last;
                });
                using (File.Open(iconPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                File.Delete(iconPath);
                Assert.IsTrue(image.IsFrozen);
                var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
                image.CopyPixels(pixels, image.PixelWidth * 4, 0);
                Assert.IsTrue(pixels.Any(value => value != 0));
            }
            finally
            {
                if (File.Exists(iconPath)) { File.Delete(iconPath); }
            }
        }

        [TestMethod]
        public void ResolverFailureClearsPendingFlagAndCanRetry()
        {
            RunSta(() =>
            {
                var clock = new Clock();
                var resolver = new Resolver();
                resolver.Identify = (request, _) => resolver.Probes == 1
                    ? throw new IOException("unavailable package")
                    : ValueTask.FromResult(new WindowExtensions.IconIdentity(request.Handle, request.Handle, request.Handle));
                using var cache = new WindowExtensions.IconCache(resolver, clock);
                using var model = new TilingWindowViewModel(cache) { Node = new WindowNode(Window(1)) };
                var failure = NextIconChange(model);
                Assert.IsNull(model.Icon);
                PumpUntil(failure);
                Assert.IsNull(model.Icon);
                clock.Advance(TimeSpan.FromSeconds(10));
                var recovery = NextIconChange(model);
                Assert.IsNull(model.Icon);
                PumpUntil(recovery);
                Assert.IsNotNull(model.Icon);
                Assert.AreEqual(2, resolver.Probes);
            });
        }

        [TestMethod]
        public void DisposedModelsAndWindowsAreNotRetainedByUnresponsiveLoad()
        {
            RunSta(() =>
            {
                var started = Signal();
                var release = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
                var resolver = new Resolver { Load = (_, _) => Hold(started, release.Task) };
                using var cache = new WindowExtensions.IconCache(resolver);
                var references = CreateDisposedModel(cache, started.Task);
                Drain();
                for (int collect = 0; collect < 3; collect++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
                Assert.IsFalse(references.Model.IsAlive, "A late resolver must not retain a disposed view model.");
                Assert.IsFalse(references.Window.IsAlive, "A queued/active resolver owns identity data, not IWindow.");
                cache.Dispose();
                release.SetResult(Pixel(11));
                PumpUntil(cache.Completion);
            });
        }

        [TestMethod]
        public async Task RepeatedOwnerLifetimesFinishWorkersAndReleaseTheirCache()
        {
            for (int cycle = 0; cycle < 100; cycle++)
            {
                var cache = new WindowExtensions.IconCache(new Resolver());
                var result = await cache.GetAsync(Window(cycle + 1), CancellationToken.None);
                Assert.IsNotNull(result.Image);
                cache.Dispose();
                cache.Dispose();
                await Bound(cache.Completion);
                Assert.AreEqual(0, cache.PendingCount);
                Assert.AreEqual(0, cache.CacheCount);
            }
        }

        [TestMethod]
        public async Task PackageDecodeOwnsClosedStreamsAndFrozenPixels()
        {
            string directory = Path.Combine(Path.GetTempPath(), "FancyWM-icons-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string manifest = Path.Combine(directory, "AppxManifest.xml");
            string logo = Path.Combine(directory, "Logo.scale-100.png");
            try
            {
                File.WriteAllText(manifest, "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\"><Properties><Logo>Assets/Logo.png</Logo></Properties></Package>");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(Pixel(73)));
                using (var stream = File.Create(logo)) { encoder.Save(stream); }
                var result = await Task.Run(() => WindowExtensions.LoadPackageIcon(directory, CancellationToken.None));
                Assert.IsTrue(result.IsFrozen);
                using (File.Open(logo, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                File.Delete(logo);
                File.Delete(manifest);
                Assert.AreEqual(73, Red(result));
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                Assert.ThrowsException<OperationCanceledException>(() => WindowExtensions.LoadPackageIcon(directory, cancelled.Token));
            }
            finally
            {
                if (File.Exists(logo)) { File.Delete(logo); }
                if (File.Exists(manifest)) { File.Delete(manifest); }
                Directory.Delete(directory);
            }
        }

        [TestMethod]
        public void IconDiscoveryCounterScenario()
        {
            RunSta(() =>
            {
                int uiThread = Environment.CurrentManagedThreadId;
                foreach (int count in new[] { 1, 10, 25, 50 })
                {
                    var started = Signal();
                    var release = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var resolver = new Resolver
                    {
                        Identify = (request, _) => ValueTask.FromResult(new WindowExtensions.IconIdentity(
                            request.Handle, "package-A-process-42-generation-7", request.Handle)),
                        Load = (_, _) => Hold(started, release.Task),
                    };
                    using var cache = new WindowExtensions.IconCache(resolver);
                    var models = Enumerable.Range(1, count)
                        .Select(handle => new TilingWindowViewModel(cache) { Node = new WindowNode(Window(handle)) }).ToArray();
                    try
                    {
                        var applied = models.Select(NextIconChange).ToArray();
                        for (int read = 0; read < 100; read++)
                        {
                            foreach (var model in models)
                            {
                                Assert.IsNull(model.Icon);
                                Assert.AreEqual(Visibility.Collapsed, model.IconVisibility);
                            }
                        }
                        PumpUntil(started.Task);
                        release.SetResult(Pixel(86));
                        PumpUntil(Task.WhenAll(applied));
                        for (int read = 0; read < 100; read++)
                        {
                            foreach (var model in models)
                            {
                                var icon = model.Icon;
                                Assert.AreEqual(86, Red(icon));
                                Assert.IsTrue(icon.IsFrozen);
                                Assert.AreEqual(Visibility.Visible, model.IconVisibility);
                            }
                        }
                        int uiCalls = resolver.Threads.Count(thread => thread == uiThread);
                        Assert.AreEqual(0, uiCalls);
                        Assert.AreEqual(1, resolver.Loads);
                        Assert.AreEqual(count, resolver.Probes);
                        cache.Dispose();
                        PumpUntil(cache.Completion);
                        Assert.AreEqual(0, cache.CacheCount);
                        Console.WriteLine($"PERFCOUNTER icon-discovery-{count} ui-resolver-calls {uiCalls}");
                        Console.WriteLine($"PERFCOUNTER icon-discovery-{count} resource-loads {resolver.Loads}");
                        Console.WriteLine($"PERFCOUNTER icon-discovery-{count} identity-probes {resolver.Probes}");
                        Console.WriteLine($"PERFCOUNTER icon-discovery-{count} binding-reads {count * 400}");
                        Console.WriteLine($"PERFCOUNTER icon-discovery-{count} applied-icons {count}");
                        Console.WriteLine($"PERFCOUNTER icon-discovery-{count} retained-cache-entries {cache.CacheCount}");
                    }
                    finally
                    {
                        release.TrySetResult(Pixel(86));
                        foreach (var model in models) { model.Dispose(); }
                    }
                }
            });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static (WeakReference Model, WeakReference Window) CreateDisposedModel(WindowExtensions.IconCache cache, Task started)
        {
            var window = Window(1);
            var model = new TilingWindowViewModel(cache) { Node = new WindowNode(window) };
            Assert.IsNull(model.Icon);
            PumpUntil(started);
            var references = (new WeakReference(model), new WeakReference(window));
            model.Dispose();
            return references;
        }

        private static Task NextIconChange(TilingWindowViewModel model)
        {
            var completion = Signal();
            System.ComponentModel.PropertyChangedEventHandler handler = null;
            handler = (_, e) =>
            {
                if (e.PropertyName == nameof(model.Icon))
                {
                    model.PropertyChanged -= handler;
                    completion.TrySetResult();
                }
            };
            model.PropertyChanged += handler;
            return completion.Task;
        }

        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static ValueTask<BitmapSource> Hold(TaskCompletionSource started, Task<BitmapSource> release)
        {
            started.TrySetResult();
            return new(release);
        }

        private static async Task Cancelled(Task task)
        {
            try { await Bound(task); Assert.Fail("The cancelled/disposed request must not succeed."); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        private static Task Bound(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10));

        private static byte Red(ImageSource image)
        {
            var pixels = new byte[4];
            ((BitmapSource)image).CopyPixels(pixels, 4, 0);
            return pixels[2];
        }

        private static void Drain()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }

        private static IWindow Window(int handle)
        {
            var window = new Mock<IWindow>();
            window.SetupGet(value => value.Handle).Returns(new IntPtr(handle));
            window.SetupGet(value => value.Workspace).Returns(new Mock<IWorkspace>().Object);
            return window.Object;
        }

        private static BitmapSource Pixel(byte red, bool frozen = true)
        {
            var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 0, 0, red, 255 }, 4);
            if (frozen) { image.Freeze(); }
            return image;
        }

        private static void PumpUntil(Task task)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var timeoutRegistration = timeout.Token.Register(() => dispatcher.BeginInvoke(() => frame.Continue = false));
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            Assert.IsTrue(task.IsCompleted, "The controlled operation did not finish.");
            task.GetAwaiter().GetResult();
        }

        private static void RunSta(Action action)
        {
            Exception error = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception caught) { error = caught; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)), "STA test exceeded its bound.");
            if (error != null) { ExceptionDispatchInfo.Capture(error).Throw(); }
        }
    }
}
