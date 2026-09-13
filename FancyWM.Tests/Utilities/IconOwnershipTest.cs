using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

using FancyWM.Utilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class IconOwnershipTest
    {
        [DllImport("user32.dll")]
        private static extern int GetGuiResources(IntPtr process, uint flags);

        [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
        private static extern int GetObjectSize(IntPtr handle, int size, [Out] byte[] buffer);

        private static int ReadBitmap(IntPtr handle) => GetObjectSize(handle, 32, new byte[32]);

        [DllImport("gdi32.dll")]
        private static extern bool GdiFlush();

        [TestMethod]
        public void BitmapIsReleasedWhenConversionThrows()
        {
            using var bitmap = new Bitmap(8, 8);
            IntPtr observedHandle = IntPtr.Zero;
            Assert.ThrowsException<InvalidOperationException>(() => WindowExtensions.ConvertBitmap(bitmap, handle =>
            {
                observedHandle = handle;
                Assert.IsTrue(ReadBitmap(handle) > 0);
                throw new InvalidOperationException("conversion failed");
            }));
            Assert.AreNotEqual(IntPtr.Zero, observedHandle);
            GdiFlush();
            Assert.AreEqual(0, ReadBitmap(observedHandle));
        }

        [TestMethod]
        public void OwnedBitmapDisposeIsIdempotent()
        {
            using var bitmap = new Bitmap(8, 8);
            var ownedHandle = new OwnedHBitmap(bitmap);
            IntPtr rawHandle = ownedHandle.DangerousGetHandle();
            Assert.IsTrue(ReadBitmap(rawHandle) > 0);
            ownedHandle.Dispose();
            Assert.IsTrue(ownedHandle.IsClosed);
            GdiFlush();
            Assert.AreEqual(0, ReadBitmap(rawHandle));
            ownedHandle.Dispose();
            Assert.AreEqual(0, ReadBitmap(rawHandle));
        }

        [TestMethod]
        public void BorrowedIconRemainsUsableAfterRepeatedConversions()
        {
            using var original = (Icon)SystemIcons.Application.Clone();
            IntPtr borrowedHandle = original.Handle;
            for (int iteration = 0; iteration < 100; iteration++)
            {
                var source = WindowExtensions.ConvertBorrowedIcon(borrowedHandle);
                Assert.AreEqual(original.Width, source.PixelWidth);
                using var copy = Icon.FromHandle(borrowedHandle).ToBitmap();
                Assert.AreEqual(original.Height, copy.Height);
            }
        }

        [TestMethod]
        public void ShellIconConversionDoesNotAccumulateNativeResources()
        {
            string iconPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ico");
            try
            {
                using (var stream = File.Create(iconPath))
                {
                    SystemIcons.Application.Save(stream);
                }
                var loadIcon = (Func<string, BitmapSource>)typeof(WindowExtensions)
                    .GetMethod("LoadShellIcon", BindingFlags.Static | BindingFlags.NonPublic)
                    .CreateDelegate(typeof(Func<string, BitmapSource>));
                using var process = Process.GetCurrentProcess();
                for (int iteration = 0; iteration < 10; iteration++) { loadIcon(iconPath); }
                int initialGdi = GetGuiResources(process.Handle, 0);
                int initialUser = GetGuiResources(process.Handle, 1);
                for (int iteration = 0; iteration < 100; iteration++)
                {
                    var source = loadIcon(iconPath);
                    Assert.IsTrue(source.PixelWidth > 0);
                    var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
                    source.CopyPixels(pixels, source.PixelWidth * 4, 0);
                    Assert.IsTrue(Array.Exists(pixels, value => value != 0));
                }
                int finalGdi = GetGuiResources(process.Handle, 0);
                int finalUser = GetGuiResources(process.Handle, 1);
                Console.WriteLine($"100 conversions: GDI {initialGdi}->{finalGdi}; USER {initialUser}->{finalUser}");
                Assert.IsTrue(finalGdi <= initialGdi + 2, "HBITMAP ownership must end after conversion.");
                Assert.IsTrue(finalUser <= initialUser + 2, "Owned shell HICON must be released.");
            }
            finally
            {
                File.Delete(iconPath);
            }
        }
    }
}
