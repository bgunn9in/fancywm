using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Disposables;
using System.Threading;
using System.Windows.Media;

using FancyWM.DllImports;

using Microsoft.Win32;

namespace FancyWM.Utilities
{
    internal class FauxMicaProvider : IMicaProvider
    {
        public event EventHandler<MicaOptionsChangedEventArgs>? PrimaryColorChanged;

        public Color PrimaryColor
        {
            get
            {
                lock (m_syncRoot)
                {
                    return m_primaryColor;
                }
            }
        }

        private readonly object m_syncRoot = new();
        private Color m_primaryColor = Colors.Transparent;
        private DateTime? m_lastWallpaperWriteTime;
        private string? m_lastWallpaperPath;
        private readonly Thread? m_thread;
        private readonly TimeSpan m_checkingInterval;
        private volatile bool m_disposed;
        private readonly ManualResetEventSlim m_stop = new();
        private readonly Func<SystemWallpaper> m_getCurrent;
        private readonly Func<string, DateTime> m_getLastWriteTime;
        private readonly Func<Color> m_captureWallpaper;
        private readonly Action<Exception> m_reportError;
        private readonly Func<TimeSpan, bool> m_waitForNextCheck;
        private readonly SerialDisposable m_systemChanges = new();
        private long m_wallpaperRevision;
        private long m_capturedRevision = -1;

        internal bool IsWorkerAlive => m_thread?.IsAlive == true;

        public FauxMicaProvider(TimeSpan checkingInterval)
            : this(checkingInterval, SystemWallpaper.GetCurrent, File.GetLastWriteTimeUtc,
                CaptureWallpaperColor, CaptureErrorReporter(checkingInterval),
                subscribeChanges: SubscribeWallpaperChanges)
        {
        }

        private static Action<Exception> CaptureErrorReporter(TimeSpan checkingInterval)
        {
            ValidateCheckingInterval(checkingInterval);
            // The DI-owned worker can outlive Application.Current during exit.
            // Keep the logger acquired while the application is still alive.
            var logger = App.Current.Logger;
            return exception => logger.Warning(exception, "Wallpaper color refresh failed");
        }

        private static void ValidateCheckingInterval(TimeSpan checkingInterval)
        {
            if (checkingInterval.TotalMilliseconds < 1 || checkingInterval.TotalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(checkingInterval));
            }
        }

        internal FauxMicaProvider(TimeSpan checkingInterval, Func<SystemWallpaper> getCurrent,
            Func<string, DateTime> getLastWriteTime, Func<Color> captureWallpaper,
            Action<Exception> reportError, bool startThread = true,
            Func<Action, IDisposable>? subscribeChanges = null, Func<TimeSpan, bool>? waitForNextCheck = null)
        {
            ValidateCheckingInterval(checkingInterval);
            m_checkingInterval = checkingInterval;
            m_getCurrent = getCurrent;
            m_getLastWriteTime = getLastWriteTime;
            m_captureWallpaper = captureWallpaper;
            m_reportError = reportError;
            m_waitForNextCheck = waitForNextCheck ?? m_stop.Wait;
            try
            {
                m_systemChanges.Disposable = subscribeChanges?.Invoke(InvalidateWallpaper);
                if (!startThread) { return; }
                m_thread = new Thread(ThreadMain)
                {
                    Name = "FauxMicaProviderThread",
                    Priority = ThreadPriority.BelowNormal,
                    IsBackground = true,
                };
                m_thread.Start();
            }
            catch
            {
                m_disposed = true;
                m_systemChanges.Dispose();
                m_stop.Dispose();
                throw;
            }
        }

        private static IDisposable SubscribeWallpaperChanges(Action invalidate)
        {
            UserPreferenceChangedEventHandler preferenceChanged = (_, args) =>
            {
                if (args.Category == UserPreferenceCategory.Desktop) { invalidate(); }
            };
            EventHandler displayChanged = (_, _) => invalidate();
            var subscription = Disposable.Create(() =>
            {
                SystemEvents.UserPreferenceChanged -= preferenceChanged;
                SystemEvents.DisplaySettingsChanged -= displayChanged;
            });
            try
            {
                SystemEvents.UserPreferenceChanged += preferenceChanged;
                SystemEvents.DisplaySettingsChanged += displayChanged;
                return subscription;
            }
            catch
            {
                subscription.Dispose();
                throw;
            }
        }

        private void InvalidateWallpaper()
        {
            lock (m_syncRoot)
            {
                if (!m_disposed) { m_wallpaperRevision++; }
            }
        }

        private void ThreadMain()
        {
            try
            {
                while (!m_disposed)
                {
                    try
                    {
                        Refresh();
                    }
                    catch (Exception exception)
                    {
                        m_reportError(exception);
                    }
                    if (m_waitForNextCheck(m_checkingInterval)) { return; }
                }
            }
            finally
            {
                lock (m_syncRoot)
                {
                    m_disposed = true;
                    PrimaryColorChanged = null;
                    m_stop.Dispose();
                }
                m_systemChanges.Dispose();
            }
        }

