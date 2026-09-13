#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;

using FancyWM.Utilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class BindingErrorListenerTest
    {
        [TestMethod]
        public void RepeatedListenerLifetimesRemoveGlobalSubscriptions()
        {
            var listeners = PresentationTraceSources.DataBindingSource.Listeners;
            int initial = listeners.Count;
            for (int cycle = 0; cycle < 100; cycle++)
            {
                var listener = BindingErrorListener.Listen(_ => { });
                Assert.AreEqual(initial + 1, listeners.Count);
                listener.Dispose();
                listener.Dispose();
                Assert.AreEqual(initial, listeners.Count);
            }
        }

        [TestMethod]
        public void WholeLinesIncludingNullAreForwardedAndPartialWritesRemainSilent()
        {
            var messages = new List<string?>();
            using var listener = new BindingErrorListener(messages.Add);
            listener.Write("partial");
            listener.Write(null);
            listener.WriteLine(null);
            listener.WriteLine("");
            listener.WriteLine("first");
            listener.WriteLine("second");
            CollectionAssert.AreEqual(new string?[] { null, "", "first", "second" }, messages);
        }

        [TestMethod]
        public void NullCallbackRetainsItsNoOpContractAcrossRepeatedDisposal()
        {
            int initial = PresentationTraceSources.DataBindingSource.Listeners.Count;
            // The original nullable invocation accepts a runtime null callback.
            // Keep that behavior rather than introducing an unrelated argument check.
            var listener = BindingErrorListener.Listen(null!);
            try
            {
                listener.Write("partial");
                listener.WriteLine(null);
                listener.WriteLine("line");
                listener.Dispose();
                listener.Dispose();
                listener.WriteLine("late");
                Assert.AreEqual(initial, PresentationTraceSources.DataBindingSource.Listeners.Count);
            }
            finally { listener.Dispose(); }
        }

        [TestMethod]
        public void WriteTargetCapturedBeforeDisposalCannotNotifyAfterItReturns()
        {
            var messages = new List<string?>();
            using var listener = new BindingErrorListener(messages.Add);
            Action<string?> captured = listener.WriteLine;
            captured("live");
            listener.Dispose();
            listener.Dispose();
            captured("late");
            captured(null);
            listener.Write("ignored");
            CollectionAssert.AreEqual(new[] { "live" }, messages);
        }

        [TestMethod]
        public void HundredRetainedDisposedListenersReleaseTheirCallbackOwners()
        {
            const int cycles = 100;
            int initial = PresentationTraceSources.DataBindingSource.Listeners.Count;
            var counts = new NotificationCounts();
            var observed = CreateObservedListeners(counts, cycles);
            try
            {
                Assert.AreEqual(initial + cycles, PresentationTraceSources.DataBindingSource.Listeners.Count);
                CollectOwners();
                Assert.AreEqual(cycles, CountAlive(observed.Owners), "An active listener must retain its callback owner.");
                DisposeAll(observed.Listeners);
                CollectOwners();
                Assert.AreEqual(0, CountAlive(observed.Owners),
                    "Retaining a disposed trace listener must not retain the callback's owner.");
                Assert.AreEqual(cycles, counts.Live);
                Assert.AreEqual(initial, PresentationTraceSources.DataBindingSource.Listeners.Count);
                GC.KeepAlive(observed.Listeners);
            }
            finally { DisposeAll(observed.Listeners); }
        }

        [TestMethod]
        public void AdmittedCallbackCanFinishAfterDisposalWithoutBlockingIt()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var disposed = new ManualResetEventSlim();
            int invocations = 0;
            int completions = 0;
            Exception? writerFailure = null;
            Exception? disposerFailure = null;
            using var listener = new BindingErrorListener(message =>
            {
                Interlocked.Increment(ref invocations);
                if (message == "admitted")
                {
                    entered.Set();
                    Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(10)), "Owned callback was not released.");
                    Interlocked.Increment(ref completions);
                }
            });
            var writer = new Thread(() =>
            {
                try { listener.WriteLine("admitted"); }
                catch (Exception exception) { writerFailure = exception; }
            }) { IsBackground = true };
            var disposer = new Thread(() =>
            {
                try { listener.Dispose(); }
                catch (Exception exception) { disposerFailure = exception; }
                finally { disposed.Set(); }
            }) { IsBackground = true };
            bool disposerStarted = false;
            writer.Start();
            try
            {
                Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)), "Owned callback did not enter.");
                disposer.Start();
                disposerStarted = true;
                Assert.IsTrue(disposed.Wait(TimeSpan.FromSeconds(10)),
                    "Dispose must not wait on a gate held by arbitrary callback code.");
                if (disposerFailure != null) ExceptionDispatchInfo.Capture(disposerFailure).Throw();
                Assert.AreEqual(0, Volatile.Read(ref completions));
                listener.WriteLine("late");
                Assert.AreEqual(1, Volatile.Read(ref invocations));
            }
            finally
            {
                release.Set();
                bool writerJoined = writer.Join(TimeSpan.FromSeconds(10));
                bool disposerJoined = !disposerStarted || disposer.Join(TimeSpan.FromSeconds(10));
                listener.Dispose();
                Assert.IsTrue(writerJoined, "Owned writer did not finish.");
                Assert.IsTrue(disposerJoined, "Owned disposer did not finish.");
            }
            if (writerFailure != null) ExceptionDispatchInfo.Capture(writerFailure).Throw();
            if (disposerFailure != null) ExceptionDispatchInfo.Capture(disposerFailure).Throw();
            Assert.AreEqual(1, invocations);
            Assert.AreEqual(1, completions);
        }

        [TestMethod]
        public void ReentrantCallbackMayDisposeAndRejectNestedLaterWrites()
        {
            var stages = new List<string>();
            BindingErrorListener? listener = null;
            listener = new BindingErrorListener(message =>
            {
                stages.Add(message + ":enter");
                if (message == "outer")
                {
                    listener!.WriteLine("inner");
                }
                else if (message == "inner")
                {
                    listener!.Dispose();
                    // Keep this finite on the old production code too: neither
                    // late message re-enters the outer/inner branches.
                    listener!.WriteLine("late-inner");
                }
                stages.Add(message + ":exit");
            });
            try
            {
                listener.WriteLine("outer");
                listener.WriteLine("late-after");
                CollectionAssert.AreEqual(new[] { "outer:enter", "inner:enter", "inner:exit", "outer:exit" }, stages);
            }
            finally { listener.Dispose(); }
        }

        [TestMethod]
        public void CallbackExceptionPropagatesUnchangedAndKeepsAnActiveListenerUsable()
        {
            var expected = new InvalidOperationException("Injected binding callback failure.");
            var messages = new List<string?>();
            using var listener = new BindingErrorListener(message =>
            {
                messages.Add(message);
                if (messages.Count == 1) throw expected;
            });
            Assert.AreSame(expected, Assert.ThrowsException<InvalidOperationException>(() => listener.WriteLine(null)));
            listener.WriteLine("after-failure");
            CollectionAssert.AreEqual(new string?[] { null, "after-failure" }, messages);
            listener.Dispose();
            listener.WriteLine("late");
            CollectionAssert.AreEqual(new string?[] { null, "after-failure" }, messages);
        }

        [TestMethod]
        public void BindingListenerCounterScenario()
        {
            const int cycles = 100;
            var counts = new NotificationCounts();
            var observation = MeasureDisposedOwnerRetention(counts, cycles);
            // The no-inline measurement method has now dropped its strong list
            // of disposed listeners. Only weak owner references leave it.
            CollectOwners();
            int finalRetainedOwners = CountAlive(observation.Owners);
            Assert.AreEqual(cycles, counts.Live);
            Assert.IsTrue(counts.Late >= 0 && counts.Late <= cycles);
            Assert.IsTrue(observation.RetainedOwners >= 0 && observation.RetainedOwners <= cycles);
            Assert.AreEqual(0, finalRetainedOwners);
            Assert.AreEqual(0, observation.RemainingSubscriptions);
            Console.WriteLine($"PERFCOUNTER binding-listener cycles {cycles}");
            Console.WriteLine($"PERFCOUNTER binding-listener live-notifications {counts.Live}");
            Console.WriteLine($"PERFCOUNTER binding-listener late-notifications {counts.Late}");
            Console.WriteLine($"PERFCOUNTER binding-listener retained-owners {observation.RetainedOwners}");
            Console.WriteLine($"PERFCOUNTER binding-listener final-retained-owners {finalRetainedOwners}");
            Console.WriteLine($"PERFCOUNTER binding-listener remaining-subscriptions {observation.RemainingSubscriptions}");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static (WeakReference[] Owners, int RetainedOwners, int RemainingSubscriptions)
            MeasureDisposedOwnerRetention(NotificationCounts counts, int cycles)
        {
            int initial = PresentationTraceSources.DataBindingSource.Listeners.Count;
            var observed = CreateObservedListeners(counts, cycles);
            try
            {
                Assert.AreEqual(initial + cycles, PresentationTraceSources.DataBindingSource.Listeners.Count);
                DisposeAll(observed.Listeners);
                foreach (var listener in observed.Listeners) listener.WriteLine("late");
                CollectOwners();
                int retainedOwners = CountAlive(observed.Owners);
                int remainingSubscriptions = PresentationTraceSources.DataBindingSource.Listeners.Count - initial;
                GC.KeepAlive(observed.Listeners);
                return (observed.Owners, retainedOwners, remainingSubscriptions);
            }
            finally { DisposeAll(observed.Listeners); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static (BindingErrorListener[] Listeners, WeakReference[] Owners)
            CreateObservedListeners(NotificationCounts counts, int count)
        {
            var listeners = new List<BindingErrorListener>(count);
            var owners = new WeakReference[count];
            try
            {
                for (int index = 0; index < count; index++)
                {
                    var owner = new FakeLogger(counts);
                    var listener = BindingErrorListener.Listen(owner.WriteLine);
                    listeners.Add(listener);
                    owners[index] = new WeakReference(owner);
                    listener.WriteLine("live");
                }
                return (listeners.ToArray(), owners);
            }
            catch
            {
                DisposeAll(listeners);
                throw;
            }
        }

        private static void DisposeAll(IEnumerable<BindingErrorListener> listeners)
        {
            foreach (var listener in listeners)
            {
                listener.Dispose();
                listener.Dispose();
            }
        }

        private static void CollectOwners()
        {
            // Retention characterization only. This scenario has no timed path,
            // allocation threshold, process-memory or CPU claim.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private static int CountAlive(IEnumerable<WeakReference> owners) => owners.Count(owner => owner.IsAlive);

        private sealed class NotificationCounts
        {
            internal int Live;
            internal int Late;
        }

        private sealed class FakeLogger(NotificationCounts counts)
        {
            internal void WriteLine(string? message)
            {
                if (message == "live") counts.Live++;
                else if (message == "late") counts.Late++;
            }
        }
    }
}
