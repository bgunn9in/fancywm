#nullable enable

using System;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    public partial class MultiDisplayTilingServiceIntegrationTest
    {
        [TestMethod]
        public void ShutdownPreparationAdmitsAndWaitsForEveryDisplayAcross100Lifetimes()
        {
            for (int cycle = 0; cycle < 100; cycle++)
            {
                using var fixture = new ServiceFixture(includeSecondDisplay: true);
                var first = fixture.GetService(fixture.PrimaryDisplay);
                var second = fixture.GetService(fixture.SecondDisplay);
                var firstCompletion = NewShutdownCompletion();
                var secondCompletion = NewShutdownCompletion();
                first.OnPrepareForShutdown = () => firstCompletion.Task;
                second.OnPrepareForShutdown = () => secondCompletion.Task;
                var prepared = fixture.Service.PrepareForShutdownAsync();
                try
                {
                    Assert.AreEqual(1, first.PrepareForShutdownCount);
                    Assert.AreEqual(1, second.PrepareForShutdownCount);
                    Assert.IsFalse(prepared.IsCompleted);
                    Assert.IsFalse(fixture.Service.Active);
                    Assert.AreSame(prepared, fixture.Service.PrepareForShutdownAsync());
                    firstCompletion.SetResult();
                    Assert.IsFalse(prepared.IsCompleted, "A later display still owns its entered layout.");
                    Assert.AreEqual(0, first.StopCount);
                    Assert.AreEqual(0, second.StopCount);
                    Assert.AreEqual(0, first.DisposeCount);
                    Assert.AreEqual(0, second.DisposeCount);
                }
                finally
                {
                    firstCompletion.TrySetResult();
                    secondCompletion.TrySetResult();
                    prepared.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                }
                fixture.Service.Stop();
                fixture.Service.Dispose();
                Assert.AreEqual(1, first.StopCount);
                Assert.AreEqual(1, second.StopCount);
                Assert.AreEqual(1, first.DisposeCount);
                Assert.AreEqual(1, second.DisposeCount);
                Assert.AreEqual(1, first.PrepareForShutdownCount);
                Assert.AreEqual(1, second.PrepareForShutdownCount);
            }
        }

        [TestMethod]
        public void ShutdownPreparationPublishesBeforeReentrancyAndRetainsChildrenAcrossDisplayEvents()
        {
            using var fixture = new ServiceFixture(includeSecondDisplay: true);
            var first = fixture.GetService(fixture.PrimaryDisplay);
            var second = fixture.GetService(fixture.SecondDisplay);
            var pending = NewShutdownCompletion();
            Task? reentrant = null;
            var added = fixture.CreateDisplay(new Rectangle(2000, 0, 2999, 999));
            int firstStarts = first.StartCount, secondStarts = second.StartCount;
            first.OnPrepareForShutdown = () =>
            {
                reentrant = fixture.Service.PrepareForShutdownAsync();
                fixture.RemoveDisplay(fixture.SecondDisplay);
                fixture.AddDisplay(added);
                fixture.Service.Start();
                return pending.Task;
            };
            var prepared = fixture.Service.PrepareForShutdownAsync();
            try
            {
                Assert.AreSame(prepared, reentrant);
                Assert.AreEqual(1, first.PrepareForShutdownCount);
                Assert.AreEqual(1, second.PrepareForShutdownCount);
                Assert.AreEqual(2, fixture.CreatedServiceCount);
                Assert.AreEqual(firstStarts, first.StartCount);
                Assert.AreEqual(secondStarts, second.StartCount);
                Assert.AreEqual(0, second.StopCount);
                Assert.AreEqual(0, second.DisposeCount);
                Assert.AreEqual(0, fixture.GarbageCollectionRequests);
                Assert.IsFalse(prepared.IsCompleted);
            }
            finally { pending.TrySetResult(); prepared.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
            fixture.Service.Stop();
            fixture.Service.Dispose();
            Assert.AreEqual(1, second.StopCount);
            Assert.AreEqual(1, second.DisposeCount);
        }

        [TestMethod]
        public void PreparationAttemptsLaterChildrenAndWaitsAfterSynchronousAndAsynchronousFailures()
        {
            using var fixture = new ServiceFixture(includeSecondDisplay: true);
            var thirdDisplay = fixture.CreateDisplay(new Rectangle(2000, 0, 2999, 999));
            fixture.AddDisplay(thirdDisplay);
            var first = fixture.GetService(fixture.PrimaryDisplay);
            var second = fixture.GetService(fixture.SecondDisplay);
            var third = fixture.GetService(thirdDisplay);
            var firstFailure = new InvalidOperationException("first preparation");
            var secondFailure = new ApplicationException("second worker");
            var thirdFailure = new ArgumentException("third worker");
            var secondCompletion = NewShutdownCompletion();
            var thirdCompletion = Task.FromException(thirdFailure);
            first.OnPrepareForShutdown = () => throw firstFailure;
            second.OnPrepareForShutdown = () => secondCompletion.Task;
            third.OnPrepareForShutdown = () => thirdCompletion;
            var prepared = fixture.Service.PrepareForShutdownAsync();
            try
            {
                Assert.IsFalse(prepared.IsCompleted);
                Assert.AreEqual(1, first.PrepareForShutdownCount);
                Assert.AreEqual(1, second.PrepareForShutdownCount);
                Assert.AreEqual(1, third.PrepareForShutdownCount);
                Assert.AreEqual(0, second.DisposeCount);
            }
            finally
            {
                secondCompletion.TrySetException(secondFailure);
                _ = thirdCompletion.Exception;
            }
            var error = Assert.ThrowsException<AggregateException>(() => prepared.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
            CollectionAssert.AreEqual(new Exception[] { firstFailure, secondFailure, thirdFailure }, error.InnerExceptions.ToArray());
            Assert.AreSame(prepared, fixture.Service.PrepareForShutdownAsync());
        }

        [TestMethod]
        public void FinalShutdownRestoreAttemptsEveryDisplayOnceAfterFailureAndReentrantStop()
        {
            using var fixture = new ServiceFixture(includeSecondDisplay: true);
            var first = fixture.GetService(fixture.PrimaryDisplay);
            var second = fixture.GetService(fixture.SecondDisplay);
            var pending = NewShutdownCompletion();
            first.OnPrepareForShutdown = () => pending.Task;
            var failure = new InvalidOperationException("first restore");
            first.OnStop = () =>
            {
                if (first.StopCount == 1) { fixture.Service.Stop(); }
                throw failure;
            };
            var prepared = fixture.Service.PrepareForShutdownAsync();
            try
            {
                try
                {
                    fixture.Service.Stop();
                    Assert.AreEqual(0, first.StopCount, "Stop must not restore while preparation still owns entered work.");
                    Assert.AreEqual(0, second.StopCount);
                }
                finally { pending.TrySetResult(); prepared.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
                var error = Assert.ThrowsException<AggregateException>(fixture.Service.Stop);
                CollectionAssert.AreEqual(new Exception[] { failure }, error.InnerExceptions.ToArray());
                Assert.AreEqual(1, first.StopCount);
                Assert.AreEqual(1, second.StopCount);
                fixture.Service.Stop();
                fixture.Service.Dispose();
                Assert.AreEqual(1, first.StopCount);
                Assert.AreEqual(1, second.StopCount);
                Assert.AreEqual(1, first.DisposeCount);
                Assert.AreEqual(1, second.DisposeCount);
            }
            finally { first.OnStop = null; }
        }

        [TestMethod]
        public void PreparedMultiDisplayDoesNotRestartRefreshOrDiscoverChildren()
        {
            using var fixture = new ServiceFixture(includeSecondDisplay: true);
            var first = fixture.GetService(fixture.PrimaryDisplay);
            var second = fixture.GetService(fixture.SecondDisplay);
            int firstStarts = first.StartCount, secondStarts = second.StartCount;
            int firstRefresh = first.RefreshCount, secondRefresh = second.RefreshCount;
            int discovery = 0;
            first.Discover = second.Discover = () => { discovery++; return true; };
            fixture.Service.PrepareForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            fixture.Service.Start();
            fixture.Service.Refresh();
            Assert.IsFalse(fixture.Service.DiscoverWindows());
            Assert.IsFalse(fixture.Service.Active);
            Assert.AreEqual(firstStarts, first.StartCount);
            Assert.AreEqual(secondStarts, second.StartCount);
            Assert.AreEqual(firstRefresh, first.RefreshCount);
            Assert.AreEqual(secondRefresh, second.RefreshCount);
            Assert.AreEqual(0, discovery);
        }

        private static TaskCompletionSource NewShutdownCompletion()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
