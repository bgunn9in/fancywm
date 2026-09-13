#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using FancyWM.AlgorithmicLayouts;
using FancyWM.Layouts.Tiling;
using FancyWM.Models;
using FancyWM.Tests.TestUtilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    public partial class MasterSatelliteDiagnosticReuseTest
    {
        [DataTestMethod]
        [DataRow("remove")]
        [DataRow("promote")]
        [DataRow("reorder")]
        [DataRow("previous")]
        [DataRow("next")]
        [DataRow("set-side")]
        [DataRow("swap-side")]
        [DataRow("orientation")]
        [DataRow("ratio")]
        [DataRow("reset-ratio")]
        [DataRow("capture-ratio")]
        [DataRow("relayout")]
        public void OrdinaryWorkspaceMutationReusesOnlyItsImmediateEngineInvariant(string command)
        {
            var fixture = new WorkspaceFixture(4);
            if (command == "reset-ratio")
            {
                Assert.IsTrue(fixture.Engine.SetRequestedMasterRatio(
                    fixture.Tree, fixture.State, fixture.Settings, 0.71).Succeeded);
            }
            fixture.Workspace.SetFocus(fixture.Windows[^1]);
            var originalNodes = fixture.Windows.Select(fixture.Tree.FindNode).ToArray();
            var originalPositions = fixture.Windows.Select(fixture.Workspace.GetOriginalPosition).ToArray();
            var directTree = new DesktopTree
            {
                WorkArea = fixture.Tree.WorkArea,
                Root = (PanelNode)fixture.Tree.Root!.Clone(),
            };
            var directState = fixture.State.Clone();
            int minSizeReads = 0;
            foreach (var window in fixture.Windows)
            {
                Mock.Get(window).SetupGet(x => x.MinSize).Returns(() =>
                {
                    minSizeReads++;
                    return new Point(0, 0);
                });
            }

            fixture.Reads.Children = 0;
            Assert.IsTrue(fixture.Engine.ValidateInvariant(fixture.Tree, fixture.State, fixture.Settings).IsValid);
            int inputValidationReads = fixture.Reads.Children;
            fixture.Reads.Children = 0;
            var direct = ApplyDirectCommand(fixture, directTree, directState, command);
            int directChildReads = fixture.Reads.Children;
            int directMinSizeReads = minSizeReads;
            fixture.Reads.Children = 0;
            minSizeReads = 0;

            var result = ApplyWorkspaceCommand(fixture, command);

            int workspaceChildReads = fixture.Reads.Children;
            int workspaceMinSizeReads = minSizeReads;
            Assert.IsTrue(direct.Succeeded && direct.Changed, direct.Message);
            Assert.IsTrue(result.Succeeded && result.Changed, result.Message);
            AssertSnapshot(direct.Before, result.Before);
            AssertSnapshot(direct.After, result.After);
            Assert.AreEqual(direct.Invariant.TreeDescription, result.Invariant.TreeDescription);
            CollectionAssert.AreEqual(direct.Invariant.Violations.ToArray(), result.Invariant.Violations.ToArray());
            Assert.AreEqual(result.Before.Revision + 1, fixture.State.Revision);
            Assert.AreEqual(directMinSizeReads, workspaceMinSizeReads,
                "The wrapper may reuse only the immutable structural result, never detached or live constraint sampling.");
            Assert.IsTrue(workspaceMinSizeReads > 0);
            for (int i = 0; i < fixture.Windows.Length; i++)
            {
                var window = fixture.Windows[i];
                var expectedNode = directTree.FindNode(window);
                var actualNode = fixture.Tree.FindNode(window);
                if (expectedNode == null)
                {
                    Assert.IsNull(actualNode);
                    Assert.IsFalse(fixture.Workspace.HasWindow(window));
                    Assert.ThrowsException<KeyNotFoundException>(() => fixture.Workspace.GetOriginalPosition(window));
                    continue;
                }
                Assert.AreSame(originalNodes[i], actualNode);
                Assert.AreSame(window, actualNode!.WindowReference);
                Assert.AreEqual(expectedNode.GenerationID, actualNode.GenerationID);
                Assert.AreEqual(expectedNode.ComputedRectangle, actualNode.ComputedRectangle);
                Assert.IsTrue(fixture.Workspace.HasWindow(window));
                Assert.AreEqual(originalPositions[i], fixture.Workspace.GetOriginalPosition(window));
            }
            Assert.AreSame(fixture.Tree.FindNode(fixture.Windows[^1]), fixture.Workspace.GetFocus(fixture.Desktop));
            Assert.AreEqual(directChildReads + inputValidationReads, workspaceChildReads,
                "The ordinary wrapper must retain its input validation and reuse the engine's immediately returned final invariant.");
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void RecoveryDiagnosticBoundaryStillReceivesFreshWorkspaceValidation(bool rebalance)
        {
            WorkspaceFixture? fixture = null;
            CountingPanel? callbackRoot = null;
            WindowNode?[]? callbackNodes = null;
            int callbacks = 0;
            fixture = new WorkspaceFixture(4, message =>
            {
                if (!message.StartsWith("Master + Satellites recovery before:", StringComparison.Ordinal)) { return; }
                callbacks++;
                var oldRoot = (SplitPanelNode)fixture!.Tree.Root!;
                var children = oldRoot.Children.ToArray();
                foreach (var child in children) { oldRoot.Detach(child); }
                callbackRoot = new CountingPanel(fixture.Reads)
                {
                    Orientation = oldRoot.Orientation,
                    Padding = oldRoot.Padding,
                    Spacing = oldRoot.Spacing,
                };
                fixture.Tree.Root = callbackRoot;
                foreach (var child in children) { callbackRoot.Attach(child); }
                callbackNodes = fixture.Windows.Select(fixture.Tree.FindNode).ToArray();
                fixture.Reads.Children = 0;
            });
            ((SplitPanelNode)fixture.Tree.Root!).Orientation = PanelOrientation.Vertical;
            long beforeRevision = fixture.State.Revision;

            var result = rebalance
                ? fixture.Workspace.RebalanceMasterSatelliteLayout(fixture.Desktop, fixture.State, fixture.Settings)
                : fixture.Workspace.NormalizeMasterSatelliteLayout(fixture.Desktop, fixture.State, fixture.Settings);

            int afterCallbackReads = fixture.Reads.Children;
            Assert.IsTrue(result.Succeeded && result.Changed, result.Message);
            Assert.AreEqual(1, callbacks);
            Assert.AreSame(callbackRoot, fixture.Tree.Root);
            Assert.AreEqual(beforeRevision + 1, fixture.State.Revision);
            CollectionAssert.AreEqual(callbackNodes!, fixture.Windows.Select(fixture.Tree.FindNode).ToArray());
            fixture.Reads.Children = 0;
            var currentInvariant = fixture.Engine.ValidateInvariant(fixture.Tree, fixture.State, fixture.Settings);
            int freshValidationReads = fixture.Reads.Children;
            Assert.IsTrue(currentInvariant.IsValid, currentInvariant.Description);
            Assert.AreNotEqual(result.Invariant.TreeDescription, currentInvariant.TreeDescription,
                "The callback replaced the root after the immutable engine result was captured.");
            Assert.IsTrue(afterCallbackReads > 0, "Recovery callbacks are not a reusable engine-return boundary.");
            Assert.AreEqual(freshValidationReads, afterCallbackReads,
                "Normalize and Rebalance must validate the current tree after their external diagnostic callback.");
        }

        [TestMethod]
        public void WorkspaceInvariantReusePreservesCommitConstraintRollbackFocusAndRegistration()
        {
            var fixture = new WorkspaceFixture(4);
            fixture.Workspace.SetFocus(fixture.Windows[^1]);
            var before = fixture.Snapshot();
            var nodes = fixture.Windows.Select(fixture.Tree.FindNode).ToArray();
            var originals = fixture.Windows.Select(fixture.Workspace.GetOriginalPosition).ToArray();
            int reads = 0;
            Mock.Get(fixture.Windows[0]).SetupGet(window => window.MinSize)
                .Returns(() => ++reads == 1 ? new Point(0, 0) : new Point(10000, 0));

            var result = fixture.Workspace.SwapMasterSatelliteSide(fixture.Desktop, fixture.State, fixture.Settings);

            Assert.AreEqual(2, reads, "Live commit must read constraints after the successful detached preflight.");
            Assert.IsFalse(result.Succeeded);
            Assert.IsFalse(result.Changed);
            Assert.AreEqual(MasterSatelliteFailureReason.MinSizeConflict, result.FailureReason);
            AssertSnapshot(before, fixture.Snapshot());
            AssertSnapshot(before, result.Before);
            AssertSnapshot(before, result.After);
            for (int i = 0; i < fixture.Windows.Length; i++)
            {
                var window = fixture.Windows[i];
                var node = fixture.Tree.FindNode(window)!;
                Assert.IsTrue(fixture.Workspace.HasWindow(window));
                Assert.AreSame(window, node.WindowReference);
                Assert.AreEqual(nodes[i]!.GenerationID, node.GenerationID);
                Assert.AreEqual(nodes[i]!.ComputedRectangle, node.ComputedRectangle);
                Assert.AreEqual(originals[i], fixture.Workspace.GetOriginalPosition(window));
            }
            Assert.AreSame(fixture.Tree.FindNode(fixture.Windows[^1]), fixture.Workspace.GetFocus(fixture.Desktop));
        }

        [TestMethod]
        public void WorkspaceInvariantReuseRetainsInputValidationBeforeOrdinaryMutation()
        {
            var fixture = new WorkspaceFixture(4);
            ((SplitPanelNode)fixture.Tree.Root!).Orientation = PanelOrientation.Vertical;
            var before = fixture.Snapshot();
            var nodes = fixture.Windows.Select(fixture.Tree.FindNode).ToArray();
            int minSizeReads = 0;
            Mock.Get(fixture.Windows[0]).SetupGet(window => window.MinSize).Returns(() =>
            {
                minSizeReads++;
                return new Point(0, 0);
            });

            var result = fixture.Workspace.SwapMasterSatelliteSide(fixture.Desktop, fixture.State, fixture.Settings);

            Assert.IsFalse(result.Succeeded);
            Assert.IsFalse(result.Changed);
            Assert.AreEqual(MasterSatelliteFailureReason.InvalidCanonicalTree, result.FailureReason);
            Assert.IsFalse(result.Invariant.IsValid);
            AssertSnapshot(before, fixture.Snapshot());
            AssertSnapshot(before, result.Before);
            AssertSnapshot(before, result.After);
            CollectionAssert.AreEqual(nodes, fixture.Windows.Select(fixture.Tree.FindNode).ToArray());
            Assert.AreEqual(0, minSizeReads, "Invalid input is rejected before any engine mutation or native constraint sampling.");
        }

        [TestMethod]
        public void WorkspaceInvariantCounterScenario()
        {
            foreach (int count in new[] { 1, 4, 10 })
            {
                var fixture = new WorkspaceFixture(count);
                var expectedNodes = fixture.Windows.Select(fixture.Tree.FindNode).ToArray();
                var expectedRectangles = expectedNodes.Select(node => node!.ComputedRectangle).ToArray();
                var expectedSatellites = fixture.State.Satellites.ToArray();
                int minSizeReads = 0;
                foreach (var window in fixture.Windows)
                {
                    Mock.Get(window).SetupGet(x => x.MinSize).Returns(() =>
                    {
                        minSizeReads++;
                        return new Point(0, 0);
                    });
                }
                for (int warmup = 0; warmup < 20; warmup++) { ApplyWorkspaceRound(fixture, expectedNodes, expectedRectangles); }
                fixture.Reads.Children = 0;
                minSizeReads = 0;
                long initialRevision = fixture.State.Revision;
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int iteration = 0; iteration < 100; iteration++)
                {
                    ApplyWorkspaceRound(fixture, expectedNodes, expectedRectangles);
                }
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                int childReads = fixture.Reads.Children;
                int nativeReads = minSizeReads;
                Assert.AreEqual(initialRevision + 400, fixture.State.Revision);
                Assert.AreSame(fixture.Windows[0], fixture.State.Master);
                CollectionAssert.AreEqual(expectedSatellites, fixture.State.Satellites.ToArray());
                Assert.IsTrue(fixture.Engine.ValidateInvariant(fixture.Tree, fixture.State, fixture.Settings).IsValid);
                string finalRectangles = string.Join(";", fixture.Windows.Select(window =>
                {
                    var rectangle = fixture.Tree.FindNode(window)!.ComputedRectangle;
                    return $"{rectangle.Left},{rectangle.Top},{rectangle.Right},{rectangle.Bottom}";
                }));
                Console.WriteLine($"PERFCOUNTER workspace-invariant-{count} allocated-bytes {allocated}");
                Console.WriteLine($"PERFCOUNTER workspace-invariant-{count} child-reads {childReads}");
                Console.WriteLine($"PERFCOUNTER workspace-invariant-{count} minsize-reads {nativeReads}");
                Console.WriteLine($"PERFCOUNTER workspace-invariant-{count} rounds 100");
                Console.WriteLine($"PERFCOUNTER workspace-invariant-{count} geometry-digest {Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(finalRectangles)))}");
            }
        }

        private static void ApplyWorkspaceRound(WorkspaceFixture fixture, WindowNode?[] expectedNodes, Rectangle[] expectedRectangles)
        {
            var result = fixture.Workspace.SwapMasterSatelliteSide(fixture.Desktop, fixture.State, fixture.Settings);
            Assert.IsTrue(result.Succeeded && result.Changed, result.Message);
            result = fixture.Workspace.SwapMasterSatelliteSide(fixture.Desktop, fixture.State, fixture.Settings);
            Assert.IsTrue(result.Succeeded && result.Changed, result.Message);
            result = fixture.Workspace.SetMasterSatelliteRatio(fixture.Desktop, fixture.State, fixture.Settings, 0.55);
            Assert.IsTrue(result.Succeeded && result.Changed, result.Message);
            result = fixture.Workspace.SetMasterSatelliteRatio(fixture.Desktop, fixture.State, fixture.Settings, 0.60);
            Assert.IsTrue(result.Succeeded && result.Changed, result.Message);
            Assert.AreEqual(MasterSide.Left, fixture.State.MasterSide);
            Assert.AreEqual(0.60, fixture.State.RequestedMasterRatio);
            for (int i = 0; i < expectedNodes.Length; i++)
            {
                var node = fixture.Tree.FindNode(fixture.Windows[i]);
                Assert.AreSame(expectedNodes[i], node);
                Assert.AreEqual(expectedRectangles[i], node!.ComputedRectangle);
            }
        }

        private static MasterSatelliteOperationResult ApplyDirectCommand(
            WorkspaceFixture fixture, DesktopTree tree, MasterSatelliteRuntimeState state, string command)
        {
            var engine = fixture.Engine;
            var settings = fixture.Settings;
            var windows = fixture.Windows;
            return command switch
            {
                "remove" => engine.RemoveWindow(tree, state, settings, windows[2]),
                "promote" => engine.PromoteToMaster(tree, state, settings, windows[2]),
                "reorder" => engine.ReorderSatellite(tree, state, settings, 0, 2),
                "previous" => engine.MoveSatellitePrevious(tree, state, settings, windows[2]),
                "next" => engine.MoveSatelliteNext(tree, state, settings, windows[1]),
                "set-side" => engine.SetMasterSide(tree, state, settings, MasterSide.Right),
                "swap-side" => engine.SwapMasterSide(tree, state, settings),
                "orientation" => engine.SetSatelliteOrientation(tree, state, settings, SatelliteLayoutOrientation.Horizontal),
                "ratio" => engine.SetRequestedMasterRatio(tree, state, settings, 0.71),
                "reset-ratio" => engine.ResetMasterRatio(tree, state, settings),
                "capture-ratio" => engine.CaptureCurrentMasterRatio(tree, state, settings),
                "relayout" => engine.Relayout(tree, state, settings),
                _ => throw new ArgumentOutOfRangeException(nameof(command)),
            };
        }

        private static MasterSatelliteOperationResult ApplyWorkspaceCommand(WorkspaceFixture fixture, string command)
        {
            var workspace = fixture.Workspace;
            var desktop = fixture.Desktop;
            var state = fixture.State;
            var settings = fixture.Settings;
            var windows = fixture.Windows;
            return command switch
            {
                "remove" => workspace.UnregisterMasterSatelliteWindow(desktop, state, settings, windows[2]),
                "promote" => workspace.PromoteMasterSatelliteWindow(desktop, state, settings, windows[2]),
                "reorder" => workspace.ReorderMasterSatelliteWindow(desktop, state, settings, 0, 2),
                "previous" => workspace.MoveMasterSatelliteWindowPrevious(desktop, state, settings, windows[2]),
                "next" => workspace.MoveMasterSatelliteWindowNext(desktop, state, settings, windows[1]),
                "set-side" => workspace.SetMasterSatelliteSide(desktop, state, settings, MasterSide.Right),
                "swap-side" => workspace.SwapMasterSatelliteSide(desktop, state, settings),
                "orientation" => workspace.SetMasterSatelliteOrientation(desktop, state, settings, SatelliteLayoutOrientation.Horizontal),
                "ratio" => workspace.SetMasterSatelliteRatio(desktop, state, settings, 0.71),
                "reset-ratio" => workspace.ResetMasterSatelliteRatio(desktop, state, settings),
                "capture-ratio" => workspace.CaptureMasterSatelliteRatio(desktop, state, settings),
                "relayout" => workspace.RelayoutMasterSatelliteLayout(desktop, state, settings),
                _ => throw new ArgumentOutOfRangeException(nameof(command)),
            };
        }

        private sealed class WorkspaceFixture
        {
            private readonly Fixture m_fixture;
            public MasterSatelliteLayoutEngine Engine => m_fixture.Engine;
            public MasterSatelliteLayoutSettings Settings => m_fixture.Settings;
            public MasterSatelliteRuntimeState State => m_fixture.State;
            public IWindow[] Windows => m_fixture.Windows;
            public ReadCounts Reads => m_fixture.Reads;
            public TilingWorkspace Workspace { get; }
            public IVirtualDesktop Desktop { get; } = new VirtualDesktopMockFactory().CreateVirtualDesktop();
            public DesktopTree Tree { get; }

            public WorkspaceFixture(int count, Action<string>? diagnostic = null)
            {
                m_fixture = new Fixture(count, diagnostic);
                Workspace = new TilingWorkspace(Engine);
                Workspace.RegisterDesktop(Desktop, m_fixture.Tree.WorkArea, PanelOrientation.Horizontal);
                foreach (var window in Windows) { Workspace.RegisterWindow(window, Desktop); }
                Tree = Workspace.GetTree(Desktop)!;
                var root = m_fixture.Tree.Root;
                m_fixture.Tree.Root = null;
                Tree.Root = root;
            }

            public MasterSatelliteLayoutSnapshot Snapshot() => Engine.CreateSnapshot(Tree, State);
        }
    }
}
