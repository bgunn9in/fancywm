#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using FancyWM.Controls;
using FancyWM.Tests.TestUtilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShapePath = System.Windows.Shapes.Path;

namespace FancyWM.Tests.Utilities
{
    // Characterizes the actual compiled SvgIcon without an Application or HWND.
    // The reference markup below is the frozen C9 markup rendered through the same WPF
    // backend, not a production-path allocation model. Its construction is never timed.
    [TestClass]
    public class SvgIconMaterializationTest
    {
        private static readonly string[] Names = ["hsplit", "vsplit", "stack", "pull-up", "float"];
        private const string ChildFlag = "FANCYWM_PERF_SVG_HEADLESS_CHILD";
        private const string ChildValue = "isolated-headless-svg";
        private const string RenderSwitch = "Switch.System.Windows.Media.ShouldRenderEvenWhenNoDisplayDevicesAreAvailable";

        public TestContext TestContext { get; set; } = null!;

        [TestMethod]
        public async Task SvgIconPixelAndOwnershipScenario() => await RunOwnedAsync(nameof(SvgIconPixelAndOwnershipScenario), () =>
        {
            int cases = 0;
            foreach (int dpi in new[] { 96, 144, 192 })
                foreach (string name in Names)
                {
                    MutableIconAndColorMatchFrozenC9Markup(name, dpi);
                    Console.WriteLine($"SVG_CASE pixel-and-color-{name}-{dpi} passed");
                    cases++;
                }
            ExternalDataContextKeepsExistingIconAndColorBindingSemantics();
            Console.WriteLine("SVG_CASE external-data-context passed");
            RepeatedIconChangesDoNotReparentAnotherOwnersPath();
            Console.WriteLine("SVG_CASE repeated-two-owner-paths-100-cycles passed");
            Assert.AreEqual(15, cases);
        });

        [TestMethod]
        public async Task SvgIconMutableGeometryScenario() => await RunOwnedAsync(nameof(SvgIconMutableGeometryScenario), async () =>
        {
            byte[][]? firstPixels = null;
            int firstThread = 0;
            await RunOnSta(() =>
            {
                AssertRasterCalibration();
                firstThread = Environment.CurrentManagedThreadId;
                var first = new SvgIcon { Color = Brushes.Crimson };
                firstPixels = Names.Select(name => { first.Icon = name; return Pixels(first, 144); }).ToArray();
                AssertMutableGeometryIsolation();
            });
            // RunOnSta joins the first owner thread after dispatcher shutdown.
            // Static geometry data must not retain that dispatcher or its objects.
            await RunOnSta(() =>
            {
                Assert.AreNotEqual(firstThread, Environment.CurrentManagedThreadId);
                AssertRasterCalibration();
                var second = new SvgIcon { Color = Brushes.Crimson };
                for (int index = 0; index < Names.Length; index++)
                {
                    second.Icon = Names[index];
                    CollectionAssert.AreEqual(firstPixels![index], Pixels(second, 144));
                }
                AssertMutableGeometryIsolation();
                AssertNoWindow(second);
            });
            Console.WriteLine("SVG_CASE mutable-geometry-figures-segments-independent-owners-and-split-variants passed");
            Console.WriteLine("SVG_CASE geometry-clone-current-value-and-data-replacement-persist-100-switches passed");
            Console.WriteLine("SVG_CASE second-STA-creation-after-first-owner-exits passed");
        });

        [TestMethod]
        public async Task SvgIconWarmConstructionAllocationScenario() => await RunOwnedAsync(nameof(SvgIconWarmConstructionAllocationScenario), () =>
        {
            Assert.AreEqual("0", Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"));
            for (int warmup = 0; warmup < 20; warmup++)
                Materialize(new SvgIcon { Icon = Names[warmup % Names.Length] });
            const int count = 50;
            var controls = new SvgIcon[count];
            long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < count; index++) controls[index] = new SvgIcon { Icon = Names[index % Names.Length] };
            long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
            Console.WriteLine($"SVG_ALLOCATION constructor-bytes {allocatedBytes} controls {count} warmup 20 tiering 0");
            // C10 measured 4,837,080 bytes in each of five isolated processes.
            // Require at least 10% less allocation; initialization is warmed above.
            Assert.IsTrue(allocatedBytes <= 4_350_000,
                $"Fifty warmed real SvgIcon constructors allocated {allocatedBytes} bytes; expected at most 4,350,000.");
            for (int index = 0; index < count; index++)
            {
                Assert.AreEqual(Names[index % Names.Length], controls[index].Icon);
                Assert.AreEqual(5, Setters(controls[index]).Length);
                AssertNoWindow(controls[index]);
            }
            GC.KeepAlive(controls);
        });

