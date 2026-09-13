using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace FancyWM.Utilities
{
    // Per-window adapter keeps native acquisition out of lifecycle regression tests.
    // The window supplies its existing cleanup order; this owner runs it once.
    internal sealed class MainWindowLifetime(
        Action<Action<Action>> releaseOwned,
        Func<Task>? completeOperations = null,
        Action<Action<Action>>? releaseDependent = null,
        Func<Action, Task>? dispatch = null) : IDisposable
    {
        private readonly object m_gate = new();
        private Action<Action<Action>>? m_releaseOwned = releaseOwned;
        private Func<Task>? m_completeOperations = completeOperations;
        private Action<Action<Action>>? m_releaseDependent = releaseDependent;
        private Func<Action, Task>? m_dispatch = dispatch;
        private readonly TaskCompletionSource m_completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Completion => m_completion.Task;

        internal void Acquire<T>(out T owner, Func<T> acquire, Action<T> release)
        {
            CheckActive();
            // Native creation and callbacks must not hold the disposal gate.
            var acquired = acquire();
            lock (m_gate)
            {
                if (m_releaseOwned != null)
                {
                    owner = acquired;
                    return;
                }
            }
            // Dispose ran before publication: the normal cleanup cannot see this
            // owner. Release it here once and stop the remaining initialization.
            var error = new ObjectDisposedException(nameof(MainWindowLifetime));
            try { release(acquired); }
            catch (Exception cleanupError) { RecordConstructionCleanup(error, cleanupError); }
            throw error;
        }

        internal void Acquire<T>(out T owner, Func<T> acquire) where T : IDisposable?
            => Acquire(out owner, acquire, static value => value?.Dispose());

        internal void Run(Action initialize)
        {
            CheckActive();
            initialize();
            CheckActive();
        }

        internal void CheckActive()
            => ObjectDisposedException.ThrowIf(Volatile.Read(ref m_releaseOwned) == null, this);

        internal void RunIfActive(Action callback)
        {
            if (Volatile.Read(ref m_releaseOwned) != null) { callback(); }
        }

        internal Task RunIfActiveAsync(Func<Task> callback)
            => Volatile.Read(ref m_releaseOwned) != null ? callback() : Task.CompletedTask;

        internal async Task RunDelayedIfActiveAsync(
            Func<CancellationToken, Task> delay,
            CancellationToken shutdown,
            Func<Task> callback)
        {
            if (shutdown.IsCancellationRequested || Volatile.Read(ref m_releaseOwned) == null) { return; }
            try
            {
                await delay(shutdown);
            }
            catch (OperationCanceledException error)
                when (shutdown.IsCancellationRequested && error.CancellationToken == shutdown)
            {
                return;
            }
            if (shutdown.IsCancellationRequested) { return; }
            await RunIfActiveAsync(() => shutdown.IsCancellationRequested ? Task.CompletedTask : callback());
        }

        internal CompositeDisposable SubscribeAll(params Func<IDisposable>[] factories)
            => RuntimeSettingsSubscription.SubscribeAll([.. factories.Select(factory => (Func<IDisposable>)(() =>
            {
                CheckActive();
                return factory();
            }))]);

        internal static void ReleaseSubscriptions(CompositeDisposable subscriptions)
            => ReleaseActions(release => DisposeSubscriptions(subscriptions, release));

        internal void ConstructionFailed(Exception error)
        {
            try { Dispose(); }
            catch (Exception cleanupError) { RecordConstructionCleanup(error, cleanupError); }
        }

        // A deferred initializer must fault its task before its Rx owner is
        // disposed; otherwise the original failure never reaches OnError.
        internal void Initialize(Action initialize) => Run(initialize);

        internal void InitializationFailed(Exception error, Action<Exception> reportFailure)
        {
            ConstructionFailed(error);
            reportFailure(error);
        }

        internal void InitializationFailed(Exception error, Action<Exception> reportFailure, Action<Action> dispatchFailure)
            => dispatchFailure(() => InitializationFailed(error, reportFailure));

        private static void RecordConstructionCleanup(Exception error, Exception cleanupError)
        {
            const string key = "MainWindowLifetime.ConstructionCleanupExceptions";
            var previous = error.Data[key] as AggregateException;
            error.Data[key] = previous == null
                ? new AggregateException(cleanupError)
                : new AggregateException(previous.InnerExceptions.Append(cleanupError));
        }

        public void Dispose()
        {
            Action<Action<Action>>? releaseOwned;
            Func<Task>? completeOperations;
            Action<Action<Action>>? releaseDependent;
            Func<Action, Task>? dispatch;
            lock (m_gate)
            {
                releaseOwned = m_releaseOwned;
                m_releaseOwned = null;
                completeOperations = m_completeOperations;
                m_completeOperations = null;
                releaseDependent = m_releaseDependent;
                m_releaseDependent = null;
                dispatch = m_dispatch;
                m_dispatch = null;
            }
            if (releaseOwned == null) { return; }
            var failures = new CleanupFailures();
            failures.Run(() => releaseOwned(failures.Run));
            Task operations = Task.CompletedTask;
            if (completeOperations != null) { failures.Run(() => operations = completeOperations()); }
            var immediateFailure = failures.Capture();
            _ = CompleteDisposalAsync(operations, releaseDependent, dispatch, failures);
            if (Completion.IsCompleted) { Completion.GetAwaiter().GetResult(); }
            else { immediateFailure?.Throw(); }
        }

        private async Task CompleteDisposalAsync(Task operations, Action<Action<Action>>? releaseDependent,
            Func<Action, Task>? dispatch, CleanupFailures failures)
        {
            // Faulted or cancelled operations have also relinquished their
            // dependencies. Preserve the failure while attempting every owner.
            try { await operations.ConfigureAwait(false); }
            catch (Exception error)
            {
                failures.Add(error);
                if (operations.Exception is { InnerExceptions.Count: > 1 } combined)
                {
                    foreach (var additional in combined.InnerExceptions)
                    {
                        // Task.WhenAll unwraps one failure. Keep other operation
                        // failures without flattening an original job aggregate.
                        if (!ReferenceEquals(additional, error)) { failures.Add(additional); }
                    }
                }
            }
            if (releaseDependent != null)
            {
                try
                {
                    if (dispatch == null) { releaseDependent(failures.Run); }
                    else { await dispatch(() => releaseDependent(failures.Run)).ConfigureAwait(false); }
                }
                catch (Exception error) { failures.Add(error); }
            }
            var failure = failures.Capture();
            if (failure == null) { m_completion.TrySetResult(); }
            else
            {
                m_completion.TrySetException(failure.SourceException);
                _ = Completion.Exception;
            }
        }

        private static void ReleaseActions(Action<Action<Action>> releaseOwned)
        {
            var failures = new CleanupFailures();
            failures.Run(() => releaseOwned(failures.Run));
            failures.Capture()?.Throw();
        }

        private sealed class CleanupFailures
        {
            private ExceptionDispatchInfo? m_first;
            private List<Exception>? m_later;

            internal void Run(Action action)
            {
                try { action(); }
                catch (Exception error)
                {
                    Add(error);
                }
            }

            internal void Add(Exception error)
            {
                if (m_first == null) { m_first = ExceptionDispatchInfo.Capture(error); }
                else { (m_later ??= []).Add(error); }
            }

            internal ExceptionDispatchInfo? Capture()
            {
                if (m_later != null)
                {
                    m_first!.SourceException.Data["MainWindowLifetime.CleanupExceptions"] = new AggregateException(m_later);
                }
                return m_first;
            }
        }

        internal static void DisposeSubscriptions(CompositeDisposable? subscriptions, Action<Action> release)
        {
            if (subscriptions == null) { return; }
            // Remove detaches ownership before disposing the child. A failed child
            // must not strand its siblings or be invoked again by aggregate Dispose.
            foreach (var subscription in subscriptions.ToArray())
            {
                release(() => subscriptions.Remove(subscription));
            }
            release(subscriptions.Dispose);
        }
    }
}
