using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using FancyWM.DllImports;

namespace FancyWM.Utilities
{
    internal static class FocusHelper
    {
        internal interface INative
        {
            bool SetForegroundWindow(IntPtr window);
            void EnsureMessageQueue();
            uint CurrentThreadId { get; }
            uint ForegroundThreadId { get; }
            bool AttachThreadInput(uint thread, uint foregroundThread, bool attach);
            void SendAltKeyPresses();
        }

        internal sealed class RequestSequence
        {
            private readonly object m_gate = new();
            private object? m_current;
            private readonly Func<Task<bool>, bool> m_wait;
            private readonly Action<Thread> m_start;
            private Thread? m_worker;
            private ThreadStart? m_workerStart;
            private FallbackRequest? m_pending;
            private bool m_stopped;

            public RequestSequence() : this(task => task.Wait(100), thread => thread.Start()) { }

            internal RequestSequence(Func<Task<bool>, bool> wait, Action<Thread> start)
            {
                m_wait = wait;
                m_start = start;
            }

            internal bool Wait(Task<bool> task) => m_wait(task);

            internal void Stop()
            {
                lock (m_gate)
                {
                    m_stopped = true;
                    Volatile.Write(ref m_current, null);
                    m_pending?.Completion.TrySetResult(false);
                    m_pending = null;
                    // An entered native call still owns the worker until cleanup
                    // returns. Stopping admission neither joins nor abandons it.
                }
            }

            public object Begin()
            {
                var request = new object();
                lock (m_gate)
                {
                    if (m_stopped) return request;
                    Volatile.Write(ref m_current, request);
                    m_pending?.Completion.TrySetResult(false);
                    m_pending = null;
                }
                return request;
            }

            public bool IsCurrent(object request) => ReferenceEquals(Volatile.Read(ref m_current), request);

            internal void Expire(object request)
            {
                lock (m_gate)
                {
                    // An older caller's timeout must not invalidate a newer request.
                    if (IsCurrent(request)) Volatile.Write(ref m_current, null);
                    if (ReferenceEquals(m_pending?.Token, request))
                    {
                        m_pending.Completion.TrySetResult(false);
                        m_pending = null;
                    }
                }
            }

            internal Task<bool> Enqueue(object token, IntPtr window, INative native)
            {
                FallbackRequest request;
                Thread? start = null;
                lock (m_gate)
                {
                    if (!IsCurrent(token)) return Task.FromResult(false);
                    request = new FallbackRequest(token, window, native);
                    m_pending?.Completion.TrySetResult(false);
                    m_pending = request;
                    if (m_worker == null)
                    {
                        start = m_worker = new Thread(m_workerStart ??= RunWorker)
                        {
                            IsBackground = true,
                            Name = "FancyWM focus fallback",
                        };
                    }
                }
                if (start != null)
                {
                    try { m_start(start); }
                    catch
                    {
                        lock (m_gate)
                        {
                            // Do not abandon a worker that has actually started.
                            if (ReferenceEquals(m_worker, start)
                                && (start.ThreadState & ThreadState.Unstarted) != 0)
                            {
                                m_worker = null;
                                m_pending?.Completion.TrySetResult(false);
                                m_pending = null;
                            }
                        }
                        Expire(token);
                        return Task.FromResult(false);
                    }
                }
                return request.Completion.Task;
            }

            private void RunWorker()
            {
                while (true)
                {
                    FallbackRequest request;
                    lock (m_gate)
                    {
                        if (m_pending == null)
                        {
                            // No native work follows release of this slot. A newly
                            // admitted worker may overlap only this thread's return.
                            m_worker = null;
                            return;
                        }
                        request = m_pending;
                        m_pending = null;
                    }
                    bool focused = RunFallback(request.Window, request.Native, this, request.Token, out bool reusable);
                    request.Completion.TrySetResult(focused && IsCurrent(request.Token));
                    if (!reusable)
                    {
                        lock (m_gate)
                        {
                            // Never reuse a thread whose input attachment cleanup
                            // failed. Pending callers observe failure and may retry.
                            m_pending?.Completion.TrySetResult(false);
                            m_pending = null;
                            m_worker = null;
                        }
                        return;
                    }
                }
            }

            private sealed class FallbackRequest(object token, IntPtr window, INative native)
            {
                internal object Token { get; } = token;
                internal IntPtr Window { get; } = window;
                internal INative Native { get; } = native;
                internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        private static readonly RequestSequence s_requests = new();

        // MainWindow is the process's sole focus owner. Its shutdown is terminal;
        // per-display disposal must never stop this shared sequence.
        internal static void Stop() => s_requests.Stop();

