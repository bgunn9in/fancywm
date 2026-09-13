using FancyWM.Tests.AlgorithmicLayouts;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    public partial class TilingServiceAlgorithmicIntegrationTest
    {
        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void DiscoveryUsesStableWindowSnapshotWhenStateReadMutatesTrackedWindows(
            bool enabled)
        {
            using var fixture = new ServiceFixture(EnabledSettings(enabled));
            fixture.DrainDispatcher();
            var initiallyTracked = fixture.CreateWindow(
                "Initially tracked",
                canResize: false);
            var addedDuringDiscovery = fixture.CreateWindow(
                "Added during discovery",
                canResize: false);
            fixture.AddWindow(initiallyTracked);

            int initiallyTrackedStateReads = 0;
            int addedDuringDiscoveryStateReads = 0;
            bool mutated = false;
            Mock.Get(addedDuringDiscovery)
                .SetupGet(window => window.State)
                .Returns(() =>
                {
                    addedDuringDiscoveryStateReads++;
                    return WindowState.Restored;
                });
            Mock.Get(initiallyTracked)
                .SetupGet(window => window.State)
                .Returns(() =>
                {
                    initiallyTrackedStateReads++;
                    if (!mutated)
                    {
                        mutated = true;
                        fixture.AddWindow(addedDuringDiscovery);
                        // Do not count synchronous WindowAdded/dispatcher setup as
                        // work performed by the discovery pass.
                        addedDuringDiscoveryStateReads = 0;
                    }
                    return WindowState.Restored;
                });

            Assert.IsFalse(fixture.Service.DiscoverWindows());
            Assert.IsTrue(mutated);
            Assert.AreEqual(1, initiallyTrackedStateReads);
            Assert.AreEqual(0, addedDuringDiscoveryStateReads);

            Assert.IsFalse(fixture.Service.DiscoverWindows());
            Assert.AreEqual(2, initiallyTrackedStateReads);
            Assert.AreEqual(1, addedDuringDiscoveryStateReads);
            Assert.IsFalse(GetBackend(fixture).HasWindow(initiallyTracked));
            Assert.IsFalse(GetBackend(fixture).HasWindow(addedDuringDiscovery));
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void NextDiscoveryReconcilesManualDesktopMoveWithoutAWindowEvent(bool enabled)
        {
            using var fixture = new ServiceFixture(EnabledSettings(enabled), includeSecondDesktop: true);
            fixture.DrainDispatcher();
            var anchor = fixture.AddWindow("Source anchor");
            var moving = fixture.AddWindow("Moving without event");
            var target = fixture.AddWindow("Target anchor", fixture.TargetDesktop);
            fixture.DrainDispatcher();
            var backend = GetBackend(fixture);
            var originalPosition = backend.GetOriginalPosition(moving);
            Assert.IsNotNull(backend.GetTree(fixture.Desktop)!.FindNode(moving));
            Assert.IsNull(backend.GetTree(fixture.TargetDesktop)!.FindNode(moving));

            fixture.SetWindowDesktop(moving, fixture.TargetDesktop);
            Assert.IsTrue(fixture.Service.DiscoverWindows());

            Assert.IsNull(backend.GetTree(fixture.Desktop)!.FindNode(moving));
            Assert.AreSame(moving, backend.GetTree(fixture.TargetDesktop)!.FindNode(moving)!.WindowReference);
            Assert.IsNotNull(backend.GetTree(fixture.Desktop)!.FindNode(anchor));
            Assert.IsNotNull(backend.GetTree(fixture.TargetDesktop)!.FindNode(target));
            Assert.AreEqual(originalPosition, backend.GetOriginalPosition(moving));
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            fixture.TargetDesktopMock.Verify(desktop => desktop.MoveWindow(It.IsAny<IWindow>()), Times.Never);
            Assert.IsFalse(fixture.Service.DiscoverWindows(), "A repeated unchanged pass must not remigrate the window.");
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void DiscoverySnapshotIsolatedFromProviderArrayMutationDuringOwnershipProbe(bool enabled)
        {
            using var fixture = new ServiceFixture(EnabledSettings(enabled), includeSecondDesktop: true);
            fixture.DrainDispatcher();
            var window = fixture.CreateWindow("Unregistered target window", canResize: false);
            fixture.AddWindow(window, fixture.TargetDesktop);
            fixture.DrainDispatcher();
            var backend = GetBackend(fixture);
            Assert.IsFalse(backend.HasWindow(window));

            IVirtualDesktop[] supplied = { fixture.Desktop, fixture.TargetDesktop };
            fixture.VirtualDesktopManagerMock.SetupGet(manager => manager.Desktops).Returns(supplied);
            bool mutated = false;
            fixture.DesktopMock.SetupGet(desktop => desktop.IsAlive).Returns(() =>
            {
                mutated = true;
                supplied[1] = fixture.Desktop;
                return true;
            });
            fixture.SetCanResize(window, canResize: true);

            Assert.IsTrue(fixture.Service.DiscoverWindows());

            Assert.IsTrue(mutated);
            Assert.AreSame(fixture.Desktop, supplied[1]);
            Assert.IsNull(backend.GetTree(fixture.Desktop)!.FindNode(window));
            Assert.AreSame(window, backend.GetTree(fixture.TargetDesktop)!.FindNode(window)!.WindowReference);
            Assert.AreEqual(0, fixture.Coordinator.ActiveTransferCount);
            Assert.AreEqual(0, fixture.Coordinator.ReservationCount);
            fixture.TargetDesktopMock.Verify(desktop => desktop.MoveWindow(It.IsAny<IWindow>()), Times.Never);
        }
    }
}
