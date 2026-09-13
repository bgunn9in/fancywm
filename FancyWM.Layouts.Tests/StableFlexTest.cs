using System;
using System.Linq;
using System.Reflection;

using FancyWM.Layouts.Tiling;
using FancyWM.Tests.TestUtilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Layouts.Tests
{
    [TestClass]
    public class StableFlexTest
    {
        [TestMethod]
        public void InitializingChildrenRestoresExactConstraintsAfterRedistribution()
        {
            var panel = new SplitPanelNode { Orientation = PanelOrientation.Vertical, Spacing = 6 };
            var tree = new DesktopTree { Root = panel, WorkArea = new Rectangle(0, 0, 3440, 1440) };
            for (int index = 0; index < 4; index++)
            {
                panel.Attach(new WindowNode(WindowMockFactory.CreateNotepadWindow()));
            }
            for (int iteration = 0; iteration < 10; iteration++)
            {
                panel.ResetConstraints();
                tree.Measure();
                tree.Arrange();
                foreach (var child in panel.Children)
                {
                    Assert.AreEqual(6d, panel.GetChildConstraints(child).MinWidth);
                    Assert.AreEqual(32767d, panel.GetChildConstraints(child).MaxWidth);
                }
            }
        }

        [DataTestMethod]
        [DataRow(1)]
        [DataRow(4)]
        [DataRow(10)]
        [DataRow(25)]
        [DataRow(50)]
        public void StableWidthMatchesUncachedResizeAfterDeterministicMutations(int count)
        {
            var random = new Random(5005 + count);
            var flex = new Flex();
            flex.SetContainerWidth(10000);
            for (int index = 0; index < count; index++) { flex.InsertItem(index, 0, 20000); }
            var uncachedResize = typeof(Flex).GetMethod("ResizeContainer", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(uncachedResize);
            for (int iteration = 0; iteration < 200; iteration++)
            {
                int index = random.Next(flex.Count);
                switch (iteration % 5)
                {
                    case 0:
                        flex.UpdateConstraints(index, random.Next(0, 5), 20000);
                        break;
                    case 1:
                        flex.MoveItem(index, random.Next(flex.Count));
                        break;
                    case 2:
                        flex.SetContainerWidth(random.Next(9000, 11000));
                        break;
                    case 3:
                        flex.DistributeItemsEvenly();
                        break;
                    case 4:
                        flex.RemoveItem(index);
                        flex.InsertItem(index, 0, 20000);
                        break;
                }
                var reference = new Flex(flex);
                uncachedResize.Invoke(reference, new object[] { flex.ContainerWidth });
                flex.SetContainerWidth(flex.ContainerWidth);
                CollectionAssert.AreEqual(reference.ToArray(), flex.ToArray(), $"count={count}, iteration={iteration}");
                Assert.AreEqual(reference.ContainerWidth, flex.ContainerWidth);
            }
        }

        [TestMethod]
        public void StablePanelUsesChangedMinimumOrderWeightsAndOrientation()
        {
            var firstWindow = WindowMockFactory.CreateNotepadWindow();
            var first = new WindowNode(firstWindow);
            var second = new WindowNode(WindowMockFactory.CreateNotepadWindow());
            var panel = new SplitPanelNode();
            var tree = new DesktopTree { Root = panel, WorkArea = new Rectangle(0, 0, 1000, 1000) };
            panel.Attach(first);
            panel.Attach(second);
            tree.Measure();
            tree.Arrange();
            Assert.IsTrue(panel.ResizeTo(first, 600, GrowDirection.Both));
            tree.Arrange();
            Assert.AreEqual(600, first.ComputedRectangle.Width);
            panel.Move(0, 1);
            tree.Measure();
            tree.Arrange();
            Assert.AreEqual(new Rectangle(400, 0, 1000, 1000), first.ComputedRectangle);
            Mock.Get(firstWindow).SetupGet(window => window.MinSize).Returns(new Point(700, 100));
            tree.Measure();
            tree.Arrange();
            Assert.AreEqual(new Rectangle(300, 0, 1000, 1000), first.ComputedRectangle);
            Assert.AreEqual(700d, panel.GetChildConstraints(first).MinWidth);
            var clone = (SplitPanelNode)panel.Clone();
            clone.ChangeOrientation(PanelOrientation.Vertical);
            var cloneTree = new DesktopTree { Root = clone, WorkArea = tree.WorkArea };
            cloneTree.Measure();
            cloneTree.Arrange();
            Assert.AreEqual(new Rectangle(300, 0, 1000, 1000), first.ComputedRectangle);
            Assert.AreEqual(PanelOrientation.Horizontal, panel.Orientation);
            Assert.AreEqual(100d, clone.GetChildConstraints(clone.Children[1]).MinWidth);
            Assert.AreEqual(first.GenerationID, clone.Children[1].GenerationID);
            Assert.AreSame(firstWindow, ((WindowNode)clone.Children[1]).WindowReference);
        }

        [TestMethod]
        public void StableWidthPreservesConstraintsWeightsOrderAndCloneIsolation()
        {
            var flex = new Flex();
            flex.SetContainerWidth(1000);
            flex.InsertItem(0, 10, 1000);
            flex.InsertItem(1, 20, 1000);
            flex.ResizeItem(0, 600);
            flex.UpdateConstraints(1, 300, 800);
            flex.MoveItem(0, 1);
            var original = flex.ToArray();
            var clone = new Flex(flex);
            for (int iteration = 0; iteration < 100; iteration++) { flex.SetContainerWidth(1000); }
            CollectionAssert.AreEqual(original, flex.ToArray());
            clone.SetContainerWidth(1500);
            CollectionAssert.AreEqual(original, flex.ToArray());
            flex.UpdateConstraints(0, 450, 800);
            flex.SetContainerWidth(1000);
            Assert.IsTrue(flex[0].Width >= 450);
            Assert.AreEqual(1000, flex.ContainerWidth);
            Assert.ThrowsException<UnsatisfiableFlexConstraintsException>(() => flex.SetContainerWidth(1));
            Assert.AreEqual(1000, flex.ContainerWidth);
        }

        [TestMethod]
        public void RepeatedStableWidthAvoidsConstraintListCopies()
        {
            var flex = new Flex();
            flex.SetContainerWidth(3440);
            for (int index = 0; index < 10; index++) { flex.InsertItem(index, 0, 10000); }
            for (int iteration = 0; iteration < 100; iteration++) { flex.SetContainerWidth(3440); }
            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0; iteration < 1000; iteration++) { flex.SetContainerWidth(3440); }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
#if DEBUG
            const long allocationLimit = 2100000;
#else
            const long allocationLimit = 200000;
#endif
            Assert.IsTrue(allocated < allocationLimit, $"Stable width allocated {allocated} bytes in 1000 calls.");
            Assert.AreEqual(3440, flex.UsedWidth, 0.000001);
        }
    }
}