        private static void AssertMutableGeometryIsolation()
        {
            var first = new SvgIcon();
            var second = new SvgIcon();
            var reference = Reference(first);
            var firstPaths = Setters(first).Select(setter => (ShapePath)setter.Value).ToArray();
            var secondPaths = Setters(second).Select(setter => (ShapePath)setter.Value).ToArray();
            var referencePaths = ((ContentControl)reference.Content).Style.Triggers.OfType<DataTrigger>()
                .SelectMany(trigger => trigger.Setters.OfType<Setter>()).Select(setter => (ShapePath)setter.Value).ToArray();
            for (int index = 0; index < Names.Length; index++)
            {
                var geometry = (PathGeometry)firstPaths[index].Data;
                Assert.AreEqual(GeometryState((PathGeometry)referencePaths[index].Data), GeometryState(geometry));
                AssertIndependentMutableGraphs(geometry, (PathGeometry)secondPaths[index].Data);
                Assert.AreEqual(referencePaths[index].LayoutTransform.Value, firstPaths[index].LayoutTransform.Value);
                Assert.AreEqual(referencePaths[index].RenderTransform.Value, firstPaths[index].RenderTransform.Value);
            }
            var firstSplit = (PathGeometry)firstPaths[0].Data;
            var secondSplit = (PathGeometry)firstPaths[1].Data;
            Assert.AreEqual(GeometryState(firstSplit), GeometryState(secondSplit));
            AssertIndependentMutableGraphs(firstSplit, secondSplit);
            var firstUnchanged = firstPaths.Skip(1).Select(path => GeometryState((PathGeometry)path.Data)).ToArray();
            var otherUnchanged = secondPaths.Select(path => GeometryState((PathGeometry)path.Data)).ToArray();

            firstSplit.FillRule = firstSplit.FillRule == FillRule.EvenOdd ? FillRule.Nonzero : FillRule.EvenOdd;
            firstSplit.Transform = new TranslateTransform(2, 3);
            var figure = firstSplit.Figures[0];
            figure.StartPoint += new Vector(2, 3);
            var segment = figure.Segments[0];
            segment.IsStroked = !segment.IsStroked;
            segment.IsSmoothJoin = !segment.IsSmoothJoin;
            MutateFirstSegmentPoint(segment);
            figure.Segments.Add(new LineSegment(new Point(7, 8), true));
            firstSplit.Figures.Add(new PathFigure(new Point(1, 2), [new LineSegment(new Point(3, 4), true)], false));
            var mutated = GeometryState(firstSplit);
            Assert.AreNotEqual(GeometryState((PathGeometry)referencePaths[0].Data), mutated);

            foreach (var clone in new[] { firstSplit.Clone(), firstSplit.CloneCurrentValue() })
            {
                AssertIndependentMutableGraphs(firstSplit, clone);
                Assert.AreEqual(mutated, GeometryState(clone));
                clone.Figures[0].StartPoint += new Vector(4, 5);
                MutateFirstSegmentPoint(clone.Figures[0].Segments[0]);
                Assert.AreEqual(mutated, GeometryState(firstSplit));
            }
            for (int cycle = 0; cycle < 100; cycle++)
            {
                first.Icon = Names[cycle % Names.Length];
                Materialize(first);
                Assert.AreSame(firstPaths[cycle % Names.Length], ((ContentControl)first.Content).Content);
                Assert.AreSame(firstSplit, firstPaths[0].Data);
                Assert.AreEqual(mutated, GeometryState(firstSplit));
            }
            CollectionAssert.AreEqual(firstUnchanged, firstPaths.Skip(1).Select(path => GeometryState((PathGeometry)path.Data)).ToArray());
            CollectionAssert.AreEqual(otherUnchanged, secondPaths.Select(path => GeometryState((PathGeometry)path.Data)).ToArray());

            var replacement = firstSplit.Clone();
            firstPaths[0].Data = replacement;
            first.Icon = "vsplit";
            Materialize(first);
            first.Icon = "hsplit";
            Materialize(first);
            Assert.AreSame(firstPaths[0], ((ContentControl)first.Content).Content);
            Assert.AreSame(replacement, firstPaths[0].Data);
            Assert.AreEqual(mutated, GeometryState(replacement));
            AssertNoWindow(first);
            AssertNoWindow(second);
            AssertNoWindow(reference);
        }

