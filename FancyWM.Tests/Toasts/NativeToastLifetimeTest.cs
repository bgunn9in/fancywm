#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

using FancyWM.Tests.TestUtilities;
using FancyWM.ThemeEngine.Wpf;
using FancyWM.Toasts;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using WinMan;

namespace FancyWM.Tests.Toasts
{
    [TestClass]
    public class NativeToastLifetimeTest
    {
        private const string ChildFlag = "FANCYWM_NATIVE_TOAST_CHILD";

        public TestContext TestContext { get; set; } = null!;

        [TestMethod]
        public Task NativeCloseReleasesHandlesAndCompletesPendingShows() => Isolated(nameof(NativeCloseReleasesHandlesAndCompletesPendingShows), () =>
        {
            foreach (string close in new[] { "window", "dispose", "service", "worker" })
            {
                using var owners = new Owners();
                using var window = new ToastWindow(owners.Workspace);
                using var service = new ToastService(window);
                using var cancellation = new CancellationTokenSource();
                IntPtr handle = AssertOwnedHandle(window);
                Assert.IsFalse(IsWindowVisible(handle), "A new empty toast must have a hidden real HWND.");
                int closed = 0;
                window.Closed += (_, _) => closed++;
                var active = service.ShowToastAsync("Native toast lifetime verification", cancellation.Token);
                Drain();
                Assert.IsTrue(IsWindowVisible(handle));
                Assert.IsFalse(active.IsCompleted);
                Assert.AreEqual(1, window.ToastItems.Count);
                var queued = service.ShowToastAsync("queued before close", CancellationToken.None);
                if (close == "window") window.Close();
                else if (close == "dispose") window.Dispose();
                else if (close == "service") service.Dispose();
                else Complete(Task.Run(service.Dispose));
                Complete(Task.WhenAll(active, queued));
                cancellation.Cancel();
                owners.RaiseLateCallbacks();
                window.Dispose();
                service.Dispose();
                Complete(service.ShowToastAsync("after close", CancellationToken.None));
                Drain();
                Assert.AreEqual(1, closed);
                AssertClosed(window, handle);
                owners.AssertReleased();
                Console.WriteLine($"NATIVE_TOAST close={close} hwnd={handle.ToInt64():X} closed={closed} pending=2 subscriptions=0");
            }
        });

        [TestMethod]
        public Task NativeConstructorFailureDestroysItsCreatedWindow() => Isolated(nameof(NativeConstructorFailureDestroysItsCreatedWindow), () =>
        {
            // Initialize WPF's dispatcher/render infrastructure before comparing HWND sets.
            using (var warmOwners = new Owners())
            using (var warmWindow = new ToastWindow(warmOwners.Workspace)) AssertOwnedHandle(warmWindow);
            Drain();
            var before = ToastHandles();
            foreach (string boundary in new[] { "position", "primary", "scaling" })
            {
                using var owners = new Owners(boundary);
                var error = Assert.ThrowsException<InvalidOperationException>(() => new ToastWindow(owners.Workspace));
                Assert.AreSame(owners.Failure, error);
                Assert.AreNotEqual(IntPtr.Zero, owners.HandleAtFailure);
                Assert.IsFalse(IsWindow(owners.HandleAtFailure), "The constructor's native HWND must be destroyed before its exception escapes.");
                owners.RaiseLateCallbacks();
                Drain();
                owners.AssertReleased();
                CollectionAssert.AreEquivalent(before, ToastHandles());
                Console.WriteLine($"NATIVE_TOAST failure={boundary} hwnd={owners.HandleAtFailure.ToInt64():X} destroyed=1 subscriptions=0");
            }
        });

