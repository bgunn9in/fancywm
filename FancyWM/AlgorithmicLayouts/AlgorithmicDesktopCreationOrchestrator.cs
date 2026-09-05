using System;

using FancyWM.Models;

using WinMan;

namespace FancyWM.AlgorithmicLayouts
{
    internal static class AlgorithmicDesktopCreationPolicy
    {
        public static bool AllowsExistingDesktopOverflow(
            MasterSatelliteOverflowPolicy policy)
        {
            return policy is MasterSatelliteOverflowPolicy.MoveToExistingDesktop
                or MasterSatelliteOverflowPolicy.MoveToExistingOrCreateDesktop;
        }

        public static bool ShouldCreateAfterSearch(
            MasterSatelliteOverflowPolicy policy,
            AlgorithmicTransferStartDisposition searchDisposition)
        {
            return policy
                    == MasterSatelliteOverflowPolicy.MoveToExistingOrCreateDesktop
                && searchDisposition
                    == AlgorithmicTransferStartDisposition.NoDestination;
        }

        public static bool ShouldCreateAfterSearch(
            MasterSatelliteOverflowPolicy policy,
            AlgorithmicDestinationSearchDisposition searchDisposition)
        {
            return policy
                    == MasterSatelliteOverflowPolicy.MoveToExistingOrCreateDesktop
                && searchDisposition
                    == AlgorithmicDestinationSearchDisposition.NoDestination;
        }
    }

    internal enum AlgorithmicDesktopCreationDisposition
    {
        CreatedAndPrepared,
        Unsupported,
        CapabilityCheckFailed,
        LimitReached,
        DuplicateInFlight,
        AlreadyCreated,
        ClaimConflict,
        CreationFailed,
        CreationReturnedNoDesktop,
        CreatedDesktopInvalid,
        CompletionRejected,
        PreparationFailed,
    }

    /// <summary>
    /// Describes one policy-neutral attempt to create and initialize an overflow
    /// desktop. A non-null <see cref="CreatedDesktop"/> means that the manager
    /// returned a desktop object, not necessarily that it was usable or prepared.
    /// </summary>
    internal sealed record AlgorithmicDesktopCreationResult(
        AlgorithmicDesktopCreationDisposition Disposition,
        AlgorithmicDesktopCreationClaimResult? ClaimResult,
        AlgorithmicDesktopCreationCompletionResult? CompletionResult,
        IVirtualDesktop? CreatedDesktop,
        string DiagnosticReason,
        Exception? Exception = null)
    {
        public bool Succeeded =>
            Disposition == AlgorithmicDesktopCreationDisposition.CreatedAndPrepared;

        public bool IsDuplicate => Disposition is
            AlgorithmicDesktopCreationDisposition.DuplicateInFlight
            or AlgorithmicDesktopCreationDisposition.AlreadyCreated;

        public bool DesktopWasCreated => CompletionResult?.Disposition is
            AlgorithmicDesktopCreationCompletionDisposition.SuccessRecorded
            or AlgorithmicDesktopCreationCompletionDisposition.SuccessAlreadyRecorded;
    }

    /// <summary>
    /// Performs the external virtual-desktop creation boundary around the
    /// workspace-wide coordinator claim. The caller remains responsible for
    /// deciding whether its overflow policy permits creation and for transferring
    /// a window after a successful result.
    /// </summary>
    internal sealed class AlgorithmicDesktopCreationOrchestrator
    {
        private readonly AlgorithmicLayoutCoordinator m_coordinator;
        private readonly IVirtualDesktopManager m_virtualDesktopManager;

        public AlgorithmicDesktopCreationOrchestrator(
            AlgorithmicLayoutCoordinator coordinator,
            IVirtualDesktopManager virtualDesktopManager)
        {
            m_coordinator = coordinator
                ?? throw new ArgumentNullException(nameof(coordinator));
            m_virtualDesktopManager = virtualDesktopManager
                ?? throw new ArgumentNullException(nameof(virtualDesktopManager));
        }

