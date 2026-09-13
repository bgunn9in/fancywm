#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

using FancyWM.Models;
using FancyWM.ViewModels;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Serilog;

namespace FancyWM.Tests.Models
{
    [TestClass]
    public class SettingsPersistenceLifetimeTest
    {
        [TestMethod]
        public async Task SaveWaitingForInitialReadPreservesInitialThenUpdatedNotification()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            var initial = new Settings { PanelHeight = 31 };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(initial));
            var clock = new HeldTimeProvider();
            var entity = new DelayedReadEntity(path, clock);
            var observations = new List<int>();
            var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = entity.Value.Subscribe(value =>
            {
                observations.Add(value.PanelHeight);
                if (value.PanelHeight == 73) updated.TrySetResult();
            }, error => updated.TrySetException(error));
            Task? save = null;
            try
            {
                await entity.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                save = entity.SaveAsync(value => value with { PanelHeight = 73 });
                Assert.IsFalse(save.IsCompleted);
                Assert.AreEqual(0, observations.Count);
                entity.ReleaseRead.TrySetResult();
                await updated.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await entity.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await save.WaitAsync(TimeSpan.FromSeconds(5));
                CollectionAssert.AreEqual(new[] { 31, 73 }, observations);
                Assert.AreEqual(73, JsonSerializer.Deserialize<Settings>(await File.ReadAllTextAsync(path))!.PanelHeight);
                Assert.AreEqual(1, entity.Writes);
                Assert.AreEqual(0, clock.ActiveTimers);
            }
            finally
            {
                entity.ReleaseRead.TrySetResult();
                clock.ReleaseAll();
                if (save != null)
                {
                    try { await save.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch { }
                }
                File.Delete(path);
                File.Delete(path + ".tmp");
                File.Delete(path + ".bak");
            }
        }

