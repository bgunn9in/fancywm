using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

using WinMan;
using FancyWM.DllImports;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Interop;
using WinMan.Windows;

namespace FancyWM.Utilities
{
    internal static partial class WindowExtensions
    {
        private class ProcessInfo(int id, string name)
        {
            public int ID = id;
            public string Name = name ?? throw new ArgumentNullException(nameof(name));
        }

        private static readonly ConditionalWeakTable<IWindow, ProcessInfo> m_processCache = [];

        [DllImport("User32", EntryPoint = "GetClassLongW", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint GetClassLong32(HWND hWnd, GetClassLong_nIndex nIndex);

        [DllImport("user32.dll", EntryPoint = "GetClassLongPtr")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr GetClassLongPtr64(HWND hWnd, GetClassLong_nIndex nIndex);

        private static IntPtr GetClassLongPtr(HWND hWnd, GetClassLong_nIndex nIndex)
        {
            if (Marshal.SizeOf<IntPtr>() == 4)
            {
                return new IntPtr(GetClassLong32(hWnd, nIndex));
            }
            else
            {
                return GetClassLongPtr64(hWnd, nIndex);
            }
        }

        internal static BitmapSource ConvertBorrowedIcon(IntPtr iconHandle)
        {
            using var icon = Icon.FromHandle(iconHandle);
            using var bitmap = icon.ToBitmap();
            return ConvertBitmap(bitmap, handle => Imaging.CreateBitmapSourceFromHBitmap(
                handle, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions()));
        }

        internal static BitmapSource ConvertBitmap(Bitmap bitmap, Func<IntPtr, BitmapSource> convert)
        {
            using var handle = new OwnedHBitmap(bitmap);
            return convert(handle.DangerousGetHandle());
        }

        private static BitmapSource LoadShellIcon(string fileName)
        {
            SHFILEINFOW shinfo = new();
            unsafe
            {
                _ = (IntPtr)(void*)PInvoke.SHGetFileInfo(fileName, 0, &shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_FLAGS.SHGFI_ICON | SHGFI_FLAGS.SHGFI_LARGEICON);
            }
            try
            {
                return ConvertBorrowedIcon(shinfo.hIcon);
            }
            finally
            {
                PInvoke.DestroyIcon(shinfo.hIcon);
            }
        }

        public static string GetCachedProcessName(this IWindow window)
        {
            return GetCachedProcessInfo(window).name;
        }

        public static string DebugString(this IWindow window)
        {
            var (id, name) = GetCachedProcessInfo(window);
            return $"{window.Handle:X8}={name}({id})";
        }

        private static (int id, string name) GetCachedProcessInfo(IWindow window)
        {
            var info = GetCachedProcessInfoInternal(window);
            return (info.ID, info.Name);
        }

        private static ProcessInfo GetCachedProcessInfoInternal(IWindow window)
        {
            ProcessInfo? info;
            if (m_processCache.TryGetValue(window, out info!))
            {
                return info;
            }

            try
            {
                var process = window.GetProcess();
                info = new(process.Id, process.ProcessName);
                if (info.Name == "ApplicationFrameHost" && window is Win32Window win32Window && win32Window.ClassName == "ApplicationFrameWindow")
                {
                    HWND hwndChild = PInvoke.FindWindowEx(new(window.Handle), new HWND(), "Windows.UI.Core.CoreWindow", null);
                    if (hwndChild.Value != IntPtr.Zero)
                    {
                        info = GetCachedProcessInfoInternal(window.Workspace.UnsafeCreateFromHandle(hwndChild.Value));
                    }
                }
            }
            catch (InvalidWindowReferenceException)
            {
                info = new(0, "Invalid handle");
            }
            catch (ExternalException)
            {
                info = new(0, "Inaccessible process");
            }
            catch (Exception e) when (e is InvalidOperationException || e is ArgumentException)
            {
                info = new(0, "Dead process");
            }
            m_processCache.AddOrUpdate(window, info);
            return info;
        }

        public static object GetMetadata(this IWindow window)
        {
            return new
            {
                window.Handle,
                window.IsAlive,
                window.IsFocused,
                window.IsTopmost,
                window.MaxSize,
                window.MinSize,
                window.Position,
                window.State,
                Permissions = new
                {
                    window.CanClose,
                    window.CanMaximize,
                    window.CanMinimize,
                    window.CanMove,
                    window.CanReorder,
                    window.CanResize,
                },
            };
        }
    }
}
