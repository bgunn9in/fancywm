#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    public partial class MainWindowLifetimeTest
    {
        [TestMethod]
        public async Task PendingDependentShutdownRejectsReentrantAndQueuedTrayCallbacks()
        {
            var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var unusedAction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int ownedReleases = 0, dependentReleases = 0, sideEffects = 0;
            Task? reentrantAsync = null;
            MainWindowLifetime? lifetime = null;
            Action synchronousCallback = () => lifetime!.RunIfActive(() => sideEffects++);
            Func<Task> asynchronousCallback = () => lifetime!.RunIfActiveAsync(() =>
            {
                sideEffects++;
                return unusedAction.Task;
            });
            var queued = new Queue<Action>();
            queued.Enqueue(synchronousCallback);
            lifetime = new MainWindowLifetime(release => release(() =>
            {
                ownedReleases++;
                // Detaching/closing the native tray UI can deliver already
                // captured callbacks while initial cleanup is still executing.
                synchronousCallback();
                reentrantAsync = asynchronousCallback();
            }), () => operation.Task, release => release(() => dependentReleases++));

            try
            {
                lifetime.Dispose();
                Assert.AreEqual(1, ownedReleases);
                Assert.IsFalse(lifetime.Completion.IsCompleted);
                Assert.AreEqual(0, dependentReleases);
                Assert.IsNotNull(reentrantAsync);
                Assert.IsTrue(reentrantAsync!.IsCompletedSuccessfully);

                queued.Dequeue()();
                var lateAsync = asynchronousCallback();
                Assert.IsTrue(lateAsync.IsCompletedSuccessfully);
                Assert.AreEqual(0, sideEffects,
                    "Pending dependent cleanup must not reopen callback admission.");
                Assert.IsFalse(unusedAction.Task.IsCompleted);
                Assert.IsFalse(lifetime.Completion.IsCompleted);
                Assert.AreEqual(0, queued.Count);
            }
            finally
            {
                unusedAction.TrySetResult();
                operation.TrySetResult();
                lifetime.Dispose();
                await lifetime.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
            Assert.AreEqual(1, dependentReleases);
            Assert.AreEqual(0, sideEffects);
        }

        [TestMethod]
        public void QueuedContextCallbackSkipsUnconstructedStateAfterDisposal()
        {
            var pending = new Queue<Action>();
            Action? closeContextMenu = null;
            int sideEffects = 0;
            var lifetime = new MainWindowLifetime(_ => { });
            pending.Enqueue(() => lifetime.RunIfActive(() =>
            {
                // MainWindow can receive this event before its ContextMenu has
                // been acquired, then fail construction before the queue drains.
                sideEffects++;
                closeContextMenu!();
            }));
            lifetime.Dispose();
            pending.Dequeue()();
            Assert.AreEqual(0, sideEffects);
            Assert.IsNull(closeContextMenu);
            Assert.AreEqual(0, pending.Count);
        }

        [TestMethod]
        public void QueuedAsyncActionSkipsFactoryAndCompletesAfterDisposal()
        {
            var pending = new Queue<Func<Task>>();
            var completion = new TaskCompletionSource<object?>();
            int actions = 0;
            var lifetime = new MainWindowLifetime(_ => { });
            pending.Enqueue(() => lifetime.RunIfActiveAsync(() =>
            {
                actions++;
                return completion.Task;
            }));
            lifetime.Dispose();
            try
            {
                var returned = pending.Dequeue()();
                Assert.IsTrue(returned.IsCompletedSuccessfully, "A discarded command must not start or retain the action's asynchronous work.");
                Assert.AreEqual(0, actions);
                Assert.AreEqual(0, pending.Count);
            }
            finally { completion.TrySetResult(null); }
        }

        [TestMethod]
        public void ActiveSynchronousCallbacksRunOnceAndPreserveOriginalFailure()
        {
            var pending = new Queue<Action>();
            int calls = 0;
            var failure = new InvalidOperationException("active callback failed");
            var lifetime = new MainWindowLifetime(_ => { });
            pending.Enqueue(() => lifetime.RunIfActive(() => calls++));
            pending.Enqueue(() => lifetime.RunIfActive(() =>
            {
                calls++;
                ThrowFromOwner(failure);
            }));
            pending.Dequeue()();
            Assert.AreEqual(1, calls);
            var observed = Assert.ThrowsException<InvalidOperationException>(pending.Dequeue());
            Assert.AreSame(failure, observed);
            StringAssert.Contains(observed.StackTrace!, nameof(ThrowFromOwner));
            Assert.AreEqual(2, calls);
            Assert.AreEqual(0, pending.Count);
            lifetime.Dispose();
        }

        [TestMethod]
        public void ActiveAsyncCallbackPreservesReturnedTaskAndOriginalFault()
        {
            var completion = new TaskCompletionSource<object?>();
            int calls = 0;
            var failure = new ApplicationException("active async action failed");
            var lifetime = new MainWindowLifetime(_ => { });
            var returned = lifetime.RunIfActiveAsync(() =>
            {
                calls++;
                return completion.Task;
            });
            Assert.AreEqual(1, calls);
            Assert.AreSame(completion.Task, returned);
            Assert.IsFalse(returned.IsCompleted);
            completion.SetException(failure);
            var observed = Assert.ThrowsException<ApplicationException>(() => returned.GetAwaiter().GetResult());
            Assert.AreSame(failure, observed);
            Assert.AreEqual(1, calls);
            lifetime.Dispose();
        }
    }
}