        private static void AssertIndependentMutableGraphs(PathGeometry first, PathGeometry second)
        {
            Assert.AreNotSame(first, second);
            Assert.IsFalse(first.IsFrozen);
            Assert.IsFalse(second.IsFrozen);
            Assert.AreNotSame(first.Figures, second.Figures);
            Assert.IsFalse(first.Figures.IsFrozen);
            Assert.IsFalse(second.Figures.IsFrozen);
            Assert.AreEqual(first.Figures.Count, second.Figures.Count);
            for (int index = 0; index < first.Figures.Count; index++)
            {
                var firstFigure = first.Figures[index];
                var secondFigure = second.Figures[index];
                Assert.AreNotSame(firstFigure, secondFigure);
                Assert.IsFalse(firstFigure.IsFrozen);
                Assert.IsFalse(secondFigure.IsFrozen);
                Assert.AreNotSame(firstFigure.Segments, secondFigure.Segments);
                Assert.IsFalse(firstFigure.Segments.IsFrozen);
                Assert.IsFalse(secondFigure.Segments.IsFrozen);
                Assert.AreEqual(firstFigure.Segments.Count, secondFigure.Segments.Count);
                for (int segment = 0; segment < firstFigure.Segments.Count; segment++)
                {
                    Assert.AreNotSame(firstFigure.Segments[segment], secondFigure.Segments[segment]);
                    Assert.IsFalse(firstFigure.Segments[segment].IsFrozen);
                    Assert.IsFalse(secondFigure.Segments[segment].IsFrozen);
                }
            }
        }

        private static string GeometryState(PathGeometry geometry) =>
            geometry.ToString(CultureInfo.InvariantCulture) + "|" + geometry.FillRule + "|" + geometry.Transform.Value
            + "|" + string.Join(";", geometry.Figures.Select(figure => $"{figure.IsClosed}/{figure.IsFilled}:"
                + string.Join(",", figure.Segments.Select(segment => $"{segment.GetType().Name}/{segment.IsStroked}/{segment.IsSmoothJoin}"))));

        private static void MutateFirstSegmentPoint(PathSegment segment)
        {
            var delta = new Vector(1, 2);
            switch (segment)
            {
                case LineSegment line: line.Point += delta; break;
                case PolyLineSegment line: line.Points[0] += delta; break;
                case BezierSegment curve: curve.Point1 += delta; break;
                case PolyBezierSegment curve: curve.Points[0] += delta; break;
                case QuadraticBezierSegment curve: curve.Point1 += delta; break;
                case PolyQuadraticBezierSegment curve: curve.Points[0] += delta; break;
                case ArcSegment arc: arc.Point += delta; break;
                default: Assert.Fail("The real glyph's segment type needs a point-mutation assertion: " + segment.GetType()); break;
            }
        }

