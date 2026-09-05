using WinMan;

namespace FancyWM.AlgorithmicLayouts
{
    internal readonly record struct LayoutStateKey(
        IVirtualDesktop VirtualDesktop,
        IDisplay Display);
}
