#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

using FancyWM.Models;
using FancyWM.Utilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class RuntimeSettingsSubscriptionTest
    {
        [DataTestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        [DataRow(4)]
        [DataRow(5)]
        public void SubscribeAllFailureReleasesEarlierSubscriptionsAndStopsAcquisition(int failedIndex)
        {
            using var source = new Subject<Settings>();
            var acquisitionOrder = new List<int>();
            var disposalOrder = new List<int>();
            var delivered = new List<int>();
            var acquired = new List<IDisposable>();
            var failure = new InvalidOperationException("Synthetic Subscribe failure");
            var factories = Enumerable.Range(0, 6).Select(index => (Func<IDisposable>)(() =>
            {
                acquisitionOrder.Add(index);
                if (index == failedIndex) { return Observable.Throw<Settings>(failure).Subscribe(); }
                var subscription = source.Subscribe(_ => delivered.Add(index));
                var owned = Disposable.Create(() => { disposalOrder.Add(index); subscription.Dispose(); });
                acquired.Add(owned);
                return owned;
            })).ToArray();
            try
            {
                var actual = Assert.ThrowsException<InvalidOperationException>(() => RuntimeSettingsSubscription.SubscribeAll(factories));
                Assert.AreSame(failure, actual);
                CollectionAssert.AreEqual(Enumerable.Range(0, failedIndex + 1).ToArray(), acquisitionOrder);
                CollectionAssert.AreEqual(Enumerable.Range(0, failedIndex).ToArray(), disposalOrder);
                source.OnNext(new Settings());
                Assert.AreEqual(0, delivered.Count, "No earlier source may outlive failed subscription construction.");
            }
            finally { foreach (var subscription in acquired) { subscription.Dispose(); } }
        }

        [TestMethod]
        public void SubscribeAllSuccessKeepsAcquisitionDeliveryAndDisposalOrder()
        {
            using var source = new Subject<Settings>();
            var acquisitionOrder = new List<int>();
            var disposalOrder = new List<int>();
            var delivered = new List<int>();
            using var subscriptions = RuntimeSettingsSubscription.SubscribeAll(Enumerable.Range(0, 6).Select(index => (Func<IDisposable>)(() =>
            {
                acquisitionOrder.Add(index);
                var subscription = source.Subscribe(_ => delivered.Add(index));
                return Disposable.Create(() => { disposalOrder.Add(index); subscription.Dispose(); });
            })).ToArray());
            CollectionAssert.AreEqual(Enumerable.Range(0, 6).ToArray(), acquisitionOrder);
            Assert.AreEqual(0, disposalOrder.Count);
            source.OnNext(new Settings());
            CollectionAssert.AreEqual(Enumerable.Range(0, 6).ToArray(), delivered);
            subscriptions.Dispose();
            subscriptions.Dispose();
            CollectionAssert.AreEqual(Enumerable.Range(0, 6).ToArray(), disposalOrder);
            source.OnNext(new Settings());
            Assert.AreEqual(6, delivered.Count);
        }

        [TestMethod]
        public void SubscribeAllFailureContinuesCleanupAndPreservesOriginalError()
        {
            var failure = new InvalidOperationException("Original Subscribe failure");
            var cleanupFirst = new ApplicationException("First cleanup failure");
            var cleanupLast = new ApplicationException("Last cleanup failure");
            var disposed = new List<int>();
            var first = Disposable.Create(() => { disposed.Add(0); throw cleanupFirst; });
            var middle = Disposable.Create(() => disposed.Add(1));
            var last = Disposable.Create(() => { disposed.Add(2); throw cleanupLast; });
            try
            {
                var actual = Assert.ThrowsException<InvalidOperationException>(() => RuntimeSettingsSubscription.SubscribeAll(
                    () => first, () => middle, () => last, () => throw failure));
                Assert.AreSame(failure, actual);
                CollectionAssert.AreEqual(new[] { 0, 1, 2 }, disposed);
                var cleanup = actual.Data["RuntimeSettingsSubscription.CleanupExceptions"] as AggregateException;
                Assert.IsNotNull(cleanup);
                CollectionAssert.AreEqual(new Exception[] { cleanupFirst, cleanupLast }, cleanup!.InnerExceptions.ToArray());
            }
            finally
            {
                foreach (var subscription in new[] { first, middle, last })
                {
                    try { subscription.Dispose(); }
                    catch (ApplicationException) { }
                }
            }
        }

        [TestMethod]
        public void SubscribeAllFailureCancelsPendingUiWorkBeforeDispatcherPump()
        {
            RunOnSta(() =>
            {
                using var source = new ReplaySubject<Settings>(1);
                source.OnNext(new Settings());
                var operations = new List<DispatcherOperation>();
                var acquired = new List<IDisposable>();
                int mutations = 0;
                var failure = new InvalidOperationException("Last Subscribe fails");
                Task QueueMutation(CancellationToken token)
                {
                    var operation = Dispatcher.CurrentDispatcher.InvokeAsync(() => mutations++, DispatcherPriority.Normal, token);
                    operations.Add(operation);
                    return operation.Task;
                }
                IDisposable Track(IDisposable subscription) { acquired.Add(subscription); return subscription; }
                try
                {
                    var actual = Assert.ThrowsException<InvalidOperationException>(() => RuntimeSettingsSubscription.SubscribeAll(
                        () => Track(RuntimeSettingsSubscription.Observe(source, _ => { }, (_, token) => QueueMutation(token)).Subscribe()),
                        () => Track(RuntimeSettingsSubscription.ApplyLatest(source.Select(settings => settings.ActivationHotkey).DistinctUntilChanged(), (_, token) => QueueMutation(token)).Subscribe()),
                        () => Track(RuntimeSettingsSubscription.ApplyLatest(source.Select(settings => settings.ActivateOnCapsLock).DistinctUntilChanged(), (_, token) => QueueMutation(token)).Subscribe()),
                        () => Track(RuntimeSettingsSubscription.ApplyLatest(RuntimeSettingsSubscription.DirectHotkeys(source), (_, token) => QueueMutation(token)).Subscribe()),
                        () => Track(RuntimeSettingsSubscription.Initialize(source, (_, token) => QueueMutation(token)).Subscribe()),
                        () => Observable.Throw<Unit>(failure).Subscribe()));
                    Assert.AreSame(failure, actual);
                    Assert.AreEqual(5, operations.Count);
                    foreach (var operation in operations) { Assert.AreEqual(DispatcherOperationStatus.Aborted, operation.Status); }
                    FancyWM.Tests.TestUtilities.Dispatchers.DoEvents();
                    Assert.AreEqual(0, mutations);
                    source.OnNext(new Settings { ActivateOnCapsLock = true, ActivationHotkey = ActivationHotkey.AllowedHotkeys[0] });
                    FancyWM.Tests.TestUtilities.Dispatchers.DoEvents();
                    Assert.AreEqual(5, operations.Count, "Failed ownership must not keep sources subscribed.");
                }
                finally { foreach (var subscription in acquired) { subscription.Dispose(); } }
            });
        }

        [TestMethod]
        public void SubscribeAllRepeatedPartialConstructionLeavesNoLiveSource()
        {
            for (int cycle = 0; cycle < 100; cycle++)
            {
                using var source = new Subject<Settings>();
                var acquired = new List<IDisposable>();
                int active = 0;
                int delivered = 0;
                int failedIndex = cycle % 6;
                var failure = new InvalidOperationException("Synthetic construction failure");
                var factories = Enumerable.Range(0, 6).Select(index => (Func<IDisposable>)(() =>
                {
                    if (index == failedIndex) { throw failure; }
                    var subscription = source.Subscribe(_ => delivered++);
                    active++;
                    var owned = Disposable.Create(() => { active--; subscription.Dispose(); });
                    acquired.Add(owned);
                    return owned;
                })).ToArray();
                try
                {
                    Assert.AreSame(failure, Assert.ThrowsException<InvalidOperationException>(() => RuntimeSettingsSubscription.SubscribeAll(factories)));
                    Assert.AreEqual(0, active, $"Cycle {cycle} retained an earlier subscription.");
                    source.OnNext(new Settings());
                    Assert.AreEqual(0, delivered);
                }
                finally { foreach (var subscription in acquired) { subscription.Dispose(); } }
            }
        }

        [TestMethod]
        public void HotkeyAndCapsLockDispatchUsesLatestValueAndStopsWhenOwnerDisposes()
        {
            RunOnSta(() =>
            {
                using var source = new Subject<Settings>();
                var hotkeys = new List<ActivationHotkey>();
                var caps = new List<bool>();
                using var owner = RuntimeSettingsSubscription.SubscribeAll(
                    () => RuntimeSettingsSubscription.ApplyLatest(source.Select(settings => settings.ActivationHotkey).DistinctUntilChanged(),
                        (value, token) => Dispatcher.CurrentDispatcher.InvokeAsync(() => hotkeys.Add(value), DispatcherPriority.Normal, token).Task).Subscribe(),
                    () => RuntimeSettingsSubscription.ApplyLatest(source.Select(settings => settings.ActivateOnCapsLock).DistinctUntilChanged(),
                        (value, token) => Dispatcher.CurrentDispatcher.InvokeAsync(() => caps.Add(value), DispatcherPriority.Normal, token).Task).Subscribe());
                source.OnNext(new Settings { ActivationHotkey = ActivationHotkey.AllowedHotkeys[0], ActivateOnCapsLock = false });
                source.OnNext(new Settings { ActivationHotkey = ActivationHotkey.AllowedHotkeys[1], ActivateOnCapsLock = true });
                FancyWM.Tests.TestUtilities.Dispatchers.DoEvents();
                CollectionAssert.AreEqual(new[] { ActivationHotkey.AllowedHotkeys[1] }, hotkeys);
                CollectionAssert.AreEqual(new[] { true }, caps);
                source.OnNext(new Settings { ActivationHotkey = ActivationHotkey.AllowedHotkeys[2], ActivateOnCapsLock = false });
                owner.Dispose();
                FancyWM.Tests.TestUtilities.Dispatchers.DoEvents();
                CollectionAssert.AreEqual(new[] { ActivationHotkey.AllowedHotkeys[1] }, hotkeys);
                CollectionAssert.AreEqual(new[] { true }, caps);
            });
        }

        [TestMethod]
        public async Task UnrelatedSettingsDoNotRestartPendingAccent()
        {
            using var source = new Subject<Settings>();
            var initial = new Settings { OverrideAccentColor = true };
            var operations = new List<PendingOperation>();
            var common = new List<Settings>();
            var delivered = new TaskCompletionSource<Settings>();
            using var subscription = RuntimeSettingsSubscription.Observe(source, common.Add, (_, token) =>
            {
                var operation = new PendingOperation(token);
                operations.Add(operation);
                return operation.Completion.Task;
            }).Subscribe(value => delivered.TrySetResult(value));
            try
            {
                source.OnNext(initial);
                for (int index = 0; index < 100; index++)
                {
                    source.OnNext(initial with { WindowPadding = index });
                }
                Assert.AreEqual(101, common.Count, "Common values must still update immediately");
                Assert.AreEqual(1, operations.Count, "Unrelated updates must not enqueue or cancel accent work");
                Assert.IsFalse(operations[0].Token.IsCancellationRequested);

                var latest = initial with { CustomAccentColor = System.Windows.Media.Colors.Red };
                source.OnNext(latest);
                Assert.AreEqual(2, operations.Count);
                Assert.IsTrue(operations[0].Token.IsCancellationRequested);
                operations[1].Completion.SetResult(null);
                Assert.AreSame(latest, await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally
            {
                subscription.Dispose();
                foreach (var operation in operations) { operation.Completion.TrySetResult(null); }
            }
        }

        [TestMethod]
        public void OnlyEffectiveAccentChangesAreApplied()
        {
            using var source = new Subject<Settings>();
            var applied = new List<Settings>();
            using var subscription = RuntimeSettingsSubscription.Observe(source, _ => { }, (settings, _) =>
            {
                applied.Add(settings);
                return Task.CompletedTask;
            }).Subscribe();
            var initial = new Settings();
            source.OnNext(initial);
            var red = initial with { CustomAccentColor = System.Windows.Media.Colors.Red };
            source.OnNext(red);
            Assert.AreEqual(1, applied.Count, "An inactive override must not reload the system accent");
            var enabled = red with { OverrideAccentColor = true };
            source.OnNext(enabled);
            source.OnNext(enabled with { WindowPadding = 20 });
            source.OnNext(initial);
            CollectionAssert.AreEqual(new[] { initial, enabled, initial }, applied);
        }

        [TestMethod]
        public void DirectHotkeysCompareContentAndOnlyDirectBindings()
        {
            using var source = new Subject<Settings>();
            var received = new List<KeybindingDictionary>();
            using var subscription = RuntimeSettingsSubscription.DirectHotkeys(source).Subscribe(received.Add);
            var initial = new Settings
            {
                Keybindings = new KeybindingDictionary(false)
                {
                    [BindableAction.ToggleManager] = new Keybinding(new HashSet<KeyCode> { KeyCode.LeftCtrl, KeyCode.A }, true),
                    [BindableAction.RefreshWorkspace] = new Keybinding(new HashSet<KeyCode> { KeyCode.R }, false),
                    [BindableAction.Cancel] = null,
                },
            };
            source.OnNext(initial);
            for (int index = 0; index < 100; index++)
            {
                source.OnNext(initial with
                {
                    WindowPadding = index,
                    Keybindings = CopyBindings(initial.Keybindings),
                });
            }
            Assert.AreEqual(1, received.Count, "Equal mappings and key sets must not re-register hotkeys");
            var changed = CopyBindings(initial.Keybindings);
            changed[BindableAction.RefreshWorkspace] = new Keybinding(new HashSet<KeyCode> { KeyCode.B }, false);
            source.OnNext(initial with { Keybindings = changed });
            Assert.AreEqual(1, received.Count, "Command-sequence bindings do not affect direct registrations");
            changed[BindableAction.RefreshWorkspace] = new Keybinding(new HashSet<KeyCode> { KeyCode.B }, true);
            source.OnNext(initial with { Keybindings = changed });
            Assert.AreEqual(2, received.Count);
            Assert.AreEqual(2, received[^1].Count);
            changed[BindableAction.ToggleManager] = null;
            source.OnNext(initial with { Keybindings = changed });
            Assert.AreEqual(3, received.Count);
            Assert.AreEqual(1, received[^1].Count);
        }

        [TestMethod]
        public void DirectHotkeySnapshotPreservesQueuedValuesAndDetectsInPlaceChanges()
        {
            using var source = new Subject<Settings>();
            var received = new List<KeybindingDictionary>();
            using var subscription = RuntimeSettingsSubscription.DirectHotkeys(source).Subscribe(received.Add);
            var keys = new HashSet<KeyCode> { KeyCode.LeftCtrl, KeyCode.A };
            var settings = new Settings
            {
                Keybindings = new KeybindingDictionary(false)
                {
                    [BindableAction.ToggleManager] = new Keybinding(keys, true),
                },
            };
            source.OnNext(settings);
            keys.Remove(KeyCode.A);
            keys.Add(KeyCode.B);
            Assert.IsTrue(received[0][BindableAction.ToggleManager]!.Keys.Contains(KeyCode.A));
            source.OnNext(settings);
            Assert.AreEqual(2, received.Count);
            Assert.IsTrue(received[1][BindableAction.ToggleManager]!.Keys.Contains(KeyCode.B));
            settings.Keybindings.Clear();
            Assert.AreEqual(1, received[1].Count);
            source.OnNext(settings);
            Assert.AreEqual(3, received.Count);
            Assert.AreEqual(0, received[2].Count, "Removing the last binding must unregister it");
            source.OnNext(settings);
            Assert.AreEqual(3, received.Count);
        }

        [TestMethod]
        public void ExclusionsCompareContentAndPreserveSnapshotsAcrossInPlaceChanges()
        {
            using var source = new Subject<Settings>();
            var received = new List<Settings>();
            using var subscription = RuntimeSettingsSubscription.Exclusions(source).Subscribe(received.Add);
            var initial = new Settings { ProcessIgnoreList = ["process"], ClassIgnoreList = ["class"] };
            source.OnNext(initial);
            for (int index = 0; index < 100; index++)
            {
                source.OnNext(initial with
                {
                    WindowPadding = index,
                    ProcessIgnoreList = [.. initial.ProcessIgnoreList],
                    ClassIgnoreList = [.. initial.ClassIgnoreList],
                });
            }
            Assert.AreEqual(1, received.Count, "Equal exclusion content must not rescan windows");
            initial.ProcessIgnoreList.Add("new process");
            CollectionAssert.AreEqual(new[] { "process" }, received[0].ProcessIgnoreList);
            source.OnNext(initial);
            Assert.AreEqual(2, received.Count);
            initial.ClassIgnoreList.Clear();
            CollectionAssert.AreEqual(new[] { "class" }, received[1].ClassIgnoreList);
            source.OnNext(initial);
            Assert.AreEqual(3, received.Count);
            Assert.AreEqual(0, received[2].ClassIgnoreList.Count);
            source.OnNext(initial);
            Assert.AreEqual(3, received.Count);
        }

        [TestMethod]
        public void DirectHotkeysPreserveRegistrationOrderForDuplicateChords()
        {
            using var source = new Subject<Settings>();
            var received = new List<KeybindingDictionary>();
            using var subscription = RuntimeSettingsSubscription.DirectHotkeys(source).Subscribe(received.Add);
            var initial = new Settings
            {
                Keybindings = new KeybindingDictionary(false)
                {
                    [BindableAction.ToggleManager] = new Keybinding(new HashSet<KeyCode> { KeyCode.A }, true),
                    [BindableAction.RefreshWorkspace] = new Keybinding(new HashSet<KeyCode> { KeyCode.A }, true),
                },
            };
            source.OnNext(initial);
            source.OnNext(initial with { Keybindings = new KeybindingDictionary(initial.Keybindings.Reverse()) });
            Assert.AreEqual(2, received.Count);
            CollectionAssert.AreEqual(initial.Keybindings.Keys.Reverse().ToArray(), received[1].Keys.ToArray());
        }

        [TestMethod]
        public void DispatcherProjectionsApplyLatestAndCancelPendingWorkAcrossLifetimes()
        {
            RunOnSta(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                for (int cycle = 0; cycle < 100; cycle++)
                {
                    using var source = new Subject<Settings>();
                    var hotkeys = new List<KeybindingDictionary>();
                    var exclusions = new List<Settings>();
                    var operations = new List<DispatcherOperation>();
                    Task Apply<T>(T value, CancellationToken token, Action<T> action)
                    {
                        var operation = dispatcher.InvokeAsync(() => action(value), DispatcherPriority.Normal, token);
                        operations.Add(operation);
                        return operation.Task;
                    }
                    using var directSubscription = RuntimeSettingsSubscription.ApplyLatest(
                        RuntimeSettingsSubscription.DirectHotkeys(source),
                        (value, token) => Apply(value, token, hotkeys.Add)).Subscribe();
                    using var exclusionsSubscription = RuntimeSettingsSubscription.ApplyLatest(
                        RuntimeSettingsSubscription.Exclusions(source),
                        (value, token) => Apply(value, token, exclusions.Add)).Subscribe();
                    var initial = new Settings
                    {
                        Keybindings = new KeybindingDictionary(false)
                        {
                            [BindableAction.ToggleManager] = new Keybinding(new HashSet<KeyCode> { KeyCode.A }, true),
                        },
                    };
                    source.OnNext(initial);
                    source.OnNext(initial with
                    {
                        WindowPadding = 20,
                        Keybindings = CopyBindings(initial.Keybindings),
                        ProcessIgnoreList = [.. initial.ProcessIgnoreList],
                        ClassIgnoreList = [.. initial.ClassIgnoreList],
                    });
                    Assert.AreEqual(2, operations.Count);
                    var latest = initial with
                    {
                        Keybindings = new KeybindingDictionary(false),
                        ProcessIgnoreList = ["latest"],
                    };
                    source.OnNext(latest);
                    Assert.AreEqual(4, operations.Count);
                    Assert.AreEqual(DispatcherOperationStatus.Aborted, operations[0].Status);
                    Assert.AreEqual(DispatcherOperationStatus.Aborted, operations[1].Status);
                    FancyWM.Tests.TestUtilities.Dispatchers.DoEvents();
                    Assert.AreEqual(1, hotkeys.Count);
                    Assert.AreEqual(0, hotkeys[0].Count);
                    Assert.AreEqual(1, exclusions.Count);
                    CollectionAssert.AreEqual(new[] { "latest" }, exclusions[0].ProcessIgnoreList);

                    source.OnNext(initial);
                    Assert.AreEqual(6, operations.Count);
                    directSubscription.Dispose();
                    exclusionsSubscription.Dispose();
                    directSubscription.Dispose();
                    exclusionsSubscription.Dispose();
                    Assert.IsFalse(source.HasObservers);
                    source.OnNext(latest);
                    FancyWM.Tests.TestUtilities.Dispatchers.DoEvents();
                    Assert.AreEqual(1, hotkeys.Count);
                    Assert.AreEqual(1, exclusions.Count);
                    Assert.AreEqual(DispatcherOperationStatus.Aborted, operations[4].Status);
                    Assert.AreEqual(DispatcherOperationStatus.Aborted, operations[5].Status);
                }
            });
        }

        [TestMethod]
        public async Task ProjectedApplyFailureIsObservedAndDisconnectsSource()
        {
            using var source = new Subject<Settings>();
            var failure = new InvalidOperationException("projection failure");
            var observed = new TaskCompletionSource<Exception>();
            int applications = 0;
            using var subscription = RuntimeSettingsSubscription.ApplyLatest(
                RuntimeSettingsSubscription.DirectHotkeys(source), (_, _) =>
                {
                    applications++;
                    return Task.FromException(failure);
                }).Subscribe(_ => Assert.Fail("A failed apply must not complete"), error => observed.TrySetResult(error));
            source.OnNext(new Settings());
            Assert.AreSame(failure, await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(source.HasObservers);
            source.OnNext(new Settings());
            Assert.AreEqual(1, applications);
        }

        [TestMethod]
        public void LateAndReturningConsumersDoNotRepeatCommonSideEffects()
        {
            using var source = new ReplaySubject<Settings>(1);
            var initial = new Settings();
            var updated = initial with { WindowPadding = initial.WindowPadding + 1 };
            source.OnNext(initial);
            var common = new List<Settings>();
            var accents = new List<Settings>();
            var applied = new List<Settings>();
            using var runtime = RuntimeSettingsSubscription.Observe(
                source,
                common.Add,
                (settings, _) =>
                {
                    accents.Add(settings);
                    return Task.CompletedTask;
                }).Subscribe(applied.Add);
            using var consumers = new CompositeDisposable();
            var observed = new List<Settings>[7];
            for (int index = 0; index < observed.Length; index++)
            {
                var values = observed[index] = [];
                consumers.Add(source.Subscribe(values.Add));
            }
            using (source.Take(1).Subscribe()) { }
            Assert.AreEqual(1, common.Count);
            Assert.AreEqual(1, accents.Count);

            source.OnNext(updated);

            CollectionAssert.AreEqual(new[] { initial, updated }, common);
            CollectionAssert.AreEqual(new[] { initial }, accents);
            CollectionAssert.AreEqual(new[] { initial }, applied);
            foreach (var values in observed)
            {
                CollectionAssert.AreEqual(new[] { initial, updated }, values);
            }
            var late = new List<Settings>();
            using var returning = source.Subscribe(late.Add);
            CollectionAssert.AreEqual(new[] { updated }, late);
            Assert.AreEqual(2, common.Count);
            Assert.AreEqual(1, accents.Count);
        }

        [TestMethod]
        public async Task SupersededAccentIsCanceledWithoutDroppingCommonUpdates()
        {
            using var source = new Subject<Settings>();
            var common = new List<Settings>();
            var operations = new List<PendingOperation>();
            var delivered = new List<Settings>();
            var completion = new TaskCompletionSource<Settings>();
            using var subscription = RuntimeSettingsSubscription.Observe(
                source,
                common.Add,
                (_, token) =>
                {
                    var operation = new PendingOperation(token);
                    operations.Add(operation);
                    return operation.Completion.Task;
                }).Subscribe(settings =>
                {
                    delivered.Add(settings);
                    completion.TrySetResult(settings);
                });
            var initial = new Settings { OverrideAccentColor = true };
            var intermediate = initial with { CustomAccentColor = System.Windows.Media.Colors.Green };
            var latest = initial with { CustomAccentColor = System.Windows.Media.Colors.Red };
            try
            {
                source.OnNext(initial);
                source.OnNext(intermediate);
                source.OnNext(latest);

                CollectionAssert.AreEqual(new[] { initial, intermediate, latest }, common);
                Assert.AreEqual(3, operations.Count);
                Assert.IsTrue(operations[0].Token.IsCancellationRequested);
                Assert.IsTrue(operations[1].Token.IsCancellationRequested);
                Assert.IsFalse(operations[2].Token.IsCancellationRequested);
                Assert.AreEqual(0, delivered.Count);

                operations[2].Completion.SetResult(null);
                Assert.AreSame(latest, await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
                operations[0].Completion.SetResult(null);
                operations[1].Completion.SetResult(null);
                CollectionAssert.AreEqual(new[] { latest }, delivered);
            }
            finally
            {
                subscription.Dispose();
                foreach (var operation in operations) { operation.Completion.TrySetResult(null); }
            }
        }

        [TestMethod]
        public async Task InitializationWaitsBeforeExclusionsAndReplaysLatest()
        {
            using var source = new ReplaySubject<Settings>(1);
            var initial = new Settings();
            var latest = initial with { ProcessIgnoreList = ["updated"] };
            source.OnNext(initial);
            var initialization = new TaskCompletionSource<object?>();
            var exclusionsApplied = new TaskCompletionSource<Settings>();
            var initialized = new List<Settings>();
            var excluded = new List<Settings>();
            using var subscription = RuntimeSettingsSubscription.Initialize(source, (settings, _) =>
            {
                initialized.Add(settings);
                return initialization.Task;
            }).Concat(source.Do(settings =>
            {
                excluded.Add(settings);
                exclusionsApplied.TrySetResult(settings);
            }).Select(_ => Unit.Default)).Subscribe();
            try
            {
                Assert.AreEqual(0, excluded.Count);
                source.OnNext(latest);
                Assert.AreEqual(0, excluded.Count);
                initialization.SetResult(null);
                Assert.AreSame(latest, await exclusionsApplied.Task.WaitAsync(TimeSpan.FromSeconds(5)));
                CollectionAssert.AreEqual(new[] { initial }, initialized);
                CollectionAssert.AreEqual(new[] { latest }, excluded);
            }
            finally
            {
                subscription.Dispose();
                initialization.TrySetResult(null);
            }
        }

        [TestMethod]
        public async Task AccentFailureIsObservedOnceAndDisconnectsSource()
        {
            using var source = new Subject<Settings>();
            var common = new List<Settings>();
            var failure = new InvalidOperationException("accent failure");
            var observed = new TaskCompletionSource<Exception>();
            int errorCount = 0;
            using var subscription = RuntimeSettingsSubscription.Observe(
                source,
                common.Add,
                (_, _) => Task.FromException(failure)).Subscribe(_ => Assert.Fail("Failed accent was published"), error =>
                {
                    errorCount++;
                    observed.TrySetResult(error);
                });
            source.OnNext(new Settings());
            Assert.AreSame(failure, await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            source.OnNext(new Settings { WindowPadding = 20 });
            Assert.AreEqual(1, errorCount);
            Assert.AreEqual(1, common.Count);
        }

        [TestMethod]
        public void CommonFailureIsObservedWithoutStartingAccent()
        {
            using var source = new Subject<Settings>();
            var failure = new InvalidOperationException("common failure");
            var errors = new List<Exception>();
            int accentCount = 0;
            using var subscription = RuntimeSettingsSubscription.Observe(
                source,
                _ => throw failure,
                (_, _) =>
                {
                    accentCount++;
                    return Task.CompletedTask;
                }).Subscribe(_ => Assert.Fail("Failed common settings were published"), errors.Add);
            source.OnNext(new Settings());
            CollectionAssert.AreEqual(new[] { failure }, errors);
            Assert.AreEqual(0, accentCount);
        }

        [TestMethod]
        public async Task InitializationFailurePreventsExclusionSubscription()
        {
            using var source = new ReplaySubject<Settings>(1);
            source.OnNext(new Settings());
            var failure = new InvalidOperationException("initialization failure");
            var observed = new TaskCompletionSource<Exception>();
            int excluded = 0;
            using var subscription = RuntimeSettingsSubscription.Initialize(
                source,
                (_, _) => Task.FromException(failure))
                .Concat(source.Do(_ => excluded++).Select(_ => Unit.Default))
                .Subscribe(_ => Assert.Fail("Failed initialization was published"), error => observed.TrySetResult(error));
            Assert.AreSame(failure, await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(0, excluded);
        }

        [TestMethod]
        public void DisposalCancelsOwnedWorkAcrossRepeatedLifetimes()
        {
            for (int cycle = 0; cycle < 100; cycle++)
            {
                using var source = new Subject<Settings>();
                int activeSubscriptions = 0;
                int commonCount = 0;
                int published = 0;
                var sourceWithLifetime = Observable.Create<Settings>(observer =>
                {
                    activeSubscriptions++;
                    var inner = source.Subscribe(observer);
                    return Disposable.Create(() =>
                    {
                        inner.Dispose();
                        activeSubscriptions--;
                    });
                });
                PendingOperation pending = null!;
                using var subscription = RuntimeSettingsSubscription.Observe(
                    sourceWithLifetime,
                    _ => commonCount++,
                    (_, token) =>
                    {
                        pending = new PendingOperation(token);
                        return pending.Completion.Task;
                    }).Subscribe(_ => published++);
                source.OnNext(new Settings());
                try
                {
                    Assert.AreEqual(1, activeSubscriptions);
                    Assert.IsNotNull(pending);
                    subscription.Dispose();
                    subscription.Dispose();
                    Assert.AreEqual(0, activeSubscriptions);
                    Assert.IsTrue(pending.Token.IsCancellationRequested);
                    source.OnNext(new Settings { WindowPadding = 20 });
                    pending.Completion.SetResult(null);
                    Assert.AreEqual(1, commonCount);
                    Assert.AreEqual(0, published);
                }
                finally
                {
                    subscription.Dispose();
                    pending?.Completion.TrySetResult(null);
                }
            }
        }

        [TestMethod]
        public void DisposingInitializationCancelsTaskAndNeverSubscribesExclusions()
        {
            using var source = new ReplaySubject<Settings>(1);
            source.OnNext(new Settings());
            PendingOperation pending = null!;
            int excluded = 0;
            using var subscription = RuntimeSettingsSubscription.Initialize(source, (_, token) =>
            {
                pending = new PendingOperation(token);
                return pending.Completion.Task;
            }).Concat(source.Do(_ => excluded++).Select(_ => Unit.Default)).Subscribe();
            try
            {
                Assert.IsNotNull(pending);
                subscription.Dispose();
                Assert.IsTrue(pending.Token.IsCancellationRequested);
                pending.Completion.SetResult(null);
                Assert.AreEqual(0, excluded);
            }
            finally
            {
                subscription.Dispose();
                pending?.Completion.TrySetResult(null);
            }
        }

        [TestMethod]
        public void DispatcherDisposalAbortsPendingAccentAndInitializationBeforePump()
        {
            RunOnSta(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                using var source = new ReplaySubject<Settings>(1);
                source.OnNext(new Settings());
                var operations = new List<DispatcherOperation>();
                int mutations = 0;
                Task QueueMutation(Settings settings, CancellationToken token)
                {
                    var operation = dispatcher.InvokeAsync(() => { mutations++; }, DispatcherPriority.Normal, token);
                    operations.Add(operation);
                    return operation.Task;
                }
                using var runtime = RuntimeSettingsSubscription.Observe(source, _ => { }, QueueMutation).Subscribe();
                using var initialization = RuntimeSettingsSubscription.Initialize(source, QueueMutation).Subscribe();
                Assert.AreEqual(2, operations.Count);
                foreach (var operation in operations) { Assert.AreEqual(DispatcherOperationStatus.Pending, operation.Status); }

                runtime.Dispose();
                initialization.Dispose();
                FancyWM.Tests.TestUtilities.Dispatchers.DoEvents();

                Assert.AreEqual(0, mutations);
                foreach (var operation in operations) { Assert.AreEqual(DispatcherOperationStatus.Aborted, operation.Status); }
            });
        }

        [TestMethod]
        public void DispatcherSupersessionAppliesOnlyLatestAccent()
        {
            RunOnSta(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                using var source = new Subject<Settings>();
                var common = new List<Settings>();
                var accents = new List<Settings>();
                using var runtime = RuntimeSettingsSubscription.Observe(
                    source,
                    common.Add,
                    (settings, token) => dispatcher.InvokeAsync(() => accents.Add(settings), DispatcherPriority.Normal, token).Task)
                    .Subscribe();
                var initial = new Settings { OverrideAccentColor = true };
                var latest = initial with { CustomAccentColor = System.Windows.Media.Colors.Red };
                source.OnNext(initial);
                source.OnNext(latest);

                FancyWM.Tests.TestUtilities.Dispatchers.DoEvents();

                CollectionAssert.AreEqual(new[] { initial, latest }, common);
                CollectionAssert.AreEqual(new[] { latest }, accents);
            });
        }

        private static KeybindingDictionary CopyBindings(KeybindingDictionary source)
        {
            return new KeybindingDictionary(source.Select(pair => new KeyValuePair<BindableAction, Keybinding?>(
                pair.Key,
                pair.Value == null ? null : new Keybinding(pair.Value.Keys.Reverse().ToHashSet(), pair.Value.IsDirectMode))));
        }

        private static void RunOnSta(Action action)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception exception) { failure = exception; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)), "STA test did not complete");
            if (failure != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); }
        }

        private sealed class PendingOperation(CancellationToken token)
        {
            public CancellationToken Token { get; } = token;
            public TaskCompletionSource<object?> Completion { get; } = new();
        }
    }
}
