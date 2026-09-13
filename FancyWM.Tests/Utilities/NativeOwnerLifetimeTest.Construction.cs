#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using FancyWM.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities;

public partial class NativeOwnerLifetimeTest
{
    [TestMethod]
    public Task OverlayNativeConstructionPositionFailure() => Construction(nameof(OverlayNativeConstructionPositionFailure), "position");
    [TestMethod]
    public Task OverlayNativeConstructionDisplaySubscriptionFailure() => Construction(nameof(OverlayNativeConstructionDisplaySubscriptionFailure), "scale-add");
    [TestMethod]
    public Task OverlayNativeConstructionSecondSurfaceFailure() => Construction(nameof(OverlayNativeConstructionSecondSurfaceFailure), "scale-second-add");
    [TestMethod]
    public Task OverlayNativeConstructionWorkspaceSubscriptionFailure() => Construction(nameof(OverlayNativeConstructionWorkspaceSubscriptionFailure), "cursor-add");
    [TestMethod]
    public Task OverlayNativeConstructionPreservesErrorDuringRollbackFailure() => Construction(nameof(OverlayNativeConstructionPreservesErrorDuringRollbackFailure), "rollback-error");

    private Task Construction(string name, string mode) => Isolated(name, () =>
    {
            using var owners = new DisplayOwners();
            Window[] before = Application.Current.Windows.Cast<Window>().ToArray();
            var expected = new InvalidOperationException("owned overlay construction " + mode);
            var supplemental = new InvalidOperationException("owned rollback release failure");
            owners.ConstructionBoundary = mode == "rollback-error" ? "cursor-add" : mode; owners.Failure = expected;
            if (mode == "rollback-error") { owners.FailureEvent = "area"; owners.RemovalFailure = supplemental; }
            try
            {
                var actual = Assert.ThrowsException<InvalidOperationException>(() => new OverlayHost(owners.Display));
                Assert.AreSame(expected, actual);
                var surviving = Application.Current.Windows.Cast<Window>().Except(before).ToArray();
                int native = surviving.Count(w => IsWindow(new WindowInteropHelper(w).Handle));
                Row(new { scenario = "overlay-construction", mode, actualException = actual.Message,
                    acquiredHwnds = owners.AcquiredHandles.Select(h => h.ToInt64()).ToArray(),
                    survivingHwnds = native, survivingWindows = surviving.Length, subscriptions = Settings.Active });
                Assert.IsTrue(owners.AcquiredHandles.Length > 0, "The failure must follow real native HWND acquisition.");
                Assert.AreEqual(0, native, "Failed production constructor retained an acquired HWND.");
                Assert.AreEqual(0, surviving.Length);
                if (mode == "rollback-error")
                {
                    var errors = (AggregateException)expected.Data["OverlayWindow.OnClosedExceptions"]!;
                    Assert.IsTrue(errors.Flatten().InnerExceptions.Contains(supplemental));
                }
                owners.Late(); Settings.Late(); Drain();
                owners.AssertReleased(); Assert.AreEqual(0, Settings.Active); Settings.Clear();
            }
            finally
            {
                // Clean only the windows acquired by this failed constructor after
                // recording/asserting the original behavior. This is not PASS evidence.
                Observe(mode == "rollback-error" ? supplemental : null, () =>
                {
                    foreach (var window in Application.Current.Windows.Cast<Window>().Except(before).ToArray())
                    {
                        window.GetType().GetProperty("AllowClose")?.SetValue(window, true);
                        window.Close();
                    }
                });
                Drain();
            }
    });
}
