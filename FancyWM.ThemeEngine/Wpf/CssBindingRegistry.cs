using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace FancyWM.ThemeEngine.Wpf
{
    public static class CssBindingRegistry
    {
        private class Binding(DependencyProperty property, CssResourceExtension extension)
        {
            public readonly DependencyProperty Property = property;
            public readonly CssResourceExtension Extension = extension;
        }

        private static readonly ConditionalWeakTable<DependencyObject, List<Binding>> s_bindings = new();
        private sealed record IndirectBinding(CssResourceExtension Extension, Dispatcher Dispatcher);

        private static readonly ConditionalWeakTable<System.Windows.Data.Binding, IndirectBinding> s_indirectBindings = new();
        private static readonly object s_lock = new();
        private static long s_generation;
        private sealed class PendingInvalidation { public bool Scheduled; }
        private static readonly ConditionalWeakTable<Dispatcher, PendingInvalidation> s_pending = new();

        static CssBindingRegistry()
        {
            CssManager.ThemeChanged += Invalidate;
        }

        public static void Register(DependencyObject obj, DependencyProperty dp, CssResourceExtension extension)
        {
            ArgumentNullException.ThrowIfNull(obj);
            ArgumentNullException.ThrowIfNull(dp);
            ArgumentNullException.ThrowIfNull(extension);

            var b = new Binding(dp, extension);
            lock (s_lock)
            {
                if (s_bindings.TryGetValue(obj, out var list))
                {
                    int index = list.FindIndex(existing => existing.Property == dp);
                    if (index < 0) list.Add(b);
                    else if (!ReferenceEquals(list[index].Extension, extension)) list[index] = b;
                }
                else
                {
                    s_bindings.Add(obj, [b]);
                }
            }
        }

        public static void Register(System.Windows.Data.Binding binding, CssResourceExtension extension)
        {
            ArgumentNullException.ThrowIfNull(binding);
            ArgumentNullException.ThrowIfNull(extension);

            lock (s_lock)
            {
                if (s_indirectBindings.TryGetValue(binding, out var current)
                    && ReferenceEquals(current.Extension, extension)) return;
                s_indirectBindings.AddOrUpdate(binding, new(extension, Dispatcher.CurrentDispatcher));
            }
        }

        private static void Invalidate()
        {
            Dispatcher[] dispatchers;
            long generation;
            lock (s_lock)
            {
                generation = ++s_generation;
                dispatchers = s_bindings.Select(pair => pair.Key.Dispatcher)
                    .Concat(s_indirectBindings.Select(pair => pair.Value.Dispatcher)).Distinct().ToArray();
            }
            foreach (var dispatcher in dispatchers)
            {
                if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) continue;
                if (dispatcher.CheckAccess()) ApplyDispatcher(dispatcher, generation);
                else ScheduleDispatcher(dispatcher);
            }
        }

        private static void ScheduleDispatcher(Dispatcher dispatcher)
        {
            PendingInvalidation pending;
            lock (s_lock)
            {
                pending = s_pending.GetOrCreateValue(dispatcher);
                if (pending.Scheduled) return;
                pending.Scheduled = true;
            }
            try
            {
                var operation = dispatcher.BeginInvoke(() =>
                {
                    long generation;
                    lock (s_lock)
                    {
                        pending.Scheduled = false;
                        generation = s_generation;
                    }
                    ApplyDispatcher(dispatcher, generation);
                });
                if (operation.Status == DispatcherOperationStatus.Aborted)
                    lock (s_lock) pending.Scheduled = false;
            }
            catch
            {
                lock (s_lock) pending.Scheduled = false;
                throw;
            }
        }

        private static void ApplyDispatcher(Dispatcher dispatcher, long generation)
        {
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            dispatcher.VerifyAccess();
            (DependencyObject Target, Binding[] Bindings)[] direct;
            KeyValuePair<System.Windows.Data.Binding, IndirectBinding>[] indirect;
            lock (s_lock)
            {
                if (generation != s_generation) return;
                direct = s_bindings.Where(pair => pair.Key.Dispatcher == dispatcher)
                    .Select(pair => (pair.Key, pair.Value.ToArray())).ToArray();
                indirect = s_indirectBindings.Where(pair => pair.Value.Dispatcher == dispatcher).ToArray();
            }

            // Snapshot only weak-table live entries under the registry lock. WPF
            // setters and notifications may register bindings or apply another theme.
            foreach (var (obj, bindings) in direct)
            {
                foreach (var b in bindings)
                {
                    if (!IsCurrent(obj, b, generation)) continue;
                    var value = b.Extension.GetValue();
                    if (IsCurrent(obj, b, generation)) obj.SetValue(b.Property, value);
                }
            }
            foreach (var (binding, registration) in indirect)
            {
                lock (s_lock)
                {
                    if (generation != s_generation || !s_indirectBindings.TryGetValue(binding, out var current)
                        || !ReferenceEquals(current, registration)) continue;
                }
                registration.Extension.NotifyPropertyChanged();
            }
        }

        private static bool IsCurrent(DependencyObject target, Binding binding, long generation)
        {
            lock (s_lock)
                return generation == s_generation && s_bindings.TryGetValue(target, out var current)
                    && current.Contains(binding);
        }

    }

}
