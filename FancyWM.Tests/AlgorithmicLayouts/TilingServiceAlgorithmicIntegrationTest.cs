#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reactive.Subjects;
using System.Windows;
using System.Windows.Threading;

using FancyWM.AlgorithmicLayouts;
using FancyWM.Layouts.Tiling;
using FancyWM.Models;
using FancyWM.Utilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using Serilog;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    [TestClass]
    public partial class TilingServiceAlgorithmicIntegrationTest
    {
        public TestContext TestContext { get; set; } = null!;

        [TestMethod]
        public void SettingsRuntimeCounterScenario()
        {
            foreach (bool enabled in new[] { false, true })
            {
                var counters = MeasureSettingsRuntimeCounters(enabled);
                string scenario = enabled ? "settings-runtime-enabled" : "settings-runtime-disabled";
                TestContext.WriteLine($"PERFCOUNTER {scenario} view-invalidations {counters.ViewInvalidations}");
                TestContext.WriteLine($"PERFCOUNTER {scenario} scaling-reads {counters.ScalingReads}");
                TestContext.WriteLine($"PERFCOUNTER {scenario} publications {counters.Publications}");
                TestContext.WriteLine($"PERFCOUNTER {scenario} iterations 100");
            }
        }

        [TestMethod]
        public void ArrangeFailureBookkeepingCounterScenario()
        {
            foreach (bool enabled in new[] { false, true })
            {
                foreach (int windowCount in new[] { 1, 4, 10 })
                {
                    foreach (bool pending in new[] { false, true })
                    {
                        MeasureArrangeFailureBookkeepingCounters(enabled, windowCount, pending);
                    }
                }
            }
        }

        private void MeasureArrangeFailureBookkeepingCounters(bool enabled, int windowCount, bool pending)
        {
            const int warmupCount = 256;
            const int iterations = 100;
            using var fixture = new ServiceFixture(EnabledSettings(enabled, maxSatellites: 9));
            var windows = Enumerable.Range(0, windowCount)
                .Select(index => fixture.AddWindow($"Bookkeeping window {index}"))
                .ToArray();
            fixture.DrainDispatcher();
            fixture.HoldLayoutForSettingsObservation();
            var tree = GetBackend(fixture).GetTree(fixture.Desktop)!;
            tree.Measure();
            tree.Arrange();
            var root = tree.Root!;
            var originalNodes = root.Nodes.ToArray();
            var originalParents = originalNodes.Select(node => node.Parent).ToArray();
            var originalRectangles = originalNodes.Select(node => node.ComputedRectangle).ToArray();
            var originalWindowNodes = root.Windows.ToArray();
            var originalWindows = originalWindowNodes.Select(node => node.WindowReference).ToArray();
            Assert.AreEqual(windowCount, originalWindowNodes.Length);
            CollectionAssert.AreEquivalent(windows, originalWindows);

            var key = new LayoutStateKey(fixture.Desktop, fixture.Display);
            bool hasState = fixture.Coordinator.TryGet(key, out var originalState);
            Assert.AreEqual(enabled, hasState);
            long originalRevision = hasState ? originalState.Revision : 0;
            var originalMaster = hasState ? originalState.Master : null;
            var originalSatellites = hasState ? originalState.Satellites.ToArray() : [];
            var newWindows = fixture.GetServiceField<HashSet<IWindow>>("m_newWindowSet");
            using (fixture.GetServiceField<DebugLock>("m_newWindowSetLock").EnterScope())
            {
                newWindows.Clear();
            }
            var notifications = fixture.GetServiceField<ArrangeFailureNotificationTracker>(
                "m_arrangeFailureNotifications");
            Assert.IsFalse(notifications.HasPending);
            var handles = windows.Select(window => window.Handle).ToArray();
            IntPtr pendingHandle = handles[0];
            if (pending) { Assert.IsTrue(notifications.TryMark(pendingHandle)); }

            bool recording = false;
            long handleReads = 0;
            long minSizeReads = 0;
            for (int index = 0; index < windows.Length; index++)
            {
                IntPtr handle = handles[index];
                var minimumSize = windows[index].MinSize;
                var mock = Mock.Get(windows[index]);
                mock.SetupGet(window => window.Handle).Returns(() =>
                {
                    if (recording) { handleReads++; }
                    return handle;
                });
                mock.SetupGet(window => window.MinSize).Returns(() =>
                {
                    if (recording) { minSizeReads++; }
                    return minimumSize;
                });
            }
            int placementNotifications = 0;
            int fallbackNotifications = 0;
            int algorithmicNotifications = 0;
            fixture.Service.PlacementFailed += (_, args) =>
            {
                placementNotifications++;
                if (args.FailReason == TilingError.NoValidPlacementExists) { fallbackNotifications++; }
            };
            fixture.Service.AlgorithmicLayoutChanged += (_, _) => algorithmicNotifications++;
            var updateTree = (Func<DesktopTree, bool>)typeof(TilingService)
                .GetMethod("UpdateTree", BindingFlags.NonPublic | BindingFlags.Instance)!
                .CreateDelegate(typeof(Func<DesktopTree, bool>), fixture.Service);
            for (int index = 0; index < warmupCount; index++)
            {
                Assert.IsTrue(updateTree(tree));
            }

            long allocatedBytes = 0;
            int arrangedPasses = 0;
            uint geometryChecksum = 2166136261;
            long revisionDelta = 0;
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                bool arranged;
                long previousHandleReads = handleReads;
                long before = GC.GetAllocatedBytesForCurrentThread();
                recording = true;
                try
                {
                    arranged = updateTree(tree);
                }
                finally
                {
                    allocatedBytes += GC.GetAllocatedBytesForCurrentThread() - before;
                    recording = false;
                }

                // Only UpdateTree and its fake adapters are inside the allocation
                // interval. These complete result checks cannot inflate that count.
                Assert.IsTrue(arranged);
                arrangedPasses++;
                if (pending)
                {
                    Assert.AreEqual((long)windowCount, handleReads - previousHandleReads);
                    Assert.IsFalse(notifications.TryMark(pendingHandle),
                        "A pending handle still present in the tree must remain suppressed.");
                }
                Assert.AreEqual(pending, notifications.HasPending);
                Assert.AreEqual(0, newWindows.Count);
                Assert.AreSame(root, tree.Root);
                var currentNodes = tree.Root!.Nodes.ToArray();
                CollectionAssert.AreEqual(originalNodes, currentNodes);
                for (int index = 0; index < currentNodes.Length; index++)
                {
                    Assert.AreSame(originalParents[index], currentNodes[index].Parent);
                    var rectangle = currentNodes[index].ComputedRectangle;
                    Assert.AreEqual(originalRectangles[index], rectangle);
                    geometryChecksum = unchecked((geometryChecksum ^ (uint)rectangle.Left) * 16777619);
                    geometryChecksum = unchecked((geometryChecksum ^ (uint)rectangle.Top) * 16777619);
                    geometryChecksum = unchecked((geometryChecksum ^ (uint)rectangle.Right) * 16777619);
                    geometryChecksum = unchecked((geometryChecksum ^ (uint)rectangle.Bottom) * 16777619);
                }
                var currentWindowNodes = root.Windows.ToArray();
                CollectionAssert.AreEqual(originalWindowNodes, currentWindowNodes);
                for (int index = 0; index < currentWindowNodes.Length; index++)
                {
                    Assert.AreSame(originalWindows[index], currentWindowNodes[index].WindowReference);
                    Assert.IsFalse(fixture.Coordinator.FloatingWindows.Contains(handles[index]));
                }
                Assert.AreEqual(hasState, fixture.Coordinator.TryGet(key, out var currentState));
                if (hasState)
                {
                    Assert.AreSame(originalState, currentState);
                    revisionDelta = currentState.Revision - originalRevision;
                    Assert.AreEqual(0L, revisionDelta);
                    Assert.AreSame(originalMaster, currentState.Master);
                    CollectionAssert.AreEqual(originalSatellites, currentState.Satellites.ToArray());
                }
                Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
                Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            }
            Assert.AreEqual((long)windowCount * iterations, minSizeReads);
            Assert.AreEqual(0, placementNotifications);
            Assert.AreEqual(0, fallbackNotifications);
            Assert.AreEqual(0, algorithmicNotifications);
            Assert.AreNotEqual(2166136261U, geometryChecksum);

            string scenario = $"arrange-bookkeeping-{(enabled ? "ms" : "ordinary")}-"
                + windowCount.ToString(CultureInfo.InvariantCulture)
                + $"-{(pending ? "pending" : "stable")}";
            (string Metric, long Value)[] counters =
            [
                ("iterations", iterations),
                ("arranged-passes", arrangedPasses),
                ("allocated-bytes", allocatedBytes),
                ("handle-reads", handleReads),
                ("minsize-reads", minSizeReads),
                ("geometry-checks", iterations),
                ("geometry-checksum", geometryChecksum),
                ("identity-checks", iterations),
                ("revision-checks", iterations),
                ("revision-delta", revisionDelta),
                ("fallback-notifications", fallbackNotifications),
                ("placement-notifications", placementNotifications),
                ("algorithmic-notifications", algorithmicNotifications),
                ("floating-windows", windows.Count(window => fixture.Coordinator.FloatingWindows.Contains(window))),
                ("pending-notifications", notifications.HasPending ? 1 : 0),
                ("new-window-count", newWindows.Count),
            ];
            foreach (var counter in counters)
            {
                TestContext.WriteLine($"PERFCOUNTER {scenario} {counter.Metric} "
                    + counter.Value.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static (int ViewInvalidations, int ScalingReads, int Publications)
            MeasureSettingsRuntimeCounters(bool enabled)
        {
            var settings = EnabledSettings(enabled);
            using var fixture = new ServiceFixture(settings);
            var master = fixture.AddWindow("Master");
            var first = fixture.AddWindow("First satellite");
            var second = fixture.AddWindow("Second satellite");
            fixture.DrainDispatcher();
            fixture.HoldLayoutForSettingsObservation();
            var tree = GetBackend(fixture).GetTree(fixture.Desktop)!;
            tree.Measure();
            tree.Arrange();
            var originalRoot = tree.Root!;
            var originalNodes = originalRoot.Nodes.ToArray();
            var originalRectangles = originalNodes.Select(node => node.ComputedRectangle).ToArray();
            var key = new LayoutStateKey(fixture.Desktop, fixture.Display);
            long revision = 0;
            if (enabled)
            {
                Assert.IsTrue(fixture.Coordinator.TryGet(key, out var initialState));
                revision = initialState.Revision;
            }
            int scalingReads = 0;
            int publications = 0;
            Mock.Get(fixture.Display).SetupGet(item => item.Scaling).Returns(() =>
            {
                scalingReads++;
                return 1.0;
            });
            fixture.CapacityPublicationHook = (_, _, _) => publications++;
            for (int index = 0; index < 20; index++)
            {
                fixture.PublishWithoutDispatch(settings with { PanelFontSize = 12 + index });
            }
            fixture.DrainDispatcher();
            scalingReads = 0;
            publications = 0;
            int beforeInvalidations = fixture.Overlay.InvalidateViewCount;

            for (int index = 0; index < 100; index++)
            {
                fixture.PublishWithoutDispatch(settings with { PanelFontSize = 12 + index });
            }
            fixture.DrainDispatcher();

            var counters = (fixture.Overlay.InvalidateViewCount - beforeInvalidations, scalingReads, publications);
            Assert.AreSame(originalRoot, tree.Root);
            CollectionAssert.AreEqual(originalNodes, tree.Root!.Nodes.ToArray());
            CollectionAssert.AreEqual(originalRectangles,
                tree.Root.Nodes.Select(node => node.ComputedRectangle).ToArray());
            CollectionAssert.AreEquivalent(new[] { master, first, second },
                tree.Root.Windows.Select(node => node.WindowReference).ToArray());
            Assert.AreEqual(settings.WindowPadding, fixture.Overlay.PanelSpacing);
            Assert.AreEqual(new Thickness(0, settings.WindowPadding + settings.PanelHeight, 0, 0),
                fixture.Overlay.PanelPadding);
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            if (enabled)
            {
                Assert.IsTrue(fixture.Coordinator.TryGet(key, out var state));
                Assert.AreEqual(revision, state.Revision);
                Assert.AreSame(master, state.Master);
                CollectionAssert.AreEqual(new[] { first, second }, state.Satellites.ToArray());
                Assert.AreEqual(settings.MasterSatelliteLayout.DefaultSatelliteOrientation, state.SatelliteOrientation);
                Assert.AreEqual(settings.MasterSatelliteLayout.DefaultMasterSide, state.MasterSide);
                Assert.AreEqual(settings.MasterSatelliteLayout.MasterRatio, state.RequestedMasterRatio);
                AssertCapacity(fixture, 3);
            }
            return counters;
        }

        [DataTestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public void UnrelatedSettingsBurstDoesNotTouchLayoutOrQueueDispatcherWork(
            bool enabled, bool workerThread)
        {
            var settings = EnabledSettings(enabled);
            using var fixture = new ServiceFixture(settings);
            var master = fixture.AddWindow("Master");
            var satellite = fixture.AddWindow("Satellite");
            fixture.DrainDispatcher();
            fixture.HoldLayoutForSettingsObservation();
            var tree = GetBackend(fixture).GetTree(fixture.Desktop)!;
            var originalRoot = tree.Root;
            var originalNodes = tree.Root!.Windows.ToArray();
            int originalInvalidations = fixture.Overlay.InvalidateViewCount;
            int publications = 0;
            fixture.CapacityPublicationHook = (_, _, _) => publications++;
            Mock.Get(fixture.Display).Invocations.Clear();
            int posted = 0;
            DispatcherHookEventHandler onPosted = (_, args) =>
            {
                if (args.Operation.Priority != DispatcherPriority.ApplicationIdle)
                {
                    System.Threading.Interlocked.Increment(ref posted);
                }
            };
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.Hooks.OperationPosted += onPosted;
            try
            {
                void PublishBurst()
                {
                    for (int index = 0; index < 100; index++)
                    {
                        fixture.PublishWithoutDispatch(settings with
                        {
                            PanelFontSize = 12 + index,
                            MasterSatelliteLayout = settings.MasterSatelliteLayout with { },
                        });
                    }
                    fixture.PublishWithoutDispatch(settings with { });
                }
                if (workerThread)
                {
                    System.Threading.Tasks.Task.Run(PublishBurst).GetAwaiter().GetResult();
                }
                else
                {
                    PublishBurst();
                }
            }
            finally
            {
                dispatcher.Hooks.OperationPosted -= onPosted;
            }
            // DispatcherFrame.Continue=false posts its own Send wakeup. Observe
            // all publication posts before pumping the fixture control frame.
            fixture.DrainDispatcher();

            Assert.AreEqual(0, posted, "Unrelated and content-identical settings must not queue runtime work.");
            Assert.AreEqual(originalInvalidations, fixture.Overlay.InvalidateViewCount);
            Assert.AreEqual(0, publications);
            Assert.IsFalse(fixture.LayoutInvalidated);
            Mock.Get(fixture.Display).VerifyGet(item => item.Scaling, Times.Never);
            Assert.AreSame(originalRoot, tree.Root);
            CollectionAssert.AreEqual(originalNodes, tree.Root!.Windows.ToArray());
            if (enabled)
            {
                Assert.IsTrue(fixture.Coordinator.TryGet(
                    new LayoutStateKey(fixture.Desktop, fixture.Display), out var state));
                Assert.AreSame(master, state.Master);
                CollectionAssert.AreEqual(new[] { satellite }, state.Satellites.ToArray());
            }
        }

        [DataTestMethod]
        [DataRow(false, true, false)]
        [DataRow(false, false, true)]
        [DataRow(false, true, true)]
        [DataRow(true, true, false)]
        [DataRow(true, false, true)]
        [DataRow(true, true, true)]
        public void GeometrySettingsUpdateBothPanelDimensionsWithOneViewInvalidation(
            bool enabled, bool changePadding, bool changeHeight)
        {
            var settings = EnabledSettings(enabled);
            using var fixture = new ServiceFixture(settings);
            var master = fixture.AddWindow("Master");
            var first = fixture.AddWindow("First satellite");
            var second = fixture.AddWindow("Second satellite");
            fixture.DrainDispatcher();
            fixture.HoldLayoutForSettingsObservation();
            int originalInvalidations = fixture.Overlay.InvalidateViewCount;
            var updated = settings with
            {
                WindowPadding = changePadding ? 9 : settings.WindowPadding,
                PanelHeight = changeHeight ? 27 : settings.PanelHeight,
            };

            fixture.Publish(updated);

            Assert.AreEqual(originalInvalidations + 1, fixture.Overlay.InvalidateViewCount);
            Assert.IsTrue(fixture.LayoutInvalidated);
            Assert.AreEqual(updated.WindowPadding, fixture.Overlay.PanelSpacing);
            Assert.AreEqual(new Thickness(0, updated.PanelHeight + updated.WindowPadding, 0, 0),
                fixture.Overlay.PanelPadding);
            var tree = GetBackend(fixture).GetTree(fixture.Desktop)!;
            foreach (var panel in tree.Root!.Nodes.OfType<PanelNode>())
            {
                Assert.AreEqual(updated.WindowPadding, panel.Spacing);
                Assert.AreEqual(new Rectangle(0, updated.PanelHeight + updated.WindowPadding, 0, 0), panel.Padding);
            }
            if (enabled)
            {
                Assert.IsTrue(fixture.Coordinator.TryGet(
                    new LayoutStateKey(fixture.Desktop, fixture.Display), out var state));
                Assert.AreSame(master, state.Master);
                CollectionAssert.AreEqual(new[] { first, second }, state.Satellites.ToArray());
                Assert.AreEqual(settings.MasterSatelliteLayout.DefaultMasterSide, state.MasterSide);
                Assert.AreEqual(settings.MasterSatelliteLayout.MasterRatio, state.RequestedMasterRatio);
                AssertCapacity(fixture, 3);
            }
            fixture.Publish(updated with { MasterSatelliteLayout = updated.MasterSatelliteLayout with { } });
            Assert.AreEqual(originalInvalidations + 1, fixture.Overlay.InvalidateViewCount);
        }

        [TestMethod]
        public void NonGeometryTilingSettingsApplyWithoutPanelOrCapacityWork()
        {
            var settings = EnabledSettings(true);
            using var fixture = new ServiceFixture(settings);
            fixture.AddWindow("Master");
            fixture.DrainDispatcher();
            fixture.HoldLayoutForSettingsObservation();
            int originalInvalidations = fixture.Overlay.InvalidateViewCount;
            int publications = 0;
            fixture.CapacityPublicationHook = (_, _, _) => publications++;
            Mock.Get(fixture.Display).Invocations.Clear();
            var updated = settings with
            {
                AllocateNewPanelSpace = !settings.AllocateNewPanelSpace,
                AnimateWindowMovement = !settings.AnimateWindowMovement,
                AutoSplitCount = settings.AutoSplitCount + 1,
                DelayReposition = !settings.DelayReposition,
                AutoFloatNewWindows = true,
                AutoCollapsePanels = true,
            };

            fixture.Publish(updated);

            Assert.AreEqual(originalInvalidations, fixture.Overlay.InvalidateViewCount);
            Assert.AreEqual(0, publications);
            Assert.IsFalse(fixture.LayoutInvalidated);
            Mock.Get(fixture.Display).VerifyGet(item => item.Scaling, Times.Never);
            Assert.AreEqual(updated.AllocateNewPanelSpace, fixture.GetServiceField<bool>("m_allocateNewPanelSpace"));
            Assert.AreEqual(updated.AnimateWindowMovement, fixture.GetServiceField<bool>("m_animateWindowMovement"));
            Assert.AreEqual(updated.AutoSplitCount, fixture.GetServiceField<int>("m_autoSplitCount"));
            Assert.AreEqual(updated.DelayReposition, fixture.GetServiceField<bool>("m_delayReposition"));
            Assert.IsTrue(GetBackend(fixture).AutoCollapse);
            var floating = fixture.AddWindow("Auto floated");
            Assert.IsTrue(fixture.Coordinator.FloatingWindows.Contains(floating));
        }

        [TestMethod]
        public void ShowFocusSettingInvalidatesLayoutWithoutTraversingPanels()
        {
            var settings = EnabledSettings(true);
            using var fixture = new ServiceFixture(settings);
            fixture.AddWindow("Master");
            fixture.DrainDispatcher();
            fixture.HoldLayoutForSettingsObservation();
            int originalInvalidations = fixture.Overlay.InvalidateViewCount;
            int publications = 0;
            fixture.CapacityPublicationHook = (_, _, _) => publications++;
            Mock.Get(fixture.Display).Invocations.Clear();

            fixture.Publish(settings with { ShowFocus = true });

            Assert.IsTrue(fixture.GetServiceField<bool>("m_showFocus"));
            Assert.IsTrue(fixture.LayoutInvalidated);
            Assert.AreEqual(originalInvalidations, fixture.Overlay.InvalidateViewCount);
            Assert.AreEqual(0, publications);
            Mock.Get(fixture.Display).VerifyGet(item => item.Scaling, Times.Never);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void QueuedDistinctSettingsApplyIsSuppressedAfterServiceDisposal(bool disposeBeforeApply)
        {
            var settings = EnabledSettings(false);
            using var fixture = new ServiceFixture(settings);
            fixture.DrainDispatcher();
            int originalInvalidations = fixture.Overlay.InvalidateViewCount;
            int originalSpacing = fixture.Overlay.PanelSpacing;
            int posted = 0;
            var dispatcher = Dispatcher.CurrentDispatcher;
            DispatcherHookEventHandler onPosted = (_, args) =>
            {
                if (args.Operation.Priority == DispatcherPriority.Normal)
                {
                    System.Threading.Interlocked.Increment(ref posted);
                }
            };
            dispatcher.Hooks.OperationPosted += onPosted;
            try
            {
                System.Threading.Tasks.Task.Run(() =>
                {
                    for (int index = 0; index < 100; index++)
                    {
                        fixture.PublishWithoutDispatch(settings with { WindowPadding = 12 });
                    }
                }).GetAwaiter().GetResult();
            }
            finally
            {
                dispatcher.Hooks.OperationPosted -= onPosted;
            }
            Assert.AreEqual(1, posted);
            if (disposeBeforeApply) { fixture.Service.Dispose(); }
            fixture.DrainDispatcher();
            Assert.AreEqual(disposeBeforeApply ? originalSpacing : 12, fixture.Overlay.PanelSpacing);
            Assert.AreEqual(originalInvalidations + (disposeBeforeApply ? 0 : 1), fixture.Overlay.InvalidateViewCount);
            fixture.Service.Dispose();
            fixture.Publish(settings with { WindowPadding = 15 });
            Assert.AreEqual(originalInvalidations + (disposeBeforeApply ? 0 : 1), fixture.Overlay.InvalidateViewCount);
        }

        [TestMethod]
        public void ReturningToCurrentSettingsReplacesDeferredCapacityShrinkSettings()
        {
            var settings = EnabledSettings(true, maxSatellites: 3);
            using var fixture = new ServiceFixture(settings, includeSecondDesktop: true);
            fixture.DrainDispatcher();
            var master = fixture.AddWindow("Master");
            var retained = fixture.AddWindow("Retained satellite");
            var firstTail = fixture.AddWindow("First tail");
            var lastTail = fixture.AddWindow("Last tail");
            var shrink = settings with
            {
                MasterSatelliteLayout = settings.MasterSatelliteLayout with { MaxSatellites = 1 },
            };
            var temporary = shrink with
            {
                MasterSatelliteLayout = shrink.MasterSatelliteLayout with
                {
                    DefaultSatelliteOrientation = SatelliteLayoutOrientation.Horizontal,
                },
            };
            int reentrantCalls = 0;
            fixture.TargetMoveAfterOwnershipChange = _ =>
            {
                fixture.TargetMoveAfterOwnershipChange = null;
                reentrantCalls++;
                fixture.PublishWithoutDispatch(temporary);
                Assert.AreEqual(temporary.MasterSatelliteLayout,
                    fixture.GetServiceField<MasterSatelliteLayoutSettings>("m_deferredMasterSatelliteSettings"));
                fixture.PublishWithoutDispatch(shrink);
                Assert.AreEqual(shrink.MasterSatelliteLayout,
                    fixture.GetServiceField<MasterSatelliteLayoutSettings>("m_deferredMasterSatelliteSettings"));
            };

            fixture.Publish(shrink);

            Assert.AreEqual(1, reentrantCalls);
            Assert.IsTrue(fixture.Coordinator.TryGet(
                new LayoutStateKey(fixture.Desktop, fixture.Display), out var state));
            Assert.AreSame(master, state.Master);
            CollectionAssert.AreEqual(new[] { retained }, state.Satellites.ToArray());
            Assert.AreEqual(SatelliteLayoutOrientation.Vertical, state.SatelliteOrientation);
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            Assert.AreSame(fixture.TargetDesktop, fixture.GetWindowDesktop(firstTail));
            Assert.AreSame(fixture.TargetDesktop, fixture.GetWindowDesktop(lastTail));
            Assert.IsNull(fixture.GetServiceField<MasterSatelliteLayoutSettings?>("m_deferredMasterSatelliteSettings"));
            fixture.Publish(temporary);
            Assert.AreEqual(SatelliteLayoutOrientation.Horizontal, state.SatelliteOrientation);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void FailedSettingsPropagationCanRetryTheSameSnapshot(bool enabled)
        {
            var settings = EnabledSettings(enabled);
            using var fixture = new ServiceFixture(settings);
            var master = fixture.AddWindow("Master");
            var satellite = fixture.AddWindow("Satellite");
            fixture.DrainDispatcher();
            fixture.HoldLayoutForSettingsObservation();
            int originalInvalidations = fixture.Overlay.InvalidateViewCount;
            int scalingReads = 0;
            Mock.Get(fixture.Display).SetupGet(item => item.Scaling).Returns(() =>
            {
                if (++scalingReads == 1) { throw new InvalidOperationException("Transient scaling failure"); }
                return 1.0;
            });
            var updated = settings with { WindowPadding = 11, PanelHeight = 23 };

            fixture.Publish(updated);
            Assert.AreEqual(1, scalingReads);
            Assert.AreEqual(originalInvalidations, fixture.Overlay.InvalidateViewCount);
            fixture.Publish(updated with { });

            Assert.AreEqual(originalInvalidations + 1, fixture.Overlay.InvalidateViewCount);
            Assert.IsTrue(fixture.LayoutInvalidated);
            Assert.AreEqual(11, fixture.Overlay.PanelSpacing);
            Assert.AreEqual(new Thickness(0, 34, 0, 0), fixture.Overlay.PanelPadding);
            var tree = GetBackend(fixture).GetTree(fixture.Desktop)!;
            foreach (var panel in tree.Root!.Nodes.OfType<PanelNode>())
            {
                Assert.AreEqual(11, panel.Spacing);
                Assert.AreEqual(new Rectangle(0, 34, 0, 0), panel.Padding);
            }
            if (enabled)
            {
                Assert.IsTrue(fixture.Coordinator.TryGet(
                    new LayoutStateKey(fixture.Desktop, fixture.Display), out var state));
                Assert.AreSame(master, state.Master);
                CollectionAssert.AreEqual(new[] { satellite }, state.Satellites.ToArray());
                AssertCapacity(fixture, 2);
            }
            int readsAfterRetry = scalingReads;
            fixture.Publish(updated with { });
            Assert.AreEqual(readsAfterRetry, scalingReads);
            Assert.AreEqual(originalInvalidations + 1, fixture.Overlay.InvalidateViewCount);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void QueuedOlderSettingsCannotReplaceAnAlreadyAppliedNewerValue(bool newerOnOwnerThread)
        {
            var settings = EnabledSettings(true);
            using var fixture = new ServiceFixture(settings);
            var master = fixture.AddWindow("Master");
            var satellite = fixture.AddWindow("Satellite");
            fixture.DrainDispatcher();
            fixture.HoldLayoutForSettingsObservation();
            Assert.IsTrue(fixture.Coordinator.TryGet(
                new LayoutStateKey(fixture.Desktop, fixture.Display), out var state));
            long revision = state.Revision;
            int invalidations = fixture.Overlay.InvalidateViewCount;
            var older = settings with
            {
                WindowPadding = 9,
                MasterSatelliteLayout = settings.MasterSatelliteLayout with
                {
                    DefaultSatelliteOrientation = SatelliteLayoutOrientation.Horizontal,
                },
            };
            var newer = settings with { WindowPadding = 11 };
            System.Threading.Tasks.Task.Run(() =>
            {
                fixture.PublishWithoutDispatch(older);
                if (!newerOnOwnerThread) { fixture.PublishWithoutDispatch(newer); }
            }).GetAwaiter().GetResult();

            if (newerOnOwnerThread) { fixture.PublishWithoutDispatch(newer); }
            fixture.DrainDispatcher();

            Assert.AreEqual(11, fixture.Overlay.PanelSpacing);
            Assert.AreEqual(new Thickness(0, settings.PanelHeight + 11, 0, 0), fixture.Overlay.PanelPadding);
            Assert.AreEqual(invalidations + (newerOnOwnerThread ? 1 : 2), fixture.Overlay.InvalidateViewCount);
            Assert.AreSame(master, state.Master);
            CollectionAssert.AreEqual(new[] { satellite }, state.Satellites.ToArray());
            Assert.AreEqual(SatelliteLayoutOrientation.Vertical, state.SatelliteOrientation);
            Assert.AreEqual(revision + (newerOnOwnerThread ? 0 : 2), state.Revision,
                "Worker-only publications must retain their original order; only an already superseded callback is skipped.");
            AssertCapacity(fixture, 2);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void DiscoverySkipsDesktopSnapshotWhenNoWindowIsEligible(bool enabled)
        {
            using var fixture = new ServiceFixture(EnabledSettings(enabled));
            var window = fixture.AddWindow("Minimized after registration");
            fixture.DrainDispatcher();
            Mock.Get(window).SetupGet(item => item.State).Returns(WinMan.WindowState.Minimized);
            fixture.VirtualDesktopManagerMock.Invocations.Clear();

            Assert.IsFalse(fixture.Service.DiscoverWindows());

            fixture.VirtualDesktopManagerMock.VerifyGet(item => item.Desktops, Times.Never);
            Assert.IsTrue(GetBackend(fixture).HasWindow(window));
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void DiscoverySharesOneSnapshotAndRetriesItsFailurePerWindow(bool enabled)
        {
            using var fixture = new ServiceFixture(EnabledSettings(enabled));
            var first = fixture.AddWindow("First");
            var second = fixture.AddWindow("Second");
            fixture.DrainDispatcher();
            fixture.VirtualDesktopManagerMock.Invocations.Clear();

            Assert.IsFalse(fixture.Service.DiscoverWindows());

            fixture.VirtualDesktopManagerMock.VerifyGet(item => item.Desktops, Times.Once);
            fixture.VirtualDesktopManagerMock.Invocations.Clear();
            fixture.VirtualDesktopManagerMock.SetupSequence(item => item.Desktops)
                .Throws(new InvalidOperationException("transient snapshot failure"))
                .Returns(new[] { fixture.Desktop })
                .Returns(new[] { fixture.Desktop });

            Assert.IsFalse(fixture.Service.DiscoverWindows());

            fixture.VirtualDesktopManagerMock.VerifyGet(item => item.Desktops, Times.Exactly(3));
            Assert.IsTrue(GetBackend(fixture).HasWindow(first));
            Assert.IsTrue(GetBackend(fixture).HasWindow(second));
        }

        private static TilingWorkspace GetBackend(ServiceFixture fixture) =>
            (TilingWorkspace)typeof(TilingService)
                .GetField("m_backend", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(fixture.Service)!;

        [TestMethod]
        public void ArrangeFailureUsesNewWindowEligibilityCapturedBeforeMeasure()
        {
            using var fixture = new ServiceFixture(EnabledSettings(true));
            var window = fixture.AddWindow("Existing master");
            fixture.DrainDispatcher();
            var backend = (TilingWorkspace)typeof(TilingService)
                .GetField("m_backend", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(fixture.Service)!;
            var newWindows = (HashSet<IWindow>)typeof(TilingService)
                .GetField("m_newWindowSet", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(fixture.Service)!;
            newWindows.Clear();
            var tree = backend.GetTree(fixture.Desktop)!;
            var originalNodes = tree.Root!.Windows.ToArray();
            Mock.Get(window).SetupGet(item => item.MinSize).Returns(() =>
            {
                newWindows.Add(window);
                return new WinMan.Point(10000, 10000);
            });
            var updateTree = (Func<DesktopTree, bool>)typeof(TilingService)
                .GetMethod("UpdateTree", BindingFlags.NonPublic | BindingFlags.Instance)!
                .CreateDelegate(typeof(Func<DesktopTree, bool>), fixture.Service);

            Assert.IsFalse(updateTree(tree));
            CollectionAssert.AreEqual(originalNodes, tree.Root.Windows.ToArray());
            Assert.IsFalse(fixture.Coordinator.FloatingWindows.Contains(window));
            Assert.IsTrue(fixture.Coordinator.TryGet(new LayoutStateKey(fixture.Desktop, fixture.Display), out var state));
            Assert.AreSame(window, state.Master);
        }

        [TestMethod]
        public void ArrangeFailureRetainsEligibilityRemovedDuringMeasure()
        {
            using var fixture = new ServiceFixture(EnabledSettings(true));
            var master = fixture.AddWindow("Master");
            var satellite = fixture.AddWindow("New satellite");
            fixture.DrainDispatcher();
            var backend = GetBackend(fixture);
            var newWindows = fixture.GetServiceField<HashSet<IWindow>>("m_newWindowSet");
            newWindows.Clear();
            newWindows.Add(satellite);
            var tree = backend.GetTree(fixture.Desktop)!;
            bool removedDuringMeasure = false;
            Mock.Get(satellite).SetupGet(item => item.MinSize).Returns(() =>
            {
                removedDuringMeasure = newWindows.Remove(satellite) || removedDuringMeasure;
                return new WinMan.Point(10000, 10000);
            });
            int placementFailures = 0;
            IWindow? failedWindow = null;
            fixture.Service.PlacementFailed += (_, args) =>
            {
                placementFailures++;
                failedWindow = args.FailSource;
                Assert.AreEqual(TilingError.NoValidPlacementExists, args.FailReason);
            };
            var updateTree = (Func<DesktopTree, bool>)typeof(TilingService)
                .GetMethod("UpdateTree", BindingFlags.NonPublic | BindingFlags.Instance)!
                .CreateDelegate(typeof(Func<DesktopTree, bool>), fixture.Service);

            Assert.IsTrue(updateTree(tree));

            Assert.IsTrue(removedDuringMeasure);
            Assert.AreEqual(0, newWindows.Count);
            Assert.AreEqual(1, placementFailures);
            Assert.AreSame(satellite, failedWindow);
            CollectionAssert.AreEqual(
                new[] { master },
                tree.Root!.Windows.Select(node => node.WindowReference).ToArray());
            Assert.IsTrue(fixture.Coordinator.FloatingWindows.Contains(satellite));
            Assert.IsTrue(fixture.Coordinator.TryGet(
                new LayoutStateKey(fixture.Desktop, fixture.Display),
                out var state));
            Assert.AreSame(master, state.Master);
            Assert.AreEqual(0, state.Satellites.Count);
        }

        [TestMethod]
        public void SettingsAndCommandsPublishEnabledDisabledEventsThroughService()
        {
            using var fixture = new ServiceFixture(EnabledSettings(false));
            var events = new List<AlgorithmicLayoutEvent>();
            fixture.Service.AlgorithmicLayoutChanged += (_, layoutEvent) =>
                events.Add(layoutEvent);

            fixture.DrainDispatcher();
            Assert.AreEqual(0, events.Count);

            fixture.Publish(EnabledSettings(true));

            Assert.AreEqual(1, events.Count);
            AssertEvent(
                events[0],
                AlgorithmicLayoutEventKind.LayoutEnabled,
                fixture.Display,
                fixture.Desktop,
                "SettingsChanged",
                "AlgorithmicLayout.LayoutEnabled");
            Assert.IsTrue(fixture.Service.CanToggleMasterSatelliteLayout());

            fixture.Service.ToggleMasterSatelliteLayout();

            Assert.AreEqual(2, events.Count);
            AssertEvent(
                events[1],
                AlgorithmicLayoutEventKind.LayoutDisabled,
                fixture.Display,
                fixture.Desktop,
                "ToggleLayout",
                "AlgorithmicLayout.LayoutDisabled");
            Assert.IsTrue(fixture.Service.CanToggleMasterSatelliteLayout());

            fixture.Service.ToggleMasterSatelliteLayout();

            Assert.AreEqual(3, events.Count);
            AssertEvent(
                events[2],
                AlgorithmicLayoutEventKind.LayoutEnabled,
                fixture.Display,
                fixture.Desktop,
                "ToggleLayout",
                "AlgorithmicLayout.LayoutEnabled");
        }

        [TestMethod]
        public void ChangingDefaultOrientationUpdatesTheActiveCanonicalLayout()
        {
            using var fixture = new ServiceFixture(EnabledSettings(
                true,
                orientation: SatelliteLayoutOrientation.Vertical));
            fixture.DrainDispatcher();
            var master = fixture.AddWindow("Master");
            var firstSatellite = fixture.AddWindow("Satellite 1");
            var secondSatellite = fixture.AddWindow("Satellite 2");
            var key = new LayoutStateKey(fixture.Desktop, fixture.Display);

            Assert.IsTrue(fixture.Coordinator.TryGet(key, out var before));
            Assert.AreEqual(
                SatelliteLayoutOrientation.Vertical,
                before.SatelliteOrientation);
            long revision = before.Revision;

            fixture.Publish(EnabledSettings(
                true,
                orientation: SatelliteLayoutOrientation.Horizontal));

            Assert.IsTrue(fixture.Coordinator.TryGet(key, out var after));
            Assert.AreSame(before, after);
            Assert.AreSame(master, after.Master);
            CollectionAssert.AreEqual(
                new[] { firstSatellite, secondSatellite },
                after.Satellites.ToArray());
            Assert.AreEqual(
                SatelliteLayoutOrientation.Horizontal,
                after.SatelliteOrientation);
            Assert.AreEqual(revision + 1, after.Revision);
            AssertCapacity(fixture, expectedOccupiedSlots: 3);
        }

        [TestMethod]
        public void MoveRightPromotesTheOnlySatelliteAdjacentToRightMaster()
        {
            using var fixture = new ServiceFixture(EnabledSettings(
                true,
                orientation: SatelliteLayoutOrientation.Horizontal,
                masterSide: MasterSide.Right));
            fixture.DrainDispatcher();
            var originalMaster = fixture.AddWindow("Master");
            var satellite = fixture.AddWindow("Firefox-equivalent satellite");
            var key = new LayoutStateKey(fixture.Desktop, fixture.Display);
            var events = new List<AlgorithmicLayoutEvent>();
            fixture.Service.AlgorithmicLayoutChanged += (_, layoutEvent) =>
                events.Add(layoutEvent);

            fixture.Focus(satellite);
            Assert.IsTrue(fixture.Service.CanMoveWindow(TilingDirection.Right));

            fixture.Service.MoveWindow(TilingDirection.Right);
            fixture.DrainDispatcher();

            Assert.IsTrue(fixture.Coordinator.TryGet(key, out var state));
            Assert.AreSame(satellite, state.Master);
            CollectionAssert.AreEqual(
                new[] { originalMaster },
                state.Satellites.ToArray());
            Assert.AreEqual(MasterSide.Right, state.MasterSide);
            Assert.AreEqual(
                SatelliteLayoutOrientation.Horizontal,
                state.SatelliteOrientation);
            Assert.AreSame(satellite, fixture.Service.GetFocus());
            Assert.IsFalse(events.Any(layoutEvent => layoutEvent.Kind is
                AlgorithmicLayoutEventKind.OperationRejected
                or AlgorithmicLayoutEventKind.TransferFailed));
        }

        [TestMethod]
        public void DisposeUnregistersRealServiceParticipantAndOverlay()
        {
            var fixture = new ServiceFixture(EnabledSettings(false));
            fixture.DrainDispatcher();

            Assert.AreEqual(1, fixture.Coordinator.RegisteredDisplayCount);
            Assert.IsFalse(fixture.Overlay.IsDisposed);

            fixture.Service.Dispose();

            Assert.AreEqual(0, fixture.Coordinator.RegisteredDisplayCount);
            Assert.IsTrue(fixture.Overlay.IsDisposed);
            fixture.Service.Dispose();
        }

        [TestMethod]
        public void FloatingMasterPromotesFirstSatelliteAndUnfloatUsesFreeSlot()
        {
            using var fixture = new ServiceFixture(EnabledSettings(true));
            fixture.DrainDispatcher();
            var master = fixture.AddWindow("Master");
            var firstSatellite = fixture.AddWindow("Satellite 1");

            var key = new LayoutStateKey(fixture.Desktop, fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(key, out var before));
            Assert.AreSame(master, before.Master);
            CollectionAssert.AreEqual(
                new[] { firstSatellite },
                before.Satellites.ToArray());
            AssertCapacity(fixture, expectedOccupiedSlots: 2);

            fixture.Focus(master);
            Assert.IsTrue(fixture.Service.CanFloat());
            fixture.Service.Float();

            Assert.IsTrue(fixture.Coordinator.FloatingWindows.Contains(master));
            Assert.IsTrue(fixture.Coordinator.TryGet(key, out var floated));
            Assert.AreSame(firstSatellite, floated.Master);
            Assert.AreEqual(0, floated.Satellites.Count);
            AssertCapacity(fixture, expectedOccupiedSlots: 1);

            Assert.IsTrue(fixture.Service.CanFloat());
            fixture.Service.Float();

            Assert.IsFalse(fixture.Coordinator.FloatingWindows.Contains(master));
            Assert.IsTrue(fixture.Coordinator.TryGet(key, out var restored));
            Assert.AreSame(firstSatellite, restored.Master);
            CollectionAssert.AreEqual(
                new[] { master },
                restored.Satellites.ToArray());
            Assert.AreSame(master, fixture.Service.GetFocus());
            AssertCapacity(fixture, expectedOccupiedSlots: 2);
        }

        [TestMethod]
        public void ExcludedDialogAndPinnedWindowsDoNotConsumeServiceCapacity()
        {
            using var fixture = new ServiceFixture(EnabledSettings(true));
            fixture.DrainDispatcher();
            var master = fixture.AddWindow("Master");
            AssertCapacity(fixture, expectedOccupiedSlots: 1);

            var excluded = fixture.CreateWindow("Excluded");
            fixture.Service.ExclusionMatchers =
                new[] { new ReferenceWindowMatcher(excluded) };
            fixture.AddWindow(excluded);

            Assert.IsTrue(fixture.Coordinator.FloatingWindows.Contains(excluded));
            AssertCapacity(fixture, expectedOccupiedSlots: 1);

            var dialog = fixture.CreateWindow("Dialog", canResize: false);
            fixture.AddWindow(dialog);

            Assert.IsFalse(fixture.Coordinator.FloatingWindows.Contains(dialog));
            AssertCapacity(fixture, expectedOccupiedSlots: 1);

            var pinned = fixture.CreateWindow("Pinned");
            fixture.Pin(pinned);
            fixture.AddWindow(pinned);

            Assert.IsFalse(fixture.Coordinator.FloatingWindows.Contains(pinned));
            AssertCapacity(fixture, expectedOccupiedSlots: 1);
            Assert.IsTrue(fixture.Coordinator.TryGet(
                new LayoutStateKey(fixture.Desktop, fixture.Display),
                out var state));
            Assert.AreSame(master, state.Master);
            Assert.AreEqual(0, state.Satellites.Count);
        }

        [TestMethod]
        public void UnfloatIntoEmptyServiceLayoutRestoresWindowAsMaster()
        {
            using var fixture = new ServiceFixture(EnabledSettings(true));
            fixture.DrainDispatcher();
            var window = fixture.AddWindow("Only window");
            fixture.Focus(window);

            fixture.Service.Float();

            var key = new LayoutStateKey(fixture.Desktop, fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(key, out var empty));
            Assert.IsNull(empty.Master);
            Assert.AreEqual(0, empty.Satellites.Count);
            AssertCapacity(fixture, expectedOccupiedSlots: 0);

            fixture.Service.Float();

            Assert.IsFalse(fixture.Coordinator.FloatingWindows.Contains(window));
            Assert.IsTrue(fixture.Coordinator.TryGet(key, out var restored));
            Assert.AreSame(window, restored.Master);
            Assert.AreEqual(0, restored.Satellites.Count);
            AssertCapacity(fixture, expectedOccupiedSlots: 1);
        }

        [TestMethod]
        public void FullServiceLayoutLeavesOverflowAndFailedUnfloatFloatingOnce()
        {
            using var fixture = new ServiceFixture(EnabledSettings(true));
            fixture.DrainDispatcher();
            var master = fixture.AddWindow("Master");
            var satellite1 = fixture.AddWindow("Satellite 1");
            var satellite2 = fixture.AddWindow("Satellite 2");
            var satellite3 = fixture.AddWindow("Satellite 3");
            AssertCapacity(fixture, expectedOccupiedSlots: 4);

            var events = new List<AlgorithmicLayoutEvent>();
            fixture.Service.AlgorithmicLayoutChanged += (_, layoutEvent) =>
                events.Add(layoutEvent);
            var overflow = fixture.AddWindow("Overflow");

            Assert.IsTrue(fixture.Coordinator.FloatingWindows.Contains(overflow));
            AssertCapacity(fixture, expectedOccupiedSlots: 4);
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            Assert.AreEqual(1, events.Count);
            var floatingEvent = events[0];
            Assert.AreEqual(
                AlgorithmicLayoutEventKind.WindowLeftFloating,
                floatingEvent.Kind);
            Assert.AreEqual(overflow.Handle, floatingEvent.WindowHandle);
            Assert.AreEqual("Overflow", floatingEvent.WindowTitle);
            Assert.AreSame(fixture.Desktop, floatingEvent.SourceDesktop);
            Assert.AreSame(fixture.Desktop, floatingEvent.TargetDesktop);
            Assert.AreSame(fixture.Display, floatingEvent.Display);
            Assert.AreEqual("NoOverflowDestination", floatingEvent.Reason);
            Assert.AreEqual(4, floatingEvent.SourceOccupiedSlots);
            Assert.AreEqual(4, floatingEvent.SourceTotalCapacity);

            fixture.RaiseWindowAdded(overflow);
            Assert.AreEqual(1, events.Count);

            fixture.Focus(overflow);
            fixture.Service.Float();

            Assert.IsTrue(fixture.Coordinator.FloatingWindows.Contains(overflow));
            AssertCapacity(fixture, expectedOccupiedSlots: 4);
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            Assert.AreEqual(1, events.Count);
            Assert.IsTrue(fixture.Coordinator.TryGet(
                new LayoutStateKey(fixture.Desktop, fixture.Display),
                out var state));
            Assert.AreSame(master, state.Master);
            CollectionAssert.AreEqual(
                new[] { satellite1, satellite2, satellite3 },
                state.Satellites.ToArray());
        }

        [TestMethod]
        public void CapacityPublicationFailureAfterDestinationRegistrationRestoresEndpoints()
        {
            using var fixture = new ServiceFixture(
                EnabledSettings(true, maxSatellites: 1),
                includeSecondDesktop: true);
            fixture.DrainDispatcher();
            var master = fixture.AddWindow("Master");
            var satellite = fixture.AddWindow("Satellite");
            var sourceKey = new LayoutStateKey(fixture.Desktop, fixture.Display);
            var targetKey = new LayoutStateKey(
                fixture.TargetDesktop,
                fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var sourceBefore));
            Assert.IsTrue(fixture.Coordinator.TryGet(targetKey, out var targetBefore));
            long sourceRevision = sourceBefore.Revision;
            long targetRevision = targetBefore.Revision;
            bool injected = false;
            fixture.CapacityPublicationHook = (key, capacity, _) =>
            {
                if (!ReferenceEquals(key.VirtualDesktop, fixture.TargetDesktop)
                    || capacity.OccupiedSlots != 1)
                {
                    return;
                }
                fixture.CapacityPublicationHook = null;
                injected = true;
                throw new InvalidOperationException(
                    "Injected publication failure after destination registration.");
            };

            var overflow = fixture.AddWindow("Overflow");

            Assert.IsTrue(injected);
            AssertRestoredOverflowEndpoints(
                fixture,
                master,
                satellite,
                overflow,
                sourceRevision,
                targetRevision);
            var transfer = fixture.Coordinator.SnapshotTransfers()
                .Single(item => item.WindowHandle == overflow.Handle);
            Assert.AreEqual(PendingWindowTransferState.Failed, transfer.State);
            Assert.AreEqual(
                "DestinationMaterializationThrew",
                transfer.TerminalReason);
            fixture.TargetDesktopMock.Verify(
                item => item.MoveWindow(overflow),
                Times.Once);
            fixture.DesktopMock.Verify(
                item => item.MoveWindow(overflow),
                Times.Once);
            CollectionAssert.AreEqual(
                new[] { false },
                fixture.TargetMoveLockSamples);
            CollectionAssert.AreEqual(
                new[] { false },
                fixture.SourceMoveLockSamples);
        }

        [TestMethod]
        public void CommitRejectionAfterDestinationRegistrationRestoresEndpoints()
        {
            using var fixture = new ServiceFixture(
                EnabledSettings(true, maxSatellites: 1),
                includeSecondDesktop: true);
            fixture.DrainDispatcher();
            var master = fixture.AddWindow("Master");
            var satellite = fixture.AddWindow("Satellite");
            var sourceKey = new LayoutStateKey(fixture.Desktop, fixture.Display);
            var targetKey = new LayoutStateKey(
                fixture.TargetDesktop,
                fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var sourceBefore));
            Assert.IsTrue(fixture.Coordinator.TryGet(targetKey, out var targetBefore));
            long sourceRevision = sourceBefore.Revision;
            long targetRevision = targetBefore.Revision;
            bool cancellationInjected = false;
            fixture.CapacityPublicationHook = (key, capacity, _) =>
            {
                if (!ReferenceEquals(key.VirtualDesktop, fixture.TargetDesktop)
                    || capacity.OccupiedSlots != 1)
                {
                    return;
                }
                fixture.CapacityPublicationHook = null;
                var moving = fixture.Coordinator.SnapshotTransfers()
                    .Single(item => !item.IsTerminal);
                cancellationInjected = fixture.Coordinator.Cancel(
                    moving.CorrelationId,
                    moving.WindowHandle,
                    "InjectedCommitRejection");
            };

            var overflow = fixture.AddWindow("Overflow");

            Assert.IsTrue(cancellationInjected);
            AssertRestoredOverflowEndpoints(
                fixture,
                master,
                satellite,
                overflow,
                sourceRevision,
                targetRevision);
            var transfer = fixture.Coordinator.SnapshotTransfers()
                .Single(item => item.WindowHandle == overflow.Handle);
            Assert.AreEqual(PendingWindowTransferState.Cancelled, transfer.State);
            Assert.AreEqual("InjectedCommitRejection", transfer.TerminalReason);
            fixture.TargetDesktopMock.Verify(
                item => item.MoveWindow(overflow),
                Times.Once);
            fixture.DesktopMock.Verify(
                item => item.MoveWindow(overflow),
                Times.Once);
            CollectionAssert.AreEqual(
                new[] { false },
                fixture.TargetMoveLockSamples);
            CollectionAssert.AreEqual(
                new[] { false },
                fixture.SourceMoveLockSamples);
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void LateFirstOwnershipManualMoveIntoFullTargetNeverOverflows(
            bool addedBeforeRemoved)
        {
            using var fixture = new ServiceFixture(
                EnabledSettings(true, maxSatellites: 1),
                includeSecondDesktop: true);
            fixture.DrainDispatcher();
            var moving = fixture.CreateWindow("Manually moved");
            fixture.AddWindow(moving);
            var targetMaster = fixture.AddWindow(
                "Target master",
                fixture.TargetDesktop);
            var targetSatellite = fixture.AddWindow(
                "Target satellite",
                fixture.TargetDesktop);
            var sourceKey = new LayoutStateKey(fixture.Desktop, fixture.Display);
            var targetKey = new LayoutStateKey(
                fixture.TargetDesktop,
                fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var sourceBefore));
            Assert.AreSame(moving, sourceBefore.Master);
            Assert.IsTrue(fixture.Coordinator.TryGet(targetKey, out var targetBefore));
            Assert.AreSame(targetMaster, targetBefore.Master);
            CollectionAssert.AreEqual(
                new[] { targetSatellite },
                targetBefore.Satellites.ToArray());
            // Model an initially inconclusive ownership probe: the backend
            // remains attached to the source, while the event tracker has no
            // remembered desktop when the next OS event arrives.
            fixture.ForgetWindowEventTracking(moving);
            var events = new List<AlgorithmicLayoutEvent>();
            fixture.Service.AlgorithmicLayoutChanged += (_, layoutEvent) =>
                events.Add(layoutEvent);

            fixture.SetWindowDesktop(moving, fixture.TargetDesktop);
            if (addedBeforeRemoved)
            {
                fixture.RaiseWindowAdded(moving);
                fixture.RaiseWindowRemoved(moving);
            }
            else
            {
                fixture.RaiseWindowRemoved(moving);
                fixture.RaiseWindowAdded(moving);
            }

            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var sourceAfter));
            Assert.IsNull(sourceAfter.Master);
            Assert.AreEqual(0, sourceAfter.Satellites.Count);
            Assert.IsTrue(fixture.Coordinator.TryGet(targetKey, out var targetAfter));
            Assert.AreSame(targetMaster, targetAfter.Master);
            CollectionAssert.AreEqual(
                new[] { targetSatellite },
                targetAfter.Satellites.ToArray());
            Assert.IsTrue(fixture.Coordinator.FloatingWindows.Contains(moving));
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            Assert.IsFalse(fixture.Coordinator.SnapshotTransfers()
                .Any(item => item.WindowHandle == moving.Handle));
            Assert.AreEqual(0, fixture.SourceMoveLockSamples.Count);
            Assert.AreEqual(0, fixture.TargetMoveLockSamples.Count);
            Assert.AreEqual(
                1,
                events.Count(item => item.Kind
                    == AlgorithmicLayoutEventKind.WindowLeftFloating));

            fixture.RaiseWindowAdded(moving);
            fixture.RaiseWindowRemoved(moving);

            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            Assert.IsFalse(fixture.Coordinator.SnapshotTransfers()
                .Any(item => item.WindowHandle == moving.Handle));
            Assert.AreEqual(0, fixture.SourceMoveLockSamples.Count);
            Assert.AreEqual(0, fixture.TargetMoveLockSamples.Count);
            Assert.AreEqual(
                1,
                events.Count(item => item.Kind
                    == AlgorithmicLayoutEventKind.WindowLeftFloating));
        }

        [TestMethod]
        public void SuccessfulOverflowMovesOutsideLocksAndCommitsTargetMaster()
        {
            using var fixture = new ServiceFixture(
                EnabledSettings(true, maxSatellites: 1),
                includeSecondDesktop: true);
            fixture.DrainDispatcher();
            var master = fixture.AddWindow("Master");
            var satellite = fixture.AddWindow("Satellite");
            var events = new List<AlgorithmicLayoutEvent>();
            int placementFailures = 0;
            fixture.Service.AlgorithmicLayoutChanged += (_, layoutEvent) =>
                events.Add(layoutEvent);
            fixture.Service.PlacementFailed += (_, _) => placementFailures++;

            var overflow = fixture.AddWindow("Overflow");

            var sourceKey = new LayoutStateKey(fixture.Desktop, fixture.Display);
            var targetKey = new LayoutStateKey(
                fixture.TargetDesktop,
                fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var source));
            Assert.AreSame(master, source.Master);
            CollectionAssert.AreEqual(
                new[] { satellite },
                source.Satellites.ToArray());
            Assert.IsTrue(fixture.Coordinator.TryGet(targetKey, out var target));
            Assert.AreSame(overflow, target.Master);
            Assert.AreEqual(0, target.Satellites.Count);
            Assert.IsFalse(fixture.Coordinator.FloatingWindows.Contains(overflow));
            Assert.AreSame(
                fixture.TargetDesktop,
                fixture.GetWindowDesktop(overflow));
            var transfer = fixture.Coordinator.SnapshotTransfers()
                .Single(item => item.WindowHandle == overflow.Handle);
            Assert.AreEqual(PendingWindowTransferState.Committed, transfer.State);
            Assert.AreEqual(0, placementFailures);
            var movedEvent = events.Single(item => item.Kind
                == AlgorithmicLayoutEventKind.WindowMovedToDesktop);
            Assert.AreEqual(overflow.Handle, movedEvent.WindowHandle);
            Assert.AreEqual("Overflow", movedEvent.WindowTitle);
            Assert.AreSame(fixture.Desktop, movedEvent.SourceDesktop);
            Assert.AreSame(fixture.TargetDesktop, movedEvent.TargetDesktop);
            Assert.AreSame(fixture.Display, movedEvent.Display);
            Assert.AreEqual(transfer.CorrelationId, movedEvent.CorrelationId);
            Assert.AreEqual("CapacityReached", movedEvent.Reason);
            Assert.AreEqual(
                "AlgorithmicLayout.WindowMovedToDesktop",
                movedEvent.MessageKey);
            Assert.AreEqual(2, movedEvent.SourceOccupiedSlots);
            Assert.AreEqual(2, movedEvent.SourceTotalCapacity);
            Assert.IsFalse(events.Any(item => item.Kind is
                AlgorithmicLayoutEventKind.TransferFailed
                or AlgorithmicLayoutEventKind.WindowLeftFloating));
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            fixture.TargetDesktopMock.Verify(
                item => item.MoveWindow(overflow),
                Times.Once);
            fixture.DesktopMock.Verify(
                item => item.MoveWindow(overflow),
                Times.Never);
            CollectionAssert.AreEqual(
                new[] { false },
                fixture.TargetMoveLockSamples);
            Assert.AreEqual(0, fixture.SourceMoveLockSamples.Count);
        }

        [TestMethod]
        public void TransientNewWindowOwnershipStillOverflowsToExistingDesktop()
        {
            using var fixture = new ServiceFixture(
                EnabledSettings(true, maxSatellites: 1),
                includeSecondDesktop: true);
            fixture.DrainDispatcher();
            var master = fixture.AddWindow("Master");
            var satellite = fixture.AddWindow("Satellite");
            fixture.SourceOwnershipProbeFailuresRemaining = 2;

            var overflow = fixture.AddWindow("Overflow");
            fixture.DrainIncomingOwnershipProbes();

            var sourceKey = new LayoutStateKey(fixture.Desktop, fixture.Display);
            var targetKey = new LayoutStateKey(
                fixture.TargetDesktop,
                fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var source));
            Assert.AreSame(master, source.Master);
            CollectionAssert.AreEqual(
                new[] { satellite },
                source.Satellites.ToArray());
            Assert.IsTrue(fixture.Coordinator.TryGet(targetKey, out var target));
            Assert.AreSame(overflow, target.Master);
            Assert.IsFalse(fixture.Coordinator.FloatingWindows.Contains(overflow));
            Assert.AreSame(
                fixture.TargetDesktop,
                fixture.GetWindowDesktop(overflow));
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            fixture.TargetDesktopMock.Verify(
                item => item.MoveWindow(overflow),
                Times.Once);
        }

        [TestMethod]
        public void PostMoveOwnershipDelayCommitsWithoutWorkspaceEvent()
        {
            using var fixture = new ServiceFixture(
                EnabledSettings(true, maxSatellites: 1),
                includeSecondDesktop: true);
            fixture.DrainDispatcher();
            var master = fixture.AddWindow("Master");
            var satellite = fixture.AddWindow("Satellite");
            var overflow = fixture.CreateWindow("Delayed post-move ownership");
            fixture.SetOwnershipProbeSequence(
                overflow,
                fixture.Desktop,
                fixture.Desktop,
                null,
                null,
                null,
                null,
                null,
                fixture.TargetDesktop);
            var events = new List<AlgorithmicLayoutEvent>();
            fixture.Service.AlgorithmicLayoutChanged += (_, layoutEvent) =>
                events.Add(layoutEvent);
            var targetKey = new LayoutStateKey(
                fixture.TargetDesktop,
                fixture.Display);

            fixture.AddWindow(overflow);
            fixture.DrainIncomingOwnershipProbesUntil(() =>
                fixture.Coordinator.TryGet(targetKey, out var targetState)
                    && ReferenceEquals(targetState.Master, overflow));

            var sourceKey = new LayoutStateKey(fixture.Desktop, fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var source));
            Assert.AreSame(master, source.Master);
            CollectionAssert.AreEqual(
                new[] { satellite },
                source.Satellites.ToArray());
            Assert.IsTrue(fixture.Coordinator.TryGet(targetKey, out var target));
            Assert.AreSame(overflow, target.Master);
            Assert.AreSame(
                fixture.TargetDesktop,
                fixture.GetWindowDesktop(overflow));
            Assert.IsFalse(fixture.Coordinator.FloatingWindows.Contains(overflow));
            Assert.IsTrue(
                fixture.GetOwnershipProbeAttemptCount(overflow) >= 8,
                "The test did not retain target ownership beyond the former four-dispatcher-turn retry window.");
            Assert.AreEqual(
                1,
                events.Count(item => item.Kind
                    == AlgorithmicLayoutEventKind.WindowMovedToDesktop));
            Assert.IsFalse(events.Any(item => item.Kind is
                AlgorithmicLayoutEventKind.TransferFailed
                or AlgorithmicLayoutEventKind.WindowLeftFloating));
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            fixture.TargetDesktopMock.Verify(
                item => item.MoveWindow(overflow),
                Times.Once);
        }

        [TestMethod]
        public void StaleSourceDiscoveryDoesNotCancelPostMoveTransfer()
        {
            using var fixture = new ServiceFixture(
                EnabledSettings(true, maxSatellites: 1),
                includeSecondDesktop: true);
            fixture.DrainDispatcher();
            var master = fixture.AddWindow("Master");
            var satellite = fixture.AddWindow("Satellite");
            var overflow = fixture.CreateWindow("Stale source ownership");
            fixture.SetOwnershipProbeSequence(
                overflow,
                fixture.Desktop,
                fixture.Desktop,
                fixture.Desktop,
                fixture.Desktop);

            fixture.AddWindow(overflow);

            Assert.AreEqual(1, fixture.Coordinator.ActiveTransferCount);
            Assert.AreEqual(1, fixture.Coordinator.ReservationCount);
            Assert.IsFalse(fixture.Coordinator.FloatingWindows.Contains(overflow));
            fixture.Service.DiscoverWindows();
            Assert.AreEqual(1, fixture.Coordinator.ActiveTransferCount);
            Assert.AreEqual(1, fixture.Coordinator.ReservationCount);
            Assert.IsFalse(fixture.Coordinator.FloatingWindows.Contains(overflow));
            fixture.TargetDesktopMock.Verify(
                item => item.MoveWindow(overflow),
                Times.Once);

            fixture.ReplaceRemainingOwnershipProbeSequence(
                overflow,
                fixture.TargetDesktop);
            var targetKey = new LayoutStateKey(
                fixture.TargetDesktop,
                fixture.Display);
            fixture.DrainIncomingOwnershipProbesUntil(() =>
                fixture.Coordinator.TryGet(targetKey, out var targetState)
                    && ReferenceEquals(targetState.Master, overflow));

            var sourceKey = new LayoutStateKey(fixture.Desktop, fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var source));
            Assert.AreSame(master, source.Master);
            CollectionAssert.AreEqual(
                new[] { satellite },
                source.Satellites.ToArray());
            Assert.IsTrue(fixture.Coordinator.TryGet(targetKey, out var target));
            Assert.AreSame(overflow, target.Master);
            Assert.IsFalse(fixture.Coordinator.FloatingWindows.Contains(overflow));
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            fixture.TargetDesktopMock.Verify(
                item => item.MoveWindow(overflow),
                Times.Once);
        }

        [TestMethod]
        public void DiscoveredFullLayoutWindowOverflowsToExistingDesktop()
        {
            using var fixture = new ServiceFixture(
                EnabledSettings(true, maxSatellites: 1),
                includeSecondDesktop: true);
            fixture.DrainDispatcher();
            var master = fixture.AddWindow("Master");
            var satellite = fixture.AddWindow("Satellite");
            var discovered = fixture.CreateWindow(
                "Discovered overflow",
                canResize: false);
            fixture.AddWindow(discovered);

            fixture.SetCanResize(discovered, canResize: true);
            fixture.Service.DiscoverWindows();
            fixture.DrainDispatcher();

            var sourceKey = new LayoutStateKey(fixture.Desktop, fixture.Display);
            var targetKey = new LayoutStateKey(
                fixture.TargetDesktop,
                fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var source));
            Assert.AreSame(master, source.Master);
            CollectionAssert.AreEqual(
                new[] { satellite },
                source.Satellites.ToArray());
            Assert.IsTrue(fixture.Coordinator.TryGet(targetKey, out var target));
            Assert.AreSame(discovered, target.Master);
            Assert.IsFalse(
                fixture.Coordinator.FloatingWindows.Contains(discovered));
            Assert.AreSame(
                fixture.TargetDesktop,
                fixture.GetWindowDesktop(discovered));
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            fixture.TargetDesktopMock.Verify(
                item => item.MoveWindow(discovered),
                Times.Once);
        }

        [TestMethod]
        public void IncomingOwnershipProbeSurvivesHwndReuse()
        {
            using var fixture = new ServiceFixture(
                EnabledSettings(true, maxSatellites: 1),
                includeSecondDesktop: true);
            fixture.DrainDispatcher();
            var master = fixture.AddWindow("Master");
            var satellite = fixture.AddWindow("Satellite");
            var reusedHandle = new IntPtr(0x4242);
            fixture.SourceOwnershipProbeFailuresRemaining = 100;

            var stale = fixture.CreateWindowWithHandle(
                "Stale wrapper",
                reusedHandle);
            fixture.AddWindow(stale);
            var replacement = fixture.CreateWindowWithHandle(
                "Replacement wrapper",
                reusedHandle);
            fixture.AddWindow(replacement);

            // A delayed removal for the old wrapper must neither cancel the
            // replacement probe nor let the old timer act on the reused HWND.
            fixture.RaiseWindowRemoved(stale);
            fixture.SourceOwnershipProbeFailuresRemaining = 0;
            fixture.DrainIncomingOwnershipProbes();

            var sourceKey = new LayoutStateKey(fixture.Desktop, fixture.Display);
            var targetKey = new LayoutStateKey(
                fixture.TargetDesktop,
                fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var source));
            Assert.AreSame(master, source.Master);
            CollectionAssert.AreEqual(
                new[] { satellite },
                source.Satellites.ToArray());
            Assert.IsTrue(fixture.Coordinator.TryGet(targetKey, out var target));
            Assert.AreSame(replacement, target.Master);
            Assert.IsFalse(
                fixture.Coordinator.FloatingWindows.Contains(replacement));
            Assert.AreSame(
                fixture.TargetDesktop,
                fixture.GetWindowDesktop(replacement));
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            fixture.TargetDesktopMock.Verify(
                item => item.MoveWindow(replacement),
                Times.Once);
            fixture.TargetDesktopMock.Verify(
                item => item.MoveWindow(stale),
                Times.Never);
        }

        [TestMethod]
        public void DeadStaleGenerationCleanupDoesNotCancelReplacementOwnershipProbe()
        {
            using var fixture = new ServiceFixture(
                EnabledSettings(true, maxSatellites: 1),
                includeSecondDesktop: true);
            fixture.DrainDispatcher();
            var master = fixture.AddWindow("Master");
            var satellite = fixture.AddWindow("Satellite");
            var reusedHandle = new IntPtr(0x4243);
            fixture.SourceOwnershipProbeFailuresRemaining = 100;

            var stale = fixture.CreateWindowWithHandle(
                "Destroyed stale wrapper",
                reusedHandle);
            fixture.AddWindow(stale);
            var replacement = fixture.CreateWindowWithHandle(
                "Live replacement wrapper",
                reusedHandle);
            fixture.AddWindow(replacement);

            fixture.SetWindowAlive(stale, false);
            fixture.RaiseWindowDestroyed(stale);
            fixture.RaiseWindowRemoved(stale);
            fixture.SourceOwnershipProbeFailuresRemaining = 0;
            fixture.DrainIncomingOwnershipProbes();

            var sourceKey = new LayoutStateKey(fixture.Desktop, fixture.Display);
            var targetKey = new LayoutStateKey(
                fixture.TargetDesktop,
                fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var source));
            Assert.AreSame(master, source.Master);
            CollectionAssert.AreEqual(
                new[] { satellite },
                source.Satellites.ToArray());
            Assert.IsTrue(fixture.Coordinator.TryGet(targetKey, out var target));
            Assert.AreSame(replacement, target.Master);
            Assert.IsFalse(
                fixture.Coordinator.FloatingWindows.Contains(replacement));
            Assert.AreSame(
                fixture.TargetDesktop,
                fixture.GetWindowDesktop(replacement));
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            fixture.TargetDesktopMock.Verify(
                item => item.MoveWindow(replacement),
                Times.Once);
            fixture.TargetDesktopMock.Verify(
                item => item.MoveWindow(stale),
                Times.Never);
        }

        [TestMethod]
        public void ActiveTransferFromReusedHwndGenerationCannotAffectReplacement()
        {
            using var fixture = new ServiceFixture(
                EnabledSettings(true, maxSatellites: 1),
                includeSecondDesktop: true);
            fixture.DrainDispatcher();
            var master = fixture.AddWindow("Master");
            var satellite = fixture.AddWindow("Satellite");
            var reusedHandle = new IntPtr(0x4244);
            var stale = fixture.CreateWindowWithHandle(
                "Transferring stale generation",
                reusedHandle);
            fixture.SetOwnershipProbeSequence(
                stale,
                fixture.Desktop,
                fixture.Desktop,
                null);
            var events = new List<AlgorithmicLayoutEvent>();
            fixture.Service.AlgorithmicLayoutChanged += (_, layoutEvent) =>
                events.Add(layoutEvent);

            fixture.AddWindow(stale);

            var oldTransfer = fixture.Coordinator.SnapshotTransfers()
                .Single(item => item.WindowHandle == reusedHandle);
            Assert.IsFalse(oldTransfer.IsTerminal);
            Assert.AreEqual(1, fixture.Coordinator.ReservationCount);

            var replacement = fixture.CreateWindowWithHandle(
                "Live replacement generation",
                reusedHandle);
            fixture.AddWindow(replacement);
            fixture.SetWindowAlive(stale, false);
            fixture.RaiseWindowDestroyed(stale);
            fixture.RaiseWindowRemoved(stale);
            fixture.DrainIncomingOwnershipProbesUntil(() =>
                fixture.Coordinator.ActiveTransferCount == 0
                    && fixture.Coordinator.ReservationCount == 0);

            var sourceKey = new LayoutStateKey(fixture.Desktop, fixture.Display);
            var targetKey = new LayoutStateKey(
                fixture.TargetDesktop,
                fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var source));
            Assert.AreSame(master, source.Master);
            CollectionAssert.AreEqual(
                new[] { satellite },
                source.Satellites.ToArray());
            Assert.IsTrue(fixture.Coordinator.TryGet(targetKey, out var target));
            Assert.AreSame(replacement, target.Master);
            Assert.IsFalse(fixture.Coordinator.FloatingWindows.Contains(replacement));
            Assert.AreSame(
                fixture.TargetDesktop,
                fixture.GetWindowDesktop(replacement));
            Assert.IsTrue(fixture.EventTracker.TryGetWindow(
                reusedHandle,
                out var currentGeneration));
            Assert.AreSame(replacement, currentGeneration);
            Assert.IsFalse(events.Any(item =>
                item.CorrelationId == oldTransfer.CorrelationId
                    && item.WindowTitle == "Live replacement generation"));
            fixture.TargetDesktopMock.Verify(
                item => item.MoveWindow(stale),
                Times.Once);
            fixture.TargetDesktopMock.Verify(
                item => item.MoveWindow(replacement),
                Times.Once);
        }

        [TestMethod]
        public void DuplicateRetiredAddedAndQueuedPositionChangedCannotReclaimReusedHwnd()
        {
            using var fixture = new ServiceFixture(EnabledSettings(true));
            fixture.DrainDispatcher();
            var reusedHandle = new IntPtr(0x4245);
            var stale = fixture.CreateWindowWithHandle(
                "Retired wrapper",
                reusedHandle);
            fixture.AddWindow(stale);

            // Queue the old wrapper's callback while it is still subscribed, but
            // do not let the tiling Dispatcher consume it before the replacement
            // generation has become current.
            fixture.QueuePositionChangedFromWorker(
                stale,
                Rectangle.OffsetAndSize(120, 120, 800, 600));

            var replacement = fixture.CreateWindowWithHandle(
                "Replacement after queued callback",
                reusedHandle);
            fixture.AddWindow(replacement);

            // The retired wrapper deliberately still reports IsAlive=true. Its
            // delayed Removed first drops the exact old wrapper-to-handle cache;
            // a later duplicate Added must still be rejected by generation
            // identity, not merely by a native-liveness check.
            fixture.RaiseWindowRemoved(stale);
            fixture.RaiseWindowAdded(stale);
            fixture.DrainDispatcher();

            var sourceKey = new LayoutStateKey(fixture.Desktop, fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var source));
            Assert.AreSame(replacement, source.Master);
            Assert.AreEqual(0, source.Satellites.Count);
            Assert.IsTrue(fixture.EventTracker.TryGetWindow(
                reusedHandle,
                out var currentGeneration));
            Assert.AreSame(replacement, currentGeneration);
            Assert.IsFalse(fixture.Coordinator.FloatingWindows.Contains(replacement));
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
        }

        [TestMethod]
        public void DestroyBeforeReplacementAddedRetainsGenerationUntilCanonicalPurge()
        {
            using var fixture = new ServiceFixture(EnabledSettings(true));
            fixture.DrainDispatcher();
            var reusedHandle = new IntPtr(0x4246);
            var stale = fixture.CreateWindowWithHandle(
                "Destroyed before replacement",
                reusedHandle);
            fixture.AddWindow(stale);

            fixture.SetWindowAlive(stale, false);
            fixture.RaiseWindowDestroyed(stale);

            var replacement = fixture.CreateWindowWithHandle(
                "Replacement after destroy",
                reusedHandle);
            fixture.AddWindow(replacement);

            // A Win32 wrapper can appear live again once the numeric HWND belongs
            // to the replacement. Neither a late removal nor a duplicate add may
            // make that retired object current again.
            fixture.SetWindowAlive(stale, true);
            fixture.RaiseWindowRemoved(stale);
            fixture.RaiseWindowAdded(stale);
            fixture.DrainDispatcher();

            var sourceKey = new LayoutStateKey(fixture.Desktop, fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var source));
            Assert.AreSame(replacement, source.Master);
            Assert.AreEqual(0, source.Satellites.Count);
            Assert.IsTrue(fixture.EventTracker.TryGetWindow(
                reusedHandle,
                out var currentGeneration));
            Assert.AreSame(replacement, currentGeneration);
            Assert.IsFalse(fixture.Coordinator.FloatingWindows.Contains(replacement));
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
        }

        [TestMethod]
        public void DeadWindowBeforeDeferredPostMoveProbeClosesTransferWithoutRecovery()
        {
            using var fixture = new ServiceFixture(
                EnabledSettings(true, maxSatellites: 1),
                includeSecondDesktop: true);
            fixture.DrainDispatcher();
            var master = fixture.AddWindow("Master");
            var satellite = fixture.AddWindow("Satellite");
            var overflow = fixture.CreateWindow("Dead deferred overflow");
            fixture.SetOwnershipProbeSequence(
                overflow,
                fixture.Desktop,
                fixture.Desktop,
                null);
            var events = new List<AlgorithmicLayoutEvent>();
            fixture.Service.AlgorithmicLayoutChanged += (_, layoutEvent) =>
                events.Add(layoutEvent);

            fixture.AddWindow(overflow);

            var transfer = fixture.Coordinator.SnapshotTransfers()
                .Single(item => item.WindowHandle == overflow.Handle);
            Assert.IsFalse(transfer.IsTerminal);
            Assert.AreEqual(1, fixture.Coordinator.ActiveTransferCount);
            Assert.AreEqual(1, fixture.Coordinator.ReservationCount);
            int ownershipAttemptsBeforeDeath =
                fixture.GetOwnershipProbeAttemptCount(overflow);

            // Model Win32 marking the old wrapper dead before the first deferred
            // ownership tick. A numeric HWND could already have been recycled, so
            // reconciliation must not call HasWindow or perform terminal recovery
            // through this wrapper.
            fixture.SetWindowAlive(overflow, false);
            fixture.ReplaceRemainingOwnershipProbeSequence(
                overflow,
                fixture.TargetDesktop);
            fixture.DrainIncomingOwnershipProbesUntil(() =>
                fixture.Coordinator.ActiveTransferCount == 0
                    && fixture.Coordinator.ReservationCount == 0);

            Assert.IsTrue(fixture.Coordinator.TryGetTransfer(
                transfer.CorrelationId,
                out var closed));
            Assert.AreEqual(PendingWindowTransferState.Cancelled, closed.State);
            Assert.AreEqual("WindowClosed", closed.TerminalReason);
            Assert.AreEqual(
                ownershipAttemptsBeforeDeath,
                fixture.GetOwnershipProbeAttemptCount(overflow),
                "A dead wrapper must be rejected before the deferred HasWindow probe.");
            var sourceKey = new LayoutStateKey(fixture.Desktop, fixture.Display);
            var targetKey = new LayoutStateKey(
                fixture.TargetDesktop,
                fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var source));
            Assert.AreSame(master, source.Master);
            CollectionAssert.AreEqual(
                new[] { satellite },
                source.Satellites.ToArray());
            Assert.IsTrue(fixture.Coordinator.TryGet(targetKey, out var target));
            Assert.IsNull(target.Master);
            Assert.AreEqual(0, target.Satellites.Count);
            Assert.IsFalse(fixture.Coordinator.FloatingWindows.Contains(overflow));
            Assert.IsFalse(events.Any(item => item.Kind is
                AlgorithmicLayoutEventKind.WindowMovedToDesktop
                or AlgorithmicLayoutEventKind.TransferFailed
                or AlgorithmicLayoutEventKind.WindowLeftFloating));
            fixture.TargetDesktopMock.Verify(
                item => item.MoveWindow(overflow),
                Times.Once);
            fixture.DesktopMock.Verify(
                item => item.MoveWindow(overflow),
                Times.Never);
        }

        [TestMethod]
        public void IncomingOwnershipRetryIsBoundedAcrossAlternatingObservations()
        {
            using var fixture = new ServiceFixture(
                EnabledSettings(true, maxSatellites: 1),
                includeSecondDesktop: true);
            fixture.DrainDispatcher();
            var master = fixture.AddWindow("Master");
            var satellite = fixture.AddWindow("Satellite");
            var overflow = fixture.CreateWindow("Alternating ownership");
            fixture.SetOwnershipProbeSequence(
                overflow,
                null,
                null,
                fixture.Desktop,
                null,
                fixture.Desktop,
                null,
                fixture.Desktop,
                null,
                fixture.Desktop,
                null,
                fixture.Desktop,
                null);
            var events = new List<AlgorithmicLayoutEvent>();
            fixture.Service.AlgorithmicLayoutChanged += (_, layoutEvent) =>
                events.Add(layoutEvent);
            fixture.TargetMoveAfterOwnershipChange = movedWindow =>
            {
                if (ReferenceEquals(movedWindow, overflow))
                {
                    // Once MoveWindow really ran, model stable ownership at its
                    // destination so the alternating script targets only the
                    // pre-move retry/dirty-check boundary under test.
                    fixture.ReplaceRemainingOwnershipProbeSequence(
                        overflow,
                        fixture.TargetDesktop);
                }
            };
            var targetKey = new LayoutStateKey(
                fixture.TargetDesktop,
                fixture.Display);

            fixture.AddWindow(overflow);
            fixture.DrainIncomingOwnershipProbesUntil(() =>
                fixture.Coordinator.TryGet(targetKey, out var state)
                    && ReferenceEquals(state.Master, overflow));

            var sourceKey = new LayoutStateKey(fixture.Desktop, fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var source));
            Assert.AreSame(master, source.Master);
            CollectionAssert.AreEqual(
                new[] { satellite },
                source.Satellites.ToArray());
            Assert.IsTrue(fixture.Coordinator.TryGet(targetKey, out var target));
            Assert.AreSame(overflow, target.Master);
            Assert.IsFalse(fixture.Coordinator.FloatingWindows.Contains(overflow));
            Assert.AreSame(
                fixture.TargetDesktop,
                fixture.GetWindowDesktop(overflow));
            Assert.IsTrue(
                fixture.GetOwnershipProbeAttemptCount(overflow) <= 9,
                "Ownership resolution exceeded two eager observations, the six-probe retry budget, and one post-move reconciliation.");
            Assert.AreEqual(
                1,
                events.Count(item => item.Kind
                    == AlgorithmicLayoutEventKind.WindowMovedToDesktop));
            Assert.IsFalse(events.Any(item => item.Kind is
                AlgorithmicLayoutEventKind.TransferFailed
                or AlgorithmicLayoutEventKind.WindowLeftFloating));
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            fixture.TargetDesktopMock.Verify(
                item => item.MoveWindow(overflow),
                Times.Once);
        }

        [TestMethod]
        public void ManualMoveDuringOwnershipRetryNeverOverflowsAgain()
        {
            using var fixture = new ServiceFixture(
                EnabledSettings(true, maxSatellites: 1),
                includeSecondDesktop: true,
                includeThirdDesktop: true);
            fixture.DrainDispatcher();
            var sourceMaster = fixture.AddWindow("Source master");
            var sourceSatellite = fixture.AddWindow("Source satellite");
            var targetMaster = fixture.AddWindow(
                "Target master",
                fixture.TargetDesktop);
            var targetSatellite = fixture.AddWindow(
                "Target satellite",
                fixture.TargetDesktop);
            var moving = fixture.CreateWindow("Manual move during retry");
            fixture.RememberWindowDesktop(moving, fixture.Desktop);
            fixture.SetOwnershipProbeSequence(
                moving,
                null,
                null,
                null,
                null,
                null,
                null);
            var events = new List<AlgorithmicLayoutEvent>();
            fixture.Service.AlgorithmicLayoutChanged += (_, layoutEvent) =>
                events.Add(layoutEvent);

            fixture.AddWindow(moving);
            Assert.IsFalse(fixture.Coordinator.FloatingWindows.Contains(moving));

            // Model the user's desktop move while Win32/VDM ownership is still
            // temporarily inconclusive. The next retry observes the chosen target.
            fixture.SetWindowDesktop(moving, fixture.TargetDesktop);
            fixture.SetOwnershipProbeSequence(moving, fixture.TargetDesktop);
            fixture.DrainIncomingOwnershipProbesUntil(
                () => fixture.Coordinator.FloatingWindows.Contains(moving));

            var sourceKey = new LayoutStateKey(fixture.Desktop, fixture.Display);
            var targetKey = new LayoutStateKey(
                fixture.TargetDesktop,
                fixture.Display);
            var thirdKey = new LayoutStateKey(
                fixture.ThirdDesktop,
                fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var source));
            Assert.AreSame(sourceMaster, source.Master);
            CollectionAssert.AreEqual(
                new[] { sourceSatellite },
                source.Satellites.ToArray());
            Assert.IsTrue(fixture.Coordinator.TryGet(targetKey, out var target));
            Assert.AreSame(targetMaster, target.Master);
            CollectionAssert.AreEqual(
                new[] { targetSatellite },
                target.Satellites.ToArray());
            Assert.IsTrue(fixture.Coordinator.TryGet(thirdKey, out var third));
            Assert.IsNull(third.Master);
            Assert.AreEqual(0, third.Satellites.Count);
            Assert.AreSame(
                fixture.TargetDesktop,
                fixture.GetWindowDesktop(moving));
            Assert.AreEqual(
                1,
                events.Count(item => item.Kind
                    == AlgorithmicLayoutEventKind.WindowLeftFloating));
            Assert.IsFalse(events.Any(item => item.Kind is
                AlgorithmicLayoutEventKind.WindowMovedToDesktop
                or AlgorithmicLayoutEventKind.TransferFailed));
            Assert.IsFalse(fixture.Coordinator.SnapshotTransfers()
                .Any(item => item.WindowHandle == moving.Handle));
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            fixture.TargetDesktopMock.Verify(
                item => item.MoveWindow(moving),
                Times.Never);
            fixture.ThirdDesktopMock.Verify(
                item => item.MoveWindow(moving),
                Times.Never);
        }

        [TestMethod]
        public void UnresolvedNewWindowOwnershipFallsBackOnceAfterBoundedRetries()
        {
            using var fixture = new ServiceFixture(
                EnabledSettings(true, maxSatellites: 1),
                includeSecondDesktop: true);
            fixture.DrainDispatcher();
            fixture.AddWindow("Master");
            fixture.AddWindow("Satellite");
            fixture.SourceOwnershipProbeFailuresRemaining = 100;
            var events = new List<AlgorithmicLayoutEvent>();
            fixture.Service.AlgorithmicLayoutChanged += (_, layoutEvent) =>
                events.Add(layoutEvent);

            var overflow = fixture.AddWindow("Unresolved overflow");
            fixture.RaiseWindowAdded(overflow);
            fixture.DrainIncomingOwnershipProbes();

            Assert.IsTrue(fixture.Coordinator.FloatingWindows.Contains(overflow));
            Assert.AreSame(fixture.Desktop, fixture.GetWindowDesktop(overflow));
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            Assert.AreEqual(
                1,
                events.Count(item => item.Kind
                    == AlgorithmicLayoutEventKind.WindowLeftFloating));
            fixture.TargetDesktopMock.Verify(
                item => item.MoveWindow(overflow),
                Times.Never);
        }

        [TestMethod]
        public void MoveFailureAfterOwnershipChangeRollsBackOutsideLocks()
        {
            using var fixture = new ServiceFixture(
                EnabledSettings(true, maxSatellites: 1),
                includeSecondDesktop: true);
            fixture.DrainDispatcher();
            var master = fixture.AddWindow("Master");
            var satellite = fixture.AddWindow("Satellite");
            var events = new List<AlgorithmicLayoutEvent>();
            fixture.Service.AlgorithmicLayoutChanged += (_, layoutEvent) =>
                events.Add(layoutEvent);
            fixture.TargetMoveAfterOwnershipChange = _ =>
                throw new InvalidOperationException(
                    "Injected failure after target ownership changed.");

            var overflow = fixture.AddWindow("Overflow");

            var sourceKey = new LayoutStateKey(fixture.Desktop, fixture.Display);
            var targetKey = new LayoutStateKey(
                fixture.TargetDesktop,
                fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var source));
            Assert.AreSame(master, source.Master);
            CollectionAssert.AreEqual(
                new[] { satellite },
                source.Satellites.ToArray());
            Assert.IsTrue(fixture.Coordinator.TryGet(targetKey, out var target));
            Assert.IsNull(target.Master);
            Assert.AreEqual(0, target.Satellites.Count);
            Assert.IsTrue(fixture.Coordinator.FloatingWindows.Contains(overflow));
            Assert.AreSame(fixture.Desktop, fixture.GetWindowDesktop(overflow));
            var transfer = fixture.Coordinator.SnapshotTransfers()
                .Single(item => item.WindowHandle == overflow.Handle);
            Assert.AreEqual(PendingWindowTransferState.Failed, transfer.State);
            Assert.AreEqual("MoveWindowFailed", transfer.TerminalReason);
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            fixture.TargetDesktopMock.Verify(
                item => item.MoveWindow(overflow),
                Times.Once);
            fixture.DesktopMock.Verify(
                item => item.MoveWindow(overflow),
                Times.Once);
            CollectionAssert.AreEqual(
                new[] { false },
                fixture.TargetMoveLockSamples);
            CollectionAssert.AreEqual(
                new[] { false },
                fixture.SourceMoveLockSamples);
            Assert.AreEqual(
                1,
                events.Count(item => item.Kind
                    == AlgorithmicLayoutEventKind.WindowLeftFloating));
            Assert.AreEqual(
                1,
                events.Count(item => item.Kind
                    == AlgorithmicLayoutEventKind.TransferFailed));
        }

        [DataTestMethod]
        [DataRow(false, 1)]
        [DataRow(true, 0)]
        public void UnavailableOrExhaustedDesktopCreationLeavesOverflowFloating(
            bool canManageVirtualDesktops,
            int maxAutoCreatedDesktops)
        {
            using var fixture = new ServiceFixture(
                EnabledSettings(
                    true,
                    maxSatellites: 1,
                    overflowPolicy:
                        MasterSatelliteOverflowPolicy.MoveToExistingOrCreateDesktop,
                    maxAutoCreatedDesktops: maxAutoCreatedDesktops),
                canManageVirtualDesktops: canManageVirtualDesktops);
            fixture.DrainDispatcher();
            fixture.AddWindow("Master");
            fixture.AddWindow("Satellite");

            var overflow = fixture.AddWindow("Overflow");

            Assert.IsTrue(fixture.Coordinator.FloatingWindows.Contains(overflow));
            Assert.AreSame(fixture.Desktop, fixture.GetWindowDesktop(overflow));
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            Assert.IsFalse(fixture.Coordinator.SnapshotTransfers()
                .Any(item => item.WindowHandle == overflow.Handle));
            fixture.VirtualDesktopManagerMock.Verify(
                item => item.CreateDesktop(),
                Times.Never);
        }

        [TestMethod]
        public void DesktopCreationExceptionLeavesOverflowFloatingOnce()
        {
            using var fixture = new ServiceFixture(
                EnabledSettings(
                    true,
                    maxSatellites: 1,
                    overflowPolicy:
                        MasterSatelliteOverflowPolicy.MoveToExistingOrCreateDesktop),
                canManageVirtualDesktops: true);
            fixture.DrainDispatcher();
            fixture.AddWindow("Master");
            fixture.AddWindow("Satellite");
            fixture.VirtualDesktopManagerMock
                .Setup(item => item.CreateDesktop())
                .Throws(new InvalidOperationException(
                    "Injected desktop creation failure."));
            var events = new List<AlgorithmicLayoutEvent>();
            fixture.Service.AlgorithmicLayoutChanged += (_, layoutEvent) =>
                events.Add(layoutEvent);

            var overflow = fixture.AddWindow("Overflow");

            Assert.IsTrue(fixture.Coordinator.FloatingWindows.Contains(overflow));
            Assert.AreSame(fixture.Desktop, fixture.GetWindowDesktop(overflow));
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            fixture.VirtualDesktopManagerMock.Verify(
                item => item.CreateDesktop(),
                Times.Once);
            Assert.AreEqual(
                1,
                events.Count(item => item.Kind
                    == AlgorithmicLayoutEventKind.WindowLeftFloating));
            Assert.IsFalse(events.Any(item =>
                item.MessageKey.Contains("Injected", StringComparison.Ordinal)
                || item.Reason.Contains("Injected", StringComparison.Ordinal)));
        }

        [TestMethod]
        public void CreatedDesktopIsPreparedBeforeOverflowMovesAndCommits()
        {
            using var fixture = new ServiceFixture(
                EnabledSettings(
                    true,
                    maxSatellites: 1,
                    overflowPolicy:
                        MasterSatelliteOverflowPolicy.MoveToExistingOrCreateDesktop),
                canManageVirtualDesktops: true);
            fixture.DrainDispatcher();
            fixture.VirtualDesktopManagerMock
                .Setup(item => item.CreateDesktop())
                .Returns(() =>
                {
                    fixture.AddVirtualDesktop(fixture.TargetDesktop);
                    return fixture.TargetDesktop;
                });
            fixture.AddWindow("Master");
            fixture.AddWindow("Satellite");

            var overflow = fixture.AddWindow("Overflow");

            var targetKey = new LayoutStateKey(
                fixture.TargetDesktop,
                fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(targetKey, out var target));
            Assert.AreSame(overflow, target.Master);
            Assert.AreEqual(0, target.Satellites.Count);
            Assert.AreSame(
                fixture.TargetDesktop,
                fixture.GetWindowDesktop(overflow));
            var transfer = fixture.Coordinator.SnapshotTransfers()
                .Single(item => item.WindowHandle == overflow.Handle);
            Assert.AreEqual(PendingWindowTransferState.Committed, transfer.State);
            Assert.AreSame(fixture.TargetDesktop, transfer.TargetDesktop);
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            fixture.VirtualDesktopManagerMock.Verify(
                item => item.CreateDesktop(),
                Times.Once);
            fixture.TargetDesktopMock.Verify(
                item => item.MoveWindow(overflow),
                Times.Once);
            CollectionAssert.AreEqual(
                new[] { false },
                fixture.TargetMoveLockSamples);
        }

        [TestMethod]
        public void CapacityShrinkWithoutDestinationFloatsTailOnceAndKeepsPrefix()
        {
            using var fixture = new ServiceFixture(EnabledSettings(true));
            fixture.DrainDispatcher();
            var master = fixture.AddWindow("Master");
            var retained = fixture.AddWindow("Retained satellite");
            var firstTail = fixture.AddWindow("First tail");
            var lastTail = fixture.AddWindow("Last tail");
            var events = new List<AlgorithmicLayoutEvent>();
            fixture.Service.AlgorithmicLayoutChanged += (_, layoutEvent) =>
                events.Add(layoutEvent);

            fixture.Publish(EnabledSettings(true, maxSatellites: 1));

            var key = new LayoutStateKey(fixture.Desktop, fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(key, out var state));
            Assert.AreSame(master, state.Master);
            CollectionAssert.AreEqual(
                new[] { retained },
                state.Satellites.ToArray());
            Assert.IsTrue(fixture.Coordinator.FloatingWindows.Contains(firstTail));
            Assert.IsTrue(fixture.Coordinator.FloatingWindows.Contains(lastTail));
            Assert.AreSame(fixture.Desktop, fixture.GetWindowDesktop(firstTail));
            Assert.AreSame(fixture.Desktop, fixture.GetWindowDesktop(lastTail));
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            var floatingEvents = events
                .Where(item => item.Kind
                    == AlgorithmicLayoutEventKind.WindowLeftFloating)
                .ToArray();
            Assert.AreEqual(2, floatingEvents.Length);
            CollectionAssert.AreEquivalent(
                new[] { firstTail.Handle, lastTail.Handle },
                floatingEvents.Select(item => item.WindowHandle).ToArray());

            fixture.Publish(EnabledSettings(true, maxSatellites: 1));

            Assert.AreEqual(
                2,
                events.Count(item => item.Kind
                    == AlgorithmicLayoutEventKind.WindowLeftFloating));
        }

        [TestMethod]
        public void CapacityShrinkAndOrientationChangeCommitTogether()
        {
            using var fixture = new ServiceFixture(EnabledSettings(
                true,
                orientation: SatelliteLayoutOrientation.Vertical));
            fixture.DrainDispatcher();
            var master = fixture.AddWindow("Master");
            var retained = fixture.AddWindow("Retained satellite");
            var firstTail = fixture.AddWindow("First tail");
            var lastTail = fixture.AddWindow("Last tail");
            fixture.SetMinSize(retained, new WinMan.Point(700, 0));
            fixture.SetMinSize(firstTail, new WinMan.Point(700, 0));
            fixture.SetMinSize(lastTail, new WinMan.Point(700, 0));
            var events = new List<AlgorithmicLayoutEvent>();
            fixture.Service.AlgorithmicLayoutChanged += (_, layoutEvent) =>
                events.Add(layoutEvent);

            fixture.Publish(EnabledSettings(
                true,
                maxSatellites: 1,
                orientation: SatelliteLayoutOrientation.Horizontal));

            var key = new LayoutStateKey(fixture.Desktop, fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(key, out var state));
            Assert.AreSame(master, state.Master);
            CollectionAssert.AreEqual(
                new[] { retained },
                state.Satellites.ToArray());
            Assert.AreEqual(
                SatelliteLayoutOrientation.Horizontal,
                state.SatelliteOrientation);
            Assert.IsTrue(fixture.Coordinator.FloatingWindows.Contains(firstTail));
            Assert.IsTrue(fixture.Coordinator.FloatingWindows.Contains(lastTail));
            AssertCapacity(fixture, expectedOccupiedSlots: 2);
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            Assert.IsFalse(events.Any(item => item.Kind
                == AlgorithmicLayoutEventKind.OperationRejected));
        }

        [TestMethod]
        public void DesktopRefreshDuringCapacityShrinkPreservesSourceStateAndOrientation()
        {
            using var fixture = new ServiceFixture(
                EnabledSettings(
                    true,
                    maxSatellites: 3,
                    orientation: SatelliteLayoutOrientation.Vertical),
                includeSecondDesktop: true);
            fixture.DrainDispatcher();
            var master = fixture.AddWindow("Master");
            var retained = fixture.AddWindow("Retained satellite");
            var firstTail = fixture.AddWindow("First tail");
            var lastTail = fixture.AddWindow("Last tail");
            var sourceKey = new LayoutStateKey(fixture.Desktop, fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var before));
            fixture.SetOwnershipProbeSequence(
                lastTail,
                fixture.Desktop,
                null,
                null,
                null,
                null);

            fixture.Publish(EnabledSettings(
                true,
                maxSatellites: 1,
                orientation: SatelliteLayoutOrientation.Horizontal));

            Assert.AreEqual(1, fixture.Coordinator.ActiveTransferCount);
            Assert.AreEqual(1, fixture.Coordinator.ReservationCount);
            fixture.RaiseCurrentDesktopChanged(fixture.Desktop);
            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var during));
            Assert.AreSame(before, during);

            fixture.ReplaceRemainingOwnershipProbeSequence(
                lastTail,
                fixture.TargetDesktop);
            fixture.DrainIncomingOwnershipProbesUntil(() =>
                fixture.Coordinator.ActiveTransferCount == 0
                    && fixture.Coordinator.ReservationCount == 0);

            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var after));
            Assert.AreSame(before, after);
            Assert.AreSame(master, after.Master);
            CollectionAssert.AreEqual(
                new[] { retained },
                after.Satellites.ToArray());
            Assert.AreEqual(
                SatelliteLayoutOrientation.Horizontal,
                after.SatelliteOrientation);
            var targetKey = new LayoutStateKey(
                fixture.TargetDesktop,
                fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(targetKey, out var target));
            CollectionAssert.AreEquivalent(
                new[] { firstTail, lastTail },
                new[] { target.Master! }
                    .Concat(target.Satellites)
                    .ToArray());
        }

        [TestMethod]
        public void ActivationShrinkCompletesOnOriginalDesktopAfterCurrentDesktopChanges()
        {
            using var fixture = new ServiceFixture(
                EnabledSettings(false),
                includeSecondDesktop: true);
            fixture.DrainDispatcher();
            var first = fixture.AddWindow("First");
            var second = fixture.AddWindow("Second");
            var focused = fixture.AddWindow("Focused");
            var sourceKey = new LayoutStateKey(
                fixture.Desktop,
                fixture.Display);
            IWindow? delayedWindow = null;
            fixture.TargetMoveAfterOwnershipChange = movedWindow =>
            {
                Assert.IsNull(delayedWindow);
                delayedWindow = movedWindow;
                fixture.TargetMoveAfterOwnershipChange = null;
                fixture.SetOwnershipProbeSequence(
                    movedWindow,
                    fixture.Desktop,
                    null,
                    null,
                    null,
                    null);
            };
            var events = new List<AlgorithmicLayoutEvent>();
            fixture.Service.AlgorithmicLayoutChanged += (_, layoutEvent) =>
                events.Add(layoutEvent);

            fixture.Publish(EnabledSettings(
                true,
                maxSatellites: 1,
                orientation: SatelliteLayoutOrientation.Horizontal));

            Assert.IsNotNull(delayedWindow);
            Assert.AreEqual(1, fixture.Coordinator.ActiveTransferCount);
            Assert.AreEqual(1, fixture.Coordinator.ReservationCount);
            fixture.RaiseCurrentDesktopChanged(fixture.TargetDesktop);
            Assert.IsFalse(fixture.Coordinator.TryGet(sourceKey, out _));

            fixture.ReplaceRemainingOwnershipProbeSequence(
                delayedWindow!,
                fixture.TargetDesktop);
            fixture.DrainIncomingOwnershipProbesUntil(() =>
                fixture.Coordinator.ActiveTransferCount == 0
                    && fixture.Coordinator.ReservationCount == 0);

            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var source));
            Assert.IsTrue(source.IsActive);
            Assert.AreEqual(
                SatelliteLayoutOrientation.Horizontal,
                source.SatelliteOrientation);
            CollectionAssert.AreEquivalent(
                new[] { first, second, focused }
                    .Where(window => !ReferenceEquals(window, delayedWindow))
                    .ToArray(),
                new[] { source.Master! }
                    .Concat(source.Satellites)
                    .ToArray());
            var targetKey = new LayoutStateKey(
                fixture.TargetDesktop,
                fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(targetKey, out var target));
            CollectionAssert.Contains(
                new[] { target.Master! }
                    .Concat(target.Satellites)
                    .ToArray(),
                delayedWindow!);
            var enabledEvents = events
                .Where(item => item.Kind
                    == AlgorithmicLayoutEventKind.LayoutEnabled)
                .ToArray();
            Assert.AreEqual(1, enabledEvents.Length);
            Assert.AreSame(fixture.Desktop, enabledEvents[0].SourceDesktop);
            Assert.AreSame(fixture.Display, enabledEvents[0].Display);
            Assert.IsFalse(events.Any(item => item.Kind
                == AlgorithmicLayoutEventKind.OperationRejected));
        }

        [TestMethod]
        public void CommandsAndFloatAreBlockedDuringDelayedCapacityShrink()
        {
            using var fixture = new ServiceFixture(
                EnabledSettings(
                    true,
                    maxSatellites: 3,
                    orientation: SatelliteLayoutOrientation.Vertical),
                includeSecondDesktop: true);
            fixture.DrainDispatcher();
            var master = fixture.AddWindow("Master");
            var retained = fixture.AddWindow("Retained satellite");
            var firstTail = fixture.AddWindow("First tail");
            var lastTail = fixture.AddWindow("Last tail");
            var sourceKey = new LayoutStateKey(
                fixture.Desktop,
                fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var before));
            long revision = before.Revision;
            IWindow? delayedWindow = null;
            fixture.TargetMoveAfterOwnershipChange = movedWindow =>
            {
                Assert.IsNull(delayedWindow);
                delayedWindow = movedWindow;
                fixture.TargetMoveAfterOwnershipChange = null;
                fixture.SetOwnershipProbeSequence(
                    movedWindow,
                    fixture.Desktop,
                    null,
                    null,
                    null,
                    null);
            };

            fixture.Publish(EnabledSettings(
                true,
                maxSatellites: 1,
                orientation: SatelliteLayoutOrientation.Horizontal));

            Assert.IsNotNull(delayedWindow);
            Assert.AreEqual(1, fixture.Coordinator.ActiveTransferCount);
            Assert.AreEqual(1, fixture.Coordinator.ReservationCount);
            fixture.Focus(retained);
            Assert.IsFalse(fixture.Service.CanToggleMasterSatelliteLayout());
            Assert.IsFalse(fixture.Service.CanPromoteFocusedWindowToMaster());
            Assert.IsFalse(fixture.Service.CanSwapMasterSide());
            Assert.IsFalse(fixture.Service.CanToggleSatelliteOrientation());
            Assert.IsFalse(fixture.Service.CanResetMasterRatio());
            Assert.IsFalse(fixture.Service.CanRebalanceMasterSatelliteLayout());
            Assert.IsFalse(fixture.Service.CanSplit(vertical: false));
            Assert.IsFalse(fixture.Service.CanFloat());
            Assert.ThrowsException<AlgorithmicLayoutCommandException>(
                fixture.Service.ToggleMasterSatelliteLayout);
            Assert.ThrowsException<AlgorithmicLayoutCommandException>(
                fixture.Service.PromoteFocusedWindowToMaster);
            Assert.ThrowsException<AlgorithmicLayoutCommandException>(
                fixture.Service.SwapMasterSide);
            Assert.ThrowsException<AlgorithmicLayoutCommandException>(
                fixture.Service.ToggleSatelliteOrientation);
            Assert.ThrowsException<AlgorithmicLayoutCommandException>(
                fixture.Service.ResetMasterRatio);
            Assert.ThrowsException<AlgorithmicLayoutCommandException>(
                fixture.Service.RebalanceMasterSatelliteLayout);
            Assert.ThrowsException<AlgorithmicLayoutCommandException>(
                () => fixture.Service.Split(vertical: false));
            Assert.ThrowsException<AlgorithmicLayoutCommandException>(
                fixture.Service.Float);
            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var during));
            Assert.AreSame(before, during);
            Assert.AreEqual(revision, during.Revision);
            Assert.AreSame(master, during.Master);
            CollectionAssert.AreEqual(
                new[] { retained, firstTail, lastTail },
                during.Satellites.ToArray());
            Assert.IsFalse(fixture.Coordinator.FloatingWindows.Contains(retained));
            Assert.AreEqual(1, fixture.Coordinator.ActiveTransferCount);
            Assert.AreEqual(1, fixture.Coordinator.ReservationCount);

            fixture.ReplaceRemainingOwnershipProbeSequence(
                delayedWindow!,
                fixture.TargetDesktop);
            fixture.DrainIncomingOwnershipProbesUntil(() =>
                fixture.Coordinator.ActiveTransferCount == 0
                    && fixture.Coordinator.ReservationCount == 0);

            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var after));
            Assert.AreSame(before, after);
            Assert.AreSame(master, after.Master);
            CollectionAssert.AreEqual(
                new[] { retained },
                after.Satellites.ToArray());
            Assert.AreEqual(
                SatelliteLayoutOrientation.Horizontal,
                after.SatelliteOrientation);
        }

        [TestMethod]
        public void ClosingRetainedSatelliteDuringDelayedCapacityShrinkPreservesSourceState()
        {
            using var fixture = new ServiceFixture(
                EnabledSettings(
                    true,
                    maxSatellites: 3,
                    orientation: SatelliteLayoutOrientation.Vertical),
                includeSecondDesktop: true);
            fixture.DrainDispatcher();
            var master = fixture.AddWindow("Master");
            var retained = fixture.AddWindow("Retained satellite");
            var firstTail = fixture.AddWindow("First tail");
            var lastTail = fixture.AddWindow("Last tail");
            var sourceKey = new LayoutStateKey(
                fixture.Desktop,
                fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var before));
            IWindow? delayedWindow = null;
            fixture.TargetMoveAfterOwnershipChange = movedWindow =>
            {
                Assert.IsNull(delayedWindow);
                delayedWindow = movedWindow;
                fixture.TargetMoveAfterOwnershipChange = null;
                fixture.SetOwnershipProbeSequence(
                    movedWindow,
                    fixture.Desktop,
                    null,
                    null,
                    null,
                    null);
            };

            fixture.Publish(EnabledSettings(
                true,
                maxSatellites: 1,
                orientation: SatelliteLayoutOrientation.Horizontal));

            Assert.IsNotNull(delayedWindow);
            Assert.AreEqual(1, fixture.Coordinator.ActiveTransferCount);
            fixture.SetWindowAlive(retained, false);
            fixture.RaiseWindowDestroyed(retained);
            fixture.RaiseWindowRemoved(retained);

            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var during));
            Assert.AreSame(before, during);
            Assert.AreSame(master, during.Master);
            CollectionAssert.AreEqual(
                new[] { firstTail, lastTail },
                during.Satellites.ToArray());
            Assert.AreEqual(1, fixture.Coordinator.ActiveTransferCount);
            Assert.AreEqual(1, fixture.Coordinator.ReservationCount);

            fixture.ReplaceRemainingOwnershipProbeSequence(
                delayedWindow!,
                fixture.TargetDesktop);
            fixture.DrainIncomingOwnershipProbesUntil(() =>
                fixture.Coordinator.ActiveTransferCount == 0
                    && fixture.Coordinator.ReservationCount == 0);

            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var after));
            Assert.AreSame(before, after);
            Assert.IsTrue(after.IsActive);
            Assert.AreSame(master, after.Master);
            Assert.AreEqual(0, after.Satellites.Count);
            Assert.AreEqual(
                SatelliteLayoutOrientation.Horizontal,
                after.SatelliteOrientation);
            Assert.IsTrue(fixture.Coordinator.TryGetCapacity(
                sourceKey,
                out var capacity));
            Assert.AreEqual(WorkspaceLayoutKind.Canonical, capacity.LayoutKind);
            Assert.AreEqual(1, capacity.OccupiedSlots);
            Assert.AreEqual(0, capacity.ReservedSlots);
            Assert.IsFalse(ReferenceEquals(after.Master, retained));
            Assert.IsFalse(after.Satellites.Any(window =>
                ReferenceEquals(window, retained)));
        }

        [TestMethod]
        public void RejectedCurrentOrientationUpdatesEmptyBackgroundDefault()
        {
            using var fixture = new ServiceFixture(
                EnabledSettings(
                    true,
                    orientation: SatelliteLayoutOrientation.Vertical),
                includeSecondDesktop: true);
            fixture.DrainDispatcher();
            fixture.AddWindow("Master");
            var satellite1 = fixture.AddWindow("Satellite 1");
            var satellite2 = fixture.AddWindow("Satellite 2");
            var satellite3 = fixture.AddWindow("Satellite 3");
            fixture.SetMinSize(satellite1, new WinMan.Point(700, 0));
            fixture.SetMinSize(satellite2, new WinMan.Point(700, 0));
            fixture.SetMinSize(satellite3, new WinMan.Point(700, 0));
            var sourceKey = new LayoutStateKey(
                fixture.Desktop,
                fixture.Display);
            var backgroundKey = new LayoutStateKey(
                fixture.TargetDesktop,
                fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var sourceBefore));
            Assert.IsTrue(fixture.Coordinator.TryGet(
                backgroundKey,
                out var backgroundBefore));
            Assert.IsNull(backgroundBefore.Master);
            Assert.AreEqual(0, backgroundBefore.Satellites.Count);
            Assert.AreEqual(
                SatelliteLayoutOrientation.Vertical,
                backgroundBefore.SatelliteOrientation);
            long sourceRevision = sourceBefore.Revision;
            var events = new List<AlgorithmicLayoutEvent>();
            fixture.Service.AlgorithmicLayoutChanged += (_, layoutEvent) =>
                events.Add(layoutEvent);

            fixture.Publish(EnabledSettings(
                true,
                orientation: SatelliteLayoutOrientation.Horizontal));

            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var sourceAfter));
            Assert.AreSame(sourceBefore, sourceAfter);
            Assert.AreEqual(
                SatelliteLayoutOrientation.Vertical,
                sourceAfter.SatelliteOrientation);
            Assert.AreEqual(sourceRevision, sourceAfter.Revision);
            Assert.IsTrue(fixture.Coordinator.TryGet(
                backgroundKey,
                out var backgroundAfter));
            Assert.AreSame(backgroundBefore, backgroundAfter);
            Assert.IsNull(backgroundAfter.Master);
            Assert.AreEqual(0, backgroundAfter.Satellites.Count);
            Assert.AreEqual(
                SatelliteLayoutOrientation.Horizontal,
                backgroundAfter.SatelliteOrientation);
            var targetMaster = fixture.AddWindow(
                "Target master",
                fixture.TargetDesktop);
            var targetSatellite = fixture.AddWindow(
                "Target satellite",
                fixture.TargetDesktop);
            Assert.IsTrue(fixture.Coordinator.TryGet(
                backgroundKey,
                out var populatedBackground));
            Assert.AreSame(backgroundBefore, populatedBackground);
            Assert.AreSame(targetMaster, populatedBackground.Master);
            CollectionAssert.AreEqual(
                new[] { targetSatellite },
                populatedBackground.Satellites.ToArray());
            Assert.AreEqual(
                SatelliteLayoutOrientation.Horizontal,
                populatedBackground.SatelliteOrientation);
            var rejected = events
                .Where(item => item.Kind
                    == AlgorithmicLayoutEventKind.OperationRejected)
                .ToArray();
            Assert.AreEqual(1, rejected.Length);
            Assert.AreSame(fixture.Desktop, rejected[0].SourceDesktop);
            Assert.AreSame(fixture.Display, rejected[0].Display);
        }

        [TestMethod]
        public void RejectedOrientationSettingRaisesOperationRejected()
        {
            using var fixture = new ServiceFixture(EnabledSettings(
                true,
                orientation: SatelliteLayoutOrientation.Vertical));
            fixture.DrainDispatcher();
            fixture.AddWindow("Master");
            var satellite1 = fixture.AddWindow("Satellite 1");
            var satellite2 = fixture.AddWindow("Satellite 2");
            var satellite3 = fixture.AddWindow("Satellite 3");
            fixture.SetMinSize(satellite1, new WinMan.Point(700, 0));
            fixture.SetMinSize(satellite2, new WinMan.Point(700, 0));
            fixture.SetMinSize(satellite3, new WinMan.Point(700, 0));
            var key = new LayoutStateKey(fixture.Desktop, fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(key, out var before));
            long revision = before.Revision;
            var events = new List<AlgorithmicLayoutEvent>();
            fixture.Service.AlgorithmicLayoutChanged += (_, layoutEvent) =>
                events.Add(layoutEvent);

            fixture.Publish(EnabledSettings(
                true,
                orientation: SatelliteLayoutOrientation.Horizontal));

            Assert.IsTrue(fixture.Coordinator.TryGet(key, out var after));
            Assert.AreSame(before, after);
            Assert.AreEqual(
                SatelliteLayoutOrientation.Vertical,
                after.SatelliteOrientation);
            Assert.AreEqual(revision, after.Revision);
            var rejected = events.Single(item => item.Kind
                == AlgorithmicLayoutEventKind.OperationRejected);
            Assert.AreSame(fixture.Desktop, rejected.SourceDesktop);
            Assert.AreSame(fixture.Display, rejected.Display);
            Assert.AreEqual(
                "AlgorithmicLayout.OrientationRejected",
                rejected.MessageKey);
        }

        [TestMethod]
        public void CapacityShrinkAndRejectedOrientationRaisesOneOperationRejected()
        {
            using var fixture = new ServiceFixture(EnabledSettings(
                true,
                orientation: SatelliteLayoutOrientation.Vertical));
            fixture.DrainDispatcher();
            fixture.AddWindow("Master");
            var retained1 = fixture.AddWindow("Retained satellite 1");
            var retained2 = fixture.AddWindow("Retained satellite 2");
            var tail = fixture.AddWindow("Tail");
            fixture.SetMinSize(retained1, new WinMan.Point(700, 0));
            fixture.SetMinSize(retained2, new WinMan.Point(700, 0));
            fixture.SetMinSize(tail, new WinMan.Point(700, 0));
            var events = new List<AlgorithmicLayoutEvent>();
            fixture.Service.AlgorithmicLayoutChanged += (_, layoutEvent) =>
                events.Add(layoutEvent);

            fixture.Publish(EnabledSettings(
                true,
                maxSatellites: 2,
                orientation: SatelliteLayoutOrientation.Horizontal));

            var key = new LayoutStateKey(fixture.Desktop, fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(key, out var state));
            CollectionAssert.AreEqual(
                new[] { retained1, retained2 },
                state.Satellites.ToArray());
            Assert.AreEqual(
                SatelliteLayoutOrientation.Vertical,
                state.SatelliteOrientation);
            Assert.IsTrue(fixture.Coordinator.FloatingWindows.Contains(tail));
            Assert.AreEqual(
                1,
                events.Count(item => item.Kind
                    == AlgorithmicLayoutEventKind.OperationRejected));
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
        }

        [TestMethod]
        public void MouseDropFloatingWindowIntoFreeSlotTilesIt()
            => RunOnSta(MouseDropFloatingWindowIntoFreeSlotTilesItCore);

        private static void MouseDropFloatingWindowIntoFreeSlotTilesItCore()
        {
            using var fixture = new ServiceFixture(
                EnabledSettings(true, maxSatellites: 2));
            fixture.Service.Start();
            fixture.DrainDispatcher();
            var master = fixture.AddWindow("Master");
            var floating = fixture.AddWindow("Floating");
            fixture.Focus(floating);
            fixture.Service.Float();
            fixture.DrainDispatcher();

            var key = new LayoutStateKey(fixture.Desktop, fixture.Display);
            Assert.IsTrue(fixture.Coordinator.FloatingWindows.Contains(floating));
            Assert.IsTrue(fixture.Coordinator.TryGet(key, out var before));
            Assert.AreSame(master, before.Master);
            Assert.AreEqual(0, before.Satellites.Count);

            fixture.SetCursor(fixture.Overlay.GetWindowRectangle(master).Center);
            fixture.RaisePositionChangeStart(floating);
            fixture.RaisePositionChanged(
                floating,
                Rectangle.OffsetAndSize(200, 100, 800, 600));
            fixture.RaisePositionChangeEnd(floating);

            Assert.IsFalse(fixture.Coordinator.FloatingWindows.Contains(floating));
            Assert.IsTrue(fixture.Coordinator.TryGet(key, out var after));
            Assert.AreSame(master, after.Master);
            CollectionAssert.AreEqual(
                new[] { floating },
                after.Satellites.ToArray());
            AssertCapacity(fixture, expectedOccupiedSlots: 2);
            Assert.IsNull(fixture.Overlay.PreviewRectangle);
            Assert.AreEqual(0, fixture.Overlay.PreviewWindows.Count);
        }

        [TestMethod]
        public void MouseDropEndFromWinManWorkerUsesTilingDispatcher()
            => RunOnSta(MouseDropEndFromWinManWorkerUsesTilingDispatcherCore);

        private static void MouseDropEndFromWinManWorkerUsesTilingDispatcherCore()
        {
            using var fixture = new ServiceFixture(
                EnabledSettings(true, maxSatellites: 2));
            fixture.Service.Start();
            fixture.DrainDispatcher();
            var master = fixture.AddWindow("Master");
            var floating = fixture.AddWindow("Floating");
            fixture.Focus(floating);
            fixture.Service.Float();
            fixture.DrainDispatcher();

            var key = new LayoutStateKey(fixture.Desktop, fixture.Display);
            Assert.IsTrue(fixture.Coordinator.FloatingWindows.Contains(floating));
            fixture.SetCursor(fixture.Overlay.GetWindowRectangle(master).Center);
            fixture.RaisePositionChangeStart(floating);
            fixture.RaisePositionChanged(
                floating,
                Rectangle.OffsetAndSize(200, 100, 800, 600));

            fixture.RaisePositionChangeEndFromWorker(floating);

            Assert.IsFalse(fixture.Coordinator.FloatingWindows.Contains(floating));
            Assert.IsTrue(fixture.Coordinator.TryGet(key, out var state));
            Assert.AreSame(master, state.Master);
            CollectionAssert.AreEqual(
                new[] { floating },
                state.Satellites.ToArray());
            AssertCapacity(fixture, expectedOccupiedSlots: 2);
            Assert.IsNull(fixture.Overlay.PreviewRectangle);
            Assert.AreEqual(0, fixture.Overlay.PreviewWindows.Count);
        }

        [TestMethod]
        public void MouseDropFloatingWindowIntoFullLayoutKeepsItFloatingByPolicy()
            => RunOnSta(
                MouseDropFloatingWindowIntoFullLayoutKeepsItFloatingByPolicyCore);

        private static void
            MouseDropFloatingWindowIntoFullLayoutKeepsItFloatingByPolicyCore()
        {
            using var fixture = new ServiceFixture(EnabledSettings(
                true,
                maxSatellites: 1,
                overflowPolicy: MasterSatelliteOverflowPolicy.FloatOnCurrentDesktop));
            fixture.Service.Start();
            fixture.DrainDispatcher();
            var master = fixture.AddWindow("Master");
            var floating = fixture.AddWindow("Floating candidate");
            fixture.Focus(floating);
            fixture.Service.Float();
            fixture.DrainDispatcher();
            var satellite = fixture.AddWindow("Satellite");
            var key = new LayoutStateKey(fixture.Desktop, fixture.Display);
            Assert.IsTrue(fixture.Coordinator.FloatingWindows.Contains(floating));
            var mouseEvents = new List<AlgorithmicLayoutEvent>();
            fixture.Service.AlgorithmicLayoutChanged += (_, layoutEvent) =>
                mouseEvents.Add(layoutEvent);

            fixture.SetCursor(new WinMan.Point(500, 700));
            fixture.RaisePositionChangeStart(floating);
            fixture.RaisePositionChanged(
                floating,
                Rectangle.OffsetAndSize(250, 100, 800, 600));
            fixture.RaisePositionChangeEnd(floating);

            Assert.IsTrue(fixture.Coordinator.FloatingWindows.Contains(floating));
            Assert.AreSame(fixture.Desktop, fixture.GetWindowDesktop(floating));
            Assert.IsTrue(fixture.Coordinator.TryGet(key, out var after));
            Assert.AreSame(master, after.Master);
            CollectionAssert.AreEqual(
                new[] { satellite },
                after.Satellites.ToArray());
            AssertCapacity(fixture, expectedOccupiedSlots: 2);
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            var floatingEvent = mouseEvents.Single(item =>
                item.Kind == AlgorithmicLayoutEventKind.WindowLeftFloating);
            Assert.AreEqual(floating.Handle, floatingEvent.WindowHandle);
            Assert.AreSame(fixture.Desktop, floatingEvent.SourceDesktop);
            Assert.AreSame(fixture.Desktop, floatingEvent.TargetDesktop);
            Assert.AreEqual("NoOverflowDestination", floatingEvent.Reason);
            Assert.IsNull(fixture.Overlay.PreviewRectangle);
            Assert.AreEqual(0, fixture.Overlay.PreviewWindows.Count);
        }

        [TestMethod]
        public void MouseDropFloatingWindowIntoFullLayoutUsesExistingDesktopPolicy()
            => RunOnSta(
                MouseDropFloatingWindowIntoFullLayoutUsesExistingDesktopPolicyCore);

        private static void
            MouseDropFloatingWindowIntoFullLayoutUsesExistingDesktopPolicyCore()
        {
            using var fixture = new ServiceFixture(
                EnabledSettings(
                    true,
                    maxSatellites: 1,
                    overflowPolicy:
                        MasterSatelliteOverflowPolicy.MoveToExistingDesktop),
                includeSecondDesktop: true);
            fixture.Service.Start();
            fixture.DrainDispatcher();
            var master = fixture.AddWindow("Master");
            var floating = fixture.AddWindow("Floating candidate");
            fixture.Focus(floating);
            fixture.Service.Float();
            fixture.DrainDispatcher();
            var satellite = fixture.AddWindow("Satellite");
            Assert.IsTrue(fixture.Coordinator.FloatingWindows.Contains(floating));
            var expectedTargetKey = new LayoutStateKey(
                fixture.TargetDesktop,
                fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGetCapacity(
                expectedTargetKey,
                out var availableTarget));
            Assert.AreEqual(0, availableTarget.OccupiedSlots);
            Assert.AreEqual(0, availableTarget.ReservedSlots);
            Assert.AreEqual(2, availableTarget.TotalCapacity);
            var mouseEvents = new List<AlgorithmicLayoutEvent>();
            fixture.Service.AlgorithmicLayoutChanged += (_, layoutEvent) =>
                mouseEvents.Add(layoutEvent);
            fixture.TargetMoveAfterOwnershipChange = window =>
            {
                fixture.RaiseWindowRemoved(window);
                fixture.RaiseWindowAdded(window);
            };

            fixture.SetCursor(new WinMan.Point(500, 700));
            fixture.RaisePositionChangeStart(floating);
            fixture.RaisePositionChanged(
                floating,
                Rectangle.OffsetAndSize(300, 100, 800, 600));
            fixture.RaisePositionChangeEnd(floating);

            var sourceKey = new LayoutStateKey(
                fixture.Desktop,
                fixture.Display);
            var targetKey = new LayoutStateKey(
                fixture.TargetDesktop,
                fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var source));
            Assert.AreSame(master, source.Master);
            CollectionAssert.AreEqual(
                new[] { satellite },
                source.Satellites.ToArray());
            Assert.IsTrue(fixture.Coordinator.TryGet(targetKey, out var target));
            Assert.AreSame(floating, target.Master);
            Assert.AreEqual(0, target.Satellites.Count);
            Assert.IsFalse(fixture.Coordinator.FloatingWindows.Contains(floating));
            Assert.AreSame(fixture.TargetDesktop, fixture.GetWindowDesktop(floating));
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            var movedEvent = mouseEvents.Single(item =>
                item.Kind == AlgorithmicLayoutEventKind.WindowMovedToDesktop);
            Assert.AreEqual(floating.Handle, movedEvent.WindowHandle);
            Assert.AreSame(fixture.Desktop, movedEvent.SourceDesktop);
            Assert.AreSame(fixture.TargetDesktop, movedEvent.TargetDesktop);
            Assert.AreNotEqual(Guid.Empty, movedEvent.CorrelationId);
            CollectionAssert.DoesNotContain(
                fixture.TargetMoveLockSamples,
                true);
            Assert.IsNull(fixture.Overlay.PreviewRectangle);
            Assert.AreEqual(0, fixture.Overlay.PreviewWindows.Count);
        }

        [TestMethod]
        public void MouseDropEndRejectionAndCancellationClearOverlayPreview()
            => RunOnSta(MouseDropEndRejectionAndCancellationClearOverlayPreviewCore);

        private static void
            MouseDropEndRejectionAndCancellationClearOverlayPreviewCore()
        {
            using var fixture = new ServiceFixture(
                EnabledSettings(true, maxSatellites: 2));
            fixture.Service.Start();
            fixture.DrainDispatcher();
            var master = fixture.AddWindow("Master");
            var satellite = fixture.AddWindow("Satellite");
            var key = new LayoutStateKey(fixture.Desktop, fixture.Display);
            var dropEvents = new List<AlgorithmicLayoutEvent>();
            fixture.Service.AlgorithmicLayoutChanged += (_, layoutEvent) =>
                dropEvents.Add(layoutEvent);

            // Accepted preview is observable, but does not mutate the live tree.
            fixture.SetCursor(fixture.Overlay.GetWindowRectangle(master).Center);
            fixture.RaisePositionChangeStart(satellite);
            var satellitePosition = satellite.Position;
            fixture.RaisePositionChanged(
                satellite,
                Rectangle.OffsetAndSize(
                    200,
                    100,
                    satellitePosition.Width,
                    satellitePosition.Height));

            var acceptedPreviewRectangle = fixture.Overlay.PreviewRectangle;
            var acceptedPreviewWindows = fixture.Overlay.PreviewWindows.ToArray();
            Assert.IsTrue(fixture.Coordinator.TryGet(key, out var duringPreview));
            Assert.AreSame(master, duringPreview.Master);
            fixture.RaisePositionChangeEnd(satellite);

            Assert.IsNotNull(acceptedPreviewRectangle);
            CollectionAssert.AreEquivalent(
                new[] { master, satellite },
                acceptedPreviewWindows);
            Assert.IsNull(fixture.Overlay.PreviewRectangle);
            Assert.AreEqual(0, fixture.Overlay.PreviewWindows.Count);
            Assert.IsTrue(fixture.Coordinator.TryGet(key, out var afterAccepted));
            Assert.AreSame(satellite, afterAccepted.Master);
            CollectionAssert.AreEqual(
                new[] { master },
                afterAccepted.Satellites.ToArray());

            // A rejected outside-layout drop replaces any stale renderer state
            // with the rejected plan's empty preview and leaves the tree intact.
            fixture.Overlay.PreviewRectangle =
                Rectangle.OffsetAndSize(1, 1, 10, 10);
            fixture.Overlay.PreviewWindows = new HashSet<IWindow> { master };
            fixture.SetCursor(new WinMan.Point(4000, 1800));
            fixture.RaisePositionChangeStart(master);
            var masterPosition = master.Position;
            fixture.RaisePositionChanged(
                master,
                Rectangle.OffsetAndSize(
                    250,
                    100,
                    masterPosition.Width,
                    masterPosition.Height));

            Assert.IsNull(fixture.Overlay.PreviewRectangle);
            Assert.AreEqual(0, fixture.Overlay.PreviewWindows.Count);
            fixture.RaisePositionChangeEnd(master);
            Assert.IsTrue(fixture.Coordinator.TryGet(key, out var afterRejected));
            Assert.AreSame(satellite, afterRejected.Master);
            CollectionAssert.AreEqual(
                new[] { master },
                afterRejected.Satellites.ToArray());
            Assert.IsNull(fixture.Overlay.PreviewRectangle);
            Assert.AreEqual(0, fixture.Overlay.PreviewWindows.Count);
            var rejectedEvent = dropEvents.Single(item =>
                item.Kind == AlgorithmicLayoutEventKind.OperationRejected);
            Assert.AreEqual("UnsupportedOperation", rejectedEvent.Reason);

            // An OS drag cancellation is represented by Start followed by End
            // without an intervening position change. End must invalidate the
            // renderer even when no drop command is applied.
            fixture.Overlay.PreviewRectangle =
                Rectangle.OffsetAndSize(2, 2, 10, 10);
            fixture.Overlay.PreviewWindows = new HashSet<IWindow> { master };
            int eventCountBeforeCancellation = dropEvents.Count;
            fixture.RaisePositionChangeStart(master);
            fixture.RaisePositionChangeEnd(master);

            Assert.IsNull(fixture.Overlay.PreviewRectangle);
            Assert.AreEqual(0, fixture.Overlay.PreviewWindows.Count);
            Assert.IsTrue(fixture.Coordinator.TryGet(key, out var afterCancelled));
            Assert.AreSame(satellite, afterCancelled.Master);
            CollectionAssert.AreEqual(
                new[] { master },
                afterCancelled.Satellites.ToArray());
            Assert.AreEqual(eventCountBeforeCancellation, dropEvents.Count);
        }

        private static void RunOnSta(Action action)
        {
            Exception? failure = null;
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(
                thread.Join(TimeSpan.FromSeconds(15)),
                "The STA mouse-integration test did not complete in 15 seconds.");
            if (failure != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(failure)
                    .Throw();
            }
        }

        [TestMethod]
        public void DesktopAddedRegistersStateInEveryRealDisplayService()
        {
            using var fixture = new TwoDisplayServiceFixture();
            fixture.DrainDispatcher();
            Assert.AreEqual(2, fixture.Coordinator.RegisteredDisplayCount);

            fixture.RaiseDesktopAdded();

            var primaryKey = new LayoutStateKey(
                fixture.CreatedDesktop,
                fixture.PrimaryDisplay);
            var secondaryKey = new LayoutStateKey(
                fixture.CreatedDesktop,
                fixture.SecondaryDisplay);
            Assert.IsTrue(fixture.Coordinator.TryGet(primaryKey, out var primary));
            Assert.IsTrue(primary.IsActive);
            Assert.IsTrue(fixture.Coordinator.TryGet(secondaryKey, out var secondary));
            Assert.IsTrue(secondary.IsActive);
            Assert.IsTrue(fixture.Coordinator.TryGetCapacity(
                primaryKey,
                out var primaryCapacity));
            Assert.IsTrue(fixture.Coordinator.TryGetCapacity(
                secondaryKey,
                out var secondaryCapacity));
            Assert.AreEqual(0, primaryCapacity.OccupiedSlots);
            Assert.AreEqual(0, secondaryCapacity.OccupiedSlots);

            fixture.SecondaryService.Dispose();

            Assert.AreEqual(1, fixture.Coordinator.RegisteredDisplayCount);
            Assert.IsTrue(fixture.Coordinator.TryGet(primaryKey, out _));
            Assert.IsFalse(fixture.Coordinator.TryGet(secondaryKey, out _));
        }

        private static void AssertRestoredOverflowEndpoints(
            ServiceFixture fixture,
            IWindow expectedMaster,
            IWindow expectedSatellite,
            IWindow overflow,
            long expectedSourceRevision,
            long expectedTargetRevision)
        {
            var sourceKey = new LayoutStateKey(fixture.Desktop, fixture.Display);
            var targetKey = new LayoutStateKey(
                fixture.TargetDesktop,
                fixture.Display);
            Assert.IsTrue(fixture.Coordinator.TryGet(sourceKey, out var source));
            Assert.AreSame(expectedMaster, source.Master);
            CollectionAssert.AreEqual(
                new[] { expectedSatellite },
                source.Satellites.ToArray());
            Assert.AreEqual(expectedSourceRevision, source.Revision);
            Assert.IsTrue(fixture.Coordinator.TryGet(targetKey, out var target));
            Assert.IsNull(target.Master);
            Assert.AreEqual(0, target.Satellites.Count);
            Assert.AreEqual(expectedTargetRevision, target.Revision);
            Assert.IsTrue(fixture.Coordinator.TryGetCapacity(
                sourceKey,
                out var sourceCapacity));
            Assert.AreEqual(WorkspaceLayoutKind.Canonical, sourceCapacity.LayoutKind);
            Assert.AreEqual(2, sourceCapacity.OccupiedSlots);
            Assert.AreEqual(0, sourceCapacity.ReservedSlots);
            Assert.IsTrue(fixture.Coordinator.TryGetCapacity(
                targetKey,
                out var targetCapacity));
            Assert.AreEqual(
                WorkspaceLayoutKind.Canonical,
                targetCapacity.LayoutKind);
            Assert.AreEqual(0, targetCapacity.OccupiedSlots);
            Assert.AreEqual(0, targetCapacity.ReservedSlots);
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            Assert.IsTrue(fixture.Coordinator.FloatingWindows.Contains(overflow));
            Assert.AreSame(fixture.Desktop, fixture.GetWindowDesktop(overflow));
        }

        private static Settings EnabledSettings(
            bool enabled,
            int maxSatellites = MasterSatelliteLayoutSettings.DefaultMaxSatellites,
            MasterSatelliteOverflowPolicy overflowPolicy =
                MasterSatelliteOverflowPolicy.MoveToExistingDesktop,
            int maxAutoCreatedDesktops =
                MasterSatelliteLayoutSettings.DefaultMaxAutoCreatedDesktops,
            SatelliteLayoutOrientation orientation =
                SatelliteLayoutOrientation.Vertical,
            MasterSide masterSide = MasterSide.Left)
        {
            return new Settings
            {
                MasterSatelliteLayout = new MasterSatelliteLayoutSettings
                {
                    Enabled = enabled,
                    DisplayScope = AlgorithmicLayoutDisplayScope.AllDisplays,
                    MaxSatellites = maxSatellites,
                    OverflowPolicy = overflowPolicy,
                    MaxAutoCreatedDesktops = maxAutoCreatedDesktops,
                    DefaultSatelliteOrientation = orientation,
                    DefaultMasterSide = masterSide,
                },
            };
        }

        private static void AssertEvent(
            AlgorithmicLayoutEvent actual,
            AlgorithmicLayoutEventKind expectedKind,
            IDisplay expectedDisplay,
            IVirtualDesktop expectedDesktop,
            string expectedReason,
            string expectedMessageKey)
        {
            Assert.AreEqual(expectedKind, actual.Kind);
            Assert.AreSame(expectedDisplay, actual.Display);
            Assert.AreSame(expectedDesktop, actual.SourceDesktop);
            Assert.AreEqual(expectedReason, actual.Reason);
            Assert.AreEqual(expectedMessageKey, actual.MessageKey);
            Assert.IsNull(actual.TargetDesktop);
            Assert.AreEqual(Guid.Empty, actual.CorrelationId);
        }

        private static void AssertCapacity(
            ServiceFixture fixture,
            int expectedOccupiedSlots)
        {
            Assert.IsTrue(fixture.Coordinator.TryGetCapacity(
                new LayoutStateKey(fixture.Desktop, fixture.Display),
                out var capacity));
            Assert.AreEqual(expectedOccupiedSlots, capacity.OccupiedSlots);
            Assert.AreEqual(0, capacity.ReservedSlots);
        }

        private sealed class ServiceFixture : IDisposable
        {
            private readonly BehaviorSubject<ITilingServiceSettings> m_settings;
            private readonly Mock<IWorkspace> m_workspaceMock;
            private readonly Dictionary<IWindow, IVirtualDesktop> m_windowDesktops
                = new(ReferenceEqualityComparer.Instance);
            private readonly Dictionary<IWindow, Mock<IWindow>> m_windowMocks
                = new(ReferenceEqualityComparer.Instance);
            private readonly Dictionary<IWindow, bool> m_windowCanResize
                = new(ReferenceEqualityComparer.Instance);
            private readonly Dictionary<IWindow, bool> m_windowIsAlive
                = new(ReferenceEqualityComparer.Instance);
            private readonly Dictionary<IWindow, WinMan.Point> m_windowMinSizes
                = new(ReferenceEqualityComparer.Instance);
            private readonly Dictionary<IWindow, Queue<IVirtualDesktop?>>
                m_windowOwnershipProbeScripts
                    = new(ReferenceEqualityComparer.Instance);
            private readonly Dictionary<IWindow, int> m_windowOwnershipProbeAttempts
                = new(ReferenceEqualityComparer.Instance);
            private readonly List<IWindow> m_workspaceWindows = new();
            private readonly HashSet<IWindow> m_pinnedWindows = new();
            private readonly List<IVirtualDesktop> m_virtualDesktops = new();
            private IWindow? m_focusedWindow;
            private IVirtualDesktop m_currentDesktop = null!;
            private WinMan.Point m_cursorLocation = new(0, 0);
            private int m_nextWindowHandle = 100;
            private bool m_disposed;

            public IVirtualDesktop Desktop { get; }
            public Mock<IVirtualDesktop> DesktopMock { get; }
            public IVirtualDesktop TargetDesktop { get; }
            public Mock<IVirtualDesktop> TargetDesktopMock { get; }
            public IVirtualDesktop ThirdDesktop { get; }
            public Mock<IVirtualDesktop> ThirdDesktopMock { get; }
            public Mock<IVirtualDesktopManager> VirtualDesktopManagerMock { get; }
            public IDisplay Display { get; }
            public AlgorithmicLayoutCoordinator Coordinator { get; }
            public AlgorithmicWindowTransferEventTracker EventTracker { get; }
            public FakeTilingOverlayRenderer Overlay { get; } = new();
            public TilingService Service { get; }
            public Action<LayoutStateKey, MasterSatelliteCapacitySnapshot, long>?
                CapacityPublicationHook { get; set; }
            public Action<IWindow>? SourceMoveAfterOwnershipChange { get; set; }
            public Action<IWindow>? TargetMoveAfterOwnershipChange { get; set; }
            public int SourceOwnershipProbeFailuresRemaining { get; set; }
            public List<bool> SourceMoveLockSamples { get; } = new();
            public List<bool> TargetMoveLockSamples { get; } = new();
            public List<bool> ThirdMoveLockSamples { get; } = new();

            public ServiceFixture(
                ITilingServiceSettings initialSettings,
                bool includeSecondDesktop = false,
                bool canManageVirtualDesktops = true,
                bool includeThirdDesktop = false,
                ILogger? logger = null,
                IAnimationThread? animationThread = null)
            {
                m_workspaceMock = new Mock<IWorkspace>(MockBehavior.Loose);
                VirtualDesktopManagerMock = new Mock<IVirtualDesktopManager>(
                    MockBehavior.Loose);
                var displayManagerMock = new Mock<IDisplayManager>(MockBehavior.Loose);
                DesktopMock = new Mock<IVirtualDesktop>(MockBehavior.Loose);
                TargetDesktopMock = new Mock<IVirtualDesktop>(MockBehavior.Loose);
                ThirdDesktopMock = new Mock<IVirtualDesktop>(MockBehavior.Loose);
                var displayMock = new Mock<IDisplay>(MockBehavior.Loose);

                Desktop = DesktopMock.Object;
                TargetDesktop = TargetDesktopMock.Object;
                ThirdDesktop = ThirdDesktopMock.Object;
                Display = displayMock.Object;
                m_currentDesktop = Desktop;
                m_virtualDesktops.Add(Desktop);
                if (includeSecondDesktop || includeThirdDesktop)
                {
                    m_virtualDesktops.Add(TargetDesktop);
                }
                if (includeThirdDesktop)
                {
                    m_virtualDesktops.Add(ThirdDesktop);
                }

                ConfigureDesktop(DesktopMock, Desktop, 0, "Desktop 1", true);
                ConfigureDesktop(
                    TargetDesktopMock,
                    TargetDesktop,
                    1,
                    "Desktop 2",
                    false);
                ConfigureDesktop(
                    ThirdDesktopMock,
                    ThirdDesktop,
                    2,
                    "Desktop 3",
                    false);
                DesktopMock.Setup(item => item.MoveWindow(It.IsAny<IWindow>()))
                    .Callback((IWindow window) =>
                    {
                        SourceMoveLockSamples.Add(
                            Service!.IsAlgorithmicTransferLockHeldByCurrentThread);
                        SetWindowDesktop(window, Desktop);
                        SourceMoveAfterOwnershipChange?.Invoke(window);
                    });
                TargetDesktopMock
                    .Setup(item => item.MoveWindow(It.IsAny<IWindow>()))
                    .Callback((IWindow window) =>
                    {
                        TargetMoveLockSamples.Add(
                            Service!.IsAlgorithmicTransferLockHeldByCurrentThread);
                        SetWindowDesktop(window, TargetDesktop);
                        TargetMoveAfterOwnershipChange?.Invoke(window);
                    });
                ThirdDesktopMock
                    .Setup(item => item.MoveWindow(It.IsAny<IWindow>()))
                    .Callback((IWindow window) =>
                    {
                        ThirdMoveLockSamples.Add(
                            Service!.IsAlgorithmicTransferLockHeldByCurrentThread);
                        SetWindowDesktop(window, ThirdDesktop);
                    });

                var workArea = Rectangle.OffsetAndSize(0, 0, 3440, 1400);
                displayMock.SetupGet(item => item.Workspace)
                    .Returns(m_workspaceMock.Object);
                displayMock.SetupGet(item => item.WorkArea).Returns(workArea);
                displayMock.SetupGet(item => item.Bounds).Returns(workArea);
                displayMock.SetupGet(item => item.Scaling).Returns(1.0);
                displayMock.SetupGet(item => item.RefreshRate).Returns(60);
                displayMock.Setup(item => item.Equals(It.IsAny<IDisplay>()))
                    .Returns((IDisplay other) => ReferenceEquals(Display, other));

                VirtualDesktopManagerMock.SetupGet(item => item.Workspace)
                    .Returns(m_workspaceMock.Object);
                VirtualDesktopManagerMock
                    .SetupGet(item => item.CanManageVirtualDesktops)
                    .Returns(canManageVirtualDesktops);
                VirtualDesktopManagerMock.SetupGet(item => item.Desktops)
                    .Returns(() => m_virtualDesktops.ToArray());
                VirtualDesktopManagerMock.SetupGet(item => item.CurrentDesktop)
                    .Returns(() => m_currentDesktop);
                VirtualDesktopManagerMock
                    .Setup(item => item.IsWindowPinned(It.IsAny<IWindow>()))
                    .Returns((IWindow window) => m_pinnedWindows.Contains(window));

                displayManagerMock.SetupGet(item => item.Workspace)
                    .Returns(m_workspaceMock.Object);
                displayManagerMock.SetupGet(item => item.PrimaryDisplay)
                    .Returns(Display);
                displayManagerMock.SetupGet(item => item.Displays)
                    .Returns(new[] { Display });

                m_workspaceMock.SetupGet(item => item.VirtualDesktopManager)
                    .Returns(VirtualDesktopManagerMock.Object);
                m_workspaceMock.SetupGet(item => item.DisplayManager)
                    .Returns(displayManagerMock.Object);
                m_workspaceMock.SetupGet(item => item.CursorLocation)
                    .Returns(() => m_cursorLocation);
                m_workspaceMock.SetupGet(item => item.FocusedWindow)
                    .Returns(() => m_focusedWindow);
                m_workspaceMock.Setup(item => item.GetSnapshot())
                    .Returns(() => m_workspaceWindows.ToArray());

                m_settings = new BehaviorSubject<ITilingServiceSettings>(
                    initialSettings);
                Coordinator = new AlgorithmicLayoutCoordinator(
                    m_workspaceMock.Object,
                    Dispatcher.CurrentDispatcher);
                EventTracker = new AlgorithmicWindowTransferEventTracker(
                    Dispatcher.CurrentDispatcher);
                Service = new TilingService(
                    m_workspaceMock.Object,
                    Display,
                    animationThread ?? new FakeAnimationThread(),
                    m_settings,
                    Coordinator,
                    autoRegisterWindows: true,
                    logger ?? new Mock<ILogger>(MockBehavior.Loose).Object,
                    (_, _) => Overlay,
                    PublishCapacity,
                    EventTracker);
            }

            private void ConfigureDesktop(
                Mock<IVirtualDesktop> desktopMock,
                IVirtualDesktop desktop,
                int index,
                string name,
                bool isCurrent)
            {
                desktopMock.SetupGet(item => item.Workspace)
                    .Returns(m_workspaceMock.Object);
                desktopMock.SetupGet(item => item.IsAlive).Returns(true);
                desktopMock.SetupGet(item => item.IsCurrent).Returns(isCurrent);
                desktopMock.SetupGet(item => item.Index).Returns(index);
                desktopMock.SetupGet(item => item.Name).Returns(name);
                desktopMock.Setup(item => item.HasWindow(It.IsAny<IWindow>()))
                    .Returns((IWindow window) =>
                    {
                        if (TryGetScriptedWindowOwnership(
                                window,
                                desktop,
                                out var scriptedOwnership))
                        {
                            return scriptedOwnership;
                        }
                        bool ownsWindow = m_windowDesktops.TryGetValue(
                                window,
                                out var owner)
                            && ReferenceEquals(owner, desktop);
                        if (ownsWindow
                            && ReferenceEquals(desktop, Desktop)
                            && SourceOwnershipProbeFailuresRemaining > 0)
                        {
                            SourceOwnershipProbeFailuresRemaining--;
                            return false;
                        }
                        return ownsWindow;
                    });
            }

            private bool TryGetScriptedWindowOwnership(
                IWindow window,
                IVirtualDesktop candidate,
                out bool ownsWindow)
            {
                if (!m_windowOwnershipProbeScripts.TryGetValue(
                        window,
                        out var observations))
                {
                    ownsWindow = false;
                    return false;
                }

                IVirtualDesktop? observedDesktop = observations.Count > 0
                    ? observations.Peek()
                    : m_windowDesktops.GetValueOrDefault(window);
                ownsWindow = observedDesktop != null
                    && ReferenceEquals(observedDesktop, candidate);
                bool completesAttempt = ownsWindow
                    || (m_virtualDesktops.Count > 0
                        && ReferenceEquals(candidate, m_virtualDesktops[^1]));
                if (completesAttempt)
                {
                    if (observations.Count > 0)
                    {
                        observations.Dequeue();
                    }
                    m_windowOwnershipProbeAttempts[window] =
                        m_windowOwnershipProbeAttempts.GetValueOrDefault(window) + 1;
                }
                return true;
            }

            private bool PublishCapacity(
                LayoutStateKey key,
                MasterSatelliteCapacitySnapshot capacity,
                long publicationSequence)
            {
                bool published = Coordinator.PublishCapacity(
                    key,
                    capacity,
                    publicationSequence);
                CapacityPublicationHook?.Invoke(
                    key,
                    capacity,
                    publicationSequence);
                return published;
            }

            public IWindow CreateWindow(string title, bool canResize = true)
                => CreateWindowCore(
                    title,
                    new IntPtr(m_nextWindowHandle++),
                    canResize);

            public IWindow CreateWindowWithHandle(
                string title,
                IntPtr handle,
                bool canResize = true)
                => CreateWindowCore(title, handle, canResize);

            private IWindow CreateWindowCore(
                string title,
                IntPtr handle,
                bool canResize)
            {
                var position = Rectangle.OffsetAndSize(100, 100, 800, 600);
                var state = WinMan.WindowState.Restored;
                var mock = new Mock<IWindow>(MockBehavior.Loose);
                var window = mock.Object;

                m_windowCanResize.Add(window, canResize);
                m_windowIsAlive.Add(window, true);
                m_windowMinSizes.Add(window, new WinMan.Point(0, 0));

                mock.SetupGet(item => item.SyncRoot).Returns(new object());
                mock.SetupGet(item => item.Workspace).Returns(m_workspaceMock.Object);
                mock.SetupGet(item => item.Title).Returns(title);
                mock.SetupGet(item => item.Position).Returns(() => position);
                mock.SetupGet(item => item.State).Returns(() => state);
                mock.SetupGet(item => item.MinSize)
                    .Returns(() => m_windowMinSizes[window]);
                mock.SetupGet(item => item.MaxSize).Returns((WinMan.Point?)null);
                mock.SetupGet(item => item.FrameMargins).Returns(new Rectangle());
                mock.SetupGet(item => item.CanResize)
                    .Returns(() => m_windowCanResize[window]);
                mock.SetupGet(item => item.CanMove).Returns(true);
                mock.SetupGet(item => item.CanReorder).Returns(true);
                mock.SetupGet(item => item.CanMinimize).Returns(true);
                mock.SetupGet(item => item.CanMaximize).Returns(true);
                mock.SetupGet(item => item.CanClose).Returns(true);
                mock.SetupGet(item => item.IsTopmost).Returns(false);
                mock.SetupGet(item => item.IsFocused)
                    .Returns(() => ReferenceEquals(m_focusedWindow, window));
                mock.SetupGet(item => item.IsAlive)
                    .Returns(() => m_windowIsAlive[window]);
                mock.SetupGet(item => item.Handle).Returns(handle);
                mock.Setup(item => item.GetProcess())
                    .Returns(System.Diagnostics.Process.GetCurrentProcess());
                mock.Setup(item => item.SetPosition(It.IsAny<Rectangle>()))
                    .Callback((Rectangle value) => position = value);
                mock.Setup(item => item.SetState(It.IsAny<WinMan.WindowState>()))
                    .Callback((WinMan.WindowState value) => state = value);
                mock.Setup(item => item.GetHashCode()).Returns(handle.GetHashCode());
                mock.Setup(item => item.Equals(It.IsAny<IWindow>()))
                    .Returns((IWindow other) =>
                    {
                        try
                        {
                            return other != null && other.Handle == handle;
                        }
                        catch (InvalidWindowReferenceException)
                        {
                            return false;
                        }
                    });
                m_windowMocks.Add(window, mock);
                return window;
            }

            public void SetCanResize(IWindow window, bool canResize)
            {
                Assert.IsTrue(m_windowCanResize.ContainsKey(window));
                m_windowCanResize[window] = canResize;
            }

            public void SetWindowAlive(IWindow window, bool isAlive)
            {
                Assert.IsTrue(m_windowIsAlive.ContainsKey(window));
                m_windowIsAlive[window] = isAlive;
            }

            public void SetMinSize(IWindow window, WinMan.Point minSize)
            {
                Assert.IsTrue(m_windowMinSizes.ContainsKey(window));
                m_windowMinSizes[window] = minSize;
            }

            public void SetOwnershipProbeSequence(
                IWindow window,
                params IVirtualDesktop?[] observations)
            {
                ArgumentNullException.ThrowIfNull(observations);
                m_windowOwnershipProbeScripts[window] = new Queue<IVirtualDesktop?>(
                    observations);
                m_windowOwnershipProbeAttempts[window] = 0;
            }

            public int GetOwnershipProbeAttemptCount(IWindow window)
                => m_windowOwnershipProbeAttempts.GetValueOrDefault(window);

            public void ReplaceRemainingOwnershipProbeSequence(
                IWindow window,
                params IVirtualDesktop?[] observations)
            {
                ArgumentNullException.ThrowIfNull(observations);
                if (!m_windowOwnershipProbeScripts.TryGetValue(
                        window,
                        out var existing))
                {
                    Assert.Fail("No ownership-probe script exists for the window.");
                    return;
                }
                existing.Clear();
                foreach (var observation in observations)
                {
                    existing.Enqueue(observation);
                }
            }

            public void RememberWindowDesktop(
                IWindow window,
                IVirtualDesktop desktop)
            {
                Assert.IsTrue(EventTracker.TryGetStableWindowHandle(
                    window,
                    out var windowHandle));
                EventTracker.RememberDesktop(windowHandle, desktop);
            }

            public IWindow AddWindow(string title)
            {
                var window = CreateWindow(title);
                AddWindow(window);
                return window;
            }

            public IWindow AddWindow(string title, IVirtualDesktop desktop)
            {
                var window = CreateWindow(title);
                AddWindow(window, desktop);
                return window;
            }

            public void AddWindow(IWindow window)
            {
                AddWindow(window, Desktop);
            }

            public void AddWindow(IWindow window, IVirtualDesktop desktop)
            {
                SetWindowDesktop(window, desktop);
                if (!m_workspaceWindows.Any(candidate =>
                    ReferenceEquals(candidate, window)))
                {
                    m_workspaceWindows.Add(window);
                }
                m_focusedWindow = window;
                RaiseWindowAdded(window);
            }

            public void SetWindowDesktop(IWindow window, IVirtualDesktop desktop)
            {
                m_windowDesktops[window] = desktop;
            }

            public void AddVirtualDesktop(IVirtualDesktop desktop)
            {
                if (!m_virtualDesktops.Contains(desktop))
                {
                    m_virtualDesktops.Add(desktop);
                }
            }

            public IVirtualDesktop GetWindowDesktop(IWindow window)
            {
                Assert.IsTrue(m_windowDesktops.TryGetValue(window, out var desktop));
                return desktop!;
            }

            public void ForgetWindowEventTracking(IWindow window)
            {
                Assert.IsTrue(EventTracker.Forget(window));
            }

            public void RaiseWindowAdded(IWindow window)
            {
                m_workspaceMock.Raise(
                    item => item.WindowAdded += null,
                    new WindowChangedEventArgs(window));
                DrainDispatcher();
            }

            public void RaiseWindowRemoved(IWindow window)
            {
                m_workspaceMock.Raise(
                    item => item.WindowRemoved += null,
                    new WindowChangedEventArgs(window));
                DrainDispatcher();
            }

            public void RaiseWindowDestroyed(IWindow window)
            {
                m_windowMocks[window].Raise(
                    item => item.Destroyed += null,
                    new WindowChangedEventArgs(window));
                DrainDispatcher();
            }

            public void RaiseCurrentDesktopChanged(IVirtualDesktop desktop)
            {
                var oldDesktop = m_currentDesktop;
                m_currentDesktop = desktop;
                VirtualDesktopManagerMock.Raise(
                    item => item.CurrentDesktopChanged += null,
                    new CurrentDesktopChangedEventArgs(desktop, oldDesktop));
                DrainDispatcher();
            }

            public void Focus(IWindow window)
            {
                Assert.IsTrue(m_workspaceWindows.Any(candidate =>
                    ReferenceEquals(candidate, window)));
                m_focusedWindow = window;
            }

            public void SetCursor(WinMan.Point cursorLocation)
            {
                m_cursorLocation = cursorLocation;
            }

            public void RaisePositionChangeStart(IWindow window)
            {
                // Initial placement includes asynchronous fake-window writes.
                // Settle those writes before callers capture a same-size move.
                DrainMouseLayoutPipeline();
                var position = window.Position;
                m_windowMocks[window].Raise(
                    item => item.PositionChangeStart += null,
                    new WindowPositionChangedEventArgs(
                        window,
                        position,
                        position));
                DrainDispatcher();
            }

            public void RaisePositionChanged(
                IWindow window,
                Rectangle newPosition)
            {
                // TilingService intentionally coalesces mouse events for the
                // display refresh period and suppresses the first 100 ms after
                // a placement failure. Keep this UI-less fixture outside both
                // timing windows before delivering the representative move.
                System.Threading.Thread.Sleep(125);
                var oldPosition = window.Position;
                window.SetPosition(newPosition);
                m_windowMocks[window].Raise(
                    item => item.PositionChanged += null,
                    new WindowPositionChangedEventArgs(
                        window,
                        newPosition,
                        oldPosition));
                DrainMouseLayoutPipeline();
            }

            public void QueuePositionChangedFromWorker(
                IWindow window,
                Rectangle newPosition)
            {
                // Keep the representative callback outside the service's initial
                // 100 ms placement-failure coalescing interval.
                System.Threading.Thread.Sleep(125);
                var oldPosition = window.Position;
                window.SetPosition(newPosition);
                System.Threading.Tasks.Task.Run(() =>
                    m_windowMocks[window].Raise(
                        item => item.PositionChanged += null,
                        new WindowPositionChangedEventArgs(
                            window,
                            newPosition,
                            oldPosition)))
                    .GetAwaiter()
                    .GetResult();
            }

            public void RaisePositionChangeEnd(IWindow window)
            {
                var position = window.Position;
                m_windowMocks[window].Raise(
                    item => item.PositionChangeEnd += null,
                    new WindowPositionChangedEventArgs(
                        window,
                        position,
                        position));
                DrainMouseLayoutPipeline();
            }

            public void RaisePositionChangeEndFromWorker(IWindow window)
            {
                var position = window.Position;
                var dispatcher = Dispatcher.CurrentDispatcher;
                var frame = new DispatcherFrame();
                var raiseTask = System.Threading.Tasks.Task.Run(() =>
                    m_windowMocks[window].Raise(
                        item => item.PositionChangeEnd += null,
                        new WindowPositionChangedEventArgs(
                            window,
                            position,
                            position)));
                _ = raiseTask.ContinueWith(
                    _ => dispatcher.BeginInvoke(
                        DispatcherPriority.ApplicationIdle,
                        new Action(() => frame.Continue = false)),
                    System.Threading.Tasks.TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
                raiseTask.GetAwaiter().GetResult();
                DrainMouseLayoutPipeline();
            }

            public void DrainMouseLayoutPipeline()
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                var queue = GetServiceField<LayoutInvalidationQueue>("m_layoutInvalidations");
                var queueType = typeof(LayoutInvalidationQueue);
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                var queueLock = queueType.GetField("m_lock", flags)!.GetValue(queue)!;
                var queueDirty = queueType.GetField("m_dirty", flags)!;
                var queueScheduled = queueType.GetField("m_scheduled", flags)!;
                var frozen = GetServiceField<Counter>("m_frozen");
                var frame = new DispatcherFrame();
                DispatcherOperation? readinessCheck = null;
                bool timedOut = false;

                (bool ServiceDirty, int Frozen, bool QueueDirty, bool Scheduled) ReadState()
                {
                    lock (queueLock)
                    {
                        return (LayoutInvalidated, frozen.Count,
                            (bool)queueDirty.GetValue(queue)!,
                            (bool)queueScheduled.GetValue(queue)!);
                    }
                }

                void ScheduleReadinessCheck()
                {
                    if (!frame.Continue || readinessCheck != null) { return; }
                    readinessCheck = dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                    {
                        var state = ReadState();
                        if (!state.ServiceDirty && state.Frozen == 0 && !state.QueueDirty && !state.Scheduled)
                        {
                            frame.Continue = false;
                        }
                    }));
                }

                void OnDispatcherOperationFinished(object? sender, DispatcherHookEventArgs args)
                {
                    if (ReferenceEquals(args.Operation, readinessCheck))
                    {
                        readinessCheck = null;
                        return;
                    }
                    ScheduleReadinessCheck();
                }

                var timeout = new DispatcherTimer(DispatcherPriority.Send, dispatcher)
                {
                    Interval = TimeSpan.FromSeconds(5)
                };
                void OnTimeout(object? sender, EventArgs args)
                {
                    timedOut = true;
                    frame.Continue = false;
                }

                // A completed dispatcher turn may precede an asynchronous layout
                // continuation. Observe actual ownership until it becomes idle;
                // the timer is only a failure guard, never completion evidence.
                dispatcher.Hooks.OperationCompleted += OnDispatcherOperationFinished;
                dispatcher.Hooks.OperationAborted += OnDispatcherOperationFinished;
                timeout.Tick += OnTimeout;
                try
                {
                    timeout.Start();
                    ScheduleReadinessCheck();
                    Dispatcher.PushFrame(frame);
                }
                finally
                {
                    timeout.Stop();
                    timeout.Tick -= OnTimeout;
                    dispatcher.Hooks.OperationCompleted -= OnDispatcherOperationFinished;
                    dispatcher.Hooks.OperationAborted -= OnDispatcherOperationFinished;
                    readinessCheck?.Abort();
                }
                Assert.IsFalse(timedOut,
                    $"The layout pipeline did not finish: active={Service.Active}, disposed={m_disposed}, state={ReadState()}.");
            }

            public void Pin(IWindow window)
            {
                m_pinnedWindows.Add(window);
            }

            public void Publish(ITilingServiceSettings settings)
            {
                m_settings.OnNext(settings);
                DrainDispatcher();
            }

            public void PublishWithoutDispatch(ITilingServiceSettings settings)
                => m_settings.OnNext(settings);

            public T GetServiceField<T>(string name) => (T)typeof(TilingService)
                .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(Service)!;

            public bool LayoutInvalidated => GetServiceField<bool>("m_dirty");

            public void HoldLayoutForSettingsObservation()
            {
                // Exercise the active service's real dirty flag while retaining
                // the normal frozen-layout guard against async placement work.
                GetServiceField<Counter>("m_frozen").Increment();
                typeof(TilingService).GetField("m_active", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .SetValue(Service, true);
                typeof(TilingService).GetField("m_dirty", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .SetValue(Service, false);
            }

            public void DrainDispatcher()
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
            }

            public void DrainIncomingOwnershipProbes()
            {
                for (int i = 0; i < 8; i++)
                {
                    DrainDispatcher();
                    var probes = ProbeMap(this).Values.Cast<object>().ToArray();
                    if (probes.Length == 0) return;
                    foreach (var probe in probes)
                    {
                        // A failed HasWindow read can consume the final fake error
                        // while still scheduling another probe. Drive the actual
                        // owned callback until completion, independently of wall time.
                        var timer = (DispatcherTimer)probe.GetType().GetProperty("Timer")!.GetValue(probe)!;
                        var tick = (EventHandler)probe.GetType().GetProperty("Tick")!.GetValue(probe)!;
                        timer.Stop();
                        tick(timer, EventArgs.Empty);
                    }
                }
                DrainDispatcher();
                Assert.AreEqual(0, ProbeMap(this).Count, "Incoming ownership probes did not reach a terminal state.");
            }

            public void DrainIncomingOwnershipProbesUntil(
                Func<bool> completed)
            {
                ArgumentNullException.ThrowIfNull(completed);
                for (int i = 0; i < 24; i++)
                {
                    DrainDispatcher();
                    if (completed())
                    {
                        return;
                    }
                    System.Threading.Thread.Sleep(60);
                }
                DrainDispatcher();
                Assert.IsTrue(
                    completed(),
                    "The deferred ownership pipeline did not reach its expected terminal state.");
            }

            public void Dispose()
            {
                if (m_disposed)
                {
                    return;
                }
                m_disposed = true;
                Service.Dispose();
                Coordinator.Dispose();
                m_settings.Dispose();
            }
        }

        private sealed class TwoDisplayServiceFixture : IDisposable
        {
            private readonly Mock<IWorkspace> m_workspace = new(MockBehavior.Loose);
            private readonly Mock<IVirtualDesktopManager> m_desktopManager
                = new(MockBehavior.Loose);
            private readonly List<IVirtualDesktop> m_desktops = new();
            private readonly BehaviorSubject<ITilingServiceSettings> m_settings;
            private bool m_disposed;

            public IVirtualDesktop InitialDesktop { get; }
            public IVirtualDesktop CreatedDesktop { get; }
            public IDisplay PrimaryDisplay { get; }
            public IDisplay SecondaryDisplay { get; }
            public AlgorithmicLayoutCoordinator Coordinator { get; }
            public TilingService PrimaryService { get; }
            public TilingService SecondaryService { get; }

            public TwoDisplayServiceFixture()
            {
                var initialDesktop = new Mock<IVirtualDesktop>(MockBehavior.Loose);
                var createdDesktop = new Mock<IVirtualDesktop>(MockBehavior.Loose);
                var primaryDisplay = new Mock<IDisplay>(MockBehavior.Loose);
                var secondaryDisplay = new Mock<IDisplay>(MockBehavior.Loose);
                var displayManager = new Mock<IDisplayManager>(MockBehavior.Loose);
                InitialDesktop = initialDesktop.Object;
                CreatedDesktop = createdDesktop.Object;
                PrimaryDisplay = primaryDisplay.Object;
                SecondaryDisplay = secondaryDisplay.Object;
                ConfigureDesktop(initialDesktop, InitialDesktop, 0, "Desktop 1", true);
                ConfigureDesktop(createdDesktop, CreatedDesktop, 1, "Desktop 2", false);
                ConfigureDisplay(
                    primaryDisplay,
                    Rectangle.OffsetAndSize(0, 0, 1920, 1080));
                ConfigureDisplay(
                    secondaryDisplay,
                    Rectangle.OffsetAndSize(1920, 0, 2560, 1440));
                m_desktops.Add(InitialDesktop);

                m_desktopManager.SetupGet(item => item.Workspace)
                    .Returns(m_workspace.Object);
                m_desktopManager.SetupGet(item => item.CanManageVirtualDesktops)
                    .Returns(true);
                m_desktopManager.SetupGet(item => item.CurrentDesktop)
                    .Returns(InitialDesktop);
                m_desktopManager.SetupGet(item => item.Desktops)
                    .Returns(() => m_desktops.ToArray());
                m_desktopManager
                    .Setup(item => item.IsWindowPinned(It.IsAny<IWindow>()))
                    .Returns(false);
                displayManager.SetupGet(item => item.Workspace)
                    .Returns(m_workspace.Object);
                displayManager.SetupGet(item => item.PrimaryDisplay)
                    .Returns(PrimaryDisplay);
                displayManager.SetupGet(item => item.Displays)
                    .Returns(new[] { PrimaryDisplay, SecondaryDisplay });
                m_workspace.SetupGet(item => item.VirtualDesktopManager)
                    .Returns(m_desktopManager.Object);
                m_workspace.SetupGet(item => item.DisplayManager)
                    .Returns(displayManager.Object);
                m_workspace.SetupGet(item => item.CursorLocation)
                    .Returns(new WinMan.Point(0, 0));
                m_workspace.Setup(item => item.GetSnapshot())
                    .Returns(Array.Empty<IWindow>());

                m_settings = new BehaviorSubject<ITilingServiceSettings>(
                    EnabledSettings(true));
                Coordinator = new AlgorithmicLayoutCoordinator(
                    m_workspace.Object,
                    Dispatcher.CurrentDispatcher);
                PrimaryService = CreateService(PrimaryDisplay);
                SecondaryService = CreateService(SecondaryDisplay);
            }

            public void RaiseDesktopAdded()
            {
                m_desktops.Add(CreatedDesktop);
                m_desktopManager.Raise(
                    item => item.DesktopAdded += null,
                    new DesktopChangedEventArgs(CreatedDesktop));
                DrainDispatcher();
            }

            public void DrainDispatcher()
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
            }

            public void Dispose()
            {
                if (m_disposed)
                {
                    return;
                }
                m_disposed = true;
                SecondaryService.Dispose();
                PrimaryService.Dispose();
                Coordinator.Dispose();
                m_settings.Dispose();
            }

            private TilingService CreateService(IDisplay display)
            {
                return new TilingService(
                    m_workspace.Object,
                    display,
                    new FakeAnimationThread(),
                    m_settings,
                    Coordinator,
                    autoRegisterWindows: true,
                    new Mock<ILogger>(MockBehavior.Loose).Object,
                    (_, _) => new FakeTilingOverlayRenderer());
            }

            private void ConfigureDesktop(
                Mock<IVirtualDesktop> desktopMock,
                IVirtualDesktop desktop,
                int index,
                string name,
                bool isCurrent)
            {
                desktopMock.SetupGet(item => item.Workspace)
                    .Returns(m_workspace.Object);
                desktopMock.SetupGet(item => item.IsAlive).Returns(true);
                desktopMock.SetupGet(item => item.IsCurrent).Returns(isCurrent);
                desktopMock.SetupGet(item => item.Index).Returns(index);
                desktopMock.SetupGet(item => item.Name).Returns(name);
                desktopMock.Setup(item => item.HasWindow(It.IsAny<IWindow>()))
                    .Returns(false);
            }

            private void ConfigureDisplay(
                Mock<IDisplay> displayMock,
                Rectangle workArea)
            {
                var display = displayMock.Object;
                displayMock.SetupGet(item => item.Workspace)
                    .Returns(m_workspace.Object);
                displayMock.SetupGet(item => item.WorkArea).Returns(workArea);
                displayMock.SetupGet(item => item.Bounds).Returns(workArea);
                displayMock.SetupGet(item => item.Scaling).Returns(1.0);
                displayMock.SetupGet(item => item.RefreshRate).Returns(60);
                displayMock.Setup(item => item.Equals(It.IsAny<IDisplay>()))
                    .Returns((IDisplay other) => ReferenceEquals(display, other));
            }
        }

        private sealed class ReferenceWindowMatcher : IWindowMatcher
        {
            private readonly IWindow m_expected;

            public ReferenceWindowMatcher(IWindow expected)
            {
                m_expected = expected;
            }

            public bool Matches(IWindow window) =>
                ReferenceEquals(m_expected, window);
        }

        private sealed class FakeAnimationThread : IAnimationThread
        {
            public void Start(IAnimationJob job)
            {
                ArgumentNullException.ThrowIfNull(job);
                job.OnCompleted();
            }

            public void Dispose()
            {
            }
        }

        internal sealed class FakeTilingOverlayRenderer : ITilingOverlayRenderer
        {
#pragma warning disable CS0067
            public event EventHandler<PanelNode>? TilingPanelMoveRequested;
            public event EventHandler<PanelNode>? TilingPanelMoving;
            public event EventHandler<TilingNode>? TilingNodeFocusRequested;
            public event EventHandler<TilingNode>? TilingNodePullUpRequested;
            public event EventHandler<TilingNode>? TilingNodeCloseRequested;
            public event EventHandler<TilingNode>? HorizontalSplitRequested;
            public event EventHandler<TilingNode>? VerticalSplitRequested;
            public event EventHandler<TilingNode>? StackRequested;
            public event EventHandler<TilingNode>? PullUpRequested;
            public event EventHandler<WindowNode>? FloatRequested;
            public event EventHandler<WindowNode>? IgnoreProcessRequested;
            public event EventHandler<WindowNode>? IgnoreClassRequested;
            public event EventHandler<WindowNode>? BeginHorizontalWithRequested;
            public event EventHandler<WindowNode>? BeginVerticalWithRequested;
            public event EventHandler<WindowNode>? BeginStackWithRequested;
#pragma warning restore CS0067

            public int PanelSpacing { get; set; }
            public Thickness PanelPadding { get; set; }
            public IReadOnlySet<IWindow> PreviewWindows { get; set; }
                = new HashSet<IWindow>();
            public Rectangle? FocusRectangle { get; set; }
            public Rectangle? PreviewRectangle { get; set; }
            public IWindow? IntentSourceWindow { get; set; }
            public bool IsDisposed { get; private set; }
            public int InvalidateViewCount { get; private set; }
            public int UpdateOverlayCount { get; private set; }
            public int HideCount { get; private set; }
            public Action? OnHide { get; set; }
            public Action? OnUpdateOverlay { get; set; }
            public IReadOnlyCollection<TilingNode> LastSnapshot { get; private set; }
                = Array.Empty<TilingNode>();

            public void UpdateOverlay(
                IReadOnlyCollection<TilingNode> snapshot,
                IReadOnlyCollection<TilingNode> focusedPath)
            {
                UpdateOverlayCount++;
                LastSnapshot = snapshot.ToArray();
                OnUpdateOverlay?.Invoke();
            }

            public Rectangle GetWindowRectangle(IWindow window)
            {
                return LastSnapshot
                    .OfType<WindowNode>()
                    .Single(item => ReferenceEquals(item.WindowReference, window))
                    .ComputedRectangle;
            }

            public void InvalidateView()
            {
                InvalidateViewCount++;
            }

            public void Show()
            {
            }

            public void Hide()
            {
                HideCount++;
                OnHide?.Invoke();
            }

            public void Dispose()
            {
                IsDisposed = true;
            }
        }
    }
}
