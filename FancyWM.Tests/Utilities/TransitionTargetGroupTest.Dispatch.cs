using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WinMan;

namespace FancyWM.Tests.Utilities
{
    public partial class TransitionTargetGroupTest
    {
        [TestMethod]
        public async Task BlockedPositionWriteDoesNotSerializeAnotherTarget()
        {
            using var release = new ManualResetEventSlim();
            var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int callerThread = Environment.CurrentManagedThreadId, workerThread = 0;
            var target = new Rectangle(7, 9, 107, 109);
            var first = new MovingWindow();
            var second = new MovingWindow();
            first.m_window.Setup(window => window.SetPosition(It.IsAny<Rectangle>())).Callback<Rectangle>(position =>
            {
                workerThread = Environment.CurrentManagedThreadId;
                firstEntered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test barrier was not released.");
                first.Position = position;
            });
            second.m_window.Setup(window => window.SetPosition(It.IsAny<Rectangle>())).Callback<Rectangle>(position =>
            {
                second.Position = position;
                secondEntered.TrySetResult();
            });
            var transition = TransitionTargetGroup.PerformTransitionAsync(
                [new(first.Object, first.Position, target), new(second.Object, second.Position, target)]);
            try
            {
                await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreNotEqual(callerThread, workerThread);
                Assert.IsFalse(transition.IsCompleted, "Completion must include the blocked native adapter.");
                Assert.AreEqual(target, second.Position);
            }
            finally
            {
                release.Set();
                await transition.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.AreEqual(target, first.Position);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task UnexpectedWriteErrorIsObservedAfterOtherTargetCompletes(bool cancelled)
        {
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var error = cancelled ? (Exception)new OperationCanceledException() : new InvalidOperationException("restored adapter failure");
            var failed = new MovingWindow();
            var good = new MovingWindow();
            var target = new Rectangle(4, 6, 104, 106);
            failed.m_window.Setup(window => window.SetPosition(It.IsAny<Rectangle>())).Throws(error);
            good.m_window.Setup(window => window.SetPosition(It.IsAny<Rectangle>())).Callback<Rectangle>(position =>
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test barrier was not released.");
                good.Position = position;
            });
            var transition = TransitionTargetGroup.PerformTransitionAsync(
                [new(failed.Object, failed.Position, target), new(good.Object, good.Position, target)]);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsFalse(transition.IsCompleted);
            }
            finally { release.Set(); }
            try
            {
                await transition.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Fail("The unexpected write error must reach the caller.");
            }
            catch (Exception actual) when (actual is InvalidOperationException or OperationCanceledException)
            {
                if (!cancelled) Assert.AreSame(error, actual);
                else Assert.IsInstanceOfType(actual, typeof(OperationCanceledException));
            }
            Assert.AreEqual(target, good.Position);
            Assert.AreEqual(cancelled, transition.IsCanceled);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task DirectTransitionToleratesOnlyTheExistingInvalidOrMinimizedFailures(bool minimized)
        {
            var failed = new MovingWindow { ThrowOnWrite = !minimized, MinimizeOnWrite = minimized };
            var good = new MovingWindow();
            var target = new Rectangle(4, 6, 104, 106);
            await TransitionTargetGroup.PerformTransitionAsync(
                [new(failed.Object, failed.Position, target), new(good.Object, good.Position, target)]).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(target, good.Position);
            Assert.AreEqual(1, good.Writes.Count);
            Assert.AreEqual(0, failed.Writes.Count);
        }

        [TestMethod]
        public async Task TransitionDispatchCounterScenario()
        {
            foreach (int count in new[] { 1, 4, 10, 25, 50 })
            {
                var windows = Enumerable.Range(0, count).Select(_ => new MovingWindow()).ToArray();
                var target = new Rectangle(11, 13, 111, 113);
                var targets = windows.Select(window => new TransitionTarget(window.Object, window.Position, target)).ToList();
                for (int iteration = 0; iteration < 20; iteration++)
                    await TransitionTargetGroup.PerformTransitionAsync(targets);
                foreach (var window in windows) { window.Reset(); window.Writes.EnsureCapacity(100); }
                long allocated = GC.GetTotalAllocatedBytes(precise: true);
                long started = Stopwatch.GetTimestamp();
                for (int iteration = 0; iteration < 100; iteration++)
                    await TransitionTargetGroup.PerformTransitionAsync(targets);
                long elapsed = Stopwatch.GetTimestamp() - started;
                allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
                foreach (var window in windows)
                {
                    Assert.AreEqual(target, window.Position);
                    Assert.AreEqual(100, window.Writes.Count);
                    Assert.IsTrue(window.Writes.All(position => position == target));
                    Assert.AreEqual(0, window.Reads);
                }
                Console.WriteLine($"PERFCOUNTER transition-dispatch-{count} allocated-bytes {allocated}");
                Console.WriteLine($"PERFCOUNTER transition-dispatch-{count} elapsed-ticks {elapsed}");
                Console.WriteLine($"PERFCOUNTER transition-dispatch-{count} timestamp-frequency {Stopwatch.Frequency}");
                Console.WriteLine($"PERFCOUNTER transition-dispatch-{count} position-writes {count * 100}");
                Console.WriteLine($"PERFCOUNTER transition-dispatch-{count} transitions 100");
            }
        }
    }
}
