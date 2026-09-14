using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

using FancyWM.AlgorithmicLayouts;
using FancyWM.Layouts;
using FancyWM.Layouts.Tiling;
using FancyWM.Models;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    public partial class MasterSatelliteDropControllerTest
    {
        [DataTestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(3)]
        public void SameDiscreteDropStillSamplesAllWindowsButReusesSimulation(int kind)
        {
            var context = CreateActive(CreateWindows(4));
            var (source, pointer) = PreviewInput(context, (MasterSatelliteDropKind)kind);
            var state = GetState(context);
            var before = Snapshot(context);
            int minimumReads = 0;
            foreach (var window in context.Windows)
            {
                Mock.Get(window).SetupGet(item => item.MinSize)
                    .Returns(() => { minimumReads++; return new Point(0, 0); });
            }

            var first = context.Controller.CreateWindowDropPlan(
                context.Workspace, context.Desktop, state, context.Settings, source, pointer);
            int coldReads = minimumReads;
            minimumReads = 0;
            var repeated = context.Controller.CreateWindowDropPlan(
                context.Workspace, context.Desktop, state, context.Settings, source,
                new Point(pointer.X, pointer.Y + 1));

            Assert.IsTrue(first.IsAccepted, first.Message);
            Assert.AreEqual((MasterSatelliteDropKind)kind, first.Kind);
            Assert.AreEqual(first.Kind, repeated.Kind);
            Assert.AreEqual(first.PreviewRectangle, repeated.PreviewRectangle);
            Assert.AreEqual(first.FromSatelliteIndex, repeated.FromSatelliteIndex);
            Assert.AreEqual(first.ToSatelliteIndex, repeated.ToSatelliteIndex);
            Assert.AreEqual(first.TargetMasterSide, repeated.TargetMasterSide);
            CollectionAssert.AreEqual(first.PreviewWindows.ToArray(), repeated.PreviewWindows.ToArray());
            Assert.AreEqual(before, Snapshot(context), "Both previews must leave the live tree unchanged.");
            Assert.IsTrue(minimumReads >= context.Windows.Length,
                "A repeated preview must still sample every current minimum size.");
            Assert.IsTrue(minimumReads < coldReads,
                $"The same logical zone repeated all simulation measurements ({minimumReads}/{coldReads}).");
            Assert.AreSame(first, repeated, "Only a fully prepared, equivalent accepted plan can be reused.");
        }

        private static (IWindow Source, Point Pointer) PreviewInput(
            ActiveContext context, MasterSatelliteDropKind kind)
        {
            var master = RectangleFor(context, context.Windows[0]);
            var target = RectangleFor(context, context.Windows[^1]);
            return kind switch
            {
                MasterSatelliteDropKind.ReorderSatellite =>
                    (context.Windows[1], new Point(target.Center.X, target.Bottom - 3)),
                MasterSatelliteDropKind.PromoteSourceSatellite =>
                    (context.Windows[1], master.Center),
                MasterSatelliteDropKind.PromoteTargetSatellite =>
                    (context.Windows[0], target.Center),
                MasterSatelliteDropKind.ChangeMasterSide =>
                    (context.Windows[0], new Point(master.Right + 1, master.Center.Y)),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
        }

        [DataTestMethod]
        [DataRow("work-area")]
        [DataRow("root-spacing")]
        [DataRow("satellite-spacing")]
        [DataRow("root-padding")]
        [DataRow("window-padding")]
        [DataRow("fractional-flex")]
        [DataRow("minimum")]
        [DataRow("maximum")]
        [DataRow("generation")]
        [DataRow("window-wrapper")]
        [DataRow("handle")]
        [DataRow("requested-ratio")]
        [DataRow("effective-ratio")]
        [DataRow("recovering")]
        [DataRow("orientation")]
        [DataRow("side")]
        [DataRow("order")]
        [DataRow("revision")]
        [DataRow("settings")]
        public void SameRevisionPreparedInputChangesReplanExactly(string change)
        {
            var context = CreateActive(CreateWindows(4));
            var state = GetState(context);
            var (source, pointer) = PreviewInput(context, MasterSatelliteDropKind.PromoteSourceSatellite);
            var original = PreviewAt(context, source, pointer);
            Assert.IsTrue(original.IsAccepted, original.Message);
            Assert.AreSame(original, PreviewAt(context, source, pointer));
            long revision = state.Revision;
            var tree = context.Workspace.GetTree(context.Desktop)!;
            var root = (SplitPanelNode)tree.Root!;
            var satellites = root.Children.OfType<SplitPanelNode>().Single();
            switch (change)
            {
                case "work-area": tree.WorkArea = Rectangle.OffsetAndSize(0, 0, 1100, 700); break;
                case "root-spacing": root.Spacing = 8; break;
                case "satellite-spacing": satellites.Spacing = 8; break;
                case "root-padding": root.Padding = new Rectangle(1, 2, 3, 4); break;
                case "window-padding": tree.FindNode(context.Windows[3])!.Padding = new Rectangle(1, 2, 3, 4); break;
                case "fractional-flex":
                    Assert.IsTrue(root.ResizeBy(root.Children[0], 0.01, GrowDirection.TowardsEnd));
                    tree.Arrange();
                    Assert.AreEqual(original.PreviewOperation!.Before.WorkArea, tree.WorkArea);
                    break;
                case "minimum": Mock.Get(context.Windows[3]).SetupGet(window => window.MinSize).Returns(new Point(40, 35)); break;
                case "maximum":
                    typeof(TilingNode).GetProperty(nameof(TilingNode.ContentMaxSize))!
                        .SetValue(tree.FindNode(context.Windows[3]), new Point(700, 700));
                    break;
                case "generation":
                case "window-wrapper":
                    var oldNode = tree.FindNode(context.Windows[3])!;
                    var parent = oldNode.Parent!;
                    int index = parent.IndexOf(oldNode);
                    var replacement = context.Windows[3];
                    if (change == "window-wrapper")
                    {
                        replacement = m_windows.Create("Replacement");
                        Mock.Get(replacement).SetupGet(window => window.Handle).Returns(context.Windows[3].Handle);
                        state.MutableSatellites[2] = replacement;
                        context.Windows[3] = replacement;
                    }
                    parent.Detach(oldNode);
                    parent.Attach(index, new WindowNode(replacement));
                    break;
                case "handle": Mock.Get(context.Windows[3]).SetupGet(window => window.Handle).Returns(new IntPtr(999)); break;
                case "requested-ratio": state.RequestedMasterRatio = 0.65; break;
                case "effective-ratio": state.EffectiveMasterRatio = 0.61; break;
                case "recovering": state.IsRecovering = true; break;
                case "orientation":
                    Assert.IsTrue(context.Workspace.SetMasterSatelliteOrientation(
                        context.Desktop, state, context.Settings, SatelliteLayoutOrientation.Horizontal).Succeeded);
                    break;
                case "side":
                    Assert.IsTrue(context.Workspace.SetMasterSatelliteSide(
                        context.Desktop, state, context.Settings, MasterSide.Right).Succeeded);
                    pointer = RectangleFor(context, state.Master!).Center;
                    break;
                case "order":
                    Assert.IsTrue(context.Workspace.ReorderMasterSatelliteWindow(
                        context.Desktop, state, context.Settings, 0, 2).Succeeded);
                    break;
                case "revision": state.Revision++; break;
                case "settings": context = context with { Settings = context.Settings with { FollowOverflowWindow = true } }; break;
                default: Assert.Fail(change); break;
            }
            if (change != "revision")
            {
                state.Revision = revision;
            }
            var actual = PreviewAt(context, source, pointer);
            var expected = PreviewAt(context with { Controller = new MasterSatelliteDropController() }, source, pointer);
            Assert.AreNotSame(original, actual, $"Changed {change} reused an old accepted plan.");
            AssertEquivalentPlan(expected, actual);
            Assert.AreSame(actual, PreviewAt(context, source, pointer), "The new exact state should become the sole cache entry.");
        }

        [TestMethod]
        public void FreshMinimumConflictAndInvalidCanonicalTreeNeverReuseAcceptedPreview()
        {
            var context = CreateActive(CreateWindows(4));
            var (source, pointer) = PreviewInput(context, MasterSatelliteDropKind.PromoteSourceSatellite);
            var first = PreviewAt(context, source, pointer);
            Mock.Get(context.Windows[0]).SetupGet(window => window.MinSize).Returns(new Point(100, 500));
            var failed = PreviewAt(context, source, pointer);
            Assert.IsFalse(failed.IsAccepted);
            Assert.AreEqual(MasterSatelliteFailureReason.MinSizeConflict, failed.FailureReason);
            Assert.AreNotSame(failed, PreviewAt(context, source, pointer), "Failed simulations must not be cached.");
            Mock.Get(context.Windows[0]).SetupGet(window => window.MinSize).Returns(new Point(0, 0));
            var recovered = PreviewAt(context, source, pointer);
            Assert.IsTrue(recovered.IsAccepted);
            Assert.AreNotSame(first, recovered, "A failure must release the previous accepted entry.");

            var root = (SplitPanelNode)context.Workspace.GetTree(context.Desktop)!.Root!;
            root.Orientation = PanelOrientation.Vertical;
            var invalid = PreviewAt(context, source, pointer);
            Assert.IsFalse(invalid.IsAccepted);
            Assert.AreEqual(MasterSatelliteFailureReason.InvalidCanonicalTree, invalid.FailureReason);
            root.Orientation = PanelOrientation.Horizontal;
            Assert.AreNotSame(recovered, PreviewAt(context, source, pointer));
        }

        [TestMethod]
        public void PointerZoneChangesOutsideRejectionAndExplicitClearReleasePreviousEntry()
        {
            var context = CreateActive(CreateWindows(4));
            var (source, pointer) = PreviewInput(context, MasterSatelliteDropKind.PromoteSourceSatellite);
            var first = PreviewAt(context, source, pointer);
            var (_, reorderPointer) = PreviewInput(context, MasterSatelliteDropKind.ReorderSatellite);
            var reordered = PreviewAt(context, source, reorderPointer);
            Assert.AreEqual(MasterSatelliteDropKind.ReorderSatellite, reordered.Kind);
            var promotedAgain = PreviewAt(context, source, pointer);
            Assert.AreNotSame(first, promotedAgain, "Only the last accepted zone is retained.");
            Assert.IsFalse(PreviewAt(context, source, new Point(-100, -100)).IsAccepted);
            var afterOutside = PreviewAt(context, source, pointer);
            Assert.AreNotSame(promotedAgain, afterOutside);
            context.Controller.ClearPreviewCache();
            context.Controller.ClearPreviewCache();
            Assert.AreNotSame(afterOutside, PreviewAt(context, source, pointer));
        }

        [TestMethod]
        public void UnexpectedProbeExceptionReleasesEntryBeforeTheNextGesture()
        {
            var context = CreateActive(CreateWindows(4));
            var (source, pointer) = PreviewInput(context, MasterSatelliteDropKind.PromoteSourceSatellite);
            var first = PreviewAt(context, source, pointer);
            var handle = source.Handle;
            Mock.Get(source).SetupGet(window => window.Handle).Throws(new InvalidOperationException("Probe failure"));
            Assert.ThrowsException<InvalidOperationException>(() => PreviewAt(context, source, pointer));
            Mock.Get(source).SetupGet(window => window.Handle).Returns(handle);
            var recovered = PreviewAt(context, source, pointer);
            Assert.IsTrue(recovered.IsAccepted);
            Assert.AreNotSame(first, recovered);
        }

        [TestMethod]
        public void RepeatedPreviewCreateAndClearCyclesRetainAtMostOneEntry()
        {
            var context = CreateActive(CreateWindows(4));
            var (source, pointer) = PreviewInput(context, MasterSatelliteDropKind.PromoteSourceSatellite);
            MasterSatelliteDropPlan previous = null!;
            for (int cycle = 0; cycle < 100; cycle++)
            {
                var first = PreviewAt(context, source, pointer);
                Assert.IsTrue(first.IsAccepted);
                Assert.AreNotSame(previous, first);
                Assert.AreSame(first, PreviewAt(context, source, pointer));
                context.Controller.ClearPreviewCache();
                context.Controller.ClearPreviewCache();
                previous = first;
            }
        }

        [DataTestMethod]
        [DataRow(1)]
        [DataRow(2)]
        public void ReentrantCancellationDuringPreparationOrSimulationCannotRepublishEntry(int cancelAtRead)
        {
            var context = CreateActive(CreateWindows(4));
            var (source, pointer) = PreviewInput(context, MasterSatelliteDropKind.PromoteSourceSatellite);
            int reads = 0;
            Mock.Get(context.Windows[0]).SetupGet(window => window.MinSize).Returns(() =>
            {
                if (++reads == cancelAtRead)
                {
                    context.Controller.ClearPreviewCache();
                }
                return new Point(0, 0);
            });
            var completed = PreviewAt(context, source, pointer);
            Assert.IsTrue(completed.IsAccepted, completed.Message);
            Assert.IsNull(typeof(MasterSatelliteDropController).GetField(
                "m_previewCache", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(context.Controller),
                "A cancelled gesture must not republish a cache entry after the native measurement returns.");
            Assert.AreNotSame(completed, PreviewAt(context, source, pointer));
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void ReentrantHandleInvalidationCannotReturnHitOrPublishCapturedKey(bool existingEntry)
        {
            var context = CreateActive(CreateWindows(4));
            var (source, pointer) = PreviewInput(context, MasterSatelliteDropKind.ChangeMasterSide);
            var primed = existingEntry ? PreviewAt(context, source, pointer) : null;
            bool prepared = false;
            bool armed = true;
            var handle = source.Handle;
            Mock.Get(source).SetupGet(window => window.MinSize).Returns(() =>
            {
                prepared = true;
                return new Point(0, 0);
            });
            Mock.Get(source).SetupGet(window => window.Handle).Returns(() =>
            {
                if (prepared && armed)
                {
                    armed = false;
                    context.Controller.ClearPreviewCache();
                }
                return handle;
            });
            var completed = PreviewAt(context, source, pointer);
            Assert.IsTrue(completed.IsAccepted, completed.Message);
            Assert.IsFalse(armed, "The callback must run after fresh preparation.");
            Assert.AreNotSame(primed, completed);
            Assert.IsNull(typeof(MasterSatelliteDropController).GetField(
                "m_previewCache", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(context.Controller));
        }

        [TestMethod]
        public void RevisionChangedDuringFreshMeasurementCannotReuseOldPlanRevision()
        {
            var context = CreateActive(CreateWindows(4));
            var state = GetState(context);
            var (source, pointer) = PreviewInput(context, MasterSatelliteDropKind.PromoteSourceSatellite);
            var first = PreviewAt(context, source, pointer);
            long before = state.Revision;
            bool changed = false;
            Mock.Get(context.Windows[0]).SetupGet(window => window.MinSize).Returns(() =>
            {
                if (!changed)
                {
                    changed = true;
                    state.Revision++;
                }
                return new Point(0, 0);
            });
            var repeated = PreviewAt(context, source, pointer);
            Assert.IsTrue(repeated.IsAccepted, repeated.Message);
            Assert.AreEqual(before + 1, state.Revision);
            Assert.AreEqual(state.Revision, repeated.SourceRevision,
                "The plan must retain the actual sourceRevision argument, including a reentrant live revision change.");
            Assert.AreEqual(before, repeated.PreviewOperation!.Before.Revision,
                "The detached state was copied before the native measurement; retain existing uncached semantics.");
            Assert.AreNotSame(first, repeated);
        }

        [DataTestMethod]
        [DataRow(1)]
        [DataRow(4)]
        [DataRow(10)]
        public void RejectedOwnSlotPreviewDoesNotAllocateMoreThanFullRejectedMouseUp(int windowCount)
        {
            // The allocator is the subject here. Mock invocation buffers and
            // dynamically optimized proxy callbacks must not enter the sample.
            var windows = Enumerable.Range(0, windowCount)
                .Select(index => new PreviewManagedWindow(index + 1)).ToArray();
            var context = CreateManagedPreviewContext(windows);
            var state = GetState(context);
            var beforeState = Snapshot(context);
            var source = context.Windows[0];
            var pointer = RectangleFor(context, source).Center;
            Func<bool> preview = () => context.Controller.CreateWindowDropPlan(
                context.Workspace, context.Desktop, state, context.Settings, source, pointer).IsAccepted;
            Func<bool> commit = () => context.Controller.ApplyWindowDropAtPointer(
                context.Workspace, context.Desktop, state, context.Settings, source, pointer).Succeeded;
            var uncachedPlanner = typeof(MasterSatelliteDropController)
                .GetMethod("CreateWindowDropPlanCore", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Func<TilingWorkspace, IVirtualDesktop, MasterSatelliteRuntimeState,
                    MasterSatelliteLayoutSettings, IWindow, Point, bool, MasterSatelliteDropPlan>>(context.Controller);
            Func<bool> uncached = () => uncachedPlanner(
                context.Workspace, context.Desktop, state, context.Settings, source, pointer, false).IsAccepted;
            bool accepted = false;
            long Measure(Func<bool> action)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                accepted |= action();
                return GC.GetAllocatedBytesForCurrentThread() - before;
            }
            for (int warmup = 0; warmup < 30; warmup++)
            {
                Measure(preview);
                Measure(commit);
                Measure(uncached);
            }
            Assert.IsFalse(accepted);
            foreach (var window in windows) window.MinimumReads = 0;
            long previewBytes = 0;
            long commitBytes = 0;
            long uncachedBytes = 0;
            long minimumPreviewBytes = long.MaxValue;
            long minimumUncachedBytes = long.MaxValue;
            void MeasurePreview()
            {
                long bytes = Measure(preview);
                previewBytes += bytes;
                minimumPreviewBytes = Math.Min(minimumPreviewBytes, bytes);
            }
            void MeasureUncached()
            {
                long bytes = Measure(uncached);
                uncachedBytes += bytes;
                minimumUncachedBytes = Math.Min(minimumUncachedBytes, bytes);
            }
            // Pair the actual public paths so tiered optimization of their shared
            // mandatory preparation cannot favor a later, separate 1000-call block.
            for (int index = 0; index < 1000; index++)
            {
                if (index % 2 == 0)
                {
                    MeasurePreview();
                    commitBytes += Measure(commit);
                    MeasureUncached();
                }
                else
                {
                    MeasureUncached();
                    commitBytes += Measure(commit);
                    MeasurePreview();
                }
            }
            Assert.IsFalse(accepted);
            Assert.AreEqual(windowCount * 3000, windows.Sum(window => window.MinimumReads),
                "All rejected paths must retain a fresh minimum-size read for every window.");
            Assert.AreEqual(beforeState, Snapshot(context));
            Console.WriteLine($"REJECTION_ALLOCATION W{windowCount} preview={previewBytes} commit={commitBytes} uncached={uncachedBytes} minimum-preview={minimumPreviewBytes} minimum-uncached={minimumUncachedBytes}");
            Assert.IsTrue(previewBytes <= commitBytes,
                $"Rejected preview allocated {previewBytes} bytes versus {commitBytes} for a full rejected mouse-up, which also creates a command result.");
            // Also catch a constant rejection overhead smaller than the command
            // result above. Minimum samples exclude incidental runtime allocations;
            // the aggregate public-path assertion remains strict and unchanged.
            Assert.IsTrue(minimumPreviewBytes <= minimumUncachedBytes,
                $"Every rejected preview allocated at least {minimumPreviewBytes} bytes versus {minimumUncachedBytes} for the same prepared rejection with useCache=false.");
        }

        [TestMethod]
        public void BackendAndDesktopIdentityCannotShareAcceptedPreview()
        {
            var firstContext = CreateActive(CreateWindows(4));
            var (source, pointer) = PreviewInput(firstContext, MasterSatelliteDropKind.PromoteSourceSatellite);
            var first = PreviewAt(firstContext, source, pointer);
            var otherContext = CreateActive(firstContext.Windows) with { Controller = firstContext.Controller };
            var other = PreviewAt(otherContext, source, pointer);
            Assert.IsTrue(other.IsAccepted);
            Assert.AreNotSame(first, other);
            Assert.AreSame(otherContext.Desktop, other.Desktop);
            Assert.AreNotSame(first, PreviewAt(firstContext, source, pointer));
        }

        [TestMethod]
        public void AcceptedPlanAndNestedDiagnosticCollectionsRemainImmutable()
        {
            var context = CreateActive(CreateWindows(4));
            var (source, pointer) = PreviewInput(context, MasterSatelliteDropKind.PromoteSourceSatellite);
            var first = PreviewAt(context, source, pointer);
            Assert.ThrowsException<NotSupportedException>(() => ((IList<IWindow>)first.PreviewWindows)[0] = context.Windows[3]);
            Assert.ThrowsException<NotSupportedException>(() => ((IList<IWindow>)first.PreviewOperation!.Before.Satellites)[0] = context.Windows[3]);
            Assert.ThrowsException<NotSupportedException>(() => ((IList<IWindow>)first.PreviewOperation!.After.Satellites).Clear());
            Assert.ThrowsException<NotSupportedException>(() => ((IList<string>)first.PreviewOperation!.Invariant.Violations).Add("Changed"));
            Assert.AreSame(first, PreviewAt(context, source, pointer));
            CollectionAssert.AreEqual(context.Windows.Skip(1).ToArray(), first.PreviewOperation!.Before.Satellites.ToArray());
        }

        [TestMethod]
        public void MouseUpAlwaysRunsFullSimulationEvenAtAnIdenticalPreparedZone()
        {
            var context = CreateActive(CreateWindows(4));
            var state = GetState(context);
            var (source, pointer) = PreviewInput(context, MasterSatelliteDropKind.PromoteSourceSatellite);
            var first = PreviewAt(context, source, pointer);
            Assert.AreSame(first, PreviewAt(context, source, pointer));
            int masterReads = 0;
            Mock.Get(context.Windows[0]).SetupGet(window => window.MinSize)
                .Returns(() => ++masterReads == 1 ? new Point(0, 0) : new Point(100, 500));
            var result = context.Controller.ApplyWindowDropAtPointer(
                context.Workspace, context.Desktop, state, context.Settings, source, pointer);
            Assert.IsFalse(result.Succeeded, "Mouse-up reused a preview instead of sampling the full commit-time simulation.");
            Assert.IsNull(result.Operation, "A failed full preview must reject before beginning the live mutation.");
            Assert.AreSame(context.Windows[0], state.Master);
            CollectionAssert.AreEqual(context.Windows.Skip(1).ToArray(), state.Satellites.ToArray());
            Mock.Get(context.Windows[0]).SetupGet(window => window.MinSize).Returns(new Point(0, 0));
            Assert.AreNotSame(first, PreviewAt(context, source, pointer));
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void CustomNodeStateAlwaysBypassesCacheIncludingCloneToBuiltIn(bool cloneToBuiltIn)
        {
            var context = CreateActive(CreateWindows(4));
            var tree = context.Workspace.GetTree(context.Desktop)!;
            var old = tree.FindNode(context.Windows[3])!;
            var parent = old.Parent!;
            int index = parent.IndexOf(old);
            parent.Detach(old);
            parent.Attach(index, new CustomPreviewWindowNode(context.Windows[3], cloneToBuiltIn));
            var (source, pointer) = PreviewInput(context, MasterSatelliteDropKind.PromoteSourceSatellite);
            var first = PreviewAt(context, source, pointer);
            var repeated = PreviewAt(context, source, pointer);
            Assert.IsTrue(first.IsAccepted, first.Message);
            Assert.IsTrue(repeated.IsAccepted, repeated.Message);
            Assert.AreEqual(first.PreviewRectangle, repeated.PreviewRectangle);
            Assert.AreNotSame(first, repeated);
        }

        private sealed class CustomPreviewWindowNode(IWindow window, bool cloneToBuiltIn) : WindowNode(window)
        {
            public override object Clone() => cloneToBuiltIn
                ? new WindowNode(WindowReference) { Padding = Padding }
                : base.Clone();
        }

        private static MasterSatelliteDropPlan PreviewAt(ActiveContext context, IWindow source, Point pointer)
            => context.Controller.CreateWindowDropPlan(
                context.Workspace, context.Desktop, GetState(context), context.Settings, source, pointer);

        private static void AssertEquivalentPlan(MasterSatelliteDropPlan expected, MasterSatelliteDropPlan actual)
        {
            Assert.AreEqual(expected.IsAccepted, actual.IsAccepted, actual.Message);
            Assert.AreEqual(expected.FailureReason, actual.FailureReason);
            Assert.AreEqual(expected.Kind, actual.Kind);
            Assert.AreSame(expected.Desktop, actual.Desktop);
            Assert.AreSame(expected.Source, actual.Source);
            Assert.AreSame(expected.Target, actual.Target);
            Assert.AreEqual(expected.SourceRevision, actual.SourceRevision);
            Assert.AreEqual(expected.FromSatelliteIndex, actual.FromSatelliteIndex);
            Assert.AreEqual(expected.ToSatelliteIndex, actual.ToSatelliteIndex);
            Assert.AreEqual(expected.TargetMasterSide, actual.TargetMasterSide);
            Assert.AreEqual(expected.PreviewRectangle, actual.PreviewRectangle);
            CollectionAssert.AreEqual(expected.PreviewWindows.ToArray(), actual.PreviewWindows.ToArray());
            if (expected.PreviewOperation != null)
            {
                Assert.IsNotNull(actual.PreviewOperation);
                Assert.AreEqual(expected.PreviewOperation.Before.TreeDescription, actual.PreviewOperation.Before.TreeDescription);
                Assert.AreEqual(expected.PreviewOperation.After.TreeDescription, actual.PreviewOperation.After.TreeDescription);
                AssertEquivalentSnapshot(expected.PreviewOperation.Before, actual.PreviewOperation.Before);
                AssertEquivalentSnapshot(expected.PreviewOperation.After, actual.PreviewOperation.After);
            }
        }

        private static void AssertEquivalentSnapshot(MasterSatelliteLayoutSnapshot expected, MasterSatelliteLayoutSnapshot actual)
        {
            Assert.AreEqual(expected.IsActive, actual.IsActive);
            Assert.AreEqual(expected.IsRecovering, actual.IsRecovering);
            Assert.AreEqual(expected.Revision, actual.Revision);
            Assert.AreEqual(expected.MasterSide, actual.MasterSide);
            Assert.AreEqual(expected.RequestedMasterRatio, actual.RequestedMasterRatio);
            Assert.AreEqual(expected.EffectiveMasterRatio, actual.EffectiveMasterRatio);
            Assert.AreEqual(expected.SatelliteOrientation, actual.SatelliteOrientation);
            Assert.AreEqual(expected.WorkArea, actual.WorkArea);
            Assert.AreSame(expected.Master, actual.Master);
            CollectionAssert.AreEqual(expected.Satellites.ToArray(), actual.Satellites.ToArray());
        }

        [TestMethod]
        public void PreviewCacheCounterScenario()
        {
            foreach (int count in new[] { 1, 4, 10 })
            {
                var context = CreateActive(CreateWindows(count));
                var inputs = Enumerable.Range(0, 1000).Select(index =>
                {
                    if (count == 1)
                    {
                        return (context.Windows[0], RectangleFor(context, context.Windows[0]).Center);
                    }
                    var (source, pointer) = PreviewInput(context, (MasterSatelliteDropKind)(index / 250));
                    return (source, new Point(pointer.X + index % 2, pointer.Y + index % 2));
                }).ToArray();
                var liveTree = context.Workspace.GetTree(context.Desktop)!;
                var liveNodes = context.Windows.Select(liveTree.FindNode).ToArray();
                var before = Snapshot(context);
                var plans = new MasterSatelliteDropPlan[inputs.Length];
                int minimumReads = 0;
                foreach (var window in context.Windows)
                {
                    Mock.Get(window).SetupGet(item => item.MinSize)
                        .Returns(() => { minimumReads++; return new Point(0, 0); });
                }
                // Twenty warmups in each logical zone, using the same public
                // production entry point in archived baseline and candidate.
                for (int kind = 0; kind < 4; kind++)
                {
                    for (int warmup = 0; warmup < 20; warmup++)
                    {
                        var input = inputs[kind * 250 + warmup];
                        PreviewAt(context, input.Item1, input.Item2);
                    }
                }
                foreach (var window in context.Windows)
                {
                    Mock.Get(window).Invocations.Clear();
                }
                minimumReads = 0;
                long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int index = 0; index < inputs.Length; index++)
                {
                    var input = inputs[index];
                    plans[index] = PreviewAt(context, input.Item1, input.Item2);
                }
                long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - started;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
                int measuredReads = minimumReads;
                var digest = new StringBuilder();
                for (int index = 0; index < plans.Length; index++)
                {
                    // The independent controller starts empty for every point.
                    // Equivalence work is deliberately outside both timers.
                    var expected = PreviewAt(context with { Controller = new MasterSatelliteDropController() },
                        inputs[index].Item1, inputs[index].Item2);
                    var actual = plans[index];
                    AssertEquivalentPlan(expected, actual);
                    Assert.AreEqual(count > 1, actual.IsAccepted, actual.Message);
                    digest.Append(actual.IsAccepted).Append('|').Append(actual.Kind).Append('|')
                        .Append(Array.IndexOf(context.Windows, actual.Source)).Append('|')
                        .Append(Array.IndexOf(context.Windows, actual.Target)).Append('|')
                        .Append(actual.FromSatelliteIndex).Append('|').Append(actual.ToSatelliteIndex).Append('|')
                        .Append(actual.TargetMasterSide).Append('|').Append(actual.PreviewRectangle).Append('|');
                    if (actual.PreviewOperation is { } operation)
                    {
                        AppendPreviewSnapshot(digest, context.Windows, operation.Before);
                        AppendPreviewSnapshot(digest, context.Windows, operation.After);
                    }
                    digest.AppendLine();
                }
                Assert.AreEqual(before, Snapshot(context));
                CollectionAssert.AreEqual(liveNodes, context.Windows.Select(liveTree.FindNode).ToArray());
                CollectionAssert.AreEqual(context.Windows.Skip(1).ToArray(), GetState(context).Satellites.ToArray());
                Assert.IsTrue(measuredReads >= count * inputs.Length,
                    "Every pointer must retain the first fresh measurement of each window.");
                Console.WriteLine($"PERFCOUNTER preview-cache-{count} allocated-bytes {allocated}");
                Console.WriteLine($"PERFCOUNTER preview-cache-{count} minimum-reads {measuredReads}");
                Console.WriteLine($"PERFCOUNTER preview-cache-{count} elapsed-ticks {ticks}");
                Console.WriteLine($"PERFCOUNTER preview-cache-{count} timestamp-frequency {System.Diagnostics.Stopwatch.Frequency}");
                Console.WriteLine($"PERFCOUNTER preview-cache-{count} pointers {inputs.Length}");
                Console.WriteLine($"PERFCOUNTER preview-cache-{count} accepted-plans {plans.Count(plan => plan.IsAccepted)}");
                Console.WriteLine($"PERFCOUNTER preview-cache-{count} plan-digest {Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(digest.ToString())))}");
            }
        }

        private static void AppendPreviewSnapshot(StringBuilder digest, IWindow[] windows, MasterSatelliteLayoutSnapshot snapshot)
        {
            digest.Append(snapshot.IsActive).Append('|').Append(snapshot.IsRecovering).Append('|')
                .Append(snapshot.Revision).Append('|').Append(snapshot.MasterSide).Append('|')
                .Append(snapshot.RequestedMasterRatio.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(snapshot.EffectiveMasterRatio.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(snapshot.SatelliteOrientation).Append('|').Append(snapshot.WorkArea).Append('|')
                .Append(Array.IndexOf(windows, snapshot.Master)).Append('|')
                .AppendJoin(',', snapshot.Satellites.Select(window => Array.IndexOf(windows, window))).Append('|');
        }
    }
}
