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
        public async Task ProductionFramePreservesSmoothIntermediateAndFinalRectangles()
        {
            var window = new MovingWindow();
            var driver = new RealFrameDriver();
            var target = new Rectangle(5, 5, 125, 125);
            var transition = new TransitionTargetGroup(driver, [new(window.Object, window.Position, target)])
                .PerformSmoothTransitionAsync(TimeSpan.FromMilliseconds(100));
            foreach (var progress in new[] { 0, .01, .25, .5, .51, .75, 1 }) driver.Step(progress);
            await transition.WaitAsync(TimeSpan.FromSeconds(5));
            CollectionAssert.AreEqual(new[]
            {
                new Rectangle(2, 2, 102, 102), new Rectangle(2, 2, 122, 122),
                new Rectangle(4, 4, 124, 124), target,
            }, window.Writes.ToArray());
            Assert.AreEqual(target, window.Position);
            Assert.AreEqual(7, window.Reads);
            Assert.AreEqual(0, driver.Frame.Jobs.Count);
        }

        [TestMethod]
        public async Task ProductionFrameDispatchesAnotherSmoothTargetWhileOneNativeWriteIsBlocked()
        {
            using var release = new ManualResetEventSlim();
            var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = new MovingWindow();
            var second = new MovingWindow();
            var target = new Rectangle(7, 9, 107, 109);
            int frameThread = 0, nativeThread = 0;
            first.m_window.Setup(window => window.SetPosition(It.IsAny<Rectangle>())).Callback<Rectangle>(position =>
            {
                nativeThread = Environment.CurrentManagedThreadId;
                firstEntered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test frame barrier was not released.");
                first.Position = position;
            });
            second.m_window.Setup(window => window.SetPosition(It.IsAny<Rectangle>())).Callback<Rectangle>(position =>
            {
                second.Position = position;
                secondEntered.TrySetResult();
            });
            var driver = new RealFrameDriver();
            var transition = new TransitionTargetGroup(driver,
                [new(first.Object, first.Position, target), new(second.Object, second.Position, target)])
                .PerformSmoothTransitionAsync(TimeSpan.FromMilliseconds(100));
            var frame = Task.Run(() => { frameThread = Environment.CurrentManagedThreadId; driver.Step(1); });
            try
            {
                await Task.WhenAll(firstEntered.Task, secondEntered.Task).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreNotEqual(frameThread, nativeThread);
                Assert.AreEqual(target, second.Position);
                Assert.IsFalse(frame.IsCompleted);
                Assert.IsFalse(transition.IsCompleted);
            }
            finally
            {
                release.Set();
                await frame.WaitAsync(TimeSpan.FromSeconds(5));
                await transition.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.AreEqual(target, first.Position);
            Assert.AreEqual(1, first.Reads);
            Assert.AreEqual(1, second.Reads);
            Assert.AreEqual(0, driver.Frame.Jobs.Count);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ProductionFrameRemovesOnlyTheInvalidOrMinimizedSmoothTarget(bool minimized)
        {
            var failed = new MovingWindow { ThrowOnWrite = !minimized, MinimizeOnWrite = minimized };
            var good = new MovingWindow();
            var target = new Rectangle(10, 10, 110, 110);
            var driver = new RealFrameDriver();
            var transition = new TransitionTargetGroup(driver,
                [new(failed.Object, failed.Position, target), new(good.Object, good.Position, target)])
                .PerformSmoothTransitionAsync(TimeSpan.FromMilliseconds(100));
            driver.Step(.5);
            Assert.AreEqual(1, driver.Frame.Jobs.Count);
            Assert.IsFalse(transition.IsCompleted);
            Assert.AreEqual(new Rectangle(5, 5, 105, 105), good.Position);
            driver.Step(1);
            await transition.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(target, good.Position);
            Assert.AreEqual(2, good.Reads);
            Assert.AreEqual(2, good.Writes.Count);
            Assert.AreEqual(new Rectangle(0, 0, 100, 100), failed.Position);
            Assert.AreEqual(1, failed.Reads);
            Assert.AreEqual(0, failed.Writes.Count);
            Assert.AreEqual(0, driver.Frame.Jobs.Count);
        }

        private sealed class RealFrameDriver : IAnimationThread
        {
            public AnimationThread.Frame Frame { get; } = new(new Stopwatch());

            public void Start(IAnimationJob job) => Frame.Jobs.Add(new(job, TimeSpan.Zero, job.Duration));

            public void Step(double progress)
            {
                foreach (var job in Frame.Jobs)
                {
                    job.StartTime = TimeSpan.FromTicks(-(long)(job.Job.Duration.Ticks * progress));
                }
                Frame.UpdateFrame();
            }

            public void Dispose() { }
        }
    }
}
