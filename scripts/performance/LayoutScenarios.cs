using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using FancyWM.Layouts;
using FancyWM.Layouts.Tiling;

using WinMan;

internal static class LayoutScenarios
{
    public static void Verify()
    {
        foreach (int count in new[] { 1, 4, 10, 25, 50 })
        {
            foreach (bool nested in new[] { false, true })
            {
                var random = new Random(5005 + count);
                var root = new SplitPanelNode();
                var tree = new DesktopTree { Root = root, WorkArea = new Rectangle(0, 0, 3440, 1440) };
                var panel = root;
                if (nested)
                {
                    for (int depth = 0; depth < 5; depth++)
                    {
                        var child = new SplitPanelNode();
                        panel.Attach(child);
                        panel = child;
                    }
                }
                for (int index = 0; index < count; index++)
                {
                    panel.Attach(new WindowNode(new FakeWindow(index + 1)));
                }
                tree.Measure();
                tree.Arrange();
                var transcript = new StringBuilder();
                for (int iteration = 0; iteration < 300; iteration++)
                {
                    var node = (WindowNode)panel.Children[random.Next(count)];
                    switch (iteration % 10)
                    {
                        case 0:
                            ((FakeWindow)node.WindowReference).MinSize = new Point(random.Next(0, 5), random.Next(0, 5));
                            break;
                        case 1:
                            panel.Move(random.Next(count), random.Next(count));
                            break;
                        case 2:
                            panel.ChangeOrientation(panel.Orientation == PanelOrientation.Horizontal
                                ? PanelOrientation.Vertical : PanelOrientation.Horizontal);
                            break;
                        case 3:
                            panel.Spacing = random.Next(0, 4) * 2;
                            break;
                        case 4:
                            transcript.Append(panel.ResizeBy(node, random.Next(-5, 6), GrowDirection.Both));
                            break;
                        case 5:
                            panel.Detach(node);
                            panel.Attach(node);
                            break;
                        case 6:
                            var beforeClone = Describe(tree);
                            var clone = new DesktopTree { Root = (SplitPanelNode)root.Clone() };
                            clone.WorkArea = new Rectangle(0, 0, 4000, 2000);
                            clone.Measure();
                            clone.Arrange();
                            if (Describe(tree) != beforeClone) { throw new InvalidOperationException("Clone changed live layout"); }
                            transcript.Append(Describe(clone));
                            break;
                        case 7:
                            panel.DistributeChildrenEvenly();
                            break;
                        case 8:
                            ((FakeWindow)node.WindowReference).MinSize = new Point(10000, 10000);
                            break;
                        case 9:
                            foreach (var window in panel.Windows) { ((FakeWindow)window.WindowReference).MinSize = null; }
                            panel.ResetConstraints();
                            break;
                    }
                    try
                    {
                        tree.Measure();
                        tree.Arrange();
                        transcript.Append("success");
                    }
                    catch (UnsatisfiableFlexConstraintsException)
                    {
                        transcript.Append("unsatisfiable");
                    }
                    transcript.Append(Describe(tree));
                    if (Environment.GetEnvironmentVariable("FWM_PERF_LAYOUT_TRACE") == "1")
                    {
                        Console.WriteLine($"{count},{nested},{iteration}:{Describe(tree)}");
                    }
                }
                Console.WriteLine($"{count},{nested},{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(transcript.ToString())))}");
            }
        }
    }

    private static string Describe(DesktopTree tree)
    {
        var result = new StringBuilder();
        foreach (var node in tree.Root!.Nodes)
        {
            result.Append(node.ComputedRectangle);
            if (node is WindowNode window) { result.Append(window.WindowReference.Handle); }
            if (node is SplitPanelNode panel)
            {
                result.Append(panel.Orientation);
                foreach (var child in panel.Children)
                {
                    var constraints = panel.GetChildConstraints(child);
                    result.Append(constraints.Width.ToString("R", CultureInfo.InvariantCulture));
                    result.Append(constraints.MinWidth.ToString("R", CultureInfo.InvariantCulture));
                    result.Append(constraints.MaxWidth.ToString("R", CultureInfo.InvariantCulture));
                }
            }
        }
        return result.ToString();
    }
}
