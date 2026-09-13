#nullable enable annotations

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Threading;

using FancyWM.Models;
using FancyWM.Utilities;
using FancyWM.ViewModels;
using FancyWM.Windows;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

using Serilog;

namespace FancyWM.Tests.Models
{
    [TestClass]
    public class SettingsViewModelTest
    {
        [TestMethod]
        public void DelayedConcurrentFieldChangeIsNotOverwrittenByTheViewModel()
        {
            var entity = new RecordingSettingsEntity(new Settings { PanelHeight = 18 });
            using var model = CreateViewModel(entity);
            entity.SaveAsync(value => value with { MasterSatelliteLayout = value.MasterSatelliteLayout with { MasterRatio = 0.72 } });
            entity.BeforeUpdate = value => value with { WindowPadding = 23,
                MasterSatelliteLayout = value.MasterSatelliteLayout with { MasterRatio = 0.76 } };
            model.PanelHeight = 31;
            Assert.AreEqual(31, entity.Current.PanelHeight);
            Assert.AreEqual(23, entity.Current.WindowPadding);
            Assert.AreEqual(0.76, entity.Current.MasterSatelliteLayout.MasterRatio);
        }

        [TestMethod]
        public void DisposeFlushesOnceAndLateEditsCannotSave()
        {
            var entity = new RecordingSettingsEntity(new Settings());
            var model = CreateViewModel(entity);
            model.PanelHeight = 31;
            model.Dispose();
            model.Dispose();
            model.PanelHeight = 42;
            Assert.AreEqual(1, entity.SaveCount);
            Assert.AreEqual(1, entity.FlushCount);
            Assert.AreEqual(31, entity.Current.PanelHeight);
        }

        [TestMethod]
        public void StartupWindowCloseAttemptsBaseClosedAfterSettingsDisposeFailure()
        {
            RunOnSta(() =>
            {
                var entity = new RecordingSettingsEntity(new Settings());
                var model = CreateViewModel(entity);
                var failure = new InvalidOperationException("controlled startup settings cleanup failure");
                var order = new List<string>();
                entity.SubscriptionDisposeFailure = failure;
                var window = new StartupWindow(model);
                entity.BeforeSubscriptionDispose = () =>
                {
                    order.Add("settings-release");
                    window.Close();
                };
                int closed = 0;
                window.Closed += (_, _) =>
                {
                    Assert.AreEqual(1, entity.FlushCount);
                    Assert.AreEqual(0, entity.ActiveSubscriptions);
                    order.Add("closed");
                    closed++;
                };
                Assert.AreEqual(IntPtr.Zero, new WindowInteropHelper(window).Handle);
                Assert.IsFalse(window.IsVisible);

                var actual = Assert.ThrowsException<InvalidOperationException>(window.Close);
                window.Close();

                Assert.AreSame(failure, actual);
                StringAssert.Contains(actual.StackTrace ?? string.Empty, "Subscription.Dispose");
                Assert.AreEqual(1, entity.SubscriptionDisposeAttempts);
                Assert.AreEqual(1, entity.SubscriptionDisposeCount);
                Assert.AreEqual(0, entity.ActiveSubscriptions);
                Assert.AreEqual(1, entity.FlushCount);
                Assert.AreEqual(1, closed, "Window.OnClosed must run even when owned view-model cleanup fails.");
                CollectionAssert.AreEqual(new[] { "settings-release", "closed" }, order);
                Assert.AreEqual(IntPtr.Zero, new WindowInteropHelper(window).Handle);
                Assert.IsFalse(window.IsVisible);
            });
        }

