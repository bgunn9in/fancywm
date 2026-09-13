using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using FancyWM.DllImports;

namespace FancyWM.Utilities
{
    internal interface IAnimationJob
    {
        bool IsCancelled { get; }
        TimeSpan Duration { get; }
        Task Task { get; }
        ValueTask Update(double progress);
        void Cancel();

        void OnCompleted();
        void OnCancelled();
    }

    internal static class AnimationJob
    {
        private class DelegateAnimationJob(Func<IAnimationJob, double, ValueTask> animate, TimeSpan duration) : IAnimationJob
        {
            public bool IsCancelled => m_isCancelled;

            public TimeSpan Duration => m_duration;

            public Task Task => m_tcs.Task;

            private readonly Func<IAnimationJob, double, ValueTask> m_animate = animate;
            private readonly TimeSpan m_duration = duration;
            private volatile bool m_isCancelled;
            private readonly TaskCompletionSource<object?> m_tcs = new();

            public void Cancel()
            {
                m_isCancelled = true;
            }

            public ValueTask Update(double progress)
            {
                return m_animate(this, progress);
            }

            public void OnCompleted()
            {
                m_tcs.TrySetResult(null);
            }

            public void OnCancelled()
            {
                m_tcs.TrySetCanceled();
            }
        }

        public static IAnimationJob Create(Func<IAnimationJob, double, ValueTask> animate, TimeSpan duration)
        {
            return new DelegateAnimationJob(animate, duration);
        }
    }

    internal interface IAnimationThread : IDisposable
    {
        void Start(IAnimationJob job);
    }

    internal partial class AnimationThread : IAnimationThread
    {
        internal partial class Compositor
        {
            [LibraryImport("dcomp")]
            internal static partial uint DCompositionWaitForCompositorClock(int count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[]? handles, uint timeoutInMs);

            [LibraryImport("dcomp")]
            internal static partial int DCompositionBoostCompositorClock(int enable);

            internal static bool s_isDCompositionCompositorAPIAvailable = true;

            internal static int BoostClock(bool enable)
            {
                int hr = 0;
                if (s_isDCompositionCompositorAPIAvailable)
                {
                    try
                    {
                        hr = DCompositionBoostCompositorClock(enable ? 1 : 0);
                    }
                    catch (EntryPointNotFoundException)
                    {
                        s_isDCompositionCompositorAPIAvailable = false;
                    }
                }
                return hr;
            }

            internal static uint Wait(uint timeoutInMs)
            {
                uint hr = 0;
                if (s_isDCompositionCompositorAPIAvailable)
                {
                    try
                    {
                        hr = DCompositionWaitForCompositorClock(0, null, timeoutInMs);
                    }
                    catch (EntryPointNotFoundException)
                    {
                        s_isDCompositionCompositorAPIAvailable = false;
                    }
                }
                else
                {
                    Thread.Sleep(10);
                }
                return hr;
            }
        }


        internal class WorkItem(IAnimationJob job, TimeSpan startTime, TimeSpan endTime)
        {
            public TimeSpan StartTime { get; set; } = startTime;
            public TimeSpan EndTime { get; set; } = endTime;
            public IAnimationJob Job { get; set; } = job ?? throw new ArgumentNullException(nameof(job));
        }

        internal sealed class Frame
        {
            private readonly Stopwatch m_clock;
            private readonly List<WorkItem> m_completedJobs = [];
            private readonly List<Task> m_tasks = [];

            internal List<WorkItem> Jobs { get; } = [];

            internal Frame(Stopwatch clock)
            {
                m_clock = clock;
            }

            internal void UpdateFrame()
            {
                UpdateFrame(static () => false);
            }

