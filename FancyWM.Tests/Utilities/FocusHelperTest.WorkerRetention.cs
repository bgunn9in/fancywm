#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    public partial class FocusHelperTest
    {
        private const int RetentionCompletedLifetimes = 128;
        private const int RetentionOverlapBatches = 64;
        private const int RetentionAdmissionsPerBatch = 16;

        [TestMethod]
        public void CompletedWorkerLifetimesReleaseSequenceOwnership()
        {
            RetentionMeasurement measurement = MeasureCompletedWorkerLifetimes(32, captureProcessCounts: false);
            AssertCompletedRetentionSemantics(measurement, 32);
        }

        [TestMethod]
        public void OverlappedWorkerLifetimesReleaseSequenceOwnership()
        {
            RetentionMeasurement measurement = MeasureOverlappedWorkerLifetimes(16, 8, captureProcessCounts: false);
            AssertOverlappedRetentionSemantics(measurement, 16, 8);
        }

        [TestMethod]
        public void FocusWorkerRetentionCounterScenario()
        {
            _ = ReadProcessSnapshot();
            // Warm every runtime and fixture path with the same workload used by
            // the measured pass so one-time thread/testhost handle initialization
            // is outside the retention delta.
            _ = MeasureCompletedWorkerLifetimes(
                RetentionCompletedLifetimes, captureProcessCounts: false);
            _ = MeasureOverlappedWorkerLifetimes(
                RetentionOverlapBatches, RetentionAdmissionsPerBatch,
                captureProcessCounts: false);
            ForceThreadCollection();

            ActiveWorkerMeasurement active = MeasureActiveWorkerFootprint();
            WriteRetentionCounter("focus-worker-active", "baseline-process-handles", active.Baseline.Handles);
            WriteRetentionCounter("focus-worker-active", "active-process-handles", active.Active.Handles);
            WriteRetentionCounter("focus-worker-active", "settled-process-handles", active.Settled.Handles);
            WriteRetentionCounter("focus-worker-active", "active-handle-delta", active.Active.Handles - active.Baseline.Handles);
            WriteRetentionCounter("focus-worker-active", "settled-handle-delta", active.Settled.Handles - active.Baseline.Handles);
            WriteRetentionCounter("focus-worker-active", "baseline-process-threads", active.Baseline.Threads);
            WriteRetentionCounter("focus-worker-active", "active-process-threads", active.Active.Threads);
            WriteRetentionCounter("focus-worker-active", "settled-process-threads", active.Settled.Threads);
            WriteRetentionCounter("focus-worker-active", "active-thread-delta", active.Active.Threads - active.Baseline.Threads);
            WriteRetentionCounter("focus-worker-active", "settled-thread-delta", active.Settled.Threads - active.Baseline.Threads);
            WriteRetentionCounter("focus-worker-active", "worker-starts", active.WorkerStarts);
            WriteRetentionCounter("focus-worker-active", "worker-exits", active.WorkerExits);
            WriteRetentionCounter("focus-worker-active", "worker-field-cleared", active.WorkerFieldCleared);

            RetentionMeasurement completed = MeasureCompletedWorkerLifetimes(
                RetentionCompletedLifetimes, captureProcessCounts: true);
            AssertCompletedRetentionSemantics(completed, RetentionCompletedLifetimes);
            WriteRetentionMeasurement("focus-worker-retention-completed", completed);

            RetentionMeasurement overlapFirst = MeasureOverlappedWorkerLifetimes(
                RetentionOverlapBatches, RetentionAdmissionsPerBatch, captureProcessCounts: true);
            AssertOverlappedRetentionSemantics(
                overlapFirst, RetentionOverlapBatches, RetentionAdmissionsPerBatch);
            WriteRetentionMeasurement("focus-worker-retention-overlap-first", overlapFirst);

            RetentionMeasurement overlapRepeat = MeasureOverlappedWorkerLifetimes(
                RetentionOverlapBatches, RetentionAdmissionsPerBatch, captureProcessCounts: true);
            AssertOverlappedRetentionSemantics(
                overlapRepeat, RetentionOverlapBatches, RetentionAdmissionsPerBatch);
            WriteRetentionMeasurement("focus-worker-retention-overlap-repeat", overlapRepeat);
        }

        private static ActiveWorkerMeasurement MeasureActiveWorkerFootprint()
        {
            ForceThreadCollection();
            ProcessSnapshot baseline = ReadStableProcessSnapshot();
            ActiveWorkerRun run = RunActiveWorkerFootprint();
            ForceThreadCollection();
            ProcessSnapshot settled = ReadStableProcessSnapshot();
            return new(baseline, run.Active, settled, run.WorkerStarts,
                run.WorkerExits, run.WorkerFieldCleared);
        }

        private static ActiveWorkerRun RunActiveWorkerFootprint()
        {
            using var entered = new ManualResetEvent(false);
            using var release = new ManualResetEvent(false);
            var counters = new RetentionNativeCounters();
            var native = new RetentionNative(counters, entered, release);
            Thread? worker = null;
            int starts = 0;
            var requests = new FocusHelper.RequestSequence(task => task.Wait(TimeSpan.FromSeconds(5)), thread =>
            {
                Interlocked.Increment(ref starts);
                worker = thread;
                thread.Start();
            });
            object token = requests.Begin();
            Task<bool> completion = requests.Enqueue(token, new IntPtr(1), native);
            Assert.IsTrue(entered.WaitOne(TimeSpan.FromSeconds(5)));
            ProcessSnapshot active = ReadStableProcessSnapshot();
            release.Set();
            Assert.IsTrue(SpinUntilCompleted(completion, TimeSpan.FromSeconds(5)));
            Assert.IsNotNull(worker);
            Assert.IsTrue(worker!.Join(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(WaitForWorkerFieldClear(requests, TimeSpan.FromSeconds(5)));
            worker = null;
            return new(active, starts, 1,
                GetRequestSequenceWorker(requests) == null ? 1 : 0);
        }

        private static RetentionMeasurement MeasureCompletedWorkerLifetimes(
            int lifetimes, bool captureProcessCounts)
        {
            ProcessSnapshot before = captureProcessCounts
                ? ReadStableProcessSnapshot()
                : default;
            RetentionRun run = RunCompletedWorkerLifetimes(lifetimes);
            int liveReferences = CollectLiveThreadReferences(run.WorkerReferences);
            ProcessSnapshot after = captureProcessCounts
                ? ReadStableProcessSnapshot()
                : default;
            return new(lifetimes, lifetimes, run.TrueResults, run.FalseResults,
                run.WorkerStarts, run.WorkerExits, run.BackgroundWorkers,
                run.ManagedWorkerThreadIds, run.Counters.QueueCalls,
                run.Counters.AttachCalls, run.Counters.DetachCalls,
                run.Counters.FocusCalls, run.Counters.OwnedAttachments,
                run.WorkerFieldCleared, liveReferences, before, after);
        }

        private static RetentionMeasurement MeasureOverlappedWorkerLifetimes(
            int batches, int admissionsPerBatch, bool captureProcessCounts)
        {
            ProcessSnapshot before = captureProcessCounts
                ? ReadStableProcessSnapshot()
                : default;
            RetentionRun run = RunOverlappedWorkerLifetimes(batches, admissionsPerBatch);
            int liveReferences = CollectLiveThreadReferences(run.WorkerReferences);
            ProcessSnapshot after = captureProcessCounts
                ? ReadStableProcessSnapshot()
                : default;
            int admissions = checked(batches * admissionsPerBatch);
            return new(batches, admissions, run.TrueResults, run.FalseResults,
                run.WorkerStarts, run.WorkerExits, run.BackgroundWorkers,
                run.ManagedWorkerThreadIds, run.Counters.QueueCalls,
                run.Counters.AttachCalls, run.Counters.DetachCalls,
                run.Counters.FocusCalls, run.Counters.OwnedAttachments,
                run.WorkerFieldCleared, liveReferences, before, after);
        }

        private static RetentionRun RunCompletedWorkerLifetimes(int lifetimes)
        {
            var counters = new RetentionNativeCounters();
            var workerReferences = new List<WeakReference<Thread>>(lifetimes);
            var workerIds = new HashSet<int>();
            object workerIdsGate = new();
            Thread? worker = null;
            int starts = 0;
            int exits = 0;
            int background = 0;
            var requests = new FocusHelper.RequestSequence(task => task.Wait(TimeSpan.FromSeconds(5)), thread =>
            {
                Interlocked.Increment(ref starts);
                if (thread.IsBackground) Interlocked.Increment(ref background);
                workerReferences.Add(new WeakReference<Thread>(thread));
                worker = thread;
                thread.Start();
            });
            int trueResults = 0;
            for (int index = 0; index < lifetimes; index++)
            {
                var native = new RetentionNative(counters, onWorkerThread: id =>
                {
                    lock (workerIdsGate) workerIds.Add(id);
                });
                object token = requests.Begin();
                Task<bool> completion = requests.Enqueue(token, new IntPtr(index + 1), native);
                Assert.IsTrue(SpinUntilCompleted(completion, TimeSpan.FromSeconds(5)));
                if (completion.Result) trueResults++;
                Assert.IsNotNull(worker);
                Assert.IsTrue(worker!.Join(TimeSpan.FromSeconds(5)));
                Interlocked.Increment(ref exits);
                worker = null;
                Assert.IsTrue(WaitForWorkerFieldClear(requests, TimeSpan.FromSeconds(5)));
            }
            return new(trueResults, lifetimes - trueResults, starts, exits,
                background, workerIds.Count, counters.Read(),
                GetRequestSequenceWorker(requests) == null ? 1 : 0,
                workerReferences.ToArray());
        }

        private static RetentionRun RunOverlappedWorkerLifetimes(
            int batches, int admissionsPerBatch)
        {
            var counters = new RetentionNativeCounters();
            var workerReferences = new List<WeakReference<Thread>>(batches);
            var workerIds = new HashSet<int>();
            object workerIdsGate = new();
            Thread? worker = null;
            int starts = 0;
            int exits = 0;
            int background = 0;
            int trueResults = 0;
            int falseResults = 0;
            var requests = new FocusHelper.RequestSequence(task => task.Wait(TimeSpan.FromSeconds(5)), thread =>
            {
                Interlocked.Increment(ref starts);
                if (thread.IsBackground) Interlocked.Increment(ref background);
                workerReferences.Add(new WeakReference<Thread>(thread));
                worker = thread;
                thread.Start();
            });
            for (int batch = 0; batch < batches; batch++)
            {
                using var entered = new ManualResetEvent(false);
                using var release = new ManualResetEvent(false);
                var completions = new Task<bool>[admissionsPerBatch];
                Action<int> observeWorker = id =>
                {
                    lock (workerIdsGate) workerIds.Add(id);
                };
                object token = requests.Begin();
                completions[0] = requests.Enqueue(token, new IntPtr(1),
                    new RetentionNative(counters, entered, release, observeWorker));
                Assert.IsTrue(entered.WaitOne(TimeSpan.FromSeconds(5)));
                for (int index = 1; index < admissionsPerBatch; index++)
                {
                    token = requests.Begin();
                    completions[index] = requests.Enqueue(token, new IntPtr(index + 1),
                        new RetentionNative(counters, onWorkerThread: observeWorker));
                }
                release.Set();
                foreach (Task<bool> completion in completions)
                {
                    Assert.IsTrue(SpinUntilCompleted(completion, TimeSpan.FromSeconds(5)));
                    if (completion.Result) trueResults++; else falseResults++;
                }
                Assert.IsNotNull(worker);
                Assert.IsTrue(worker!.Join(TimeSpan.FromSeconds(5)));
                Interlocked.Increment(ref exits);
                worker = null;
                Assert.IsTrue(WaitForWorkerFieldClear(requests, TimeSpan.FromSeconds(5)));
            }
            return new(trueResults, falseResults, starts, exits, background,
                workerIds.Count, counters.Read(),
                GetRequestSequenceWorker(requests) == null ? 1 : 0,
                workerReferences.ToArray());
        }

        private static void AssertCompletedRetentionSemantics(
            RetentionMeasurement measurement, int lifetimes)
        {
            Assert.AreEqual(lifetimes, measurement.Lifetimes);
            Assert.AreEqual(lifetimes, measurement.Admissions);
            Assert.AreEqual(lifetimes, measurement.TrueResults);
            Assert.AreEqual(0, measurement.FalseResults);
            Assert.AreEqual(lifetimes, measurement.WorkerStarts);
            Assert.AreEqual(lifetimes, measurement.WorkerExits);
            Assert.AreEqual(lifetimes, measurement.BackgroundWorkers);
            Assert.AreEqual(lifetimes, measurement.ManagedWorkerThreadIds);
            Assert.AreEqual(lifetimes, measurement.QueueCalls);
            Assert.AreEqual(lifetimes, measurement.AttachCalls);
            Assert.AreEqual(lifetimes, measurement.DetachCalls);
            Assert.AreEqual(lifetimes, measurement.FocusCalls);
            Assert.AreEqual(0, measurement.OwnedAttachments);
            Assert.AreEqual(1, measurement.WorkerFieldCleared);
            Assert.AreEqual(0, measurement.LiveWorkerReferences);
        }

        private static void AssertOverlappedRetentionSemantics(
            RetentionMeasurement measurement, int batches, int admissionsPerBatch)
        {
            int admissions = checked(batches * admissionsPerBatch);
            Assert.AreEqual(batches, measurement.Lifetimes);
            Assert.AreEqual(admissions, measurement.Admissions);
            Assert.AreEqual(batches, measurement.TrueResults);
            Assert.AreEqual(admissions - batches, measurement.FalseResults);
            Assert.AreEqual(batches, measurement.WorkerStarts);
            Assert.AreEqual(batches, measurement.WorkerExits);
            Assert.AreEqual(batches, measurement.BackgroundWorkers);
            Assert.AreEqual(batches, measurement.ManagedWorkerThreadIds);
            Assert.AreEqual(batches * 2, measurement.QueueCalls);
            Assert.AreEqual(batches, measurement.AttachCalls);
            Assert.AreEqual(batches, measurement.DetachCalls);
            Assert.AreEqual(batches, measurement.FocusCalls);
            Assert.AreEqual(0, measurement.OwnedAttachments);
            Assert.AreEqual(1, measurement.WorkerFieldCleared);
            Assert.AreEqual(0, measurement.LiveWorkerReferences);
        }

        private static Thread? GetRequestSequenceWorker(FocusHelper.RequestSequence requests)
        {
            FieldInfo? field = typeof(FocusHelper.RequestSequence).GetField(
                "m_worker", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "The retention fixture requires RequestSequence.m_worker.");
            return (Thread?)field!.GetValue(requests);
        }

        private static bool WaitForWorkerFieldClear(
            FocusHelper.RequestSequence requests, TimeSpan timeout)
        {
            long deadline = Stopwatch.GetTimestamp()
                + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
            while (GetRequestSequenceWorker(requests) != null
                && Stopwatch.GetTimestamp() < deadline)
            {
                Thread.Yield();
            }
            return GetRequestSequenceWorker(requests) == null;
        }

        private static int CollectLiveThreadReferences(WeakReference<Thread>[] references)
        {
            for (int attempt = 0; attempt < 4; attempt++) ForceThreadCollection();
            return references.Count(reference => reference.TryGetTarget(out _));
        }

        private static void ForceThreadCollection()
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        private static ProcessSnapshot ReadStableProcessSnapshot()
        {
            int handles = int.MaxValue;
            int threads = int.MaxValue;
            for (int sample = 0; sample < 5; sample++)
            {
                ProcessSnapshot current = ReadProcessSnapshot();
                handles = Math.Min(handles, current.Handles);
                threads = Math.Min(threads, current.Threads);
                Thread.Sleep(10);
            }
            return new(handles, threads);
        }

        private static ProcessSnapshot ReadProcessSnapshot()
        {
            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            return new(process.HandleCount, process.Threads.Count);
        }

        private static void WriteRetentionMeasurement(
            string scenario, RetentionMeasurement measurement)
        {
            WriteRetentionCounter(scenario, "lifetimes", measurement.Lifetimes);
            WriteRetentionCounter(scenario, "admissions", measurement.Admissions);
            WriteRetentionCounter(scenario, "true-results", measurement.TrueResults);
            WriteRetentionCounter(scenario, "false-results", measurement.FalseResults);
            WriteRetentionCounter(scenario, "worker-starts", measurement.WorkerStarts);
            WriteRetentionCounter(scenario, "worker-exits", measurement.WorkerExits);
            WriteRetentionCounter(scenario, "background-workers", measurement.BackgroundWorkers);
            WriteRetentionCounter(scenario, "managed-worker-thread-ids", measurement.ManagedWorkerThreadIds);
            WriteRetentionCounter(scenario, "queue-calls", measurement.QueueCalls);
            WriteRetentionCounter(scenario, "attach-calls", measurement.AttachCalls);
            WriteRetentionCounter(scenario, "detach-calls", measurement.DetachCalls);
            WriteRetentionCounter(scenario, "focus-calls", measurement.FocusCalls);
            WriteRetentionCounter(scenario, "owned-attachments", measurement.OwnedAttachments);
            WriteRetentionCounter(scenario, "worker-field-cleared", measurement.WorkerFieldCleared);
            WriteRetentionCounter(scenario, "live-worker-references", measurement.LiveWorkerReferences);
            WriteRetentionCounter(scenario, "baseline-process-handles", measurement.Before.Handles);
            WriteRetentionCounter(scenario, "settled-process-handles", measurement.After.Handles);
            WriteRetentionCounter(scenario, "settled-handle-delta", measurement.After.Handles - measurement.Before.Handles);
            WriteRetentionCounter(scenario, "baseline-process-threads", measurement.Before.Threads);
            WriteRetentionCounter(scenario, "settled-process-threads", measurement.After.Threads);
            WriteRetentionCounter(scenario, "settled-thread-delta", measurement.After.Threads - measurement.Before.Threads);
        }

        private static void WriteRetentionCounter(string scenario, string metric, long value)
        {
            Console.WriteLine($"PERFCOUNTER {scenario} {metric} "
                + value.ToString(CultureInfo.InvariantCulture));
        }

        private readonly record struct ProcessSnapshot(int Handles, int Threads);

        private readonly record struct ActiveWorkerMeasurement(
            ProcessSnapshot Baseline, ProcessSnapshot Active,
            ProcessSnapshot Settled, int WorkerStarts, int WorkerExits,
            int WorkerFieldCleared);

        private readonly record struct ActiveWorkerRun(
            ProcessSnapshot Active, int WorkerStarts, int WorkerExits,
            int WorkerFieldCleared);

        private readonly record struct RetentionMeasurement(
            int Lifetimes, int Admissions, int TrueResults, int FalseResults,
            int WorkerStarts, int WorkerExits, int BackgroundWorkers,
            int ManagedWorkerThreadIds, int QueueCalls, int AttachCalls,
            int DetachCalls, int FocusCalls, int OwnedAttachments,
            int WorkerFieldCleared, int LiveWorkerReferences,
            ProcessSnapshot Before, ProcessSnapshot After);

        private readonly record struct RetentionRun(
            int TrueResults, int FalseResults, int WorkerStarts, int WorkerExits,
            int BackgroundWorkers, int ManagedWorkerThreadIds,
            RetentionNativeSnapshot Counters, int WorkerFieldCleared,
            WeakReference<Thread>[] WorkerReferences);

        private readonly record struct RetentionNativeSnapshot(
            int QueueCalls, int AttachCalls, int DetachCalls,
            int FocusCalls, int OwnedAttachments);

        private sealed class RetentionNativeCounters
        {
            private int m_queueCalls;
            private int m_attachCalls;
            private int m_detachCalls;
            private int m_focusCalls;
            private int m_ownedAttachments;

            internal void Queue() => Interlocked.Increment(ref m_queueCalls);
            internal void Focus() => Interlocked.Increment(ref m_focusCalls);
            internal void Attach()
            {
                Interlocked.Increment(ref m_attachCalls);
                Interlocked.Increment(ref m_ownedAttachments);
            }
            internal void Detach()
            {
                Interlocked.Increment(ref m_detachCalls);
                Interlocked.Decrement(ref m_ownedAttachments);
            }
            internal RetentionNativeSnapshot Read() => new(
                Volatile.Read(ref m_queueCalls), Volatile.Read(ref m_attachCalls),
                Volatile.Read(ref m_detachCalls), Volatile.Read(ref m_focusCalls),
                Volatile.Read(ref m_ownedAttachments));
        }

        private sealed class RetentionNative : FocusHelper.INative
        {
            private readonly RetentionNativeCounters m_counters;
            private readonly ManualResetEvent? m_entered;
            private readonly ManualResetEvent? m_release;
            private readonly Action<int>? m_onWorkerThread;

            internal RetentionNative(
                RetentionNativeCounters counters,
                ManualResetEvent? entered = null,
                ManualResetEvent? release = null,
                Action<int>? onWorkerThread = null)
            {
                m_counters = counters;
                m_entered = entered;
                m_release = release;
                m_onWorkerThread = onWorkerThread;
            }

            public bool SetForegroundWindow(IntPtr window)
            {
                m_counters.Focus();
                return true;
            }

            public void EnsureMessageQueue()
            {
                m_counters.Queue();
                m_onWorkerThread?.Invoke(Environment.CurrentManagedThreadId);
                m_entered?.Set();
                if (m_release != null
                    && !m_release.WaitOne(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("The retention fixture did not release its worker.");
                }
            }

            public uint CurrentThreadId => 11;
            public uint ForegroundThreadId => 22;

            public bool AttachThreadInput(uint thread, uint foregroundThread, bool attach)
            {
                Assert.AreEqual(11U, thread);
                Assert.AreEqual(22U, foregroundThread);
                if (attach) m_counters.Attach(); else m_counters.Detach();
                return true;
            }

            public void SendAltKeyPresses() => Assert.Fail("Direct-success retention fixture must not send Alt.");
        }
    }
}
