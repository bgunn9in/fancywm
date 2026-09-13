#nullable enable
using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    public partial class FocusHelperTest
    {
        private const int RejectedAdmissionWarmupCalls = 1_000;
        private const int RejectedAdmissionMeasuredCalls = 100_000;

        [DataTestMethod]
        [DataRow("superseded")]
        [DataRow("expired")]
        [DataRow("stopped")]
        public void RejectedFallbackAdmissionAllocatesNoRequest(string mode)
        {
            var fixture = new RejectedAdmissionFixture(mode);
            RejectedAdmissionMeasurement measurement = MeasureRejectedAdmission(
                fixture, RejectedAdmissionWarmupCalls, RejectedAdmissionMeasuredCalls);

            AssertRejectedAdmissionSemantics(fixture, measurement, RejectedAdmissionMeasuredCalls);
            Assert.AreEqual(0L, measurement.AllocatedBytes,
                $"Rejected fallback admission still allocates a request: mode={mode}, "
                + $"total={measurement.AllocatedBytes} B, "
                + $"per-call={(double)measurement.AllocatedBytes / RejectedAdmissionMeasuredCalls:F3} B.");
        }

        [TestMethod]
        public void FocusRejectedAdmissionCounterScenario()
        {
            foreach (string mode in new[] { "superseded", "expired", "stopped" })
            {
                var fixture = new RejectedAdmissionFixture(mode);
                RejectedAdmissionMeasurement measurement = MeasureRejectedAdmission(
                    fixture, RejectedAdmissionWarmupCalls, RejectedAdmissionMeasuredCalls);
                AssertRejectedAdmissionSemantics(fixture, measurement, RejectedAdmissionMeasuredCalls);

                string scenario = $"focus-rejected-{mode}";
                WriteRejectedAdmissionCounter(scenario, "allocated-bytes", measurement.AllocatedBytes);
                WriteRejectedAdmissionCounter(scenario, "elapsed-ticks", measurement.ElapsedTicks);
                WriteRejectedAdmissionCounter(scenario, "timestamp-frequency", Stopwatch.Frequency);
                WriteRejectedAdmissionCounter(scenario, "calls", RejectedAdmissionMeasuredCalls);
                WriteRejectedAdmissionCounter(scenario, "rejected", measurement.Rejected);
                WriteRejectedAdmissionCounter(scenario, "native-calls", fixture.Native.Calls);
                WriteRejectedAdmissionCounter(scenario, "worker-starts", fixture.Callbacks.WorkerStarts);
                WriteRejectedAdmissionCounter(scenario, "wait-calls", fixture.Callbacks.WaitCalls);
                WriteRejectedAdmissionCounter(scenario, "current-preserved", measurement.CurrentPreserved);
            }
        }

        private static RejectedAdmissionMeasurement MeasureRejectedAdmission(
            RejectedAdmissionFixture fixture, int warmupCalls, int measuredCalls)
        {
            for (int index = 0; index < warmupCalls; index++)
            {
                _ = fixture.Requests.Enqueue(fixture.RejectedToken, new IntPtr(index + 1), fixture.Native);
            }

            int rejected = 0;
            int currentPreserved = 0;
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            for (int index = 0; index < measuredCalls; index++)
            {
                Task<bool> task = fixture.Requests.Enqueue(
                    fixture.RejectedToken, new IntPtr(index + 1), fixture.Native);
                if (task.IsCompletedSuccessfully && !task.Result)
                {
                    rejected++;
                }
                if (fixture.IsCurrentStatePreserved())
                {
                    currentPreserved++;
                }
            }
            long elapsed = Stopwatch.GetTimestamp() - started;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            GC.KeepAlive(fixture);
            return new(allocated, elapsed, rejected, currentPreserved);
        }

        private static void AssertRejectedAdmissionSemantics(
            RejectedAdmissionFixture fixture, RejectedAdmissionMeasurement measurement, int calls)
        {
            Assert.AreEqual(calls, measurement.Rejected);
            Assert.AreEqual(calls, measurement.CurrentPreserved);
            Assert.AreEqual(0, fixture.Native.Calls);
            Assert.AreEqual(0, fixture.Callbacks.WorkerStarts);
            Assert.AreEqual(0, fixture.Callbacks.WaitCalls);
            Assert.IsTrue(fixture.IsCurrentStatePreserved());
        }

        private static void WriteRejectedAdmissionCounter(string scenario, string metric, long value)
        {
            Console.WriteLine($"PERFCOUNTER {scenario} {metric} "
                + value.ToString(CultureInfo.InvariantCulture));
        }

        private readonly record struct RejectedAdmissionMeasurement(
            long AllocatedBytes, long ElapsedTicks, int Rejected, int CurrentPreserved);

        private sealed class RejectedAdmissionFixture
        {
            private readonly object? m_currentToken;
            private readonly object? m_postStopToken;

            internal RejectedAdmissionFixture(string mode)
            {
                Callbacks = new();
                Native = new();
                Requests = new(Callbacks.Wait, Callbacks.Start);
                RejectedToken = Requests.Begin();
                switch (mode)
                {
                    case "superseded":
                        m_currentToken = Requests.Begin();
                        break;
                    case "expired":
                        Requests.Expire(RejectedToken);
                        m_currentToken = Requests.Begin();
                        break;
                    case "stopped":
                        Requests.Stop();
                        m_postStopToken = Requests.Begin();
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
                }
            }

            internal RejectedAdmissionCallbacks Callbacks { get; }
            internal RejectedAdmissionNative Native { get; }
            internal FocusHelper.RequestSequence Requests { get; }
            internal object RejectedToken { get; }

            internal bool IsCurrentStatePreserved()
            {
                if (m_currentToken != null)
                {
                    return Requests.IsCurrent(m_currentToken)
                        && !Requests.IsCurrent(RejectedToken);
                }
                return m_postStopToken != null
                    && !Requests.IsCurrent(RejectedToken)
                    && !Requests.IsCurrent(m_postStopToken);
            }
        }

        private sealed class RejectedAdmissionCallbacks
        {
            private int m_waitCalls;
            private int m_workerStarts;

            internal int WaitCalls => Volatile.Read(ref m_waitCalls);
            internal int WorkerStarts => Volatile.Read(ref m_workerStarts);

            internal bool Wait(Task<bool> task)
            {
                Interlocked.Increment(ref m_waitCalls);
                return task.IsCompleted;
            }

            internal void Start(Thread thread)
            {
                Interlocked.Increment(ref m_workerStarts);
            }
        }

        private sealed class RejectedAdmissionNative : FocusHelper.INative
        {
            private int m_calls;

            internal int Calls => Volatile.Read(ref m_calls);

            public bool SetForegroundWindow(IntPtr window)
            {
                Interlocked.Increment(ref m_calls);
                return false;
            }

            public void EnsureMessageQueue()
            {
                Interlocked.Increment(ref m_calls);
            }

            public uint CurrentThreadId
            {
                get
                {
                    Interlocked.Increment(ref m_calls);
                    return 11;
                }
            }

            public uint ForegroundThreadId
            {
                get
                {
                    Interlocked.Increment(ref m_calls);
                    return 22;
                }
            }

            public bool AttachThreadInput(uint thread, uint foregroundThread, bool attach)
            {
                Interlocked.Increment(ref m_calls);
                return true;
            }

            public void SendAltKeyPresses()
            {
                Interlocked.Increment(ref m_calls);
            }
        }
    }
}