        public static bool ForceActivate(IntPtr hwnd) => ForceActivate(hwnd, Native.Instance, s_requests);

        internal static bool ForceActivate(IntPtr hwnd, INative native) => ForceActivate(hwnd, native, new RequestSequence());

        internal static bool ForceActivate(IntPtr hwnd, INative native, RequestSequence requests)
        {
            object request = requests.Begin();
            if (!requests.IsCurrent(request)) return false;
            if (native.SetForegroundWindow(hwnd))
            {
                return requests.IsCurrent(request);
            }
            if (!requests.IsCurrent(request)) return false;

            Task<bool> fallback = requests.Enqueue(request, hwnd, native);
            try
            {
                // Timeout bounds the caller's wait, not an entered native call.
                // Its worker slot remains occupied until native cleanup returns.
                return requests.Wait(fallback) && fallback.IsCompletedSuccessfully
                    && fallback.GetAwaiter().GetResult() && requests.IsCurrent(request);
            }
            finally
            {
                requests.Expire(request);
            }
        }

        internal static bool RunFallback(IntPtr hwnd, INative native) => RunFallback(hwnd, native, null, null, out _);

        private static bool RunFallback(IntPtr hwnd, INative native, RequestSequence? requests, object? token, out bool reusable)
        {
            reusable = true;
            try
            {
                // Stop when supersession is observed between native stages.
                // The check-to-call interval is not atomic, and a native call
                // that has already been entered cannot be canceled here.
                if (!IsCurrent(requests, token)) return false;
                native.EnsureMessageQueue();
                if (!IsCurrent(requests, token)) return false;
                uint thread = native.CurrentThreadId;
                uint foregroundThread = native.ForegroundThreadId;
                if (!IsCurrent(requests, token)) return false;
                reusable = false;
                if (!native.AttachThreadInput(thread, foregroundThread, true))
                {
                    reusable = true;
                    return false;
                }

                bool focused = false;
                try
                {
                    if (!IsCurrent(requests, token)) return false;
                    focused = native.SetForegroundWindow(hwnd);
                    if (!IsCurrent(requests, token)) return false;
                    if (!focused)
                    {
                        native.SendAltKeyPresses();
                        if (!IsCurrent(requests, token)) return false;
                        focused = native.SetForegroundWindow(hwnd);
                    }
                }
                finally
                {
                    // Detach only our successful attachment, using its original IDs.
                    // Completion is published by the caller after this cleanup.
                    reusable = native.AttachThreadInput(thread, foregroundThread, false);
                    if (!reusable) focused = false;
                }
                return focused && IsCurrent(requests, token);
            }
            catch (Exception)
            {
                // A fallback failure must complete the request rather than escape
                // the worker and leave its completion source unresolved.
                return false;
            }
        }

        private static bool IsCurrent(RequestSequence? requests, object? token) =>
            requests == null || requests.IsCurrent(token!);

        private sealed class Native : INative
        {
            public static readonly Native Instance = new();
            public bool SetForegroundWindow(IntPtr window) => PInvoke.SetForegroundWindow(new(window));
            public void EnsureMessageQueue() =>
                PInvoke.PeekMessage(out MSG _, new HWND(-1), 0, 0, PeekMessage_wRemoveMsg.PM_NOREMOVE);
            public uint CurrentThreadId => PInvoke.GetCurrentThreadId();
            public unsafe uint ForegroundThreadId =>
                PInvoke.GetWindowThreadProcessId(new(PInvoke.GetForegroundWindow()), null);
            public bool AttachThreadInput(uint thread, uint foregroundThread, bool attach) =>
                PInvoke.AttachThreadInput(thread, foregroundThread, attach);

            public void SendAltKeyPresses()
            {
                // Preserve the existing two Alt keypresses used to disable the lock.
                INPUT[] inp = new INPUT[4];
                inp[0].type = inp[1].type = inp[2].type = inp[3].type = INPUT_typeFlags.INPUT_KEYBOARD;
                inp[0].Anonymous.ki.wVk = inp[1].Anonymous.ki.wVk = inp[2].Anonymous.ki.wVk = inp[3].Anonymous.ki.wVk = (ushort)Constants.VK_MENU;
                inp[0].Anonymous.ki.dwFlags = inp[2].Anonymous.ki.dwFlags = keybd_eventFlags.KEYEVENTF_EXTENDEDKEY;
                inp[1].Anonymous.ki.dwFlags = inp[3].Anonymous.ki.dwFlags = keybd_eventFlags.KEYEVENTF_EXTENDEDKEY | keybd_eventFlags.KEYEVENTF_KEYUP;
                PInvoke.SendInput(inp, Marshal.SizeOf<INPUT>());
            }
        }
    }
}
