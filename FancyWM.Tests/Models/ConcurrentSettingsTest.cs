#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using FancyWM.Models;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Models
{
    [TestClass]
    public class ConcurrentSettingsTest
    {
        [TestMethod]
        public async Task FixedWindowPublishesImmediatelyAndCannotBeExtendedByEdits()
        {
            using var fixture = new TimedFixture();
            var values = new List<int>();
            await fixture.Entity.Value.FirstAsync();
            using var subscription = fixture.Entity.Subscribe(value => values.Add(value.PanelHeight));
            var saves = new List<Task>();
            for (int i = 1; i <= 100; i++)
            {
                int height = i;
                saves.Add(fixture.Entity.SaveAsync(value => value with { PanelHeight = height }));
                if (i < 100) fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
            }
            Assert.AreEqual(101, values.Count);
            Assert.AreEqual(100, values[^1]);
            Assert.AreEqual(0, fixture.Entity.Writes);
            Assert.AreEqual(1, fixture.Clock.ActiveTimers);
            Assert.IsTrue(saves.All(save => ReferenceEquals(saves[0], save)),
                "A delayed batch must not retain a Task/continuation for every field update.");
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
            await Task.WhenAll(saves).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, fixture.Entity.Writes);
            Assert.AreEqual(100, fixture.Read().PanelHeight);
            Assert.AreEqual(0, fixture.Clock.ActiveTimers);
        }

        [TestMethod]
        public async Task FlushBypassesDelayAndRepeatedLifecycleLeavesNoTimer()
        {
            using var fixture = new TimedFixture();
            await fixture.Entity.Value.FirstAsync();
            for (int cycle = 1; cycle <= 100; cycle++)
            {
                var save = fixture.Entity.SaveAsync(value => value with { PanelHeight = cycle });
                var flush = fixture.Entity.FlushAsync();
                await Task.WhenAll(save, flush).WaitAsync(TimeSpan.FromSeconds(5));
                await fixture.Entity.FlushAsync();
                Assert.AreEqual(cycle, fixture.Read().PanelHeight);
                Assert.AreEqual(cycle, fixture.Entity.Writes);
                Assert.AreEqual(0, fixture.Clock.ActiveTimers);
            }
            fixture.Clock.Advance(TimeSpan.FromMinutes(10));
            Assert.AreEqual(100, fixture.Entity.Writes);
        }

        [TestMethod]
        public async Task FailedWriteKeepsGoodFileAndFlushRetriesLatestAcceptedState()
        {
            using var fixture = new TimedFixture();
            await fixture.Entity.Value.FirstAsync();
            fixture.Entity.FailNextWrite = true;
            var save = fixture.Entity.SaveAsync(value => value with { PanelHeight = 77 });
            var flush = fixture.Entity.FlushAsync();
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => save);
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => flush);
            Assert.AreEqual(18, fixture.Read().PanelHeight);
            Assert.AreEqual(77, (await fixture.Entity.Value.FirstAsync()).PanelHeight);
            Assert.IsFalse(File.Exists(fixture.Path + ".tmp"));
            Assert.AreEqual(0, fixture.Clock.ActiveTimers);
            await fixture.Entity.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(77, fixture.Read().PanelHeight);
            Assert.AreEqual(2, fixture.Entity.Writes);
            Assert.IsFalse(File.Exists(fixture.Path + ".bak"));
        }

        [TestMethod]
        public async Task TimerAcquisitionFailureCompletesSaveAndFlushCanRetry()
        {
            using var fixture = new TimedFixture();
            await fixture.Entity.Value.FirstAsync();
            fixture.Clock.FailNextTimer = true;
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                fixture.Entity.SaveAsync(value => value with { PanelHeight = 52 }));
            Assert.AreEqual(0, fixture.Entity.Writes);
            await fixture.Entity.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(52, fixture.Read().PanelHeight);
            Assert.AreEqual(1, fixture.Entity.Writes);
            Assert.AreEqual(0, fixture.Clock.ActiveTimers);
        }

        [TestMethod]
        public async Task ReentrantAndConcurrentUpdatesMergeWithoutStaleNotifications()
        {
            using var fixture = new TimedFixture();
            await fixture.Entity.Value.FirstAsync();
            Task reentrant = Task.CompletedTask;
            using var firstObserver = fixture.Entity.Subscribe(value =>
            {
                if (value.PanelHeight == 42 && value.WindowPadding == 8)
                    reentrant = fixture.Entity.SaveAsync(current => current with { WindowPadding = 9 });
            });
            var observations = new List<(int, int)>();
            using var secondObserver = fixture.Entity.Subscribe(value => observations.Add((value.PanelHeight, value.WindowPadding)));
            var save = fixture.Entity.SaveAsync(value => value with { PanelHeight = 42 });
            CollectionAssert.AreEqual(new[] { (18, 8), (42, 8), (42, 9) }, observations);
            var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int admissionCount = 0;
            var concurrent = Enumerable.Range(0, 100).Select(_ => Task.Run(async () =>
                await fixture.Entity.SaveAsync(value =>
                {
                    if (Interlocked.Increment(ref admissionCount) == 100) admitted.TrySetResult();
                    return value with { WindowPadding = value.WindowPadding + 1 };
                }))).ToArray();
            await admitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Entity.FlushAsync();
            await Task.WhenAll(concurrent.Append(save).Append(reentrant));
            await fixture.Entity.FlushAsync();
            Assert.AreEqual(109, fixture.Read().WindowPadding);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task FlushDuringWriteWaitsForLatestAndFailureKeepsPendingFields(bool failFirst)
        {
            using var fixture = new TimedFixture();
            await fixture.Entity.Value.FirstAsync();
            var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Entity.BeforeWrite = async count =>
            {
                if (count == 1)
                {
                    firstStarted.TrySetResult();
                    await firstRelease.Task.ConfigureAwait(false);
                    if (failFirst) throw new IOException("first transaction failed");
                }
                else
                {
                    secondStarted.TrySetResult();
                    await secondRelease.Task.ConfigureAwait(false);
                }
            };
            Task first = fixture.Entity.SaveAsync(value => value with { PanelHeight = 22 });
            Task firstFlush = fixture.Entity.FlushAsync();
            Task second = Task.CompletedTask;
            Task finalFlush = Task.CompletedTask;
            try
            {
                await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                second = fixture.Entity.SaveAsync(value => value with { WindowPadding = 13 });
                finalFlush = fixture.Entity.FlushAsync();
                Assert.IsFalse(finalFlush.IsCompleted);
                firstRelease.TrySetResult();
                await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsFalse(finalFlush.IsCompleted);
                secondRelease.TrySetResult();
                if (failFirst)
                {
                    await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => first);
                    await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => firstFlush);
                }
                else await Task.WhenAll(first, firstFlush);
                await Task.WhenAll(second, finalFlush).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreEqual(22, fixture.Read().PanelHeight);
                Assert.AreEqual(13, fixture.Read().WindowPadding);
                Assert.AreEqual(2, fixture.Entity.Writes);
                Assert.AreEqual(0, fixture.Clock.ActiveTimers);
            }
            finally
            {
                firstRelease.TrySetResult();
                secondRelease.TrySetResult();
                try { await Task.WhenAll(first, firstFlush, second, finalFlush); }
                catch (InvalidOperationException) when (failFirst) { }
            }
        }

        [TestMethod]
        public async Task MalformedSettingsRecoverValidJsonAndPreserveTheOriginal()
        {
            string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(directory);
            string path = System.IO.Path.Combine(directory, "settings.json");
            const string malformed = "{\"PanelHeight\":";
            try
            {
                await File.WriteAllTextAsync(path, malformed);
                var entity = new ObservableJsonEntityWithCommentPreservation<Settings>(path, () => new Settings { PanelHeight = 33 });
                Assert.AreEqual(33, (await entity.Value.FirstAsync()).PanelHeight);
                Assert.AreEqual(33, JsonSerializer.Deserialize<Settings>(await File.ReadAllTextAsync(path)).PanelHeight);
                string[] backups = Directory.GetFiles(directory, "settings.json.*.bak");
                Assert.AreEqual(1, backups.Length);
                Assert.AreEqual(malformed, await File.ReadAllTextAsync(backups[0]));
            }
            finally
            {
                foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
                Directory.Delete(directory);
            }
        }

        [TestMethod]
        public async Task DefaultFileIsWrittenAndUnrecognizedReadFailureTerminates()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".json");
            try
            {
                var entity = new ObservableJsonEntity<Settings>(path, () => new Settings { PanelHeight = 33 });
                Assert.AreEqual(33, (await entity.Value.FirstAsync()).PanelHeight);
                Assert.AreEqual(33, JsonSerializer.Deserialize<Settings>(await File.ReadAllTextAsync(path)).PanelHeight);
                var failing = new ReadFailureEntity(path);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () => await failing.Value.FirstAsync());
            }
            finally { File.Delete(path); }
        }

        private sealed class ReadFailureEntity(string path) : ObservableFileEntityBase<Settings>(path, () => new Settings())
        {
            protected override Task<Settings> ReadAsync(Stream stream) => throw new InvalidOperationException("controlled read failure");
            protected override Task WriteAsync(Stream stream, Settings value) => throw new AssertFailedException("Unexpected recovery write");
        }

        private sealed class TimedFixture : IDisposable
        {
            public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".json");
            public ManualTimeProvider Clock { get; } = new();
            public TimedEntity Entity { get; }
            public TimedFixture()
            {
                File.WriteAllText(Path, JsonSerializer.Serialize(new Settings { WindowPadding = 8 }));
                Entity = new TimedEntity(Path, Clock);
            }
            public Settings Read() => JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path));
            public void Dispose()
            {
                Entity.FlushAsync().GetAwaiter().GetResult();
                File.Delete(Path);
                File.Delete(Path + ".tmp");
                File.Delete(Path + ".bak");
            }
        }

        private sealed class TimedEntity(string path, TimeProvider clock)
            : ObservableFileEntityBase<Settings>(path, () => new Settings(), clock)
        {
            public int Writes { get; private set; }
            public bool FailNextWrite { get; set; }
            public Func<int, Task>? BeforeWrite { get; set; }
            protected override Task<Settings> ReadAsync(Stream stream) =>
                Task.FromResult(JsonSerializer.Deserialize<Settings>(stream));
            protected override async Task WriteAsync(Stream stream, Settings value)
            {
                Writes++;
                if (BeforeWrite != null) await BeforeWrite(Writes).ConfigureAwait(false);
                if (FailNextWrite)
                {
                    FailNextWrite = false;
                    stream.WriteByte(123);
                    throw new IOException("controlled partial write");
                }
                await JsonSerializer.SerializeAsync(stream, value).ConfigureAwait(false);
            }
        }

        private sealed class ManualTimeProvider : TimeProvider
        {
            private readonly object m_lock = new();
            private readonly List<ManualTimer> m_timers = [];
            private long m_ticks;
            public bool FailNextTimer { get; set; }
            public int ActiveTimers { get { lock (m_lock) return m_timers.Count; } }
            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                Assert.AreEqual(Timeout.InfiniteTimeSpan, period);
                if (FailNextTimer)
                {
                    FailNextTimer = false;
                    throw new InvalidOperationException("controlled timer failure");
                }
                lock (m_lock)
                {
                    var timer = new ManualTimer(this, callback, state, m_ticks + dueTime.Ticks);
                    m_timers.Add(timer);
                    return timer;
                }
            }
            public void Advance(TimeSpan amount)
            {
                ManualTimer[] ready;
                lock (m_lock)
                {
                    m_ticks += amount.Ticks;
                    ready = m_timers.Where(timer => timer.Due <= m_ticks).ToArray();
                    foreach (var timer in ready) m_timers.Remove(timer);
                }
                foreach (var timer in ready) timer.Callback(timer.State);
            }
            private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, long due) : ITimer
            {
                public TimerCallback Callback { get; } = callback;
                public object? State { get; } = state;
                public long Due { get; } = due;
                public bool Change(TimeSpan dueTime, TimeSpan period) => throw new AssertFailedException("A fixed admission deadline must not be extended.");
                public void Dispose() { lock (owner.m_lock) owner.m_timers.Remove(this); }
                public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
            }
        }

        [TestMethod]
        public async Task DistinctUpdatesDuringWriteKeepLatestMemoryAndBoundDiskWork()
        {
            var (writes, visible, saved) = await RunDistinctBurstAsync();
            Assert.AreEqual(100, visible.PanelHeight, "Observers must see the latest accepted update before disk I/O finishes.");
            Assert.AreEqual(100, saved.PanelHeight);
            Assert.AreEqual(108, saved.WindowPadding, "Each updater must merge against the preceding accepted value.");
            Assert.AreEqual(2, writes, "One in-flight transaction and one latest pending transaction suffice.");
        }

        [TestMethod]
        public async Task DistinctSettingsCounterScenario()
        {
            await RunDistinctBurstAsync(); // Same full warmup scenario for both production variants.
            var (writes, _, saved) = await RunDistinctBurstAsync();
            Assert.AreEqual(100, saved.PanelHeight);
            Assert.AreEqual(108, saved.WindowPadding);
            Console.WriteLine($"PERFCOUNTER settings-distinct writes {writes}");
            Console.WriteLine("PERFCOUNTER settings-distinct updates 100");
        }

        private static async Task<(int Writes, Settings Visible, Settings Saved)> RunDistinctBurstAsync()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            var initial = new Settings { WindowPadding = 8 };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(initial));
            var entity = new ControlledEntity(path);
            Task[] updates = [];
            try
            {
                await entity.Value.FirstAsync();
                var first = entity.SaveAsync(value => value with { PanelHeight = 1, WindowPadding = value.WindowPadding + 1 });
                await entity.Writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
                updates = new[] { first }.Concat(Enumerable.Range(2, 99).Select(height =>
                    entity.SaveAsync(value => value with { PanelHeight = height, WindowPadding = value.WindowPadding + 1 }))).ToArray();
                var visible = await entity.Value.FirstAsync();
                entity.Continue.TrySetResult(true);
                await Task.WhenAll(updates).WaitAsync(TimeSpan.FromSeconds(10));
                var saved = JsonSerializer.Deserialize<Settings>(await File.ReadAllTextAsync(path));
                return (entity.Writes, visible, saved);
            }
            finally
            {
                entity.Continue.TrySetResult(true);
                await Task.WhenAll(updates);
                File.Delete(path);
                File.Delete(path + ".tmp");
                File.Delete(path + ".bak");
            }
        }

        [TestMethod]
        public async Task ConcurrentFieldsMergeAgainstTheLatestSuccessfulWrite()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            try
            {
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new Settings()));
                var entity = new ControlledEntity(path);
                await entity.Value.FirstAsync();
                var first = entity.SaveAsync(value => value with { PanelHeight = 37 });
                await entity.Writing.Task;
                var second = entity.SaveAsync(value => value with { WindowPadding = 13 });
                entity.Continue.TrySetResult(true);
                await Task.WhenAll(first, second);
                var saved = JsonSerializer.Deserialize<Settings>(await File.ReadAllTextAsync(path));
                Assert.AreEqual(37, saved.PanelHeight);
                Assert.AreEqual(13, saved.WindowPadding);
                var replay = await entity.Value.FirstAsync();
                Assert.AreEqual(37, replay.PanelHeight);
                Assert.AreEqual(13, replay.WindowPadding);
            }
            finally
            {
                File.Delete(path);
                File.Delete(path + ".tmp");
                File.Delete(path + ".bak");
            }
        }

        [TestMethod]
        public async Task IdenticalPendingUpdatesWriteOnce()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            try
            {
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new Settings()));
                var entity = new ControlledEntity(path);
                await entity.Value.FirstAsync();
                var updates = Enumerable.Range(0, 100).Select(_ =>
                    entity.SaveAsync(value => value with { PanelHeight = 37 })).ToArray();
                await entity.Writing.Task;
                entity.Continue.TrySetResult(true);
                await Task.WhenAll(updates);
                Assert.AreEqual(1, entity.Writes);
                Assert.AreEqual(37, (await entity.Value.FirstAsync()).PanelHeight);
            }
            finally
            {
                File.Delete(path);
                File.Delete(path + ".tmp");
                File.Delete(path + ".bak");
            }
        }

        private sealed class ControlledEntity(string path)
            : ObservableFileEntityBase<Settings>(path, () => new Settings())
        {
            public TaskCompletionSource<bool> Writing { get; } = new();
            public TaskCompletionSource<bool> Continue { get; } = new();
            public int Writes { get; private set; }

            protected override async Task<Settings> ReadAsync(Stream stream) =>
                await JsonSerializer.DeserializeAsync<Settings>(stream);

            protected override async Task WriteAsync(Stream stream, Settings value)
            {
                Writes++;
                Writing.TrySetResult(true);
                await Continue.Task;
                await JsonSerializer.SerializeAsync(stream, value);
            }
        }
    }
}
