using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using FancyWM.Tests.TestUtilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using WinMan;
using FancyWM.Layouts.Tiling;

namespace FancyWM.Layouts.Tests.Tiling
{
    [TestClass]
    public class SplitPanelNodeTest
    {
        private const int MediumWorkAreaWidth = 1920;
        private const int MediumWorkAreaHeight = 1080;
        private static readonly Rectangle MediumWorkArea = new(0, 0, MediumWorkAreaWidth, MediumWorkAreaHeight);

        [TestMethod]
        public void TestOneWindow()
        {
            var desktop = new DesktopTree
            {
                Root = new SplitPanelNode(),
                WorkArea = MediumWorkArea,
            };

            var nodepadNode = new WindowNode(WindowMockFactory.CreateNotepadWindow());
            desktop.Root.Attach(nodepadNode);
            desktop.Measure();
            desktop.Arrange();

            Assert.AreEqual(MediumWorkArea, nodepadNode.ComputedRectangle);
        }

        [TestMethod]
        public void TestTwoWindows()
        {
            var desktop = new DesktopTree
            {
                Root = new SplitPanelNode(),
                WorkArea = MediumWorkArea,
            };

            var nodepadNode = new WindowNode(WindowMockFactory.CreateNotepadWindow());
            desktop.Root.Attach(nodepadNode);
            desktop.Measure();
            desktop.Arrange();
            var explorerNode = new WindowNode(WindowMockFactory.CreateExplorerWindow());
            desktop.Root.Attach(explorerNode);
            desktop.Measure();
            desktop.Arrange();

            Assert.AreEqual(new Rectangle(0, 0, MediumWorkAreaWidth / 2, MediumWorkAreaHeight), nodepadNode.ComputedRectangle);
            Assert.AreEqual(new Rectangle(MediumWorkAreaWidth / 2, 0, MediumWorkAreaWidth, MediumWorkAreaHeight), explorerNode.ComputedRectangle);
        }


        [TestMethod]
        public void TestThreeWindows()
        {
            var desktop = new DesktopTree
            {
                Root = new SplitPanelNode(),
                WorkArea = MediumWorkArea,
            };

            var nodepadNode = new WindowNode(WindowMockFactory.CreateNotepadWindow());
            desktop.Root.Attach(nodepadNode);
            desktop.Measure();
            desktop.Arrange();
            var nodepadNode2 = new WindowNode(WindowMockFactory.CreateNotepadWindow());
            desktop.Root.Attach(nodepadNode2);
            desktop.Measure();
            desktop.Arrange();
            var explorerNode = new WindowNode(WindowMockFactory.CreateExplorerWindow());
            desktop.Root.Attach(explorerNode);
            desktop.Measure();
            desktop.Arrange();

            Assert.AreEqual(new Rectangle(0, 0, MediumWorkAreaWidth / 3, MediumWorkAreaHeight), nodepadNode.ComputedRectangle);
            Assert.AreEqual(new Rectangle(MediumWorkAreaWidth / 3, 0, MediumWorkAreaWidth / 3 * 2, MediumWorkAreaHeight), nodepadNode2.ComputedRectangle);
            Assert.AreEqual(new Rectangle(MediumWorkAreaWidth / 3 * 2, 0, MediumWorkAreaWidth, MediumWorkAreaHeight), explorerNode.ComputedRectangle);
        }

        [TestMethod]
        public void TestFourWindowsOneLarge()
        {
            var desktop = new DesktopTree
            {
                Root = new SplitPanelNode(),
                WorkArea = MediumWorkArea,
            };

            var nodepadNode = new WindowNode(WindowMockFactory.CreateNotepadWindow());
            desktop.Root.Attach(nodepadNode);
            desktop.Measure();
            desktop.Arrange();
            var nodepadNode2 = new WindowNode(WindowMockFactory.CreateNotepadWindow());
            desktop.Root.Attach(nodepadNode2);
            desktop.Measure();
            desktop.Arrange();
            var explorerNode = new WindowNode(WindowMockFactory.CreateExplorerWindow());
            desktop.Root.Attach(explorerNode);
            desktop.Measure();
            desktop.Arrange();
            var discordNode = new WindowNode(WindowMockFactory.CreateDiscordWindow());
            desktop.Root.Attach(discordNode);
            desktop.Measure();
            desktop.Arrange();
        }

        [TestMethod]
        public void TestTwoLargeWindows()
        {
            var desktop = new DesktopTree
            {
                Root = new SplitPanelNode(),
                WorkArea = MediumWorkArea,
            };

            var explorerNode = new WindowNode(WindowMockFactory.CreateExplorerWindow());
            desktop.Root.Attach(explorerNode);
            desktop.Measure();
            desktop.Arrange();
            var discordNode = new WindowNode(WindowMockFactory.CreateDiscordWindow());
            desktop.Root.Attach(discordNode);
            desktop.Measure();
            desktop.Arrange();
            Assert.ThrowsException<UnsatisfiableFlexConstraintsException>(() =>
            {
                var discordNode2 = new WindowNode(WindowMockFactory.CreateDiscordWindow());
                desktop.Root.Attach(discordNode2);
                desktop.Measure();
                desktop.Arrange();
            });
        }

