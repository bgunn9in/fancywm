using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;

using WinMan;

namespace FancyWM.AlgorithmicLayouts
{
    internal sealed record ManualDesktopMoveMarker(
        IntPtr WindowHandle,
        IVirtualDesktop SourceDesktop,
        IVirtualDesktop TargetDesktop,
        DateTimeOffset CreatedAt,
        DateTimeOffset Deadline);

    /// <summary>
    /// Dispatcher-confined correlation state for workspace window events. Window
    /// wrappers are keyed by reference identity because a dead replacement wrapper
    /// must not inherit the cached HWND of another object that compares equal.
    /// </summary>
    internal sealed class AlgorithmicWindowTransferEventTracker
    {
        private static readonly TimeSpan DefaultManualMoveRetention = TimeSpan.FromSeconds(30);

        private readonly Dispatcher m_dispatcher;
        private readonly TimeProvider m_timeProvider;
        private readonly TimeSpan m_manualMoveRetention;
        private readonly Dictionary<IWindow, IntPtr> m_windowHandles
            = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<IntPtr, IWindow> m_windowsByHandle = [];
        private readonly Dictionary<IntPtr, IVirtualDesktop> m_knownDesktops = [];
        private readonly Dictionary<IntPtr, ManualDesktopMoveMarker> m_manualMoves = [];

        public AlgorithmicWindowTransferEventTracker(
            Dispatcher dispatcher,
            TimeProvider? timeProvider = null,
            TimeSpan? manualMoveRetention = null)
        {
            m_dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            m_timeProvider = timeProvider ?? TimeProvider.System;
            m_manualMoveRetention = manualMoveRetention ?? DefaultManualMoveRetention;
            if (m_manualMoveRetention <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(manualMoveRetention),
                    "The manual desktop-move retention must be positive.");
            }
        }

        public bool TryGetStableWindowHandle(IWindow window, out IntPtr windowHandle)
        {
            return TryGetStableWindowHandle(
                window,
                out windowHandle,
                out _);
        }

        public bool TryGetStableWindowHandle(
            IWindow window,
            out IntPtr windowHandle,
            out bool replacedWindowGeneration)
        {
            return TryGetStableWindowHandle(
                window,
                out windowHandle,
                out replacedWindowGeneration,
                out _);
        }

        public bool TryGetStableWindowHandle(
            IWindow window,
            out IntPtr windowHandle,
            out bool replacedWindowGeneration,
            out IWindow? previousWindowGeneration)
        {
            ArgumentNullException.ThrowIfNull(window);
            VerifyAccess();
            replacedWindowGeneration = false;
            previousWindowGeneration = null;

            if (m_windowHandles.TryGetValue(window, out windowHandle))
            {
                return windowHandle != IntPtr.Zero;
            }

            try
            {
                windowHandle = window.Handle;
            }
            catch (InvalidWindowReferenceException)
            {
                windowHandle = IntPtr.Zero;
                return false;
            }
            if (windowHandle == IntPtr.Zero)
            {
                return false;
            }

            if (m_windowsByHandle.TryGetValue(windowHandle, out var previousWindow)
                && !ReferenceEquals(previousWindow, window))
            {
                // A newly observed wrapper owns a new HWND generation. Desktop
                // and manual-move evidence from the prior native window must not
                // be inherited merely because Windows reused the numeric handle.
                m_knownDesktops.Remove(windowHandle);
                m_manualMoves.Remove(windowHandle);
                replacedWindowGeneration = true;
                previousWindowGeneration = previousWindow;
            }
            m_windowHandles.Add(window, windowHandle);
            m_windowsByHandle[windowHandle] = window;
            return true;
        }

        /// <summary>
        /// Returns a handle already captured for this exact wrapper without
        /// making it the current generation for that HWND. Removal callbacks
        /// use this so a late event from an old wrapper cannot displace a newer
        /// wrapper after native handle reuse.
        /// </summary>
        public bool TryGetRememberedWindowHandle(
            IWindow window,
            out IntPtr windowHandle)
        {
            ArgumentNullException.ThrowIfNull(window);
            VerifyAccess();
            return m_windowHandles.TryGetValue(window, out windowHandle)
                && windowHandle != IntPtr.Zero;
        }

        public bool TryGetWindow(IntPtr windowHandle, out IWindow window)
        {
            VerifyAccess();
            if (windowHandle == IntPtr.Zero)
            {
                window = null!;
                return false;
            }
            return m_windowsByHandle.TryGetValue(windowHandle, out window!);
        }

        public void RememberDesktop(IntPtr windowHandle, IVirtualDesktop desktop)
        {
            ArgumentNullException.ThrowIfNull(desktop);
            VerifyAccess();
            ValidateWindowHandle(windowHandle);
            m_knownDesktops[windowHandle] = desktop;
        }

        public bool TryGetKnownDesktop(
            IntPtr windowHandle,
            out IVirtualDesktop desktop)
        {
            VerifyAccess();
            if (windowHandle == IntPtr.Zero)
            {
                desktop = null!;
                return false;
            }
            return m_knownDesktops.TryGetValue(windowHandle, out desktop!);
        }

