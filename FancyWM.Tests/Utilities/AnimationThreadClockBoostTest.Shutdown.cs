#nullable enable
using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    public partial class AnimationThreadClockBoostTest
    {
        private static readonly TimeSpan ShutdownGuard = TimeSpan.FromSeconds(5);

        [TestMethod]
        public async Task DisposeDuringBlockedFrameSettlesAcceptedAndQueuedJobs()
        {
            using var entered = new ManualResetEventSlim();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var native = new ShutdownNative(independentReferences: 3);
            var active = new ShutdownJob { UpdateBody = () => { entered.Set(); return new(release.Task); } };
            var queued = new ShutdownJob();
            var owner = new AnimationThread(144, native.Boost, _ => { native.Wait(); return 0; });
            var worker = ShutdownWorker(owner);
            var queue = ShutdownQueue(owner);
            Task? disposing = null;
            try
            {
                owner.Start(active);
                Assert.IsTrue(entered.Wait(ShutdownGuard), "The accepted job must enter its controlled update.");
                owner.Start(queued);
                disposing = Task.Run(owner.Dispose);
                ObserveClosedAdmission(queue);

                Assert.IsFalse(owner.Completion.IsCompleted, "An entered update remains owned until its task returns.");
                Assert.IsFalse(active.Task.IsCompleted);
                Assert.IsFalse(queued.Task.IsCompleted);
                Assert.AreEqual(0, queued.Updates);
                Assert.AreEqual(4, native.OutstandingReferences, "The blocked frame still owns one boost reference.");

                release.TrySetResult();
                await disposing.WaitAsync(ShutdownGuard);
                await ObserveWorkerExit(owner, worker);

                AssertCancelledOnce(active);
                AssertCancelledOnce(queued);
                Assert.AreEqual(1, active.Updates);
                Assert.AreEqual(0, queued.Updates, "Shutdown must not dispatch an accepted queued successor.");
                Assert.AreEqual(1, native.Waits);
                Assert.AreEqual(1, native.EnableCalls);
                Assert.AreEqual(1, native.DisableCalls);
                Assert.AreEqual(3, native.OutstandingReferences);
            }
            finally
            {
                active.Cancel();
                queued.Cancel();
                release.TrySetResult();
                owner.Dispose();
                if (disposing != null) await disposing.WaitAsync(ShutdownGuard);
                Assert.IsTrue(worker.Join(ShutdownGuard), "The test must release and join its owned worker, including on a red run.");
            }
        }

        [TestMethod]
        public async Task DisposeDuringBlockedWaitStopsBeforeAnUnstartedFrameAndKeepsItsBoostUntilReturn()
        {
            var observation = await RunAnimationShutdownCycle(requireClosedAdmissionException: false);
            Assert.AreEqual(1, observation.Waits);
            Assert.AreEqual(0, observation.Updates, "A completed shutdown request must be checked after the entered wait returns.");
            Assert.AreEqual(0, observation.QueuedUpdates);
            Assert.AreEqual(2, observation.Cancellations);
            Assert.AreEqual(0, observation.SuccessfulJobs);
            Assert.AreEqual(1, observation.EnableCalls);
            Assert.AreEqual(1, observation.DisableCalls);
            Assert.AreEqual(0, observation.RemainingReferences);
            Assert.AreEqual(1, observation.WorkerExits);
        }

        [TestMethod]
        public async Task LateStartDuringShutdownThrowsObjectDisposedAndLeavesTheRejectedJobUnowned()
        {
            var observation = await RunAnimationShutdownCycle(requireClosedAdmissionException: true);
            Assert.AreEqual(1, observation.LateRejections);
            Assert.AreEqual(2, observation.AcceptedJobs);
        }

        [TestMethod]
        public async Task DisposeFromAnActiveUpdateStopsItsQueuedSuccessorWithoutJoiningItself()
        {
            var native = new ShutdownNative();
            var queued = new ShutdownJob();
            var active = new ShutdownJob();
            var owner = new AnimationThread(144, native.Boost, _ => { native.Wait(); return 0; });
            var worker = ShutdownWorker(owner);
            active.UpdateBody = () =>
            {
                owner.Start(queued);
                owner.Dispose();
                owner.Dispose();
                return ValueTask.CompletedTask;
            };
            try
            {
                owner.Start(active);
                await ObserveWorkerExit(owner, worker);
                AssertCancelledOnce(active);
                AssertCancelledOnce(queued);
                Assert.AreEqual(1, active.Updates);
                Assert.AreEqual(0, queued.Updates);
                Assert.AreEqual(1, native.Waits);
                Assert.AreEqual(1, native.EnableCalls);
                Assert.AreEqual(1, native.DisableCalls);
                Assert.AreEqual(0, native.OutstandingReferences);
            }
            finally
            {
                active.Cancel();
                queued.Cancel();
                owner.Dispose();
                Assert.IsTrue(worker.Join(ShutdownGuard));
            }
        }

        [TestMethod]
        public async Task OneHundredEmptyOwnersStopIdempotentlyWithoutEnteringTheCompositor()
        {
            var native = new ShutdownNative(independentReferences: 3);
            for (int cycle = 0; cycle < 100; cycle++)
            {
                var owner = new AnimationThread(144, native.Boost, _ => { native.Wait(); return 0; });
                var worker = ShutdownWorker(owner);
                try
                {
                    owner.Dispose();
                    owner.Dispose();
                    await ObserveWorkerExit(owner, worker);
                    owner.Dispose();
                    var rejected = new ShutdownJob();
                    Assert.ThrowsException<ObjectDisposedException>(() => owner.Start(rejected));
                    Assert.AreEqual(0, rejected.Updates);
                    Assert.AreEqual(0, rejected.Cancellations);
                    Assert.IsFalse(rejected.Task.IsCompleted, "A rejected job remains the caller's responsibility.");
                }
                finally
                {
                    owner.Dispose();
                    Assert.IsTrue(worker.Join(ShutdownGuard));
                }
            }
            Assert.AreEqual(0, native.Waits);
            Assert.AreEqual(0, native.EnableCalls);
            Assert.AreEqual(0, native.DisableCalls);
            Assert.AreEqual(3, native.OutstandingReferences);
        }

        [TestMethod]
        public async Task StartRacingDisposeSettlesEveryAcceptedJobAndRejectsEveryLateAdmission()
        {
            for (int cycle = 0; cycle < 100; cycle++)
            {
                using var startGate = new Barrier(3);
                var native = new ShutdownNative();
                var owner = new AnimationThread(144, native.Boost, _ => { native.Wait(); return 0; });
                var worker = ShutdownWorker(owner);
                var job = new ShutdownJob();
                bool accepted = false;
                Exception? admissionError = null;
                var starting = Task.Run(() =>
                {
                    Assert.IsTrue(startGate.SignalAndWait(ShutdownGuard));
                    try { owner.Start(job); accepted = true; }
                    catch (InvalidOperationException error) { admissionError = error; }
                });
                var stopping = Task.Run(() =>
                {
                    Assert.IsTrue(startGate.SignalAndWait(ShutdownGuard));
                    owner.Dispose();
                });
                try
                {
                    Assert.IsTrue(startGate.SignalAndWait(ShutdownGuard));
                    await Task.WhenAll(starting, stopping).WaitAsync(ShutdownGuard);
                    await ObserveWorkerExit(owner, worker);
                    if (accepted)
                    {
                        Assert.IsNull(admissionError);
                        AssertCancelledOnce(job);
                        Assert.IsTrue(job.Updates is 0 or 1);
                    }
                    else
                    {
                        Assert.IsInstanceOfType(admissionError, typeof(ObjectDisposedException));
                        Assert.AreEqual(0, job.Updates);
                        Assert.AreEqual(0, job.Cancellations);
                        Assert.IsFalse(job.Task.IsCompleted);
                    }
                    Assert.AreEqual(native.EnableCalls, native.DisableCalls);
                    Assert.AreEqual(0, native.OutstandingReferences);
                }
                finally
                {
                    job.Cancel();
                    owner.Dispose();
                    await Task.WhenAll(starting, stopping).WaitAsync(ShutdownGuard);
                    Assert.IsTrue(worker.Join(ShutdownGuard));
                }
            }
        }

        [TestMethod]
        public async Task AnimationShutdownCounterScenario()
        {
            _ = await RunAnimationShutdownCycle(requireClosedAdmissionException: false);
            const int cycles = 100;
            var total = new ShutdownObservation();
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                var observation = await RunAnimationShutdownCycle(requireClosedAdmissionException: false);
                total.AcceptedJobs += observation.AcceptedJobs;
                total.Waits += observation.Waits;
                total.Updates += observation.Updates;
                total.QueuedUpdates += observation.QueuedUpdates;
                total.Cancellations += observation.Cancellations;
                total.SuccessfulJobs += observation.SuccessfulJobs;
                total.EnableCalls += observation.EnableCalls;
                total.DisableCalls += observation.DisableCalls;
                total.RemainingReferences += observation.RemainingReferences;
                total.WorkerExits += observation.WorkerExits;
                total.LateRejections += observation.LateRejections;
            }
            Assert.AreEqual(200, total.AcceptedJobs);
            Assert.AreEqual(200, total.Cancellations);
            Assert.AreEqual(0, total.SuccessfulJobs);
            Assert.AreEqual(100, total.EnableCalls);
            Assert.AreEqual(100, total.DisableCalls);
            Assert.AreEqual(0, total.RemainingReferences);
            Assert.AreEqual(100, total.WorkerExits);
            Assert.AreEqual(100, total.LateRejections);
            Console.WriteLine($"PERFCOUNTER animation-shutdown cycles {cycles}");
            Console.WriteLine($"PERFCOUNTER animation-shutdown accepted-jobs {total.AcceptedJobs}");
            Console.WriteLine($"PERFCOUNTER animation-shutdown waits {total.Waits}");
            Console.WriteLine($"PERFCOUNTER animation-shutdown updates {total.Updates}");
            Console.WriteLine($"PERFCOUNTER animation-shutdown queued-updates {total.QueuedUpdates}");
            Console.WriteLine($"PERFCOUNTER animation-shutdown cancellations {total.Cancellations}");
            Console.WriteLine($"PERFCOUNTER animation-shutdown successful-jobs {total.SuccessfulJobs}");
            Console.WriteLine($"PERFCOUNTER animation-shutdown enable-calls {total.EnableCalls}");
            Console.WriteLine($"PERFCOUNTER animation-shutdown disable-calls {total.DisableCalls}");
            Console.WriteLine($"PERFCOUNTER animation-shutdown remaining-references {total.RemainingReferences}");
            Console.WriteLine($"PERFCOUNTER animation-shutdown worker-exits {total.WorkerExits}");
            Console.WriteLine($"PERFCOUNTER animation-shutdown late-rejections {total.LateRejections}");
        }

        private static async Task<ShutdownObservation> RunAnimationShutdownCycle(bool requireClosedAdmissionException)
        {
            using var waitEntered = new ManualResetEventSlim();
            using var waitRelease = new ManualResetEventSlim();
            var native = new ShutdownNative(independentReferences: 3);
            var active = new ShutdownJob();
            var queued = new ShutdownJob();
            var rejected = new ShutdownJob();
            var owner = new AnimationThread(144, native.Boost, _ =>
            {
                if (native.Wait() == 1)
                {
                    waitEntered.Set();
                    waitRelease.Wait(); // Always released in the owning test's finally.
                }
                return 0;
            });
            var worker = ShutdownWorker(owner);
            var queue = ShutdownQueue(owner);
            Task? disposing = null;
            try
            {
                owner.Start(active);
                Assert.IsTrue(waitEntered.Wait(ShutdownGuard));
                owner.Start(queued);
                disposing = Task.Run(owner.Dispose);
                ObserveClosedAdmission(queue);
                Assert.IsFalse(owner.Completion.IsCompleted, "An entered wait is not abandoned or reported as a stopped worker.");
                Assert.IsFalse(active.Task.IsCompleted);
                Assert.IsFalse(queued.Task.IsCompleted);
                Assert.AreEqual(4, native.OutstandingReferences);
                Exception? rejection = null;
                try { owner.Start(rejected); }
                catch (InvalidOperationException error) { rejection = error; }
                Assert.IsNotNull(rejection, "Queue closure must reject a late admission in both comparison variants.");
                if (requireClosedAdmissionException)
                {
                    Assert.IsInstanceOfType(rejection, typeof(ObjectDisposedException));
                }
                waitRelease.Set();
                await disposing.WaitAsync(ShutdownGuard);
                await ObserveWorkerExit(owner, worker);
                owner.Dispose();
                owner.Dispose();
                AssertCancelledOnce(active);
                AssertCancelledOnce(queued);
                Assert.AreEqual(0, rejected.Updates);
                Assert.AreEqual(0, rejected.Cancellations);
                Assert.IsFalse(rejected.Task.IsCompleted);
                Assert.AreEqual(3, native.OutstandingReferences, "Other callers' compositor references must survive every shutdown.");
                return new ShutdownObservation
                {
                    AcceptedJobs = 2,
                    Waits = native.Waits,
                    Updates = active.Updates + queued.Updates,
                    QueuedUpdates = queued.Updates,
                    Cancellations = active.Cancellations + queued.Cancellations,
                    SuccessfulJobs = active.Completions + queued.Completions,
                    EnableCalls = native.EnableCalls,
                    DisableCalls = native.DisableCalls,
                    RemainingReferences = native.OutstandingReferences - 3,
                    WorkerExits = 1,
                    LateRejections = 1,
                };
            }
            finally
            {
                active.Cancel();
                queued.Cancel();
                rejected.Cancel();
                waitRelease.Set();
                owner.Dispose();
                if (disposing != null) await disposing.WaitAsync(ShutdownGuard);
                Assert.IsTrue(worker.Join(ShutdownGuard), "No fake compositor worker may outlive the fixture, even on a red run.");
            }
        }

        private static void ObserveClosedAdmission(BlockingCollection<IAnimationJob> queue)
        {
            Assert.IsTrue(SpinWait.SpinUntil(() => queue.IsAddingCompleted, ShutdownGuard),
                "The disposer must close the existing queue before the controlled wait is released.");
        }

        private static async Task ObserveWorkerExit(AnimationThread owner, Thread worker)
        {
            await owner.Completion.WaitAsync(ShutdownGuard);
            Assert.IsTrue(worker.Join(ShutdownGuard), "Completion must correspond to eventual exit of the one owned worker.");
        }

        private static Thread ShutdownWorker(AnimationThread owner) => (Thread)typeof(AnimationThread)
            .GetField("m_thread", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

        private static BlockingCollection<IAnimationJob> ShutdownQueue(AnimationThread owner) => (BlockingCollection<IAnimationJob>)typeof(AnimationThread)
            .GetField("m_queue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

        private static void AssertCancelledOnce(ShutdownJob job)
        {
            Assert.IsTrue(job.Task.IsCanceled);
            Assert.AreEqual(1, job.Cancellations);
            Assert.AreEqual(0, job.Completions);
        }

        private sealed class ShutdownNative(int independentReferences = 0)
        {
            private int m_enableCalls;
            private int m_disableCalls;
            private int m_references = independentReferences;
            private int m_waits;
            public int EnableCalls => Volatile.Read(ref m_enableCalls);
            public int DisableCalls => Volatile.Read(ref m_disableCalls);
            public int OutstandingReferences => Volatile.Read(ref m_references);
            public int Waits => Volatile.Read(ref m_waits);
            public int Wait() => Interlocked.Increment(ref m_waits);
            public int Boost(bool enable)
            {
                if (enable)
                {
                    Interlocked.Increment(ref m_enableCalls);
                    Interlocked.Increment(ref m_references);
                }
                else
                {
                    Interlocked.Increment(ref m_disableCalls);
                    Interlocked.Decrement(ref m_references);
                }
                return 0;
            }
        }

        private sealed class ShutdownJob : IAnimationJob
        {
            private readonly TaskCompletionSource m_completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int m_cancelled;
            private int m_updates;
            private int m_cancellations;
            private int m_completions;
            public bool IsCancelled => Volatile.Read(ref m_cancelled) != 0;
            public TimeSpan Duration => TimeSpan.FromDays(1);
            public Task Task => m_completion.Task;
            public Func<ValueTask>? UpdateBody { get; set; }
            public int Updates => Volatile.Read(ref m_updates);
            public int Cancellations => Volatile.Read(ref m_cancellations);
            public int Completions => Volatile.Read(ref m_completions);
            public async ValueTask Update(double progress)
            {
                Interlocked.Increment(ref m_updates);
                if (UpdateBody != null) await UpdateBody();
                Cancel(); // Both historical and new policies can safely settle a dispatched fixture job.
            }
            public void Cancel() => Volatile.Write(ref m_cancelled, 1);
            public void OnCompleted()
            {
                Interlocked.Increment(ref m_completions);
                m_completion.TrySetResult();
            }
            public void OnCancelled()
            {
                Interlocked.Increment(ref m_cancellations);
                m_completion.TrySetCanceled();
            }
        }

        private sealed class ShutdownObservation
        {
            public int AcceptedJobs;
            public int Waits;
            public int Updates;
            public int QueuedUpdates;
            public int Cancellations;
            public int SuccessfulJobs;
            public int EnableCalls;
            public int DisableCalls;
            public int RemainingReferences;
            public int WorkerExits;
            public int LateRejections;
        }
    }
}
