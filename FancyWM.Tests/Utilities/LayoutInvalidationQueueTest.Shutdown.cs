#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using FancyWM.Utilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    public partial class LayoutInvalidationQueueTest
    {
        [TestMethod]
        public void CompletionStaysPendingUntilDisposeAndRetainsItsIdentity()
        {
            using var queue = new LayoutInvalidationQueue(_ => Assert.Fail("Unexpected post"),
                () => true, () => Task.CompletedTask, _ => Assert.Fail("Unexpected error"));
            var completion = queue.Completion;
            Assert.AreSame(completion, queue.Completion);
            Assert.IsFalse(completion.IsCompleted);

            queue.Dispose();
            for (int attempt = 0; attempt < 100; attempt++)
            {
                queue.Dispose();
                queue.Invalidate();
                Assert.AreSame(completion, queue.Completion);
            }
            Assert.IsTrue(completion.IsCompletedSuccessfully);
        }

        [TestMethod]
        public void DisposeBeforeQueuedCallbackCompletesWithoutInvokingLateDependencies()
        {
            var scheduled = new Queue<Action>();
            int eligibilityReads = 0;
            int applies = 0;
            int errors = 0;
            using var queue = new LayoutInvalidationQueue(scheduled.Enqueue, () =>
            {
                eligibilityReads++;
                return true;
            }, () =>
            {
                applies++;
                return Task.CompletedTask;
            }, _ => errors++);

            queue.Invalidate();
            Assert.AreEqual(1, scheduled.Count);
            Assert.AreEqual(1, eligibilityReads);
            queue.Dispose();
            Assert.IsTrue(queue.Completion.IsCompletedSuccessfully);
            for (int attempt = 0; attempt < 100; attempt++) { queue.Invalidate(); }
            scheduled.Dequeue()();

            Assert.AreEqual(1, eligibilityReads);
            Assert.AreEqual(0, applies);
            Assert.AreEqual(0, errors);
            Assert.AreEqual(0, scheduled.Count);
        }

        [TestMethod]
        public void DisposeKeepsCompletionPendingUntilEnteredApplyAndItsFinallyReturn()
        {
            var context = new QueueShutdownContext();
            var scheduled = new Queue<Action>();
            // Synchronous completion posts into the controlled context, avoiding
            // timing-dependent ThreadPool progress in the shutdown assertions.
            var release = new TaskCompletionSource();
            int finallyCalls = 0;
            using var queue = new LayoutInvalidationQueue(scheduled.Enqueue, () => true, async () =>
            {
                try { await release.Task; }
                finally { finallyCalls++; }
            }, exception => Assert.Fail(exception.ToString()));

            queue.Invalidate();
            context.Run(scheduled.Dequeue());
            try
            {
                queue.Invalidate();
                queue.Dispose();
                Assert.IsFalse(queue.Completion.IsCompleted,
                    "The entered apply still owns the resources required by its finally.");
                Assert.AreEqual(0, finallyCalls);
            }
            finally
            {
                queue.Dispose();
                release.TrySetResult();
                context.Drain();
            }

            Assert.AreEqual(1, finallyCalls);
            Assert.IsTrue(queue.Completion.IsCompletedSuccessfully);
            Assert.AreEqual(0, scheduled.Count);
        }

        [TestMethod]
        public void ReentrantDisposeInsideApplyDefersCompletionAndRejectsFurtherInvalidation()
        {
            var context = new QueueShutdownContext();
            var scheduled = new Queue<Action>();
            var release = new TaskCompletionSource();
            bool pendingInsideApply = false;
            int applies = 0;
            LayoutInvalidationQueue? queue = null;
            using (queue = new LayoutInvalidationQueue(scheduled.Enqueue, () => true, () =>
            {
                applies++;
                queue!.Dispose();
                pendingInsideApply = !queue.Completion.IsCompleted;
                queue.Invalidate();
                return release.Task;
            }, exception => Assert.Fail(exception.ToString())))
            {
                queue.Invalidate();
                context.Run(scheduled.Dequeue());
                try
                {
                    Assert.IsTrue(pendingInsideApply);
                    Assert.IsFalse(queue.Completion.IsCompleted);
                    queue.Dispose();
                    queue.Invalidate();
                    Assert.AreEqual(0, scheduled.Count);
                }
                finally
                {
                    release.TrySetResult();
                    context.Drain();
                }
                Assert.AreEqual(1, applies);
                Assert.IsTrue(queue.Completion.IsCompletedSuccessfully);
            }
        }

        [TestMethod]
        public void ConcurrentDisposeCannotCompleteWhileSynchronousApplyIsStillEntered()
        {
            var scheduled = new Queue<Action>();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            Exception? workerFailure = null;
            int applyExits = 0;
            using var queue = new LayoutInvalidationQueue(scheduled.Enqueue, () => true, () =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) { throw new TimeoutException("Apply release was not signaled."); }
                Interlocked.Increment(ref applyExits);
                return Task.CompletedTask;
            }, exception => workerFailure = exception);
            queue.Invalidate();
            var callback = scheduled.Dequeue();
            var worker = new Thread(() =>
            {
                try { callback(); }
                catch (Exception exception) { workerFailure = exception; }
            });
            worker.Start();
            try
            {
                Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
                for (int attempt = 0; attempt < 100; attempt++) { queue.Dispose(); }
                queue.Invalidate();
                Assert.IsFalse(queue.Completion.IsCompleted);
                Assert.AreEqual(0, Volatile.Read(ref applyExits));
            }
            finally
            {
                queue.Dispose();
                release.Set();
                Assert.IsTrue(worker.Join(TimeSpan.FromSeconds(5)), "Owned callback thread must exit.");
            }
            Assert.IsNull(workerFailure);
            Assert.AreEqual(1, applyExits);
            Assert.IsTrue(queue.Completion.IsCompletedSuccessfully);
            Assert.AreEqual(0, scheduled.Count);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void DisposeWaitsForAnEnteredErrorReporter(bool schedulerFailure)
        {
            var scheduled = new Queue<Action>();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var expected = new InvalidOperationException("Expected operation failure.");
            Exception? reported = null;
            Exception? workerFailure = null;
            int reports = 0;
            int reportExits = 0;
            using var queue = new LayoutInvalidationQueue(callback =>
            {
                if (schedulerFailure) { throw expected; }
                scheduled.Enqueue(callback);
            }, () => true, () => throw expected, exception =>
            {
                reported = exception;
                Interlocked.Increment(ref reports);
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) { throw new TimeoutException("Reporter release was not signaled."); }
                Interlocked.Increment(ref reportExits);
            });

            Action callback;
            if (schedulerFailure) { callback = queue.Invalidate; }
            else
            {
                queue.Invalidate();
                callback = scheduled.Dequeue();
            }
            var worker = new Thread(() =>
            {
                try { callback(); }
                catch (Exception exception) { workerFailure = exception; }
            });
            worker.Start();
            try
            {
                Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
                queue.Dispose();
                for (int attempt = 0; attempt < 100; attempt++) { queue.Invalidate(); }
                Assert.IsFalse(queue.Completion.IsCompleted);
                Assert.AreEqual(0, Volatile.Read(ref reportExits));
            }
            finally
            {
                queue.Dispose();
                release.Set();
                Assert.IsTrue(worker.Join(TimeSpan.FromSeconds(5)), "Owned reporter thread must exit.");
            }
            Assert.IsNull(workerFailure);
            Assert.AreSame(expected, reported);
            Assert.AreEqual(1, reports);
            Assert.AreEqual(1, reportExits);
            Assert.IsTrue(queue.Completion.IsCompletedSuccessfully);
            Assert.AreEqual(0, scheduled.Count);
        }

        [TestMethod]
        public void ThrowingSchedulerReporterDefersReentrantCompletionAndPreservesItsException()
        {
            var operationFailure = new InvalidOperationException("Scheduling failed.");
            var reporterFailure = new ArgumentException("Reporting failed.");
            Exception? reported = null;
            bool pendingInsideReporter = false;
            LayoutInvalidationQueue? queue = null;
            using (queue = new LayoutInvalidationQueue(_ => throw operationFailure, () => true,
                () => Task.CompletedTask, exception =>
                {
                    reported = exception;
                    queue!.Dispose();
                    pendingInsideReporter = !queue.Completion.IsCompleted;
                    throw reporterFailure;
                }))
            {
                var observed = Assert.ThrowsException<ArgumentException>(queue.Invalidate);
                Assert.AreSame(reporterFailure, observed);
                Assert.AreSame(operationFailure, reported);
                Assert.IsTrue(pendingInsideReporter);
                Assert.IsTrue(queue.Completion.IsCompletedSuccessfully);
            }
        }

        [TestMethod]
        public void ThrowingApplyReporterDrainsBeforeItsExceptionIsDeliveredToTheContext()
        {
            var context = new QueueShutdownContext();
            var scheduled = new Queue<Action>();
            var operationFailure = new InvalidOperationException("Apply failed.");
            var reporterFailure = new ArgumentException("Reporting failed.");
            Exception? reported = null;
            bool pendingInsideReporter = false;
            LayoutInvalidationQueue? queue = null;
            using (queue = new LayoutInvalidationQueue(scheduled.Enqueue, () => true,
                () => throw operationFailure, exception =>
                {
                    reported = exception;
                    queue!.Dispose();
                    pendingInsideReporter = !queue.Completion.IsCompleted;
                    throw reporterFailure;
                }))
            {
                queue.Invalidate();
                context.Run(scheduled.Dequeue());
                var observed = Assert.ThrowsException<ArgumentException>(context.Drain);
                Assert.AreSame(reporterFailure, observed);
                Assert.AreSame(operationFailure, reported);
                Assert.IsTrue(pendingInsideReporter);
                Assert.IsTrue(queue.Completion.IsCompletedSuccessfully);
                Assert.AreEqual(0, scheduled.Count);
            }
        }

        [TestMethod]
        public void ReentrantDisposeFromEligibilityPreventsTheApplyAndDrainsThePredicate()
        {
            var scheduled = new Queue<Action>();
            int eligibilityReads = 0;
            int applies = 0;
            bool pendingInsidePredicate = false;
            LayoutInvalidationQueue? queue = null;
            using (queue = new LayoutInvalidationQueue(scheduled.Enqueue, () =>
            {
                if (++eligibilityReads == 2)
                {
                    queue!.Dispose();
                    pendingInsidePredicate = !queue.Completion.IsCompleted;
                }
                return true;
            }, () =>
            {
                applies++;
                return Task.CompletedTask;
            }, exception => Assert.Fail(exception.ToString())))
            {
                queue.Invalidate();
                scheduled.Dequeue()();
                Assert.IsTrue(pendingInsidePredicate);
                Assert.AreEqual(0, applies);
                Assert.AreEqual(2, eligibilityReads);
                Assert.AreEqual(0, scheduled.Count);
                Assert.IsTrue(queue.Completion.IsCompletedSuccessfully);
            }
        }

        [TestMethod]
        public void OneHundredDisposedPendingPassesDrainExactlyOnceWithoutLateWork()
        {
            int applies = 0;
            int exits = 0;
            int errors = 0;
            for (int cycle = 0; cycle < 100; cycle++)
            {
                var context = new QueueShutdownContext();
                var scheduled = new Queue<Action>();
                var release = new TaskCompletionSource();
                using var queue = new LayoutInvalidationQueue(scheduled.Enqueue, () => true, async () =>
                {
                    applies++;
                    await release.Task;
                    exits++;
                }, _ => errors++);
                queue.Invalidate();
                context.Run(scheduled.Dequeue());
                var completion = queue.Completion;
                try
                {
                    queue.Invalidate();
                    queue.Dispose();
                    queue.Dispose();
                    Assert.IsFalse(completion.IsCompleted, $"Cycle {cycle}: entered pass must drain.");
                    for (int late = 0; late < 100; late++) { queue.Invalidate(); }
                }
                finally
                {
                    queue.Dispose();
                    release.TrySetResult();
                    context.Drain();
                }
                Assert.AreSame(completion, queue.Completion);
                Assert.IsTrue(completion.IsCompletedSuccessfully);
                Assert.AreEqual(0, scheduled.Count);
                Assert.AreEqual(cycle + 1, exits);
            }
            Assert.AreEqual(100, applies);
            Assert.AreEqual(100, exits);
            Assert.AreEqual(0, errors);
        }

        private sealed class QueueShutdownContext : SynchronizationContext
        {
            private readonly Queue<(SendOrPostCallback Callback, object? State)> m_callbacks = new();

            public override void Post(SendOrPostCallback callback, object? state)
            {
                lock (m_callbacks) { m_callbacks.Enqueue((callback, state)); }
            }

            public void Run(Action callback)
            {
                var previous = Current;
                SetSynchronizationContext(this);
                try { callback(); }
                finally { SetSynchronizationContext(previous); }
            }

            public void Drain()
            {
                while (true)
                {
                    (SendOrPostCallback Callback, object? State) next;
                    lock (m_callbacks)
                    {
                        if (m_callbacks.Count == 0) { return; }
                        next = m_callbacks.Dequeue();
                    }
                    Run(() => next.Callback(next.State));
                }
            }
        }
    }
}
