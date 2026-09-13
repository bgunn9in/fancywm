#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using FancyWM.ThemeEngine.Wpf;
using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class ThemeEngineManagerTest
    {
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

        [TestMethod]
        public async Task SupersededConversionAndQueuedApplyYieldOnlyLatestDefaultAndCustomCss()
        {
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var dispatcher = new QueuedDispatcher();
            using var fixture = new Fixture();
            fixture.Dispatch = dispatcher.Post;
            fixture.Parse = css =>
            {
                if (css == "D0\nA") { entered.TrySetResult(); Assert.IsTrue(release.Wait(Deadline)); }
            };
            var owner = fixture.Create();
            fixture.Custom = "A";
            owner.RequestReload(false);
            await entered.Task.WaitAsync(Deadline);
            fixture.Custom = "B";
            owner.RequestReload(false);
            release.Set();
            var staleApply = await dispatcher.Next();
            fixture.Custom = "C";
            fixture.Defaults.OnNext("D1");
            owner.RequestReload(false);
            staleApply.Run();
            var latestApply = await dispatcher.Next();
            latestApply.Run();
            await owner.Completion.WaitAsync(Deadline);

            CollectionAssert.AreEqual(new[] { "D0\nC0", "D1\nC" }, fixture.Applied.ToArray());
            Assert.AreEqual(1, fixture.MaxConcurrency);
            Assert.AreEqual("D1\nC", fixture.Current!.Keys.Single());
        }

        [DataTestMethod]
        [DataRow("convert")]
        [DataRow("dispatch")]
        [DataRow("apply")]
        public async Task FailedReloadRetainsGoodResourcesAndIdenticalRequestCanRetry(string stage)
        {
            using var fixture = new Fixture();
            var owner = fixture.Create();
            var previous = fixture.Current;
            fixture.Custom = "new";
            bool fail = true;
            fixture.Parse = _ => { if (stage == "convert" && fail) throw new FormatException("convert"); };
            fixture.Dispatch = (apply, token) =>
            {
                if (stage == "dispatch" && fail) throw new InvalidOperationException("dispatch");
                apply();
                return Task.CompletedTask;
            };
            fixture.Apply = resources =>
            {
                if (stage == "apply" && fail && resources.Keys.Single() == "D0\nnew")
                    throw new InvalidOperationException("apply");
            };
            owner.RequestReload(false);
            await owner.Completion.WaitAsync(Deadline);
            Assert.AreSame(previous, fixture.Current);
            Assert.AreEqual(1, fixture.Errors.Count);
            fail = false;
            owner.RequestReload(false);
            await owner.Completion.WaitAsync(Deadline);
            Assert.AreEqual("D0\nnew", fixture.Current!.Keys.Single());
            Assert.AreEqual(1, fixture.MaxConcurrency);
        }

        [TestMethod]
        public async Task ReadRetriesAreBoundedAsynchronousAndLaterRequestRecovers()
        {
            using var fixture = new Fixture();
            var attempts = Channel.CreateUnbounded<int>();
            bool fail = true;
            int count = 0;
            fixture.Read = (_, _) =>
            {
                attempts.Writer.TryWrite(Interlocked.Increment(ref count));
                return fail ? Task.FromException<string>(new IOException("locked")) : Task.FromResult("new");
            };
            var owner = fixture.Create();
            owner.RequestReload(false);
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                Assert.AreEqual(attempt, await attempts.Reader.ReadAsync().AsTask().WaitAsync(Deadline));
                if (attempt < 5)
                {
                    await fixture.Clock.WaitForSchedule(TimeSpan.FromMilliseconds(80));
                    fixture.Clock.Advance(TimeSpan.FromMilliseconds(80));
                }
            }
            await owner.Completion.WaitAsync(Deadline);
            Assert.AreEqual(1, fixture.Errors.Count);
            Assert.AreEqual("D0\nC0", fixture.Current!.Keys.Single());
            fail = false;
            owner.RequestReload(false);
            await owner.Completion.WaitAsync(Deadline);
            Assert.AreEqual(6, count);
            Assert.AreEqual("D0\nnew", fixture.Current!.Keys.Single());
        }

        [DataTestMethod]
        [DataRow("create")]
        [DataRow("change")]
        [DataRow("early-callback")]
        public async Task TimerFailureCannotStrandPendingReload(string stage)
        {
            using var fixture = new Fixture();
            var owner = fixture.Create();
            fixture.Custom = "new";
            if (stage == "create") fixture.Clock.FailCreate = true;
            else owner.RequestReload();
            if (stage == "change") fixture.Clock.FailChange = true;
            if (stage == "early-callback")
            {
                fixture.Clock.FailChange = true;
                fixture.Clock.FireEarly();
            }
            else owner.RequestReload();
            await owner.Completion.WaitAsync(Deadline);
            Assert.AreEqual("D0\nnew", fixture.Current!.Keys.Single());
            Assert.AreEqual(1, fixture.Errors.Count);
            fixture.Custom = "again";
            owner.RequestReload();
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(300));
            await owner.Completion.WaitAsync(Deadline);
            Assert.AreEqual("D0\nagain", fixture.Current!.Keys.Single());
        }

        [TestMethod]
        public async Task DebounceUsesMonotonicTimeAcrossUtcClockRollback()
        {
            using var fixture = new Fixture();
            var owner = fixture.Create();
            fixture.Custom = "new";
            owner.RequestReload();
            fixture.Clock.UtcOffset -= TimeSpan.FromHours(2);
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(300));
            await owner.Completion.WaitAsync(Deadline);
            Assert.AreEqual("D0\nnew", fixture.Current!.Keys.Single());
            Assert.AreEqual(1, fixture.Reads);
        }

        [TestMethod]
        public void SynchronousSubscriptionNotificationAndFailedWatcherSetupReleaseAcquiredResources()
        {
            int subscriptions = 0, reads = 0, applies = 0;
            var source = Observable.Create<string>(observer =>
            {
                subscriptions++;
                observer.OnNext("changed during subscribe");
                return Disposable.Create(() => subscriptions--);
            });
            Assert.ThrowsException<IOException>(() => new ThemeEngineManager("E:\\themes\\custom.css", "D0", "C0", source,
                (_, _) => throw new IOException("watcher setup"),
                (_, _) => { Interlocked.Increment(ref reads); return Task.FromResult("C0"); },
                css => Resources(css), _ => applies++, (apply, _) => { apply(); return Task.CompletedTask; },
                new ManualClock(), _ => { }));
            Assert.AreEqual(0, subscriptions);
            Assert.AreEqual(0, reads, "No worker may escape a constructor which has not acquired its watcher.");
            Assert.AreEqual(1, applies, "The initial dictionary is applied synchronously.");
        }

        [TestMethod]
        public async Task ThemeSwitchRejectsOldWatcherAndDefaultFileWriteConverges()
        {
            using var fixture = new Fixture();
            var owner = fixture.Create();
            var oldWatcher = fixture.Watchers.Single().Changed;
            fixture.Custom = "new";
            owner.SetTheme("second.css");
            await owner.Completion.WaitAsync(Deadline);
            int reads = fixture.Reads;
            oldWatcher();
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await owner.Completion.WaitAsync(Deadline);
            Assert.AreEqual(reads, fixture.Reads);
            Assert.AreEqual(1, fixture.ActiveWatchers);
            Assert.AreEqual("E:\\themes\\second.css", fixture.LastReadPath);

            owner.SetTheme("_default.css");
            await owner.Completion.WaitAsync(Deadline);
            fixture.OnWriteDefault = css =>
            {
                fixture.Custom = css;
                fixture.Watchers.Last().Changed();
            };
            fixture.Defaults.OnNext("D1");
            await fixture.Writes.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(300));
            await owner.Completion.WaitAsync(Deadline);
            Assert.AreEqual(1, fixture.DefaultWrites);
            Assert.AreEqual("D1\nD1", fixture.Current!.Keys.Single());
        }

        [TestMethod]
        public async Task ReentrantRequestDuringApplyPreservesLastInvalidation()
        {
            using var fixture = new Fixture();
            var owner = fixture.Create();
            fixture.Apply = resources =>
            {
                if (resources.Keys.Single() == "D0\nA")
                {
                    fixture.Custom = "B";
                    owner.RequestReload(false);
                }
            };
            fixture.Custom = "A";
            owner.RequestReload(false);
            await owner.Completion.WaitAsync(Deadline);
            CollectionAssert.AreEqual(new[] { "D0\nC0", "D0\nA", "D0\nB" }, fixture.Applied.ToArray());
            Assert.AreEqual(1, fixture.MaxConcurrency);
        }

        [TestMethod]
        public async Task ReentrantDisposeThenApplyFailureDoesNotRestoreOrRetainObsoleteResources()
        {
            using var fixture = new Fixture();
            var owner = fixture.Create();
            fixture.Apply = _ => { owner.Dispose(); throw new InvalidOperationException("disposed during apply"); };
            fixture.Custom = "new";
            owner.RequestReload(false);
            await owner.Completion.WaitAsync(Deadline);
            Assert.AreEqual(2, fixture.Applies, "No restoration may apply resources after reentrant disposal.");
            Assert.AreEqual(1, fixture.Errors.Count);
            Assert.AreEqual(0, fixture.RetainedResources);
            AssertOwnerSnapshotsReleased(owner);
        }

        [TestMethod]
        public async Task DisposeWhileCancellationCallbackFinishesWorkerDoesNotDisposeTokenSourceBeforeCancelReturns()
        {
            using var fixture = new Fixture();
            using var cancellationEntered = new ManualResetEventSlim();
            using var releaseCancellation = new ManualResetEventSlim();
            var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var readResult = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration registration = default;
            fixture.Read = (_, token) =>
            {
                registration = token.Register(() =>
                {
                    cancellationEntered.Set();
                    readResult.TrySetCanceled(token);
                    Assert.IsTrue(releaseCancellation.Wait(Deadline));
                });
                readEntered.TrySetResult();
                return readResult.Task;
            };
            var owner = fixture.Create();
            owner.RequestReload(false);
            await readEntered.Task.WaitAsync(Deadline);
            var completion = owner.Completion;
            var disposal = Task.Run(owner.Dispose);
            Assert.IsTrue(cancellationEntered.Wait(Deadline));
            try
            {
                await completion.WaitAsync(Deadline);
                Assert.IsFalse(disposal.IsCompleted, "The owner must not block the worker on cancellation teardown.");
            }
            finally { releaseCancellation.Set(); }
            await disposal.WaitAsync(Deadline);
            registration.Dispose();
            AssertOwnerSnapshotsReleased(owner);
            Assert.AreEqual(0, fixture.RetainedResources);
        }

        [TestMethod]
        public async Task DisposedQueuedApplyAndLateNotificationsCannotCreateNewWork()
        {
            var dispatcher = new QueuedDispatcher();
            using var fixture = new Fixture();
            fixture.Dispatch = dispatcher.Post;
            var owner = fixture.Create();
            fixture.Custom = "new";
            owner.RequestReload(false);
            var queued = await dispatcher.Next();
            var completion = owner.Completion;
            owner.Dispose();
            queued.Run();
            fixture.Watchers.Single().Changed();
            fixture.Defaults.OnNext("late");
            owner.RequestReload();
            fixture.Clock.Advance(TimeSpan.FromDays(1));
            await completion.WaitAsync(Deadline);
            Assert.AreEqual(1, fixture.Applies);
            Assert.AreEqual(1, fixture.Reads);
            Assert.AreEqual(0, fixture.RetainedResources);
            AssertOwnerSnapshotsReleased(owner);
        }

        [TestMethod]
        public void ThrowingTimerTeardownStillReleasesSnapshotsAndOwnerCompletion()
        {
            using var fixture = new Fixture();
            var owner = fixture.Create();
            owner.RequestReload();
            fixture.Clock.ThrowOnDispose = true;
            Assert.ThrowsException<InvalidOperationException>(owner.Dispose);
            Assert.IsTrue(owner.Completion.IsCompletedSuccessfully);
            Assert.AreEqual(0, fixture.RetainedResources);
            AssertOwnerSnapshotsReleased(owner);
            owner.Dispose();
        }

        [TestMethod]
        public async Task SnapshotResourceLookupMatchesProxyWithoutRegisteringNewPropertiesAcrossLifetimes()
        {
            await OnSta(() =>
            {
                string[] names = ["SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2",
                    "SystemAccentColorLight3", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3"];
                var proxy = new ResourceProxyHost();
                var snapshot = new FrameworkElement();
                for (int index = 0; index < names.Length; index++)
                {
                    var color = Color.FromRgb((byte)(20 + index), (byte)(80 + index), (byte)(140 + index));
                    proxy.Resources[names[index]] = color;
                    snapshot.Resources[names[index]] = color;
                }
                string expected = ThemeEngineManager.GetDefaultCss(proxy.BindResource, true, true);
                Assert.AreEqual(expected, ThemeEngineManager.GetDefaultCss(snapshot.FindResource, true, true));
                for (int lifetime = 0; lifetime < 100; lifetime++)
                {
                    var next = new FrameworkElement();
                    foreach (string name in names) next.Resources[name] = snapshot.Resources[name];
                    Assert.AreEqual(expected, ThemeEngineManager.GetDefaultCss(next.FindResource, true, true));
                }
            });
        }

        [TestMethod]
        public async Task RealDispatcherOwnsInitialAndReloadApplyWhileReadAndConversionUseWorker()
        {
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        int uiThread = Environment.CurrentManagedThreadId;
                        var conversions = new ConcurrentQueue<int>();
                        var applies = new ConcurrentQueue<int>();
                        int readThread = 0;
                        using var fixture = new Fixture();
                        fixture.Parse = _ => conversions.Enqueue(Environment.CurrentManagedThreadId);
                        fixture.Apply = _ => applies.Enqueue(Environment.CurrentManagedThreadId);
                        fixture.Read = (_, _) =>
                        {
                            readThread = Environment.CurrentManagedThreadId;
                            return Task.FromResult("new");
                        };
                        fixture.Dispatch = (apply, token) => dispatcher.InvokeAsync(apply, DispatcherPriority.Normal, token).Task;
                        var owner = fixture.Create();
                        Assert.AreEqual(1, fixture.Applies, "Startup resources must be available before the factory returns.");
                        owner.RequestReload(false);
                        await owner.Completion.WaitAsync(Deadline);
                        CollectionAssert.AreEqual(new[] { uiThread, uiThread }, applies.ToArray());
                        Assert.AreEqual(uiThread, conversions.First());
                        Assert.AreNotEqual(uiThread, conversions.Last());
                        Assert.AreNotEqual(uiThread, readThread);
                        Assert.AreEqual("D0\nnew", fixture.Current!.Keys.Single());
                        owner.Dispose();
                        Assert.AreEqual(0, fixture.RetainedResources);
                        finished.TrySetResult();
                    }
                    catch (Exception error) { finished.TrySetException(error); }
                    finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
                });
                Dispatcher.Run();
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            await finished.Task.WaitAsync(Deadline);
            Assert.IsTrue(thread.Join(Deadline), "The owned Dispatcher loop must exit.");
        }

        [TestMethod]
        public async Task RepeatedDisposeCancelsActiveReadWithoutRetainingWorkersOrLateCallbacks()
        {
            for (int lifecycle = 0; lifecycle < 100; lifecycle++)
            {
                using var fixture = new Fixture();
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var held = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.Read = (_, token) =>
                {
                    entered.TrySetResult();
                    return held.Task.WaitAsync(token);
                };
                var owner = fixture.Create();
                owner.RequestReload(false);
                await entered.Task.WaitAsync(Deadline);
                var completion = owner.Completion;
                owner.Dispose();
                owner.Dispose();
                await completion.WaitAsync(Deadline);
                held.TrySetResult("late");
                fixture.Watchers.Single().Changed();
                fixture.Defaults.OnNext("late");
                owner.RequestReload(false);
                Assert.AreEqual(1, fixture.Applies);
                Assert.AreEqual(1, fixture.Conversions);
                Assert.AreEqual(0, fixture.RetainedResources);
                AssertOwnerSnapshotsReleased(owner);
            }
        }

        [TestMethod]
        public async Task ThemeReloadCounterScenario()
        {
            using var fixture = new Fixture { RealConverter = true };
            var owner = fixture.Create();
            var initialResources = fixture.Current;
            for (int request = 0; request < 100; request++) owner.RequestReload();
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(300));
            await owner.Completion.WaitAsync(Deadline);
            Assert.AreEqual(1, fixture.Conversions);
            Assert.AreEqual(1, fixture.Applies);
            Assert.AreEqual(1, fixture.Reads);
            Assert.AreEqual(1, fixture.MaxConcurrency);
            Assert.AreSame(initialResources, fixture.Current);
            await OnSta(() =>
            {
                Assert.AreEqual(Colors.Red, initialResources!["panel-bar/color"].As<Color>());
                Assert.AreEqual(Colors.Blue, initialResources["panel-bar:hover/color"].As<Color>());
                Assert.AreEqual(new Thickness(2), initialResources["panel-bar/border-width"].As<Thickness>());
            });
            owner.Dispose();
            Assert.AreEqual(0, fixture.RetainedResources);
            int retained = 0;
            for (int lifecycle = 0; lifecycle < 100; lifecycle++)
            {
                using var cycle = new Fixture();
                var cycleOwner = cycle.Create();
                cycleOwner.RequestReload();
                cycleOwner.Dispose();
                cycleOwner.Dispose();
                cycle.Watchers.Single().Changed();
                cycle.Defaults.OnNext("late");
                cycle.Clock.Advance(TimeSpan.FromSeconds(1));
                await cycleOwner.Completion.WaitAsync(Deadline);
                retained += cycle.RetainedResources;
                AssertOwnerSnapshotsReleased(cycleOwner);
                Assert.AreEqual(1, cycle.Applies);
            }
            Assert.AreEqual(0, retained);
            Console.WriteLine("PERFCOUNTER theme-reload requests 100");
            Console.WriteLine($"PERFCOUNTER theme-reload conversions {fixture.Conversions}");
            Console.WriteLine($"PERFCOUNTER theme-reload applies {fixture.Applies}");
            Console.WriteLine($"PERFCOUNTER theme-reload max-concurrency {fixture.MaxConcurrency}");
            Console.WriteLine("PERFCOUNTER theme-reload lifecycles 100");
            Console.WriteLine($"PERFCOUNTER theme-reload retained-resources {retained}");
        }

        private static void AssertOwnerSnapshotsReleased(ThemeEngineManager owner)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Assert.IsNull(typeof(ThemeEngineManager).GetField("m_lastAppliedResources", flags)!.GetValue(owner));
            foreach (string field in new[] { "m_defaultCss", "m_lastAppliedCss", "m_lastWrittenDefaultCss" })
                Assert.AreEqual(string.Empty, typeof(ThemeEngineManager).GetField(field, flags)!.GetValue(owner));
        }
        private static IReadOnlyDictionary<string, CssValue> Resources(string css) => new Dictionary<string, CssValue> { [css] = null! };
        private static async Task OnSta(Action action)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { action(); done.TrySetResult(); }
                catch (Exception error) { done.TrySetException(error); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            await done.Task.WaitAsync(Deadline);
            Assert.IsTrue(thread.Join(Deadline), "The owned STA thread must exit.");
        }

        private sealed class Fixture : IDisposable
        {
            public readonly Subject<string> Defaults = new();
            public readonly ManualClock Clock = new();
            public readonly List<Watcher> Watchers = [];
            public readonly ConcurrentQueue<string> Applied = new();
            public readonly ConcurrentQueue<Exception> Errors = new();
            public readonly Channel<string> Writes = Channel.CreateUnbounded<string>();
            public string Custom = "C0";
            public string? LastReadPath;
            public Action<string>? Parse, OnWriteDefault;
            public Action<IReadOnlyDictionary<string, CssValue>>? Apply;
            public Func<string, CancellationToken, Task<string>>? Read;
            public Func<Action, CancellationToken, Task> Dispatch = (apply, token) =>
            { token.ThrowIfCancellationRequested(); apply(); return Task.CompletedTask; };
            public IReadOnlyDictionary<string, CssValue>? Current;
            public bool RealConverter;
            public int Subscriptions, ActiveWatchers, Reads, Conversions, Applies, MaxConcurrency, DefaultWrites;
            private int m_concurrency;
            private ThemeEngineManager? m_owner;
            public int RetainedResources => Subscriptions + ActiveWatchers + Clock.ActiveTimers;
            public ThemeEngineManager Create()
            {
                var converter = new CssToWpfResourceConverter();
                string defaults = RealConverter ? "panel-bar { color: red; border-style: solid; border-width: 2px; }" : "D0";
                if (RealConverter) Custom = "panel-bar:hover { color: blue; }";
                m_owner = new ThemeEngineManager("E:\\themes\\custom.css", defaults, Custom,
                    Observable.Create<string>(observer =>
                    {
                        Interlocked.Increment(ref Subscriptions);
                        var subscription = Defaults.Subscribe(observer);
                        return Disposable.Create(() => { subscription.Dispose(); Interlocked.Decrement(ref Subscriptions); });
                    }),
                    (path, changed) =>
                    {
                        Interlocked.Increment(ref ActiveWatchers);
                        var watcher = new Watcher(path, changed, () => Interlocked.Decrement(ref ActiveWatchers));
                        Watchers.Add(watcher);
                        return watcher;
                    },
                    (path, token) =>
                    {
                        Interlocked.Increment(ref Reads);
                        LastReadPath = path;
                        return Read?.Invoke(path, token) ?? Task.FromResult(Custom);
                    },
                    css =>
                    {
                        Interlocked.Increment(ref Conversions);
                        int concurrent = Interlocked.Increment(ref m_concurrency);
                        int maximum;
                        while ((maximum = Volatile.Read(ref MaxConcurrency)) < concurrent
                            && Interlocked.CompareExchange(ref MaxConcurrency, concurrent, maximum) != maximum) { }
                        try
                        {
                            Parse?.Invoke(css);
                            return RealConverter ? converter.Convert(ThemeEngineManager.HtmlTemplate, css) : Resources(css);
                        }
                        finally { Interlocked.Decrement(ref m_concurrency); }
                    },
                    resources =>
                    {
                        Interlocked.Increment(ref Applies);
                        Current = resources;
                        Apply?.Invoke(resources);
                        if (!RealConverter) Applied.Enqueue(resources.Keys.Single());
                    }, (apply, token) => Dispatch(apply, token), Clock, Errors.Enqueue,
                    (css, token) =>
                    {
                        Interlocked.Increment(ref DefaultWrites);
                        OnWriteDefault?.Invoke(css);
                        Writes.Writer.TryWrite(css);
                        return Task.CompletedTask;
                    });
                return m_owner;
            }
            public void Dispose() { m_owner?.Dispose(); Defaults.Dispose(); }
        }
        private sealed class Watcher(string path, Action changed, Action disposed) : IDisposable
        {
            public string Path { get; } = path;
            public Action Changed { get; } = changed;
            private Action? m_dispose = disposed;
            public void Dispose() => Interlocked.Exchange(ref m_dispose, null)?.Invoke();
        }
        private sealed class QueuedDispatcher
        {
            private readonly Channel<QueuedApply> m_queue = Channel.CreateUnbounded<QueuedApply>();
            public Task Post(Action apply, CancellationToken token)
            {
                var posted = new QueuedApply(apply, token);
                m_queue.Writer.TryWrite(posted);
                return posted.Task;
            }
            public Task<QueuedApply> Next() => m_queue.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        }
        private sealed class QueuedApply(Action apply, CancellationToken token)
        {
            private readonly TaskCompletionSource m_done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task Task => m_done.Task;
            public void Run()
            {
                try { apply(); m_done.TrySetResult(); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { m_done.TrySetCanceled(token); }
                catch (Exception error) { m_done.TrySetException(error); }
            }
        }
        private sealed class ManualClock : TimeProvider
        {
            private readonly object m_lock = new();
            private readonly List<ManualTimer> m_timers = [];
            private readonly Channel<TimeSpan> m_scheduled = Channel.CreateUnbounded<TimeSpan>();
            private long m_timestamp;
            public TimeSpan UtcOffset;
            public bool FailCreate, FailChange, ThrowOnDispose;
            public override long TimestampFrequency => TimeSpan.TicksPerSecond;
            public override long GetTimestamp() { lock (m_lock) return m_timestamp; }
            public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(GetTimestamp()) + UtcOffset;
            public int ActiveTimers { get { lock (m_lock) return m_timers.Count(timer => !timer.Disposed); } }
            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                lock (m_lock)
                {
                    if (FailCreate) { FailCreate = false; throw new InvalidOperationException("timer acquisition"); }
                    var timer = new ManualTimer(this, callback, state);
                    m_timers.Add(timer);
                    timer.Change(dueTime, period);
                    return timer;
                }
            }
            public void Advance(TimeSpan elapsed)
            {
                List<ManualTimer> due;
                lock (m_lock)
                {
                    m_timestamp += elapsed.Ticks;
                    due = m_timers.Where(timer => !timer.Disposed && timer.Due <= m_timestamp).ToList();
                    foreach (var timer in due) timer.Due = long.MaxValue;
                }
                foreach (var timer in due) timer.Callback(timer.State);
            }
            public void FireEarly()
            {
                ManualTimer timer;
                lock (m_lock) timer = m_timers.First(item => !item.Disposed);
                timer.Callback(timer.State);
            }
            public async Task WaitForSchedule(TimeSpan delay)
            {
                while (await m_scheduled.Reader.ReadAsync().AsTask().WaitAsync(Deadline) != delay) { }
            }
            private sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state) : ITimer
            {
                public readonly TimerCallback Callback = callback;
                public readonly object? State = state;
                public bool Disposed;
                public long Due = long.MaxValue;
                public bool Change(TimeSpan dueTime, TimeSpan period)
                {
                    lock (owner.m_lock)
                    {
                        if (Disposed) return false;
                        if (owner.FailChange) { owner.FailChange = false; return false; }
                        Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.m_timestamp + dueTime.Ticks;
                        if (dueTime != Timeout.InfiniteTimeSpan) owner.m_scheduled.Writer.TryWrite(dueTime);
                        return true;
                    }
                }
                public void Dispose()
                {
                    lock (owner.m_lock)
                    {
                        if (Disposed) return;
                        Disposed = true;
                        if (owner.ThrowOnDispose) { owner.ThrowOnDispose = false; throw new InvalidOperationException("timer teardown"); }
                    }
                }
                public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
            }
        }
    }
}
