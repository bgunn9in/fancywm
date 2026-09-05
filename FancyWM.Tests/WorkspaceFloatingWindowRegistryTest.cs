#nullable enable

using System;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests
{
    [TestClass]
    public class WorkspaceFloatingWindowRegistryTest
    {
        [TestMethod]
        public void FloatingOwnershipFollowsStableHandleAcrossWrappers()
        {
            var registry = new WorkspaceFloatingWindowRegistry();
            var original = CreateWindow(101);
            var replacement = CreateWindow(101);

            Assert.IsTrue(registry.Add(original));
            Assert.IsTrue(registry.Contains(replacement));
            Assert.IsTrue(registry.Remove(replacement));
            Assert.IsFalse(registry.Contains(original));
        }

        [TestMethod]
        public void StableHandleCanBeRemovedAfterWrapperBecomesInvalid()
        {
            var registry = new WorkspaceFloatingWindowRegistry();
            var window = CreateWindow(202);

            Assert.IsTrue(registry.Add(window));
            Assert.IsTrue(registry.Remove(new IntPtr(202)));
            Assert.IsFalse(registry.Contains(new IntPtr(202)));
        }

        [TestMethod]
        public void DeadHiddenOwnerDoesNotPoisonReusedHandle()
        {
            var registry = new WorkspaceFloatingWindowRegistry();
            bool ownerAlive = true;
            var owner = CreateWindow(252, () => ownerAlive);
            var reusedHandleWindow = CreateWindow(252);

            Assert.IsTrue(registry.Add(owner));
            ownerAlive = false;

            Assert.IsFalse(registry.Contains(reusedHandleWindow));
            Assert.IsTrue(registry.Add(reusedHandleWindow));
            Assert.IsTrue(registry.Contains(reusedHandleWindow));
        }

        [TestMethod]
        public void ConcurrentDisplayParticipantsShareOneAtomicEntry()
        {
            var registry = new WorkspaceFloatingWindowRegistry();
            var first = CreateWindow(303);
            var second = CreateWindow(303);

            Parallel.For(0, 1_000, index =>
            {
                if ((index & 1) == 0)
                {
                    registry.Add(first);
                }
                else
                {
                    registry.Contains(second);
                }
            });

            Assert.IsTrue(registry.Contains(second));
            Assert.IsTrue(registry.Remove(second));
            Assert.IsFalse(registry.Contains(first));
        }

        private static IWindow CreateWindow(
            int handle,
            Func<bool>? isAlive = null)
        {
            var window = new Mock<IWindow>(MockBehavior.Loose);
            window.SetupGet(item => item.Handle).Returns(new IntPtr(handle));
            window.SetupGet(item => item.IsAlive).Returns(
                isAlive ?? (() => true));
            return window.Object;
        }
    }
}