        public AlgorithmicDesktopCreationResult CreateAndPrepareDesktop(
            IntPtr windowHandle,
            Guid correlationId,
            int maxAutoCreatedDesktops,
            Action<IVirtualDesktop> prepareDesktop)
        {
            ValidateRequest(
                windowHandle,
                correlationId,
                maxAutoCreatedDesktops,
                prepareDesktop);

            bool canManage;
            try
            {
                canManage = m_virtualDesktopManager.CanManageVirtualDesktops;
            }
            catch (Exception exception)
            {
                return Result(
                    AlgorithmicDesktopCreationDisposition.CapabilityCheckFailed,
                    null,
                    null,
                    null,
                    "The virtual desktop manager capability check failed.",
                    exception);
            }

            if (!canManage)
            {
                return Result(
                    AlgorithmicDesktopCreationDisposition.Unsupported,
                    null,
                    null,
                    null,
                    "The virtual desktop manager does not support desktop creation.");
            }

            var claimResult = m_coordinator.TryClaimDesktopCreation(
                windowHandle,
                correlationId,
                maxAutoCreatedDesktops);
            if (!claimResult.IsNewClaim || claimResult.Claim == null)
            {
                return FromRejectedClaim(claimResult);
            }

            var claim = claimResult.Claim;
            IVirtualDesktop? createdDesktop;
            try
            {
                // CreateDesktop is an OS boundary. TryClaimDesktopCreation has
                // returned, so no coordinator mutation lock spans this call.
                createdDesktop = m_virtualDesktopManager.CreateDesktop();
            }
            catch (Exception exception)
            {
                var failure = m_coordinator.FailDesktopCreation(claim);
                return Result(
                    AlgorithmicDesktopCreationDisposition.CreationFailed,
                    claimResult,
                    failure,
                    null,
                    "The virtual desktop manager failed to create a desktop.",
                    exception);
            }

            if (createdDesktop == null)
            {
                var failure = m_coordinator.FailDesktopCreation(claim);
                return Result(
                    AlgorithmicDesktopCreationDisposition.CreationReturnedNoDesktop,
                    claimResult,
                    failure,
                    null,
                    "The virtual desktop manager returned no created desktop.");
            }

            string? invalidReason;
            Exception? validationException = null;
            try
            {
                invalidReason = ValidateCreatedDesktop(createdDesktop);
            }
            catch (Exception exception)
            {
                invalidReason = "The created desktop could not be validated.";
                validationException = exception;
            }

            if (invalidReason != null)
            {
                var failure = m_coordinator.FailDesktopCreation(claim);
                return Result(
                    AlgorithmicDesktopCreationDisposition.CreatedDesktopInvalid,
                    claimResult,
                    failure,
                    createdDesktop,
                    invalidReason,
                    validationException);
            }

            AlgorithmicDesktopCreationCompletionResult completion;
            try
            {
                completion = m_coordinator.CompleteDesktopCreation(
                    claim,
                    createdDesktop);
            }
            catch (Exception exception)
            {
                var failure = m_coordinator.FailDesktopCreation(claim);
                return Result(
                    AlgorithmicDesktopCreationDisposition.CompletionRejected,
                    claimResult,
                    failure,
                    createdDesktop,
                    "The coordinator could not record the created desktop.",
                    exception);
            }

            if (completion.Disposition is not
                (AlgorithmicDesktopCreationCompletionDisposition.SuccessRecorded
                    or AlgorithmicDesktopCreationCompletionDisposition.SuccessAlreadyRecorded))
            {
                // A rejected success leaves the acquired claim active. Release it
                // exactly once so it cannot permanently consume global capacity.
                var failure = m_coordinator.FailDesktopCreation(claim);
                return Result(
                    AlgorithmicDesktopCreationDisposition.CompletionRejected,
                    claimResult,
                    failure,
                    createdDesktop,
                    completion.DiagnosticReason
                        ?? "The coordinator rejected the created desktop.");
            }

            try
            {
                // Preparation is another caller-owned boundary and deliberately
                // runs only after the success was recorded. A preparation failure
                // must not refund the quota: the OS desktop was still created.
                prepareDesktop(createdDesktop);
            }
            catch (Exception exception)
            {
                return Result(
                    AlgorithmicDesktopCreationDisposition.PreparationFailed,
                    claimResult,
                    completion,
                    createdDesktop,
                    "The created desktop could not be prepared for tiling.",
                    exception);
            }

            return Result(
                AlgorithmicDesktopCreationDisposition.CreatedAndPrepared,
                claimResult,
                completion,
                createdDesktop,
                "The desktop was created, recorded, and prepared.");
        }