        [TestMethod]
        public void StartupWindowClosePreservesClosedHandlerFailureAfterSettingsCleanup()
        {
            RunOnSta(() =>
            {
                var entity = new RecordingSettingsEntity(new Settings());
                var model = CreateViewModel(entity);
                var failure = new InvalidOperationException("controlled Closed handler failure");
                var window = new StartupWindow(model);
                int laterClosed = 0;
                window.Closed += (_, _) =>
                {
                    Assert.AreEqual(1, entity.FlushCount);
                    Assert.AreEqual(0, entity.ActiveSubscriptions);
                    ThrowClosedHandlerFailure(failure);
                };
                window.Closed += (_, _) => laterClosed++;

                var actual = Assert.ThrowsException<InvalidOperationException>(window.Close);
                window.Close();

                Assert.AreSame(failure, actual);
                StringAssert.Contains(actual.StackTrace ?? string.Empty, nameof(ThrowClosedHandlerFailure));
                Assert.AreEqual(0, laterClosed, "A throwing Closed subscriber must retain normal multicast short-circuit semantics.");
                Assert.AreEqual(1, entity.SubscriptionDisposeAttempts);
                Assert.AreEqual(1, entity.SubscriptionDisposeCount);
                Assert.AreEqual(0, entity.ActiveSubscriptions);
                Assert.AreEqual(1, entity.FlushCount);
            });
        }

        [TestMethod]
        public void StartupWindowClosePreservesSettingsFailureAndRecordsClosedFailure()
        {
            RunOnSta(() =>
            {
                var entity = new RecordingSettingsEntity(new Settings());
                var settingsFailure = new InvalidOperationException("controlled startup settings cleanup failure");
                var closedFailure = new InvalidOperationException("controlled Closed handler failure");
                var existingFailure = new InvalidOperationException("existing supplemental failure");
                settingsFailure.Data["StartupWindow.OnClosedExceptions"] = new AggregateException(existingFailure);
                entity.SubscriptionDisposeFailure = settingsFailure;
                var model = CreateViewModel(entity);
                var window = new StartupWindow(model);
                int closed = 0;
                window.Closed += (_, _) =>
                {
                    Assert.AreEqual(1, entity.FlushCount);
                    Assert.AreEqual(0, entity.ActiveSubscriptions);
                    closed++;
                    throw closedFailure;
                };

                var actual = Assert.ThrowsException<InvalidOperationException>(window.Close);
                window.Close();

                Assert.AreSame(settingsFailure, actual);
                Assert.AreEqual(1, closed);
                var supplemental = (AggregateException)actual.Data["StartupWindow.OnClosedExceptions"]!;
                Assert.AreEqual(2, supplemental.InnerExceptions.Count);
                Assert.AreSame(existingFailure, supplemental.InnerExceptions[0]);
                Assert.AreSame(closedFailure, supplemental.InnerExceptions[1]);
                Assert.AreEqual(1, entity.FlushCount);
                Assert.AreEqual(0, entity.ActiveSubscriptions);
            });
        }

        [TestMethod]
        public void StartupWindowCloseDoesNotMaskSettingsFailureWhenExceptionDataIsUnavailable()
        {
            RunOnSta(() =>
            {
                var entity = new RecordingSettingsEntity(new Settings());
                var settingsFailure = new ThrowingDataException("controlled startup settings cleanup failure");
                entity.SubscriptionDisposeFailure = settingsFailure;
                var model = CreateViewModel(entity);
                var window = new StartupWindow(model);
                int closed = 0;
                window.Closed += (_, _) =>
                {
                    Assert.AreEqual(1, entity.FlushCount);
                    Assert.AreEqual(0, entity.ActiveSubscriptions);
                    closed++;
                    throw new InvalidOperationException("controlled Closed handler failure");
                };

                var actual = Assert.ThrowsException<ThrowingDataException>(window.Close);

                Assert.AreSame(settingsFailure, actual);
                Assert.AreEqual(1, closed);
                Assert.AreEqual(1, entity.FlushCount);
                Assert.AreEqual(0, entity.ActiveSubscriptions);
            });
        }

