using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;

using FancyWM.Models;

using WinMan;

namespace FancyWM.AlgorithmicLayouts
{
    internal enum AlgorithmicDesktopCreationClaimDisposition
    {
        Acquired,
        ExistingInFlight,
        AlreadyCompleted,
        LimitReached,
        WindowAlreadyClaimed,
        CorrelationAlreadyClaimed,
    }

    internal enum AlgorithmicDesktopCreationCompletionDisposition
    {
        SuccessRecorded,
        SuccessAlreadyRecorded,
        FailureReleased,
        ClaimNotFound,
        ClaimMismatch,
        DesktopAlreadyRecorded,
    }

    internal sealed record AlgorithmicDesktopCreationClaim(
        Guid ClaimId,
        Guid CorrelationId,
        IntPtr WindowHandle);

    internal readonly record struct AlgorithmicDesktopCreationAvailability(
        bool CanCreate,
        int MaxAutoCreatedDesktops,
        int SuccessfulCreationCount,
        int InFlightCount,
        int RemainingCreationCount,
        string? DiagnosticReason);

    internal readonly record struct AlgorithmicDesktopCreationClaimResult(
        AlgorithmicDesktopCreationClaimDisposition Disposition,
        AlgorithmicDesktopCreationClaim? Claim,
        AlgorithmicDesktopCreationAvailability Availability,
        string? DiagnosticReason)
    {
        public bool Accepted => Disposition is
            AlgorithmicDesktopCreationClaimDisposition.Acquired
            or AlgorithmicDesktopCreationClaimDisposition.ExistingInFlight;

        public bool IsNewClaim =>
            Disposition == AlgorithmicDesktopCreationClaimDisposition.Acquired;
    }

    internal readonly record struct AlgorithmicDesktopCreationCompletionResult(
        AlgorithmicDesktopCreationCompletionDisposition Disposition,
        AlgorithmicDesktopCreationClaim Claim,
        string? DiagnosticReason)
    {
        public bool Completed => Disposition is
            AlgorithmicDesktopCreationCompletionDisposition.SuccessRecorded
            or AlgorithmicDesktopCreationCompletionDisposition.SuccessAlreadyRecorded
            or AlgorithmicDesktopCreationCompletionDisposition.FailureReleased;
    }

    internal sealed record AlgorithmicDesktopCreationSnapshot(
        int SuccessfulCreationCount,
        int InFlightCount,
        int TrackedCreatedDesktopCount,
        IReadOnlyList<AlgorithmicDesktopCreationClaim> ActiveClaims,
        IReadOnlyList<IVirtualDesktop> TrackedCreatedDesktops);

    /// <summary>
    /// Dispatcher-confined bookkeeping for virtual desktops created by the
    /// algorithmic-layout feature. This type deliberately performs no virtual
    /// desktop operation: callers acquire a claim before invoking an external
    /// creation API and explicitly complete or release it afterwards.
    /// </summary>
    internal sealed class AlgorithmicDesktopCreationTracker
    {
        private readonly Dispatcher m_dispatcher;
        private readonly Dictionary<Guid, AlgorithmicDesktopCreationClaim> m_activeClaims = [];
        private readonly Dictionary<IntPtr, Guid> m_activeClaimsByWindow = [];
        private readonly Dictionary<Guid, Guid> m_activeClaimsByCorrelation = [];
        private readonly Dictionary<DesktopCreationRequestKey, AlgorithmicDesktopCreationClaim>
            m_successfulClaims = [];
        private readonly Dictionary<IntPtr, AlgorithmicDesktopCreationClaim>
            m_successfulClaimsByWindow = [];
        private readonly HashSet<IVirtualDesktop> m_sessionCreatedDesktops
            = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<IVirtualDesktop> m_liveCreatedDesktops
            = new(ReferenceEqualityComparer.Instance);

