#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

using FancyWM.Models;
using FancyWM.Tests.TestUtilities;
using FancyWM.ViewModels;
using FancyWM.Windows;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Serilog;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class SettingsWindowCloseTest
    {
        private const string ChildFlag = "FANCYWM_SETTINGS_WINDOW_CLOSE_CHILD";
        private const string ChildValue = "owned-fancywm-application";
        private const string CounterChildValue = "owned-fancywm-application-counter";
        private const string TestName = "FancyWM.Tests.Utilities.SettingsWindowCloseTest.ViewModelFailureStillCompletesOwnerCleanup";
        private const string CounterTestName = "FancyWM.Tests.Utilities.SettingsWindowCloseTest.SettingsWindowCloseCounterScenario";

        public TestContext TestContext { get; set; } = null!;

        [TestMethod]
        public void CompleteCloseRunsEveryOperationInOrder()
        {
            var order = new List<string>();

            SettingsWindow.CompleteClose(
                Record(order, "pages"),
                _ => Assert.Fail("Successful page disposal must not be reported."),
                Record(order, "closed"),
                Record(order, "view-model"),
                Record(order, "logical-focus"),
                Record(order, "keyboard-focus"),
                Record(order, "collection"));

            CollectionAssert.AreEqual(new[]
            {
                "pages", "closed", "view-model", "logical-focus", "keyboard-focus", "collection",
            }, order);
        }

        [TestMethod]
        public void CompleteCloseReportsAndSuppressesPageDisposalFailure()
        {
            var order = new List<string>();
            var pageFailure = new InvalidOperationException("controlled settings-page cleanup failure");
            Exception? reported = null;

            SettingsWindow.CompleteClose(
                Record(order, "pages", pageFailure),
                error =>
                {
                    order.Add("report-pages");
                    reported = error;
                },
                Record(order, "closed"),
                Record(order, "view-model"),
                Record(order, "logical-focus"),
                Record(order, "keyboard-focus"),
                Record(order, "collection"));

            Assert.AreSame(pageFailure, reported);
            CollectionAssert.AreEqual(new[]
            {
                "pages", "report-pages", "closed", "view-model", "logical-focus", "keyboard-focus", "collection",
            }, order);
        }

        [TestMethod]
        public void CompleteClosePreservesLoggerFailureAndStillRunsOwnerCleanup()
        {
            var order = new List<string>();
            var pageFailure = new InvalidOperationException("controlled settings-page cleanup failure");
            var loggerFailure = new InvalidOperationException("controlled settings-page logger failure");
            var viewModelFailure = new InvalidOperationException("controlled settings view-model failure");

            var actual = Assert.ThrowsException<InvalidOperationException>(() => SettingsWindow.CompleteClose(
                Record(order, "pages", pageFailure),
                error =>
                {
                    order.Add("report-pages");
                    Assert.AreSame(pageFailure, error);
                    ThrowControlledFailure(loggerFailure);
                },
                Record(order, "closed"),
                Record(order, "view-model", viewModelFailure),
                Record(order, "logical-focus"),
                Record(order, "keyboard-focus"),
                Record(order, "collection")));

            Assert.AreSame(loggerFailure, actual);
            StringAssert.Contains(actual.StackTrace ?? string.Empty, nameof(ThrowControlledFailure));
            var supplemental = (AggregateException)actual.Data["SettingsWindow.OnClosedExceptions"]!;
            Assert.AreEqual(1, supplemental.InnerExceptions.Count);
            Assert.AreSame(viewModelFailure, supplemental.InnerExceptions[0]);
            CollectionAssert.AreEqual(new[]
            {
                "pages", "report-pages", "closed", "view-model", "logical-focus", "keyboard-focus", "collection",
            }, order);
        }

        [TestMethod]
        public void CompleteClosePreservesFirstFailureAndRecordsLaterFailuresInOrder()
        {
            var order = new List<string>();
            var closedFailure = new InvalidOperationException("controlled Closed failure");
            var existingFailure = new InvalidOperationException("existing supplemental failure");
            var viewModelFailure = new InvalidOperationException("controlled view-model failure");
            var logicalFocusFailure = new InvalidOperationException("controlled logical-focus failure");
            var keyboardFocusFailure = new InvalidOperationException("controlled keyboard-focus failure");
            var collectionFailure = new InvalidOperationException("controlled collection failure");
            closedFailure.Data["SettingsWindow.OnClosedExceptions"] = new AggregateException(existingFailure);

            var actual = Assert.ThrowsException<InvalidOperationException>(() => SettingsWindow.CompleteClose(
                Record(order, "pages"),
                _ => Assert.Fail("Successful page disposal must not be reported."),
                Record(order, "closed", closedFailure),
                Record(order, "view-model", viewModelFailure),
                Record(order, "logical-focus", logicalFocusFailure),
                Record(order, "keyboard-focus", keyboardFocusFailure),
                Record(order, "collection", collectionFailure)));

            Assert.AreSame(closedFailure, actual);
            StringAssert.Contains(actual.StackTrace ?? string.Empty, nameof(ThrowControlledFailure));
            var supplemental = (AggregateException)actual.Data["SettingsWindow.OnClosedExceptions"]!;
            CollectionAssert.AreEqual(new Exception[]
            {
                existingFailure, viewModelFailure, logicalFocusFailure, keyboardFocusFailure, collectionFailure,
            }, new List<Exception>(supplemental.InnerExceptions));
            CollectionAssert.AreEqual(new[]
            {
                "pages", "closed", "view-model", "logical-focus", "keyboard-focus", "collection",
            }, order);
        }

        [TestMethod]
        public void CompleteCloseDoesNotMaskFirstFailureWhenExceptionDataIsUnavailable()
        {
            var order = new List<string>();
            var closedFailure = new ThrowingDataException("controlled Closed failure");
            var viewModelFailure = new InvalidOperationException("controlled view-model failure");

            var actual = Assert.ThrowsException<ThrowingDataException>(() => SettingsWindow.CompleteClose(
                Record(order, "pages"),
                _ => Assert.Fail("Successful page disposal must not be reported."),
                Record(order, "closed", closedFailure),
                Record(order, "view-model", viewModelFailure),
                Record(order, "logical-focus"),
                Record(order, "keyboard-focus"),
                Record(order, "collection")));

            Assert.AreSame(closedFailure, actual);
            CollectionAssert.AreEqual(new[]
            {
                "pages", "closed", "view-model", "logical-focus", "keyboard-focus", "collection",
            }, order);
        }

        [TestMethod]
        public async Task ViewModelFailureStillCompletesOwnerCleanup()
        {
            if (Environment.GetEnvironmentVariable(ChildFlag) == ChildValue)
            {
                RunOnSta(VerifyActualWindowClose);
                return;
            }

            var originalApplication = Application.Current;
            await IsolatedTestProcess.RunAsync(TestContext, typeof(SettingsWindowCloseTest), TestName,
                ChildFlag, ChildValue, "settings-window-close-child");
            Assert.AreSame(originalApplication, Application.Current);
        }

        [TestMethod]
        public async Task SettingsWindowCloseCounterScenario()
        {
            if (Environment.GetEnvironmentVariable(ChildFlag) == CounterChildValue)
            {
                RunOnSta(VerifyCounterScenario);
                return;
            }

            var originalApplication = Application.Current;
            await IsolatedTestProcess.RunAsync(TestContext, typeof(SettingsWindowCloseTest), CounterTestName,
                ChildFlag, CounterChildValue, "settings-window-close-counter-child");
            Assert.AreSame(originalApplication, Application.Current);
        }

        private static void VerifyActualWindowClose()
        {
            Assert.IsNull(Application.Current);
            var logger = new LoggerConfiguration().CreateLogger();
            using var services = new ServiceCollection().AddSingleton<ILogger>(logger).BuildServiceProvider();
            var application = new App(services) { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.InitializeComponent();
            try
            {
                var failure = new InvalidOperationException("controlled settings-window view-model cleanup failure");
                var entity = new ThrowingSettingsEntity(failure);
                var viewModel = new SettingsViewModel(entity, logger, () => Task.FromResult(false));
                var window = new SettingsWindow(viewModel);
                int closed = 0;
                var collectionOperations = new List<DispatcherOperation>();
                window.Closed += (_, _) =>
                {
                    Assert.AreEqual(0, entity.DisposeAttempts,
                        "The base Closed event must retain its original position before view-model cleanup.");
                    closed++;
                    window.Close();
                };
                FocusManager.SetFocusedElement(window, window);
                Assert.AreSame(window, FocusManager.GetFocusedElement(window));
                Assert.AreEqual(IntPtr.Zero, new WindowInteropHelper(window).Handle);
                DispatcherHookEventHandler onPosted = (_, args) =>
                {
                    if (args.Operation.Priority == DispatcherPriority.ApplicationIdle)
                    {
                        lock (collectionOperations) collectionOperations.Add(args.Operation);
                    }
                };
                window.Dispatcher.Hooks.OperationPosted += onPosted;
                try
                {
                    var actual = Assert.ThrowsException<InvalidOperationException>(window.Close);
                    window.Close();
                    Assert.AreSame(failure, actual);
                    Assert.AreEqual(1, closed, "The base Closed event must retain its original position before view-model cleanup.");
                    Assert.AreEqual(1, entity.DisposeAttempts);
                    Assert.AreEqual(1, entity.ReleaseCount);
                    Assert.IsNull(FocusManager.GetFocusedElement(window),
                        "A failing view-model release must not retain logical focus on the closed window.");
                    Assert.IsNull(Keyboard.FocusedElement);
                    lock (collectionOperations) Assert.AreEqual(1, collectionOperations.Count,
                        "The existing post-close collection request must still be scheduled after a view-model failure.");
                    Assert.AreEqual(IntPtr.Zero, new WindowInteropHelper(window).Handle);
                    Assert.IsFalse(window.IsVisible);
                }
                finally
                {
                    window.Dispatcher.Hooks.OperationPosted -= onPosted;
                    lock (collectionOperations)
                    {
                        foreach (var operation in collectionOperations)
                        {
                            if (operation.Status == DispatcherOperationStatus.Pending) operation.Abort();
                        }
                    }
                    window.Close();
                }
            }
            finally
            {
                application.Shutdown();
            }
        }

        private static void VerifyCounterScenario()
        {
            Assert.IsNull(Application.Current);
            var logger = new LoggerConfiguration().CreateLogger();
            using var services = new ServiceCollection().AddSingleton<ILogger>(logger).BuildServiceProvider();
            var application = new App(services) { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.InitializeComponent();
            try
            {
                RunCounterCase("settings-window-close-normal", logger, failViewModel: false, failClosed: false);
                RunCounterCase("settings-window-close-viewmodel-failure", logger, failViewModel: true, failClosed: false);
                RunCounterCase("settings-window-close-combined-failure", logger, failViewModel: true, failClosed: true);
            }
            finally
            {
                application.Shutdown();
            }
        }

        private static void RunCounterCase(string caseName, ILogger logger, bool failViewModel, bool failClosed)
        {
            int cycles = 0;
            int subscriptionDisposeAttempts = 0;
            int releasedSubscriptions = 0;
            int flushAttempts = 0;
            int primaryOutcomes = 0;
            int closedCallbacks = 0;
            int supplementalErrors = 0;
            int retainedLogicalFocus = 0;
            int collectionPosts = 0;
            int repeatCloseErrors = 0;
            int nonzeroHandles = 0;
            int activeSubscriptions = 0;

            for (int cycle = 0; cycle < 100; cycle++)
            {
                var viewModelFailure = failViewModel
                    ? new InvalidOperationException("controlled settings-window view-model failure " + cycle)
                    : null;
                var closedFailure = failClosed
                    ? new InvalidOperationException("controlled settings-window Closed failure " + cycle)
                    : null;
                var entity = new ThrowingSettingsEntity(viewModelFailure);
                var viewModel = new SettingsViewModel(entity, logger, () => Task.FromResult(false));
                var window = new SettingsWindow(viewModel);
                int thisClosed = 0;
                window.Closed += (_, _) =>
                {
                    Assert.AreEqual(0, entity.DisposeAttempts,
                        "The base Closed event must retain its original position before view-model cleanup.");
                    thisClosed++;
                    window.Close();
                    if (closedFailure != null) ThrowControlledFailure(closedFailure);
                };
                FocusManager.SetFocusedElement(window, window);
                Assert.AreSame(window, FocusManager.GetFocusedElement(window));
                if (new WindowInteropHelper(window).Handle != IntPtr.Zero) nonzeroHandles++;

                var pendingCollections = new List<DispatcherOperation>();
                DispatcherHookEventHandler onPosted = (_, args) =>
                {
                    if (args.Operation.Priority == DispatcherPriority.ApplicationIdle)
                    {
                        lock (pendingCollections) pendingCollections.Add(args.Operation);
                    }
                };
                window.Dispatcher.Hooks.OperationPosted += onPosted;
                Exception? outcome = null;
                try
                {
                    try { window.Close(); }
                    catch (Exception error) { outcome = error; }
                    try { window.Close(); }
                    catch { repeatCloseErrors++; }

                    if (!failViewModel && !failClosed)
                    {
                        Assert.IsNull(outcome);
                        primaryOutcomes++;
                    }
                    else if (failViewModel && !failClosed)
                    {
                        Assert.AreSame(viewModelFailure, outcome);
                        primaryOutcomes++;
                    }
                    else if (ReferenceEquals(outcome, closedFailure))
                    {
                        primaryOutcomes++;
                        var supplemental = (AggregateException)closedFailure!.Data["SettingsWindow.OnClosedExceptions"]!;
                        Assert.AreEqual(1, supplemental.InnerExceptions.Count);
                        Assert.AreSame(viewModelFailure, supplemental.InnerExceptions[0]);
                        supplementalErrors += supplemental.InnerExceptions.Count;
                    }
                    else
                    {
                        Assert.AreSame(viewModelFailure, outcome,
                            "The historical implementation may mask Closed with the later view-model failure; no other outcome is valid.");
                        Assert.IsFalse(viewModelFailure!.Data.Contains("SettingsWindow.OnClosedExceptions"));
                    }

                    if (ReferenceEquals(window, FocusManager.GetFocusedElement(window))) retainedLogicalFocus++;
                    Assert.IsNull(Keyboard.FocusedElement);
                    Assert.IsFalse(window.IsVisible);
                    if (new WindowInteropHelper(window).Handle != IntPtr.Zero) nonzeroHandles++;
                }
                finally
                {
                    window.Dispatcher.Hooks.OperationPosted -= onPosted;
                    lock (pendingCollections)
                    {
                        collectionPosts += pendingCollections.Count;
                        foreach (var operation in pendingCollections)
                        {
                            if (operation.Status == DispatcherOperationStatus.Pending) operation.Abort();
                        }
                    }
                    FocusManager.SetFocusedElement(window, null);
                    window.Close();
                }

                cycles++;
                subscriptionDisposeAttempts += entity.DisposeAttempts;
                releasedSubscriptions += entity.ReleaseCount;
                flushAttempts += entity.FlushCount;
                closedCallbacks += thisClosed;
                activeSubscriptions += entity.ActiveSubscriptions;
            }

            WriteCounter(caseName, "cycles", cycles);
            WriteCounter(caseName, "subscription-dispose-attempts", subscriptionDisposeAttempts);
            WriteCounter(caseName, "released-subscriptions", releasedSubscriptions);
            WriteCounter(caseName, "flush-attempts", flushAttempts);
            WriteCounter(caseName, "primary-outcomes", primaryOutcomes);
            WriteCounter(caseName, "closed-callbacks", closedCallbacks);
            WriteCounter(caseName, "supplemental-errors", supplementalErrors);
            WriteCounter(caseName, "retained-logical-focus", retainedLogicalFocus);
            WriteCounter(caseName, "collection-posts", collectionPosts);
            WriteCounter(caseName, "repeat-close-errors", repeatCloseErrors);
            WriteCounter(caseName, "nonzero-handles", nonzeroHandles);
            WriteCounter(caseName, "active-subscriptions", activeSubscriptions);
        }

        private static void WriteCounter(string caseName, string metric, int value)
        {
            Console.WriteLine($"PERFCOUNTER {caseName} {metric} {value}");
        }

        private static void RunOnSta(Action action)
        {
            ExceptionDispatchInfo? failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "The isolated SettingsWindow STA must finish without showing a window.");
            failure?.Throw();
        }

        private static Action Record(List<string> order, string operation, Exception? failure = null)
        {
            return () =>
            {
                order.Add(operation);
                if (failure != null) ThrowControlledFailure(failure);
            };
        }

        private static void ThrowControlledFailure(Exception failure) => throw failure;

        private sealed class ThrowingDataException(string message) : Exception(message)
        {
            public override IDictionary Data => throw new NotSupportedException("Controlled unavailable exception data.");
        }

        private sealed class ThrowingSettingsEntity(Exception? disposeFailure) : IObservableFileEntity<Settings>
        {
            private readonly Settings m_settings = new();
            private readonly Exception? m_disposeFailure = disposeFailure;

            public int DisposeAttempts { get; private set; }
            public int ReleaseCount { get; private set; }
            public int FlushCount { get; private set; }
            public int ActiveSubscriptions { get; private set; }
            public string FullPath => "settings-window-close-test.json";
            public IObservable<Settings> Value => this;

            public IDisposable Subscribe(IObserver<Settings> observer)
            {
                ActiveSubscriptions++;
                observer.OnNext(m_settings);
                return new Subscription(this);
            }

            public Task SaveAsync(Func<Settings, Settings> update)
            {
                update(m_settings);
                return Task.CompletedTask;
            }

            public Task FlushAsync()
            {
                FlushCount++;
                return Task.CompletedTask;
            }

            private sealed class Subscription(ThrowingSettingsEntity owner) : IDisposable
            {
                private bool m_disposed;

                public void Dispose()
                {
                    if (m_disposed) return;
                    m_disposed = true;
                    owner.DisposeAttempts++;
                    owner.ReleaseCount++;
                    owner.ActiveSubscriptions--;
                    if (owner.m_disposeFailure != null) throw owner.m_disposeFailure;
                }
            }
        }
    }
}
