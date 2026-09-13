using System;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

using FancyWM.Models;

namespace FancyWM.Utilities
{
    internal static class RuntimeSettingsSubscription
    {
        public static CompositeDisposable SubscribeAll(params Func<IDisposable>[] factories)
        {
            ArgumentNullException.ThrowIfNull(factories);
            var acquired = new IDisposable[factories.Length];
            int count = 0;
            try
            {
                foreach (var factory in factories)
                {
                    ArgumentNullException.ThrowIfNull(factory);
                    var subscription = factory();
                    ArgumentNullException.ThrowIfNull(subscription);
                    acquired[count++] = subscription;
                }
                return new CompositeDisposable(acquired);
            }
            catch (Exception error)
            {
                // A later Subscribe can throw before the aggregate owner exists.
                // Release every acquired source, including its queued UI work.
                List<Exception>? cleanupErrors = null;
                for (int index = 0; index < count; index++)
                {
                    try { acquired[index].Dispose(); }
                    catch (Exception cleanupError) { (cleanupErrors ??= []).Add(cleanupError); }
                }
                if (cleanupErrors != null)
                {
                    error.Data["RuntimeSettingsSubscription.CleanupExceptions"] = new AggregateException(cleanupErrors);
                }
                throw;
            }
        }

        public static IObservable<Settings> Observe(
            IObservable<Settings> source,
            Action<Settings> applyCommon,
            Func<Settings, CancellationToken, Task> applyAccentAsync)
        {
            return source
                .Do(applyCommon)
                .DistinctUntilChanged(settings => settings.OverrideAccentColor ? (Color?)settings.CustomAccentColor : null)
                .Select(settings => Observable.FromAsync(token => applyAccentAsync(settings, token))
                    .Select(_ => settings))
                .Switch();
        }

        public static IObservable<Unit> Initialize(
            IObservable<Settings> source,
            Func<Settings, CancellationToken, Task> initializeAsync)
        {
            return source
                .Take(1)
                .SelectMany(settings => Observable.FromAsync(token => initializeAsync(settings, token)));
        }

        public static IObservable<KeybindingDictionary> DirectHotkeys(IObservable<Settings> source)
        {
            return Observable.Defer(() =>
            {
                KeybindingDictionary? previous = null;
                return source.Select(settings => settings.Keybindings)
                    .Where(current => previous == null || !DirectHotkeysMatch(current, previous))
                    .Select(current => previous = new KeybindingDictionary(current
                        .Where(pair => pair.Value?.IsDirectMode == true)
                        .Select(pair => new KeyValuePair<BindableAction, Keybinding?>(
                            pair.Key, new Keybinding(pair.Value!.Keys.ToFrozenSet(), true)))));
            });
        }

        public static IObservable<Settings> Exclusions(IObservable<Settings> source)
        {
            return Observable.Defer(() =>
            {
                Settings? previous = null;
                return source.Where(current => previous == null
                    || !current.ProcessIgnoreList.SequenceEqual(previous.ProcessIgnoreList, StringComparer.Ordinal)
                    || !current.ClassIgnoreList.SequenceEqual(previous.ClassIgnoreList, StringComparer.Ordinal))
                    .Select(current => previous = current with
                    {
                        ProcessIgnoreList = [.. current.ProcessIgnoreList],
                        ClassIgnoreList = [.. current.ClassIgnoreList],
                    });
            });
        }

        public static IObservable<Unit> ApplyLatest<T>(
            IObservable<T> source,
            Func<T, CancellationToken, Task> applyAsync)
        {
            return source.Select(value => Observable.FromAsync(token => applyAsync(value, token))).Switch();
        }

        private static bool DirectHotkeysMatch(KeybindingDictionary current, KeybindingDictionary previous)
        {
            // Registration order is observable when a manually edited file assigns
            // the same direct chord to multiple actions. Preserve that order.
            var retained = previous.GetEnumerator();
            foreach (var pair in current)
            {
                if (pair.Value?.IsDirectMode != true) { continue; }
                if (!retained.MoveNext()
                    || retained.Current.Key != pair.Key
                    || !retained.Current.Value!.Keys.SetEquals(pair.Value.Keys))
                {
                    return false;
                }
            }
            return !retained.MoveNext();
        }
    }
}
