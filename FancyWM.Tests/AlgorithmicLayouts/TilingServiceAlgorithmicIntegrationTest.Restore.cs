#nullable enable

using System.Linq;
using FancyWM.Layouts.Tiling;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    public partial class TilingServiceAlgorithmicIntegrationTest
    {
        [DataTestMethod]
        [DataRow(false, WindowState.Minimized, true)]
        [DataRow(false, WindowState.Maximized, true)]
        [DataRow(false, WindowState.Minimized, false)]
        [DataRow(false, WindowState.Maximized, false)]
        [DataRow(true, WindowState.Minimized, true)]
        [DataRow(true, WindowState.Maximized, true)]
        [DataRow(true, WindowState.Minimized, false)]
        [DataRow(true, WindowState.Maximized, false)]
        public void RestoreWithoutFocusPublishesNativeGeometry(
            bool algorithmic, WindowState previousState, bool saveLocation)
            => RunOnSta(() =>
            {
                using var fixture = new ServiceFixture(EnabledSettings(algorithmic) with
                {
                    AnimateWindowMovement = false,
                    DelayReposition = false
                });
                var restored = fixture.CreateWindow("Restored without activation");
                if (!saveLocation) restored.SetState(previousState);
                fixture.AddWindow(restored);
                var survivor = fixture.AddWindow("Keeps focus");
                fixture.Focus(survivor);
                fixture.Service.Start();
                fixture.DrainMouseLayoutPipeline();

                if (saveLocation)
                {
                    restored.SetState(previousState);
                    Mock.Get(restored).Raise(window => window.StateChanged += null,
                        new WindowStateChangedEventArgs(restored, previousState, WindowState.Restored));
                    fixture.DrainMouseLayoutPipeline();
                }
                var backend = GetBackend(fixture);
                Assert.IsFalse(backend.HasWindow(restored));
                var survivorBefore = survivor.Position;
                int overlaysBefore = fixture.Overlay.UpdateOverlayCount;

                restored.SetState(WindowState.Restored);
                Mock.Get(restored).Raise(window => window.StateChanged += null,
                    new WindowStateChangedEventArgs(restored, WindowState.Restored, previousState));
                fixture.DrainMouseLayoutPipeline();

                Assert.IsTrue(backend.HasWindow(restored));
                Assert.IsTrue(fixture.Overlay.UpdateOverlayCount > overlaysBefore,
                    "Restoration must publish a layout without a focus or unrelated settings event.");
                var tree = backend.GetTree(fixture.Desktop)!;
                CollectionAssert.AreEquivalent(new[] { restored, survivor },
                    fixture.Overlay.LastSnapshot.OfType<WindowNode>()
                        .Select(node => node.WindowReference).ToArray());
                foreach (var window in new[] { restored, survivor })
                    Assert.AreEqual(tree.FindNode(window)!.ComputedRectangle, window.Position,
                        "Every surviving native window must receive the restored layout geometry.");
                Assert.AreNotEqual(survivorBefore, survivor.Position);
                Assert.AreSame(survivor, survivor.Workspace.FocusedWindow);
                Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
                Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            });
    }
}