        /// <summary>
        /// Records a manual move when the observed desktop differs from the last
        /// known desktop, then advances known ownership. Re-observing the target
        /// (the opposite workspace-event order or a duplicate event) preserves the
        /// existing marker until it is consumed or expires.
        /// </summary>
        public bool ObservePotentialManualMove(
            IntPtr windowHandle,
            IVirtualDesktop observedDesktop,
            out ManualDesktopMoveMarker marker)
        {
            ArgumentNullException.ThrowIfNull(observedDesktop);
            VerifyAccess();
            ValidateWindowHandle(windowHandle);
            CleanupExpiredCore(m_timeProvider.GetUtcNow());

            bool changedDesktop = m_knownDesktops.TryGetValue(
                    windowHandle,
                    out var sourceDesktop)
                && !MasterSatelliteDisplayEligibility.DesktopsMatch(
                    sourceDesktop,
                    observedDesktop);
            m_knownDesktops[windowHandle] = observedDesktop;
            if (!changedDesktop)
            {
                marker = null!;
                return false;
            }

            var now = m_timeProvider.GetUtcNow();
            marker = RecordManualMoveCore(
                windowHandle,
                sourceDesktop!,
                observedDesktop,
                now);
            return true;
        }

        public bool RecordManualMove(
            IntPtr windowHandle,
            IVirtualDesktop sourceDesktop,
            IVirtualDesktop targetDesktop,
            out ManualDesktopMoveMarker marker)
        {
            ArgumentNullException.ThrowIfNull(sourceDesktop);
            ArgumentNullException.ThrowIfNull(targetDesktop);
            VerifyAccess();
            ValidateWindowHandle(windowHandle);
            var now = m_timeProvider.GetUtcNow();
            CleanupExpiredCore(now);
            m_knownDesktops[windowHandle] = targetDesktop;
            if (MasterSatelliteDisplayEligibility.DesktopsMatch(
                sourceDesktop,
                targetDesktop))
            {
                marker = null!;
                return false;
            }

            marker = RecordManualMoveCore(
                windowHandle,
                sourceDesktop,
                targetDesktop,
                now);
            return true;
        }

        public bool TryConsumeManualMove(
            IntPtr windowHandle,
            IVirtualDesktop targetDesktop,
            out ManualDesktopMoveMarker marker)
        {
            ArgumentNullException.ThrowIfNull(targetDesktop);
            VerifyAccess();
            if (windowHandle == IntPtr.Zero)
            {
                marker = null!;
                return false;
            }
            CleanupExpiredCore(m_timeProvider.GetUtcNow());
            if (!m_manualMoves.TryGetValue(windowHandle, out marker!)
                || !MasterSatelliteDisplayEligibility.DesktopsMatch(
                    marker.TargetDesktop,
                    targetDesktop))
            {
                marker = null!;
                return false;
            }

            m_manualMoves.Remove(windowHandle);
            return true;
        }

        public bool TryGetManualMove(
            IntPtr windowHandle,
            IVirtualDesktop targetDesktop,
            out ManualDesktopMoveMarker marker)
        {
            ArgumentNullException.ThrowIfNull(targetDesktop);
            VerifyAccess();
            if (windowHandle == IntPtr.Zero)
            {
                marker = null!;
                return false;
            }
            CleanupExpiredCore(m_timeProvider.GetUtcNow());
            if (!m_manualMoves.TryGetValue(windowHandle, out marker!)
                || !MasterSatelliteDisplayEligibility.DesktopsMatch(
                    marker.TargetDesktop,
                    targetDesktop))
            {
                marker = null!;
                return false;
            }
            return true;
        }

        public int CleanupExpired()
        {
            VerifyAccess();
            return CleanupExpiredCore(m_timeProvider.GetUtcNow());
        }

        public bool Forget(IWindow window)
        {
            ArgumentNullException.ThrowIfNull(window);
            VerifyAccess();
            if (!m_windowHandles.Remove(window, out var windowHandle))
            {
                return false;
            }

            if (m_windowsByHandle.TryGetValue(windowHandle, out var current)
                && !ReferenceEquals(current, window))
            {
                return true;
            }
            if (m_windowsByHandle.TryGetValue(windowHandle, out current)
                && ReferenceEquals(current, window))
            {
                m_windowsByHandle.Remove(windowHandle);
            }

            m_knownDesktops.Remove(windowHandle);
            m_manualMoves.Remove(windowHandle);
            return true;
        }

        public bool Forget(IntPtr windowHandle)
        {
            VerifyAccess();
            if (windowHandle == IntPtr.Zero)
            {
                return false;
            }

            bool removed = m_knownDesktops.Remove(windowHandle);
            removed |= m_manualMoves.Remove(windowHandle);
            removed |= m_windowsByHandle.Remove(windowHandle);
            foreach (var window in m_windowHandles
                .Where(item => item.Value == windowHandle)
                .Select(item => item.Key)
                .ToArray())
            {
                removed |= m_windowHandles.Remove(window);
            }
            return removed;
        }

        public void Clear()
        {
            VerifyAccess();
            m_windowHandles.Clear();
            m_windowsByHandle.Clear();
            m_knownDesktops.Clear();
            m_manualMoves.Clear();
        }

        private int CleanupExpiredCore(DateTimeOffset now)
        {
            var expiredHandles = m_manualMoves
                .Where(item => item.Value.Deadline <= now)
                .Select(item => item.Key)
                .ToArray();
            foreach (var windowHandle in expiredHandles)
            {
                m_manualMoves.Remove(windowHandle);
            }
            return expiredHandles.Length;
        }

        private ManualDesktopMoveMarker RecordManualMoveCore(
            IntPtr windowHandle,
            IVirtualDesktop sourceDesktop,
            IVirtualDesktop targetDesktop,
            DateTimeOffset now)
        {
            var marker = new ManualDesktopMoveMarker(
                windowHandle,
                sourceDesktop,
                targetDesktop,
                now,
                now + m_manualMoveRetention);
            m_manualMoves[windowHandle] = marker;
            return marker;
        }

        private void VerifyAccess()
        {
            m_dispatcher.VerifyAccess();
        }

        private static void ValidateWindowHandle(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
            {
                throw new ArgumentException(
                    "The tracked window handle must not be zero.",
                    nameof(windowHandle));
            }
        }
    }
}
