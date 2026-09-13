#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FancyWM.Models;
using FancyWM.Pages.Settings;
using FancyWM.Tests.TestUtilities;
using FancyWM.ViewModels;
using FancyWM.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace FancyWM.Tests.Utilities;

[TestClass]
public partial class NativeOwnerLifetimeTest
{
    private const string ChildFlag = "FANCYWM_NATIVE_OWNER_CHILD";
    public TestContext TestContext { get; set; } = null!;
    private static Entity Settings = null!;
    private static readonly List<Exception> LoggedErrors = [];

    [TestMethod]
    public Task SettingsNativeCloseAndStaleNavigation() => Isolated(nameof(SettingsNativeCloseAndStaleNavigation), () =>
    {
        foreach (string mode in new[] { "normal", "reentrant", "queued" })
        {
            var entity = new Entity();
            var vm = ViewModel(entity);
            var window = new SettingsWindow(vm) { ShowActivated = false, ShowInTaskbar = false };
            IntPtr handle = Handle(window);
            window.GoToPage(typeof(GeneralPage)); Drain();
            var content = (Border)window.FindName("PageContent");
            Assert.IsInstanceOfType(content.Child, typeof(GeneralPage));
            window.GoToPage(typeof(KeybindingsPage)); Drain();
            Assert.IsInstanceOfType(content.Child, typeof(KeybindingsPage));
            FocusManager.SetFocusedElement(window, window);
            int closed = 0;
            window.Closed += (_, _) =>
            {
                closed++;
                Assert.IsNull(content.Child, "Pages must release before Closed notification.");
                Assert.AreEqual(1, entity.Active, "View-model releases after Closed notification.");
                if (mode == "reentrant") window.Close();
            };
            if (mode == "queued") window.GoToPage(typeof(NeverPage));
            window.Close(); window.Close(); window.GoToPage(typeof(NeverPage));
            entity.Late(); Drain();
            Assert.AreEqual(1, closed); AssertClosed(window, handle);
            Assert.AreEqual(0, entity.Active); Assert.AreEqual(1, entity.Releases);
            Assert.AreEqual(1, entity.Flushes); Assert.IsNull(FocusManager.GetFocusedElement(window));
            Assert.IsNull(content.Child); Assert.IsTrue(Field<bool>(vm, "m_isDisposed"));
            Row(new { scenario = "settings-close", mode, hwnd = handle.ToInt64(), destroyed = true,
                closed, subscriptions = entity.Active, flushes = entity.Flushes, pages = 0, stale = true, dispatcherAlive = Alive });
            entity.Clear();
        }
    });

    [TestMethod]
    public Task SettingsNativeFailureOrdering() => Isolated(nameof(SettingsNativeFailureOrdering), () =>
    {
        foreach (string mode in new[] { "closed", "viewmodel", "combined", "page" })
        {
            var entity = new Entity();
            var vm = ViewModel(entity);
            var window = new SettingsWindow(vm) { ShowActivated = false, ShowInTaskbar = false };
            IntPtr handle = Handle(window);
            var first = new InvalidOperationException("owned Closed failure");
            var later = new InvalidOperationException("owned view-model failure");
            if (mode is "viewmodel" or "combined") entity.ReleaseFailure = later;
            FailingPage? failingPage = null;
            if (mode == "page")
            {
                window.GoToPage(typeof(FailingPage)); Drain();
                failingPage = (FailingPage)((Border)window.FindName("PageContent")).Child;
            }
            int closed = 0;
            window.Closed += (_, _) =>
            {
                closed++; Assert.AreEqual(1, entity.Active);
                if (mode is "closed" or "combined") throw first;
            };
            var expected = mode is "closed" or "combined" ? first : mode == "viewmodel" ? later : null;
            var errors = Observe(expected, window.Close);
            Assert.AreEqual(expected == null ? 0 : 1, errors.Count);
            if (expected != null) Assert.AreSame(expected, errors.Single());
            if (mode == "combined")
                Assert.AreSame(later, ((AggregateException)first.Data["SettingsWindow.OnClosedExceptions"]!).InnerExceptions.Single());
            window.Close(); entity.Late(); Drain(); AssertClosed(window, handle);
            Assert.AreEqual(1, closed); Assert.AreEqual(0, entity.Active); Assert.AreEqual(1, entity.Flushes);
            Assert.IsNull(((Border)window.FindName("PageContent")).Child);
            if (failingPage != null)
            {
                Assert.AreEqual(1, failingPage.Disposals);
                var logged = LoggedErrors.Single();
                Assert.AreSame(failingPage.Failure, ((AggregateException)logged).InnerExceptions.Single());
                LoggedErrors.Clear();
            }
            Row(new { scenario = "settings-failure", mode, hwnd = handle.ToInt64(), destroyed = true,
                errors = errors.Count, closed, subscriptions = entity.Active, flushes = entity.Flushes,
                originalException = true, dispatcherAlive = Alive });
            entity.Clear();
        }
    });