        private AlgorithmicDesktopCreationResult FromRejectedClaim(
            AlgorithmicDesktopCreationClaimResult claimResult)
        {
            var disposition = claimResult.Disposition switch
            {
                AlgorithmicDesktopCreationClaimDisposition.LimitReached =>
                    AlgorithmicDesktopCreationDisposition.LimitReached,
                AlgorithmicDesktopCreationClaimDisposition.ExistingInFlight =>
                    AlgorithmicDesktopCreationDisposition.DuplicateInFlight,
                AlgorithmicDesktopCreationClaimDisposition.AlreadyCompleted =>
                    AlgorithmicDesktopCreationDisposition.AlreadyCreated,
                _ => AlgorithmicDesktopCreationDisposition.ClaimConflict,
            };
            return Result(
                disposition,
                claimResult,
                null,
                null,
                claimResult.DiagnosticReason
                    ?? "The coordinator rejected the desktop-creation claim.");
        }

        private string? ValidateCreatedDesktop(IVirtualDesktop desktop)
        {
            if (!desktop.IsAlive)
            {
                return "The virtual desktop manager returned a desktop that is not alive.";
            }

            var owner = desktop.Workspace;
            if (owner != null && !ReferenceEquals(owner, m_coordinator.Workspace))
            {
                return "The virtual desktop manager returned a desktop from another workspace.";
            }

            return null;
        }

        private static AlgorithmicDesktopCreationResult Result(
            AlgorithmicDesktopCreationDisposition disposition,
            AlgorithmicDesktopCreationClaimResult? claimResult,
            AlgorithmicDesktopCreationCompletionResult? completionResult,
            IVirtualDesktop? createdDesktop,
            string diagnosticReason,
            Exception? exception = null)
        {
            return new AlgorithmicDesktopCreationResult(
                disposition,
                claimResult,
                completionResult,
                createdDesktop,
                diagnosticReason,
                exception);
        }

        private static void ValidateRequest(
            IntPtr windowHandle,
            Guid correlationId,
            int maxAutoCreatedDesktops,
            Action<IVirtualDesktop> prepareDesktop)
        {
            if (windowHandle == IntPtr.Zero)
            {
                throw new ArgumentException(
                    "The desktop-creation request requires a non-zero window handle.",
                    nameof(windowHandle));
            }
            if (correlationId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The desktop-creation request requires a non-empty correlation ID.",
                    nameof(correlationId));
            }
            if (maxAutoCreatedDesktops is
                < MasterSatelliteLayoutSettings.MinimumMaxAutoCreatedDesktops
                or > MasterSatelliteLayoutSettings.MaximumMaxAutoCreatedDesktops)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxAutoCreatedDesktops),
                    maxAutoCreatedDesktops,
                    $"The maximum must be between {MasterSatelliteLayoutSettings.MinimumMaxAutoCreatedDesktops} and {MasterSatelliteLayoutSettings.MaximumMaxAutoCreatedDesktops}.");
            }
            ArgumentNullException.ThrowIfNull(prepareDesktop);
        }
    }
}
