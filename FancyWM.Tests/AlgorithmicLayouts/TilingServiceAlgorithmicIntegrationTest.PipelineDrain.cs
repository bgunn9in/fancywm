#nullable enable

using System;
using System.Reflection;
using System.Windows.Threading;

using FancyWM.Utilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    public partial class TilingServiceAlgorithmicIntegrationTest
    {
        [TestMethod]
        public void MousePipelineDrainWaitsForOwnedCompletionBeyondThreeDispatcherTurns()
            => RunOnSta(() =>
            {
                using var fixture = new ServiceFixture(EnabledSettings(true));
                fixture.Service.Start();
                fixture.DrainDispatcher();
                var frozen = fixture.GetServiceField<Counter>("m_frozen");
                Assert.IsFalse(frozen.IsPositive(), "The empty setup must have finished its initial layout.");
                int initialPasses = fixture.Overlay.UpdateOverlayCount;
                frozen.Increment();
                InvokeServicePipelineMethod(fixture, "InvalidateLayout");
                Assert.IsTrue(fixture.LayoutInvalidated);

                const int completionTurn = 8;
                int turns = 0;
                bool released = false;
                var dispatcher = Dispatcher.CurrentDispatcher;
                DispatcherOperation? pending = null;
                void CompleteAfterControlledTurns()
                {
                    turns++;
                    if (turns == completionTurn)
                    {
                        released = true;
                        InvokeServicePipelineMethod(fixture, "Unfreeze");
                    }
                    else
                    {
                        pending = dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                            new Action(CompleteAfterControlledTurns));
                    }
                }

                try
                {
                    pending = dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                        new Action(CompleteAfterControlledTurns));
                    fixture.DrainMouseLayoutPipeline();

                    Assert.AreEqual(completionTurn, turns,
                        "A fixed number of dispatcher turns is not completion of the owned layout pipeline.");
                    Assert.IsTrue(released);
                    Assert.IsFalse(frozen.IsPositive());
                    Assert.IsFalse(fixture.LayoutInvalidated);
                    Assert.AreEqual(initialPasses + 1, fixture.Overlay.UpdateOverlayCount,
                        "The pending invalidation must finish exactly one real layout after its owner unfreezes.");
                }
                finally
                {
                    pending?.Abort();
                    if (!released) { InvokeServicePipelineMethod(fixture, "Unfreeze"); }
                }
            });

        private static void InvokeServicePipelineMethod(ServiceFixture fixture, string name)
            => typeof(TilingService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(fixture.Service, null);
    }
}
