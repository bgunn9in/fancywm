#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;

using FancyWM.Windows;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class OverlayHostCloseTest
    {
        private const string SupplementalExceptionKey = "OverlayHost.CloseExceptions";

        private static readonly string[] s_cleanupOrder =
        {
            "cursor-input",
            "refresh-loop",
            "anchor",
            "workspace-cursor",
            "workspace-focus",
            "workspace-added",
            "workspace-removed",
            "mouse-hook",
            "dispatch",
            "nonhit-visibility",
            "nonhit-allow-close",
            "nonhit-close",
            "hit-visibility",
            "hit-allow-close",
            "hit-close",
        };

        private static readonly string[] s_independentFailureBoundaries =
        {
            "cursor-input",
            "refresh-loop",
            "anchor",
            "workspace-cursor",
            "workspace-focus",
            "workspace-added",
            "workspace-removed",
            "mouse-hook",
            "nonhit-visibility",
            "nonhit-allow-close",
            "nonhit-close",
            "hit-visibility",
            "hit-allow-close",
            "hit-close",
        };

        [TestMethod]
        public void CompleteCloseRunsEveryOperationOnceInHistoricalOrder()
        {
            bool closed = false;
            var order = new List<string>();

            InvokeClose(ref closed, order);

            Assert.IsTrue(closed);
            CollectionAssert.AreEqual(ExpectedOrder(), order);
        }

        [TestMethod]
        public void FailureAtEveryCleanupBoundaryStillAttemptsRemainingOwners()
        {
            foreach (string boundary in s_independentFailureBoundaries)
            {
                bool closed = false;
                var order = new List<string>();
                var failure = new InvalidOperationException($"controlled {boundary} failure");

                var actual = Assert.ThrowsException<InvalidOperationException>(() =>
                    InvokeClose(ref closed, order, operation => operation == boundary ? failure : null));

                Assert.AreSame(failure, actual, boundary);
                Assert.IsTrue(closed, boundary);
                CollectionAssert.AreEqual(ExpectedOrder(), order, boundary);
            }
        }

        [TestMethod]
        public void CompleteClosePreservesFirstFailureAndRecordsLaterFailuresInOrder()
        {
            bool closed = false;
            var order = new List<string>();
            var cursorFailure = new InvalidOperationException("controlled cursor failure");
            var existingFailure = new InvalidOperationException("existing supplemental failure");
            var refreshFailure = new InvalidOperationException("controlled refresh failure");
            var workspaceFailure = new InvalidOperationException("controlled workspace failure");
            var nonHitFailure = new InvalidOperationException("controlled nonhit close failure");
            var hitFailure = new InvalidOperationException("controlled hit close failure");
            cursorFailure.Data[SupplementalExceptionKey] = new AggregateException(existingFailure);

            var failures = new Dictionary<string, Exception>
            {
                ["cursor-input"] = cursorFailure,
                ["refresh-loop"] = refreshFailure,
                ["workspace-focus"] = workspaceFailure,
                ["nonhit-close"] = nonHitFailure,
                ["hit-close"] = hitFailure,
            };

            var actual = Assert.ThrowsException<InvalidOperationException>(() =>
                InvokeClose(ref closed, order, operation => failures.TryGetValue(operation, out Exception? error) ? error : null));

            Assert.AreSame(cursorFailure, actual);
            StringAssert.Contains(actual.StackTrace ?? string.Empty, nameof(ThrowControlledFailure));
            var supplemental = (AggregateException)actual.Data[SupplementalExceptionKey]!;
            CollectionAssert.AreEqual(new Exception[]
            {
                existingFailure, refreshFailure, workspaceFailure, nonHitFailure, hitFailure,
            }, new List<Exception>(supplemental.InnerExceptions));
            CollectionAssert.AreEqual(ExpectedOrder(), order);
        }

        [TestMethod]
        public void CompleteCloseDoesNotMaskFirstFailureWhenExceptionDataIsUnavailable()
        {
            bool closed = false;
            var order = new List<string>();
            var firstFailure = new ThrowingDataException("controlled unavailable exception data");
            var laterFailure = new InvalidOperationException("controlled later failure");

            var actual = Assert.ThrowsException<ThrowingDataException>(() => InvokeClose(
                ref closed,
                order,
                operation => operation switch
                {
                    "cursor-input" => firstFailure,
                    "refresh-loop" => laterFailure,
                    _ => null,
                }));

            Assert.AreSame(firstFailure, actual);
            CollectionAssert.AreEqual(ExpectedOrder(), order);
        }

        [TestMethod]
        public void CompleteCloseRejectsReentrantAndRepeatedCleanupAfterAdmission()
        {
            bool closed = false;
            var order = new List<string>();
            int unexpectedCleanup = 0;

            void ReentrantClose()
            {
                OverlayHost.CompleteClose(
                    ref closed,
                    () => unexpectedCleanup++,
                    () => unexpectedCleanup++,
                    () => unexpectedCleanup++,
                    () => unexpectedCleanup++,
                    release => release(() => unexpectedCleanup++),
                    release => release(() => unexpectedCleanup++));
            }

            OverlayHost.CompleteClose(
                ref closed,
                () => order.Add("invalidate"),
                () =>
                {
                    order.Add("cursor-input");
                    ReentrantClose();
                },
                () => order.Add("refresh-loop"),
                () => order.Add("anchor"),
                release => release(() => order.Add("subscription")),
                release => release(() => order.Add("window")));
            ReentrantClose();

            Assert.IsTrue(closed);
            Assert.AreEqual(0, unexpectedCleanup);
            CollectionAssert.AreEqual(new[]
            {
                "invalidate", "cursor-input", "refresh-loop", "anchor", "subscription", "window",
            }, order);
        }

        [TestMethod]
        public void DispatcherFailureDoesNotRunWindowOperationsOutsideDispatcherCallback()
        {
            bool closed = false;
            var order = new List<string>();
            var dispatchFailure = new InvalidOperationException("controlled dispatch failure");

            var actual = Assert.ThrowsException<InvalidOperationException>(() => OverlayHost.CompleteClose(
                ref closed,
                () => order.Add("invalidate"),
                () => order.Add("cursor-input"),
                () => order.Add("refresh-loop"),
                () => order.Add("anchor"),
                release => release(() => order.Add("subscription")),
                _ =>
                {
                    order.Add("dispatch");
                    ThrowControlledFailure(dispatchFailure);
                }));

            Assert.AreSame(dispatchFailure, actual);
            CollectionAssert.AreEqual(new[]
            {
                "invalidate", "cursor-input", "refresh-loop", "anchor", "subscription", "dispatch",
            }, order);
        }

        [TestMethod]
        public void OverlayHostCloseCounterScenario()
        {
            RunCounterCase("normal", null);
            RunCounterCase("cursor-failure", "cursor-input");
            RunCounterCase("nonhit-close-failure", "nonhit-close");
        }

        private static void RunCounterCase(string caseName, string? failureBoundary)
        {
            const int Lifetimes = 100;
            int invalidations = 0;
            int cursorAttempts = 0;
            int cursorReleases = 0;
            int refreshAttempts = 0;
            int refreshReleases = 0;
            int subscriptionAttempts = 0;
            int subscriptionReleases = 0;
            int dispatchAttempts = 0;
            int nonHitCloseAttempts = 0;
            int nonHitCloseReleases = 0;
            int hitCloseAttempts = 0;
            int hitCloseReleases = 0;
            int primaryOutcomes = 0;
            int supplementalErrors = 0;
            int repeatActions = 0;
            int activeOwners = 0;

            for (int cycle = 0; cycle < Lifetimes; cycle++)
            {
                bool closed = false;
                activeOwners += 9;
                var controlledFailure = new InvalidOperationException($"controlled {failureBoundary} failure");
                Exception? outcome = null;

                try
                {
                    OverlayHost.CompleteClose(
                        ref closed,
                        () => invalidations++,
                        ReleaseThenMaybeThrow("cursor-input", controlledFailure, failureBoundary, () => cursorAttempts++, () =>
                        {
                            cursorReleases++;
                            activeOwners--;
                        }),
                        ReleaseThenMaybeThrow("refresh-loop", controlledFailure, failureBoundary, () => refreshAttempts++, () =>
                        {
                            refreshReleases++;
                            activeOwners--;
                        }),
                        () => { },
                        release =>
                        {
                            for (int subscription = 0; subscription < 5; subscription++)
                            {
                                release(() =>
                                {
                                    subscriptionAttempts++;
                                    subscriptionReleases++;
                                    activeOwners--;
                                });
                            }
                        },
                        release =>
                        {
                            dispatchAttempts++;
                            release(() => { });
                            release(() => { });
                            release(ReleaseThenMaybeThrow("nonhit-close", controlledFailure, failureBoundary, () => nonHitCloseAttempts++, () =>
                            {
                                nonHitCloseReleases++;
                                activeOwners--;
                            }));
                            release(() => { });
                            release(() => { });
                            release(() =>
                            {
                                hitCloseAttempts++;
                                hitCloseReleases++;
                                activeOwners--;
                            });
                        });
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

                Assert.IsTrue(closed, $"Close admission was not recorded for {caseName} cycle {cycle}.");
                if (failureBoundary == null)
                {
                    Assert.IsNull(outcome, $"Normal cycle {cycle} produced an unexpected close failure.");
                }
                else
                {
                    Assert.AreSame(controlledFailure, outcome, $"Wrong primary failure for {caseName} cycle {cycle}.");
                }

                OverlayHost.CompleteClose(
                    ref closed,
                    () => repeatActions++,
                    () => repeatActions++,
                    () => repeatActions++,
                    () => repeatActions++,
                    release => release(() => repeatActions++),
                    release => release(() => repeatActions++));
            }

            Assert.AreEqual(Lifetimes, invalidations);
            Assert.AreEqual(Lifetimes, cursorAttempts);
            Assert.AreEqual(cursorAttempts, cursorReleases);
            Assert.AreEqual(refreshAttempts, refreshReleases);
            Assert.AreEqual(subscriptionAttempts, subscriptionReleases);
            Assert.AreEqual(nonHitCloseAttempts, nonHitCloseReleases);
            Assert.AreEqual(hitCloseAttempts, hitCloseReleases);
            Assert.AreEqual(failureBoundary == null ? 0 : Lifetimes, primaryOutcomes);
            Assert.AreEqual(0, supplementalErrors);
            Assert.AreEqual(0, repeatActions);
            Assert.IsTrue(activeOwners >= 0 && activeOwners <= Lifetimes * 8);
            if (failureBoundary == null) Assert.AreEqual(0, activeOwners);

            WriteCounter(caseName, "lifetimes", Lifetimes);
            WriteCounter(caseName, "invalidations", invalidations);
            WriteCounter(caseName, "cursor-attempts", cursorAttempts);
            WriteCounter(caseName, "cursor-releases", cursorReleases);
            WriteCounter(caseName, "refresh-attempts", refreshAttempts);
            WriteCounter(caseName, "refresh-releases", refreshReleases);
            WriteCounter(caseName, "subscription-attempts", subscriptionAttempts);
            WriteCounter(caseName, "subscription-releases", subscriptionReleases);
            WriteCounter(caseName, "dispatch-attempts", dispatchAttempts);
            WriteCounter(caseName, "nonhit-close-attempts", nonHitCloseAttempts);
            WriteCounter(caseName, "nonhit-close-releases", nonHitCloseReleases);
            WriteCounter(caseName, "hit-close-attempts", hitCloseAttempts);
            WriteCounter(caseName, "hit-close-releases", hitCloseReleases);
            WriteCounter(caseName, "primary-outcomes", primaryOutcomes);
            WriteCounter(caseName, "supplemental-errors", supplementalErrors);
            WriteCounter(caseName, "repeat-actions", repeatActions);
            WriteCounter(caseName, "active-owners", activeOwners);
        }

        private static Action ReleaseThenMaybeThrow(
            string operation,
            Exception failure,
            string? failureBoundary,
            Action recordAttempt,
            Action release)
        {
            return () =>
            {
                recordAttempt();
                release();
                if (operation == failureBoundary) ThrowControlledFailure(failure);
            };
        }

        private static void InvokeClose(
            ref bool closed,
            List<string> order,
            Func<string, Exception?>? failureFor = null)
        {
            Action Step(string operation) => () =>
            {
                order.Add(operation);
                if (failureFor?.Invoke(operation) is Exception failure)
                {
                    ThrowControlledFailure(failure);
                }
            };

            OverlayHost.CompleteClose(
                ref closed,
                Step("invalidate"),
                Step("cursor-input"),
                Step("refresh-loop"),
                Step("anchor"),
                release =>
                {
                    release(Step("workspace-cursor"));
                    release(Step("workspace-focus"));
                    release(Step("workspace-added"));
                    release(Step("workspace-removed"));
                    release(Step("mouse-hook"));
                },
                release =>
                {
                    Step("dispatch")();
                    release(Step("nonhit-visibility"));
                    release(Step("nonhit-allow-close"));
                    release(Step("nonhit-close"));
                    release(Step("hit-visibility"));
                    release(Step("hit-allow-close"));
                    release(Step("hit-close"));
                });
        }

        private static string[] ExpectedOrder()
        {
            var expected = new string[s_cleanupOrder.Length + 1];
            expected[0] = "invalidate";
            Array.Copy(s_cleanupOrder, 0, expected, 1, s_cleanupOrder.Length);
            return expected;
        }

        private static void WriteCounter(string caseName, string metric, int value)
        {
            Console.WriteLine($"PERFCOUNTER {caseName} {metric} {value}");
        }

        private static void ThrowControlledFailure(Exception failure) => throw failure;

        private sealed class ThrowingDataException(string message) : Exception(message)
        {
            public override IDictionary Data => throw new NotSupportedException("Controlled unavailable exception data.");
        }
    }
}