        private static void MutableIconAndColorMatchFrozenC9Markup(string initialIcon, int dpi)
        {
            var brush = new SolidColorBrush(Colors.Crimson);
            var actual = new SvgIcon { Icon = initialIcon, Color = brush };
            var reference = Reference(actual);
            var other = new SvgIcon { Icon = initialIcon, Color = new SolidColorBrush(Colors.LimeGreen) };
            var otherPixels = Pixels(other, dpi);
            var original = AssertPixels(actual, reference, dpi);
            Assert.IsTrue(original.Any(value => value != 0), "The selected icon must draw actual pixels. " + DescribeVisuals(actual));

            brush.Color = Colors.CornflowerBlue;
            var changed = AssertPixels(actual, reference, dpi);
            Assert.IsFalse(original.SequenceEqual(changed), "Mutating the caller-owned brush must change the rendered color.");
            Assert.AreSame(brush, actual.Color);
            Assert.IsFalse(brush.IsFrozen, "A control must not freeze a brush supplied by its caller.");

            actual.Color = new SolidColorBrush(Colors.Goldenrod);
            foreach (string icon in Names)
            {
                actual.Icon = icon;
                Assert.IsTrue(AssertPixels(actual, reference, dpi).Any(value => value != 0));
                CollectionAssert.AreEqual(otherPixels, Pixels(other, dpi), "A second icon owner must remain unchanged.");
            }
            actual.Icon = null!;
            Assert.IsTrue(AssertPixels(actual, reference, dpi).All(value => value == 0));
            actual.Icon = "unrecognized-icon";
            Assert.IsTrue(AssertPixels(actual, reference, dpi).All(value => value == 0));
            actual.Icon = initialIcon;
            actual.Color = brush;
            CollectionAssert.AreEqual(changed, AssertPixels(actual, reference, dpi));
            AssertNoWindow(actual);
            AssertNoWindow(reference);
            AssertNoWindow(other);
        }

        private static void ExternalDataContextKeepsExistingIconAndColorBindingSemantics()
        {
            var model = new IconModel { Icon = "hsplit", Color = new SolidColorBrush(Colors.Crimson) };
            var actual = new SvgIcon { DataContext = model };
            var reference = Reference(model);
            foreach (string name in Names)
            {
                model.Icon = name;
                AssertPixels(actual, reference, 144);
                model.Color = new SolidColorBrush(Colors.CornflowerBlue);
                AssertPixels(actual, reference, 144);
                actual.Icon = "ignored-control-local-value";
                AssertPixels(actual, reference, 144);
            }
            AssertNoWindow(actual);
        }

        private static void RepeatedIconChangesDoNotReparentAnotherOwnersPath()
        {
            var first = new SvgIcon { Color = Brushes.White };
            var second = new SvgIcon { Color = Brushes.Crimson };
            for (int cycle = 0; cycle < 100; cycle++)
            {
                string icon = Names[cycle % Names.Length];
                first.Icon = icon;
                second.Icon = icon;
                Materialize(first);
                Materialize(second);
                var firstPath = (ShapePath)((ContentControl)first.Content).Content;
                var secondPath = (ShapePath)((ContentControl)second.Content).Content;
                Assert.AreNotSame(firstPath, secondPath, "Only immutable data may be shared, never UIElement ownership.");
                var expected = Pixels(second, 96);
                first.Icon = null!;
                Materialize(first);
                Assert.AreSame(secondPath, ((ContentControl)second.Content).Content);
                CollectionAssert.AreEqual(expected, Pixels(second, 96));
            }
            AssertNoWindow(first);
            AssertNoWindow(second);
        }

        [TestMethod]
        public async Task SvgIconMaterializationCounterScenario() => await RunOwnedAsync(nameof(SvgIconMaterializationCounterScenario), () =>
        {
            var application = Application.Current;
            // Warm type/BAML/template infrastructure without inspecting any Setter.Value.
            for (int warmup = 0; warmup < 20; warmup++)
                Materialize(new SvgIcon { Icon = Names[warmup % Names.Length] });
            foreach (int count in new[] { 1, 10, 25, 50 }) MeasureGroup(count);

            // Separate probe: no layout and no Icon assignment before reading private
            // stored values. This explicitly distinguishes already-materialized objects
            // from objects that the public Setter.Value getter could itself produce.
            var probe = new SvgIcon();
            var setters = Setters(probe);
            FieldInfo? storage = typeof(Setter).GetField("_value", BindingFlags.Instance | BindingFlags.NonPublic);
            Emit("svg-setter-probe", "storage-field-available", storage == null ? 0 : 1);
            Emit("svg-setter-probe", "stored-paths-before-first-getter",
                storage == null ? 0 : setters.Count(setter => storage.GetValue(setter) is ShapePath));
            var values = new object[setters.Length];
            long startBytes = GC.GetAllocatedBytesForCurrentThread();
            long startTicks = Stopwatch.GetTimestamp();
            for (int index = 0; index < setters.Length; index++) values[index] = setters[index].Value;
            long firstTicks = Stopwatch.GetTimestamp() - startTicks;
            long firstBytes = GC.GetAllocatedBytesForCurrentThread() - startBytes;
            Emit("svg-setter-probe", "first-value-ticks", firstTicks);
            Emit("svg-setter-probe", "first-value-bytes", firstBytes);
            var secondValues = new object[setters.Length];
            startBytes = GC.GetAllocatedBytesForCurrentThread();
            startTicks = Stopwatch.GetTimestamp();
            for (int index = 0; index < setters.Length; index++) secondValues[index] = setters[index].Value;
            long secondTicks = Stopwatch.GetTimestamp() - startTicks;
            long secondBytes = GC.GetAllocatedBytesForCurrentThread() - startBytes;
            Emit("svg-setter-probe", "second-value-ticks", secondTicks);
            Emit("svg-setter-probe", "second-value-bytes", secondBytes);
            Assert.AreEqual(5, values.Length, "Current markup baseline has five explicit trigger values.");
            for (int index = 0; index < values.Length; index++) Assert.AreSame(values[index], secondValues[index]);
            Assert.AreSame(application, Application.Current, "This fixture must not create an Application singleton.");
            AssertNoWindow(probe);
            Console.WriteLine("SVG_RUNTIME " + typeof(Setter).Assembly.FullName);
        });

