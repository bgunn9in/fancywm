#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

using FancyWM.Layouts;
using FancyWM.Layouts.Tiling;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinMan;

namespace FancyWM.Tests
{
    [TestClass]
    public class GenericPreviewTraversalTest
    {
        private static readonly Func<TilingWorkspace, TilingNode, PanelNode, Point, bool> s_moveNodeTest =
            typeof(TilingWorkspace).GetMethod("MoveNodeTest", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Func<TilingWorkspace, TilingNode, PanelNode, Point, bool>>();

        private static readonly Func<TilingNode, Point, TilingNode?> s_findMoveTarget =
            typeof(TilingWorkspace).GetMethod("FindMoveTarget", BindingFlags.Static | BindingFlags.NonPublic)!
                .CreateDelegate<Func<TilingNode, Point, TilingNode?>>();

        private delegate void ClearPreviewConstraintsDelegate(
            TilingNode node,
            long sourceGeneration,
            ref TilingNode? source,
            ref bool canReuseSource);

        private static readonly ClearPreviewConstraintsDelegate s_clearPreviewConstraints =
            typeof(TilingWorkspace).GetMethod("ClearPreviewConstraints", BindingFlags.Static | BindingFlags.NonPublic)!
                .CreateDelegate<ClearPreviewConstraintsDelegate>();

        [DataTestMethod]
        [DataRow("flat", 10)]
        [DataRow("flat", 25)]
        [DataRow("flat", 50)]
        [DataRow("balanced", 10)]
        [DataRow("balanced", 25)]
        [DataRow("balanced", 50)]
        [DataRow("skewed", 10)]
        [DataRow("skewed", 25)]
        [DataRow("skewed", 50)]
        public void BuiltInOutsideMoveTargetAvoidsEnumerationAllocationsAndLeavesTreeUnchanged(string shape, int count)
        {
            var fixture = new Fixture(shape, count);
            var point = new Point(-100, -100);
            var before = Snapshot(fixture.Tree);
            for (int i = 0; i < 30; i++)
            {
                HistoricalMoveTarget(fixture.Source, point);
                fixture.Workspace.MoveNode(fixture.Source, point);
            }
            fixture.ResetReads();
            long referenceBytes = 0, actualBytes = 0;
            for (int i = 0; i < 50; i++)
            {
                for (int half = 0; half < 2; half++)
                {
                    bool reference = (i + half) % 2 == 0;
                    long start = GC.GetAllocatedBytesForCurrentThread();
                    TilingNode? result = null;
                    if (reference) result = HistoricalMoveTarget(fixture.Source, point);
                    else fixture.Workspace.MoveNode(fixture.Source, point);
                    long bytes = GC.GetAllocatedBytesForCurrentThread() - start;
                    if (reference) referenceBytes += bytes;
                    else actualBytes += bytes;
                    Assert.IsNull(result);
                }
            }
            Assert.AreEqual(before, Snapshot(fixture.Tree));
            Assert.AreEqual(0, fixture.Reads, "An absent target must not trigger layout or fresh native constraint reads.");
            fixture.AssertRegistration();
            Assert.IsTrue(actualBytes <= referenceBytes - count * 32L * 50,
                $"Actual MoveNode {actualBytes} B must avoid at least one leaf enumeration allocation per window compared with historical target search {referenceBytes} B.");
        }

        [DataTestMethod]
        [DataRow("window")]
        [DataRow("placeholder")]
        [DataRow("panel")]
        [DataRow("source-window")]
        [DataRow("source-placeholder")]
        [DataRow("source-panel")]
        [DataRow("root-header")]
        public void MoveTargetPreservesPriorityPreorderAndSourceExclusions(string scenario)
        {
            var fixture = new MoveTargetFixture(new SplitPanelNode());
            var (source, point, expected) = fixture.Prepare(scenario);
            var before = Snapshot(fixture.Tree);
            Assert.AreSame(expected, HistoricalMoveTarget(source, point));
            Assert.AreSame(expected, s_findMoveTarget(source, point));
            if (scenario is "root-header" or "source-window" or "source-panel")
                new TilingWorkspace().MoveNode(source, point);
            if (scenario == "source-placeholder")
                Assert.AreEqual(TilingError.CausesRecursiveNesting,
                    Assert.ThrowsException<TilingFailedException>(() => new TilingWorkspace().MoveNode(source, point)).FailReason);
            Assert.AreEqual(before, Snapshot(fixture.Tree));
            fixture.AssertRegistration();
        }

        [DataTestMethod]
        [DataRow(false, "window")]
        [DataRow(true, "window")]
        [DataRow(false, "placeholder")]
        [DataRow(true, "placeholder")]
        [DataRow(false, "panel")]
        [DataRow(true, "panel")]
        public void DerivedMoveTargetPreservesGetterEnumerationAndDisposalCadence(bool nested, string scenario)
        {
            var script = new MoveTargetEnumerationScript { ReverseSecondNodes = scenario == "panel" };
            var fixture = new MoveTargetFixture(new MoveTargetEnumerationPanel(script), nested);
            var (source, point, _) = fixture.Prepare(scenario);
            var before = Snapshot(fixture.Tree);
            script.Reset();
            var expected = HistoricalMoveTarget(source, point);
            var expectedEvents = script.Events.ToArray();
            Assert.AreSame(scenario == "window" ? fixture.FirstWindow
                : scenario == "placeholder" ? fixture.FirstPlaceholder : fixture.SecondPanel, expected);
            Assert.AreEqual(scenario == "panel" ? 2 : scenario == "placeholder" || !nested ? 1 : 0, script.NodeGetters);
            script.Reset();
            Assert.AreSame(expected, s_findMoveTarget(source, point));
            CollectionAssert.AreEqual(expectedEvents, script.Events.ToArray());
            Assert.AreEqual(before, Snapshot(fixture.Tree));
            fixture.AssertRegistration();
        }

        [DataTestMethod]
        [DataRow("windows-get")]
        [DataRow("nodes-get")]
        [DataRow("windows-start")]
        [DataRow("nodes-start")]
        [DataRow("windows-yield")]
        [DataRow("nodes-yield")]
        [DataRow("windows-dispose")]
        [DataRow("nodes-dispose")]
        public void DerivedMoveTargetPreservesExceptionIdentityAndDisposalOrder(string failureAt)
        {
            var script = new MoveTargetEnumerationScript { FailureAt = failureAt };
            var fixture = new MoveTargetFixture(new MoveTargetEnumerationPanel(script));
            var (source, point, _) = fixture.Prepare("placeholder");
            var before = Snapshot(fixture.Tree);
            script.Reset();
            var expected = Assert.ThrowsException<InvalidOperationException>(() => HistoricalMoveTarget(source, point));
            var expectedEvents = script.Events.ToArray();
            script.Reset();
            var actual = Assert.ThrowsException<InvalidOperationException>(() => s_findMoveTarget(source, point));
            Assert.AreSame(script.Failure, expected);
            Assert.AreSame(expected, actual);
            CollectionAssert.AreEqual(expectedEvents, script.Events.ToArray());
            Assert.AreEqual(before, Snapshot(fixture.Tree));
            fixture.AssertRegistration();
        }

        [TestMethod]
        public void PartialBuiltInMoveTargetIsDiscardedAfterCustomGetterChangesEarlierTargets()
        {
            var fixture = new MoveTargetFixture(new SplitPanelNode());
            var script = new MoveTargetEnumerationScript();
            var custom = new MoveTargetEnumerationPanel(script);
            fixture.Tree.Root!.Attach(custom);
            custom.Attach(new WindowNode(new PreviewWindow(99)));
            fixture.Tree.Measure();
            fixture.Tree.Arrange();
            script.BeforeWindowsGetter = () => fixture.FirstPlaceholder.Arrange(new RectangleF(MoveTargetFixture.Outside));
            var (source, point, _) = fixture.Prepare("placeholder");
            script.Reset();
            var expected = HistoricalMoveTarget(source, point);
            var expectedEvents = script.Events.ToArray();
            var after = Snapshot(fixture.Tree);
            Assert.AreSame(fixture.SecondPlaceholder, expected);
            fixture.Prepare("placeholder");
            script.Reset();
            Assert.AreSame(expected, s_findMoveTarget(source, point));
            CollectionAssert.AreEqual(expectedEvents, script.Events.ToArray());
            Assert.AreEqual(after, Snapshot(fixture.Tree));
            fixture.AssertRegistration();
        }

        [TestMethod]
        public void BuiltInWindowHitBeforeCustomTailDoesNotInvokeItsGetters()
        {
            var fixture = new MoveTargetFixture(new SplitPanelNode());
            var script = new MoveTargetEnumerationScript { FailureAt = "windows-get" };
            var custom = new MoveTargetEnumerationPanel(script);
            fixture.Tree.Root!.Attach(custom);
            custom.Attach(new WindowNode(new PreviewWindow(99)));
            fixture.Tree.Measure();
            fixture.Tree.Arrange();
            var (source, point, expected) = fixture.Prepare("window");
            script.Reset();
            Assert.AreSame(expected, HistoricalMoveTarget(source, point));
            Assert.AreEqual(0, script.Events.Count);
            Assert.AreSame(expected, s_findMoveTarget(source, point));
            Assert.AreEqual(0, script.Events.Count);
            fixture.AssertRegistration();
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void BuiltInCloneGenerationLookupPreservesFirstMatchAndAvoidsRepeatedEnumeration(bool sourceFirst)
        {
            var fixture = new GenerationLookupFixture(sourceFirst);
            var before = Snapshot(fixture.Tree);
            int referenceEnumerations = 0;
            var point = new Point(11999, 400);
            fixture.ReadOrder.Clear();
            bool expected = HistoricalMoveNodeTest(fixture.Workspace, fixture.Source, fixture.Target, point,
                () => referenceEnumerations++);
            var expectedReads = Enumerable.Range(100, 13)
                .Concat(sourceFirst ? new[] { 5, 2, 1, 3, 4 } : new[] { 2, 1, 5, 3, 4 }).ToArray();
            Assert.IsFalse(expected, "The first target generation is distinct from the last target under the test point.");
            Assert.AreEqual(2, referenceEnumerations);
            CollectionAssert.AreEqual(expectedReads, fixture.ReadOrder.ToArray());

            fixture.ReadOrder.Clear();
            Assert.AreEqual(expected, s_moveNodeTest(fixture.Workspace, fixture.Source, fixture.Target, point));
            CollectionAssert.AreEqual(expectedReads, fixture.ReadOrder.ToArray(),
                "Preflight must move the first source-generation match into the first target-generation match.");
            Assert.AreEqual(before, Snapshot(fixture.Tree));
            fixture.AssertRegistration();

            // Exact built-in Nodes cannot be overridden to count accesses without
            // changing the custom-node contract. The model records its two lookups;
            // the allocation bound detects removal of their repeated leaf arrays.
            for (int i = 0; i < 20; i++)
            {
                fixture.ReadOrder.Clear();
                HistoricalMoveNodeTest(fixture.Workspace, fixture.Source, fixture.Target, point);
                fixture.ReadOrder.Clear();
                s_moveNodeTest(fixture.Workspace, fixture.Source, fixture.Target, point);
            }
            long referenceBytes = 0, actualBytes = 0;
            for (int i = 0; i < 50; i++)
            {
                for (int half = 0; half < 2; half++)
                {
                    bool reference = (i + half) % 2 == 0;
                    fixture.ReadOrder.Clear();
                    long start = GC.GetAllocatedBytesForCurrentThread();
                    bool result = reference
                        ? HistoricalMoveNodeTest(fixture.Workspace, fixture.Source, fixture.Target, point)
                        : s_moveNodeTest(fixture.Workspace, fixture.Source, fixture.Target, point);
                    long bytes = GC.GetAllocatedBytesForCurrentThread() - start;
                    if (reference) referenceBytes += bytes;
                    else actualBytes += bytes;
                    Assert.AreEqual(expected, result);
                    CollectionAssert.AreEqual(expectedReads, fixture.ReadOrder.ToArray());
                }
            }
            Assert.IsTrue(actualBytes <= referenceBytes - 12 * 32L * 50,
                $"Actual preflight {actualBytes} B must remove at least one repeated leaf array per prefix window from historical preflight {referenceBytes} B.");
            Assert.AreEqual(before, Snapshot(fixture.Tree));
            fixture.AssertRegistration();
        }

        [TestMethod]
        public void MoveNodeAncestorScanStartsAtParentAndUsesVirtualEqualityForSameReference()
        {
            var script = new AncestorEqualityScript();
            script.Compare = (panel, other) =>
            {
                if (panel.Name == "source-parent")
                {
                    if (!ReferenceEquals(panel, other))
                        throw new InvalidOperationException("The source parent itself must not be part of Ancestors.");
                    return true;
                }
                if (panel.Name == "root")
                    throw new InvalidOperationException("A successful ancestor comparison must short-circuit before the next parent.");
                Assert.AreEqual("target", panel.Name);
                Assert.AreSame(panel, other, "EqualityComparer must still call virtual Equals for the same ancestor reference.");
                return true;
            };
            var workspace = new TilingWorkspace();
            var root = new AncestorEqualityPanel("root", script) { Orientation = PanelOrientation.Horizontal };
            var target = new AncestorEqualityPanel("target", script);
            var sourceParent = new AncestorEqualityPanel("source-parent", script);
            var source = new WindowNode(new PreviewWindow(901));
            var retained = new WindowNode(new PreviewWindow(902));
            var tree = new DesktopTree { Root = root, WorkArea = new Rectangle(0, 0, 1200, 800) };
            root.Attach(target);
            target.Attach(sourceParent);
            target.Attach(retained);
            sourceParent.Attach(source);
            tree.Measure();
            tree.Arrange();
            var point = target.ComputedRectangle.Center;
            var rootRectangle = root.ComputedRectangle;
            var targetRectangle = target.ComputedRectangle;
            var sourceRectangle = source.ComputedRectangle;

            script.Calls.Clear();
            bool expected = HistoricalMoveNodeTest(workspace, source, target, point);
            CollectionAssert.AreEqual(new[] { "target", "source-parent" }, script.Calls.ToArray());
            script.Calls.Clear();

            Assert.AreEqual(expected, s_moveNodeTest(workspace, source, target, point));
            CollectionAssert.AreEqual(new[] { "target", "source-parent" }, script.Calls.ToArray());
            Assert.AreEqual(rootRectangle, root.ComputedRectangle);
            Assert.AreEqual(targetRectangle, target.ComputedRectangle);
            Assert.AreEqual(sourceRectangle, source.ComputedRectangle);
            Assert.AreEqual(1, root.Children.Count);
            Assert.AreSame(target, root.Children[0]);
            Assert.AreSame(root, target.Parent);
            Assert.AreEqual(2, target.Children.Count);
            Assert.AreSame(sourceParent, target.Children[0]);
            Assert.AreSame(retained, target.Children[1]);
            Assert.AreSame(target, sourceParent.Parent);
            Assert.AreEqual(1, sourceParent.Children.Count);
            Assert.AreSame(source, sourceParent.Children[0]);
            Assert.AreSame(sourceParent, source.Parent);
            Assert.AreSame(source, tree.FindNode(source.WindowReference));
            Assert.AreSame(retained, tree.FindNode(retained.WindowReference));
        }

        [TestMethod]
        public void MoveNodeAncestorScanPreservesVirtualEqualityFailureBeforeMutation()
        {
            var targetFailure = new InvalidOperationException("controlled ancestor equality failure");
            var sourceFailure = new InvalidOperationException("the source parent must not be compared");
            var script = new AncestorEqualityScript();
            var workspace = new TilingWorkspace();
            var root = new SplitPanelNode { Orientation = PanelOrientation.Horizontal };
            var target = new AncestorEqualityPanel("target", script);
            var sourceParent = new AncestorEqualityPanel("source-parent", script);
            var source = new WindowNode(new PreviewWindow(903));
            var retained = new WindowNode(new PreviewWindow(904));
            var tree = new DesktopTree { Root = root, WorkArea = new Rectangle(0, 0, 1200, 800) };
            root.Attach(target);
            target.Attach(sourceParent);
            target.Attach(retained);
            sourceParent.Attach(source);
            tree.Measure();
            tree.Arrange();
            script.Compare = (panel, other) =>
            {
                Assert.AreSame(panel, other);
                if (panel.Name != "target") throw sourceFailure;

                var cloneSourceParent = panel.Children
                    .OfType<AncestorEqualityPanel>()
                    .Single(child => child.Name == "source-parent");
                Assert.AreEqual(1, cloneSourceParent.Children.Count,
                    "Ancestor comparison must happen before detaching the cloned source.");
                var cloneSource = cloneSourceParent.Children[0];
                Assert.AreEqual(source.GenerationID, cloneSource.GenerationID);
                Assert.AreSame(cloneSourceParent, cloneSource.Parent);
                Assert.AreSame(cloneSource, panel.Desktop!.FindNode(source.WindowReference));
                throw targetFailure;
            };
            var rootRectangle = root.ComputedRectangle;
            var targetRectangle = target.ComputedRectangle;
            var sourceRectangle = source.ComputedRectangle;

            script.Calls.Clear();
            Assert.AreSame(targetFailure, Assert.ThrowsException<InvalidOperationException>(() =>
                HistoricalMoveNodeTest(workspace, source, target, target.ComputedRectangle.Center)));
            CollectionAssert.AreEqual(new[] { "target" }, script.Calls.ToArray());
            script.Calls.Clear();

            Assert.AreSame(targetFailure, Assert.ThrowsException<InvalidOperationException>(() =>
                s_moveNodeTest(workspace, source, target, target.ComputedRectangle.Center)));
            CollectionAssert.AreEqual(new[] { "target" }, script.Calls.ToArray());
            Assert.AreEqual(rootRectangle, root.ComputedRectangle);
            Assert.AreEqual(targetRectangle, target.ComputedRectangle);
            Assert.AreEqual(sourceRectangle, source.ComputedRectangle);
            Assert.AreEqual(1, root.Children.Count);
            Assert.AreSame(target, root.Children[0]);
            Assert.AreSame(root, target.Parent);
            Assert.AreEqual(2, target.Children.Count);
            Assert.AreSame(sourceParent, target.Children[0]);
            Assert.AreSame(retained, target.Children[1]);
            Assert.AreSame(target, sourceParent.Parent);
            Assert.AreEqual(1, sourceParent.Children.Count);
            Assert.AreSame(source, sourceParent.Children[0]);
            Assert.AreSame(sourceParent, source.Parent);
            Assert.AreSame(source, tree.FindNode(source.WindowReference));
            Assert.AreSame(retained, tree.FindNode(retained.WindowReference));
        }

        [TestMethod]
        public void MoveNodeAncestorScanReadsNextParentAfterVirtualEqualityMutation()
        {
            var unexpectedRootFailure = new InvalidOperationException("A detached middle panel must terminate the parent walk.");
            var script = new AncestorEqualityScript
            {
                Compare = (panel, other) =>
                {
                    if (panel.Name == "source-parent")
                    {
                        if (!ReferenceEquals(panel, other))
                            throw new InvalidOperationException("The source parent itself must not be compared.");
                        return true;
                    }
                    if (panel.Name == "root") throw unexpectedRootFailure;
                    Assert.AreEqual("middle", panel.Name);
                    panel.CollapseIfSingle();
                    return false;
                },
            };
            var workspace = new TilingWorkspace();
            var root = new AncestorEqualityPanel("root", script) { Orientation = PanelOrientation.Horizontal };
            var middle = new AncestorEqualityPanel("middle", script);
            var sourceParent = new AncestorEqualityPanel("source-parent", script);
            var target = new SplitPanelNode();
            var source = new WindowNode(new PreviewWindow(905));
            var retained = new WindowNode(new PreviewWindow(906));
            var tree = new DesktopTree { Root = root, WorkArea = new Rectangle(0, 0, 1200, 800) };
            root.Attach(middle);
            root.Attach(target);
            middle.Attach(sourceParent);
            sourceParent.Attach(source);
            target.Attach(retained);
            tree.Measure();
            tree.Arrange();
            var point = target.ComputedRectangle.Center;
            var rootRectangle = root.ComputedRectangle;
            var middleRectangle = middle.ComputedRectangle;
            var sourceRectangle = source.ComputedRectangle;

            script.Calls.Clear();
            bool expected = HistoricalMoveNodeTest(workspace, source, target, point);
            CollectionAssert.AreEqual(new[] { "middle", "source-parent" }, script.Calls.ToArray());
            script.Calls.Clear();

            Assert.AreEqual(expected, s_moveNodeTest(workspace, source, target, point));
            CollectionAssert.AreEqual(new[] { "middle", "source-parent" }, script.Calls.ToArray());
            Assert.AreEqual(rootRectangle, root.ComputedRectangle);
            Assert.AreEqual(middleRectangle, middle.ComputedRectangle);
            Assert.AreEqual(sourceRectangle, source.ComputedRectangle);
            Assert.AreEqual(2, root.Children.Count);
            Assert.AreSame(middle, root.Children[0]);
            Assert.AreSame(target, root.Children[1]);
            Assert.AreSame(root, middle.Parent);
            Assert.AreSame(root, target.Parent);
            Assert.AreEqual(1, middle.Children.Count);
            Assert.AreSame(sourceParent, middle.Children[0]);
            Assert.AreSame(middle, sourceParent.Parent);
            Assert.AreEqual(1, sourceParent.Children.Count);
            Assert.AreSame(source, sourceParent.Children[0]);
            Assert.AreSame(sourceParent, source.Parent);
            Assert.AreEqual(1, target.Children.Count);
            Assert.AreSame(retained, target.Children[0]);
            Assert.AreSame(source, tree.FindNode(source.WindowReference));
            Assert.AreSame(retained, tree.FindNode(retained.WindowReference));
        }

        [TestMethod]
        public void MoveNodeAncestorScanAvoidsIteratorAllocation()
        {
            var fixture = new AncestorAllocationFixture();
            var point = fixture.Target.ComputedRectangle.Center;
            var before = Snapshot(fixture.Tree);
            bool expected = HistoricalMoveNodeTest(fixture.Workspace, fixture.Source, fixture.Target, point);
            Assert.AreEqual(expected, s_moveNodeTest(fixture.Workspace, fixture.Source, fixture.Target, point));

            for (int i = 0; i < 100; i++)
            {
                HistoricalMoveNodeTest(fixture.Workspace, fixture.Source, fixture.Target, point);
                s_moveNodeTest(fixture.Workspace, fixture.Source, fixture.Target, point);
            }

            const int iterations = 1000;
            long referenceBytes = 0;
            long actualBytes = 0;
            for (int i = 0; i < iterations; i++)
            {
                for (int half = 0; half < 2; half++)
                {
                    bool reference = (i + half) % 2 == 0;
                    long start = GC.GetAllocatedBytesForCurrentThread();
                    bool result = reference
                        ? HistoricalMoveNodeTest(fixture.Workspace, fixture.Source, fixture.Target, point)
                        : s_moveNodeTest(fixture.Workspace, fixture.Source, fixture.Target, point);
                    long bytes = GC.GetAllocatedBytesForCurrentThread() - start;
                    if (reference) referenceBytes += bytes;
                    else actualBytes += bytes;
                    Assert.AreEqual(expected, result);
                }
            }

            Console.WriteLine($"PERFCOUNTER move-node-ancestor reference-bytes={referenceBytes} actual-bytes={actualBytes} iterations={iterations}");
            Assert.IsTrue(actualBytes <= referenceBytes - 24L * iterations,
                $"Actual parent walk {actualBytes} B must remove at least 24 B/call from the historical iterator {referenceBytes} B.");
            Assert.AreEqual(before, Snapshot(fixture.Tree));
            Assert.AreSame(fixture.Source, fixture.Tree.FindNode(fixture.Source.WindowReference));
            Assert.AreSame(fixture.Retained, fixture.Tree.FindNode(fixture.Retained.WindowReference));
        }

        [TestMethod]
        public void PublicMoveNodeRecursionGuardIncludesTargetAndUsesVirtualEqualityForSameReference()
        {
            var script = new PathEqualityScript();
            var root = new SplitPanelNode { Orientation = PanelOrientation.Horizontal };
            var target = new PathEqualityPlaceholder("target", script);
            var retained = new WindowNode(new PreviewWindow(909));
            var tree = new DesktopTree { Root = root, WorkArea = new Rectangle(0, 0, 1200, 800) };
            root.Attach(target);
            root.Attach(retained);
            tree.Measure();
            tree.Arrange();
            var before = Snapshot(tree);
            script.Compare = (name, current, other) =>
            {
                Assert.AreEqual("target", name);
                Assert.AreSame(target, current);
                Assert.AreSame(target, other);
                return true;
            };
            script.Enabled = true;

            var error = Assert.ThrowsException<TilingFailedException>(() =>
                new TilingWorkspace().MoveNode(target, target.ComputedRectangle.Center));

            Assert.AreEqual(TilingError.CausesRecursiveNesting, error.FailReason);
            CollectionAssert.AreEqual(new[] { "target" }, script.Calls.ToArray());
            script.Enabled = false;
            Assert.AreEqual(before, Snapshot(tree));
            Assert.AreSame(retained, tree.FindNode(retained.WindowReference));
        }

        [TestMethod]
        public void PublicMoveNodeRecursionGuardDoesNotOverrideFalseVirtualEqualityForSameReference()
        {
            var script = new PathEqualityScript();
            var root = new PathEqualityPanel("root", script) { Orientation = PanelOrientation.Horizontal };
            var target = new PathEqualityPlaceholder("target", script);
            var retained = new WindowNode(new PreviewWindow(911));
            var tree = new DesktopTree { Root = root, WorkArea = new Rectangle(0, 0, 1200, 800) };
            root.Attach(target);
            root.Attach(retained);
            tree.Measure();
            tree.Arrange();
            var before = Snapshot(tree);
            var failure = new InvalidOperationException("controlled parent comparison after a false same-reference equality result");
            script.Compare = (name, current, other) =>
            {
                Assert.AreSame(target, other);
                if (name == "target")
                {
                    Assert.AreSame(target, current);
                    return false;
                }
                Assert.AreEqual("root", name);
                Assert.AreSame(root, current);
                throw failure;
            };
            script.Enabled = true;

            var actual = Assert.ThrowsException<InvalidOperationException>(() =>
                new TilingWorkspace().MoveNode(target, target.ComputedRectangle.Center));

            Assert.AreSame(failure, actual);
            CollectionAssert.AreEqual(new[] { "target", "root" }, script.Calls.ToArray());
            script.Enabled = false;
            Assert.AreEqual(before, Snapshot(tree));
            Assert.AreSame(retained, tree.FindNode(retained.WindowReference));
        }

        [TestMethod]
        public void PublicMoveNodeRecursionGuardPreservesComparisonOrderAndShortCircuit()
        {
            var fixture = new PublicRecursionGuardFixture();
            fixture.Script.Compare = (name, current, other) =>
            {
                Assert.AreSame(fixture.Source, other);
                if (name == "upper") throw new InvalidOperationException("A successful source comparison must short-circuit before the next parent.");
                return name == "source";
            };
            var before = Snapshot(fixture.Tree);
            fixture.Script.Enabled = true;

            var error = Assert.ThrowsException<TilingFailedException>(() =>
                fixture.Workspace.MoveNode(fixture.Source, fixture.Target.ComputedRectangle.Center));

            Assert.AreEqual(TilingError.CausesRecursiveNesting, error.FailReason);
            CollectionAssert.AreEqual(new[] { "target", "middle", "source" }, fixture.Script.Calls.ToArray());
            fixture.Script.Enabled = false;
            Assert.AreEqual(before, Snapshot(fixture.Tree));
            fixture.AssertRegistration();
        }

        [TestMethod]
        public void PublicMoveNodeRecursionGuardPreservesEqualityFailureBeforeMutation()
        {
            var fixture = new PublicRecursionGuardFixture();
            var failure = new InvalidOperationException("controlled public recursion equality failure");
            fixture.Script.Compare = (name, current, other) =>
            {
                Assert.AreSame(fixture.Source, other);
                if (name == "target") throw failure;
                throw new InvalidOperationException("The first comparison must preserve the original failure.");
            };
            var before = Snapshot(fixture.Tree);
            fixture.Script.Enabled = true;

            var actual = Assert.ThrowsException<InvalidOperationException>(() =>
                fixture.Workspace.MoveNode(fixture.Source, fixture.Target.ComputedRectangle.Center));

            Assert.AreSame(failure, actual);
            CollectionAssert.AreEqual(new[] { "target" }, fixture.Script.Calls.ToArray());
            fixture.Script.Enabled = false;
            Assert.AreEqual(before, Snapshot(fixture.Tree));
            fixture.AssertRegistration();
        }

        [TestMethod]
        public void PublicMoveNodeRecursionGuardReadsParentAfterEqualityMutation()
        {
            var fixture = new PublicRecursionGuardFixture();
            var sourceRectangle = fixture.Source.ComputedRectangle;
            var targetRectangle = fixture.Target.ComputedRectangle;
            fixture.Script.Compare = (name, current, other) =>
            {
                Assert.AreSame(fixture.Source, other);
                if (name == "target")
                {
                    fixture.Middle.CollapseIfSingle();
                    Assert.AreSame(fixture.Source, fixture.Target.Parent);
                    return false;
                }
                if (name == "middle") throw new InvalidOperationException("The next Parent read must observe the collapsed panel.");
                if (name == "upper") throw new InvalidOperationException("The source match must short-circuit before the upper panel.");
                return name == "source";
            };
            fixture.Script.Enabled = true;

            var error = Assert.ThrowsException<TilingFailedException>(() =>
                fixture.Workspace.MoveNode(fixture.Source, fixture.Target.ComputedRectangle.Center));

            Assert.AreEqual(TilingError.CausesRecursiveNesting, error.FailReason);
            CollectionAssert.AreEqual(new[] { "target", "source" }, fixture.Script.Calls.ToArray());
            Assert.AreSame(fixture.Source, fixture.Target.Parent);
            Assert.AreSame(fixture.Target, fixture.Source.Children.Single());
            Assert.IsNull(fixture.Middle.Parent);
            Assert.AreSame(fixture.Target, fixture.Middle.Children.Single(), "Collapse keeps the detached panel's child reference while changing the child's live Parent.");
            Assert.AreEqual(sourceRectangle, fixture.Source.ComputedRectangle);
            Assert.AreEqual(targetRectangle, fixture.Target.ComputedRectangle);
            fixture.AssertRegistration();
        }

        [TestMethod]
        public void PublicMoveNodeRecursionGuardReevaluatesStackPathAfterEqualityMutation()
        {
            var fixture = new PublicRecursionGuardFixture();
            StackPanelNode? insertedStack = null;
            fixture.Script.Compare = (name, current, other) =>
            {
                Assert.AreSame(fixture.Source, other);
                if (name == "upper")
                {
                    insertedStack = new StackPanelNode();
                    fixture.Target.Embed(insertedStack);
                }
                return false;
            };
            fixture.Script.Enabled = true;

            var error = Assert.ThrowsException<TilingFailedException>(() =>
                fixture.Workspace.MoveNode(fixture.Source, fixture.Target.ComputedRectangle.Center));

            Assert.AreEqual(TilingError.NestingInStackPanel, error.FailReason);
            CollectionAssert.AreEqual(new[] { "target", "middle", "source", "upper" }, fixture.Script.Calls.ToArray());
            Assert.IsNotNull(insertedStack);
            fixture.Script.Enabled = false;
            var stack = insertedStack!;
            Assert.AreSame(fixture.Middle, stack.Parent);
            Assert.AreSame(stack, fixture.Target.Parent);
            Assert.AreSame(stack, fixture.Middle.Children.Single());
            Assert.AreSame(fixture.Target, stack.Children.Single());
            Assert.AreSame(fixture.Upper, fixture.Source.Parent);
            Assert.AreSame(fixture.Middle, fixture.Source.Children.Single());
            fixture.AssertRegistration();
        }

        [DataTestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public void PublicMoveNodeStackGuardRejectsNonWindowBeforeMutation(bool derivedStack, bool targetHeader)
        {
            var workspace = new TilingWorkspace();
            var root = new SplitPanelNode { Orientation = PanelOrientation.Horizontal };
            var sourceParent = new SplitPanelNode { Orientation = PanelOrientation.Vertical };
            var source = new PlaceholderNode();
            var retainedAdapter = new PreviewWindow(912);
            var retained = new WindowNode(retainedAdapter);
            StackPanelNode stack = derivedStack ? new DerivedStackPanelNode() : new StackPanelNode();
            stack.Padding = new Rectangle(0, 30, 0, 0);
            var targetAdapter = new PreviewWindow(913);
            var target = new WindowNode(targetAdapter);
            var tree = new DesktopTree { Root = root, WorkArea = new Rectangle(0, 0, 1200, 800) };
            root.Attach(sourceParent);
            root.Attach(stack);
            sourceParent.Attach(source);
            sourceParent.Attach(retained);
            stack.Attach(target);
            tree.Measure();
            tree.Arrange();
            var point = targetHeader
                ? new Point(stack.ComputedRectangle.Center.X, stack.ComputedRectangle.Top - stack.Padding.Top / 2)
                : target.ComputedRectangle.Center;
            Assert.AreSame(targetHeader ? stack : target, s_findMoveTarget(source, point));
            var before = Snapshot(tree);
            retainedAdapter.MinimumReads = 0;
            targetAdapter.MinimumReads = 0;

            var error = Assert.ThrowsException<TilingFailedException>(() => workspace.MoveNode(source, point));

            Assert.AreEqual(TilingError.NestingInStackPanel, error.FailReason);
            Assert.AreEqual(0, retainedAdapter.MinimumReads + targetAdapter.MinimumReads,
                "The stack admission guard must fail before minimum-size preflight.");
            Assert.AreEqual(before, Snapshot(tree));
            CollectionAssert.AreEqual(new TilingNode[] { sourceParent, stack }, root.Children.ToArray());
            CollectionAssert.AreEqual(new TilingNode[] { source, retained }, sourceParent.Children.ToArray());
            CollectionAssert.AreEqual(new TilingNode[] { target }, stack.Children.ToArray());
            Assert.AreSame(target, tree.FindNode(targetAdapter));
            Assert.AreSame(retained, tree.FindNode(retainedAdapter));
        }

        [TestMethod]
        public void PublicMoveNodeStackGuardAllowsWindowAndPreservesStackOrder()
        {
            var fixture = new Fixture("stack", 2);
            var point = fixture.Target.ComputedRectangle.Center;
            Assert.AreSame(fixture.Target, s_findMoveTarget(fixture.Source, point));
            var before = Snapshot(fixture.Tree);
            var order = ((StackPanelNode)fixture.Tree.Root!).Children.ToArray();
            fixture.ResetReads();

            fixture.Workspace.MoveNode(fixture.Source, point);

            Assert.AreEqual(0, fixture.Reads);
            Assert.AreEqual(before, Snapshot(fixture.Tree));
            CollectionAssert.AreEqual(order, ((StackPanelNode)fixture.Tree.Root!).Children.ToArray());
            fixture.AssertRegistration();
        }

        [TestMethod]
        public void PublicMoveNodeStackGuardContinuesWindowSwapWhenNestingDisabled()
        {
            var fixture = new Fixture("stack", 2);
            var stack = (StackPanelNode)fixture.Tree.Root!;
            var point = fixture.Target.ComputedRectangle.Center;
            Assert.AreSame(fixture.Target, s_findMoveTarget(fixture.Source, point));
            CollectionAssert.AreEqual(new TilingNode[] { fixture.Target, fixture.Source }, stack.Children.ToArray());
            fixture.ResetReads();

            fixture.Workspace.MoveNode(fixture.Source, point, allowNesting: false);

            Assert.AreEqual(0, fixture.Reads);
            CollectionAssert.AreEqual(new TilingNode[] { fixture.Source, fixture.Target }, stack.Children.ToArray());
            Assert.AreSame(stack, fixture.Source.Parent);
            Assert.AreSame(stack, fixture.Target.Parent);
            fixture.AssertRegistration();
        }

#if !DEBUG
        [TestMethod]
        public void PublicMoveNodeRecursionGuardAvoidsPathIteratorAllocation()
        {
            var fixture = new Fixture("stack", 2);
            var point = fixture.Target.ComputedRectangle.Center;
            Assert.AreSame(fixture.Target, s_findMoveTarget(fixture.Source, point));
            var before = Snapshot(fixture.Tree);
            for (int i = 0; i < 100; i++) fixture.Workspace.MoveNode(fixture.Source, point);
            fixture.ResetReads();

            const int iterations = 1_000;
            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < iterations; i++) fixture.Workspace.MoveNode(fixture.Source, point);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

            Console.WriteLine($"PERFCOUNTER public-move-node-recursion allocated-bytes={allocated} iterations={iterations}");
            Assert.IsTrue(allocated <= 424_000,
                $"Public MoveNode recursion guard allocated {allocated} B; expected at most 424000 B for {iterations} calls.");
            Assert.AreEqual(0, fixture.Reads);
            Assert.AreEqual(before, Snapshot(fixture.Tree));
            fixture.AssertRegistration();
        }

        [TestMethod]
        public void PublicMoveNodeStackGuardAvoidsIteratorAllocation()
        {
            var fixture = new Fixture("stack", 2);
            var point = fixture.Target.ComputedRectangle.Center;
            Assert.AreSame(fixture.Target, s_findMoveTarget(fixture.Source, point));
            var before = Snapshot(fixture.Tree);
            for (int i = 0; i < 100; i++) fixture.Workspace.MoveNode(fixture.Source, point);
            fixture.ResetReads();

            const int iterations = 1_000;
            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < iterations; i++) fixture.Workspace.MoveNode(fixture.Source, point);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

            Console.WriteLine($"PERFCOUNTER public-move-node-stack allocated-bytes={allocated} iterations={iterations}");
            Assert.IsTrue(allocated <= 248_000,
                $"Public MoveNode stack guard allocated {allocated} B; expected at most 248000 B for {iterations} calls.");
            Assert.AreEqual(0, fixture.Reads);
            Assert.AreEqual(before, Snapshot(fixture.Tree));
            fixture.AssertRegistration();
        }
#endif

        [DataTestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public void DerivedCloneGenerationLookupRetainsTwoEnumerationsAndTheirChangingFirstMatches(bool hideSelf, bool nested)
        {
            var script = new GenerationEnumerationScript();
            var fixture = new GenerationLookupFixture(true, script, hideSelf, nested);
            var before = Snapshot(fixture.Tree);
            var point = new Point(11999, 400);
            fixture.Reset();
            bool expected = HistoricalMoveNodeTest(fixture.Workspace, fixture.Source, fixture.Target, point);
            var expectedGenerations = script.YieldedGenerations.ToArray();
            var expectedReads = fixture.ReadOrder.ToArray();
            Assert.IsTrue(expected, "The second custom enumeration makes the last target generation its first match.");
            Assert.AreEqual(2, script.GetterCalls);
            Assert.AreEqual(2, script.IteratorStarts);
            Assert.AreEqual(2, script.IteratorDisposals);
            Assert.AreEqual(hideSelf ? 17 : 19, expectedGenerations.Length);
            CollectionAssert.AreEqual(Enumerable.Range(100, 13).Concat(new[] { 5, 2, 3, 4, 1 }).ToArray(), expectedReads);

            fixture.Reset();
            Assert.AreEqual(expected, s_moveNodeTest(fixture.Workspace, fixture.Source, fixture.Target, point));
            Assert.AreEqual(2, script.GetterCalls, "Derived Nodes must still be queried separately for source and target.");
            Assert.AreEqual(2, script.IteratorStarts);
            Assert.AreEqual(2, script.IteratorDisposals);
            CollectionAssert.AreEqual(expectedGenerations, script.YieldedGenerations.ToArray());
            CollectionAssert.AreEqual(expectedReads, fixture.ReadOrder.ToArray());
            var copy = script.Copies.Single();
            var firstSourceClone = Walk(copy).OfType<WindowNode>().Single(node => node.WindowReference == fixture.Adapters[1]).Parent!;
            var secondSourceClone = Walk(copy).OfType<WindowNode>().Single(node => node.WindowReference == fixture.Adapters[3]).Parent!;
            var firstTargetClone = Walk(copy).OfType<WindowNode>().Single(node => node.WindowReference == fixture.Adapters[2]).Parent!;
            var secondTargetClone = Walk(copy).OfType<WindowNode>().Single(node => node.WindowReference == fixture.Adapters[4]).Parent!;
            Assert.AreSame(secondTargetClone, firstSourceClone.Parent);
            Assert.AreSame(copy, secondSourceClone.Parent);
            Assert.AreSame(copy, firstTargetClone.Parent);
            Assert.AreEqual(before, Snapshot(fixture.Tree));
            fixture.AssertRegistration();
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void HiddenCloneGenerationRetainsFailureOrderAndDisposesEveryEnumeration(bool sourceMissing)
        {
            var script = new GenerationEnumerationScript();
            var fixture = new GenerationLookupFixture(true, script, hideSelf: true, nested: true);
            script.HiddenGeneration = sourceMissing ? fixture.Source.GenerationID : fixture.Target.GenerationID;
            var before = Snapshot(fixture.Tree);
            fixture.Reset();
            var expected = Assert.ThrowsException<InvalidOperationException>(() =>
                HistoricalMoveNodeTest(fixture.Workspace, fixture.Source, fixture.Target, new Point(11999, 400)));
            var expectedGenerations = script.YieldedGenerations.ToArray();
            fixture.Reset();
            var actual = Assert.ThrowsException<InvalidOperationException>(() =>
                s_moveNodeTest(fixture.Workspace, fixture.Source, fixture.Target, new Point(11999, 400)));
            Assert.AreEqual(expected.Message, actual.Message);
            Assert.AreEqual(sourceMissing ? 1 : 2, script.GetterCalls);
            Assert.AreEqual(script.GetterCalls, script.IteratorStarts);
            Assert.AreEqual(script.IteratorStarts, script.IteratorDisposals);
            CollectionAssert.AreEqual(expectedGenerations, script.YieldedGenerations.ToArray());
            Assert.AreEqual(0, fixture.ReadOrder.Count, "Missing clones must fail before preflight mutation or minimum-size sampling.");
            Assert.AreEqual(before, Snapshot(fixture.Tree));
            fixture.AssertRegistration();
        }

        [TestMethod]
        public void CustomLookupAfterTentativeBuiltInMatchRepeatsBothHistoricalLookups()
        {
            var workspace = new TilingWorkspace();
            var root = new SplitPanelNode { Orientation = PanelOrientation.Horizontal };
            var tree = new DesktopTree { Root = root, WorkArea = new Rectangle(0, 0, 1200, 800) };
            var firstTarget = new SplitPanelNode { Orientation = PanelOrientation.Vertical };
            var secondTarget = (SplitPanelNode)firstTarget.Clone();
            var script = new GenerationEnumerationScript();
            var custom = new GenerationEnumerationPanel(script, hideSelf: false);
            var source = new SplitPanelNode { Orientation = PanelOrientation.Vertical };
            var reads = new List<int>();
            var adapters = Enumerable.Range(1, 3).Select(id => new PreviewWindow(id) { OnMinimumRead = () => reads.Add(id) }).ToArray();
            root.Attach(firstTarget);
            root.Attach(secondTarget);
            root.Attach(custom);
            firstTarget.Attach(new WindowNode(adapters[0]));
            secondTarget.Attach(new WindowNode(adapters[1]));
            custom.Attach(source);
            source.Attach(new WindowNode(adapters[2]));
            tree.Measure();
            tree.Arrange();
            var before = Snapshot(tree);
            PanelNode? visitedRoot = null;
            script.BeforeEnumeration = (node, lookup) =>
            {
                Assert.AreEqual(1, lookup);
                visitedRoot = node.Parent!;
                // Source lookup stops inside this iterator, so the outer list
                // is disposed before another MoveNext observes its mutation.
                visitedRoot.Move(0, 1);
            };
            script.Reset();
            reads.Clear();
            int referenceEnumerations = 0;
            bool expected = HistoricalMoveNodeTest(workspace, source, firstTarget, new Point(100, 400), () => referenceEnumerations++);
            Assert.IsTrue(expected);
            Assert.AreEqual(2, referenceEnumerations);
            Assert.AreEqual(1, script.GetterCalls, "Only the source lookup reaches the later custom subtree.");
            CollectionAssert.AreEqual(new[] { 2, 3, 1 }, reads.ToArray());
            var expectedGenerations = script.YieldedGenerations.ToArray();

            script.Reset();
            reads.Clear();
            visitedRoot = null;
            Assert.AreEqual(expected, s_moveNodeTest(workspace, source, firstTarget, new Point(100, 400)),
                "The tentative target must be discarded: the first historical lookup changed the second lookup's first match.");
            Assert.AreEqual(1, script.GetterCalls);
            Assert.AreEqual(1, script.IteratorStarts);
            Assert.AreEqual(1, script.IteratorDisposals);
            CollectionAssert.AreEqual(new[] { 2, 3, 1 }, reads.ToArray());
            CollectionAssert.AreEqual(expectedGenerations, script.YieldedGenerations.ToArray());
            Assert.IsNotNull(visitedRoot);
            var movedSource = Walk(visitedRoot!).OfType<WindowNode>().Single(node => node.WindowReference == adapters[2]).Parent!;
            var selectedTarget = Walk(visitedRoot!).OfType<WindowNode>().Single(node => node.WindowReference == adapters[1]).Parent!;
            Assert.AreSame(selectedTarget, movedSource.Parent);
            Assert.AreEqual(before, Snapshot(tree));
            foreach (var adapter in adapters)
                Assert.AreSame(Walk(root).OfType<WindowNode>().Single(node => node.WindowReference == adapter), tree.FindNode(adapter));
        }

        [TestMethod]
        public void BuiltInCloneMatchesBeforeCustomTailDoNotEnumerateItsNodes()
        {
            var fixture = new GenerationLookupFixture(sourceFirst: true);
            var script = new GenerationEnumerationScript
            {
                BeforeEnumeration = (_, _) => throw new InvalidOperationException("The late custom Nodes iterator must remain unobserved."),
            };
            var custom = new GenerationEnumerationPanel(script, hideSelf: true);
            fixture.Tree.Root!.Attach(custom);
            var adapter = new PreviewWindow(999) { OnMinimumRead = () => fixture.ReadOrder.Add(999) };
            fixture.Adapters.Add(999, adapter);
            custom.Attach(new WindowNode(adapter));
            fixture.Tree.Measure();
            fixture.Tree.Arrange();
            var before = Snapshot(fixture.Tree);
            script.Reset();
            fixture.ReadOrder.Clear();
            int referenceEnumerations = 0;
            bool expected = HistoricalMoveNodeTest(fixture.Workspace, fixture.Source, fixture.Target, new Point(11999, 400),
                () => referenceEnumerations++);
            var expectedReads = fixture.ReadOrder.ToArray();
            Assert.AreEqual(2, referenceEnumerations);
            Assert.AreEqual(0, script.GetterCalls);
            script.Reset();
            fixture.ReadOrder.Clear();

            Assert.AreEqual(expected, s_moveNodeTest(fixture.Workspace, fixture.Source, fixture.Target, new Point(11999, 400)));
            Assert.AreEqual(0, script.GetterCalls, "Both built-in matches precede the custom tail.");
            Assert.AreEqual(0, script.IteratorStarts);
            Assert.AreEqual(0, script.IteratorDisposals);
            Assert.AreEqual(0, script.YieldedGenerations.Count);
            Assert.AreEqual(1, script.Copies.Count, "Clone ownership remains unchanged even though lookup never visits the custom tail.");
            CollectionAssert.AreEqual(expectedReads, fixture.ReadOrder.ToArray());
            Assert.AreEqual(before, Snapshot(fixture.Tree));
            fixture.AssertRegistration();
        }

        [DataTestMethod]
        [DataRow("flat", 10, true)]
        [DataRow("flat", 25, false)]
        [DataRow("balanced", 10, true)]
        [DataRow("balanced", 25, false)]
        [DataRow("balanced", 50, true)]
        [DataRow("skewed", 10, false)]
        [DataRow("skewed", 25, true)]
        [DataRow("skewed", 50, false)]
        [DataRow("stack", 10, true)]
        [DataRow("stack", 25, false)]
        public void PreviewMatchesHistoricalModelAndLeavesTheLiveTreeUntouched(string shape, int count, bool nesting)
        {
            var fixture = new Fixture(shape, count);
            var before = Snapshot(fixture.Tree);
            foreach (var source in new[] { fixture.Source, fixture.Tree.Root!.Children[0] })
            {
                foreach (var point in fixture.Points)
                {
                    fixture.ResetReads();
                    var expected = Observe(() => HistoricalPreview(fixture.Workspace, source, point, nesting));
                    int expectedReads = fixture.Reads;
                    fixture.ResetReads();
                    var actual = Observe(() => fixture.Workspace.MockMoveNode(source, point, nesting));
                    AssertEquivalent(expected, actual);
                    Assert.AreEqual(expectedReads, fixture.Reads, "Native minimum-size sampling must remain unchanged.");
                    Assert.AreEqual(before, Snapshot(fixture.Tree));
                    fixture.AssertRegistration();
                }
            }
        }

        [DataTestMethod]
        [DataRow("balanced")]
        [DataRow("skewed")]
        public void FixedSeedTreeVariationsPreservePreviewRectanglesAndConstraints(string shape)
        {
            var random = new Random(0x9009);
            for (int iteration = 0; iteration < 20; iteration++)
            {
                var fixture = new Fixture(shape, 10 + random.Next(16));
                foreach (var panel in Walk(fixture.Tree.Root!).OfType<SplitPanelNode>())
                {
                    panel.Padding = new Rectangle(random.Next(5), random.Next(7), random.Next(5), random.Next(7));
                    panel.Spacing = random.Next(5);
                    if (random.Next(2) == 0) panel.ChangeOrientation(PanelOrientation.Vertical);
                }
                fixture.Tree.Measure();
                fixture.Tree.Arrange();
                var before = Snapshot(fixture.Tree);
                foreach (var point in fixture.Points)
                {
                    var expected = Observe(() => HistoricalPreview(fixture.Workspace, fixture.Source, point, true));
                    var actual = Observe(() => fixture.Workspace.MockMoveNode(fixture.Source, point, true));
                    AssertEquivalent(expected, actual);
                    Assert.AreEqual(before, Snapshot(fixture.Tree));
                    fixture.AssertRegistration();
                }
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void FreshCrossParentMinimumSizeAndFailureBehaviorRemainUnchanged(bool failedRead)
        {
            var fixture = new Fixture("balanced", 10);
            var point = fixture.Windows[0].ComputedRectangle.Center;
            var window = fixture.Adapters[0];
            window.Minimum = new Point(20000, 20000);
            window.ThrowMinimum = failedRead;
            var before = Snapshot(fixture.Tree);
            fixture.ResetReads();
            var expected = Observe(() => HistoricalPreview(fixture.Workspace, fixture.Source, point, true));
            int expectedReads = fixture.Reads;
            fixture.ResetReads();
            var actual = Observe(() => fixture.Workspace.MockMoveNode(fixture.Source, point, true));
            AssertEquivalent(expected, actual);
            Assert.AreEqual(expectedReads, fixture.Reads);
            Assert.IsTrue(fixture.Reads > 0, "The preflight must sample current native minimum sizes.");
            if (!failedRead) Assert.AreEqual(TilingError.TargetCannotFit, actual.Failure);
            Assert.AreEqual(before, Snapshot(fixture.Tree));
            fixture.AssertRegistration();
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void CallbackReorderingDuplicateGenerationsKeepsTheFirstMatchAfterArrange(bool hidden)
        {
            var fixture = CallbackFixture.Create(hidden);
            var before = Snapshot(fixture.Tree);
            fixture.Script.Reset();
            var expected = HistoricalPreview(fixture.Workspace, fixture.Source, new Point(-100, -100), true);
            int expectedEnumerations = fixture.Script.NodeEnumerations;
            int expectedClones = fixture.Script.Copies.Count;
            fixture.Script.Reset();
            var actual = fixture.Workspace.MockMoveNode(fixture.Source, new Point(-100, -100), true);
            Assert.AreEqual(expected, actual);
            Assert.AreEqual(hidden ? new Rectangle(600, 0, 1200, 800) : new Rectangle(0, 0, 600, 800), actual.preArrange);
            Assert.AreEqual(new Rectangle(0, 0, 600, 800), actual.postArrange);
            Assert.AreEqual(2, fixture.Script.Calls);
            Assert.AreEqual(expectedEnumerations, fixture.Script.NodeEnumerations, "Unknown Nodes callbacks must keep their original enumeration cadence.");
            Assert.AreEqual(expectedClones, fixture.Script.Copies.Count);
            Assert.AreEqual(before, Snapshot(fixture.Tree));
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void PlaceholderPreviewAndPlacementRetainCloneIsolation(bool placeOnPlaceholder)
        {
            var root = new SplitPanelNode { Orientation = PanelOrientation.Horizontal };
            var tree = new DesktopTree { Root = root, WorkArea = new Rectangle(0, 0, 1200, 800) };
            var first = new WindowNode(new PreviewWindow(1));
            var placeholder = new PlaceholderNode();
            var source = new WindowNode(new PreviewWindow(2));
            root.Attach(first);
            root.Attach(placeholder);
            root.Attach(source);
            tree.Measure();
            tree.Arrange();
            var workspace = new TilingWorkspace();
            var point = placeOnPlaceholder ? placeholder.ComputedRectangle.Center : new Point(-100, -100);
            var before = Snapshot(tree);
            var expected = HistoricalPreview(workspace, source, point, true);
            var actual = workspace.MockMoveNode(source, point, true);
            Assert.AreEqual(expected, actual);
            Assert.AreEqual(before, Snapshot(tree));
            Assert.AreSame(root, placeholder.Parent);
            Assert.AreSame(source, tree.FindNode(source.WindowReference));
            Assert.AreSame(first, tree.FindNode(first.WindowReference));
        }

        [DataTestMethod]
        [DataRow("flat", 10)]
        [DataRow("balanced", 25)]
        [DataRow("skewed", 50)]
        public void BuiltInPreviewRemovesAtLeastOneLeafEnumerationAllocationPerWindow(string shape, int count)
        {
            var fixture = new Fixture(shape, count);
            var point = fixture.Target.ComputedRectangle.Center;
            for (int i = 0; i < 20; i++)
            {
                HistoricalPreview(fixture.Workspace, fixture.Source, point, true);
                fixture.Workspace.MockMoveNode(fixture.Source, point, true);
            }
            long referenceBytes = 0, actualBytes = 0;
            for (int i = 0; i < 50; i++)
            {
                bool referenceFirst = i % 2 == 0;
                long before = GC.GetAllocatedBytesForCurrentThread();
                if (referenceFirst) HistoricalPreview(fixture.Workspace, fixture.Source, point, true);
                else fixture.Workspace.MockMoveNode(fixture.Source, point, true);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                if (referenceFirst) referenceBytes += allocated;
                else actualBytes += allocated;

                before = GC.GetAllocatedBytesForCurrentThread();
                if (referenceFirst) fixture.Workspace.MockMoveNode(fixture.Source, point, true);
                else HistoricalPreview(fixture.Workspace, fixture.Source, point, true);
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                if (referenceFirst) actualBytes += allocated;
                else referenceBytes += allocated;
            }
            Assert.IsTrue(actualBytes <= referenceBytes - count * 32L * 50,
                $"Actual preview {actualBytes} B must remove at least the known leaf-node arrays from historical model {referenceBytes} B.");
            fixture.AssertRegistration();
        }

        [DataTestMethod]
        [DataRow(false, 1)]
        [DataRow(false, 10)]
        [DataRow(false, 50)]
        [DataRow(true, 1)]
        [DataRow(true, 10)]
        [DataRow(true, 50)]
        public void BuiltInConstraintClearingDoesNotAllocatePerPanel(bool stack, int panelCount)
        {
            var tree = CreateConstraintChain(stack, panelCount, out var root, out var panels,
                out var firstMatch, out var laterMatch, out var window, out var adapter);
            tree.Measure();
            var children = panels.Select(panel => panel.Children.ToArray()).ToArray();
            var parents = Walk(root).Select(node => node.Parent).ToArray();

            TilingNode? source = null;
            bool canReuseSource = true;
            s_clearPreviewConstraints(root, firstMatch.GenerationID, ref source, ref canReuseSource);

            Assert.AreSame(firstMatch, source, "The first preorder generation match must remain reusable.");
            Assert.AreNotSame(laterMatch, source);
            Assert.IsTrue(canReuseSource);
            CollectionAssert.AreEqual(parents, Walk(root).Select(node => node.Parent).ToArray());
            for (int i = 0; i < panels.Length; i++)
                CollectionAssert.AreEqual(children[i], panels[i].Children.ToArray());
            foreach (var node in Walk(root))
            {
                Assert.AreEqual(new Point(), node.ContentMinSize);
                Assert.AreEqual(new Point(short.MaxValue, short.MaxValue), node.ContentMaxSize);
            }
            Assert.AreSame(window, tree.FindNode(adapter));

            for (int i = 0; i < 1_000; i++)
            {
                source = null;
                canReuseSource = true;
                s_clearPreviewConstraints(root, firstMatch.GenerationID, ref source, ref canReuseSource);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1_000; i++)
            {
                source = null;
                canReuseSource = true;
                s_clearPreviewConstraints(root, firstMatch.GenerationID, ref source, ref canReuseSource);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreSame(firstMatch, source);
            Assert.IsTrue(canReuseSource);
            Assert.AreEqual(0L, allocated,
                $"Clearing {panelCount} exact {(stack ? "stack" : "split")} panels allocated {allocated} bytes.");
        }

        [TestMethod]
        public void ConstraintClearingRetainsOuterListVersionCheckAfterCustomFallbackMutation()
        {
            var root = new SplitPanelNode();
            var tree = new DesktopTree { Root = root, WorkArea = new Rectangle(0, 0, 1200, 800) };
            var custom = new ConstraintMutationPanel();
            var visitedAdapter = new PreviewWindow(201) { Minimum = new Point(31, 37) };
            var visited = new WindowNode(visitedAdapter);
            var tailAdapter = new PreviewWindow(202) { Minimum = new Point(41, 43) };
            var tail = new WindowNode(tailAdapter);
            var addedAdapter = new PreviewWindow(203) { Minimum = new Point(47, 53) };
            var added = new WindowNode(addedAdapter);
            root.Attach(custom);
            custom.Attach(visited);
            root.Attach(tail);
            tree.Measure();
            added.Measure();
            var tailMinimum = tail.ContentMinSize;
            var addedMinimum = added.ContentMinSize;
            custom.OnDispose = () => root.Attach(added);

            TilingNode? source = null;
            bool canReuseSource = true;
            Assert.ThrowsException<InvalidOperationException>(() =>
                s_clearPreviewConstraints(root, tail.GenerationID, ref source, ref canReuseSource));

            Assert.AreEqual(1, custom.Disposals);
            Assert.IsFalse(canReuseSource);
            Assert.IsNull(source, "The unvisited tail must not be captured after the parent list changes.");
            Assert.AreEqual(new Point(), root.ContentMinSize);
            Assert.AreEqual(new Point(), custom.ContentMinSize);
            Assert.AreEqual(new Point(), visited.ContentMinSize);
            Assert.AreEqual(tailMinimum, tail.ContentMinSize);
            Assert.AreEqual(addedMinimum, added.ContentMinSize);
            CollectionAssert.AreEqual(new TilingNode[] { custom, tail, added }, root.Children.ToArray());
            Assert.AreSame(visited, tree.FindNode(visitedAdapter));
            Assert.AreSame(tail, tree.FindNode(tailAdapter));
            Assert.AreSame(added, tree.FindNode(addedAdapter));
        }

        private static DesktopTree CreateConstraintChain(bool stack, int panelCount, out PanelNode root,
            out PanelNode[] panels, out PlaceholderNode firstMatch, out PlaceholderNode laterMatch,
            out WindowNode window, out PreviewWindow adapter)
        {
            var result = new List<PanelNode>(panelCount);
            root = stack ? new StackPanelNode() : new SplitPanelNode();
            var tree = new DesktopTree { Root = root, WorkArea = new Rectangle(0, 0, 4096, 4096) };
            result.Add(root);
            var parent = root;
            for (int i = 1; i < panelCount; i++)
            {
                PanelNode child = stack ? new StackPanelNode() : new SplitPanelNode();
                parent.Attach(child);
                result.Add(child);
                parent = child;
            }
            firstMatch = new PlaceholderNode();
            laterMatch = (PlaceholderNode)firstMatch.Clone();
            adapter = new PreviewWindow(stack ? 301 + panelCount : 401 + panelCount) { Minimum = new Point(17, 19) };
            window = new WindowNode(adapter);
            parent.Attach(firstMatch);
            parent.Attach(window);
            parent.Attach(laterMatch);
            panels = result.ToArray();
            return tree;
        }

        [TestMethod]
        public void GenericPreviewCounterScenario()
        {
            string? requestedCase = Environment.GetEnvironmentVariable("FWM_PERF_GENERIC_CASE");
            if (string.IsNullOrWhiteSpace(requestedCase))
            {
                requestedCase = null;
            }
            else
            {
                requestedCase = requestedCase.ToLowerInvariant();
            }
            bool matchedRequestedCase = requestedCase == null;
            foreach (var shape in new[] { "flat", "balanced", "skewed" })
            {
                foreach (int count in new[] { 10, 25, 50 })
                {
                    if (requestedCase != null && requestedCase != $"{shape}-{count}")
                    {
                        continue;
                    }
                    matchedRequestedCase = true;
                    var fixture = new Fixture(shape, count);
                    var points = new[] { fixture.Windows[0].ComputedRectangle.Center, fixture.Target.ComputedRectangle.Center, fixture.Source.ComputedRectangle.Center, new Point(-100, -100) };
                    var expected = points.Select(point => HistoricalPreview(fixture.Workspace, fixture.Source, point, true)).ToArray();
                    var beforeTree = Snapshot(fixture.Tree);
                    for (int i = 0; i < 30; i++) fixture.Workspace.MockMoveNode(fixture.Source, points[i % points.Length], true);
                    fixture.ResetReads();
                    var results = new (Rectangle preArrange, Rectangle postArrange)[300];
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    long start = Stopwatch.GetTimestamp();
                    for (int i = 0; i < results.Length; i++)
                        results[i] = fixture.Workspace.MockMoveNode(fixture.Source, points[i % points.Length], true);
                    long elapsed = Stopwatch.GetTimestamp() - start;
                    long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                    var digest = new StringBuilder();
                    for (int i = 0; i < results.Length; i++)
                    {
                        Assert.AreEqual(expected[i % points.Length], results[i]);
                        digest.Append(results[i].preArrange).Append('|').Append(results[i].postArrange).AppendLine();
                    }
                    Assert.AreEqual(beforeTree, Snapshot(fixture.Tree));
                    fixture.AssertRegistration();
                    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(digest.ToString())));
                    Console.WriteLine($"PERFCOUNTER generic-preview-{shape}-{count} geometry-digest {hash}");
                    Console.WriteLine($"PERFCOUNTER generic-preview-{shape}-{count} allocated-bytes {bytes}");
                    Console.WriteLine($"PERFCOUNTER generic-preview-{shape}-{count} elapsed-ticks {elapsed}");
                    Console.WriteLine($"PERFCOUNTER generic-preview-{shape}-{count} timestamp-frequency {Stopwatch.Frequency}");
                    Console.WriteLine($"PERFCOUNTER generic-preview-{shape}-{count} previews {results.Length}");
                    Console.WriteLine($"PERFCOUNTER generic-preview-{shape}-{count} minimum-reads {fixture.Reads}");
                }
            }
            Assert.IsTrue(matchedRequestedCase, $"Unknown FWM_PERF_GENERIC_CASE: {requestedCase}");
        }

        // Fixed reference copied from MoveNode before target-search optimization.
        // Expected selection never calls the current MoveNode implementation.
        private static TilingNode? HistoricalMoveTarget(TilingNode node, Point pt)
        {
            return node.Desktop!.Root!.Windows
                .Where(x => x != node)
                .Concat(node.Desktop.Root.Nodes.Where(x => x.Type == TilingNodeType.Placeholder))
                .FirstOrDefault(x => x.ComputedRectangle.Contains(pt)) ?? node.Desktop.Root!.Nodes
                    .OfType<PanelNode>()
                    .Where(x => x != node)
                    .FirstOrDefault(x => Rectangle.OffsetAndSize(
                        x.ComputedRectangle.Left - x.Padding.Left,
                        x.ComputedRectangle.Top - x.Padding.Top,
                        x.ComputedRectangle.Width + x.Padding.Left + x.Padding.Right,
                        x.Padding.Top).Contains(pt));
        }

        // Differential reference copied from the pre-lookup MoveNodeTest body.
        // This model is never the measured production path in the counter scenario.
        private static bool HistoricalMoveNodeTest(TilingWorkspace workspace, TilingNode node, PanelNode newParentNode, Point pt,
            Action? onEnumeration = null)
        {
            var rootClone = (PanelNode)node.Desktop!.Root!.Clone();
            onEnumeration?.Invoke();
            var nodeClone = rootClone.Nodes.First(candidate => candidate.GenerationID == node.GenerationID);
            onEnumeration?.Invoke();
            var newParentClone = (PanelNode)rootClone.Nodes.First(candidate => candidate.GenerationID == newParentNode.GenerationID);
            var testTree = new DesktopTree { Root = rootClone, WorkArea = node.Desktop.WorkArea };
            var nodeCloneParent = nodeClone.Parent!;
            var newParentIsAncestor = nodeCloneParent.Ancestors.Contains(newParentClone);
            nodeCloneParent.Detach(nodeClone);
            nodeCloneParent.Cleanup(collapse: workspace.AutoCollapse);
            if (newParentClone.Desktop == null && newParentIsAncestor) return true;
            newParentClone.Attach(nodeClone);
            newParentClone.RemovePlaceholders();
            testTree.Measure();
            testTree.Arrange();
            return newParentClone.ComputedRectangle.Contains(pt);
        }

        // Differential reference copied from the C7 MockMoveNode body. This is a
        // test model, not a production benchmark. The counter measures only the
        // actual public workspace method; the model supplies expected rectangles.
        private static (Rectangle preArrange, Rectangle postArrange) HistoricalPreview(TilingWorkspace workspace, TilingNode sourceNode, Point pt, bool allowNesting)
        {
            var desktop = sourceNode.Desktop!;
            var rootClone = (PanelNode)desktop.Root!.Clone();
            var sourceNodeClone = rootClone.Nodes.First(x => x.GenerationID == sourceNode.GenerationID);
            var testTree = new DesktopTree { Root = rootClone, WorkArea = desktop.WorkArea };
            workspace.MoveNode(sourceNodeClone, pt, allowNesting);
            var unconstrainedParentClone = (PanelNode)sourceNodeClone.Parent!.Clone();
            try { testTree.Arrange(); }
            catch (UnsatisfiableFlexConstraintsException) { throw new TilingFailedException(TilingError.NoValidPlacementExists); }
            foreach (var node in unconstrainedParentClone.Nodes) node.ClearConstraints();
            unconstrainedParentClone.Padding = new();
            try { unconstrainedParentClone.Arrange(new RectangleF(unconstrainedParentClone.ComputedRectangle)); }
            catch (UnsatisfiableFlexConstraintsException) { throw new TilingFailedException(TilingError.NoValidPlacementExists); }
            var unconstrainedSourceNodeClone = unconstrainedParentClone.Nodes.First(x => x.GenerationID == sourceNode.GenerationID);
            return (unconstrainedSourceNodeClone.ComputedRectangle, sourceNodeClone.ComputedRectangle);
        }

        private sealed record Outcome((Rectangle Pre, Rectangle Post) Rectangle, Type? Error, string? Message, TilingError? Failure);

        private static Outcome Observe(Func<(Rectangle, Rectangle)> action)
        {
            try { return new(action(), null, null, null); }
            catch (Exception exception) { return new(default, exception.GetType(), exception.Message, (exception as TilingFailedException)?.FailReason); }
        }

        private static void AssertEquivalent(Outcome expected, Outcome actual)
        {
            Assert.AreEqual(expected.Error, actual.Error);
            Assert.AreEqual(expected.Message, actual.Message);
            Assert.AreEqual(expected.Failure, actual.Failure);
            Assert.AreEqual(expected.Rectangle, actual.Rectangle);
        }

        private static IEnumerable<TilingNode> Walk(TilingNode node)
        {
            yield return node;
            if (node is PanelNode panel)
                foreach (var child in panel.Children)
                    foreach (var descendant in Walk(child)) yield return descendant;
        }

        private static string Snapshot(DesktopTree tree)
        {
            var result = new StringBuilder().Append(tree.WorkArea);
            foreach (var node in Walk(tree.Root!))
            {
                result.Append('|').Append(node.GenerationID).Append(':').Append(node.Parent?.GenerationID)
                    .Append(':').Append(node.GetType().Name).Append(':').Append(node.ComputedRectangle)
                    .Append(':').Append(node.ContentMinSize).Append(':').Append(node.ContentMaxSize).Append(':').Append(node.Padding);
                if (node is WindowNode window) result.Append(':').Append(window.WindowReference.Handle);
                if (node is PanelNode panel) result.Append(':').Append(panel.Spacing);
                if (node is SplitPanelNode split)
                {
                    result.Append(':').Append(split.Orientation).Append(':').Append(split.ContainerLength.ToString("R", CultureInfo.InvariantCulture));
                    foreach (var child in split.Children)
                    {
                        var c = split.GetChildConstraints(child);
                        result.Append(':').Append(c.Width.ToString("R", CultureInfo.InvariantCulture))
                            .Append('/').Append(c.MinWidth.ToString("R", CultureInfo.InvariantCulture))
                            .Append('/').Append(c.MaxWidth.ToString("R", CultureInfo.InvariantCulture));
                    }
                }
            }
            return result.ToString();
        }

        private sealed class MoveTargetFixture
        {
            public static readonly Rectangle Outside = new(700, 700, 800, 800);
            private static readonly Rectangle Hit = new(400, 200, 600, 400);
            public readonly DesktopTree Tree;
            public readonly WindowNode Source = new(new PreviewWindow(1));
            public readonly WindowNode FirstWindow = new(new PreviewWindow(2));
            public readonly WindowNode SecondWindow = new(new PreviewWindow(3));
            public readonly PlaceholderNode FirstPlaceholder = new();
            public readonly PlaceholderNode SecondPlaceholder = new();
            public readonly SplitPanelNode FirstPanel = new() { Padding = new Rectangle(0, 20, 0, 0) };
            public readonly SplitPanelNode SecondPanel = new() { Padding = new Rectangle(0, 20, 0, 0) };

            public MoveTargetFixture(SplitPanelNode root, bool nested = false)
            {
                root.Padding = new Rectangle(0, 20, 0, 0);
                var treeRoot = nested ? new SplitPanelNode() : root;
                Tree = new DesktopTree { Root = treeRoot, WorkArea = new Rectangle(0, 0, 1000, 1000) };
                if (nested) treeRoot.Attach(root);
                root.Attach(FirstPlaceholder);
                root.Attach(FirstPanel);
                root.Attach(FirstWindow);
                root.Attach(SecondPlaceholder);
                root.Attach(SecondPanel);
                root.Attach(SecondWindow);
                root.Attach(Source);
                Tree.Measure();
                Tree.Arrange();
            }

            public (TilingNode Source, Point Point, TilingNode? Expected) Prepare(string scenario)
            {
                foreach (var node in new TilingNode[] { FirstPlaceholder, FirstPanel, FirstWindow, SecondPlaceholder, SecondPanel, SecondWindow, Source })
                    node.Arrange(new RectangleF(Outside));
                var point = new Point(500, 210);
                if (scenario is "window" or "placeholder" or "panel")
                {
                    FirstPanel.Arrange(new RectangleF(Hit));
                    SecondPanel.Arrange(new RectangleF(Hit));
                    if (scenario != "panel")
                    {
                        FirstPlaceholder.Arrange(new RectangleF(Hit));
                        SecondPlaceholder.Arrange(new RectangleF(Hit));
                    }
                    if (scenario == "window")
                    {
                        FirstWindow.Arrange(new RectangleF(Hit));
                        SecondWindow.Arrange(new RectangleF(Hit));
                    }
                    return (Source, point, scenario == "window" ? FirstWindow : scenario == "placeholder" ? FirstPlaceholder : FirstPanel);
                }
                if (scenario == "source-placeholder")
                {
                    FirstPlaceholder.Arrange(new RectangleF(Hit));
                    return (FirstPlaceholder, point, FirstPlaceholder);
                }
                if (scenario == "source-panel")
                {
                    FirstPanel.Arrange(new RectangleF(Hit));
                    return (FirstPanel, point, null);
                }
                if (scenario == "source-window")
                {
                    Source.Arrange(new RectangleF(Hit));
                    return (Source, point, null);
                }
                FirstPanel.Arrange(new RectangleF(new Rectangle(400, -20, 600, 200)));
                return (Source, new Point(500, -10), Tree.Root);
            }

            public void AssertRegistration()
            {
                foreach (var node in Walk(Tree.Root!).OfType<WindowNode>())
                    Assert.AreSame(node, Tree.FindNode(node.WindowReference));
            }
        }

        private sealed class MoveTargetEnumerationScript
        {
            public bool Enabled;
            public bool ReverseSecondNodes;
            public int NodeGetters;
            public string? FailureAt;
            public readonly InvalidOperationException Failure = new("Move-target enumeration failure.");
            public readonly List<string> Events = [];
            public Action? BeforeWindowsGetter;
            public void Reset() { Enabled = true; NodeGetters = 0; Events.Clear(); }
            public void Record(string stage, long? generation = null)
            {
                Events.Add(generation.HasValue ? stage + ":" + generation.Value : stage);
                if (stage == FailureAt) throw Failure;
            }
        }

        private sealed class MoveTargetEnumerationPanel(MoveTargetEnumerationScript script) : SplitPanelNode
        {
            public override IEnumerable<WindowNode> Windows
            {
                get
                {
                    if (!script.Enabled) return base.Windows;
                    script.Record("windows-get");
                    script.BeforeWindowsGetter?.Invoke();
                    return EnumerateWindows();
                }
            }
            public override IEnumerable<TilingNode> Nodes
            {
                get
                {
                    if (!script.Enabled) return base.Nodes;
                    script.NodeGetters++;
                    script.Record("nodes-get");
                    return EnumerateNodes(script.NodeGetters);
                }
            }
            private IEnumerable<WindowNode> EnumerateWindows()
            {
                try
                {
                    script.Record("windows-start");
                    foreach (var window in base.Windows)
                    {
                        script.Record("windows-yield", window.GenerationID);
                        yield return window;
                    }
                }
                finally { script.Record("windows-dispose"); }
            }
            private IEnumerable<TilingNode> EnumerateNodes(int getter)
            {
                try
                {
                    script.Record("nodes-start");
                    var nodes = script.ReverseSecondNodes && getter == 2 ? base.Nodes.Reverse() : base.Nodes;
                    foreach (var node in nodes)
                    {
                        script.Record("nodes-yield", node.GenerationID);
                        yield return node;
                    }
                }
                finally { script.Record("nodes-dispose"); }
            }
        }

        private sealed class AncestorEqualityScript
        {
            public Func<AncestorEqualityPanel, object?, bool> Compare { get; set; } = (_, _) => false;
            public List<string> Calls { get; } = [];

            public bool Invoke(AncestorEqualityPanel panel, object? other)
            {
                Calls.Add(panel.Name);
                return Compare(panel, other);
            }
        }

        private sealed class AncestorEqualityPanel(string name, AncestorEqualityScript script) : SplitPanelNode
        {
            public string Name { get; } = name;

            public override bool Equals(object? other) => script.Invoke(this, other);

            public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
        }

        private sealed class PathEqualityScript
        {
            public Func<string, TilingNode, object?, bool> Compare { get; set; } = (_, _, _) => false;
            public bool Enabled { get; set; }
            public List<string> Calls { get; } = [];

            public bool Invoke(string name, TilingNode current, object? other)
            {
                if (!Enabled) return ReferenceEquals(current, other);
                Calls.Add(name);
                return Compare(name, current, other);
            }
        }

        private sealed class PathEqualityPanel(string name, PathEqualityScript script) : SplitPanelNode
        {
            public string Name { get; } = name;
            public override bool Equals(object? other) => script.Invoke(Name, this, other);
            public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
        }

        private sealed class PathEqualityPlaceholder(string name, PathEqualityScript script) : PlaceholderNode
        {
            public override bool Equals(object? other) => script.Invoke(name, this, other);
            public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
        }

        private sealed class PublicRecursionGuardFixture
        {
            public TilingWorkspace Workspace { get; } = new();
            public PathEqualityScript Script { get; } = new();
            public DesktopTree Tree { get; }
            public PathEqualityPanel Upper { get; }
            public PathEqualityPanel Source { get; }
            public PathEqualityPanel Middle { get; }
            public PathEqualityPlaceholder Target { get; }
            public WindowNode Retained { get; } = new(new PreviewWindow(910));

            public PublicRecursionGuardFixture()
            {
                var root = new SplitPanelNode { Orientation = PanelOrientation.Horizontal };
                Upper = new PathEqualityPanel("upper", Script);
                Source = new PathEqualityPanel("source", Script);
                Middle = new PathEqualityPanel("middle", Script);
                Target = new PathEqualityPlaceholder("target", Script);
                Tree = new DesktopTree { Root = root, WorkArea = new Rectangle(0, 0, 1200, 800) };
                root.Attach(Upper);
                root.Attach(Retained);
                Upper.Attach(Source);
                Source.Attach(Middle);
                Middle.Attach(Target);
                Tree.Measure();
                Tree.Arrange();
            }

            public void AssertRegistration() => Assert.AreSame(Retained, Tree.FindNode(Retained.WindowReference));
        }

        private sealed class DerivedAncestorRoot : SplitPanelNode { }

        private sealed class DerivedStackPanelNode : StackPanelNode { }

        private sealed class AncestorAllocationFixture
        {
            public TilingWorkspace Workspace { get; } = new();
            public DesktopTree Tree { get; }
            public WindowNode Source { get; } = new(new PreviewWindow(907));
            public WindowNode Retained { get; } = new(new PreviewWindow(908));
            public SplitPanelNode Target { get; } = new();

            public AncestorAllocationFixture()
            {
                var root = new DerivedAncestorRoot { Orientation = PanelOrientation.Horizontal };
                var sourceParent = new SplitPanelNode();
                Tree = new DesktopTree { Root = root, WorkArea = new Rectangle(0, 0, 1200, 800) };
                root.Attach(sourceParent);
                root.Attach(Target);
                sourceParent.Attach(Source);
                Target.Attach(Retained);
                Tree.Measure();
                Tree.Arrange();
            }
        }

        private sealed class GenerationLookupFixture
        {
            public readonly TilingWorkspace Workspace = new();
            public readonly DesktopTree Tree;
            public readonly TilingNode Source;
            public readonly PanelNode Target;
            public readonly Dictionary<int, PreviewWindow> Adapters = [];
            public readonly List<int> ReadOrder = [];
            private readonly GenerationEnumerationScript? m_script;

            public GenerationLookupFixture(bool sourceFirst, GenerationEnumerationScript? script = null, bool hideSelf = false, bool nested = false)
            {
                m_script = script;
                PanelNode branch = script == null
                    ? new SplitPanelNode { Orientation = PanelOrientation.Horizontal }
                    : new GenerationEnumerationPanel(script, hideSelf) { Orientation = PanelOrientation.Horizontal };
                PanelNode root = nested ? new SplitPanelNode { Orientation = PanelOrientation.Horizontal } : branch;
                Tree = new DesktopTree { Root = root, WorkArea = new Rectangle(0, 0, 12000, 800) };
                if (nested) root.Attach(branch);
                foreach (int id in Enumerable.Range(100, 12)) branch.Attach(CreateWindow(id));
                branch.Attach(new PlaceholderNode());
                var stack = new StackPanelNode();
                branch.Attach(stack);
                stack.Attach(CreateWindow(112));
                var firstSource = new SplitPanelNode { Orientation = PanelOrientation.Vertical };
                var secondSource = (SplitPanelNode)firstSource.Clone();
                var firstTarget = new SplitPanelNode { Orientation = PanelOrientation.Vertical };
                var secondTarget = (SplitPanelNode)firstTarget.Clone();
                branch.Attach(sourceFirst ? firstSource : firstTarget);
                branch.Attach(CreateWindow(5));
                branch.Attach(sourceFirst ? firstTarget : firstSource);
                branch.Attach(secondSource);
                branch.Attach(secondTarget);
                firstSource.Attach(CreateWindow(1));
                firstTarget.Attach(CreateWindow(2));
                secondSource.Attach(CreateWindow(3));
                secondTarget.Attach(CreateWindow(4));
                Source = secondSource;
                Target = secondTarget;
                Tree.Measure();
                Tree.Arrange();
            }

            private WindowNode CreateWindow(int id)
            {
                var adapter = new PreviewWindow(id) { OnMinimumRead = () => ReadOrder.Add(id) };
                Adapters.Add(id, adapter);
                return new WindowNode(adapter);
            }

            public void Reset()
            {
                m_script?.Reset();
                ReadOrder.Clear();
            }

            public void AssertRegistration()
            {
                foreach (var adapter in Adapters.Values)
                    Assert.AreSame(Walk(Tree.Root!).OfType<WindowNode>().Single(node => node.WindowReference == adapter), Tree.FindNode(adapter));
            }
        }

        private sealed class GenerationEnumerationScript
        {
            public int GetterCalls;
            public int IteratorStarts;
            public int IteratorDisposals;
            public long HiddenGeneration;
            public Action<GenerationEnumerationPanel, int>? BeforeEnumeration;
            public List<long> YieldedGenerations { get; } = [];
            public List<GenerationEnumerationPanel> Copies { get; } = [];

            public void Reset()
            {
                GetterCalls = IteratorStarts = IteratorDisposals = 0;
                YieldedGenerations.Clear();
                Copies.Clear();
            }
        }

        private sealed class GenerationEnumerationPanel(GenerationEnumerationScript script, bool hideSelf) : SplitPanelNode
        {
            public override IEnumerable<TilingNode> Nodes
            {
                get
                {
                    int lookup = ++script.GetterCalls;
                    return Enumerate(lookup);
                }
            }

            private IEnumerable<TilingNode> Enumerate(int lookup)
            {
                script.IteratorStarts++;
                try
                {
                    script.BeforeEnumeration?.Invoke(this, lookup);
                    if (!hideSelf && GenerationID != script.HiddenGeneration)
                    {
                        script.YieldedGenerations.Add(GenerationID);
                        yield return this;
                    }
                    var children = lookup % 2 == 0 ? Children.Reverse() : Children;
                    foreach (var child in children)
                    {
                        foreach (var node in child.Nodes)
                        {
                            if (node.GenerationID == script.HiddenGeneration) continue;
                            script.YieldedGenerations.Add(node.GenerationID);
                            yield return node;
                        }
                    }
                }
                finally
                {
                    script.IteratorDisposals++;
                }
            }

            public override object Clone()
            {
                var copy = (GenerationEnumerationPanel)base.Clone();
                script.Copies.Add(copy);
                return copy;
            }
        }

        private sealed class Fixture
        {
            public readonly TilingWorkspace Workspace = new();
            public readonly DesktopTree Tree;
            public readonly PreviewWindow[] Adapters;
            public readonly WindowNode[] Windows;
            public TilingNode Source => Windows[^1];
            public WindowNode Target => Windows[^2];
            public int Reads => Adapters.Sum(window => window.MinimumReads);
            public Point[] Points => [Target.ComputedRectangle.Center, Windows[0].ComputedRectangle.Center, Source.ComputedRectangle.Center, new(-100, -100)];

            public Fixture(string shape, int count)
            {
                Adapters = Enumerable.Range(1, count).Select(id => new PreviewWindow(id)).ToArray();
                Windows = Adapters.Select(window => new WindowNode(window)).ToArray();
                PanelNode root = shape == "stack" ? new StackPanelNode { Spacing = 2 }
                    : new SplitPanelNode { Orientation = PanelOrientation.Horizontal, Spacing = 2 };
                Tree = new DesktopTree { Root = root, WorkArea = new Rectangle(0, 0, 8192, 8192) };
                if (shape is "flat" or "stack") foreach (var window in Windows.Take(count - 2)) root.Attach(window);
                else AddBranch(root, Windows.Take(count - 2).ToArray(), shape == "skewed", 0);
                root.Attach(Target);
                root.Attach(Source);
                Tree.Measure();
                Tree.Arrange();
            }

            private static void AddBranch(PanelNode parent, WindowNode[] windows, bool skewed, int depth)
            {
                if (windows.Length == 1) { parent.Attach(windows[0]); return; }
                if (skewed)
                {
                    for (int i = 0; i < Math.Min(windows.Length, 25); i++)
                    {
                        var next = new SplitPanelNode { Orientation = i % 2 == 0 ? PanelOrientation.Vertical : PanelOrientation.Horizontal };
                        parent.Attach(next);
                        parent = next;
                    }
                    foreach (var window in windows) parent.Attach(window);
                    return;
                }
                var branch = new SplitPanelNode { Orientation = depth % 2 == 0 ? PanelOrientation.Vertical : PanelOrientation.Horizontal };
                parent.Attach(branch);
                int half = skewed ? 1 : windows.Length / 2;
                AddBranch(branch, windows.Take(half).ToArray(), skewed, depth + 1);
                AddBranch(branch, windows.Skip(half).ToArray(), skewed, depth + 1);
            }

            public void ResetReads() { foreach (var window in Adapters) window.MinimumReads = 0; }
            public void AssertRegistration()
            {
                foreach (var node in Windows) Assert.AreSame(node, Tree.FindNode(node.WindowReference));
            }
        }

        private sealed class CallbackFixture
        {
            public readonly TilingWorkspace Workspace = new();
            public required DesktopTree Tree;
            public required TilingNode Source;
            public required ReorderingFunction Script;

            public static CallbackFixture Create(bool hidden)
            {
                var script = new ReorderingFunction(hidden);
                var callback = new CallbackPanel(script, hidden);
                PanelNode root = hidden ? new SplitPanelNode { Orientation = PanelOrientation.Horizontal } : callback;
                var tree = new DesktopTree { Root = root, WorkArea = new Rectangle(0, 0, 1200, 800) };
                var source = new SplitPanelNode { Orientation = PanelOrientation.Vertical };
                var duplicate = (SplitPanelNode)source.Clone();
                root.Attach(source);
                if (hidden) { root.Attach(callback); callback.Attach(duplicate); }
                else root.Attach(duplicate);
                source.Attach(new WindowNode(new PreviewWindow(1)));
                duplicate.Attach(new WindowNode(new PreviewWindow(2)));
                tree.Measure();
                tree.Arrange();
                return new CallbackFixture { Tree = tree, Source = source, Script = script };
            }
        }

        private sealed class CallbackPanel(ReorderingFunction script, bool hidden) : LayoutFunctionNode(script)
        {
            public override IEnumerable<TilingNode> Nodes
            {
                get
                {
                    script.NodeEnumerations++;
                    return hidden ? Children.SelectMany(child => child.Nodes) : base.Nodes;
                }
            }
            public override object Clone()
            {
                var copy = (CallbackPanel)base.Clone();
                script.Copies.Add(copy);
                return copy;
            }
        }

        private sealed class ReorderingFunction(bool hidden) : ILayoutFunction
        {
            public int Calls;
            public int NodeEnumerations;
            public List<CallbackPanel> Copies { get; } = [];
            public void Reset() { Calls = 0; NodeEnumerations = 0; Copies.Clear(); }
            public IReadOnlyList<Rectangle> Execute(Rectangle area, IEnumerable<Constraints> constraints)
            {
                Calls++;
                if (Calls == 2 && Copies.Count != 0)
                {
                    var parent = hidden ? Copies[^1].Parent! : Copies[^1];
                    parent.Move(0, 1);
                }
                int count = constraints.Count();
                return Enumerable.Range(0, count).Select(index =>
                    new Rectangle(area.Left + area.Width * index / count, area.Top, area.Left + area.Width * (index + 1) / count, area.Bottom)).ToArray();
            }
        }

        private sealed class ConstraintMutationPanel : SplitPanelNode
        {
            public Action? OnDispose;
            public int Disposals;

            public override IEnumerable<TilingNode> Nodes => EnumerateNodes();

            private IEnumerable<TilingNode> EnumerateNodes()
            {
                try
                {
                    yield return this;
                    foreach (var child in Children)
                        foreach (var descendant in child.Nodes)
                            yield return descendant;
                }
                finally
                {
                    Disposals++;
                    var callback = OnDispose;
                    OnDispose = null;
                    callback?.Invoke();
                }
            }
        }

        private sealed class PreviewWindow(int id) : IWindow
        {
            public int MinimumReads;
            public Action? OnMinimumRead;
            public Point Minimum;
            public bool ThrowMinimum;
            public object SyncRoot { get; } = new();
            public IWorkspace Workspace => throw Unsupported();
            public string Title => "preview";
            public Rectangle Position => new(0, 0, 640, 480);
            public WindowState State => WindowState.Restored;
            public Point? MinSize { get { MinimumReads++; OnMinimumRead?.Invoke(); if (ThrowMinimum) throw new InvalidWindowReferenceException(Handle); return Minimum; } }
            public Point? MaxSize => null;
            public Rectangle FrameMargins => new();
            public bool CanResize => true;
            public bool CanMove => true;
            public bool CanReorder => true;
            public bool CanMinimize => true;
            public bool CanMaximize => true;
            public bool CanClose => true;
            public bool IsTopmost => false;
            public bool IsFocused => false;
            public bool IsAlive => true;
            public IntPtr Handle { get; } = new(id);
            public bool Equals(IWindow? other) => ReferenceEquals(this, other);
            public override bool Equals(object? other) => ReferenceEquals(this, other);
            public override int GetHashCode() => Handle.GetHashCode();
            public Process GetProcess() => throw Unsupported();
            public IWindow? GetPreviousWindow() => throw Unsupported();
            public IWindow? GetNextWindow() => throw Unsupported();
            public void Close() => throw Unsupported();
            public void SetPosition(Rectangle position) => throw Unsupported();
            public void SetState(WindowState state) => throw Unsupported();
            public void SetTopmost(bool topmost) => throw Unsupported();
            public void InsertAfter(IWindow other) => throw Unsupported();
            public void SendToBack() => throw Unsupported();
            public void BringToFront() => throw Unsupported();
            public bool RequestFocus() => throw Unsupported();
            public event EventHandler<WindowPositionChangedEventArgs>? PositionChangeStart { add => throw Unsupported(); remove => throw Unsupported(); }
            public event EventHandler<WindowPositionChangedEventArgs>? PositionChangeEnd { add => throw Unsupported(); remove => throw Unsupported(); }
            public event EventHandler<WindowPositionChangedEventArgs>? PositionChanged { add => throw Unsupported(); remove => throw Unsupported(); }
            public event EventHandler<WindowStateChangedEventArgs>? StateChanged { add => throw Unsupported(); remove => throw Unsupported(); }
            public event EventHandler<WindowTopmostChangedEventArgs>? TopmostChanged { add => throw Unsupported(); remove => throw Unsupported(); }
            public event EventHandler<WindowFocusChangedEventArgs>? GotFocus { add => throw Unsupported(); remove => throw Unsupported(); }
            public event EventHandler<WindowFocusChangedEventArgs>? LostFocus { add => throw Unsupported(); remove => throw Unsupported(); }
            public event EventHandler<WindowChangedEventArgs>? Added { add => throw Unsupported(); remove => throw Unsupported(); }
            public event EventHandler<WindowChangedEventArgs>? Removed { add => throw Unsupported(); remove => throw Unsupported(); }
            public event EventHandler<WindowChangedEventArgs>? Destroyed { add => throw Unsupported(); remove => throw Unsupported(); }
            public event EventHandler<WindowTitleChangedEventArgs>? TitleChanged { add => throw Unsupported(); remove => throw Unsupported(); }
            private static NotSupportedException Unsupported() => new("The generic preview fixture must not invoke native operations.");
        }
    }
}
