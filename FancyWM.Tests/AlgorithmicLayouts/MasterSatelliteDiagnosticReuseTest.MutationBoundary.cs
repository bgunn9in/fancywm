#nullable enable

using System;
using System.Linq;

using FancyWM.Layouts.Tiling;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    public partial class MasterSatelliteDiagnosticReuseTest
    {
        [TestMethod]
        public void WorkspaceInputInvariantIsNotReusedAcrossUnversionedWindowEquality()
        {
            var fixture = new WorkspaceFixture(4);
            var before = fixture.Snapshot();
            var nodes = fixture.Windows.Select(fixture.Tree.FindNode).ToArray();
            int minSizeReads = 0;
            foreach (var window in fixture.Windows)
            {
                Mock.Get(window).SetupGet(value => value.MinSize).Returns(() =>
                {
                    minSizeReads++;
                    return new Point(0, 0);
                });
            }

            fixture.Reads.Children = 0;
            Assert.IsTrue(fixture.Engine.ValidateInvariant(fixture.Tree, fixture.State, fixture.Settings).IsValid);
            int oneValidationReads = fixture.Reads.Children;
            Assert.IsTrue(oneValidationReads > 0);

            var controlled = new InvalidOperationException("controlled unversioned window equality change");
            int throwAfterChildReads = fixture.Reads.Children + oneValidationReads;
            bool armed = true;
            foreach (var window in fixture.Windows)
            {
                Mock.Get(window).Setup(value => value.Equals(It.IsAny<IWindow>()))
                    .Returns((IWindow other) => armed && fixture.Reads.Children > throwAfterChildReads
                        ? throw controlled
                        : ReferenceEquals(window, other));
            }

            var caught = Assert.ThrowsException<InvalidOperationException>(() =>
                fixture.Workspace.SwapMasterSatelliteSide(fixture.Desktop, fixture.State, fixture.Settings));

            armed = false;
            Assert.AreSame(controlled, caught);
            Assert.AreEqual(0, minSizeReads,
                "The second invariant boundary must fail before detached preflight or live MinSize sampling.");
            AssertSnapshot(before, fixture.Snapshot());
            CollectionAssert.AreEqual(nodes, fixture.Windows.Select(fixture.Tree.FindNode).ToArray());
            foreach (var window in fixture.Windows)
            {
                Assert.IsTrue(fixture.Workspace.HasWindow(window));
                Assert.AreSame(window, fixture.Tree.FindNode(window)!.WindowReference);
            }
        }

        [TestMethod]
        public void WorkspaceUnversionedEqualityBoundaryCounterScenario()
        {
            foreach (int count in new[] { 1, 4, 10 })
            {
                var fixture = new WorkspaceFixture(count);
                var before = fixture.Snapshot();
                var nodes = fixture.Windows.Select(fixture.Tree.FindNode).ToArray();
                int minSizeReads = 0;
                int controlledErrors = 0;
                bool armed = false;
                int throwAfterChildReads = int.MaxValue;
                var controlled = new InvalidOperationException("controlled unversioned window equality change");
                foreach (var window in fixture.Windows)
                {
                    Mock.Get(window).SetupGet(value => value.MinSize).Returns(() =>
                    {
                        minSizeReads++;
                        return new Point(0, 0);
                    });
                    Mock.Get(window).Setup(value => value.Equals(It.IsAny<IWindow>()))
                        .Returns((IWindow other) => armed && fixture.Reads.Children > throwAfterChildReads
                            ? throw controlled
                            : ReferenceEquals(window, other));
                }

                fixture.Reads.Children = 0;
                Assert.IsTrue(fixture.Engine.ValidateInvariant(fixture.Tree, fixture.State, fixture.Settings).IsValid);
                int oneValidationReads = fixture.Reads.Children;
                Assert.IsTrue(oneValidationReads > 0);
                int measuredStartReads = fixture.Reads.Children;
                armed = true;
                for (int iteration = 0; iteration < 100; iteration++)
                {
                    throwAfterChildReads = fixture.Reads.Children + oneValidationReads;
                    try
                    {
                        fixture.Workspace.SwapMasterSatelliteSide(fixture.Desktop, fixture.State, fixture.Settings);
                        Assert.Fail("The engine boundary reused a workspace result across unversioned equality.");
                    }
                    catch (InvalidOperationException error) when (ReferenceEquals(error, controlled))
                    {
                        controlledErrors++;
                    }
                }
                armed = false;

                Assert.AreEqual(100, controlledErrors);
                Assert.AreEqual(0, minSizeReads);
                AssertSnapshot(before, fixture.Snapshot());
                CollectionAssert.AreEqual(nodes, fixture.Windows.Select(fixture.Tree.FindNode).ToArray());
                foreach (var window in fixture.Windows)
                {
                    Assert.IsTrue(fixture.Workspace.HasWindow(window));
                    Assert.AreSame(window, fixture.Tree.FindNode(window)!.WindowReference);
                }
                Console.WriteLine($"PERFCOUNTER workspace-equality-boundary-{count} cycles 100");
                Console.WriteLine($"PERFCOUNTER workspace-equality-boundary-{count} controlled-errors {controlledErrors}");
                Console.WriteLine($"PERFCOUNTER workspace-equality-boundary-{count} child-reads {fixture.Reads.Children - measuredStartReads}");
                Console.WriteLine($"PERFCOUNTER workspace-equality-boundary-{count} minsize-reads {minSizeReads}");
                Console.WriteLine($"PERFCOUNTER workspace-equality-boundary-{count} revision-delta {fixture.State.Revision - before.Revision}");
                Console.WriteLine($"PERFCOUNTER workspace-equality-boundary-{count} identity-matches {nodes.Length * 100}");
                Console.WriteLine($"PERFCOUNTER workspace-equality-boundary-{count} registrations {fixture.Windows.Count(fixture.Workspace.HasWindow) * 100}");
            }
        }
    }
}
