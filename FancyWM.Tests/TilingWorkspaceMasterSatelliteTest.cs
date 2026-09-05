using System;
using System.Collections.Generic;
using System.Linq;

using FancyWM.AlgorithmicLayouts;
using FancyWM.Layouts;
using FancyWM.Layouts.Tiling;
using FancyWM.Models;
using FancyWM.Tests.AlgorithmicLayouts;
using FancyWM.Tests.TestUtilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests
{
    [TestClass]
    public class TilingWorkspaceMasterSatelliteTest
    {
        private readonly UniqueWindowMockFactory m_windows = new();
        private readonly VirtualDesktopMockFactory m_desktops = new();
        private readonly Rectangle m_workArea = Rectangle.OffsetAndSize(0, 0, 1000, 600);

        [TestMethod]
        public void TreeSnapshotIsDetachedAndUnknownDesktopReturnsFalse()
        {
            var workspace = new TilingWorkspace();
            var desktop = m_desktops.CreateVirtualDesktop();
            var unknownDesktop = m_desktops.CreateVirtualDesktop();
            workspace.RegisterDesktop(desktop, m_workArea, PanelOrientation.Horizontal);
            var liveTree = workspace.GetTree(desktop)!;

            Assert.IsTrue(workspace.TryGetTreeSnapshot(desktop, out var snapshot));
            Assert.AreNotSame(liveTree, snapshot);
            Assert.AreNotSame(liveTree.Root, snapshot.Root);

            snapshot.WorkArea = Rectangle.OffsetAndSize(50, 60, 300, 200);
            snapshot.Root!.Attach(new WindowNode(m_windows.Create("snapshot-only")));

            Assert.AreEqual(m_workArea, liveTree.WorkArea);
            Assert.AreEqual(0, liveTree.Root!.Windows.Count());
            Assert.IsFalse(workspace.TryGetTreeSnapshot(unknownDesktop, out var missing));
            Assert.IsNull(missing);
        }

        [TestMethod]
        public void ActivationRebuildsManualTreeAndPreservesFocusAndOriginalPositions()
        {
            var (workspace, engine, desktop) = CreateWorkspace();
            var settings = CreateSettings();
            var first = m_windows.Create("first");
            var second = m_windows.Create("second");
            var firstPosition = Rectangle.OffsetAndSize(11, 21, 301, 201);
            var secondPosition = Rectangle.OffsetAndSize(41, 51, 401, 251);
            SetPosition(first, firstPosition);
            SetPosition(second, secondPosition);
            workspace.RegisterWindow(first);
            workspace.RegisterWindow(second);
            workspace.SetFocus(second);
            var oldFocus = workspace.GetFocus(desktop);
            var state = engine.CreateState(settings, true);

            var operation = workspace.ActivateMasterSatelliteLayout(
                desktop,
                state,
                settings,
                new[] { second, first });

            Assert.IsTrue(operation.Succeeded, operation.Message);
            Assert.AreSame(second, state.Master);
            CollectionAssert.AreEqual(new[] { first }, state.Satellites.ToArray());
            AssertFocusedWindow(workspace, desktop, second);
            Assert.AreNotSame(oldFocus, workspace.GetFocus(desktop));
            AssertOriginalPosition(workspace, first, firstPosition);
            AssertOriginalPosition(workspace, second, secondPosition);
            Assert.AreEqual(WorkspaceLayoutKind.Canonical, GetLayoutKind(workspace, desktop, state, settings));
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void ActivationCapacityAndMinSizeFailuresAreAtomic(bool capacityFailure)
        {
            var (workspace, engine, desktop) = CreateWorkspace();
            var settings = CreateSettings(maxSatellites: capacityFailure ? 1 : 3);
            var windows = capacityFailure
                ? CreateWindows(3)
                : new[]
                {
                    m_windows.Create("wide-master", minimumWidth: 700),
                    m_windows.Create("wide-satellite", minimumWidth: 700),
                };
            foreach (var window in windows)
            {
                workspace.RegisterWindow(window);
            }
            workspace.SetFocus(windows[^1]);
            var focusedNode = workspace.GetFocus(desktop);
            var tree = workspace.GetTree(desktop)!;
            var root = tree.Root;
            var state = engine.CreateState(settings, true);
            var revision = state.Revision;
            var before = engine.CreateSnapshot(tree, state);
            var originals = windows.ToDictionary(window => window, window => workspace.GetOriginalPosition(window));

            var operation = workspace.ActivateMasterSatelliteLayout(desktop, state, settings, windows);

            Assert.IsFalse(operation.Succeeded);
            Assert.AreEqual(
                capacityFailure
                    ? MasterSatelliteFailureReason.CapacityReached
                    : MasterSatelliteFailureReason.MinSizeConflict,
                operation.FailureReason);
            Assert.AreSame(root, tree.Root);
            Assert.AreSame(focusedNode, workspace.GetFocus(desktop));
            Assert.AreEqual(revision, state.Revision);
            Assert.IsNull(state.Master);
            Assert.AreEqual(0, state.Satellites.Count);
            Assert.AreEqual(before.TreeDescription, engine.CreateSnapshot(tree, state).TreeDescription);
            CollectionAssert.AreEqual(windows, TreeWindows(tree));
            foreach (var window in windows)
            {
                AssertOriginalPosition(workspace, window, originals[window]);
            }
        }

        [TestMethod]
        public void CapacityClassifiesUntouchedManualAndCorruptedLayouts()
        {
            var settings = CreateSettings();

            var (emptyWorkspace, _, emptyDesktop) = CreateWorkspace();
            var empty = emptyWorkspace.QueryMasterSatelliteCapacity(emptyDesktop, null, settings);
            AssertCapacity(empty, WorkspaceLayoutKind.Empty, true, MasterSatelliteWindowRole.Master, null, 0, 4, 0);

            var (manualWorkspace, _, manualDesktop) = CreateWorkspace();
            manualWorkspace.RegisterWindow(m_windows.Create("manual"));
            var manual = manualWorkspace.QueryMasterSatelliteCapacity(manualDesktop, null, settings);
            AssertCapacity(manual, WorkspaceLayoutKind.Manual, false, null, null, 1, 4, 0);

            var (corruptWorkspace, corruptEngine, corruptDesktop) = CreateWorkspace();
            var corruptState = corruptEngine.CreateState(settings, true);
            Assert.IsTrue(corruptWorkspace.RegisterMaster(
                corruptDesktop,
                corruptState,
                settings,
                m_windows.Create("master")).Succeeded);
            ((SplitPanelNode)corruptWorkspace.GetTree(corruptDesktop)!.Root!).Orientation = PanelOrientation.Vertical;
            var corrupted = corruptWorkspace.QueryMasterSatelliteCapacity(corruptDesktop, corruptState, settings);
            AssertCapacity(corrupted, WorkspaceLayoutKind.CorruptedCanonical, false, null, null, 1, 4, 1);
        }

        [TestMethod]
        public void CapacityTracksCanonicalEmptyPartialAndFullLayouts()
        {
            var (workspace, engine, desktop) = CreateWorkspace();
            var settings = CreateSettings();
            var state = engine.CreateState(settings, true);

            var empty = workspace.QueryMasterSatelliteCapacity(desktop, state, settings);
            AssertCapacity(empty, WorkspaceLayoutKind.Canonical, true, MasterSatelliteWindowRole.Master, null, 0, 4, 0);

            Assert.IsTrue(workspace.RegisterMaster(desktop, state, settings, m_windows.Create("master")).Succeeded);
            var partial = workspace.QueryMasterSatelliteCapacity(desktop, state, settings);
            AssertCapacity(partial, WorkspaceLayoutKind.Canonical, true, MasterSatelliteWindowRole.Satellite, 0, 1, 4, 1);
            Assert.IsTrue(workspace.TryGetMasterSatelliteSnapshot(desktop, state, out var immutableSnapshot));
            Assert.AreEqual(1, immutableSnapshot.Revision);
            Assert.AreEqual(0, immutableSnapshot.Satellites.Count);
            Assert.ThrowsException<NotSupportedException>(() =>
                ((IList<IWindow>)immutableSnapshot.Satellites).Add(m_windows.Create("cannot-add")));

            for (int i = 0; i < settings.MaxSatellites; i++)
            {
                Assert.IsTrue(workspace.RegisterSatellite(
                    desktop,
                    state,
                    settings,
                    m_windows.Create($"satellite-{i}"),
                    i).Succeeded);
            }
            var full = workspace.QueryMasterSatelliteCapacity(desktop, state, settings);
            AssertCapacity(full, WorkspaceLayoutKind.Canonical, false, null, null, 4, 4, 4);
            Assert.AreEqual(1, immutableSnapshot.Revision,
                "A previously returned snapshot must not track live runtime-state mutations.");
            Assert.AreEqual(0, immutableSnapshot.Satellites.Count);
            Assert.IsTrue(workspace.ValidateMasterSatelliteLayout(desktop, state, settings).IsValid);
        }

        [TestMethod]
        public void BatchPreflightSupportsFutureReservedSlotsAndRejectsCumulativeMinSizeAtomically()
        {
            var (workspace, engine, desktop) = CreateWorkspace();
            var settings = CreateSettings(maxSatellites: 2);
            var state = engine.CreateState(settings, true);
            var tree = workspace.GetTree(desktop)!;
            var rootBefore = tree.Root;
            var snapshotBefore = engine.CreateSnapshot(tree, state);

            var master = m_windows.Create("future-master");
            var satellite0 = m_windows.Create("future-satellite-0");
            var satellite1 = m_windows.Create("future-satellite-1");
            var feasible = workspace.PreflightMasterSatellitePlacements(
                desktop,
                state,
                settings,
                [
                    new MasterSatellitePlacementPreflight(
                        master,
                        MasterSatelliteWindowRole.Master,
                        null),
                    new MasterSatellitePlacementPreflight(
                        satellite0,
                        MasterSatelliteWindowRole.Satellite,
                        0),
                    new MasterSatellitePlacementPreflight(
                        satellite1,
                        MasterSatelliteWindowRole.Satellite,
                        1),
                ]);

            Assert.IsTrue(feasible.Succeeded, feasible.Message);
            Assert.AreEqual(MasterSatelliteWindowRole.Satellite, feasible.Role);
            Assert.AreEqual(1, feasible.SatelliteIndex);
            Assert.AreSame(rootBefore, tree.Root);
            Assert.AreEqual(0, state.Revision);
            Assert.IsNull(state.Master);
            Assert.AreEqual(0, state.Satellites.Count);
            Assert.AreEqual(
                snapshotBefore.TreeDescription,
                engine.CreateSnapshot(tree, state).TreeDescription);

            var tallSatellite0 = m_windows.Create(
                "tall-future-satellite-0",
                minimumHeight: 400);
            var tallSatellite1 = m_windows.Create(
                "tall-future-satellite-1",
                minimumHeight: 400);
            var prefix = new[]
            {
                new MasterSatellitePlacementPreflight(
                    master,
                    MasterSatelliteWindowRole.Master,
                    null),
                new MasterSatellitePlacementPreflight(
                    tallSatellite0,
                    MasterSatelliteWindowRole.Satellite,
                    0),
            };
            var feasiblePrefix = workspace.PreflightMasterSatellitePlacements(
                desktop,
                state,
                settings,
                prefix);
            var cumulativeConflict = workspace.PreflightMasterSatellitePlacements(
                desktop,
                state,
                settings,
                [
                    .. prefix,
                    new MasterSatellitePlacementPreflight(
                        tallSatellite1,
                        MasterSatelliteWindowRole.Satellite,
                        1),
                ]);

            Assert.IsTrue(feasiblePrefix.Succeeded, feasiblePrefix.Message);
            Assert.IsFalse(cumulativeConflict.Succeeded);
            Assert.AreEqual(
                MasterSatelliteFailureReason.MinSizeConflict,
                cumulativeConflict.FailureReason);
            Assert.AreSame(rootBefore, tree.Root);
            Assert.AreEqual(0, state.Revision);
            Assert.IsNull(state.Master);
            Assert.AreEqual(0, state.Satellites.Count);
            Assert.AreEqual(
                snapshotBefore.TreeDescription,
                engine.CreateSnapshot(tree, state).TreeDescription);
            Assert.IsFalse(workspace.TryGetOriginalPosition(master, out _));
            Assert.IsFalse(workspace.TryGetOriginalPosition(satellite0, out _));
            Assert.IsFalse(workspace.TryGetOriginalPosition(satellite1, out _));
            Assert.IsFalse(workspace.TryGetOriginalPosition(tallSatellite0, out _));
            Assert.IsFalse(workspace.TryGetOriginalPosition(tallSatellite1, out _));
        }

        [TestMethod]
        public void UnknownDesktopCapacityAndLayoutKindAreDefensive()
        {
            var workspace = new TilingWorkspace();
            var desktop = m_desktops.CreateVirtualDesktop();
            var settings = CreateSettings();
            var state = new MasterSatelliteLayoutEngine().CreateState(settings, true);

            Assert.IsFalse(workspace.TryGetLayoutKind(desktop, state, settings, out var kind));
            Assert.AreEqual(WorkspaceLayoutKind.CorruptedCanonical, kind);
            Assert.IsFalse(workspace.TryGetMasterSatelliteSnapshot(desktop, state, out var snapshot));
            Assert.IsNull(snapshot);
            Assert.IsFalse(workspace.ValidateMasterSatelliteLayout(desktop, state, settings).IsValid);
            var capacity = workspace.QueryMasterSatelliteCapacity(desktop, state, settings);
            AssertCapacity(capacity, WorkspaceLayoutKind.CorruptedCanonical, false, null, null, 0, 4, 0);
            Assert.IsFalse(string.IsNullOrWhiteSpace(capacity.DiagnosticReason));
        }

        [TestMethod]
        public void DisabledSettingsRejectCapacityPlacementAndOperationsWithoutMutation()
        {
            var (workspace, engine, desktop) = CreateWorkspace();
            var enabled = CreateSettings();
            var disabled = new MasterSatelliteLayoutSettings
            {
                Enabled = false,
                MasterRatio = enabled.MasterRatio,
                DefaultMasterSide = enabled.DefaultMasterSide,
                DefaultSatelliteOrientation = enabled.DefaultSatelliteOrientation,
                MaxSatellites = enabled.MaxSatellites,
            };
            var state = engine.CreateState(enabled, true);
            var master = m_windows.Create("master");
            Assert.IsTrue(workspace.RegisterMaster(desktop, state, enabled, master).Succeeded);
            workspace.SetFocus(master);
            var candidate = m_windows.Create("candidate");
            var tree = workspace.GetTree(desktop)!;
            var root = tree.Root;
            var focus = workspace.GetFocus(desktop);
            var revision = state.Revision;
            var before = engine.CreateSnapshot(tree, state);

            var capacity = workspace.QueryMasterSatelliteCapacity(desktop, state, disabled);
            Assert.IsFalse(capacity.CanAcceptWindow);
            Assert.IsFalse(string.IsNullOrWhiteSpace(capacity.DiagnosticReason));
            var placement = workspace.RegisterSatellite(desktop, state, disabled, candidate, 0);
            var operation = workspace.SetMasterSatelliteSide(desktop, state, disabled, MasterSide.Right);

            Assert.IsFalse(placement.Succeeded);
            Assert.AreEqual(MasterSatelliteFailureReason.Inactive, placement.FailureReason);
            Assert.IsFalse(operation.Succeeded);
            Assert.AreEqual(MasterSatelliteFailureReason.Inactive, operation.FailureReason);
            Assert.AreSame(root, tree.Root);
            Assert.AreSame(focus, workspace.GetFocus(desktop));
            Assert.AreEqual(revision, state.Revision);
            Assert.AreEqual(MasterSide.Left, state.MasterSide);
            Assert.AreEqual(before.TreeDescription, engine.CreateSnapshot(tree, state).TreeDescription);
            Assert.IsFalse(workspace.TryGetOriginalPosition(candidate, out _));
        }

        [TestMethod]
        public void PlacementPreflightNeverMutatesLiveTreeStateFocusOrMetadata()
        {
            var (workspace, engine, desktop) = CreateWorkspace();
            var settings = CreateSettings();
            var state = engine.CreateState(settings, true);
            var master = m_windows.Create("master");
            var masterPosition = Rectangle.OffsetAndSize(10, 20, 300, 200);
            Assert.IsTrue(workspace.RegisterMaster(
                desktop,
                state,
                settings,
                master,
                masterPosition).Succeeded);
            workspace.SetFocus(master);
            var focus = workspace.GetFocus(desktop);
            var tree = workspace.GetTree(desktop)!;
            var root = tree.Root;
            var revision = state.Revision;
            var before = engine.CreateSnapshot(tree, state);
            var candidate = m_windows.Create("candidate");
            var impossible = m_windows.Create("impossible", minimumWidth: 1100);

            var success = workspace.PreflightMasterSatellitePlacement(
                desktop,
                state,
                settings,
                candidate,
                MasterSatelliteWindowRole.Satellite,
                0);
            Assert.IsTrue(success.Succeeded, success.Message);
            Assert.AreEqual(MasterSatelliteWindowRole.Satellite, success.Role);
            Assert.AreEqual(0, success.SatelliteIndex);
            AssertPreflightDidNotMutate();

            var failure = workspace.PreflightMasterSatellitePlacement(
                desktop,
                state,
                settings,
                impossible,
                MasterSatelliteWindowRole.Satellite,
                0);
            Assert.IsFalse(failure.Succeeded);
            Assert.AreEqual(MasterSatelliteFailureReason.MinSizeConflict, failure.FailureReason);
            AssertPreflightDidNotMutate();

            void AssertPreflightDidNotMutate()
            {
                Assert.AreSame(root, tree.Root);
                Assert.AreSame(focus, workspace.GetFocus(desktop));
                Assert.AreEqual(revision, state.Revision);
                Assert.AreSame(master, state.Master);
                Assert.AreEqual(0, state.Satellites.Count);
                Assert.AreEqual(before.TreeDescription, engine.CreateSnapshot(tree, state).TreeDescription);
                AssertOriginalPosition(workspace, master, masterPosition);
                Assert.IsFalse(workspace.TryGetOriginalPosition(candidate, out _));
                Assert.IsFalse(workspace.TryGetOriginalPosition(impossible, out _));
            }
        }

        [TestMethod]
        public void RegisterMasterAndIndexedSatellitesCommitRoleOrderAndOriginalMetadata()
        {
            var (workspace, engine, desktop) = CreateWorkspace();
            var settings = CreateSettings();
            var state = engine.CreateState(settings, true);
            var master = m_windows.Create("A");
            var b = m_windows.Create("B");
            var c = m_windows.Create("C");
            var d = m_windows.Create("D");
            var masterPosition = Rectangle.OffsetAndSize(1, 2, 301, 202);
            var cPosition = Rectangle.OffsetAndSize(30, 40, 330, 240);

            var masterPlacement = workspace.RegisterMaster(
                desktop,
                state,
                settings,
                master,
                masterPosition);
            Assert.IsTrue(masterPlacement.Succeeded, masterPlacement.Message);
            Assert.AreEqual(MasterSatelliteWindowRole.Master, masterPlacement.Role);
            Assert.IsNull(masterPlacement.SatelliteIndex);
            Assert.IsTrue(workspace.RegisterSatellite(desktop, state, settings, b, 0).Succeeded);
            Assert.IsTrue(workspace.RegisterSatellite(desktop, state, settings, d, 1).Succeeded);
            var indexed = workspace.RegisterSatellite(desktop, state, settings, c, 1, cPosition);

            Assert.IsTrue(indexed.Succeeded, indexed.Message);
            Assert.AreEqual(MasterSatelliteWindowRole.Satellite, indexed.Role);
            Assert.AreEqual(1, indexed.SatelliteIndex);
            Assert.AreSame(master, state.Master);
            CollectionAssert.AreEqual(new[] { b, c, d }, state.Satellites.ToArray());
            CollectionAssert.AreEqual(new[] { b, c, d }, SatelliteWindows(workspace.GetTree(desktop)!));
            AssertOriginalPosition(workspace, master, masterPosition);
            AssertOriginalPosition(workspace, c, cPosition);
        }

        [TestMethod]
        public void ReservedTokensAreKeyBoundRevisionIndependentAndValidatedAtomically()
        {
            var (workspace, engine, desktop) = CreateWorkspace();
            var display = CreateDisplay();
            var otherDisplay = CreateDisplay();
            var key = new LayoutStateKey(desktop, display);
            var settings = CreateSettings();
            var state = engine.CreateState(settings, true);
            Assert.IsTrue(workspace.RegisterMaster(desktop, state, settings, m_windows.Create("master")).Succeeded);
            var first = m_windows.Create("first");
            var second = m_windows.Create("second");
            var firstToken = new MasterSatelliteReservedSlot(
                Guid.NewGuid(),
                key,
                MasterSatelliteWindowRole.Satellite,
                0);
            var secondToken = new MasterSatelliteReservedSlot(
                Guid.NewGuid(),
                key,
                MasterSatelliteWindowRole.Satellite,
                1);

            var outOfOrder = workspace.RegisterReservedWindow(
                key,
                state,
                settings,
                second,
                secondToken,
                Rectangle.OffsetAndSize(3, 4, 300, 200));
            Assert.IsFalse(outOfOrder.Succeeded);
            Assert.AreEqual(1, state.Revision);
            Assert.IsFalse(workspace.TryGetOriginalPosition(second, out _));

            var firstPlacement = workspace.RegisterReservedWindow(
                key,
                state,
                settings,
                first,
                firstToken,
                Rectangle.OffsetAndSize(1, 2, 300, 200));
            Assert.IsTrue(firstPlacement.Succeeded, firstPlacement.Message);
            var secondPlacement = workspace.RegisterReservedWindow(
                key,
                state,
                settings,
                second,
                secondToken,
                Rectangle.OffsetAndSize(3, 4, 300, 200));
            Assert.IsTrue(secondPlacement.Succeeded, secondPlacement.Message,
                "An intervening commit/revision must not invalidate another reservation token.");
            CollectionAssert.AreEqual(new[] { first, second }, state.Satellites.ToArray());

            var replay = workspace.RegisterReservedWindow(key, state, settings, first, firstToken);
            Assert.IsFalse(replay.Succeeded);
            Assert.AreEqual(MasterSatelliteFailureReason.WindowAlreadyPresent, replay.FailureReason);

            AssertRejectedAtomically(new MasterSatelliteReservedSlot(
                Guid.Empty,
                key,
                MasterSatelliteWindowRole.Satellite,
                2), key);
            AssertRejectedAtomically(new MasterSatelliteReservedSlot(
                Guid.NewGuid(),
                new LayoutStateKey(desktop, otherDisplay),
                MasterSatelliteWindowRole.Satellite,
                2), key);
            AssertRejectedAtomically(new MasterSatelliteReservedSlot(
                Guid.NewGuid(),
                key,
                MasterSatelliteWindowRole.Satellite,
                null), key);
            AssertRejectedAtomically(new MasterSatelliteReservedSlot(
                Guid.NewGuid(),
                key,
                MasterSatelliteWindowRole.Master,
                2), key);
            AssertRejectedAtomically(new MasterSatelliteReservedSlot(
                Guid.NewGuid(),
                key,
                MasterSatelliteWindowRole.Master,
                null), key);

            void AssertRejectedAtomically(MasterSatelliteReservedSlot token, LayoutStateKey suppliedKey)
            {
                var candidate = m_windows.Create($"rejected-{Guid.NewGuid()}");
                var tree = workspace.GetTree(desktop)!;
                var root = tree.Root;
                var revision = state.Revision;
                var before = engine.CreateSnapshot(tree, state);

                var result = workspace.RegisterReservedWindow(
                    suppliedKey,
                    state,
                    settings,
                    candidate,
                    token,
                    Rectangle.OffsetAndSize(10, 20, 300, 200));

                Assert.IsFalse(result.Succeeded);
                Assert.AreSame(root, tree.Root);
                Assert.AreEqual(revision, state.Revision);
                Assert.AreEqual(before.TreeDescription, engine.CreateSnapshot(tree, state).TreeDescription);
                Assert.IsFalse(workspace.TryGetOriginalPosition(candidate, out _));
            }
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void ReservedTransferPreservesOriginalMetadataAcrossEventOrders(bool destinationFirst)
        {
            var engine = new MasterSatelliteLayoutEngine();
            var workspace = new TilingWorkspace(engine);
            var sourceDesktop = m_desktops.CreateVirtualDesktop();
            var destinationDesktop = m_desktops.CreateVirtualDesktop();
            workspace.RegisterDesktop(sourceDesktop, m_workArea, PanelOrientation.Horizontal);
            workspace.RegisterDesktop(destinationDesktop, m_workArea, PanelOrientation.Horizontal);
            var sourceState = engine.CreateState(CreateSettings(), true);
            var destinationState = engine.CreateState(CreateSettings(), true);
            var settings = CreateSettings();
            var window = m_windows.Create("transferred");
            var original = Rectangle.OffsetAndSize(17, 23, 417, 323);
            var key = new LayoutStateKey(destinationDesktop, CreateDisplay());
            var token = new MasterSatelliteReservedSlot(
                Guid.NewGuid(),
                key,
                MasterSatelliteWindowRole.Master,
                null);
            Assert.IsTrue(workspace.RegisterMaster(
                sourceDesktop,
                sourceState,
                settings,
                window,
                original).Succeeded);

            void RegisterDestination()
            {
                var result = workspace.RegisterReservedWindow(
                    key,
                    destinationState,
                    settings,
                    window,
                    token,
                    original);
                Assert.IsTrue(result.Succeeded, result.Message);
            }

            void RemoveSource()
            {
                var result = workspace.UnregisterMasterSatelliteWindow(
                    sourceDesktop,
                    sourceState,
                    settings,
                    window,
                    preserveOriginalPosition: true);
                Assert.IsTrue(result.Succeeded, result.Message);
            }

            if (destinationFirst)
            {
                RegisterDestination();
                RemoveSource();
            }
            else
            {
                RemoveSource();
                RegisterDestination();
            }

            Assert.IsNull(workspace.GetTree(sourceDesktop)!.FindNode(window));
            Assert.IsNotNull(workspace.GetTree(destinationDesktop)!.FindNode(window));
            Assert.AreSame(window, destinationState.Master);
            AssertOriginalPosition(workspace, window, original);
        }

        [TestMethod]
        public void CanonicalUnregisterIgnoresAutoCollapseAndPreservesRemainingFocusAndMetadata()
        {
            var (workspace, engine, desktop) = CreateWorkspace();
            workspace.AutoCollapse = true;
            var settings = CreateSettings();
            var state = engine.CreateState(settings, true);
            var windows = CreateWindows(3);
            Assert.IsTrue(workspace.RegisterMaster(desktop, state, settings, windows[0]).Succeeded);
            Assert.IsTrue(workspace.RegisterSatellite(desktop, state, settings, windows[1], 0).Succeeded);
            Assert.IsTrue(workspace.RegisterSatellite(desktop, state, settings, windows[2], 1).Succeeded);
            workspace.SetFocus(windows[1]);

            var removeSatellite = workspace.UnregisterMasterSatelliteWindow(
                desktop,
                state,
                settings,
                windows[2]);
            Assert.IsTrue(removeSatellite.Succeeded, removeSatellite.Message);
            Assert.AreEqual(1, state.Satellites.Count);
            Assert.AreEqual(1, AssertSatellitePanel(workspace.GetTree(desktop)!).Children.Count);
            AssertFocusedWindow(workspace, desktop, windows[1]);
            Assert.IsFalse(workspace.TryGetOriginalPosition(windows[2], out _));

            var removeMaster = workspace.UnregisterMasterSatelliteWindow(
                desktop,
                state,
                settings,
                windows[0]);
            Assert.IsTrue(removeMaster.Succeeded, removeMaster.Message);
            Assert.AreSame(windows[1], state.Master);
            Assert.AreEqual(0, state.Satellites.Count);
            Assert.AreEqual(1, workspace.GetTree(desktop)!.Root!.Children.Count);
            Assert.IsFalse(workspace.GetTree(desktop)!.Root!.Children.OfType<PanelNode>().Any());
            AssertFocusedWindow(workspace, desktop, windows[1]);
            Assert.IsFalse(workspace.TryGetOriginalPosition(windows[0], out _));
            Assert.IsTrue(workspace.TryGetOriginalPosition(windows[1], out _));

            var removeLast = workspace.UnregisterMasterSatelliteWindow(
                desktop,
                state,
                settings,
                windows[1]);
            Assert.IsTrue(removeLast.Succeeded, removeLast.Message);
            Assert.IsNull(state.Master);
            Assert.AreEqual(0, workspace.GetTree(desktop)!.Root!.Children.Count);
            Assert.IsNull(workspace.GetFocus(desktop));
            Assert.IsFalse(workspace.TryGetOriginalPosition(windows[1], out _));
        }

        [TestMethod]
        public void WorkspaceRoutesPromotionReorderSideOrientationAndRatioOperations()
        {
            var (workspace, engine, desktop) = CreateWorkspace();
            var settings = CreateSettings();
            var state = engine.CreateState(settings, true);
            var windows = CreateWindows(4);
            Assert.IsTrue(workspace.RegisterMaster(desktop, state, settings, windows[0]).Succeeded);
            for (int i = 1; i < windows.Length; i++)
            {
                Assert.IsTrue(workspace.RegisterSatellite(desktop, state, settings, windows[i], i - 1).Succeeded);
            }
            workspace.SetFocus(windows[2]);

            AssertSucceeded(workspace.PromoteMasterSatelliteWindow(desktop, state, settings, windows[3]));
            CollectionAssert.AreEqual(new[] { windows[1], windows[2], windows[0] }, state.Satellites.ToArray());
            AssertSucceeded(workspace.ReorderMasterSatelliteWindow(desktop, state, settings, 0, 2));
            CollectionAssert.AreEqual(new[] { windows[2], windows[0], windows[1] }, state.Satellites.ToArray());
            AssertSucceeded(workspace.MoveMasterSatelliteWindowPrevious(desktop, state, settings, windows[1]));
            AssertSucceeded(workspace.MoveMasterSatelliteWindowNext(desktop, state, settings, windows[1]));
            CollectionAssert.AreEqual(new[] { windows[2], windows[0], windows[1] }, state.Satellites.ToArray());

            AssertSucceeded(workspace.SetMasterSatelliteSide(desktop, state, settings, MasterSide.Right));
            Assert.AreEqual(MasterSide.Right, state.MasterSide);
            AssertSucceeded(workspace.SwapMasterSatelliteSide(desktop, state, settings));
            Assert.AreEqual(MasterSide.Left, state.MasterSide);
            AssertSucceeded(workspace.SetMasterSatelliteOrientation(
                desktop,
                state,
                settings,
                SatelliteLayoutOrientation.Horizontal));
            Assert.AreEqual(SatelliteLayoutOrientation.Horizontal, state.SatelliteOrientation);

            AssertSucceeded(workspace.SetMasterSatelliteRatio(desktop, state, settings, 0.72));
            Assert.AreEqual(0.72, state.RequestedMasterRatio, 0.001);
            AssertSucceeded(workspace.ResetMasterSatelliteRatio(desktop, state, settings));
            Assert.AreEqual(MasterSatelliteLayoutSettings.DefaultMasterRatio, state.RequestedMasterRatio, 0.001);

            var tree = workspace.GetTree(desktop)!;
            var root = (SplitPanelNode)tree.Root!;
            tree.Measure();
            tree.Arrange();
            var masterNode = tree.FindNode(state.Master!)!;
            Assert.IsTrue(root.ResizeTo(masterNode, 680, GrowDirection.Both));
            tree.Arrange();
            var capturedRatio = root.GetChildConstraints(masterNode).Width / root.ContainerLength;
            AssertSucceeded(workspace.CaptureMasterSatelliteRatio(desktop, state, settings));
            Assert.AreEqual(capturedRatio, state.RequestedMasterRatio, 0.001);
            AssertSucceeded(workspace.RelayoutMasterSatelliteLayout(desktop, state, settings));

            AssertFocusedWindow(workspace, desktop, windows[2]);
            Assert.AreEqual(WorkspaceLayoutKind.Canonical, GetLayoutKind(workspace, desktop, state, settings));
        }

        [TestMethod]
        public void NormalizeRecoversCanonicalTreeAndPreservesFocusAndOriginalMetadata()
        {
            var (workspace, engine, desktop) = CreateWorkspace();
            var settings = CreateSettings();
            var state = engine.CreateState(settings, true);
            var windows = CreateWindows(3);
            var originals = new[]
            {
                Rectangle.OffsetAndSize(1, 2, 301, 202),
                Rectangle.OffsetAndSize(3, 4, 303, 204),
                Rectangle.OffsetAndSize(5, 6, 305, 206),
            };
            Assert.IsTrue(workspace.RegisterMaster(desktop, state, settings, windows[0], originals[0]).Succeeded);
            Assert.IsTrue(workspace.RegisterSatellite(desktop, state, settings, windows[1], 0, originals[1]).Succeeded);
            Assert.IsTrue(workspace.RegisterSatellite(desktop, state, settings, windows[2], 1, originals[2]).Succeeded);
            workspace.SetFocus(windows[2]);
            var oldFocus = workspace.GetFocus(desktop);
            var revision = state.Revision;
            ((SplitPanelNode)workspace.GetTree(desktop)!.Root!).Orientation = PanelOrientation.Vertical;

            var operation = workspace.NormalizeMasterSatelliteLayout(desktop, state, settings);

            Assert.IsTrue(operation.Succeeded, operation.Message);
            Assert.IsTrue(operation.Changed);
            Assert.AreEqual(revision + 1, state.Revision);
            Assert.AreNotSame(oldFocus, workspace.GetFocus(desktop));
            AssertFocusedWindow(workspace, desktop, windows[2]);
            CollectionAssert.AreEqual(windows.Skip(1).ToArray(), state.Satellites.ToArray());
            for (int i = 0; i < windows.Length; i++)
            {
                AssertOriginalPosition(workspace, windows[i], originals[i]);
            }
            Assert.AreEqual(WorkspaceLayoutKind.Canonical, GetLayoutKind(workspace, desktop, state, settings));
        }

        [TestMethod]
        public void RevisionDiagnosticsReportOnlyCommittedRevisionChanges()
        {
            var diagnostics = new List<WorkspaceLayoutRevisionDiagnostic>();
            var engine = new MasterSatelliteLayoutEngine();
            var workspace = new TilingWorkspace(engine, diagnostics.Add);
            var desktop = m_desktops.CreateVirtualDesktop();
            workspace.RegisterDesktop(desktop, m_workArea, PanelOrientation.Horizontal);
            var settings = CreateSettings();
            var state = engine.CreateState(settings, true);

            Assert.IsTrue(workspace.RegisterMaster(desktop, state, settings, m_windows.Create("master")).Succeeded);
            Assert.IsFalse(workspace.RegisterMaster(desktop, state, settings, m_windows.Create("wrong-role")).Succeeded);
            var noOp = workspace.SetMasterSatelliteSide(desktop, state, settings, MasterSide.Left);
            Assert.IsTrue(noOp.Succeeded);
            Assert.IsFalse(noOp.Changed);
            Assert.IsTrue(workspace.SetMasterSatelliteSide(desktop, state, settings, MasterSide.Right).Succeeded);

            Assert.AreEqual(2, diagnostics.Count);
            AssertDiagnostic(diagnostics[0], desktop, "RegisterMaster", 0, 1);
            AssertDiagnostic(diagnostics[1], desktop, "SetMasterSide", 1, 2);
        }

        [TestMethod]
        public void AlgorithmicStateAndTreesAreIndependentAcrossDesktops()
        {
            var engine = new MasterSatelliteLayoutEngine();
            var workspace = new TilingWorkspace(engine);
            var firstDesktop = m_desktops.CreateVirtualDesktop();
            var secondDesktop = m_desktops.CreateVirtualDesktop();
            workspace.RegisterDesktop(firstDesktop, m_workArea, PanelOrientation.Horizontal);
            workspace.RegisterDesktop(secondDesktop, Rectangle.OffsetAndSize(0, 0, 1600, 900), PanelOrientation.Horizontal);
            var settings = CreateSettings();
            var firstState = engine.CreateState(settings, true);
            var secondState = engine.CreateState(settings, true);
            var firstMaster = m_windows.Create("first-master");
            var firstSatellite = m_windows.Create("first-satellite");
            var secondMaster = m_windows.Create("second-master");

            Assert.IsTrue(workspace.RegisterMaster(firstDesktop, firstState, settings, firstMaster).Succeeded);
            Assert.IsTrue(workspace.RegisterSatellite(firstDesktop, firstState, settings, firstSatellite, 0).Succeeded);
            Assert.IsTrue(workspace.RegisterMaster(secondDesktop, secondState, settings, secondMaster).Succeeded);
            Assert.IsTrue(workspace.SetMasterSatelliteSide(firstDesktop, firstState, settings, MasterSide.Right).Succeeded);

            Assert.AreEqual(MasterSide.Right, firstState.MasterSide);
            Assert.AreEqual(MasterSide.Left, secondState.MasterSide);
            Assert.AreEqual(2, workspace.QueryMasterSatelliteCapacity(firstDesktop, firstState, settings).OccupiedSlots);
            Assert.AreEqual(1, workspace.QueryMasterSatelliteCapacity(secondDesktop, secondState, settings).OccupiedSlots);
            Assert.IsTrue(workspace.TryGetTreeSnapshot(firstDesktop, out var firstTree));
            Assert.IsTrue(workspace.TryGetTreeSnapshot(secondDesktop, out var secondTree));
            CollectionAssert.AreEquivalent(new[] { firstMaster, firstSatellite }, TreeWindows(firstTree));
            CollectionAssert.AreEqual(new[] { secondMaster }, TreeWindows(secondTree));
        }

        [TestMethod]
        public void GenericRegistrationRemainsManualWhenNoRuntimeStateIsSupplied()
        {
            var (workspace, _, desktop) = CreateWorkspace();
            workspace.AutoCollapse = true;
            var first = m_windows.Create("first");
            var second = m_windows.Create("second");
            var firstPosition = first.Position;

            var firstNode = workspace.RegisterWindow(first);
            var secondNode = workspace.RegisterWindow(second);

            Assert.AreSame(firstNode, workspace.FindWindow(first));
            Assert.AreSame(secondNode, workspace.FindWindow(second));
            AssertOriginalPosition(workspace, first, firstPosition);
            var capacity = workspace.QueryMasterSatelliteCapacity(desktop, null, CreateSettings());
            Assert.AreEqual(WorkspaceLayoutKind.Manual, capacity.LayoutKind);
            Assert.IsFalse(capacity.CanAcceptWindow);

            workspace.UnregisterWindow(first);
            Assert.IsFalse(workspace.HasWindow(first));
            Assert.IsTrue(workspace.HasWindow(second));
            Assert.IsFalse(workspace.TryGetOriginalPosition(first, out _));
        }

        [TestMethod]
        public void TransferSpecificManualDetachPreservesOriginalPosition()
        {
            var (workspace, _, _) = CreateWorkspace();
            var window = m_windows.Create("manual-transfer");
            var original = window.Position;
            workspace.RegisterWindow(window);

            workspace.UnregisterWindowPreservingOriginalPosition(window);

            Assert.IsFalse(workspace.HasWindow(window));
            AssertOriginalPosition(workspace, window, original);
        }

        [TestMethod]
        public void ActivationPrefixPreflightDoesNotMutateOverCapacityManualTree()
        {
            var (workspace, engine, desktop) = CreateWorkspace();
            var settings = CreateSettings(maxSatellites: 2);
            var windows = CreateWindows(5);
            foreach (var window in windows)
            {
                workspace.RegisterWindow(window);
            }
            workspace.SetFocus(windows[1]);
            var rootBefore = workspace.GetTree(desktop)!.Root;
            var orderBefore = TreeWindows(workspace.GetTree(desktop)!);
            var plan = MasterSatelliteCapacityTransitionPlanner.PlanActivation(
                orderBefore,
                windows[1],
                settings.MaxSatellites);
            var candidateState = engine.CreateState(settings, true);

            var result = workspace.PreflightMasterSatelliteActivation(
                desktop,
                candidateState,
                settings,
                plan.AdmittedWindows);

            Assert.IsTrue(result.Succeeded, result.Message);
            Assert.AreSame(rootBefore, workspace.GetTree(desktop)!.Root);
            CollectionAssert.AreEqual(orderBefore, TreeWindows(workspace.GetTree(desktop)!));
            AssertFocusedWindow(workspace, desktop, windows[1]);
            Assert.IsNull(candidateState.Master);
            Assert.AreEqual(0, candidateState.Satellites.Count);
            Assert.AreEqual(0, candidateState.Revision);
        }

        [TestMethod]
        public void RejectedActivationPrefixPreflightLeavesEverySourceWindowUntouched()
        {
            var (workspace, engine, desktop) = CreateWorkspace();
            var settings = CreateSettings(maxSatellites: 2);
            var master = m_windows.Create("wide-master", minimumWidth: 700);
            var satellite = m_windows.Create("wide-satellite", minimumWidth: 700);
            var extra = m_windows.Create("extra");
            var windows = new[] { master, satellite, extra };
            foreach (var window in windows)
            {
                workspace.RegisterWindow(window);
            }
            var rootBefore = workspace.GetTree(desktop)!.Root;
            var state = engine.CreateState(settings, true);

            var result = workspace.PreflightMasterSatelliteActivation(
                desktop,
                state,
                settings,
                new[] { master, satellite });

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(
                MasterSatelliteFailureReason.MinSizeConflict,
                result.FailureReason);
            Assert.AreSame(rootBefore, workspace.GetTree(desktop)!.Root);
            CollectionAssert.AreEqual(windows, TreeWindows(workspace.GetTree(desktop)!));
            Assert.IsNull(state.Master);
            Assert.AreEqual(0, state.Revision);
        }

        [TestMethod]
        public void CrossDesktopRestorePointReversesSourceDetachAndDestinationPlacementExactly()
        {
            var engine = new MasterSatelliteLayoutEngine();
            var workspace = new TilingWorkspace(engine);
            var source = m_desktops.CreateVirtualDesktop();
            var target = m_desktops.CreateVirtualDesktop();
            workspace.RegisterDesktop(source, m_workArea, PanelOrientation.Horizontal);
            workspace.RegisterDesktop(target, m_workArea, PanelOrientation.Horizontal);
            var sourceSettings = CreateSettings(maxSatellites: 4);
            var targetSettings = CreateSettings(maxSatellites: 2);
            var sourceState = engine.CreateState(sourceSettings, true);
            var targetState = engine.CreateState(targetSettings, true);
            var windows = CreateWindows(5);
            foreach (var window in windows)
            {
                workspace.RegisterWindow(window);
            }
            AssertSucceeded(workspace.ActivateMasterSatelliteLayout(
                source,
                sourceState,
                sourceSettings,
                windows));
            AssertSucceeded(workspace.ActivateMasterSatelliteLayout(
                target,
                targetState,
                targetSettings,
                Array.Empty<IWindow>()));
            workspace.SetFocus(windows[1]);
            var sourceTreeBefore = engine.CreateSnapshot(
                workspace.GetTree(source)!,
                sourceState).TreeDescription;
            var targetTreeBefore = engine.CreateSnapshot(
                workspace.GetTree(target)!,
                targetState).TreeDescription;
            var sourceRevision = sourceState.Revision;
            var targetRevision = targetState.Revision;
            Assert.IsTrue(workspace.TryGetOriginalPosition(
                windows[4],
                out var originalPosition));

            var restorePoint = workspace.CaptureMasterSatelliteTransferRestorePoint(
                source,
                sourceState,
                sourceSettings,
                target,
                targetState,
                targetSettings,
                windows[4]);
            AssertSucceeded(workspace.DetachMasterSatelliteTransferSource(restorePoint));
            var display = CreateDisplay();
            var targetKey = new LayoutStateKey(target, display);
            var placement = workspace.RegisterReservedWindow(
                targetKey,
                targetState,
                targetSettings,
                windows[4],
                new MasterSatelliteReservedSlot(
                    Guid.NewGuid(),
                    targetKey,
                    MasterSatelliteWindowRole.Master,
                    null),
                originalPosition);
            Assert.IsTrue(placement.Succeeded, placement.Message);

            workspace.RestoreMasterSatelliteTransfer(restorePoint);

            CollectionAssert.AreEqual(windows, TreeWindows(workspace.GetTree(source)!));
            Assert.AreEqual(0, TreeWindows(workspace.GetTree(target)!).Length);
            Assert.AreEqual(sourceRevision, sourceState.Revision);
            Assert.AreEqual(targetRevision, targetState.Revision);
            Assert.AreEqual(
                sourceTreeBefore,
                engine.CreateSnapshot(workspace.GetTree(source)!, sourceState).TreeDescription);
            Assert.AreEqual(
                targetTreeBefore,
                engine.CreateSnapshot(workspace.GetTree(target)!, targetState).TreeDescription);
            AssertFocusedWindow(workspace, source, windows[1]);
            AssertOriginalPosition(workspace, windows[4], originalPosition);
            Assert.IsTrue(workspace.ValidateMasterSatelliteLayout(
                source,
                sourceState,
                sourceSettings).IsValid);
            Assert.IsTrue(workspace.ValidateMasterSatelliteLayout(
                target,
                targetState,
                targetSettings).IsValid);
        }

        [TestMethod]
        public void RejectedSourceDetachIsAtomicAndDoesNotLoseAnExtraWindow()
        {
            var engine = new MasterSatelliteLayoutEngine();
            var workspace = new TilingWorkspace(engine);
            var source = m_desktops.CreateVirtualDesktop();
            var target = m_desktops.CreateVirtualDesktop();
            workspace.RegisterDesktop(source, m_workArea, PanelOrientation.Horizontal);
            workspace.RegisterDesktop(target, m_workArea, PanelOrientation.Horizontal);
            var oldSettings = CreateSettings(maxSatellites: 3);
            var reducedSettings = CreateSettings(maxSatellites: 1);
            var targetSettings = CreateSettings(maxSatellites: 3);
            var sourceState = engine.CreateState(oldSettings, true);
            var targetState = engine.CreateState(targetSettings, true);
            var windows = CreateWindows(4);
            foreach (var window in windows)
            {
                workspace.RegisterWindow(window);
            }
            AssertSucceeded(workspace.ActivateMasterSatelliteLayout(
                source,
                sourceState,
                oldSettings,
                windows));
            AssertSucceeded(workspace.ActivateMasterSatelliteLayout(
                target,
                targetState,
                targetSettings,
                Array.Empty<IWindow>()));
            var rootBefore = workspace.GetTree(source)!.Root;
            var revisionBefore = sourceState.Revision;
            var restorePoint = workspace.CaptureMasterSatelliteTransferRestorePoint(
                source,
                sourceState,
                reducedSettings,
                target,
                targetState,
                targetSettings,
                windows[^1]);

            var result = workspace.DetachMasterSatelliteTransferSource(restorePoint);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(
                MasterSatelliteFailureReason.InvalidCanonicalTree,
                result.FailureReason);
            Assert.AreSame(rootBefore, workspace.GetTree(source)!.Root);
            CollectionAssert.AreEqual(windows, TreeWindows(workspace.GetTree(source)!));
            Assert.AreEqual(revisionBefore, sourceState.Revision);
        }

        private (TilingWorkspace Workspace, MasterSatelliteLayoutEngine Engine, IVirtualDesktop Desktop)
            CreateWorkspace()
        {
            var engine = new MasterSatelliteLayoutEngine();
            var workspace = new TilingWorkspace(engine);
            var desktop = m_desktops.CreateVirtualDesktop();
            workspace.RegisterDesktop(desktop, m_workArea, PanelOrientation.Horizontal);
            return (workspace, engine, desktop);
        }

        private static MasterSatelliteLayoutSettings CreateSettings(int maxSatellites = 3)
        {
            return new MasterSatelliteLayoutSettings
            {
                Enabled = true,
                MasterRatio = 0.60,
                DefaultMasterSide = MasterSide.Left,
                DefaultSatelliteOrientation = SatelliteLayoutOrientation.Vertical,
                MaxSatellites = maxSatellites,
            };
        }

        private IWindow[] CreateWindows(int count)
        {
            return Enumerable.Range(0, count)
                .Select(index => m_windows.Create(((char)('A' + index)).ToString()))
                .ToArray();
        }

        private static void SetPosition(IWindow window, Rectangle position)
        {
            Mock.Get(window).SetupGet(candidate => candidate.Position).Returns(position);
        }

        private static IDisplay CreateDisplay()
        {
            var mock = new Mock<IDisplay>(MockBehavior.Loose);
            var display = mock.Object;
            mock.Setup(candidate => candidate.Equals(It.IsAny<IDisplay>()))
                .Returns((IDisplay other) => ReferenceEquals(display, other));
            return display;
        }

        private static void AssertCapacity(
            MasterSatelliteCapacitySnapshot capacity,
            WorkspaceLayoutKind kind,
            bool canAccept,
            MasterSatelliteWindowRole? role,
            int? index,
            int occupied,
            int total,
            long revision)
        {
            Assert.AreEqual(kind, capacity.LayoutKind);
            Assert.AreEqual(canAccept, capacity.CanAcceptWindow);
            Assert.AreEqual(role, capacity.NextRole);
            Assert.AreEqual(index, capacity.NextSatelliteIndex);
            Assert.AreEqual(occupied, capacity.OccupiedSlots);
            Assert.AreEqual(total, capacity.TotalCapacity);
            Assert.AreEqual(revision, capacity.Revision);
        }

        private static WorkspaceLayoutKind GetLayoutKind(
            TilingWorkspace workspace,
            IVirtualDesktop desktop,
            MasterSatelliteRuntimeState state,
            MasterSatelliteLayoutSettings settings)
        {
            Assert.IsTrue(workspace.TryGetLayoutKind(desktop, state, settings, out var kind));
            return kind;
        }

        private static void AssertOriginalPosition(
            TilingWorkspace workspace,
            IWindow window,
            Rectangle expected)
        {
            Assert.IsTrue(workspace.TryGetOriginalPosition(window, out var actual));
            Assert.AreEqual(expected, actual);
        }

        private static void AssertFocusedWindow(
            TilingWorkspace workspace,
            IVirtualDesktop desktop,
            IWindow expected)
        {
            Assert.IsInstanceOfType(workspace.GetFocus(desktop), typeof(WindowNode));
            Assert.AreSame(expected, ((WindowNode)workspace.GetFocus(desktop)!).WindowReference);
        }

        private static void AssertSucceeded(MasterSatelliteOperationResult operation)
        {
            Assert.IsTrue(operation.Succeeded, operation.Message);
            Assert.IsTrue(operation.Invariant.IsValid, operation.Invariant.Description);
        }

        private static void AssertDiagnostic(
            WorkspaceLayoutRevisionDiagnostic diagnostic,
            IVirtualDesktop desktop,
            string operation,
            long before,
            long after)
        {
            Assert.AreSame(desktop, diagnostic.Desktop);
            Assert.AreEqual(operation, diagnostic.Operation);
            Assert.AreEqual(before, diagnostic.BeforeRevision);
            Assert.AreEqual(after, diagnostic.AfterRevision);
        }

        private static IWindow[] TreeWindows(DesktopTree tree)
        {
            return tree.Root?.Windows.Select(node => node.WindowReference).ToArray() ?? Array.Empty<IWindow>();
        }

        private static SplitPanelNode AssertSatellitePanel(DesktopTree tree)
        {
            var panel = tree.Root!.Children.OfType<SplitPanelNode>().SingleOrDefault();
            Assert.IsNotNull(panel);
            return panel;
        }

        private static IWindow[] SatelliteWindows(DesktopTree tree)
        {
            return AssertSatellitePanel(tree)
                .Children
                .Cast<WindowNode>()
                .Select(node => node.WindowReference)
                .ToArray();
        }
    }
}
