using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using WinMan.Windows;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class Win32RecentTimerTest
    {
        [TestMethod]
        public void IdleIsUnarmedAndBurstArmsOnceWithoutResettingCadence()
        {
            using var fixture = new TimerFixture();
            for (int tick = 0; tick < 60000; tick++) { fixture.UpdateTimer(); }
            Assert.AreEqual(0, fixture.TimerChanges.Count);
            for (int index = 1; index <= 100; index++) { fixture.Track(new IntPtr(index)); }
            Assert.AreEqual(1, fixture.PendingUpdates.Count);
            fixture.DrainUpdates();
            Assert.IsTrue(fixture.Enabled);
            fixture.Track(new IntPtr(101));
            fixture.UpdateTimer();
            CollectionAssert.AreEqual(new[] { true }, fixture.TimerChanges);
            fixture.Tick();
            Assert.AreEqual(101, fixture.Checks.Count);
        }

        [TestMethod]
        public void ObservationIncludesBoundaryAndFinalExpiredCheckThenStops()
        {
            using var fixture = new TimerFixture();
            fixture.Track(new IntPtr(1));
            fixture.DrainUpdates();
            fixture.Now = 500;
            fixture.Tick();
            Assert.IsTrue(fixture.Enabled);
            fixture.Now = 510;
            fixture.Tick();
            fixture.DrainUpdates();
            Assert.IsFalse(fixture.Enabled);
            fixture.Tick();
            CollectionAssert.AreEqual(new[] { new IntPtr(1), new IntPtr(1) }, fixture.Checks);
            CollectionAssert.AreEqual(new[] { true, false }, fixture.TimerChanges);
        }

        [TestMethod]
        public void ArrivalBeforeQueuedStopKeepsTimerAndNewWindow()
        {
            using var fixture = new TimerFixture();
            fixture.Track(new IntPtr(1));
            fixture.DrainUpdates();
            fixture.Now = 510;
            fixture.Tick();
            fixture.Track(new IntPtr(2));
            fixture.DrainUpdates();
            fixture.Tick();
            Assert.IsTrue(fixture.Enabled);
            CollectionAssert.AreEqual(new[] { true }, fixture.TimerChanges);
            CollectionAssert.AreEqual(new[] { new IntPtr(1), new IntPtr(2) }, fixture.Checks);
        }

        [TestMethod]
        public async Task ArrivalDuringNativeStopRearmsAfterStopCompletes()
        {
            using var stopping = new ManualResetEventSlim();
            using var arriving = new ManualResetEventSlim();
            using var continueStop = new ManualResetEventSlim();
            using var fixture = new TimerFixture();
            fixture.Track(new IntPtr(1));
            fixture.DrainUpdates();
            fixture.Now = 510;
            fixture.Tick();
            fixture.ChangeTimer = enabled =>
            {
                if (!enabled)
                {
                    stopping.Set();
                    Assert.IsTrue(continueStop.Wait(TimeSpan.FromSeconds(5)));
                }
            };
            var stop = Task.Run(fixture.UpdateTimer);
            try
            {
                Assert.IsTrue(stopping.Wait(TimeSpan.FromSeconds(5)));
                var arrival = Task.Run(() =>
                {
                    arriving.Set();
                    fixture.Track(new IntPtr(2));
                });
                Assert.IsTrue(arriving.Wait(TimeSpan.FromSeconds(5)));
                continueStop.Set();
                await Task.WhenAll(stop, arrival);
            }
            finally
            {
                continueStop.Set();
                await stop;
            }
            fixture.DrainUpdates();
            fixture.Tick();
            Assert.IsTrue(fixture.Enabled);
            CollectionAssert.AreEqual(new[] { true, false, true }, fixture.TimerChanges);
            Assert.AreEqual(new IntPtr(2), fixture.Checks[^1]);
        }

        [TestMethod]
        public void ArrivalDuringVisibilityCheckSurvivesExpiry()
        {
            using var fixture = new TimerFixture();
            fixture.Track(new IntPtr(1));
            fixture.DrainUpdates();
            fixture.Now = 510;
            fixture.CheckVisibility = handle =>
            {
                if (handle == new IntPtr(1)) { fixture.Track(new IntPtr(2)); }
            };
            fixture.Tick();
            fixture.DrainUpdates();
            fixture.Tick();
            Assert.IsTrue(fixture.Enabled);
            CollectionAssert.AreEqual(new[] { new IntPtr(1), new IntPtr(2) }, fixture.Checks);
        }

        [TestMethod]
        public void VisibilityFailureStillStopsExpiredTimerAndRetainsYoungerCandidates()
        {
            using var fixture = new TimerFixture();
            fixture.Track(new IntPtr(1));
            fixture.DrainUpdates();
            fixture.CheckVisibility = _ => throw new InvalidOperationException("visibility failed");
            fixture.Now = 510;
            Assert.ThrowsException<InvalidOperationException>(fixture.Tick);
            fixture.DrainUpdates();
            Assert.IsFalse(fixture.Enabled);
            fixture.Track(new IntPtr(2));
            fixture.DrainUpdates();
            Assert.ThrowsException<InvalidOperationException>(fixture.Tick);
            Assert.IsTrue(fixture.Enabled);
            fixture.CheckVisibility = _ => { };
            fixture.Tick();
            Assert.AreEqual(new IntPtr(2), fixture.Checks[^1]);
        }

        [TestMethod]
        public void FailedPostAndNativeStartRetryThroughExistingReconciliation()
        {
            using var fixture = new TimerFixture();
            fixture.PostUpdate = () => throw new InvalidOperationException("post failed");
            Assert.ThrowsException<InvalidOperationException>(() => fixture.Track(new IntPtr(1)));
            fixture.PostUpdate = null;
            fixture.Track(new IntPtr(2));
            fixture.ChangeTimer = _ => throw new InvalidOperationException("timer failed");
            Assert.ThrowsException<InvalidOperationException>(fixture.UpdateTimer);
            Assert.IsFalse(fixture.Enabled);
            fixture.ChangeTimer = null;
            fixture.UpdateTimer();
            fixture.Tick();
            CollectionAssert.AreEqual(new[] { new IntPtr(1), new IntPtr(2) }, fixture.Checks);
            Assert.IsTrue(fixture.Enabled);
        }

        [TestMethod]
        public void RepeatedObservationCyclesAndDisposeRejectLateWork()
        {
            using var fixture = new TimerFixture();
            for (int cycle = 0; cycle < 100; cycle++)
            {
                fixture.Track(new IntPtr(cycle + 1));
                fixture.DrainUpdates();
                fixture.Now += 510;
                fixture.Tick();
                fixture.DrainUpdates();
                Assert.IsFalse(fixture.Enabled);
            }
            Assert.AreEqual(100, fixture.Checks.Count);
            Assert.AreEqual(200, fixture.TimerChanges.Count);
            fixture.Track(new IntPtr(101));
            fixture.DrainUpdates();
            fixture.Workspace.Dispose();
            fixture.Workspace.Dispose();
            fixture.DrainUpdates();
            fixture.Tick();
            fixture.Track(new IntPtr(102));
            fixture.DrainUpdates();
            Assert.IsFalse(fixture.Enabled);
            Assert.AreEqual(100, fixture.Checks.Count);
            Assert.AreEqual(202, fixture.TimerChanges.Count);
            Assert.ThrowsException<ObjectDisposedException>(fixture.Workspace.Open);
        }

        [TestMethod]
        public void DisposeDuringCheckSuppressesRemainingChecksAndPendingArm()
        {
            using var fixture = new TimerFixture();
            fixture.Track(new IntPtr(1));
            fixture.Track(new IntPtr(2));
            fixture.CheckVisibility = _ => fixture.Workspace.Dispose();
            fixture.Tick();
            fixture.DrainUpdates();
            Assert.AreEqual(1, fixture.Checks.Count);
            Assert.AreEqual(0, fixture.TimerChanges.Count);
        }

        [TestMethod]
        public void FailedStopCanRetryWithoutLosingTheNextCandidate()
        {
            using var fixture = new TimerFixture();
            fixture.Track(new IntPtr(1));
            fixture.DrainUpdates();
            fixture.Now = 510;
            fixture.Tick();
            fixture.ChangeTimer = _ => throw new InvalidOperationException("stop failed");
            Assert.ThrowsException<InvalidOperationException>(fixture.UpdateTimer);
            Assert.IsTrue(fixture.Enabled);
            fixture.Track(new IntPtr(2));
            fixture.ChangeTimer = null;
            fixture.DrainUpdates();
            fixture.Tick();
            Assert.IsTrue(fixture.Enabled);
            fixture.Now = 1020;
            fixture.Tick();
            fixture.DrainUpdates();
            Assert.IsFalse(fixture.Enabled);
            CollectionAssert.AreEqual(new[] { true, false }, fixture.TimerChanges);
            Assert.AreEqual(new IntPtr(2), fixture.Checks[^1]);
        }

        [TestMethod]
        public void RecentTimerFailureDoesNotSuppressGlobalReconciliation()
        {
            using var fixture = new TimerFixture();
            fixture.Track(new IntPtr(1));
            fixture.ChangeTimer = _ => throw new InvalidOperationException("timer failed");
            int errors = 0;
            fixture.Workspace.UnhandledException += (_, _) => errors++;
            fixture.DeliverTimer(1);
            Assert.AreEqual(1, errors);
            Assert.AreEqual(1, fixture.ReadQueuedFlag("m_onTimerWatchQueued"));
            fixture.ChangeTimer = null;
        }

        [TestMethod]
        public void ShutdownPostFailureStillCompletesOwnedProcessingLoops()
        {
            using var fixture = new TimerFixture();
            fixture.Track(new IntPtr(1));
            fixture.DrainUpdates();
            fixture.PostUpdate = () => throw new InvalidOperationException("shutdown post failed");
            Assert.ThrowsException<InvalidOperationException>(fixture.Workspace.Dispose);
            Assert.IsTrue(fixture.GetLoopCompletion("m_processingLoop").IsCompleted);
            Assert.IsTrue(fixture.GetLoopCompletion("m_backgroundProcessingLoop").IsCompleted);
            fixture.PostUpdate = null;
            fixture.UpdateTimer();
            Assert.IsFalse(fixture.Enabled);
            fixture.Tick();
            Assert.AreEqual(0, fixture.Checks.Count);
        }

        [TestMethod]
        public void StaleNativeTicksAreIgnoredAndActiveMessagesCoalesce()
        {
            using var fixture = new TimerFixture();
            fixture.DeliverTimer(2);
            Assert.AreEqual(0, fixture.ReadQueuedFlag("m_onRecentTimerWatchQueued"));
            fixture.Track(new IntPtr(1));
            fixture.DrainUpdates();
            for (int index = 0; index < 1000; index++) { fixture.DeliverTimer(2); }
            Assert.AreEqual(1, fixture.ReadQueuedFlag("m_onRecentTimerWatchQueued"));
            fixture.Now = 510;
            fixture.Tick();
            fixture.DrainUpdates();
            fixture.DeliverTimer(2);
            Assert.AreEqual(0, fixture.ReadQueuedFlag("m_onRecentTimerWatchQueued"));
            fixture.Workspace.Dispose();
            fixture.DeliverTimer(1);
            fixture.DeliverTimer(2);
            Assert.AreEqual(0, fixture.ReadQueuedFlag("m_onTimerWatchQueued"));
            Assert.AreEqual(0, fixture.ReadQueuedFlag("m_onRecentTimerWatchQueued"));
        }

        public TestContext TestContext { get; set; }

        [TestMethod]
        public void TenLogicalIdleMinutesProduceNoTicksAndDelayedVisibilityKeepsCadence()
        {
            using var fixture = new TimerFixture();
            int callbacks = 0;
            void Advance(int milliseconds)
            {
                for (int elapsed = 0; elapsed < milliseconds; elapsed += 10)
                {
                    fixture.Now += 10;
                    fixture.DrainUpdates();
                    if (fixture.Enabled) { callbacks++; fixture.Tick(); }
                }
                fixture.DrainUpdates();
            }
            Advance(600000);
            Assert.AreEqual(0, callbacks);
            TestContext.WriteLine($"PERFCOUNTER recent-idle callbacks {callbacks}");
            bool visible = false;
            fixture.CheckVisibility = _ => visible |= fixture.Now >= 600490;
            fixture.Track(new IntPtr(1));
            Advance(480);
            Assert.IsFalse(visible);
            Advance(10);
            Assert.IsTrue(visible);
            Advance(110);
            Assert.AreEqual(51, callbacks);
            Assert.IsFalse(fixture.Enabled);
            TestContext.WriteLine($"PERFCOUNTER recent-active callbacks {callbacks}");
            Advance(600000);
            Assert.AreEqual(51, callbacks);
            TestContext.WriteLine($"PERFCOUNTER recent-expired callbacks {callbacks - 51}");
        }

        [TestMethod]
        public void EmptyRecentCallbackDoesNotAllocate()
        {
            using var workspace = new Win32Workspace();
            var callback = (Action)typeof(Win32Workspace)
                .GetMethod("OnRecentTimerWatch", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate(typeof(Action), workspace);
            for (int iteration = 0; iteration < 2000; iteration++) { callback(); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0; iteration < 10000; iteration++) { callback(); }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0L, allocated, "An empty observation set must not allocate per callback.");
        }

        private sealed class TimerFixture : IDisposable
        {
            public Win32Workspace Workspace { get; }
            public long Now { get; set; }
            public bool Enabled { get; private set; }
            public Queue<Action> PendingUpdates { get; } = new();
            public List<bool> TimerChanges { get; } = new();
            public List<IntPtr> Checks { get; } = new();
            public Action PostUpdate { get; set; }
            public Action<bool> ChangeTimer { get; set; }
            public Action<IntPtr> CheckVisibility { get; set; }
            public Action<IntPtr> Track { get; }
            public Action Tick { get; }
            public Action UpdateTimer { get; }

            public TimerFixture()
            {
                var constructor = typeof(Win32Workspace).GetConstructor(
                    BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new[] { typeof(Func<long>), typeof(Action), typeof(Action<bool>), typeof(Action<IntPtr>) }, null)!;
                Workspace = (Win32Workspace)constructor.Invoke(new object[]
                {
                    (Func<long>)(() => Now),
                    (Action)(() =>
                    {
                        PostUpdate?.Invoke();
                        PendingUpdates.Enqueue(UpdateTimer);
                    }),
                    (Action<bool>)(enabled =>
                    {
                        ChangeTimer?.Invoke(enabled);
                        Enabled = enabled;
                        TimerChanges.Add(enabled);
                    }),
                    (Action<IntPtr>)(handle =>
                    {
                        Checks.Add(handle);
                        CheckVisibility?.Invoke(handle);
                    }),
                });
                Tick = Bind("OnRecentTimerWatch");
                UpdateTimer = Bind("UpdateRecentTimer");
                var method = typeof(Win32Workspace).GetMethod("WatchRecentWindow", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var handleType = method.GetParameters()[0].ParameterType;
                var handle = Expression.Parameter(typeof(IntPtr), "handle");
                Track = Expression.Lambda<Action<IntPtr>>(Expression.Call(Expression.Constant(Workspace), method,
                    Expression.New(handleType.GetConstructor(new[] { typeof(IntPtr) })!, handle)), handle).Compile();
            }

            private Action Bind(string name) => (Action)typeof(Win32Workspace)
                .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate(typeof(Action), Workspace);

            public void DeliverTimer(uint timerId)
            {
                var procedure = (Action<IntPtr, uint, UIntPtr, IntPtr>)typeof(Win32Workspace)
                    .GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .CreateDelegate(typeof(Action<IntPtr, uint, UIntPtr, IntPtr>), Workspace);
                procedure(IntPtr.Zero, 0x0113, new UIntPtr(timerId), IntPtr.Zero);
            }

            public int ReadQueuedFlag(string name) => (int)typeof(Win32Workspace)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Workspace)!;

            public Task GetLoopCompletion(string name)
            {
                var loop = typeof(Win32Workspace).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Workspace)!;
                var channel = loop.GetType().GetField("m_channel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(loop)!;
                var reader = channel.GetType().GetProperty("Reader")!.GetValue(channel)!;
                return (Task)reader.GetType().GetProperty("Completion")!.GetValue(reader)!;
            }

            public void DrainUpdates()
            {
                while (PendingUpdates.TryDequeue(out var update)) { update(); }
            }

            public void Dispose()
            {
                Workspace.Dispose();
                DrainUpdates();
            }
        }
    }
}