        [TestMethod]
        public void TestWindowGrowsTooLarge()
        {
            var desktop = new DesktopTree
            {
                Root = new SplitPanelNode(),
                WorkArea = MediumWorkArea,
            };

            var explorerNode = new WindowNode(WindowMockFactory.CreateExplorerWindow());
            desktop.Root.Attach(explorerNode);
            desktop.Measure();
            desktop.Arrange();
            var explorerNode2 = new WindowNode(WindowMockFactory.CreateExplorerWindow());
            desktop.Root.Attach(explorerNode2);
            desktop.Measure();
            desktop.Arrange();
            var discordNode = new WindowNode(WindowMockFactory.CreateDiscordWindow());
            desktop.Root.Attach(discordNode);
            desktop.Measure();
            desktop.Arrange();
            // Replace with larger window to simulate the window growing after it was added
            desktop.Root.SetReference(1, new WindowNode(WindowMockFactory.CreateDiscordWindow()));
            Assert.ThrowsException<UnsatisfiableFlexConstraintsException>(() =>
            {
                desktop.Measure();
                desktop.Arrange();
            });
        }

        [TestMethod]
        public void TestChangeOrientationResetsConstraints()
        {
            var panel = new SplitPanelNode { Orientation = PanelOrientation.Horizontal };
            var desktop = new DesktopTree
            {
                Root = panel,
                WorkArea = Rectangle.OffsetAndSize(0, 0, 1000, 600),
            };
            var first = new WindowNode(WindowMockFactory.CreateNotepadWindow());
            var second = new WindowNode(WindowMockFactory.CreateNotepadWindow());
            panel.Attach(first);
            panel.Attach(second);
            desktop.Measure();
            desktop.Arrange();
            Assert.IsTrue(panel.ResizeTo(first, 700, GrowDirection.Both));

            Assert.IsTrue(panel.ChangeOrientation(PanelOrientation.Vertical));
            Assert.AreEqual(0, panel.GetChildConstraints(first).MaxWidth, 0.01);
            Assert.AreEqual(0, panel.GetChildConstraints(second).MaxWidth, 0.01);

            desktop.Measure();
            desktop.Arrange();

            Assert.AreEqual(600, panel.ContainerLength, 0.01);
            Assert.AreEqual(300, panel.GetChildConstraints(first).Width, 0.01);
            Assert.AreEqual(300, panel.GetChildConstraints(second).Width, 0.01);
        }

        [TestMethod]
        public void TestChangeOrientationToCurrentValuePreservesConstraints()
        {
            var panel = new SplitPanelNode { Orientation = PanelOrientation.Horizontal };
            var desktop = new DesktopTree
            {
                Root = panel,
                WorkArea = Rectangle.OffsetAndSize(0, 0, 1000, 600),
            };
            var first = new WindowNode(WindowMockFactory.CreateNotepadWindow());
            var second = new WindowNode(WindowMockFactory.CreateNotepadWindow());
            panel.Attach(first);
            panel.Attach(second);
            desktop.Measure();
            desktop.Arrange();
            Assert.IsTrue(panel.ResizeTo(first, 700, GrowDirection.Both));
            var firstWidth = panel.GetChildConstraints(first).Width;
            var secondWidth = panel.GetChildConstraints(second).Width;

            Assert.IsFalse(panel.ChangeOrientation(PanelOrientation.Horizontal));

            Assert.AreEqual(firstWidth, panel.GetChildConstraints(first).Width, 0.01);
            Assert.AreEqual(secondWidth, panel.GetChildConstraints(second).Width, 0.01);
        }

        [TestMethod]
        public void TestMoveCarriesChildConstraints()
        {
            var panel = new SplitPanelNode { Orientation = PanelOrientation.Horizontal };
            var desktop = new DesktopTree
            {
                Root = panel,
                WorkArea = Rectangle.OffsetAndSize(0, 0, 1000, 600),
            };
            var first = new WindowNode(WindowMockFactory.CreateNotepadWindow());
            var second = new WindowNode(WindowMockFactory.CreateNotepadWindow());
            panel.Attach(first);
            panel.Attach(second);
            desktop.Measure();
            desktop.Arrange();
            Assert.IsTrue(panel.ResizeTo(first, 700, GrowDirection.Both));
            var firstWidth = panel.GetChildConstraints(first).Width;
            var secondWidth = panel.GetChildConstraints(second).Width;

            panel.Move(0, 1);

            Assert.AreSame(second, panel.Children[0]);
            Assert.AreSame(first, panel.Children[1]);
            Assert.AreEqual(secondWidth, panel.GetChildConstraints(second).Width, 0.01);
            Assert.AreEqual(firstWidth, panel.GetChildConstraints(first).Width, 0.01);
        }

        [TestMethod]
        public void TestContainerLengthAccountsForRootSpacing()
        {
            var panel = new SplitPanelNode
            {
                Orientation = PanelOrientation.Horizontal,
                Spacing = 10,
            };
            var desktop = new DesktopTree
            {
                Root = panel,
                WorkArea = Rectangle.OffsetAndSize(0, 0, 1000, 600),
            };
            panel.Attach(new WindowNode(WindowMockFactory.CreateNotepadWindow()));

            desktop.Measure();
            desktop.Arrange();

            Assert.AreEqual(990, panel.ContainerLength, 0.01);
        }
    }
}