        public AlgorithmicDesktopCreationTracker(Dispatcher dispatcher)
        {
            m_dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        public int SuccessfulCreationCount
        {
            get
            {
                VerifyAccess();
                return m_sessionCreatedDesktops.Count;
            }
        }

        public int InFlightCount
        {
            get
            {
                VerifyAccess();
                return m_activeClaims.Count;
            }
        }

        public AlgorithmicDesktopCreationAvailability CanCreate(
            int maxAutoCreatedDesktops)
        {
            VerifyAccess();
            ValidateMaximum(maxAutoCreatedDesktops);
            return CreateAvailability(maxAutoCreatedDesktops);
        }

        public AlgorithmicDesktopCreationClaimResult TryClaim(
            IntPtr windowHandle,
            Guid correlationId,
            int maxAutoCreatedDesktops)
        {
            VerifyAccess();
            ValidateWindowHandle(windowHandle);
            ValidateCorrelationId(correlationId);
            ValidateMaximum(maxAutoCreatedDesktops);

            var requestKey = new DesktopCreationRequestKey(windowHandle, correlationId);
            if (m_successfulClaims.TryGetValue(requestKey, out var completedClaim))
            {
                return new AlgorithmicDesktopCreationClaimResult(
                    AlgorithmicDesktopCreationClaimDisposition.AlreadyCompleted,
                    completedClaim,
                    CreateAvailability(maxAutoCreatedDesktops),
                    "A desktop creation for this window and correlation already completed.");
            }
            if (m_successfulClaimsByWindow.TryGetValue(
                windowHandle,
                out completedClaim))
            {
                return new AlgorithmicDesktopCreationClaimResult(
                    AlgorithmicDesktopCreationClaimDisposition.AlreadyCompleted,
                    completedClaim,
                    CreateAvailability(maxAutoCreatedDesktops),
                    "This window already caused an automatic desktop creation.");
            }

            if (m_activeClaimsByWindow.TryGetValue(windowHandle, out var windowClaimId))
            {
                var windowClaim = m_activeClaims[windowClaimId];
                if (windowClaim.CorrelationId == correlationId)
                {
                    return new AlgorithmicDesktopCreationClaimResult(
                        AlgorithmicDesktopCreationClaimDisposition.ExistingInFlight,
                        windowClaim,
                        CreateAvailability(maxAutoCreatedDesktops),
                        "The matching desktop-creation claim is already in flight.");
                }

                return new AlgorithmicDesktopCreationClaimResult(
                    AlgorithmicDesktopCreationClaimDisposition.WindowAlreadyClaimed,
                    windowClaim,
                    CreateAvailability(maxAutoCreatedDesktops),
                    "The window already has a different desktop-creation claim in flight.");
            }

            if (m_activeClaimsByCorrelation.TryGetValue(correlationId, out var correlationClaimId))
            {
                var correlationClaim = m_activeClaims[correlationClaimId];
                return new AlgorithmicDesktopCreationClaimResult(
                    AlgorithmicDesktopCreationClaimDisposition.CorrelationAlreadyClaimed,
                    correlationClaim,
                    CreateAvailability(maxAutoCreatedDesktops),
                    "The correlation already belongs to a different window creation claim.");
            }

            var availability = CreateAvailability(maxAutoCreatedDesktops);
            if (!availability.CanCreate)
            {
                return new AlgorithmicDesktopCreationClaimResult(
                    AlgorithmicDesktopCreationClaimDisposition.LimitReached,
                    null,
                    availability,
                    availability.DiagnosticReason);
            }

            var claim = new AlgorithmicDesktopCreationClaim(
                Guid.NewGuid(),
                correlationId,
                windowHandle);
            m_activeClaims.Add(claim.ClaimId, claim);
            m_activeClaimsByWindow.Add(windowHandle, claim.ClaimId);
            m_activeClaimsByCorrelation.Add(correlationId, claim.ClaimId);
            return new AlgorithmicDesktopCreationClaimResult(
                AlgorithmicDesktopCreationClaimDisposition.Acquired,
                claim,
                CreateAvailability(maxAutoCreatedDesktops),
                null);
        }

        public bool TryGetActiveClaim(
            IntPtr windowHandle,
            out AlgorithmicDesktopCreationClaim claim)
        {
            VerifyAccess();
            if (windowHandle == IntPtr.Zero
                || !m_activeClaimsByWindow.TryGetValue(windowHandle, out var claimId))
            {
                claim = null!;
                return false;
            }

            claim = m_activeClaims[claimId];
            return true;
        }

        public bool TryGetActiveClaimByCorrelation(
            Guid correlationId,
            out AlgorithmicDesktopCreationClaim claim)
        {
            VerifyAccess();
            if (correlationId == Guid.Empty
                || !m_activeClaimsByCorrelation.TryGetValue(correlationId, out var claimId))
            {
                claim = null!;
                return false;
            }

            claim = m_activeClaims[claimId];
            return true;
        }

        public AlgorithmicDesktopCreationCompletionResult CompleteSuccess(
            AlgorithmicDesktopCreationClaim claim,
            IVirtualDesktop desktop)
        {
            ArgumentNullException.ThrowIfNull(claim);
            ArgumentNullException.ThrowIfNull(desktop);
            VerifyAccess();

            var requestKey = new DesktopCreationRequestKey(
                claim.WindowHandle,
                claim.CorrelationId);
            if (m_successfulClaims.TryGetValue(requestKey, out var completedClaim)
                && ClaimsMatch(completedClaim, claim))
            {
                return new AlgorithmicDesktopCreationCompletionResult(
                    AlgorithmicDesktopCreationCompletionDisposition.SuccessAlreadyRecorded,
                    completedClaim,
                    "The desktop-creation success was already recorded.");
            }

            var validation = ValidateActiveClaim(claim);
            if (validation != null)
            {
                return validation.Value;
            }

            if (m_sessionCreatedDesktops.Contains(desktop))
            {
                return new AlgorithmicDesktopCreationCompletionResult(
                    AlgorithmicDesktopCreationCompletionDisposition.DesktopAlreadyRecorded,
                    claim,
                    "The desktop is already associated with another successful creation claim.");
            }

            ReleaseActiveClaim(claim);
            m_successfulClaims.Add(requestKey, claim);
            m_successfulClaimsByWindow.Add(claim.WindowHandle, claim);
            m_sessionCreatedDesktops.Add(desktop);
            m_liveCreatedDesktops.Add(desktop);
            return new AlgorithmicDesktopCreationCompletionResult(
                AlgorithmicDesktopCreationCompletionDisposition.SuccessRecorded,
                claim,
                null);
        }

        public AlgorithmicDesktopCreationCompletionResult CompleteFailure(
            AlgorithmicDesktopCreationClaim claim)
        {
            ArgumentNullException.ThrowIfNull(claim);
            VerifyAccess();

            var validation = ValidateActiveClaim(claim);
            if (validation != null)
            {
                return validation.Value;
            }

            ReleaseActiveClaim(claim);
            return new AlgorithmicDesktopCreationCompletionResult(
                AlgorithmicDesktopCreationCompletionDisposition.FailureReleased,
                claim,
                null);
        }

        public bool IsTrackedCreatedDesktop(IVirtualDesktop desktop)
        {
            ArgumentNullException.ThrowIfNull(desktop);
            VerifyAccess();
            return m_liveCreatedDesktops.Contains(desktop);
        }

        /// <summary>
        /// Removes the live desktop reference after DesktopRemoved. The successful
        /// creation counter is intentionally not decremented because the limit is
        /// the number of feature-created desktops during this process session.
        /// </summary>
        public bool DesktopRemoved(IVirtualDesktop desktop)
        {
            ArgumentNullException.ThrowIfNull(desktop);
            VerifyAccess();
            return m_liveCreatedDesktops.Remove(desktop);
        }

        public bool ForgetWindow(IntPtr windowHandle)
        {
            VerifyAccess();
            if (windowHandle == IntPtr.Zero)
            {
                return false;
            }
            return m_successfulClaimsByWindow.Remove(windowHandle);
        }

        public void Clear()
        {
            VerifyAccess();
            m_activeClaims.Clear();
            m_activeClaimsByWindow.Clear();
            m_activeClaimsByCorrelation.Clear();
            m_successfulClaims.Clear();
            m_successfulClaimsByWindow.Clear();
            m_sessionCreatedDesktops.Clear();
            m_liveCreatedDesktops.Clear();
        }

        public AlgorithmicDesktopCreationSnapshot GetSnapshot()
        {
            VerifyAccess();
            return new AlgorithmicDesktopCreationSnapshot(
                m_sessionCreatedDesktops.Count,
                m_activeClaims.Count,
                m_liveCreatedDesktops.Count,
                m_activeClaims.Values
                    .OrderBy(item => item.ClaimId)
                    .ToArray(),
                m_liveCreatedDesktops.ToArray());
        }

        private AlgorithmicDesktopCreationAvailability CreateAvailability(
            int maxAutoCreatedDesktops)
        {
            int successfulCreationCount = m_sessionCreatedDesktops.Count;
            int used = checked(successfulCreationCount + m_activeClaims.Count);
            int remaining = Math.Max(0, maxAutoCreatedDesktops - used);
            bool canCreate = remaining > 0;
            string? reason = canCreate
                ? null
                : maxAutoCreatedDesktops == 0
                    ? "Automatic desktop creation is disabled by the zero limit."
                    : $"The session desktop-creation limit ({maxAutoCreatedDesktops}) is exhausted by {successfulCreationCount} successful and {m_activeClaims.Count} in-flight creation(s).";
            return new AlgorithmicDesktopCreationAvailability(
                canCreate,
                maxAutoCreatedDesktops,
                successfulCreationCount,
                m_activeClaims.Count,
                remaining,
                reason);
        }

        private AlgorithmicDesktopCreationCompletionResult? ValidateActiveClaim(
            AlgorithmicDesktopCreationClaim claim)
        {
            if (!m_activeClaims.TryGetValue(claim.ClaimId, out var activeClaim))
            {
                return new AlgorithmicDesktopCreationCompletionResult(
                    AlgorithmicDesktopCreationCompletionDisposition.ClaimNotFound,
                    claim,
                    "The desktop-creation claim is not active.");
            }

            if (!ClaimsMatch(activeClaim, claim))
            {
                return new AlgorithmicDesktopCreationCompletionResult(
                    AlgorithmicDesktopCreationCompletionDisposition.ClaimMismatch,
                    claim,
                    "The claim ID does not belong to the supplied window and correlation.");
            }

            return null;
        }

        private void ReleaseActiveClaim(AlgorithmicDesktopCreationClaim claim)
        {
            m_activeClaims.Remove(claim.ClaimId);
            m_activeClaimsByWindow.Remove(claim.WindowHandle);
            m_activeClaimsByCorrelation.Remove(claim.CorrelationId);
        }

        private void VerifyAccess()
        {
            m_dispatcher.VerifyAccess();
        }

        private static bool ClaimsMatch(
            AlgorithmicDesktopCreationClaim left,
            AlgorithmicDesktopCreationClaim right)
        {
            return left.ClaimId == right.ClaimId
                && left.WindowHandle == right.WindowHandle
                && left.CorrelationId == right.CorrelationId;
        }

        private static void ValidateMaximum(int maxAutoCreatedDesktops)
        {
            if (maxAutoCreatedDesktops is
                < MasterSatelliteLayoutSettings.MinimumMaxAutoCreatedDesktops
                or > MasterSatelliteLayoutSettings.MaximumMaxAutoCreatedDesktops)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxAutoCreatedDesktops),
                    maxAutoCreatedDesktops,
                    $"The maximum must be between {MasterSatelliteLayoutSettings.MinimumMaxAutoCreatedDesktops} and {MasterSatelliteLayoutSettings.MaximumMaxAutoCreatedDesktops}.");
            }
        }

        private static void ValidateWindowHandle(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
            {
                throw new ArgumentException(
                    "The creation claim requires a non-zero window handle.",
                    nameof(windowHandle));
            }
        }

        private static void ValidateCorrelationId(Guid correlationId)
        {
            if (correlationId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The creation claim requires a non-empty correlation ID.",
                    nameof(correlationId));
            }
        }

        private readonly record struct DesktopCreationRequestKey(
            IntPtr WindowHandle,
            Guid CorrelationId);
    }
}
