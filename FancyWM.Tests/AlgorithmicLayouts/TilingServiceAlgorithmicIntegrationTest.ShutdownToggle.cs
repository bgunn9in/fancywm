#nullable enable

using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    public partial class TilingServiceAlgorithmicIntegrationTest
    {
        [TestMethod]
        public void ShutdownCancelsDeferredDesktopToggleBeforeAnyLateWindowStateChange()
            => RunOnSta(() =>
            {
                using var fixture = new ServiceFixture(EnabledSettings(false) with { AnimateWindowMovement = false });
                var window = fixture.AddWindow("Deferred toggle owner");
                fixture.Service.Start();
                fixture.DrainMouseLayoutPipeline();
                var pending = new TaskCompletionSource();
                TimeSpan? duration = null;
                CancellationToken cancellation = default;
                typeof(TilingService).GetField("m_layoutDelay", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .SetValue(fixture.Service, (Func<TimeSpan, CancellationToken, Task>)((delay, token) =>
                    {
                        duration = delay;
                        cancellation = token;
                        return pending.Task;
                    }));
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(fixture.Service.ToggleDesktop));
                fixture.DrainDispatcher();
                Assert.AreEqual(TimeSpan.FromMilliseconds(50), duration);
                var prepared = fixture.Service.PrepareForShutdownAsync();
                fixture.DrainMouseLayoutPipeline();
                prepared.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                fixture.Service.Dispose();
                int stateWrites = Mock.Get(window).Invocations.Count(call => call.Method.Name == nameof(IWindow.SetState));
                pending.SetResult();
                fixture.DrainDispatcher();
                Assert.AreEqual(stateWrites, Mock.Get(window).Invocations.Count(call => call.Method.Name == nameof(IWindow.SetState)),
                    "The delayed toggle cannot minimize user windows after the shutdown owner releases its workspace.");
                Assert.AreEqual(WindowState.Restored, window.State);
                Assert.IsTrue(cancellation.IsCancellationRequested);
            });

        [TestMethod]
        public void PreparedServiceRejectsDesktopToggleBeforeReadingOrChangingWindows()
            => RunOnSta(() =>
            {
                using var fixture = new ServiceFixture(EnabledSettings(false));
                var window = fixture.AddWindow("Rejected late toggle");
                typeof(TilingService).GetField("m_layoutDelay", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .SetValue(fixture.Service, (Func<TimeSpan, CancellationToken, Task>)((_, _) => Task.CompletedTask));
                fixture.Service.PrepareForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                int stateWrites = Mock.Get(window).Invocations.Count(call => call.Method.Name == nameof(IWindow.SetState));
                fixture.Service.ToggleDesktop();
                fixture.DrainDispatcher();
                Assert.AreEqual(stateWrites, Mock.Get(window).Invocations.Count(call => call.Method.Name == nameof(IWindow.SetState)));
                Assert.IsFalse(fixture.Service.Active);
            });
    }
}