        private static void MeasureGroup(int count)
        {
            var controls = new SvgIcon[count];
            long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            long beforeTicks = Stopwatch.GetTimestamp();
            for (int index = 0; index < count; index++) controls[index] = new SvgIcon { Icon = Names[index % Names.Length] };
            long constructorTicks = Stopwatch.GetTimestamp() - beforeTicks;
            long constructorBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
            beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            beforeTicks = Stopwatch.GetTimestamp();
            foreach (var control in controls) Materialize(control);
            long layoutTicks = Stopwatch.GetTimestamp() - beforeTicks;
            long layoutBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;

            // Inspection and raster output are outside both production timing regions.
            var paths = controls.SelectMany(Setters).Select(setter => (ShapePath)setter.Value).ToArray();
            var geometries = new HashSet<Geometry>(paths.Select(path => path.Data), ReferenceEqualityComparer.Instance);
            var layouts = new HashSet<Transform>(paths.Select(path => path.LayoutTransform), ReferenceEqualityComparer.Instance);
            var renders = new HashSet<Transform>(paths.Select(path => path.RenderTransform), ReferenceEqualityComparer.Instance);
            var images = controls.Select(control => Pixels(control, 96)).ToArray();
            Assert.IsTrue(images.All(pixels => pixels.Any(value => value != 0)), "Every selected glyph must draw actual pixels.");
            var allPixels = images.SelectMany(pixels => pixels).ToArray();
            string scenario = "svg-materialization-" + count;
            Emit(scenario, "constructor-bytes", constructorBytes);
            Emit(scenario, "constructor-ticks", constructorTicks);
            Emit(scenario, "layout-bytes", layoutBytes);
            Emit(scenario, "layout-ticks", layoutTicks);
            Emit(scenario, "timestamp-frequency", Stopwatch.Frequency);
            Emit(scenario, "svg-count", count);
            Emit(scenario, "setter-paths", paths.Length);
            Emit(scenario, "unique-geometries", geometries.Count);
            Emit(scenario, "frozen-geometries", geometries.Count(geometry => geometry.IsFrozen));
            Emit(scenario, "unique-layout-transforms", layouts.Count);
            Emit(scenario, "frozen-layout-transforms", layouts.Count(transform => transform.IsFrozen));
            Emit(scenario, "unique-render-transforms", renders.Count);
            Emit(scenario, "frozen-render-transforms", renders.Count(transform => transform.IsFrozen));
            Console.WriteLine($"PERFCOUNTER {scenario} pixel-digest {Convert.ToHexString(SHA256.HashData(allPixels))}");
            foreach (var control in controls) AssertNoWindow(control);
            GC.KeepAlive(controls);
        }

        private static Setter[] Setters(SvgIcon icon) => ((ContentControl)icon.Content).Style.Triggers
            .OfType<DataTrigger>().SelectMany(trigger => trigger.Setters.OfType<Setter>())
            .Where(setter => setter.Property == ContentControl.ContentProperty).ToArray();

