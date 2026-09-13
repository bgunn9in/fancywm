using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

using WinMan;
using WinMan.Windows;

namespace FancyWM.Utilities
{
    internal static partial class WindowExtensions
    {
        internal readonly record struct IconRequest(nint Handle, bool IsNativeWindow);

        internal sealed record IconIdentity(nint Handle, object? ResourceKey, object State, bool IsTransient = false);

        internal readonly record struct IconResult(BitmapSource? Image, long RetryAfter);

        internal interface IIconResolver
        {
            ValueTask<IconIdentity?> IdentifyAsync(IconRequest request, CancellationToken cancellationToken);
            ValueTask<BitmapSource?> LoadAsync(IconIdentity identity, CancellationToken cancellationToken);
            bool IsCurrent(IconIdentity identity);
        }

        // One owner contains both window single-flight admission and the bounded process/package
        // resource index. Neither native work nor user callbacks run while its lock is held.
        internal sealed class IconCache : IDisposable
        {
            private sealed class Job(IWindow window, IconRequest request)
            {
                internal readonly WeakReference<IWindow> Window = new(window);
                internal readonly IconRequest Request = request;
                internal readonly CancellationTokenSource Cancellation = new();
                internal readonly TaskCompletionSource<IconResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                internal LinkedListNode<Job>? QueueNode;
                internal int Observers;
            }

            private sealed record CachedResource(object Key, IconResult Result);

            private readonly object m_lock = new();
            private readonly IIconResolver m_resolver;
            private readonly int m_queueCapacity;
            private readonly int m_cacheCapacity;
            private readonly TimeSpan m_negativeLifetime;
            private readonly TimeSpan m_positiveLifetime;
            private readonly ConditionalWeakTable<IWindow, Job> m_windows = new();
            private readonly LinkedList<Job> m_queue = [];
            private readonly HashSet<Job> m_jobs = [];
            private readonly Dictionary<object, LinkedListNode<CachedResource>> m_resources = [];
            private readonly LinkedList<CachedResource> m_resourceOrder = [];
            private readonly Dictionary<object, TaskCompletionSource<IconResult>> m_loads = [];
            private readonly CancellationTokenSource m_lifetime = new();
            private readonly Task[] m_workers;
            private TaskCompletionSource m_workAvailable = NewSignal();
            private TaskCompletionSource m_spaceAvailable = NewSignal();
            private bool m_disposed;

            internal TimeProvider Clock { get; }
            internal Task Completion => Task.WhenAll(m_workers);
            internal int PendingCount { get { lock (m_lock) { return m_queue.Count; } } }
            internal int CacheCount { get { lock (m_lock) { return m_resources.Count; } } }

            internal IconCache(IIconResolver resolver, TimeProvider? clock = null, int workerCount = 2,
                int queueCapacity = 64, int cacheCapacity = 256, TimeSpan? negativeLifetime = null)
            {
                ArgumentNullException.ThrowIfNull(resolver);
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cacheCapacity);
                m_resolver = resolver;
                Clock = clock ?? TimeProvider.System;
                m_queueCapacity = queueCapacity;
                m_cacheCapacity = cacheCapacity;
                m_negativeLifetime = negativeLifetime ?? TimeSpan.FromSeconds(10);
                ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(m_negativeLifetime, TimeSpan.Zero);
                m_positiveLifetime = TimeSpan.FromMinutes(5);
                m_workers = new Task[workerCount];
                for (int worker = 0; worker < m_workers.Length; worker++)
                {
                    // The number of loops is fixed. A hung native call keeps its slot; cancellation
                    // never abandons it and starts a replacement worker.
                    m_workers[worker] = Task.Run(WorkAsync);
                }
            }

            internal Task<IconResult> GetAsync(IWindow window, CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(window);
                return GetAsync(new WeakReference<IWindow>(window),
                    new IconRequest(window.Handle, window is Win32Window), cancellationToken);
            }

