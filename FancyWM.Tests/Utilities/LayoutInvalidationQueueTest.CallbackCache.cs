#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using FancyWM.Utilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    public partial class LayoutInvalidationQueueTest
    {
        [TestMethod]
        public void CallbackCacheReusesScheduledDelegateAcrossCompletedPasses()
        {
            var trace = new List<string>();
            var scheduler = new CallbackCacheScheduler(trace);
            var expectedFailure = new InvalidOperationException("Expected apply failure.");
            Exception? reported = null;
            bool enabled = true;
            int eligibilityReads = 0;
            int applies = 0;
            int errors = 0;
            LayoutInvalidationQueue? queue = null;
            using (queue = new LayoutInvalidationQueue(scheduler.Post, () =>
            {
                eligibilityReads++;
                trace.Add("eligible");
                return enabled;
            }, () =>
            {
                applies++;
                trace.Add($"apply:{applies}");
                if (applies == 1)
                {
                    queue!.Invalidate();
                    queue.Invalidate();
                }
                if (applies == 2) { throw expectedFailure; }
                return Task.CompletedTask;
            }, exception =>
            {
                errors++;
                reported = exception;
                trace.Add("error");
            }))
            {
                var completion = queue.Completion;
                Assert.IsFalse(completion.IsCompleted);
                queue.Invalidate();
                queue.Invalidate();
                var firstCallback = scheduler.PendingCallback;
                Assert.IsNotNull(firstCallback);
                Assert.AreEqual(1, scheduler.Posts);

                scheduler.RunNext();
                bool sameFollowupCallback = ReferenceEquals(firstCallback, scheduler.PendingCallback);
                Assert.AreEqual(1, applies);
                Assert.AreEqual(2, scheduler.Posts);
                scheduler.RunNext();
                Assert.IsNull(scheduler.PendingCallback);
                Assert.IsFalse(completion.IsCompleted);

                enabled = false;
                queue.Invalidate();
                Assert.IsNull(scheduler.PendingCallback);
                enabled = true;
                queue.Invalidate();
                bool sameRecoveryCallback = ReferenceEquals(firstCallback, scheduler.PendingCallback);
                scheduler.RunNext();
                Assert.IsNull(scheduler.PendingCallback);
                Assert.IsFalse(completion.IsCompleted);

                queue.Invalidate();
                bool sameLateCallback = ReferenceEquals(firstCallback, scheduler.PendingCallback);
                queue.Dispose();
                Assert.IsTrue(completion.IsCompletedSuccessfully);
                queue.Invalidate();
                scheduler.RunNext();
                queue.Dispose();

                Assert.AreSame(completion, queue.Completion);
                Assert.IsTrue(completion.IsCompletedSuccessfully);
                Assert.IsNull(scheduler.PendingCallback);
                Assert.AreEqual(4, scheduler.Posts);
                Assert.AreEqual(3, applies);
                Assert.AreEqual(8, eligibilityReads);
                Assert.AreEqual(1, errors);
                Assert.AreSame(expectedFailure, reported);
                CollectionAssert.AreEqual(new[]
                {
                    "eligible", "post", "dispatch", "eligible", "apply:1",
                    "eligible", "post", "dispatch", "eligible", "apply:2", "error",
                    "eligible", "eligible", "post", "dispatch", "eligible", "apply:3",
                    "eligible", "post", "dispatch",
                }, trace);
                Assert.IsTrue(sameFollowupCallback,
                    "A completed pass must schedule its dirty follow-up with the same delegate instance.");
                Assert.IsTrue(sameRecoveryCallback,
                    "A later eligible invalidation must reuse the callback after an apply failure.");
                Assert.IsTrue(sameLateCallback,
                    "A new pass must reuse the callback after the previous pass has completed.");
                Assert.AreEqual(0, scheduler.CallbackInstanceChanges);
            }
        }

        [TestMethod]
        public void CallbackCacheDoesNotRootDisposedQueueAfterCompletedPass()
        {
            var observation = CreateDisposedCallbackCacheObservation();
            CollectCallbackCacheObservation();

            Assert.IsFalse(observation.Queue.IsAlive,
                "The queue and its cached instance callback must be collectible after a completed pass and Dispose.");
            Assert.IsFalse(observation.Callback.IsAlive,
                "The drained scheduler must not retain the callback or its owner.");
            Assert.IsTrue(observation.Completion.IsCompletedSuccessfully);
            Assert.IsNull(observation.Scheduler.PendingCallback);
            GC.KeepAlive(observation.Scheduler);
            GC.KeepAlive(observation.Completion);
        }

        [TestMethod]
        public void LayoutCallbackCounterScenario()
        {
            const int warmups = 1000;
            const int cycles = 10000;
            foreach (int burstSize in new[] { 1, 10, 50 })
            {
                var scheduler = new CallbackCacheScheduler();
                int eligibilityReads = 0;
                int applies = 0;
                int errors = 0;
                using var queue = new LayoutInvalidationQueue(scheduler.Post, () =>
                {
                    eligibilityReads++;
                    return true;
                }, () =>
                {
                    applies++;
                    return Task.CompletedTask;
                }, _ => errors++);
                for (int cycle = 0; cycle < warmups; cycle++)
                {
                    for (int invalidation = 0; invalidation < burstSize; invalidation++)
                    {
                        queue.Invalidate();
                    }
                    scheduler.RunNext();
                }
                scheduler.ResetCounters();
                eligibilityReads = 0;
                applies = 0;
                errors = 0;
                int invalidations = 0;

                long before = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                for (int cycle = 0; cycle < cycles; cycle++)
                {
                    for (int invalidation = 0; invalidation < burstSize; invalidation++)
                    {
                        queue.Invalidate();
                        invalidations++;
                    }
                    scheduler.RunNext();
                }
                long elapsed = Stopwatch.GetTimestamp() - started;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                Assert.AreEqual(cycles * burstSize, invalidations);
                Assert.AreEqual(cycles, scheduler.Posts);
                Assert.AreEqual(cycles, applies);
                Assert.AreEqual(cycles * 2, eligibilityReads);
                Assert.AreEqual(0, errors);
                Assert.IsNull(scheduler.PendingCallback);
                Assert.IsFalse(queue.Completion.IsCompleted);
                queue.Dispose();
                Assert.IsTrue(queue.Completion.IsCompletedSuccessfully);

                string scenario = $"layout-callback-{burstSize}";
                Console.WriteLine($"PERFCOUNTER {scenario} allocated-bytes {allocated}");
                Console.WriteLine($"PERFCOUNTER {scenario} elapsed-ticks {elapsed}");
                Console.WriteLine($"PERFCOUNTER {scenario} timestamp-frequency {Stopwatch.Frequency}");
                Console.WriteLine($"PERFCOUNTER {scenario} invalidations {invalidations}");
                Console.WriteLine($"PERFCOUNTER {scenario} posts {scheduler.Posts}");
                Console.WriteLine($"PERFCOUNTER {scenario} applies {applies}");
                Console.WriteLine($"PERFCOUNTER {scenario} eligibility-reads {eligibilityReads}");
                Console.WriteLine($"PERFCOUNTER {scenario} errors {errors}");
                Console.WriteLine($"PERFCOUNTER {scenario} callback-instance-changes {scheduler.CallbackInstanceChanges}");
                Console.WriteLine($"PERFCOUNTER {scenario} cycles {cycles}");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static (WeakReference Queue, WeakReference Callback, CallbackCacheScheduler Scheduler, Task Completion)
            CreateDisposedCallbackCacheObservation()
        {
            var scheduler = new CallbackCacheScheduler();
            using var queue = new LayoutInvalidationQueue(scheduler.Post, static () => true,
                static () => Task.CompletedTask, static exception => Assert.Fail(exception.ToString()));
            queue.Invalidate();
            var callbackReference = new WeakReference(scheduler.PendingCallback!);
            scheduler.RunNext();
            Assert.IsNull(scheduler.PendingCallback);
            queue.Dispose();
            Assert.IsTrue(queue.Completion.IsCompletedSuccessfully);
            // The instrumentation's identity history is an intentional external
            // root. Release it so only the queue's own callback cycle remains.
            scheduler.ForgetCallback();
            return (new WeakReference(queue), callbackReference, scheduler, queue.Completion);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CollectCallbackCacheObservation()
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        private sealed class CallbackCacheScheduler(List<string>? trace = null)
        {
            private Action? m_pending;
            private Action? m_previous;

            public Action? PendingCallback => m_pending;
            public int Posts { get; private set; }
            public int CallbackInstanceChanges { get; private set; }

            public void Post(Action callback)
            {
                if (m_pending != null) { throw new InvalidOperationException("A layout callback is already queued."); }
                Posts++;
                if (m_previous != null && !ReferenceEquals(m_previous, callback)) { CallbackInstanceChanges++; }
                m_previous = callback;
                m_pending = callback;
                trace?.Add("post");
            }

            public void RunNext()
            {
                var callback = m_pending ?? throw new InvalidOperationException("No layout callback is queued.");
                m_pending = null;
                trace?.Add("dispatch");
                callback();
            }

            public void ResetCounters()
            {
                if (m_pending != null) { throw new InvalidOperationException("The previous layout pass has not drained."); }
                Posts = 0;
                CallbackInstanceChanges = 0;
                // Retain the final warmup identity so every measured post is
                // compared, including the first post after counter reset.
            }

            public void ForgetCallback()
            {
                if (m_pending != null) { throw new InvalidOperationException("The previous layout pass has not drained."); }
                m_previous = null;
            }
        }
    }
}
