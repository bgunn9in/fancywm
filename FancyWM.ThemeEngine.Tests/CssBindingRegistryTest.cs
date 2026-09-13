using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

using FancyWM.ThemeEngine.Wpf;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.ThemeEngine.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class CssBindingRegistryTest
    {
        [TestMethod]
        public Task DependencyCallbacksRunOutsideRegistryLock() => OnSta(() =>
        {
            SetTheme();
            var target = new Target();
            bool? underLock = null;
            var registryLock = typeof(CssBindingRegistry).GetField("s_lock", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            target.Changed = () => underLock = Monitor.IsEntered(registryLock);
            CssBindingRegistry.Register(target, Target.ColorProperty, Resource("button/color"));
            Invalidate();
            Assert.AreEqual(Colors.Red, target.Color);
            Assert.AreEqual(false, underLock, "WPF callbacks must not own the global registry lock.");
        });

        [TestMethod]
        public Task ReentrantRegistrationReplacesPropertyWithoutInvalidatingEnumeration() => OnSta(() =>
        {
            SetTheme();
            var target = new Target();
            var replacement = Resource("button/background-color");
            target.Changed = () =>
            {
                target.Changed = null;
                CssBindingRegistry.Register(target, Target.ColorProperty, replacement);
            };
            CssBindingRegistry.Register(target, Target.ColorProperty, Resource("button/color"));
            Invalidate();
            Assert.AreEqual(Colors.Red, target.Color);
            Invalidate();
            Assert.AreEqual(Colors.Blue, target.Color, "The current registration wins on the next invalidation.");
        });

        [TestMethod]
        public Task IndirectRegistrationIsIdempotentAndNotifiesOutsideLock() => OnSta(() =>
        {
            SetTheme();
            var extension = Resource("button/color");
            var binding = new System.Windows.Data.Binding { Source = extension };
            int notifications = 0;
            var registryLock = typeof(CssBindingRegistry).GetField("s_lock", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            extension.PropertyChanged += (_, args) =>
            {
                Assert.IsFalse(Monitor.IsEntered(registryLock));
                Assert.AreEqual("Value", args.PropertyName);
                Assert.AreEqual(Colors.Red, extension.GetValue());
                notifications++;
            };
            for (int i = 0; i < 100; i++) CssBindingRegistry.Register(binding, extension);
            Invalidate();
            Assert.AreEqual(1, notifications);
            GC.KeepAlive(binding);
        });

        [TestMethod]
        public Task NestedInvalidationDoesNotRepeatObsoleteNotifications() => OnSta(() =>
        {
            SetTheme();
            var target = new Target();
            var extension = Resource("button/color");
            var binding = new System.Windows.Data.Binding { Source = extension };
            int notifications = 0;
            extension.PropertyChanged += (_, _) => notifications++;
            CssBindingRegistry.Register(binding, extension);
            CssBindingRegistry.Register(target, Target.ColorProperty, Resource("button/color"));
            target.Changed = () =>
            {
                target.Changed = null;
                SetTheme("button { color: blue; background-color: red; }");
                Invalidate();
            };
            Invalidate();
            Assert.AreEqual(Colors.Blue, target.Color);
            Assert.AreEqual(Colors.Blue, extension.GetValue());
            Assert.AreEqual(1, notifications, "Only the current generation may complete its notification pass.");
            GC.KeepAlive(binding);
        });

        [TestMethod]
        public Task WorkerInvalidationUsesRegisteredDispatchers() => OnSta(() =>
        {
            SetTheme();
            int owner = Environment.CurrentManagedThreadId;
            int directCalls = 0, indirectCalls = 0;
            var target = new Target { Changed = () => { Assert.AreEqual(owner, Environment.CurrentManagedThreadId); directCalls++; } };
            var extension = Resource("button/color");
            var binding = new System.Windows.Data.Binding { Source = extension };
            extension.PropertyChanged += (_, _) => { Assert.AreEqual(owner, Environment.CurrentManagedThreadId); indirectCalls++; };
            CssBindingRegistry.Register(target, Target.ColorProperty, Resource("button/color"));
            CssBindingRegistry.Register(binding, extension);
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            var work = Task.Run(() =>
            {
                try { Invalidate(); }
                finally { dispatcher.BeginInvoke(() => frame.Continue = false); }
            });
            Dispatcher.PushFrame(frame);
            work.GetAwaiter().GetResult();
            Assert.AreEqual(Colors.Red, target.Color);
            Assert.AreEqual(1, directCalls);
            Assert.AreEqual(1, indirectCalls);
            GC.KeepAlive(binding);
        });

        [TestMethod]
        public Task RegistrationDoesNotKeepDiscardedTargetsOrBindingsAlive() => OnSta(() =>
        {
            SetTheme();
            var references = new List<WeakReference>();
            for (int i = 0; i < 100; i++) references.AddRange(RegisterDiscarded());
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.IsTrue(references.TrueForAll(reference => !reference.IsAlive));
            Invalidate();
        });

        [TestMethod]
        public Task ForeignInvalidationBurstPostsOneLatestPassWithoutWaiting() => OnSta(() =>
        {
            SetTheme();
            var target = new Target();
            int changes = 0, posts = 0;
            target.Changed = () => changes++;
            CssBindingRegistry.Register(target, Target.ColorProperty, Resource("button/color"));
            var dispatcher = Dispatcher.CurrentDispatcher;
            DispatcherHookEventHandler posted = (_, _) => Interlocked.Increment(ref posts);
            dispatcher.Hooks.OperationPosted += posted;
            try
            {
                var work = Task.Run(() => { for (int i = 0; i < 100; i++) Invalidate(); });
                Assert.IsTrue(work.Wait(TimeSpan.FromSeconds(5)), "Invalidation must not wait for a foreign UI callback.");
                Assert.AreEqual(1, posts, "A stalled owner must have only one pending pass.");
            }
            finally { dispatcher.Hooks.OperationPosted -= posted; }
            SetTheme("button { color: blue; background-color: red; }");
            Pump(dispatcher);
            Assert.AreEqual(Colors.Blue, target.Color, "The pending pass resolves the current theme.");
            Assert.AreEqual(1, changes);
        });

        [TestMethod]
        public Task QueuedPassDoesNotRetainTargetsBetweenSnapshotAndApply() => OnSta(() =>
        {
            SetTheme();
            var references = RegisterDiscarded();
            Assert.IsTrue(Task.Run(Invalidate).Wait(TimeSpan.FromSeconds(5)));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.IsTrue(Array.TrueForAll(references, reference => !reference.IsAlive));
            Pump(Dispatcher.CurrentDispatcher);
        });

        [TestMethod]
        public Task ConcurrentOwnersShareLazyValuesWithoutDuplicateConversion() => OnSta(() =>
        {
            SetTheme("button { color: red; background-color: blue; border: 2px solid black; }");
            var resources = CssManager.Current;
            var extension = Resource("button/background-color");
            extension.As = typeof(Brush);
            using var start = new Barrier(8);
            var tasks = new Task<Brush>[8];
            for (int i = 0; i < tasks.Length; i++)
                tasks[i] = Task.Run(() =>
                {
                    Assert.IsTrue(start.SignalAndWait(TimeSpan.FromSeconds(5)));
                    Brush result = null;
                    for (int j = 0; j < 100; j++)
                    {
                        Assert.AreEqual(new Thickness(2), resources["button/border-width"].As<Thickness>());
                        result = (Brush)extension.GetValue();
                        Assert.IsTrue(result.IsFrozen);
                        Assert.AreEqual(Colors.Blue, ((SolidColorBrush)result).Color);
                        foreach (var pair in resources) Assert.IsNotNull(pair.Value);
                    }
                    return result;
                });
            Assert.IsTrue(Task.WaitAll(tasks, TimeSpan.FromSeconds(10)));
            foreach (var task in tasks) Assert.AreSame(tasks[0].Result, task.Result);
        });

        private static void Pump(Dispatcher dispatcher)
        {
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }

        [TestMethod]
        public Task UnfrozenCachedValuesAreNotReturnedToAnotherOwner() => OnSta(() =>
        {
            SetTheme();
            var value = CssManager.Current["button/color"];
            var foreign = new TaskCompletionSource<Brush>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() => foreign.SetResult(new SolidColorBrush(Colors.Blue))) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)));
            var foreignBrush = foreign.Task.GetAwaiter().GetResult();
            Assert.IsFalse(foreignBrush.CheckAccess());
            var cache = (Dictionary<Type, object>)typeof(CssValue).GetField("m_cache", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);
            cache[typeof(Brush)] = foreignBrush;
            var extension = Resource("button/color");
            extension.As = typeof(Brush);
            typeof(CssResourceExtension).GetField("m_previousValue", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(extension, value);
            typeof(CssResourceExtension).GetField("m_previousResult", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(extension, foreignBrush);
            var actual = (SolidColorBrush)extension.GetValue();
            Assert.AreEqual(Colors.Red, actual.Color);
            Assert.IsTrue(actual.IsFrozen);
            Assert.AreNotSame(foreignBrush, actual);
            Assert.AreSame(actual, extension.GetValue());
            Assert.AreSame(actual, value.As<Brush>());
            Assert.AreEqual(1, cache.Count, "Cross-owner conversion replaces the same bounded type slot.");
        });

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference[] RegisterDiscarded()
        {
            var target = new Target();
            var direct = Resource("button/color");
            var indirect = Resource("button/color");
            var binding = new System.Windows.Data.Binding { Source = indirect };
            CssBindingRegistry.Register(target, Target.ColorProperty, direct);
            CssBindingRegistry.Register(binding, indirect);
            return [new(target), new(direct), new(binding), new(indirect)];
        }

        private static CssResourceExtension Resource(string path) => new(path) { As = typeof(Color) };

        private static void SetTheme(string css = "button { color: red; background-color: blue; }")
        {
            var resources = new CssToWpfResourceConverter().Convert("<button></button>", css);
            // Exercise the real registry without constructing a WPF Application or HWND.
            typeof(CssManager).GetField("_current", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, resources);
        }

        private static void Invalidate()
        {
            try { typeof(CssBindingRegistry).GetMethod("Invalidate", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
        }

        private static async Task OnSta(Action action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    ClearRegistry();
                    action();
                    completion.SetResult();
                }
                catch (Exception error) { completion.SetException(error); }
                finally { ClearRegistry(); System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)), "Owned STA test thread must exit."); }
        }

        private static void ClearRegistry()
        {
            foreach (var name in new[] { "s_bindings", "s_indirectBindings" })
            {
                var table = typeof(CssBindingRegistry).GetField(name, BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                table.GetType().GetMethod("Clear").Invoke(table, null);
            }
        }

        private sealed class Target : DependencyObject
        {
            public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
                "Color", typeof(Color), typeof(Target), new PropertyMetadata(Colors.Transparent,
                    (obj, _) => ((Target)obj).Changed?.Invoke()));
            public Action Changed { get; set; }
            public Color Color => (Color)GetValue(ColorProperty);
        }
    }
}