        [TestMethod]
        public void StartupWindowCloseIsIdempotentDuringAndAfterClosedDelivery()
        {
            RunOnSta(() =>
            {
                var entity = new RecordingSettingsEntity(new Settings());
                var model = CreateViewModel(entity);
                var window = new StartupWindow(model);
                int closed = 0;
                window.Closed += (_, _) =>
                {
                    Assert.AreEqual(1, entity.FlushCount);
                    Assert.AreEqual(0, entity.ActiveSubscriptions);
                    closed++;
                    window.Close();
                };

                window.Close();
                window.Close();

                Assert.AreEqual(1, closed);
                Assert.AreEqual(1, entity.SubscriptionDisposeAttempts);
                Assert.AreEqual(1, entity.SubscriptionDisposeCount);
                Assert.AreEqual(0, entity.ActiveSubscriptions);
                Assert.AreEqual(1, entity.FlushCount);
            });
        }

        [TestMethod]
        public void StartupWindowCloseCounterScenario()
        {
            int cycles = 0;
            int subscriptionDisposeAttempts = 0;
            int releasedSubscriptions = 0;
            int flushAttempts = 0;
            int primaryOutcomes = 0;
            int closedCallbacks = 0;
            int supplementalErrors = 0;
            int repeatCloseErrors = 0;
            int nonzeroHandles = 0;
            int activeSubscriptions = 0;

            RunOnSta(() =>
            {
                for (int cycle = 0; cycle < 100; cycle++)
                {
                    var settingsFailure = new InvalidOperationException("controlled startup cleanup failure " + cycle);
                    var closedFailure = new InvalidOperationException("controlled Closed failure " + cycle);
                    var entity = new RecordingSettingsEntity(new Settings())
                    {
                        SubscriptionDisposeFailure = settingsFailure,
                    };
                    var model = CreateViewModel(entity);
                    var window = new StartupWindow(model);
                    int thisClosed = 0;
                    window.Closed += (_, _) =>
                    {
                        thisClosed++;
                        throw closedFailure;
                    };
                    if (new WindowInteropHelper(window).Handle != IntPtr.Zero) nonzeroHandles++;

                    try
                    {
                        window.Close();
                        Assert.Fail("The controlled settings cleanup must fail.");
                    }
                    catch (Exception error)
                    {
                        Assert.AreSame(settingsFailure, error);
                        primaryOutcomes++;
                    }
                    try { window.Close(); }
                    catch { repeatCloseErrors++; }

                    if (thisClosed != 0)
                    {
                        var supplemental = (AggregateException)settingsFailure.Data["StartupWindow.OnClosedExceptions"]!;
                        Assert.AreEqual(1, supplemental.InnerExceptions.Count);
                        Assert.AreSame(closedFailure, supplemental.InnerExceptions[0]);
                        supplementalErrors += supplemental.InnerExceptions.Count;
                    }
                    else
                    {
                        Assert.IsFalse(settingsFailure.Data.Contains("StartupWindow.OnClosedExceptions"));
                    }
                    if (new WindowInteropHelper(window).Handle != IntPtr.Zero) nonzeroHandles++;
                    Assert.IsFalse(window.IsVisible);

                    cycles++;
                    subscriptionDisposeAttempts += entity.SubscriptionDisposeAttempts;
                    releasedSubscriptions += entity.SubscriptionDisposeCount;
                    flushAttempts += entity.FlushCount;
                    closedCallbacks += thisClosed;
                    activeSubscriptions += entity.ActiveSubscriptions;
                }
            });

            Console.WriteLine($"PERFCOUNTER startup-window-close cycles {cycles}");
            Console.WriteLine($"PERFCOUNTER startup-window-close subscription-dispose-attempts {subscriptionDisposeAttempts}");
            Console.WriteLine($"PERFCOUNTER startup-window-close released-subscriptions {releasedSubscriptions}");
            Console.WriteLine($"PERFCOUNTER startup-window-close flush-attempts {flushAttempts}");
            Console.WriteLine($"PERFCOUNTER startup-window-close primary-outcomes {primaryOutcomes}");
            Console.WriteLine($"PERFCOUNTER startup-window-close closed-callbacks {closedCallbacks}");
            Console.WriteLine($"PERFCOUNTER startup-window-close supplemental-errors {supplementalErrors}");
            Console.WriteLine($"PERFCOUNTER startup-window-close repeat-close-errors {repeatCloseErrors}");
            Console.WriteLine($"PERFCOUNTER startup-window-close nonzero-handles {nonzeroHandles}");
            Console.WriteLine($"PERFCOUNTER startup-window-close active-subscriptions {activeSubscriptions}");
        }

