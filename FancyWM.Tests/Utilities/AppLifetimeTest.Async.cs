#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    public partial class AppLifetimeTest
    {
        [TestMethod]
        public async Task NormalTerminationKeepsWindowsMouseAndDispatcherAliveUntilAnimationCompletion()
        {
            var completion = NewCompletion();
            var calls = new List<string>();
            var window = new PendingWindow("main", calls, completion.Task);
            var terminating = TerminateAsync([window], calls);
            try
            {
                Assert.IsFalse(terminating.IsCompleted, "Normal termination must keep its Dispatcher available for dependent cleanup.");
                CollectionAssert.AreEqual(new[] { "dispose:main", "completion:main" }, calls);
            }
            finally
            {
                completion.TrySetResult();
                await terminating.WaitAsync(TimeSpan.FromSeconds(10));
            }
            CollectionAssert.AreEqual(new[]
            {
                "dispose:main", "completion:main", "mouse", "close:main", "shutdown"
            }, calls);
        }

        [TestMethod]
        public async Task AllWindowDisposalsAreAdmittedBeforeWaitingForEveryCompletion()
        {
            var first = NewCompletion();
            var second = NewCompletion();
            var calls = new List<string>();
            var windows = new[] { new PendingWindow("a", calls, first.Task), new PendingWindow("b", calls, second.Task) };
            var closing = CloseAsync(windows, calls);
            try
            {
                CollectionAssert.AreEqual(new[] { "dispose:a", "completion:a", "dispose:b", "completion:b" }, calls);
                Assert.IsFalse(closing.IsCompleted);
                first.SetResult();
                Assert.IsFalse(closing.IsCompleted, "A second owned animation must retain its resources independently.");
            }
            finally
            {
                first.TrySetResult();
                second.TrySetResult();
                await closing.WaitAsync(TimeSpan.FromSeconds(10));
            }
            CollectionAssert.AreEqual(new[]
            {
                "dispose:a", "completion:a", "dispose:b", "completion:b", "mouse", "close:a", "close:b"
            }, calls);
        }

        [TestMethod]
        public async Task AsyncDisposalAndCompletionFailuresPreserveIdentityAndContinueEveryOwner()
        {
            var firstFailure = new InvalidOperationException("initial disposal");
            var workerFailure = new ApplicationException("animation worker");
            var closeFailure = new ArgumentException("close");
            var mouseFailure = new ApplicationException("mouse");
            var calls = new List<string>();
            var firstTask = Task.FromException(firstFailure);
            var secondTask = Task.FromException(workerFailure);
            var windows = new[]
            {
                new PendingWindow("a", calls, firstTask)
                {
                    OnDispose = () => throw firstFailure,
                    OnClose = () => throw closeFailure,
                },
                new PendingWindow("b", calls, secondTask),
            };
            try
            {
                var error = await Assert.ThrowsExceptionAsync<AggregateException>(() => App.CloseOwnedWindowsAsync(
                    () => windows, window => window.Dispose(), window => window.Completion(), window => window.Close(),
                    DispatchImmediately, () => { calls.Add("mouse"); throw mouseFailure; }));
                CollectionAssert.AreEqual(new Exception[] { firstFailure, workerFailure, mouseFailure, closeFailure }, error.InnerExceptions.ToArray());
                CollectionAssert.AreEqual(new[]
                {
                    "dispose:a", "completion:a", "dispose:b", "completion:b", "mouse", "close:a", "close:b"
                }, calls);
            }
            finally { _ = firstTask.Exception; _ = secondTask.Exception; }
        }

        [TestMethod]
        public async Task FaultedEarlierCompletionStillWaitsForPendingLaterOwner()
        {
            var failure = new InvalidOperationException("first worker");
            var failed = Task.FromException(failure);
            var pending = NewCompletion();
            var calls = new List<string>();
            var closing = CloseAsync([new PendingWindow("a", calls, failed), new PendingWindow("b", calls, pending.Task)], calls);
            try
            {
                Assert.IsFalse(closing.IsCompleted, "An earlier worker failure does not release a later active owner's resources.");
                Assert.IsFalse(calls.Any(call => call.StartsWith("close:", StringComparison.Ordinal)));
            }
            finally
            {
                pending.TrySetResult();
                try { await closing.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (AggregateException) { }
                _ = failed.Exception;
            }
            var error = await Assert.ThrowsExceptionAsync<AggregateException>(() => closing);
            CollectionAssert.AreEqual(new Exception[] { failure }, error.InnerExceptions.ToArray());
            Assert.AreEqual(1, calls.Count(call => call == "close:a"));
            Assert.AreEqual(1, calls.Count(call => call == "close:b"));
            Assert.AreEqual(1, calls.Count(call => call == "mouse"));
        }

        [TestMethod]
        public async Task CompletionGetterFailureDoesNotSkipLaterWindowOrMouseCleanup()
        {
            var failure = new ApplicationException("completion getter");
            var calls = new List<string>();
            var windows = new[]
            {
                new PendingWindow("a", calls, Task.CompletedTask) { GetCompletion = () => throw failure },
                new PendingWindow("b", calls, Task.CompletedTask),
            };
            var error = await Assert.ThrowsExceptionAsync<AggregateException>(() => CloseAsync(windows, calls));
            CollectionAssert.AreEqual(new Exception[] { failure }, error.InnerExceptions.ToArray());
            CollectionAssert.AreEqual(new[]
            {
                "dispose:a", "completion:a", "dispose:b", "completion:b", "mouse", "close:a", "close:b"
            }, calls);
        }

        [TestMethod]
        public async Task AsyncAlreadyClosingSuppressionDoesNotSuppressWorkerFailure()
        {
            var failure = new InvalidOperationException("worker error must be reported");
            var failed = Task.FromException(failure);
            var calls = new List<string>();
            var window = new PendingWindow("a", calls, failed)
            {
                OnClose = () => throw new InvalidOperationException("already closing"),
            };
            try
            {
                var error = await Assert.ThrowsExceptionAsync<AggregateException>(() => CloseAsync([window], calls));
                CollectionAssert.AreEqual(new Exception[] { failure }, error.InnerExceptions.ToArray());
                CollectionAssert.AreEqual(new[] { "dispose:a", "completion:a", "mouse", "close:a" }, calls);
            }
            finally { _ = failed.Exception; }
        }

        [TestMethod]
        public async Task AsyncSnapshotKeepsOriginalOwnersAcrossDisposalMutation()
        {
            var pending = NewCompletion();
            var calls = new List<string>();
            var first = new PendingWindow("a", calls, pending.Task);
            var second = new PendingWindow("b", calls, Task.CompletedTask);
            var added = new PendingWindow("new", calls, Task.CompletedTask);
            var windows = new List<PendingWindow> { first, second };
            first.OnDispose = () => { windows.Remove(second); windows.Add(added); };
            var closing = CloseAsync(windows, calls);
            try
            {
                CollectionAssert.AreEqual(new[] { "dispose:a", "completion:a", "dispose:b", "completion:b" }, calls);
            }
            finally { pending.TrySetResult(); await closing.WaitAsync(TimeSpan.FromSeconds(10)); }
            Assert.AreEqual(0, calls.Count(call => call.Contains("new", StringComparison.Ordinal)));
            Assert.AreEqual(1, calls.Count(call => call == "close:b"));
        }

        [TestMethod]
        public async Task AsyncSnapshotFailureDoesNotMutatePartialEnumerationAndStillReleasesMouse()
        {
            var failure = new ApplicationException("snapshot");
            var calls = new List<string>();
            IEnumerable<PendingWindow> Windows()
            {
                calls.Add("enumerate");
                yield return new PendingWindow("a", calls, Task.CompletedTask);
                throw failure;
            }
            var error = await Assert.ThrowsExceptionAsync<AggregateException>(() => CloseAsync(Windows(), calls));
            CollectionAssert.AreEqual(new Exception[] { failure }, error.InnerExceptions.ToArray());
            CollectionAssert.AreEqual(new[] { "enumerate", "mouse" }, calls);
        }

        [TestMethod]
        public async Task DispatcherFailureAfterAdmissionStillWaitsForCapturedCompletion()
        {
            var failure = new ApplicationException("dispatch failed after callback");
            var pending = NewCompletion();
            var calls = new List<string>();
            int dispatches = 0;
            Task Dispatch(Action action)
            {
                action();
                if (++dispatches == 1) { throw failure; }
                return Task.CompletedTask;
            }
            var window = new PendingWindow("a", calls, pending.Task);
            var closing = App.CloseOwnedWindowsAsync(() => new[] { window }, owner => owner.Dispose(),
                owner => owner.Completion(), owner => owner.Close(), Dispatch, () => calls.Add("mouse"));
            try
            {
                Assert.IsFalse(closing.IsCompleted);
                CollectionAssert.AreEqual(new[] { "dispose:a", "completion:a" }, calls);
            }
            finally
            {
                pending.TrySetResult();
                try { await closing.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (AggregateException) { }
            }
            var error = await Assert.ThrowsExceptionAsync<AggregateException>(() => closing);
            CollectionAssert.AreEqual(new Exception[] { failure }, error.InnerExceptions.ToArray());
            CollectionAssert.AreEqual(new[] { "dispose:a", "completion:a", "mouse", "close:a" }, calls);
        }

        [TestMethod]
        public async Task NormalTerminationReportsCleanupFailureAndStillAttemptsShutdownWhenReporterThrows()
        {
            var failure = new ApplicationException("worker");
            var reportFailure = new InvalidOperationException("logger");
            var failed = Task.FromException(failure);
            var calls = new List<string>();
            var window = new PendingWindow("a", calls, failed);
            try
            {
                var error = await Assert.ThrowsExceptionAsync<AggregateException>(() => App.TerminateOwnedWindowsAsync(
                    () => new[] { window }, owner => owner.Dispose(), owner => owner.Completion(), owner => owner.Close(),
                    DispatchImmediately, () => calls.Add("mouse"), reported =>
                    {
                        calls.Add("report");
                        Assert.AreSame(failure, ((AggregateException)reported).InnerExceptions.Single());
                        throw reportFailure;
                    }, () => calls.Add("shutdown")));
                Assert.AreEqual(2, error.InnerExceptions.Count);
                Assert.AreSame(failure, ((AggregateException)error.InnerExceptions[0]).InnerExceptions.Single());
                Assert.AreSame(reportFailure, error.InnerExceptions[1]);
                CollectionAssert.AreEqual(new[] { "dispose:a", "completion:a", "mouse", "close:a", "report", "shutdown" }, calls);
            }
            finally { _ = failed.Exception; }
        }

        [TestMethod]
        public async Task NormalTerminationRetainsShutdownFailureWithoutRunningCleanupTwice()
        {
            var failure = new ApplicationException("shutdown");
            var calls = new List<string>();
            var error = await Assert.ThrowsExceptionAsync<AggregateException>(() => App.TerminateOwnedWindowsAsync<PendingWindow>(
                () => [], owner => owner.Dispose(), owner => owner.Completion(), owner => owner.Close(),
                DispatchImmediately, () => calls.Add("mouse"), _ => calls.Add("report"),
                () => { calls.Add("shutdown"); throw failure; }));
            CollectionAssert.AreEqual(new Exception[] { failure }, error.InnerExceptions.ToArray());
            CollectionAssert.AreEqual(new[] { "mouse", "shutdown" }, calls);
        }

        [TestMethod]
        public async Task ReentrantAndConcurrentTerminationRequestsShareOnePublishedOperation()
        {
            var pending = NewCompletion();
            TaskCompletionSource? owned = null;
            int starts = 0;
            Task? reentrant = null;
            Task Start()
            {
                Interlocked.Increment(ref starts);
                reentrant = App.BeginTermination(ref owned, () => throw new AssertFailedException("Reentrant termination ran twice."));
                return pending.Task;
            }
            var first = App.BeginTermination(ref owned, Start);
            var requests = new Task[100];
            Parallel.For(0, requests.Length, index => requests[index] = App.BeginTermination(ref owned, Start));
            try
            {
                Assert.AreEqual(1, starts);
                Assert.AreSame(first, reentrant);
                Assert.IsTrue(requests.All(request => ReferenceEquals(first, request)));
                Assert.IsFalse(first.IsCompleted);
            }
            finally { pending.TrySetResult(); await first.WaitAsync(TimeSpan.FromSeconds(10)); }
            Assert.AreSame(first, App.BeginTermination(ref owned, Start));
            Assert.AreEqual(1, starts);
        }

        [TestMethod]
        public async Task FailedTerminationRetainsOriginalFailureAndRejectsRepeatedStarts()
        {
            var failure = new ApplicationException("synchronous termination callback");
            TaskCompletionSource? owned = null;
            int attempts = 0;
            Task Start() { attempts++; throw failure; }
            var first = App.BeginTermination(ref owned, Start);
            Assert.AreSame(failure, await Assert.ThrowsExceptionAsync<ApplicationException>(() => first));
            Assert.AreSame(first, App.BeginTermination(ref owned, Start));
            Assert.AreEqual(1, attempts);
        }

        [TestMethod]
        public async Task CanceledCompletionStillReleasesAllOwnersAndReportsCancellation()
        {
            var canceled = Task.FromCanceled(new CancellationToken(true));
            var calls = new List<string>();
            var windows = new[] { new PendingWindow("a", calls, canceled), new PendingWindow("b", calls, Task.CompletedTask) };
            var error = await Assert.ThrowsExceptionAsync<AggregateException>(() => CloseAsync(windows, calls));
            Assert.IsInstanceOfType(error.InnerExceptions.Single(), typeof(TaskCanceledException));
            CollectionAssert.AreEqual(new[]
            {
                "dispose:a", "completion:a", "dispose:b", "completion:b", "mouse", "close:a", "close:b"
            }, calls);
        }

        [TestMethod]
        public async Task DeferredNormalShutdownUsesOwnerDispatcherForEveryWpfAction()
        {
            using var ready = new ManualResetEventSlim();
            using var disposed = new ManualResetEventSlim();
            var pending = NewCompletion();
            var calls = new List<string>();
            Dispatcher? dispatcher = null;
            Exception? threadFailure = null;
            int ownerId = 0, shutdownCount = 0;
            var thread = new Thread(() =>
            {
                try
                {
                    dispatcher = Dispatcher.CurrentDispatcher;
                    ownerId = Environment.CurrentManagedThreadId;
                    ready.Set();
                    Dispatcher.Run();
                }
                catch (Exception error) { threadFailure = error; ready.Set(); }
            }) { IsBackground = true, Name = "AppLifetime.ShutdownOwner" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Task? terminating = null;
            try
            {
                Assert.IsTrue(ready.Wait(TimeSpan.FromSeconds(10)));
                Assert.IsNull(threadFailure);
                Assert.IsNotNull(dispatcher);
                void AssertOwner() => Assert.AreEqual(ownerId, Environment.CurrentManagedThreadId);
                var window = new PendingWindow("a", calls, pending.Task)
                {
                    OnDispose = () => { AssertOwner(); disposed.Set(); },
                    OnClose = AssertOwner,
                    GetCompletion = () => { AssertOwner(); return pending.Task; },
                };
                terminating = App.TerminateOwnedWindowsAsync(
                    () => { AssertOwner(); return new[] { window }; }, owner => owner.Dispose(),
                    owner => owner.Completion(), owner => owner.Close(),
                    action => dispatcher!.InvokeAsync(action).Task,
                    () => { AssertOwner(); calls.Add("mouse"); }, _ => Assert.Fail("No error expected."),
                    () => { AssertOwner(); Interlocked.Increment(ref shutdownCount); calls.Add("shutdown"); });
                Assert.IsTrue(disposed.Wait(TimeSpan.FromSeconds(10)));
                Assert.AreEqual(0, Volatile.Read(ref shutdownCount));
                await dispatcher!.InvokeAsync(() =>
                {
                    Assert.IsFalse(terminating.IsCompleted);
                    Assert.AreEqual(0, Volatile.Read(ref shutdownCount));
                    calls.Add("dispatcher:responsive");
                }).Task;
                pending.SetResult();
                await terminating.WaitAsync(TimeSpan.FromSeconds(10));
                CollectionAssert.AreEqual(new[]
                {
                    "dispose:a", "completion:a", "dispatcher:responsive", "mouse", "close:a", "shutdown"
                }, calls);
            }
            finally
            {
                pending.TrySetResult();
                if (terminating != null)
                {
                    try { await terminating.WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch { }
                }
                dispatcher?.BeginInvokeShutdown(DispatcherPriority.Background);
                Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)), "The owned STA Dispatcher must exit.");
            }
            Assert.IsNull(threadFailure);
            Assert.AreEqual(1, shutdownCount);
        }

        [TestMethod]
        public async Task AppAsyncShutdownCounterScenario()
        {
            const int cycles = 100;
            int earlyCloses = 0, earlyShutdowns = 0, disposals = 0, closes = 0, mice = 0, shutdowns = 0;
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                var completion = NewCompletion();
                var calls = new List<string>();
                var window = new PendingWindow("main", calls, completion.Task)
                {
                    OnDispose = () => disposals++,
                    OnClose = () => closes++,
                };
                int closedBefore = closes, shutdownBefore = shutdowns;
                var terminating = App.TerminateOwnedWindowsAsync(
                    () => new[] { window }, owner => owner.Dispose(), owner => owner.Completion(), owner => owner.Close(),
                    DispatchImmediately, () => mice++, _ => Assert.Fail("No error expected."), () => shutdowns++);
                earlyCloses += closes - closedBefore;
                earlyShutdowns += shutdowns - shutdownBefore;
                completion.SetResult();
                await terminating.WaitAsync(TimeSpan.FromSeconds(10));
            }
            Assert.AreEqual(cycles, disposals);
            Assert.AreEqual(cycles, closes);
            Assert.AreEqual(cycles, mice);
            Assert.AreEqual(cycles, shutdowns);
            Console.WriteLine($"PERFCOUNTER app-async-shutdown cycles {cycles}");
            Console.WriteLine($"PERFCOUNTER app-async-shutdown early-window-closes {earlyCloses}");
            Console.WriteLine($"PERFCOUNTER app-async-shutdown early-shutdowns {earlyShutdowns}");
            Console.WriteLine($"PERFCOUNTER app-async-shutdown dispose-attempts {disposals}");
            Console.WriteLine($"PERFCOUNTER app-async-shutdown close-attempts {closes}");
            Console.WriteLine($"PERFCOUNTER app-async-shutdown mouse-cleanups {mice}");
            Console.WriteLine($"PERFCOUNTER app-async-shutdown shutdowns {shutdowns}");
        }

        private static TaskCompletionSource NewCompletion()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static Task DispatchImmediately(Action action) { action(); return Task.CompletedTask; }

        private static Task CloseAsync(IEnumerable<PendingWindow> windows, List<string> calls)
            => App.CloseOwnedWindowsAsync(() => windows, window => window.Dispose(), window => window.Completion(),
                window => window.Close(), DispatchImmediately, () => calls.Add("mouse"));

        private static Task TerminateAsync(IEnumerable<PendingWindow> windows, List<string> calls)
            => App.TerminateOwnedWindowsAsync(() => windows, window => window.Dispose(), window => window.Completion(),
                window => window.Close(), DispatchImmediately, () => calls.Add("mouse"), _ => calls.Add("report"),
                () => calls.Add("shutdown"));

        private sealed class PendingWindow(string name, List<string> calls, Task completion)
        {
            public Action? OnDispose { get; set; }
            public Action? OnClose { get; set; }
            public Func<Task>? GetCompletion { get; set; }
            public void Dispose() { calls.Add($"dispose:{name}"); OnDispose?.Invoke(); }
            public Task Completion() { calls.Add($"completion:{name}"); return GetCompletion?.Invoke() ?? completion; }
            public void Close() { calls.Add($"close:{name}"); OnClose?.Invoke(); }
        }
    }
}
