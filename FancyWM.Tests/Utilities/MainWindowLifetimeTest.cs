#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public partial class MainWindowLifetimeTest
    {
        [TestMethod]
        public void DisposeContinuesOwnedCleanupAfterSubscriptionFailure()
        {
            for (int cycle = 0; cycle < 100; cycle++)
            {
                var calls = new List<string>();
                var failure = new InvalidOperationException("subscription failed");
                var lifetime = new MainWindowLifetime(release =>
                {
                    release(() => calls.Add("cancel"));
                    release(() => { calls.Add("subscription"); throw failure; });
                    release(() => calls.Add("binding"));
                    release(() => calls.Add("workspace"));
                });
                Assert.AreSame(failure, Assert.ThrowsException<InvalidOperationException>(lifetime.Dispose));
                lifetime.Dispose();
                CollectionAssert.AreEqual(new[] { "cancel", "subscription", "binding", "workspace" }, calls);
            }
        }

        [TestMethod]
        public void SuccessfulDisposalPreservesOrderAndAllowsReentrantDisposal()
        {
            var calls = new List<int>();
            MainWindowLifetime lifetime = null!;
            lifetime = new MainWindowLifetime(release =>
            {
                release(() => { calls.Add(1); lifetime.Dispose(); });
                release(() => calls.Add(2));
                release(() => calls.Add(3));
            });
            lifetime.Dispose();
            lifetime.Dispose();
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, calls);
        }

        [TestMethod]
        public async Task ConcurrentDisposeCannotRunOwnedCleanupTwice()
        {
            using var entered = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            int first = 0, last = 0;
            var lifetime = new MainWindowLifetime(release =>
            {
                release(() =>
                {
                    Interlocked.Increment(ref first);
                    entered.Set();
                    Assert.IsTrue(resume.Wait(TimeSpan.FromSeconds(10)));
                });
                release(() => Interlocked.Increment(ref last));
            });
            var disposing = Task.Run(lifetime.Dispose);
            try
            {
                Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
                lifetime.Dispose();
                Assert.AreEqual(1, first);
                Assert.AreEqual(0, last);
            }
            finally { resume.Set(); await disposing.WaitAsync(TimeSpan.FromSeconds(10)); }
            Assert.AreEqual(1, last);
        }

        [TestMethod]
        public void MultipleFailuresPreserveFirstExceptionAndOrderedSecondaryDiagnostics()
        {
            var first = new InvalidOperationException("first");
            var second = new ApplicationException("second");
            var third = new ArgumentException("third");
            var calls = new List<int>();
            var lifetime = new MainWindowLifetime(release =>
            {
                release(() => { calls.Add(1); ThrowFromOwner(first); });
                release(() => { calls.Add(2); throw second; });
                release(() => { calls.Add(3); throw third; });
                release(() => calls.Add(4));
            });
            var observed = Assert.ThrowsException<InvalidOperationException>(lifetime.Dispose);
            Assert.AreSame(first, observed);
            StringAssert.Contains(observed.StackTrace!, nameof(ThrowFromOwner));
            var secondary = (AggregateException)observed.Data["MainWindowLifetime.CleanupExceptions"]!;
            CollectionAssert.AreEqual(new Exception[] { second, third }, secondary.InnerExceptions.ToArray());
            lifetime.Dispose();
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, calls);
        }

        [TestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        public void RealCompositeSubscriptionsAllDisposeOnceAfterAChildThrows(int failingChild)
        {
            for (int cycle = 0; cycle < 100; cycle++)
            {
                var calls = new List<int>();
                var failure = new InvalidOperationException("child failure");
                var subscriptions = new CompositeDisposable(Enumerable.Range(0, 3)
                    .Select(index => new CallbackOwner(() =>
                    {
                        calls.Add(index);
                        if (index == failingChild) { throw failure; }
                    })));
                var lifetime = new MainWindowLifetime(release =>
                {
                    MainWindowLifetime.DisposeSubscriptions(subscriptions, release);
                    release(() => calls.Add(3));
                });
                Assert.AreSame(failure, Assert.ThrowsException<InvalidOperationException>(lifetime.Dispose));
                lifetime.Dispose();
                subscriptions.Dispose();
                Assert.AreEqual(0, subscriptions.Count);
                Assert.IsTrue(subscriptions.IsDisposed);
                subscriptions.Add(new CallbackOwner(() => calls.Add(4)));
                CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4 }, calls);
            }
        }

        [TestMethod]
        public void NullAndEmptySubscriptionOwnersStillAllowRemainingCleanup()
        {
            int remaining = 0;
            var empty = new CompositeDisposable();
            new MainWindowLifetime(release =>
            {
                MainWindowLifetime.DisposeSubscriptions(null, release);
                MainWindowLifetime.DisposeSubscriptions(empty, release);
                release(() => remaining++);
            }).Dispose();
            Assert.IsTrue(empty.IsDisposed);
            Assert.AreEqual(1, remaining);
        }

        [TestMethod]
        public void ThrowingCancellationCallbackStillReleasesSubscriptionsAndFinalOwner()
        {
            using var cancellation = new CancellationTokenSource();
            var calls = new List<string>();
            var failure = new InvalidOperationException("cancel failure");
            using var registration = cancellation.Token.Register(() => { calls.Add("cancel"); throw failure; });
            var subscriptions = new CompositeDisposable(new CallbackOwner(() => calls.Add("subscription")));
            var lifetime = new MainWindowLifetime(release =>
            {
                release(cancellation.Cancel);
                MainWindowLifetime.DisposeSubscriptions(subscriptions, release);
                release(() => calls.Add("final"));
            });
            var observed = Assert.ThrowsException<AggregateException>(lifetime.Dispose);
            Assert.AreSame(failure, observed.InnerException);
            Assert.IsTrue(cancellation.IsCancellationRequested);
            Assert.IsTrue(subscriptions.IsDisposed);
            lifetime.Dispose();
            CollectionAssert.AreEqual(new[] { "cancel", "subscription", "final" }, calls);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void RetainedDisposedLifetimesReleaseBoundOwners(bool throws)
        {
            var observations = Enumerable.Range(0, 100).Select(_ => CreateObservedLifetime(throws)).ToArray();
            CollectOwners();
            Assert.AreEqual(100, observations.Count(value => value.Owner.IsAlive));
            foreach (var value in observations)
            {
                if (throws) { Assert.ThrowsException<InvalidOperationException>(value.Lifetime.Dispose); }
                else { value.Lifetime.Dispose(); }
            }
            CollectOwners();
            Assert.AreEqual(0, observations.Count(value => value.Owner.IsAlive));
            GC.KeepAlive(observations);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static (MainWindowLifetime Lifetime, WeakReference Owner) CreateObservedLifetime(bool throws)
        {
            var owner = new CleanupOwner(throws);
            return (new MainWindowLifetime(owner.Release), new WeakReference(owner));
        }

        private static void CollectOwners()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowFromOwner(Exception error) => throw error;

        private sealed class CallbackOwner(Action callback) : IDisposable
        {
            // Deliberately non-idempotent: a duplicate call must remain observable.
            public void Dispose() => callback();
        }

        private sealed class CleanupOwner(bool throws)
        {
            public void Release(Action<Action> release) => release(() =>
            {
                if (throws) { throw new InvalidOperationException("owner failure"); }
            });
        }

        [TestMethod]
        public void MainWindowLifetimeCounterScenario()
        {
            const int cycles = 100;
            int attempts = 0, remaining = 0, errors = 0, duplicates = 0;
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                var owners = new bool[] { true, true, true, true };
                var failure = new InvalidOperationException("subscription failed");
                var lifetime = new MainWindowLifetime(release =>
                {
                    for (int index = 0; index < owners.Length; index++)
                    {
                        int owner = index;
                        release(() =>
                        {
                            attempts++;
                            if (!owners[owner]) { duplicates++; }
                            owners[owner] = false;
                            if (owner == 1) { throw failure; }
                        });
                    }
                });
                try { lifetime.Dispose(); }
                catch (InvalidOperationException error) { Assert.AreSame(failure, error); errors++; }
                lifetime.Dispose();
                remaining += owners.Count(owned => owned);
            }
            Assert.AreEqual(cycles, errors);
            Assert.AreEqual(0, duplicates);
            Assert.AreEqual(cycles * 4, attempts + remaining);
            Console.WriteLine($"PERFCOUNTER main-window-lifetime cycles {cycles}");
            Console.WriteLine($"PERFCOUNTER main-window-lifetime cleanup-attempts {attempts}");
            Console.WriteLine($"PERFCOUNTER main-window-lifetime remaining-owners {remaining}");
            Console.WriteLine($"PERFCOUNTER main-window-lifetime original-errors {errors}");
            Console.WriteLine($"PERFCOUNTER main-window-lifetime duplicate-cleanups {duplicates}");
        }
    }
}