        [TestMethod]
        public void SubscriptionDisposeFailureStillAttemptsEveryOwnedCleanupAndPreservesOriginalError()
        {
            var entity = new RecordingSettingsEntity(new Settings());
            var model = CreateViewModel(entity);
            var failure = new InvalidOperationException("controlled subscription cleanup failure");
            entity.SubscriptionDisposeFailure = failure;
            entity.BeforeSubscriptionDispose = model.Dispose;

            var first = model.Keybindings![0];
            var second = model.Keybindings[1];
            IReadOnlySet<KeyCode> duplicatePattern = new HashSet<KeyCode>
            {
                KeyCode.LeftCtrl, KeyCode.RightCtrl, KeyCode.F24,
            };
            first.Pattern = duplicatePattern;
            int savesBeforeDispose = entity.SaveCount;
            int notifications = 0;
            model.PropertyChanged += (_, _) => notifications++;

            var actual = Assert.ThrowsException<InvalidOperationException>(model.Dispose);

            Assert.AreSame(failure, actual);
            Assert.AreEqual(1, entity.SubscriptionDisposeCount);
            Assert.AreEqual(0, entity.ActiveSubscriptions);
            Assert.AreEqual(1, entity.FlushCount);

            model.Dispose();
            model.PanelHeight = 42;
            second.Pattern = new HashSet<KeyCode>(duplicatePattern);
            Assert.AreEqual(1, entity.SubscriptionDisposeCount);
            Assert.AreEqual(1, entity.FlushCount);
            Assert.AreEqual(savesBeforeDispose, entity.SaveCount);
            Assert.AreEqual(0, notifications, "ViewModelBase.Dispose must release PropertyChanged subscribers.");
            Assert.AreSame(duplicatePattern, first.Pattern,
                "A disposed SettingsViewModel must no longer normalize changes from a retained keybinding view model.");
        }

        [TestMethod]
        public void SubscriptionDisposeFailureIsIdempotentAcrossOneHundredLifetimes()
        {
            for (int cycle = 0; cycle < 100; cycle++)
            {
                var entity = new RecordingSettingsEntity(new Settings());
                var failure = new InvalidOperationException("controlled subscription cleanup failure " + cycle);
                entity.SubscriptionDisposeFailure = failure;
                var model = CreateViewModel(entity);
                int notifications = 0;
                model.PropertyChanged += (_, _) => notifications++;

                Assert.AreSame(failure, Assert.ThrowsException<InvalidOperationException>(model.Dispose));
                model.Dispose();
                model.PanelHeight = cycle + 1;

                Assert.AreEqual(1, entity.SubscriptionDisposeCount);
                Assert.AreEqual(0, entity.ActiveSubscriptions);
                Assert.AreEqual(1, entity.FlushCount);
                Assert.AreEqual(0, entity.SaveCount);
                Assert.AreEqual(0, notifications);
            }
        }

