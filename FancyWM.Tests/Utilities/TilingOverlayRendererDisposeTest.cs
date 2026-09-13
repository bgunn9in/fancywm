#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using FancyWM.Utilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class TilingOverlayRendererDisposeTest
    {
        private const string SupplementalExceptionKey = "TilingOverlayRenderer.DisposeExceptions";

        private static readonly string[] s_cleanupOrder =
        {
            "capture", "drag", "overlay", "viewmodel", "display", "subscriptions", "invalidate", "events",
        };

        [TestMethod]
        public void CompleteDisposeRunsEveryOperationOnceInHistoricalOrderAfterAdmission()
        {
            int state = 0;
            var order = new List<string>();

            InvokeDispose(ref state, order, duringStep: _ =>
                Assert.AreNotEqual(0, Volatile.Read(ref state), "Admission must precede callbacks that can reenter Dispose."));

            CollectionAssert.AreEqual(s_cleanupOrder, order);
        }

        [TestMethod]
        public void FailureAtEveryCleanupBoundaryStillAttemptsRemainingOwnersOnce()
        {
            foreach (string boundary in s_cleanupOrder)
            {
                int state = 0;
                var order = new List<string>();
                var failure = new InvalidOperationException($"controlled {boundary} failure");

                var actual = Assert.ThrowsException<InvalidOperationException>(() =>
                    InvokeDispose(ref state, order, operation => operation == boundary ? failure : null));

                Assert.AreSame(failure, actual, boundary);
                Assert.AreNotEqual(0, state, boundary);
                CollectionAssert.AreEqual(s_cleanupOrder, order, boundary);
            }
        }

        [TestMethod]
        public void CompleteDisposePreservesFirstThrowSiteAndAppendsLaterFailuresInOrder()
        {
            int state = 0;
            var order = new List<string>();
            var firstFailure = new InvalidOperationException("controlled content capture failure");
            var existingFailure = new InvalidOperationException("existing supplemental failure");
            var dragFailure = new InvalidOperationException("controlled drag handler failure");
            var overlayFailure = new InvalidOperationException("controlled overlay close failure");
            var viewModelFailure = new InvalidOperationException("controlled view model failure");
            var displayFailure = new InvalidOperationException("controlled display subscription failure");
            var subscriptionsFailure = new InvalidOperationException("controlled settings subscriptions failure");
            var invalidateFailure = new InvalidOperationException("controlled invalidation failure");
            var eventsFailure = new InvalidOperationException("controlled event clearing failure");
            firstFailure.Data[SupplementalExceptionKey] = new AggregateException(existingFailure);
            var failures = new Dictionary<string, Exception>
            {
                ["capture"] = firstFailure,
                ["drag"] = dragFailure,
                ["overlay"] = overlayFailure,
                ["viewmodel"] = viewModelFailure,
                ["display"] = displayFailure,
                ["subscriptions"] = subscriptionsFailure,
                ["invalidate"] = invalidateFailure,
                ["events"] = eventsFailure,
            };

            var actual = Assert.ThrowsException<InvalidOperationException>(() =>
                InvokeDispose(ref state, order, operation => failures[operation]));

            Assert.AreSame(firstFailure, actual);
            StringAssert.Contains(actual.StackTrace ?? string.Empty, nameof(ThrowControlledFailure));
            var supplemental = (AggregateException)actual.Data[SupplementalExceptionKey]!;
            CollectionAssert.AreEqual(new Exception[]
            {
                existingFailure, dragFailure, overlayFailure, viewModelFailure, displayFailure,
                subscriptionsFailure, invalidateFailure, eventsFailure,
            }, new List<Exception>(supplemental.InnerExceptions));
            CollectionAssert.AreEqual(s_cleanupOrder, order);
        }

        [TestMethod]
        public void CompleteDisposeDoesNotMaskFirstFailureWhenExceptionDataIsUnavailable()
        {
            int state = 0;
            var order = new List<string>();
            var firstFailure = new ThrowingDataException("controlled unavailable exception data");
            var laterFailure = new InvalidOperationException("controlled later failure");

            var actual = Assert.ThrowsException<ThrowingDataException>(() => InvokeDispose(
                ref state,
                order,
                operation => operation switch
                {
                    "capture" => firstFailure,
                    "overlay" => laterFailure,
                    _ => null,
                }));

            Assert.AreSame(firstFailure, actual);
            StringAssert.Contains(actual.StackTrace ?? string.Empty, nameof(ThrowControlledFailure));
            CollectionAssert.AreEqual(s_cleanupOrder, order);
        }

        [TestMethod]
        public void CompleteDisposeRejectsReentrantAndRepeatedCleanupAfterSuccess()
        {
            VerifyReentrantAndRepeatedCleanup(null);
        }

        [TestMethod]
        public void CompleteDisposeRejectsReentrantAndRepeatedCleanupAfterEveryFailure()
        {
            foreach (string boundary in s_cleanupOrder)
            {
                VerifyReentrantAndRepeatedCleanup(boundary);
            }
        }

        [TestMethod]
        public Task ReleaseDragHandlersRemovesAllThreeOwnedHandlersAndPreservesOtherSubscribers() => RunOnSta(() =>
        {
            var content = new ContentControl();
            int[] ownedCalls = new int[3];
            int[] otherCalls = new int[3];
            RoutedEventHandler started = (_, _) => ownedCalls[0]++;
            RoutedEventHandler completed = (_, _) => ownedCalls[1]++;
            RoutedEventHandler dragging = (_, _) => ownedCalls[2]++;
            Draggable.AddDragStartedHandler(content, started);
            Draggable.AddDragCompletedHandler(content, completed);
            Draggable.AddDraggingHandler(content, dragging);
            Draggable.AddDragStartedHandler(content, (_, _) => otherCalls[0]++);
            Draggable.AddDragCompletedHandler(content, (_, _) => otherCalls[1]++);
            Draggable.AddDraggingHandler(content, (_, _) => otherCalls[2]++);

            RaiseDragEvents(content);
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, ownedCalls);
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, otherCalls);

            TilingOverlayRenderer.ReleaseDragHandlers(content, started, completed, dragging);
            RaiseDragEvents(content);
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, ownedCalls);
            CollectionAssert.AreEqual(new[] { 2, 2, 2 }, otherCalls);

            TilingOverlayRenderer.ReleaseDragHandlers(content, started, completed, dragging);
            RaiseDragEvents(content);
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, ownedCalls);
            CollectionAssert.AreEqual(new[] { 3, 3, 3 }, otherCalls);
            Assert.IsNull(Window.GetWindow(content), "The fixture must remain outside every Window.");
            Assert.IsNull(PresentationSource.FromVisual(content), "The fixture must never acquire a presentation source or HWND.");
        });

        [TestMethod]
        public void ClearEventHandlersReleasesAllFifteenOutgoingDelegateFields()
        {
            // The public constructor creates native overlay windows. This path
            // requires only the existing managed outgoing event fields.
            var renderer = (TilingOverlayRenderer)RuntimeHelpers.GetUninitializedObject(typeof(TilingOverlayRenderer));
            int calls = 0;
            renderer.TilingPanelMoveRequested += (_, _) => calls++;
            renderer.TilingPanelMoving += (_, _) => calls++;
            renderer.TilingNodeFocusRequested += (_, _) => calls++;
            renderer.TilingNodePullUpRequested += (_, _) => calls++;
            renderer.TilingNodeCloseRequested += (_, _) => calls++;
            renderer.HorizontalSplitRequested += (_, _) => calls++;
            renderer.VerticalSplitRequested += (_, _) => calls++;
            renderer.StackRequested += (_, _) => calls++;
            renderer.PullUpRequested += (_, _) => calls++;
            renderer.FloatRequested += (_, _) => calls++;
            renderer.IgnoreProcessRequested += (_, _) => calls++;
            renderer.IgnoreClassRequested += (_, _) => calls++;
            renderer.BeginHorizontalWithRequested += (_, _) => calls++;
            renderer.BeginVerticalWithRequested += (_, _) => calls++;
            renderer.BeginStackWithRequested += (_, _) => calls++;

            EventInfo[] events = typeof(TilingOverlayRenderer).GetEvents(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            Assert.AreEqual(15, events.Length, "Update this ownership fixture if the renderer adds another outgoing event.");
            var fields = new List<FieldInfo>();
            foreach (EventInfo rendererEvent in events)
            {
                FieldInfo? field = typeof(TilingOverlayRenderer).GetField(rendererEvent.Name, BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(field, rendererEvent.Name);
                Assert.IsNotNull(field!.GetValue(renderer), $"The fixture did not subscribe {rendererEvent.Name}.");
                fields.Add(field);
            }

            renderer.ClearEventHandlers();
            foreach (FieldInfo field in fields)
            {
                Assert.IsNull(field.GetValue(renderer), $"Outgoing event {field.Name} still retains its subscriber.");
            }
            renderer.ClearEventHandlers();
            foreach (FieldInfo field in fields)
            {
                Assert.IsNull(field.GetValue(renderer), field.Name);
            }
            Assert.AreEqual(0, calls, "Clearing ownership must not invoke event subscribers.");
        }

        [TestMethod]
        public void TilingOverlayRendererDisposeCounterScenario()
        {
            RunCounterCase("normal", null);
            RunCounterCase("overlay-failure", "overlay");
            RunCounterCase("viewmodel-failure", "viewmodel");
        }

        private static void VerifyReentrantAndRepeatedCleanup(string? failureBoundary)
        {
            int state = 0;
            int unexpectedActions = 0;
            var order = new List<string>();
            var controlledFailure = new InvalidOperationException($"controlled {failureBoundary} failure");

            void Reenter()
            {
                Action unexpected = () => unexpectedActions++;
                TilingOverlayRenderer.CompleteDispose(
                    ref state, unexpected, unexpected, unexpected, unexpected, unexpected, unexpected, unexpected, unexpected);
            }

            void Dispose() => InvokeDispose(
                ref state,
                order,
                operation => operation == failureBoundary ? controlledFailure : null,
                duringStep: _ => Reenter());

            if (failureBoundary == null)
            {
                Dispose();
            }
            else
            {
                var actual = Assert.ThrowsException<InvalidOperationException>(Dispose);
                Assert.AreSame(controlledFailure, actual, failureBoundary);
            }
            Reenter();
            Reenter();

            Assert.AreNotEqual(0, state);
            Assert.AreEqual(0, unexpectedActions, failureBoundary ?? "normal");
            CollectionAssert.AreEqual(s_cleanupOrder, order, failureBoundary ?? "normal");
        }

        private static void RunCounterCase(string caseName, string? failureBoundary)
        {
            const int Lifetimes = 100;
            // One representative managed owner for each independent release
            // group after content capture. These are not native/heap counts.
            const int OwnersPerLifetime = 7;
            int[] attempts = new int[s_cleanupOrder.Length];
            int[] releases = new int[s_cleanupOrder.Length];
            int primaryOutcomes = 0;
            int supplementalErrors = 0;
            int repeatActions = 0;
            int activeOwners = 0;

            for (int cycle = 0; cycle < Lifetimes; cycle++)
            {
                int state = 0;
                activeOwners += OwnersPerLifetime;
                var controlledFailure = new InvalidOperationException($"controlled {failureBoundary} failure");
                Exception? outcome = null;

                Action Step(int index) => () =>
                {
                    attempts[index]++;
                    if (index > 0)
                    {
                        releases[index]++;
                        activeOwners--;
                    }
                    if (s_cleanupOrder[index] == failureBoundary) ThrowControlledFailure(controlledFailure);
                };

                try
                {
                    TilingOverlayRenderer.CompleteDispose(
                        ref state, Step(0), Step(1), Step(2), Step(3), Step(4), Step(5), Step(6), Step(7));
                }
                catch (Exception error)
                {
                    outcome = error;
                    primaryOutcomes++;
                    if (error.Data[SupplementalExceptionKey] is AggregateException aggregate)
                    {
                        supplementalErrors += aggregate.InnerExceptions.Count;
                    }
                }

                if (failureBoundary == null)
                {
                    Assert.IsNull(outcome, $"Normal cycle {cycle} produced an unexpected disposal failure.");
                }
                else
                {
                    Assert.AreSame(controlledFailure, outcome, $"Wrong primary failure for {caseName} cycle {cycle}.");
                }

                Action repeat = () => repeatActions++;
                TilingOverlayRenderer.CompleteDispose(
                    ref state, repeat, repeat, repeat, repeat, repeat, repeat, repeat, repeat);
            }

            Assert.AreEqual(Lifetimes, attempts[0]);
            int totalReleases = 0;
            for (int index = 1; index < s_cleanupOrder.Length; index++)
            {
                Assert.IsTrue(attempts[index] >= 0 && attempts[index] <= Lifetimes, s_cleanupOrder[index]);
                Assert.AreEqual(attempts[index], releases[index], s_cleanupOrder[index]);
                if (failureBoundary == null) Assert.AreEqual(Lifetimes, releases[index], s_cleanupOrder[index]);
                totalReleases += releases[index];
            }
            Assert.AreEqual(failureBoundary == null ? 0 : Lifetimes, primaryOutcomes);
            Assert.AreEqual(0, supplementalErrors);
            Assert.IsTrue(repeatActions >= 0 && repeatActions <= Lifetimes * s_cleanupOrder.Length);
            Assert.AreEqual(Lifetimes * OwnersPerLifetime - totalReleases, activeOwners);
            Assert.IsTrue(activeOwners >= 0 && activeOwners <= Lifetimes * OwnersPerLifetime);
            if (failureBoundary == null) Assert.AreEqual(0, activeOwners);

            WriteCounter(caseName, "lifetimes", Lifetimes);
            WriteCounter(caseName, "capture-attempts", attempts[0]);
            for (int index = 1; index < s_cleanupOrder.Length; index++)
            {
                WriteCounter(caseName, $"{s_cleanupOrder[index]}-attempts", attempts[index]);
                WriteCounter(caseName, $"{s_cleanupOrder[index]}-releases", releases[index]);
            }
            WriteCounter(caseName, "primary-outcomes", primaryOutcomes);
            WriteCounter(caseName, "supplemental-errors", supplementalErrors);
            WriteCounter(caseName, "repeat-actions", repeatActions);
            WriteCounter(caseName, "active-owners", activeOwners);
        }

        private static void InvokeDispose(
            ref int state,
            List<string> order,
            Func<string, Exception?>? failureFor = null,
            Action<string>? duringStep = null)
        {
            Action Step(string operation) => () =>
            {
                order.Add(operation);
                duringStep?.Invoke(operation);
                if (failureFor?.Invoke(operation) is Exception failure) ThrowControlledFailure(failure);
            };

            TilingOverlayRenderer.CompleteDispose(
                ref state,
                Step("capture"),
                Step("drag"),
                Step("overlay"),
                Step("viewmodel"),
                Step("display"),
                Step("subscriptions"),
                Step("invalidate"),
                Step("events"));
        }

        private static void RaiseDragEvents(UIElement element)
        {
            element.RaiseEvent(new RoutedEventArgs(Draggable.DragStartedEvent, element));
            element.RaiseEvent(new RoutedEventArgs(Draggable.DragCompletedEvent, element));
            element.RaiseEvent(new RoutedEventArgs(Draggable.DraggingEvent, element));
        }

        private static void WriteCounter(string caseName, string metric, int value) =>
            Console.WriteLine($"PERFCOUNTER {caseName} {metric} {value}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowControlledFailure(Exception failure) => throw failure;

        private static async Task RunOnSta(Action action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { action(); completion.SetResult(); }
                catch (Exception error) { completion.SetException(error); }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)), "The owned STA test worker must exit."); }
        }

        private sealed class ThrowingDataException(string message) : Exception(message)
        {
            public override IDictionary Data => throw new NotSupportedException("Controlled unavailable exception data.");
        }
    }
}
