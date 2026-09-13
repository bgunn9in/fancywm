#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using System.Reactive.Disposables;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using FancyWM.Layouts.Tiling;
using FancyWM.Models;
using FancyWM.Utilities;
using FancyWM.ViewModels;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class OverlayUpdateTest
    {
        [TestMethod]
        public void ChangesEnumeratesEachSourceOnceAndKeepsAnIndependentOrderedResult()
        {
            var previousItems = new List<int> { 3, 1, 3, 2, 5 };
            var nextItems = new List<int> { 4, 2, 4, 3, 6 };
            var previous = new CountingCollection<int>(previousItems);
            var next = new CountingCollection<int>(nextItems);
            var (added, removed, persisted) = previous.Changes(next);
            CollectionAssert.AreEqual(new[] { 4, 6 }, added.ToArray());
            CollectionAssert.AreEqual(new[] { 1, 5 }, removed.ToArray());
            CollectionAssert.AreEqual(new[] { 3, 2 }, persisted.ToArray());
            Assert.AreEqual(1, previous.Enumerations);
            Assert.AreEqual(1, next.Enumerations);

            previousItems.Clear();
            nextItems.Clear();
            CollectionAssert.AreEqual(new[] { 4, 6 }, added.ToArray());
            CollectionAssert.AreEqual(new[] { 1, 5 }, removed.ToArray());
            CollectionAssert.AreEqual(new[] { 3, 2 }, persisted.ToArray());
            Assert.AreEqual(1, previous.Enumerations);
            Assert.AreEqual(1, next.Enumerations);
        }

        [TestMethod]
        public void ChangesKeepsFirstSourceRepresentativesWithCustomEquality()
        {
            string[] previous = ["Alpha", "beta", "ALPHA", "gamma"];
            string[] next = ["BETA", "delta", "DELTA", "alpha"];
            var (added, removed, persisted) = previous.Changes(next, StringComparer.OrdinalIgnoreCase);
            CollectionAssert.AreEqual(new[] { "delta" }, added.ToArray());
            CollectionAssert.AreEqual(new[] { "gamma" }, removed.ToArray());
            CollectionAssert.AreEqual(new[] { "Alpha", "beta" }, persisted.ToArray());
            Assert.AreSame(previous[0], persisted.First());
            Assert.AreSame(next[1], added.First());
        }

        [DataTestMethod]
        [DataRow(1)]
        [DataRow(10)]
        [DataRow(25)]
        [DataRow(50)]
        public void StablePreviewOverlapDoesNotNotifyOrRestartTransitions(int count)
        {
            using var fixture = new RendererFixture();
            var nodes = Enumerable.Range(0, count + 2).Select(fixture.CreateWindow).ToArray();
            fixture.Update(nodes, []);
            var first = nodes.Take(count + 1).Select(node => node.WindowReference).ToHashSet();
            fixture.Renderer.PreviewWindows = first;
            var notifications = new List<(WindowNode Node, bool Visible)>();
            foreach (var node in nodes)
            {
                var vm = fixture.WindowModel(node);
                vm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(TilingWindowViewModel.IsPreviewVisible))
                    {
                        notifications.Add((node, vm.IsPreviewVisible));
                    }
                };
            }

            fixture.Renderer.PreviewWindows = new HashSet<IWindow>(first);
            Assert.AreEqual(0, notifications.Count, "An equivalent set must leave all preview transitions running.");
            var second = nodes.Skip(1).Select(node => node.WindowReference).ToHashSet();
            fixture.Renderer.PreviewWindows = second;
            CollectionAssert.AreEqual(new[] { (nodes[0], false), (nodes[^1], true) }, notifications);
            foreach (var node in nodes)
            {
                Assert.AreEqual(second.Contains(node.WindowReference), fixture.WindowModel(node).IsPreviewVisible);
            }
        }

        [TestMethod]
        public void PreviewRemovalsStillPrecedeAdditionsInOriginalModelOrder()
        {
            using var fixture = new RendererFixture();
            var nodes = Enumerable.Range(0, 4).Select(fixture.CreateWindow).ToArray();
            fixture.Update(nodes, []);
            fixture.Renderer.PreviewWindows = new HashSet<IWindow> { nodes[1].WindowReference, nodes[3].WindowReference };
            var notifications = new List<(WindowNode Node, bool Visible)>();
            foreach (var node in nodes)
            {
                var vm = fixture.WindowModel(node);
                vm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(TilingWindowViewModel.IsPreviewVisible))
                    {
                        notifications.Add((node, vm.IsPreviewVisible));
                    }
                };
            }
            fixture.Renderer.PreviewWindows = new HashSet<IWindow> { nodes[0].WindowReference, nodes[2].WindowReference };
            CollectionAssert.AreEqual(new[] { (nodes[1], false), (nodes[3], false), (nodes[0], true), (nodes[2], true) }, notifications);
        }

        [TestMethod]
        public void RendererKeepsPersistentOrderIdentityFocusAndReleasesRemovedModels()
        {
            using var fixture = new RendererFixture();
            var nodes = Enumerable.Range(0, 4).Select(fixture.CreateWindow).ToArray();
            fixture.Update([nodes[0], nodes[1], nodes[2]], [nodes[1]]);
            var removed = fixture.WindowModel(nodes[0]);
            var persisted = fixture.WindowModel(nodes[1]);
            Assert.AreEqual(3, fixture.CursorSubscriptions);
            fixture.Update([nodes[2], nodes[1], nodes[3], nodes[1]], [nodes[3]]);

            CollectionAssert.AreEqual(new TilingNode?[] { nodes[1], nodes[2], nodes[3] },
                fixture.ViewModel.WindowElements.Select(vm => vm.Node).ToArray());
            Assert.AreSame(persisted, fixture.WindowModel(nodes[1]));
            Assert.IsNull(removed.Node);
            Assert.IsNull(removed.PrimaryActionCommand);
            Assert.IsFalse(persisted.HasFocus);
            Assert.IsTrue(fixture.WindowModel(nodes[3]).HasFocus);
            Assert.AreEqual(3, fixture.CursorSubscriptions);

            fixture.Renderer.InvalidateView();
            fixture.Renderer.InvalidateView();
            Assert.AreEqual(0, fixture.CursorSubscriptions);
            Assert.AreEqual(0, fixture.Models.Count);
            Assert.AreEqual(0, fixture.ViewModel.WindowElements.Count);
        }

        [TestMethod]
        public void PublicUpdateRetriesSameSnapshotAfterMiddleWindowCreationFailure()
        {
            using var fixture = new RendererFixture();
            var nodes = Enumerable.Range(0, 4).Select(fixture.CreateWindow).ToArray();
            var root = new SplitPanelNode();
            var tree = new DesktopTree { Root = root, WorkArea = fixture.DisplayBounds };
            foreach (var node in nodes) { root.Attach(node); }
            tree.Measure();
            tree.Arrange();
            fixture.Renderer.UpdateOverlay([nodes[0]], [nodes[0]]);
            var originalModel = fixture.WindowModel(nodes[0]);
            var preview = new HashSet<IWindow> { nodes[0].WindowReference, nodes[3].WindowReference };
            fixture.Renderer.PreviewWindows = preview;
            Assert.IsTrue(originalModel.IsPreviewVisible);
            var failedWindow = Mock.Get(nodes[2].WindowReference);
            var createdModels = CaptureCreatedWindowModels(nodes[2]);
            var failure = new InvalidOperationException("controlled middle creation failure before same-snapshot retry");
            failedWindow.SetupGet(window => window.Title).Returns(() =>
            {
                ThrowCreationFailure(failure);
                return string.Empty;
            });
            try
            {
                var actual = Assert.ThrowsException<InvalidOperationException>(() => fixture.Renderer.UpdateOverlay(nodes, [nodes[3]]));
                Assert.AreSame(failure, actual);
                StringAssert.Contains(actual.StackTrace ?? string.Empty, nameof(ThrowCreationFailure));
                Assert.AreEqual(2, fixture.Models.Count);
                var prefixModel = fixture.WindowModel(nodes[1]);
                AssertReleasedCreatedWindow(createdModels.Single(), failedWindow);
                Assert.AreEqual(3, fixture.CursorAdds);
                Assert.AreEqual(2, fixture.CursorSubscriptions);
                Assert.IsFalse(fixture.Models.ContainsKey(nodes[3]));

                failedWindow.SetupGet(window => window.Title).Returns("recovered title");
                fixture.Renderer.UpdateOverlay(nodes, [nodes[3]]);

                Assert.AreEqual(nodes.Length, fixture.Models.Count, "Retrying the same array must create the missing middle and tail owners.");
                CollectionAssert.AreEqual(nodes, fixture.ViewModel.WindowElements.Select(model => model.Node).ToArray());
                var recoveredModels = nodes.Select(fixture.WindowModel).ToArray();
                Assert.AreNotSame(originalModel, recoveredModels[0]);
                Assert.AreNotSame(prefixModel, recoveredModels[1]);
                AssertReleasedCreatedWindow(originalModel, Mock.Get(nodes[0].WindowReference));
                AssertReleasedCreatedWindow(prefixModel, Mock.Get(nodes[1].WindowReference));
                Assert.AreSame(preview, fixture.Renderer.PreviewWindows);
                for (int index = 0; index < nodes.Length; index++)
                {
                    var model = recoveredModels[index];
                    Assert.AreSame(nodes[index], model.Node);
                    Assert.AreSame(nodes[index].WindowReference, ((WindowNode)model.Node!).WindowReference);
                    Assert.AreEqual(index == 3, model.HasFocus);
                    Assert.AreEqual(preview.Contains(nodes[index].WindowReference), model.IsPreviewVisible);
                    Assert.AreEqual(index == 2 ? "recovered title" : $"fake-{index}", model.Title);
                    var bounds = nodes[index].ComputedRectangle;
                    Assert.AreEqual(new Rectangle(bounds.Left - fixture.DisplayBounds.Left, bounds.Top - fixture.DisplayBounds.Top,
                        bounds.Right - fixture.DisplayBounds.Left, bounds.Bottom - fixture.DisplayBounds.Top), model.ComputedBounds);
                }
                Assert.AreEqual(7, fixture.CursorAdds);
                Assert.AreEqual(4, fixture.CursorSubscriptions);

                fixture.Renderer.UpdateOverlay(nodes, [nodes[3]]);
                fixture.Renderer.PreviewWindows = preview;
                CollectionAssert.AreEqual(recoveredModels, fixture.ViewModel.WindowElements.ToArray());
                Assert.AreEqual(7, fixture.CursorAdds, "A successful same-snapshot update must keep the recovered owners.");
            }
            finally
            {
                failedWindow.SetupGet(window => window.Title).Returns("fixture cleanup");
                foreach (var model in createdModels) { model.Dispose(); }
            }
        }

        [TestMethod]
        public void PublicUpdateRetriesRemainingRemovalsForSameEmptySnapshot()
        {
            using var fixture = new RendererFixture();
            fixture.PopulatePanelAndWindow();
            var panel = fixture.ViewModel.PanelElements.Single();
            var window = fixture.ViewModel.WindowElements.Single();
            var windowMock = Mock.Get(((WindowNode)window.Node!).WindowReference);
            var emptySnapshot = Array.Empty<TilingNode>();
            var failure = new InvalidOperationException("controlled panel removal failure before same-snapshot retry");
            NotifyCollectionChangedEventHandler changed = (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Remove) { ThrowInvalidationFailure(failure); }
            };
            fixture.ViewModel.PanelElements.CollectionChanged += changed;
            try
            {
                var actual = Assert.ThrowsException<InvalidOperationException>(() => fixture.Renderer.UpdateOverlay(emptySnapshot, []));
                Assert.AreSame(failure, actual);
                StringAssert.Contains(actual.StackTrace ?? string.Empty, nameof(ThrowInvalidationFailure));
                Assert.AreEqual(1, fixture.Models.Count);
                Assert.AreEqual(0, fixture.ViewModel.PanelElements.Count);
                Assert.AreEqual(1, fixture.ViewModel.WindowElements.Count);
                Assert.IsNull(panel.PrimaryActionCommand);
                fixture.ViewModel.PanelElements.CollectionChanged -= changed;

                fixture.Renderer.UpdateOverlay(emptySnapshot, []);

                Assert.AreEqual(0, fixture.Models.Count, "The same empty array must retry the window removal skipped after the panel failure.");
                Assert.AreEqual(0, fixture.ViewModel.PanelElements.Count);
                Assert.AreEqual(0, fixture.ViewModel.WindowElements.Count);
                Assert.AreEqual(0, fixture.PreviousSnapshot.Count);
                Assert.AreEqual(0, fixture.CursorSubscriptions);
                AssertReleasedCreatedWindow(window, windowMock);
                fixture.Renderer.UpdateOverlay(emptySnapshot, []);
                Assert.AreEqual(1, fixture.CursorAdds);
            }
            finally
            {
                fixture.ViewModel.PanelElements.CollectionChanged -= changed;
                panel.Dispose();
                window.Dispose();
            }
        }

        [DataTestMethod]
        [DataRow("insert")]
        [DataRow("remove")]
        public void PublicRecoveryHandlesCollectionFailureBeforeMutation(string boundary)
        {
            var collection = new FailBeforeMutationCollection<TilingWindowViewModel>();
            using var fixture = new RendererFixture(new TilingOverlayViewModel { WindowElements = collection });
            var node = fixture.CreateWindow(0);
            var window = Mock.Get(node.WindowReference);
            var createdModels = CaptureCreatedWindowModels(node);
            TilingNode[] snapshot = [node];
            TilingNode[] emptySnapshot = [];
            var failure = new InvalidOperationException($"controlled {boundary} collection failure before mutation");
            if (boundary == "insert")
            {
                collection.NextInsertFailure = failure;
            }
            else
            {
                fixture.Renderer.UpdateOverlay(snapshot, [node]);
                collection.NextRemoveFailure = failure;
            }
            var requestedSnapshot = boundary == "insert" ? snapshot : emptySnapshot;
            try
            {
                var actual = Assert.ThrowsException<InvalidOperationException>(() => fixture.Renderer.UpdateOverlay(requestedSnapshot, [node]));
                Assert.AreSame(failure, actual);
                StringAssert.Contains(actual.StackTrace ?? string.Empty, nameof(ThrowCreationFailure));
                var interruptedModel = createdModels.Single();
                Assert.AreEqual(boundary == "insert" ? 1 : 0, fixture.Models.Count);
                Assert.AreEqual(boundary == "insert" ? 0 : 1, collection.Count);
                Assert.AreEqual(boundary == "insert" ? 1 : 0, fixture.CursorSubscriptions);

                fixture.Renderer.UpdateOverlay(requestedSnapshot, [node]);

                Assert.AreEqual(boundary == "insert" ? 1 : 0, collection.Count,
                    "Recovery must reconcile a dictionary/collection disagreement left before the observable mutation.");
                Assert.AreEqual(collection.Count, fixture.Models.Count);
                AssertReleasedCreatedWindow(interruptedModel, window);
                if (boundary == "insert")
                {
                    var recoveredModel = fixture.WindowModel(node);
                    Assert.AreNotSame(interruptedModel, recoveredModel);
                    Assert.AreSame(node, recoveredModel.Node);
                    Assert.AreSame(recoveredModel, collection.Single());
                    Assert.IsTrue(recoveredModel.HasFocus);
                    Assert.AreEqual(2, fixture.CursorAdds);
                    Assert.AreEqual(1, fixture.CursorSubscriptions);
                    fixture.Renderer.UpdateOverlay(requestedSnapshot, [node]);
                    Assert.AreSame(recoveredModel, fixture.WindowModel(node));
                    Assert.AreEqual(2, fixture.CursorAdds);
                }
                else
                {
                    Assert.AreEqual(0, fixture.PreviousSnapshot.Count);
                    Assert.AreEqual(0, fixture.CursorSubscriptions);
                    Assert.AreEqual(1, fixture.CursorAdds);
                }
            }
            finally
            {
                collection.NextInsertFailure = null;
                collection.NextRemoveFailure = null;
                foreach (var model in createdModels) { model.Dispose(); }
            }
        }

        [TestMethod]
        public void PublicRecoveryRemainsPendingWhenResetNotificationThrows()
        {
            using var fixture = new RendererFixture();
            var nodes = Enumerable.Range(0, 2).Select(fixture.CreateWindow).ToArray();
            fixture.Renderer.UpdateOverlay([nodes[0]], [nodes[0]]);
            var originalModel = fixture.WindowModel(nodes[0]);
            var failedWindow = Mock.Get(nodes[1].WindowReference);
            var createdModels = CaptureCreatedWindowModels(nodes[1]);
            var creationFailure = new InvalidOperationException("controlled creation failure before failing recovery cleanup");
            var resetFailure = new InvalidOperationException("controlled recovery Reset notification failure");
            failedWindow.SetupGet(window => window.Title).Returns(() =>
            {
                ThrowCreationFailure(creationFailure);
                return string.Empty;
            });
            bool failReset = true;
            int resets = 0;
            NotifyCollectionChangedEventHandler changed = (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Reset)
                {
                    resets++;
                    if (failReset) { ThrowInvalidationFailure(resetFailure); }
                }
            };
            try
            {
                var first = Assert.ThrowsException<InvalidOperationException>(() => fixture.Renderer.UpdateOverlay(nodes, [nodes[1]]));
                Assert.AreSame(creationFailure, first);
                Assert.AreEqual(2, fixture.CursorAdds);
                Assert.AreEqual(1, fixture.CursorSubscriptions);
                failedWindow.SetupGet(window => window.Title).Returns("recovered title");
                fixture.ViewModel.WindowElements.CollectionChanged += changed;

                var second = Assert.ThrowsException<InvalidOperationException>(() => fixture.Renderer.UpdateOverlay(nodes, [nodes[1]]));
                Assert.AreSame(resetFailure, second);
                StringAssert.Contains(second.StackTrace ?? string.Empty, nameof(ThrowInvalidationFailure));
                Assert.AreEqual(1, resets);
                Assert.AreEqual(2, fixture.CursorAdds, "A failed recovery cleanup must not acquire any replacement owner.");
                Assert.AreEqual(0, fixture.CursorSubscriptions);
                Assert.AreEqual(0, fixture.Models.Count);
                Assert.AreEqual(0, fixture.ViewModel.WindowElements.Count);
                AssertReleasedCreatedWindow(originalModel, Mock.Get(nodes[0].WindowReference));

                failReset = false;
                fixture.Renderer.UpdateOverlay(nodes, [nodes[1]]);

                Assert.AreEqual(2, resets, "An incomplete recovery remains pending even when the previous cleanup emptied the collections.");
                CollectionAssert.AreEqual(nodes, fixture.ViewModel.WindowElements.Select(model => model.Node).ToArray());
                var recoveredModels = nodes.Select(fixture.WindowModel).ToArray();
                Assert.IsFalse(recoveredModels[0].HasFocus);
                Assert.IsTrue(recoveredModels[1].HasFocus);
                Assert.AreEqual(4, fixture.CursorAdds);
                Assert.AreEqual(2, fixture.CursorSubscriptions);
                fixture.Renderer.UpdateOverlay(nodes, [nodes[1]]);
                CollectionAssert.AreEqual(recoveredModels, fixture.ViewModel.WindowElements.ToArray());
                Assert.AreEqual(2, resets);
                Assert.AreEqual(4, fixture.CursorAdds);
            }
            finally
            {
                failReset = false;
                fixture.ViewModel.WindowElements.CollectionChanged -= changed;
                failedWindow.SetupGet(window => window.Title).Returns("fixture cleanup");
                foreach (var model in createdModels) { model.Dispose(); }
            }
        }

        [TestMethod]
        public void StaleFailedPassDoesNotScheduleRecoveryOverReentrantFreshSnapshot()
        {
            using var fixture = new RendererFixture();
            var nodes = Enumerable.Range(0, 3).Select(fixture.CreateWindow).ToArray();
            TilingNode[] interruptedSnapshot = [nodes[0], nodes[1]];
            TilingNode[] freshSnapshot = [nodes[2]];
            var prefixModels = CaptureCreatedWindowModels(nodes[0]);
            var failedModels = CaptureCreatedWindowModels(nodes[1]);
            var failedWindow = Mock.Get(nodes[1].WindowReference);
            var failure = new InvalidOperationException("controlled obsolete creation failure after fresh reentry");
            TilingWindowViewModel? freshModel = null;
            int reentries = 0;
            int resets = 0;
            NotifyCollectionChangedEventHandler changed = (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Reset) { resets++; }
            };
            failedWindow.SetupGet(window => window.Title).Returns(() =>
            {
                reentries++;
                fixture.Renderer.InvalidateView();
                fixture.Renderer.UpdateOverlay(freshSnapshot, [nodes[2]]);
                freshModel = fixture.WindowModel(nodes[2]);
                ThrowCreationFailure(failure);
                return string.Empty;
            });
            try
            {
                var actual = Assert.ThrowsException<InvalidOperationException>(() => fixture.Renderer.UpdateOverlay(interruptedSnapshot, []));
                Assert.AreSame(failure, actual);
                StringAssert.Contains(actual.StackTrace ?? string.Empty, nameof(ThrowCreationFailure));
                Assert.AreEqual(1, reentries);
                AssertReleasedCreatedWindow(prefixModels.Single(), Mock.Get(nodes[0].WindowReference));
                AssertReleasedCreatedWindow(failedModels.Single(), failedWindow);
                Assert.AreEqual(1, fixture.Models.Count);
                Assert.AreSame(freshModel, fixture.WindowModel(nodes[2]));
                Assert.AreEqual(3, fixture.CursorAdds);
                Assert.AreEqual(1, fixture.CursorSubscriptions);
                fixture.ViewModel.WindowElements.CollectionChanged += changed;

                fixture.Renderer.UpdateOverlay(freshSnapshot, [nodes[2]]);

                Assert.AreSame(freshModel, fixture.WindowModel(nodes[2]), "The obsolete failure must not invalidate the independently completed fresh snapshot.");
                CollectionAssert.AreEqual(freshSnapshot, fixture.ViewModel.WindowElements.Select(model => model.Node).ToArray());
                Assert.IsTrue(freshModel!.HasFocus);
                Assert.AreEqual(0, resets);
                Assert.AreEqual(3, fixture.CursorAdds);
                Assert.AreEqual(1, fixture.CursorSubscriptions);
            }
            finally
            {
                failedWindow.SetupGet(window => window.Title).Returns("fixture cleanup");
                fixture.ViewModel.WindowElements.CollectionChanged -= changed;
                foreach (var model in prefixModels.Concat(failedModels)) { model.Dispose(); }
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void PublicRecoverySuppressesNestedUpdateAndHonorsDisposeDuringReset(bool disposeDuringReset)
        {
            using var fixture = new RendererFixture();
            var nodes = Enumerable.Range(0, 3).Select(fixture.CreateWindow).ToArray();
            var originalModels = CaptureCreatedWindowModels(nodes[0]);
            var failedModels = CaptureCreatedWindowModels(nodes[1]);
            TilingNode[] currentSnapshot = [nodes[0], nodes[1]];
            TilingNode[] nestedSnapshot = [nodes[2]];
            fixture.Renderer.UpdateOverlay([nodes[0]], [nodes[0]]);
            var failedWindow = Mock.Get(nodes[1].WindowReference);
            var failure = new InvalidOperationException("controlled creation failure before reentrant recovery cleanup");
            failedWindow.SetupGet(window => window.Title).Returns(() =>
            {
                ThrowCreationFailure(failure);
                return string.Empty;
            });
            int resets = 0;
            int nestedAcquisitions = 0;
            NotifyCollectionChangedEventHandler changed = (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Reset)
                {
                    resets++;
                    if (resets == 1)
                    {
                        int addsBefore = fixture.CursorAdds;
                        fixture.Renderer.UpdateOverlay(nestedSnapshot, [nodes[2]]);
                        nestedAcquisitions = fixture.CursorAdds - addsBefore;
                        if (disposeDuringReset) { fixture.DisposeManagedOwner(); }
                    }
                }
            };
            try
            {
                var actual = Assert.ThrowsException<InvalidOperationException>(() => fixture.Renderer.UpdateOverlay(currentSnapshot, [nodes[1]]));
                Assert.AreSame(failure, actual);
                Assert.AreEqual(2, fixture.CursorAdds);
                failedWindow.SetupGet(window => window.Title).Returns("recovered title");
                fixture.ViewModel.WindowElements.CollectionChanged += changed;

                fixture.Renderer.UpdateOverlay(currentSnapshot, [nodes[1]]);

                Assert.AreEqual(1, resets);
                Assert.AreEqual(0, nestedAcquisitions, "A Reset callback cannot admit another update while recovery invalidates its owners.");
                Assert.IsFalse(fixture.Models.ContainsKey(nodes[2]));
                if (disposeDuringReset)
                {
                    Assert.AreEqual(0, fixture.Models.Count);
                    Assert.AreEqual(0, fixture.ViewModel.WindowElements.Count);
                    Assert.AreEqual(0, fixture.PreviousSnapshot.Count);
                    Assert.AreEqual(0, fixture.CursorSubscriptions);
                    Assert.AreEqual(2, fixture.CursorAdds);
                    fixture.Renderer.UpdateOverlay(currentSnapshot, [nodes[1]]);
                    Assert.AreEqual(2, fixture.CursorAdds, "Dispose during recovery rejects both the current update and later updates.");
                }
                else
                {
                    CollectionAssert.AreEqual(currentSnapshot, fixture.ViewModel.WindowElements.Select(model => model.Node).ToArray());
                    var recoveredModels = currentSnapshot.Cast<WindowNode>().Select(fixture.WindowModel).ToArray();
                    Assert.IsFalse(recoveredModels[0].HasFocus);
                    Assert.IsTrue(recoveredModels[1].HasFocus);
                    Assert.AreEqual(4, fixture.CursorAdds);
                    Assert.AreEqual(2, fixture.CursorSubscriptions);
                    fixture.Renderer.UpdateOverlay(currentSnapshot, [nodes[1]]);
                    CollectionAssert.AreEqual(recoveredModels, fixture.ViewModel.WindowElements.ToArray());
                    Assert.AreEqual(1, resets);
                    Assert.AreEqual(4, fixture.CursorAdds);
                }
            }
            finally
            {
                failedWindow.SetupGet(window => window.Title).Returns("fixture cleanup");
                fixture.ViewModel.WindowElements.CollectionChanged -= changed;
                foreach (var model in originalModels.Concat(failedModels)) { model.Dispose(); }
                foreach (var model in fixture.Models.Values) { model.Dispose(); }
            }
        }

        [DataTestMethod]
        [DataRow("title")]
        [DataRow("bounds")]
        public void UnpublishedWindowModelIsReleasedAfterCreationFailure(string boundary)
        {
            using var fixture = new RendererFixture();
            var node = fixture.CreateWindow(0);
            var window = Mock.Get(node.WindowReference);
            var createdModels = CaptureCreatedWindowModels(node);
            var failure = new InvalidOperationException($"controlled {boundary} creation failure");
            if (boundary == "title")
            {
                window.SetupGet(value => value.Title).Returns(() =>
                {
                    ThrowCreationFailure(failure);
                    return string.Empty;
                });
            }
            else { fixture.OnBoundsRead = () => ThrowCreationFailure(failure); }

            try
            {
                var actual = Assert.ThrowsException<InvalidOperationException>(() => fixture.Renderer.UpdateOverlay([node], []));
                Assert.AreSame(failure, actual);
                StringAssert.Contains(actual.StackTrace ?? string.Empty, nameof(ThrowCreationFailure));
                Assert.AreEqual(1, createdModels.Count);
                var unpublishedModel = createdModels.Single();
                Assert.AreEqual(0, fixture.Models.Count);
                Assert.AreEqual(0, fixture.ViewModel.WindowElements.Count);
                Assert.AreEqual(0, fixture.ViewModel.PanelElements.Count);
                fixture.Renderer.InvalidateView();
                fixture.Renderer.InvalidateView();
                Assert.AreEqual(0, fixture.PreviousSnapshot.Count);
                Assert.AreEqual(0, fixture.CursorSubscriptions);
                AssertReleasedCreatedWindow(unpublishedModel, window);

                fixture.OnBoundsRead = null;
                window.SetupGet(value => value.Title).Returns("recovered title");
                fixture.Renderer.UpdateOverlay([node], [node]);
                var recoveredModel = fixture.WindowModel(node);
                Assert.AreNotSame(unpublishedModel, recoveredModel);
                Assert.AreSame(node, recoveredModel.Node);
                Assert.AreEqual("recovered title", recoveredModel.Title);
                Assert.IsTrue(recoveredModel.HasFocus);
                Assert.AreEqual(2, fixture.CursorAdds);
                Assert.AreEqual(1, fixture.CursorSubscriptions);
                fixture.Renderer.UpdateOverlay([node], [node]);
                Assert.AreSame(recoveredModel, fixture.WindowModel(node));
                Assert.AreEqual(2, fixture.CursorAdds);
            }
            finally
            {
                fixture.OnBoundsRead = null;
                window.SetupGet(value => value.Title).Returns("fixture cleanup");
                // Retain and inspect unpublished R0 owners before repairing them.
                // InvalidateView alone cannot discover these failed creations.
                foreach (var model in createdModels) { model.Dispose(); }
            }
        }

        [TestMethod]
        public void PanelCreationFailureDoesNotPublishModelOrAcquireTailWindow()
        {
            using var fixture = new RendererFixture();
            var panel = new SplitPanelNode();
            var node = fixture.CreateWindow(0);
            var tree = new DesktopTree { Root = panel, WorkArea = fixture.DisplayBounds };
            panel.Attach(node);
            tree.Measure();
            tree.Arrange();
            var snapshot = panel.Nodes.ToArray();
            var failure = new InvalidOperationException("controlled panel bounds creation failure");
            fixture.OnBoundsRead = () => ThrowCreationFailure(failure);
            try
            {
                var actual = Assert.ThrowsException<InvalidOperationException>(() => fixture.Renderer.UpdateOverlay(snapshot, []));
                Assert.AreSame(failure, actual);
                StringAssert.Contains(actual.StackTrace ?? string.Empty, nameof(ThrowCreationFailure));
                Assert.AreEqual(0, fixture.Models.Count);
                Assert.AreEqual(0, fixture.ViewModel.PanelElements.Count);
                Assert.AreEqual(0, fixture.ViewModel.WindowElements.Count);
                Assert.AreEqual(0, fixture.CursorAdds);
                Assert.AreEqual(0, fixture.CursorSubscriptions);
                CollectionAssert.AreEqual(new TilingNode[] { panel, node }, panel.Nodes.ToArray());

                fixture.OnBoundsRead = null;
                fixture.Renderer.InvalidateView();
                fixture.Renderer.InvalidateView();
                fixture.Renderer.UpdateOverlay(snapshot, [node, panel]);
                Assert.AreSame(panel, fixture.ViewModel.PanelElements.Single().Node);
                Assert.AreSame(node, fixture.ViewModel.WindowElements.Single().Node);
                Assert.AreSame(fixture.WindowModel(node), fixture.ViewModel.PanelElements.Single().ChildNodes.Single());
                Assert.AreEqual(1, fixture.CursorAdds);
                Assert.AreEqual(1, fixture.CursorSubscriptions);
            }
            finally { fixture.OnBoundsRead = null; }
        }

        [TestMethod]
        public void DuplicateRegistrationReleasesOnlyTheUnpublishedWindowModel()
        {
            using var fixture = new RendererFixture();
            var node = fixture.CreateWindow(0);
            var window = Mock.Get(node.WindowReference);
            var createdModels = CaptureCreatedWindowModels(node);
            var existingCommand = new DelegateCommand<TilingNodeViewModel>(_ => { });
            var existingModel = new TilingWindowViewModel
            {
                Node = node,
                Title = "existing model",
                PrimaryActionCommand = existingCommand,
            };
            // An interrupted pass can leave registration ahead of the recorded
            // snapshot. The failed Add must not release that existing owner.
            fixture.Models.Add(node, existingModel);
            fixture.ViewModel.WindowElements.Add(existingModel);
            try
            {
                Assert.ThrowsException<ArgumentException>(() => fixture.Renderer.UpdateOverlay([node], []));
                Assert.AreEqual(2, createdModels.Count);
                var unpublishedModel = createdModels.Single(model => !ReferenceEquals(model, existingModel));
                Assert.AreEqual("fake-0", unpublishedModel.Title);
                Assert.AreEqual(1, fixture.Models.Count);
                Assert.AreSame(existingModel, fixture.WindowModel(node));
                Assert.AreSame(existingModel, fixture.ViewModel.WindowElements.Single());
                Assert.AreSame(node, existingModel.Node);
                Assert.AreEqual("existing model", existingModel.Title);
                Assert.AreSame(existingCommand, existingModel.PrimaryActionCommand);
                Assert.AreEqual(2, fixture.CursorAdds);
                Assert.AreEqual(1, fixture.CursorSubscriptions);
                AssertReleasedCreatedWindow(unpublishedModel, window);

                fixture.Renderer.InvalidateView();
                Assert.IsNull(existingModel.Node);
                Assert.IsNull(existingModel.PrimaryActionCommand);
                Assert.AreEqual(0, fixture.CursorSubscriptions);
                Assert.AreEqual(0, fixture.Models.Count);
                Assert.AreEqual(0, fixture.ViewModel.WindowElements.Count);
            }
            finally
            {
                foreach (var model in createdModels) { model.Dispose(); }
            }
        }

        [DataTestMethod]
        [DataRow("title", false)]
        [DataRow("title", true)]
        [DataRow("bounds", false)]
        [DataRow("bounds", true)]
        public void PublicUpdateRejectsWindowPublicationAfterCreationCallbackInvalidatesOrDisposesOwner(string boundary, bool dispose)
        {
            using var fixture = new RendererFixture();
            var snapshot = new[] { fixture.CreateWindow(0), fixture.CreateWindow(1) };
            var window = Mock.Get(snapshot[0].WindowReference);
            var createdModels = CaptureCreatedWindowModels(snapshot[0]);
            int callbacks = 0;
            void InterruptCreation()
            {
                if (callbacks != 0) { return; }
                callbacks++;
                if (dispose) { fixture.DisposeManagedOwner(); }
                else { fixture.Renderer.InvalidateView(); }
            }
            if (boundary == "title")
            {
                window.SetupGet(value => value.Title).Returns(() =>
                {
                    InterruptCreation();
                    return "interrupted title";
                });
            }
            else { fixture.OnBoundsRead = InterruptCreation; }

            try
            {
                fixture.Renderer.UpdateOverlay(snapshot, []);
                Assert.AreEqual(1, callbacks);
                Assert.AreEqual(1, createdModels.Count);
                var unpublishedModel = createdModels.Single();
                Assert.AreEqual(1, fixture.CursorAdds, "The obsolete pass must not acquire a second window owner.");
                Assert.AreEqual(0, fixture.CursorSubscriptions);
                Assert.AreEqual(0, fixture.Models.Count);
                Assert.AreEqual(0, fixture.PreviousSnapshot.Count);
                Assert.AreEqual(0, fixture.ViewModel.WindowElements.Count);
                Assert.AreEqual(0, fixture.ViewModel.PanelElements.Count);
                AssertReleasedCreatedWindow(unpublishedModel, window);

                fixture.OnBoundsRead = null;
                window.SetupGet(value => value.Title).Returns("recovered title");
                fixture.Renderer.UpdateOverlay(snapshot, []);
                if (dispose)
                {
                    fixture.DisposeManagedOwner();
                    fixture.Renderer.InvalidateView();
                    Assert.AreEqual(1, fixture.CursorAdds);
                    Assert.AreEqual(0, fixture.CursorSubscriptions);
                    Assert.AreEqual(0, fixture.Models.Count);
                    Assert.AreEqual(0, fixture.ViewModel.WindowElements.Count);
                }
                else
                {
                    Assert.AreEqual(3, fixture.CursorAdds);
                    Assert.AreEqual(2, fixture.CursorSubscriptions);
                    CollectionAssert.AreEqual(snapshot, fixture.ViewModel.WindowElements.Select(model => model.Node).ToArray());
                    var recoveredModels = fixture.ViewModel.WindowElements.ToArray();
                    Assert.AreNotSame(unpublishedModel, recoveredModels[0]);
                    fixture.Renderer.UpdateOverlay(snapshot, []);
                    CollectionAssert.AreEqual(recoveredModels, fixture.ViewModel.WindowElements.ToArray());
                    Assert.AreEqual(3, fixture.CursorAdds);
                }
            }
            finally
            {
                fixture.OnBoundsRead = null;
                window.SetupGet(value => value.Title).Returns("fixture cleanup");
                foreach (var model in createdModels.Concat(fixture.Models.Values.OfType<TilingWindowViewModel>()).Distinct())
                {
                    model.Dispose();
                }
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void PublicUpdateRejectsPanelPublicationAfterCreationCallbackInvalidatesOrDisposesOwner(bool dispose)
        {
            using var fixture = new RendererFixture();
            var panel = new SplitPanelNode();
            var node = fixture.CreateWindow(0);
            var tree = new DesktopTree { Root = panel, WorkArea = fixture.DisplayBounds };
            panel.Attach(node);
            tree.Measure();
            tree.Arrange();
            var snapshot = panel.Nodes.ToArray();
            int callbacks = 0;
            fixture.OnBoundsRead = () =>
            {
                if (callbacks != 0) { return; }
                callbacks++;
                if (dispose) { fixture.DisposeManagedOwner(); }
                else { fixture.Renderer.InvalidateView(); }
            };
            try
            {
                fixture.Renderer.UpdateOverlay(snapshot, []);
                Assert.AreEqual(1, callbacks);
                Assert.AreEqual(0, fixture.Models.Count);
                Assert.AreEqual(0, fixture.PreviousSnapshot.Count);
                Assert.AreEqual(0, fixture.ViewModel.PanelElements.Count);
                Assert.AreEqual(0, fixture.ViewModel.WindowElements.Count);
                Assert.AreEqual(0, fixture.CursorAdds, "A cancelled panel creation must not acquire its tail window.");
                Assert.AreEqual(0, fixture.CursorSubscriptions);

                fixture.OnBoundsRead = null;
                fixture.Renderer.UpdateOverlay(snapshot, []);
                if (dispose)
                {
                    fixture.DisposeManagedOwner();
                    fixture.Renderer.InvalidateView();
                    Assert.AreEqual(0, fixture.Models.Count);
                    Assert.AreEqual(0, fixture.ViewModel.PanelElements.Count);
                    Assert.AreEqual(0, fixture.ViewModel.WindowElements.Count);
                    Assert.AreEqual(0, fixture.CursorAdds);
                }
                else
                {
                    Assert.AreSame(panel, fixture.ViewModel.PanelElements.Single().Node);
                    Assert.AreSame(node, fixture.ViewModel.WindowElements.Single().Node);
                    Assert.AreSame(fixture.WindowModel(node), fixture.ViewModel.PanelElements.Single().ChildNodes.Single());
                    Assert.AreEqual(1, fixture.CursorAdds);
                    Assert.AreEqual(1, fixture.CursorSubscriptions);
                    var recoveredPanel = fixture.ViewModel.PanelElements.Single();
                    var recoveredWindow = fixture.WindowModel(node);
                    fixture.Renderer.UpdateOverlay(snapshot, []);
                    Assert.AreSame(recoveredPanel, fixture.ViewModel.PanelElements.Single());
                    Assert.AreSame(recoveredWindow, fixture.WindowModel(node));
                    Assert.AreEqual(1, fixture.CursorAdds);
                }
            }
            finally
            {
                fixture.OnBoundsRead = null;
                foreach (var model in fixture.Models.Values) { model.Dispose(); }
            }
        }

        [TestMethod]
        public void CreationFailurePreservesPrimaryAndRecordsLaterCursorCleanupFailure()
        {
            using var fixture = new RendererFixture();
            const string errorKey = "TilingOverlayRenderer.CreateViewModelExceptions";
            var node = fixture.CreateWindow(0);
            var window = Mock.Get(node.WindowReference);
            var createdModels = CaptureCreatedWindowModels(node);
            var primary = new InvalidOperationException("controlled primary title failure");
            var existing = new InvalidOperationException("existing supplemental creation failure");
            var cleanupFailure = new InvalidOperationException("controlled late cursor cleanup failure");
            primary.Data[errorKey] = new AggregateException(existing);
            window.SetupGet(value => value.Title).Returns(() =>
            {
                ThrowCreationFailure(primary);
                return string.Empty;
            });
            int cursorCleanupAttempts = 0;
            fixture.OnCursorRemove = () =>
            {
                cursorCleanupAttempts++;
                ThrowCreationFailure(cleanupFailure);
            };
            try
            {
                var actual = Assert.ThrowsException<InvalidOperationException>(() => fixture.Renderer.UpdateOverlay([node], []));
                Assert.AreSame(primary, actual);
                StringAssert.Contains(actual.StackTrace ?? string.Empty, nameof(ThrowCreationFailure));
                Assert.AreEqual(1, createdModels.Count);
                Assert.AreEqual(1, cursorCleanupAttempts, "The unpublished owner must be released once after its creation failure.");
                var supplemental = (AggregateException)actual.Data[errorKey]!;
                CollectionAssert.AreEqual(new Exception[] { existing, cleanupFailure }, supplemental.InnerExceptions.ToArray());
                Assert.AreEqual(0, fixture.Models.Count);
                Assert.AreEqual(0, fixture.ViewModel.WindowElements.Count);
                Assert.AreEqual(0, fixture.CursorSubscriptions);
                AssertReleasedCreatedWindow(createdModels.Single(), window);
            }
            finally
            {
                fixture.OnCursorRemove = null;
                window.SetupGet(value => value.Title).Returns("fixture cleanup");
                foreach (var model in createdModels) { model.Dispose(); }
            }
        }

        [TestMethod]
        public void RepeatedTitleCreationFailuresDoNotAccumulateUnpublishedWindowOwners()
        {
            using var fixture = new RendererFixture();
            var createdModels = new List<TilingWindowViewModel>(100);
            var windows = new List<Mock<IWindow>>(100);
            int maximumCursorSubscriptions = 0;
            try
            {
                for (int cycle = 0; cycle < 100; cycle++)
                {
                    var node = fixture.CreateWindow(cycle);
                    var window = Mock.Get(node.WindowReference);
                    windows.Add(window);
                    var captured = CaptureCreatedWindowModels(node);
                    var failure = new InvalidOperationException($"controlled title creation failure {cycle}");
                    window.SetupGet(value => value.Title).Returns(() =>
                    {
                        ThrowCreationFailure(failure);
                        return string.Empty;
                    });
                    try
                    {
                        var actual = Assert.ThrowsException<InvalidOperationException>(() => fixture.Renderer.UpdateOverlay([node], []));
                        Assert.AreSame(failure, actual);
                        StringAssert.Contains(actual.StackTrace ?? string.Empty, nameof(ThrowCreationFailure));
                        Assert.AreEqual(1, captured.Count);
                        Assert.AreEqual(0, fixture.Models.Count);
                        Assert.AreEqual(0, fixture.ViewModel.WindowElements.Count);
                        maximumCursorSubscriptions = Math.Max(maximumCursorSubscriptions, fixture.CursorSubscriptions);
                        fixture.Renderer.InvalidateView();
                        fixture.Renderer.InvalidateView();
                        Assert.AreEqual(0, fixture.PreviousSnapshot.Count);
                    }
                    finally { createdModels.AddRange(captured); }
                }

                Assert.AreEqual(100, createdModels.Count);
                Assert.AreEqual(100, fixture.CursorAdds);
                Assert.AreEqual(0, fixture.CursorSubscriptions);
                Assert.AreEqual(0, maximumCursorSubscriptions);
                for (int index = 0; index < createdModels.Count; index++)
                {
                    AssertReleasedCreatedWindow(createdModels[index], windows[index]);
                }
            }
            finally
            {
                foreach (var model in createdModels) { model.Dispose(); }
            }
        }

        [TestMethod]
        public void RemovedWindowModelIsDisposedAfterCollectionNotificationFailure()
        {
            using var fixture = new RendererFixture();
            var node = fixture.CreateWindow(0);
            fixture.Update([node], []);
            var model = fixture.WindowModel(node);
            var window = Mock.Get(node.WindowReference);
            var failure = new InvalidOperationException("controlled window removal notification failure");
            NotifyCollectionChangedEventHandler changed = (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Remove)
                {
                    fixture.Renderer.InvalidateView();
                    ThrowInvalidationFailure(failure);
                }
            };
            fixture.ViewModel.WindowElements.CollectionChanged += changed;
            try
            {
                var actual = Assert.ThrowsException<InvalidOperationException>(() => fixture.Update([], []));
                Assert.AreSame(failure, actual);
                StringAssert.Contains(actual.StackTrace ?? string.Empty, nameof(ThrowInvalidationFailure));
            }
            finally
            {
                fixture.ViewModel.WindowElements.CollectionChanged -= changed;
            }

            Assert.AreEqual(0, fixture.Models.Count);
            Assert.AreEqual(0, fixture.ViewModel.WindowElements.Count);
            Assert.AreEqual(0, fixture.PreviousSnapshot.Count);
            fixture.Renderer.InvalidateView();
            fixture.Renderer.InvalidateView();
            try
            {
                Assert.AreEqual(0, fixture.CursorSubscriptions);
                Assert.IsNull(model.Node);
                Assert.IsNull(model.PrimaryActionCommand);
                Assert.IsNull(model.SecondaryActionCommand);
                Assert.IsNull(model.CloseCommand);
                window.VerifyRemove(value => value.Removed -= It.IsAny<EventHandler<WindowChangedEventArgs>>(), Times.Exactly(2));
                window.VerifyRemove(value => value.PositionChangeStart -= It.IsAny<EventHandler<WindowPositionChangedEventArgs>>(), Times.Once());
                window.VerifyRemove(value => value.PositionChangeEnd -= It.IsAny<EventHandler<WindowPositionChangedEventArgs>>(), Times.Once());
                window.VerifyRemove(value => value.TitleChanged -= It.IsAny<EventHandler<WindowTitleChangedEventArgs>>(), Times.Once());
            }
            finally
            {
                // Keep the intentionally failing R0 run isolated from fixture
                // cleanup; candidate disposal is idempotent.
                model.Dispose();
            }
        }

        [TestMethod]
        public void RemovedPanelModelIsDisposedAfterCollectionNotificationFailure()
        {
            using var fixture = new RendererFixture();
            fixture.PopulatePanelAndWindow();
            var panel = fixture.ViewModel.PanelElements.Single();
            var window = fixture.ViewModel.WindowElements.Single();
            var failure = new InvalidOperationException("controlled panel removal notification failure");
            NotifyCollectionChangedEventHandler changed = (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Remove)
                {
                    ThrowInvalidationFailure(failure);
                }
            };
            fixture.ViewModel.PanelElements.CollectionChanged += changed;
            try
            {
                var actual = Assert.ThrowsException<InvalidOperationException>(() => fixture.Update([], []));
                Assert.AreSame(failure, actual);
                StringAssert.Contains(actual.StackTrace ?? string.Empty, nameof(ThrowInvalidationFailure));
            }
            finally
            {
                fixture.ViewModel.PanelElements.CollectionChanged -= changed;
            }

            Assert.AreEqual(1, fixture.Models.Count, "The later window removal must not be reported as completed.");
            Assert.AreEqual(0, fixture.ViewModel.PanelElements.Count);
            Assert.AreEqual(1, fixture.ViewModel.WindowElements.Count);
            Assert.AreEqual(0, fixture.PreviousSnapshot.Count);
            fixture.Renderer.InvalidateView();
            try
            {
                Assert.IsNull(panel.PrimaryActionCommand);
                Assert.IsNull(panel.SecondaryActionCommand);
                Assert.IsNull(panel.CloseCommand);
                Assert.IsNull(window.Node);
                Assert.AreEqual(0, fixture.CursorSubscriptions);
                Assert.AreEqual(0, fixture.Models.Count);
                Assert.AreEqual(0, fixture.ViewModel.WindowElements.Count);
            }
            finally
            {
                panel.Dispose();
            }
        }

        [TestMethod]
        public void RemovalFailurePreservesPrimaryAndRecordsLaterModelDisposalFailure()
        {
            using var fixture = new RendererFixture();
            const string errorKey = "TilingOverlayRenderer.UpdateViewModelsExceptions";
            var node = fixture.CreateWindow(0);
            fixture.Update([node], []);
            fixture.WindowModel(node).Dispose();
            var disposalFailure = new InvalidOperationException("controlled detached model disposal failure");
            var model = new ThrowingAfterReleaseWindowViewModel(disposalFailure)
            {
                Node = node,
                PrimaryActionCommand = new DelegateCommand<TilingNodeViewModel>(_ => { }),
                SecondaryActionCommand = new DelegateCommand<TilingNodeViewModel>(_ => { }),
                CloseCommand = new DelegateCommand<TilingNodeViewModel>(_ => { }),
            };
            fixture.Models[node] = model;
            fixture.ViewModel.WindowElements[0] = model;
            var primary = new InvalidOperationException("controlled primary collection failure");
            var existing = new InvalidOperationException("existing supplemental failure");
            primary.Data[errorKey] = new AggregateException(existing);
            NotifyCollectionChangedEventHandler changed = (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Remove)
                {
                    ThrowInvalidationFailure(primary);
                }
            };
            fixture.ViewModel.WindowElements.CollectionChanged += changed;
            try
            {
                var actual = Assert.ThrowsException<InvalidOperationException>(() => fixture.Update([], []));
                Assert.AreSame(primary, actual);
                StringAssert.Contains(actual.StackTrace ?? string.Empty, nameof(ThrowInvalidationFailure));
                Assert.AreEqual(1, model.DisposeAttempts);
                var supplemental = (AggregateException)actual.Data[errorKey]!;
                CollectionAssert.AreEqual(
                    new Exception[] { existing, disposalFailure },
                    supplemental.InnerExceptions.ToArray());
                Assert.IsNull(model.Node);
                Assert.IsNull(model.PrimaryActionCommand);
                Assert.IsNull(model.SecondaryActionCommand);
                Assert.IsNull(model.CloseCommand);
                Assert.AreEqual(0, fixture.CursorSubscriptions);
            }
            finally
            {
                fixture.ViewModel.WindowElements.CollectionChanged -= changed;
                if (fixture.CursorSubscriptions != 0)
                {
                    try { model.Dispose(); }
                    catch (InvalidOperationException error) { Assert.AreSame(disposalFailure, error); }
                }
            }
        }

        [TestMethod]
        public void RepeatedRemovalNotificationFailuresDoNotAccumulateWindowOwners()
        {
            using var fixture = new RendererFixture();
            InvalidOperationException? currentFailure = null;
            NotifyCollectionChangedEventHandler changed = (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Remove && currentFailure != null)
                {
                    ThrowInvalidationFailure(currentFailure);
                }
            };
            fixture.ViewModel.WindowElements.CollectionChanged += changed;
            try
            {
                for (int cycle = 0; cycle < 100; cycle++)
                {
                    var node = fixture.CreateWindow(cycle);
                    fixture.Update([node], []);
                    var model = fixture.WindowModel(node);
                    currentFailure = new InvalidOperationException($"controlled removal failure {cycle}");
                    try
                    {
                        var actual = Assert.ThrowsException<InvalidOperationException>(() => fixture.Update([], []));
                        Assert.AreSame(currentFailure, actual);
                        Assert.AreEqual(0, fixture.CursorSubscriptions);
                        Assert.IsNull(model.Node);
                        Assert.AreEqual(0, fixture.Models.Count);
                        Assert.AreEqual(0, fixture.ViewModel.WindowElements.Count);
                        Assert.AreEqual(0, fixture.PreviousSnapshot.Count);
                    }
                    finally
                    {
                        model.Dispose();
                    }
                }
            }
            finally
            {
                currentFailure = null;
                fixture.ViewModel.WindowElements.CollectionChanged -= changed;
            }

            fixture.Renderer.InvalidateView();
            fixture.Renderer.InvalidateView();
            Assert.AreEqual(100, fixture.CursorAdds);
            Assert.AreEqual(0, fixture.CursorSubscriptions);
        }

        [TestMethod]
        public void RendererEnumeratesEachFocusPathOnceAndKeepsDirectFocusAndBounds()
        {
            using var fixture = new RendererFixture();
            var root = new SplitPanelNode();
            var tree = new DesktopTree { Root = root, WorkArea = fixture.DisplayBounds };
            var left = new SplitPanelNode();
            var right = new SplitPanelNode();
            root.Attach(left);
            root.Attach(right);
            var first = fixture.CreateWindow(0);
            var second = fixture.CreateWindow(1);
            left.Attach(first);
            right.Attach(second);
            tree.Measure();
            tree.Arrange();
            var snapshot = root.Nodes.ToArray();
            fixture.Update(snapshot, [first, left, root]);
            fixture.Update(snapshot, [first, left, root]);
            var focus = new CountingCollection<TilingNode>(new TilingNode[] { second, right, root });
            fixture.Update(snapshot, focus);
            Assert.AreEqual(1, focus.Enumerations);
            Assert.IsFalse(fixture.WindowModel(first).HasFocus);
            Assert.IsTrue(fixture.WindowModel(second).HasFocus);
            Assert.IsFalse(((TilingPanelViewModel)fixture.Models[left]).ChildHasDirectFocus);
            Assert.IsTrue(((TilingPanelViewModel)fixture.Models[right]).ChildHasDirectFocus);
            Assert.IsFalse(((TilingPanelViewModel)fixture.Models[root]).ChildHasDirectFocus);
            Assert.IsTrue(fixture.Models[right].HasFocus);
            Assert.IsTrue(fixture.Models[root].HasFocus);
            var actual = fixture.WindowModel(second).ComputedBounds;
            var expected = second.ComputedRectangle;
            Assert.AreEqual(new Rectangle(expected.Left - 100, expected.Top - 200,
                expected.Right - 100, expected.Bottom - 200), actual);
        }

        [TestMethod]
        public void RepeatedRendererReplacementAndInvalidationLeavesNoCursorSubscribers()
        {
            using var fixture = new RendererFixture();
            for (int cycle = 0; cycle < 100; cycle++)
            {
                var nodes = Enumerable.Range(cycle * 3, 3).Select(fixture.CreateWindow).ToArray();
                fixture.Update(nodes, [nodes[1]]);
                Assert.AreEqual(3, fixture.CursorSubscriptions);
                fixture.Update([nodes[2]], [nodes[2]]);
                Assert.AreEqual(1, fixture.CursorSubscriptions);
                fixture.Renderer.InvalidateView();
                Assert.AreEqual(0, fixture.CursorSubscriptions);
            }
        }

        [DataTestMethod]
        [DataRow("panel")]
        [DataRow("window")]
        [DataRow("first-model")]
        [DataRow("middle-model")]
        [DataRow("last-model")]
        public void InvalidateViewContinuesAfterEveryIndependentFailureAndRemainsReusable(string boundary)
        {
            using var fixture = new RendererFixture();
            var order = new List<string>();
            var failure = new InvalidOperationException($"controlled {boundary} failure");
            bool injectFailure = true;
            void Release(string owner)
            {
                order.Add(owner);
                if (owner == boundary && injectFailure) { ThrowInvalidationFailure(failure); }
            }
            var probes = AddInvalidationProbes(fixture, Release);
            var snapshot = fixture.PopulatePanelAndWindow();
            var panel = fixture.ViewModel.PanelElements.Single();
            var window = fixture.ViewModel.WindowElements.Single();
            NotifyCollectionChangedEventHandler panelChanged = (_, _) => Release("panel");
            NotifyCollectionChangedEventHandler windowChanged = (_, _) => Release("window");
            fixture.ViewModel.PanelElements.CollectionChanged += panelChanged;
            fixture.ViewModel.WindowElements.CollectionChanged += windowChanged;
            try
            {
                var actual = Assert.ThrowsException<InvalidOperationException>(fixture.Renderer.InvalidateView);

                Assert.AreSame(failure, actual);
                StringAssert.Contains(actual.StackTrace ?? string.Empty, nameof(ThrowInvalidationFailure));
                CollectionAssert.AreEqual(new[] { "panel", "window", "first-model", "middle-model", "last-model" }, order);
                AssertInvalidationReleasedOwners(fixture, probes, panel, window);
            }
            finally
            {
                injectFailure = false;
                fixture.ViewModel.PanelElements.CollectionChanged -= panelChanged;
                fixture.ViewModel.WindowElements.CollectionChanged -= windowChanged;
            }

            fixture.Update(snapshot, []);
            Assert.AreEqual(1, fixture.CursorSubscriptions, "The same snapshot must create a new window model after invalidation.");
            Assert.AreNotSame(window, fixture.ViewModel.WindowElements.Single());
            fixture.Renderer.InvalidateView();
            AssertInvalidationReleasedOwners(fixture, probes, panel, window);
        }

        [TestMethod]
        public void InvalidateViewPreservesFirstFailureAndRecordsLaterFailuresInReleaseOrder()
        {
            using var fixture = new RendererFixture();
            const string errorKey = "TilingOverlayRenderer.InvalidateViewExceptions";
            var order = new List<string>();
            var panelFailure = new InvalidOperationException("controlled panel reset failure");
            var existingFailure = new InvalidOperationException("existing supplemental failure");
            var windowFailure = new InvalidOperationException("controlled window reset failure");
            var firstModelFailure = new InvalidOperationException("controlled first model failure");
            var lastModelFailure = new InvalidOperationException("controlled last model failure");
            panelFailure.Data[errorKey] = new AggregateException(existingFailure);
            var failures = new Dictionary<string, Exception>
            {
                ["panel"] = panelFailure,
                ["window"] = windowFailure,
                ["first-model"] = firstModelFailure,
                ["last-model"] = lastModelFailure,
            };
            bool injectFailure = true;
            void Release(string owner)
            {
                order.Add(owner);
                if (injectFailure && failures.TryGetValue(owner, out var failure)) { ThrowInvalidationFailure(failure); }
            }
            var probes = AddInvalidationProbes(fixture, Release);
            fixture.PopulatePanelAndWindow();
            var panel = fixture.ViewModel.PanelElements.Single();
            var window = fixture.ViewModel.WindowElements.Single();
            NotifyCollectionChangedEventHandler panelChanged = (_, _) => Release("panel");
            NotifyCollectionChangedEventHandler windowChanged = (_, _) => Release("window");
            fixture.ViewModel.PanelElements.CollectionChanged += panelChanged;
            fixture.ViewModel.WindowElements.CollectionChanged += windowChanged;
            try
            {
                var actual = Assert.ThrowsException<InvalidOperationException>(fixture.Renderer.InvalidateView);

                Assert.AreSame(panelFailure, actual);
                StringAssert.Contains(actual.StackTrace ?? string.Empty, nameof(ThrowInvalidationFailure));
                var supplemental = (AggregateException)actual.Data[errorKey]!;
                CollectionAssert.AreEqual(new Exception[] { existingFailure, windowFailure, firstModelFailure, lastModelFailure },
                    supplemental.InnerExceptions.ToArray());
                CollectionAssert.AreEqual(new[] { "panel", "window", "first-model", "middle-model", "last-model" }, order);
                AssertInvalidationReleasedOwners(fixture, probes, panel, window);
            }
            finally
            {
                injectFailure = false;
                fixture.ViewModel.PanelElements.CollectionChanged -= panelChanged;
                fixture.ViewModel.WindowElements.CollectionChanged -= windowChanged;
            }
        }

        [DataTestMethod]
        [DataRow("panel")]
        [DataRow("first-model")]
        public void InvalidateViewSuppressesCallbackReentryAndAllowsLaterInvalidation(string boundary)
        {
            using var fixture = new RendererFixture();
            var order = new List<string>();
            bool reenter = true;
            void Release(string owner)
            {
                order.Add(owner);
                if (owner == boundary && reenter)
                {
                    reenter = false; // Keep the unfixed case bounded rather than overflowing the stack.
                    fixture.Renderer.InvalidateView();
                }
            }
            var probes = AddInvalidationProbes(fixture, Release);
            var snapshot = fixture.PopulatePanelAndWindow();
            var panel = fixture.ViewModel.PanelElements.Single();
            var window = fixture.ViewModel.WindowElements.Single();
            NotifyCollectionChangedEventHandler panelChanged = (_, _) => Release("panel");
            NotifyCollectionChangedEventHandler windowChanged = (_, _) => Release("window");
            fixture.ViewModel.PanelElements.CollectionChanged += panelChanged;
            fixture.ViewModel.WindowElements.CollectionChanged += windowChanged;
            try
            {
                fixture.Renderer.InvalidateView();

                Assert.IsFalse(reenter);
                CollectionAssert.AreEqual(new[] { "panel", "window", "first-model", "middle-model", "last-model" }, order);
                AssertInvalidationReleasedOwners(fixture, probes, panel, window);
            }
            finally
            {
                fixture.ViewModel.PanelElements.CollectionChanged -= panelChanged;
                fixture.ViewModel.WindowElements.CollectionChanged -= windowChanged;
            }

            fixture.Update(snapshot, []);
            Assert.AreEqual(1, fixture.CursorSubscriptions);
            Assert.AreNotSame(window, fixture.ViewModel.WindowElements.Single());
            fixture.Renderer.InvalidateView();
            AssertInvalidationReleasedOwners(fixture, probes, panel, window);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void PublicUpdateStopsAfterAddCallbackInvalidatesOrDisposesOwner(bool dispose)
        {
            using var fixture = new RendererFixture();
            var snapshot = new[] { fixture.CreateWindow(0), fixture.CreateWindow(1) };
            TilingWindowViewModel? publishedModel = null;
            int callbacks = 0;
            NotifyCollectionChangedEventHandler changed = (_, args) =>
            {
                if (args.Action != NotifyCollectionChangedAction.Add || callbacks != 0) { return; }
                callbacks++;
                publishedModel = (TilingWindowViewModel)args.NewItems![0]!;
                if (dispose) { fixture.DisposeManagedOwner(); }
                else { fixture.Renderer.InvalidateView(); }
            };
            fixture.ViewModel.WindowElements.CollectionChanged += changed;
            try { fixture.Renderer.UpdateOverlay(snapshot, []); }
            finally { fixture.ViewModel.WindowElements.CollectionChanged -= changed; }

            Assert.AreEqual(1, callbacks);
            Assert.IsNotNull(publishedModel);
            Assert.IsNull(publishedModel!.Node);
            Assert.IsNull(publishedModel.PrimaryActionCommand);
            Assert.AreEqual(1, fixture.CursorAdds, "The suspended update must not acquire a tail owner after cleanup returns.");
            Assert.AreEqual(0, fixture.CursorSubscriptions);
            Assert.AreEqual(0, fixture.Models.Count);
            Assert.AreEqual(0, fixture.PreviousSnapshot.Count);
            Assert.AreEqual(0, fixture.ViewModel.WindowElements.Count);

            fixture.Renderer.UpdateOverlay(snapshot, []);
            if (dispose)
            {
                fixture.DisposeManagedOwner();
                fixture.Renderer.InvalidateView();
                Assert.AreEqual(1, fixture.CursorAdds, "Disposed owners must reject later public updates.");
                Assert.AreEqual(0, fixture.CursorSubscriptions);
                Assert.AreEqual(0, fixture.Models.Count);
                Assert.AreEqual(0, fixture.ViewModel.WindowElements.Count);
            }
            else
            {
                Assert.AreEqual(3, fixture.CursorAdds);
                Assert.AreEqual(2, fixture.CursorSubscriptions);
                CollectionAssert.AreEqual(snapshot, fixture.ViewModel.WindowElements.Select(model => model.Node).ToArray());
                Assert.AreNotSame(publishedModel, fixture.WindowModel(snapshot[0]));
                fixture.Renderer.UpdateOverlay(snapshot, []);
                Assert.AreEqual(3, fixture.CursorAdds, "The next identical update must preserve both fresh model identities.");
            }
        }

        [TestMethod]
        public void InvalidateViewCapturesEveryModelBeforeCollectionCallbackMutatesDictionary()
        {
            using var fixture = new RendererFixture();
            var order = new List<string>();
            var probes = AddInvalidationProbes(fixture, order.Add);
            fixture.PopulatePanelAndWindow();
            var panel = fixture.ViewModel.PanelElements.Single();
            var window = fixture.ViewModel.WindowElements.Single();
            NotifyCollectionChangedEventHandler changed = (_, _) =>
            {
                order.Add("panel-clear");
                Assert.AreEqual(5, fixture.Models.Count);
                fixture.Models.Clear();
            };
            fixture.ViewModel.PanelElements.CollectionChanged += changed;
            try { fixture.Renderer.InvalidateView(); }
            finally { fixture.ViewModel.PanelElements.CollectionChanged -= changed; }

            CollectionAssert.AreEqual(new[] { "panel-clear", "first-model", "middle-model", "last-model" }, order);
            AssertInvalidationReleasedOwners(fixture, probes, panel, window);
        }

        [TestMethod]
        public void ResourceRefreshPreservesOwnerThreadReadWriteOrder()
        {
            using var fixture = new RendererFixture();
            Assert.IsTrue(fixture.ApplySettings(new Settings
            {
                PanelHeight = 31,
                WindowPadding = 7,
                PanelFontSize = 10
            }));
            fixture.SetScalingSequence(1, 2, 3, 4);
            var writes = new List<string>();
            fixture.ViewModel.PropertyChanged += (_, args) => writes.Add(args.PropertyName!);
            int posts = 0;

            fixture.RefreshResources(() => true, _ => posts++);

            Assert.AreEqual(0, posts);
            Assert.AreEqual(4, fixture.ScalingReads);
            CollectionAssert.AreEqual(new[]
            {
                nameof(TilingOverlayViewModel.DisplayScaling),
                nameof(TilingOverlayViewModel.FontSize),
                nameof(TilingOverlayViewModel.IconSize),
                nameof(TilingOverlayViewModel.TabWidth)
            }, writes);
            Assert.AreEqual(1d, fixture.ViewModel.DisplayScaling);
            Assert.AreEqual(20d, fixture.ViewModel.FontSize);
            Assert.AreEqual(30d, fixture.ViewModel.IconSize);
            Assert.AreEqual(175d * 4 * 10 / 12, fixture.ViewModel.TabWidth, 0.000000001);
        }

        [TestMethod]
        public void QueuedResourceRefreshUsesLatestScalingAndSettings()
        {
            using var fixture = new RendererFixture();
            bool hasAccess = false;
            Action? queued = null;
            fixture.SetScalingSequence(1, 1, 1, 1);
            fixture.RefreshResources(() => hasAccess, callback => queued = callback);
            Assert.IsNotNull(queued);
            Assert.AreEqual(0, fixture.ScalingReads);

            Assert.IsTrue(fixture.ApplySettings(new Settings
            {
                PanelHeight = 37,
                WindowPadding = 9,
                PanelFontSize = 18
            }));
            fixture.SetScalingSequence(2, 3, 4, 5);
            hasAccess = true;
            queued!();

            Assert.AreEqual(4, fixture.ScalingReads);
            Assert.AreEqual(37d, fixture.PanelHeight);
            Assert.AreEqual(9d, fixture.WindowPadding);
            Assert.AreEqual(18, fixture.PanelFontSize);
            Assert.AreEqual(2d, fixture.ViewModel.DisplayScaling);
            Assert.AreEqual(54d, fixture.ViewModel.FontSize);
            Assert.AreEqual(72d, fixture.ViewModel.IconSize);
            Assert.AreEqual(175d * 5 * 18 / 12, fixture.ViewModel.TabWidth, 0.000000001);
        }

        [TestMethod]
        public void QueuedResourceRefreshAfterDisposeSkipsAdaptersReadsAndWrites()
        {
            using var fixture = new RendererFixture();
            bool hasAccess = false;
            Action? queued = null;
            int accessChecks = 0;
            int posts = 0;
            fixture.SetScalingSequence(2, 3, 4, 5);
            fixture.RefreshResources(
                () => { accessChecks++; return hasAccess; },
                callback => { posts++; queued = callback; });
            Assert.IsNotNull(queued);
            Assert.AreEqual(1, accessChecks);
            Assert.AreEqual(1, posts);
            Assert.AreEqual(0, fixture.ScalingReads);

            fixture.DisposeManagedOwner();
            hasAccess = true;
            queued!();
            fixture.RefreshResources(
                () => { accessChecks++; return true; },
                _ => posts++);

            Assert.AreEqual(1, accessChecks, "A terminal owner must reject callbacks before consulting Dispatcher state.");
            Assert.AreEqual(1, posts);
            Assert.AreEqual(0, fixture.ScalingReads);
            AssertResourceValues(fixture, 0, 0, 0, 0);
        }

        [TestMethod]
        public void LateResourceCallbacksDoNotPostOrChangeSettings()
        {
            using var fixture = new RendererFixture();
            var initial = new Settings { PanelHeight = 29, WindowPadding = 6, PanelFontSize = 13 };
            var late = new Settings { PanelHeight = 41, WindowPadding = 11, PanelFontSize = 19 };
            Assert.IsTrue(fixture.ApplySettings(initial));
            fixture.DisposeManagedOwner();
            fixture.SetScalingSequence(2, 3, 4, 5);

            Assert.IsFalse(fixture.ApplySettings(late));
            fixture.RaiseSettingsChanged(late);
            fixture.RaiseScalingChanged();

            Assert.AreEqual(29d, fixture.PanelHeight);
            Assert.AreEqual(6d, fixture.WindowPadding);
            Assert.AreEqual(13, fixture.PanelFontSize);
            Assert.AreEqual(0, fixture.ScalingReads);
            AssertResourceValues(fixture, 0, 0, 0, 0);
        }

        [TestMethod]
        public void ActiveNullSettingsPreservesExistingFailure()
        {
            using var fixture = new RendererFixture();
            Assert.ThrowsException<NullReferenceException>(() => fixture.ApplySettings(null!));
        }

        [TestMethod]
        public void ResourcePostFailurePreservesExceptionAndAllowsNextRefresh()
        {
            using var fixture = new RendererFixture();
            var failure = new InvalidOperationException("controlled resource post failure");
            var actual = Assert.ThrowsException<InvalidOperationException>(() =>
                fixture.RefreshResources(() => false, _ => ThrowResourcePostFailure(failure)));
            Assert.AreSame(failure, actual);
            fixture.SetScalingSequence(2, 3, 4, 5);

            fixture.RefreshResources(() => true, _ => Assert.Fail("Owner-thread retry must not post."));

            Assert.AreEqual(4, fixture.ScalingReads);
            AssertResourceValues(fixture, 2, 36, 48, 875);
        }

        [TestMethod]
        public void ResourceAccessFailurePreservesExceptionAndAllowsNextRefresh()
        {
            using var fixture = new RendererFixture();
            var failure = new InvalidOperationException("controlled resource access failure");
            var actual = Assert.ThrowsException<InvalidOperationException>(() =>
                fixture.RefreshResources(
                    () => throw failure,
                    _ => Assert.Fail("A failed access probe must not post.")));
            Assert.AreSame(failure, actual);
            fixture.SetScalingSequence(2, 3, 4, 5);

            fixture.RefreshResources(() => true, _ => Assert.Fail("Owner-thread retry must not post."));

            Assert.AreEqual(4, fixture.ScalingReads);
            AssertResourceValues(fixture, 2, 36, 48, 875);
        }

        [TestMethod]
        public void ResourceWriteFailurePreservesExceptionAndAllowsNextRefresh()
        {
            using var fixture = new RendererFixture();
            var failure = new InvalidOperationException("controlled resource write failure");
            System.ComponentModel.PropertyChangedEventHandler handler = (_, args) =>
            {
                if (args.PropertyName == nameof(TilingOverlayViewModel.FontSize))
                {
                    ThrowResourcePostFailure(failure);
                }
            };
            fixture.ViewModel.PropertyChanged += handler;
            fixture.SetScalingSequence(1, 2, 3, 4);
            var actual = Assert.ThrowsException<InvalidOperationException>(() =>
                fixture.RefreshResources(() => true, _ => Assert.Fail("Owner-thread refresh must not post.")));
            fixture.ViewModel.PropertyChanged -= handler;
            Assert.AreSame(failure, actual);
            Assert.AreEqual(2, fixture.ScalingReads);
            AssertResourceValues(fixture, 1, 24, 0, 0);

            fixture.SetScalingSequence(5, 6, 7, 8);
            fixture.RefreshResources(() => true, _ => Assert.Fail("Owner-thread retry must not post."));
            Assert.AreEqual(4, fixture.ScalingReads);
            AssertResourceValues(fixture, 5, 72, 84, 1400);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void DisposeDuringAccessCheckSuppressesPostAndApply(bool reportsAccess)
        {
            using var fixture = new RendererFixture();
            int posts = 0;
            fixture.SetScalingSequence(2, 3, 4, 5);

            fixture.RefreshResources(
                () => { fixture.DisposeManagedOwner(); return reportsAccess; },
                _ => posts++);

            Assert.AreEqual(0, posts);
            Assert.AreEqual(0, fixture.ScalingReads);
            AssertResourceValues(fixture, 0, 0, 0, 0);
        }

        [DataTestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        public void DisposeDuringScalingReadPreventsCurrentAndRemainingWrites(int disposeReadIndex)
        {
            using var fixture = new RendererFixture();
            fixture.SetScalingSequence(1, 2, 3, 4);
            fixture.OnScalingRead = index =>
            {
                if (index == disposeReadIndex) { fixture.DisposeManagedOwner(); }
            };

            fixture.RefreshResources(() => true, _ => Assert.Fail("Owner-thread refresh must not post."));

            Assert.AreEqual(disposeReadIndex + 1, fixture.ScalingReads);
            AssertResourceValues(
                fixture,
                disposeReadIndex >= 1 ? 1 : 0,
                disposeReadIndex >= 2 ? 24 : 0,
                disposeReadIndex >= 3 ? 36 : 0,
                0);
        }

        [DataTestMethod]
        [DataRow(nameof(TilingOverlayViewModel.DisplayScaling), 1, 1d, 0d, 0d, 0d)]
        [DataRow(nameof(TilingOverlayViewModel.FontSize), 2, 1d, 24d, 0d, 0d)]
        [DataRow(nameof(TilingOverlayViewModel.IconSize), 3, 1d, 24d, 36d, 0d)]
        [DataRow(nameof(TilingOverlayViewModel.TabWidth), 4, 1d, 24d, 36d, 700d)]
        public void DisposeDuringResourceNotificationStopsRemainingReads(
            string propertyName,
            int expectedReads,
            double displayScaling,
            double fontSize,
            double iconSize,
            double tabWidth)
        {
            using var fixture = new RendererFixture();
            fixture.SetScalingSequence(1, 2, 3, 4);
            fixture.ViewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == propertyName) { fixture.DisposeManagedOwner(); }
            };

            fixture.RefreshResources(() => true, _ => Assert.Fail("Owner-thread refresh must not post."));

            Assert.AreEqual(expectedReads, fixture.ScalingReads);
            AssertResourceValues(fixture, displayScaling, fontSize, iconSize, tabWidth);
        }

        [TestMethod]
        public void InvalidateDuringResourceNotificationLeavesRefreshReusable()
        {
            using var fixture = new RendererFixture();
            bool invalidate = true;
            fixture.ViewModel.PropertyChanged += (_, args) =>
            {
                if (invalidate && args.PropertyName == nameof(TilingOverlayViewModel.DisplayScaling))
                {
                    invalidate = false;
                    fixture.Renderer.InvalidateView();
                }
            };
            fixture.SetScalingSequence(1, 2, 3, 4);
            fixture.RefreshResources(() => true, _ => Assert.Fail("Owner-thread refresh must not post."));
            Assert.AreEqual(4, fixture.ScalingReads);
            AssertResourceValues(fixture, 1, 24, 36, 700);

            fixture.SetScalingSequence(5, 6, 7, 8);
            fixture.RefreshResources(() => true, _ => Assert.Fail("Owner-thread refresh must not post."));
            Assert.AreEqual(4, fixture.ScalingReads);
            AssertResourceValues(fixture, 5, 72, 84, 1400);
        }

        [TestMethod]
        public void DisposeInsidePostRejectsAlreadyQueuedRefresh()
        {
            using var fixture = new RendererFixture();
            bool hasAccess = false;
            Action? queued = null;
            fixture.SetScalingSequence(2, 3, 4, 5);
            fixture.RefreshResources(
                () => hasAccess,
                callback =>
                {
                    queued = callback;
                    fixture.DisposeManagedOwner();
                });
            Assert.IsNotNull(queued);

            hasAccess = true;
            queued!();

            Assert.AreEqual(0, fixture.ScalingReads);
            AssertResourceValues(fixture, 0, 0, 0, 0);
        }

        [TestMethod]
        public void ConcurrentDisposeWhilePostingRejectsCapturedCallback()
        {
            using var fixture = new RendererFixture();
            using var barrier = new Barrier(2);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            bool hasAccess = false;
            Action? queued = null;
            fixture.SetScalingSequence(2, 3, 4, 5);
            var refresh = Task.Run(() => fixture.RefreshResources(
                () => hasAccess,
                callback =>
                {
                    queued = callback;
                    barrier.SignalAndWait(cancellation.Token);
                    barrier.SignalAndWait(cancellation.Token);
                }));

            try
            {
                barrier.SignalAndWait(cancellation.Token);
                fixture.DisposeManagedOwner();
                hasAccess = true;
                barrier.SignalAndWait(cancellation.Token);
                refresh.GetAwaiter().GetResult();
                Assert.IsNotNull(queued);

                queued!();

                Assert.AreEqual(0, fixture.ScalingReads);
                AssertResourceValues(fixture, 0, 0, 0, 0);
            }
            finally
            {
                cancellation.Cancel();
                try { refresh.GetAwaiter().GetResult(); }
                catch { /* Preserve an earlier assertion while still observing the worker. */ }
            }
        }

        [TestMethod]
        public void ResourceCallbackCounterScenario()
        {
            foreach (string scenario in new[] { "active", "queued-before-dispose", "captured-after-dispose" })
            {
                int lifetimes = 0;
                int posts = 0;
                int drainedCallbacks = 0;
                int scalingReads = 0;
                int resourceWrites = 0;
                int subscriptionReleases = 0;
                int pendingCallbacks = 0;

                for (int lifetime = 0; lifetime < 100; lifetime++)
                {
                    var viewModel = new CountingOverlayViewModel();
                    using var fixture = new RendererFixture(viewModel);
                    Assert.IsTrue(fixture.ApplySettings(new Settings
                    {
                        PanelHeight = 30,
                        WindowPadding = 8,
                        PanelFontSize = 16
                    }));
                    fixture.SetScalingSequence(1.5, 1.5, 1.5, 1.5);
                    bool hasAccess = false;
                    var queue = new Queue<Action>();

                    if (scenario == "captured-after-dispose")
                    {
                        fixture.DisposeManagedOwner();
                    }

                    fixture.RefreshResources(
                        () => hasAccess,
                        callback =>
                        {
                            posts++;
                            queue.Enqueue(callback);
                        });

                    if (scenario == "queued-before-dispose")
                    {
                        fixture.DisposeManagedOwner();
                    }

                    hasAccess = true;
                    while (queue.Count != 0)
                    {
                        drainedCallbacks++;
                        queue.Dequeue()();
                    }

                    if (scenario == "active")
                    {
                        fixture.DisposeManagedOwner();
                    }

                    Assert.AreEqual(1, fixture.SubscriptionReleases);
                    Assert.IsTrue(viewModel.ResourceWrites == 0 || viewModel.ResourceWrites == 4,
                        "A lifetime must either apply the complete resource tuple or reject it before the first write.");
                    Assert.AreEqual(viewModel.ResourceWrites, fixture.ScalingReads);
                    if (scenario == "active")
                    {
                        Assert.AreEqual(4, viewModel.ResourceWrites);
                    }
                    if (viewModel.ResourceWrites == 0)
                    {
                        AssertResourceValues(fixture, 0, 0, 0, 0);
                    }
                    else
                    {
                        AssertResourceValues(fixture, 1.5, 24, 24, 350);
                    }

                    lifetimes++;
                    scalingReads += fixture.ScalingReads;
                    resourceWrites += viewModel.ResourceWrites;
                    subscriptionReleases += fixture.SubscriptionReleases;
                    pendingCallbacks += queue.Count;
                }

                Console.WriteLine($"PERFCOUNTER overlay-resources-{scenario} lifetimes {lifetimes}");
                Console.WriteLine($"PERFCOUNTER overlay-resources-{scenario} posts {posts}");
                Console.WriteLine($"PERFCOUNTER overlay-resources-{scenario} drained-callbacks {drainedCallbacks}");
                Console.WriteLine($"PERFCOUNTER overlay-resources-{scenario} scaling-reads {scalingReads}");
                Console.WriteLine($"PERFCOUNTER overlay-resources-{scenario} resource-writes {resourceWrites}");
                Console.WriteLine($"PERFCOUNTER overlay-resources-{scenario} subscription-releases {subscriptionReleases}");
                Console.WriteLine($"PERFCOUNTER overlay-resources-{scenario} pending-callbacks {pendingCallbacks}");
            }
        }

        [TestMethod]
        public void OverlayCreationCounterScenario()
        {
            foreach (string scenario in new[] { "normal", "title-failure" })
            {
                using var fixture = new RendererFixture();
                var models = new List<TilingWindowViewModel>(100);
                var windows = new List<Mock<IWindow>>(100);
                int cycles = 0;
                int titleReads = 0;
                int publishedModels = 0;
                int primaryOutcomes = 0;
                int cursorSubscriptions = 0;
                int windowSubscriptions = 0;
                int nodeReferences = 0;
                int modelsNeedingFixtureCleanup = 0;
                try
                {
                    for (int cycle = 0; cycle < 100; cycle++)
                    {
                        var node = fixture.CreateWindow(cycle);
                        var window = Mock.Get(node.WindowReference);
                        windows.Add(window);
                        var captured = CaptureCreatedWindowModels(node);
                        string expectedTitle = $"fake-{cycle}";
                        var failure = scenario == "title-failure"
                            ? new InvalidOperationException($"controlled title creation failure {cycle}")
                            : null;
                        window.SetupGet(value => value.Title).Returns(() =>
                        {
                            titleReads++;
                            if (failure != null) { ThrowCreationFailure(failure); }
                            return expectedTitle;
                        });
                        try
                        {
                            if (failure != null)
                            {
                                var actual = Assert.ThrowsException<InvalidOperationException>(
                                    () => fixture.Renderer.UpdateOverlay([node], [node]));
                                Assert.AreSame(failure, actual);
                                StringAssert.Contains(actual.StackTrace ?? string.Empty, nameof(ThrowCreationFailure));
                                primaryOutcomes++;
                            }
                            else
                            {
                                fixture.Renderer.UpdateOverlay([node], [node]);
                                var model = fixture.WindowModel(node);
                                Assert.AreEqual(1, fixture.Models.Count);
                                Assert.AreSame(model, fixture.ViewModel.WindowElements.Single());
                                Assert.AreSame(node, model.Node);
                                Assert.AreEqual(expectedTitle, model.Title);
                                Assert.IsTrue(model.HasFocus);
                                publishedModels++;
                                fixture.Renderer.UpdateOverlay([], []);
                            }

                            Assert.AreEqual(1, captured.Count);
                            Assert.AreEqual(0, fixture.Models.Count);
                            Assert.AreEqual(0, fixture.ViewModel.WindowElements.Count);
                            Assert.AreEqual(0, fixture.ViewModel.PanelElements.Count);
                            fixture.Renderer.InvalidateView();
                            fixture.Renderer.InvalidateView();
                            Assert.AreEqual(0, fixture.PreviousSnapshot.Count);
                            cycles++;
                        }
                        finally { models.AddRange(captured); }
                    }

                    Assert.AreEqual(100, models.Count);
                    Assert.AreEqual(100, fixture.CursorAdds);
                    cursorSubscriptions = fixture.CursorSubscriptions;
                    windowSubscriptions = windows.Sum(WindowSubscriptionBalance);
                    nodeReferences = models.Count(model => model.Node != null);
                    modelsNeedingFixtureCleanup = nodeReferences;
                    Assert.IsTrue(cursorSubscriptions == 0 || cursorSubscriptions == 100,
                        "The common fixture observes complete R0 or candidate ownership after repeated invalidation.");
                    Assert.AreEqual(cursorSubscriptions, nodeReferences);
                    Assert.AreEqual(nodeReferences * 5, windowSubscriptions);
                    if (scenario == "normal") { Assert.AreEqual(0, cursorSubscriptions); }
                }
                finally
                {
                    // Keep at most100 models for inspection, then repair R0
                    // owners after the counters; candidate disposal is idempotent.
                    foreach (var model in models) { model.Dispose(); }
                }

                int remainingWindowSubscriptions = windows.Sum(WindowSubscriptionBalance);
                Assert.AreEqual(scenario == "normal" ? 200 : 100, titleReads);
                Assert.AreEqual(0, fixture.CursorSubscriptions);
                Assert.AreEqual(0, remainingWindowSubscriptions);
                Assert.IsTrue(models.All(model => model.Node == null));
                Console.WriteLine($"PERFCOUNTER overlay-creation-{scenario} cycles {cycles}");
                Console.WriteLine($"PERFCOUNTER overlay-creation-{scenario} title-reads {titleReads}");
                Console.WriteLine($"PERFCOUNTER overlay-creation-{scenario} created-models {models.Count}");
                Console.WriteLine($"PERFCOUNTER overlay-creation-{scenario} published-models {publishedModels}");
                Console.WriteLine($"PERFCOUNTER overlay-creation-{scenario} primary-outcomes {primaryOutcomes}");
                Console.WriteLine($"PERFCOUNTER overlay-creation-{scenario} cursor-subscriptions-after-invalidation {cursorSubscriptions}");
                Console.WriteLine($"PERFCOUNTER overlay-creation-{scenario} window-subscriptions-after-invalidation {windowSubscriptions}");
                Console.WriteLine($"PERFCOUNTER overlay-creation-{scenario} node-references-after-invalidation {nodeReferences}");
                Console.WriteLine($"PERFCOUNTER overlay-creation-{scenario} models-needing-fixture-cleanup {modelsNeedingFixtureCleanup}");
                Console.WriteLine($"PERFCOUNTER overlay-creation-{scenario} cursor-subscriptions-after-fixture-cleanup {fixture.CursorSubscriptions}");
                Console.WriteLine($"PERFCOUNTER overlay-creation-{scenario} window-subscriptions-after-fixture-cleanup {remainingWindowSubscriptions}");
            }

            static int WindowSubscriptionBalance(Mock<IWindow> window)
            {
                int balance = 0;
                foreach (string eventName in new[] { "Removed", "PositionChangeStart", "PositionChangeEnd", "TitleChanged" })
                {
                    int adds = window.Invocations.Count(invocation => invocation.Method.Name == "add_" + eventName);
                    int removes = window.Invocations.Count(invocation => invocation.Method.Name == "remove_" + eventName);
                    int expectedAdds = eventName == "Removed" ? 2 : 1;
                    Assert.AreEqual(expectedAdds, adds, $"Each created window must acquire {eventName} handlers exactly once per owner.");
                    Assert.IsTrue(removes == 0 || removes == expectedAdds,
                        $"Unexpected partial or repeated release of {eventName} handlers.");
                    balance += adds - removes;
                }
                return balance;
            }
        }

        [TestMethod]
        public void OverlayRemovalCounterScenario()
        {
            foreach (string scenario in new[] { "normal", "notification-failure" })
            {
                using var fixture = new RendererFixture();
                var models = new List<TilingWindowViewModel>(100);
                InvalidOperationException? currentFailure = null;
                int cycles = 0;
                int removalNotifications = 0;
                int primaryOutcomes = 0;
                int detachedModels = 0;
                int cursorSubscriptions = 0;
                int nodeReferences = 0;
                int commandReferences = 0;
                int modelsNeedingFixtureCleanup = 0;
                NotifyCollectionChangedEventHandler changed = (_, args) =>
                {
                    if (args.Action == NotifyCollectionChangedAction.Remove)
                    {
                        removalNotifications++;
                        if (currentFailure != null) { ThrowInvalidationFailure(currentFailure); }
                    }
                };
                fixture.ViewModel.WindowElements.CollectionChanged += changed;
                try
                {
                    for (int cycle = 0; cycle < 100; cycle++)
                    {
                        var node = fixture.CreateWindow(cycle);
                        fixture.Update([node], []);
                        models.Add(fixture.WindowModel(node));
                        if (scenario == "notification-failure")
                        {
                            currentFailure = new InvalidOperationException($"controlled removal failure {cycle}");
                            var actual = Assert.ThrowsException<InvalidOperationException>(() => fixture.Update([], []));
                            Assert.AreSame(currentFailure, actual);
                            StringAssert.Contains(actual.StackTrace ?? string.Empty, nameof(ThrowInvalidationFailure));
                            primaryOutcomes++;
                        }
                        else
                        {
                            fixture.Update([], []);
                        }

                        Assert.AreEqual(0, fixture.Models.Count);
                        Assert.AreEqual(0, fixture.ViewModel.WindowElements.Count);
                        Assert.AreEqual(0, fixture.PreviousSnapshot.Count);
                        detachedModels++;
                        cycles++;
                    }

                    fixture.Renderer.InvalidateView();
                    fixture.Renderer.InvalidateView();
                    Assert.AreEqual(100, fixture.CursorAdds);
                    cursorSubscriptions = fixture.CursorSubscriptions;
                    foreach (var model in models)
                    {
                        if (model.Node != null) { nodeReferences++; }
                        int commands = (model.PrimaryActionCommand != null ? 1 : 0)
                            + (model.SecondaryActionCommand != null ? 1 : 0)
                            + (model.CloseCommand != null ? 1 : 0);
                        commandReferences += commands;
                        if (model.Node != null || commands != 0) { modelsNeedingFixtureCleanup++; }
                    }

                    Assert.IsTrue(cursorSubscriptions == 0 || cursorSubscriptions == 100,
                        "The common fixture observes complete R0 or candidate cleanup, without partial owner release.");
                    Assert.AreEqual(cursorSubscriptions, nodeReferences);
                    Assert.AreEqual(nodeReferences * 3, commandReferences);
                    Assert.AreEqual(nodeReferences, modelsNeedingFixtureCleanup);
                    if (scenario == "normal") { Assert.AreEqual(0, cursorSubscriptions); }
                }
                finally
                {
                    currentFailure = null;
                    fixture.ViewModel.WindowElements.CollectionChanged -= changed;
                    // The bounded list deliberately retains models for inspection.
                    // Repair the observed R0 owners only after taking the counters;
                    // candidate Dispose remains idempotent.
                    foreach (var model in models) { model.Dispose(); }
                }

                Assert.AreEqual(0, fixture.CursorSubscriptions);
                Assert.IsTrue(models.All(model => model.Node == null
                    && model.PrimaryActionCommand == null
                    && model.SecondaryActionCommand == null
                    && model.CloseCommand == null));
                Console.WriteLine($"PERFCOUNTER overlay-removal-{scenario} cycles {cycles}");
                Console.WriteLine($"PERFCOUNTER overlay-removal-{scenario} removal-notifications {removalNotifications}");
                Console.WriteLine($"PERFCOUNTER overlay-removal-{scenario} primary-outcomes {primaryOutcomes}");
                Console.WriteLine($"PERFCOUNTER overlay-removal-{scenario} detached-models {detachedModels}");
                Console.WriteLine($"PERFCOUNTER overlay-removal-{scenario} cursor-subscriptions-after-invalidation {cursorSubscriptions}");
                Console.WriteLine($"PERFCOUNTER overlay-removal-{scenario} node-references-after-invalidation {nodeReferences}");
                Console.WriteLine($"PERFCOUNTER overlay-removal-{scenario} command-references-after-invalidation {commandReferences}");
                Console.WriteLine($"PERFCOUNTER overlay-removal-{scenario} models-needing-fixture-cleanup {modelsNeedingFixtureCleanup}");
                Console.WriteLine($"PERFCOUNTER overlay-removal-{scenario} cursor-subscriptions-after-fixture-cleanup {fixture.CursorSubscriptions}");
            }
        }

        [TestMethod]
        public void OverlayRecoveryCounterScenario()
        {
            foreach (string scenario in new[] { "normal", "removal-failure" })
            {
                using var fixture = new RendererFixture();
                int cycles = 0;
                int primaryOutcomes = 0;
                int correctRequestedSnapshotCycles = 0;
                int cursorSubscriptionsAfterRetry = 0;
                int windowSubscriptionsAfterRetry = 0;
                int staleNodeReferencesAfterRetry = 0;
                int staleWindowSubscriptionsAfterRetry = 0;
                int cursorSubscriptionsAfterCleanup = 0;
                int windowSubscriptionsAfterCleanup = 0;
                for (int cycle = 0; cycle < 100; cycle++)
                {
                    var nodes = Enumerable.Range(cycle * 4, 4).Select(fixture.CreateWindow).ToArray();
                    var windows = nodes.Select(node => Mock.Get(node.WindowReference)).ToArray();
                    var createdModels = nodes.Select(CaptureCreatedWindowModels).ToArray();
                    TilingNode[] initialSnapshot = [nodes[0], nodes[1], nodes[2], nodes[3]];
                    TilingNode[] requestedSnapshot = [nodes[2], nodes[3]];
                    TilingNode[] focusedPath = [nodes[3]];
                    var preview = new HashSet<IWindow> { nodes[2].WindowReference };
                    InvalidOperationException? currentFailure = null;
                    NotifyCollectionChangedEventHandler changed = (_, args) =>
                    {
                        if (args.Action == NotifyCollectionChangedAction.Remove && currentFailure != null)
                        {
                            ThrowInvalidationFailure(currentFailure);
                        }
                    };
                    fixture.ViewModel.WindowElements.CollectionChanged += changed;
                    try
                    {
                        fixture.Renderer.UpdateOverlay(initialSnapshot, focusedPath);
                        fixture.Renderer.PreviewWindows = preview;
                        CollectionAssert.AreEqual(initialSnapshot, fixture.ViewModel.WindowElements.Select(model => model.Node).ToArray());
                        Assert.AreEqual(4, fixture.CursorSubscriptions);
                        Assert.AreEqual(20, windows.Sum(WindowSubscriptionBalance));
                        if (scenario == "removal-failure")
                        {
                            currentFailure = new InvalidOperationException($"controlled same-snapshot removal failure {cycle}");
                            var actual = Assert.ThrowsException<InvalidOperationException>(
                                () => fixture.Renderer.UpdateOverlay(requestedSnapshot, focusedPath));
                            Assert.AreSame(currentFailure, actual);
                            StringAssert.Contains(actual.StackTrace ?? string.Empty, nameof(ThrowInvalidationFailure));
                            primaryOutcomes++;
                            currentFailure = null;
                        }
                        else
                        {
                            fixture.Renderer.UpdateOverlay(requestedSnapshot, focusedPath);
                        }

                        fixture.Renderer.UpdateOverlay(requestedSnapshot, focusedPath);

                        var observedModels = fixture.ViewModel.WindowElements.ToArray();
                        var keepModels = new[] { fixture.WindowModel(nodes[2]), fixture.WindowModel(nodes[3]) };
                        Assert.AreSame(nodes[2], keepModels[0].Node);
                        Assert.AreSame(nodes[3], keepModels[1].Node);
                        Assert.IsFalse(keepModels[0].HasFocus);
                        Assert.IsTrue(keepModels[1].HasFocus);
                        Assert.IsTrue(keepModels[0].IsPreviewVisible);
                        Assert.IsFalse(keepModels[1].IsPreviewVisible);
                        Assert.AreSame(preview, fixture.Renderer.PreviewWindows);
                        bool correctSnapshot = fixture.Models.Count == requestedSnapshot.Length
                            && observedModels.SequenceEqual(keepModels)
                            && fixture.PreviousSnapshot.SequenceEqual(requestedSnapshot);
                        if (correctSnapshot) { correctRequestedSnapshotCycles++; }
                        if (scenario == "normal") { Assert.IsTrue(correctSnapshot); }

                        int activeWindows = windows.Sum(WindowSubscriptionBalance);
                        int staleNodes = createdModels.SelectMany(models => models)
                            .Count(model => model.Node != null && !requestedSnapshot.Contains(model.Node));
                        int staleWindows = windows.Take(2).Sum(WindowSubscriptionBalance);
                        Assert.IsTrue(fixture.CursorSubscriptions == 2 || fixture.CursorSubscriptions == 3,
                            "The common fixture accepts the complete requested snapshot or the single observed R0 remainder.");
                        Assert.AreEqual(fixture.CursorSubscriptions * 5, activeWindows);
                        Assert.AreEqual(fixture.CursorSubscriptions - 2, staleNodes);
                        Assert.AreEqual(staleNodes * 5, staleWindows);
                        Assert.AreEqual(correctSnapshot, staleNodes == 0);
                        cursorSubscriptionsAfterRetry += fixture.CursorSubscriptions;
                        windowSubscriptionsAfterRetry += activeWindows;
                        staleNodeReferencesAfterRetry += staleNodes;
                        staleWindowSubscriptionsAfterRetry += staleWindows;

                        int addsBeforeStableUpdate = fixture.CursorAdds;
                        fixture.Renderer.UpdateOverlay(requestedSnapshot, focusedPath);
                        fixture.Renderer.PreviewWindows = preview;
                        CollectionAssert.AreEqual(observedModels, fixture.ViewModel.WindowElements.ToArray());
                        Assert.AreSame(keepModels[0], fixture.WindowModel(nodes[2]));
                        Assert.AreSame(keepModels[1], fixture.WindowModel(nodes[3]));
                        Assert.AreEqual(addsBeforeStableUpdate, fixture.CursorAdds);
                        cycles++;
                    }
                    finally
                    {
                        currentFailure = null;
                        fixture.ViewModel.WindowElements.CollectionChanged -= changed;
                        // Inspect R0's retained removal before cleaning this cycle;
                        // keep captured owners bounded and isolate every repetition.
                        fixture.Renderer.InvalidateView();
                        foreach (var model in createdModels.SelectMany(models => models)) { model.Dispose(); }
                    }

                    int remainingWindows = windows.Sum(WindowSubscriptionBalance);
                    Assert.AreEqual(0, fixture.CursorSubscriptions);
                    Assert.AreEqual(0, remainingWindows);
                    Assert.AreEqual(0, fixture.Models.Count);
                    Assert.AreEqual(0, fixture.ViewModel.WindowElements.Count);
                    Assert.AreEqual(0, fixture.PreviousSnapshot.Count);
                    Assert.IsTrue(createdModels.SelectMany(models => models).All(model => model.Node == null));
                    cursorSubscriptionsAfterCleanup += fixture.CursorSubscriptions;
                    windowSubscriptionsAfterCleanup += remainingWindows;
                }

                Console.WriteLine($"PERFCOUNTER overlay-recovery-{scenario} cycles {cycles}");
                Console.WriteLine($"PERFCOUNTER overlay-recovery-{scenario} primary-outcomes {primaryOutcomes}");
                Console.WriteLine($"PERFCOUNTER overlay-recovery-{scenario} correct-requested-snapshot-cycles {correctRequestedSnapshotCycles}");
                Console.WriteLine($"PERFCOUNTER overlay-recovery-{scenario} cursor-subscriptions-after-retry {cursorSubscriptionsAfterRetry}");
                Console.WriteLine($"PERFCOUNTER overlay-recovery-{scenario} window-subscriptions-after-retry {windowSubscriptionsAfterRetry}");
                Console.WriteLine($"PERFCOUNTER overlay-recovery-{scenario} stale-node-references-after-retry {staleNodeReferencesAfterRetry}");
                Console.WriteLine($"PERFCOUNTER overlay-recovery-{scenario} stale-window-subscriptions-after-retry {staleWindowSubscriptionsAfterRetry}");
                Console.WriteLine($"PERFCOUNTER overlay-recovery-{scenario} cursor-subscriptions-after-cleanup {cursorSubscriptionsAfterCleanup}");
                Console.WriteLine($"PERFCOUNTER overlay-recovery-{scenario} window-subscriptions-after-cleanup {windowSubscriptionsAfterCleanup}");
            }

            static int WindowSubscriptionBalance(Mock<IWindow> window)
            {
                int balance = 0;
                int? activeOwners = null;
                foreach (string eventName in new[] { "Removed", "PositionChangeStart", "PositionChangeEnd", "TitleChanged" })
                {
                    int adds = window.Invocations.Count(invocation => invocation.Method.Name == "add_" + eventName);
                    int removes = window.Invocations.Count(invocation => invocation.Method.Name == "remove_" + eventName);
                    int handlersPerOwner = eventName == "Removed" ? 2 : 1;
                    Assert.AreEqual(0, adds % handlersPerOwner);
                    Assert.AreEqual(0, removes % handlersPerOwner);
                    int owners = (adds - removes) / handlersPerOwner;
                    Assert.IsTrue(owners == 0 || owners == 1, $"Unexpected partial, repeated or duplicate ownership of {eventName} handlers.");
                    if (activeOwners.HasValue) { Assert.AreEqual(activeOwners.Value, owners); }
                    activeOwners = owners;
                    balance += adds - removes;
                }
                return balance;
            }
        }

        [TestMethod]
        public void OverlayUpdatesCounterScenario()
        {
            foreach (int count in new[] { 1, 10, 25, 50 })
            {
                using var fixture = new RendererFixture();
                var nodes = Enumerable.Range(0, count).Select(fixture.CreateWindow).ToArray();
                var snapshot = new CountingCollection<TilingNode>(nodes);
                var focus = new CountingCollection<TilingNode>(nodes.Reverse().ToArray());
                var firstPreview = nodes.Select(node => node.WindowReference).ToHashSet();
                var secondPreview = new HashSet<IWindow>(firstPreview);
                for (int warmup = 0; warmup < 20; warmup++)
                {
                    fixture.Update(snapshot, focus);
                    fixture.Renderer.PreviewWindows = warmup % 2 == 0 ? firstPreview : secondPreview;
                }

                var models = fixture.ViewModel.WindowElements.ToArray();
                int previewNotifications = 0;
                foreach (var vm in models)
                {
                    vm.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(TilingWindowViewModel.IsPreviewVisible)) { previewNotifications++; }
                    };
                }
                snapshot.Reset();
                focus.Reset();
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                for (int update = 0; update < 100; update++)
                {
                    fixture.Update(snapshot, focus);
                    fixture.Renderer.PreviewWindows = update % 2 == 0 ? firstPreview : secondPreview;
                }
                long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

                CollectionAssert.AreEqual(models, fixture.ViewModel.WindowElements.ToArray());
                CollectionAssert.AreEqual(nodes, fixture.ViewModel.WindowElements.Select(vm => vm.Node).ToArray());
                Assert.IsTrue(models.All(vm => vm.HasFocus && vm.IsPreviewVisible));
                Console.WriteLine($"PERFCOUNTER overlay-updates-{count} snapshot-enumerations {snapshot.Enumerations}");
                Console.WriteLine($"PERFCOUNTER overlay-updates-{count} focus-enumerations {focus.Enumerations}");
                Console.WriteLine($"PERFCOUNTER overlay-updates-{count} preview-notifications {previewNotifications}");
                Console.WriteLine($"PERFCOUNTER overlay-updates-{count} allocated-bytes {allocated}");
                Console.WriteLine($"PERFCOUNTER overlay-updates-{count} windows {count}");
                Console.WriteLine($"PERFCOUNTER overlay-updates-{count} updates 100");
            }
        }

        private static List<TilingWindowViewModel> CaptureCreatedWindowModels(WindowNode node)
        {
            var models = new List<TilingWindowViewModel>();
            Mock.Get(node.WindowReference)
                .SetupAdd(window => window.Removed += It.IsAny<EventHandler<WindowChangedEventArgs>>())
                .Callback<EventHandler<WindowChangedEventArgs>>(handler =>
                {
                    var model = (TilingWindowViewModel)handler.Target!;
                    if (!models.Contains(model)) { models.Add(model); }
                });
            return models;
        }

        private static void AssertReleasedCreatedWindow(TilingWindowViewModel model, Mock<IWindow> window)
        {
            Assert.IsNull(model.Node);
            Assert.IsNull(model.PrimaryActionCommand);
            Assert.IsNull(model.SecondaryActionCommand);
            Assert.IsNull(model.CloseCommand);
            window.VerifyRemove(value => value.Removed -= It.IsAny<EventHandler<WindowChangedEventArgs>>(), Times.Exactly(2));
            window.VerifyRemove(value => value.PositionChangeStart -= It.IsAny<EventHandler<WindowPositionChangedEventArgs>>(), Times.Once());
            window.VerifyRemove(value => value.PositionChangeEnd -= It.IsAny<EventHandler<WindowPositionChangedEventArgs>>(), Times.Once());
            window.VerifyRemove(value => value.TitleChanged -= It.IsAny<EventHandler<WindowTitleChangedEventArgs>>(), Times.Once());
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowCreationFailure(Exception failure) => throw failure;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowInvalidationFailure(Exception failure) => throw failure;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowResourcePostFailure(Exception failure) => throw failure;

        private static void AssertResourceValues(
            RendererFixture fixture,
            double displayScaling,
            double fontSize,
            double iconSize,
            double tabWidth)
        {
            Assert.AreEqual(displayScaling, fixture.ViewModel.DisplayScaling, 0.000000001);
            Assert.AreEqual(fontSize, fixture.ViewModel.FontSize, 0.000000001);
            Assert.AreEqual(iconSize, fixture.ViewModel.IconSize, 0.000000001);
            Assert.AreEqual(tabWidth, fixture.ViewModel.TabWidth, 0.000000001);
        }

        private static RecordingNodeViewModel[] AddInvalidationProbes(RendererFixture fixture, Action<string> release)
        {
            return new[] { "first-model", "middle-model", "last-model" }.Select(owner =>
            {
                var model = new RecordingNodeViewModel(() => release(owner));
                fixture.Models.Add(new PlaceholderNode(), model);
                return model;
            }).ToArray();
        }

        private static void AssertInvalidationReleasedOwners(RendererFixture fixture,
            RecordingNodeViewModel[] probes, TilingPanelViewModel panel, TilingWindowViewModel window)
        {
            foreach (var probe in probes) { Assert.AreEqual(1, probe.DisposeAttempts); }
            Assert.IsNull(panel.PrimaryActionCommand);
            Assert.IsNull(window.Node);
            Assert.IsNull(window.PrimaryActionCommand);
            Assert.AreEqual(0, fixture.CursorSubscriptions);
            Assert.AreEqual(0, fixture.ViewModel.PanelElements.Count);
            Assert.AreEqual(0, fixture.ViewModel.WindowElements.Count);
            Assert.AreEqual(0, fixture.Models.Count);
            Assert.AreEqual(0, fixture.PreviousSnapshot.Count);
        }

        private sealed class RecordingNodeViewModel(Action dispose) : TilingNodeViewModel
        {
            public int DisposeAttempts { get; private set; }
            public override void Dispose()
            {
                DisposeAttempts++;
                base.Dispose();
                dispose();
            }
        }

        private sealed class ThrowingAfterReleaseWindowViewModel(Exception failure) : TilingWindowViewModel
        {
            public int DisposeAttempts { get; private set; }

            public override void Dispose()
            {
                DisposeAttempts++;
                base.Dispose();
                ThrowInvalidationFailure(failure);
            }
        }

        private sealed class FailBeforeMutationCollection<T> : ObservableCollection<T>
        {
            public Exception? NextInsertFailure { get; set; }
            public Exception? NextRemoveFailure { get; set; }

            protected override void InsertItem(int index, T item)
            {
                var failure = NextInsertFailure;
                NextInsertFailure = null;
                if (failure != null) { ThrowCreationFailure(failure); }
                base.InsertItem(index, item);
            }

            protected override void RemoveItem(int index)
            {
                var failure = NextRemoveFailure;
                NextRemoveFailure = null;
                if (failure != null) { ThrowCreationFailure(failure); }
                base.RemoveItem(index);
            }
        }

        private sealed class CountingCollection<T>(IReadOnlyCollection<T> values) : IReadOnlyCollection<T>
        {
            public int Count => values.Count;
            public int Enumerations { get; private set; }
            public void Reset() => Enumerations = 0;
            public IEnumerator<T> GetEnumerator() { Enumerations++; return values.GetEnumerator(); }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private sealed class CountingOverlayViewModel : TilingOverlayViewModel
        {
            public int ResourceWrites { get; private set; }

            protected override void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
            {
                if (propertyName == nameof(DisplayScaling)
                    || propertyName == nameof(FontSize)
                    || propertyName == nameof(IconSize)
                    || propertyName == nameof(TabWidth))
                {
                    ResourceWrites++;
                }
                base.SetField(ref field, value, propertyName);
            }
        }

        internal sealed class RendererFixture : IDisposable
        {
            public TilingOverlayRenderer Renderer { get; }
            public TilingOverlayViewModel ViewModel { get; }
            public Dictionary<TilingNode, TilingNodeViewModel> Models { get; } = [];
            public Rectangle DisplayBounds { get; } = new(100, 200, 2100, 1400);
            public int CursorSubscriptions { get; private set; }
            public int CursorAdds { get; private set; }
            public int ScalingReads { get; private set; }
            public int SubscriptionReleases { get; private set; }
            public Action<int>? OnScalingRead { get; set; }
            public Action? OnBoundsRead { get; set; }
            public Action? OnCursorRemove { get; set; }
            public double PanelHeight => GetField<double>("m_panelHeight");
            public double WindowPadding => GetField<double>("m_windowPadding");
            public int PanelFontSize => GetField<int>("m_panelFontSize");
            public IReadOnlyCollection<TilingNode> PreviousSnapshot => (IReadOnlyCollection<TilingNode>)typeof(TilingOverlayRenderer)
                .GetField("m_previousSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Renderer)!;
            private readonly Mock<IWorkspace> m_workspace = new();
            private readonly Mock<IDisplay> m_display = new();
            private readonly Action<IReadOnlyCollection<TilingNode>, IReadOnlyCollection<TilingNode>> m_update;
            private Queue<double> m_scalingValues = new();
            private double m_fallbackScaling = 1;

            public RendererFixture(TilingOverlayViewModel? viewModel = null)
            {
                ViewModel = viewModel ?? new TilingOverlayViewModel();
                m_workspace.SetupAdd(workspace => workspace.CursorLocationChanged += It.IsAny<EventHandler<CursorLocationChangedEventArgs>>())
                    .Callback(() => { CursorSubscriptions++; CursorAdds++; });
                m_workspace.SetupRemove(workspace => workspace.CursorLocationChanged -= It.IsAny<EventHandler<CursorLocationChangedEventArgs>>())
                    .Callback(() =>
                    {
                        CursorSubscriptions--;
                        OnCursorRemove?.Invoke();
                    });
                m_display.SetupGet(value => value.Bounds).Returns(() =>
                {
                    OnBoundsRead?.Invoke();
                    return DisplayBounds;
                });
                m_display.SetupGet(value => value.Scaling).Returns(() =>
                {
                    int index = ScalingReads++;
                    OnScalingRead?.Invoke(index);
                    return m_scalingValues.Count == 0 ? m_fallbackScaling : m_scalingValues.Dequeue();
                });
                // The public constructor shows native overlays. Initialize only
                // existing managed fields; invoke the real production methods.
                Renderer = (TilingOverlayRenderer)RuntimeHelpers.GetUninitializedObject(typeof(TilingOverlayRenderer));
                SetField("m_display", m_display.Object);
                SetField("m_viewModel", ViewModel);
                var disposables = new CompositeDisposable();
                if (ViewModel is CountingOverlayViewModel)
                {
                    disposables.Add(Disposable.Create(() => SubscriptionReleases++));
                }
                SetField("m_disposables", disposables);
                // The public managed update path must never create native views.
                // This fixture represents an already initialized overlay surface.
                SetField("m_isOverlayInit", true);
                SetField("m_nodeViewModels", Models);
                SetField("m_previousSnapshot", Array.Empty<TilingNode>());
                SetField("m_previewWindows", new HashSet<IWindow>());
                SetField("m_panelItemPrimaryActionCommand", new DelegateCommand<TilingNodeViewModel>(_ => { }));
                SetField("m_panelItemSecondaryActionCommand", new DelegateCommand<TilingNodeViewModel>(_ => { }));
                SetField("m_panelItemCloseActionCommand", new DelegateCommand<TilingNodeViewModel>(_ => { }));
                SetField("m_panelHeight", 22d);
                SetField("m_windowPadding", 4d);
                SetField("m_panelFontSize", 12);
                m_update = typeof(TilingOverlayRenderer).GetMethod("UpdateViewModels", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .CreateDelegate<Action<IReadOnlyCollection<TilingNode>, IReadOnlyCollection<TilingNode>>>(Renderer);
            }

            public WindowNode CreateWindow(int index)
            {
                var window = new Mock<IWindow>();
                window.SetupGet(value => value.Workspace).Returns(m_workspace.Object);
                window.SetupGet(value => value.Title).Returns($"fake-{index}");
                window.Setup(value => value.Equals(It.IsAny<IWindow>()))
                    .Returns<IWindow>(other => ReferenceEquals(window.Object, other));
                return new WindowNode(window.Object);
            }

            public TilingNode[] PopulatePanelAndWindow()
            {
                var root = new SplitPanelNode();
                var tree = new DesktopTree { Root = root, WorkArea = DisplayBounds };
                root.Attach(CreateWindow(0));
                tree.Measure();
                tree.Arrange();
                var snapshot = root.Nodes.ToArray();
                Update(snapshot, []);
                Assert.AreEqual(1, ViewModel.PanelElements.Count);
                Assert.AreEqual(1, ViewModel.WindowElements.Count);
                Assert.IsNotNull(ViewModel.PanelElements.Single().PrimaryActionCommand);
                Assert.AreEqual(snapshot.Length, PreviousSnapshot.Count);
                return snapshot;
            }

            public TilingWindowViewModel WindowModel(WindowNode node) => (TilingWindowViewModel)Models[node];
            public void Update(IReadOnlyCollection<TilingNode> snapshot, IReadOnlyCollection<TilingNode> focusedPath) => m_update(snapshot, focusedPath);
            public void RefreshResources(Func<bool> checkAccess, Action<Action> post) => Renderer.UpdateResourcesCore(checkAccess, post);
            public bool ApplySettings(Settings settings) => Renderer.ApplySettingsIfActive(settings);
            public void SetScalingSequence(params double[] values)
            {
                m_scalingValues = new Queue<double>(values);
                if (values.Length != 0) { m_fallbackScaling = values[^1]; }
                ScalingReads = 0;
            }
            public void RaiseSettingsChanged(Settings settings) => typeof(TilingOverlayRenderer)
                .GetMethod("OnSettingsChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Renderer, new object?[] { settings });
            public void RaiseScalingChanged() => typeof(TilingOverlayRenderer)
                .GetMethod("OnDisplayScalingChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Renderer, new object?[]
                {
                    m_display.Object,
                    new DisplayScalingChangedEventArgs(m_display.Object, m_fallbackScaling, 1)
                });
            public void DisposeManagedOwner() => Renderer.DisposeCore(() => { }, () => { }, () => { });
            public void Dispose() { Renderer.InvalidateView(); ViewModel.Dispose(); Assert.AreEqual(0, CursorSubscriptions); }
            private void SetField(string name, object value) => typeof(TilingOverlayRenderer)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Renderer, value);
            private T GetField<T>(string name) => (T)typeof(TilingOverlayRenderer)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Renderer)!;
        }
    }
}
