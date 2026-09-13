using System;
using System.Threading;
using System.Threading.Tasks;

using WinMan;

namespace FancyWM.Toasts
{
    internal class ToastService : IToastService, IDisposable
    {
        private readonly ToastWindow m_toastWindow;
        private readonly CancellationTokenSource m_shutdown = new();
        private readonly CancellationToken m_shutdownToken;
        private int m_disposed;

        public ToastService(IWorkspace workspace)
            : this(App.Current.Dispatcher.Invoke(() => new ToastWindow(workspace)))
        {
        }

        internal ToastService(ToastWindow toastWindow)
        {
            m_toastWindow = toastWindow ?? throw new ArgumentNullException(nameof(toastWindow));
            m_shutdownToken = m_shutdown.Token;
            m_toastWindow.Dispatcher.Invoke(() => m_toastWindow.Closed += OnWindowClosed);
        }

        public async Task ShowToastAsync(object content, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref m_disposed) != 0 || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var completeRegistration = cancellationToken.Register(() => tcs.TrySetResult());
            using var shutdownRegistration = m_shutdownToken.Register(() => tcs.TrySetResult());
            await m_toastWindow.Dispatcher.InvokeAsync(() =>
            {
                if (Volatile.Read(ref m_disposed) != 0 || cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetResult();
                    return;
                }
                m_toastWindow.ShowToast(content, cancellationToken);
            });
            await tcs.Task;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref m_disposed, 1) != 0) { return; }
            m_shutdown.Cancel();
            try
            {
                m_toastWindow.Dispatcher.Invoke(() =>
                {
                    m_toastWindow.Closed -= OnWindowClosed;
                    m_toastWindow.Dispose();
                });
            }
            finally { m_shutdown.Dispose(); }
        }

        private void OnWindowClosed(object? sender, EventArgs e) => Dispose();
    }
}
