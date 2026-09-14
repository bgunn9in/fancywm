#nullable enable
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using FancyWM.AlgorithmicLayouts;
using FancyWM.Layouts.Tiling;
using FancyWM.Models;
using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    public partial class TilingServiceAlgorithmicIntegrationTest
    {
        [DataTestMethod]
        [DataRow(MasterSide.Left, SatelliteLayoutOrientation.Horizontal, false)]
        [DataRow(MasterSide.Right, SatelliteLayoutOrientation.Horizontal, false)]
        [DataRow(MasterSide.Left, SatelliteLayoutOrientation.Vertical, false)]
        [DataRow(MasterSide.Right, SatelliteLayoutOrientation.Vertical, false)]
        [DataRow(MasterSide.Left, SatelliteLayoutOrientation.Vertical, true)]
        [DataRow(MasterSide.Right, SatelliteLayoutOrientation.Vertical, true)]
        public void CommandHandlerAndDirectHotkeyRoutePreserveMasterAndSlots(
            MasterSide side, SatelliteLayoutOrientation orientation, bool mixed)
            => RunOnSta(() =>
            {
                using var fixture = new ServiceFixture(MixedSettings(mixed, side, orientation));
                fixture.Service.Start();
                var windows = Enumerable.Range(0, 4).Select(i => fixture.AddWindow($"Window {i}")).ToArray();
                fixture.DrainMouseLayoutPipeline();
                var backend = GetBackend(fixture);
                var state = MixedState(fixture);
                // Only construct the action dispatcher shell; no WPF window is shown.
                // Both the normal command entry and production direct-hotkey wrapper
                // call the actual MainWindow.ExecuteAction against the real service.
                var shell = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
                typeof(MainWindow).GetField("m_tiling", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(shell, fixture.Service);
                var execute = (MainWindow.DirectHotkeyActionExecutor)typeof(MainWindow)
                    .GetMethod("ExecuteAction", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .CreateDelegate(typeof(MainWindow.DirectHotkeyActionExecutor), shell);
                using var lifetime = new MainWindowLifetime(_ => { });
                void Invoke(BindableAction action, bool direct)
                {
                    if (direct)
                    {
                        MainWindow.HandleDirectHotkeyPressedAsync(lifetime, action, execute,
                            (failure, _) => Task.FromException(failure)).GetAwaiter().GetResult();
                    }
                    else
                    {
                        string? friendlyName = null;
                        execute(action, ref friendlyName);
                    }
                    fixture.DrainMouseLayoutPipeline();
                }
                if (mixed)
                {
                    FocusMixedWindow(fixture, windows[1]);
                    Assert.IsTrue(fixture.Service.CanToggleFocusedSatelliteSlot());
                    Invoke(BindableAction.ToggleFocusedSatelliteSlot, true);
                    CollectionAssert.AreEqual(new[] { windows[3], windows[2], windows[1] }, state.Satellites.ToArray());
                    Invoke(BindableAction.MoveUp, false);
                    CollectionAssert.AreEqual(windows.Skip(1).ToArray(), state.Satellites.ToArray());
                }
                int index = mixed ? 2 : orientation == SatelliteLayoutOrientation.Vertical ? 1
                    : side == MasterSide.Left ? 0 : 2;
                var source = state.Satellites[index];
                var master = state.Master!;
                var before = state.Satellites.ToArray();
                var slot = backend.GetTree(fixture.Desktop)!.FindNode(source)!.ComputedRectangle;
                FocusMixedWindow(fixture, source);
                var towardMaster = side == MasterSide.Left ? TilingDirection.Left : TilingDirection.Right;
                Assert.IsTrue(fixture.Service.CanMoveWindow(towardMaster));
                Invoke(side == MasterSide.Left ? BindableAction.MoveLeft : BindableAction.MoveRight, true);
                Assert.AreSame(source, state.Master);
                before[index] = master;
                CollectionAssert.AreEqual(before, state.Satellites.ToArray());
                Assert.AreEqual(slot, backend.GetTree(fixture.Desktop)!.FindNode(master)!.ComputedRectangle);
                Assert.AreSame(source, fixture.Service.GetFocus());
                Invoke(side == MasterSide.Left ? BindableAction.MoveRight : BindableAction.MoveLeft, false);
                Assert.AreSame(source, state.Master);
                Assert.AreEqual(side == MasterSide.Left ? MasterSide.Right : MasterSide.Left, state.MasterSide);
                CollectionAssert.AreEqual(before, state.Satellites.ToArray());
                Assert.AreSame(source, fixture.Service.GetFocus());
                AssertMixedServiceInvariant(fixture, 4);
            });

        [DataTestMethod]
        [DataRow(MasterSide.Left)]
        [DataRow(MasterSide.Right)]
        public void MixedMouseOverlayPreviewMatchesCommittedSwapAndMasterSide(MasterSide side)
            => RunOnSta(() =>
            {
                using var fixture = new ServiceFixture(MixedSettings(true, side));
                fixture.Service.Start();
                var windows = Enumerable.Range(0, 4).Select(i => fixture.AddWindow($"Mouse {i}")).ToArray();
                fixture.DrainMouseLayoutPipeline();
                var state = MixedState(fixture);
                void Drag(IWindow source, IWindow target)
                {
                    FocusMixedWindow(fixture, source);
                    fixture.SetCursor(fixture.Overlay.GetWindowRectangle(target).Center);
                    fixture.RaisePositionChangeStart(source);
                    var position = source.Position;
                    fixture.RaisePositionChanged(source, Rectangle.OffsetAndSize(
                        position.Left + 15, position.Top + 15, position.Width, position.Height));
                    var preview = fixture.Overlay.PreviewRectangle;
                    Assert.IsNotNull(preview);
                    fixture.RaisePositionChangeEnd(source);
                    Assert.AreEqual(preview, GetBackend(fixture).GetTree(fixture.Desktop)!.FindNode(source)!.ComputedRectangle);
                    Assert.IsNull(fixture.Overlay.PreviewRectangle);
                    Assert.AreSame(source, fixture.Service.GetFocus());
                }
                Drag(windows[3], windows[2]);
                CollectionAssert.AreEqual(new[] { windows[1], windows[3], windows[2] }, state.Satellites.ToArray());
                Drag(windows[0], windows[1]);
                Assert.AreSame(windows[0], state.Master);
                Assert.AreEqual(side == MasterSide.Left ? MasterSide.Right : MasterSide.Left, state.MasterSide);
                Drag(windows[2], windows[0]);
                Assert.AreSame(windows[2], state.Master);
                CollectionAssert.AreEqual(new[] { windows[1], windows[3], windows[0] }, state.Satellites.ToArray());
                AssertMixedServiceInvariant(fixture, 4);
            });

        [DataTestMethod]
        [DataRow(MasterSide.Left)]
        [DataRow(MasterSide.Right)]
        public void MixedSettingsAndWindowLifecycleTransitionsReachRealOverlay(MasterSide side)
            => RunOnSta(() =>
            {
                using var fixture = new ServiceFixture(MixedSettings(false, side));
                fixture.Service.Start();
                var windows = Enumerable.Range(0, 4).Select(i => fixture.AddWindow($"Lifecycle {i}")).ToArray();
                fixture.DrainMouseLayoutPipeline();
                var state = MixedState(fixture);
                Assert.IsFalse(state.IsMixedLayout);
                fixture.Publish(MixedSettings(true, side));
                fixture.DrainMouseLayoutPipeline();
                Assert.IsTrue(state.IsMixedLayout);
                FocusMixedWindow(fixture, windows[1]);
                fixture.Service.Float();
                fixture.DrainMouseLayoutPipeline();
                Assert.IsFalse(state.IsMixedLayout);
                CollectionAssert.AreEqual(new[] { windows[2], windows[3] }, state.Satellites.ToArray());
                fixture.Service.Float();
                fixture.DrainMouseLayoutPipeline();
                Assert.IsTrue(state.IsMixedLayout);
                CollectionAssert.AreEqual(new[] { windows[2], windows[3], windows[1] }, state.Satellites.ToArray());
                windows[3].SetState(WindowState.Minimized);
                Mock.Get(windows[3]).Raise(w => w.StateChanged += null,
                    new WindowStateChangedEventArgs(windows[3], WindowState.Minimized, WindowState.Restored));
                fixture.DrainMouseLayoutPipeline();
                Assert.IsFalse(state.IsMixedLayout);
                Assert.IsFalse(GetBackend(fixture).HasWindow(windows[3]));
                windows[3].SetState(WindowState.Restored);
                Mock.Get(windows[3]).Raise(w => w.StateChanged += null,
                    new WindowStateChangedEventArgs(windows[3], WindowState.Restored, WindowState.Minimized));
                fixture.DrainMouseLayoutPipeline();
                Assert.IsTrue(state.IsMixedLayout);
                CollectionAssert.AreEqual(new[] { windows[2], windows[1], windows[3] }, state.Satellites.ToArray());
                var extra = fixture.AddWindow("Fourth satellite");
                fixture.DrainMouseLayoutPipeline();
                Assert.IsFalse(state.IsMixedLayout);
                AssertMixedServiceInvariant(fixture, 5);
                fixture.SetWindowAlive(extra, false);
                fixture.RaiseWindowDestroyed(extra);
                fixture.RaiseWindowRemoved(extra);
                fixture.DrainMouseLayoutPipeline();
                Assert.IsTrue(state.IsMixedLayout);
                fixture.Publish(MixedSettings(false, side));
                fixture.DrainMouseLayoutPipeline();
                Assert.IsFalse(state.IsMixedLayout);
                AssertMixedServiceInvariant(fixture, 4);
            });

        [TestMethod]
        public void ImpossibleMixedSettingKeepsTreeAndNotifiesThroughService()
            => RunOnSta(() =>
            {
                using var fixture = new ServiceFixture(MixedSettings(false));
                var windows = Enumerable.Range(0, 4).Select(i => fixture.AddWindow($"Wide {i}")).ToArray();
                foreach (var window in windows)
                    fixture.SetMinSize(window, new Point(1200, 50));
                fixture.DrainDispatcher();
                var state = MixedState(fixture);
                long revision = state.Revision;
                var events = new System.Collections.Generic.List<AlgorithmicLayoutEvent>();
                fixture.Service.AlgorithmicLayoutChanged += (_, e) => events.Add(e);
                fixture.Publish(MixedSettings(true));
                Assert.IsFalse(state.UseMixedSatellites);
                Assert.AreEqual(revision, state.Revision);
                Assert.IsTrue(events.Any(e => e.MessageKey == "AlgorithmicLayout.MixedLayoutRejected"));
            });

        [TestMethod]
        public void CapacityShrinkEnablesMixedAfterSurplusLeaves()
            => RunOnSta(() =>
            {
                var initial = MixedSettings(false) with { MasterSatelliteLayout = MixedSettings(false).MasterSatelliteLayout
                    with { OverflowPolicy = MasterSatelliteOverflowPolicy.FloatOnCurrentDesktop } };
                using var fixture = new ServiceFixture(initial);
                var windows = Enumerable.Range(0, 5).Select(i => fixture.AddWindow($"Capacity {i}")).ToArray();
                fixture.Publish(initial with { MasterSatelliteLayout = initial.MasterSatelliteLayout
                    with { UseMixedSatellites = true, MaxSatellites = 3 } });
                fixture.DrainDispatcher();
                var state = MixedState(fixture);
                Assert.IsTrue(state.IsMixedLayout);
                CollectionAssert.AreEqual(windows.Skip(1).Take(3).ToArray(), state.Satellites.ToArray());
                Assert.IsTrue(fixture.Coordinator.FloatingWindows.Contains(windows[4]));
                Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            });

        private static void FocusMixedWindow(ServiceFixture fixture, IWindow window)
        {
            fixture.Focus(window);
            Mock.Get(window).Raise(w => w.GotFocus += null, new WindowFocusChangedEventArgs(window, true));
            fixture.DrainDispatcher();
        }

        private static Settings MixedSettings(bool mixed, MasterSide side = MasterSide.Left,
            SatelliteLayoutOrientation orientation = SatelliteLayoutOrientation.Vertical)
        {
            var settings = EnabledSettings(true, maxSatellites: 4, masterSide: side, orientation: orientation);
            return settings with { AnimateWindowMovement = false, DelayReposition = false,
                MasterSatelliteLayout = settings.MasterSatelliteLayout with { UseMixedSatellites = mixed } };
        }

        private static MasterSatelliteRuntimeState MixedState(ServiceFixture fixture)
        {
            Assert.IsTrue(fixture.Coordinator.TryGet(new LayoutStateKey(fixture.Desktop, fixture.Display), out var state));
            return state;
        }

        private static void AssertMixedServiceInvariant(ServiceFixture fixture, int count)
        {
            var state = MixedState(fixture);
            var lifecycle = fixture.GetServiceField<MasterSatelliteRuntimeLifecycle>("m_masterSatelliteLifecycle");
            var invariant = GetBackend(fixture).ValidateMasterSatelliteLayout(fixture.Desktop, state, lifecycle.SettingsSnapshot);
            Assert.IsTrue(invariant.IsValid, invariant.Description);
            Assert.AreEqual(count, state.Satellites.Count + 1);
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            foreach (var window in state.Satellites.Prepend(state.Master!))
            {
                Assert.IsFalse(fixture.Coordinator.FloatingWindows.Contains(window));
                Assert.AreSame(fixture.Desktop, fixture.GetWindowDesktop(window));
            }
        }
    }
}
