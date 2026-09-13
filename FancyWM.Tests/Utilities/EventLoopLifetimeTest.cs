using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinMan.Windows;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class EventLoopLifetimeTest
    {
        private static readonly Type LoopType = typeof(Win32Workspace).Assembly.GetType("WinMan.Windows.Utilities.EventLoop", true);
        private static readonly MethodInfo Enqueue = LoopType.GetMethod("InvokeAsync");
        private static readonly MethodInfo Run = LoopType.GetMethod("Run");
        private static readonly MethodInfo Shutdown = LoopType.GetMethod("Shutdown");

        [TestMethod]
        public void IdleLoopReleasesCompletedCallbackOwnerBeforeShutdown()
        {
            var loop = Activator.CreateInstance(LoopType, true);
            Exception failure = null;
            var worker = new Thread(() => { try { Run.Invoke(loop, null); } catch (Exception e) { failure = e; } });
            using var completed = new ManualResetEventSlim();
            worker.Start();
            try
            {
                var weak = EnqueueOwnedPayload(loop, completed);
                Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(5)));
                Assert.IsTrue(SpinWait.SpinUntil(() => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)));
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Assert.IsFalse(weak.IsAlive, "A live idle loop must release the completed callback payload.");
                Assert.IsTrue(worker.IsAlive, "Process/worker exit cannot prove live-loop retention.");
            }
            finally
            {
                Shutdown.Invoke(loop, null);
                Assert.IsTrue(worker.Join(TimeSpan.FromSeconds(5)));
            }
            Assert.IsNull(failure);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference EnqueueOwnedPayload(object loop, ManualResetEventSlim completed)
        {
            var owner = new Payload(completed);
            Enqueue.Invoke(loop, new object[] { (Action)owner.Execute });
            return new WeakReference(owner);
        }

        private sealed class Payload(ManualResetEventSlim completed)
        {
            public void Execute() => completed.Set();
        }

        [TestMethod]
        public void LoopPreservesFifoReentrancyOriginalFailureAndAcceptedShutdownWork()
        {
            var loop = Activator.CreateInstance(LoopType, true);
            var order = new List<int>();
            var original = new InvalidOperationException("owned callback failure");
            Exception reported = null, workerFailure = null;
            int workerId = 0, callbackThread = 0;
            EventHandler<UnhandledExceptionEventArgs> handler = (_, e) => { reported = (Exception)e.ExceptionObject; callbackThread = Environment.CurrentManagedThreadId; };
            LoopType.GetEvent("UnhandledException").AddEventHandler(loop, handler);
            void post(Action action) => Enqueue.Invoke(loop, new object[] { action });
            post(() => { order.Add(1); post(() => { order.Add(4); Shutdown.Invoke(loop, null); }); });
            post(() => { order.Add(2); throw original; });
            post(() => order.Add(3));
            var worker = new Thread(() => { workerId = Environment.CurrentManagedThreadId; try { Run.Invoke(loop, null); } catch (Exception e) { workerFailure = e; } });
            worker.Start();
            try { Assert.IsTrue(worker.Join(TimeSpan.FromSeconds(5))); }
            finally { if (worker.IsAlive) { Shutdown.Invoke(loop, null); worker.Join(TimeSpan.FromSeconds(5)); } }
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, order);
            Assert.AreSame(original, reported);
            Assert.AreEqual(workerId, callbackThread);
            Assert.IsNull(workerFailure);
        }
    }
}
