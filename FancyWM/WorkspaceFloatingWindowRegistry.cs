using System;
using System.Collections.Generic;

using WinMan;

namespace FancyWM
{
    /// <summary>
    /// Workspace-wide floating ownership keyed by the stable native window handle.
    /// A <see cref="TilingService"/> keeps its local reference set for its own
    /// bookkeeping, while this registry prevents another display service from
    /// accidentally tiling the same explicitly floating window during an
    /// Added/Removed or display-hotplug race.
    /// </summary>
    internal sealed class WorkspaceFloatingWindowRegistry
    {
        private readonly object m_syncRoot = new();
        private readonly Dictionary<IntPtr, FloatingOwner> m_windows = [];

        internal bool IsLockHeldByCurrentThread
            => System.Threading.Monitor.IsEntered(m_syncRoot);

        public bool Contains(IWindow window)
        {
            return TryContains(window, out var isFloating) && isFloating;
        }

        public bool TryContains(IWindow window, out bool isFloating)
        {
            ArgumentNullException.ThrowIfNull(window);
            if (!TryGetHandle(window, out var windowHandle))
            {
                isFloating = false;
                return false;
            }
            isFloating = Contains(windowHandle);
            return true;
        }

        public bool Contains(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
            {
                return false;
            }
            return ContainsLiveOwner(windowHandle, candidate: null);
        }

        public bool Add(IWindow window)
        {
            ArgumentNullException.ThrowIfNull(window);
            if (!TryGetHandle(window, out var windowHandle))
            {
                return false;
            }
            if (ContainsLiveOwner(windowHandle, window))
            {
                return false;
            }
            lock (m_syncRoot)
            {
                if (m_windows.ContainsKey(windowHandle))
                {
                    return false;
                }
                m_windows.Add(windowHandle, new FloatingOwner(window));
                return true;
            }
        }

        public bool Remove(IWindow window)
        {
            ArgumentNullException.ThrowIfNull(window);
            return TryGetHandle(window, out var windowHandle)
                && Remove(windowHandle);
        }

        public bool Remove(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
            {
                return false;
            }
            lock (m_syncRoot)
            {
                return m_windows.Remove(windowHandle);
            }
        }

        public void Clear()
        {
            lock (m_syncRoot)
            {
                m_windows.Clear();
            }
        }

        private bool ContainsLiveOwner(
            IntPtr windowHandle,
            IWindow? candidate)
        {
            FloatingOwner owner;
            lock (m_syncRoot)
            {
                if (!m_windows.TryGetValue(windowHandle, out owner!))
                {
                    return false;
                }
                if (candidate != null && ReferenceEquals(owner.Window, candidate))
                {
                    return true;
                }
            }

            if (IsAlive(owner.Window))
            {
                return true;
            }

            lock (m_syncRoot)
            {
                if (m_windows.TryGetValue(windowHandle, out var current)
                    && ReferenceEquals(current, owner))
                {
                    m_windows.Remove(windowHandle);
                }
            }
            return false;
        }

        private static bool IsAlive(IWindow window)
        {
            try
            {
                return window.IsAlive;
            }
            catch (InvalidWindowReferenceException)
            {
                return false;
            }
        }

        private static bool TryGetHandle(IWindow window, out IntPtr windowHandle)
        {
            try
            {
                windowHandle = window.Handle;
                return windowHandle != IntPtr.Zero;
            }
            catch (InvalidWindowReferenceException)
            {
                windowHandle = IntPtr.Zero;
                return false;
            }
        }

        private sealed record FloatingOwner(IWindow Window);
    }
}
