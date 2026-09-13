using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using FancyWM.DllImports;

namespace FancyWM.Utilities
{
    internal sealed class LowLevelMouseHook : IDisposable
    {
        public enum MouseButton
        {
            Left,
            Right,
            Middle,
        }

        public struct ButtonStateChangedEventArgs(LowLevelMouseHook.MouseButton button, bool isPressed, int ptX, int ptY)
        {
            public readonly MouseButton Button = button;
            public readonly bool IsPressed = isPressed;
            public bool Handled = false;
            public readonly int X = ptX;
            public readonly int Y = ptY;
        }

        public delegate void ButtonStateChangedEventHandler(object? sender, ref ButtonStateChangedEventArgs e);

        public event ButtonStateChangedEventHandler? ButtonStateChanged;

        private static readonly TimeSpan RehookIdleInterval = TimeSpan.FromSeconds(5);

        private readonly HOOKPROC m_hookProcDelegate;
        private readonly Thread m_hookThread;
        private readonly HookRegistrationPolicy.ThreadLifetime m_lifetime;
        private HHOOK m_hHook;
        private DateTime m_lastActiveTimestamp = DateTime.UtcNow;

        internal Task Completion => m_lifetime.Completion;

        internal Thread WorkerThread => m_hookThread;

        public LowLevelMouseHook()
            : this(
                static (owner, lifetime) => owner.HookThreadMessageLoop(lifetime),
                static threadId => PInvoke.PostThreadMessage(
                    threadId, Constants.WM_QUIT, new(0), new(0)))
        {
        }

        internal LowLevelMouseHook(
            Action<LowLevelMouseHook, HookRegistrationPolicy.ThreadLifetime> worker,
            Func<uint, bool> postQuit,
            Action<Thread>? startThread = null)
        {
            m_hookProcDelegate = HookProc;
            m_lifetime = new(postQuit);
            m_hookThread = new Thread(() => RunWorker(worker))
            {
                Name = "LowLevelMouseHookThread",
                IsBackground = true,
            };
            m_hookThread.SetApartmentState(ApartmentState.STA);
            try
            {
                (startThread ?? (static thread => thread.Start()))(m_hookThread);
            }
            catch (Exception error)
            {
                m_lifetime.Complete(error);
                GC.SuppressFinalize(this);
                throw;
            }
        }

        private void RunWorker(
            Action<LowLevelMouseHook, HookRegistrationPolicy.ThreadLifetime> worker)
        {
            Exception? failure = null;
            try { worker(this, m_lifetime); }
            catch (Exception error) { failure = error; }
            finally { m_lifetime.Complete(failure); }
        }

        private void HookThreadMessageLoop(HookRegistrationPolicy.ThreadLifetime lifetime)
        {
            // Message queues are lazily created so we force the creation of one by asking for its status
            _ = PInvoke.GetQueueStatus(GetQueueStatus_flags.QS_ALLEVENTS);
            if (!lifetime.PublishQueue(PInvoke.GetCurrentThreadId())) { return; }
            if (lifetime.IsStopping) { return; }

            HINSTANCE hInstance = new(PInvoke.GetModuleHandle(new PCWSTR()));
            Func<DateTime> utcNow = static () => DateTime.UtcNow;
            Func<HHOOK> install = () => PInvoke.SetWindowsHookEx(
                SetWindowsHookEx_idHook.WH_MOUSE_LL, m_hookProcDelegate, hInstance, 0);
            Func<HHOOK, bool> unhook = static hook => PInvoke.UnhookWindowsHookEx(hook);

            RunOwnedMessageLoop(ref m_hHook, install,
                static () => PInvoke.SetTimer(new HWND(), 0, 1000, null),
                (ref HHOOK current) => HookRegistrationPolicy.RunMessageLoop(ref current,
                    lifetime, HookRegistrationPolicy.ReadNativeMessage,
                    (ref HHOOK hook) => HookRegistrationPolicy.RefreshIfIdle(ref hook,
                        ref m_lastActiveTimestamp, RehookIdleInterval, utcNow, install, unhook),
                    static (in MSG message) => { _ = PInvoke.DispatchMessage(in message); }),
                static timerId => PInvoke.KillTimer(new HWND(), timerId), unhook);
        }

        internal static void RunOwnedMessageLoop(ref HHOOK current, Func<HHOOK> install,
            Func<nuint> createTimer, HookRegistrationPolicy.MessageLoop runLoop,
            Func<nuint, bool> killTimer, Func<HHOOK, bool> unhook)
            => HookRegistrationPolicy.RunLoop(ref current, install, createTimer, runLoop,
                killTimer, unhook, "Failed to set global WH_MOUSE_LL!",
                "Failed to unset the global WH_MOUSE_LL!");

        private LRESULT HookProc(int code, WPARAM wParam, LPARAM lParam)
        {
            if (code >= Constants.HC_ACTION)
            {
                // Update the timestamp
                m_lastActiveTimestamp = DateTime.UtcNow;

                ButtonStateChangedEventArgs? e;
                switch (wParam.Value)
                {
                    case Constants.WM_LBUTTONDOWN:
                        {
                            var mshs = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                            e = new ButtonStateChangedEventArgs(MouseButton.Left, true, mshs.pt.x, mshs.pt.y);
                            break;
                        }

                    case Constants.WM_LBUTTONUP:
                        {
                            var mshs = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                            e = new ButtonStateChangedEventArgs(MouseButton.Left, false, mshs.pt.x, mshs.pt.y);
                            break;
                        }

                    case Constants.WM_RBUTTONDOWN:
                        {
                            var mshs = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                            e = new ButtonStateChangedEventArgs(MouseButton.Right, true, mshs.pt.x, mshs.pt.y);
                            break;
                        }

                    case Constants.WM_RBUTTONUP:
                        {
                            var mshs = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                            e = new ButtonStateChangedEventArgs(MouseButton.Right, false, mshs.pt.x, mshs.pt.y);
                            break;
                        }

                    case Constants.WM_MBUTTONDOWN:
                        {
                            var mshs = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                            e = new ButtonStateChangedEventArgs(MouseButton.Middle, true, mshs.pt.x, mshs.pt.y);
                            break;
                        }

                    case Constants.WM_MBUTTONUP:
                        {
                            var mshs = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                            e = new ButtonStateChangedEventArgs(MouseButton.Middle, false, mshs.pt.x, mshs.pt.y);
                            break;
                        }
                    default:
                        return PInvoke.CallNextHookEx(new HHOOK(), code, wParam, lParam);
                }

                if (e is ButtonStateChangedEventArgs evt)
                {
                    if (DispatchButtonStateChanged(ref evt))
                    {
                        return new(PInvoke.CallNextHookEx(new HHOOK(), code, wParam, lParam) | 1);
                    }
                }
            }

            return PInvoke.CallNextHookEx(new HHOOK(), code, wParam, lParam);
        }

        internal bool DispatchButtonStateChanged(ref ButtonStateChangedEventArgs e)
        {
            if (!m_lifetime.TryEnterCallback()) { return false; }
            try
            {
                ButtonStateChanged?.Invoke(this, ref e);
                return e.Handled && !m_lifetime.IsStopping;
            }
            finally
            {
                m_lifetime.ExitCallback();
            }
        }

        private void Dispose(bool disposing)
        {
            if (disposing) { m_lifetime?.RequestStop(); }
            else { m_lifetime?.RequestStopFromFinalizer(); }
        }

        ~LowLevelMouseHook()
        {
            Dispose(disposing: false);
        }

        internal void RequestFinalizerStopForTest() => Dispose(disposing: false);

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
