#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

using FancyWM.Windows;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using WinMan;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class OverlayWindowCloseTest
    {
        private const string SupplementalExceptionKey = "OverlayWindow.OnClosedExceptions";

        [TestMethod]
        public void CompleteCloseRunsEveryOperationOnceInHistoricalOrder()
        {
            var order = new List<string>();

            InvokeClose(order);

            CollectionAssert.AreEqual(ExpectedOrder(), order);
        }

        [TestMethod]
        public void FailureAtEveryCleanupBoundaryStillAttemptsRemainingOwners()
        {
            foreach (string boundary in ExpectedOrder())
            {
                var order = new List<string>();
                var failure = new InvalidOperationException($"controlled {boundary} failure");

                var actual = Assert.ThrowsException<InvalidOperationException>(() =>
                    InvokeClose(order, operation => operation == boundary ? failure : null));

                Assert.AreSame(failure, actual, boundary);
                CollectionAssert.AreEqual(ExpectedOrder(), order, boundary);
            }
        }

        [TestMethod]
        public void CompleteClosePreservesFirstFailureAndRecordsLaterFailuresInOrder()
        {
            var order = new List<string>();
            var closedFailure = new InvalidOperationException("controlled Closed failure");
            var existingFailure = new InvalidOperationException("existing supplemental failure");
            var settingsFailure = new InvalidOperationException("controlled settings failure");
            var workAreaFailure = new InvalidOperationException("controlled WorkArea failure");
            var scalingFailure = new InvalidOperationException("controlled Scaling failure");
            var contentFailure = new InvalidOperationException("controlled Content failure");
            closedFailure.Data[SupplementalExceptionKey] = new AggregateException(existingFailure);
            var failures = new Dictionary<string, Exception>
            {
                ["closed"] = closedFailure,
                ["settings"] = settingsFailure,
                ["work-area"] = workAreaFailure,
                ["scaling"] = scalingFailure,
                ["content"] = contentFailure,
            };

            var actual = Assert.ThrowsException<InvalidOperationException>(() =>
                InvokeClose(order, operation => failures[operation]));

            Assert.AreSame(closedFailure, actual);
            StringAssert.Contains(actual.StackTrace ?? string.Empty, nameof(ThrowControlledFailure));
            var supplemental = (AggregateException)actual.Data[SupplementalExceptionKey]!;
            CollectionAssert.AreEqual(new Exception[]
            {
                existingFailure, settingsFailure, workAreaFailure, scalingFailure, contentFailure,
            }, new List<Exception>(supplemental.InnerExceptions));
            CollectionAssert.AreEqual(ExpectedOrder(), order);
        }

        [TestMethod]
        public void CompleteCloseDoesNotMaskFirstFailureWhenExceptionDataIsUnavailable()
        {
            var order = new List<string>();
            var firstFailure = new ThrowingDataException("controlled unavailable exception data");
            var laterFailure = new InvalidOperationException("controlled later failure");

            var actual = Assert.ThrowsException<ThrowingDataException>(() => InvokeClose(
                order,
                operation => operation switch
                {
                    "closed" => firstFailure,
                    "settings" => laterFailure,
                    _ => null,
                }));

            Assert.AreSame(firstFailure, actual);
            CollectionAssert.AreEqual(ExpectedOrder(), order);
        }

        [TestMethod]
        public void CloseAdmissionPublishesTerminalStateBeforeClosedAndRejectsRepeat()
        {
            int closeState = 0;
            int observedByClosed = -1;

            Assert.IsTrue(OverlayHost.TryBeginOverlayWindowClose(ref closeState));
            OverlayHost.CompleteOverlayWindowClose(
                () => observedByClosed = Volatile.Read(ref closeState),
                () => { },
                () => { },
                () => { },
                () => { });

            Assert.AreEqual(1, observedByClosed);
            Assert.IsFalse(OverlayHost.TryBeginOverlayWindowClose(ref closeState));
        }

        [TestMethod]
        public void CloseAdmissionRejectsReentrantCloseFromClosedNotification()
        {
            int closeState = 0;
            int closedNotifications = 0;

            Assert.IsTrue(OverlayHost.TryBeginOverlayWindowClose(ref closeState));
            OverlayHost.CompleteOverlayWindowClose(
                () =>
                {
                    closedNotifications++;
                    Assert.IsFalse(OverlayHost.TryBeginOverlayWindowClose(ref closeState));
                },
                () => { },
                () => { },
                () => { },
                () => { });

            Assert.AreEqual(1, closedNotifications);
        }

        [DataTestMethod]
        [DataRow("settings")]
        [DataRow("scaling")]
        [DataRow("workarea")]
        public void QueuedCallbackBeforeCloseSkipsEveryEffect(string kind)
        {
            int closeState = 0;
            int prepared = 0;
            int posts = 0;
            int firstEffects = 0;
            int secondEffects = 0;
            var callbacks = new Queue<Action>();

            QueueCallback(
                kind,
                () => Volatile.Read(ref closeState) != 0,
                callback =>
                {
                    posts++;
                    callbacks.Enqueue(callback);
                },
                value => prepared = value,
                () => firstEffects++,
                () => secondEffects++);

            Assert.AreEqual(1, posts);
            Assert.IsTrue(OverlayHost.TryBeginOverlayWindowClose(ref closeState));
            callbacks.Dequeue()();

            Assert.AreEqual(kind == "settings" ? 17 : 0, prepared);
            Assert.AreEqual(0, firstEffects);
            Assert.AreEqual(0, secondEffects);
            Assert.AreEqual(0, callbacks.Count);
        }

        [DataTestMethod]
        [DataRow("settings")]
        [DataRow("scaling")]
        [DataRow("workarea")]
        public void CallbackCapturedAfterCloseSkipsPrepareAndPost(string kind)
        {
            int closeState = 0;
            int prepared = 0;
            int posts = 0;
            int effects = 0;

            Assert.IsTrue(OverlayHost.TryBeginOverlayWindowClose(ref closeState));
            QueueCallback(
                kind,
                () => Volatile.Read(ref closeState) != 0,
                callback =>
                {
                    posts++;
                    callback();
                },
                value => prepared = value,
                () => effects++,
                () => effects++);

            Assert.AreEqual(0, prepared);
            Assert.AreEqual(0, posts);
            Assert.AreEqual(0, effects);
        }

        [DataTestMethod]
        [DataRow("settings", 1)]
        [DataRow("scaling", 2)]
        [DataRow("workarea", 1)]
        public void ActiveCallbackRemainsDeferredAndPreservesOrder(string kind, int expectedEffects)
        {
            int closeState = 0;
            int currentValue = 1;
            int prepared = 0;
            int posts = 0;
            var order = new List<int>();
            var callbacks = new Queue<Action>();

            QueueCallback(
                kind,
                () => Volatile.Read(ref closeState) != 0,
                callback =>
                {
                    posts++;
                    callbacks.Enqueue(callback);
                },
                value => prepared = value,
                () => order.Add(currentValue),
                () => order.Add(currentValue + 1));

            Assert.AreEqual(1, posts);
            Assert.AreEqual(kind == "settings" ? 17 : 0, prepared);
            Assert.AreEqual(0, order.Count);
            currentValue = 20;
            callbacks.Dequeue()();

            Assert.AreEqual(expectedEffects, order.Count);
            Assert.AreEqual(20, order[0]);
            if (kind == "scaling") Assert.AreEqual(21, order[1]);
        }

        [TestMethod]
        public void ActiveSettingsCallbacksUseLatestStoredFontAndScalingAtDrain()
        {
            int closeState = 0;
            int panelFontSize = 0;
            double scaling = 1;
            var callbacks = new Queue<Action>();
            var resources = new Hashtable();

            void QueueSettings(int value) => OverlayHost.QueueOverlayWindowSettingsCallback(
                () => Volatile.Read(ref closeState) != 0,
                value,
                current => panelFontSize = current,
                callbacks.Enqueue,
                () => OverlayHost.UpdateOverlayWindowResources(
                    () => Volatile.Read(ref closeState) != 0,
                    () => resources,
                    () => panelFontSize,
                    () => scaling));

            QueueSettings(12);
            QueueSettings(18);
            scaling = 1.5;
            callbacks.Dequeue()();

            Assert.AreEqual(1.5, resources["DisplayScaling"]);
            Assert.AreEqual(27d, resources["OverlayFontSize"]);
            callbacks.Dequeue()();
            Assert.AreEqual(27d, resources["OverlayFontSize"]);
        }

        [TestMethod]
        public void PostFailurePreservesPreparedFontAndAllowsRetry()
        {
            int closeState = 0;
            int panelFontSize = 0;
            int effects = 0;
            var failure = new InvalidOperationException("controlled post failure");

            var actual = Assert.ThrowsException<InvalidOperationException>(() =>
                OverlayHost.QueueOverlayWindowSettingsCallback(
                    () => Volatile.Read(ref closeState) != 0,
                    13,
                    value => panelFontSize = value,
                    _ => ThrowControlledFailure(failure),
                    () => effects++));

            Assert.AreSame(failure, actual);
            Assert.AreEqual(13, panelFontSize);
            Assert.AreEqual(0, effects);

            OverlayHost.QueueOverlayWindowSettingsCallback(
                () => Volatile.Read(ref closeState) != 0,
                19,
                value => panelFontSize = value,
                callback => callback(),
                () => effects++);
            Assert.AreEqual(19, panelFontSize);
            Assert.AreEqual(1, effects);
        }

        [TestMethod]
        public void CloseDuringPostLeavesQueuedCallbackInert()
        {
            int closeState = 0;
            int effects = 0;
            var callbacks = new Queue<Action>();

            OverlayHost.QueueOverlayWindowCallback(
                () => Volatile.Read(ref closeState) != 0,
                callback =>
                {
                    Assert.IsTrue(OverlayHost.TryBeginOverlayWindowClose(ref closeState));
                    callbacks.Enqueue(callback);
                },
                () => effects++);
            callbacks.Dequeue()();

            Assert.AreEqual(0, effects);
        }

        [TestMethod]
        public void CloseDuringSettingsPrepareRejectsPost()
        {
            int closeState = 0;
            int prepared = 0;
            int posts = 0;

            OverlayHost.QueueOverlayWindowSettingsCallback(
                () => Volatile.Read(ref closeState) != 0,
                23,
                value =>
                {
                    prepared = value;
                    Assert.IsTrue(OverlayHost.TryBeginOverlayWindowClose(ref closeState));
                },
                _ => posts++,
                () => Assert.Fail("A terminal settings callback must not update resources."));

            Assert.AreEqual(23, prepared);
            Assert.AreEqual(0, posts);
        }

        [TestMethod]
        public void CloseDuringScalingTransformRejectsResourceTail()
        {
            int closeState = 0;
            int transforms = 0;
            int resources = 0;

            OverlayHost.QueueOverlayWindowCallback(
                () => Volatile.Read(ref closeState) != 0,
                callback => callback(),
                () =>
                {
                    transforms++;
                    Assert.IsTrue(OverlayHost.TryBeginOverlayWindowClose(ref closeState));
                },
                () => resources++);

            Assert.AreEqual(1, transforms);
            Assert.AreEqual(0, resources);
        }

        [TestMethod]
        public void ActiveCallbackFailurePreservesIdentityStopsTailAndAllowsRetry()
        {
            int closeState = 0;
            int firstEffects = 0;
            int secondEffects = 0;
            foreach (string boundary in new[] { "first", "second" })
            {
                var failure = new InvalidOperationException($"controlled {boundary} callback failure");
                int firstBefore = firstEffects;
                int secondBefore = secondEffects;

                var actual = Assert.ThrowsException<InvalidOperationException>(() =>
                    OverlayHost.QueueOverlayWindowCallback(
                        () => Volatile.Read(ref closeState) != 0,
                        callback => callback(),
                        () =>
                        {
                            if (boundary == "first") ThrowControlledFailure(failure);
                            firstEffects++;
                        },
                        () =>
                        {
                            if (boundary == "second") ThrowControlledFailure(failure);
                            secondEffects++;
                        }));

                Assert.AreSame(failure, actual);
                Assert.AreEqual(boundary == "first" ? firstBefore : firstBefore + 1, firstEffects);
                Assert.AreEqual(secondBefore, secondEffects);
            }

            OverlayHost.QueueOverlayWindowCallback(
                () => Volatile.Read(ref closeState) != 0,
                callback => callback(),
                () => firstEffects++,
                () => secondEffects++);
            Assert.AreEqual(2, firstEffects);
            Assert.AreEqual(1, secondEffects);
        }

        [TestMethod]
        public void ResourceUpdatePreservesReceiverAndValueReadOrder()
        {
            var order = new List<string>();
            var resources = new RecordingDictionary
            {
                OnSet = (key, _) => order.Add($"set:{key}"),
            };
            var scaling = new Queue<double>(new[] { 1.25, 1.5 });

            OverlayHost.UpdateOverlayWindowResources(
                () => false,
                () =>
                {
                    order.Add("resources");
                    return resources;
                },
                () =>
                {
                    order.Add("font");
                    return 16;
                },
                () =>
                {
                    double value = scaling.Dequeue();
                    order.Add("scaling");
                    return value;
                });

            CollectionAssert.AreEqual(
                new[]
                {
                    "resources", "scaling", "set:DisplayScaling",
                    "resources", "font", "scaling", "set:OverlayFontSize",
                },
                order);
            Assert.AreEqual(1.25, resources["DisplayScaling"]);
            Assert.AreEqual(24d, resources["OverlayFontSize"]);
        }

        [TestMethod]
        public void CloseAtEveryResourceBoundaryStopsTheTail()
        {
            for (int closeAt = 1; closeAt <= 7; closeAt++)
            {
                int closeState = 0;
                int boundary = 0;
                var order = new List<string>();
                var resources = new RecordingDictionary();

                void Step(string operation)
                {
                    order.Add(operation);
                    boundary++;
                    if (boundary == closeAt)
                    {
                        Assert.IsTrue(OverlayHost.TryBeginOverlayWindowClose(ref closeState));
                    }
                }
                resources.OnSet = (key, _) => Step($"set:{key}");

                OverlayHost.UpdateOverlayWindowResources(
                    () => Volatile.Read(ref closeState) != 0,
                    () => { Step("resources"); return resources; },
                    () => { Step("font"); return 14; },
                    () => { Step("scaling"); return 1.5; });

                Assert.AreEqual(closeAt, order.Count, $"Boundary {closeAt}: {string.Join(",", order)}");
                if (closeAt < 3) Assert.IsFalse(resources.Contains("DisplayScaling"));
                if (closeAt < 7) Assert.IsFalse(resources.Contains("OverlayFontSize"));
            }
        }

        [TestMethod]
        public void ResourceFailureAtEveryBoundaryPreservesIdentityAndStopsTail()
        {
            for (int failureAt = 1; failureAt <= 7; failureAt++)
            {
                int boundary = 0;
                var resources = new RecordingDictionary();
                var failure = new InvalidOperationException($"controlled resource boundary {failureAt}");
                void Step()
                {
                    boundary++;
                    if (boundary == failureAt) ThrowControlledFailure(failure);
                }
                resources.OnSet = (_, _) => Step();

                var actual = Assert.ThrowsException<InvalidOperationException>(() =>
                    OverlayHost.UpdateOverlayWindowResources(
                        () => false,
                        () => { Step(); return resources; },
                        () => { Step(); return 14; },
                        () => { Step(); return 1.5; }));

                Assert.AreSame(failure, actual);
                Assert.AreEqual(failureAt, boundary);
            }

            var retryResources = new Hashtable();
            OverlayHost.UpdateOverlayWindowResources(
                () => false,
                () => retryResources,
                () => 20,
                () => 2);
            Assert.AreEqual(2d, retryResources["DisplayScaling"]);
            Assert.AreEqual(40d, retryResources["OverlayFontSize"]);
        }

        [TestMethod]
        public void RenderTransformPreservesLocationDisplayReadOrderAndGeometry()
        {
            var order = new List<string>();
            Transform? result = null;
            var bounds = new Rectangle(10, 20, 210, 220);
            var workArea = new Rectangle(30, 50, 190, 180);
            var scaling = new Queue<double>(new[] { 1.25, 2d });

            OverlayHost.UpdateOverlayWindowRenderTransform(
                () => false,
                () => order.Add("location"),
                () => { order.Add("bounds"); return bounds; },
                () => { order.Add("workarea"); return workArea; },
                () =>
                {
                    order.Add("scaling");
                    return scaling.Dequeue();
                },
                transform =>
                {
                    order.Add("transform");
                    result = transform;
                });

            CollectionAssert.AreEqual(
                new[] { "location", "bounds", "workarea", "scaling", "scaling", "transform" },
                order);
            var group = (TransformGroup)result!;
            var translation = (TranslateTransform)group.Children[0];
            var scale = (ScaleTransform)group.Children[1];
            Assert.AreEqual(-20d, translation.X);
            Assert.AreEqual(-30d, translation.Y);
            Assert.AreEqual(0.8d, scale.ScaleX, 0.0000001);
            Assert.AreEqual(0.5d, scale.ScaleY, 0.0000001);
        }

        [TestMethod]
        public void CloseAtEveryTransformBoundaryStopsTheTail()
        {
            for (int closeAt = 1; closeAt <= 5; closeAt++)
            {
                int closeState = 0;
                int boundary = 0;
                var order = new List<string>();

                void Step(string operation)
                {
                    order.Add(operation);
                    boundary++;
                    if (boundary == closeAt)
                    {
                        Assert.IsTrue(OverlayHost.TryBeginOverlayWindowClose(ref closeState));
                    }
                }

                OverlayHost.UpdateOverlayWindowRenderTransform(
                    () => Volatile.Read(ref closeState) != 0,
                    () => Step("location"),
                    () => { Step("bounds"); return new Rectangle(0, 0, 100, 100); },
                    () => { Step("workarea"); return new Rectangle(5, 10, 90, 80); },
                    () => { Step("scaling"); return 1.25; },
                    _ => Step("transform"));

                Assert.AreEqual(closeAt, order.Count, $"Boundary {closeAt}: {string.Join(",", order)}");
            }
        }

        [TestMethod]
        public void TransformFailureAtEveryBoundaryPreservesIdentityAndStopsTail()
        {
            for (int failureAt = 1; failureAt <= 6; failureAt++)
            {
                int boundary = 0;
                var failure = new InvalidOperationException($"controlled transform boundary {failureAt}");
                void Step()
                {
                    boundary++;
                    if (boundary == failureAt) ThrowControlledFailure(failure);
                }

                var actual = Assert.ThrowsException<InvalidOperationException>(() =>
                    OverlayHost.UpdateOverlayWindowRenderTransform(
                        () => false,
                        Step,
                        () => { Step(); return new Rectangle(0, 0, 100, 100); },
                        () => { Step(); return new Rectangle(5, 10, 90, 80); },
                        () => { Step(); return 1.25; },
                        _ => Step()));

                Assert.AreSame(failure, actual);
                Assert.AreEqual(failureAt, boundary);
            }
        }

        [TestMethod]
        public void LocationUpdatePreservesEqualAndFailedReadSemantics()
        {
            var desired = new Rectangle(10, 20, 210, 220);
            int positionWrites = 0;

            OverlayHost.EnsureOverlayWindowLocation(
                () => false,
                () => desired,
                () => desired,
                _ => positionWrites++);
            Assert.AreEqual(0, positionWrites);

            OverlayHost.EnsureOverlayWindowLocation(
                () => false,
                () => null,
                () => desired,
                actual =>
                {
                    Assert.AreEqual(desired, actual);
                    positionWrites++;
                });
            Assert.AreEqual(1, positionWrites);
        }

        [TestMethod]
        public void CloseAtEveryLocationBoundaryStopsTheTail()
        {
            for (int closeAt = 1; closeAt <= 3; closeAt++)
            {
                int closeState = 0;
                int boundary = 0;
                var order = new List<string>();
                void Step(string operation)
                {
                    order.Add(operation);
                    boundary++;
                    if (boundary == closeAt)
                    {
                        Assert.IsTrue(OverlayHost.TryBeginOverlayWindowClose(ref closeState));
                    }
                }

                OverlayHost.EnsureOverlayWindowLocation(
                    () => Volatile.Read(ref closeState) != 0,
                    () => { Step("rectangle"); return null; },
                    () => { Step("workarea"); return new Rectangle(0, 0, 100, 100); },
                    _ => Step("position"));

                Assert.AreEqual(closeAt, order.Count, $"Boundary {closeAt}: {string.Join(",", order)}");
            }
        }

        [TestMethod]
        public void CloseDuringPositionWriteStopsTransformTail()
        {
            int closeState = 0;
            int boundsReads = 0;
            int transformWrites = 0;

            OverlayHost.UpdateOverlayWindowRenderTransform(
                () => Volatile.Read(ref closeState) != 0,
                () => OverlayHost.EnsureOverlayWindowLocation(
                    () => Volatile.Read(ref closeState) != 0,
                    () => null,
                    () => new Rectangle(0, 0, 100, 100),
                    _ => Assert.IsTrue(OverlayHost.TryBeginOverlayWindowClose(ref closeState))),
                () =>
                {
                    boundsReads++;
                    return new Rectangle(0, 0, 100, 100);
                },
                () => new Rectangle(0, 0, 100, 100),
                () => 1,
                _ => transformWrites++);

            Assert.AreEqual(0, boundsReads);
            Assert.AreEqual(0, transformWrites);
        }

        [TestMethod]
        public void EnteredRectangleReadReturningAfterCloseSkipsLocationTail()
        {
            int closeState = 0;
            int workAreaReads = 0;
            int positionWrites = 0;
            using var readEntered = new ManualResetEventSlim();
            using var allowRead = new ManualResetEventSlim();
            Task? worker = null;
            try
            {
                worker = Task.Run(() => OverlayHost.EnsureOverlayWindowLocation(
                    () => Volatile.Read(ref closeState) != 0,
                    () =>
                    {
                        readEntered.Set();
                        Assert.IsTrue(allowRead.Wait(TimeSpan.FromSeconds(5)), "Timed out waiting to finish the controlled rectangle read.");
                        return null;
                    },
                    () =>
                    {
                        Interlocked.Increment(ref workAreaReads);
                        return new Rectangle(0, 0, 100, 100);
                    },
                    _ => Interlocked.Increment(ref positionWrites)));

                Assert.IsTrue(readEntered.Wait(TimeSpan.FromSeconds(5)), "Timed out waiting for the controlled rectangle read.");
                Assert.IsTrue(OverlayHost.TryBeginOverlayWindowClose(ref closeState));
            }
            finally
            {
                allowRead.Set();
                worker?.GetAwaiter().GetResult();
            }

            Assert.AreEqual(0, Volatile.Read(ref workAreaReads));
            Assert.AreEqual(0, Volatile.Read(ref positionWrites));
        }

        [TestMethod]
        public void LocationFailureAtEveryBoundaryPreservesIdentityAndStopsTail()
        {
            for (int failureAt = 1; failureAt <= 3; failureAt++)
            {
                int boundary = 0;
                var failure = new InvalidOperationException($"controlled location boundary {failureAt}");
                void Step()
                {
                    boundary++;
                    if (boundary == failureAt) ThrowControlledFailure(failure);
                }

                var actual = Assert.ThrowsException<InvalidOperationException>(() =>
                    OverlayHost.EnsureOverlayWindowLocation(
                        () => false,
                        () => { Step(); return null; },
                        () => { Step(); return new Rectangle(0, 0, 100, 100); },
                        _ => Step()));

                Assert.AreSame(failure, actual);
                Assert.AreEqual(failureAt, boundary);
            }
        }

        [TestMethod]
        public void AdmittedPostCompletingAfterCloseQueuesOnlyInertCallback()
        {
            int closeState = 0;
            int effects = 0;
            var callbacks = new Queue<Action>();
            using var postEntered = new ManualResetEventSlim();
            using var allowPost = new ManualResetEventSlim();
            Task? worker = null;
            try
            {
                worker = Task.Run(() => OverlayHost.QueueOverlayWindowCallback(
                    () => Volatile.Read(ref closeState) != 0,
                    callback =>
                    {
                        postEntered.Set();
                        Assert.IsTrue(allowPost.Wait(TimeSpan.FromSeconds(5)), "Timed out waiting to finish the controlled post.");
                        lock (callbacks) callbacks.Enqueue(callback);
                    },
                    () => Interlocked.Increment(ref effects)));

                Assert.IsTrue(postEntered.Wait(TimeSpan.FromSeconds(5)), "Timed out waiting for the controlled post.");
                Assert.IsTrue(OverlayHost.TryBeginOverlayWindowClose(ref closeState));
            }
            finally
            {
                allowPost.Set();
                worker?.GetAwaiter().GetResult();
            }

            Action callback;
            lock (callbacks) callback = callbacks.Dequeue();
            callback();
            Assert.AreEqual(0, Volatile.Read(ref effects));
        }

        [TestMethod]
        public void CloseFailureStillRejectsLateCallbacks()
        {
            int closeState = 0;
            int posts = 0;
            var failure = new InvalidOperationException("controlled Closed failure");

            Assert.IsTrue(OverlayHost.TryBeginOverlayWindowClose(ref closeState));
            var actual = Assert.ThrowsException<InvalidOperationException>(() =>
                OverlayHost.CompleteOverlayWindowClose(
                    () => ThrowControlledFailure(failure),
                    () => { },
                    () => { },
                    () => { },
                    () => { }));
            Assert.AreSame(failure, actual);

            OverlayHost.QueueOverlayWindowCallback(
                () => Volatile.Read(ref closeState) != 0,
                _ => posts++,
                () => Assert.Fail("Late callback ran after failing close."));
            Assert.AreEqual(0, posts);
        }

        private static void QueueCallback(
            string kind,
            Func<bool> isClosed,
            Action<Action> post,
            Action<int> prepareSettings,
            Action first,
            Action second)
        {
            switch (kind)
            {
                case "settings":
                    OverlayHost.QueueOverlayWindowSettingsCallback(
                        isClosed,
                        17,
                        prepareSettings,
                        post,
                        first);
                    break;
                case "scaling":
                    OverlayHost.QueueOverlayWindowCallback(isClosed, post, first, second);
                    break;
                case "workarea":
                    OverlayHost.QueueOverlayWindowCallback(isClosed, post, first);
                    break;
                default:
                    Assert.Fail($"Unknown callback kind: {kind}");
                    break;
            }
        }

        [TestMethod]
        public void OverlayWindowCallbacksCounterScenario()
        {
            int probeState = 0;
            Assert.IsTrue(OverlayHost.TryBeginOverlayWindowClose(ref probeState));
            bool guarded = Volatile.Read(ref probeState) != 0;

            foreach (string kind in new[] { "settings", "scaling", "workarea" })
            {
                RunOverlayWindowCallbackCounterCase(kind, "active", guarded);
                RunOverlayWindowCallbackCounterCase(kind, "queued-before-close", guarded);
                RunOverlayWindowCallbackCounterCase(kind, "captured-after-close", guarded);
            }
        }

        private static void RunOverlayWindowCallbackCounterCase(string kind, string timing, bool guarded)
        {
            const int Lifetimes = 100;
            int posts = 0;
            int drainedCallbacks = 0;
            int transformApplies = 0;
            int resourceApplies = 0;
            int ownerReleases = 0;
            int pendingCallbacks = 0;

            for (int cycle = 0; cycle < Lifetimes; cycle++)
            {
                int closeState = 0;
                int panelFontSize = 12;
                double scaling = 1.25;
                var callbacks = new Queue<Action>();
                var resources = new Hashtable();
                int beforePosts = posts;
                int beforeDrains = drainedCallbacks;
                int beforeTransforms = transformApplies;
                int beforeResources = resourceApplies;
                int beforeReleases = ownerReleases;

                bool IsClosed() => Volatile.Read(ref closeState) != 0;
                void Close()
                {
                    Assert.IsTrue(OverlayHost.TryBeginOverlayWindowClose(ref closeState));
                    OverlayHost.CompleteOverlayWindowClose(
                        () => { },
                        () => ownerReleases++,
                        () => ownerReleases++,
                        () => ownerReleases++,
                        () => ownerReleases++);
                }
                void Post(Action callback)
                {
                    posts++;
                    callbacks.Enqueue(callback);
                }
                void Transform()
                {
                    OverlayHost.UpdateOverlayWindowRenderTransform(
                        IsClosed,
                        () => OverlayHost.EnsureOverlayWindowLocation(
                            IsClosed,
                            () => new Rectangle(0, 0, 100, 100),
                            () => new Rectangle(0, 0, 100, 100),
                            _ => Assert.Fail("Equal rectangles must not write a position.")),
                        () => new Rectangle(0, 0, 100, 100),
                        () => new Rectangle(5, 10, 90, 80),
                        () => scaling,
                        _ => transformApplies++);
                }
                void Resources()
                {
                    OverlayHost.UpdateOverlayWindowResources(
                        IsClosed,
                        () => resources,
                        () => panelFontSize,
                        () => scaling);
                    resourceApplies++;
                }
                void Capture()
                {
                    QueueCallback(
                        kind,
                        IsClosed,
                        Post,
                        value => panelFontSize = value,
                        kind == "settings" ? Resources : Transform,
                        Resources);
                }

                switch (timing)
                {
                    case "active":
                        Capture();
                        break;
                    case "queued-before-close":
                        Capture();
                        Close();
                        break;
                    case "captured-after-close":
                        Close();
                        Capture();
                        break;
                    default:
                        Assert.Fail($"Unknown callback timing: {timing}");
                        break;
                }

                while (callbacks.Count != 0)
                {
                    pendingCallbacks++;
                    Action callback = callbacks.Dequeue();
                    pendingCallbacks--;
                    drainedCallbacks++;
                    callback();
                }
                if (timing == "active") Close();

                bool effectsExpected = timing == "active" || !guarded;
                int expectedPosts = timing == "captured-after-close" && guarded ? 0 : 1;
                int expectedTransforms = effectsExpected && kind != "settings" ? 1 : 0;
                int expectedResources = effectsExpected && kind != "workarea" ? 1 : 0;
                Assert.AreEqual(expectedPosts, posts - beforePosts, $"{kind}/{timing}/{cycle} posts");
                Assert.AreEqual(expectedPosts, drainedCallbacks - beforeDrains, $"{kind}/{timing}/{cycle} drains");
                Assert.AreEqual(expectedTransforms, transformApplies - beforeTransforms, $"{kind}/{timing}/{cycle} transforms");
                Assert.AreEqual(expectedResources, resourceApplies - beforeResources, $"{kind}/{timing}/{cycle} resources");
                Assert.AreEqual(4, ownerReleases - beforeReleases, $"{kind}/{timing}/{cycle} owner releases");
                Assert.AreEqual(0, callbacks.Count, $"{kind}/{timing}/{cycle} callback queue");
                Assert.AreEqual(0, pendingCallbacks, $"{kind}/{timing}/{cycle} pending callbacks");
            }

            string caseName = $"overlay-window-callbacks-{kind}-{timing}";
            WriteCounter(caseName, "lifetimes", Lifetimes);
            WriteCounter(caseName, "posts", posts);
            WriteCounter(caseName, "drained-callbacks", drainedCallbacks);
            WriteCounter(caseName, "transform-applies", transformApplies);
            WriteCounter(caseName, "resource-applies", resourceApplies);
            WriteCounter(caseName, "owner-releases", ownerReleases);
            WriteCounter(caseName, "pending-callbacks", pendingCallbacks);
        }

        [TestMethod]
        public void OverlayWindowCloseCounterScenario()
        {
            RunCounterCase("normal", null);
            RunCounterCase("base-failure", "closed");
            RunCounterCase("settings-failure", "settings");
        }

        private static void RunCounterCase(string caseName, string? failureBoundary)
        {
            const int Lifetimes = 100;
            int baseAttempts = 0;
            int settingsAttempts = 0;
            int settingsReleases = 0;
            int workAreaAttempts = 0;
            int workAreaReleases = 0;
            int scalingAttempts = 0;
            int scalingReleases = 0;
            int contentAttempts = 0;
            int contentReleases = 0;
            int primaryOutcomes = 0;
            int supplementalErrors = 0;
            int activeOwners = 0;

            for (int cycle = 0; cycle < Lifetimes; cycle++)
            {
                activeOwners += 4;
                var controlledFailure = new InvalidOperationException($"controlled {failureBoundary} failure");
                Exception? outcome = null;
                try
                {
                    OverlayHost.CompleteOverlayWindowClose(
                        () =>
                        {
                            baseAttempts++;
                            if (failureBoundary == "closed") ThrowControlledFailure(controlledFailure);
                        },
                        ReleaseThenMaybeThrow("settings", controlledFailure, failureBoundary, () => settingsAttempts++, () =>
                        {
                            settingsReleases++;
                            activeOwners--;
                        }),
                        () =>
                        {
                            workAreaAttempts++;
                            workAreaReleases++;
                            activeOwners--;
                        },
                        () =>
                        {
                            scalingAttempts++;
                            scalingReleases++;
                            activeOwners--;
                        },
                        () =>
                        {
                            contentAttempts++;
                            contentReleases++;
                            activeOwners--;
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

                if (failureBoundary == null)
                {
                    Assert.IsNull(outcome, $"Normal cycle {cycle} produced an unexpected close failure.");
                }
                else
                {
                    Assert.AreSame(controlledFailure, outcome, $"Wrong primary failure for {caseName} cycle {cycle}.");
                }
            }

            Assert.AreEqual(Lifetimes, baseAttempts);
            Assert.AreEqual(settingsAttempts, settingsReleases);
            Assert.AreEqual(workAreaAttempts, workAreaReleases);
            Assert.AreEqual(scalingAttempts, scalingReleases);
            Assert.AreEqual(contentAttempts, contentReleases);
            Assert.AreEqual(failureBoundary == null ? 0 : Lifetimes, primaryOutcomes);
            Assert.AreEqual(0, supplementalErrors);
            Assert.IsTrue(activeOwners >= 0 && activeOwners <= Lifetimes * 4);
            if (failureBoundary == null) Assert.AreEqual(0, activeOwners);

            WriteCounter(caseName, "lifetimes", Lifetimes);
            WriteCounter(caseName, "base-attempts", baseAttempts);
            WriteCounter(caseName, "settings-attempts", settingsAttempts);
            WriteCounter(caseName, "settings-releases", settingsReleases);
            WriteCounter(caseName, "workarea-attempts", workAreaAttempts);
            WriteCounter(caseName, "workarea-releases", workAreaReleases);
            WriteCounter(caseName, "scaling-attempts", scalingAttempts);
            WriteCounter(caseName, "scaling-releases", scalingReleases);
            WriteCounter(caseName, "content-attempts", contentAttempts);
            WriteCounter(caseName, "content-releases", contentReleases);
            WriteCounter(caseName, "primary-outcomes", primaryOutcomes);
            WriteCounter(caseName, "supplemental-errors", supplementalErrors);
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

            OverlayHost.CompleteOverlayWindowClose(
                Step("closed"),
                Step("settings"),
                Step("work-area"),
                Step("scaling"),
                Step("content"));
        }

        private static string[] ExpectedOrder() =>
        [
            "closed", "settings", "work-area", "scaling", "content",
        ];

        private static void WriteCounter(string caseName, string metric, int value)
        {
            Console.WriteLine($"PERFCOUNTER {caseName} {metric} {value}");
        }

        private static void ThrowControlledFailure(Exception failure) => throw failure;

        private sealed class ThrowingDataException(string message) : Exception(message)
        {
            public override IDictionary Data => throw new NotSupportedException("Controlled unavailable exception data.");
        }

        private sealed class RecordingDictionary : IDictionary
        {
            private readonly Hashtable m_values = new();

            public Action<object, object?>? OnSet { get; set; }

            public object? this[object key]
            {
                get => m_values[key];
                set
                {
                    OnSet?.Invoke(key, value);
                    m_values[key] = value;
                }
            }

            public ICollection Keys => m_values.Keys;
            public ICollection Values => m_values.Values;
            public bool IsReadOnly => false;
            public bool IsFixedSize => false;
            public int Count => m_values.Count;
            public object SyncRoot => m_values.SyncRoot;
            public bool IsSynchronized => false;

            public void Add(object key, object? value) => m_values.Add(key, value);
            public void Clear() => m_values.Clear();
            public bool Contains(object key) => m_values.Contains(key);
            public void CopyTo(Array array, int index) => m_values.CopyTo(array, index);
            public IDictionaryEnumerator GetEnumerator() => m_values.GetEnumerator();
            public void Remove(object key) => m_values.Remove(key);
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
