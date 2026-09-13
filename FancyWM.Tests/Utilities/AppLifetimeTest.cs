#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public partial class AppLifetimeTest
    {
        [TestMethod]
        public void SuccessPreservesDispatcherAndWindowThenMouseOrderAcross100Cycles()
        {
            for (int cycle = 0; cycle < 100; cycle++)
            {
                var calls = new List<string>();
                var windows = new[] { new WindowOwner("a", calls), new WindowOwner("b", calls) };
                bool insideDispatch = false;
                App.CloseOwnedWindows(
                    () =>
                    {
                        Assert.IsTrue(insideDispatch, "Window enumeration must occur inside the synchronous dispatcher action.");
                        calls.Add("snapshot");
                        return windows;
                    },
                    window => window.Dispose(),
                    window => window.Close(),
                    action =>
                    {
                        calls.Add("dispatch:enter");
                        insideDispatch = true;
                        try { action(); }
                        finally { insideDispatch = false; calls.Add("dispatch:exit"); }
                    },
                    () =>
                    {
                        Assert.IsFalse(insideDispatch, "Keep the existing mouse cleanup after the window dispatcher action returns.");
                        calls.Add("mouse");
                    });
                CollectionAssert.AreEqual(new[]
                {
                    "dispatch:enter", "snapshot", "dispose:a", "close:a", "dispose:b", "close:b", "dispatch:exit", "mouse"
                }, calls);
            }
        }

        [TestMethod]
        [DataRow(0)]
        [DataRow(1)]
        public void DisposeFailureStillClosesSameAndLaterWindowsAndMouse(int failingWindow)
        {
            for (int cycle = 0; cycle < 100; cycle++)
            {
                var calls = new List<string>();
                var failure = new InvalidOperationException("dispose failed");
                var windows = new[] { new WindowOwner("a", calls), new WindowOwner("b", calls) };
                windows[failingWindow].OnDispose = () => throw failure;
                var observed = Assert.ThrowsException<AggregateException>(() => Close(windows, calls));
                CollectionAssert.AreEqual(new Exception[] { failure }, observed.InnerExceptions.ToArray());
                CollectionAssert.AreEqual(new[] { "dispose:a", "close:a", "dispose:b", "close:b", "mouse" }, calls);
            }
        }

        [TestMethod]
        [DataRow(0)]
        [DataRow(1)]
        public void CloseFailureStillClosesLaterWindowsAndMouse(int failingWindow)
        {
            for (int cycle = 0; cycle < 100; cycle++)
            {
                var calls = new List<string>();
                var failure = new ApplicationException("close failed");
                var windows = new[] { new WindowOwner("a", calls), new WindowOwner("b", calls) };
                windows[failingWindow].OnClose = () => throw failure;
                var observed = Assert.ThrowsException<AggregateException>(() => Close(windows, calls));
                CollectionAssert.AreEqual(new Exception[] { failure }, observed.InnerExceptions.ToArray());
                CollectionAssert.AreEqual(new[] { "dispose:a", "close:a", "dispose:b", "close:b", "mouse" }, calls);
            }
        }

        [TestMethod]
        public void InvalidOperationFromCloseIsIgnoredWhileDisposeErrorIsPreserved()
        {
            var calls = new List<string>();
            var disposeFailure = new InvalidOperationException("dispose must be reported");
            var closeFailure = new InvalidOperationException("existing already-closing contract");
            var windows = new[] { new WindowOwner("a", calls), new WindowOwner("b", calls) };
            windows[0].OnDispose = () => throw disposeFailure;
            windows[0].OnClose = () => throw closeFailure;
            var observed = Assert.ThrowsException<AggregateException>(() => Close(windows, calls));
            CollectionAssert.AreEqual(new Exception[] { disposeFailure }, observed.InnerExceptions.ToArray());
            CollectionAssert.AreEqual(new[] { "dispose:a", "close:a", "dispose:b", "close:b", "mouse" }, calls);
        }

        [TestMethod]
        public void AlreadyClosingWindowDoesNotAbortRemainingWindows()
        {
            var calls = new List<string>();
            var windows = new[] { new WindowOwner("a", calls), new WindowOwner("b", calls) };
            windows[0].OnClose = () => throw new InvalidOperationException("already closing");
            Close(windows, calls);
            CollectionAssert.AreEqual(new[] { "dispose:a", "close:a", "dispose:b", "close:b", "mouse" }, calls);
        }

        [TestMethod]
        public void MultipleDisposeCloseAndMouseFailuresAggregateInEncounterOrder()
        {
            var calls = new List<string>();
            var errors = new Exception[]
            {
                new InvalidOperationException("dispose:a"), new ApplicationException("close:a"),
                new ArgumentException("dispose:b"), new ApplicationException("close:b"),
                new InvalidOperationException("mouse")
            };
            var windows = new[] { new WindowOwner("a", calls), new WindowOwner("b", calls) };
            windows[0].OnDispose = () => throw errors[0];
            windows[0].OnClose = () => throw errors[1];
            windows[1].OnDispose = () => throw errors[2];
            windows[1].OnClose = () => throw errors[3];
            var observed = Assert.ThrowsException<AggregateException>(() => App.CloseOwnedWindows(
                () => windows, window => window.Dispose(), window => window.Close(), action => action(),
                () => { calls.Add("mouse"); throw errors[4]; }));
            StringAssert.StartsWith(observed.Message, "Application shutdown failed!");
            CollectionAssert.AreEqual(errors, observed.InnerExceptions.ToArray());
            CollectionAssert.AreEqual(new[] { "dispose:a", "close:a", "dispose:b", "close:b", "mouse" }, calls);
        }

        [TestMethod]
        public void DispatcherFailureBeforeCallbackStillAttemptsMouseCleanup()
        {
            var calls = new List<string>();
            var failure = new InvalidOperationException("dispatcher unavailable");
            var observed = Assert.ThrowsException<AggregateException>(() => App.CloseOwnedWindows<WindowOwner>(
                () => { calls.Add("snapshot"); return []; },
                window => window.Dispose(), window => window.Close(),
                _ => { calls.Add("dispatch"); throw failure; },
                () => calls.Add("mouse")));
            CollectionAssert.AreEqual(new Exception[] { failure }, observed.InnerExceptions.ToArray());
            CollectionAssert.AreEqual(new[] { "dispatch", "mouse" }, calls);
        }

        [TestMethod]
        public void DispatcherFailureAfterCallbackAndMouseFailureFollowWindowErrors()
        {
            var calls = new List<string>();
            var windowFailure = new ApplicationException("window");
            var dispatcherFailure = new InvalidOperationException("dispatcher after callback");
            var mouseFailure = new ArgumentException("mouse");
            var window = new WindowOwner("a", calls) { OnDispose = () => throw windowFailure };
            var observed = Assert.ThrowsException<AggregateException>(() => App.CloseOwnedWindows(
                () => new[] { window }, owner => owner.Dispose(), owner => owner.Close(),
                action => { calls.Add("dispatch:enter"); action(); calls.Add("dispatch:exit"); throw dispatcherFailure; },
                () => { calls.Add("mouse"); throw mouseFailure; }));
            CollectionAssert.AreEqual(new Exception[] { windowFailure, dispatcherFailure, mouseFailure }, observed.InnerExceptions.ToArray());
            CollectionAssert.AreEqual(new[] { "dispatch:enter", "dispose:a", "close:a", "dispatch:exit", "mouse" }, calls);
        }

        [TestMethod]
        public void SnapshotEnumerationFailureDoesNotMutatePartialSnapshotAndStillCleansMouse()
        {
            var calls = new List<string>();
            var failure = new ApplicationException("enumeration failed");
            var window = new WindowOwner("a", calls);
            IEnumerable<WindowOwner> Enumerate()
            {
                calls.Add("enumerate:first");
                yield return window;
                calls.Add("enumerate:failure");
                throw failure;
            }
            var observed = Assert.ThrowsException<AggregateException>(() => App.CloseOwnedWindows(
                Enumerate, owner => owner.Dispose(), owner => owner.Close(), action => action(), () => calls.Add("mouse")));
            CollectionAssert.AreEqual(new Exception[] { failure }, observed.InnerExceptions.ToArray());
            CollectionAssert.AreEqual(new[] { "enumerate:first", "enumerate:failure", "mouse" }, calls);
        }

        [TestMethod]
        public void SnapshotMutationPreservesOriginalWindowsAndExcludesNewWindows()
        {
            for (int cycle = 0; cycle < 100; cycle++)
            {
                var calls = new List<string>();
                var first = new WindowOwner("a", calls);
                var second = new WindowOwner("b", calls);
                var added = new WindowOwner("c", calls);
                var windows = new List<WindowOwner> { first, second };
                first.OnDispose = () => { Assert.IsTrue(windows.Remove(second)); windows.Add(added); };
                first.OnClose = () => Assert.IsTrue(windows.Remove(first));
                Close(windows, calls);
                CollectionAssert.AreEqual(new[] { "dispose:a", "close:a", "dispose:b", "close:b", "mouse" }, calls);
                CollectionAssert.AreEqual(new[] { added }, windows);
            }
        }

        [TestMethod]
        public void NestedCleanupAggregateRemainsAnOriginalError()
        {
            var calls = new List<string>();
            var nested = new AggregateException("owner cleanup", new InvalidOperationException("one"), new ArgumentException("two"));
            var windows = new[] { new WindowOwner("a", calls) { OnDispose = () => throw nested } };
            var observed = Assert.ThrowsException<AggregateException>(() => Close(windows, calls));
            CollectionAssert.AreEqual(new Exception[] { nested }, observed.InnerExceptions.ToArray());
            CollectionAssert.AreEqual(new[] { "dispose:a", "close:a", "mouse" }, calls);
        }

        [TestMethod]
        public void EmptySnapshotStillAttemptsMouseCleanup()
        {
            var calls = new List<string>();
            Close([], calls);
            CollectionAssert.AreEqual(new[] { "mouse" }, calls);
        }

        [TestMethod]
        public void MouseOnlyFailureIsAggregatedWithOriginalIdentity()
        {
            var failure = new InvalidOperationException("mouse failed");
            int mouseAttempts = 0;
            var observed = Assert.ThrowsException<AggregateException>(() => App.CloseOwnedWindows<WindowOwner>(
                () => [], window => window.Dispose(), window => window.Close(), action => action(),
                () => { mouseAttempts++; throw failure; }));
            CollectionAssert.AreEqual(new Exception[] { failure }, observed.InnerExceptions.ToArray());
            Assert.AreEqual(1, mouseAttempts);
        }

        [TestMethod]
        public void AppShutdownCounterScenario()
        {
            const int cycles = 100;
            int disposes = 0, closes = 0, mice = 0, errors = 0, remaining = 0;
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                var calls = new List<string>();
                var failure = new InvalidOperationException("first disposal failed");
                var disposed = new int[3];
                var closed = new int[3];
                var windows = Enumerable.Range(0, 3).Select(index => new WindowOwner(index.ToString(), calls)
                {
                    OnDispose = () =>
                    {
                        disposed[index]++;
                        disposes++;
                        if (index == 0) { throw failure; }
                    },
                    OnClose = () => { closed[index]++; closes++; }
                }).ToArray();
                try
                {
                    App.CloseOwnedWindows(() => windows, window => window.Dispose(), window => window.Close(),
                        action => action(), () => mice++);
                    Assert.Fail("The known disposal failure must remain observable.");
                }
                catch (AggregateException observed)
                {
                    CollectionAssert.AreEqual(new Exception[] { failure }, observed.InnerExceptions.ToArray());
                    errors++;
                }
                CollectionAssert.AreEqual(new[] { 1, 1, 1 }, disposed);
                Assert.IsTrue(closed.All(count => count <= 1), "Cleanup must never close the same snapshot entry twice.");
                remaining += closed.Count(count => count == 0);
            }
            Assert.AreEqual(cycles, errors);
            Assert.AreEqual(cycles * 3, disposes);
            Assert.AreEqual(cycles * 3, closes + remaining);
            Console.WriteLine($"PERFCOUNTER app-shutdown cycles {cycles}");
            Console.WriteLine($"PERFCOUNTER app-shutdown dispose-attempts {disposes}");
            Console.WriteLine($"PERFCOUNTER app-shutdown close-attempts {closes}");
            Console.WriteLine($"PERFCOUNTER app-shutdown mouse-cleanups {mice}");
            Console.WriteLine($"PERFCOUNTER app-shutdown original-errors {errors}");
        }

        private static void Close(IEnumerable<WindowOwner> windows, List<string> calls) => App.CloseOwnedWindows(
            () => windows, window => window.Dispose(), window => window.Close(), action => action(), () => calls.Add("mouse"));

        private sealed class WindowOwner(string name, List<string> calls)
        {
            public Action? OnDispose { get; set; }
            public Action? OnClose { get; set; }

            // Deliberately count every call: idempotent fakes would hide duplicates.
            public void Dispose() { calls.Add($"dispose:{name}"); OnDispose?.Invoke(); }
            public void Close() { calls.Add($"close:{name}"); OnClose?.Invoke(); }
        }
    }
}