        [DataTestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public void FlushFailureRetainsLoggingPolicyAndStillReleasesViewModelEvents(
            bool throwSynchronously,
            bool loggerFails)
        {
            var flushFailure = new InvalidOperationException("controlled flush failure");
            var loggerFailure = new InvalidOperationException("controlled logger failure");
            var entity = new RecordingSettingsEntity(new Settings())
            {
                FlushFailure = flushFailure,
                ThrowFlushSynchronously = throwSynchronously,
            };
            var logger = new Mock<ILogger>(MockBehavior.Loose);
            if (loggerFails)
            {
                logger.Setup(value => value.Error(It.IsAny<Exception>(), It.IsAny<string>()))
                    .Throws(loggerFailure);
            }
            var model = CreateViewModel(entity, logger.Object);
            int notifications = 0;
            model.PropertyChanged += (_, _) => notifications++;

            if (loggerFails)
            {
                Assert.AreSame(loggerFailure, Assert.ThrowsException<InvalidOperationException>(model.Dispose));
            }
            else
            {
                model.Dispose();
            }

            model.Dispose();
            model.PanelHeight = 42;
            Assert.AreEqual(1, entity.SubscriptionDisposeCount);
            Assert.AreEqual(0, entity.ActiveSubscriptions);
            Assert.AreEqual(1, entity.FlushCount);
            Assert.AreEqual(0, notifications);
            logger.Verify(value => value.Error(flushFailure, "Failed to flush settings on close"), Times.Once);
        }

        [TestMethod]
        public void SubscriptionFailurePreservesLaterLoggerFailureAsSupplementalEvidence()
        {
            var subscriptionFailure = new InvalidOperationException("controlled subscription failure");
            var flushFailure = new InvalidOperationException("controlled flush failure");
            var loggerFailure = new InvalidOperationException("controlled logger failure");
            var existingFailure = new InvalidOperationException("existing supplemental failure");
            subscriptionFailure.Data["SettingsViewModel.CleanupExceptions"] =
                new AggregateException(existingFailure);
            var entity = new RecordingSettingsEntity(new Settings())
            {
                SubscriptionDisposeFailure = subscriptionFailure,
                FlushFailure = flushFailure,
            };
            var logger = new Mock<ILogger>(MockBehavior.Loose);
            logger.Setup(value => value.Error(It.IsAny<Exception>(), It.IsAny<string>()))
                .Throws(loggerFailure);
            var model = CreateViewModel(entity, logger.Object);
            int notifications = 0;
            model.PropertyChanged += (_, _) => notifications++;

            var actual = Assert.ThrowsException<InvalidOperationException>(model.Dispose);

            Assert.AreSame(subscriptionFailure, actual);
            var supplemental = (AggregateException)actual.Data["SettingsViewModel.CleanupExceptions"]!;
            Assert.AreEqual(2, supplemental.InnerExceptions.Count);
            Assert.AreSame(existingFailure, supplemental.InnerExceptions[0]);
            Assert.AreSame(loggerFailure, supplemental.InnerExceptions[1]);
            model.PanelHeight = 42;
            Assert.AreEqual(0, notifications);
            Assert.AreEqual(1, entity.FlushCount);
            Assert.AreEqual(0, entity.ActiveSubscriptions);
        }

        [TestMethod]
        public void SupplementalMetadataFailureCannotMaskPrimaryCleanupError()
        {
            var subscriptionFailure = new ThrowingDataException("controlled primary failure");
            var entity = new RecordingSettingsEntity(new Settings())
            {
                SubscriptionDisposeFailure = subscriptionFailure,
                FlushFailure = new InvalidOperationException("controlled flush failure"),
            };
            var logger = new Mock<ILogger>(MockBehavior.Loose);
            logger.Setup(value => value.Error(It.IsAny<Exception>(), It.IsAny<string>()))
                .Throws(new InvalidOperationException("controlled logger failure"));
            var model = CreateViewModel(entity, logger.Object);
            int notifications = 0;
            model.PropertyChanged += (_, _) => notifications++;

            Assert.AreSame(subscriptionFailure,
                Assert.ThrowsException<ThrowingDataException>(model.Dispose));
            model.PanelHeight = 42;
            Assert.AreEqual(0, notifications);
            Assert.AreEqual(1, entity.FlushCount);
            Assert.AreEqual(0, entity.ActiveSubscriptions);
        }

        [TestMethod]
        public void NullSubscriptionRetainsHistoricalDisposeCompatibilityAndFinalCleanup()
        {
            var entity = new RecordingSettingsEntity(new Settings())
            {
                ReturnNullSubscription = true,
            };
            var model = CreateViewModel(entity);
            int notifications = 0;
            model.PropertyChanged += (_, _) => notifications++;

            model.Dispose();
            model.Dispose();
            model.PanelHeight = 42;

            Assert.AreEqual(0, entity.SubscriptionDisposeCount);
            Assert.AreEqual(0, entity.ActiveSubscriptions);
            Assert.AreEqual(1, entity.FlushCount);
            Assert.AreEqual(0, notifications);
        }

        [TestMethod]
        public void SettingsDisposeCounterScenario()
        {
            foreach (string scenario in new[] { "normal", "subscription-failure", "logger-failure" })
            {
                int cycles = 0;
                int subscriptionDisposeAttempts = 0;
                int releasedSubscriptions = 0;
                int flushAttempts = 0;
                int primaryOutcomes = 0;
                int lateNotifications = 0;
                int lateKeybindingMutations = 0;
                int repeatDisposeErrors = 0;
                int activeSubscriptions = 0;
                int logAttempts = 0;

                for (int cycle = 0; cycle < 100; cycle++)
                {
                    var subscriptionFailure = new InvalidOperationException("controlled subscription failure " + cycle);
                    var flushFailure = new InvalidOperationException("controlled flush failure " + cycle);
                    var loggerFailure = new InvalidOperationException("controlled logger failure " + cycle);
                    var entity = new RecordingSettingsEntity(new Settings());
                    var logger = new Mock<ILogger>(MockBehavior.Loose);
                    Exception? expectedFailure = null;
                    if (scenario == "subscription-failure")
                    {
                        entity.SubscriptionDisposeFailure = expectedFailure = subscriptionFailure;
                    }
                    else if (scenario == "logger-failure")
                    {
                        entity.FlushFailure = flushFailure;
                        expectedFailure = loggerFailure;
                        logger.Setup(value => value.Error(It.IsAny<Exception>(), It.IsAny<string>()))
                            .Callback(() => logAttempts++)
                            .Throws(loggerFailure);
                    }
                    if (scenario != "logger-failure")
                    {
                        logger.Setup(value => value.Error(It.IsAny<Exception>(), It.IsAny<string>()))
                            .Callback(() => logAttempts++);
                    }

                    var model = CreateViewModel(entity, logger.Object);
                    if (scenario == "subscription-failure")
                    {
                        entity.BeforeSubscriptionDispose = model.Dispose;
                    }
                    var first = model.Keybindings![0];
                    var second = model.Keybindings[1];
                    IReadOnlySet<KeyCode> duplicatePattern = new HashSet<KeyCode>
                    {
                        KeyCode.LeftCtrl, KeyCode.RightCtrl, KeyCode.F24,
                    };
                    first.Pattern = duplicatePattern;
                    int notifications = 0;
                    model.PropertyChanged += (_, _) => notifications++;

                    try
                    {
                        model.Dispose();
                        Assert.IsNull(expectedFailure);
                        primaryOutcomes++;
                    }
                    catch (Exception error)
                    {
                        Assert.AreSame(expectedFailure, error);
                        primaryOutcomes++;
                    }
                    try
                    {
                        model.Dispose();
                    }
                    catch
                    {
                        repeatDisposeErrors++;
                    }
                    model.PanelHeight = 101 + cycle;
                    second.Pattern = new HashSet<KeyCode>(duplicatePattern);

                    cycles++;
                    subscriptionDisposeAttempts += entity.SubscriptionDisposeAttempts;
                    releasedSubscriptions += entity.SubscriptionDisposeCount;
                    flushAttempts += entity.FlushCount;
                    if (notifications != 0) lateNotifications++;
                    if (first.Pattern == null) lateKeybindingMutations++;
                    activeSubscriptions += entity.ActiveSubscriptions;
                }

                Console.WriteLine($"PERFCOUNTER settings-dispose-{scenario} cycles {cycles}");
                Console.WriteLine($"PERFCOUNTER settings-dispose-{scenario} subscription-dispose-attempts {subscriptionDisposeAttempts}");
                Console.WriteLine($"PERFCOUNTER settings-dispose-{scenario} released-subscriptions {releasedSubscriptions}");
                Console.WriteLine($"PERFCOUNTER settings-dispose-{scenario} flush-attempts {flushAttempts}");
                Console.WriteLine($"PERFCOUNTER settings-dispose-{scenario} primary-outcomes {primaryOutcomes}");
                Console.WriteLine($"PERFCOUNTER settings-dispose-{scenario} late-notifications {lateNotifications}");
                Console.WriteLine($"PERFCOUNTER settings-dispose-{scenario} late-keybinding-mutations {lateKeybindingMutations}");
                Console.WriteLine($"PERFCOUNTER settings-dispose-{scenario} repeat-dispose-errors {repeatDisposeErrors}");
                Console.WriteLine($"PERFCOUNTER settings-dispose-{scenario} active-subscriptions {activeSubscriptions}");
                Console.WriteLine($"PERFCOUNTER settings-dispose-{scenario} log-attempts {logAttempts}");
            }
        }

        [TestMethod]
        public void AccentChangeNotifiesEveryChannelExactlyOnce()
        {
            var entity = new RecordingSettingsEntity(new Settings());
            using var model = CreateViewModel(entity);
            var notifications = new List<string>();
            model.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName.StartsWith("CustomAccentColor", StringComparison.Ordinal))
                {
                    notifications.Add(args.PropertyName);
                }
            };
            model.CustomAccentColor = System.Windows.Media.Color.FromArgb(12, 34, 56, 78);
            CollectionAssert.AreEqual(new[]
            {
                "CustomAccentColor", "CustomAccentColorA", "CustomAccentColorR",
                "CustomAccentColorG", "CustomAccentColorB",
            }, notifications);
            Assert.AreEqual(1, entity.SaveCount);
        }

