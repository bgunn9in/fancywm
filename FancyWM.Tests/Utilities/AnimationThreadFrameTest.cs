#nullable enable
using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class AnimationThreadFrameTest
    {
        [TestMethod]
        public void EachJobUsesItsOwnStartAndDurationAndFinishesOnlyAtItsFinalFrame()
        {
            var frame = new AnimationThread.Frame(new Stopwatch());
            var half = new Job(TimeSpan.FromMilliseconds(20));
            var final = new Job(TimeSpan.FromMilliseconds(5));
            frame.Jobs.Add(Item(half, 10));
            frame.Jobs.Add(Item(final, 10));
            frame.UpdateFrame();
            Assert.AreEqual(.5, half.LastProgress);
            Assert.AreEqual(1d, final.LastProgress);
            Assert.AreEqual(1, half.Updates);
            Assert.AreEqual(1, final.Updates);
            Assert.IsFalse(half.Task.IsCompleted);
            Assert.IsTrue(final.Task.IsCompletedSuccessfully);
            Assert.AreEqual(1, frame.Jobs.Count);
            Assert.AreSame(half, frame.Jobs[0].Job);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void CancellationIsObservedAfterUpdateAndFinalProgressWins(bool final)
        {
            var frame = new AnimationThread.Frame(new Stopwatch());
            var job = new Job(TimeSpan.FromMilliseconds(20));
            job.Cancel();
            frame.Jobs.Add(Item(job, final ? 20 : 10));
            frame.UpdateFrame();
            Assert.AreEqual(1, job.Updates);
            Assert.AreEqual(final ? 1 : 0, job.Completed);
            Assert.AreEqual(final ? 0 : 1, job.Cancelled);
            Assert.AreEqual(!final, job.Task.IsCanceled);
            Assert.AreEqual(0, frame.Jobs.Count);
        }

        [TestMethod]
        public async Task EveryPendingUpdateStartsBeforeFrameWaitsAndCancellationRemainsCurrent()
        {
            var firstEntered = Signal();
            var secondEntered = Signal();
            var firstRelease = Signal();
            var secondRelease = Signal();
            var first = new Job(TimeSpan.FromMilliseconds(20), () => { firstEntered.SetResult(); return new(firstRelease.Task); });
            var second = new Job(TimeSpan.FromMilliseconds(20), () => { secondEntered.SetResult(); return new(secondRelease.Task); });
            var frame = new AnimationThread.Frame(new Stopwatch());
            frame.Jobs.Add(Item(first, 10));
            frame.Jobs.Add(Item(second, 20));
            var runningFrame = Task.Run(frame.UpdateFrame);
            try
            {
                await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsFalse(runningFrame.IsCompleted);
                Assert.IsFalse(first.Task.IsCompleted);
                Assert.IsFalse(second.Task.IsCompleted);
                first.Cancel();
                secondRelease.SetResult();
                await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsFalse(runningFrame.IsCompleted, "One pending window must not be abandoned by the frame.");
            }
            finally
            {
                firstRelease.TrySetResult();
                secondRelease.TrySetResult();
                await runningFrame.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.IsTrue(first.Task.IsCanceled);
            Assert.IsTrue(second.Task.IsCompletedSuccessfully);
            Assert.AreEqual(0, frame.Jobs.Count);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void SynchronousThrowOrFaultedValueTaskStillDispatchesLaterJobs(bool faultedValueTask)
        {
            var error = new InvalidOperationException("frame adapter failure");
            var failed = new Job(TimeSpan.FromMilliseconds(20), () => faultedValueTask ? ValueTask.FromException(error) : throw error);
            var good = new Job(TimeSpan.FromMilliseconds(20));
            var frame = new AnimationThread.Frame(new Stopwatch());
            frame.Jobs.Add(Item(failed, 20));
            frame.Jobs.Add(Item(good, 20));
            var actual = Assert.ThrowsException<AggregateException>(frame.UpdateFrame);
            Assert.AreSame(error, actual.InnerExceptions.Single());
            Assert.AreEqual(1, failed.Updates);
            Assert.AreEqual(1, good.Updates);
            Assert.IsFalse(failed.Task.IsCompleted);
            Assert.IsTrue(good.Task.IsCompletedSuccessfully);
            Assert.AreEqual(2, frame.Jobs.Count, "The existing failed frame never reaches active-list removal.");
        }

        [TestMethod]
        public async Task AsynchronousFailureWaitsForEveryStartedUpdateBeforeAggregating()
        {
            var error = new InvalidOperationException("pending frame failure");
            var failedEntered = Signal();
            var goodEntered = Signal();
            var failedRelease = Signal();
            var goodRelease = Signal();
            var failed = new Job(TimeSpan.FromMilliseconds(20), () => { failedEntered.SetResult(); return new(failedRelease.Task); });
            var good = new Job(TimeSpan.FromMilliseconds(20), () => { goodEntered.SetResult(); return new(goodRelease.Task); });
            var frame = new AnimationThread.Frame(new Stopwatch());
            frame.Jobs.Add(Item(failed, 20));
            frame.Jobs.Add(Item(good, 20));
            var runningFrame = Task.Run(frame.UpdateFrame);
            AggregateException? actual = null;
            try
            {
                await Task.WhenAll(failedEntered.Task, goodEntered.Task).WaitAsync(TimeSpan.FromSeconds(5));
                failedRelease.SetException(error);
                Assert.IsFalse(runningFrame.IsCompleted, "A failed update cannot release a frame that still has a pending window.");
                Assert.IsFalse(good.Task.IsCompleted);
            }
            finally
            {
                failedRelease.TrySetException(error);
                goodRelease.TrySetResult();
                try
                {
                    await runningFrame.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (AggregateException exception)
                {
                    actual = exception;
                }
            }
            Assert.IsNotNull(actual);
            Assert.AreSame(error, actual!.InnerExceptions.Single());
            Assert.AreEqual(1, failed.Updates);
            Assert.AreEqual(1, good.Updates);
            Assert.IsTrue(good.Task.IsCompletedSuccessfully);
            Assert.AreEqual(2, frame.Jobs.Count);
        }

        [TestMethod]
        public void CompletionCallbackFailureStillDispatchesLaterJobsAndKeepsActiveList()
        {
            var error = new InvalidOperationException("completion callback failure");
            var failed = new Job(TimeSpan.FromMilliseconds(20), completed: () => throw error);
            var good = new Job(TimeSpan.FromMilliseconds(20));
            var frame = new AnimationThread.Frame(new Stopwatch());
            frame.Jobs.Add(Item(failed, 20));
            frame.Jobs.Add(Item(good, 20));
            var actual = Assert.ThrowsException<AggregateException>(frame.UpdateFrame);
            Assert.AreSame(error, actual.InnerExceptions.Single());
            Assert.AreEqual(1, failed.Completed);
            Assert.IsFalse(failed.Task.IsCompleted);
            Assert.AreEqual(1, good.Completed);
            Assert.IsTrue(good.Task.IsCompletedSuccessfully);
            Assert.AreEqual(2, frame.Jobs.Count);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void IdleFrameBuffersDoNotRetainCompletedJobsAcrossOneHundredLifetimes(bool cancel)
        {
            var frame = new AnimationThread.Frame(new Stopwatch());
            var references = CompleteJobs(frame, cancel);
            Collect();
            Assert.IsTrue(references.All(reference => !reference.IsAlive),
                "An idle animation thread must not keep the previous targets alive through its frame buffers.");
            GC.KeepAlive(frame);
        }

        [TestMethod]
        public void FailedFrameReleasesTemporaryReferencesAfterItsActiveJobsAreReleased()
        {
            var frame = new AnimationThread.Frame(new Stopwatch());
            var references = FailFrameAndReleaseActiveJobs(frame);
            Collect();
            Assert.IsTrue(references.All(reference => !reference.IsAlive),
                "Temporary frame buffers must release jobs even when an update fails.");
            GC.KeepAlive(frame);
        }

        [DataTestMethod]
        [DataRow(1)]
        [DataRow(10)]
        [DataRow(50)]
        public void StableFramesAllocateNoTransientCollectionsAfterCapacityWarmup(int count)
        {
            var frame = CreateStableFrame(count);
            long compilerStateBytes = GetCompilerStateAllocation();
            for (int i = 0; i < 100; i++) frame.UpdateFrame();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) frame.UpdateFrame();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(compilerStateBytes * count * 1000, bytes,
                "Stable frames may allocate the Debug compiler's async state, but no transient frame collections. Release requires exactly zero bytes.");
            AssertStableFrame(frame, count, 1100);
        }

        [TestMethod]
        public void AnimationFrameCounterScenario()
        {
            foreach (int count in new[] { 1, 4, 10, 25, 50 })
            {
                var frame = CreateStableFrame(count);
                for (int i = 0; i < 1000; i++) frame.UpdateFrame();
                long before = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                for (int i = 0; i < 10000; i++) frame.UpdateFrame();
                long elapsed = Stopwatch.GetTimestamp() - started;
                long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                AssertStableFrame(frame, count, 11000);
                Console.WriteLine($"PERFCOUNTER animation-frame-{count} allocated-bytes {bytes}");
                Console.WriteLine($"PERFCOUNTER animation-frame-{count} elapsed-ticks {elapsed}");
                Console.WriteLine($"PERFCOUNTER animation-frame-{count} timestamp-frequency {Stopwatch.Frequency}");
                Console.WriteLine($"PERFCOUNTER animation-frame-{count} updates {count * 10000}");
                Console.WriteLine($"PERFCOUNTER animation-frame-{count} frames 10000");
            }
        }

        [TestMethod]
        public void GrowingShrinkingAndEmptyFramesOnlyWaitForCurrentJobs()
        {
            var frame = new AnimationThread.Frame(new Stopwatch());
            int updates = 0;
            int expectedUpdates = 0;
            for (int cycle = 0; cycle < 100; cycle++)
            {
                foreach (int count in new[] { 1, 50, 4, 25, 0, 10, 1 })
                {
                    var active = new IAnimationJob[count];
                    for (int index = 0; index < count; index++)
                    {
                        active[index] = AnimationJob.Create((_, progress) =>
                        {
                            Assert.AreEqual(1d, progress);
                            updates++;
                            return ValueTask.CompletedTask;
                        }, TimeSpan.FromMilliseconds(20));
                        frame.Jobs.Add(new(active[index], TimeSpan.FromMilliseconds(-20), TimeSpan.Zero));
                    }
                    frame.UpdateFrame();
                    expectedUpdates += count;
                    Assert.AreEqual(expectedUpdates, updates);
                    Assert.IsTrue(active.All(job => job.Task.IsCompletedSuccessfully));
                    Assert.AreEqual(0, frame.Jobs.Count);
                    frame.UpdateFrame();
                    Assert.AreEqual(expectedUpdates, updates, "An empty frame cannot re-run prior targets or wait on cleared buffer slots.");
                }
            }
        }

        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static long GetCompilerStateAllocation()
        {
            var update = typeof(AnimationThread.Frame).GetMethod("UpdateJob", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var stateType = update.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
#if !DEBUG
            Assert.IsTrue(stateType.IsValueType, "The Release async state must stay allocation-free for synchronous updates.");
#endif
            if (stateType.IsValueType) return 0;
            // Debug emits one class instance per async call even when the await
            // completes synchronously. Its exact runtime size is the only allowed
            // allocation; it is measured outside the production frame loop.
            _ = RuntimeHelpers.GetUninitializedObject(stateType);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var compilerState = RuntimeHelpers.GetUninitializedObject(stateType);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(compilerState);
            return allocated;
        }

        private static AnimationThread.Frame CreateStableFrame(int count)
        {
            var frame = new AnimationThread.Frame(new Stopwatch());
            for (int i = 0; i < count; i++) frame.Jobs.Add(Item(new Job(TimeSpan.FromMilliseconds(20)), 10));
            return frame;
        }

        private static void AssertStableFrame(AnimationThread.Frame frame, int count, int updates)
        {
            Assert.AreEqual(count, frame.Jobs.Count);
            foreach (var job in frame.Jobs)
            {
                Assert.AreEqual(updates, ((Job)job.Job).Updates);
                Assert.AreEqual(.5, ((Job)job.Job).LastProgress);
                Assert.IsFalse(job.Job.Task.IsCompleted);
            }
        }

        private static AnimationThread.WorkItem Item(Job job, int elapsedMilliseconds) =>
            new(job, TimeSpan.FromMilliseconds(-elapsedMilliseconds), job.Duration - TimeSpan.FromMilliseconds(elapsedMilliseconds));

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference[] CompleteJobs(AnimationThread.Frame frame, bool cancel)
        {
            var references = new WeakReference[100];
            for (int i = 0; i < 100; i++)
            {
                var job = new Job(TimeSpan.FromMilliseconds(20));
                if (cancel) job.Cancel();
                references[i] = new WeakReference(job);
                frame.Jobs.Add(Item(job, cancel ? 10 : 20));
                frame.UpdateFrame();
                Assert.AreEqual(cancel, job.Task.IsCanceled);
                Assert.AreEqual(!cancel, job.Task.IsCompletedSuccessfully);
                Assert.AreEqual(0, frame.Jobs.Count);
            }
            return references;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference[] FailFrameAndReleaseActiveJobs(AnimationThread.Frame frame)
        {
            var failed = new Job(TimeSpan.FromMilliseconds(20), () => throw new InvalidOperationException("weak frame failure"));
            var good = new Job(TimeSpan.FromMilliseconds(20));
            frame.Jobs.Add(Item(failed, 20));
            frame.Jobs.Add(Item(good, 20));
            Assert.ThrowsException<AggregateException>(frame.UpdateFrame);
            Assert.AreEqual(2, frame.Jobs.Count);
            frame.Jobs.Clear();
            return [new WeakReference(failed), new WeakReference(good)];
        }

        private static void Collect()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private sealed class Job(TimeSpan duration, Func<ValueTask>? update = null, Action? completed = null) : IAnimationJob
        {
            private readonly TaskCompletionSource m_completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool IsCancelled { get; private set; }
            public TimeSpan Duration { get; } = duration;
            public Task Task => m_completion.Task;
            public int Updates;
            public int Completed;
            public int Cancelled;
            public double LastProgress;
            public ValueTask Update(double progress)
            {
                Updates++;
                LastProgress = progress;
                return update?.Invoke() ?? ValueTask.CompletedTask;
            }
            public void Cancel() => IsCancelled = true;
            public void OnCompleted() { Completed++; completed?.Invoke(); m_completion.TrySetResult(); }
            public void OnCancelled() { Cancelled++; m_completion.TrySetCanceled(); }
        }
    }
}
