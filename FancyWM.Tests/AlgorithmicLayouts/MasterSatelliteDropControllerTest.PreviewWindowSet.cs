#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using FancyWM.AlgorithmicLayouts;
using FancyWM.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WinMan;

namespace FancyWM.Tests.AlgorithmicLayouts
{
    public partial class MasterSatelliteDropControllerTest
    {
        [TestMethod]
        public void PreviewWindowSetSamplesCurrentEqualityAndOwnsEachResult()
        {
            var windows = CreateWindows(2);
            int[] equalityKeys = [1, 2];
            for (int i = 0; i < windows.Length; i++)
            {
                int index = i;
                Mock.Get(windows[i]).Setup(w => w.GetHashCode()).Returns(() => equalityKeys[index]);
                Mock.Get(windows[i]).Setup(w => w.Equals(It.IsAny<IWindow>())).Returns<IWindow>(other =>
                {
                    int otherIndex = Array.FindIndex(windows, item => ReferenceEquals(item, other));
                    return otherIndex >= 0 && equalityKeys[index] == equalityKeys[otherIndex];
                });
            }
            var plan = new MasterSatelliteDropPlan { PreviewWindows = Array.AsReadOnly(windows) };
            var first = plan.CreatePreviewWindowSet();
            Assert.AreEqual(2, first.Count);
            equalityKeys[1] = equalityKeys[0];
            var merged = plan.CreatePreviewWindowSet();
            Assert.AreEqual(1, merged.Count);
            Assert.AreSame(windows[0], merged.Single());
            Assert.AreEqual(2, first.Count, "A later conversion must not mutate a previously published set.");
            equalityKeys = [8, 9];
            var split = plan.CreatePreviewWindowSet();
            Assert.AreEqual(2, split.Count);
            Assert.IsTrue(windows.All(split.Contains), "A reused plan must not reuse stale hash buckets.");
            split.Clear();
            CollectionAssert.AreEqual(windows, plan.PreviewWindows.ToArray());
            CollectionAssert.AreEqual(windows, plan.CreatePreviewWindowSet().ToArray());
            Assert.AreEqual(0, new MasterSatelliteDropPlan().CreatePreviewWindowSet().Count);
            var single = plan with { PreviewWindows = Array.AsReadOnly(windows.Take(1).ToArray()) };
            Assert.AreSame(windows[0], single.CreatePreviewWindowSet().Single());
        }

        [TestMethod]
        public void PreviewWindowSetAllocationCounterScenario()
        {
            foreach (string mode in new[] { "horizontal", "vertical", "mixed" })
            foreach (var side in new[] { MasterSide.Left, MasterSide.Right })
            {
                var windows = Enumerable.Range(0, 4).Select(i => new PreviewManagedWindow(i + 1)).ToArray();
                var context = CreateManagedPreviewContext(windows);
                var state = GetState(context);
                Assert.IsTrue(context.Workspace.SetMasterSatelliteOrientation(context.Desktop, state, context.Settings,
                    mode == "horizontal" ? SatelliteLayoutOrientation.Horizontal : SatelliteLayoutOrientation.Vertical).Succeeded);
                Assert.IsTrue(context.Workspace.SetMasterSatelliteSide(context.Desktop, state, context.Settings, side).Succeeded);
                Assert.IsTrue(context.Workspace.SetMixedSatellites(context.Desktop, state, context.Settings, mode == "mixed").Succeeded);
                var before = Snapshot(context);
                foreach (string operation in new[] { "satellite", "promotion", "master-side" })
                {
                    IWindow source = operation == "master-side" ? state.Master! : state.Satellites[0];
                    IWindow target = operation == "promotion" ? state.Master! : state.Satellites[2];
                    var plan = PlanAtWindow(context, source, target);
                    Assert.IsTrue(plan.IsAccepted, plan.Message);
                    Assert.AreSame(plan, PlanAtWindow(context, source, target));
                    // This is the exact production conversion before the change.
                    Func<HashSet<IWindow>> baseline = () => plan.PreviewWindows.ToHashSet();
                    Func<HashSet<IWindow>> candidate = plan.CreatePreviewWindowSet;
                    for (int i = 0; i < 32; i++) { baseline(); candidate(); }
                    long beforeBytes = 0, afterBytes = 0;
                    for (int pair = 0; pair < 3; pair++)
                    {
                        if (pair % 2 == 0) { beforeBytes += Measure(baseline); afterBytes += Measure(candidate); }
                        else { afterBytes += Measure(candidate); beforeBytes += Measure(baseline); }
                    }
                    CollectionAssert.AreEqual(baseline().ToArray(), candidate().ToArray());
                    Assert.IsTrue(afterBytes < beforeBytes,
                        $"Indexed conversion did not reduce allocated bytes: {beforeBytes} -> {afterBytes}.");
                    Console.WriteLine($"PERFCOUNTER preview-window-set-{mode}-{side}-{operation} conversions=3000 members={plan.PreviewWindows.Count} before-bytes={beforeBytes} after-bytes={afterBytes}");
                    long Measure(Func<HashSet<IWindow>> create)
                    {
                        int members = 0;
                        long start = GC.GetAllocatedBytesForCurrentThread();
                        for (int i = 0; i < 1000; i++) members += create().Count;
                        long bytes = GC.GetAllocatedBytesForCurrentThread() - start;
                        Assert.AreEqual(1000 * baseline().Count, members);
                        return bytes;
                    }
                }
                Assert.AreEqual(before, Snapshot(context));
                AssertInvariant(context);
            }
        }
    }
}
