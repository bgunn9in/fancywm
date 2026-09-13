using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Data;
using FancyWM.Pages.Settings;
using FancyWM.ViewModels;
using FancyWM.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

internal static partial class Program
{
    private static int BrowserCycles;
    private static uint BrowserPid;
    private static void SettleFrameworkViews()
    {
        // ViewManager deliberately holds non-INCC collections for two purge
        // cycles. Exercise ordinary public binding activity; never call Purge,
        // edit the framework's tables, or dispose a production owner here.
        for (int i = 0; i < 3; i++) { CreateNeutralView(); Settle(); }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateNeutralView() => _ = CollectionViewSource.GetDefaultView(new object[] { new object() });
    private static void CaptureRetentionDump()
    {
        string path = Path.Combine(Output, "browser-retention-red.dmp");
        using var process = Process.GetCurrentProcess();
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        Require(MiniDumpWriteDump(process.Handle, Environment.ProcessId, file.SafeFileHandle.DangerousGetHandle(), 2, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero), "Owned retention dump failed: " + Marshal.GetLastWin32Error());
        Row(new { Kind = "RetentionDump", Path = path, Bytes = file.Length });
    }
    [DllImport("Dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(IntPtr process, int pid, IntPtr file, int type, IntPtr exception, IntPtr streams, IntPtr callback);
    private static void BrowserRetention()
    {
        string data = Path.Combine(Output, "owned-browser-data");
        Require(!Directory.Exists(data), "Browser data already exists.");
        // Process-local override; no registry, browser profile or security-policy changes.
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", data);
        var keeper = new WebView2 { Source = new Uri("about:blank"), Focusable = false };
        var host = new Window { Content = keeper, Width = 400, Height = 300, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        IntPtr keeperHwnd = Ensure(host);
        CoreWebView2Environment? environment = null;
        bool exited = false;
        EventHandler<CoreWebView2BrowserProcessExitedEventArgs> onExit = (_, e) =>
        {
            Require(e.BrowserProcessId == BrowserPid, "Foreign browser exit event.");
            exited = true; Row(new { Kind = "BrowserProcessExited", Pid = e.BrowserProcessId, Reason = e.BrowserProcessExitKind.ToString(), DispatcherAlive = !App.Dispatcher.HasShutdownStarted });
        };
        try
        {
            host.Show(); var initialized = keeper.EnsureCoreWebView2Async(); Wait(() => initialized.IsCompleted, "Keeper browser init"); initialized.GetAwaiter().GetResult();
            environment = keeper.CoreWebView2.Environment; BrowserPid = keeper.CoreWebView2.BrowserProcessId;
            Require(Path.GetFullPath(environment.UserDataFolder) == data, "Browser escaped owned data folder.");
            environment.BrowserProcessExited += onExit;
            using var process = Process.GetProcessById((int)BrowserPid);
            Row(new { Kind = "BrowserEnvironment", Pid = BrowserPid, StartedUtc = process.StartTime.ToUniversalTime(), Executable = process.MainModule!.FileName,
                RuntimeVersion = environment.BrowserVersionString, DataFolder = data, KeeperHwnd = keeperHwnd.ToInt64(), SharedKeeper = true });
            Retention("help-browser", BrowserCycle);
        }
        finally
        {
            host.Content = null; keeper.Dispose(); host.Close();
            if (environment != null)
            {
                Wait(() => exited, "Owned WebView2 process collection exit");
                environment.BrowserProcessExited -= onExit;
            }
        }
        Require(!IsWindow(keeperHwnd), "Browser keeper HWND retained.");
        FinalHandles = [keeperHwnd];
        Write("browser-summary.json", new { Pid = BrowserPid, Exited = exited, KeeperDestroyed = true, DataFolder = data, Cycles = BrowserCycles, SharedKeeper = true });
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] BrowserCycle()
    {
        var vm = new SettingsViewModel(State.Settings);
        var window = new SettingsWindow(vm) { Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        IntPtr hwnd = Ensure(window); var source = HwndSource.FromHwnd(hwnd)!;
        var navigation = Field(window, "m_pages")!;
        try
        {
            window.GoToPage(typeof(HelpPage)); Drain();
            var page = (HelpPage)Field(navigation, "m_current")!;
            var browser = (WebView2)typeof(HelpPage).GetField("Browser", Instance)!.GetValue(page)!;
            browser.Focusable = false;
            browser.NavigationStarting += (_, e) => { if (e.Uri != "about:blank") e.Cancel = true; };
            browser.Source = new Uri("about:blank");
            window.Show();
            var initialized = browser.EnsureCoreWebView2Async(); Wait(() => initialized.IsCompleted, "HelpPage native browser init"); initialized.GetAwaiter().GetResult();
            var core = browser.CoreWebView2;
            Require(core != null && core.BrowserProcessId == BrowserPid, "Help browser did not join the owned runtime.");
            var script = browser.ExecuteScriptAsync("document.readyState"); Wait(() => script.IsCompleted, "Native browser script");
            string state = script.GetAwaiter().GetResult(); Require(state is "\"complete\"" or "\"interactive\"", "Local document not ready.");
            window.GoToPage(typeof(GeneralPage)); Drain(); window.GoToPage(typeof(HelpPage)); Drain();
            Require(ReferenceEquals(page, Field(navigation, "m_current")), "HelpPage cache identity changed.");
            WeakReference[] refs = [new(window), new(source), new(vm), new(page), new(browser), new(core)];
            window.Close(); window.Close(); window.GoToPage(typeof(HelpPage)); Drain();
            Require(!IsWindow(hwnd) && source.IsDisposed && !App.Windows.Cast<Window>().Contains(window), "Help native host retained.");
            Require(page.Content == null && page.DataContext == null && Field(page, "m_browserOwner") == null && Field(navigation, "m_current") == null && Field(navigation, "m_help") == null,
                "Settings page/browser ownership retained.");
            bool rejectedDisposedAccess = false;
            try { _ = browser.CoreWebView2; }
            catch (ObjectDisposedException error) when (error.ObjectName == "Browser") { rejectedDisposedAccess = true; }
            Require(rejectedDisposedAccess, "Disposed browser still accepts controller access.");
            Row(new { Kind = "BrowserLifetime", Cycle = ++BrowserCycles, Hwnd = hwnd.ToInt64(), BrowserPid, NativeScriptCompleted = true,
                CachedPageReused = true, HostDestroyed = true, SourceDisposed = true, BrowserDisposed = true, StaleNavigationInert = true });
            return refs;
        }
        finally { window.Close(); }
    }
}
