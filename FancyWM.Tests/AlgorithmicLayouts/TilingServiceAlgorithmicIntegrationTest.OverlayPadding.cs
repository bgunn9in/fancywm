#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using FancyWM.Controls;
using FancyWM.Layouts.Tiling;
using FancyWM.Models;
using FancyWM.Tests.TestUtilities;
using FancyWM.Tests.Utilities;
using FancyWM.ThemeEngine.Wpf;
using FancyWM.Utilities;
using FancyWM.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModernWpf;
using ModernWpf.Controls;
using Moq;
using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    public partial class TilingServiceAlgorithmicIntegrationTest
    {
        private const string PaddingChildFlag = "FANCYWM_OVERLAY_PADDING_CHILD";

        [TestMethod]
        public Task OverlayPaddingPreservesMaterializedOwners() => RunPaddingOwned(
            nameof(OverlayPaddingPreservesMaterializedOwners), () =>
        {
            foreach (int count in new[] { 1, 10, 50 })
            {
                using var fixture = new PaddingFixture(count);
                var models = fixture.Renderer.Models.Values.ToArray();
                var visuals = PaddingVisuals(fixture.Control).ToArray();
                int subscriptions = fixture.SubscriptionAdds;
                foreach (int padding in new[] { 12, 4, 12, 12 })
                {
                    fixture.Apply(padding);
                    fixture.AssertGeometry();
                    CollectionAssert.AreEqual(models, fixture.Renderer.Models.Values.ToArray(),
                        "A completed padding-only pass must preserve model ownership.");
                    CollectionAssert.AreEqual(visuals, PaddingVisuals(fixture.Control).ToArray(),
                        "Padding must retain the materialized WPF tree, including windows, tabs and SVGs.");
                    Assert.AreEqual(subscriptions, fixture.SubscriptionAdds);
                }
            }
        });

        [TestMethod]
        public Task OverlayPaddingMaterializationCounterScenario() => RunPaddingOwned(
            nameof(OverlayPaddingMaterializationCounterScenario), () =>
        {
            Assert.AreEqual("0", Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"));
            foreach (int count in new[] { 1, 10, 50 })
            {
                using var fixture = new PaddingFixture(count);
                for (int i = 0; i < 6; i++) fixture.Apply(i % 2 == 0 ? 12 : 4);
                long bytes = 0, ticks = 0;
                int modelsCreated = 0, windowsCreated = 0, svgCreated = 0, tabsCreated = 0;
                int beforeAdds = fixture.SubscriptionAdds;
                for (int i = 0; i < 12; i++)
                {
                    var oldModels = fixture.Renderer.Models.Values.ToHashSet();
                    var oldVisuals = PaddingVisuals(fixture.Control).ToHashSet();
                    long startBytes = GC.GetAllocatedBytesForCurrentThread();
                    long startTicks = Stopwatch.GetTimestamp();
                    fixture.Apply(i % 2 == 0 ? 12 : 4);
                    ticks += Stopwatch.GetTimestamp() - startTicks;
                    bytes += GC.GetAllocatedBytesForCurrentThread() - startBytes;
                    fixture.AssertGeometry();
                    modelsCreated += fixture.Renderer.Models.Values.Count(model => !oldModels.Contains(model));
                    var created = PaddingVisuals(fixture.Control).Where(visual => !oldVisuals.Contains(visual)).ToArray();
                    windowsCreated += created.OfType<TilingWindow>().Count();
                    svgCreated += created.OfType<SvgIcon>().Count();
                    tabsCreated += created.OfType<TilingNodeTab>().Count();
                }
                foreach (var counter in new (string Name, long Value)[]
                {
                    ("iterations", 12), ("warmups", 6), ("allocated-bytes", bytes), ("elapsed-ticks", ticks),
                    ("timestamp-frequency", Stopwatch.Frequency), ("models-created", modelsCreated),
                    ("windows-created", windowsCreated), ("svg-created", svgCreated), ("tabs-created", tabsCreated),
                    ("subscription-adds", fixture.SubscriptionAdds - beforeAdds),
                    ("settled-models", fixture.Renderer.Models.Count),
                    ("settled-windows", PaddingVisuals(fixture.Control).OfType<TilingWindow>().Count()),
                }) Console.WriteLine($"PERFCOUNTER overlay-padding-{count} {counter.Name} {counter.Value}");
            }
        });

        [TestMethod]
        public Task OverlayPaddingReentrantPassesCancelAndRetry() => RunPaddingOwned(
            nameof(OverlayPaddingReentrantPassesCancelAndRetry), () =>
        {
            foreach (string boundary in new[] { "add", "remove", "persist" })
            {
                // Exercise managed admission independently of WPF's ItemsControl
                // response to nested collection notifications. The baseline dev3
                // visual case retains extra containers and is recorded separately.
                using var fixture = new PaddingFixture(10, materialize: false);
                var snapshot = fixture.Snapshot;
                if (boundary == "add") fixture.Renderer.Renderer.UpdateOverlay(snapshot.Take(2).ToArray(), []);
                var oldModels = fixture.Renderer.Models.Values.ToArray();
                bool called = false;
                void Interrupt()
                {
                    if (called) return;
                    called = true;
                    fixture.PublishPadding(12);
                }
                NotifyCollectionChangedEventHandler changed = (_, args) =>
                {
                    if (args.Action == (boundary == "add" ? NotifyCollectionChangedAction.Add : NotifyCollectionChangedAction.Remove))
                        Interrupt();
                };
                fixture.Renderer.ViewModel.WindowElements.CollectionChanged += changed;
                if (boundary == "persist")
                {
                    var first = fixture.Renderer.ViewModel.WindowElements.First();
                    first.Title = "force a changed notification";
                    first.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(first.Title)) Interrupt(); };
                }
                try
                {
                    fixture.Renderer.Renderer.UpdateOverlay(boundary == "remove" ? [] : snapshot, []);
                    Assert.IsTrue(called, boundary);
                    Assert.AreEqual(0, fixture.Renderer.Models.Count, boundary);
                    Assert.AreEqual(0, fixture.Renderer.PreviousSnapshot.Count, boundary);
                    Assert.IsTrue(oldModels.OfType<TilingWindowViewModel>().All(model => model.Node == null),
                        $"{boundary}: obsolete windows must release their node and subscriptions.");
                }
                finally { fixture.Renderer.ViewModel.WindowElements.CollectionChanged -= changed; }
                fixture.Refresh();
                fixture.AssertGeometry();
            }
        });

        [TestMethod]
        public Task OverlayPaddingFailureHeightAndDisposalKeepFullRecovery() => RunPaddingOwned(
            nameof(OverlayPaddingFailureHeightAndDisposalKeepFullRecovery), () =>
        {
            using var fixture = new PaddingFixture(10);
            var first = fixture.Renderer.ViewModel.WindowElements.First();
            var failure = new InvalidOperationException("controlled update failure");
            first.Title = "force failed notification";
            System.ComponentModel.PropertyChangedEventHandler changed = (_, args) =>
            {
                if (args.PropertyName == nameof(first.Title)) throw failure;
            };
            first.PropertyChanged += changed;
            try
            {
                Assert.AreSame(failure, Assert.ThrowsException<InvalidOperationException>(
                    () => fixture.Renderer.Renderer.UpdateOverlay(fixture.Snapshot, [])));
            }
            finally { first.PropertyChanged -= changed; }
            fixture.Apply(12);
            fixture.AssertGeometry();
            Assert.IsNull(first.Node, "A failed pass must force full recovery before reuse.");
            var previous = fixture.Renderer.Models.Values.ToArray();
            fixture.Settings = fixture.Settings with { PanelHeight = 30 };
            fixture.Apply(4);
            fixture.AssertGeometry();
            Assert.IsTrue(previous.All(PaddingModelReleased), "Height retains the full invalidation path.");
            previous = fixture.Renderer.Models.Values.ToArray();
            fixture.FailNextScalingRead();
            fixture.PublishPadding(12);
            fixture.Apply(12);
            fixture.AssertGeometry();
            Assert.IsTrue(previous.All(PaddingModelReleased), "Retry after failed settings propagation must invalidate fully.");
            previous = fixture.Renderer.Models.Values.ToArray();
            fixture.RaiseScaling();
            fixture.Refresh();
            fixture.AssertGeometry();
            Assert.IsTrue(previous.All(PaddingModelReleased), "Scaling must retain full invalidation.");
            fixture.Renderer.DisposeManagedOwner();
            fixture.PublishPadding(12);
            fixture.Renderer.Renderer.UpdateOverlay(fixture.Snapshot, []);
            Assert.AreEqual(0, fixture.Renderer.Models.Count);
        });

        private Task RunPaddingOwned(string method, Action action)
        {
            if (Environment.GetEnvironmentVariable(PaddingChildFlag) != "1")
                return IsolatedTestProcess.RunAsync(TestContext, GetType(),
                    $"{typeof(TilingServiceAlgorithmicIntegrationTest).FullName}.{method}", PaddingChildFlag, "1", "overlay-padding-child",
                    new Dictionary<string, string> { ["DOTNET_TieredCompilation"] = "0" });
            return RunPaddingSta(() =>
            {
                Assert.IsTrue(Environment.Is64BitProcess);
                Assert.IsNull(Application.Current);
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var flags = BindingFlags.Static | BindingFlags.NonPublic;
                var stopped = typeof(WindowExtensions).GetField("m_iconDiscoveryStopped", flags)!;
                var owner = typeof(WindowExtensions).GetField("m_iconOwner", flags)!;
                var cssField = typeof(CssManager).GetField("_current", flags)!;
                var previousCss = cssField.GetValue(null);
                var previousStopped = stopped.GetValue(null);
                Assert.IsNull(owner.GetValue(null));
                try
                {
                    // Exclude native icon I/O explicitly; keep real node bindings and subscriptions.
                    stopped.SetValue(null, true);
                    var theme = new ThemeResources { CanBeAccessedAcrossThreads = true, RequestedTheme = ApplicationTheme.Dark, AccentColor = Colors.CornflowerBlue };
                    theme.BeginInit();
                    app.Resources.MergedDictionaries.Add(theme);
                    theme.EndInit();
                    app.Resources.MergedDictionaries.Add(new XamlControlsResources());
                    foreach (string name in new[] { "Rounded", "Generic" })
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/FancyWM;component/Themes/Fluent/{name}.xaml", UriKind.Relative) });
                    app.Resources["MicaPrimaryColor"] = Colors.Black;
                    app.Resources["MicaOpacity"] = 0.8;
                    var css = ThemeEngineManager.GetDefaultCss(_ => Colors.CornflowerBlue, true, true);
                    cssField.SetValue(null, new Dictionary<string, CssValue>(new CssToWpfResourceConverter().Convert(ThemeEngineManager.HtmlTemplate, css)));
                    action();
                    Assert.AreEqual(0, app.Windows.Count);
                    Assert.IsNull(owner.GetValue(null));
                }
                finally
                {
                    cssField.SetValue(null, previousCss);
                    stopped.SetValue(null, previousStopped);
                    app.Shutdown();
                }
            });
        }

        private static bool PaddingModelReleased(TilingNodeViewModel model) => model.PrimaryActionCommand == null
            && (model is not TilingWindowViewModel || model.Node == null);

        private static async Task RunPaddingSta(Action action)
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
            try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(35)); }
            finally { Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5))); }
        }

        private static List<DependencyObject> PaddingVisuals(DependencyObject root)
        {
            var result = new List<DependencyObject> { root };
            for (int i = 0; i < result.Count; i++)
                for (int child = 0, count = VisualTreeHelper.GetChildrenCount(result[i]); child < count; child++)
                    result.Add(VisualTreeHelper.GetChild(result[i], child));
            return result;
        }

        private sealed class PaddingFixture : IDisposable
        {
            private readonly ServiceFixture m_service;
            private readonly DesktopTree m_tree;
            private readonly IWindow[] m_windows;
            private readonly int m_initialWindowSubscriptions;
            private readonly TilingOverlay? m_control;
            private static readonly FieldInfo GuiField = typeof(TilingService).GetField("m_gui", BindingFlags.Instance | BindingFlags.NonPublic)!;
            public OverlayUpdateTest.RendererFixture Renderer { get; } = new();
            public TilingOverlay Control => m_control!;
            public Settings Settings { get; set; }
            public TilingNode[] Snapshot => m_tree.Root!.Nodes.ToArray();
            public int SubscriptionAdds => m_windows.Sum(window => Mock.Get(window).Invocations.Count(invocation => invocation.Method.Name.StartsWith("add_", StringComparison.Ordinal)))
                + Mock.Get(m_windows[0].Workspace).Invocations.Count(invocation => invocation.Method.Name == "add_CursorLocationChanged")
                + Renderer.CursorAdds;
            private int WindowSubscriptionBalance => m_windows.Sum(window => Mock.Get(window).Invocations.Sum(invocation =>
                invocation.Method.Name.StartsWith("add_", StringComparison.Ordinal) ? 1
                : invocation.Method.Name.StartsWith("remove_", StringComparison.Ordinal) ? -1 : 0));

            public PaddingFixture(int count, bool materialize = true, Serilog.ILogger? logger = null)
            {
                Settings = EnabledSettings(false) with { WindowPadding = 4, PanelHeight = 22 };
                m_service = new ServiceFixture(Settings, logger: logger);
                m_service.AddWindow("padding-0");
                m_service.DrainDispatcher();
                m_service.HoldLayoutForSettingsObservation();
                m_tree = GetBackend(m_service).GetTree(m_service.Desktop)!;
                // One registered fake window creates the actual service tree. Add
                // the remaining layout nodes directly while the pipeline is held;
                // this excludes repeated discovery/placement from fixture setup.
                for (int i = 1; i < count; i++) m_tree.Root!.Attach(Renderer.CreateWindow(i));
                m_windows = Snapshot.OfType<WindowNode>().Select(node => node.WindowReference).ToArray();
                m_initialWindowSubscriptions = WindowSubscriptionBalance;
                GuiField.SetValue(m_service.Service, Renderer.Renderer);
                Renderer.Renderer.PanelPadding = m_service.Overlay.PanelPadding;
                Renderer.Renderer.PanelSpacing = m_service.Overlay.PanelSpacing;
                Renderer.ApplySettings(Settings);
                Renderer.ViewModel.DisplayScaling = 1;
                Renderer.ViewModel.FontSize = 12;
                Renderer.ViewModel.IconSize = 16;
                Renderer.Renderer.PreviewWindows = m_windows.Take(1).ToHashSet();
                if (materialize) m_control = new TilingOverlay { ViewModel = Renderer.ViewModel };
                Refresh();
                AssertGeometry();
            }

            public void PublishPadding(int padding)
            {
                Settings = Settings with { WindowPadding = padding };
                Renderer.ApplySettings(Settings);
                m_service.PublishWithoutDispatch(Settings);
            }
            public void Apply(int padding) { PublishPadding(padding); Refresh(); }
            public void FailNextScalingRead()
            {
                bool fail = true;
                Mock.Get(m_service.Display).SetupGet(display => display.Scaling).Returns(() =>
                {
                    if (fail) { fail = false; throw new InvalidOperationException("controlled scaling failure"); }
                    return 1d;
                });
            }
            public void RaiseScaling() => typeof(TilingService)
                .GetMethod("OnDisplayScalingChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(m_service.Service, new object[] { m_service.Display, new DisplayScalingChangedEventArgs(m_service.Display, 1, 1) });
            public void Refresh()
            {
                m_tree.Measure();
                m_tree.Arrange();
                var snapshot = Snapshot;
                var focus = snapshot.OfType<WindowNode>().First();
                Renderer.Renderer.UpdateOverlay(snapshot, [focus, m_tree.Root!]);
                if (m_control == null) return;
                Control.ApplyTemplate();
                Control.Measure(new Size(3440, 1400));
                Control.Arrange(new Rect(0, 0, 3440, 1400));
                Control.UpdateLayout();
            }
            public void AssertGeometry()
            {
                if (m_control != null)
                {
                    Assert.IsNull(Window.GetWindow(Control));
                    Assert.IsNull(PresentationSource.FromVisual(Control));
                    Assert.AreEqual(m_windows.Length, PaddingVisuals(Control).OfType<TilingWindow>().Count());
                }
                Assert.AreEqual(m_windows.Length, Renderer.ViewModel.WindowElements.Count);
                var focus = Snapshot.OfType<WindowNode>().First();
                foreach (var pair in Renderer.Models)
                {
                    var expected = pair.Key.ComputedRectangle;
                    var bounds = Renderer.DisplayBounds;
                    expected = new Rectangle(expected.Left - bounds.Left, expected.Top - bounds.Top, expected.Right - bounds.Left, expected.Bottom - bounds.Top);
                    Assert.AreEqual(expected, pair.Value.ComputedBounds);
                    if (pair.Value is TilingWindowViewModel window)
                    {
                        Assert.AreEqual(pair.Key == focus, window.HasFocus);
                        Assert.AreEqual(m_windows[0] == ((WindowNode)pair.Key).WindowReference, window.IsPreviewVisible);
                        Assert.AreEqual((double)Settings.PanelHeight + 4, window.ActionsHeight);
                        Assert.AreEqual((double)(16 + Settings.PanelHeight + Settings.WindowPadding) * 2, window.RevealHighlightRadius);
                    }
                    if (pair.Value is TilingPanelViewModel panel)
                    {
                        var node = (PanelNode)pair.Key;
                        Assert.AreEqual(Settings.WindowPadding, node.Spacing);
                        Assert.AreEqual(new Rectangle(0, Settings.PanelHeight + Settings.WindowPadding, 0, 0), node.Padding);
                        Assert.AreEqual(Rectangle.OffsetAndSize((int)(expected.Left + Settings.WindowPadding / 2),
                            (int)(expected.Top - node.Padding.Top + Settings.WindowPadding / 2),
                            expected.Width - Settings.WindowPadding, Settings.PanelHeight), panel.HeaderBounds);
                        CollectionAssert.AreEqual(node.Children.Select(child => Renderer.Models[child]).ToArray(), panel.ChildNodes.ToArray());
                    }
                }
            }
            public void Dispose()
            {
                GuiField.SetValue(m_service.Service, m_service.Overlay);
                Renderer.Dispose();
                Assert.AreEqual(m_initialWindowSubscriptions, WindowSubscriptionBalance);
                if (m_control != null)
                {
                    Control.Content = null;
                    Control.DataContext = null;
                }
                m_service.Dispose();
            }
        }
    }
}