    [TestMethod]
    public Task SettingsNativeRetention() => Isolated(nameof(SettingsNativeRetention), () => Retention("settings", CreateSettings));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CreateSettings()
    {
        var entity = new Entity(); var vm = ViewModel(entity);
        var window = new SettingsWindow(vm) { ShowActivated = false, ShowInTaskbar = false };
        IntPtr handle = Handle(window);
        window.GoToPage(typeof(GeneralPage)); Drain();
        var page = ((Border)window.FindName("PageContent")).Child;
        Assert.IsInstanceOfType(page, typeof(GeneralPage));
        WeakReference[] weak = [new(window), new(vm), new(page), new(entity), new(HwndSource.FromHwnd(handle))];
        window.Close(); AssertClosed(window, handle); Assert.AreEqual(0, entity.Active); entity.Clear();
        return weak;
    }

    [TestMethod]
    public Task ApplicationShutdownClosesNativeOwnersBeforeDispatcherStops() => Isolated(nameof(ApplicationShutdownClosesNativeOwnersBeforeDispatcherStops), () =>
    {
        var entity = new Entity(); var vm = ViewModel(entity);
        var window = new SettingsWindow(vm) { ShowActivated = false, ShowInTaskbar = false };
        IntPtr handle = Handle(window);
        using var owners = new DisplayOwners();
        var host = new OverlayHost(owners.Display); var pair = Pair(host);
        host.Show(); Drain();
        int closed = 0;
        foreach (Window w in new[] { window, pair.Hit, pair.NonHit })
            w.Closed += (_, _) => { Assert.IsFalse(Dispatcher.CurrentDispatcher.HasShutdownStarted); closed++; };
        // OverlayHost is a service owner, not an Application.Window. Its owner must
        // close it before WPF shutdown, as TilingOverlayRenderer.Dispose does.
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(() => { host.Close(); Application.Current.Shutdown(); });
        Application.Current.Run();
        Assert.IsTrue(Dispatcher.CurrentDispatcher.HasShutdownFinished);
        Assert.IsFalse(IsWindow(handle)); Assert.IsFalse(IsWindow(pair.HitHandle)); Assert.IsFalse(IsWindow(pair.NonHitHandle));
        Assert.AreEqual(3, closed); Assert.AreEqual(0, entity.Active); owners.AssertReleased();
        Assert.AreEqual(0, Settings.Active); entity.Clear(); Settings.Clear();
        Row(new { scenario = "shutdown", hwnd = handle.ToInt64(), hit = pair.HitHandle.ToInt64(), nonhit = pair.NonHitHandle.ToInt64(),
            destroyed = true, closed, subscriptions = 0, beforeDispatcherStop = true, dispatcherStopped = true });
    });

    private Task Isolated(string name, Action action)
    {
        if (Environment.GetEnvironmentVariable(ChildFlag) != name)
            return IsolatedTestProcess.RunAsync(TestContext, typeof(NativeOwnerLifetimeTest),
                typeof(NativeOwnerLifetimeTest).FullName + "." + name, ChildFlag, name, name);
        return RunSta(action, name);
    }

