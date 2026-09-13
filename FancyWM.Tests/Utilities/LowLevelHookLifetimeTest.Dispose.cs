#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

using FancyWM.DllImports;
using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    public partial class LowLevelHookLifetimeTest
    {
        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task DisposeBeforeQueuePublicationReturnsAndSkipsAcquisition(bool mouse)
        {
            using var workerStarted = new ManualResetEventSlim();
            using var releasePublication = new ManualResetEventSlim();
            int acquisitions = 0;
            int posts = 0;
            using var owner = ManagedHookOwner.Start(mouse, (_, lifetime) =>
            {
                workerStarted.Set();
                releasePublication.Wait();
                if (lifetime.PublishQueue(101)) { Interlocked.Increment(ref acquisitions); }
            }, _ => { Interlocked.Increment(ref posts); return true; });

            Assert.IsTrue(workerStarted.Wait(TimeSpan.FromSeconds(5)));
            Task dispose = Task.Run(owner.Dispose);
            try
            {
                Assert.IsTrue(dispose.Wait(TimeSpan.FromMilliseconds(500)),
                    "Dispose must not spin or wait for startup publication.");
                Assert.AreEqual(0, posts);
            }
            finally
            {
                releasePublication.Set();
                await Observe(dispose);
                Join(owner);
            }

            await owner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, acquisitions);
            Assert.AreEqual(0, posts);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task QuitPostCannotOutlivePublishedThreadIdentity(bool mouse)
        {
            using var published = new ManualResetEventSlim();
            using var releaseWorker = new ManualResetEventSlim();
            using var terminalReached = new ManualResetEventSlim();
            using var postEntered = new ManualResetEventSlim();
            using var releasePost = new ManualResetEventSlim();
            using var owner = ManagedHookOwner.Start(mouse, (_, lifetime) =>
            {
                Assert.IsTrue(lifetime.PublishQueue(202));
                published.Set();
                releaseWorker.Wait();
                terminalReached.Set();
            }, threadId =>
            {
                Assert.AreEqual((uint)202, threadId);
                postEntered.Set();
                releasePost.Wait();
                return true;
            });

            Task? dispose = null;
            try
            {
                Assert.IsTrue(published.Wait(TimeSpan.FromSeconds(5)));
                dispose = Task.Run(owner.Dispose);
                Assert.IsTrue(postEntered.Wait(TimeSpan.FromSeconds(5)));
                releaseWorker.Set();
                Assert.IsTrue(terminalReached.Wait(TimeSpan.FromSeconds(5)));
                Assert.IsFalse(owner.WorkerThread.Join(TimeSpan.FromMilliseconds(500)),
                    "Worker exit must wait for the in-flight post lease.");
            }
            finally
            {
                releaseWorker.Set();
                releasePost.Set();
                if (dispose == null) { owner.Dispose(); }
                else { await Observe(dispose); }
                Join(owner);
            }
            await owner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }

        [DataTestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public async Task FailedQuitPostIsObservedByCompletion(bool mouse, bool throws)
        {
            using var published = new ManualResetEventSlim();
            using var releaseWorker = new ManualResetEventSlim();
            using var postEntered = new ManualResetEventSlim();
            var postFailure = new InvalidOperationException("post failed");
            int workAfterWatchdogWake = 0;
            using var owner = ManagedHookOwner.Start(mouse, (_, lifetime) =>
            {
                Assert.IsTrue(lifetime.PublishQueue(303));
                published.Set();
                releaseWorker.Wait();
                if (HookRegistrationPolicy.ShouldContinueMessageLoop(lifetime))
                {
                    Interlocked.Increment(ref workAfterWatchdogWake);
                }
            }, _ =>
            {
                postEntered.Set();
                return throws ? throw postFailure : false;
            });

            Assert.IsTrue(published.Wait(TimeSpan.FromSeconds(5)));
            Task dispose = Task.Run(owner.Dispose);
            Assert.IsTrue(postEntered.Wait(TimeSpan.FromSeconds(5)));
            releaseWorker.Set();
            Exception? disposeFailure = await CaptureAsync(dispose);
            Exception? completionFailure = await CaptureAsync(owner.Completion);
            Join(owner);

            Assert.IsNull(disposeFailure, "Dispose reports asynchronously through Completion.");
            Assert.IsNotNull(completionFailure);
            if (throws) { Assert.AreSame(postFailure, completionFailure); }
            else { StringAssert.Contains(completionFailure!.Message, "WM_QUIT"); }
            Assert.AreEqual(0, workAfterWatchdogWake,
                "The existing watchdog wake must not refresh or dispatch after stop.");
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ConcurrentDisposePostsAtMostOnce(bool mouse)
        {
            const int callers = 8;
            using var published = new ManualResetEventSlim();
            using var releaseWorker = new ManualResetEventSlim();
            using var postEntered = new ManualResetEventSlim();
            using var releasePost = new ManualResetEventSlim();
            using var ready = new CountdownEvent(callers);
            using var go = new ManualResetEventSlim();
            int posts = 0;
            int returned = 0;
            using var owner = ManagedHookOwner.Start(mouse, (_, lifetime) =>
            {
                Assert.IsTrue(lifetime.PublishQueue(404));
                published.Set();
                releaseWorker.Wait();
            }, _ =>
            {
                Interlocked.Increment(ref posts);
                postEntered.Set();
                releasePost.Wait();
                return true;
            });

            Assert.IsTrue(published.Wait(TimeSpan.FromSeconds(5)));
            Task[] disposals = new Task[callers];
            for (int i = 0; i < disposals.Length; i++)
            {
                disposals[i] = Task.Run(() =>
                {
                    ready.Signal();
                    go.Wait();
                    owner.Dispose();
                    Interlocked.Increment(ref returned);
                });
            }
            Assert.IsTrue(ready.Wait(TimeSpan.FromSeconds(5)));
            go.Set();
            Assert.IsTrue(postEntered.Wait(TimeSpan.FromSeconds(5)));
            try
            {
                Assert.IsTrue(SpinWait.SpinUntil(
                    () => Volatile.Read(ref posts) > 1 || Volatile.Read(ref returned) >= callers - 1,
                    TimeSpan.FromSeconds(5)));
                Assert.AreEqual(1, posts);
            }
            finally
            {
                releasePost.Set();
                releaseWorker.Set();
                await Observe(Task.WhenAll(disposals));
                Join(owner);
            }
            await owner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(callers, returned);
            Assert.AreEqual(1, posts);
            int lateCallbacks = 0;
            owner.Subscribe(() => Interlocked.Increment(ref lateCallbacks));
            Assert.IsFalse(owner.Dispatch(handled: true));
            Assert.AreEqual(0, lateCallbacks,
                "Every returned Dispose caller must observe callback admission closed.");
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task EnteredCallbackDelaysCompletionAndLateCallbackIsRejected(bool mouse)
        {
            using var published = new ManualResetEventSlim();
            using var releaseWorker = new ManualResetEventSlim();
            using var callbackEntered = new ManualResetEventSlim();
            using var releaseCallback = new ManualResetEventSlim();
            using var postEntered = new ManualResetEventSlim();
            int callbacks = 0;
            using var owner = ManagedHookOwner.Start(mouse, (_, lifetime) =>
            {
                Assert.IsTrue(lifetime.PublishQueue(505));
                published.Set();
                releaseWorker.Wait();
            }, _ => { postEntered.Set(); return true; });
            owner.Subscribe(() =>
            {
                Interlocked.Increment(ref callbacks);
                callbackEntered.Set();
                releaseCallback.Wait();
            });

            Assert.IsTrue(published.Wait(TimeSpan.FromSeconds(5)));
            Task<bool> entered = Task.Run(() => owner.Dispatch(handled: true));
            Assert.IsTrue(callbackEntered.Wait(TimeSpan.FromSeconds(5)));
            Task dispose = Task.Run(owner.Dispose);
            Assert.IsTrue(postEntered.Wait(TimeSpan.FromSeconds(5)));
            releaseWorker.Set();
            try
            {
                Join(owner);
                Assert.IsFalse(owner.Completion.IsCompleted,
                    "Completion cannot overtake an admitted callback.");
            }
            finally
            {
                releaseCallback.Set();
                Assert.IsFalse(await entered.WaitAsync(TimeSpan.FromSeconds(5)),
                    "A callback which observes Dispose must not suppress input afterward.");
                await Observe(dispose);
            }
            await owner.Completion.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(1, callbacks);
            Assert.IsFalse(owner.Dispatch(handled: true));
            Assert.AreEqual(1, callbacks, "No subscriber may be admitted after stop.");
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task SubscriberCanDisposeOwnerReentrantly(bool mouse)
        {
            using var published = new ManualResetEventSlim();
            using var dispatchNow = new ManualResetEventSlim();
            ManagedHookOwner? owner = null;
            int posts = 0;
            bool? suppressed = null;
            owner = ManagedHookOwner.Start(mouse, (rawOwner, lifetime) =>
            {
                Assert.IsTrue(lifetime.PublishQueue(606));
                published.Set();
                dispatchNow.Wait();
                suppressed = ManagedHookOwner.Dispatch(rawOwner, handled: true);
            }, _ => { Interlocked.Increment(ref posts); return true; });
            using (owner)
            {
                owner.Subscribe(owner.Dispose);
                Assert.IsTrue(published.Wait(TimeSpan.FromSeconds(5)));
                dispatchNow.Set();
                Exception? failure = await CaptureAsync(owner.Completion);
                Join(owner);
                Assert.IsNull(failure);
                Assert.AreEqual(1, posts);
                Assert.AreEqual(false, suppressed);
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task StartupFailureBeforePublicationCompletesWithoutDisposeWait(bool mouse)
        {
            var primary = new InvalidOperationException("queue creation failed");
            using var owner = ManagedHookOwner.Start(mouse, (_, _) => throw primary, _ =>
                throw new AssertFailedException("No post is valid without publication."));

            Exception? failure = await CaptureAsync(owner.Completion);
            Assert.AreSame(primary, failure);
            owner.Dispose();
            Join(owner);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void ThreadStartFailurePreservesConstructionError(bool mouse)
        {
            var primary = new InvalidOperationException("start failed");
            Exception? failure = Capture(() => ManagedHookOwner.Start(mouse, (_, _) => { },
                _ => throw new AssertFailedException("No post is valid for an unstarted thread."),
                _ => throw primary));
            Assert.AreSame(primary, failure);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task FinalizerStopRequestNeverWaitsForPublication(bool mouse)
        {
            using var workerStarted = new ManualResetEventSlim();
            using var releasePublication = new ManualResetEventSlim();
            int posts = 0;
            using var owner = ManagedHookOwner.Start(mouse, (_, lifetime) =>
            {
                workerStarted.Set();
                releasePublication.Wait();
                _ = lifetime.PublishQueue(707);
            }, _ => { Interlocked.Increment(ref posts); return true; });

            Assert.IsTrue(workerStarted.Wait(TimeSpan.FromSeconds(5)));
            Task finalizerRequest = Task.Run(owner.RequestFinalizerStop);
            try
            {
                Assert.IsTrue(finalizerRequest.Wait(TimeSpan.FromMilliseconds(500)));
                Assert.AreEqual(0, posts);
            }
            finally
            {
                releasePublication.Set();
                await Observe(finalizerRequest);
                Join(owner);
            }
            await owner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, posts);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task OneHundredOwnersLeaveNoManagedWorkers(bool mouse)
        {
            int posts = 0;
            for (int cycle = 1; cycle <= 100; cycle++)
            {
                using var published = new ManualResetEventSlim();
                using var releaseWorker = new ManualResetEventSlim();
                uint threadId = (uint)(800 + cycle);
                using var owner = ManagedHookOwner.Start(mouse, (_, lifetime) =>
                {
                    Assert.IsTrue(lifetime.PublishQueue(threadId));
                    published.Set();
                    releaseWorker.Wait();
                }, observed =>
                {
                    Assert.AreEqual(threadId, observed);
                    Interlocked.Increment(ref posts);
                    releaseWorker.Set();
                    return true;
                });
                Assert.IsTrue(published.Wait(TimeSpan.FromSeconds(5)));
                owner.Dispose();
                await owner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Join(owner);
                Assert.IsFalse(owner.WorkerThread.IsAlive);
            }
            Assert.AreEqual(100, posts);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task DisposeDuringLateAcquisitionReleasesEveryAcquiredOwner(bool mouse)
        {
            using var installEntered = new ManualResetEventSlim();
            using var releaseInstall = new ManualResetEventSlim();
            int installs = 0, timers = 0, timerReleases = 0, hookReleases = 0, posts = 0;
            using var owner = ManagedHookOwner.Start(mouse, (_, lifetime) =>
            {
                Assert.IsTrue(lifetime.PublishQueue(1001));
                HHOOK current = default;
                Run(mouse, ref current,
                    () =>
                    {
                        Interlocked.Increment(ref installs);
                        installEntered.Set();
                        releaseInstall.Wait();
                        return Handle(1002);
                    },
                    () => { Interlocked.Increment(ref timers); return 1003; },
                    (ref HHOOK _) => { },
                    _ => { Interlocked.Increment(ref timerReleases); return true; },
                    _ => { Interlocked.Increment(ref hookReleases); return true; });
            }, _ => { Interlocked.Increment(ref posts); return true; });

            try
            {
                Assert.IsTrue(installEntered.Wait(TimeSpan.FromSeconds(5)));
                owner.Dispose();
            }
            finally
            {
                releaseInstall.Set();
                owner.Dispose();
                await owner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Join(owner);
            }
            Assert.AreEqual(1, installs);
            Assert.AreEqual(1, timers);
            Assert.AreEqual(1, timerReleases);
            Assert.AreEqual(1, hookReleases);
            Assert.AreEqual(1, posts);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task TimedOutWaitKeepsOriginalCompletionAndDoesNotRestartOwner(bool mouse)
        {
            using var published = new ManualResetEventSlim();
            using var releaseWorker = new ManualResetEventSlim();
            int posts = 0;
            using var owner = ManagedHookOwner.Start(mouse, (_, lifetime) =>
            {
                Assert.IsTrue(lifetime.PublishQueue(1101));
                published.Set();
                releaseWorker.Wait();
            }, _ => { Interlocked.Increment(ref posts); return true; });

            try
            {
                Assert.IsTrue(published.Wait(TimeSpan.FromSeconds(5)));
                owner.Dispose();
                owner.Dispose();
                await Assert.ThrowsExceptionAsync<TimeoutException>(
                    () => owner.Completion.WaitAsync(TimeSpan.Zero));
                Assert.AreEqual(1, posts);
                Assert.IsTrue(owner.WorkerThread.IsAlive);
            }
            finally
            {
                releaseWorker.Set();
                owner.Dispose();
                await owner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Join(owner);
            }
            Assert.AreEqual(1, posts);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task WorkerFailureRemainsPrimaryWhenQuitPostAlsoFails(bool mouse)
        {
            using var published = new ManualResetEventSlim();
            using var releaseWorker = new ManualResetEventSlim();
            var workerFailure = new InvalidOperationException("worker failed");
            var postFailure = new ApplicationException("post failed");
            using var owner = ManagedHookOwner.Start(mouse, (_, lifetime) =>
            {
                Assert.IsTrue(lifetime.PublishQueue(1201));
                published.Set();
                releaseWorker.Wait();
                throw workerFailure;
            }, _ => throw postFailure);

            Assert.IsTrue(published.Wait(TimeSpan.FromSeconds(5)));
            owner.Dispose();
            releaseWorker.Set();
            Exception? observed = await CaptureAsync(owner.Completion);
            Join(owner);
            Assert.AreSame(workerFailure, observed);
            var cleanup = observed!.Data[HookRegistrationPolicy.CleanupExceptionsDataKey]
                as AggregateException ?? throw new AssertFailedException("Post failure was not retained.");
            Assert.AreEqual(1, cleanup.InnerExceptions.Count);
            Assert.AreSame(postFailure, cleanup.InnerExceptions[0]);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task NormalCallbackPreservesHandledAndUnhandledResults(bool mouse)
        {
            using var published = new ManualResetEventSlim();
            using var releaseWorker = new ManualResetEventSlim();
            int callbacks = 0;
            using var owner = ManagedHookOwner.Start(mouse, (_, lifetime) =>
            {
                Assert.IsTrue(lifetime.PublishQueue(1301));
                published.Set();
                releaseWorker.Wait();
            }, _ => { releaseWorker.Set(); return true; });
            owner.Subscribe(() => Interlocked.Increment(ref callbacks));

            Assert.IsTrue(published.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(owner.Dispatch(handled: false));
            Assert.IsTrue(owner.Dispatch(handled: true));
            Assert.AreEqual(2, callbacks);
            owner.Dispose();
            await owner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Join(owner);
        }

        [TestMethod]
        public async Task HookDisposalCounterScenario()
        {
            await MeasureDisposalOwner(mouse: false, "keyboard");
            await MeasureDisposalOwner(mouse: true, "mouse");
        }

        private static async Task MeasureDisposalOwner(bool mouse, string ownerName)
        {
            const int cycles = 100;
            int quitPostAttempts = 0;
            int observedPostFailures = 0;
            int completionSuccesses = 0;
            int lateSubscriberCallbacks = 0;
            int suppressedLateInput = 0;
            int completedWorkers = 0;
            int liveWorkersAfterJoin = 0;

            for (int cycle = 1; cycle <= cycles; cycle++)
            {
                using var published = new ManualResetEventSlim();
                using var releaseWorker = new ManualResetEventSlim();
                using var hook = ManagedHookOwner.Start(mouse, (_, lifetime) =>
                {
                    Assert.IsTrue(lifetime.PublishQueue((uint)(2000 + cycle)));
                    published.Set();
                    releaseWorker.Wait();
                }, _ =>
                {
                    quitPostAttempts++;
                    releaseWorker.Set();
                    return false;
                });
                hook.Subscribe(() => lateSubscriberCallbacks++);
                Assert.IsTrue(published.Wait(TimeSpan.FromSeconds(5)));
                hook.Dispose();
                hook.Dispose();
                Exception? failure = await CaptureAsync(hook.Completion);
                if (failure == null) { completionSuccesses++; }
                else if (failure.Message.Contains("WM_QUIT", StringComparison.Ordinal))
                {
                    observedPostFailures++;
                }
                else { throw failure; }
                if (hook.Dispatch(handled: true)) { suppressedLateInput++; }
                if (hook.WorkerThread.Join(TimeSpan.FromSeconds(5))) { completedWorkers++; }
                if (hook.WorkerThread.IsAlive) { liveWorkersAfterJoin++; }
            }

            Assert.AreEqual(cycles, quitPostAttempts);
            Assert.IsTrue(observedPostFailures == 0 || observedPostFailures == cycles);
            Assert.AreEqual(cycles - observedPostFailures, completionSuccesses);
            Assert.IsTrue(lateSubscriberCallbacks == 0 || lateSubscriberCallbacks == cycles);
            Assert.AreEqual(0, suppressedLateInput);
            Assert.AreEqual(cycles, completedWorkers);
            Assert.AreEqual(0, liveWorkersAfterJoin);

            DisposalCounter(ownerName, "cycles", cycles);
            DisposalCounter(ownerName, "quit-post-attempts", quitPostAttempts);
            DisposalCounter(ownerName, "observed-post-failures", observedPostFailures);
            DisposalCounter(ownerName, "completion-successes", completionSuccesses);
            DisposalCounter(ownerName, "late-subscriber-callbacks", lateSubscriberCallbacks);
            DisposalCounter(ownerName, "suppressed-late-input", suppressedLateInput);
            DisposalCounter(ownerName, "completed-workers", completedWorkers);
            DisposalCounter(ownerName, "live-workers-after-join", liveWorkersAfterJoin);
        }

        private static void DisposalCounter(string owner, string metric, int value)
            => Console.WriteLine($"PERFCOUNTER hook-disposal-{owner} {metric} {value}");

        private static async Task<Exception?> CaptureAsync(Task task)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                return null;
            }
            catch (Exception error)
            {
                return error;
            }
        }

        private static async Task Observe(Task task)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch when (task.IsCompleted) { }
        }

        private static void Join(ManagedHookOwner owner)
            => Assert.IsTrue(owner.WorkerThread.Join(TimeSpan.FromSeconds(5)),
                "The controlled hook worker did not terminate.");

        private sealed class ManagedHookOwner : IDisposable
        {
            private readonly LowLevelKeyboardHook? m_keyboard;
            private readonly LowLevelMouseHook? m_mouse;

            private ManagedHookOwner(LowLevelKeyboardHook keyboard) => m_keyboard = keyboard;

            private ManagedHookOwner(LowLevelMouseHook mouse) => m_mouse = mouse;

            internal Task Completion => m_keyboard?.Completion ?? m_mouse!.Completion;

            internal Thread WorkerThread => m_keyboard?.WorkerThread ?? m_mouse!.WorkerThread;

            internal static ManagedHookOwner Start(bool mouse,
                Action<object, HookRegistrationPolicy.ThreadLifetime> worker,
                Func<uint, bool> postQuit,
                Action<Thread>? startThread = null)
            {
                if (mouse)
                {
                    return new(new LowLevelMouseHook(
                        (owner, lifetime) => worker(owner, lifetime), postQuit, startThread));
                }
                return new(new LowLevelKeyboardHook(
                    (owner, lifetime) => worker(owner, lifetime), postQuit, startThread));
            }

            internal void Subscribe(Action callback)
            {
                if (m_mouse != null)
                {
                    m_mouse.ButtonStateChanged += Handler;
                    void Handler(object? sender, ref LowLevelMouseHook.ButtonStateChangedEventArgs e)
                        => callback();
                }
                else
                {
                    m_keyboard!.KeyStateChanged += Handler;
                    void Handler(object? sender, ref LowLevelKeyboardHook.KeyStateChangedEventArgs e)
                        => callback();
                }
            }

            internal bool Dispatch(bool handled) => Dispatch(m_mouse ?? (object)m_keyboard!, handled);

            internal static bool Dispatch(object owner, bool handled)
            {
                if (owner is LowLevelMouseHook mouse)
                {
                    var e = new LowLevelMouseHook.ButtonStateChangedEventArgs(
                        LowLevelMouseHook.MouseButton.Left, true, 10, 20) { Handled = handled };
                    return mouse.DispatchButtonStateChanged(ref e);
                }
                var keyboard = (LowLevelKeyboardHook)owner;
                var key = new LowLevelKeyboardHook.KeyStateChangedEventArgs(
                    KeyCode.A, true) { Handled = handled };
                return keyboard.DispatchKeyStateChanged(ref key);
            }

            internal void RequestFinalizerStop()
            {
                if (m_mouse != null) { m_mouse.RequestFinalizerStopForTest(); }
                else { m_keyboard!.RequestFinalizerStopForTest(); }
            }

            public void Dispose()
            {
                if (m_mouse != null) { m_mouse.Dispose(); }
                else { m_keyboard!.Dispose(); }
            }
        }
    }
}
