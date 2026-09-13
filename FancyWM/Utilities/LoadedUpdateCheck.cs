using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FancyWM.Utilities
{
    internal sealed class LoadedUpdateCheck(
        MainWindowLifetime lifetime,
        Func<Func<Task>, Task> post,
        Action<Exception> reportFailure,
        Func<bool> shutdownRequested)
    {
        private readonly object m_gate = new();
        private int m_activeOperations;
        private TaskCompletionSource? m_idle;
        private List<Exception>? m_failures;
        private OperationCanceledException? m_cancellation;

        internal Task Completion
        {
            get
            {
                lock (m_gate) { return m_idle?.Task ?? Task.CompletedTask; }
            }
        }

        internal void Start(Func<Task> run)
        {
            bool accepted = false;
            lock (m_gate)
            {
                lifetime.RunIfActive(() =>
                {
                    if (m_activeOperations != 0) { return; }
                    m_idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    m_activeOperations++;
                    accepted = true;
                });
            }
            if (!accepted) { return; }

            Task operation;
            var invocation = new Invocation();
            try
            {
                operation = post(() => RunAsync(run, invocation))
                    ?? throw new InvalidOperationException("The update-check dispatcher returned no task.");
            }
            catch
            {
                CompleteOperation();
                throw;
            }
            _ = ObserveOperationAsync(operation, invocation);
        }

        private async Task RunAsync(Func<Task> run, Invocation invocation)
        {
            try
            {
                try
                {
                    await lifetime.RunIfActiveAsync(run);
                }
                catch (OperationCanceledException) when (shutdownRequested())
                {
                }
                catch (Exception error)
                {
                    reportFailure(error);
                }
            }
            catch (OperationCanceledException error)
            {
                invocation.Cancellation = error;
                throw;
            }
        }

        private async Task ObserveOperationAsync(Task operation, Invocation invocation)
        {
            Exception? failure = null;
            OperationCanceledException? cancellation = null;
            try
            {
                await operation.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                shutdownRequested() && invocation.Cancellation == null)
            {
            }
            catch (OperationCanceledException error)
            {
                cancellation = error;
            }
            catch (Exception error)
            {
                failure = error;
            }
            CompleteOperation(failure, cancellation);
        }

        private sealed class Invocation
        {
            internal OperationCanceledException? Cancellation;
        }

        private void CompleteOperation(
            Exception? failure = null,
            OperationCanceledException? cancellation = null)
        {
            TaskCompletionSource? idle = null;
            Exception[]? failures = null;
            OperationCanceledException? completedCancellation = null;
            lock (m_gate)
            {
                if (failure != null) { (m_failures ??= []).Add(failure); }
                m_cancellation ??= cancellation;
                if (--m_activeOperations == 0)
                {
                    idle = m_idle;
                    failures = m_failures?.ToArray();
                    m_failures = null;
                    completedCancellation = m_cancellation;
                    m_cancellation = null;
                }
            }
            if (idle == null) { return; }
            if (failures is { Length: > 0 })
            {
                idle.TrySetException(failures.Length == 1 ? failures[0] : new AggregateException(failures));
                _ = idle.Task.Exception;
            }
            else if (completedCancellation != null)
            {
                idle.TrySetCanceled(completedCancellation.CancellationToken);
            }
            else
            {
                idle.TrySetResult();
            }
        }
    }
}
