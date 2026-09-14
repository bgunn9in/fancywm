#nullable enable
using System;
using System.Collections.Specialized;
using System.Linq;
using FancyWM.Layouts.Tiling;
using FancyWM.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class OverlayChildOrderTest
    {
        [TestMethod]
        public void ChildOrderKeepsSurvivorsAcrossReorderReplacementRemovalAndAddition()
        {
            using var fixture = new OverlayUpdateTest.RendererFixture();
            var root = new SplitPanelNode();
            var tree = new DesktopTree { Root = root, WorkArea = fixture.DisplayBounds };
            var windows = Enumerable.Range(0, 4).Select(fixture.CreateWindow).ToArray();
            foreach (var window in windows.Take(3)) root.Attach(window);
            Refresh(fixture, tree);
            var panel = (TilingPanelViewModel)fixture.Models[root];
            var models = windows.Take(3).Select(fixture.WindowModel).ToArray();
            int changes = 0;
            panel.ChildNodes.CollectionChanged += (_, args) =>
            {
                changes++;
                Assert.AreNotEqual(NotifyCollectionChangedAction.Reset, args.Action);
            };
            root.Move(0, 2);
            Refresh(fixture, tree);
            CollectionAssert.AreEqual(new[] { models[1], models[2], models[0] }, panel.ChildNodes.ToArray());
            int afterMove = changes;
            Refresh(fixture, tree);
            Assert.AreEqual(afterMove, changes, "An unchanged order must not notify.");
            root.Detach(windows[2]);
            root.Attach(1, windows[3]);
            Refresh(fixture, tree);
            CollectionAssert.AreEqual(new[] { models[1], fixture.WindowModel(windows[3]), models[0] }, panel.ChildNodes.ToArray());
            Assert.IsNull(models[2].Node);
            root.Detach(windows[1]);
            Refresh(fixture, tree);
            CollectionAssert.AreEqual(new[] { fixture.WindowModel(windows[3]), models[0] }, panel.ChildNodes.ToArray());
            root.Attach(0, windows[2]);
            Refresh(fixture, tree);
            Assert.AreNotSame(models[2], fixture.WindowModel(windows[2]));
            CollectionAssert.AreEqual(new[] { fixture.WindowModel(windows[2]), fixture.WindowModel(windows[3]), models[0] }, panel.ChildNodes.ToArray());
        }

        [DataTestMethod]
        [DataRow("move", false)]
        [DataRow("insert", false)]
        [DataRow("remove", false)]
        [DataRow("move", true)]
        [DataRow("insert", true)]
        [DataRow("remove", true)]
        public void ChildNotificationInvalidationOrFailureCancelsAndRecovers(string change, bool throwFromListener)
        {
            using var fixture = new OverlayUpdateTest.RendererFixture();
            var root = new SplitPanelNode();
            var tree = new DesktopTree { Root = root, WorkArea = fixture.DisplayBounds };
            var windows = Enumerable.Range(0, 3).Select(fixture.CreateWindow).ToArray();
            root.Attach(windows[0]);
            root.Attach(windows[1]);
            Refresh(fixture, tree);
            var previousModels = fixture.Models.Values.ToArray();
            var panel = (TilingPanelViewModel)fixture.Models[root];
            var action = change == "move" ? NotifyCollectionChangedAction.Move
                : change == "insert" ? NotifyCollectionChangedAction.Add : NotifyCollectionChangedAction.Remove;
            var failure = new InvalidOperationException("controlled tab collection listener failure");
            int listeners = 0;
            panel.ChildNodes.CollectionChanged += (_, args) =>
            {
                Assert.AreEqual(action, args.Action);
                listeners++;
                if (throwFromListener) throw failure;
                fixture.Renderer.InvalidateView();
            };
            panel.ChildNodes.CollectionChanged += (_, _) =>
            {
                listeners++;
                Assert.IsTrue(fixture.Models.Count > 0,
                    "Full invalidation must wait until all collection listeners have finished.");
            };
            if (change == "move") root.Move(1, 0);
            else if (change == "insert") root.Attach(0, windows[2]);
            else root.Detach(windows[1]);
            if (throwFromListener)
                Assert.AreSame(failure, Assert.ThrowsException<InvalidOperationException>(() => Refresh(fixture, tree)));
            else
            {
                Refresh(fixture, tree);
                Assert.AreEqual(0, fixture.Models.Count);
                Assert.AreEqual(0, fixture.ViewModel.PanelElements.Count);
                Assert.AreEqual(0, fixture.ViewModel.WindowElements.Count);
            }
            Assert.AreEqual(throwFromListener ? 1 : 2, listeners);
            Refresh(fixture, tree);
            var recovered = (TilingPanelViewModel)fixture.Models[root];
            Assert.AreNotSame(panel, recovered);
            CollectionAssert.AreEqual(root.Children.Select(node => fixture.Models[node]).ToArray(), recovered.ChildNodes.ToArray());
            Assert.IsTrue(previousModels.OfType<TilingWindowViewModel>().All(model => model.Node == null));
            Assert.AreEqual(root.Windows.Count(), fixture.CursorSubscriptions);
        }

        private static void Refresh(OverlayUpdateTest.RendererFixture fixture, DesktopTree tree)
        {
            tree.Measure();
            tree.Arrange();
            fixture.Renderer.UpdateOverlay(tree.Root!.Nodes.ToArray(), []);
        }
    }
}
