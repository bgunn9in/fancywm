using System.Runtime.InteropServices;

namespace FancyWM.DllImports
{
    public static partial class PInvoke
    {
        // The pinned generator projects GetMessage as bool and loses its -1
        // failure result. Keep the signed native status for the hook owners.
        [DllImport("user32.dll", EntryPoint = "GetMessageW", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int GetMessageStatus(out MSG message, HWND window,
            uint minimumMessage, uint maximumMessage);
    }
}