    private static async Task RunSta(Action action, string name)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var logger = new LoggerConfiguration().WriteTo.Sink(new ErrorSink()).CreateLogger();
            using var services = new ServiceCollection().AddSingleton<ILogger>(logger).BuildServiceProvider();
            var app = new App(services) { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                // Load the App.xaml dictionaries without its MainWindow StartupUri.
                // Application rejects assigning null to StartupUri once initialized.
                app.Resources.MergedDictionaries.Add(new ModernWpf.ThemeResources { CanBeAccessedAcrossThreads = true });
                app.Resources.MergedDictionaries.Add(new ModernWpf.Controls.XamlControlsResources());
                // Only the AppState collaborator is replaced. Actual App resources,
                // windows, navigation, view-models, Rx, timers and HWNDs are production.
                Settings = new Entity();
                var state = (AppState)RuntimeHelpers.GetUninitializedObject(typeof(AppState));
                typeof(AppState).GetField("<Settings>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(state, Settings);
                typeof(App).GetField("m_appState", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, new Lazy<AppState>(() => state));
                var sentinel = new Window { ShowActivated = false, ShowInTaskbar = false };
                app.MainWindow = sentinel;
                Drain();
                Row(new { scenario = "process", test = name, pid = Environment.ProcessId, thread = GetCurrentThreadId(),
                    startedUtc = DateTime.UtcNow.ToString("O"), bitness = IntPtr.Size * 8, scope = "minimal-App-controlled-providers" });
                action();
                Assert.AreEqual(0, LoggedErrors.Count, "Unexpected production logged exception.");
                Row(new { scenario = "process-end", test = name, pid = Environment.ProcessId, endedUtc = DateTime.UtcNow.ToString("O") });
                completion.SetResult();
            }
            catch (Exception error) { completion.SetException(error); }
            finally
            {
                if (!Dispatcher.CurrentDispatcher.HasShutdownFinished)
                {
                    app.Shutdown(); Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(35)); }
        finally { Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)), "Owned native STA must finish."); }
    }

    private static SettingsViewModel ViewModel(Entity entity) => new(entity, App.Current.Logger, () => Task.FromResult(false));
    private static bool Alive => !Dispatcher.CurrentDispatcher.HasShutdownStarted && !Dispatcher.CurrentDispatcher.HasShutdownFinished;
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static void Row(object data) => Console.WriteLine("NATIVE_OWNER " + JsonSerializer.Serialize(data));
    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Collect()
    {
        for (int pass = 0; pass < 3; pass++) { Drain(); GC.Collect(); GC.WaitForPendingFinalizers(); }
        Drain();
    }
    private static void SettleResources()
    {
        // Give WPF's delayed render/input cleanup the same fixed live-dispatcher
        // interval before every resource sample, including the warmup baseline.
        Complete(Task.Delay(1200)); Collect();
    }
    private static void Complete(Task task)
    {
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(5)) { Drain(); Thread.Yield(); }
        Assert.IsTrue(task.IsCompleted); task.GetAwaiter().GetResult();
    }
    private static List<Exception> Observe(Exception? expected, Action action)
    {
        var result = new List<Exception>();
        DispatcherUnhandledExceptionEventHandler handler = (_, args) =>
        {
            if (expected == null || !ReferenceEquals(args.Exception, expected)) return;
            result.Add(args.Exception); args.Handled = true;
        };
        Dispatcher.CurrentDispatcher.UnhandledException += handler;
        try
        {
            try { action(); }
            catch (Exception error) when (expected != null && ReferenceEquals(error, expected)) { result.Add(error); }
            Drain();
        }
        finally { Dispatcher.CurrentDispatcher.UnhandledException -= handler; }
        return result;
    }
    private static IntPtr Handle(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
        Assert.AreNotEqual(IntPtr.Zero, handle); Assert.IsTrue(IsWindow(handle));
        uint thread = GetWindowThreadProcessId(handle, out uint pid);
        Assert.AreEqual((uint)Environment.ProcessId, pid); Assert.AreEqual(GetCurrentThreadId(), thread);
        Assert.IsNotNull(HwndSource.FromHwnd(handle)); Assert.IsTrue(Application.Current.Windows.Cast<Window>().Contains(window));
        return handle;
    }
    private static void AssertClosed(Window window, IntPtr handle)
    {
        Assert.IsFalse(IsWindow(handle), "Owned HWND survived close.");
        Assert.AreEqual(IntPtr.Zero, new WindowInteropHelper(window).Handle);
        Assert.IsNull(HwndSource.FromHwnd(handle));
        Assert.IsFalse(Application.Current.Windows.Cast<Window>().Contains(window));
    }
    private static void Retention(string owner, Func<WeakReference[]> create)
    {
        for (int i = 0; i < 10; i++) create();
        SettleResources(); using var process = Process.GetCurrentProcess();
        int initialUser = GetGuiResources(process.Handle, 1), initialGdi = GetGuiResources(process.Handle, 0);
        Assert.IsTrue(initialUser > 0);
        var weak = new List<WeakReference>();
        for (int epoch = 1; epoch <= 2; epoch++)
        {
            for (int cycle = 0; cycle < 50; cycle++) weak.AddRange(create());
            SettleResources(); int live = weak.Count(w => w.IsAlive);
            int user = GetGuiResources(process.Handle, 1), gdi = GetGuiResources(process.Handle, 0);
            Row(new { scenario = "retention", owner, epoch, cycles = epoch * 50, references = weak.Count, live,
                initialUser, user, initialGdi, gdi, dispatcherAlive = Alive });
            Assert.AreEqual(0, live, "An owner remains rooted on the live dispatcher.");
            Assert.AreEqual(initialUser, user); Assert.AreEqual(initialGdi, gdi); Assert.IsTrue(Alive);
        }
    }
    public sealed class NeverPage : Border
    {
        public NeverPage(SettingsViewModel vm) => throw new AssertFailedException("Stale navigation constructed a page.");
    }
    public sealed class FailingPage(SettingsViewModel vm) : Border, IDisposable
    {
        private readonly SettingsViewModel m_vm = vm;
        public readonly Exception Failure = new InvalidOperationException("owned page disposal failure (production logs and tolerates)");
        public int Disposals;
        public void Dispose() { Disposals++; throw Failure; }
    }
    private sealed class ErrorSink : ILogEventSink
    {
        public void Emit(LogEvent value) { if (value.Exception != null) LoggedErrors.Add(value.Exception); }
    }
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint process);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr handle, uint command);
}
