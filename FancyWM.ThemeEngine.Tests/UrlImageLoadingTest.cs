using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FancyWM.ThemeEngine.Wpf;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.ThemeEngine.Tests
{
    [TestClass]
    public partial class UrlImageLoadingTest
    {
        [TestMethod]
        public async Task NetworkImageConversionReturnsBeforeResponseAndReusesValue()
        {
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var uri = $"http://127.0.0.1:{port}/{Guid.NewGuid():N}.png";
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var responseReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            byte[] png = CreatePng();
            Task server = ServeAsync();
            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                // The deadline is only a failure bound, not the proof of asynchrony.
                using var stopped = lifetime.Token.Register(() => dispatcher.BeginInvokeShutdown(DispatcherPriority.Send));
                try
                {
                    var resources = new CssToWpfResourceConverter().Convert("<button></button>",
                        $"button {{ background-image: url('{uri}'); }}");
                    var value = resources["button/background-image"];
                    var brush = value.As<ImageBrush>();
                    var bitmap = (BitmapImage)brush.ImageSource;
                    Assert.IsFalse(release.Task.IsCompleted, "No response bytes may precede conversion.");
                    Assert.IsTrue(bitmap.IsDownloading);
                    Assert.AreEqual(Stretch.None, brush.Stretch);
                    Assert.AreEqual(TileMode.Tile, brush.TileMode);
                    for (int index = 0; index < 100; index++) Assert.AreSame(brush, value.As<ImageBrush>());
                    bitmap.DownloadFailed += (_, args) =>
                    {
                        complete.TrySetException(args.ErrorException);
                        dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                    };
                    bitmap.DownloadCompleted += (_, _) =>
                    {
                        try
                        {
                            Assert.AreEqual(2, bitmap.PixelWidth);
                            Assert.AreEqual(1, bitmap.PixelHeight);
                            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                            byte[] pixels = new byte[8];
                            converted.CopyPixels(pixels, 8, 0);
                            CollectionAssert.AreEqual(new byte[] { 0, 0, 255, 255, 255, 0, 0, 255 }, pixels);
                            Assert.AreSame(brush, value.As<ImageBrush>());
                            complete.TrySetResult();
                        }
                        catch (Exception error) { complete.TrySetException(error); }
                        finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
                    };
                    returned.TrySetResult();
                    Dispatcher.Run();
                }
                catch (Exception error)
                {
                    returned.TrySetException(error);
                    complete.TrySetException(error);
                }
                finally { dispatcher.InvokeShutdown(); }
            }) { IsBackground = true, Name = "URL image conversion regression" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            try
            {
                await returned.Task.WaitAsync(lifetime.Token);
                await responseReady.Task.WaitAsync(lifetime.Token);
                release.SetResult();
                await complete.Task.WaitAsync(lifetime.Token);
                await server;
            }
            finally
            {
                release.TrySetResult();
                lifetime.Cancel();
                listener.Stop();
                try
                {
                    try { await server; }
                    catch (OperationCanceledException) { }
                    catch (SocketException) when (lifetime.IsCancellationRequested) { }
                }
                finally
                {
                    Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)), "The owned STA must exit.");
                }
            }

            async Task ServeAsync()
            {
                using var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                using var stream = client.GetStream();
                byte[] one = new byte[1];
                var header = new StringBuilder();
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    Assert.IsTrue(header.Length < 16384, "Bound the local fixture request.");
                    Assert.AreEqual(1, await stream.ReadAsync(one, lifetime.Token));
                    header.Append((char)one[0]);
                }
                responseReady.SetResult();
                await release.Task.WaitAsync(lifetime.Token);
                byte[] response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {png.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, lifetime.Token);
                await stream.WriteAsync(png, lifetime.Token);
                await stream.FlushAsync(lifetime.Token);

                // Let the client observe an orderly end-of-response before disposing the socket.
                // Closing immediately after the final write can intermittently reset the loopback
                // connection on Windows while BitmapImage is still consuming the response.
                client.Client.Shutdown(SocketShutdown.Send);
                while (await stream.ReadAsync(one, lifetime.Token) != 0) { }
            }
        }

        private static byte[] CreatePng()
        {
            var bitmap = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 0, 0, 255, 255, 255, 0, 0, 255 }, 8);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
    }
}
