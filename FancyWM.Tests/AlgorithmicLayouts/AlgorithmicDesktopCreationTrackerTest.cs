#nullable enable

using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

using FancyWM.AlgorithmicLayouts;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    [TestClass]
    public class AlgorithmicDesktopCreationTrackerTest
    {
        [TestMethod]
        public void ZeroLimitRejectsClaimWithDiagnostic()
        {
            var tracker = CreateTracker();

            var availability = tracker.CanCreate(0);
            var result = tracker.TryClaim(new IntPtr(101), Guid.NewGuid(), 0);

            Assert.IsFalse(availability.CanCreate);
            Assert.AreEqual(0, availability.RemainingCreationCount);
            StringAssert.Contains(availability.DiagnosticReason, "zero");
            Assert.AreEqual(
                AlgorithmicDesktopCreationClaimDisposition.LimitReached,
                result.Disposition);
            Assert.IsFalse(result.Accepted);
            Assert.IsNull(result.Claim);
            Assert.AreEqual(0, tracker.GetSnapshot().InFlightCount);
        }

        [TestMethod]
        public void OneLimitIncludesInflightThenSuccessfulCreation()
        {
            var tracker = CreateTracker();
            var first = tracker.TryClaim(new IntPtr(201), Guid.NewGuid(), 1);

            Assert.IsTrue(first.Accepted);
            Assert.IsTrue(first.IsNewClaim);
            Assert.IsFalse(tracker.CanCreate(1).CanCreate);
            Assert.AreEqual(
                AlgorithmicDesktopCreationClaimDisposition.LimitReached,
                tracker.TryClaim(new IntPtr(202), Guid.NewGuid(), 1).Disposition);

            var completion = tracker.CompleteSuccess(first.Claim!, CreateDesktop("Created"));

            Assert.AreEqual(
                AlgorithmicDesktopCreationCompletionDisposition.SuccessRecorded,
                completion.Disposition);
            Assert.AreEqual(1, tracker.SuccessfulCreationCount);
            Assert.AreEqual(0, tracker.InFlightCount);
            Assert.IsFalse(tracker.CanCreate(1).CanCreate);
            Assert.AreEqual(
                AlgorithmicDesktopCreationClaimDisposition.LimitReached,
                tracker.TryClaim(new IntPtr(203), Guid.NewGuid(), 1).Disposition);
        }

        [TestMethod]
        public void NineLimitAllowsNineConcurrentClaimsAndRejectsTenth()
        {
            var tracker = CreateTracker();
            var claims = Enumerable.Range(1, 9)
                .Select(index => tracker.TryClaim(
                    new IntPtr(300 + index),
                    Guid.NewGuid(),
                    9))
                .ToArray();

            Assert.IsTrue(claims.All(item => item.IsNewClaim));
            Assert.AreEqual(9, tracker.InFlightCount);
            Assert.IsFalse(tracker.CanCreate(9).CanCreate);
            Assert.AreEqual(
                AlgorithmicDesktopCreationClaimDisposition.LimitReached,
                tracker.TryClaim(new IntPtr(310), Guid.NewGuid(), 9).Disposition);
            Assert.AreEqual(9, tracker.GetSnapshot().ActiveClaims.Count);
        }

        [TestMethod]
        public void DuplicateWindowAndCorrelationReturnsSameInflightClaim()
        {
            var tracker = CreateTracker();
            var handle = new IntPtr(401);
            var correlationId = Guid.NewGuid();
            var first = tracker.TryClaim(handle, correlationId, 1);

            var duplicate = tracker.TryClaim(handle, correlationId, 0);

            Assert.AreEqual(
                AlgorithmicDesktopCreationClaimDisposition.ExistingInFlight,
                duplicate.Disposition);
            Assert.IsTrue(duplicate.Accepted);
            Assert.IsFalse(duplicate.IsNewClaim);
            Assert.AreEqual(first.Claim, duplicate.Claim);
            Assert.AreEqual(1, tracker.InFlightCount);
            Assert.IsTrue(tracker.TryGetActiveClaim(handle, out var byWindow));
            Assert.IsTrue(tracker.TryGetActiveClaimByCorrelation(correlationId, out var byCorrelation));
            Assert.AreEqual(first.Claim, byWindow);
            Assert.AreEqual(first.Claim, byCorrelation);
        }

        [TestMethod]
        public void WindowAndCorrelationEachAllowOnlyOneInflightClaim()
        {
            var tracker = CreateTracker();
            var handle = new IntPtr(501);
            var correlationId = Guid.NewGuid();
            var first = tracker.TryClaim(handle, correlationId, 9);

            var sameWindow = tracker.TryClaim(handle, Guid.NewGuid(), 9);
            var sameCorrelation = tracker.TryClaim(new IntPtr(502), correlationId, 9);

            Assert.AreEqual(
                AlgorithmicDesktopCreationClaimDisposition.WindowAlreadyClaimed,
                sameWindow.Disposition);
            Assert.AreEqual(first.Claim, sameWindow.Claim);
            Assert.AreEqual(
                AlgorithmicDesktopCreationClaimDisposition.CorrelationAlreadyClaimed,
                sameCorrelation.Disposition);
            Assert.AreEqual(first.Claim, sameCorrelation.Claim);
            Assert.AreEqual(1, tracker.InFlightCount);
        }

        [TestMethod]
        public void FailureReleasesClaimAndCapacityForRetry()
        {
            var tracker = CreateTracker();
            var handle = new IntPtr(601);
            var correlationId = Guid.NewGuid();
            var first = tracker.TryClaim(handle, correlationId, 1);

            var failure = tracker.CompleteFailure(first.Claim!);
            var retry = tracker.TryClaim(handle, correlationId, 1);

            Assert.AreEqual(
                AlgorithmicDesktopCreationCompletionDisposition.FailureReleased,
                failure.Disposition);
            Assert.AreEqual(0, tracker.SuccessfulCreationCount);
            Assert.AreEqual(1, tracker.InFlightCount);
            Assert.IsTrue(retry.IsNewClaim);
            Assert.AreNotEqual(first.Claim!.ClaimId, retry.Claim!.ClaimId);
        }

        [TestMethod]
        public void SuccessIsIdempotentAndDuplicateRequestDoesNotCreateAgain()
        {
            var tracker = CreateTracker();
            var handle = new IntPtr(701);
            var correlationId = Guid.NewGuid();
            var claim = tracker.TryClaim(handle, correlationId, 9).Claim!;
            var desktop = CreateDesktop("Created");

            var first = tracker.CompleteSuccess(claim, desktop);
            var duplicateCompletion = tracker.CompleteSuccess(claim, desktop);
            var duplicateClaim = tracker.TryClaim(handle, correlationId, 9);

            Assert.AreEqual(
                AlgorithmicDesktopCreationCompletionDisposition.SuccessRecorded,
                first.Disposition);
            Assert.AreEqual(
                AlgorithmicDesktopCreationCompletionDisposition.SuccessAlreadyRecorded,
                duplicateCompletion.Disposition);
            Assert.AreEqual(
                AlgorithmicDesktopCreationClaimDisposition.AlreadyCompleted,
                duplicateClaim.Disposition);
            Assert.AreEqual(claim, duplicateClaim.Claim);
            Assert.AreEqual(1, tracker.SuccessfulCreationCount);
            Assert.AreEqual(0, tracker.InFlightCount);
            Assert.IsTrue(tracker.IsTrackedCreatedDesktop(desktop));
            Assert.IsFalse(tracker.TryGetActiveClaim(handle, out _));
        }

        [TestMethod]
        public void CompletedWindowCannotCreateAgainWithAnotherCorrelationUntilForgotten()
        {
            var tracker = CreateTracker();
            var handle = new IntPtr(751);
            var first = tracker.TryClaim(handle, Guid.NewGuid(), 2).Claim!;
            tracker.CompleteSuccess(first, CreateDesktop("Created"));

            var duplicate = tracker.TryClaim(handle, Guid.NewGuid(), 2);

            Assert.AreEqual(
                AlgorithmicDesktopCreationClaimDisposition.AlreadyCompleted,
                duplicate.Disposition);
            Assert.AreEqual(first, duplicate.Claim);
            Assert.IsTrue(tracker.ForgetWindow(handle));
            Assert.IsTrue(tracker.TryClaim(handle, Guid.NewGuid(), 2).IsNewClaim);
        }

        [TestMethod]
        public void DesktopRemovalDropsLiveIdentityButDoesNotRefundSessionLimit()
        {
            var tracker = CreateTracker();
            var claim = tracker.TryClaim(new IntPtr(801), Guid.NewGuid(), 1).Claim!;
            var desktop = CreateDesktop("Removed");
            tracker.CompleteSuccess(claim, desktop);

            Assert.IsTrue(tracker.DesktopRemoved(desktop));

            var snapshot = tracker.GetSnapshot();
            Assert.AreEqual(1, snapshot.SuccessfulCreationCount);
            Assert.AreEqual(0, snapshot.TrackedCreatedDesktopCount);
            Assert.IsFalse(tracker.IsTrackedCreatedDesktop(desktop));
            Assert.IsFalse(tracker.DesktopRemoved(desktop));
            Assert.IsFalse(tracker.CanCreate(1).CanCreate);
        }

        [TestMethod]
        public void RemovedDesktopIdentityCannotBeCountedAsASecondSessionCreation()
        {
            var tracker = CreateTracker();
            var desktop = CreateDesktop("Reused identity");
            var first = tracker.TryClaim(new IntPtr(851), Guid.NewGuid(), 2).Claim!;
            tracker.CompleteSuccess(first, desktop);
            tracker.DesktopRemoved(desktop);
            var second = tracker.TryClaim(new IntPtr(852), Guid.NewGuid(), 2).Claim!;

            var result = tracker.CompleteSuccess(second, desktop);

            Assert.AreEqual(
                AlgorithmicDesktopCreationCompletionDisposition.DesktopAlreadyRecorded,
                result.Disposition);
            Assert.AreEqual(1, tracker.SuccessfulCreationCount);
            Assert.AreEqual(1, tracker.InFlightCount);
        }

        [TestMethod]
        public void CreatedDesktopsUseReferenceIdentityEvenWhenObjectsCompareEqual()
        {
            var tracker = CreateTracker();
            var firstDesktop = CreateDesktop("Equal");
            var secondDesktopMock = new Mock<IVirtualDesktop>(MockBehavior.Loose);
            secondDesktopMock.SetupGet(item => item.Name).Returns("Equal");
            secondDesktopMock.Setup(item => item.Equals(firstDesktop)).Returns(true);
            var secondDesktop = secondDesktopMock.Object;
            var firstClaim = tracker.TryClaim(new IntPtr(901), Guid.NewGuid(), 2).Claim!;
            var secondClaim = tracker.TryClaim(new IntPtr(902), Guid.NewGuid(), 2).Claim!;

            tracker.CompleteSuccess(firstClaim, firstDesktop);
            var secondResult = tracker.CompleteSuccess(secondClaim, secondDesktop);

            Assert.AreEqual(
                AlgorithmicDesktopCreationCompletionDisposition.SuccessRecorded,
                secondResult.Disposition);
            Assert.AreEqual(2, tracker.SuccessfulCreationCount);
            Assert.AreEqual(2, tracker.GetSnapshot().TrackedCreatedDesktopCount);
        }

        [TestMethod]
        public void CompletionValidatesClaimIdWindowAndCorrelationOwnership()
        {
            var tracker = CreateTracker();
            var claim = tracker.TryClaim(new IntPtr(1001), Guid.NewGuid(), 1).Claim!;
            var forgedWindow = claim with { WindowHandle = new IntPtr(1002) };
            var forgedCorrelation = claim with { CorrelationId = Guid.NewGuid() };

            var windowResult = tracker.CompleteFailure(forgedWindow);
            var correlationResult = tracker.CompleteSuccess(
                forgedCorrelation,
                CreateDesktop("Not created"));

            Assert.AreEqual(
                AlgorithmicDesktopCreationCompletionDisposition.ClaimMismatch,
                windowResult.Disposition);
            Assert.AreEqual(
                AlgorithmicDesktopCreationCompletionDisposition.ClaimMismatch,
                correlationResult.Disposition);
            Assert.AreEqual(1, tracker.InFlightCount);
            Assert.IsTrue(tracker.TryGetActiveClaim(claim.WindowHandle, out var active));
            Assert.AreEqual(claim, active);
        }

        [TestMethod]
        public void DuplicateDesktopCannotCompleteTwoDifferentClaims()
        {
            var tracker = CreateTracker();
            var desktop = CreateDesktop("Single object");
            var first = tracker.TryClaim(new IntPtr(1101), Guid.NewGuid(), 2).Claim!;
            var second = tracker.TryClaim(new IntPtr(1102), Guid.NewGuid(), 2).Claim!;
            tracker.CompleteSuccess(first, desktop);

            var result = tracker.CompleteSuccess(second, desktop);

            Assert.AreEqual(
                AlgorithmicDesktopCreationCompletionDisposition.DesktopAlreadyRecorded,
                result.Disposition);
            Assert.AreEqual(1, tracker.SuccessfulCreationCount);
            Assert.AreEqual(1, tracker.InFlightCount);
            Assert.IsTrue(tracker.TryGetActiveClaim(second.WindowHandle, out _));
        }

        [TestMethod]
        public void InputsAndDispatcherOwnershipAreValidated()
        {
            var tracker = CreateTracker();

            Assert.ThrowsException<ArgumentOutOfRangeException>(() => tracker.CanCreate(-1));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => tracker.CanCreate(10));
            Assert.ThrowsException<ArgumentException>(() =>
                tracker.TryClaim(IntPtr.Zero, Guid.NewGuid(), 1));
            Assert.ThrowsException<ArgumentException>(() =>
                tracker.TryClaim(new IntPtr(1201), Guid.Empty, 1));
            Assert.ThrowsException<ArgumentNullException>(() =>
                tracker.CompleteSuccess(null!, CreateDesktop("Unused")));
            Assert.ThrowsException<ArgumentNullException>(() =>
                tracker.CompleteSuccess(
                    new AlgorithmicDesktopCreationClaim(
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        new IntPtr(1202)),
                    null!));

            var dispatcherException = Task.Run(() =>
            {
                try
                {
                    tracker.GetSnapshot();
                    return null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            }).GetAwaiter().GetResult();
            Assert.IsInstanceOfType(dispatcherException, typeof(InvalidOperationException));
        }

        private static AlgorithmicDesktopCreationTracker CreateTracker()
        {
            return new AlgorithmicDesktopCreationTracker(Dispatcher.CurrentDispatcher);
        }

        private static IVirtualDesktop CreateDesktop(string name)
        {
            var desktop = new Mock<IVirtualDesktop>(MockBehavior.Loose);
            desktop.SetupGet(item => item.Name).Returns(name);
            return desktop.Object;
        }
    }
}
