using System;
using System.Collections.Generic;
using System.Linq;

namespace FancyWM.AlgorithmicLayouts
{
    internal readonly record struct ArrangeFailureCandidate(
        IntPtr Handle,
        long Generation,
        bool IsNewWindow);

    internal enum ArrangeFailureDisposition
    {
        NoCandidate,
        LegacyFloatingCandidate,
        AlgorithmicOverflowCandidate,
    }

    internal readonly record struct ArrangeFailureDecision(
        ArrangeFailureDisposition Disposition,
        IntPtr CandidateHandle)
    {
        public bool HasCandidate => Disposition != ArrangeFailureDisposition.NoCandidate;
    }

    /// <summary>
    /// Pure policy for choosing the window associated with an arrange failure.
    /// Algorithmic layouts may only choose a window which is actually in the
    /// service's new-window set. The legacy path retains FancyWM's newest-node
    /// fallback when no algorithmic runtime state owns the tree.
    /// </summary>
    internal static class MasterSatelliteArrangeFailurePolicy
    {
        public static ArrangeFailureDecision Decide(
            bool algorithmicLayoutActive,
            IEnumerable<ArrangeFailureCandidate> candidates)
        {
            ArgumentNullException.ThrowIfNull(candidates);

            var eligible = algorithmicLayoutActive
                ? candidates.Where(candidate => candidate.IsNewWindow)
                : candidates;
            var selected = eligible
                .OrderByDescending(candidate => candidate.Generation)
                .FirstOrDefault();
            if (selected == default)
            {
                return new ArrangeFailureDecision(
                    ArrangeFailureDisposition.NoCandidate,
                    IntPtr.Zero);
            }

            return new ArrangeFailureDecision(
                algorithmicLayoutActive
                    ? ArrangeFailureDisposition.AlgorithmicOverflowCandidate
                    : ArrangeFailureDisposition.LegacyFloatingCandidate,
                selected.Handle);
        }
    }

    /// <summary>
    /// Suppresses repeated failure notifications while an HWND remains in a
    /// failing tree. Handles are released as soon as they disappear, preventing
    /// stale suppression when Windows later reuses an HWND.
    /// </summary>
    internal sealed class ArrangeFailureNotificationTracker
    {
        private readonly HashSet<IntPtr> m_pendingHandles = [];
        private readonly object m_lock = new();

        public bool TryMark(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
            {
                return false;
            }
            lock (m_lock)
            {
                return m_pendingHandles.Add(handle);
            }
        }

        public void ReleaseResolved(IEnumerable<IntPtr> currentTreeHandles)
        {
            ArgumentNullException.ThrowIfNull(currentTreeHandles);
            var retained = currentTreeHandles.ToHashSet();
            lock (m_lock)
            {
                m_pendingHandles.IntersectWith(retained);
            }
        }

        public void Forget(IntPtr handle)
        {
            lock (m_lock)
            {
                m_pendingHandles.Remove(handle);
            }
        }
    }
}
