using System;
using System.Linq;

using FancyWM.AlgorithmicLayouts;
using FancyWM.Layouts.Tiling;
using FancyWM.Models;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    [TestClass]
    public class MasterSatelliteRuntimeLifecycleTest
    {
        private readonly UniqueWindowMockFactory m_windows = new();

        [TestMethod]
        public void RegistryIsolatesEveryDesktopDisplayPairAndMutationsAreIdempotent()
        {
            var registry = new MasterSatelliteRuntimeRegistry();
            var engine = new MasterSatelliteLayoutEngine();
            var settings = CreateSettings();
            var desktop1 = CreateDesktop("D1");
            var desktop2 = CreateDesktop("D2");
            var display1 = CreateDisplay(Rectangle.OffsetAndSize(0, 0, 1920, 1080));
            var display2 = CreateDisplay(Rectangle.OffsetAndSize(1920, 0, 2560, 1440));
            var entries = new[]
            {
                (Key: new LayoutStateKey(desktop1, display1), State: engine.CreateState(settings, true)),
                (Key: new LayoutStateKey(desktop1, display2), State: engine.CreateState(settings, true)),
                (Key: new LayoutStateKey(desktop2, display1), State: engine.CreateState(settings, true)),
                (Key: new LayoutStateKey(desktop2, display2), State: engine.CreateState(settings, true)),
            };

            foreach (var entry in entries)
            {
                Assert.IsTrue(registry.TryAdd(entry.Key, entry.State));
                Assert.IsFalse(registry.TryAdd(entry.Key, engine.CreateState(settings, true)),
                    "A duplicate key must preserve the already registered state.");
            }

            Assert.AreEqual(4, registry.Count);
            foreach (var entry in entries)
            {
                Assert.IsTrue(registry.TryGet(entry.Key, out var actual));
                Assert.AreSame(entry.State, actual);
            }

            var display1Entries = registry.SnapshotForDisplay(display1);
            Assert.AreEqual(2, display1Entries.Count);
            CollectionAssert.AreEquivalent(
                new[] { desktop1, desktop2 },
                display1Entries.Select(entry => entry.Key.VirtualDesktop).ToArray());
            Assert.ThrowsException<NotSupportedException>(() =>
                ((System.Collections.Generic.IList<MasterSatelliteRuntimeEntry>)display1Entries).Clear());

            Assert.IsTrue(registry.Remove(entries[0].Key));
            Assert.IsFalse(registry.Remove(entries[0].Key));
            Assert.IsFalse(registry.TryGet(entries[0].Key, out _));
            Assert.AreEqual(1, registry.RemoveDisplay(display1));
            Assert.AreEqual(0, registry.RemoveDisplay(display1));
            Assert.AreEqual(2, registry.Count);
            Assert.AreEqual(2, registry.SnapshotForDisplay(display2).Count);

            registry.Clear();
            registry.Clear();
            Assert.AreEqual(0, registry.Count);
        }

        [TestMethod]
        public void DesktopLifecycleIsIdempotentAndCurrentDesktopActivationIsLazy()
        {
            var display = CreateDisplay(Rectangle.OffsetAndSize(0, 0, 1000, 600));
            var lifecycle = new MasterSatelliteRuntimeLifecycle(display);
            var workspace = new TilingWorkspace();
            var desktop1 = CreateDesktop("D1");
            var desktop2 = CreateDesktop("D2");
            RegisterDesktop(workspace, desktop1, display.WorkArea, m_windows.Create("D1-master"));
            RegisterDesktop(workspace, desktop2, display.WorkArea);

            var first = lifecycle.ApplySettings(
                workspace,
                CreateSettings(scope: AlgorithmicLayoutDisplayScope.AllDisplays),
                display,
                desktop1);
            Assert.IsTrue(first.StateAdded);
            Assert.IsTrue(lifecycle.TryGetState(desktop1, out var desktop1State));
            Assert.IsFalse(lifecycle.TryGetState(desktop2, out _));

            var added = lifecycle.DesktopAdded(workspace, desktop2, display);
            Assert.IsTrue(added.StateAdded);
            Assert.IsTrue(lifecycle.TryGetState(desktop2, out var desktop2State));
            Assert.AreNotSame(desktop1State, desktop2State);
            Assert.AreEqual(2, lifecycle.StateCount);

            var desktop2Revision = desktop2State.Revision;
            var duplicateAdd = lifecycle.DesktopAdded(workspace, desktop2, display);
            Assert.IsTrue(duplicateAdd.StatePresent);
            Assert.IsFalse(duplicateAdd.StateAdded);
            Assert.AreEqual("AlreadyActive", duplicateAdd.Action);
            Assert.IsNull(duplicateAdd.Operation);
            Assert.AreEqual(desktop2Revision, desktop2State.Revision);
            Assert.AreEqual(2, lifecycle.StateCount);
            Assert.IsTrue(lifecycle.TryGetState(desktop2, out var sameDesktop2State));
            Assert.AreSame(desktop2State, sameDesktop2State);

            Assert.IsTrue(lifecycle.DesktopRemoved(desktop2));
            Assert.IsFalse(lifecycle.TryGetState(desktop2, out _));

            var changed = lifecycle.CurrentDesktopChanged(workspace, desktop2, display);
            Assert.IsTrue(changed.StateAdded);
            Assert.IsTrue(lifecycle.TryGetState(desktop2, out var recreatedDesktop2State));
            Assert.AreNotSame(desktop1State, recreatedDesktop2State);
            Assert.AreNotSame(desktop2State, recreatedDesktop2State);
            Assert.IsTrue(lifecycle.TryGetState(desktop1, out var retainedDesktop1State));
            Assert.AreSame(desktop1State, retainedDesktop1State);
            Assert.AreEqual(2, lifecycle.StateCount);

            Assert.IsTrue(lifecycle.DesktopRemoved(desktop2));
            Assert.IsFalse(lifecycle.DesktopRemoved(desktop2));
            Assert.IsTrue(lifecycle.TryGetState(desktop1, out _));
            Assert.AreEqual(1, lifecycle.StateCount);
        }

        [TestMethod]
        public void ExistingBackgroundDesktopIsInitializedAfterLifecycleBecomesReady()
        {
            var display = CreateDisplay(Rectangle.OffsetAndSize(0, 0, 1000, 600));
            var lifecycle = new MasterSatelliteRuntimeLifecycle(display);
            var workspace = new TilingWorkspace();
            var current = CreateDesktop("Current");
            var background = CreateDesktop("Background");
            RegisterDesktop(workspace, current, display.WorkArea);
            RegisterDesktop(workspace, background, display.WorkArea);
            lifecycle.CacheSettings(
                CreateSettings(scope: AlgorithmicLayoutDisplayScope.AllDisplays),
                display);

            var results = lifecycle.InitializeExistingDesktops(
                workspace,
                new[] { background, current, background },
                current,
                display);

            Assert.AreEqual(2, results.Count);
            Assert.AreSame(current, results[0].Key.VirtualDesktop);
            Assert.AreSame(background, results[1].Key.VirtualDesktop);
            Assert.IsTrue(results.All(result => result.StateAdded));
            Assert.IsTrue(lifecycle.TryGetState(current, out _));
            Assert.IsTrue(lifecycle.TryGetState(background, out var backgroundState));
            var capacity = workspace.QueryMasterSatelliteCapacity(
                background,
                backgroundState,
                lifecycle.SettingsSnapshot);
            Assert.AreEqual(WorkspaceLayoutKind.Canonical, capacity.LayoutKind);
            Assert.IsTrue(capacity.CanAcceptWindow);
            Assert.AreEqual(MasterSatelliteWindowRole.Master, capacity.NextRole);
        }

        [TestMethod]
        public void ExistingManualBackgroundDesktopIsUnavailableWithoutMutation()
        {
            var display = CreateDisplay(Rectangle.OffsetAndSize(0, 0, 1000, 600));
            var lifecycle = new MasterSatelliteRuntimeLifecycle(display);
            var workspace = new TilingWorkspace();
            var current = CreateDesktop("Current");
            var background = CreateDesktop("Manual background");
            RegisterDesktop(workspace, current, display.WorkArea);
            var windows = CreateWindows(2);
            var nodes = RegisterDesktop(workspace, background, display.WorkArea, windows);
            workspace.SetFocus(windows[1]);
            var tree = workspace.GetTree(background)!;
            var root = tree.Root;
            var focus = workspace.GetFocus(background);
            var originals = windows.ToDictionary(window => window, workspace.GetOriginalPosition);
            lifecycle.CacheSettings(
                CreateSettings(scope: AlgorithmicLayoutDisplayScope.AllDisplays),
                display);

            var results = lifecycle.InitializeExistingDesktops(
                workspace,
                new[] { background, current },
                current,
                display);

            Assert.AreEqual(2, results.Count);
            var skipped = results[1];
            Assert.AreEqual("BackgroundManualLayoutSkipped", skipped.Action);
            Assert.IsFalse(skipped.StatePresent);
            Assert.IsFalse(skipped.StateAdded);
            Assert.IsFalse(lifecycle.TryGetState(background, out _));
            Assert.AreSame(root, tree.Root);
            Assert.AreSame(focus, workspace.GetFocus(background));
            CollectionAssert.AreEqual(nodes, windows.Select(tree.FindNode).ToArray());
            CollectionAssert.AreEqual(windows, TreeWindows(tree));
            foreach (var window in windows)
            {
                Assert.IsTrue(workspace.TryGetOriginalPosition(window, out var actual));
                Assert.AreEqual(originals[window], actual);
            }

            var capacity = workspace.QueryMasterSatelliteCapacity(
                background,
                null,
                lifecycle.SettingsSnapshot);
            Assert.AreEqual(WorkspaceLayoutKind.Manual, capacity.LayoutKind);
            Assert.IsFalse(capacity.CanAcceptWindow);
            Assert.IsNull(capacity.NextRole);
        }

        [TestMethod]
        public void DesktopAddedSkipsInvalidActiveBackgroundUntilItBecomesCurrent()
        {
            var display = CreateDisplay(Rectangle.OffsetAndSize(0, 0, 1000, 600));
            var lifecycle = new MasterSatelliteRuntimeLifecycle(display);
            var workspace = new TilingWorkspace();
            var desktop = CreateDesktop("Background");
            var master = m_windows.Create("Master");
            RegisterDesktop(workspace, desktop, display.WorkArea, master);
            lifecycle.CacheSettings(
                CreateSettings(scope: AlgorithmicLayoutDisplayScope.AllDisplays),
                display);

            var activated = lifecycle.CurrentDesktopChanged(workspace, desktop, display);
            Assert.IsTrue(activated.StateAdded);
            Assert.IsTrue(lifecycle.TryGetState(desktop, out var state));

            var intruder = m_windows.Create("Manual intruder");
            var tree = workspace.GetTree(desktop)!;
            var root = tree.Root!;
            var intruderNode = workspace.RegisterWindow(intruder, root);
            workspace.SetFocus(intruder);
            var revision = state.Revision;
            var focus = workspace.GetFocus(desktop);

            var skipped = lifecycle.DesktopAdded(workspace, desktop, display);

            Assert.AreEqual("BackgroundCorruptedLayoutSkipped", skipped.Action);
            Assert.IsTrue(skipped.StatePresent);
            Assert.IsFalse(skipped.StateAdded);
            Assert.IsNotNull(skipped.Invariant);
            Assert.IsFalse(skipped.Invariant!.IsValid);
            Assert.AreEqual(revision, state.Revision);
            Assert.AreSame(root, tree.Root);
            Assert.AreSame(intruderNode, tree.FindNode(intruder));
            Assert.AreSame(focus, workspace.GetFocus(desktop));
            var capacity = workspace.QueryMasterSatelliteCapacity(
                desktop,
                state,
                lifecycle.SettingsSnapshot);
            Assert.AreEqual(WorkspaceLayoutKind.CorruptedCanonical, capacity.LayoutKind);
            Assert.IsFalse(capacity.CanAcceptWindow);

            var current = lifecycle.CurrentDesktopChanged(workspace, desktop, display);

            Assert.AreEqual("Rebuilt", current.Action);
            Assert.IsTrue(current.StatePresent);
            Assert.IsTrue(current.Operation!.Succeeded, current.Operation.Message);
            Assert.IsTrue(current.Invariant!.IsValid, current.Invariant.Description);
            Assert.AreSame(master, state.Master);
            Assert.AreEqual(revision + 1, state.Revision);
        }

        [TestMethod]
        public void AbortedCapacityTransitionRemovesInvalidBackgroundRuntimeState()
        {
            var display = CreateDisplay(Rectangle.OffsetAndSize(0, 0, 1000, 600));
            var lifecycle = new MasterSatelliteRuntimeLifecycle(display);
            var workspace = new TilingWorkspace();
            var desktop = CreateDesktop("Aborted background source");
            var windows = CreateWindows(4);
            RegisterDesktop(workspace, desktop, display.WorkArea, windows);
            var originalSettings = new MasterSatelliteLayoutSettings
            {
                Enabled = true,
                DisplayScope = AlgorithmicLayoutDisplayScope.AllDisplays,
                MaxSatellites = 3,
            };
            Assert.IsTrue(lifecycle.ApplySettings(
                workspace,
                originalSettings,
                display,
                desktop).StateAdded);
            Assert.IsTrue(lifecycle.TryGetState(desktop, out var state));
            var tree = workspace.GetTree(desktop)!;
            var root = tree.Root;
            var revision = state.Revision;
            lifecycle.CacheSettings(originalSettings with { MaxSatellites = 1 }, display);

            var invalidBackground = lifecycle.DesktopAdded(
                workspace,
                desktop,
                display);
            Assert.AreEqual(
                "BackgroundCorruptedLayoutSkipped",
                invalidBackground.Action);
            Assert.IsTrue(invalidBackground.StatePresent);

            var deactivated = lifecycle.DeactivateCapacityTransitionSource(
                desktop,
                display);

            Assert.AreEqual("CapacityTransitionAborted", deactivated.Action);
            Assert.IsTrue(deactivated.StateRemoved);
            Assert.IsFalse(deactivated.StatePresent);
            Assert.IsFalse(lifecycle.TryGetState(desktop, out _));
            Assert.AreSame(root, tree.Root);
            Assert.AreEqual(revision, state.Revision);
            CollectionAssert.AreEqual(windows, TreeWindows(tree));
            var refreshed = lifecycle.DesktopAdded(workspace, desktop, display);
            Assert.IsFalse(refreshed.StatePresent);
            Assert.IsFalse(lifecycle.TryGetState(desktop, out _));
            Assert.AreSame(root, tree.Root);
        }

        [TestMethod]
        public void AbortedActivationSourceIsExcludedFromImmediateReactivation()
        {
            var display = CreateDisplay(Rectangle.OffsetAndSize(0, 0, 1000, 600));
            var lifecycle = new MasterSatelliteRuntimeLifecycle(display);
            var workspace = new TilingWorkspace();
            var source = CreateDesktop("Aborted activation source");
            var target = CreateDesktop("Empty background target");
            var windows = CreateWindows(4);
            RegisterDesktop(workspace, source, display.WorkArea, windows);
            RegisterDesktop(workspace, target, display.WorkArea);
            workspace.SetFocus(windows[2]);
            var tree = workspace.GetTree(source)!;
            var root = tree.Root;
            var focus = workspace.GetFocus(source);
            var originals = windows.ToDictionary(
                window => window,
                workspace.GetOriginalPosition);
            lifecycle.CacheSettings(new MasterSatelliteLayoutSettings
            {
                Enabled = true,
                DisplayScope = AlgorithmicLayoutDisplayScope.AllDisplays,
                MaxSatellites = 1,
            }, display);

            var results = lifecycle.InitializeExistingDesktops(
                workspace,
                new[] { source, target },
                source,
                display,
                new[] { source });

            Assert.AreEqual(1, results.Count);
            Assert.AreSame(target, results[0].Key.VirtualDesktop);
            Assert.IsTrue(results[0].StateAdded);
            Assert.IsFalse(lifecycle.TryGetState(source, out _));
            Assert.IsTrue(lifecycle.TryGetState(target, out _));
            Assert.AreSame(root, tree.Root);
            Assert.AreSame(focus, workspace.GetFocus(source));
            CollectionAssert.AreEqual(windows, TreeWindows(tree));
            foreach (var window in windows)
            {
                Assert.IsTrue(workspace.TryGetOriginalPosition(window, out var actual));
                Assert.AreEqual(originals[window], actual);
            }
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void ActivationUsesFocusedWindowThenPreservesRemainingVisualOrder(bool useFocus)
        {
            var display = CreateDisplay(Rectangle.OffsetAndSize(0, 0, 1000, 600));
            var lifecycle = new MasterSatelliteRuntimeLifecycle(display);
            var workspace = new TilingWorkspace();
            var desktop = CreateDesktop("D1");
            var windows = CreateWindows(3);
            RegisterDesktop(workspace, desktop, display.WorkArea, windows);
            if (useFocus)
            {
                workspace.SetFocus(windows[1]);
            }
            var settings = new MasterSatelliteLayoutSettings
            {
                Enabled = true,
                DisplayScope = AlgorithmicLayoutDisplayScope.AllDisplays,
                MasterRatio = 0.68,
                DefaultMasterSide = MasterSide.Right,
                DefaultSatelliteOrientation = SatelliteLayoutOrientation.Horizontal,
                MaxSatellites = 3,
            };

            var result = lifecycle.ApplySettings(workspace, settings, display, desktop);

            Assert.IsTrue(result.StateAdded);
            Assert.IsTrue(result.Operation!.Succeeded, result.Operation.Message);
            Assert.IsTrue(lifecycle.TryGetState(desktop, out var state));
            Assert.AreSame(useFocus ? windows[1] : windows[0], state.Master);
            CollectionAssert.AreEqual(
                useFocus ? new[] { windows[0], windows[2] } : new[] { windows[1], windows[2] },
                state.Satellites.ToArray());
            Assert.AreEqual(settings.MasterRatio, state.RequestedMasterRatio, 0.001);
            Assert.AreEqual(settings.DefaultMasterSide, state.MasterSide);
            Assert.AreEqual(settings.DefaultSatelliteOrientation, state.SatelliteOrientation);
            Assert.IsTrue(result.Invariant!.IsValid, result.Invariant.Description);
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void RejectedActivationLeavesManualTreeFocusAndMetadataExactAndCommitsNoState(
            bool capacityFailure)
        {
            var display = CreateDisplay(Rectangle.OffsetAndSize(0, 0, 1000, 600));
            var lifecycle = new MasterSatelliteRuntimeLifecycle(display);
            var workspace = new TilingWorkspace();
            var desktop = CreateDesktop("D1");
            var windows = capacityFailure
                ? CreateWindows(3)
                : new[]
                {
                    m_windows.Create("wide-master", minimumWidth: 700),
                    m_windows.Create("wide-satellite", minimumWidth: 700),
                };
            var nodes = RegisterDesktop(workspace, desktop, display.WorkArea, windows);
            workspace.SetFocus(windows[^1]);
            var tree = workspace.GetTree(desktop)!;
            var root = tree.Root;
            var focus = workspace.GetFocus(desktop);
            var originals = windows.ToDictionary(window => window, workspace.GetOriginalPosition);
            var settings = new MasterSatelliteLayoutSettings
            {
                Enabled = true,
                DisplayScope = AlgorithmicLayoutDisplayScope.AllDisplays,
                MaxSatellites = capacityFailure ? 1 : 3,
            };

            var result = lifecycle.ApplySettings(workspace, settings, display, desktop);

            Assert.IsFalse(result.StatePresent);
            Assert.IsFalse(result.StateAdded);
            Assert.AreEqual("ActivationRejected", result.Action);
            Assert.IsNotNull(result.Operation);
            Assert.IsFalse(result.Operation.Succeeded);
            Assert.AreEqual(
                capacityFailure
                    ? MasterSatelliteFailureReason.CapacityReached
                    : MasterSatelliteFailureReason.MinSizeConflict,
                result.Operation.FailureReason);
            Assert.AreEqual(0, lifecycle.StateCount);
            Assert.IsFalse(lifecycle.TryGetState(desktop, out _));
            Assert.AreSame(root, tree.Root);
            Assert.AreSame(focus, workspace.GetFocus(desktop));
            CollectionAssert.AreEqual(nodes, windows.Select(tree.FindNode).ToArray());
            CollectionAssert.AreEqual(windows, TreeWindows(tree));
            foreach (var window in windows)
            {
                Assert.IsTrue(workspace.TryGetOriginalPosition(window, out var actual));
                Assert.AreEqual(originals[window], actual);
            }
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void DisableOrScopeOutRemovesStateWithoutChangingCanonicalTree(bool disable)
        {
            var display = CreateDisplay(Rectangle.OffsetAndSize(0, 0, 1000, 600));
            var otherDisplay = CreateDisplay(Rectangle.OffsetAndSize(1000, 0, 1920, 1080));
            var lifecycle = new MasterSatelliteRuntimeLifecycle(display);
            var workspace = new TilingWorkspace();
            var desktop = CreateDesktop("D1");
            var windows = CreateWindows(3);
            RegisterDesktop(workspace, desktop, display.WorkArea, windows);
            Assert.IsTrue(lifecycle.ApplySettings(
                workspace,
                CreateSettings(scope: AlgorithmicLayoutDisplayScope.AllDisplays),
                display,
                desktop).StateAdded);
            var tree = workspace.GetTree(desktop)!;
            var root = tree.Root;
            var nodes = windows.Select(tree.FindNode).ToArray();
            workspace.SetFocus(windows[1]);
            var focus = workspace.GetFocus(desktop);
            var originals = windows.ToDictionary(window => window, workspace.GetOriginalPosition);
            var newSettings = CreateSettings(
                enabled: !disable,
                scope: disable
                    ? AlgorithmicLayoutDisplayScope.AllDisplays
                    : AlgorithmicLayoutDisplayScope.PrimaryDisplay);

            var result = lifecycle.ApplySettings(
                workspace,
                newSettings,
                disable ? display : otherDisplay,
                desktop);

            Assert.IsFalse(result.Eligible);
            Assert.IsTrue(result.StateRemoved);
            Assert.AreEqual(0, lifecycle.StateCount);
            Assert.IsFalse(lifecycle.TryGetState(desktop, out _));
            Assert.AreSame(root, tree.Root);
            Assert.AreSame(focus, workspace.GetFocus(desktop));
            CollectionAssert.AreEqual(nodes, windows.Select(tree.FindNode).ToArray());
            CollectionAssert.AreEqual(windows, TreeWindows(tree));
            foreach (var window in windows)
            {
                Assert.IsTrue(workspace.TryGetOriginalPosition(window, out var actual));
                Assert.AreEqual(originals[window], actual);
            }
        }

        [TestMethod]
        public void WorkAreaChangeRelayoutsEveryActiveDesktopUsingTheNewArea()
        {
            var oldArea = Rectangle.OffsetAndSize(0, 0, 1000, 600);
            var newArea = Rectangle.OffsetAndSize(100, 50, 1600, 900);
            var display = CreateDisplay(oldArea);
            var lifecycle = new MasterSatelliteRuntimeLifecycle(display);
            var workspace = new TilingWorkspace();
            var desktop1 = CreateDesktop("D1");
            var desktop2 = CreateDesktop("D2");
            RegisterDesktop(workspace, desktop1, oldArea, CreateWindows(2));
            RegisterDesktop(workspace, desktop2, oldArea, CreateWindows(3));
            var settings = new MasterSatelliteLayoutSettings
            {
                Enabled = true,
                DisplayScope = AlgorithmicLayoutDisplayScope.AllDisplays,
                MasterRatio = 0.67,
            };
            Assert.IsTrue(lifecycle.ApplySettings(workspace, settings, display, desktop1).StateAdded);
            Assert.IsTrue(lifecycle.CurrentDesktopChanged(workspace, desktop2, display).StateAdded);
            Assert.IsTrue(lifecycle.TryGetState(desktop1, out var state1));
            Assert.IsTrue(lifecycle.TryGetState(desktop2, out var state2));
            var revision1 = state1.Revision;
            var revision2 = state2.Revision;

            var results = lifecycle.WorkAreaChanged(workspace, newArea);

            Assert.AreEqual(2, results.Count);
            Assert.IsTrue(results.All(result => result.Operation!.Succeeded));
            Assert.IsTrue(results.All(result => result.Invariant!.IsValid));
            Assert.AreEqual(newArea, workspace.GetTree(desktop1)!.WorkArea);
            Assert.AreEqual(newArea, workspace.GetTree(desktop2)!.WorkArea);
            Assert.AreEqual(0.67, state1.RequestedMasterRatio, 0.001);
            Assert.AreEqual(0.67, state2.RequestedMasterRatio, 0.001);
            Assert.AreEqual(revision1 + 1, state1.Revision);
            Assert.AreEqual(revision2 + 1, state2.Revision);
            Assert.IsTrue(workspace.TryGetMasterSatelliteSnapshot(desktop1, state1, out var snapshot1));
            Assert.IsTrue(workspace.TryGetMasterSatelliteSnapshot(desktop2, state2, out var snapshot2));
            Assert.AreEqual(newArea, snapshot1.WorkArea);
            Assert.AreEqual(newArea, snapshot2.WorkArea);
        }

        [TestMethod]
        public void ScalingRelayoutPreservesPropagatedPaddingRatioAndInvariant()
        {
            var workArea = Rectangle.OffsetAndSize(0, 0, 1200, 700);
            var display = CreateDisplay(workArea, scaling: 1.75);
            var lifecycle = new MasterSatelliteRuntimeLifecycle(display);
            var workspace = new TilingWorkspace();
            var desktop = CreateDesktop("D1");
            RegisterDesktop(workspace, desktop, workArea, CreateWindows(3));
            var settings = new MasterSatelliteLayoutSettings
            {
                Enabled = true,
                DisplayScope = AlgorithmicLayoutDisplayScope.AllDisplays,
                MasterRatio = 0.69,
            };
            Assert.IsTrue(lifecycle.ApplySettings(workspace, settings, display, desktop).StateAdded);
            Assert.IsTrue(lifecycle.TryGetState(desktop, out var state));
            var revision = state.Revision;
            var expectedPadding = new Rectangle(0, 38, 0, 0);
            const int expectedSpacing = 12;

            var results = lifecycle.ScalingChanged(workspace, expectedPadding, expectedSpacing);

            Assert.AreEqual(1, results.Count);
            Assert.IsTrue(results[0].Operation!.Succeeded, results[0].Operation.Message);
            Assert.IsTrue(results[0].Invariant!.IsValid, results[0].Invariant.Description);
            Assert.AreEqual(revision + 1, state.Revision);
            Assert.AreEqual(0.69, state.RequestedMasterRatio, 0.001);
            foreach (var panel in workspace.GetTree(desktop)!.Root!.Nodes.OfType<PanelNode>())
            {
                Assert.AreEqual(expectedPadding, panel.Padding);
                Assert.AreEqual(expectedSpacing, panel.Spacing);
            }
        }

        [TestMethod]
        public void DisplayScopeHonorsPrimaryAllAndDisabledModes()
        {
            var primary = CreateDisplay(Rectangle.OffsetAndSize(0, 0, 1920, 1080));
            var secondary = CreateDisplay(Rectangle.OffsetAndSize(1920, 0, 1920, 1080));

            Assert.IsTrue(MasterSatelliteDisplayEligibility.IsEligible(
                CreateSettings(scope: AlgorithmicLayoutDisplayScope.PrimaryDisplay),
                primary,
                primary));
            Assert.IsFalse(MasterSatelliteDisplayEligibility.IsEligible(
                CreateSettings(scope: AlgorithmicLayoutDisplayScope.PrimaryDisplay),
                secondary,
                primary));
            Assert.IsTrue(MasterSatelliteDisplayEligibility.IsEligible(
                CreateSettings(scope: AlgorithmicLayoutDisplayScope.AllDisplays),
                primary,
                primary));
            Assert.IsTrue(MasterSatelliteDisplayEligibility.IsEligible(
                CreateSettings(scope: AlgorithmicLayoutDisplayScope.AllDisplays),
                secondary,
                primary));
            Assert.IsFalse(MasterSatelliteDisplayEligibility.IsEligible(
                CreateSettings(enabled: false, scope: AlgorithmicLayoutDisplayScope.AllDisplays),
                primary,
                primary));
        }

        [DataTestMethod]
        [DataRow(2100, 900, true)]
        [DataRow(700, 300, true)]
        [DataRow(2099, 900, false)]
        [DataRow(900, 2100, false)]
        [DataRow(1920, 1080, false)]
        [DataRow(1000, 1000, false)]
        [DataRow(0, 900, false)]
        [DataRow(2100, 0, false)]
        public void UltrawideScopeUsesOnlyAValidLandscapeAspectRatio(
            int width,
            int height,
            bool expected)
        {
            var workArea = Rectangle.OffsetAndSize(137, 251, width, height);
            var display = CreateDisplay(workArea);
            var primary = CreateDisplay(Rectangle.OffsetAndSize(0, 0, 1920, 1080));
            var settings = CreateSettings(scope: AlgorithmicLayoutDisplayScope.UltrawideDisplays);

            Assert.AreEqual(expected, MasterSatelliteDisplayEligibility.IsUltrawide(workArea));
            Assert.AreEqual(expected, MasterSatelliteDisplayEligibility.IsEligible(settings, display, primary));
        }

        [TestMethod]
        public void UltrawideThresholdIsDocumentedAsTwentyOneByNine()
        {
            Assert.AreEqual(21d / 9d, MasterSatelliteDisplayEligibility.UltrawideMinimumAspectRatio, 0.000001);
        }

        private static MasterSatelliteLayoutSettings CreateSettings(
            bool enabled = true,
            AlgorithmicLayoutDisplayScope scope = AlgorithmicLayoutDisplayScope.PrimaryDisplay)
        {
            return new MasterSatelliteLayoutSettings
            {
                Enabled = enabled,
                DisplayScope = scope,
            };
        }

        private static IVirtualDesktop CreateDesktop(string name)
        {
            var mock = new Mock<IVirtualDesktop>(MockBehavior.Loose);
            mock.SetupGet(desktop => desktop.Name).Returns(name);
            mock.SetupGet(desktop => desktop.IsAlive).Returns(true);
            return mock.Object;
        }

        private IWindow[] CreateWindows(int count)
        {
            return Enumerable.Range(0, count)
                .Select(index => m_windows.Create(((char)('A' + index)).ToString()))
                .ToArray();
        }

        private static WindowNode[] RegisterDesktop(
            TilingWorkspace workspace,
            IVirtualDesktop desktop,
            Rectangle workArea,
            params IWindow[] windows)
        {
            workspace.RegisterDesktop(desktop, workArea, PanelOrientation.Horizontal);
            var root = workspace.GetTree(desktop)!.Root!;
            return windows.Select(window => workspace.RegisterWindow(window, root)).ToArray();
        }

        private static IWindow[] TreeWindows(DesktopTree tree)
        {
            return tree.Root?.Windows.Select(node => node.WindowReference).ToArray()
                ?? Array.Empty<IWindow>();
        }

        private static IDisplay CreateDisplay(Rectangle workArea, double scaling = 1.0)
        {
            var mock = new Mock<IDisplay>(MockBehavior.Loose);
            var display = mock.Object;
            mock.SetupGet(candidate => candidate.WorkArea).Returns(workArea);
            mock.SetupGet(candidate => candidate.Scaling).Returns(scaling);
            mock.Setup(candidate => candidate.Equals(It.IsAny<IDisplay>()))
                .Returns((IDisplay other) => ReferenceEquals(display, other));
            return display;
        }
    }
}
