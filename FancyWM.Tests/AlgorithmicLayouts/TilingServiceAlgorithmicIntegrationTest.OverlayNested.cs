#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FancyWM.Controls;
using FancyWM.Layouts.Tiling;
using FancyWM.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    public partial class TilingServiceAlgorithmicIntegrationTest
    {
        [TestMethod]
        public Task OverlayNestedPaddingKeepsCollectionsAndVisualsConsistent() => RunPaddingOwned(
            nameof(OverlayNestedPaddingKeepsCollectionsAndVisualsConsistent), () =>
        {
            var failures = new List<string>();
            foreach (int count in new[] { 1, 10, 50 })
            foreach (bool add in new[] { true, false })
            {
                var logger = new Mock<Serilog.ILogger>();
                using var fixture = new PaddingFixture(count, materialize: false, logger: logger.Object);
                var snapshot = fixture.Snapshot;
                PrepareNestedMutation(fixture, snapshot, panels: false, add);
                using var views = new NestedOverlayViews(fixture.Renderer.ViewModel);
                bool called = false;
                NotifyCollectionChangedEventHandler changed = (_, args) =>
                {
                    if (called || args.Action != (add ? NotifyCollectionChangedAction.Add : NotifyCollectionChangedAction.Remove)) return;
                    called = true;
                    fixture.PublishPadding(12);
                };
                fixture.Renderer.ViewModel.WindowElements.CollectionChanged += changed;
                int remaining;
                try
                {
                    fixture.Renderer.Renderer.UpdateOverlay(NestedTarget(snapshot, panels: false, add), []);
                    remaining = fixture.Renderer.ViewModel.WindowElements.Count;
                }
                finally { fixture.Renderer.ViewModel.WindowElements.CollectionChanged -= changed; }
                fixture.Refresh();
                views.Materialize();
                // A real dispatcher turn must not be mistaken for a repair by
                // the test: observe both before and after the idle barrier.
                int immediate = PaddingVisuals(views.Hit).OfType<TilingWindow>().Count();
                DrainNestedDispatcher();
                views.Materialize();
                int settled = PaddingVisuals(views.Hit).OfType<TilingWindow>().Count();
                var errors = logger.Invocations.Where(call => call.Method.Name == "Error")
                    .SelectMany(call => call.Arguments.OfType<Exception>()).ToArray();
                Console.WriteLine($"NESTED_REPRO windows={count} add={add} remaining={remaining} immediate={immediate} settled={settled} errors={errors.Length}");
                foreach (var error in errors) Console.WriteLine(error);
                try
                {
                    Assert.IsTrue(called);
                    Assert.AreEqual(count, settled, "Recovery must not append fresh models beside disposed collection entries.");
                    Assert.AreEqual(0, remaining, "The interrupted outer pass must finish its complete invalidation.");
                    Assert.AreEqual(0, errors.Length, "Padding propagation must not fail the collection reentrancy guard.");
                    fixture.AssertGeometry();
                    views.AssertOwnedItems(fixture.Renderer.ViewModel);
                }
                catch (AssertFailedException error) { failures.Add($"{count}/{add}: {error.Message}"); }
            }
            Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
        });

        [TestMethod]
        public Task OverlayNestedInvalidationAndDisposeFinishAfterListeners() => RunPaddingOwned(
            nameof(OverlayNestedInvalidationAndDisposeFinishAfterListeners), () =>
        {
            foreach (bool panels in new[] { false, true })
            foreach (bool add in new[] { true, false })
            foreach (bool dispose in new[] { false, true })
            foreach (bool listenerFirst in new[] { false, true })
            {
                using var fixture = new PaddingFixture(3, materialize: false);
                var snapshot = fixture.Snapshot;
                PrepareNestedMutation(fixture, snapshot, panels, add);
                INotifyCollectionChanged collection = panels ? fixture.Renderer.ViewModel.PanelElements : fixture.Renderer.ViewModel.WindowElements;
                bool called = false;
                int callbackDepth = 0, resets = 0, tailNotifications = 0;
                NotifyCollectionChangedEventHandler changed = (_, args) =>
                {
                    if (args.Action == NotifyCollectionChangedAction.Reset)
                    {
                        resets++;
                        Assert.AreEqual(0, callbackDepth, "Reset must follow the suspended collection notification.");
                        Assert.AreEqual(1, tailNotifications, "Every original listener must finish before Reset.");
                        return;
                    }
                    if (called || args.Action != (add ? NotifyCollectionChangedAction.Add : NotifyCollectionChangedAction.Remove)) return;
                    called = true;
                    callbackDepth++;
                    try
                    {
                        if (dispose)
                        {
                            fixture.Renderer.DisposeManagedOwner();
                            fixture.Renderer.DisposeManagedOwner();
                        }
                        else
                        {
                            fixture.Renderer.Renderer.InvalidateView();
                            fixture.Renderer.Renderer.InvalidateView();
                        }
                        int adds = fixture.Renderer.CursorAdds;
                        fixture.Renderer.Renderer.UpdateOverlay(snapshot, []);
                        Assert.AreEqual(adds, fixture.Renderer.CursorAdds, "Pending invalidation rejects nested admission.");
                    }
                    finally { callbackDepth--; }
                };
                NotifyCollectionChangedEventHandler tail = (_, args) =>
                {
                    if (args.Action == (add ? NotifyCollectionChangedAction.Add : NotifyCollectionChangedAction.Remove)) tailNotifications++;
                };
                if (listenerFirst) collection.CollectionChanged += changed;
                using var views = new NestedOverlayViews(fixture.Renderer.ViewModel);
                if (!listenerFirst) collection.CollectionChanged += changed;
                collection.CollectionChanged += tail;
                try
                {
                    fixture.Renderer.Renderer.UpdateOverlay(NestedTarget(snapshot, panels, add), []);
                    Assert.IsTrue(called);
                    Assert.AreEqual(1, resets, "Repeated requests coalesce at the mutation boundary.");
                    Assert.AreEqual(0, fixture.Renderer.Models.Count);
                    Assert.AreEqual(0, fixture.Renderer.PreviousSnapshot.Count);
                    Assert.AreEqual(0, fixture.Renderer.ViewModel.WindowElements.Count);
                    Assert.AreEqual(0, fixture.Renderer.ViewModel.PanelElements.Count);
                    Assert.AreEqual(0, fixture.Renderer.CursorSubscriptions);
                }
                finally
                {
                    collection.CollectionChanged -= changed;
                    collection.CollectionChanged -= tail;
                }
                views.Materialize();
                views.AssertOwnedItems(fixture.Renderer.ViewModel);
                fixture.Refresh();
                views.Materialize();
                if (dispose) Assert.AreEqual(0, fixture.Renderer.Models.Count);
                else fixture.AssertGeometry();
                views.AssertOwnedItems(fixture.Renderer.ViewModel);
                Console.WriteLine($"NESTED_CASE panels={panels} add={add} dispose={dispose} listenerFirst={listenerFirst} PASS");
            }
        });

        [TestMethod]
        public Task OverlayNestedFailurePreservesPrimaryAndRetriesCleanup() => RunPaddingOwned(
            nameof(OverlayNestedFailurePreservesPrimaryAndRetriesCleanup), () =>
        {
            foreach (bool add in new[] { true, false })
            {
                using var fixture = new PaddingFixture(3, materialize: false);
                var snapshot = fixture.Snapshot;
                PrepareNestedMutation(fixture, snapshot, panels: false, add);
                using var views = new NestedOverlayViews(fixture.Renderer.ViewModel);
                var primary = new InvalidOperationException("controlled original notification failure");
                var cleanup = new InvalidOperationException("controlled deferred reset failure");
                bool interrupted = false;
                NotifyCollectionChangedEventHandler changed = (_, args) =>
                {
                    if (args.Action == NotifyCollectionChangedAction.Reset) throw cleanup;
                    if (interrupted || args.Action != (add ? NotifyCollectionChangedAction.Add : NotifyCollectionChangedAction.Remove)) return;
                    interrupted = true;
                    fixture.Renderer.Renderer.InvalidateView();
                    ThrowOverlayNestedPrimary(primary);
                };
                fixture.Renderer.ViewModel.WindowElements.CollectionChanged += changed;
                try
                {
                    var actual = Assert.ThrowsException<InvalidOperationException>(() =>
                        fixture.Renderer.Renderer.UpdateOverlay(NestedTarget(snapshot, panels: false, add), []));
                    Assert.AreSame(primary, actual, "Deferred cleanup must preserve the original notification error.");
                    StringAssert.Contains(actual.StackTrace!, nameof(ThrowOverlayNestedPrimary));
                    var later = (AggregateException)actual.Data["TilingOverlayRenderer.UpdateViewModelsExceptions"]!;
                    Assert.IsNotNull(later);
                    CollectionAssert.AreEqual(new[] { cleanup }, later.InnerExceptions.ToArray());
                    Assert.AreEqual(0, fixture.Renderer.Models.Count);
                    Assert.AreEqual(0, fixture.Renderer.CursorSubscriptions);
                }
                finally { fixture.Renderer.ViewModel.WindowElements.CollectionChanged -= changed; }
                int resets = 0;
                NotifyCollectionChangedEventHandler retry = (_, args) => { if (args.Action == NotifyCollectionChangedAction.Reset) resets++; };
                fixture.Renderer.ViewModel.WindowElements.CollectionChanged += retry;
                try { fixture.Refresh(); }
                finally { fixture.Renderer.ViewModel.WindowElements.CollectionChanged -= retry; }
                Assert.AreEqual(1, resets, "A failed deferred reset must be retried before accepting a fresh snapshot.");
                fixture.AssertGeometry();
                views.Materialize();
                views.AssertOwnedItems(fixture.Renderer.ViewModel);
                var models = fixture.Renderer.Models.Values.ToArray();
                fixture.Refresh();
                CollectionAssert.AreEqual(models, fixture.Renderer.Models.Values.ToArray());
                Console.WriteLine($"NESTED_FAILURE add={add} PASS");
            }
        });

        private static void ThrowOverlayNestedPrimary(Exception error) => throw error;

        private static TilingNode[] NestedTarget(TilingNode[] snapshot, bool panels, bool add) => add ? snapshot
            : snapshot.Where(node => panels ? node is not PanelNode : node is not WindowNode).ToArray();

        private static void PrepareNestedMutation(PaddingFixture fixture, TilingNode[] snapshot, bool panels, bool add)
        {
            if (add) fixture.Renderer.Renderer.UpdateOverlay(NestedTarget(snapshot, panels, add: false), []);
        }

        private static void DrainNestedDispatcher()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }

        private sealed class NestedOverlayViews : IDisposable
        {
            public TilingOverlay Hit { get; }
            private readonly NonHitTestableTilingOverlay m_nonHit;
            public NestedOverlayViews(TilingOverlayViewModel model)
            {
                Hit = new TilingOverlay { ViewModel = model };
                m_nonHit = new NonHitTestableTilingOverlay { ViewModel = model };
                Materialize();
            }
            public void Materialize()
            {
                foreach (var control in new FrameworkElement[] { Hit, m_nonHit })
                {
                    control.ApplyTemplate();
                    control.Measure(new Size(3440, 1400));
                    control.Arrange(new Rect(0, 0, 3440, 1400));
                    control.UpdateLayout();
                    Assert.IsNull(Window.GetWindow(control));
                    Assert.IsNull(PresentationSource.FromVisual(control));
                }
            }
            public void AssertOwnedItems(TilingOverlayViewModel model)
            {
                var windows = PaddingVisuals(Hit).OfType<TilingWindow>().ToArray();
                CollectionAssert.AreEqual(model.WindowElements.ToArray(), windows.Select(window => window.ViewModel).ToArray());
                var panels = PaddingVisuals(Hit).OfType<TilingPanel>().ToArray();
                CollectionAssert.AreEqual(model.PanelElements.ToArray(), panels.Select(panel => panel.ViewModel).ToArray());
                var layers = PaddingVisuals(m_nonHit).OfType<ItemsControl>().ToArray();
                Assert.AreEqual(2, layers.Length);
                foreach (var layer in layers)
                {
                    CollectionAssert.AreEqual(model.WindowElements.ToArray(), layer.Items.Cast<TilingWindowViewModel>().ToArray());
                    for (int i = 0; i < layer.Items.Count; i++)
                    {
                        var container = (ContentPresenter)layer.ItemContainerGenerator.ContainerFromIndex(i);
                        Assert.IsNotNull(container);
                        Assert.AreSame(model.WindowElements[i], container.Content);
                    }
                }
            }
            public void Dispose()
            {
                Hit.Content = null;
                Hit.DataContext = null;
                m_nonHit.Content = null;
                m_nonHit.DataContext = null;
            }
        }
    }
}
