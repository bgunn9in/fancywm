using System;
using System.Threading.Tasks;

namespace FancyWM.Utilities
{
    internal sealed class LayoutInvalidationQueue(
        Action<Action> schedule,
        Func<bool> canRun,
        Func<Task> apply,
        Action<Exception> reportError) : IDisposable
    {
        private readonly object m_lock = new();
        private readonly TaskCompletionSource m_completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool m_dirty;
        private bool m_scheduled;
        private bool m_disposed;
        private int m_activeCallbacks;
        private long m_revision;
        private Action? m_execute;

        internal Task Completion => m_completion.Task;

        public void Invalidate()
        {
            lock (m_lock)
            {
                if (m_disposed) { return; }
                m_dirty = true;
                m_revision++;
            }
            ScheduleIfNeeded();
        }

        private void ScheduleIfNeeded()
        {
            lock (m_lock)
            {
                if (m_disposed || m_scheduled || !m_dirty) { return; }
                m_activeCallbacks++;
            }
            try { ScheduleIfNeededCore(); }
            finally { CompleteCallback(); }
        }

        private void ScheduleIfNeededCore()
        {
            while (true)
            {
                long revision;
                lock (m_lock)
                {
                    if (m_disposed || m_scheduled || !m_dirty) { return; }
                    // Eligibility can dispose this owner reentrantly.
                    if (!canRun() || m_disposed) { return; }
                    m_scheduled = true;
                    revision = m_revision;
                }
                try
                {
                    schedule(m_execute ??= Execute);
                    return;
                }
                catch (Exception exception)
                {
                    bool retry;
                    lock (m_lock)
                    {
                        m_scheduled = false;
                        retry = m_revision != revision;
                    }
                    reportError(exception);
                    if (!retry) { return; }
                }
            }
        }

        private async void Execute()
        {
            bool entered = false;
            try
            {
                lock (m_lock)
                {
                    if (m_disposed) { return; }
                    m_activeCallbacks++;
                    entered = true;
                    if (!canRun() || m_disposed) { return; }
                    m_dirty = false;
                }
                await apply();
            }
            catch (Exception exception)
            {
                reportError(exception);
            }
            finally
            {
                lock (m_lock) { m_scheduled = false; }
                try { ScheduleIfNeeded(); }
                finally
                {
                    if (entered) { CompleteCallback(); }
                }
            }
        }

        private void CompleteCallback()
        {
            lock (m_lock)
            {
                m_activeCallbacks--;
                if (m_disposed && m_activeCallbacks == 0) { m_completion.TrySetResult(); }
            }
        }

        public void Dispose()
        {
            lock (m_lock)
            {
                m_disposed = true;
                m_dirty = false;
                // A queued callback owns no dependencies until it enters Execute.
                // Entered apply/scheduler/error callbacks retain their ownership
                // until they return; Completion never waits on an idle Dispatcher.
                if (m_activeCallbacks == 0) { m_completion.TrySetResult(); }
            }
        }
    }
}