        [TestMethod]
        public void ChangingOneMasterSatelliteFieldPreservesTheRestOfTheNestedSettings()
        {
            var originalLayout = CreateNonDefaultLayout();
            var model = new RecordingSettingsEntity(new Settings
            {
                MasterSatelliteLayout = originalLayout,
                PanelHeight = 37,
                ShowStartupWindow = false,
            });

            using var viewModel = CreateViewModel(model);
            viewModel.MasterRatio = 0.68;

            Assert.AreEqual(1, model.SaveCount);
            Assert.AreEqual(originalLayout with { MasterRatio = 0.68 }, model.Current.MasterSatelliteLayout);
            Assert.AreEqual(37, model.Current.PanelHeight);
            Assert.IsFalse(model.Current.ShowStartupWindow);
        }

        [TestMethod]
        public void UwqhdPresetChangesOnlyItsDocumentedSettingsAndSavesOnce()
        {
            var model = new RecordingSettingsEntity(new Settings
            {
                MasterSatelliteLayout = CreateNonDefaultLayout(),
                PanelHeight = 37,
                ShowStartupWindow = false,
            });

            using var viewModel = CreateViewModel(model);
            viewModel.ApplyUwqhdPreset();

            Assert.AreEqual(1, model.SaveCount);
            Assert.AreEqual(new MasterSatelliteLayoutSettings
            {
                Enabled = true,
                MasterRatio = 0.60,
                DefaultMasterSide = MasterSide.Left,
                DefaultSatelliteOrientation = SatelliteLayoutOrientation.Vertical,
                MaxSatellites = 3,
                OverflowPolicy = MasterSatelliteOverflowPolicy.MoveToExistingDesktop,
                DisplayScope = AlgorithmicLayoutDisplayScope.AllDisplays,
                MaxAutoCreatedDesktops = 7,
                FollowOverflowWindow = false,
            }, model.Current.MasterSatelliteLayout);
            Assert.AreEqual(37, model.Current.PanelHeight);
            Assert.IsFalse(model.Current.ShowStartupWindow);
        }