        private static byte[] AssertPixels(FrameworkElement actual, FrameworkElement reference, int dpi)
        {
            var expected = Pixels(reference, dpi);
            var pixels = Pixels(actual, dpi);
            CollectionAssert.AreEqual(expected, pixels, $"Exact offscreen Pbgra32 pixels differ at {dpi} DPI.");
            return pixels;
        }
        private static byte[] Pixels(FrameworkElement element, int dpi)
        {
            Materialize(element);
            int side = 16 * dpi / 96;
            var bitmap = new RenderTargetBitmap(side, side, dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(element);
            var pixels = new byte[side * side * 4];
            bitmap.CopyPixels(pixels, side * 4, 0);
            return pixels;
        }
        private static void Materialize(FrameworkElement element)
        {
            element.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            element.ApplyTemplate();
            element.Measure(new Size(16, 16));
            element.Arrange(new Rect(0, 0, 16, 16));
            element.UpdateLayout();
        }
        private static UserControl Reference(object dataContext)
        {
            var document = XDocument.Parse(ReferenceMarkup);
            document.Root!.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))!.Remove();
            document.Root.Attribute(XName.Get("DataContext", "http://schemas.microsoft.com/expression/blend/2008"))!.Remove();
            var control = (UserControl)XamlReader.Parse(document.ToString());
            control.DataContext = dataContext;
            return control;
        }
        private static void AssertNoWindow(FrameworkElement element)
        {
            Assert.IsNull(Window.GetWindow(element));
            Assert.IsNull(PresentationSource.FromVisual(element));
        }
        private static string DescribeVisuals(DependencyObject root)
        {
            var elements = new List<DependencyObject> { root };
            for (int index = 0; index < elements.Count; index++)
                for (int child = 0; child < VisualTreeHelper.GetChildrenCount(elements[index]); child++)
                    elements.Add(VisualTreeHelper.GetChild(elements[index], child));
            return string.Join(" | ", elements.Select(element => element is FrameworkElement view
                ? $"{view.GetType().Name}: actual={view.ActualWidth},{view.ActualHeight}, desired={view.DesiredSize}, visible={view.IsVisible}, opacity={view.Opacity}, data={view.DataContext?.GetType().Name}, fill={(view as ShapePath)?.Fill}, bounds={(view as ShapePath)?.Data?.Bounds}"
                : element.GetType().Name));
        }
        private static void Emit(string scenario, string metric, long value) => Console.WriteLine($"PERFCOUNTER {scenario} {metric} {value}");

        private Task RunOwnedAsync(string method, Action action) => RunOwnedAsync(method, () => RunOnSta(() =>
        {
            Assert.IsNull(Application.Current);
            AssertRasterCalibration();
            action();
        }));

        private async Task RunOwnedAsync(string method, Func<Task> action)
        {
            if (Environment.GetEnvironmentVariable(ChildFlag) == ChildValue)
            {
                // WPF caches this process-local switch. Set it only in the owned
                // child, before its first MediaContext, never in the normal host.
                // https://github.com/dotnet/wpf/issues/11466
                AppContext.SetSwitch(RenderSwitch, true);
                Console.WriteLine($"SVG_RENDER_CONTEXT {RenderSwitch}=true mode=RenderTargetBitmap-software no-HWND runtime={FileVersionInfo.GetVersionInfo(typeof(RenderTargetBitmap).Assembly.Location).ProductVersion}");
                await action();
                return;
            }
            var application = Application.Current;
            bool hadSwitch = AppContext.TryGetSwitch(RenderSwitch, out bool switchValue);
            await IsolatedTestProcess.RunAsync(TestContext, typeof(SvgIconMaterializationTest),
                typeof(SvgIconMaterializationTest).FullName + "." + method, ChildFlag, ChildValue,
                method == nameof(SvgIconMaterializationCounterScenario) ? "svg-counter-child" : "svg-behavior-child",
                method == nameof(SvgIconWarmConstructionAllocationScenario)
                    ? new Dictionary<string, string> { ["DOTNET_TieredCompilation"] = "0" } : null);
            Assert.AreSame(application, Application.Current);
            Assert.AreEqual(hadSwitch, AppContext.TryGetSwitch(RenderSwitch, out bool currentValue));
            Assert.AreEqual(switchValue, currentValue);
        }

