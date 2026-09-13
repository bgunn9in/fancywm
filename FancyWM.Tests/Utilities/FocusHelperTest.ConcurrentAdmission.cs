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
        private const int ConcurrentAdmissionRegressionCalls = 64;
        private const int ConcurrentAdmissionCounterCalls = 256;
        private const int ContendedEnqueueCalls = 32;
        private const long WorkerTransitionAllocationBudgetPerCall = 32;

        [TestMethod]
        public void ConcurrentEnqueuesWaitForSharedGateAndReplaceExactlyOnePendingWinner()
        {
            ConcurrentEnqueueMeasurement measurement = MeasureContendedEnqueues(ContendedEnqueueCalls);
            AssertContendedEnqueueSemantics(measurement, ContendedEnqueueCalls);
        }

        [DataTestMethod]
        [DataRow("direct-success")]
        [DataRow("alt-retry")]
        [DataRow("attach-failure")]
        public void OverlappedWorkerAdmissionsPreserveLatestOwnershipAndCompletionOrder(string mode)
        {
            _ = MeasureWorkerOverlap(mode, 4);
            WorkerOverlapMeasurement measurement = MeasureWorkerOverlap(
                mode, ConcurrentAdmissionRegressionCalls);
            AssertWorkerOverlapSemantics(mode, measurement, ConcurrentAdmissionRegressionCalls);
        }

        [DataTestMethod]
        [DataRow("direct-success")]
        [DataRow("alt-retry")]
        [DataRow("attach-failure")]
        public void ConcurrentWorkerTransitionsStayWithinAllocationBudget(string mode)
        {
            _ = MeasureWorkerOverlap(mode, 4);
            WorkerOverlapMeasurement measurement = MeasureWorkerOverlap(
                mode, ConcurrentAdmissionRegressionCalls);
            AssertWorkerOverlapSemantics(mode, measurement, ConcurrentAdmissionRegressionCalls);

            long transitions = ConcurrentAdmissionRegressionCalls - 1;
            long budget = checked(WorkerTransitionAllocationBudgetPerCall * transitions);
            Assert.IsTrue(measurement.WorkerTransitionAllocatedBytes <= budget,
                $"RunWorker allocates above its per-transition budget: mode={mode}, "
                + $"total={measurement.WorkerTransitionAllocatedBytes} B, "
                + $"per-transition={(double)measurement.WorkerTransitionAllocatedBytes / transitions:F3} B, "
                + $"budget={WorkerTransitionAllocationBudgetPerCall} B/transition.");
        }

        [TestMethod]
        public void FocusConcurrentAdmissionCounterScenario()
        {
            FirstDelegateMeasurement firstDelegate = MeasureFirstWorkerDelegateAllocation();
            WriteConcurrentAdmissionCounter("focus-first-worker-delegate", "first-caller-allocated-bytes", firstDelegate.FirstCallerAllocatedBytes);
            WriteConcurrentAdmissionCounter("focus-first-worker-delegate", "warm-caller-allocated-bytes", firstDelegate.WarmCallerAllocatedBytes);
            WriteConcurrentAdmissionCounter("focus-first-worker-delegate", "lazy-delegate-bytes", firstDelegate.FirstCallerAllocatedBytes - firstDelegate.WarmCallerAllocatedBytes);
            WriteConcurrentAdmissionCounter("focus-first-worker-delegate", "admissions", 2);
            WriteConcurrentAdmissionCounter("focus-first-worker-delegate", "worker-starts", 2);
            WriteConcurrentAdmissionCounter("focus-first-worker-delegate", "worker-exits", 2);

            ConcurrentEnqueueMeasurement contention = MeasureContendedEnqueues(ContendedEnqueueCalls);
            AssertContendedEnqueueSemantics(contention, ContendedEnqueueCalls);
            WriteConcurrentAdmissionCounter("focus-concurrent-enqueue", "calls", contention.Calls);
            WriteConcurrentAdmissionCounter("focus-concurrent-enqueue", "accepted", contention.Accepted);
            WriteConcurrentAdmissionCounter("focus-concurrent-enqueue", "replaced", contention.Replaced);
            WriteConcurrentAdmissionCounter("focus-concurrent-enqueue", "true-results", contention.TrueResults);
            WriteConcurrentAdmissionCounter("focus-concurrent-enqueue", "false-results", contention.FalseResults);
            WriteConcurrentAdmissionCounter("focus-concurrent-enqueue", "worker-starts", contention.WorkerStarts);
            WriteConcurrentAdmissionCounter("focus-concurrent-enqueue", "worker-exits", contention.WorkerExits);
            WriteConcurrentAdmissionCounter("focus-concurrent-enqueue", "background-workers", contention.BackgroundWorkers);
            WriteConcurrentAdmissionCounter("focus-concurrent-enqueue", "overlapped-callers", contention.OverlappedCallers);
            WriteConcurrentAdmissionCounter("focus-concurrent-enqueue", "caller-allocated-bytes", contention.CallerAllocatedBytes);
            WriteConcurrentAdmissionCounter("focus-concurrent-enqueue", "total-allocated-bytes", contention.TotalAllocatedBytes);
            WriteConcurrentAdmissionCounter("focus-concurrent-enqueue", "lock-wait-lower-bound-ticks", contention.LockWaitLowerBoundTicks);
            WriteConcurrentAdmissionCounter("focus-concurrent-enqueue", "enqueue-elapsed-ticks", contention.EnqueueElapsedTicks);
            WriteConcurrentAdmissionCounter("focus-concurrent-enqueue", "timestamp-frequency", Stopwatch.Frequency);
            WriteConcurrentAdmissionCounter("focus-concurrent-enqueue", "queue-calls", contention.QueueCalls);
            WriteConcurrentAdmissionCounter("focus-concurrent-enqueue", "attach-calls", contention.AttachCalls);
            WriteConcurrentAdmissionCounter("focus-concurrent-enqueue", "detach-calls", contention.DetachCalls);
            WriteConcurrentAdmissionCounter("focus-concurrent-enqueue", "focus-calls", contention.FocusCalls);
            WriteConcurrentAdmissionCounter("focus-concurrent-enqueue", "owned-attachments", contention.OwnedAttachments);

            foreach (string mode in new[] { "direct-success", "alt-retry", "attach-failure" })
            {
                _ = MeasureWorkerOverlap(mode, 4);
                WorkerOverlapMeasurement measurement = MeasureWorkerOverlap(
                    mode, ConcurrentAdmissionCounterCalls);
                AssertWorkerOverlapSemantics(mode, measurement, ConcurrentAdmissionCounterCalls);
                string scenario = $"focus-worker-overlap-{mode}";
                WriteConcurrentAdmissionCounter(scenario, "admissions", measurement.Admissions);
                WriteConcurrentAdmissionCounter(scenario, "accepted", measurement.Accepted);
                WriteConcurrentAdmissionCounter(scenario, "superseded", measurement.Superseded);
                WriteConcurrentAdmissionCounter(scenario, "completion-callbacks", measurement.CompletionCallbacks);
                WriteConcurrentAdmissionCounter(scenario, "completion-order-checksum", measurement.CompletionOrderChecksum);
                WriteConcurrentAdmissionCounter(scenario, "true-results", measurement.TrueResults);
                WriteConcurrentAdmissionCounter(scenario, "false-results", measurement.FalseResults);
                WriteConcurrentAdmissionCounter(scenario, "latest-current", measurement.LatestCurrent);
                WriteConcurrentAdmissionCounter(scenario, "worker-starts", measurement.WorkerStarts);
                WriteConcurrentAdmissionCounter(scenario, "worker-exits", measurement.WorkerExits);
                WriteConcurrentAdmissionCounter(scenario, "background-workers", measurement.BackgroundWorkers);
                WriteConcurrentAdmissionCounter(scenario, "worker-thread-count", measurement.WorkerThreadCount);
                WriteConcurrentAdmissionCounter(scenario, "worker-transition-allocated-bytes", measurement.WorkerTransitionAllocatedBytes);
                WriteConcurrentAdmissionCounter(scenario, "worker-transition-min-bytes", measurement.WorkerTransitionMinBytes);
                WriteConcurrentAdmissionCounter(scenario, "worker-transition-max-bytes", measurement.WorkerTransitionMaxBytes);
                WriteConcurrentAdmissionCounter(scenario, "caller-allocated-bytes", measurement.CallerAllocatedBytes);
                WriteConcurrentAdmissionCounter(scenario, "total-allocated-bytes", measurement.TotalAllocatedBytes);
                WriteConcurrentAdmissionCounter(scenario, "enqueue-elapsed-ticks", measurement.EnqueueElapsedTicks);
                WriteConcurrentAdmissionCounter(scenario, "timestamp-frequency", Stopwatch.Frequency);
                WriteConcurrentAdmissionCounter(scenario, "queue-calls", measurement.QueueCalls);
                WriteConcurrentAdmissionCounter(scenario, "thread-id-reads", measurement.ThreadIdReads);
                WriteConcurrentAdmissionCounter(scenario, "foreground-id-reads", measurement.ForegroundIdReads);
                WriteConcurrentAdmissionCounter(scenario, "attach-calls", measurement.AttachCalls);
                WriteConcurrentAdmissionCounter(scenario, "detach-calls", measurement.DetachCalls);
                WriteConcurrentAdmissionCounter(scenario, "focus-calls", measurement.FocusCalls);
                WriteConcurrentAdmissionCounter(scenario, "alt-presses", measurement.AltPresses);
                WriteConcurrentAdmissionCounter(scenario, "owned-attachments", measurement.OwnedAttachments);
                WriteConcurrentAdmissionCounter(scenario, "native-timeouts", measurement.NativeTimeouts);
            }
        }

        private static FirstDelegateMeasurement MeasureFirstWorkerDelegateAllocation()
        {
            var jitFixture = new WorkerAdmissionFixture("direct-success");
            _ = MeasureWorkerAdmissions(jitFixture, 1, 1);

            var fixture = new WorkerAdmissionFixture("direct-success");
            WorkerAdmissionMeasurement first = MeasureWorkerAdmissions(fixture, 0, 1);
            WorkerAdmissionMeasurement warm = MeasureWorkerAdmissions(fixture, 0, 1);
            AssertWorkerAdmissionSemantics("direct-success", fixture, first, 1);
            AssertWorkerAdmissionSemantics("direct-success", fixture, warm, 1);
            return new(first.AllocatedBytes, warm.AllocatedBytes);
        }

        private static ConcurrentEnqueueMeasurement MeasureContendedEnqueues(int calls)
        {
            using var releaseCallers = new ManualResetEvent(false);
            using var ready = new CountdownEvent(calls);
            using var attempting = new CountdownEvent(calls);
            using var finished = new CountdownEvent(calls);
            var counters = new ConcurrentNativeCounters();
            var natives = Enumerable.Range(0, calls)
                .Select(index => new ConcurrentAdmissionNative(index, "direct-success", counters, null))
                .ToArray();
            var completions = new Task<bool>?[calls];
            var callerAllocated = new long[calls];
            var callStarted = new long[calls];
            var callFinished = new long[calls];
            Thread? worker = null;
            int workerStarts = 0;
            int backgroundWorkers = 0;
            var requests = new FocusHelper.RequestSequence(task => task.Wait(TimeSpan.FromSeconds(5)), thread =>
            {
                Interlocked.Increment(ref workerStarts);
                if (thread.IsBackground) Interlocked.Increment(ref backgroundWorkers);
                Interlocked.CompareExchange(ref worker, thread, null);
            });
            object token = requests.Begin();
            var callers = new Thread[calls];
            for (int index = 0; index < calls; index++)
            {
                int callerIndex = index;
                callers[index] = new Thread(() =>
                {
                    ready.Signal();
                    if (!releaseCallers.WaitOne(TimeSpan.FromSeconds(5))) return;
                    long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                    callStarted[callerIndex] = Stopwatch.GetTimestamp();
                    attempting.Signal();
                    completions[callerIndex] = requests.Enqueue(
                        token, new IntPtr(callerIndex + 1), natives[callerIndex]);
                    callFinished[callerIndex] = Stopwatch.GetTimestamp();
                    callerAllocated[callerIndex] = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                    finished.Signal();
                })
                {
                    IsBackground = true,
                    Name = $"FancyWM focus contention caller {index}",
                };
            }

            object gate = GetRequestSequenceGate(requests);
            long releaseTicks;
            long totalBefore;
            Monitor.Enter(gate);
            try
            {
                foreach (Thread caller in callers) caller.Start();
                Assert.IsTrue(ready.Wait(TimeSpan.FromSeconds(5)));
                totalBefore = GC.GetTotalAllocatedBytes(true);
                releaseCallers.Set();
                Assert.IsTrue(attempting.Wait(TimeSpan.FromSeconds(5)));
                Thread.Sleep(20);
                releaseTicks = Stopwatch.GetTimestamp();
            }
            finally
            {
                Monitor.Exit(gate);
            }

            Assert.IsTrue(finished.Wait(TimeSpan.FromSeconds(5)));
            foreach (Thread caller in callers)
                Assert.IsTrue(caller.Join(TimeSpan.FromSeconds(5)));
            Assert.IsNotNull(worker);
            int replacedBeforeStart = completions.Count(task => task!.IsCompletedSuccessfully && !task.Result);
            int pendingBeforeStart = completions.Count(task => !task!.IsCompleted);
            worker!.Start();
            Assert.IsTrue(worker.Join(TimeSpan.FromSeconds(5)));
            foreach (Task<bool>? completion in completions)
                Assert.IsTrue(SpinUntilCompleted(completion!, TimeSpan.FromSeconds(5)));
            long totalAllocated = GC.GetTotalAllocatedBytes(true) - totalBefore;

            int trueResults = completions.Count(task => task!.Result);
            int falseResults = completions.Length - trueResults;
            long lowerBound = 0;
            long elapsed = 0;
            int overlapped = 0;
            for (int index = 0; index < calls; index++)
            {
                lowerBound += releaseTicks - callStarted[index];
                elapsed += callFinished[index] - callStarted[index];
                if (callStarted[index] <= releaseTicks && callFinished[index] >= releaseTicks)
                    overlapped++;
            }
            ConcurrentNativeSnapshot native = counters.Read();
            return new(calls, calls, replacedBeforeStart, pendingBeforeStart,
                trueResults, falseResults, workerStarts, 1, backgroundWorkers,
                overlapped, callerAllocated.Sum(), totalAllocated, lowerBound, elapsed,
                native.QueueCalls, native.AttachCalls, native.DetachCalls,
                native.FocusCalls, native.OwnedAttachments);
        }

        private static WorkerOverlapMeasurement MeasureWorkerOverlap(string mode, int calls)
        {
            using var coordinator = new WorkerOverlapCoordinator(calls);
            var counters = new ConcurrentNativeCounters();
            var natives = Enumerable.Range(0, calls)
                .Select(index => new ConcurrentAdmissionNative(index,
                    index == calls - 1 ? mode : "direct-success", counters, coordinator))
                .ToArray();
            var completions = new Task<bool>?[calls];
            object? latestToken = null;
            Thread? worker = null;
            int workerStarts = 0;
            int backgroundWorkers = 0;
            var requests = new FocusHelper.RequestSequence(task => task.Wait(TimeSpan.FromSeconds(5)), thread =>
            {
                Interlocked.Increment(ref workerStarts);
                if (thread.IsBackground) Interlocked.Increment(ref backgroundWorkers);
                Interlocked.CompareExchange(ref worker, thread, null);
                thread.Start();
            });

            long totalBefore = GC.GetTotalAllocatedBytes(true);
            long callerAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long enqueueElapsed = 0;
            int completionCallbacks = 0;
            long completionOrderChecksum = 0;
            for (int index = 0; index < calls; index++)
            {
                long started = Stopwatch.GetTimestamp();
                latestToken = requests.Begin();
                completions[index] = requests.Enqueue(
                    latestToken, new IntPtr(index + 1), natives[index]);
                enqueueElapsed += Stopwatch.GetTimestamp() - started;

                if (index == 0)
                {
                    Assert.IsTrue(coordinator.WaitEntered(index, TimeSpan.FromSeconds(5)));
                    continue;
                }

                coordinator.Release(index - 1);
                Assert.IsTrue(SpinUntilCompleted(completions[index - 1]!, TimeSpan.FromSeconds(5)));
                completionCallbacks++;
                completionOrderChecksum += index - 1;
                Assert.IsTrue(coordinator.WaitEntered(index, TimeSpan.FromSeconds(5)));
            }

            coordinator.Release(calls - 1);
            Assert.IsTrue(SpinUntilCompleted(completions[calls - 1]!, TimeSpan.FromSeconds(5)));
            completionCallbacks++;
            completionOrderChecksum += calls - 1;
            Assert.IsNotNull(worker);
            Assert.IsTrue(worker!.Join(TimeSpan.FromSeconds(5)));
            long callerAllocated = GC.GetAllocatedBytesForCurrentThread() - callerAllocatedBefore;
            long totalAllocated = GC.GetTotalAllocatedBytes(true) - totalBefore;

            long[] samples = coordinator.ReadAllocationSamples();
            long transitionAllocated = samples[^1] - samples[0];
            long transitionMin = long.MaxValue;
            long transitionMax = long.MinValue;
            for (int index = 1; index < samples.Length; index++)
            {
                long delta = samples[index] - samples[index - 1];
                transitionMin = Math.Min(transitionMin, delta);
                transitionMax = Math.Max(transitionMax, delta);
            }
            ConcurrentNativeSnapshot native = counters.Read();
            int[] workerThreads = coordinator.ReadWorkerThreads();
            return new(calls, calls, calls - 1, completionCallbacks,
                completionOrderChecksum, completions.Count(task => task!.Result),
                completions.Count(task => !task!.Result),
                requests.IsCurrent(latestToken!) ? 1 : 0,
                workerStarts, 1, backgroundWorkers,
                workerThreads.Distinct().Count(), transitionAllocated,
                transitionMin, transitionMax, callerAllocated, totalAllocated,
                enqueueElapsed, native.QueueCalls, native.ThreadIdReads,
                native.ForegroundIdReads, native.AttachCalls, native.DetachCalls,
                native.FocusCalls, native.AltPresses, native.OwnedAttachments,
                coordinator.NativeTimeouts);
        }

        private static object GetRequestSequenceGate(FocusHelper.RequestSequence requests)
        {
            FieldInfo? field = typeof(FocusHelper.RequestSequence).GetField(
                "m_gate", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "The controlled contention fixture requires RequestSequence.m_gate.");
            object? gate = field!.GetValue(requests);
            Assert.IsNotNull(gate);
            return gate!;
        }

        private static bool SpinUntilCompleted(Task task, TimeSpan timeout)
        {
            long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
            while (!task.IsCompleted && Stopwatch.GetTimestamp() < deadline)
                Thread.Yield();
            return task.IsCompleted;
        }

        private static void AssertContendedEnqueueSemantics(
            ConcurrentEnqueueMeasurement measurement, int calls)
        {
            Assert.AreEqual(calls, measurement.Calls);
            Assert.AreEqual(calls, measurement.Accepted);
            Assert.AreEqual(calls - 1, measurement.Replaced);
            Assert.AreEqual(1, measurement.PendingBeforeStart);
            Assert.AreEqual(1, measurement.TrueResults);
            Assert.AreEqual(calls - 1, measurement.FalseResults);
            Assert.AreEqual(1, measurement.WorkerStarts);
            Assert.AreEqual(1, measurement.WorkerExits);
            Assert.AreEqual(1, measurement.BackgroundWorkers);
            Assert.AreEqual(calls, measurement.OverlappedCallers);
            Assert.IsTrue(measurement.LockWaitLowerBoundTicks > 0);
            Assert.IsTrue(measurement.EnqueueElapsedTicks >= measurement.LockWaitLowerBoundTicks);
            Assert.AreEqual(1, measurement.QueueCalls);
            Assert.AreEqual(1, measurement.AttachCalls);
            Assert.AreEqual(1, measurement.DetachCalls);
            Assert.AreEqual(1, measurement.FocusCalls);
            Assert.AreEqual(0, measurement.OwnedAttachments);
        }

        private static void AssertWorkerOverlapSemantics(
            string mode, WorkerOverlapMeasurement measurement, int calls)
        {
            int expectedTrue = mode == "attach-failure" ? 0 : 1;
            int expectedFocus = mode switch
            {
                "direct-success" => 1,
                "alt-retry" => 2,
                "attach-failure" => 0,
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
            };
            Assert.AreEqual(calls, measurement.Admissions);
            Assert.AreEqual(calls, measurement.Accepted);
            Assert.AreEqual(calls - 1, measurement.Superseded);
            Assert.AreEqual(calls, measurement.CompletionCallbacks);
            Assert.AreEqual((long)calls * (calls - 1) / 2, measurement.CompletionOrderChecksum);
            Assert.AreEqual(expectedTrue, measurement.TrueResults);
            Assert.AreEqual(calls - expectedTrue, measurement.FalseResults);
            Assert.AreEqual(1, measurement.LatestCurrent);
            Assert.AreEqual(1, measurement.WorkerStarts);
            Assert.AreEqual(1, measurement.WorkerExits);
            Assert.AreEqual(1, measurement.BackgroundWorkers);
            Assert.AreEqual(1, measurement.WorkerThreadCount);
            Assert.AreEqual(calls, measurement.QueueCalls);
            Assert.AreEqual(1, measurement.ThreadIdReads);
            Assert.AreEqual(1, measurement.ForegroundIdReads);
            Assert.AreEqual(1, measurement.AttachCalls);
            Assert.AreEqual(mode == "attach-failure" ? 0 : 1, measurement.DetachCalls);
            Assert.AreEqual(expectedFocus, measurement.FocusCalls);
            Assert.AreEqual(mode == "alt-retry" ? 1 : 0, measurement.AltPresses);
            Assert.AreEqual(0, measurement.OwnedAttachments);
            Assert.AreEqual(0, measurement.NativeTimeouts);
            Assert.IsTrue(measurement.WorkerTransitionAllocatedBytes >= 0);
            Assert.IsTrue(measurement.WorkerTransitionMinBytes >= 0);
            Assert.IsTrue(measurement.WorkerTransitionMaxBytes >= measurement.WorkerTransitionMinBytes);
        }

        private static void WriteConcurrentAdmissionCounter(string scenario, string metric, long value)
        {
            Console.WriteLine($"PERFCOUNTER {scenario} {metric} "
                + value.ToString(CultureInfo.InvariantCulture));
        }

        private readonly record struct FirstDelegateMeasurement(
            long FirstCallerAllocatedBytes, long WarmCallerAllocatedBytes);

        private readonly record struct ConcurrentEnqueueMeasurement(
            int Calls, int Accepted, int Replaced, int PendingBeforeStart,
            int TrueResults, int FalseResults, int WorkerStarts, int WorkerExits,
            int BackgroundWorkers, int OverlappedCallers, long CallerAllocatedBytes,
            long TotalAllocatedBytes, long LockWaitLowerBoundTicks,
            long EnqueueElapsedTicks, int QueueCalls, int AttachCalls,
            int DetachCalls, int FocusCalls, int OwnedAttachments);

        private readonly record struct WorkerOverlapMeasurement(
            int Admissions, int Accepted, int Superseded, int CompletionCallbacks,
            long CompletionOrderChecksum, int TrueResults, int FalseResults,
            int LatestCurrent, int WorkerStarts, int WorkerExits,
            int BackgroundWorkers, int WorkerThreadCount,
            long WorkerTransitionAllocatedBytes, long WorkerTransitionMinBytes,
            long WorkerTransitionMaxBytes, long CallerAllocatedBytes,
            long TotalAllocatedBytes, long EnqueueElapsedTicks, int QueueCalls,
            int ThreadIdReads, int ForegroundIdReads, int AttachCalls,
            int DetachCalls, int FocusCalls, int AltPresses,
            int OwnedAttachments, int NativeTimeouts);

        private readonly record struct ConcurrentNativeSnapshot(
            int QueueCalls, int ThreadIdReads, int ForegroundIdReads,
            int AttachCalls, int DetachCalls, int FocusCalls,
            int AltPresses, int OwnedAttachments);

        private sealed class ConcurrentNativeCounters
        {
            private int m_queueCalls;
            private int m_threadIdReads;
            private int m_foregroundIdReads;
            private int m_attachCalls;
            private int m_detachCalls;
            private int m_focusCalls;
            private int m_altPresses;
            private int m_ownedAttachments;

            internal void Queue() => Interlocked.Increment(ref m_queueCalls);
            internal void ThreadId() => Interlocked.Increment(ref m_threadIdReads);
            internal void ForegroundId() => Interlocked.Increment(ref m_foregroundIdReads);
            internal void Focus() => Interlocked.Increment(ref m_focusCalls);
            internal void Alt() => Interlocked.Increment(ref m_altPresses);
            internal void Attach(bool owned)
            {
                Interlocked.Increment(ref m_attachCalls);
                if (owned) Interlocked.Increment(ref m_ownedAttachments);
            }
            internal void Detach() { Interlocked.Increment(ref m_detachCalls); Interlocked.Decrement(ref m_ownedAttachments); }

            internal ConcurrentNativeSnapshot Read() => new(
                Volatile.Read(ref m_queueCalls), Volatile.Read(ref m_threadIdReads),
                Volatile.Read(ref m_foregroundIdReads), Volatile.Read(ref m_attachCalls),
                Volatile.Read(ref m_detachCalls), Volatile.Read(ref m_focusCalls),
                Volatile.Read(ref m_altPresses), Volatile.Read(ref m_ownedAttachments));
        }

        private sealed class ConcurrentAdmissionNative(
            int index, string mode, ConcurrentNativeCounters counters,
            WorkerOverlapCoordinator? coordinator) : FocusHelper.INative
        {
            private int m_focusAttempts;

            public bool SetForegroundWindow(IntPtr window)
            {
                counters.Focus();
                int attempt = Interlocked.Increment(ref m_focusAttempts);
                return mode != "alt-retry" || attempt == 2;
            }

            public void EnsureMessageQueue()
            {
                counters.Queue();
                coordinator?.Enter(index);
            }

            public uint CurrentThreadId { get { counters.ThreadId(); return 11; } }
            public uint ForegroundThreadId { get { counters.ForegroundId(); return 22; } }

            public bool AttachThreadInput(uint thread, uint foregroundThread, bool attach)
            {
                if (thread != 11 || foregroundThread != 22) return false;
                if (attach)
                {
                    bool attached = mode != "attach-failure";
                    counters.Attach(attached);
                    return attached;
                }
                counters.Detach();
                return true;
            }

            public void SendAltKeyPresses() => counters.Alt();
        }

        private sealed class WorkerOverlapCoordinator : IDisposable
        {
            private readonly ManualResetEvent[] m_entered;
            private readonly ManualResetEvent[] m_release;
            private readonly long[] m_allocationSamples;
            private readonly int[] m_workerThreads;
            private int m_nativeTimeouts;

            internal WorkerOverlapCoordinator(int calls)
            {
                m_entered = Enumerable.Range(0, calls).Select(_ => new ManualResetEvent(false)).ToArray();
                m_release = Enumerable.Range(0, calls).Select(_ => new ManualResetEvent(false)).ToArray();
                m_allocationSamples = new long[calls];
                m_workerThreads = new int[calls];
            }

            internal int NativeTimeouts => Volatile.Read(ref m_nativeTimeouts);

            internal void Enter(int index)
            {
                m_allocationSamples[index] = GC.GetAllocatedBytesForCurrentThread();
                m_workerThreads[index] = Environment.CurrentManagedThreadId;
                m_entered[index].Set();
                if (!m_release[index].WaitOne(TimeSpan.FromSeconds(5)))
                {
                    Interlocked.Increment(ref m_nativeTimeouts);
                    throw new TimeoutException("The controlled worker overlap release was not observed.");
                }
            }

            internal bool WaitEntered(int index, TimeSpan timeout) => m_entered[index].WaitOne(timeout);
            internal void Release(int index) => m_release[index].Set();
            internal long[] ReadAllocationSamples() => (long[])m_allocationSamples.Clone();
            internal int[] ReadWorkerThreads() => (int[])m_workerThreads.Clone();

            public void Dispose()
            {
                foreach (ManualResetEvent handle in m_entered) handle.Dispose();
                foreach (ManualResetEvent handle in m_release) handle.Dispose();
            }
        }
    }
}