            internal void UpdateFrame(Func<bool> isStopping)
            {
                m_completedJobs.Clear();
                try
                {
                    var jobs = CollectionsMarshal.AsSpan(Jobs);
                    CollectionsMarshal.SetCount(m_tasks, jobs.Length);
                    var tasks = CollectionsMarshal.AsSpan(m_tasks);
                    for (int index = 0; index < jobs.Length; index++)
                    {
                        tasks[index] = isStopping() ? Task.CompletedTask : UpdateJob(jobs[index]);
                    }
                    Task.WaitAll(tasks);
                    foreach (var completedJob in m_completedJobs)
                    {
                        Jobs.Remove(completedJob);
                    }
                }
                finally
                {
                    // The thread can block for its next job after this frame.
                    // Keep reusable capacity without retaining finished targets.
                    m_tasks.Clear();
                    m_completedJobs.Clear();
                }
            }

            private async Task UpdateJob(WorkItem job)
            {
                var progress = Math.Min(1.0, (m_clock.Elapsed - job.StartTime).TotalMilliseconds / job.Job.Duration.TotalMilliseconds);
                await job.Job.Update(progress);
                if (progress >= 1.0)
                {
                    lock (m_completedJobs)
                    {
                        m_completedJobs.Add(job);
                    }
                    job.Job.OnCompleted();
                }
                else if (job.Job.IsCancelled)
                {
                    lock (m_completedJobs)
                    {
                        m_completedJobs.Add(job);
                    }
                    job.Job.OnCancelled();
                }
            }
        }

        internal sealed class ClockBoost(Func<bool, int> boostClock) : IDisposable
        {
            private bool m_owned;
            private bool m_disposed;

            public void BeginFrame()
            {
                ObjectDisposedException.ThrowIf(m_disposed, this);
                if (!m_owned)
                {
                    // A failed request owns no reference. A later frame retains
                    // the existing opportunity to retry without changing cadence.
                    m_owned = boostClock(true) >= 0;
                }
            }

            public void EndFrame(bool hasPendingQueue, bool hasActiveJobs)
            {
                ObjectDisposedException.ThrowIf(m_disposed, this);
                if (!hasPendingQueue && !hasActiveJobs)
                {
                    Release();
                }
            }

            public void Dispose()
            {
                if (m_disposed) return;
                m_disposed = true;
                Release();
            }

            private void Release()
            {
                if (m_owned && boostClock(false) >= 0)
                {
                    m_owned = false;
                }
                // A failed release keeps ownership: do not acquire another
                // reference. The next idle boundary or loop exit may retry once.
            }
        }

        private readonly Thread m_thread;
        private readonly TimeSpan m_targetFrameTime;
        private readonly BlockingCollection<IAnimationJob> m_queue = [];
        private readonly Stopwatch m_sw = new();
        private readonly Func<bool, int> m_boostClock;
        private readonly Func<uint, uint> m_waitClock;
        private readonly TaskCompletionSource m_completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object m_admission = new();
        private volatile bool m_stopRequested;

        internal Task Completion => m_completion.Task;

        public AnimationThread(int targetFrameRate) : this(targetFrameRate, Compositor.BoostClock, Compositor.Wait)
        {
        }

        internal AnimationThread(int targetFrameRate, Func<bool, int> boostClock, Func<uint, uint> waitClock)
        {
            m_boostClock = boostClock;
            m_waitClock = waitClock;
            m_targetFrameTime = TimeSpan.FromSeconds(1.0 / targetFrameRate);
            m_thread = new Thread(ThreadStart)
            {
                Name = "AnimationThread"
            };
            m_thread.Start();
        }

        public void Start(IAnimationJob job)
        {
            ArgumentNullException.ThrowIfNull(job);
            lock (m_admission)
            {
                ObjectDisposedException.ThrowIf(m_stopRequested, this);
                m_queue.Add(job);
            }
        }

        private void ThreadStart(object? obj)
        {
            m_sw.Restart();
            Exception? failure = null;
            try
            {
                RunLoop(m_queue, m_sw, m_targetFrameTime, m_boostClock, m_waitClock,
                    () => m_stopRequested, Dispose);
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                // Only the worker releases the queue, after every admitted
                // update and terminal callback has returned. The worker never
                // publishes completion while an update is still using its owner.
                Dispose();
                m_queue.Dispose();
            }
            if (failure is null)
            {
                m_completion.TrySetResult();
            }
            else
            {
                m_completion.TrySetException(failure);
                _ = m_completion.Task.Exception; // Retain for awaiters and observe even during application shutdown.
                try { Trace.TraceError($"Animation worker failed: {failure}"); }
                catch { } // A diagnostic listener must not replace the original failure.
            }
        }

