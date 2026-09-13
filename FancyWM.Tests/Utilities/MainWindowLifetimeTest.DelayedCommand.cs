#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    public partial class MainWindowLifetimeTest
    {
        private static readonly TimeSpan DelayedCommandGuard = TimeSpan.FromSeconds(10);

        [TestMethod]
        public async Task DelayedCommandDisposedDuringAcceptedDelayDoesNotRunAction()
        {
            var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int actions = 0;
            var lifetime = new MainWindowLifetime(_ => { });
            var running = lifetime.RunDelayedIfActiveAsync(_ => delay.Task, CancellationToken.None, () =>
            {
                actions++;
                return Task.CompletedTask;
            });

            Assert.IsFalse(running.IsCompleted);
            lifetime.Dispose();
            delay.SetResult();
            await running.WaitAsync(DelayedCommandGuard);

            Assert.AreEqual(0, actions);
        }

        [TestMethod]
        public async Task DelayedCommandShutdownDuringAcceptedDelayDoesNotRunAction()
        {
            using var shutdown = new CancellationTokenSource();
            var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int actions = 0;
            var lifetime = new MainWindowLifetime(_ => { });
            var running = lifetime.RunDelayedIfActiveAsync(_ => delay.Task, shutdown.Token, () =>
            {
                actions++;
                return Task.CompletedTask;
            });

            shutdown.Cancel();
            delay.SetResult();
            await running.WaitAsync(DelayedCommandGuard);

            Assert.AreEqual(0, actions);
            lifetime.Dispose();
        }

        [TestMethod]
        public async Task DelayedCommandAlreadyShuttingDownSkipsDelayAndAction()
        {
            using var shutdown = new CancellationTokenSource();
            shutdown.Cancel();
            int delays = 0, actions = 0;
            var lifetime = new MainWindowLifetime(_ => { });

            await lifetime.RunDelayedIfActiveAsync(_ =>
            {
                delays++;
                return Task.CompletedTask;
            }, shutdown.Token, () =>
            {
                actions++;
                return Task.CompletedTask;
            }).WaitAsync(DelayedCommandGuard);

            Assert.AreEqual(0, delays);
            Assert.AreEqual(0, actions);
            lifetime.Dispose();
        }

        [TestMethod]
        public async Task DelayedCommandCancellationReachesDelayAndSkipsAction()
        {
            using var shutdown = new CancellationTokenSource();
            var fallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken observed = default;
            int actions = 0;
            var lifetime = new MainWindowLifetime(_ => { });
            var running = lifetime.RunDelayedIfActiveAsync(token =>
            {
                observed = token;
                return token.CanBeCanceled
                    ? Task.Delay(Timeout.InfiniteTimeSpan, token)
                    : fallback.Task;
            }, shutdown.Token, () =>
            {
                actions++;
                return Task.CompletedTask;
            });

            shutdown.Cancel();
            fallback.TrySetResult();
            await running.WaitAsync(DelayedCommandGuard);

            Assert.AreEqual(shutdown.Token, observed);
            Assert.AreEqual(0, actions);
            lifetime.Dispose();
        }

        [TestMethod]
        public async Task DelayedCommandQueuedContinuationRechecksShutdownBeforeAction()
        {
            using var shutdown = new CancellationTokenSource();
            var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var context = new QueuedSynchronizationContext();
            var previous = SynchronizationContext.Current;
            int actions = 0;
            var lifetime = new MainWindowLifetime(_ => { });
            Task running;
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                running = lifetime.RunDelayedIfActiveAsync(_ => delay.Task, shutdown.Token, () =>
                {
                    actions++;
                    return Task.CompletedTask;
                });
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }

            delay.SetResult();
            await context.WaitForPostAsync(DelayedCommandGuard);
            lifetime.Dispose();
            shutdown.Cancel();
            context.RunOne();
            await running.WaitAsync(DelayedCommandGuard);

            Assert.AreEqual(0, actions);
            Assert.AreEqual(0, context.Count);
        }

        [TestMethod]
        public async Task DelayedCommandDisposedBeforeAdmissionSkipsDelayAndAction()
        {
            int delays = 0, actions = 0;
            var lifetime = new MainWindowLifetime(_ => { });
            lifetime.Dispose();

            await lifetime.RunDelayedIfActiveAsync(_ =>
            {
                delays++;
                return Task.CompletedTask;
            }, CancellationToken.None, () =>
            {
                actions++;
                return Task.CompletedTask;
            }).WaitAsync(DelayedCommandGuard);

            Assert.AreEqual(0, delays);
            Assert.AreEqual(0, actions);
        }

        [TestMethod]
        public async Task DelayedCommandNormalPathRunsEachStageOnceAndAwaitsAction()
        {
            using var shutdown = new CancellationTokenSource();
            var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var action = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var actionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int delays = 0, actions = 0;
            var lifetime = new MainWindowLifetime(_ => { });
            var running = lifetime.RunDelayedIfActiveAsync(_ =>
            {
                delays++;
                return delay.Task;
            }, shutdown.Token, () =>
            {
                actions++;
                actionStarted.TrySetResult();
                return action.Task;
            });

            delay.SetResult();
            await actionStarted.Task.WaitAsync(DelayedCommandGuard);
            Assert.IsFalse(running.IsCompleted);
            Assert.AreEqual(1, delays);
            Assert.AreEqual(1, actions);
            action.SetResult();
            await running.WaitAsync(DelayedCommandGuard);
            lifetime.Dispose();
        }

        [TestMethod]
        public async Task DelayedCommandPreservesSynchronousDelayFailure()
        {
            var failure = new InvalidOperationException("delay failed synchronously");
            var lifetime = new MainWindowLifetime(_ => { });
            var running = lifetime.RunDelayedIfActiveAsync(_ => throw failure,
                CancellationToken.None, static () => Task.CompletedTask);

            var observed = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => running);
            Assert.AreSame(failure, observed);
            lifetime.Dispose();
        }

        [TestMethod]
        public async Task DelayedCommandPreservesAsynchronousDelayFailure()
        {
            var failure = new ApplicationException("delay failed asynchronously");
            var lifetime = new MainWindowLifetime(_ => { });
            var running = lifetime.RunDelayedIfActiveAsync(
                _ => Task.FromException(failure), CancellationToken.None,
                static () => Task.CompletedTask);

            var observed = await Assert.ThrowsExceptionAsync<ApplicationException>(() => running);
            Assert.AreSame(failure, observed);
            lifetime.Dispose();
        }

        [TestMethod]
        public async Task DelayedCommandPreservesSynchronousActionFailure()
        {
            var failure = new InvalidOperationException("action failed synchronously");
            var lifetime = new MainWindowLifetime(_ => { });
            var running = lifetime.RunDelayedIfActiveAsync(
                static _ => Task.CompletedTask, CancellationToken.None,
                () => throw failure);

            var observed = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => running);
            Assert.AreSame(failure, observed);
            lifetime.Dispose();
        }

        [TestMethod]
        public async Task DelayedCommandPreservesAsynchronousActionFailure()
        {
            var failure = new ApplicationException("action failed asynchronously");
            var lifetime = new MainWindowLifetime(_ => { });
            var running = lifetime.RunDelayedIfActiveAsync(
                static _ => Task.CompletedTask, CancellationToken.None,
                () => Task.FromException(failure));

            var observed = await Assert.ThrowsExceptionAsync<ApplicationException>(() => running);
            Assert.AreSame(failure, observed);
            lifetime.Dispose();
        }

        [TestMethod]
        public async Task DelayedCommandDoesNotMaskForeignCancellationWhenShutdownRaces()
        {
            using var shutdown = new CancellationTokenSource();
            using var foreign = new CancellationTokenSource();
            var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failure = new OperationCanceledException("foreign cancellation", foreign.Token);
            int actions = 0;
            var lifetime = new MainWindowLifetime(_ => { });
            var running = lifetime.RunDelayedIfActiveAsync(_ => delay.Task, shutdown.Token, () =>
            {
                actions++;
                return Task.CompletedTask;
            });

            shutdown.Cancel();
            delay.SetException(failure);
            var observed = await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => running);

            Assert.AreSame(failure, observed);
            Assert.AreEqual(0, actions);
            lifetime.Dispose();
        }

        [TestMethod]
        public async Task DelayedCommandPreservesCapturedDispatcherAffinity()
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher? dispatcher = null;
            var thread = new Thread(() =>
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                dispatcher.BeginInvoke(new Action(async () =>
                {
                    var lifetime = new MainWindowLifetime(_ => { });
                    try
                    {
                        int expectedThread = Environment.CurrentManagedThreadId;
                        int actionThread = -1;
                        var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        var running = lifetime.RunDelayedIfActiveAsync(_ => delay.Task,
                            CancellationToken.None, () =>
                            {
                                actionThread = Environment.CurrentManagedThreadId;
                                return Task.CompletedTask;
                            });
                        ThreadPool.QueueUserWorkItem(_ => delay.TrySetResult());
                        await running.WaitAsync(DelayedCommandGuard);
                        Assert.AreEqual(expectedThread, actionThread);
                        completed.TrySetResult();
                    }
                    catch (Exception error)
                    {
                        completed.TrySetException(error);
                    }
                    finally
                    {
                        lifetime.Dispose();
                        dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                    }
                }));
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "Delayed command Dispatcher affinity"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            try
            {
                await completed.Task.WaitAsync(DelayedCommandGuard);
            }
            finally
            {
                dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
                Assert.IsTrue(thread.Join(DelayedCommandGuard), "Owned Dispatcher thread did not stop.");
            }
        }

        [TestMethod]
        public async Task DelayedCommandLifetimeCounterScenario()
        {
            const int cycles = 100;
            int delayAttempts = 0, activeActions = 0, lateActions = 0;
            int cancellations = 0, completedRuns = 0;
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                var active = new MainWindowLifetime(_ => { });
                await active.RunDelayedIfActiveAsync(_ =>
                {
                    delayAttempts++;
                    return Task.CompletedTask;
                }, CancellationToken.None, () =>
                {
                    activeActions++;
                    return Task.CompletedTask;
                }).WaitAsync(DelayedCommandGuard);
                completedRuns++;
                active.Dispose();

                using var shutdown = new CancellationTokenSource();
                var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var closing = new MainWindowLifetime(_ => { });
                var running = closing.RunDelayedIfActiveAsync(_ =>
                {
                    delayAttempts++;
                    return delay.Task;
                }, shutdown.Token, () =>
                {
                    lateActions++;
                    return Task.CompletedTask;
                });
                closing.Dispose();
                shutdown.Cancel();
                cancellations++;
                delay.SetResult();
                await running.WaitAsync(DelayedCommandGuard);
                completedRuns++;
            }

            Assert.AreEqual(cycles * 2, delayAttempts);
            Assert.AreEqual(cycles, activeActions);
            Assert.IsTrue(lateActions == 0 || lateActions == cycles,
                "A comparison variant must reject every late action or preserve the old behavior for every cycle.");
            Assert.AreEqual(cycles, cancellations);
            Assert.AreEqual(cycles * 2, completedRuns);
            Console.WriteLine($"PERFCOUNTER delayed-command-lifetime cycles {cycles}");
            Console.WriteLine($"PERFCOUNTER delayed-command-lifetime delay-attempts {delayAttempts}");
            Console.WriteLine($"PERFCOUNTER delayed-command-lifetime active-actions {activeActions}");
            Console.WriteLine($"PERFCOUNTER delayed-command-lifetime late-actions {lateActions}");
            Console.WriteLine($"PERFCOUNTER delayed-command-lifetime cancellations {cancellations}");
            Console.WriteLine($"PERFCOUNTER delayed-command-lifetime completed-runs {completedRuns}");
        }

        private sealed class QueuedSynchronizationContext : SynchronizationContext
        {
            private readonly object m_gate = new();
            private readonly Queue<(SendOrPostCallback Callback, object? State)> m_callbacks = new();
            private readonly SemaphoreSlim m_posted = new(0);

            internal int Count
            {
                get { lock (m_gate) { return m_callbacks.Count; } }
            }

            public override void Post(SendOrPostCallback callback, object? state)
            {
                lock (m_gate) { m_callbacks.Enqueue((callback, state)); }
                m_posted.Release();
            }

            internal async Task WaitForPostAsync(TimeSpan timeout)
            {
                Assert.IsTrue(await m_posted.WaitAsync(timeout), "Delayed continuation was not queued.");
            }

            internal void RunOne()
            {
                (SendOrPostCallback Callback, object? State) work;
                lock (m_gate) { work = m_callbacks.Dequeue(); }
                work.Callback(work.State);
            }
        }
    }
}
