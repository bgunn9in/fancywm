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
    public partial class MainWindowLifetimeTest
    {
        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void PartialConstructionFailurePreservesAcquisitionErrorAndReleasesEarlierOwners(bool cleanupThrows)
        {
            for (int cycle = 0; cycle < 100; cycle++)
            {
                var calls = new List<string>();
                var failure = new InvalidOperationException("acquisition failed");
                var cleanupFailure = new ApplicationException("earlier cleanup failed");
                CallbackOwner? first = null, second = null, missing = null;
                var lifetime = new MainWindowLifetime(release =>
                {
                    release(() => first?.Dispose());
                    release(() => second?.Dispose());
                    release(() => missing?.Dispose());
                });
                var observed = Assert.ThrowsException<InvalidOperationException>(() => CompleteConstruction(lifetime, () =>
                {
                    lifetime.Acquire(out first, () => new CallbackOwner(() =>
                    {
                        calls.Add("first");
                        if (cleanupThrows) { throw cleanupFailure; }
                    }));
                    lifetime.Acquire(out second, () => new CallbackOwner(() => calls.Add("second")));
                    lifetime.Acquire(out missing, () => ThrowConstructionAcquisition(failure));
                    lifetime.Run(() => calls.Add("later initialization"));
                }));
                Assert.AreSame(failure, observed);
                StringAssert.Contains(observed.StackTrace!, nameof(ThrowConstructionAcquisition));
                Assert.IsNull(missing);
                CollectionAssert.AreEqual(new[] { "first", "second" }, calls);
                if (cleanupThrows)
                {
                    CollectionAssert.AreEqual(new Exception[] { cleanupFailure }, ConstructionCleanup(observed).InnerExceptions.ToArray());
                }
                else { Assert.IsFalse(observed.Data.Contains("MainWindowLifetime.ConstructionCleanupExceptions")); }
                lifetime.ConstructionFailed(observed);
                lifetime.Dispose();
                CollectionAssert.AreEqual(new[] { "first", "second" }, calls);
            }
        }

        [DataTestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        public void FailureAtEachAcquisitionBoundaryReleasesExactlyPublishedOwners(int failedIndex)
        {
            var acquisitionOrder = new List<int>();
            var disposalOrder = new List<int>();
            var owners = new CallbackOwner?[4];
            var failure = new InvalidOperationException("partial acquisition");
            var lifetime = new MainWindowLifetime(release =>
            {
                for (int index = 0; index < owners.Length; index++)
                {
                    int owner = index;
                    release(() => owners[owner]?.Dispose());
                }
            });
            var observed = Assert.ThrowsException<InvalidOperationException>(() => CompleteConstruction(lifetime, () =>
            {
                for (int index = 0; index < owners.Length; index++)
                {
                    int owner = index;
                    lifetime.Acquire(out owners[owner], () =>
                    {
                        acquisitionOrder.Add(owner);
                        if (owner == failedIndex) { return ThrowConstructionAcquisition(failure); }
                        return new CallbackOwner(() => disposalOrder.Add(owner));
                    });
                }
            }));
            Assert.AreSame(failure, observed);
            CollectionAssert.AreEqual(Enumerable.Range(0, failedIndex + 1).ToArray(), acquisitionOrder);
            CollectionAssert.AreEqual(Enumerable.Range(0, failedIndex).ToArray(), disposalOrder);
            for (int index = failedIndex; index < owners.Length; index++) { Assert.IsNull(owners[index]); }
            lifetime.Dispose();
            CollectionAssert.AreEqual(Enumerable.Range(0, failedIndex).ToArray(), disposalOrder);
            Assert.ThrowsException<ObjectDisposedException>(lifetime.CheckActive);
        }

        [TestMethod]
        public void DisposalDuringAcquisitionReleasesReturnedOwnerAndStopsLaterAcquisitions()
        {
            for (int cycle = 0; cycle < 100; cycle++)
            {
                var calls = new List<string>();
                CallbackOwner? first = null, returned = null, later = null;
                var lifetime = new MainWindowLifetime(release =>
                {
                    release(() => first?.Dispose());
                    release(() => returned?.Dispose());
                    release(() => later?.Dispose());
                });
                lifetime.Acquire(out first, () => new CallbackOwner(() => calls.Add("first disposed")));
                Assert.ThrowsException<ObjectDisposedException>(() => CompleteConstruction(lifetime, () =>
                {
                    lifetime.Acquire(out returned, () =>
                    {
                        calls.Add("acquire late");
                        lifetime.Dispose();
                        return new CallbackOwner(() => calls.Add("returned disposed"));
                    });
                    lifetime.Acquire(out later, () =>
                    {
                        calls.Add("acquire later");
                        return new CallbackOwner(() => calls.Add("later disposed"));
                    });
                }));
                Assert.IsNull(returned);
                Assert.IsNull(later);
                lifetime.Dispose();
                CollectionAssert.AreEqual(new[] { "acquire late", "first disposed", "returned disposed" }, calls);
            }
        }

        [TestMethod]
        public void DisposedLifetimeRejectsActionsAndFactoriesBeforeTheirSideEffects()
        {
            int acquisitions = 0, initializations = 0;
            CallbackOwner? owner = null;
            var lifetime = new MainWindowLifetime(release => release(() => owner?.Dispose()));
            lifetime.Dispose();
            Assert.ThrowsException<ObjectDisposedException>(lifetime.CheckActive);
            Assert.ThrowsException<ObjectDisposedException>(() => lifetime.Run(() => initializations++));
            Assert.ThrowsException<ObjectDisposedException>(() => lifetime.Acquire(out owner, () =>
            {
                acquisitions++;
                return new CallbackOwner(() => { });
            }));
            Assert.ThrowsException<ObjectDisposedException>(() => lifetime.SubscribeAll(() =>
            {
                acquisitions++;
                return new CallbackOwner(() => { });
            }));
            Assert.AreEqual(0, acquisitions);
            Assert.AreEqual(0, initializations);
            Assert.IsNull(owner);
        }

        [TestMethod]
        public void PropertySetterFailureAfterPublicationReleasesOwnerAndPreservesOriginalError()
        {
            var failure = new InvalidOperationException("property initialization failed");
            var calls = new List<string>();
            ConstructionPropertyOwner? owner = null, created = null;
            var lifetime = new MainWindowLifetime(release => release(() => owner?.Dispose()));
            created = new ConstructionPropertyOwner(() =>
            {
                Assert.AreSame(created, owner, "The returned owner must be published before any observable property setter.");
                calls.Add("setter");
                ThrowConstructionSetter(failure);
            }, () => calls.Add("dispose"));
            var observed = Assert.ThrowsException<InvalidOperationException>(() => CompleteConstruction(lifetime, () =>
            {
                lifetime.Acquire(out owner, () => created!);
                lifetime.Run(() => owner!.Value = 1);
                lifetime.Run(() => calls.Add("later"));
            }));
            Assert.AreSame(failure, observed);
            StringAssert.Contains(observed.StackTrace!, nameof(ThrowConstructionSetter));
            lifetime.Dispose();
            CollectionAssert.AreEqual(new[] { "setter", "dispose" }, calls);
        }

        [TestMethod]
        public void DisposalFromPropertySetterStopsRemainingInitialization()
        {
            var calls = new List<string>();
            ConstructionPropertyOwner? owner = null;
            var lifetime = new MainWindowLifetime(release => release(() => owner?.Dispose()));
            Assert.ThrowsException<ObjectDisposedException>(() => CompleteConstruction(lifetime, () =>
            {
                lifetime.Acquire(out owner, () => new ConstructionPropertyOwner(() =>
                {
                    calls.Add("setter");
                    lifetime.Dispose();
                }, () => calls.Add("dispose")));
                lifetime.Run(() => owner!.Value = 1);
                lifetime.Run(() => calls.Add("later setter"));
            }));
            lifetime.Dispose();
            CollectionAssert.AreEqual(new[] { "setter", "dispose" }, calls);
        }

        [TestMethod]
        public void ExplicitNonDisposableOwnerReleasesLateResultWithoutPublishingIt()
        {
            int owner = 0, releases = 0;
            var lifetime = new MainWindowLifetime(_ => { });
            Assert.ThrowsException<ObjectDisposedException>(() => lifetime.Acquire(out owner, () =>
            {
                lifetime.Dispose();
                return 42;
            }, value => { Assert.AreEqual(42, value); releases++; }));
            Assert.AreEqual(0, owner);
            Assert.AreEqual(1, releases);
        }

        [TestMethod]
        public async Task ConcurrentDisposalCanFinishWhileAcquisitionIsBlockedAndReleasesLateOwner()
        {
            using var entered = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            CallbackOwner? owner = null;
            int cleanup = 0, lateRelease = 0;
            Exception? observed = null;
            var lifetime = new MainWindowLifetime(release =>
            {
                Interlocked.Increment(ref cleanup);
                release(() => owner?.Dispose());
            });
            var acquiring = Task.Run(() =>
            {
                try
                {
                    lifetime.Acquire(out owner, () =>
                    {
                        entered.Set();
                        Assert.IsTrue(resume.Wait(TimeSpan.FromSeconds(10)), "The controlled acquisition was not released.");
                        return new CallbackOwner(() => Interlocked.Increment(ref lateRelease));
                    });
                }
                catch (Exception error) { observed = error; }
            });
            Task? disposing = null;
            try
            {
                Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
                disposing = Task.Run(lifetime.Dispose);
                await disposing.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.AreEqual(1, Volatile.Read(ref cleanup));
                Assert.AreEqual(0, Volatile.Read(ref lateRelease));
            }
            finally
            {
                resume.Set();
                await acquiring.WaitAsync(TimeSpan.FromSeconds(10));
                if (disposing != null) { await disposing.WaitAsync(TimeSpan.FromSeconds(10)); }
            }
            Assert.IsInstanceOfType(observed, typeof(ObjectDisposedException));
            Assert.IsNull(owner);
            lifetime.Dispose();
            Assert.AreEqual(1, cleanup);
            Assert.AreEqual(1, lateRelease);
        }

        [TestMethod]
        public void SuccessfulAcquisitionPublishesOwnersUntilOneOrderedDisposal()
        {
            var calls = new List<string>();
            CallbackOwner? first = null, second = null;
            var lifetime = new MainWindowLifetime(release =>
            {
                release(() => first?.Dispose());
                release(() => second?.Dispose());
            });
            var created = new CallbackOwner(() => calls.Add("first"));
            lifetime.Acquire(out first, () => created);
            lifetime.Run(() => Assert.AreSame(created, first));
            lifetime.Acquire(out second, () => new CallbackOwner(() => calls.Add("second")));
            lifetime.CheckActive();
            Assert.AreEqual(0, calls.Count);
            lifetime.Dispose();
            lifetime.Dispose();
            CollectionAssert.AreEqual(new[] { "first", "second" }, calls);
        }

        [TestMethod]
        public void LateOwnerCleanupFailurePreservesDisposedAdmissionErrorAndDiagnostics()
        {
            CallbackOwner? owner = null;
            int releases = 0;
            var cleanupFailure = new ApplicationException("late owner release failed");
            var lifetime = new MainWindowLifetime(release => release(() => owner?.Dispose()));
            var observed = Assert.ThrowsException<ObjectDisposedException>(() => lifetime.Acquire(out owner, () =>
            {
                lifetime.Dispose();
                return new CallbackOwner(() => { releases++; throw cleanupFailure; });
            }));
            Assert.IsNull(owner);
            CollectionAssert.AreEqual(new Exception[] { cleanupFailure }, ConstructionCleanup(observed).InnerExceptions.ToArray());
            lifetime.ConstructionFailed(observed);
            lifetime.Dispose();
            Assert.AreEqual(1, releases);
            CollectionAssert.AreEqual(new Exception[] { cleanupFailure }, ConstructionCleanup(observed).InnerExceptions.ToArray());
        }

        [DataTestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        public void DisposalInsideSubscriptionReleasesEarlierChildrenAndStopsLaterFactories(int disposingFactory)
        {
            for (int cycle = 0; cycle < 100; cycle++)
            {
                var acquired = new List<int>();
                var released = new List<int>();
                CompositeDisposable? subscriptions = null;
                var lifetime = new MainWindowLifetime(release => MainWindowLifetime.DisposeSubscriptions(subscriptions, release));
                var factories = Enumerable.Range(0, 3).Select(index => (Func<IDisposable>)(() =>
                {
                    acquired.Add(index);
                    if (index == disposingFactory) { lifetime.Dispose(); }
                    return new CallbackOwner(() => released.Add(index));
                })).ToArray();
                Assert.ThrowsException<ObjectDisposedException>(() => CompleteConstruction(lifetime, () =>
                    lifetime.Acquire<CompositeDisposable>(out subscriptions, () => lifetime.SubscribeAll(factories), MainWindowLifetime.ReleaseSubscriptions)));
                CollectionAssert.AreEqual(Enumerable.Range(0, disposingFactory + 1).ToArray(), acquired);
                CollectionAssert.AreEquivalent(Enumerable.Range(0, disposingFactory + 1).ToArray(), released);
                Assert.IsNull(subscriptions);
                lifetime.Dispose();
                Assert.AreEqual(disposingFactory + 1, released.Count);
            }
        }

        [TestMethod]
        public void LateCompositeCleanupContinuesAfterFirstChildThrows()
        {
            var released = new List<int>();
            var failure = new ApplicationException("first child failed");
            var composite = new CompositeDisposable(
                new CallbackOwner(() => { released.Add(0); throw failure; }),
                new CallbackOwner(() => released.Add(1)),
                new CallbackOwner(() => released.Add(2)));
            CompositeDisposable? owner = null;
            var lifetime = new MainWindowLifetime(release => MainWindowLifetime.DisposeSubscriptions(owner, release));
            var observed = Assert.ThrowsException<ObjectDisposedException>(() => lifetime.Acquire<CompositeDisposable>(out owner, () =>
            {
                lifetime.Dispose();
                return composite;
            }, MainWindowLifetime.ReleaseSubscriptions));
            Assert.IsNull(owner);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, released);
            Assert.IsTrue(composite.IsDisposed);
            Assert.AreEqual(0, composite.Count);
            CollectionAssert.AreEqual(new Exception[] { failure }, ConstructionCleanup(observed).InnerExceptions.ToArray());
            lifetime.ConstructionFailed(observed);
            lifetime.Dispose();
            composite.Dispose();
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, released);
        }

        [TestMethod]
        public void ConstructionLifetimeCounterScenario()
        {
            const int cycles = 100;
            int attempts = 0, remaining = 0, errors = 0, duplicates = 0;
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                var owned = new[] { true, true, true };
                var owners = new CallbackOwner?[3];
                CallbackOwner? missing = null;
                var failure = new InvalidOperationException("constructor acquisition failed");
                var cleanupFailure = new ApplicationException("first owner cleanup failed");
                var lifetime = new MainWindowLifetime(release =>
                {
                    for (int index = 0; index < owners.Length; index++)
                    {
                        int owner = index;
                        release(() => owners[owner]?.Dispose());
                    }
                    release(() => missing?.Dispose());
                });
                var observed = Assert.ThrowsException<InvalidOperationException>(() => CompleteConstruction(lifetime, () =>
                {
                    for (int index = 0; index < owners.Length; index++)
                    {
                        int owner = index;
                        lifetime.Acquire(out owners[owner], () => new CallbackOwner(() =>
                        {
                            attempts++;
                            if (!owned[owner]) { duplicates++; }
                            owned[owner] = false;
                            if (owner == 0) { throw cleanupFailure; }
                        }));
                    }
                    lifetime.Acquire(out missing, () => ThrowConstructionAcquisition(failure));
                }));
                Assert.AreSame(failure, observed);
                StringAssert.Contains(observed.StackTrace!, nameof(ThrowConstructionAcquisition));
                Assert.IsNull(missing);
                errors++;
                lifetime.ConstructionFailed(observed);
                remaining += owned.Count(value => value);
            }
            Assert.AreEqual(cycles, errors);
            Assert.AreEqual(0, duplicates);
            Assert.AreEqual(cycles * 3, attempts + remaining);
            Console.WriteLine($"PERFCOUNTER construction-lifetime cycles {cycles}");
            Console.WriteLine($"PERFCOUNTER construction-lifetime cleanup-attempts {attempts}");
            Console.WriteLine($"PERFCOUNTER construction-lifetime remaining-owners {remaining}");
            Console.WriteLine($"PERFCOUNTER construction-lifetime original-errors {errors}");
            Console.WriteLine($"PERFCOUNTER construction-lifetime duplicate-cleanups {duplicates}");
        }

        private static void CompleteConstruction(MainWindowLifetime lifetime, Action initialize)
        {
            try { initialize(); }
            catch (Exception error)
            {
                lifetime.ConstructionFailed(error);
                throw;
            }
        }

        private static AggregateException ConstructionCleanup(Exception error)
        {
            var cleanup = error.Data["MainWindowLifetime.ConstructionCleanupExceptions"] as AggregateException;
            Assert.IsNotNull(cleanup);
            return cleanup!;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static CallbackOwner ThrowConstructionAcquisition(Exception error) => throw error;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowConstructionSetter(Exception error) => throw error;

        private sealed class ConstructionPropertyOwner(Action initialize, Action release) : IDisposable
        {
            public int Value { set => initialize(); }
            public void Dispose() => release();
        }
    }
}