        [TestMethod]
        public Task NativeCloseFailureStillDestroysTheWindowAndReleasesOwners() => Isolated(nameof(NativeCloseFailureStillDestroysTheWindowAndReleasesOwners), () =>
        {
            foreach (string boundary in new[] { "remove-primary", "remove-scaling", "closed" })
            foreach (bool dispose in new[] { false, true })
            {
                using var owners = new Owners(boundary);
                using var window = new ToastWindow(owners.Workspace);
                using var service = new ToastService(window);
                IntPtr handle = AssertOwnedHandle(window);
                var active = service.ShowToastAsync("close failure verification", CancellationToken.None);
                Drain();
                int closed = 0;
                window.Closed += (_, _) =>
                {
                    closed++;
                    if (boundary == "closed") throw owners.Failure;
                };
                var failures = new List<Exception>();
                DispatcherUnhandledExceptionEventHandler onFailure = (_, args) =>
                {
                    if (!ReferenceEquals(args.Exception, owners.Failure)) return;
                    failures.Add(args.Exception);
                    args.Handled = true;
                };
                window.Dispatcher.UnhandledException += onFailure;
                try
                {
                    // Close dispatches WM_DESTROY: its exception is delivered through
                    // WPF's dispatcher, whereas Dispose can fail before that message.
                    try
                    {
                        if (dispose) window.Dispose();
                        else window.Close();
                    }
                    catch (InvalidOperationException error) when (ReferenceEquals(error, owners.Failure))
                    {
                        failures.Add(error);
                    }
                    Drain();
                }
                finally { window.Dispatcher.UnhandledException -= onFailure; }
                Assert.AreEqual(1, failures.Count, "The exact injected error must be observed once at its real WPF boundary.");
                Assert.AreSame(owners.Failure, failures.Single());
                Complete(active);
                Drain();
                AssertClosed(window, handle);
                Assert.AreEqual(1, closed);
                owners.AssertReleased();
                Console.WriteLine($"NATIVE_TOAST closeFailure={boundary} dispose={dispose} hwnd={handle.ToInt64():X} destroyed=1 closed=1 subscriptions=0");
            }
        });

        [TestMethod]
        public Task NativeApplicationShutdownClosesToastAndCompletesItsWaiter() => Isolated(nameof(NativeApplicationShutdownClosesToastAndCompletesItsWaiter), () =>
        {
            using var owners = new Owners();
            var window = new ToastWindow(owners.Workspace);
            var service = new ToastService(window);
            IntPtr handle = AssertOwnedHandle(window);
            var pending = service.ShowToastAsync("application shutdown verification", CancellationToken.None);
            Drain();
            Assert.IsTrue(IsWindowVisible(handle));
            Assert.IsFalse(pending.IsCompleted);
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = dispatcher.BeginInvoke(() => Application.Current.Shutdown());
            Application.Current.Run();
            Assert.IsTrue(dispatcher.HasShutdownFinished);
            Assert.IsTrue(pending.Wait(TimeSpan.FromSeconds(5)), "Application shutdown must settle an uncancelled active toast.");
            Assert.IsFalse(IsWindow(handle));
            Assert.AreEqual(IntPtr.Zero, new WindowInteropHelper(window).Handle);
            Assert.IsTrue(window.IsDisposed);
            owners.AssertReleased();
            Assert.IsTrue(service.ShowToastAsync("after shutdown", CancellationToken.None).IsCompletedSuccessfully);
            service.Dispose();
            window.Dispose();
            Console.WriteLine($"NATIVE_TOAST applicationShutdown=1 hwnd={handle.ToInt64():X} destroyed=1 pending=0 subscriptions=0");
        });