            private async Task<IconResult> GetAsync(WeakReference<IWindow> reference,
                IconRequest request, CancellationToken cancellationToken)
            {
                Job? job = null;
                while (job == null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Task? available = null;
                    lock (m_lock)
                    {
                        ObjectDisposedException.ThrowIf(m_disposed, this);
                        if (!reference.TryGetTarget(out var window) || window.Handle != request.Handle)
                        {
                            return Missing();
                        }
                        if (m_windows.TryGetValue(window, out var existing) &&
                            existing.Request == request && !existing.Cancellation.IsCancellationRequested)
                        {
                            job = existing;
                        }
                        else if (m_queue.Count < m_queueCapacity)
                        {
                            job = new Job(window, request);
                            job.QueueNode = m_queue.AddLast(job);
                            m_jobs.Add(job);
                            m_windows.AddOrUpdate(window, job);
                            m_workAvailable.TrySetResult();
                        }
                        else
                        {
                            available = m_spaceAvailable.Task;
                        }
                        if (job != null)
                        {
                            job.Observers++;
                        }
                    }
                    if (available != null)
                    {
                        // All overflow readers await the same signal. They are not admitted jobs,
                        // and cancellation releases each observer without waiting for a worker.
                        await available.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                try
                {
                    return await job.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Release(job);
                }
            }

            private void Release(Job job)
            {
                bool cancel = false;
                bool dispose = false;
                lock (m_lock)
                {
                    if (--job.Observers == 0 && !job.Completion.Task.IsCompleted)
                    {
                        RemoveWindow(job);
                        cancel = true;
                        if (job.QueueNode != null)
                        {
                            m_queue.Remove(job.QueueNode);
                            job.QueueNode = null;
                            m_jobs.Remove(job);
                            job.Completion.TrySetCanceled();
                            SignalSpace();
                            if (m_queue.Count == 0) { m_workAvailable = NewSignal(); }
                            dispose = true;
                        }
                    }
                }
                if (cancel) { Cancel(job, dispose); }
            }

            private async Task WorkAsync()
            {
                while (true)
                {
                    Job? job = null;
                    Task? available = null;
                    lock (m_lock)
                    {
                        if (m_disposed) { return; }
                        if (m_queue.First is { } first)
                        {
                            job = first.Value;
                            m_queue.RemoveFirst();
                            job.QueueNode = null;
                            SignalSpace();
                            if (m_queue.Count == 0) { m_workAvailable = NewSignal(); }
                        }
                        else
                        {
                            available = m_workAvailable.Task;
                        }
                    }
                    if (job == null)
                    {
                        await available!.ConfigureAwait(false);
                        continue;
                    }

                    IconResult result = Missing();
                    try
                    {
                        job.Cancellation.Token.ThrowIfCancellationRequested();
                        var identity = await m_resolver.IdentifyAsync(job.Request, job.Cancellation.Token).ConfigureAwait(false);
                        if (identity != null && identity.Handle == job.Request.Handle)
                        {
                            job.Cancellation.Token.ThrowIfCancellationRequested();
                            // A shared load contains identity data, never an IWindow or view model.
                            // It finishes in this worker even if its original observer disappears.
                            var resource = await GetResourceAsync(identity).ConfigureAwait(false);
                            job.Cancellation.Token.ThrowIfCancellationRequested();
                            if (m_resolver.IsCurrent(identity))
                            {
                                result = resource;
                            }
                            else if (identity.ResourceKey != null)
                            {
                                lock (m_lock) { RemoveResource(identity.ResourceKey); }
                            }
                        }
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        // Missing, inaccessible, dead and cancelled windows all keep their
                        // placeholder. A later read may retry after the bounded negative lifetime.
                    }
                    finally
                    {
                        lock (m_lock)
                        {
                            RemoveWindow(job);
                            m_jobs.Remove(job);
                            job.Completion.TrySetResult(result);
                        }
                        // Cancellation and disposal serialize on the source itself: Release may
                        // have observed the still-running job just before this completion.
                        lock (job.Cancellation) { job.Cancellation.Dispose(); }
                    }
                }
            }

            private async Task<IconResult> GetResourceAsync(IconIdentity identity)
            {
                TaskCompletionSource<IconResult>? flight = null;
                bool ownsFlight = true;
                lock (m_lock)
                {
                    if (identity.ResourceKey is { } key)
                    {
                        if (m_resources.TryGetValue(key, out var node))
                        {
                            if (node.Value.Result.RetryAfter > Clock.GetTimestamp())
                            {
                                m_resourceOrder.Remove(node);
                                m_resourceOrder.AddLast(node);
                                return node.Value.Result;
                            }
                            RemoveResource(key);
                        }
                        ownsFlight = !m_loads.TryGetValue(key, out flight);
                        if (ownsFlight)
                        {
                            flight = new(TaskCreationOptions.RunContinuationsAsynchronously);
                            m_loads.Add(key, flight);
                        }
                    }
                }
                if (!ownsFlight) { return await flight!.Task.ConfigureAwait(false); }

                BitmapSource? image = null;
                IconResult result = default;
                try
                {
                    var loaded = await m_resolver.LoadAsync(identity, m_lifetime.Token).ConfigureAwait(false);
                    if (loaded != null && !loaded.IsFrozen)
                    {
                        loaded.Freeze();
                    }
                    image = loaded;
                }
                finally
                {
                    result = new IconResult(image, ExpiresAfter(
                        image == null || identity.IsTransient ? m_negativeLifetime : m_positiveLifetime));
                    lock (m_lock)
                    {
                        if (identity.ResourceKey is { } key)
                        {
                            m_loads.Remove(key);
                            if (!m_disposed)
                            {
                                RemoveResource(key);
                                m_resources.Add(key, m_resourceOrder.AddLast(new CachedResource(key, result)));
                                while (m_resources.Count > m_cacheCapacity)
                                {
                                    RemoveResource(m_resourceOrder.First!.Value.Key);
                                }
                            }
                            flight!.TrySetResult(result);
                        }
                    }
                }
                return result;
            }

            internal IconResult Missing() => new(null, ExpiresAfter(m_negativeLifetime));

            private long ExpiresAfter(TimeSpan duration) =>
                Clock.GetTimestamp() + (long)(duration.TotalSeconds * Clock.TimestampFrequency);

            private void RemoveWindow(Job job)
            {
                if (job.Window.TryGetTarget(out var window) &&
                    m_windows.TryGetValue(window, out var current) && ReferenceEquals(current, job))
                {
                    m_windows.Remove(window);
                }
            }

            private void RemoveResource(object key)
            {
                if (m_resources.Remove(key, out var node)) { m_resourceOrder.Remove(node); }
            }

            private void SignalSpace()
            {
                m_spaceAvailable.TrySetResult();
                m_spaceAvailable = NewSignal();
            }

            private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

            private static void Cancel(Job job, bool dispose)
            {
                lock (job.Cancellation)
                {
                    try { job.Cancellation.Cancel(); }
                    catch (ObjectDisposedException) { }
                    finally { if (dispose) { job.Cancellation.Dispose(); } }
                }
            }

            public void Dispose()
            {
                (Job Job, bool Queued)[] jobs;
                lock (m_lock)
                {
                    if (m_disposed) { return; }
                    m_disposed = true;
                    jobs = new (Job, bool)[m_jobs.Count];
                    int index = 0;
                    foreach (var job in m_jobs)
                    {
                        jobs[index++] = (job, job.QueueNode != null);
                        RemoveWindow(job);
                        job.QueueNode = null;
                        job.Completion.TrySetCanceled();
                    }
                    m_queue.Clear();
                    m_jobs.Clear();
                    m_resources.Clear();
                    m_resourceOrder.Clear();
                    m_workAvailable.TrySetResult();
                    SignalSpace();
                }
                foreach (var (job, queued) in jobs)
                {
                    Cancel(job, queued);
                }
                m_lifetime.Cancel();
                // OnExit does not synchronously wait for native calls. The fixed worker set owns
                // final source disposal and cannot be replaced after this owner is stopped.
                _ = Completion.ContinueWith(_ => m_lifetime.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }
}
