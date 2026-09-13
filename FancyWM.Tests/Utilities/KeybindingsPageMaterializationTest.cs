#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

using FancyWM.Controls;
using FancyWM.Models;
using FancyWM.Pages.Settings;
using FancyWM.Tests.TestUtilities;
using FancyWM.ViewModels;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ModernWpf;
using ModernWpf.Controls;

using Serilog;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class KeybindingsPageMaterializationTest
    {
        private const string ChildFlag = "FANCYWM_PERF_KEYBINDINGS_PAGE_CHILD";
        private const string ChildValue = "isolated-keybindings-page";
        private const string CounterTest = "FancyWM.Tests.Utilities.KeybindingsPageMaterializationTest.KeybindingsPageMaterializationCounterScenario";
        private const string TooltipTest = "FancyWM.Tests.Utilities.KeybindingsPageMaterializationTest.DescriptionTooltipsPreserveBindingAndCurrentOwnership";

        private static readonly BindableAction[] GroupRepresentatives =
        [
            BindableAction.ToggleManager,
            BindableAction.CreateHorizontalPanel,
            BindableAction.ToggleMasterSatelliteLayout,
            BindableAction.PullWindowUp,
            BindableAction.MoveFocusLeft,
            BindableAction.IncreaseWidth,
            BindableAction.SwitchToPreviousDesktop,
            BindableAction.SwitchToPreviousDisplay,
        ];

        public TestContext TestContext { get; set; } = null!;

        [TestMethod]
        public Task DescriptionTooltipsPreserveBindingAndCurrentOwnership() => RunOwnedAsync(TooltipTest, () =>
        {
            using var owner = CreatePage(bindingsPerGroup: 1);
            Materialize(owner.Page);
            var hosts = DescriptionHosts(owner.Page);
            Assert.AreEqual(GroupRepresentatives.Length, hosts.Length);
            foreach (var host in hosts)
            {
                var model = (KeybindingViewModel)host.DataContext;
                Assert.IsInstanceOfType(host.ToolTip, typeof(TextBlock));
                var tooltip = (TextBlock)host.ToolTip;
                var binding = BindingOperations.GetBinding(tooltip, TextBlock.TextProperty);
                Assert.IsNotNull(binding);
                Assert.AreEqual(nameof(KeybindingViewModel.Description), binding.Path.Path);
                Assert.IsFalse(string.IsNullOrWhiteSpace(model.Description));
                Assert.IsNull(tooltip.DataContext,
                    "The unopened tooltip remains outside the host's inherited data-context tree.");
            }
            AssertNoWindow(owner.Page);
        });

        [TestMethod]
        public Task KeybindingsPageMaterializationCounterScenario() => RunOwnedAsync(CounterTest, () =>
        {
            Assert.AreEqual("0", Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"));
            foreach (int bindingsPerGroup in new[] { 1, 10, 50 })
                MeasureGroup(bindingsPerGroup);
        }, new Dictionary<string, string> { ["DOTNET_TieredCompilation"] = "0" });

        private static void MeasureGroup(int bindingsPerGroup)
        {
            using var owner = CreateModel(bindingsPerGroup);
            long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            long beforeTicks = Stopwatch.GetTimestamp();
            var page = new KeybindingsPage(owner.Model);
            long constructorTicks = Stopwatch.GetTimestamp() - beforeTicks;
            long constructorBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;

            beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            beforeTicks = Stopwatch.GetTimestamp();
            Materialize(page);
            long layoutTicks = Stopwatch.GetTimestamp() - beforeTicks;
            long layoutBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;

            var visuals = Visuals(page);
            var logical = LogicalElements(page);
            var objects = new HashSet<DependencyObject>(visuals.Concat(logical));
            var hosts = DescriptionHosts(page);
            int bindingCount = bindingsPerGroup * GroupRepresentatives.Length;
            Assert.AreEqual(bindingCount, objects.OfType<KeyPressBox>().Count());
            Assert.AreEqual(bindingCount, hosts.Length);
            Assert.AreEqual(10, objects.OfType<ItemsControl>().Count(),
                "The activation ComboBox, one group list and eight binding lists must materialize.");
            AssertNoWindow(page);

            string scenario = "keybindings-page-" + bindingsPerGroup;
            Emit(scenario, "constructor-bytes", constructorBytes);
            Emit(scenario, "constructor-ticks", constructorTicks);
            Emit(scenario, "layout-bytes", layoutBytes);
            Emit(scenario, "layout-ticks", layoutTicks);
            Emit(scenario, "timestamp-frequency", Stopwatch.Frequency);
            Emit(scenario, "groups", GroupRepresentatives.Length);
            Emit(scenario, "bindings", bindingCount);
            Emit(scenario, "visuals", visuals.Count);
            Emit(scenario, "logical", logical.Count);
            Emit(scenario, "dependency-objects", objects.Count);
            Emit(scenario, "items-controls", objects.OfType<ItemsControl>().Count());
            Emit(scenario, "key-press-boxes", objects.OfType<KeyPressBox>().Count());
            Emit(scenario, "text-blocks", objects.OfType<TextBlock>().Count());
            Emit(scenario, "description-hosts", hosts.Length);
            Emit(scenario, "eager-description-tooltip-elements", hosts.Count(host => host.ToolTip is UIElement));
            Emit(scenario, "description-tooltip-strings", hosts.Count(host => host.ToolTip is string));
            page.Content = null;
            page.DataContext = null;
            GC.KeepAlive(page);
        }

        private static PageOwner CreatePage(int bindingsPerGroup)
        {
            var model = CreateModel(bindingsPerGroup);
            return new PageOwner(model, new KeybindingsPage(model.Model));
        }

        private static ModelOwner CreateModel(int bindingsPerGroup)
        {
            var entity = new SettingsEntity(new Settings { Keybindings = new KeybindingDictionary(useDefaults: false) });
            var model = new SettingsViewModel(entity, new LoggerConfiguration().CreateLogger(), () => Task.FromResult(false));
            var keybindings = new ObservableCollection<KeybindingViewModel>(
                GroupRepresentatives.SelectMany(action => Enumerable.Range(0, bindingsPerGroup).Select(_ =>
                    new KeybindingViewModel { Action = action })));
            typeof(SettingsViewModel).GetField("m_keybindings", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(model, keybindings);
            return new ModelOwner(entity, model);
        }

        private static TextBlock[] DescriptionHosts(DependencyObject root) =>
            Visuals(root).Concat(LogicalElements(root)).OfType<TextBlock>().Distinct()
                .Where(text => text.DataContext is KeybindingViewModel && text.ToolTip != null)
                .ToArray();

        private static List<DependencyObject> Visuals(DependencyObject root)
        {
            var result = new List<DependencyObject> { root };
            for (int index = 0; index < result.Count; index++)
                for (int child = 0; child < VisualTreeHelper.GetChildrenCount(result[index]); child++)
                    result.Add(VisualTreeHelper.GetChild(result[index], child));
            return result;
        }

        private static List<DependencyObject> LogicalElements(DependencyObject root)
        {
            var result = new List<DependencyObject> { root };
            var seen = new HashSet<DependencyObject> { root };
            for (int index = 0; index < result.Count; index++)
                foreach (var child in LogicalTreeHelper.GetChildren(result[index]))
                    if (child is DependencyObject element && seen.Add(element)) result.Add(element);
            return result;
        }

        private static void Materialize(FrameworkElement element)
        {
            element.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            element.ApplyTemplate();
            element.Measure(new Size(640, 480));
            element.Arrange(new Rect(0, 0, 640, 480));
            element.UpdateLayout();
            element.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        }

        private static void AssertNoWindow(FrameworkElement element)
        {
            Assert.IsNull(Window.GetWindow(element));
            Assert.IsNull(PresentationSource.FromVisual(element));
            Assert.AreEqual(0, Application.Current.Windows.Count);
        }

        private static void Emit(string scenario, string metric, long value) =>
            Console.WriteLine($"PERFCOUNTER {scenario} {metric} {value}");

        private Task RunOwnedAsync(string testName, Action action,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            if (Environment.GetEnvironmentVariable(ChildFlag) == ChildValue)
                return RunOnSta(() =>
                {
                    Assert.IsNull(Application.Current);
                    var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    try
                    {
                        var theme = new ThemeResources
                        {
                            CanBeAccessedAcrossThreads = true,
                            RequestedTheme = ApplicationTheme.Dark,
                            AccentColor = Colors.CornflowerBlue,
                        };
                        theme.BeginInit();
                        application.Resources.MergedDictionaries.Add(theme);
                        theme.EndInit();
                        application.Resources.MergedDictionaries.Add(new XamlControlsResources());
                        application.Resources.MergedDictionaries.Add(new ResourceDictionary
                        {
                            Source = new Uri("/FancyWM;component/Themes/Fluent/Rounded.xaml", UriKind.Relative),
                        });
                        application.Resources.MergedDictionaries.Add(new ResourceDictionary
                        {
                            Source = new Uri("/FancyWM;component/Themes/Fluent/Generic.xaml", UriKind.Relative),
                        });
                        application.Resources["MicaPrimaryColor"] = Colors.Black;
                        application.Resources["MicaOpacity"] = 0.8;
                        action();
                    }
                    finally { application.Shutdown(); }
                });

            return IsolatedTestProcess.RunAsync(TestContext, typeof(KeybindingsPageMaterializationTest), testName,
                ChildFlag, ChildValue, "keybindings-page-child", environment);
        }

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
            try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(45)); }
            finally { Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5))); }
        }

        private sealed class PageOwner(ModelOwner model, KeybindingsPage page) : IDisposable
        {
            public KeybindingsPage Page { get; } = page;
            public void Dispose()
            {
                Page.Content = null;
                Page.DataContext = null;
                model.Dispose();
            }
        }

        private sealed class ModelOwner(SettingsEntity entity, SettingsViewModel model) : IDisposable
        {
            public SettingsViewModel Model { get; } = model;
            public void Dispose()
            {
                Model.Dispose();
                Assert.AreEqual(0, entity.ActiveSubscriptions);
            }
        }

        private sealed class SettingsEntity(Settings current) : IObservableFileEntity<Settings>
        {
            private readonly List<IObserver<Settings>> m_observers = [];
            private Settings m_current = current;
            public string FullPath => "keybindings-page-materialization.json";
            public IObservable<Settings> Value => this;
            public int ActiveSubscriptions => m_observers.Count;
            public IDisposable Subscribe(IObserver<Settings> observer)
            {
                m_observers.Add(observer);
                observer.OnNext(m_current);
                return new Subscription(m_observers, observer);
            }
            public Task SaveAsync(Func<Settings, Settings> update)
            {
                m_current = update(m_current);
                foreach (var observer in m_observers.ToArray()) observer.OnNext(m_current);
                return Task.CompletedTask;
            }
            public Task FlushAsync() => Task.CompletedTask;

            private sealed class Subscription(List<IObserver<Settings>> observers, IObserver<Settings> observer) : IDisposable
            {
                public void Dispose() => observers.Remove(observer);
            }
        }
    }
}
