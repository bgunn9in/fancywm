#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    public partial class MainWindowLifetimeTest
    {
        [TestMethod]
        public void MicaCallbackCapturedBeforeDisposeRejectsLatePostAndApply()
        {
            var probe = new MicaCallbackProbe();
            using var lifetime = new MainWindowLifetime(_ => { });
            Action captured = () => probe.Invoke(lifetime);

            lifetime.Dispose();
            captured();

            AssertNoMicaCallbackEffects(probe);
        }

        [TestMethod]
        public void MicaCallbackRejectsReentrantPostDuringOwnedRelease()
        {
            var probe = new MicaCallbackProbe();
            int ownedReleases = 0;
            MainWindowLifetime? lifetime = null;
            lifetime = new MainWindowLifetime(release => release(() =>
            {
                ownedReleases++;
                probe.Invoke(lifetime!);
            }));

            lifetime.Dispose();
            lifetime.Dispose();

            Assert.AreEqual(1, ownedReleases);
            Assert.IsTrue(lifetime.Completion.IsCompletedSuccessfully);
            AssertNoMicaCallbackEffects(probe);
        }

        [TestMethod]
        public async Task MicaCallbackRejectsCapturedPostWhileDependentShutdownIsPending()
        {
            var probe = new MicaCallbackProbe();
            var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int dependentReleases = 0;
            var lifetime = new MainWindowLifetime(_ => { }, () => operation.Task,
                release => release(() => dependentReleases++));
            Action captured = () => probe.Invoke(lifetime);
            try
            {
                lifetime.Dispose();
                Assert.IsFalse(lifetime.Completion.IsCompleted);

                captured();

                AssertNoMicaCallbackEffects(probe);
                Assert.AreEqual(0, dependentReleases);
                Assert.IsFalse(lifetime.Completion.IsCompleted);
            }
            finally
            {
                operation.TrySetResult();
                lifetime.Dispose();
                await lifetime.Completion.WaitAsync(VirtualDesktopCallbackGuard);
            }
            Assert.AreEqual(1, dependentReleases);
        }

        [TestMethod]
        public void MicaCallbackActivePostsOnceAndAppliesOnlyWhenCallerDrainsQueue()
        {
            var probe = new MicaCallbackProbe();
            using var lifetime = new MainWindowLifetime(_ => { });

            probe.Invoke(lifetime);

            Assert.AreEqual(1, probe.Posts);
            Assert.AreEqual(0, probe.Applies);
            Assert.AreEqual(1, probe.Pending.Count);
            CollectionAssert.AreEqual(new[] { "post" }, probe.Calls);

            probe.Pending.Dequeue()();

            Assert.AreEqual(1, probe.Posts);
            Assert.AreEqual(1, probe.Applies);
            Assert.AreEqual(0, probe.Pending.Count);
            CollectionAssert.AreEqual(new[] { "post", "apply" }, probe.Calls);
        }

        [TestMethod]
        public void MicaCallbackQueuedBeforeShutdownRejectsApplyAfterShutdown()
        {
            var probe = new MicaCallbackProbe();
            using var lifetime = new MainWindowLifetime(_ => { });

            probe.Invoke(lifetime);
            Assert.AreEqual(1, probe.Posts);
            Assert.AreEqual(0, probe.Applies);
            Assert.AreEqual(1, probe.Pending.Count);

            lifetime.Dispose();
            probe.Pending.Dequeue()();

            Assert.AreEqual(1, probe.Posts);
            Assert.AreEqual(0, probe.Applies);
            Assert.AreEqual(0, probe.Pending.Count);
            CollectionAssert.AreEqual(new[] { "post" }, probe.Calls);
        }

        [TestMethod]
        public async Task MicaCallbackAdmittedPostEnqueuesAfterShutdownButRejectsApply()
        {
            var enteredPost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completedPost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var releasePost = new ManualResetEventSlim();
            using var lifetime = new MainWindowLifetime(_ => { });
            var pending = new Queue<Action>();
            int posts = 0;
            int applies = 0;
            var thread = new Thread(() =>
            {
                try
                {
                    MainWindow.HandleMicaPrimaryColorChanged(lifetime, callback =>
                    {
                        posts++;
                        enteredPost.TrySetResult();
                        if (!releasePost.Wait(VirtualDesktopCallbackGuard))
                        {
                            throw new TimeoutException("Owned Mica callback post was not released.");
                        }
                        pending.Enqueue(callback);
                    }, () => applies++);
                    completedPost.TrySetResult();
                }
                catch (Exception error)
                {
                    enteredPost.TrySetException(error);
                    completedPost.TrySetException(error);
                }
            })
            {
                IsBackground = true,
                Name = "Mica callback admitted post",
            };
            thread.Start();
            try
            {
                await enteredPost.Task.WaitAsync(VirtualDesktopCallbackGuard);
                Assert.AreEqual(1, posts);
                Assert.AreEqual(0, applies);
                Assert.AreEqual(0, pending.Count);
                Assert.IsFalse(completedPost.Task.IsCompleted);

                lifetime.Dispose();
                releasePost.Set();
                await completedPost.Task.WaitAsync(VirtualDesktopCallbackGuard);
                Assert.AreEqual(1, pending.Count);
                pending.Dequeue()();

                Assert.AreEqual(1, posts);
                Assert.AreEqual(0, applies);
                Assert.AreEqual(0, pending.Count);
            }
            finally
            {
                lifetime.Dispose();
                releasePost.Set();
                Assert.IsTrue(thread.Join(VirtualDesktopCallbackGuard), "Owned Mica callback thread did not stop.");
            }
        }

        [TestMethod]
        public void MicaCallbackSynchronousPostFailurePreservesIdentityWithoutApply()
        {
            var failure = new InvalidOperationException("Mica callback post failed.");
            var probe = new MicaCallbackProbe { PostFailure = failure };
            using var lifetime = new MainWindowLifetime(_ => { });

            var observed = Assert.ThrowsException<InvalidOperationException>(() => probe.Invoke(lifetime));

            Assert.AreSame(failure, observed);
            Assert.AreEqual(1, probe.Posts);
            Assert.AreEqual(0, probe.Applies);
            Assert.AreEqual(0, probe.Pending.Count);
            CollectionAssert.AreEqual(new[] { "post" }, probe.Calls);
        }

        [TestMethod]
        public void MicaCallbackQueuedApplyFailurePreservesIdentityOnCallerQueue()
        {
            var failure = new ApplicationException("Mica callback apply failed.");
            var probe = new MicaCallbackProbe { ApplyFailure = failure };
            using var lifetime = new MainWindowLifetime(_ => { });

            probe.Invoke(lifetime);

            Assert.AreEqual(1, probe.Posts);
            Assert.AreEqual(0, probe.Applies);
            Assert.AreEqual(1, probe.Pending.Count);
            var observed = Assert.ThrowsException<ApplicationException>(probe.Pending.Dequeue());

            Assert.AreSame(failure, observed);
            Assert.AreEqual(1, probe.Posts);
            Assert.AreEqual(1, probe.Applies);
            Assert.AreEqual(0, probe.Pending.Count);
            CollectionAssert.AreEqual(new[] { "post", "apply" }, probe.Calls);
        }

        [TestMethod]
        public void MicaCallbackLifetimeCounterScenario()
        {
            const int cycles = 100;
            int activePosts = 0;
            int activeApplies = 0;
            int latePosts = 0;
            int lateApplies = 0;
            int queuedBeforeClosePosts = 0;
            int queuedAfterCloseApplies = 0;
            int ownedReleases = 0;
            int completedCallbacks = 0;
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                var activeProbe = new MicaCallbackProbe();
                var queuedProbe = new MicaCallbackProbe();
                var lateProbe = new MicaCallbackProbe();
                using var lifetime = new MainWindowLifetime(release => release(() => ownedReleases++));
                Action captured = () => lateProbe.Invoke(lifetime);

                activeProbe.Invoke(lifetime);
                completedCallbacks++;
                Assert.AreEqual(1, activeProbe.Pending.Count);
                activeProbe.Pending.Dequeue()();
                activePosts += activeProbe.Posts;
                activeApplies += activeProbe.Applies;

                queuedProbe.Invoke(lifetime);
                completedCallbacks++;
                Assert.AreEqual(1, queuedProbe.Pending.Count);
                queuedBeforeClosePosts += queuedProbe.Posts;

                lifetime.Dispose();
                queuedProbe.Pending.Dequeue()();
                queuedAfterCloseApplies += queuedProbe.Applies;

                captured();
                // A completed callback is a returned helper invocation, including
                // a rejected late invocation; it is not an applied color update.
                completedCallbacks++;
                while (lateProbe.Pending.Count != 0)
                {
                    lateProbe.Pending.Dequeue()();
                }
                latePosts += lateProbe.Posts;
                lateApplies += lateProbe.Applies;
                Assert.AreEqual(0, activeProbe.Pending.Count);
                Assert.AreEqual(0, queuedProbe.Pending.Count);
                Assert.AreEqual(0, lateProbe.Pending.Count);
                Assert.IsTrue(lifetime.Completion.IsCompletedSuccessfully);
            }

            Assert.AreEqual(cycles, activePosts);
            Assert.AreEqual(cycles, activeApplies);
            Assert.IsTrue(latePosts == 0 || latePosts == cycles,
                "A comparison variant must consistently reject or preserve all late posts.");
            Assert.AreEqual(0, lateApplies);
            Assert.AreEqual(cycles, queuedBeforeClosePosts);
            Assert.AreEqual(0, queuedAfterCloseApplies);
            Assert.AreEqual(cycles, ownedReleases);
            Assert.AreEqual(cycles * 3, completedCallbacks);
            Console.WriteLine($"PERFCOUNTER mica-primary-color-callback-lifetime cycles {cycles}");
            Console.WriteLine($"PERFCOUNTER mica-primary-color-callback-lifetime active-posts {activePosts}");
            Console.WriteLine($"PERFCOUNTER mica-primary-color-callback-lifetime active-applies {activeApplies}");
            Console.WriteLine($"PERFCOUNTER mica-primary-color-callback-lifetime late-posts {latePosts}");
            Console.WriteLine($"PERFCOUNTER mica-primary-color-callback-lifetime late-applies {lateApplies}");
            Console.WriteLine($"PERFCOUNTER mica-primary-color-callback-lifetime queued-before-close-posts {queuedBeforeClosePosts}");
            Console.WriteLine($"PERFCOUNTER mica-primary-color-callback-lifetime queued-after-close-applies {queuedAfterCloseApplies}");
            Console.WriteLine($"PERFCOUNTER mica-primary-color-callback-lifetime owned-releases {ownedReleases}");
            Console.WriteLine($"PERFCOUNTER mica-primary-color-callback-lifetime completed-callbacks {completedCallbacks}");
        }

        private static void AssertNoMicaCallbackEffects(MicaCallbackProbe probe)
        {
            Assert.AreEqual(0, probe.Posts);
            Assert.AreEqual(0, probe.Applies);
            Assert.AreEqual(0, probe.Pending.Count);
            Assert.AreEqual(0, probe.Calls.Count);
        }

        private sealed class MicaCallbackProbe
        {
            internal int Posts;
            internal int Applies;
            internal Exception? PostFailure;
            internal Exception? ApplyFailure;
            internal readonly Queue<Action> Pending = new();
            internal readonly List<string> Calls = [];

            internal void Invoke(MainWindowLifetime lifetime)
                => MainWindow.HandleMicaPrimaryColorChanged(lifetime, Post, Apply);

            private void Post(Action callback)
            {
                Posts++;
                Calls.Add("post");
                if (PostFailure != null) { throw PostFailure; }
                Pending.Enqueue(callback);
            }

            private void Apply()
            {
                Applies++;
                Calls.Add("apply");
                if (ApplyFailure != null) { throw ApplyFailure; }
            }
        }
    }
}