        private static void AssertRasterCalibration()
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen()) context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 16, 16));
            var bitmap = new RenderTargetBitmap(16, 16, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var pixels = new byte[16 * 16 * 4];
            bitmap.CopyPixels(pixels, 16 * 4, 0);
            Assert.IsTrue(pixels.All(value => value == byte.MaxValue), "The owned offscreen renderer must draw an exact opaque white calibration rectangle.");
        }

        private sealed class IconModel : INotifyPropertyChanged
        {
            private string? m_icon;
            private Brush? m_color;
            public string? Icon { get => m_icon; set { m_icon = value; PropertyChanged?.Invoke(this, new(nameof(Icon))); } }
            public Brush? Color { get => m_color; set { m_color = value; PropertyChanged?.Invoke(this, new(nameof(Color))); } }
            public event PropertyChangedEventHandler? PropertyChanged;
        }
        private static async Task RunOnSta(Action action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                var application = Application.Current;
                try
                {
                    action();
                    Assert.AreSame(application, Application.Current, "The SVG fixture must preserve the ambient Application.");
                    completion.SetResult();
                }
                catch (Exception error) { completion.SetException(error); }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
            finally { Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)), "Owned SVG STA did not exit."); }
        }

        // Frozen C9/C10 SvgIcon.xaml source SHA256 (trailing XML whitespace normalized):
        // B413361DBB41E3735278DAEC7977DF3B37846595F807EE4C21612D1E34DA6FC8.
        private const string ReferenceMarkup = """
        <UserControl x:Class="FancyWM.Controls.SvgIcon"
                     xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                     xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
                     xmlns:local="clr-namespace:FancyWM.Controls"
                     mc:Ignorable="d"
                     d:DataContext="{d:DesignInstance Type=local:SvgIcon}"
                     d:DesignHeight="16" d:DesignWidth="16">
            <ContentControl Width="16" Height="16">
                <ContentControl.Style>
                    <Style TargetType="ContentControl">
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding Icon}" Value="hsplit">
                                <Setter Property="Content">
                                    <Setter.Value>
                                        <Path Fill="{Binding Color}">
                                            <Path.LayoutTransform>
                                                <ScaleTransform ScaleX="0.75" ScaleY="0.75" />
                                            </Path.LayoutTransform>
                                            <Path.RenderTransform>
                                                <TransformGroup>
                                                    <TranslateTransform X="-1" Y="-17" />
                                                    <RotateTransform Angle="90" />
                                                </TransformGroup>
                                            </Path.RenderTransform>
                                            <Path.Data>
                                                <PathGeometry Figures="M19 13H5c-1.1 0-2 .9-2 2v4c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2v-4c0-1.1-.9-2-2-2zm0-10H5c-1.1 0-2 .9-2 2v4c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2z" />
                                            </Path.Data>
                                        </Path>
                                    </Setter.Value>
                                </Setter>
                            </DataTrigger>
                            <DataTrigger Binding="{Binding Icon}" Value="vsplit">
                                <Setter Property="Content">
                                    <Setter.Value>
                                        <Path Fill="{Binding Color}">
                                            <Path.LayoutTransform>
                                                <ScaleTransform ScaleX="0.75" ScaleY="0.75" />
                                            </Path.LayoutTransform>
                                            <Path.RenderTransform>
                                                <TranslateTransform Y="-1" />
                                            </Path.RenderTransform>
                                            <Path.Data>
                                                <PathGeometry Figures="M19 13H5c-1.1 0-2 .9-2 2v4c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2v-4c0-1.1-.9-2-2-2zm0-10H5c-1.1 0-2 .9-2 2v4c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2z" />
                                            </Path.Data>
                                        </Path>
                                    </Setter.Value>
                                </Setter>
                            </DataTrigger>
                            <DataTrigger Binding="{Binding Icon}" Value="stack">
                                <Setter Property="Content">
                                    <Setter.Value>
                                        <Path Fill="{Binding Color}">
                                            <Path.LayoutTransform>
                                                <ScaleTransform ScaleX="0.75" ScaleY="0.8" />
                                            </Path.LayoutTransform>
                                            <Path.RenderTransform>
                                                <TransformGroup>
                                                    <TranslateTransform X="-1" Y="-1" />
                                                </TransformGroup>
                                            </Path.RenderTransform>
                                            <Path.Data>
                                                <PathGeometry Figures="M12.6 18.06c-.36.28-.87.28-1.23 0l-6.15-4.78a.991.991 0 0 0-1.22 0c-.51.4-.51 1.17 0 1.57l6.76 5.26c.72.56 1.73.56 2.46 0l6.76-5.26c.51-.4.51-1.17 0-1.57l-.01-.01a.991.991 0 0 0-1.22 0l-6.15 4.79zm.63-3.02 6.76-5.26c.51-.4.51-1.18 0-1.58l-6.76-5.26c-.72-.56-1.73-.56-2.46 0L4.01 8.21c-.51.4-.51 1.18 0 1.58l6.76 5.26c.72.56 1.74.56 2.46-.01z" />
                                            </Path.Data>
                                        </Path>
                                    </Setter.Value>
                                </Setter>
                            </DataTrigger>
                            <DataTrigger Binding="{Binding Icon}" Value="pull-up">
                                <Setter Property="Content">
                                    <Setter.Value>
                                        <Path Fill="{Binding Color}">
                                            <Path.LayoutTransform>
                                                <ScaleTransform ScaleX="0.6667" ScaleY="0.75" />
                                            </Path.LayoutTransform>
                                            <Path.RenderTransform>
                                                <TranslateTransform X="-1" Y="-1" />
                                            </Path.RenderTransform>
                                            <Path.Data>
                                                <PathGeometry Figures="M3.01 13.28c-.14-2.57 1.66-4.73 4.07-5.18l-.79.78c-.39.39-.39 1.02 0 1.41.39.39 1.02.39 1.41 0l2.59-2.59c.39-.39.39-1.02 0-1.41L7.71 3.7a.9959.9959 0 0 0-1.41 0c-.39.39-.39 1.02 0 1.41l.88.88v.06C3.54 6.48.75 9.7 1.03 13.52 1.29 17.22 4.55 20 8.26 20H10c.55 0 1-.45 1-1s-.45-1-1-1H8.22c-2.7 0-5.07-2.04-5.21-4.72zM13 15v3c0 1.1.9 2 2 2h5c1.1 0 2-.9 2-2v-3c0-1.1-.9-2-2-2h-5c-1.1 0-2 .9-2 2zm7 3h-5v-3h5v3zm0-14h-5c-1.1 0-2 .9-2 2v3c0 1.1.9 2 2 2h5c1.1 0 2-.9 2-2V6c0-1.1-.9-2-2-2z" />
                                            </Path.Data>
                                        </Path>
                                    </Setter.Value>
                                </Setter>
                            </DataTrigger>
                            <DataTrigger Binding="{Binding Icon}" Value="float">
                                <Setter Property="Content">
                                    <Setter.Value>
                                        <Path Fill="{Binding Color}">
                                            <Path.LayoutTransform>
                                                <ScaleTransform ScaleX="0.6667" ScaleY="0.75" />
                                            </Path.LayoutTransform>
                                            <Path.RenderTransform>
                                                <TranslateTransform X="0" Y="-1" />
                                            </Path.RenderTransform>
                                            <Path.Data>
                                                <PathGeometry Figures="M18 7h-6c-.55 0-1 .45-1 1v4c0 .55.45 1 1 1h6c.55 0 1-.45 1-1V8c0-.55-.45-1-1-1zm3-4H3c-1.1 0-2 .9-2 2v14c0 1.1.9 1.98 2 1.98h18c1.1 0 2-.88 2-1.98V5c0-1.1-.9-2-2-2zm-1 16.01H4c-.55 0-1-.45-1-1V5.98c0-.55.45-1 1-1h16c.55 0 1 .45 1 1v12.03c0 .55-.45 1-1 1z" />
                                            </Path.Data>
                                        </Path>
                                    </Setter.Value>
                                </Setter>
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </ContentControl.Style>
            </ContentControl>
        </UserControl>
        """;
    }
}
