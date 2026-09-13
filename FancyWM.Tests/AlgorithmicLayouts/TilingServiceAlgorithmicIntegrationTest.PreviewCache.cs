using System.Reflection;

using FancyWM.AlgorithmicLayouts;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    public partial class TilingServiceAlgorithmicIntegrationTest
    {
        [DataTestMethod]
        [DataRow("stop")]
        [DataRow("dispose")]
        [DataRow("desktop-switch")]
        [DataRow("desktop-removal")]
        [DataRow("disable")]
        [DataRow("cancel")]
        [DataRow("no-interaction")]
        public void CanonicalPreviewCacheIsReleasedAtServiceLifetimeBoundaries(string boundary)
            => RunOnSta(() =>
            {
                using var fixture = new ServiceFixture(EnabledSettings(true, maxSatellites: 3));
                fixture.Service.Start();
                fixture.DrainDispatcher();
                var master = fixture.AddWindow("Master");
                var satellite = fixture.AddWindow("Satellite");
                var controller = fixture.GetServiceField<MasterSatelliteDropController>("m_masterSatelliteDrops");
                var backend = fixture.GetServiceField<TilingWorkspace>("m_backend");
                var lifecycle = fixture.GetServiceField<MasterSatelliteRuntimeLifecycle>("m_masterSatelliteLifecycle");
                Assert.IsTrue(lifecycle.TryGetState(fixture.Desktop, out var state));
                if (boundary == "cancel")
                {
                    fixture.RaisePositionChangeStart(satellite);
                }
                var tree = backend.GetTree(fixture.Desktop)!;
                tree.Measure();
                tree.Arrange();
                var plan = controller.CreateWindowDropPlan(
                    backend, fixture.Desktop, state, lifecycle.SettingsSnapshot,
                    satellite, tree.FindNode(master)!.ComputedRectangle.Center);
                Assert.IsTrue(plan.IsAccepted, plan.Message);
                var cacheField = typeof(MasterSatelliteDropController).GetField(
                    "m_previewCache", BindingFlags.NonPublic | BindingFlags.Instance)!;
                Assert.IsNotNull(cacheField.GetValue(controller));

                switch (boundary)
                {
                    case "stop": fixture.Service.Stop(); break;
                    case "dispose": fixture.Service.Dispose(); break;
                    case "desktop-switch": fixture.RaiseCurrentDesktopChanged(fixture.TargetDesktop); break;
                    case "desktop-removal":
                        fixture.VirtualDesktopManagerMock.Raise(
                            manager => manager.DesktopRemoved += null,
                            new DesktopChangedEventArgs(fixture.Desktop));
                        break;
                    case "disable": fixture.Publish(EnabledSettings(false)); break;
                    case "cancel": fixture.RaisePositionChangeEnd(satellite); break;
                    case "no-interaction":
                        Assert.IsNull(typeof(TilingService).GetMethod(
                            "GetPreviewRectangle", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(fixture.Service, null));
                        break;
                    default: Assert.Fail(boundary); break;
                }
                Assert.IsNull(cacheField.GetValue(controller),
                    $"The {boundary} path retained the accepted plan and its desktop/window references.");
            });
    }
}