        [TestMethod]
        public async Task InitialValueCallbackCanSaveAndImmediatelyFlushBeforeReturning()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new Settings()));
            var clock = new HeldTimeProvider();
            var entity = new DelayedReadEntity(path, clock);
            var callbackCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? save = null;
            using var subscription = entity.Value.Take(1).Subscribe(_ =>
            {
                try
                {
                    save = entity.SaveAsync(value => value with { PanelHeight = 73, WindowPadding = 19 });
                    entity.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    Assert.IsTrue(save.IsCompletedSuccessfully,
                        "The first replay callback must admit its save before Flush returns");
                    var saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path))!;
                    Assert.AreEqual(73, saved.PanelHeight);
                    Assert.AreEqual(19, saved.WindowPadding);
                    callbackCompleted.TrySetResult();
                }
                catch (Exception error)
                {
                    callbackCompleted.TrySetException(error);
                }
            }, error => callbackCompleted.TrySetException(error));
            var savedNotification = entity.Value.Where(value => value.PanelHeight == 73).FirstAsync();
            var notificationTask = savedNotification.ToTask();
            try
            {
                await entity.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsFalse(callbackCompleted.Task.IsCompleted);
                entity.ReleaseRead.TrySetResult();
                await callbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                // Reentrant publication follows the initial callback after it returns.
                // The callback's own completion signal is not a publication barrier.
                await notificationTask.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreEqual(1, entity.Writes);
                Assert.AreEqual(0, clock.ActiveTimers);
                Assert.AreEqual(73, (await entity.Value.FirstAsync()).PanelHeight);
            }
            finally
            {
                entity.ReleaseRead.TrySetResult();
                // Let a failing candidate release its isolated work before deleting its file.
                clock.ReleaseAll();
                if (save != null)
                {
                    try { await save.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch { }
                }
                File.Delete(path);
                File.Delete(path + ".tmp");
                File.Delete(path + ".bak");
            }
        }

        [TestMethod]
        public async Task ViewModelCloseFlushesRealFileAcrossOneHundredStaLifetimes()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            var options = AppState.CreateSettingsJsonSerializerOptions();
            var original = new Settings
            {
                RemindToRateReview = false,
                CheckForUpdates = false,
                MasterSatelliteLayout = new MasterSatelliteLayoutSettings
                {
                    Enabled = true,
                    MasterRatio = 0.72,
                    DefaultMasterSide = MasterSide.Right,
                    DefaultSatelliteOrientation = SatelliteLayoutOrientation.Horizontal,
                    MaxSatellites = 7,
                },
            };
            const string comment = "/*preserved across repeated settings closes*/";
            await File.WriteAllTextAsync(path, comment + Environment.NewLine + JsonSerializer.Serialize(original, options));
            var entity = new ObservableJsonEntityWithCommentPreservation<Settings>(path, () => new Settings(), options);
            await entity.Value.FirstAsync();
            var tracked = new TrackingEntity(entity);
            bool completed = false;
            try
            {
                await RunOnStaAsync(() =>
                {
                    using var logger = new LoggerConfiguration().CreateLogger();
                    for (int cycle = 1; cycle <= 100; cycle++)
                    {
                        using var model = new SettingsViewModel(tracked, logger, () => Task.FromResult(false));
                        Assert.AreEqual(1, tracked.ActiveSubscriptions);
                        model.PanelHeight = 20 + cycle;
                        model.WindowPadding = cycle % 12 + 1;
                        model.ProcessIgnoreList = ["Taskmgr", "cycle-" + cycle];

                        // Deliberately do not pump the Dispatcher while Dispose waits for disk.
                        model.Dispose();
                        model.Dispose();
                        Assert.AreEqual(0, tracked.ActiveSubscriptions);
                        Assert.IsTrue(tracked.Saves.All(task => task.IsCompletedSuccessfully));
                        Assert.IsTrue(entity.FlushAsync().IsCompletedSuccessfully,
                            "Close must leave no unfinished persistence transaction");
                        int savesAtClose = tracked.Saves.Count;
                        model.PanelHeight = 1000 + cycle;
                        Assert.AreEqual(savesAtClose, tracked.Saves.Count, "Disposed views must not save late edits");

                        string contents = File.ReadAllText(path);
                        StringAssert.Contains(contents, comment);
                        var saved = JsonSerializer.Deserialize<Settings>(contents, options)!;
                        Assert.AreEqual(20 + cycle, saved.PanelHeight);
                        Assert.AreEqual(cycle % 12 + 1, saved.WindowPadding);
                        CollectionAssert.AreEqual(new[] { "Taskmgr", "cycle-" + cycle }, saved.ProcessIgnoreList);
                        Assert.AreEqual(original.MasterSatelliteLayout, saved.MasterSatelliteLayout);
                        Assert.IsFalse(saved.RemindToRateReview);
                        Assert.IsFalse(saved.CheckForUpdates);
                        Assert.IsFalse(File.Exists(path + ".tmp"));
                        Assert.IsFalse(File.Exists(path + ".bak"));
                    }
                });
                completed = true;
                Assert.AreEqual(0, tracked.ActiveSubscriptions);
                Assert.IsTrue(tracked.Saves.Count >= 100);
                Assert.IsTrue(tracked.Saves.All(task => task.IsCompletedSuccessfully));
                Assert.AreEqual(120, (await entity.Value.FirstAsync()).PanelHeight);
            }
            finally
            {
                // A watchdog failure preserves the isolated fixture for diagnosis; it
                // must not race cleanup against a potentially blocked test thread.
                if (completed)
                {
                    File.Delete(path);
                    File.Delete(path + ".tmp");
                    File.Delete(path + ".bak");
                }
            }
        }

        [TestMethod]
        public async Task ThrowingUpdaterPreservesReplayAndFileThenLaterSaveSucceeds()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            var options = AppState.CreateSettingsJsonSerializerOptions();
            string original = "/*updater failure must preserve these bytes*/\n" + JsonSerializer.Serialize(new Settings(), options);
            await File.WriteAllTextAsync(path, original);
            var entity = new ObservableJsonEntityWithCommentPreservation<Settings>(path, () => new Settings(), options);
            await entity.Value.FirstAsync();
            var observations = new List<int>();
            var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = entity.Subscribe(value =>
            {
                observations.Add(value.PanelHeight);
                if (value.PanelHeight == 42) updated.TrySetResult();
            });
            try
            {
                var failure = new InvalidOperationException("controlled updater failure");
                var observed = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => entity.SaveAsync(_ => throw failure));
                Assert.AreSame(failure, observed);
                Assert.IsTrue(entity.FlushAsync().IsCompletedSuccessfully);
                Assert.AreEqual(original, await File.ReadAllTextAsync(path));
                Assert.AreEqual(18, (await entity.Value.FirstAsync()).PanelHeight);
                CollectionAssert.AreEqual(new[] { 18 }, observations);

                var save = entity.SaveAsync(value => value with { PanelHeight = 42 });
                await Task.WhenAll(save, entity.FlushAsync(), updated.Task).WaitAsync(TimeSpan.FromSeconds(5));
                CollectionAssert.AreEqual(new[] { 18, 42 }, observations);
                string contents = await File.ReadAllTextAsync(path);
                StringAssert.Contains(contents, "/*updater failure must preserve these bytes*/");
                Assert.AreEqual(42, JsonSerializer.Deserialize<Settings>(contents, options)!.PanelHeight);
                Assert.IsFalse(File.Exists(path + ".tmp"));
                Assert.IsFalse(File.Exists(path + ".bak"));
            }
            finally
            {
                await entity.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
                File.Delete(path);
                File.Delete(path + ".tmp");
                File.Delete(path + ".bak");
            }
        }

        [TestMethod]
        public async Task PendingAutostartQueryDoesNotBlockEditsOrRestartForEveryEmission()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            var options = AppState.CreateSettingsJsonSerializerOptions();
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new Settings(), options));
            var entity = new ObservableJsonEntityWithCommentPreservation<Settings>(path, () => new Settings(), options);
            await entity.Value.FirstAsync();
            var tracked = new TrackingEntity(entity);
            bool completed = false;
            try
            {
                await RunOnStaAsync(() =>
                {
                    var dispatcher = Dispatcher.CurrentDispatcher;
                    // ExecuteSynchronously on the production continuation makes the
                    // worker's completion below a barrier for any late Dispatcher post.
                    var query = new TaskCompletionSource<bool>();
                    int queries = 0;
                    using var logger = new LoggerConfiguration().CreateLogger();
                    using var model = new SettingsViewModel(tracked, logger, () =>
                    {
                        queries++;
                        return query.Task;
                    });
                    Settings? latest = null;
                    using var replay = entity.Subscribe(value => latest = value);
                    try
                    {
                        for (int index = 1; index <= 100; index++) model.PanelHeight = 20 + index;
                        Assert.AreEqual(100, tracked.Saves.Count,
                            "Settings are editable while the independent autostart query is pending");
                        Assert.AreEqual(120, latest!.PanelHeight);
                        Assert.AreEqual(1, queries, "Echoed settings must not restart the operating-system query");
                        Assert.IsFalse(query.Task.IsCompleted);
                        model.Dispose();
                        Assert.IsTrue(tracked.Saves.All(task => task.IsCompletedSuccessfully));
                        Assert.AreEqual(120, JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), options)!.PanelHeight);

                        int posted = 0;
                        int notified = 0;
                        model.PropertyChanged += (_, _) => notified++;
                        DispatcherHookEventHandler onPosted = (_, _) => Interlocked.Increment(ref posted);
                        dispatcher.Hooks.OperationPosted += onPosted;
                        try
                        {
                            Task.Run(() => query.SetResult(true)).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                            Assert.AreEqual(0, posted, "A disposed view must not post a late autostart result");
                            Assert.AreEqual(0, notified);
                            Assert.IsFalse(model.RunsAtStartup);
                        }
                        finally
                        {
                            dispatcher.Hooks.OperationPosted -= onPosted;
                        }
                    }
                    finally
                    {
                        model.Dispose();
                        query.TrySetResult(false);
                    }
                });
                completed = true;
            }
            finally
            {
                if (completed)
                {
                    File.Delete(path);
                    File.Delete(path + ".tmp");
                    File.Delete(path + ".bak");
                }
            }
        }

        private static async Task RunOnStaAsync(Action action)
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                Exception? failure = null;
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                try { action(); }
                catch (Exception error) { failure = error; }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(null);
                    dispatcher.InvokeShutdown();
                }
                if (failure == null) completed.TrySetResult();
                else completed.TrySetException(failure);
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }

        private sealed class TrackingEntity(IObservableFileEntity<Settings> inner) : IObservableFileEntity<Settings>
        {
            public List<Task> Saves { get; } = [];
            public int ActiveSubscriptions { get; private set; }
            public string FullPath => inner.FullPath;
            public IObservable<Settings> Value => this;
            public IDisposable Subscribe(IObserver<Settings> observer)
            {
                ActiveSubscriptions++;
                var subscription = inner.Subscribe(observer);
                return Disposable.Create(() =>
                {
                    subscription.Dispose();
                    ActiveSubscriptions--;
                });
            }
            public Task SaveAsync(Func<Settings, Settings> update)
            {
                var save = inner.SaveAsync(update);
                Saves.Add(save);
                return save;
            }
            public Task FlushAsync() => inner.FlushAsync();
        }

        private sealed class DelayedReadEntity(string path, TimeProvider clock)
            : ObservableFileEntityBase<Settings>(path, () => new Settings(), clock)
        {
            public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Writes { get; private set; }
            protected override async Task<Settings> ReadAsync(Stream stream)
            {
                ReadStarted.TrySetResult();
                await ReleaseRead.Task.ConfigureAwait(false);
                return (await JsonSerializer.DeserializeAsync<Settings>(stream).ConfigureAwait(false))!;
            }
            protected override async Task WriteAsync(Stream stream, Settings value)
            {
                Writes++;
                await JsonSerializer.SerializeAsync(stream, value).ConfigureAwait(false);
            }
        }

        private sealed class HeldTimeProvider : TimeProvider
        {
            private readonly object m_lock = new();
            private readonly List<HeldTimer> m_timers = [];
            public int ActiveTimers { get { lock (m_lock) return m_timers.Count; } }
            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                Assert.AreEqual(TimeSpan.FromMilliseconds(100), dueTime);
                Assert.AreEqual(Timeout.InfiniteTimeSpan, period);
                var timer = new HeldTimer(this, callback, state);
                lock (m_lock) m_timers.Add(timer);
                return timer;
            }
            public void ReleaseAll()
            {
                HeldTimer[] timers;
                lock (m_lock) { timers = [.. m_timers]; m_timers.Clear(); }
                foreach (var timer in timers) timer.Callback(timer.State);
            }
            private sealed class HeldTimer(HeldTimeProvider owner, TimerCallback callback, object? state) : ITimer
            {
                public TimerCallback Callback { get; } = callback;
                public object? State { get; } = state;
                public bool Change(TimeSpan dueTime, TimeSpan period) => throw new AssertFailedException("Unexpected timer rearm");
                public void Dispose() { lock (owner.m_lock) owner.m_timers.Remove(this); }
                public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
            }
        }
    }
}
