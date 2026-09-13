#nullable enable
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    public partial class AnimationThreadClockBoostTest
    {
        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task OwnedWaitFailureSettlesEveryAcceptedJobDisposesItsQueueAndPreservesThePrimaryError(bool releaseThrows)
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var waitError = new InvalidOperationException("owned wait failure");
            var releaseError = new InvalidOperationException("owned boost release failure");
            var native = new BoostNative((enable, _) => !enable && releaseThrows ? throw releaseError : 0,
                independentReferences: 3);
            var active = new FailureJob();
            var queued = new FailureJob();
            var owner = new AnimationThread(144, native.Boost, _ =>
            {
                entered.Set();
                release.Wait();
                throw waitError;
            });
            var worker = ShutdownWorker(owner);
            var queue = ShutdownQueue(owner);
            try
            {
                owner.Start(active);
                Assert.IsTrue(entered.Wait(ShutdownGuard));
                owner.Start(queued);
                Assert.IsFalse(owner.Completion.IsCompleted);
                release.Set();
                var actual = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => owner.Completion.WaitAsync(ShutdownGuard));
                Assert.AreSame(waitError, actual);
                Assert.IsTrue(worker.Join(ShutdownGuard));
                AssertFailureJobCancelledOnce(active);
                AssertFailureJobCancelledOnce(queued);
                Assert.AreEqual(0, active.Updates);
                Assert.AreEqual(0, queued.Updates);
                Assert.AreEqual(1, native.EnableCalls);
                Assert.AreEqual(1, native.DisableCalls);
                Assert.AreEqual(releaseThrows ? 4 : 3, native.OutstandingReferences);
                AssertQueueDisposed(queue);
                Assert.ThrowsException<ObjectDisposedException>(() => owner.Start(new FailureJob()));
                owner.Dispose();
                owner.Dispose();
            }
            finally
            {
                release.Set();
                owner.Dispose();
                Assert.IsTrue(worker.Join(ShutdownGuard));
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task OwnedSynchronousOrAsynchronousUpdateFailureSettlesItsQueuedSuccessorAndReportsTheOriginalAggregate(bool asynchronous)
        {
            using var entered = new ManualResetEventSlim();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var frameError = new InvalidOperationException("owned frame failure");
            var native = new ShutdownNative();
            var queued = new FailureJob();
            var failed = new FailureJob();
            var owner = new AnimationThread(144, native.Boost, _ => { native.Wait(); return 0; });
            var worker = ShutdownWorker(owner);
            var queue = ShutdownQueue(owner);
            failed.UpdateBody = () =>
            {
                owner.Start(queued);
                entered.Set();
                return asynchronous ? new ValueTask(release.Task) : throw frameError;
            };
            try
            {
                owner.Start(failed);
                Assert.IsTrue(entered.Wait(ShutdownGuard));
                if (asynchronous)
                {
                    Assert.IsFalse(owner.Completion.IsCompleted);
                    Assert.IsFalse(failed.Task.IsCompleted);
                    Assert.IsFalse(queued.Task.IsCompleted);
                    release.SetException(frameError);
                }
                var actual = await Assert.ThrowsExceptionAsync<AggregateException>(() => owner.Completion.WaitAsync(ShutdownGuard));
                Assert.AreSame(frameError, actual.InnerExceptions.Single());
                Assert.IsTrue(worker.Join(ShutdownGuard));
                AssertFailureJobCancelledOnce(failed);
                AssertFailureJobCancelledOnce(queued);
                Assert.AreEqual(1, failed.Updates);
                Assert.AreEqual(0, queued.Updates);
                Assert.AreEqual(1, native.Waits);
                Assert.AreEqual(1, native.EnableCalls);
                Assert.AreEqual(1, native.DisableCalls);
                Assert.AreEqual(0, native.OutstandingReferences);
                AssertQueueDisposed(queue);
            }
            finally
            {
                release.TrySetException(frameError);
                owner.Dispose();
                Assert.IsTrue(worker.Join(ShutdownGuard));
            }
        }

        [TestMethod]
        public async Task RunLoopFailureWaitsForEveryEnteredPeerBeforeCancellingJobsAndReleasingBoost()
        {
            using var queue = new BlockingCollection<IAnimationJob>();
            var failedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var peerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failedRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var peerRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var frameError = new InvalidOperationException("asynchronous peer failure");
            var native = new ShutdownNative();
            var failed = new FailureJob
            {
                UpdateBody = () => { failedEntered.SetResult(); return new(failedRelease.Task); },
            };
            var peer = new FailureJob
            {
                UpdateBody = () => { peerEntered.SetResult(); return new(peerRelease.Task); },
            };
            queue.Add(failed);
            queue.Add(peer);
            var running = Task.Run(() => AnimationThread.RunLoop(queue, new Stopwatch(), TimeSpan.FromSeconds(1d / 144),
                native.Boost, _ => { native.Wait(); return 0; }));
            try
            {
                await Task.WhenAll(failedEntered.Task, peerEntered.Task).WaitAsync(ShutdownGuard);
                failedRelease.SetException(frameError);
                Assert.IsFalse(running.IsCompleted, "A failed update cannot abandon a peer that has already entered.");
                Assert.IsFalse(failed.Task.IsCompleted);
                Assert.IsFalse(peer.Task.IsCompleted);
                Assert.AreEqual(0, failed.Cancellations);
                Assert.AreEqual(0, peer.Cancellations);
                Assert.AreEqual(1, native.OutstandingReferences);

                peer.Cancel();
                peerRelease.SetResult();
                var actual = await Assert.ThrowsExceptionAsync<AggregateException>(() => running.WaitAsync(ShutdownGuard));
                Assert.AreSame(frameError, actual.InnerExceptions.Single());
                AssertFailureJobCancelledOnce(failed);
                AssertFailureJobCancelledOnce(peer);
                Assert.AreEqual(1, peer.CancelCalls,
                    "The peer's successful cancellation callback must not run again when the failed frame retains its work item.");
                Assert.AreEqual(1, failed.Updates);
                Assert.AreEqual(1, peer.Updates);
                Assert.AreEqual(0, native.OutstandingReferences);
                Assert.IsTrue(queue.IsCompleted);
            }
            finally
            {
                failedRelease.TrySetException(frameError);
                peerRelease.TrySetResult();
                try { await running.WaitAsync(ShutdownGuard); }
                catch (AggregateException) { }
            }
        }

        [TestMethod]
        public void CleanupAttemptsEveryActiveAndQueuedOwnerAfterCancelAndTerminalCallbackFailures()
        {
            using var queue = new BlockingCollection<IAnimationJob>();
            var primaryError = new InvalidOperationException("primary wait failure");
            var cancelError = new InvalidOperationException("first cancellation request failure");
            var terminalError = new InvalidOperationException("first terminal callback failure");
            var queuedError = new InvalidOperationException("queued terminal callback failure");
            var first = new FailureJob { CancelError = cancelError, CancelledError = terminalError };
            var second = new FailureJob();
            var queued = new FailureJob { CancelledError = queuedError };
            var native = new ShutdownNative(independentReferences: 3);
            queue.Add(first);
            queue.Add(second);
            var actual = Assert.ThrowsException<InvalidOperationException>(() =>
                AnimationThread.RunLoop(queue, new Stopwatch(), TimeSpan.FromSeconds(1d / 144), native.Boost, _ =>
                {
                    queue.Add(queued);
                    throw primaryError;
                }));
            Assert.AreSame(primaryError, actual);
            AssertFailureJobCancelledOnce(first);
            AssertFailureJobCancelledOnce(second);
            AssertFailureJobCancelledOnce(queued);
            Assert.AreEqual(1, first.CancelCalls);
            Assert.AreEqual(1, second.CancelCalls);
            Assert.AreEqual(1, queued.CancelCalls);
            Assert.AreEqual(0, first.Updates + second.Updates + queued.Updates);
            Assert.IsTrue(queue.IsCompleted);
            Assert.AreEqual(1, native.EnableCalls);
            Assert.AreEqual(1, native.DisableCalls);
            Assert.AreEqual(3, native.OutstandingReferences);
        }

        [TestMethod]
        public async Task CooperativeStopReportsItsFirstCleanupFailureAndStillReleasesLaterJobsQueueAndBoost()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var cancelError = new InvalidOperationException("cooperative cleanup request failure");
            var terminalError = new InvalidOperationException("cooperative terminal callback failure");
            var first = new FailureJob { CancelError = cancelError, CancelledError = terminalError };
            var queued = new FailureJob();
            var native = new ShutdownNative();
            var owner = new AnimationThread(144, enable =>
            {
                int result = native.Boost(enable);
                if (enable)
                {
                    entered.Set();
                    release.Wait();
                }
                return result;
            }, _ => { native.Wait(); return 0; });
            var worker = ShutdownWorker(owner);
            var queue = ShutdownQueue(owner);
            Task? disposing = null;
            try
            {
                owner.Start(first);
                Assert.IsTrue(entered.Wait(ShutdownGuard));
                owner.Start(queued);
                disposing = Task.Run(owner.Dispose);
                ObserveClosedAdmission(queue);
                Assert.IsFalse(owner.Completion.IsCompleted);
                release.Set();
                await disposing.WaitAsync(ShutdownGuard);
                var actual = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => owner.Completion.WaitAsync(ShutdownGuard));
                Assert.AreSame(cancelError, actual, "The first shutdown error is retained when a later terminal callback also fails.");
                Assert.IsTrue(worker.Join(ShutdownGuard));
                AssertFailureJobCancelledOnce(first);
                AssertFailureJobCancelledOnce(queued);
                Assert.AreEqual(0, first.Updates + queued.Updates);
                Assert.AreEqual(0, native.Waits);
                Assert.AreEqual(1, native.EnableCalls);
                Assert.AreEqual(1, native.DisableCalls);
                Assert.AreEqual(0, native.OutstandingReferences);
                AssertQueueDisposed(queue);
            }
            finally
            {
                release.Set();
                owner.Dispose();
                if (disposing != null) await disposing.WaitAsync(ShutdownGuard);
                Assert.IsTrue(worker.Join(ShutdownGuard));
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task FrameStopBetweenDispatchesWaitsTheEnteredUpdateAndPreservesFinalProgress(bool finalProgress)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int stopped = 0;
            var active = new FailureJob
            {
                DurationValue = TimeSpan.FromMilliseconds(finalProgress ? 100 : 200),
                UpdateBody = () =>
                {
                    Volatile.Write(ref stopped, 1);
                    entered.SetResult();
                    return new(release.Task);
                },
            };
            var later = new FailureJob();
            var frame = new AnimationThread.Frame(new Stopwatch());
            frame.Jobs.Add(new(active, TimeSpan.FromMilliseconds(-100), default));
            frame.Jobs.Add(new(later, TimeSpan.FromMilliseconds(-100), default));
            var running = Task.Run(() => frame.UpdateFrame(() => Volatile.Read(ref stopped) != 0));
            try
            {
                await entered.Task.WaitAsync(ShutdownGuard);
                Assert.IsFalse(running.IsCompleted);
                Assert.IsFalse(active.Task.IsCompleted);
                Assert.IsFalse(later.Task.IsCompleted);
                Assert.AreEqual(1, active.Updates);
                Assert.AreEqual(0, later.Updates);
                active.Cancel();
                release.SetResult();
                await running.WaitAsync(ShutdownGuard);
                Assert.AreEqual(finalProgress ? 1d : .5d, active.LastProgress);
                Assert.AreEqual(finalProgress ? 1 : 0, active.Completions);
                Assert.AreEqual(finalProgress ? 0 : 1, active.Cancellations);
                Assert.AreEqual(finalProgress, active.Task.IsCompletedSuccessfully);
                Assert.AreEqual(!finalProgress, active.Task.IsCanceled);
                Assert.AreEqual(0, later.Updates);
                Assert.IsFalse(later.Task.IsCompleted, "RunLoop retains ownership of the undispatched item until terminal cleanup.");
                Assert.AreEqual(1, frame.Jobs.Count);
                Assert.AreSame(later, frame.Jobs.Single().Job);
            }
            finally
            {
                release.TrySetResult();
                await running.WaitAsync(ShutdownGuard);
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ReentrantDisposeDuringBoostAcquisitionStopsBeforeWaitAndReleasesOnlySuccessfulOwnership(bool enableSucceeds)
        {
            var native = new BoostNative((enable, _) => enable && !enableSucceeds ? FailedHResult : 0,
                independentReferences: 3);
            int waits = 0;
            AnimationThread? owner = null;
            owner = new AnimationThread(144, enable =>
            {
                int result = native.Boost(enable);
                if (enable) owner!.Dispose();
                return result;
            }, _ => { Interlocked.Increment(ref waits); return 0; });
            var worker = ShutdownWorker(owner);
            var queue = ShutdownQueue(owner);
            var job = new FailureJob();
            try
            {
                owner.Start(job);
                await ObserveWorkerExit(owner, worker);
                AssertFailureJobCancelledOnce(job);
                Assert.AreEqual(0, job.Updates);
                Assert.AreEqual(0, waits);
                Assert.AreEqual(1, native.EnableCalls);
                Assert.AreEqual(enableSucceeds ? 1 : 0, native.DisableCalls);
                Assert.AreEqual(3, native.OutstandingReferences);
                AssertQueueDisposed(queue);
            }
            finally
            {
                owner.Dispose();
                Assert.IsTrue(worker.Join(ShutdownGuard));
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void DurationFailureKeepsTheAlreadyAcceptedJobOwnedUntilCancellation(bool queueAlreadyCompleted)
        {
            using var queue = new BlockingCollection<IAnimationJob>();
            var error = new InvalidOperationException("duration getter failure");
            var failed = new FailureJob { DurationError = error };
            var queued = new FailureJob();
            var native = new ShutdownNative();
            queue.Add(queued);
            queue.Add(failed);
            if (queueAlreadyCompleted) queue.CompleteAdding();
            var actual = Assert.ThrowsException<InvalidOperationException>(() =>
                AnimationThread.RunLoop(queue, new Stopwatch(), TimeSpan.FromSeconds(1d / 144), native.Boost,
                    _ => { native.Wait(); return 0; }));
            Assert.AreSame(error, actual);
            AssertFailureJobCancelledOnce(failed);
            AssertFailureJobCancelledOnce(queued);
            Assert.AreEqual(0, failed.Updates + queued.Updates);
            Assert.AreEqual(0, native.Waits);
            Assert.AreEqual(0, native.EnableCalls);
            Assert.AreEqual(0, native.DisableCalls);
            Assert.IsTrue(queue.IsCompleted);
        }

        [TestMethod]
        public void ThrowingTaskGetterCannotSkipOtherAcceptedJobsOrBoostReleaseOrReplaceThePrimaryError()
        {
            using var queue = new BlockingCollection<IAnimationJob>();
            var primaryError = new InvalidOperationException("primary wait error before task getter failure");
            var taskError = new NotSupportedException("custom task getter failure");
            var first = new FailureJob { TaskError = taskError };
            var second = new FailureJob();
            var queued = new FailureJob();
            var native = new ShutdownNative(independentReferences: 3);
            queue.Add(first);
            queue.Add(second);
            var actual = Assert.ThrowsException<InvalidOperationException>(() =>
                AnimationThread.RunLoop(queue, new Stopwatch(), TimeSpan.FromSeconds(1d / 144), native.Boost, _ =>
                {
                    queue.Add(queued);
                    throw primaryError;
                }));
            Assert.AreSame(primaryError, actual);
            Assert.IsTrue(first.CompletionTask.IsCanceled,
                "An unreadable custom Task cannot prevent cancellation of the job the loop already owns.");
            Assert.AreEqual(1, first.CancelCalls);
            Assert.AreEqual(1, first.Cancellations);
            AssertFailureJobCancelledOnce(second);
            AssertFailureJobCancelledOnce(queued);
            Assert.AreEqual(0, first.Updates + second.Updates + queued.Updates);
            Assert.IsTrue(queue.IsCompleted);
            Assert.AreEqual(1, native.EnableCalls);
            Assert.AreEqual(1, native.DisableCalls);
            Assert.AreEqual(3, native.OutstandingReferences);
        }

        private static void AssertQueueDisposed(BlockingCollection<IAnimationJob> queue)
        {
            Assert.ThrowsException<ObjectDisposedException>(() => { _ = queue.Count; });
        }

        private static void AssertFailureJobCancelledOnce(FailureJob job)
        {
            Assert.IsTrue(job.Task.IsCanceled);
            Assert.AreEqual(1, job.Cancellations);
            Assert.AreEqual(0, job.Completions);
        }

        private sealed class FailureJob : IAnimationJob
        {
            private readonly TaskCompletionSource m_completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int m_cancelled;
            private int m_updates;
            private int m_cancellations;
            private int m_completions;
            private int m_cancelCalls;
            public bool IsCancelled => Volatile.Read(ref m_cancelled) != 0;
            public TimeSpan DurationValue { get; init; } = TimeSpan.FromDays(1);
            public TimeSpan Duration => DurationError is null ? DurationValue : throw DurationError;
            public Exception? DurationError { get; init; }
            public Exception? CancelError { get; init; }
            public Exception? CancelledError { get; init; }
            public Exception? TaskError { get; init; }
            public Task Task => TaskError is null ? m_completion.Task : throw TaskError;
            public Task CompletionTask => m_completion.Task;
            public Func<ValueTask>? UpdateBody { get; set; }
            public int Updates => Volatile.Read(ref m_updates);
            public int Cancellations => Volatile.Read(ref m_cancellations);
            public int Completions => Volatile.Read(ref m_completions);
            public int CancelCalls => Volatile.Read(ref m_cancelCalls);
            public double LastProgress { get; private set; }
            public ValueTask Update(double progress)
            {
                LastProgress = progress;
                Interlocked.Increment(ref m_updates);
                return UpdateBody?.Invoke() ?? ValueTask.CompletedTask;
            }
            public void Cancel()
            {
                Interlocked.Increment(ref m_cancelCalls);
                Volatile.Write(ref m_cancelled, 1);
                if (CancelError is not null) throw CancelError;
            }
            public void OnCompleted()
            {
                Interlocked.Increment(ref m_completions);
                m_completion.TrySetResult();
            }
            public void OnCancelled()
            {
                Interlocked.Increment(ref m_cancellations);
                m_completion.TrySetCanceled();
                if (CancelledError is not null) throw CancelledError;
            }
        }
    }
}
