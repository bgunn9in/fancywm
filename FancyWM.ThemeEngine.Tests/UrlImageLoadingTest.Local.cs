using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using FancyWM.ThemeEngine.Wpf;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.ThemeEngine.Tests
{
    public partial class UrlImageLoadingTest
    {
        [TestMethod]
        public async Task LocalImageCounterScenario()
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            string directory = Path.Combine(Path.GetTempPath(), "FancyWM-local-images-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var files = new List<string>();
            var thread = new Thread(() =>
            {
                try
                {
                    string warmup = CreateFile(16, "warmup");
                    var warmupValue = ImageValue(warmup);
                    warmupValue.As<ImageBrush>().ImageSource.ToString();
                    foreach (int size in new[] { 16, 2048, 4096 })
                    {
                        string path = CreateFile(size, "measured");
                        var value = ImageValue(path);
                        byte[] pixels = new byte[checked(size * size * 4)];
                        long before = GC.GetAllocatedBytesForCurrentThread();
                        long started = Stopwatch.GetTimestamp();
                        var brush = value.As<ImageBrush>();
                        long conversionTicks = Stopwatch.GetTimestamp() - started;
                        long conversionBytes = GC.GetAllocatedBytesForCurrentThread() - before;
                        var bitmap = (BitmapImage)brush.ImageSource;
                        Assert.IsFalse(bitmap.IsDownloading);
                        Assert.AreEqual(size, bitmap.PixelWidth);
                        Assert.AreEqual(size, bitmap.PixelHeight);
                        Assert.AreEqual(Stretch.None, brush.Stretch);
                        Assert.AreEqual(TileMode.Tile, brush.TileMode);
                        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                        started = Stopwatch.GetTimestamp();
                        converted.CopyPixels(pixels, size * 4, 0);
                        long pixelTicks = Stopwatch.GetTimestamp() - started;
                        string actualDigest = Convert.ToHexString(SHA256.HashData(pixels));
                        string expectedDigest = Convert.ToHexString(SHA256.HashData(Pixels(size)));
                        Assert.AreEqual(expectedDigest, actualDigest);
                        int matches = 0;
                        before = GC.GetAllocatedBytesForCurrentThread();
                        started = Stopwatch.GetTimestamp();
                        for (int i = 0; i < 1000; i++)
                            if (ReferenceEquals(brush, value.As<ImageBrush>())) matches++;
                        long cachedTicks = Stopwatch.GetTimestamp() - started;
                        long cachedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
                        Assert.AreEqual(1000, matches);
                        Console.WriteLine($"PERFCOUNTER local-image-{size} conversion-ticks {conversionTicks}");
                        Console.WriteLine($"PERFCOUNTER local-image-{size} conversion-bytes {conversionBytes}");
                        Console.WriteLine($"PERFCOUNTER local-image-{size} first-pixels-ticks {pixelTicks}");
                        Console.WriteLine($"PERFCOUNTER local-image-{size} cached-reads-ticks {cachedTicks}");
                        Console.WriteLine($"PERFCOUNTER local-image-{size} cached-reads-bytes {cachedBytes}");
                        Console.WriteLine($"PERFCOUNTER local-image-{size} timestamp-frequency {Stopwatch.Frequency}");
                        Console.WriteLine($"PERFCOUNTER local-image-{size} reuse-reads {matches}");
                        Console.WriteLine($"PERFCOUNTER local-image-{size} pixels {size * size}");
                        Console.WriteLine($"PERFCOUNTER local-image-{size} pixel-digest {actualDigest}");
                    }
                    var references = SeparateValueLifetimes(warmup);
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    int retained = references.Count(reference => reference.IsAlive);
                    Assert.AreEqual(0, retained, "Existing cache must not retain separate CSS values/brushes after their owners disappear.");
                    Console.WriteLine($"PERFCOUNTER local-image-lifetimes retained-values-brushes {retained}");
                    Console.WriteLine("PERFCOUNTER local-image-lifetimes cycles 100");
                    completion.SetResult();
                }
                catch (Exception error) { completion.SetException(error); }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            }) { IsBackground = true, Name = "Local theme image characterization" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(60)); }
            finally
            {
                Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)), "The owned image STA must exit.");
                // The live warmup value uses WPF's existing on-demand URI decoder.
                // Collect only after its owning thread has returned, outside every
                // measurement, before deleting the fixture's input files.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                // These are the exact files created by this fixture. No recursive
                // delete and no enumeration/deletion of unrelated temp data.
                foreach (string file in files) File.Delete(file);
                Directory.Delete(directory, recursive: false);
            }

            string CreateFile(int size, string suffix)
            {
                string path = Path.Combine(directory, size + "-" + suffix + ".png");
                byte[] pixels = Pixels(size);
                var source = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                files.Add(path);
                encoder.Save(stream);
                return path;
            }
        }

        private static CssValue ImageValue(string path)
        {
            string uri = new Uri(path).AbsoluteUri;
            return new CssToWpfResourceConverter().Convert("<button></button>",
                $"button {{ background-image: url('{uri}'); }}")["button/background-image"];
        }

        private static byte[] Pixels(int size)
        {
            var pixels = new byte[checked(size * size * 4)];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int i = (y * size + x) * 4;
                    pixels[i] = (byte)(x % 251);
                    pixels[i + 1] = (byte)(y % 241);
                    pixels[i + 2] = (byte)((x + y) % 239);
                    pixels[i + 3] = 255;
                }
            return pixels;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference[] SeparateValueLifetimes(string path)
        {
            var references = new WeakReference[200];
            for (int i = 0; i < 100; i++)
            {
                var value = ImageValue(path);
                var brush = value.As<ImageBrush>();
                Assert.AreSame(brush, value.As<ImageBrush>());
                references[2 * i] = new WeakReference(value);
                references[2 * i + 1] = new WeakReference(brush);
            }
            return references;
        }
    }
}
