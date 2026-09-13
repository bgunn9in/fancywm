using System;
using System.Collections.Generic;
using System.Reactive.Disposables;

using FancyWM.Utilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class OverlayRefreshLoopTest
    {
        [TestMethod]
        public void StopDuringSubscriptionDisposesReturnedTokenAndAllowsRestart()
        {
            int active = 0;
            int updates = 0;
            OverlayRefreshLoop loop = null;
            using (loop = new OverlayRefreshLoop(() =>
            {
                updates++;
                loop.Stop();
            }, callback =>
            {
                active++;
                callback();
                return Disposable.Create(() => active--);
            }))
            {
                for (int cycle = 0; cycle < 100; cycle++)
                {
                    loop.Start();
                    Assert.AreEqual(0, active);
                    Assert.AreEqual(cycle + 1, updates);
                }
            }
        }

        [TestMethod]
        public void SubscriptionFailureCanRetryAndDisposeDuringSubscriptionReleasesToken()
        {
            int attempts = 0;
            int active = 0;
            OverlayRefreshLoop loop = null;
            using (loop = new OverlayRefreshLoop(() => loop.Dispose(), callback =>
            {
                if (++attempts == 1) { throw new InvalidOperationException(); }
                active++;
                callback();
                return Disposable.Create(() => active--);
            }))
            {
                Assert.ThrowsException<InvalidOperationException>(() => loop.Start());
                loop.Start();
                Assert.AreEqual(2, attempts);
                Assert.AreEqual(0, active);
                loop.Start();
                Assert.AreEqual(2, attempts);
            }
        }

        [TestMethod]
        public void RepeatedShowHideAndLateTicksKeepOneSubscription()
        {
            var callbacks = new List<Action>();
            int active = 0;
            int updates = 0;
            using var loop = new OverlayRefreshLoop(() => updates++, callback =>
            {
                active++;
                callbacks.Add(callback);
                return Disposable.Create(() => active--);
            });
            for (int cycle = 0; cycle < 100; cycle++)
            {
                loop.Start();
                loop.Start();
                Assert.AreEqual(1, active);
                callbacks[^1]();
                Assert.AreEqual(cycle + 1, updates);
                loop.Stop();
                callbacks[^1]();
                Assert.AreEqual(cycle + 1, updates);
                Assert.AreEqual(0, active);
            }
            loop.Start();
            foreach (var staleCallback in callbacks.GetRange(0, 100)) { staleCallback(); }
            Assert.AreEqual(100, updates);
            loop.Dispose();
            loop.Dispose();
            loop.Start();
            callbacks[^1]();
            Assert.AreEqual(0, active);
            Assert.AreEqual(100, updates);
        }
    }
}
