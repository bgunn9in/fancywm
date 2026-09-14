#nullable enable
using System;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using FancyWM.AlgorithmicLayouts;
using FancyWM.Controls;
using FancyWM.Layouts.Tiling;
using FancyWM.Models;
using FancyWM.Tests.Utilities;
using FancyWM.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    public partial class TilingServiceAlgorithmicIntegrationTest
    {
        [TestMethod]
        public Task OverlayReorderMaterializationCounterScenario() => RunPaddingOwned(
            nameof(OverlayReorderMaterializationCounterScenario), () =>
        {
            foreach (string mode in new[] { "horizontal", "vertical", "mixed" })
            foreach (var side in new[] { MasterSide.Left, MasterSide.Right })
            {
                using var renderer = new OverlayUpdateTest.RendererFixture();
                var settings = new MasterSatelliteLayoutSettings
                {
                    Enabled = true,
                    UseMixedSatellites = mode == "mixed",
                    DefaultSatelliteOrientation = mode == "horizontal"
                        ? SatelliteLayoutOrientation.Horizontal : SatelliteLayoutOrientation.Vertical,
                    DefaultMasterSide = side,
                };
                var engine = new MasterSatelliteLayoutEngine();
                var state = engine.CreateState(settings);
                var tree = new DesktopTree { WorkArea = renderer.DisplayBounds };
                var windows = Enumerable.Range(0, 4).Select(i => renderer.CreateWindow(i).WindowReference).ToArray();
                Assert.IsTrue(engine.BuildLayout(tree, state, settings, windows).Succeeded);
                var focused = tree.FindNode(windows[1])!;
                renderer.ViewModel.DisplayScaling = 1;
                renderer.ViewModel.FontSize = 12;
                renderer.ViewModel.IconSize = 16;
                renderer.ViewModel.TabWidth = 175;
                renderer.Renderer.PreviewWindows = windows.Skip(1).ToHashSet();
                var control = new TilingOverlay { ViewModel = renderer.ViewModel };
                void Refresh()
                {
                    renderer.Renderer.UpdateOverlay(tree.Root!.Nodes.ToArray(), focused.PathToRoot.ToArray());
                    control.ApplyTemplate();
                    control.Measure(new Size(2000, 1200));
                    control.Arrange(new Rect(0, 0, 2000, 1200));
                    control.UpdateLayout();
                }
                void Change(int step)
                {
                    var result = (step % 4) switch
                    {
                        0 => engine.ReorderSatellite(tree, state, settings, 0, 1),
                        1 => engine.ReorderSatellite(tree, state, settings, 1, 2),
                        2 => engine.SwapMasterSide(tree, state, settings),
                        _ => engine.PromoteToMaster(tree, state, settings, state.Satellites[0]),
                    };
                    Assert.IsTrue(result.Succeeded, result.ToString());
                    Assert.IsTrue(engine.ValidateInvariant(tree, state, settings).IsValid);
                }
                Refresh();
                for (int i = 0; i < 4; i++) { Change(i); Refresh(); }
                var models = renderer.Models.Values.ToArray();
                int subscriptions = renderer.CursorAdds;
                int resets = 0, adds = 0, removes = 0, moves = 0, replaces = 0;
                foreach (var panel in renderer.ViewModel.PanelElements)
                    panel.ChildNodes.CollectionChanged += (_, args) =>
                    {
                        switch (args.Action)
                        {
                            case NotifyCollectionChangedAction.Reset: resets++; break;
                            case NotifyCollectionChangedAction.Add: adds += args.NewItems!.Count; break;
                            case NotifyCollectionChangedAction.Remove: removes += args.OldItems!.Count; break;
                            case NotifyCollectionChangedAction.Move: moves++; break;
                            case NotifyCollectionChangedAction.Replace: replaces++; break;
                        }
                    };
                int createdTabs = 0;
                for (int i = 0; i < 12; i++)
                {
                    var oldTabs = PaddingVisuals(control).OfType<TilingNodeTab>().ToHashSet();
                    Change(i);
                    Refresh();
                    createdTabs += PaddingVisuals(control).OfType<TilingNodeTab>().Count(tab => !oldTabs.Contains(tab));
                    CollectionAssert.AreEquivalent(models, renderer.Models.Values.ToArray());
                    Assert.AreEqual(subscriptions, renderer.CursorAdds);
                    Assert.AreSame(focused, tree.FindNode(windows[1]));
                    foreach (var pair in renderer.Models)
                    {
                        var bounds = pair.Key.ComputedRectangle;
                        Assert.AreEqual(new Rectangle(bounds.Left - renderer.DisplayBounds.Left,
                            bounds.Top - renderer.DisplayBounds.Top, bounds.Right - renderer.DisplayBounds.Left,
                            bounds.Bottom - renderer.DisplayBounds.Top), pair.Value.ComputedBounds);
                        if (pair.Value is TilingPanelViewModel panel)
                            CollectionAssert.AreEqual(((PanelNode)pair.Key).Children.Select(n => renderer.Models[n]).ToArray(), panel.ChildNodes.ToArray());
                        if (pair.Value is TilingWindowViewModel window)
                        {
                            Assert.AreEqual(pair.Key == focused, window.HasFocus);
                            Assert.AreEqual(windows.Skip(1).Contains(((WindowNode)pair.Key).WindowReference), window.IsPreviewVisible);
                        }
                    }
                    Assert.AreEqual(tree.Root!.Nodes.Count() - 1, PaddingVisuals(control).OfType<TilingNodeTab>().Count());
                    foreach (var panelControl in PaddingVisuals(control).OfType<TilingPanel>())
                    {
                        var panel = (TilingPanelViewModel)panelControl.DataContext;
                        CollectionAssert.AreEqual(panel.ChildNodes.ToArray(),
                            PaddingVisuals(panelControl).OfType<TilingNodeTab>().Select(tab => tab.DataContext).ToArray());
                    }
                    Assert.IsNull(Window.GetWindow(control));
                    Assert.IsNull(PresentationSource.FromVisual(control));
                }
                Console.WriteLine($"PERFCOUNTER overlay-reorder-{mode}-{side} updates=12 resets={resets} adds={adds} removes={removes} moves={moves} replaces={replaces} tabs-created={createdTabs}");
                control.Content = null;
                control.DataContext = null;
            }
        });
    }
}
