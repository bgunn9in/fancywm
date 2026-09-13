using System;
using System.Diagnostics;
using System.Threading;

namespace FancyWM.Utilities
{
    public class BindingErrorListener(Action<string?> logAction) : TraceListener
    {
        private Action<string?>? m_logAction = logAction;

        public static BindingErrorListener Listen(Action<string?> logAction)
        {
            var listener = new BindingErrorListener(logAction);
            PresentationTraceSources.DataBindingSource.Listeners
                .Add(listener);
            return listener;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Release the callback owner and close admission before removing
                // the registration; a previously admitted callback may finish.
                Interlocked.Exchange(ref m_logAction, null);
                PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
            }
            base.Dispose(disposing);
        }

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            Volatile.Read(ref m_logAction)?.Invoke(message);
        }
    }
}
