using System;
using System.Reactive.Disposables;
using System.Windows.Threading;

namespace FancyWM.Utilities
{
    internal sealed class OverlayRefreshLoop(Action update, Func<Action, IDisposable> subscribe) : IDisposable
    {
        private readonly Dispatcher m_dispatcher = Dispatcher.CurrentDispatcher;
        private IDisposable? m_subscription;
        private long m_generation;
        private bool m_disposed;
        private bool m_started;

        public static IDisposable SubscribeTimer(Action callback)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            EventHandler tick = (_, _) => callback();
            timer.Tick += tick;
            timer.Start();
            return Disposable.Create(() => { timer.Stop(); timer.Tick -= tick; });
        }

        public void Start()
        {
            m_dispatcher.VerifyAccess();
            if (m_disposed || m_started) { return; }
            m_started = true;
            long generation = ++m_generation;
            try
            {
                var subscription = subscribe(() =>
                {
                    m_dispatcher.VerifyAccess();
                    if (!m_disposed && generation == m_generation) { update(); }
                });
                if (!m_disposed && generation == m_generation)
                {
                    m_subscription = subscription;
                }
                else
                {
                    subscription.Dispose();
                }
            }
            catch
            {
                if (generation == m_generation) { m_started = false; }
                throw;
            }
        }

        public void Stop()
        {
            m_dispatcher.VerifyAccess();
            m_generation++;
            m_started = false;
            var subscription = m_subscription;
            m_subscription = null;
            subscription?.Dispose();
        }

        public void Dispose()
        {
            m_dispatcher.VerifyAccess();
            m_disposed = true;
            Stop();
        }
    }
}
