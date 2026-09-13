#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using FancyWM.Models;
using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    public partial class MainWindowLifetimeTest
    {
        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void DeferredInitializationFailureReportsOriginalAfterReleasingConstructedOwners(bool cleanupThrows)
        {
            using var source = new Subject<Settings>();
            using var reported = new ManualResetEventSlim();
            var ready = new TaskCompletionSource<object?>();
            var calls = new List<string>();
            var errors = new List<Exception>();
            var failure = new InvalidOperationException("deferred tiling Start failed");
            var cleanupFailure = new ApplicationException("earlier owner cleanup failed");
            CallbackOwner? earlier = null, tiling = null;
            CompositeDisposable? subscriptions = null;
            Task? initialization = null;
            int excluded = 0, starts = 0;
            var lifetime = new MainWindowLifetime(release =>
            {
                release(() => earlier?.Dispose());
                MainWindowLifetime.DisposeSubscriptions(subscriptions, release);
                release(() => tiling?.Dispose());
            });
            lifetime.Acquire(out earlier, () => new CallbackOwner(() =>
            {
                calls.Add("earlier cleanup");
                if (cleanupThrows) { throw cleanupFailure; }
            }));
            var initialize = RuntimeSettingsSubscription.Initialize(source, (_, cancellation) =>
                initialization = RunControlledInitializationAsync(lifetime, ready.Task, cancellation, () =>
                {
                    lifetime.Acquire(out tiling, () => new CallbackOwner(() => calls.Add("tiling cleanup")));
                    starts++;
                    ThrowDelayedInitialization(failure);
                }));
            var exclusions = Observable.Defer(() =>
            {
                excluded++;
                return Observable.Never<Unit>();
            });
            lifetime.Acquire<CompositeDisposable>(out subscriptions, () => lifetime.SubscribeAll(() =>
                initialize.Concat(exclusions).Subscribe(_ => { }, error =>
                {
                    try
                    {
                        lifetime.InitializationFailed(error, reportedError =>
                        {
                            calls.Add("report");
                            errors.Add(reportedError);
                        });
                    }
                    finally { reported.Set(); }
                })), MainWindowLifetime.ReleaseSubscriptions);
            try
            {
                source.OnNext(new Settings());
                Assert.IsNotNull(initialization);
                Assert.AreEqual(0, starts, "The constructor-owned subscriptions must exist before delayed startup runs.");
                ready.SetResult(null);
                var observed = Assert.ThrowsException<InvalidOperationException>(() =>
                    initialization!.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
                Assert.AreSame(failure, observed);
                Assert.IsTrue(reported.Wait(TimeSpan.FromSeconds(5)),
                    "Disposing the initialization subscription before its task faults must not swallow the original startup error.");
                Assert.AreEqual(1, errors.Count);
                Assert.AreSame(failure, errors.Single());
                StringAssert.Contains(failure.StackTrace!, nameof(ThrowDelayedInitialization));
                CollectionAssert.AreEqual(new[] { "earlier cleanup", "tiling cleanup", "report" }, calls);
                Assert.AreEqual(1, starts);
                Assert.AreEqual(0, excluded, "A failed startup must never subscribe its exclusion successor.");
                Assert.IsTrue(subscriptions!.IsDisposed);
                if (cleanupThrows)
                {
                    CollectionAssert.AreEqual(new Exception[] { cleanupFailure }, ConstructionCleanup(failure).InnerExceptions.ToArray());
                }
                else { Assert.IsFalse(failure.Data.Contains("MainWindowLifetime.ConstructionCleanupExceptions")); }
                source.OnNext(new Settings());
                lifetime.Dispose();
                CollectionAssert.AreEqual(new[] { "earlier cleanup", "tiling cleanup", "report" }, calls);
            }
            finally
            {
                ready.TrySetResult(null);
                lifetime.Dispose();
            }
        }

        [TestMethod]
        public void DisposalCancelsPendingInitializationWithoutReportingOrAcquiringTiling()
        {
            using var source = new Subject<Settings>();
            var ready = new TaskCompletionSource<object?>();
            var errors = new List<Exception>();
            CompositeDisposable? subscriptions = null;
            CallbackOwner? tiling = null;
            Task? initialization = null;
            int acquired = 0, excluded = 0, cleanup = 0;
            var lifetime = new MainWindowLifetime(release =>
            {
                cleanup++;
                MainWindowLifetime.DisposeSubscriptions(subscriptions, release);
                release(() => tiling?.Dispose());
            });
            var initialize = RuntimeSettingsSubscription.Initialize(source, (_, cancellation) =>
                initialization = RunControlledInitializationAsync(lifetime, ready.Task, cancellation, () =>
                {
                    acquired++;
                    lifetime.Acquire(out tiling, () => new CallbackOwner(() => { }));
                }));
            var exclusions = Observable.Defer(() =>
            {
                excluded++;
                return Observable.Never<Unit>();
            });
            lifetime.Acquire<CompositeDisposable>(out subscriptions, () => lifetime.SubscribeAll(() =>
                initialize.Concat(exclusions).Subscribe(_ => { }, error => lifetime.InitializationFailed(error, errors.Add))),
                MainWindowLifetime.ReleaseSubscriptions);
            source.OnNext(new Settings());
            Assert.IsNotNull(initialization);
            lifetime.Dispose();
            ready.SetResult(null);
            try { initialization!.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            Assert.IsTrue(initialization!.IsCanceled);
            Assert.IsTrue(subscriptions!.IsDisposed);
            Assert.AreEqual(0, errors.Count);
            Assert.AreEqual(0, acquired);
            Assert.AreEqual(0, excluded);
            Assert.AreEqual(1, cleanup);
            Assert.IsNull(tiling);
            lifetime.Dispose();
            Assert.AreEqual(1, cleanup);
        }

        [TestMethod]
        public void DisposalDuringDeferredFactoryReleasesReturnedTilingWithoutReportingShutdownFailure()
        {
            using var source = new Subject<Settings>();
            var ready = new TaskCompletionSource<object?>();
            var errors = new List<Exception>();
            CompositeDisposable? subscriptions = null;
            CallbackOwner? tiling = null;
            Task? initialization = null;
            int returnedCleanup = 0, excluded = 0, starts = 0;
            var lifetime = new MainWindowLifetime(release =>
            {
                MainWindowLifetime.DisposeSubscriptions(subscriptions, release);
                release(() => tiling?.Dispose());
            });
            var initialize = RuntimeSettingsSubscription.Initialize(source, (_, cancellation) =>
                initialization = RunControlledInitializationAsync(lifetime, ready.Task, cancellation, () =>
                {
                    lifetime.Acquire(out tiling, () =>
                    {
                        lifetime.Dispose();
                        return new CallbackOwner(() => returnedCleanup++);
                    });
                    lifetime.Run(() => starts++);
                }));
            var exclusions = Observable.Defer(() =>
            {
                excluded++;
                return Observable.Never<Unit>();
            });
            lifetime.Acquire<CompositeDisposable>(out subscriptions, () => lifetime.SubscribeAll(() =>
                initialize.Concat(exclusions).Subscribe(_ => { }, error => lifetime.InitializationFailed(error, errors.Add))),
                MainWindowLifetime.ReleaseSubscriptions);
            source.OnNext(new Settings());
            Assert.IsNotNull(initialization);
            ready.SetResult(null);
            Assert.ThrowsException<ObjectDisposedException>(() =>
                initialization!.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
            Assert.IsTrue(subscriptions!.IsDisposed);
            Assert.AreEqual(0, errors.Count, "The already canceled startup subscription must not report expected shutdown rejection.");
            Assert.AreEqual(0, starts);
            Assert.AreEqual(0, excluded);
            Assert.AreEqual(1, returnedCleanup);
            Assert.IsNull(tiling);
            lifetime.Dispose();
            Assert.AreEqual(1, returnedCleanup);
        }

        [TestMethod]
        public void BackgroundInitializationFailureQueuesCleanupAndReportingOnOwnerDispatcher()
        {
            using var initialized = new ManualResetEventSlim();
            using var allowPump = new ManualResetEventSlim();
            using var observerReturned = new ManualResetEventSlim();
            using var reported = new ManualResetEventSlim();
            var completion = new TaskCompletionSource<object?>();
            var failure = new InvalidOperationException("background initialization failed");
            Dispatcher? dispatcher = null;
            Exception? ownerFailure = null, reportedError = null;
            int ownerId = 0, producerId = 0, cleanupId = 0, reportId = 0;
            int cleanupCount = 0, reportCount = 0, excluded = 0;
            var ownerThread = new Thread(() =>
            {
                try
                {
                    var ownerDispatcher = Dispatcher.CurrentDispatcher;
                    dispatcher = ownerDispatcher;
                    ownerId = Environment.CurrentManagedThreadId;
                    using var source = new Subject<Settings>();
                    CompositeDisposable? subscriptions = null;
                    using var lifetime = new MainWindowLifetime(release =>
                    {
                        cleanupId = Environment.CurrentManagedThreadId;
                        Interlocked.Increment(ref cleanupCount);
                        MainWindowLifetime.DisposeSubscriptions(subscriptions, release);
                    });
                    var initialize = RuntimeSettingsSubscription.Initialize(source, (_, _) => completion.Task);
                    var exclusions = Observable.Defer(() =>
                    {
                        Interlocked.Increment(ref excluded);
                        return Observable.Never<Unit>();
                    });
                    lifetime.Acquire<CompositeDisposable>(out subscriptions, () => lifetime.SubscribeAll(() =>
                        initialize.Concat(exclusions).Subscribe(_ => { }, error =>
                        {
                            try
                            {
                                lifetime.InitializationFailed(error, observed =>
                                {
                                    reportedError = observed;
                                    reportId = Environment.CurrentManagedThreadId;
                                    Interlocked.Increment(ref reportCount);
                                    reported.Set();
                                    ownerDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                                }, work =>
                                {
                                    if (ownerDispatcher.CheckAccess()) { work(); }
                                    else { _ = ownerDispatcher.InvokeAsync(work); }
                                });
                            }
                            finally { observerReturned.Set(); }
                        })), MainWindowLifetime.ReleaseSubscriptions);
                    source.OnNext(new Settings());
                    initialized.Set();
                    if (!allowPump.Wait(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException("The owner Dispatcher was not released.");
                    }
                    Dispatcher.Run();
                }
                catch (Exception error) { ownerFailure = error; }
                finally { initialized.Set(); }
            }) { IsBackground = true, Name = "MainWindowLifetime.InitializationOwner" };
            ownerThread.SetApartmentState(ApartmentState.STA);
            ownerThread.Start();
            Task? producer = null;
            try
            {
                Assert.IsTrue(initialized.Wait(TimeSpan.FromSeconds(10)));
                Assert.IsNull(ownerFailure);
                producer = Task.Run(() =>
                {
                    producerId = Environment.CurrentManagedThreadId;
                    completion.SetException(failure);
                });
                producer.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                Assert.IsTrue(observerReturned.Wait(TimeSpan.FromSeconds(10)),
                    "Fault admission must return without waiting for the owner's blocked queue.");
                Assert.AreNotEqual(ownerId, producerId);
                Assert.AreEqual(0, Volatile.Read(ref cleanupCount), "Cleanup must not execute on the fault producer.");
                Assert.AreEqual(0, Volatile.Read(ref reportCount));
                allowPump.Set();
                Assert.IsTrue(reported.Wait(TimeSpan.FromSeconds(10)));
            }
            finally
            {
                allowPump.Set();
                dispatcher?.BeginInvokeShutdown(DispatcherPriority.Background);
                if (producer != null) { producer.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
                Assert.IsTrue(ownerThread.Join(TimeSpan.FromSeconds(10)), "The owned STA Dispatcher must exit.");
            }
            Assert.IsNull(ownerFailure);
            Assert.AreSame(failure, reportedError);
            Assert.AreEqual(ownerId, cleanupId);
            Assert.AreEqual(ownerId, reportId);
            Assert.AreEqual(1, cleanupCount);
            Assert.AreEqual(1, reportCount);
            Assert.AreEqual(0, excluded);
        }

        private static async Task RunControlledInitializationAsync(
            MainWindowLifetime lifetime,
            Task ready,
            CancellationToken cancellation,
            Action initialize)
        {
            await ready.WaitAsync(cancellation).ConfigureAwait(false);
            lifetime.Initialize(initialize);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowDelayedInitialization(Exception error) => throw error;
    }
}