        [TestMethod]
        public Task ClosedNativeWindowsAreCollectibleOnALiveDispatcher() => Isolated(nameof(ClosedNativeWindowsAreCollectibleOnALiveDispatcher), () =>
        {
            using var owners = new Owners();
            for (int cycle = 0; cycle < 10; cycle++) CreateAndClose(owners);
            Collect();
            using var process = Process.GetCurrentProcess();
            int initialUser = GetGuiResources(process.Handle, 1);
            int initialGdi = GetGuiResources(process.Handle, 0);
            Assert.IsTrue(initialUser > 0, "The live WPF dispatcher must have observable USER resources.");
            var weak = new List<WeakReference>();
            for (int epoch = 0; epoch < 2; epoch++)
            {
                for (int cycle = 0; cycle < 50; cycle++) weak.Add(CreateAndClose(owners));
                Collect();
                owners.AssertReleased();
                int live = weak.Count(reference => reference.IsAlive);
                int user = GetGuiResources(process.Handle, 1);
                int gdi = GetGuiResources(process.Handle, 0);
                Console.WriteLine($"NATIVE_TOAST epoch={epoch + 1} cycles={weak.Count} live={live} USER={initialUser}->{user} GDI={initialGdi}->{gdi}");
                Assert.AreEqual(0, live, "Closed windows must be collectible while their STA dispatcher is still alive.");
                Assert.AreEqual(initialUser, user, "Repeated hidden HWND lifetimes must release USER objects.");
                Assert.AreEqual(initialGdi, gdi, "Repeated hidden HWND lifetimes must release GDI objects.");
                Assert.AreEqual(0, ToastHandles().Length);
            }
        });

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference CreateAndClose(Owners owners)
        {
            var window = new ToastWindow(owners.Workspace);
            IntPtr handle = AssertOwnedHandle(window);
            var weak = new WeakReference(window);
            window.Dispose();
            AssertClosed(window, handle);
            owners.AssertReleased();
            return weak;
        }

        private Task Isolated(string name, Action action)
        {
            if (Environment.GetEnvironmentVariable(ChildFlag) != name)
                return IsolatedTestProcess.RunAsync(TestContext, typeof(NativeToastLifetimeTest),
                    typeof(NativeToastLifetimeTest).FullName + "." + name, ChildFlag, name, name);
            return RunSta(action);
        }

