#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using FancyWM.Controls;
using FancyWM.Tests.TestUtilities;
using FancyWM.Utilities;
using FancyWM.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModernWpf;
using ModernWpf.Controls;
using ShapePath = System.Windows.Shapes.Path;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class ActionBarMaterializationTest
    {
        private const string ChildFlag = "FANCYWM_PERF_ACTIONBAR_CHILD";
        private const string ChildValue = "isolated-generic-wpf-application";
        private const string TestName = "FancyWM.Tests.Utilities.ActionBarMaterializationTest.ActionBarMaterializationCounterScenario";

        public TestContext TestContext { get; set; } = null!;

        [TestMethod]
        public async Task ActionBarMaterializationCounterScenario()
        {
            if (Environment.GetEnvironmentVariable(ChildFlag) == ChildValue)
            {
                await RunOnSta(MeasureWithOwnedApplication);
                return;
            }

            // WPF permits one Application per process, including after Shutdown.
            // Keep its and ModernWpf's static owners out of the normal test host.
            var originalApplication = Application.Current;
            await IsolatedTestProcess.RunAsync(TestContext, typeof(ActionBarMaterializationTest), TestName,
                ChildFlag, ChildValue, "actionbar-child");
            Assert.AreSame(originalApplication, Application.Current);
        }

        private static void MeasureWithOwnedApplication()
        {
            Assert.IsNull(Application.Current);
            var iconOwner = typeof(WindowExtensions).GetField("m_iconOwner", BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.IsNotNull(iconOwner);
            Assert.IsNull(iconOwner.GetValue(null), "The isolated fixture must not start icon discovery.");
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                // Actual resource dictionaries used by FancyWM; only the app-local
                // color/opacity input is fixed, without creating FauxMicaProvider.
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
                Assert.AreEqual(typeof(Application), application.GetType());
                Assert.AreEqual(new CornerRadius(0, 0, 4, 4), application.FindResource("ControlCornerRadiusBottom"));

                // Resource setup is excluded. The W1 sample still includes cold
                // control initialization; later groups reuse framework caches.
                foreach (int count in new[] { 1, 10, 25, 50 }) MeasureGroup(application, count);
                Assert.AreEqual(0, application.Windows.Count);
                Assert.IsNull(iconOwner.GetValue(null), "Node=null must leave native icon discovery uninitialized.");
            }
            finally { application.Shutdown(); }
        }

        private static void MeasureGroup(Application application, int count)
        {
            using var overlay = new TilingOverlayViewModel { DisplayScaling = 1, FontSize = 12, IconSize = 16 };
            var models = Enumerable.Range(0, count).Select(_ => new TilingWindowViewModel
            {
                Overlay = overlay,
                ActionsVisibility = Visibility.Hidden,
            }).ToArray();
            var controls = new TilingWindow[count];
            try
            {
                long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
                long beforeTicks = Stopwatch.GetTimestamp();
                for (int index = 0; index < count; index++) controls[index] = new TilingWindow { ViewModel = models[index] };
                long constructorTicks = Stopwatch.GetTimestamp() - beforeTicks;
                long constructorBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
                var before = CountMaterialization(controls);
                beforeBytes = GC.GetAllocatedBytesForCurrentThread();
                beforeTicks = Stopwatch.GetTimestamp();
                foreach (var control in controls) Materialize(control);
                long layoutTicks = Stopwatch.GetTimestamp() - beforeTicks;
                long layoutBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
                var after = CountMaterialization(controls);
                Assert.AreEqual(count * 6, before.Buttons);
                Assert.AreEqual(count * 5, before.SvgIcons);
                Assert.AreEqual(count * 6, after.Buttons);
                Assert.AreEqual(count * 5, after.SvgIcons);
                Assert.AreEqual(count * 5, after.ActivePaths);
                Assert.AreEqual(count * 6, before.TooltipContents);
                Assert.AreEqual(count * 6, after.TooltipContents);
                Assert.AreEqual(count, before.ContextMenus);
                Assert.AreEqual(count * 2, before.MenuItems);
                Assert.AreEqual(count, after.ShadowEffects);
                Assert.AreEqual(count, after.OpacityMasks);
                for (int index = 0; index < count; index++)
                {
                    var control = controls[index];
                    Assert.AreSame(models[index], control.ViewModel);
                    Assert.IsNull(models[index].Node);
                    Assert.IsNull(models[index].Icon);
                    Assert.IsNull(Window.GetWindow(control));
                    Assert.IsNull(PresentationSource.FromVisual(control));
                    Assert.IsFalse(control.ContextMenu.IsOpen);
                    Assert.AreEqual(Visibility.Hidden, models[index].ActionsVisibility);
                    Assert.AreEqual(6, Visuals(control).OfType<Button>().Count());
                    foreach (var svg in Visuals(control).OfType<SvgIcon>())
                        Assert.AreEqual(1, Visuals(svg).OfType<ShapePath>().Count());
                    var actionBar = Visuals(control).OfType<Border>().Single(border => border.Effect is DropShadowEffect);
                    Assert.AreEqual(Visibility.Visible, actionBar.Visibility);
                    Assert.AreEqual(0.0, actionBar.Opacity);
                }
                Assert.AreEqual(0, application.Windows.Count);
                string scenario = "actionbar-materialization-" + count;
                Emit(scenario, "constructor-bytes", constructorBytes);
                Emit(scenario, "constructor-ticks", constructorTicks);
                Emit(scenario, "layout-bytes", layoutBytes);
                Emit(scenario, "layout-ticks", layoutTicks);
                Emit(scenario, "timestamp-frequency", Stopwatch.Frequency);
                EmitCounts(scenario, "before-layout", before);
                EmitCounts(scenario, "after-layout", after);
                Emit(scenario, "vm-count", count);
                GC.KeepAlive(controls);
            }
            finally
            {
                foreach (var control in controls)
                    if (control != null) control.ViewModel = null!;
                foreach (var model in models) model.Dispose();
            }
        }

        private readonly record struct Counts(int Visuals, int Logical, int Buttons, int SvgIcons, int ActivePaths,
            int TooltipContents, int ContextMenus, int MenuItems, int ShadowEffects, int OpacityMasks);

        private static Counts CountMaterialization(TilingWindow[] controls)
        {
            var visuals = controls.SelectMany(Visuals).ToArray();
            var logical = controls.SelectMany(LogicalElements).ToArray();
            var objects = new HashSet<DependencyObject>(visuals.Concat(logical));
            var buttons = objects.OfType<Button>().ToArray();
            Assert.IsTrue(buttons.All(button => button.ToolTip is TextBlock),
                "The declared tooltip TextBlocks exist without materializing a ToolTip popup.");
            return new Counts(visuals.Length, logical.Length, buttons.Length, objects.OfType<SvgIcon>().Count(),
                visuals.OfType<ShapePath>().Count(), buttons.Count(button => button.ToolTip is TextBlock),
                controls.Count(control => control.ContextMenu != null),
                controls.Sum(control => control.ContextMenu.Items.OfType<MenuItem>().Count()),
                objects.OfType<UIElement>().Count(element => element.Effect is DropShadowEffect),
                objects.OfType<UIElement>().Count(element => element.OpacityMask is VisualBrush { Visual: not null }));
        }

        private static void EmitCounts(string scenario, string phase, Counts counts)
        {
            Emit(scenario, "visuals-" + phase, counts.Visuals);
            Emit(scenario, "logical-" + phase, counts.Logical);
            Emit(scenario, "buttons-" + phase, counts.Buttons);
            Emit(scenario, "svg-icons-" + phase, counts.SvgIcons);
            Emit(scenario, "active-paths-" + phase, counts.ActivePaths);
            Emit(scenario, "tooltip-content-" + phase, counts.TooltipContents);
            Emit(scenario, "context-menus-" + phase, counts.ContextMenus);
            Emit(scenario, "menu-items-" + phase, counts.MenuItems);
            Emit(scenario, "shadow-effects-" + phase, counts.ShadowEffects);
            Emit(scenario, "opacity-masks-" + phase, counts.OpacityMasks);
        }
        private static void Emit(string scenario, string metric, long value) =>
            Console.WriteLine($"PERFCOUNTER {scenario} {metric} {value}");

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
            element.ApplyTemplate();
            element.Measure(new Size(640, 120));
            element.Arrange(new Rect(0, 0, 640, 120));
            element.UpdateLayout();
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
            try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
            finally { Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5))); }
        }
    }
}
