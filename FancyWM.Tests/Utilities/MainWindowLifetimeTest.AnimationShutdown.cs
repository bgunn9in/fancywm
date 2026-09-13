#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    public partial class MainWindowLifetimeTest
    {
        private static readonly TimeSpan DependentGuard = TimeSpan.FromSeconds(10);

        [TestMethod]
        public async Task AnimationShutdownDefersDependentWorkspaceDisposal()
        {
            var result = await RunDependentShutdownCycle(requireSafe: true);
            Assert.AreEqual(0, result.Premature);
            Assert.AreEqual(1, result.Pending);
        }

        [TestMethod]
        public async Task DependentShutdownCounterScenario()
        {
            _ = await RunDependentShutdownCycle(requireSafe: false);
            const int cycles = 100;
            int premature = 0, pending = 0, releases = 0, cancellations = 0, exits = 0, duplicates = 0, remaining = 0;
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                var result = await RunDependentShutdownCycle(requireSafe: false);
                premature += result.Premature;
                pending += result.Pending;
                releases += result.Releases;
                cancellations += result.Cancellations;
                exits += result.Exits;
                duplicates += result.Duplicates;
                remaining += result.Remaining;
            }
            Console.WriteLine($"PERFCOUNTER dependent-shutdown cycles {cycles}");
            Console.WriteLine($"PERFCOUNTER dependent-shutdown premature-cleanups {premature}");
            Console.WriteLine($"PERFCOUNTER dependent-shutdown pending-completions {pending}");
            Console.WriteLine($"PERFCOUNTER dependent-shutdown cleanup-attempts {releases}");
            Console.WriteLine($"PERFCOUNTER dependent-shutdown cancelled-jobs {cancellations}");
            Console.WriteLine($"PERFCOUNTER dependent-shutdown worker-exits {exits}");
            Console.WriteLine($"PERFCOUNTER dependent-shutdown duplicate-cleanups {duplicates}");
            Console.WriteLine($"PERFCOUNTER dependent-shutdown remaining-owners {remaining}");
        }

        private static async Task<(int Premature, int Pending, int Releases, int Cancellations, int Exits, int Duplicates, int Remaining)>
            RunDependentShutdownCycle(bool requireSafe)
        {
            using var disposed = new ManualResetEventSlim();
            var update = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releases = new int[3];
            int premature = 0;
            var animation = new AnimationThread(144, _ => 0, _ => 0);
            var worker = DependentWorker(animation);
            MainWindowLifetime lifetime = null!;
            lifetime = new MainWindowLifetime(release => release(animation.Dispose),
                () => animation.Completion,
                release =>
                {
                    for (int index = 0; index < releases.Length; index++)
                    {
                        int owner = index;
                        release(() =>
                        {
                            if (!animation.Completion.IsCompleted) { Interlocked.Increment(ref premature); }
                            Interlocked.Increment(ref releases[owner]);
                            lifetime.Dispose();
                        });
                    }
                });
            var completion = lifetime.Completion;
            var job = AnimationJob.Create((_, _) =>
            {
                lifetime.Dispose(); // The worker must never wait for itself.
                disposed.Set();
                return new ValueTask(update.Task);
            }, TimeSpan.FromHours(1));
            try
            {
                animation.Start(job);
                Assert.IsTrue(disposed.Wait(DependentGuard));
                Assert.IsFalse(animation.Completion.IsCompleted);
                Assert.IsFalse(job.Task.IsCompleted);
                int observedPremature = Volatile.Read(ref premature);
                int pending = completion.IsCompleted ? 0 : 1;
                Assert.AreSame(completion, lifetime.Completion);
                lifetime.Dispose();
                Assert.ThrowsException<ObjectDisposedException>(lifetime.CheckActive);
                if (requireSafe)
                {
                    Assert.AreEqual(0, observedPremature, "Entered Update still owns its tiling/workspace dependencies.");
                    Assert.AreEqual(1, pending, "Shutdown completion must include the deferred cleanup.");
                }
                update.SetResult();
                await animation.Completion.WaitAsync(DependentGuard);
                await completion.WaitAsync(DependentGuard);
                Assert.IsTrue(worker.Join(DependentGuard));
                Assert.IsTrue(job.Task.IsCanceled);
                CollectionAssert.AreEqual(new[] { 1, 1, 1 }, releases);
                lifetime.Dispose();
                CollectionAssert.AreEqual(new[] { 1, 1, 1 }, releases);
                return (observedPremature, pending, releases.Sum(), job.Task.IsCanceled ? 1 : 0,
                    worker.IsAlive ? 0 : 1, releases.Sum(count => Math.Max(0, count - 1)), releases.Count(count => count == 0));
            }
            finally
            {
                update.TrySetResult();
                animation.Dispose();
                Assert.IsTrue(worker.Join(DependentGuard));
                await lifetime.Completion.WaitAsync(DependentGuard);
            }
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task DeferredCleanupPreservesWorkerFailureAndAttemptsEveryDependentOwner(bool earlyFailure)
        {
            var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = new InvalidOperationException("head failed");
            var workerFailure = new ApplicationException("worker failed");
            var cleanupFailure = new ArgumentException("dependent cleanup failed");
            var calls = new List<string>();
            var lifetime = new MainWindowLifetime(release =>
            {
                release(() => { calls.Add("stop"); if (earlyFailure) { throw first; } });
            }, () => operation.Task, release =>
            {
                release(() => { calls.Add("tiling"); throw cleanupFailure; });
                release(() => calls.Add("workspace"));
            });
            if (earlyFailure) { Assert.AreSame(first, Assert.ThrowsException<InvalidOperationException>(lifetime.Dispose)); }
            else { lifetime.Dispose(); }
            CollectionAssert.AreEqual(new[] { "stop" }, calls);
            Assert.IsFalse(lifetime.Completion.IsCompleted);
            operation.SetException(workerFailure);
            var expected = earlyFailure ? (Exception)first : workerFailure;
            try { await lifetime.Completion.WaitAsync(DependentGuard); Assert.Fail("Failure must remain observable."); }
            catch (Exception error) when (ReferenceEquals(error, expected)) { }
            CollectionAssert.AreEqual(new[] { "stop", "tiling", "workspace" }, calls);
            var later = (AggregateException)expected.Data["MainWindowLifetime.CleanupExceptions"]!;
            CollectionAssert.AreEqual(earlyFailure ? new Exception[] { workerFailure, cleanupFailure } : [cleanupFailure],
                later.InnerExceptions.ToArray());
            lifetime.Dispose();
            Assert.AreEqual(3, calls.Count);
        }

        [TestMethod]
        public async Task CancelledOperationStillReleasesDependenciesAndReportsCancellation()
        {
            var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int releases = 0;
            var lifetime = new MainWindowLifetime(_ => { }, () => operation.Task,
                release => release(() => releases++));
            lifetime.Dispose();
            Assert.AreEqual(0, releases);
            operation.SetCanceled();
            await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => lifetime.Completion.WaitAsync(DependentGuard));
            Assert.AreEqual(1, releases);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task CombinedWorkerAndLayoutFailuresRemainDistinctAndWaitForBothOwners(bool aggregateWorkerError)
        {
            var worker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var layout = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Exception workerFailure = aggregateWorkerError
                ? new AggregateException(new InvalidOperationException("animation failed"))
                : new InvalidOperationException("animation failed");
            var layoutFailure = new ApplicationException("layout cancellation failed");
            var cleanupFailure = new ArgumentException("cleanup failed");
            int releases = 0;
            var lifetime = new MainWindowLifetime(_ => { }, () => Task.WhenAll(worker.Task, layout.Task), release =>
            {
                release(() => { releases++; throw cleanupFailure; });
            });
            lifetime.Dispose();
            worker.SetException(workerFailure);
            Assert.IsFalse(lifetime.Completion.IsCompleted);
            Assert.AreEqual(0, releases);
            layout.SetException(layoutFailure);
            Exception? observed = null;
            try { await lifetime.Completion.WaitAsync(DependentGuard); }
            catch (Exception error) { observed = error; }
            Assert.AreSame(workerFailure, observed);
            var secondary = (AggregateException)observed!.Data["MainWindowLifetime.CleanupExceptions"]!;
            CollectionAssert.AreEqual(new Exception[] { layoutFailure, cleanupFailure }, secondary.InnerExceptions.ToArray());
            Assert.AreEqual(1, releases);
        }

        [TestMethod]
        public async Task CompletedWorkerKeepsSynchronousCleanupAndFailureContract()
        {
            var error = new InvalidOperationException("dependent");
            int released = 0;
            var lifetime = new MainWindowLifetime(_ => { }, () => Task.CompletedTask, release =>
            {
                release(() => throw error);
                release(() => released++);
            });
            Assert.AreSame(error, Assert.ThrowsException<InvalidOperationException>(lifetime.Dispose));
            Assert.AreEqual(1, released);
            Assert.IsTrue(lifetime.Completion.IsCompleted);
            Assert.AreSame(error, await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => lifetime.Completion));
        }

        [TestMethod]
        public async Task ConcurrentDisposalSharesPendingCompletionAndAttemptsDeferredOwnersOnce()
        {
            var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int stops = 0, releases = 0;
            var lifetime = new MainWindowLifetime(release => release(() => Interlocked.Increment(ref stops)),
                () => operation.Task, release => release(() => Interlocked.Increment(ref releases)));
            var completion = lifetime.Completion;
            using var barrier = new Barrier(3);
            Task DisposeConcurrently() => Task.Run(() =>
            {
                Assert.IsTrue(barrier.SignalAndWait(DependentGuard));
                lifetime.Dispose();
            });
            var first = DisposeConcurrently();
            var second = DisposeConcurrently();
            try
            {
                Assert.IsTrue(barrier.SignalAndWait(DependentGuard));
                await Task.WhenAll(first, second).WaitAsync(DependentGuard);
                Assert.AreEqual(1, stops);
                Assert.AreEqual(0, releases);
                Assert.IsFalse(completion.IsCompleted);
                Assert.AreSame(completion, lifetime.Completion);
                operation.SetResult();
                await completion.WaitAsync(DependentGuard);
                Assert.AreEqual(1, releases);
            }
            finally
            {
                operation.TrySetResult();
                await Task.WhenAll(first, second).WaitAsync(DependentGuard);
                await completion.WaitAsync(DependentGuard);
            }
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task FailedDispatcherDoesNotReleaseDependenciesOnAnArbitraryThread(bool throwsSynchronously)
        {
            var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failure = new InvalidOperationException("owner dispatcher unavailable");
            int releases = 0, posts = 0;
            var lifetime = new MainWindowLifetime(_ => { }, () => operation.Task,
                release => release(() => releases++), _ =>
                {
                    posts++;
                    return throwsSynchronously ? throw failure : Task.FromException(failure);
                });
            lifetime.Dispose();
            Assert.AreEqual(0, posts);
            operation.SetResult();
            Assert.AreSame(failure, await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => lifetime.Completion.WaitAsync(DependentGuard)));
            Assert.AreEqual(1, posts);
            Assert.AreEqual(0, releases);
            lifetime.Dispose();
            Assert.AreEqual(1, posts);
        }

        [TestMethod]
        public async Task DependentCleanupIsDispatchedAfterWorkerCompletionOnOwningSta()
        {
            var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                ready.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            }) { IsBackground = true, Name = "DependentCleanup.STA" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            var dispatcher = await ready.Task.WaitAsync(DependentGuard);
            var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int releases = 0, posts = 0;
            var lifetime = new MainWindowLifetime(_ => { }, () => operation.Task,
                release => release(() =>
                {
                    Assert.IsTrue(dispatcher.CheckAccess());
                    Assert.IsTrue(operation.Task.IsCompleted);
                    releases++;
                }), cleanup =>
                {
                    Interlocked.Increment(ref posts);
                    return dispatcher.InvokeAsync(cleanup).Task;
                });
            try
            {
                lifetime.Dispose();
                Assert.AreEqual(0, posts, "A pending worker must not post dependent teardown yet.");
                operation.SetResult();
                await lifetime.Completion.WaitAsync(DependentGuard);
                Assert.AreEqual(1, releases);
                Assert.AreEqual(1, posts);
            }
            finally
            {
                operation.TrySetResult();
                try { await lifetime.Completion.WaitAsync(DependentGuard); }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                    Assert.IsTrue(thread.Join(DependentGuard));
                }
            }
        }

        private static Thread DependentWorker(AnimationThread animation)
            => (Thread)typeof(AnimationThread).GetField("m_thread", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(animation)!;
    }
}
