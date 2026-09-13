#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public partial class AnimationThreadClockBoostTest
    {
        private const int FailedHResult = unchecked((int)0x80004005);

        [TestMethod]
        public void ActiveFramesKeepOneOwnedReferenceUntilTheLastJobFinishes()
        {
            var native = new BoostNative();
            using var boost = new AnimationThread.ClockBoost(native.Boost);
            for (int frame = 0; frame < 3; frame++)
            {
                boost.BeginFrame();
                Assert.AreEqual(1, native.OutstandingReferences);
                boost.EndFrame(hasPendingQueue: false, hasActiveJobs: frame < 2);
                Assert.AreEqual(frame < 2 ? 1 : 0, native.OutstandingReferences);
            }
            CollectionAssert.AreEqual(new[] { true, false }, native.Calls);
        }

        [TestMethod]
        public void QueuedSuccessorDoesNotAcquireASecondReferenceAfterTheCurrentJobFinishes()
        {
            var native = new BoostNative();
            using var boost = new AnimationThread.ClockBoost(native.Boost);
            boost.BeginFrame();
            boost.EndFrame(hasPendingQueue: true, hasActiveJobs: false);
            boost.BeginFrame();
            boost.EndFrame(hasPendingQueue: false, hasActiveJobs: false);
            Assert.AreEqual(0, native.OutstandingReferences);
            CollectionAssert.AreEqual(new[] { true, false }, native.Calls);
        }

        [TestMethod]
        public void RepeatedIdleAndActiveBurstsBalanceOnlyTheOwnedReference()
        {
            var native = new BoostNative(independentReferences: 3);
            var boost = new AnimationThread.ClockBoost(native.Boost);
            for (int cycle = 0; cycle < 100; cycle++)
            {
                boost.BeginFrame();
                boost.EndFrame(hasPendingQueue: true, hasActiveJobs: true);
                boost.BeginFrame();
                boost.EndFrame(hasPendingQueue: false, hasActiveJobs: false);
                Assert.AreEqual(3, native.OutstandingReferences, "References owned by other callers remain intact.");
            }
            boost.Dispose();
            boost.Dispose();
            Assert.AreEqual(100, native.EnableCalls);
            Assert.AreEqual(100, native.DisableCalls);
            Assert.AreEqual(3, native.OutstandingReferences);
        }

        [TestMethod]
        public void DisposeReleasesAnActiveReferenceOnceAndPreventsReacquisition()
        {
            var native = new BoostNative();
            var boost = new AnimationThread.ClockBoost(native.Boost);
            boost.BeginFrame();
            boost.Dispose();
            boost.Dispose();
            Assert.AreEqual(0, native.OutstandingReferences);
            CollectionAssert.AreEqual(new[] { true, false }, native.Calls);
            Assert.ThrowsException<ObjectDisposedException>(boost.BeginFrame);
            Assert.ThrowsException<ObjectDisposedException>(() => boost.EndFrame(false, false));
            CollectionAssert.AreEqual(new[] { true, false }, native.Calls);
        }

        [TestMethod]
        public void FailedEnableDoesNotReleaseAnotherOwnersReferenceAndRetriesOnTheNextFrame()
        {
            var native = new BoostNative(
                (enable, attempt) => enable && attempt == 1 ? FailedHResult : 0,
                independentReferences: 3);
            using var boost = new AnimationThread.ClockBoost(native.Boost);
            boost.BeginFrame();
            boost.EndFrame(hasPendingQueue: false, hasActiveJobs: false);
            Assert.AreEqual(3, native.OutstandingReferences);
            Assert.AreEqual(0, native.DisableCalls, "A failed acquisition does not own a reference to release.");
            boost.BeginFrame();
            boost.EndFrame(hasPendingQueue: false, hasActiveJobs: false);
            Assert.AreEqual(3, native.OutstandingReferences);
            CollectionAssert.AreEqual(new[] { true, true, false }, native.Calls);
        }

        [DataTestMethod]
        [DataRow(0)]
        [DataRow(1)]
        public void NonnegativeEnableResultOwnsExactlyOneReference(int successHResult)
        {
            var native = new BoostNative((enable, _) => enable ? successHResult : 0);
            using var boost = new AnimationThread.ClockBoost(native.Boost);
            boost.BeginFrame();
            boost.EndFrame(hasPendingQueue: false, hasActiveJobs: false);
            Assert.AreEqual(1, native.EnableCalls);
            Assert.AreEqual(1, native.DisableCalls);
            Assert.AreEqual(0, native.OutstandingReferences);
        }

        [TestMethod]
        public void ThrowingEnableDoesNotClaimOrReleaseAnotherOwnersReference()
        {
            var error = new InvalidOperationException("enable failed");
            var native = new BoostNative((enable, _) => enable ? throw error : 0, independentReferences: 3);
            var boost = new AnimationThread.ClockBoost(native.Boost);
            Assert.AreSame(error, Assert.ThrowsException<InvalidOperationException>(boost.BeginFrame));
            boost.Dispose();
            Assert.AreEqual(1, native.EnableCalls);
            Assert.AreEqual(0, native.DisableCalls);
            Assert.AreEqual(3, native.OutstandingReferences);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void FailedReleaseKeepsOwnershipAndTheNextFrameDoesNotAcquireAgain(bool throwOnRelease)
        {
            var error = new InvalidOperationException("release failed");
            var native = new BoostNative((enable, attempt) =>
                !enable && attempt == 1 ? (throwOnRelease ? throw error : FailedHResult) : 0);
            using var boost = new AnimationThread.ClockBoost(native.Boost);
            boost.BeginFrame();
            if (throwOnRelease)
            {
                Assert.AreSame(error, Assert.ThrowsException<InvalidOperationException>(() => boost.EndFrame(false, false)));
            }
            else
            {
                boost.EndFrame(false, false);
            }
            Assert.AreEqual(1, native.OutstandingReferences);
            boost.BeginFrame();
            Assert.AreEqual(1, native.EnableCalls, "The failed release must not create a second owned reference.");
            boost.EndFrame(false, false);
            Assert.AreEqual(0, native.OutstandingReferences);
            CollectionAssert.AreEqual(new[] { true, false, false }, native.Calls);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void DisposeRetriesFailedIdleReleaseOnceAndNeverLoops(bool exitReleaseSucceeds)
        {
            var native = new BoostNative((enable, attempt) =>
                enable || (attempt == 2 && exitReleaseSucceeds) ? 0 : FailedHResult);
            var boost = new AnimationThread.ClockBoost(native.Boost);
            boost.BeginFrame();
            boost.EndFrame(false, false);
            Assert.AreEqual(1, native.OutstandingReferences);
            boost.Dispose();
            boost.Dispose();
            Assert.AreEqual(exitReleaseSucceeds ? 0 : 1, native.OutstandingReferences,
                "A native release failure is reported as an outstanding reference, never as successful cleanup.");
            CollectionAssert.AreEqual(new[] { true, false, false }, native.Calls);
        }

        [TestMethod]
        public void EmptyCompletedLoopDoesNotAcquireBoostOrWaitForAFrame()
        {
            using var queue = new BlockingCollection<IAnimationJob>();
            queue.CompleteAdding();
            var native = new BoostNative();
            int waits = 0;
            AnimationThread.RunLoop(queue, new Stopwatch(), TimeSpan.FromSeconds(1d / 144),
                native.Boost, _ => { waits++; return 0; });
            Assert.AreEqual(0, native.Calls.Count);
            Assert.AreEqual(0, waits);
        }

        [TestMethod]
        public void ProductionLoopKeepsExactWaitsUpdatesAndCancellationWhenWorkArrivesDuringAFrame()
        {
            var native = new BoostNative();
            var observation = RunQueuedBurst(native);
            Assert.AreEqual(2, observation.Waits);
            Assert.AreEqual(2, observation.Updates);
            Assert.AreEqual(2, observation.Cancellations);
            Assert.AreEqual(2, observation.ZeroProgressUpdates);
            Assert.AreEqual(1, native.EnableCalls);
            Assert.AreEqual(1, native.DisableCalls);
            Assert.AreEqual(0, native.OutstandingReferences);
        }

        [TestMethod]
        public void ProductionLoopWithOneLongerJobKeepsOneBoostAcrossEveryFrame()
        {
            using var queue = new BlockingCollection<IAnimationJob>();
            var native = new BoostNative();
            int waits = 0;
            var job = new LoopJob("longer", cancelAfterUpdates: 3);
            job.AfterUpdate = () =>
            {
                if (job.IsCancelled) queue.CompleteAdding();
            };
            queue.Add(job);
            AnimationThread.RunLoop(queue, new Stopwatch(), TimeSpan.FromSeconds(1d / 144),
                native.Boost, timeout =>
                {
                    Assert.AreEqual(6u, timeout);
                    Assert.AreEqual(1, native.OutstandingReferences);
                    waits++;
                    return 0;
                });
            Assert.AreEqual(3, waits);
            Assert.AreEqual(3, job.Updates);
            Assert.AreEqual(1, job.Cancellations);
            Assert.IsTrue(job.Task.IsCanceled);
            CollectionAssert.AreEqual(new[] { true, false }, native.Calls);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void WaitFailurePreservesTheOriginalErrorWhenFinalReleaseAlsoFails(bool releaseThrows)
        {
            using var queue = new BlockingCollection<IAnimationJob>();
            var waitError = new InvalidOperationException("wait failed");
            var releaseError = new InvalidOperationException("release also failed");
            var native = new BoostNative((enable, _) => enable ? 0 : (releaseThrows ? throw releaseError : FailedHResult));
            var job = new LoopJob("unstarted", cancelAfterUpdates: 1);
            queue.Add(job);
            var actual = Assert.ThrowsException<InvalidOperationException>(() =>
                AnimationThread.RunLoop(queue, new Stopwatch(), TimeSpan.FromSeconds(1d / 144),
                    native.Boost, _ => throw waitError));
            Assert.AreSame(waitError, actual);
            Assert.AreEqual(0, job.Updates);
            Assert.IsTrue(job.Task.IsCanceled, "An accepted job must settle even when the compositor wait fails.");
            Assert.AreEqual(1, job.Cancellations);
            Assert.AreEqual(1, native.EnableCalls);
            Assert.AreEqual(1, native.DisableCalls, "Loop failure allows one bounded final cleanup attempt.");
            Assert.AreEqual(1, native.OutstandingReferences, "The fake release failed; no successful native cleanup is claimed.");
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void FrameFailureReleasesItsBoostWithQueuedWorkAndPreservesTheOriginalAggregate(bool releaseThrows)
        {
            using var queue = new BlockingCollection<IAnimationJob>();
            var frameError = new InvalidOperationException("frame failed");
            var releaseError = new InvalidOperationException("release also failed");
            var native = new BoostNative((enable, _) => !enable && releaseThrows ? throw releaseError : 0);
            var queued = new LoopJob("queued", cancelAfterUpdates: 1);
            var failed = new LoopJob("failed", cancelAfterUpdates: int.MaxValue)
            {
                AfterUpdate = () => { queue.Add(queued); throw frameError; },
            };
            queue.Add(failed);
            var actual = Assert.ThrowsException<AggregateException>(() =>
                AnimationThread.RunLoop(queue, new Stopwatch(), TimeSpan.FromSeconds(1d / 144), native.Boost, _ => 0));
            Assert.AreSame(frameError, actual.InnerExceptions.Single());
            Assert.AreEqual(1, failed.Updates);
            Assert.AreEqual(0, queued.Updates);
            Assert.IsTrue(failed.Task.IsCanceled);
            Assert.IsTrue(queued.Task.IsCanceled);
            Assert.AreEqual(1, failed.Cancellations);
            Assert.AreEqual(1, queued.Cancellations);
            Assert.AreEqual(1, native.EnableCalls);
            Assert.AreEqual(1, native.DisableCalls);
            Assert.AreEqual(releaseThrows ? 1 : 0, native.OutstandingReferences);
        }

        [TestMethod]
        public void AnimationClockBoostCounterScenario()
        {
            _ = RunQueuedBurst(new BoostNative()); // Identical untimed warmup outside counters.
            const int cycles = 100;
            var native = new BoostNative();
            int waits = 0;
            int updates = 0;
            int cancellations = 0;
            int zeroProgressUpdates = 0;
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                var observation = RunQueuedBurst(native);
                waits += observation.Waits;
                updates += observation.Updates;
                cancellations += observation.Cancellations;
                zeroProgressUpdates += observation.ZeroProgressUpdates;
            }
            // The same actual-loop fixture runs on the preserved seam and candidate.
            // The dedicated regressions above require balanced reference ownership.
            Assert.AreEqual(200, waits);
            Assert.AreEqual(200, updates);
            Assert.AreEqual(200, cancellations);
            Assert.AreEqual(200, zeroProgressUpdates);
            Console.WriteLine($"PERFCOUNTER animation-clock-boost cycles {cycles}");
            Console.WriteLine($"PERFCOUNTER animation-clock-boost waits {waits}");
            Console.WriteLine($"PERFCOUNTER animation-clock-boost updates {updates}");
            Console.WriteLine($"PERFCOUNTER animation-clock-boost cancellations {cancellations}");
            Console.WriteLine($"PERFCOUNTER animation-clock-boost enable-calls {native.EnableCalls}");
            Console.WriteLine($"PERFCOUNTER animation-clock-boost disable-calls {native.DisableCalls}");
            Console.WriteLine($"PERFCOUNTER animation-clock-boost remaining-references {native.OutstandingReferences}");
            Console.WriteLine($"PERFCOUNTER animation-clock-boost zero-progress-updates {zeroProgressUpdates}");
        }

        private static BurstObservation RunQueuedBurst(BoostNative native)
        {
            using var queue = new BlockingCollection<IAnimationJob>();
            var order = new List<string>();
            int waits = 0;
            var second = new LoopJob("second", 1) { AfterUpdate = queue.CompleteAdding };
            var first = new LoopJob("first", 1) { AfterUpdate = () => queue.Add(second) };
            first.OnEvent = order.Add;
            second.OnEvent = order.Add;
            queue.Add(first);
            AnimationThread.RunLoop(queue, new Stopwatch(), TimeSpan.FromSeconds(1d / 144), native.Boost,
                timeout =>
                {
                    Assert.AreEqual(6u, timeout, "The existing truncation of the 144Hz timeout stays unchanged.");
                    Assert.IsTrue(native.OutstandingReferences > 0, "Each frame runs while a boost request is held.");
                    order.Add("wait");
                    waits++;
                    return 0;
                });
            CollectionAssert.AreEqual(new[]
            {
                "wait", "update:first", "cancel:first", "wait", "update:second", "cancel:second",
            }, order);
            Assert.IsTrue(queue.IsCompleted);
            Assert.IsTrue(first.Task.IsCanceled);
            Assert.IsTrue(second.Task.IsCanceled);
            return new(waits, first.Updates + second.Updates, first.Cancellations + second.Cancellations,
                first.ZeroProgressUpdates + second.ZeroProgressUpdates);
        }

        private sealed record BurstObservation(int Waits, int Updates, int Cancellations, int ZeroProgressUpdates);

        private sealed class BoostNative(Func<bool, int, int>? result = null, int independentReferences = 0)
        {
            public List<bool> Calls { get; } = [];
            public int EnableCalls { get; private set; }
            public int DisableCalls { get; private set; }
            public int OutstandingReferences { get; private set; } = independentReferences;

            public int Boost(bool enable)
            {
                Calls.Add(enable);
                int attempt = enable ? ++EnableCalls : ++DisableCalls;
                int hr = result?.Invoke(enable, attempt) ?? 0;
                if (hr >= 0) OutstandingReferences += enable ? 1 : -1;
                return hr;
            }
        }

        private sealed class LoopJob(string name, int cancelAfterUpdates) : IAnimationJob
        {
            private readonly TaskCompletionSource m_completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool IsCancelled { get; private set; }
            public TimeSpan Duration => TimeSpan.FromSeconds(1);
            public Task Task => m_completion.Task;
            public int Updates { get; private set; }
            public int Cancellations { get; private set; }
            public int ZeroProgressUpdates { get; private set; }
            public Action? AfterUpdate { get; set; }
            public Action<string>? OnEvent { get; set; }

            public ValueTask Update(double progress)
            {
                Assert.AreEqual(0d, progress, "The stopped fixture clock makes every admitted frame exact and deterministic.");
                ZeroProgressUpdates++;
                Updates++;
                OnEvent?.Invoke($"update:{name}");
                if (Updates >= cancelAfterUpdates) Cancel();
                AfterUpdate?.Invoke();
                return ValueTask.CompletedTask;
            }

            public void Cancel() => IsCancelled = true;
            public void OnCompleted() => m_completion.TrySetResult();
            public void OnCancelled()
            {
                Cancellations++;
                OnEvent?.Invoke($"cancel:{name}");
                m_completion.TrySetCanceled();
            }
        }
    }
}