        [TestMethod]
        public void OverflowPolicyUpdatesDependentUiState()
        {
            var model = new RecordingSettingsEntity(new Settings());
            using var viewModel = CreateViewModel(model);

            Assert.IsTrue(viewModel.CanFollowOverflowWindow);
            Assert.IsFalse(viewModel.CanConfigureMaxAutoCreatedDesktops);

            viewModel.OverflowPolicy = MasterSatelliteOverflowPolicy.FloatOnCurrentDesktop;
            Assert.IsFalse(viewModel.CanFollowOverflowWindow);
            Assert.IsFalse(viewModel.CanConfigureMaxAutoCreatedDesktops);

            viewModel.OverflowPolicy = MasterSatelliteOverflowPolicy.MoveToExistingOrCreateDesktop;
            Assert.IsTrue(viewModel.CanFollowOverflowWindow);
            Assert.IsTrue(viewModel.CanConfigureMaxAutoCreatedDesktops);
        }

        private static SettingsViewModel CreateViewModel(RecordingSettingsEntity model, ILogger? logger = null)
        {
            logger ??= new LoggerConfiguration().CreateLogger();
            return new SettingsViewModel(model, logger, () => Task.FromResult(false));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowClosedHandlerFailure(Exception failure)
        {
            throw failure;
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
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "The isolated StartupWindow STA must finish without showing a window.");
            failure?.Throw();
        }

