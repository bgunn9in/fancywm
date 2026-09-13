
using Microsoft.VisualStudio.TestTools.UnitTesting;

using FancyWM.Utilities;
using System.Threading;
using System.Threading.Tasks;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class LowLevelKeyboardHookTest
    {
        [TestMethod]
        public async Task TestNewDispose()
        {
            using var published = new ManualResetEventSlim();
            using var releaseWorker = new ManualResetEventSlim();
            var llkbh = new LowLevelKeyboardHook((_, lifetime) =>
            {
                Assert.IsTrue(lifetime.PublishQueue(919));
                published.Set();
                releaseWorker.Wait();
            }, threadId =>
            {
                Assert.AreEqual((uint)919, threadId);
                releaseWorker.Set();
                return true;
            });
            try
            {
                Assert.IsTrue(published.Wait(System.TimeSpan.FromSeconds(5)));
                llkbh.Dispose();
                llkbh.Dispose();
            }
            finally
            {
                releaseWorker.Set();
                llkbh.Dispose();
                await llkbh.Completion.WaitAsync(System.TimeSpan.FromSeconds(5));
                Assert.IsTrue(llkbh.WorkerThread.Join(System.TimeSpan.FromSeconds(5)));
            }
        }
    }
}
