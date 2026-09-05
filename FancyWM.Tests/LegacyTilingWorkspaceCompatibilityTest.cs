using System.Linq;

using FancyWM.Layouts.Tiling;
using FancyWM.Tests.AlgorithmicLayouts;
using FancyWM.Tests.TestUtilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using WinMan;

namespace FancyWM.Tests
{
    [TestClass]
    public class LegacyTilingWorkspaceCompatibilityTest
    {
        private readonly VirtualDesktopMockFactory m_desktops = new();
        private readonly UniqueWindowMockFactory m_windows = new();

        [TestMethod]
        public void AutoSplitCountStillLimitsLegacyTopLevelPanelWidth()
        {
            var workspace = CreateWorkspace(out var desktop);
            var first = workspace.RegisterWindow(
                m_windows.Create("first"),
                maxTreeWidth: 2);
            var second = workspace.RegisterWindow(
                m_windows.Create("second"),
                maxTreeWidth: 2);

            var third = workspace.RegisterWindow(
                m_windows.Create("third"),
                maxTreeWidth: 2);

            var root = (SplitPanelNode)workspace.GetTree(desktop)!.Root!;
            Assert.AreEqual(2, root.Children.Count);
            Assert.AreSame(first, root.Children[0]);
            Assert.IsInstanceOfType(root.Children[1], typeof(SplitPanelNode));
            var nested = (SplitPanelNode)root.Children[1];
            Assert.AreNotEqual(root.Orientation, nested.Orientation);
            CollectionAssert.AreEqual(
                new TilingNode[] { second, third },
                nested.Children.ToArray());
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void AutoCollapseStillControlsLegacySingleChildPanelCleanup(
            bool autoCollapse)
        {
            var workspace = CreateWorkspace(out var desktop);
            workspace.AutoCollapse = autoCollapse;
            var first = workspace.RegisterWindow(m_windows.Create("first"));
            workspace.WrapInSplitPanel(first, vertical: true);
            var nested = first.Parent!;
            var secondWindow = m_windows.Create("second");
            workspace.RegisterWindow(secondWindow, nested);

            workspace.UnregisterWindow(secondWindow);

            var root = workspace.GetTree(desktop)!.Root!;
            Assert.AreEqual(1, root.Children.Count);
            if (autoCollapse)
            {
                Assert.AreSame(first, root.Children[0]);
                Assert.AreSame(root, first.Parent);
                Assert.IsNull(nested.Parent);
            }
            else
            {
                Assert.AreSame(nested, root.Children[0]);
                Assert.AreSame(nested, first.Parent);
                CollectionAssert.AreEqual(
                    new TilingNode[] { first },
                    nested.Children.ToArray());
            }
        }

        private TilingWorkspace CreateWorkspace(out IVirtualDesktop desktop)
        {
            var workspace = new TilingWorkspace();
            desktop = m_desktops.CreateVirtualDesktop();
            workspace.RegisterDesktop(
                desktop,
                Rectangle.OffsetAndSize(0, 0, 1920, 1080),
                PanelOrientation.Horizontal);
            return workspace;
        }
    }
}