        private static MasterSatelliteLayoutSettings CreateNonDefaultLayout()
        {
            return new MasterSatelliteLayoutSettings
            {
                Enabled = true,
                MasterRatio = 0.72,
                DefaultMasterSide = MasterSide.Right,
                DefaultSatelliteOrientation = SatelliteLayoutOrientation.Horizontal,
                MaxSatellites = 8,
                OverflowPolicy = MasterSatelliteOverflowPolicy.MoveToExistingOrCreateDesktop,
                DisplayScope = AlgorithmicLayoutDisplayScope.AllDisplays,
                MaxAutoCreatedDesktops = 7,
                FollowOverflowWindow = true,
            };
        }

        private sealed class ThrowingDataException(string message) : Exception(message)
        {
            public override IDictionary Data => throw new InvalidOperationException("Exception.Data is unavailable");
        }

        private sealed class RecordingSettingsEntity : IObservableFileEntity<Settings>
        {
            private readonly List<IObserver<Settings>> m_observers = [];

            public RecordingSettingsEntity(Settings initial)
            {
                Current = initial;
            }

            public Settings Current { get; private set; }

            public int SaveCount { get; private set; }
            public int FlushCount { get; private set; }
            public int SubscriptionDisposeCount { get; private set; }
            public int SubscriptionDisposeAttempts { get; private set; }
            public int ActiveSubscriptions => m_observers.Count;
            public Func<Settings, Settings>? BeforeUpdate { get; set; }
            public Action? BeforeSubscriptionDispose { get; set; }
            public Exception? SubscriptionDisposeFailure { get; set; }
            public Exception? FlushFailure { get; set; }
            public bool ThrowFlushSynchronously { get; set; }
            public bool ReturnNullSubscription { get; set; }

            public string FullPath => "settings-view-model-test.json";

            public IObservable<Settings> Value => this;

            public IDisposable Subscribe(IObserver<Settings> observer)
            {
                if (ReturnNullSubscription)
                {
                    observer.OnNext(Current);
                    return null!;
                }
                m_observers.Add(observer);
                observer.OnNext(Current);
                return new Subscription(this, observer);
            }

            public Task SaveAsync(Func<Settings, Settings> update)
            {
                if (BeforeUpdate != null) Current = BeforeUpdate(Current);
                Current = update(Current);
                SaveCount++;
                foreach (var observer in m_observers.ToArray())
                {
                    observer.OnNext(Current);
                }
                return Task.CompletedTask;
            }

            public Task FlushAsync()
            {
                FlushCount++;
                if (FlushFailure != null)
                {
                    if (ThrowFlushSynchronously) throw FlushFailure;
                    return Task.FromException(FlushFailure);
                }
                return Task.CompletedTask;
            }

            private sealed class Subscription(RecordingSettingsEntity owner, IObserver<Settings> observer) : IDisposable
            {
                [MethodImpl(MethodImplOptions.NoInlining)]
                public void Dispose()
                {
                    owner.SubscriptionDisposeAttempts++;
                    if (!owner.m_observers.Remove(observer)) return;
                    owner.SubscriptionDisposeCount++;
                    owner.BeforeSubscriptionDispose?.Invoke();
                    if (owner.SubscriptionDisposeFailure != null)
                    {
                        throw owner.SubscriptionDisposeFailure;
                    }
                }
            }
        }
    }
}