        internal void Refresh()
        {
            long revision;
            lock (m_syncRoot)
            {
                if (m_disposed) { return; }
                revision = m_wallpaperRevision;
            }
            var current = m_getCurrent();
            DateTime? timestamp = null;
            Color newColor;
            if (current.WallpaperPath is string wallpaperPath)
            {
                timestamp = m_getLastWriteTime(wallpaperPath);
                if (wallpaperPath == m_lastWallpaperPath && timestamp == m_lastWallpaperWriteTime
                    && revision == m_capturedRevision) { return; }
                newColor = m_captureWallpaper();
            }
            else
            {
                newColor = current.RGB is byte[] channels
                    ? Color.FromRgb(channels[0], channels[1], channels[2])
                    : Colors.Transparent;
            }
            EventHandler<MicaOptionsChangedEventArgs>? handler;
            lock (m_syncRoot)
            {
                if (m_disposed || revision != m_wallpaperRevision) { return; }
                m_lastWallpaperPath = current.WallpaperPath;
                m_lastWallpaperWriteTime = timestamp;
                m_capturedRevision = revision;
                if (newColor == m_primaryColor) { return; }
                m_primaryColor = newColor;
                handler = PrimaryColorChanged;
            }
            if (!m_disposed) { handler?.Invoke(this, new MicaOptionsChangedEventArgs()); }
        }

        private static Color CaptureWallpaperColor()
        {
            IntPtr wallpaperHandle = GetWallpaperHWND();
            PInvoke.GetWindowRect(new(wallpaperHandle), out var rectangle);
            using var originalImage = new System.Drawing.Bitmap(rectangle.right - rectangle.left, rectangle.bottom - rectangle.top);
            using (var graphics = System.Drawing.Graphics.FromImage(originalImage))
            {
                var deviceContext = graphics.GetHdc();
                try
                {
                    const PrintWindow_nFlags renderFullContent = (PrintWindow_nFlags)0x00000002;
                    if (!PInvoke.PrintWindow(new(wallpaperHandle), new HDC(deviceContext), renderFullContent))
                    {
                        throw new InvalidOperationException("Could not capture wallpaper");
                    }
                }
                finally
                {
                    graphics.ReleaseHdc(deviceContext);
                }
            }
            using var pixelImage = new System.Drawing.Bitmap(originalImage, new(2, 2));
            var colors = new[]
            {
                pixelImage.GetPixel(0, 0), pixelImage.GetPixel(0, 1),
                pixelImage.GetPixel(1, 0), pixelImage.GetPixel(1, 1),
            };
            return ToMediaColor(TransformColor(AverageColor(colors)));
        }

        private static IntPtr GetWallpaperHWND()
        {
            IntPtr hwndDefView = IntPtr.Zero;
            unsafe
            {
                PInvoke.EnumWindows((HWND hwnd, LPARAM lParam) =>
                {
                    IntPtr hwndDefView = PInvoke.FindWindowEx(hwnd, new HWND(), "SHELLDLL_DefView", null);
                    if (hwndDefView != IntPtr.Zero)
                    {
                        *((IntPtr*)lParam.Value) = hwndDefView;
                        return false;
                    }
                    return true;
                }, new LPARAM((nint)(&hwndDefView)));
            }

            if (hwndDefView == IntPtr.Zero)
            {
                throw new Exception("Could not find SHELLDLL_DefView!");
            }
            return hwndDefView;
        }

        private static System.Drawing.Color TransformColor(System.Drawing.Color color)
        {
            var h = color.GetHue();
            var l = color.GetBrightness();
            return GDIColorFromHSL(h, 1, l);
        }

        public static System.Drawing.Color GDIColorFromHSL(float h, float s, float l)
        {
            byte r;
            byte g;
            byte b;

            if (s == 0)
            {
                r = g = b = (byte)(l * 255);
            }
            else
            {
                float v1, v2;
                float hue = (float)h / 360;

                v2 = (l < 0.5) ? (l * (1 + s)) : ((l + s) - (l * s));
                v1 = 2 * l - v2;

                r = (byte)(255 * RGBComponent(v1, v2, hue + (1.0f / 3)));
                g = (byte)(255 * RGBComponent(v1, v2, hue));
                b = (byte)(255 * RGBComponent(v1, v2, hue - (1.0f / 3)));
            }

            return System.Drawing.Color.FromArgb(r, g, b);
        }

        private static float RGBComponent(float v1, float v2, float vH)
        {
            if (vH < 0)
                vH += 1;

            if (vH > 1)
                vH -= 1;

            if ((6 * vH) < 1)
                return (v1 + (v2 - v1) * 6 * vH);

            if ((2 * vH) < 1)
                return v2;

            if ((3 * vH) < 2)
                return (v1 + (v2 - v1) * ((2.0f / 3) - vH) * 6);

            return v1;
        }

        private static System.Drawing.Color AverageColor(IEnumerable<System.Drawing.Color> colors)
        {
            int r = 0;
            int g = 0;
            int b = 0;
            int count = 0;
            foreach (var color in colors)
            {
                r += color.R;
                g += color.G;
                b += color.B;
                count += 1;
            }
            return System.Drawing.Color.FromArgb(r / count, g / count, b / count);
        }

        private static Color ToMediaColor(System.Drawing.Color color)
        {
            return new Color
            {
                A = 255,
                R = color.R,
                G = color.G,
                B = color.B,
            };
        }

        public void Dispose()
        {
            lock (m_syncRoot)
            {
                if (m_disposed) { return; }
                m_disposed = true;
                PrimaryColorChanged = null;
                m_stop.Set();
                if (m_thread == null) { m_stop.Dispose(); }
            }
            m_systemChanges.Dispose();
            if (m_thread != null && Thread.CurrentThread != m_thread)
            {
                m_thread.Join(TimeSpan.FromMilliseconds(Math.Min(2000, m_checkingInterval.TotalMilliseconds * 2)));
            }
        }
    }
}
