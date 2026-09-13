using System.Collections;
using System.Text.Json;
using FancyWM.Layouts.Tiling;

internal static partial class Program
{
    private static async Task PreparePanelAllocations(int count, JsonElement created)
    {
        // Simultaneous window discovery can arrange partially populated Flex
        // panels. Their inherited widths then depend on callback timing. Freeze
        // equal allocations using the existing layout operation, before warmup
        // or any measurement boundary, preserving the real tree and window nodes.
        await VerifiedGeometry(count);
        var indices = created.GetProperty("Windows").EnumerateArray()
            .ToDictionary(w => w.GetProperty("Hwnd").GetInt64(), w => w.GetProperty("Index").GetInt32());
        if (indices.Count != count) throw new InvalidOperationException("Unexpected owned target set.");
        foreach (var tiling in TilingServices())
        {
            using (BackendLock(tiling).EnterScope())
            {
                var backend = Field(tiling, "m_backend")!;
                foreach (var tree in ((IEnumerable)Property(backend, "Trees")!).Cast<DesktopTree>())
                {
                    if (tree.Root is null) continue;
                    var slots = tree.Root.Windows.ToArray();
                    var ordered = slots.OrderBy(n => indices[n.WindowReference.Handle.ToInt64()]).ToArray();
                    for (int i = 0; i < slots.Length; i++)
                    {
                        if (ReferenceEquals(slots[i], ordered[i])) continue;
                        int other = Array.IndexOf(slots, ordered[i], i + 1);
                        slots[i].Swap(slots[other]);
                        (slots[i], slots[other]) = (slots[other], slots[i]);
                    }
                    if (!tree.Root.Windows.SequenceEqual(ordered))
                        throw new InvalidOperationException("Owned target order was not retained.");
                    Distribute(tree.Root);
                }
            }
            tiling.GetType().GetMethod("InvalidateLayout", Instance)!.Invoke(tiling, null);
        }
        await Idle(count);
        await VerifiedGeometry(count);

        static void Distribute(TilingNode node)
        {
            if (node is PanelNode panel)
            {
                foreach (var child in panel.Children) Distribute(child);
            }
            if (node is SplitPanelNode split) split.DistributeChildrenEvenly();
        }
    }
}
