#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;

using FancyWM.Layouts.Tiling;
using FancyWM.Tests.AlgorithmicLayouts;
using FancyWM.Tests.TestUtilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using WinMan;

namespace FancyWM.Tests
{
    [TestClass]
    public class TilingWorkspaceStateTest
    {
        [DataTestMethod]
        [DataRow(1)]
        [DataRow(4)]
        [DataRow(10)]
        [DataRow(50)]
        public void RepeatedTreeLookupDoesNotAllocateOrChangeTheSelectedDesktop(int desktopCount)
        {
            var (owner, trees, states) = CreateStates(desktopCount);
            for (int i = 0; i < 1000; i++) { Assert.AreSame(states[i % desktopCount], owner.GetState(trees[i % desktopCount])); }
            int matched = 0;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                int index = i % desktopCount;
                if (ReferenceEquals(states[index], owner.GetState(trees[index]))) { matched++; }
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(1000, matched);
            Assert.AreEqual(0L, allocated, "Repeated tree-identity lookups must not rebuild a closure and filtered desktop sequence.");
        }

        [TestMethod]
        public void TreeLookupUsesReferenceIdentityEvenWhenTreesOverrideEquality()
        {
            var owner = new TilingWorkspaceState();
            var factory = new VirtualDesktopMockFactory();
            var firstTree = new EqualTree();
            var secondTree = new EqualTree();
            var first = new DesktopState { DesktopTree = firstTree };
            var second = new DesktopState { DesktopTree = secondTree };
            owner.AddState(factory.CreateVirtualDesktop(), first);
            owner.AddState(factory.CreateVirtualDesktop(), second);

            Assert.AreSame(first, owner.GetState(firstTree));
            Assert.AreSame(second, owner.GetState(secondTree));
            Assert.IsNull(owner.GetState(new EqualTree()));
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void DuplicateTreeAliasRemainsAmbiguousUntilOneDesktopIsRemoved(bool sameStateObject)
        {
            var owner = new TilingWorkspaceState();
            var factory = new VirtualDesktopMockFactory();
            var desktops = Enumerable.Range(0, 3).Select(_ => factory.CreateVirtualDesktop()).ToArray();
            var tree = new DesktopTree();
            var first = new DesktopState { DesktopTree = tree };
            var second = sameStateObject ? first : new DesktopState { DesktopTree = tree };
            var third = sameStateObject ? first : new DesktopState { DesktopTree = tree };
            owner.AddState(desktops[0], first);
            Assert.AreSame(first, owner.GetState(tree));
            owner.AddState(desktops[1], second);
            Assert.ThrowsException<InvalidOperationException>(() => owner.GetState(tree));
            owner.AddState(desktops[2], third);
            Assert.ThrowsException<InvalidOperationException>(() => owner.GetState(tree));
            owner.RemoveState(desktops[1]);
            Assert.ThrowsException<InvalidOperationException>(() => owner.GetState(tree));
            owner.RemoveState(desktops[0]);
            Assert.AreSame(third, owner.GetState(tree));
            owner.RemoveState(desktops[2]);
            Assert.IsNull(owner.GetState(tree));
        }

        [TestMethod]
        public void FailedDesktopRegistrationAndRemovalDoNotPublishOrEraseTreeEntries()
        {
            var owner = new TilingWorkspaceState();
            var factory = new VirtualDesktopMockFactory();
            var desktop = factory.CreateVirtualDesktop();
            var original = new DesktopState { DesktopTree = new DesktopTree() };
            var rejected = new DesktopState { DesktopTree = new DesktopTree() };
            owner.AddState(desktop, original);

            Assert.ThrowsException<ArgumentException>(() => owner.AddState(desktop, rejected));
            Assert.ThrowsException<ArgumentException>(() => owner.RemoveState(factory.CreateVirtualDesktop()));
            Assert.AreSame(original, owner.GetState(desktop));
            Assert.AreSame(original, owner.GetState(original.DesktopTree));
            Assert.IsNull(owner.GetState(rejected.DesktopTree));
            Assert.AreEqual(1, owner.States.Count());
        }

        [TestMethod]
        public void TreeLookupRetainsNullQueryAndMalformedRegistrationSemantics()
        {
            var owner = new TilingWorkspaceState();
            var factory = new VirtualDesktopMockFactory();
            var firstDesktop = factory.CreateVirtualDesktop();
            var secondDesktop = factory.CreateVirtualDesktop();
            var nullTreeState = new DesktopState { DesktopTree = null! };
            Assert.IsNull(owner.GetState((DesktopTree)null!));
            Assert.ThrowsException<ArgumentNullException>(() => owner.AddState(null!, nullTreeState));
            Assert.ThrowsException<ArgumentNullException>(() => owner.RemoveState(null!));
            owner.AddState(firstDesktop, nullTreeState);
            Assert.AreSame(nullTreeState, owner.GetState((DesktopTree)null!));
            owner.AddState(secondDesktop, new DesktopState { DesktopTree = null! });
            Assert.ThrowsException<InvalidOperationException>(() => owner.GetState((DesktopTree)null!));
            owner.RemoveState(secondDesktop);
            Assert.AreSame(nullTreeState, owner.GetState((DesktopTree)null!));
            owner.RemoveState(firstDesktop);
            Assert.IsNull(owner.GetState((DesktopTree)null!));

            var ordinary = new DesktopState { DesktopTree = new DesktopTree() };
            owner.AddState(firstDesktop, ordinary);
            owner.AddState(secondDesktop, null!);
            Assert.ThrowsException<NullReferenceException>(() => owner.GetState(ordinary.DesktopTree));
            Assert.ThrowsException<NullReferenceException>(() => owner.GetState(new DesktopTree()));
            owner.RemoveState(secondDesktop);
            Assert.AreSame(ordinary, owner.GetState(ordinary.DesktopTree));
        }

        [TestMethod]
        public void RootReplacementAndDesktopReregistrationKeepFocusBoundToTheCurrentTree()
        {
            var workspace = new TilingWorkspace();
            var desktop = new VirtualDesktopMockFactory().CreateVirtualDesktop();
            var window = new UniqueWindowMockFactory().Create("focused");
            var workArea = Rectangle.OffsetAndSize(0, 0, 1000, 600);
            workspace.RegisterDesktop(desktop, workArea, PanelOrientation.Horizontal);
            var tree = workspace.GetTree(desktop)!;
            var node = new WindowNode(window);
            tree.Root!.Attach(node);
            workspace.SetFocus(node);
            Assert.AreSame(node, workspace.GetFocus(desktop));

            tree.Root = (PanelNode)tree.Root.Clone();
            var replacement = tree.FindNode(window)!;
            Assert.AreNotSame(node, replacement);
            Assert.AreEqual(node.GenerationID, replacement.GenerationID);
            workspace.SetFocus(replacement);
            Assert.AreSame(replacement, workspace.GetFocus(desktop));

            workspace.UnregisterDesktop(desktop);
            Assert.ThrowsException<ArgumentException>(() => workspace.SetFocus(replacement));
            workspace.RegisterDesktop(desktop, workArea, PanelOrientation.Vertical);
            Assert.AreNotSame(tree, workspace.GetTree(desktop));
            Assert.ThrowsException<ArgumentException>(() => workspace.SetFocus(replacement));
            var current = new WindowNode(window);
            workspace.GetTree(desktop)!.Root!.Attach(current);
            workspace.SetFocus(current);
            Assert.AreSame(current, workspace.GetFocus(desktop));
        }

        [TestMethod]
        public void RepeatedRegistrationRemovalReleasesEveryRemovedTreeAndState()
        {
            var owner = new TilingWorkspaceState();
            var references = RegisterAndRemoveCycles(owner);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.AreEqual(0, owner.States.Count());
            Assert.AreEqual(0, owner.Desktops.Count());
            Assert.IsTrue(references.All(reference => !reference.IsAlive),
                "The live workspace owner must not retain tree/state entries after desktop removal.");
            GC.KeepAlive(owner);
        }

        [DataTestMethod]
        [DataRow(-1)]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        public void VdmScanPreservesInsertionOrderFirstMatchAndProbeCount(int firstMatch)
        {
            var owner = new TilingWorkspaceState();
            var window = new LookupWindow(100);
            var probes = new List<int>();
            var states = new DesktopState[3];
            for (int i = 0; i < states.Length; i++)
            {
                states[i] = new DesktopState { DesktopTree = new DesktopTree() };
                owner.AddState(new LookupDesktop(i, firstMatch >= 0 && (i == firstMatch || firstMatch == 0 && i == 2) ? window : null, probes), states[i]);
            }

            var actual = owner.FindByVdm(window);

            if (firstMatch < 0) { Assert.IsNull(actual); }
            else { Assert.AreSame(states[firstMatch], actual); }
            CollectionAssert.AreEqual(
                Enumerable.Range(0, firstMatch < 0 ? states.Length : firstMatch + 1).ToArray(),
                probes.ToArray());
        }

        [DataTestMethod]
        [DataRow(-1)]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        public void TreeScanPreservesInsertionOrderFirstMatchAndProbeCount(int firstMatch)
        {
            var owner = new TilingWorkspaceState();
            var window = new LookupWindow(100);
            var states = new DesktopState[3];
            for (int i = 0; i < states.Length; i++)
            {
                var tree = new DesktopTree { Root = new SplitPanelNode() };
                tree.Root.Attach(new WindowNode(firstMatch >= 0 && (i == firstMatch || firstMatch == 0 && i == 2)
                    ? window
                    : new LookupWindow(i + 1)));
                states[i] = new DesktopState { DesktopTree = tree };
                owner.AddState(new LookupDesktop(i), states[i]);
            }
            window.HashReads = 0;

            var actual = owner.FindByTree(window);

            if (firstMatch < 0) { Assert.IsNull(actual); }
            else { Assert.AreSame(states[firstMatch], actual); }
            Assert.AreEqual(firstMatch < 0 ? states.Length : firstMatch + 1, window.HashReads);
        }

        [TestMethod]
        public void WindowStateScansPropagateTheExactProbeExceptionAndStop()
        {
            var window = new LookupWindow(100);
            var vdmOwner = new TilingWorkspaceState();
            var vdmProbes = new List<int>();
            var expectedVdm = new InvalidOperationException("vdm sentinel");
            var first = new LookupDesktop(0, null, vdmProbes);
            var throwing = new LookupDesktop(1, null, vdmProbes)
            {
                MembershipProbe = _ => throw expectedVdm,
            };
            var untouched = new LookupDesktop(2, window, vdmProbes);
            foreach (var desktop in new[] { first, throwing, untouched })
            {
                vdmOwner.AddState(desktop, new DesktopState { DesktopTree = new DesktopTree() });
            }

            var actualVdm = Assert.ThrowsException<InvalidOperationException>(() => vdmOwner.FindByVdm(window));
            Assert.AreSame(expectedVdm, actualVdm);
            CollectionAssert.AreEqual(new[] { 0, 1 }, vdmProbes.ToArray());

            var treeOwner = CreateWindowLookupStates(3);
            var expectedTree = new InvalidOperationException("tree sentinel");
            treeOwner.Windows[^1].HashReads = 0;
            treeOwner.Windows[^1].HashAction = () => throw expectedTree;
            var actualTree = Assert.ThrowsException<InvalidOperationException>(
                () => treeOwner.Owner.FindByTree(treeOwner.Windows[^1]));
            Assert.AreSame(expectedTree, actualTree);
            Assert.AreEqual(1, treeOwner.Windows[^1].HashReads);
        }

        [TestMethod]
        public void VdmScanRetainsPostProbeLookupWhenTheSelectedDesktopIsRemovedOrReplaced()
        {
            var window = new LookupWindow(100);
            var removedOwner = new TilingWorkspaceState();
            var removedDesktop = new LookupDesktop(0);
            var removedState = new DesktopState { DesktopTree = new DesktopTree() };
            removedOwner.AddState(removedDesktop, removedState);
            removedDesktop.MembershipProbe = _ =>
            {
                removedOwner.RemoveState(removedDesktop);
                return true;
            };

            Assert.ThrowsException<KeyNotFoundException>(() => removedOwner.FindByVdm(window));
            Assert.AreEqual(0, removedOwner.States.Count());

            var replacementOwner = new TilingWorkspaceState();
            var replacementDesktop = new LookupDesktop(0);
            var original = new DesktopState { DesktopTree = new DesktopTree() };
            var replacement = new DesktopState { DesktopTree = new DesktopTree() };
            replacementOwner.AddState(replacementDesktop, original);
            replacementDesktop.MembershipProbe = _ =>
            {
                replacementOwner.RemoveState(replacementDesktop);
                replacementOwner.AddState(replacementDesktop, replacement);
                replacementDesktop.MembershipProbe = candidate => ReferenceEquals(candidate, window);
                return true;
            };

            Assert.AreSame(replacement, replacementOwner.FindByVdm(window));
            Assert.AreSame(replacement, replacementOwner.FindByVdm(window));

            var throwingOwner = new TilingWorkspaceState();
            var throwingDesktop = new LookupDesktop(0, window);
            throwingOwner.AddState(throwingDesktop, original);
            var expected = new InvalidOperationException("final dictionary lookup sentinel");
            throwingDesktop.HashAction = () => throw expected;
            var actual = Assert.ThrowsException<InvalidOperationException>(() => throwingOwner.FindByVdm(window));
            Assert.AreSame(expected, actual);
        }

        [TestMethod]
        public void WindowStateScansPreserveDictionaryMutationSemantics()
        {
            var window = new LookupWindow(100);
            var vdmOwner = new TilingWorkspaceState();
            var firstDesktop = new LookupDesktop(0);
            vdmOwner.AddState(firstDesktop, new DesktopState { DesktopTree = new DesktopTree() });
            vdmOwner.AddState(new LookupDesktop(1), new DesktopState { DesktopTree = new DesktopTree() });
            firstDesktop.MembershipProbe = _ =>
            {
                vdmOwner.AddState(new LookupDesktop(2), new DesktopState { DesktopTree = new DesktopTree() });
                return false;
            };
            Assert.ThrowsException<InvalidOperationException>(() => vdmOwner.FindByVdm(window));

            var treeFixture = CreateWindowLookupStates(2);
            var query = new LookupWindow(100);
            query.HashAction = () =>
            {
                query.HashAction = null;
                treeFixture.Owner.AddState(new LookupDesktop(2), new DesktopState { DesktopTree = new DesktopTree() });
            };
            Assert.ThrowsException<InvalidOperationException>(() => treeFixture.Owner.FindByTree(query));

            var matchingAddFixture = CreateWindowLookupStates(1);
            matchingAddFixture.Windows[0].HashAction = () =>
            {
                matchingAddFixture.Windows[0].HashAction = null;
                matchingAddFixture.Owner.AddState(new LookupDesktop(1), new DesktopState { DesktopTree = new DesktopTree() });
            };
            Assert.AreSame(
                matchingAddFixture.States[0],
                matchingAddFixture.Owner.FindByTree(matchingAddFixture.Windows[0]),
                "Adding after the current tree has matched must not advance the invalidated enumerator.");

            var removalFixture = CreateWindowLookupStates(1);
            var selectedState = removalFixture.States[0];
            removalFixture.Windows[0].HashAction = () =>
            {
                removalFixture.Windows[0].HashAction = null;
                removalFixture.Owner.RemoveState(removalFixture.Desktops[0]);
            };
            Assert.AreSame(selectedState, removalFixture.Owner.FindByTree(removalFixture.Windows[0]));
            Assert.AreEqual(0, removalFixture.Owner.States.Count());

            var removeUnvisitedOwner = new TilingWorkspaceState();
            var removeFirst = new LookupDesktop(0);
            var removeMatch = new LookupDesktop(1, window);
            var removeTail = new LookupDesktop(2);
            var removeMatchState = new DesktopState { DesktopTree = new DesktopTree() };
            removeUnvisitedOwner.AddState(removeFirst, new DesktopState { DesktopTree = new DesktopTree() });
            removeUnvisitedOwner.AddState(removeMatch, removeMatchState);
            removeUnvisitedOwner.AddState(removeTail, new DesktopState { DesktopTree = new DesktopTree() });
            removeFirst.MembershipProbe = _ =>
            {
                removeUnvisitedOwner.RemoveState(removeTail);
                return false;
            };
            Assert.AreSame(removeMatchState, removeUnvisitedOwner.FindByVdm(window));
            Assert.AreEqual(2, removeUnvisitedOwner.States.Count());
        }

        [TestMethod]
        public void TreeScanRetainsMalformedStateAndEarlyMatchBehavior()
        {
            var window = new LookupWindow(100);
            var firstOwner = new TilingWorkspaceState();
            var matchingTree = new DesktopTree { Root = new SplitPanelNode() };
            matchingTree.Root.Attach(new WindowNode(window));
            var matchingState = new DesktopState { DesktopTree = matchingTree };
            firstOwner.AddState(new LookupDesktop(0), matchingState);
            firstOwner.AddState(new LookupDesktop(1), null!);
            Assert.AreSame(matchingState, firstOwner.FindByTree(window));

            var malformedOwner = new TilingWorkspaceState();
            malformedOwner.AddState(new LookupDesktop(0), null!);
            malformedOwner.AddState(new LookupDesktop(1), matchingState);
            Assert.ThrowsException<NullReferenceException>(() => malformedOwner.FindByTree(window));

            var nullTreeOwner = new TilingWorkspaceState();
            nullTreeOwner.AddState(new LookupDesktop(0), new DesktopState { DesktopTree = null! });
            Assert.ThrowsException<NullReferenceException>(() => nullTreeOwner.FindByTree(window));
        }

        [TestMethod]
        public void WindowStateScansReadNullQueriesAndCurrentMembershipOnEveryCall()
        {
            var vdmOwner = new TilingWorkspaceState();
            var vdmState = new DesktopState { DesktopTree = new DesktopTree() };
            bool ownsQuery = true;
            var desktop = new LookupDesktop(0)
            {
                MembershipProbe = candidate => candidate == null && ownsQuery,
            };
            vdmOwner.AddState(desktop, vdmState);
            Assert.AreSame(vdmState, vdmOwner.FindByVdm(null!));
            ownsQuery = false;
            Assert.IsNull(vdmOwner.FindByVdm(null!));
            Assert.AreEqual(2, desktop.MembershipReads);

            var treeOwner = new TilingWorkspaceState();
            var firstDesktop = new LookupDesktop(0);
            var secondDesktop = new LookupDesktop(1);
            var firstTree = new DesktopTree { Root = new SplitPanelNode() };
            var secondTree = new DesktopTree { Root = new SplitPanelNode() };
            var window = new LookupWindow(100);
            firstTree.Root.Attach(new WindowNode(window));
            var firstState = new DesktopState { DesktopTree = firstTree };
            var secondState = new DesktopState { DesktopTree = secondTree };
            treeOwner.AddState(firstDesktop, firstState);
            treeOwner.AddState(secondDesktop, secondState);
            Assert.AreSame(firstState, treeOwner.FindByTree(new LookupWindow(100)),
                "DesktopTree must keep using IWindow equality rather than wrapper identity.");
            Assert.ThrowsException<ArgumentNullException>(() => treeOwner.FindByTree(null!));

            firstTree.Root = new SplitPanelNode();
            Assert.IsNull(treeOwner.FindByTree(window));
            secondTree.Root.Attach(new WindowNode(window));
            Assert.AreSame(secondState, treeOwner.FindByTree(window));
            treeOwner.RemoveState(secondDesktop);
            Assert.IsNull(treeOwner.FindByTree(window));
            Assert.AreSame(firstState, treeOwner.GetState(firstDesktop));
        }

        [DataTestMethod]
        [DataRow(false, 1)]
        [DataRow(false, 10)]
        [DataRow(true, 1)]
        [DataRow(true, 10)]
        public void RepeatedWindowStateScanDoesNotAllocateOrChangeTheSelectedState(bool byTree, int desktopCount)
        {
            var fixture = CreateWindowLookupStates(desktopCount);
            var selected = fixture.States[^1];
            var window = fixture.Windows[^1];
            for (int i = 0; i < 1000; i++)
            {
                Assert.AreSame(selected, byTree ? fixture.Owner.FindByTree(window) : fixture.Owner.FindByVdm(window));
            }
            foreach (var desktop in fixture.Desktops) { desktop.MembershipReads = 0; }
            window.HashReads = 0;

            int matched = 0;
            long before = GC.GetAllocatedBytesForCurrentThread();
            if (byTree)
            {
                for (int i = 0; i < 1000; i++)
                {
                    if (ReferenceEquals(selected, fixture.Owner.FindByTree(window))) { matched++; }
                }
            }
            else
            {
                for (int i = 0; i < 1000; i++)
                {
                    if (ReferenceEquals(selected, fixture.Owner.FindByVdm(window))) { matched++; }
                }
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            long probes = byTree ? window.HashReads : fixture.Desktops.Sum(desktop => desktop.MembershipReads);

            Assert.AreEqual(1000, matched);
            Assert.AreEqual(1000L * desktopCount, probes);
            Assert.AreEqual(0L, allocated, "Repeated window-state scans must not allocate a closure, delegate or boxed dictionary enumerator.");
        }

        [TestMethod]
        public void WindowStateLookupCounterScenario()
        {
            foreach (int count in new[] { 1, 4, 10, 50 })
            {
                var fixture = CreateWindowLookupStates(count);
                var selected = fixture.States[^1];
                var window = fixture.Windows[^1];
                var missing = new LookupWindow(1000 + count);
                for (int i = 0; i < 1000; i++)
                {
                    var query = i % 2 == 0 ? window : missing;
                    var expected = i % 2 == 0 ? selected : null;
                    Assert.AreSame(expected, fixture.Owner.FindByVdm(query));
                    Assert.AreSame(expected, fixture.Owner.FindByTree(query));
                }

                foreach (var desktop in fixture.Desktops)
                {
                    desktop.MembershipReads = 0;
                    desktop.HashReads = 0;
                }
                _ = Stopwatch.GetTimestamp();
                int vdmMatched = 0;
                int vdmResidentHits = 0;
                long vdmBefore = GC.GetAllocatedBytesForCurrentThread();
                long vdmStarted = Stopwatch.GetTimestamp();
                const int lookups = 100000;
                for (int i = 0; i < lookups; i++)
                {
                    bool resident = i % 2 == 0;
                    var actual = fixture.Owner.FindByVdm(resident ? window : missing);
                    if (ReferenceEquals(resident ? selected : null, actual)) { vdmMatched++; }
                    if (resident && ReferenceEquals(selected, actual)) { vdmResidentHits++; }
                }
                long vdmElapsed = Stopwatch.GetTimestamp() - vdmStarted;
                long vdmAllocated = GC.GetAllocatedBytesForCurrentThread() - vdmBefore;
                long vdmProbes = fixture.Desktops.Sum(desktop => desktop.MembershipReads);
                long vdmDesktopHashes = fixture.Desktops.Sum(desktop => desktop.HashReads);
                WriteWindowStateLookupCounters(
                    "vdm", count, vdmAllocated, vdmElapsed, lookups, vdmMatched, vdmResidentHits, vdmProbes, vdmDesktopHashes);

                window.HashReads = 0;
                missing.HashReads = 0;
                foreach (var desktop in fixture.Desktops) { desktop.HashReads = 0; }
                int treeMatched = 0;
                int treeResidentHits = 0;
                long treeBefore = GC.GetAllocatedBytesForCurrentThread();
                long treeStarted = Stopwatch.GetTimestamp();
                for (int i = 0; i < lookups; i++)
                {
                    bool resident = i % 2 == 0;
                    var actual = fixture.Owner.FindByTree(resident ? window : missing);
                    if (ReferenceEquals(resident ? selected : null, actual)) { treeMatched++; }
                    if (resident && ReferenceEquals(selected, actual)) { treeResidentHits++; }
                }
                long treeElapsed = Stopwatch.GetTimestamp() - treeStarted;
                long treeAllocated = GC.GetAllocatedBytesForCurrentThread() - treeBefore;
                long treeProbes = window.HashReads + missing.HashReads;
                long treeDesktopHashes = fixture.Desktops.Sum(desktop => desktop.HashReads);
                WriteWindowStateLookupCounters(
                    "tree", count, treeAllocated, treeElapsed, lookups, treeMatched, treeResidentHits, treeProbes, treeDesktopHashes);

                Assert.AreEqual(lookups, vdmMatched);
                Assert.AreEqual(lookups, treeMatched);
                Assert.AreEqual(lookups / 2, vdmResidentHits);
                Assert.AreEqual(lookups / 2, treeResidentHits);
                Assert.AreEqual((long)lookups * count, vdmProbes);
                Assert.AreEqual((long)lookups * count, treeProbes);
                Assert.AreEqual(lookups / 2, vdmDesktopHashes);
                Assert.AreEqual(0, treeDesktopHashes);
            }
        }

        [TestMethod]
        public void TreeStateLookupCounterScenario()
        {
            foreach (int count in new[] { 1, 4, 10, 50 })
            {
                var (owner, trees, states) = CreateStates(count);
                for (int i = 0; i < 1000; i++) { Assert.AreSame(states[i % count], owner.GetState(trees[i % count])); }
                const int lookups = 100000;
                int matched = 0;
                long before = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                for (int i = 0; i < lookups; i++)
                {
                    int index = i % count;
                    if (ReferenceEquals(states[index], owner.GetState(trees[index]))) { matched++; }
                }
                long elapsed = Stopwatch.GetTimestamp() - started;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                Assert.AreEqual(lookups, matched);
                Assert.AreEqual(count, owner.States.Count());
                Assert.IsNull(owner.GetState(new DesktopTree()));
                Console.WriteLine($"PERFCOUNTER tree-state-index-{count} allocated-bytes {allocated}");
                Console.WriteLine($"PERFCOUNTER tree-state-index-{count} elapsed-ticks {elapsed}");
                Console.WriteLine($"PERFCOUNTER tree-state-index-{count} timestamp-frequency {Stopwatch.Frequency}");
                Console.WriteLine($"PERFCOUNTER tree-state-index-{count} lookups {lookups}");
                Console.WriteLine($"PERFCOUNTER tree-state-index-{count} matched-lookups {matched}");
            }
        }

        private static (TilingWorkspaceState Owner, DesktopTree[] Trees, DesktopState[] States) CreateStates(int count)
        {
            var owner = new TilingWorkspaceState();
            var factory = new VirtualDesktopMockFactory();
            var states = Enumerable.Range(0, count)
                .Select(_ => new DesktopState { DesktopTree = new DesktopTree() }).ToArray();
            foreach (var state in states) { owner.AddState(factory.CreateVirtualDesktop(), state); }
            return (owner, states.Select(state => state.DesktopTree).ToArray(), states);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference[] RegisterAndRemoveCycles(TilingWorkspaceState owner)
        {
            var factory = new VirtualDesktopMockFactory();
            var references = new WeakReference[200];
            for (int i = 0; i < 100; i++)
            {
                var desktop = factory.CreateVirtualDesktop();
                var tree = new DesktopTree();
                var state = new DesktopState { DesktopTree = tree };
                references[2 * i] = new WeakReference(tree);
                references[2 * i + 1] = new WeakReference(state);
                owner.AddState(desktop, state);
                Assert.AreSame(state, owner.GetState(tree));
                owner.RemoveState(desktop);
                Assert.IsNull(owner.GetState(tree));
            }
            return references;
        }

        private static (TilingWorkspaceState Owner, LookupDesktop[] Desktops, DesktopState[] States, LookupWindow[] Windows)
            CreateWindowLookupStates(int count)
        {
            var owner = new TilingWorkspaceState();
            var desktops = new LookupDesktop[count];
            var states = new DesktopState[count];
            var windows = new LookupWindow[count];
            for (int i = 0; i < count; i++)
            {
                var window = windows[i] = new LookupWindow(i + 1);
                var tree = new DesktopTree { Root = new SplitPanelNode() };
                tree.Root.Attach(new WindowNode(window));
                var state = states[i] = new DesktopState { DesktopTree = tree };
                var desktop = desktops[i] = new LookupDesktop(i, window);
                owner.AddState(desktop, state);
            }
            return (owner, desktops, states, windows);
        }

        private static void WriteWindowStateLookupCounters(
            string kind,
            int desktopCount,
            long allocated,
            long elapsed,
            int lookups,
            int matched,
            int residentHits,
            long probes,
            long desktopHashReads)
        {
            string scenario = $"window-state-{kind}-{desktopCount}";
            Console.WriteLine($"PERFCOUNTER {scenario} allocated-bytes {allocated}");
            Console.WriteLine($"PERFCOUNTER {scenario} elapsed-ticks {elapsed}");
            Console.WriteLine($"PERFCOUNTER {scenario} timestamp-frequency {Stopwatch.Frequency}");
            Console.WriteLine($"PERFCOUNTER {scenario} lookups {lookups}");
            Console.WriteLine($"PERFCOUNTER {scenario} matched-lookups {matched}");
            Console.WriteLine($"PERFCOUNTER {scenario} resident-hits {residentHits}");
            Console.WriteLine($"PERFCOUNTER {scenario} probes {probes}");
            Console.WriteLine($"PERFCOUNTER {scenario} desktop-hash-reads {desktopHashReads}");
        }

        private sealed class LookupDesktop(int id, IWindow? ownedWindow = null, List<int>? probeOrder = null) : IVirtualDesktop
        {
            public int MembershipReads;
            public int HashReads;
            public Func<IWindow, bool>? MembershipProbe { get; set; }
            public Action? HashAction { get; set; }
            public IWorkspace Workspace => throw UnsupportedLookupOperation();
            public bool IsAlive => true;
            public bool IsCurrent => false;
            public int Index => id;
            public string Name => $"D{id}";
            public event EventHandler<DesktopChangedEventArgs>? Removed { add { } remove { } }
            public bool HasWindow(IWindow window)
            {
                MembershipReads++;
                probeOrder?.Add(id);
                return MembershipProbe?.Invoke(window) ?? ReferenceEquals(ownedWindow, window);
            }
            public void MoveWindow(IWindow window) => throw UnsupportedLookupOperation();
            public void SwitchTo() => throw UnsupportedLookupOperation();
            public void SetName(string newName) => throw UnsupportedLookupOperation();
            public void Remove() => throw UnsupportedLookupOperation();
            public override bool Equals(object? obj) => ReferenceEquals(this, obj);
            public override int GetHashCode()
            {
                HashReads++;
                HashAction?.Invoke();
                return id;
            }
        }

        private sealed class LookupWindow(int id) : IWindow
        {
            private int Id { get; } = id;
            public int HashReads;
            public Action? HashAction { get; set; }
            public object SyncRoot { get; } = new();
            public IWorkspace Workspace => throw UnsupportedLookupOperation();
            public string Title => $"W{Id}";
            public Rectangle Position => Rectangle.OffsetAndSize(0, 0, 640, 480);
            public WindowState State => WindowState.Restored;
            public Point? MinSize => new Point(0, 0);
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
            public event EventHandler<WindowPositionChangedEventArgs>? PositionChangeStart { add { } remove { } }
            public event EventHandler<WindowPositionChangedEventArgs>? PositionChangeEnd { add { } remove { } }
            public event EventHandler<WindowPositionChangedEventArgs>? PositionChanged { add { } remove { } }
            public event EventHandler<WindowStateChangedEventArgs>? StateChanged { add { } remove { } }
            public event EventHandler<WindowTopmostChangedEventArgs>? TopmostChanged { add { } remove { } }
            public event EventHandler<WindowFocusChangedEventArgs>? GotFocus { add { } remove { } }
            public event EventHandler<WindowFocusChangedEventArgs>? LostFocus { add { } remove { } }
            public event EventHandler<WindowChangedEventArgs>? Added { add { } remove { } }
            public event EventHandler<WindowChangedEventArgs>? Removed { add { } remove { } }
            public event EventHandler<WindowChangedEventArgs>? Destroyed { add { } remove { } }
            public event EventHandler<WindowTitleChangedEventArgs>? TitleChanged { add { } remove { } }
            public bool Equals(IWindow? other) => other is LookupWindow window && Id == window.Id;
            public override bool Equals(object? other) => other is LookupWindow window && Id == window.Id;
            public override int GetHashCode()
            {
                HashReads++;
                HashAction?.Invoke();
                return Id;
            }
            public Process GetProcess() => throw UnsupportedLookupOperation();
            public IWindow? GetPreviousWindow() => throw UnsupportedLookupOperation();
            public IWindow? GetNextWindow() => throw UnsupportedLookupOperation();
            public void Close() => throw UnsupportedLookupOperation();
            public void SetPosition(Rectangle position) => throw UnsupportedLookupOperation();
            public void SetState(WindowState state) => throw UnsupportedLookupOperation();
            public void SetTopmost(bool topmost) => throw UnsupportedLookupOperation();
            public void InsertAfter(IWindow other) => throw UnsupportedLookupOperation();
            public void SendToBack() => throw UnsupportedLookupOperation();
            public void BringToFront() => throw UnsupportedLookupOperation();
            public bool RequestFocus() => throw UnsupportedLookupOperation();
        }

        private static NotSupportedException UnsupportedLookupOperation()
            => new("The managed lookup fixture must not invoke native operations or own external subscriptions.");

        private sealed class EqualTree : DesktopTree
        {
            public override bool Equals(object? obj) => obj is EqualTree;
            public override int GetHashCode() => 0;
        }
    }
}