        private static async Task RunSta(Action action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                try
                {
                    CssManager.ApplyTheme(new Dictionary<string, CssValue>(new CssToWpfResourceConverter().Convert("<fixture></fixture>", "fixture { color: red; }")));
                    action();
                    completion.SetResult();
                }
                catch (Exception error) { completion.SetException(error); }
                finally
                {
                    if (!Dispatcher.CurrentDispatcher.HasShutdownFinished)
                    {
                        app.Shutdown();
                        Dispatcher.CurrentDispatcher.InvokeShutdown();
                    }
                }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
            finally { Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)), "The owned native STA thread must terminate."); }
        }

        private static IntPtr AssertOwnedHandle(ToastWindow window)
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            Assert.AreNotEqual(IntPtr.Zero, handle);
            Assert.IsTrue(IsWindow(handle));
            uint thread = GetWindowThreadProcessId(handle, out uint process);
            Assert.AreEqual((uint)Environment.ProcessId, process);
            Assert.AreEqual(GetCurrentThreadId(), thread);
            Assert.IsTrue(GetDpiForWindow(handle) > 0);
            return handle;
        }

        private static void AssertClosed(ToastWindow window, IntPtr handle)
        {
            Assert.IsFalse(IsWindow(handle));
            Assert.AreEqual(IntPtr.Zero, new WindowInteropHelper(window).Handle);
            Assert.IsNull(HwndSource.FromHwnd(handle));
            Assert.IsTrue(window.IsDisposed);
            Assert.AreEqual(0, window.ToastItems.Count);
            Assert.IsFalse(Application.Current.Windows.Cast<Window>().Contains(window));
        }

        private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        private static void Collect()
        {
            for (int pass = 0; pass < 3; pass++)
            {
                Drain();
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            Drain();
        }

        private static void Complete(Task task)
        {
            var timer = Stopwatch.StartNew();
            while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(5))
            {
                Drain();
                Thread.Yield();
            }
            Assert.IsTrue(task.IsCompleted, "The toast operation must settle on the live dispatcher.");
            task.GetAwaiter().GetResult();
        }

        private static IntPtr[] ToastHandles()
        {
            var handles = new List<IntPtr>();
            Assert.IsTrue(EnumThreadWindows(GetCurrentThreadId(), (handle, _) =>
            {
                var title = new StringBuilder(256);
                GetWindowText(handle, title, title.Capacity);
                if (title.ToString() == "ToastWindow") handles.Add(handle);
                return true;
            }, IntPtr.Zero));
            return handles.ToArray();
        }

        private sealed class Owners : IDisposable
        {
            public InvalidOperationException Failure { get; } = new("controlled native toast constructor failure");
            public IntPtr HandleAtFailure { get; private set; }
            public IWorkspace Workspace { get; }
            private readonly Mock<IDisplay> m_display = new();
            private readonly Mock<IDisplayManager> m_manager = new();
            private EventHandler<PrimaryDisplayChangedEventArgs>? m_primary;
            private EventHandler<DisplayScalingChangedEventArgs>? m_scaling;
            private EventHandler<PrimaryDisplayChangedEventArgs>? m_latePrimary;
            private EventHandler<DisplayScalingChangedEventArgs>? m_lateScaling;

            public Owners(string? failureBoundary = null)
            {
                Assert.IsTrue(SystemParametersInfo(0x0030, 0, out var area, 0)); // SPI_GETWORKAREA
                m_display.SetupGet(display => display.WorkArea).Returns(() =>
                {
                    FailAt("position");
                    return new WinMan.Rectangle(area.Left, area.Top, area.Right, area.Bottom);
                });
                m_display.SetupAdd(display => display.ScalingChanged += It.IsAny<EventHandler<DisplayScalingChangedEventArgs>>())
                    .Callback<EventHandler<DisplayScalingChangedEventArgs>>(handler => { m_scaling += handler; m_lateScaling = handler; FailAt("scaling"); });
                m_display.SetupRemove(display => display.ScalingChanged -= It.IsAny<EventHandler<DisplayScalingChangedEventArgs>>())
                    .Callback<EventHandler<DisplayScalingChangedEventArgs>>(handler =>
                    {
                        m_scaling -= handler;
                        if (failureBoundary == "remove-scaling") throw Failure;
                    });
                m_manager.SetupGet(manager => manager.PrimaryDisplay).Returns(m_display.Object);
                m_manager.SetupAdd(manager => manager.PrimaryDisplayChanged += It.IsAny<EventHandler<PrimaryDisplayChangedEventArgs>>())
                    .Callback<EventHandler<PrimaryDisplayChangedEventArgs>>(handler => { m_primary += handler; m_latePrimary = handler; FailAt("primary"); });
                m_manager.SetupRemove(manager => manager.PrimaryDisplayChanged -= It.IsAny<EventHandler<PrimaryDisplayChangedEventArgs>>())
                    .Callback<EventHandler<PrimaryDisplayChangedEventArgs>>(handler =>
                    {
                        m_primary -= handler;
                        if (failureBoundary == "remove-primary") throw Failure;
                    });
                var workspace = new Mock<IWorkspace>();
                workspace.SetupGet(value => value.DisplayManager).Returns(m_manager.Object);
                Workspace = workspace.Object;

                void FailAt(string boundary)
                {
                    if (failureBoundary != boundary) return;
                    HandleAtFailure = ToastHandles().Single();
                    throw Failure;
                }
            }

            public void RaiseLateCallbacks()
            {
                m_latePrimary?.Invoke(m_manager.Object, new PrimaryDisplayChangedEventArgs(m_display.Object, m_display.Object));
                m_lateScaling?.Invoke(m_display.Object, new DisplayScalingChangedEventArgs(m_display.Object, 1.25, 1));
            }

            public void AssertReleased()
            {
                Assert.IsNull(m_primary);
                Assert.IsNull(m_scaling);
                // Mock call histories and deliberately retained stale delegates are fixture roots.
                m_latePrimary = null;
                m_lateScaling = null;
                m_manager.Invocations.Clear();
                m_display.Invocations.Clear();
            }

            public void Dispose() => AssertReleased();
        }

        private delegate bool EnumWindowCallback(IntPtr handle, IntPtr state);
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRectangle { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr handle);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint process);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, EnumWindowCallback callback, IntPtr state);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int count);
        [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, uint flags);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr handle);
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] private static extern bool SystemParametersInfo(uint action, uint parameter, out NativeRectangle rectangle, uint flags);
    }
}