        internal static void RunLoop(
            BlockingCollection<IAnimationJob> queue,
            Stopwatch clock,
            TimeSpan targetFrameTime,
            Func<bool, int> boostClock,
            Func<uint, uint> waitClock)
        {
            RunLoop(queue, clock, targetFrameTime, boostClock, waitClock, static () => false, queue.CompleteAdding);
        }

        private static void RunLoop(
            BlockingCollection<IAnimationJob> queue,
            Stopwatch clock,
            TimeSpan targetFrameTime,
            Func<bool, int> boostClock,
            Func<uint, uint> waitClock,
            Func<bool> isStopping,
            Action stopAccepting)
        {
            var frame = new Frame(clock);
            var jobs = frame.Jobs;
            var boost = new ClockBoost(boostClock);
            Exception? failure = null;
            Action<IAnimationJob> admit = Admit;

            try
            {
                while (!isStopping())
                {
                    // TryTake returns false for an empty completed queue.
                    // Only the blocking Take below needs a completion catch;
                    // an exception from a job getter must remain a job failure.
                    while (!isStopping() && queue.TryTake(out IAnimationJob? pendingJob))
                    {
                        Admit(pendingJob);
                    }

                    if (jobs.Count == 0 && !AdmitNext(queue, admit))
                    {
                        break;
                    }

                    if (isStopping()) break;
                    boost.BeginFrame();
                    try
                    {
                        if (isStopping()) break;
                        uint waitResult = waitClock((uint)targetFrameTime.TotalMilliseconds);
                        Debug.Assert(waitResult >= 0);

                        if (!isStopping()) frame.UpdateFrame(isStopping);
                    }
                    finally
                    {
                        boost.EndFrame(queue.Count != 0, jobs.Count != 0);
                    }
                }
            }
            catch (Exception error)
            {
                failure = error;
            }
            // No terminal callback may overlap an already-entered Update.
            // Frame waits for all of them, including when one update fails.
            // Native calls that never return remain a separate shutdown limit.
            Attempt(stopAccepting);
            foreach (var item in jobs) CancelUnfinished(item.Job);
            jobs.Clear();
            while (queue.TryTake(out var pending)) CancelUnfinished(pending);
            Attempt(boost.Dispose);
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();

            void Admit(IAnimationJob job)
            {
                // Retain ownership even if a custom Duration getter throws.
                var item = new WorkItem(job, clock.Elapsed, default);
                jobs.Add(item);
                item.EndTime = clock.Elapsed + job.Duration;
            }

            void CancelUnfinished(IAnimationJob job)
            {
                try
                {
                    if (job.Task.IsCompleted) return;
                }
                catch (Exception error)
                {
                    failure ??= error;
                }
                Attempt(job.Cancel);
                Attempt(job.OnCancelled);
            }

            void Attempt(Action action)
            {
                try { action(); }
                catch (Exception error) { failure ??= error; }
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool AdmitNext(BlockingCollection<IAnimationJob> queue, Action<IAnimationJob> admit)
        {
            // End the payload's stack lifetime before the next idle wait. Debug
            // JIT return temporaries otherwise retain the last completed job.
            IAnimationJob? job = null;
            try
            {
                while (job == null) { job = queue.Take(); }
            }
            catch (InvalidOperationException) when (queue.IsCompleted)
            {
                return false;
            }
            admit(job);
            return true;
        }

        public void Dispose()
        {
            lock (m_admission)
            {
                if (!m_stopRequested)
                {
                    m_stopRequested = true;
                    m_queue.CompleteAdding();
                }
            }
            // Stop admission without blocking the caller's Dispatcher. Owners
            // await Completion before releasing resources used by entered jobs.
        }
    }
}
