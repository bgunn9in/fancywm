#nullable enable
using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    public partial class FocusHelperTest
    {
        private const int WorkerAdmissionRegressionWarmupCalls = 16;
        private const int WorkerAdmissionRegressionMeasuredCalls = 128;
        private const int WorkerAdmissionCounterWarmupCalls = 32;
        private const int WorkerAdmissionCounterMeasuredCalls = 500;

        // The old source measures 368 B/call in the common fixture. The cached
        // bound ThreadStart removes 64 B after warmup; keep 32 B of margin from
        // either value so Debug and Release enforce the same regression budget.
        private const long WorkerAdmissionAllocationBudgetPerCall = 336;

        [DataTestMethod]
        [DataRow("direct-success")]
        [DataRow("alt-retry")]
        [DataRow("attach-failure")]
        public void RepeatedCompletedWorkerAdmissionsPreserveSemantics(string mode)
        {
            var fixture = new WorkerAdmissionFixture(mode);
            WorkerAdmissionMeasurement measurement = MeasureWorkerAdmissions(
                fixture, WorkerAdmissionRegressionWarmupCalls,
                WorkerAdmissionRegressionMeasuredCalls);

            AssertWorkerAdmissionSemantics(
                mode, fixture, measurement, WorkerAdmissionRegressionMeasuredCalls);
        }

        [DataTestMethod]
        [DataRow("direct-success")]
        [DataRow("alt-retry")]
        [DataRow("attach-failure")]
        public void RepeatedCompletedWorkerAdmissionsStayWithinCallerAllocationBudget(string mode)
        {
            var fixture = new WorkerAdmissionFixture(mode);
            WorkerAdmissionMeasurement measurement = MeasureWorkerAdmissions(
                fixture, WorkerAdmissionRegressionWarmupCalls,
                WorkerAdmissionRegressionMeasuredCalls);

            AssertWorkerAdmissionSemantics(
                mode, fixture, measurement, WorkerAdmissionRegressionMeasuredCalls);
            long budget = checked(WorkerAdmissionAllocationBudgetPerCall
                * WorkerAdmissionRegressionMeasuredCalls);
            Assert.IsTrue(measurement.AllocatedBytes <= budget,
                $"Repeated admitted worker construction exceeds its caller allocation budget: "
                + $"mode={mode}, total={measurement.AllocatedBytes} B, "
                + $"per-call={(double)measurement.AllocatedBytes / WorkerAdmissionRegressionMeasuredCalls:F3} B, "
                + $"budget={WorkerAdmissionAllocationBudgetPerCall} B/call.");
        }

        [TestMethod]
        public void CompletedWorkerStartDelegateDoesNotRootRequestSequence()
        {
            WeakReference sequence = CreateReleasedWorkerAdmissionSequence();
            for (int attempt = 0; attempt < 5 && sequence.IsAlive; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Thread.Yield();
            }
            Assert.IsFalse(sequence.IsAlive,
                "A completed worker-start delegate must not externally root its RequestSequence owner.");
        }

        [TestMethod]
        public void FocusWorkerAdmissionCounterScenario()
        {
            foreach (string mode in new[] { "direct-success", "alt-retry", "attach-failure" })
            {
                var fixture = new WorkerAdmissionFixture(mode);
                WorkerAdmissionMeasurement measurement = MeasureWorkerAdmissions(
                    fixture, WorkerAdmissionCounterWarmupCalls,
                    WorkerAdmissionCounterMeasuredCalls);
                AssertWorkerAdmissionSemantics(
                    mode, fixture, measurement, WorkerAdmissionCounterMeasuredCalls);

                string scenario = $"focus-worker-{mode}";
                WriteWorkerAdmissionCounter(scenario, "allocated-bytes", measurement.AllocatedBytes);
                WriteWorkerAdmissionCounter(scenario, "elapsed-ticks", measurement.ElapsedTicks);
                WriteWorkerAdmissionCounter(scenario, "timestamp-frequency", Stopwatch.Frequency);
                WriteWorkerAdmissionCounter(scenario, "admissions", measurement.Admissions);
                WriteWorkerAdmissionCounter(scenario, "true-results", measurement.TrueResults);
                WriteWorkerAdmissionCounter(scenario, "current-results", measurement.CurrentResults);
                WriteWorkerAdmissionCounter(scenario, "worker-starts", measurement.WorkerStarts);
                WriteWorkerAdmissionCounter(scenario, "worker-exits", measurement.WorkerExits);
                WriteWorkerAdmissionCounter(scenario, "background-workers", measurement.BackgroundWorkers);
                WriteWorkerAdmissionCounter(scenario, "join-timeouts", measurement.JoinTimeouts);
                WriteWorkerAdmissionCounter(scenario, "wait-calls", measurement.WaitCalls);
                WriteWorkerAdmissionCounter(scenario, "queue-calls", measurement.QueueCalls);
                WriteWorkerAdmissionCounter(scenario, "thread-id-reads", measurement.ThreadIdReads);
                WriteWorkerAdmissionCounter(scenario, "foreground-id-reads", measurement.ForegroundIdReads);
                WriteWorkerAdmissionCounter(scenario, "attach-calls", measurement.AttachCalls);
                WriteWorkerAdmissionCounter(scenario, "detach-calls", measurement.DetachCalls);
                WriteWorkerAdmissionCounter(scenario, "focus-calls", measurement.FocusCalls);
                WriteWorkerAdmissionCounter(scenario, "alt-presses", measurement.AltPresses);
                WriteWorkerAdmissionCounter(scenario, "owned-attachments", measurement.OwnedAttachments);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference CreateReleasedWorkerAdmissionSequence()
        {
            var fixture = new WorkerAdmissionFixture("direct-success");
            WorkerAdmissionMeasurement measurement = MeasureWorkerAdmissions(fixture, 1, 1);
            AssertWorkerAdmissionSemantics("direct-success", fixture, measurement, 1);
            return new WeakReference(fixture.Requests);
        }

        private static WorkerAdmissionMeasurement MeasureWorkerAdmissions(
            WorkerAdmissionFixture fixture, int warmupCalls, int measuredCalls)
        {
            for (int index = 0; index < warmupCalls; index++)
            {
                _ = RunWorkerAdmission(fixture, index, out _);
            }

            WorkerAdmissionCounters before = fixture.ReadCounters();
            int trueResults = 0;
            int currentResults = 0;
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            for (int index = 0; index < measuredCalls; index++)
            {
                bool result = RunWorkerAdmission(fixture, index + warmupCalls, out bool current);
                if (result) trueResults++;
                if (current) currentResults++;
            }
            long elapsed = Stopwatch.GetTimestamp() - started;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            WorkerAdmissionCounters delta = fixture.ReadCounters() - before;
            GC.KeepAlive(fixture);
            return new(allocated, elapsed, measuredCalls, trueResults, currentResults, delta);
        }

        private static bool RunWorkerAdmission(
            WorkerAdmissionFixture fixture, int index, out bool current)
        {
            object token = fixture.Requests.Begin();
            Task<bool> completion = fixture.Requests.Enqueue(
                token, new IntPtr(index + 1), fixture.Native);
            if (!completion.IsCompleted)
            {
                throw new AssertFailedException(
                    "The synchronous test starter joined a worker whose request did not complete.");
            }
            bool result = completion.GetAwaiter().GetResult();
            current = fixture.Requests.IsCurrent(token);
            return result;
        }

        private static void AssertWorkerAdmissionSemantics(
            string mode, WorkerAdmissionFixture fixture,
            WorkerAdmissionMeasurement measurement, int calls)
        {
            int expectedTrue = mode == "attach-failure" ? 0 : calls;
            int expectedFocus = mode switch
            {
                "direct-success" => calls,
                "alt-retry" => checked(calls * 2),
                "attach-failure" => 0,
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
            };
            int expectedDetach = mode == "attach-failure" ? 0 : calls;
            int expectedAlt = mode == "alt-retry" ? calls : 0;

            Assert.AreEqual(calls, measurement.Admissions);
            Assert.AreEqual(expectedTrue, measurement.TrueResults);
            Assert.AreEqual(calls, measurement.CurrentResults);
            Assert.AreEqual(calls, measurement.WorkerStarts);
            Assert.AreEqual(calls, measurement.WorkerExits);
            Assert.AreEqual(calls, measurement.BackgroundWorkers);
            Assert.AreEqual(0, measurement.JoinTimeouts);
            Assert.AreEqual(0, measurement.WaitCalls);
            Assert.AreEqual(calls, measurement.QueueCalls);
            Assert.AreEqual(calls, measurement.ThreadIdReads);
            Assert.AreEqual(calls, measurement.ForegroundIdReads);
            Assert.AreEqual(calls, measurement.AttachCalls);
            Assert.AreEqual(expectedDetach, measurement.DetachCalls);
            Assert.AreEqual(expectedFocus, measurement.FocusCalls);
            Assert.AreEqual(expectedAlt, measurement.AltPresses);
            Assert.AreEqual(0, measurement.OwnedAttachments);
            Assert.AreEqual(0, fixture.ReadCounters().OwnedAttachments);
        }

        private static void WriteWorkerAdmissionCounter(
            string scenario, string metric, long value)
        {
            Console.WriteLine($"PERFCOUNTER {scenario} {metric} "
                + value.ToString(CultureInfo.InvariantCulture));
        }

        private readonly record struct WorkerAdmissionMeasurement(
            long AllocatedBytes,
            long ElapsedTicks,
            int Admissions,
            int TrueResults,
            int CurrentResults,
            WorkerAdmissionCounters Counters)
        {
            internal int WorkerStarts => Counters.WorkerStarts;
            internal int WorkerExits => Counters.WorkerExits;
            internal int BackgroundWorkers => Counters.BackgroundWorkers;
            internal int JoinTimeouts => Counters.JoinTimeouts;
            internal int WaitCalls => Counters.WaitCalls;
            internal int QueueCalls => Counters.QueueCalls;
            internal int ThreadIdReads => Counters.ThreadIdReads;
            internal int ForegroundIdReads => Counters.ForegroundIdReads;
            internal int AttachCalls => Counters.AttachCalls;
            internal int DetachCalls => Counters.DetachCalls;
            internal int FocusCalls => Counters.FocusCalls;
            internal int AltPresses => Counters.AltPresses;
            internal int OwnedAttachments => Counters.OwnedAttachments;
        }

        private readonly record struct WorkerAdmissionCounters(
            int WorkerStarts,
            int WorkerExits,
            int BackgroundWorkers,
            int JoinTimeouts,
            int WaitCalls,
            int QueueCalls,
            int ThreadIdReads,
            int ForegroundIdReads,
            int AttachCalls,
            int DetachCalls,
            int FocusCalls,
            int AltPresses,
            int OwnedAttachments)
        {
            public static WorkerAdmissionCounters operator -(
                WorkerAdmissionCounters right, WorkerAdmissionCounters left) => new(
                right.WorkerStarts - left.WorkerStarts,
                right.WorkerExits - left.WorkerExits,
                right.BackgroundWorkers - left.BackgroundWorkers,
                right.JoinTimeouts - left.JoinTimeouts,
                right.WaitCalls - left.WaitCalls,
                right.QueueCalls - left.QueueCalls,
                right.ThreadIdReads - left.ThreadIdReads,
                right.ForegroundIdReads - left.ForegroundIdReads,
                right.AttachCalls - left.AttachCalls,
                right.DetachCalls - left.DetachCalls,
                right.FocusCalls - left.FocusCalls,
                right.AltPresses - left.AltPresses,
                right.OwnedAttachments - left.OwnedAttachments);
        }

        private sealed class WorkerAdmissionFixture
        {
            internal WorkerAdmissionFixture(string mode)
            {
                Callbacks = new();
                Native = new(mode);
                Requests = new(Callbacks.Wait, Callbacks.StartAndJoin);
            }

            internal WorkerAdmissionCallbacks Callbacks { get; }
            internal WorkerAdmissionNative Native { get; }
            internal FocusHelper.RequestSequence Requests { get; }

            internal WorkerAdmissionCounters ReadCounters()
            {
                WorkerAdmissionCounters native = Native.ReadCounters();
                return new(
                    Callbacks.WorkerStarts,
                    Callbacks.WorkerExits,
                    Callbacks.BackgroundWorkers,
                    Callbacks.JoinTimeouts,
                    Callbacks.WaitCalls,
                    native.QueueCalls,
                    native.ThreadIdReads,
                    native.ForegroundIdReads,
                    native.AttachCalls,
                    native.DetachCalls,
                    native.FocusCalls,
                    native.AltPresses,
                    native.OwnedAttachments);
            }
        }

        private sealed class WorkerAdmissionCallbacks
        {
            private int m_workerStarts;
            private int m_workerExits;
            private int m_backgroundWorkers;
            private int m_joinTimeouts;
            private int m_waitCalls;

            internal int WorkerStarts => Volatile.Read(ref m_workerStarts);
            internal int WorkerExits => Volatile.Read(ref m_workerExits);
            internal int BackgroundWorkers => Volatile.Read(ref m_backgroundWorkers);
            internal int JoinTimeouts => Volatile.Read(ref m_joinTimeouts);
            internal int WaitCalls => Volatile.Read(ref m_waitCalls);

            internal bool Wait(Task<bool> task)
            {
                Interlocked.Increment(ref m_waitCalls);
                return task.IsCompleted;
            }

            internal void StartAndJoin(Thread thread)
            {
                Interlocked.Increment(ref m_workerStarts);
                if (thread.IsBackground)
                {
                    Interlocked.Increment(ref m_backgroundWorkers);
                }
                thread.Start();
                if (thread.Join(TimeSpan.FromSeconds(5)))
                {
                    Interlocked.Increment(ref m_workerExits);
                }
                else
                {
                    Interlocked.Increment(ref m_joinTimeouts);
                }
            }
        }

        private sealed class WorkerAdmissionNative : FocusHelper.INative
        {
            private readonly string m_mode;
            private int m_queueCalls;
            private int m_threadIdReads;
            private int m_foregroundIdReads;
            private int m_attachCalls;
            private int m_detachCalls;
            private int m_focusCalls;
            private int m_altPresses;
            private int m_ownedAttachments;

            internal WorkerAdmissionNative(string mode)
            {
                if (mode is not ("direct-success" or "alt-retry" or "attach-failure"))
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
                m_mode = mode;
            }

            public bool SetForegroundWindow(IntPtr window)
            {
                int call = Interlocked.Increment(ref m_focusCalls);
                return m_mode != "alt-retry" || (call & 1) == 0;
            }

            public void EnsureMessageQueue()
            {
                Interlocked.Increment(ref m_queueCalls);
            }

            public uint CurrentThreadId
            {
                get
                {
                    Interlocked.Increment(ref m_threadIdReads);
                    return 11;
                }
            }

            public uint ForegroundThreadId
            {
                get
                {
                    Interlocked.Increment(ref m_foregroundIdReads);
                    return 22;
                }
            }

            public bool AttachThreadInput(uint thread, uint foregroundThread, bool attach)
            {
                Assert.AreEqual(11U, thread);
                Assert.AreEqual(22U, foregroundThread);
                if (attach)
                {
                    Interlocked.Increment(ref m_attachCalls);
                    if (m_mode == "attach-failure") return false;
                    Interlocked.Increment(ref m_ownedAttachments);
                    return true;
                }
                Interlocked.Increment(ref m_detachCalls);
                return Interlocked.Decrement(ref m_ownedAttachments) == 0;
            }

            public void SendAltKeyPresses()
            {
                Interlocked.Increment(ref m_altPresses);
            }

            internal WorkerAdmissionCounters ReadCounters() => new(
                0,
                0,
                0,
                0,
                0,
                Volatile.Read(ref m_queueCalls),
                Volatile.Read(ref m_threadIdReads),
                Volatile.Read(ref m_foregroundIdReads),
                Volatile.Read(ref m_attachCalls),
                Volatile.Read(ref m_detachCalls),
                Volatile.Read(ref m_focusCalls),
                Volatile.Read(ref m_altPresses),
                Volatile.Read(ref m_ownedAttachments));
        }
    }
}
