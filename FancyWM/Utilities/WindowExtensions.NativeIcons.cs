using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

using FancyWM.DllImports;

namespace FancyWM.Utilities
{
    internal static partial class WindowExtensions
    {
        private static readonly object m_iconOwnerLock = new();
        private static IconCache? m_iconOwner;
        private static bool m_iconDiscoveryStopped;

        internal static IconCache? Icons
        {
            get
            {
                lock (m_iconOwnerLock)
                {
                    return m_iconDiscoveryStopped ? null : m_iconOwner ??= new IconCache(new NativeIconResolver());
                }
            }
        }

        internal static void StopIconDiscovery()
        {
            IconCache? owner;
            lock (m_iconOwnerLock)
            {
                m_iconDiscoveryStopped = true;
                owner = m_iconOwner;
            }
            owner?.Dispose();
        }

        [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int GetIconWindowClass(nint window, StringBuilder className, int capacity);

        [DllImport("ole32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int CoInitializeEx(nint reserved, uint flags);

        [DllImport("ole32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern void CoUninitialize();

        internal sealed record NativeWindowIdentity(nint Handle, uint Process, uint Thread,
            long? Started, string? Module, string? Class, nint ClassIcon);

        internal sealed record NativeIconState(NativeWindowIdentity Parent, NativeWindowIdentity? Child,
            string? PackageDirectory, long ManifestVersion);

        private sealed record NativeIconKey(uint Process, long Started, string? Module,
            string? Class, nint Icon, string? PackageDirectory, long ManifestVersion, string? FallbackModule);

        internal sealed class NativeIconResolver : IIconResolver
        {
            public async ValueTask<IconIdentity?> IdentifyAsync(IconRequest request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parent = ReadWindowIdentity(request.Handle, request.IsNativeWindow);
                if (parent == null) { return null; }
                NativeWindowIdentity? child = null;
                string? packageDirectory = null;
                long manifestVersion = 0;
                bool transient = parent.Started == null;
                if (parent.Class == "ApplicationFrameWindow")
                {
                    nint childHandle = FindCoreWindow(parent.Handle);
                    foreach (int delay in new[] { 30, 300, 3000 })
                    {
                        if (childHandle != 0) { break; }
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                        childHandle = FindCoreWindow(parent.Handle);
                    }
                    if (childHandle != 0)
                    {
                        child = ReadWindowIdentity(childHandle, readClass: false);
                        if (child?.Module is { } module)
                        {
                            packageDirectory = Path.GetDirectoryName(module);
                            manifestVersion = ReadManifestVersion(packageDirectory);
                        }
                    }
                    transient |= packageDirectory == null || manifestVersion == 0;
                }
                cancellationToken.ThrowIfCancellationRequested();
                var source = packageDirectory != null && manifestVersion != 0 ? child! : parent;
                object? key = source.Started is { } started
                    ? new NativeIconKey(source.Process, started, source.Module, source.Class,
                        parent.ClassIcon, packageDirectory, manifestVersion, parent.Module)
                    : null;
                return new IconIdentity(parent.Handle, key,
                    new NativeIconState(parent, child, packageDirectory, manifestVersion), transient);
            }

            public ValueTask<BitmapSource?> LoadAsync(IconIdentity identity, CancellationToken cancellationToken)
            {
                var state = (NativeIconState)identity.State;
                cancellationToken.ThrowIfCancellationRequested();
                if (state.PackageDirectory != null && state.ManifestVersion != 0)
                {
                    try
                    {
                        if (LoadPackageIcon(state.PackageDirectory, cancellationToken) is { } modern)
                        {
                            return ValueTask.FromResult<BitmapSource?>(modern);
                        }
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
                        error is System.Xml.XmlException || error is NotSupportedException || error is FormatException)
                    {
                        // Keep the ordinary class/shell fallback when a package is unavailable or
                        // its manifest/image is incomplete during an update.
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (state.Parent.ClassIcon != 0)
                {
                    return ValueTask.FromResult<BitmapSource?>(ConvertBorrowedIcon(state.Parent.ClassIcon));
                }
                if (state.Parent.Module != null)
                {
                    return ValueTask.FromResult<BitmapSource?>(LoadShellIconOnWorker(state.Parent.Module));
                }
                return ValueTask.FromResult<BitmapSource?>(null);
            }

            public bool IsCurrent(IconIdentity identity)
            {
                var state = (NativeIconState)identity.State;
                return IsWindowIdentityCurrent(state.Parent) &&
                    (state.Child == null || (FindCoreWindow(state.Parent.Handle) == state.Child.Handle &&
                        IsWindowIdentityCurrent(state.Child))) &&
                    (state.PackageDirectory == null ||
                        ReadManifestVersion(state.PackageDirectory) == state.ManifestVersion);
            }
        }

        private static nint FindCoreWindow(nint parent) =>
            PInvoke.FindWindowEx(new(parent), new HWND(), "Windows.UI.Core.CoreWindow", null).Value;

        private static NativeWindowIdentity? ReadWindowIdentity(nint handle, bool readClass)
        {
            uint processId = 0;
            uint threadId;
            unsafe { threadId = PInvoke.GetWindowThreadProcessId(new(handle), &processId); }
            if (processId == 0 || threadId == 0) { return null; }
            long? started = null;
            string? module = null;
            try
            {
                using var process = Process.GetProcessById((int)processId);
                try { started = process.StartTime.ToUniversalTime().Ticks; }
                catch (Exception error) when (IsProcessAccessFailure(error)) { }
                try { module = process.MainModule?.FileName; }
                catch (Exception error) when (IsProcessAccessFailure(error)) { }
            }
            catch (Exception error) when (IsProcessAccessFailure(error)) { }
            string? className = null;
            if (readClass)
            {
                var buffer = new StringBuilder(256);
                if (GetIconWindowClass(handle, buffer, buffer.Capacity) != 0) { className = buffer.ToString(); }
            }
            nint classIcon = GetClassLongPtr(new(handle), GetClassLong_nIndex.GCL_HICON);
            if (classIcon == 0) { classIcon = GetClassLongPtr(new(handle), GetClassLong_nIndex.GCL_HICONSM); }
            return new(handle, processId, threadId, started, module, className, classIcon);
        }

        private static bool IsWindowIdentityCurrent(NativeWindowIdentity expected)
        {
            var current = ReadWindowIdentity(expected.Handle, expected.Class != null);
            return current != null && current.Process == expected.Process && current.Thread == expected.Thread &&
                current.Class == expected.Class && current.ClassIcon == expected.ClassIcon &&
                (expected.Started == null || expected.Started == current.Started) &&
                (expected.Module == null || StringComparer.OrdinalIgnoreCase.Equals(expected.Module, current.Module));
        }

        private static bool IsProcessAccessFailure(Exception error) => error is InvalidOperationException ||
            error is ArgumentException || error is System.ComponentModel.Win32Exception ||
            error is NotSupportedException;

        private static long ReadManifestVersion(string? directory)
        {
            if (directory == null) { return 0; }
            var manifest = new FileInfo(Path.Combine(directory, "AppxManifest.xml"));
            return manifest.Exists ? manifest.LastWriteTimeUtc.Ticks : 0;
        }

        internal static BitmapSource? LoadPackageIcon(string directory, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(Path.Combine(directory, "AppxManifest.xml"));
            var manifest = XDocument.Load(stream);
            const string ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
            string? logo = manifest.Root?.Element(XName.Get("Properties", ns))?.Element(XName.Get("Logo", ns))?.Value;
            if (string.IsNullOrWhiteSpace(logo)) { return null; }
            string pattern = Path.GetFileNameWithoutExtension(logo) + "*" + Path.GetExtension(logo);
            foreach (string file in Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var imageStream = File.OpenRead(file);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = imageStream;
                image.EndInit();
                image.Freeze();
                return image;
            }
            return null;
        }

        private static BitmapSource LoadShellIconOnWorker(string fileName)
        {
            // SHGetFileInfo requires COM initialization on the calling thread. This synchronous
            // scope cannot cross an await; an already initialized STA is also valid here.
            int status = CoInitializeEx(0, 0);
            const int changedMode = unchecked((int)0x80010106);
            if (status < 0 && status != changedMode) { Marshal.ThrowExceptionForHR(status); }
            try { return LoadShellIcon(fileName); }
            finally { if (status >= 0) { CoUninitialize(); } }
        }
    }
}
