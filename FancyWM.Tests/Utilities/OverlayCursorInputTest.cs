#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

using FancyWM.DllImports;
using FancyWM.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class OverlayCursorInputTest
    {
        [TestMethod]
        public void HiddenAndClosedInputDoesNotPostOrReadNativeState()
        {
            using var fixture = new Fixture();
            for (int input = 0; input < 1000; input++) fixture.Invalidate();
            Assert.AreEqual(0, fixture.Posts, "Hidden overlays must not enqueue cursor work.");
            fixture.Close();
            for (int input = 0; input < 1000; input++) fixture.Invalidate();
            fixture.Drain();
            Assert.AreEqual(0, fixture.Posts);
            Assert.AreEqual(0, fixture.CursorReads);
            Assert.AreEqual(0, fixture.StyleReads);
            Assert.AreEqual(0, fixture.Writes.Count);
        }

        [TestMethod]
        public void VisibleBurstSamplesLatestPointAndPreservesOtherStyleBits()
        {
            using var fixture = new Fixture { Origin = new(100, 200), Scale = 2 };
            fixture.Show();
            for (int input = 0; input < 1000; input++)
            {
                fixture.Cursor = new(100 + input, 200 + input);
                fixture.Invalidate();
            }
            fixture.Cursor = new(110, 212);
            fixture.Drain();
            Assert.AreEqual(1, fixture.Posts);
            Assert.AreEqual(1, fixture.CursorReads);
            Assert.AreEqual(new Point(5, 6), fixture.LastHitPoint);
            Assert.AreEqual(Fixture.OtherStyleBits, fixture.Style);
            fixture.Invalidate();
            fixture.Drain();
            Assert.AreEqual(1, fixture.Writes.Count, "An unchanged enabled style must not be written twice.");
            fixture.Cursor = new(228, 212);
            fixture.Invalidate();
            fixture.Drain();
            Assert.AreEqual(Fixture.OtherStyleBits | Fixture.Disabled, fixture.Style);
            Assert.AreEqual(2, fixture.Writes.Count);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void PendingCallbackCannotApplyAfterHideOrClose(bool close)
        {
            using var fixture = new Fixture();
            fixture.Show();
            if (close) fixture.Close(); else fixture.Hide();
            fixture.Drain();
            Assert.AreEqual(0, fixture.CursorReads);
            Assert.AreEqual(0, fixture.StyleReads);
            Assert.AreEqual(0, fixture.Writes.Count);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void ShowAfterHiddenPendingPassRequestsLatestPointWithoutAnotherInput(bool drainWhileHidden)
        {
            using var fixture = new Fixture();
            fixture.Show();
            fixture.Hide();
            if (drainWhileHidden) fixture.Drain();
            fixture.Cursor = new(14, 15);
            fixture.Show();
            fixture.Show();
            fixture.Drain();
            Assert.AreEqual(1, fixture.HitTests);
            Assert.AreEqual(drainWhileHidden ? 2 : 1, fixture.Posts);
            Assert.AreEqual(new Point(14, 15), fixture.LastHitPoint);
            Assert.AreEqual(Fixture.OtherStyleBits, fixture.Style);
        }

        [DataTestMethod]
        [DataRow(Boundary.Cursor)]
        [DataRow(Boundary.Convert)]
        [DataRow(Boundary.Hit)]
        [DataRow(Boundary.Style)]
        public void CloseAtEveryNativeBoundaryPreventsLaterOperations(Boundary boundary)
        {
            using var fixture = new Fixture();
            fixture.OnBoundary = current => { if (current == boundary) fixture.Close(); };
            fixture.Show();
            fixture.Drain();
            Assert.AreEqual(0, fixture.Writes.Count);
            Assert.AreEqual((int)boundary >= (int)Boundary.Convert ? 1 : 0, fixture.Conversions);
            Assert.AreEqual((int)boundary >= (int)Boundary.Hit ? 1 : 0, fixture.HitTests);
            Assert.AreEqual((int)boundary >= (int)Boundary.Style ? 1 : 0, fixture.StyleReads);
        }

        [DataTestMethod]
        [DataRow(Boundary.Cursor)]
        [DataRow(Boundary.Convert)]
        [DataRow(Boundary.Hit)]
        [DataRow(Boundary.Style)]
        public void HideAndShowAtEveryNativeBoundaryDiscardOldGeneration(Boundary boundary)
        {
            using var fixture = new Fixture();
            fixture.OnBoundary = current =>
            {
                if (current != boundary) return;
                fixture.OnBoundary = null;
                fixture.Hide();
                fixture.Cursor = new(100, 100);
                fixture.Show();
            };
            fixture.Show();
            fixture.DrainOne();
            Assert.AreEqual(0, fixture.Writes.Count, "The old hit result must not enable a newly shown generation.");
            fixture.Drain();
            Assert.AreEqual(new Point(100, 100), fixture.LastHitPoint);
            Assert.AreEqual(Fixture.OtherStyleBits | Fixture.Disabled, fixture.Style);
            Assert.AreEqual(0, fixture.Writes.Count);
            Assert.AreEqual(2, fixture.Posts);
        }

        [TestMethod]
        public void ReentrantInputPreservesLatestInvalidationWithoutOverlappingPasses()
        {
            using var fixture = new Fixture();
            fixture.OnBoundary = current =>
            {
                if (current != Boundary.Hit) return;
                fixture.OnBoundary = null;
                fixture.Cursor = new(100, 100);
                for (int input = 0; input < 1000; input++) fixture.Invalidate();
            };
            fixture.Show();
            fixture.DrainOne();
            Assert.AreEqual(1, fixture.Pending.Count);
            fixture.Drain();
            Assert.AreEqual(2, fixture.HitTests);
            Assert.AreEqual(new Point(100, 100), fixture.LastHitPoint);
            Assert.AreEqual(Fixture.OtherStyleBits | Fixture.Disabled, fixture.Style);
            Assert.AreEqual(2, fixture.Posts);
        }

        [DataTestMethod]
        [DataRow(Boundary.Convert)]
        [DataRow(Boundary.Hit)]
        public void PresentationSourceFailureStillDisablesOnlyWhileOwnerIsCurrent(Boundary boundary)
        {
            using var fixture = new Fixture { Style = Fixture.OtherStyleBits };
            fixture.OnBoundary = current => { if (current == boundary) throw new InvalidOperationException("disconnected visual"); };
            fixture.Show();
            fixture.Drain();
            Assert.AreEqual(Fixture.OtherStyleBits | Fixture.Disabled, fixture.Style);
            Assert.AreEqual(0, fixture.Errors.Count);
            Assert.AreEqual(0, fixture.Escaped.Count);
            fixture.Style = Fixture.OtherStyleBits;
            fixture.OnBoundary = current =>
            {
                if (current != boundary) return;
                fixture.Close();
                throw new InvalidOperationException("closed visual");
            };
            fixture.Invalidate();
            fixture.Drain();
            Assert.AreEqual(Fixture.OtherStyleBits, fixture.Style);
            Assert.AreEqual(1, fixture.Writes.Count);
        }

        [TestMethod]
        public void SchedulingFailureIsReportedAndLaterInputCanRetry()
        {
            using var fixture = new Fixture { FailNextPost = true };
            fixture.Show();
            Assert.AreEqual(1, fixture.Errors.Count);
            Assert.AreEqual(0, fixture.Escaped.Count);
            fixture.Invalidate();
            fixture.Drain();
            Assert.AreEqual(2, fixture.Posts);
            Assert.AreEqual(1, fixture.HitTests);
        }

        [TestMethod]
        public void InvalidationDuringFailedPostIsRetriedOnceAndCloseCannotResurrectIt()
        {
            using var fixture = new Fixture { FailNextPost = true };
            fixture.OnPost = () =>
            {
                fixture.OnPost = null;
                fixture.Invalidate();
            };
            fixture.Show();
            Assert.AreEqual(1, fixture.Errors.Count);
            Assert.AreEqual(2, fixture.Posts);
            fixture.Close();
            fixture.Drain();
            Assert.AreEqual(0, fixture.CursorReads);
        }

        [DataTestMethod]
        [DataRow(Boundary.Cursor)]
        [DataRow(Boundary.Hit)]
        [DataRow(Boundary.Style)]
        [DataRow(Boundary.Write)]
        public void UnexpectedFailureIsObservedAndReentrantInputStillApplies(Boundary boundary)
        {
            using var fixture = new Fixture();
            var error = new ApplicationException("native adapter failure");
            fixture.OnBoundary = current =>
            {
                if (current != boundary) return;
                fixture.OnBoundary = null;
                fixture.Cursor = new(100, 100);
                fixture.Invalidate();
                throw error;
            };
            fixture.Show();
            fixture.Drain();
            Assert.AreEqual(0, fixture.Escaped.Count);
            CollectionAssert.AreEqual(new[] { error }, fixture.Errors);
            Assert.AreEqual(new Point(100, 100), fixture.LastHitPoint);
            Assert.AreEqual(Fixture.OtherStyleBits | Fixture.Disabled, fixture.Style);
            Assert.AreEqual(2, fixture.Posts);
        }

        [TestMethod]
        public async Task ConcurrentProducersDuringPostKeepOnePendingPassAndLatestCursor()
        {
            using var fixture = new Fixture();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            fixture.ActivateWithoutInvalidation();
            fixture.OnPost = () =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("controlled post release");
            };
            var posting = Task.Run(fixture.Invalidate);
            try
            {
                Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
                await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
                {
                    for (int input = 0; input < 1000; input++) fixture.Invalidate();
                }))).WaitAsync(TimeSpan.FromSeconds(5));
                fixture.Cursor = new(11, 12);
            }
            finally
            {
                release.Set();
                await posting.WaitAsync(TimeSpan.FromSeconds(5));
            }
            fixture.OnPost = null;
            fixture.Drain();
            Assert.AreEqual(1, fixture.Posts);
            Assert.AreEqual(1, fixture.HitTests);
            Assert.AreEqual(new Point(11, 12), fixture.LastHitPoint);
        }

        [TestMethod]
        public void BackgroundProducerAppliesNativeAdaptersOnlyOnCapturedStaDispatcher()
        {
            RunSta(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                int ownerThread = Environment.CurrentManagedThreadId;
                using var fixture = new Fixture(callback => dispatcher.BeginInvoke(callback, DispatcherPriority.Background));
                fixture.OnBoundary = _ => Assert.AreEqual(ownerThread, Environment.CurrentManagedThreadId);
                fixture.ActivateWithoutInvalidation();
                Task.Run(fixture.Invalidate).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                Assert.AreEqual(0, fixture.CursorReads);
                fixture.Cursor = new(31, 32);
                var frame = new DispatcherFrame();
                dispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.ApplicationIdle);
                Dispatcher.PushFrame(frame);
                Assert.AreEqual(1, fixture.CursorReads);
                Assert.AreEqual(new Point(31, 32), fixture.LastHitPoint);
                Assert.AreEqual(0, fixture.Errors.Count);
                Assert.AreEqual(0, fixture.Escaped.Count);
                fixture.Close();
                Task.Run(fixture.Invalidate).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                Assert.AreEqual(1, fixture.Posts);
            });
        }

        [TestMethod]
        public void NestedDispatcherPumpCannotApplyNewerPointBeforeAnOlderPass()
        {
            RunSta(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                DispatcherOperation? firstPost = null;
                using var fixture = new Fixture(callback =>
                {
                    var operation = dispatcher.BeginInvoke(callback, DispatcherPriority.Background);
                    firstPost ??= operation;
                });
                int hitsInsideNestedPump = 0;
                fixture.OnBoundary = boundary =>
                {
                    if (boundary != Boundary.Hit) return;
                    fixture.OnBoundary = null;
                    fixture.Cursor = new(100, 100);
                    fixture.Invalidate();
                    var nested = new DispatcherFrame();
                    dispatcher.BeginInvoke(() => nested.Continue = false, DispatcherPriority.ApplicationIdle);
                    Dispatcher.PushFrame(nested);
                    hitsInsideNestedPump = fixture.HitTests;
                };
                fixture.Show();
                Assert.IsNotNull(firstPost);
                Assert.AreEqual(DispatcherOperationStatus.Completed, firstPost!.Wait(TimeSpan.FromSeconds(5)));
                var drain = new DispatcherFrame();
                dispatcher.BeginInvoke(() => drain.Continue = false, DispatcherPriority.ApplicationIdle);
                Dispatcher.PushFrame(drain);
                Assert.AreEqual(1, hitsInsideNestedPump, "A nested UI pump must not overlap cursor passes.");
                Assert.AreEqual(2, fixture.HitTests);
                Assert.AreEqual(new Point(100, 100), fixture.LastHitPoint);
                Assert.AreEqual(Fixture.OtherStyleBits | Fixture.Disabled, fixture.Style,
                    "The older inside result must not overwrite the newer outside result.");
                Assert.AreEqual(0, fixture.Errors.Count);
                Assert.AreEqual(0, fixture.Escaped.Count);
            });
        }

        [TestMethod]
        public void OneHundredClosedLifetimesDrainWithoutEffectsAndReleaseDelegateTargets()
        {
            var pending = new Queue<Action>();
            var lateNativeCalls = new int[1];
            var references = Enumerable.Range(0, 100).Select(_ => CreateClosedLifetime(pending, lateNativeCalls)).ToArray();
            DrainExternal(pending);
            Assert.AreEqual(0, lateNativeCalls[0]);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.IsTrue(references.All(reference => !reference.IsAlive), "Consumed closed callbacks must release their bounded delegate targets.");
        }

        [TestMethod]
        public void CursorInputCounterScenario()
        {
            foreach (int displays in new[] { 1, 2, 4 })
            {
                foreach (string mode in new[] { "visible", "hidden", "closed" })
                {
                    var fixtures = Enumerable.Range(0, displays).Select(_ => new Fixture()).ToArray();
                    try
                    {
                        foreach (var fixture in fixtures)
                        {
                            if (mode == "visible") fixture.ActivateWithoutInvalidation();
                            else if (mode == "closed") fixture.Close();
                        }
                        for (int input = 0; input < 1000; input++)
                        {
                            var point = input % 2 == 0 ? new Point(12, 13) : new Point(80, 90);
                            foreach (var fixture in fixtures)
                            {
                                fixture.Cursor = point;
                                fixture.Invalidate();
                                fixture.Drain();
                                Assert.AreEqual(0, fixture.Errors.Count);
                                Assert.AreEqual(0, fixture.Escaped.Count);
                                if (mode == "visible")
                                {
                                    Assert.AreEqual(point, fixture.LastHitPoint);
                                    Assert.AreEqual(Fixture.OtherStyleBits | (input % 2 == 0 ? 0 : Fixture.Disabled), fixture.Style);
                                }
                            }
                        }
                        string scenario = $"overlay-cursor-{mode}-{displays}";
                        Console.WriteLine($"PERFCOUNTER {scenario} posts {fixtures.Sum(fixture => fixture.Posts)}");
                        Console.WriteLine($"PERFCOUNTER {scenario} cursor-reads {fixtures.Sum(fixture => fixture.CursorReads)}");
                        Console.WriteLine($"PERFCOUNTER {scenario} conversions {fixtures.Sum(fixture => fixture.Conversions)}");
                        Console.WriteLine($"PERFCOUNTER {scenario} hit-tests {fixtures.Sum(fixture => fixture.HitTests)}");
                        Console.WriteLine($"PERFCOUNTER {scenario} style-reads {fixtures.Sum(fixture => fixture.StyleReads)}");
                        Console.WriteLine($"PERFCOUNTER {scenario} style-writes {fixtures.Sum(fixture => fixture.Writes.Count)}");
                        Console.WriteLine($"PERFCOUNTER {scenario} input-events {1000 * displays}");
                    }
                    finally { foreach (var fixture in fixtures) fixture.Dispose(); }
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference CreateClosedLifetime(Queue<Action> pending, int[] nativeCalls)
        {
            var fixture = new Fixture(pending.Enqueue);
            fixture.OnBoundary = _ => nativeCalls[0]++;
            fixture.Show();
            fixture.Close();
            fixture.Close();
            fixture.Invalidate();
            return new(fixture);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DrainExternal(Queue<Action> pending)
        {
            while (pending.TryDequeue(out var callback)) callback();
        }

        private static void RunSta(Action action)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception error) { failure = error; }
                finally { Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown(); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)), "Controlled STA cursor test did not finish.");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        public enum Boundary { Cursor, Convert, Hit, Style, Write }

        private sealed class Fixture : IDisposable
        {
            internal const int OtherStyleBits = 0x123456;
            internal const int Disabled = (int)WINDOWS_STYLE.WS_DISABLED;
            private readonly (Action Invalidate, IDisposable Lifetime) m_input;
            private long m_generation;
            private long m_activeGeneration;
            private bool m_closed;
            internal readonly Queue<Action> Pending = new();
            internal readonly List<int> Writes = [];
            internal readonly List<Exception> Errors = [];
            internal readonly List<Exception> Escaped = [];
            internal int Posts, CursorReads, Conversions, HitTests, StyleReads;
            internal Point Cursor = new(12, 13);
            internal Point Origin;
            internal double Scale = 1;
            internal Point LastHitPoint;
            internal int Style = OtherStyleBits | Disabled;
            internal Action<Boundary>? OnBoundary;
            internal Action? OnPost;
            internal bool FailNextPost;

            internal Fixture(Action<Action>? schedule = null)
            {
                m_input = OverlayHost.CreateCursorInput(callback =>
                {
                    Posts++;
                    OnPost?.Invoke();
                    if (FailNextPost)
                    {
                        FailNextPost = false;
                        throw new ApplicationException("failed dispatcher post");
                    }
                    (schedule ?? Pending.Enqueue)(callback);
                }, () => Interlocked.Read(ref m_activeGeneration),
                () => { CursorReads++; var result = Cursor; OnBoundary?.Invoke(Boundary.Cursor); return result; },
                point =>
                {
                    Conversions++;
                    var result = new Point((point.X - Origin.X) / Scale, (point.Y - Origin.Y) / Scale);
                    OnBoundary?.Invoke(Boundary.Convert);
                    return result;
                }, point =>
                {
                    HitTests++;
                    LastHitPoint = point;
                    bool result = point.X >= 0 && point.X < 64 && point.Y >= 0 && point.Y < 64;
                    OnBoundary?.Invoke(Boundary.Hit);
                    return result;
                }, () => { StyleReads++; int result = Style; OnBoundary?.Invoke(Boundary.Style); return result; },
                value => { OnBoundary?.Invoke(Boundary.Write); Style = value; Writes.Add(value); },
                Errors.Add);
            }

            internal void ActivateWithoutInvalidation()
            {
                if (m_closed || m_activeGeneration != 0) return;
                Interlocked.Exchange(ref m_activeGeneration, ++m_generation);
            }
            internal void Show()
            {
                if (m_closed || m_activeGeneration != 0) return;
                ActivateWithoutInvalidation();
                Invalidate();
            }
            internal void Hide() => Interlocked.Exchange(ref m_activeGeneration, 0);
            internal void Close()
            {
                if (m_closed) return;
                m_closed = true;
                Hide();
                m_input.Lifetime.Dispose();
            }
            internal void Invalidate()
            {
                try { m_input.Invalidate(); }
                catch (Exception error) { Escaped.Add(error); }
            }
            internal void DrainOne()
            {
                Assert.IsTrue(Pending.TryDequeue(out var callback));
                try { callback!(); }
                catch (Exception error) { Escaped.Add(error); }
            }
            internal void Drain()
            {
                for (int pass = 0; Pending.Count > 0 && pass < 20; pass++) DrainOne();
                Assert.AreEqual(0, Pending.Count, "Cursor queue failed to quiesce.");
            }
            public void Dispose() => Close();
        }
    }
}
