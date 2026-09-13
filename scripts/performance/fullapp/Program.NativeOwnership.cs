internal static partial class Program
{
    private static readonly List<object> OwnershipWaits = [];

    private static bool WindowOwnersReady(IEnumerable<uint> processIds, int? ownedProcess)
    {
        bool ready = true;
        foreach (uint process in processIds)
        {
            // A destroyed HWND reports PID zero before its asynchronous removal
            // reaches the layout model. It cannot satisfy the idle boundary.
            // Known foreign owners must still fail, even beside an unknown HWND.
            if (process == 0) ready = false;
            else if (ownedProcess is null || process != ownedProcess)
                throw new InvalidOperationException("A foreign HWND entered the managed layout.");
        }
        return ready;
    }
}
