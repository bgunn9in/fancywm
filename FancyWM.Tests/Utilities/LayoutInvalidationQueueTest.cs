using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using FancyWM.Utilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public partial class LayoutInvalidationQueueTest
    {
        [TestMethod]
        public void FailedPostPreservesNewerInvalidationWithoutRetryingForever()
        {
            var pending = new Queue<Action>();
            int posts = 0;
            int passes = 0;
            int errors = 0;
            LayoutInvalidationQueue queue = null;
            using (queue = new LayoutInvalidationQueue(callback =>
            {
                posts++;
                if (posts == 1)
                {
                    queue.Invalidate();
                    throw new InvalidOperationException("transient scheduler failure");
                }
                pending.Enqueue(callback);
            }, () => true, () =>
            {
                passes++;
                return Task.CompletedTask;
            }, _ => errors++))
            {
                queue.Invalidate();
                Assert.AreEqual(1, errors);
                Assert.AreEqual(1, pending.Count);
                pending.Dequeue()();
                Assert.AreEqual(1, passes);
                Assert.AreEqual(0, pending.Count);
            }

            using var unavailableQueue = new LayoutInvalidationQueue(_ =>
            {
                posts++;
                throw new InvalidOperationException("dispatcher shut down");
            }, () => true, () => Task.CompletedTask, _ => errors++);
            unavailableQueue.Invalidate();
            Assert.AreEqual(3, posts);
            Assert.AreEqual(2, errors);
        }

        [TestMethod]
        public void FailedPassPreservesReentrantInvalidationAndDisposeDuringAwaitStopsFollowup()
        {
            var pending = new Queue<Action>();
            var barrier = new TaskCompletionSource<bool>();
            int passes = 0;
            int errors = 0;
            LayoutInvalidationQueue queue = null;
            using (queue = new LayoutInvalidationQueue(pending.Enqueue, () => true, () =>
            {
                passes++;
                queue.Invalidate();
                if (passes == 1) { throw new InvalidOperationException(); }
                return barrier.Task;
            }, _ => errors++))
            {
                queue.Invalidate();
                pending.Dequeue()();
                Assert.AreEqual(1, errors);
                Assert.AreEqual(1, pending.Count);
                pending.Dequeue()();
                Assert.AreEqual(2, passes);
                queue.Dispose();
                barrier.SetResult(true);
                Assert.AreEqual(0, pending.Count);
            }
        }

        [TestMethod]
        public void InvalidationsDuringAwaitKeepLatestPassWithoutOverlappingWork()
        {
            var pending = new Queue<Action>();
            var barrier = new TaskCompletionSource<bool>();
            int passes = 0;
            using var queue = new LayoutInvalidationQueue(pending.Enqueue, () => true,
                () => ++passes == 1 ? barrier.Task : Task.CompletedTask,
                exception => Assert.Fail(exception.ToString()));
            queue.Invalidate();
            pending.Dequeue()();
            for (int eventIndex = 0; eventIndex < 10000; eventIndex++) { queue.Invalidate(); }
            Assert.AreEqual(1, passes);
            Assert.AreEqual(0, pending.Count);
            barrier.SetResult(true);
            Assert.AreEqual(1, pending.Count);
            pending.Dequeue()();
            Assert.AreEqual(2, passes);
            Assert.AreEqual(0, pending.Count);
        }

        [TestMethod]
        public void BurstSchedulesOnePassAndReentrantChangesScheduleOneMore()
        {
            var pending = new Queue<Action>();
            int passes = 0;
            LayoutInvalidationQueue queue = null;
            using (queue = new LayoutInvalidationQueue(pending.Enqueue, () => true, () =>
            {
                passes++;
                if (passes == 1)
                {
                    for (int eventIndex = 0; eventIndex < 10000; eventIndex++) { queue.Invalidate(); }
                }
                return Task.CompletedTask;
            }, exception => Assert.Fail(exception.ToString())))
            {
                for (int eventIndex = 0; eventIndex < 10000; eventIndex++) { queue.Invalidate(); }
                Assert.AreEqual(1, pending.Count);
                pending.Dequeue()();
                Assert.AreEqual(1, pending.Count);
                pending.Dequeue()();
                Assert.AreEqual(2, passes);
                Assert.AreEqual(0, pending.Count);
            }
        }

        [TestMethod]
        public void FrozenAndDisposedCallbacksCannotApplyAndErrorsReleaseOwnership()
        {
            var pending = new Queue<Action>();
            bool enabled = true;
            int passes = 0;
            int errors = 0;
            using var queue = new LayoutInvalidationQueue(pending.Enqueue, () => enabled, () =>
            {
                passes++;
                throw new InvalidOperationException();
            }, _ => errors++);
            queue.Invalidate();
            enabled = false;
            pending.Dequeue()();
            Assert.AreEqual(0, passes);
            Assert.AreEqual(0, pending.Count);
            enabled = true;
            queue.Invalidate();
            pending.Dequeue()();
            Assert.AreEqual(1, errors);
            queue.Invalidate();
            queue.Dispose();
            pending.Dequeue()();
            Assert.AreEqual(1, passes);
            queue.Invalidate();
            Assert.AreEqual(0, pending.Count);
        }
    }
}
