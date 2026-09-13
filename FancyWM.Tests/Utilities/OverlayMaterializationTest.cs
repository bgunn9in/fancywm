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
using System.Windows.Shapes;
using System.Windows.Threading;

using FancyWM.Controls;
using FancyWM.ThemeEngine.Wpf;
using FancyWM.Utilities;
using FancyWM.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    // Actual no-HWND XAML: the first count includes process-cold WPF initialization.
    // Later counts share framework caches; counts do not establish retained/native heap.
    [TestClass]
    public class OverlayMaterializationTest
    {
        [TestMethod]
        public Task NonHitTestableOverlayMaterializationCounterScenario() => RunOnSta(() =>
        {
            var css = ThemeEngineManager.GetDefaultCss(_ => Colors.CornflowerBlue, true, true);
            var dictionary = new Dictionary<string, CssValue>(
                new CssToWpfResourceConverter().Convert(ThemeEngineManager.HtmlTemplate, css));
            var cssField = typeof(CssManager).GetField("_current", BindingFlags.Static | BindingFlags.NonPublic)!;
            var previous = cssField.GetValue(null);
            try
            {
                // Keep the seed through first layout: item templates materialize
                // their CssResource markup extensions after root construction.
                cssField.SetValue(null, dictionary);
                foreach (int count in new[] { 1, 10, 25, 50 })
                {
                    using var model = new TilingOverlayViewModel
                    {
                        DisplayScaling = 1,
                        FontSize = 12,
                        IconSize = 16,
                    };
                    for (int index = 0; index < count; index++)
                    {
                        model.WindowElements.Add(new TilingWindowViewModel
                        {
                            Overlay = model,
                            ComputedBounds = WinMan.Rectangle.OffsetAndSize(
                                index % 5 * 100, index / 5 * 70, 90, 60),
                            ActionsVisibility = Visibility.Hidden,
                            IsPreviewVisible = false,
                            RevealHighlightOpacity = 0,
                        });
                    }
                    try
                    {
                        long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
                        long beforeTicks = Stopwatch.GetTimestamp();
                        var control = new NonHitTestableTilingOverlay { ViewModel = model };
                        long constructorTicks = Stopwatch.GetTimestamp() - beforeTicks;
                        long constructorBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
                        var before = Visuals(control);
                        var logicalBefore = LogicalElements(control);
                        var declaredLayers = logicalBefore.OfType<ItemsControl>().ToArray();
                        Assert.AreEqual(2, declaredLayers.Length);
                        int materializedBefore = declaredLayers.Sum(layer => Enumerable.Range(0, count)
                            .Count(index => layer.ItemContainerGenerator.ContainerFromIndex(index) != null));
                        Assert.AreEqual(0, materializedBefore,
                            "The declarations exist, but per-window template containers materialize during layout.");
                        beforeBytes = GC.GetAllocatedBytesForCurrentThread();
                        beforeTicks = Stopwatch.GetTimestamp();
                        Materialize(control);
                        long layoutTicks = Stopwatch.GetTimestamp() - beforeTicks;
                        long layoutBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
                        var after = Visuals(control);
                        var logicalAfter = LogicalElements(control);
                        Assert.IsNull(Window.GetWindow(control));
                        Assert.IsNull(PresentationSource.FromVisual(control));
                        Assert.AreEqual(count, model.WindowElements.Count);
                        Assert.IsTrue(model.WindowElements.All(window => window.Node == null));
                        var layers = after.OfType<ItemsControl>().ToArray();
                        Assert.AreEqual(2, layers.Length);
                        Assert.IsTrue(layers.All(layer => ReferenceEquals(layer.ItemsSource, model.WindowElements)));
                        // Verify generated containers and their bound VM identity,
                        // instead of assuming declarations imply eager visuals.
                        foreach (var layer in layers)
                        {
                            Assert.AreEqual(count, layer.Items.Count);
                            for (int index = 0; index < count; index++)
                            {
                                var container = (ContentPresenter)layer.ItemContainerGenerator.ContainerFromIndex(index);
                                Assert.IsNotNull(container);
                                Assert.AreSame(model.WindowElements[index], container.Content);
                                Assert.AreEqual(1, Visuals(container).OfType<Rectangle>().Count());
                            }
                        }
                        Console.WriteLine($"PERFCOUNTER overlay-materialization-{count} logical-before-layout {logicalBefore.Count}");
                        Console.WriteLine($"PERFCOUNTER overlay-materialization-{count} logical-after-layout {logicalAfter.Count}");
                        Console.WriteLine($"PERFCOUNTER overlay-materialization-{count} materialized-items-before-layout {materializedBefore}");
                        Console.WriteLine($"PERFCOUNTER overlay-materialization-{count} constructor-bytes {constructorBytes}");
                        Console.WriteLine($"PERFCOUNTER overlay-materialization-{count} constructor-ticks {constructorTicks}");
                        Console.WriteLine($"PERFCOUNTER overlay-materialization-{count} layout-bytes {layoutBytes}");
                        Console.WriteLine($"PERFCOUNTER overlay-materialization-{count} layout-ticks {layoutTicks}");
                        Console.WriteLine($"PERFCOUNTER overlay-materialization-{count} timestamp-frequency {Stopwatch.Frequency}");
                        Console.WriteLine($"PERFCOUNTER overlay-materialization-{count} visuals-before-layout {before.Count}");
                        Console.WriteLine($"PERFCOUNTER overlay-materialization-{count} visuals-after-layout {after.Count}");
                        Console.WriteLine($"PERFCOUNTER overlay-materialization-{count} rectangles-before-layout {before.OfType<Rectangle>().Count()}");
                        Console.WriteLine($"PERFCOUNTER overlay-materialization-{count} rectangles-after-layout {after.OfType<Rectangle>().Count()}");
                        Console.WriteLine($"PERFCOUNTER overlay-materialization-{count} presenters-after-layout {after.OfType<ContentPresenter>().Count()}");
                        Console.WriteLine($"PERFCOUNTER overlay-materialization-{count} vm-count {count}");
                    }
                    finally
                    {
                        foreach (var window in model.WindowElements) window.Dispose();
                        model.WindowElements.Clear();
                    }
                }
            }
            finally { cssField.SetValue(null, previous); }
        });

        private static List<DependencyObject> Visuals(DependencyObject root)
        {
            var result = new List<DependencyObject> { root };
            for (int index = 0; index < result.Count; index++)
            {
                var current = result[index];
                int count = VisualTreeHelper.GetChildrenCount(current);
                for (int child = 0; child < count; child++) result.Add(VisualTreeHelper.GetChild(current, child));
            }
            return result;
        }
        private static List<DependencyObject> LogicalElements(DependencyObject root)
        {
            var elements = new List<DependencyObject> { root };
            var seen = new HashSet<DependencyObject> { root };
            for (int index = 0; index < elements.Count; index++)
                foreach (var child in LogicalTreeHelper.GetChildren(elements[index]))
                    if (child is DependencyObject dependency && seen.Add(dependency)) elements.Add(dependency);
            return elements;
        }

        private static void Materialize(FrameworkElement element)
        {
            element.ApplyTemplate();
            element.Measure(new Size(640, 720));
            element.Arrange(new Rect(0, 0, 640, 720));
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
