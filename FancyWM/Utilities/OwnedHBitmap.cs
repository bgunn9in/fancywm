using System;
using System.Drawing;
using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

namespace FancyWM.Utilities
{
    internal sealed class OwnedHBitmap : SafeHandleZeroOrMinusOneIsInvalid
    {
        public OwnedHBitmap(Bitmap bitmap) : base(true)
        {
            SetHandle(bitmap.GetHbitmap());
        }

        [DllImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr handle);

        protected override bool ReleaseHandle() => DeleteObject(handle);
    }
}
