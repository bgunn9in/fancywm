using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WinMan;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public partial class TransitionTargetGroupTest
    {
        [TestMethod]
        public async Task RoundedFramesSkipRepeatedRectanglesButKeepHalfwaySizeChange()
        {
            var window = new MovingWindow();
            var scheduler = new Frames();
            var target = new Rectangle(5, 5, 125, 125);
            var transition = new TransitionTargetGroup(scheduler, [new(window.Object, window.Position, target)])
                .PerformSmoothTransitionAsync(TimeSpan.FromMilliseconds(100));
            foreach (var progress in new[] { 0, .01, .25, .5, .51, .75, 1 }) await scheduler.Step(progress);
            await transition.WaitAsync(TimeSpan.FromSeconds(5));
            CollectionAssert.AreEqual(new[]
            {
                new Rectangle(2, 2, 102, 102), new Rectangle(2, 2, 122, 122),
                new Rectangle(4, 4, 124, 124), target,
            }, window.Writes.ToArray());
            Assert.AreEqual(target, window.Position);
            Assert.AreEqual(7, window.Reads, "Every frame still observes external position changes.");
        }

        [TestMethod]
        public async Task AlreadyAtTargetCompletesWithoutNativeWrite()
        {
            var window = new MovingWindow();
            var scheduler = new Frames();
            var transition = new TransitionTargetGroup(scheduler, [new(window.Object, window.Position, window.Position)])
                .PerformSmoothTransitionAsync(TimeSpan.FromMilliseconds(100));
            await scheduler.Step(0);
            await transition.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, window.Writes.Count);
            Assert.AreEqual(1, window.Reads);
            Assert.IsTrue(scheduler.Jobs[0].IsCancelled);
        }

        [TestMethod]
        public async Task ExternalMoveKeepsExistingFinalRectangleAndDoesNotCancelOtherTargets()
        {
            var first = new MovingWindow();
            var second = new MovingWindow();
            var scheduler = new Frames();
            var target = new Rectangle(10, 10, 130, 130);
            var transition = new TransitionTargetGroup(scheduler,
                [new(first.Object, first.Position, target), new(second.Object, second.Position, target)])
                .PerformSmoothTransitionAsync(TimeSpan.FromMilliseconds(100));
            await scheduler.Step(.25);
            first.Position = new Rectangle(50, 50, 150, 150);
            await scheduler.Step(.5);
            Assert.AreEqual(target, first.Position);
            Assert.IsTrue(scheduler.Jobs[0].IsCancelled);
            Assert.IsFalse(scheduler.Jobs[1].IsCancelled);
            await scheduler.Step(1);
            await transition.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(target, second.Position);
        }

        [TestMethod]
        public async Task WindowDisappearingDuringWriteDoesNotCancelAnotherWindow()
        {
            var invalid = new MovingWindow { ThrowOnWrite = true };
            var good = new MovingWindow();
            var scheduler = new Frames();
            var target = new Rectangle(10, 10, 110, 110);
            var transition = new TransitionTargetGroup(scheduler,
                [new(invalid.Object, invalid.Position, target), new(good.Object, good.Position, target)])
                .PerformSmoothTransitionAsync(TimeSpan.FromMilliseconds(100));
            await scheduler.Step(.5);
            Assert.IsTrue(scheduler.Jobs[0].IsCancelled);
            Assert.IsFalse(scheduler.Jobs[1].IsCancelled);
            await scheduler.Step(1);
            await transition.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(target, good.Position);
            Assert.AreEqual(2, good.Writes.Count);
        }

        [TestMethod]
        public async Task MinimizedWindowWriteFailureCancelsOnlyItsOwnJob()
        {
            var minimized = new MovingWindow { MinimizeOnWrite = true };
            var good = new MovingWindow();
            var scheduler = new Frames();
            var target = new Rectangle(10, 10, 110, 110);
            var transition = new TransitionTargetGroup(scheduler,
                [new(minimized.Object, minimized.Position, target), new(good.Object, good.Position, target)])
                .PerformSmoothTransitionAsync(TimeSpan.FromMilliseconds(100));
            await scheduler.Step(.5);
            Assert.IsTrue(scheduler.Jobs[0].IsCancelled);
            Assert.IsFalse(scheduler.Jobs[1].IsCancelled);
            await scheduler.Step(1);
            await transition.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(target, good.Position);
            Assert.AreEqual(new Rectangle(0, 0, 100, 100), minimized.Position);
            Assert.AreEqual(0, minimized.Writes.Count);
        }

        [TestMethod]
        public async Task RoundedTransitionCounterScenario()
        {
            foreach (int count in new[] { 1, 4, 10, 25, 50 })
            {
                await Run(count, 2);
                var result = await Run(count, 100);
                Console.WriteLine($"PERFCOUNTER transition-rounded-{count} position-writes {result.Writes}");
                Console.WriteLine($"PERFCOUNTER transition-rounded-{count} position-reads {result.Reads}");
                Console.WriteLine($"PERFCOUNTER transition-rounded-{count} transitions 100");
            }

            static async Task<(int Writes, int Reads)> Run(int count, int iterations)
            {
                int writes = 0, reads = 0;
                var target = new Rectangle(5, 0, 105, 100);
                var windows = Enumerable.Range(0, count).Select(_ => new MovingWindow()).ToArray();
                for (int iteration = 0; iteration < iterations; iteration++)
                {
                    foreach (var window in windows) window.Reset();
                    var scheduler = new Frames();
                    var transition = new TransitionTargetGroup(scheduler, windows.Select(window => new TransitionTarget(window.Object, window.Position, target)))
                        .PerformSmoothTransitionAsync(TimeSpan.FromMilliseconds(100));
                    for (int frame = 1; frame <= 14; frame++) await scheduler.Step(frame / 14d);
                    await transition.WaitAsync(TimeSpan.FromSeconds(5));
                    foreach (var window in windows)
                    {
                        Assert.AreEqual(target, window.Position);
                        Assert.AreEqual(14, window.Reads, "Observation cadence remains intact.");
                        writes += window.Writes.Count;
                        reads += window.Reads;
                    }
                }
                return (writes, reads);
            }
        }

        private sealed class MovingWindow
        {
            public readonly Mock<IWindow> m_window = new();
            public IWindow Object => m_window.Object;
            public Rectangle Position = new(0, 0, 100, 100);
            public List<Rectangle> Writes { get; } = [];
            public int Reads;
            public bool ThrowOnWrite;
            public bool MinimizeOnWrite;
            private WindowState m_state = WindowState.Restored;
            public MovingWindow()
            {
                m_window.SetupGet(window => window.Position).Returns(() => { Reads++; return Position; });
                m_window.SetupGet(window => window.State).Returns(() => m_state);
                m_window.Setup(window => window.SetPosition(It.IsAny<Rectangle>())).Callback<Rectangle>(position =>
                {
                    if (ThrowOnWrite) throw new InvalidWindowReferenceException(IntPtr.Zero);
                    if (MinimizeOnWrite)
                    {
                        m_state = WindowState.Minimized;
                        throw new InvalidOperationException("A minimized window cannot be moved by this adapter.");
                    }
                    Writes.Add(position);
                    Position = position;
                });
            }
            public void Reset() { Position = new(0, 0, 100, 100); Writes.Clear(); Reads = 0; }
        }

        private sealed class Frames : IAnimationThread
        {
            public List<IAnimationJob> Jobs { get; } = [];
            private readonly HashSet<IAnimationJob> m_completed = [];
            public void Start(IAnimationJob job) => Jobs.Add(job);
            public async Task Step(double progress)
            {
                foreach (var job in Jobs)
                {
                    if (m_completed.Contains(job)) continue;
                    await job.Update(progress);
                    if (progress >= 1) { job.OnCompleted(); m_completed.Add(job); }
                    else if (job.IsCancelled) { job.OnCancelled(); m_completed.Add(job); }
                }
            }
            public void Dispose() { }
        }
    }
}
