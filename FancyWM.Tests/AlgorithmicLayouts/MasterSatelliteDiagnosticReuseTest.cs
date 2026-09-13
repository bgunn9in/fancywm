#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using FancyWM.AlgorithmicLayouts;
using FancyWM.Layouts.Tiling;
using FancyWM.Models;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    [TestClass]
    public partial class MasterSatelliteDiagnosticReuseTest
    {
        [DataTestMethod]
        [DataRow(1)]
        [DataRow(4)]
        [DataRow(10)]
        public void UnchangedAndRejectedCommandsKeepOneDiagnosticWalkAndExactState(int windowCount)
        {
            var fixture = new Fixture(windowCount);
            var before = fixture.Snapshot();
            var nodes = fixture.Windows.Select(fixture.Tree.FindNode).ToArray();
            fixture.Reads.Children = 0;
            Assert.IsTrue(fixture.Engine.ValidateInvariant(fixture.Tree, fixture.State, fixture.Settings).IsValid);
            int oneValidationReads = fixture.Reads.Children;

            fixture.Reads.Children = 0;
            var unchanged = fixture.Engine.SetMasterSide(fixture.Tree, fixture.State, fixture.Settings, fixture.State.MasterSide);
            int unchangedReads = fixture.Reads.Children;
            Assert.IsTrue(unchanged.Succeeded);
            Assert.IsFalse(unchanged.Changed);
            AssertSnapshot(before, unchanged.Before);
            AssertSnapshot(before, unchanged.After);
            Assert.AreEqual(oneValidationReads, unchangedReads, "Snapshot and validation must share one already captured tree description.");

            fixture.Reads.Children = 0;
            var rejected = fixture.Engine.ReorderSatellite(fixture.Tree, fixture.State, fixture.Settings, -1, 0);
            int rejectedReads = fixture.Reads.Children;
            Assert.IsFalse(rejected.Succeeded);
            Assert.AreEqual(MasterSatelliteFailureReason.InvalidSatelliteIndex, rejected.FailureReason);
            AssertSnapshot(before, rejected.Before);
            AssertSnapshot(before, rejected.After);
            Assert.AreEqual(oneValidationReads, rejectedReads);
            CollectionAssert.AreEqual(nodes, fixture.Windows.Select(fixture.Tree.FindNode).ToArray());
        }

        [TestMethod]
        public void SharedDescriptionDoesNotSkipValidationOfAnInvalidNoOp()
        {
            var fixture = new Fixture(4);
            ((SplitPanelNode)fixture.Tree.Root!).Orientation = PanelOrientation.Vertical;
            var expected = fixture.Engine.ValidateInvariant(fixture.Tree, fixture.State, fixture.Settings);
            var before = fixture.Snapshot();
            Assert.IsFalse(expected.IsValid);

            var result = fixture.Engine.SetMasterSide(fixture.Tree, fixture.State, fixture.Settings, fixture.State.MasterSide);

            Assert.IsFalse(result.Succeeded);
            Assert.IsFalse(result.Changed);
            Assert.AreEqual(MasterSatelliteFailureReason.InvalidCanonicalTree, result.FailureReason);
            CollectionAssert.AreEqual(expected.Violations.ToArray(), result.Invariant.Violations.ToArray());
            AssertSnapshot(before, result.Before);
            AssertSnapshot(before, result.After);
            Assert.AreEqual(before.TreeDescription, result.Invariant.TreeDescription);
        }

        [TestMethod]
        public void CommittedDiagnosticsRetainBeforeStateAndRemainStableAfterLaterMutations()
        {
            var fixture = new Fixture(4);
            var before = fixture.Snapshot();
            var nodes = fixture.Windows.Select(fixture.Tree.FindNode).ToArray();
            var result = fixture.Engine.ReorderSatellite(fixture.Tree, fixture.State, fixture.Settings, 0, 2);
            Assert.IsTrue(result.Succeeded, result.Message);
            Assert.IsTrue(result.Changed);
            Assert.AreEqual(before.Revision + 1, fixture.State.Revision);
            AssertSnapshot(before, result.Before);
            var after = fixture.Snapshot();
            AssertSnapshot(after, result.After);
            Assert.AreSame(result.After.TreeDescription, result.Invariant.TreeDescription,
                "The immutable committed description should be shared with its matching snapshot.");
            CollectionAssert.AreEqual(nodes, fixture.Windows.Select(fixture.Tree.FindNode).ToArray());

            var promote = fixture.Engine.PromoteToMaster(fixture.Tree, fixture.State, fixture.Settings, fixture.Windows[2]);
            Assert.IsTrue(promote.Succeeded, promote.Message);
            var side = fixture.Engine.SwapMasterSide(fixture.Tree, fixture.State, fixture.Settings);
            Assert.IsTrue(side.Succeeded, side.Message);
            AssertSnapshot(before, result.Before);
            AssertSnapshot(after, result.After);
            Assert.AreEqual(after.TreeDescription, result.Invariant.TreeDescription);
            CollectionAssert.AreEqual(nodes, fixture.Windows.Select(fixture.Tree.FindNode).ToArray());
        }

        [TestMethod]
        public void CommitTimeConstraintFailureKeepsRollbackSnapshotAndRegistration()
        {
            var fixture = new Fixture(4);
            var before = fixture.Snapshot();
            var generations = fixture.Windows.Select(window => fixture.Tree.FindNode(window)!.GenerationID).ToArray();
            int reads = 0;
            Mock.Get(fixture.Windows[0]).SetupGet(window => window.MinSize)
                .Returns(() => ++reads == 1 ? new Point(0, 0) : new Point(10000, 0));

            var result = fixture.Engine.SwapMasterSide(fixture.Tree, fixture.State, fixture.Settings);

            Assert.AreEqual(2, reads, "A valid detached preflight must be followed by the actual commit-time constraint read.");
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(MasterSatelliteFailureReason.MinSizeConflict, result.FailureReason);
            Assert.IsTrue(result.Message!.Contains("rolled", StringComparison.OrdinalIgnoreCase)
                || result.Message.Contains("restored", StringComparison.OrdinalIgnoreCase));
            AssertSnapshot(before, fixture.Snapshot());
            AssertSnapshot(before, result.Before);
            AssertSnapshot(before, result.After);
            Assert.AreEqual(before.TreeDescription, result.Invariant.TreeDescription);
            CollectionAssert.AreEqual(generations, fixture.Windows.Select(window => fixture.Tree.FindNode(window)!.GenerationID).ToArray());
            foreach (var window in fixture.Windows) { Assert.AreSame(window, fixture.Tree.FindNode(window)!.WindowReference); }
        }

        [TestMethod]
        public void RollbackSnapshotStillCapturesChangesMadeByDiagnosticCallback()
        {
            Fixture? fixture = null;
            bool callbackRan = false;
            fixture = new Fixture(4, message =>
            {
                if (message.Contains("rolled back", StringComparison.Ordinal))
                {
                    callbackRan = true;
                    ((SplitPanelNode)fixture!.Tree.Root!).Orientation = PanelOrientation.Vertical;
                }
            });
            int reads = 0;
            Mock.Get(fixture.Windows[0]).SetupGet(window => window.MinSize)
                .Returns(() => ++reads == 1 ? new Point(0, 0) : new Point(10000, 0));

            var result = fixture.Engine.SwapMasterSide(fixture.Tree, fixture.State, fixture.Settings);

            Assert.IsTrue(callbackRan);
            Assert.IsFalse(result.Succeeded);
            AssertSnapshot(fixture.Snapshot(), result.After);
            Assert.AreNotEqual(result.Invariant.TreeDescription, result.After.TreeDescription,
                "The rollback snapshot follows the callback and cannot reuse the earlier invariant description.");
            Assert.IsTrue(result.After.TreeDescription.Contains("CountingPanel#", StringComparison.Ordinal));
            Assert.IsTrue(result.After.TreeDescription.Contains("(Vertical)", StringComparison.Ordinal));
        }

        [TestMethod]
        public void DiagnosticReuseCounterScenario()
        {
            foreach (int count in new[] { 1, 4, 10 })
            {
                var fixture = new Fixture(count);
                for (int warmup = 0; warmup < 20; warmup++) { ApplyRound(fixture); }
                fixture.Reads.Children = 0;
                uint geometryChecksum = 0;
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int iteration = 0; iteration < 100; iteration++)
                {
                    geometryChecksum = unchecked(geometryChecksum * 31 + ApplyRound(fixture));
                }
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                int childReads = fixture.Reads.Children;
                Assert.IsTrue(fixture.Engine.ValidateInvariant(fixture.Tree, fixture.State, fixture.Settings).IsValid);
                Assert.AreEqual(MasterSide.Left, fixture.State.MasterSide);
                Assert.AreEqual(0.60, fixture.State.RequestedMasterRatio);
                Assert.AreSame(fixture.Windows[0], fixture.State.Master);
                CollectionAssert.AreEqual(fixture.Windows.Skip(1).ToArray(), fixture.State.Satellites.ToArray());
                Console.WriteLine($"PERFCOUNTER diagnostic-reuse-{count} allocated-bytes {allocated}");
                Console.WriteLine($"PERFCOUNTER diagnostic-reuse-{count} child-reads {childReads}");
                Console.WriteLine($"PERFCOUNTER diagnostic-reuse-{count} rounds 100");
                Console.WriteLine($"PERFCOUNTER diagnostic-reuse-{count} geometry-checksum {geometryChecksum}");
                string finalRectangles = string.Join(";", fixture.Windows.Select(window =>
                {
                    var rectangle = fixture.Tree.FindNode(window)!.ComputedRectangle;
                    return $"{rectangle.Left},{rectangle.Top},{rectangle.Right},{rectangle.Bottom}";
                }));
                Console.WriteLine($"PERFCOUNTER diagnostic-reuse-{count} geometry-digest {Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(finalRectangles)))}");
            }
        }

        private static uint ApplyRound(Fixture fixture)
        {
            var originalNodes = fixture.Windows.Select(fixture.Tree.FindNode).ToArray();
            Assert.IsTrue(fixture.Engine.SwapMasterSide(fixture.Tree, fixture.State, fixture.Settings).Succeeded);
            Assert.IsTrue(fixture.Engine.SwapMasterSide(fixture.Tree, fixture.State, fixture.Settings).Succeeded);
            Assert.IsTrue(fixture.Engine.SetRequestedMasterRatio(fixture.Tree, fixture.State, fixture.Settings, 0.55).Succeeded);
            Assert.IsTrue(fixture.Engine.SetRequestedMasterRatio(fixture.Tree, fixture.State, fixture.Settings, 0.60).Succeeded);
            if (fixture.Windows.Length > 1)
            {
                var master = fixture.State.Master!;
                Assert.IsTrue(fixture.Engine.PromoteToMaster(fixture.Tree, fixture.State, fixture.Settings, fixture.Windows[^1]).Succeeded);
                Assert.IsTrue(fixture.Engine.PromoteToMaster(fixture.Tree, fixture.State, fixture.Settings, master).Succeeded);
                int last = fixture.State.Satellites.Count - 1;
                Assert.IsTrue(fixture.Engine.ReorderSatellite(fixture.Tree, fixture.State, fixture.Settings, 0, last).Succeeded);
                Assert.IsTrue(fixture.Engine.ReorderSatellite(fixture.Tree, fixture.State, fixture.Settings, last, 0).Succeeded);
            }
            var extra = fixture.ExtraWindow;
            bool hasCapacity = fixture.State.Satellites.Count < fixture.Settings.MaxSatellites;
            var placement = fixture.Engine.PlaceWindow(fixture.Tree, fixture.State, fixture.Settings, extra);
            if (hasCapacity)
            {
                Assert.IsTrue(placement.Succeeded, placement.Message);
                Assert.IsTrue(fixture.Engine.RemoveWindow(fixture.Tree, fixture.State, fixture.Settings, extra).Succeeded);
            }
            else
            {
                Assert.IsFalse(placement.Succeeded);
                Assert.AreEqual(MasterSatelliteFailureReason.CapacityReached, placement.FailureReason);
                Assert.IsNull(fixture.Tree.FindNode(extra));
            }
            CollectionAssert.AreEqual(originalNodes, fixture.Windows.Select(fixture.Tree.FindNode).ToArray());
            Assert.AreSame(fixture.Windows[0], fixture.State.Master);
            CollectionAssert.AreEqual(fixture.Windows.Skip(1).ToArray(), fixture.State.Satellites.ToArray());
            Assert.AreEqual(new Rectangle(0, 0, fixture.Windows.Length == 1 ? 1000 : 600, 900), originalNodes[0]!.ComputedRectangle);
            uint geometryChecksum = 0;
            int previousBottom = 0;
            for (int i = 0; i < originalNodes.Length; i++)
            {
                var rectangle = originalNodes[i]!.ComputedRectangle;
                if (i > 0)
                {
                    Assert.AreEqual(600, rectangle.Left);
                    Assert.AreEqual(1000, rectangle.Right);
                    Assert.AreEqual(previousBottom, rectangle.Top);
                    Assert.IsTrue(rectangle.Bottom > rectangle.Top);
                    previousBottom = rectangle.Bottom;
                }
                geometryChecksum = unchecked((geometryChecksum * 31 + (uint)rectangle.Left) * 31 + (uint)rectangle.Top);
                geometryChecksum = unchecked((geometryChecksum * 31 + (uint)rectangle.Right) * 31 + (uint)rectangle.Bottom);
            }
            if (originalNodes.Length > 1) { Assert.AreEqual(900, previousBottom); }
            return geometryChecksum;
        }

        private static void AssertSnapshot(MasterSatelliteLayoutSnapshot expected, MasterSatelliteLayoutSnapshot actual)
        {
            Assert.AreEqual(expected.IsActive, actual.IsActive);
            Assert.AreEqual(expected.MasterSide, actual.MasterSide);
            Assert.AreEqual(expected.RequestedMasterRatio, actual.RequestedMasterRatio);
            Assert.AreEqual(expected.EffectiveMasterRatio, actual.EffectiveMasterRatio);
            Assert.AreEqual(expected.SatelliteOrientation, actual.SatelliteOrientation);
            Assert.AreSame(expected.Master, actual.Master);
            CollectionAssert.AreEqual(expected.Satellites.ToArray(), actual.Satellites.ToArray());
            Assert.AreEqual(expected.Revision, actual.Revision);
            Assert.AreEqual(expected.IsRecovering, actual.IsRecovering);
            Assert.AreEqual(expected.WorkArea, actual.WorkArea);
            Assert.AreEqual(expected.TreeDescription, actual.TreeDescription);
        }

        private sealed class Fixture
        {
            public MasterSatelliteLayoutEngine Engine { get; }
            public MasterSatelliteLayoutSettings Settings { get; }
            public MasterSatelliteRuntimeState State { get; }
            public DesktopTree Tree { get; }
            public IWindow[] Windows { get; }
            public IWindow ExtraWindow { get; }
            public ReadCounts Reads { get; } = new();

            public Fixture(int windowCount, Action<string>? diagnostic = null)
            {
                Engine = new MasterSatelliteLayoutEngine(diagnostic);
                Settings = new MasterSatelliteLayoutSettings
                {
                    Enabled = true,
                    MasterRatio = 0.60,
                    DefaultMasterSide = MasterSide.Left,
                    DefaultSatelliteOrientation = SatelliteLayoutOrientation.Vertical,
                    MaxSatellites = Math.Max(3, windowCount),
                };
                State = Engine.CreateState(Settings, true);
                var factory = new UniqueWindowMockFactory();
                Windows = Enumerable.Range(0, windowCount).Select(index => factory.Create($"window-{index}")).ToArray();
                ExtraWindow = factory.Create("extra");
                var root = new CountingPanel(Reads) { Orientation = PanelOrientation.Horizontal };
                Tree = new DesktopTree { Root = root, WorkArea = Rectangle.OffsetAndSize(0, 0, 1000, 900) };
                State.Master = Windows[0];
                root.Attach(new WindowNode(Windows[0]));
                if (windowCount > 1)
                {
                    var satellites = new CountingPanel(Reads) { Orientation = PanelOrientation.Vertical };
                    root.Attach(satellites);
                    foreach (var window in Windows.Skip(1))
                    {
                        State.MutableSatellites.Add(window);
                        satellites.Attach(new WindowNode(window));
                    }
                }
                var initialized = Engine.Relayout(Tree, State, Settings);
                Assert.IsTrue(initialized.Succeeded, initialized.Message);
                if (windowCount > 1)
                {
                    ((SplitPanelNode)root.Children[1]).DistributeChildrenEvenly();
                    Tree.Arrange();
                }
            }

            public MasterSatelliteLayoutSnapshot Snapshot() => Engine.CreateSnapshot(Tree, State);
        }

        private sealed class ReadCounts { public int Children; }
        private sealed class CountingPanel(ReadCounts counts) : SplitPanelNode
        {
            public override IReadOnlyList<TilingNode> Children
            {
                get { counts.Children++; return base.Children; }
            }
        }
    }
}
