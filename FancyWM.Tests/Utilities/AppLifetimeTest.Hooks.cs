#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    public partial class AppLifetimeTest
    {
        [TestMethod]
        public async Task TerminationWaitsForBothHookCompletionsBeforeShutdown()
        {
            var mouse = NewHookCompletion();
            var keyboard = NewHookCompletion();
            var calls = new List<string>();
            var terminating = App.TerminateOwnedWindowsAsync<object>(
                () => [], _ => { }, _ => Task.CompletedTask, _ => { }, DispatchImmediately,
                new App.ShutdownOwner[]
                {
                    new(() => calls.Add("stop:mouse"), () =>
                    {
                        calls.Add("completion:mouse");
                        return mouse.Task;
                    }),
                    new(() => calls.Add("stop:keyboard"), () =>
                    {
                        calls.Add("completion:keyboard");
                        return keyboard.Task;
                    }),
                },
                _ => Assert.Fail("No error expected."), () => calls.Add("shutdown"));

            CollectionAssert.AreEqual(new[]
            {
                "stop:mouse", "completion:mouse", "stop:keyboard", "completion:keyboard",
            }, calls);
            Assert.IsFalse(terminating.IsCompleted);
            mouse.SetResult();
            Assert.IsFalse(terminating.IsCompleted,
                "A completed first hook must not release a still-running second hook owner.");
            keyboard.SetResult();
            await terminating.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual("shutdown", calls.Last());
        }

        [TestMethod]
        public async Task HookFailureStillWaitsForLaterOwnerAndAttemptsShutdown()
        {
            var failure = new InvalidOperationException("mouse completion failed");
            var failed = Task.FromException(failure);
            var keyboard = NewHookCompletion();
            var calls = new List<string>();
            var terminating = App.TerminateOwnedWindowsAsync<object>(
                () => [], _ => { }, _ => Task.CompletedTask, _ => { }, DispatchImmediately,
                new App.ShutdownOwner[]
                {
                    new(() => calls.Add("stop:mouse"), () => failed),
                    new(() => calls.Add("stop:keyboard"), () => keyboard.Task),
                },
                _ => calls.Add("report"), () => calls.Add("shutdown"));
            try
            {
                Assert.IsFalse(terminating.IsCompleted);
                Assert.IsFalse(calls.Contains("shutdown"));
            }
            finally
            {
                keyboard.TrySetResult();
            }

            var error = await Assert.ThrowsExceptionAsync<AggregateException>(() => terminating);
            _ = failed.Exception;
            Assert.AreSame(failure,
                ((AggregateException)error.InnerExceptions.Single()).InnerExceptions.Single());
            CollectionAssert.AreEqual(new[]
            {
                "stop:mouse", "stop:keyboard", "report", "shutdown",
            }, calls);
        }

        [TestMethod]
        public async Task HookStopAndCompletionGetterFailuresDoNotSkipOtherOwners()
        {
            var stopFailure = new InvalidOperationException("mouse stop failed");
            var getterFailure = new ApplicationException("mouse completion getter failed");
            var calls = new List<string>();
            var error = await Assert.ThrowsExceptionAsync<AggregateException>(() =>
                App.CloseOwnedWindowsAsync<object>(
                    () => [], _ => { }, _ => Task.CompletedTask, _ => { }, DispatchImmediately,
                    new App.ShutdownOwner[]
                    {
                        new(() => { calls.Add("stop:mouse"); throw stopFailure; },
                            () => { calls.Add("completion:mouse"); throw getterFailure; }),
                        new(() => calls.Add("stop:keyboard"), () =>
                        {
                            calls.Add("completion:keyboard");
                            return Task.CompletedTask;
                        }),
                    }));

            CollectionAssert.AreEqual(new Exception[] { stopFailure, getterFailure },
                error.InnerExceptions.ToArray());
            CollectionAssert.AreEqual(new[]
            {
                "stop:mouse", "completion:mouse", "stop:keyboard", "completion:keyboard",
            }, calls);
        }

        [TestMethod]
        public async Task HooksCompleteBeforeClosingTheLastDispatcherWindow()
        {
            var completion = NewHookCompletion();
            var calls = new List<string>();
            bool dispatcherAvailable = true;
            var window = new object();
            var closing = App.CloseOwnedWindowsAsync(
                () => new[] { window }, _ => calls.Add("dispose"), _ => Task.CompletedTask,
                _ => { calls.Add("close"); dispatcherAvailable = false; }, DispatchImmediately,
                new App.ShutdownOwner[]
                {
                    new(() =>
                    {
                        Assert.IsTrue(dispatcherAvailable);
                        calls.Add("stop:hook");
                    }, () => completion.Task),
                });

            CollectionAssert.AreEqual(new[] { "dispose", "stop:hook" }, calls);
            Assert.IsFalse(closing.IsCompleted);
            completion.SetResult();
            await closing.WaitAsync(TimeSpan.FromSeconds(5));
            CollectionAssert.AreEqual(new[] { "dispose", "stop:hook", "close" }, calls);
        }

        [TestMethod]
        public async Task FailedHookAcquisitionIsNotRetriedByCompletionGetter()
        {
            var failure = new InvalidOperationException("hook construction failed");
            int acquisitions = 0;
            var neverStopped = NewHookCompletion();
            var shutdownOwner = App.CreateShutdownOwner(
                () =>
                {
                    acquisitions++;
                    if (acquisitions == 1) { throw failure; }
                    return neverStopped;
                },
                _ => { }, owner => owner.Task);

            var error = await Assert.ThrowsExceptionAsync<AggregateException>(() =>
                App.CloseOwnedWindowsAsync<object>(
                    () => [], _ => { }, _ => Task.CompletedTask, _ => { }, DispatchImmediately,
                    new[] { shutdownOwner }));

            Assert.AreEqual(1, acquisitions);
            CollectionAssert.AreEqual(new Exception[] { failure }, error.InnerExceptions.ToArray());
            Assert.IsFalse(neverStopped.Task.IsCompleted,
                "A second, un-stopped hook instance must never be acquired for Completion.");
        }

        private static TaskCompletionSource NewHookCompletion()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
