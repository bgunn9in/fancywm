using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Diagnostics;
using System.Reactive.Linq;

using FancyWM.DllImports;

using FancyWM.Utilities;
using FancyWM.Windows;
using FancyWM.Models;
using WinMan;
using WinMan.Windows;
using FancyWM.Controls;
using ModernWpf;
using Hardcodet.Wpf.TaskbarNotification;
using System.Reactive.Disposables;
using System.Reactive;
using Serilog;
using FancyWM.Toasts;
using System.Threading;
using FancyWM.Pages.Settings;
using System.Media;
using Microsoft.Extensions.DependencyInjection;
using System.Windows.Media.Animation;
using System.Text;
using FancyWM.Resources;

using Strings = FancyWM.Resources.Strings;
using System.Data;
using System.Windows.Threading;
using SystemParameters = FancyWM.Utilities.SystemParameters;
using System.IO;
using FancyWM.AlgorithmicLayouts;

namespace FancyWM
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, IDisposable
    {
        private readonly Win32Workspace m_workspace;
        private readonly IVirtualDesktopManager m_subscribedDesktopManager;
        private readonly AlgorithmicLayoutCoordinator m_algorithmicLayoutCoordinator;
        private ITilingService m_tiling;
        private readonly CompositeDisposable m_subscriptions;
        private readonly UnmanagedResourceGuard? m_unmanagedResourceGuard;
        private readonly ToastService m_toasts;

        private readonly TimeSpan ToastDurationCommandSequence = TimeSpan.FromMilliseconds(3000);
        private readonly TimeSpan ToastDurationCommandSequenceWithContextHints = TimeSpan.FromMilliseconds(9000);
        private readonly TimeSpan ToastDurationLong = TimeSpan.FromMilliseconds(3000);
        private readonly TimeSpan ToastDurationShort = TimeSpan.FromMilliseconds(1000);
        private readonly TimeSpan RateReviewDelay = TimeSpan.FromMinutes(30);
        private readonly double ResizeDisplayPercentage = 1.0 / 12.0;

        private readonly ILogger m_logger;

        private IVirtualDesktop? m_prevDesktop = null;
        private IDisplay? m_prevDisplay = null;
        private IDisplay? m_currentDisplay = null;

        private LowLevelHotkey[]? m_cmdHks;
        private LowLevelHotkey? m_capsLockHk;

        private readonly TaskbarIcon m_notifyIcon;
        private readonly CountdownTimer m_hideCountdownTimer;
        private readonly ContextMenu m_contextMenu;
        private readonly bool m_explorerHasVirtualDesktopTooltip;
        private readonly LowLevelKeyboardHook m_llkbdHook;
        private KeybindingDictionary m_keybindings;
        private readonly IntPtr m_hwnd;
        private readonly AnimationThread m_animationThread;
        private bool m_enableRateReviewRequests;
        private DateTime m_reviewTooltipShown;
        private bool m_showContextHints;
        private bool m_soundOnFailure;
        private bool m_checkForUpdates;
        private readonly Stopwatch m_stopwatch;
        private readonly IMicaProvider m_micaProvider;
        private readonly ModifierWindowMover m_mvm;
        private long m_cmdSequenceId = 0;
        private bool m_showFocusDuringAction;
        private bool m_notifyVirtualDesktopServiceIncompatibility;
        private LowLevelHotkey[] m_directHks = [];
        private DispatcherTimer m_dispatcherTimer;
        private readonly CancellationTokenSource m_shutdownCancellation = new();
        private readonly BindingErrorListener m_bindingErrorListener;
        private bool m_disposed;
        private readonly MainWindowLifetime m_lifetime;
        private readonly LoadedUpdateCheck m_loadedUpdateCheck;
        private Task m_tilingShutdown = Task.CompletedTask;
        private bool m_restoreLayoutOnShutdown;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
        public MainWindow()
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
        {
            m_lifetime = new MainWindowLifetime(DisposeOwnedResources,
                CompleteOwnedOperationsAsync,
                DisposeDependentResources, DispatchCleanupAsync);
            try
            {
                m_lifetime.Run(InitializeComponent);

                m_logger = App.Current.Logger;

                m_loadedUpdateCheck = new LoadedUpdateCheck(
                    m_lifetime,
                    callback => Dispatcher.InvokeAsync(callback).Task.Unwrap(),
                    error => m_logger.Error(error, "Update check loop failed!"),
                    () => m_shutdownCancellation.IsCancellationRequested);

                m_lifetime.Acquire(out m_unmanagedResourceGuard, ApplySystemParameters);

                m_lifetime.Acquire(out m_bindingErrorListener, () => BindingErrorListener.Listen(OnBindingError));

                m_hwnd = new WindowInteropHelper(this).EnsureHandle();

                m_lifetime.Acquire(out m_workspace, () => new Win32Workspace());
                m_lifetime.Run(() => m_workspace.UnhandledException += OnWorkspaceUnhandledException);

                m_lifetime.Run(m_workspace.Open);

                m_lifetime.Acquire(out m_algorithmicLayoutCoordinator, () => new AlgorithmicLayoutCoordinator(
                    m_workspace,
                    Dispatcher));

                m_lifetime.Acquire(out m_toasts, () => new ToastService(m_workspace));

                m_lifetime.Acquire(out m_animationThread, () => new AnimationThread(m_workspace.DisplayManager.Displays.Select(x => x.RefreshRate).Max()));

                // Keep the already acquired borrowed manager for cleanup; its
                // workspace getter would otherwise initialize COM during unwind.
                m_subscribedDesktopManager = m_workspace.VirtualDesktopManager;
                m_lifetime.Run(() => m_subscribedDesktopManager.CurrentDesktopChanged += OnVirtualDesktopChanged);
                m_lifetime.Run(() => m_subscribedDesktopManager.DesktopRemoved += OnVirtualDesktopRemoved);
                m_lifetime.Run(() => m_workspace.FocusedWindowChanged += OnFocusedWindowChanged);

                m_lifetime.Acquire(out m_mvm, () => new ModifierWindowMover(m_workspace, App.Current.Services.GetRequiredService<LowLevelMouseHook>()));

                var settings = App.Current.AppState.Settings;
                var runtimeSettings = RuntimeSettingsSubscription.Observe(
                    settings,
                    value =>
                    {
                        if (m_disposed) { return; }
                        m_keybindings = value.Keybindings;
                        m_enableRateReviewRequests = value.RemindToRateReview;
                        m_showContextHints = value.ShowContextHints;
                        m_soundOnFailure = value.SoundOnFailure;
                        m_checkForUpdates = value.CheckForUpdates;
                        m_showFocusDuringAction = value.ShowFocusDuringAction;
                        m_notifyVirtualDesktopServiceIncompatibility = value.NotifyVirtualDesktopServiceIncompatibility;
                        m_mvm.IsEnabled = value.ModifierMoveWindow;
                        m_mvm.AutoFocus = value.ModifierMoveWindowAutoFocus;
                    },
                    (value, cancellationToken) => Dispatcher.InvokeAsync(() =>
                    {
                        if (m_disposed) { return; }
                        if (value.OverrideAccentColor)
                        {
                            ThemeManager.Current.AccentColor = value.CustomAccentColor;
                        }
                        else
                        {
                            ThemeManager.Current.AccentColor = null;
                        }
                    }, DispatcherPriority.Normal, cancellationToken).Task);

                var startupSettings = settings
                    .Take(1)
                    .Do(x =>
                    {
                        if (x.ShowStartupWindow)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                if (m_disposed) { return; }
                                new StartupWindow(new ViewModels.SettingsViewModel(App.Current.AppState.Settings)).Show();
                            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        }
                    });

                var activationHotkeySettings = RuntimeSettingsSubscription.ApplyLatest(
                    settings.Select(x => x.ActivationHotkey).DistinctUntilChanged(),
                    (value, cancellationToken) => Dispatcher.InvokeAsync(() =>
                    {
                        if (m_disposed) { return; }
                        RebindActivationHotkey(value);
                    }, DispatcherPriority.Normal, cancellationToken).Task);

                var activateOnCapsLockSetting = RuntimeSettingsSubscription.ApplyLatest(
                    settings.Select(x => x.ActivateOnCapsLock).DistinctUntilChanged(),
                    (value, cancellationToken) => Dispatcher.InvokeAsync(() =>
                    {
                        if (m_disposed) { return; }
                        RebindCapsLockHotkey(value);
                    }, DispatcherPriority.Normal, cancellationToken).Task);

                var keybindingsSettings = RuntimeSettingsSubscription.ApplyLatest(
                    RuntimeSettingsSubscription.DirectHotkeys(settings),
                    (value, cancellationToken) => Dispatcher.InvokeAsync(() =>
                    {
                        if (m_disposed) { return; }
                        RebindDirectHotkeys(value);
                    }, DispatcherPriority.Normal, cancellationToken).Task);

                var multiMonitorObservable = RuntimeSettingsSubscription.Initialize(
                    settings,
                    (initialSettings, cancellationToken) => Dispatcher.InvokeAsync(() =>
                    {
                        if (m_disposed) { return; }
                        m_lifetime.Initialize(() =>
                        {
                            if (initialSettings.MultiMonitorSupport)
                            {
                                m_lifetime.Acquire<ITilingService>(out m_tiling, () => new MultiDisplayTilingService(
                                    m_workspace,
                                    m_animationThread,
                                    settings,
                                    m_algorithmicLayoutCoordinator));
                            }
                            else
                            {
                                m_lifetime.Acquire<ITilingService>(out m_tiling, () => new TilingService(
                                    m_workspace,
                                    m_workspace.DisplayManager.PrimaryDisplay,
                                    m_animationThread,
                                    settings,
                                    m_algorithmicLayoutCoordinator,
                                    true));
                            }

                            m_lifetime.Run(() => m_tiling.PlacementFailed += OnTilingFailed);
                            m_lifetime.Run(() => m_tiling.AlgorithmicLayoutChanged += OnAlgorithmicLayoutChanged);
                            m_lifetime.Run(m_tiling.Start);
                        });
                    }, DispatcherPriority.Normal, cancellationToken).Task);

                var exclusionListSettings = RuntimeSettingsSubscription.ApplyLatest(
                    RuntimeSettingsSubscription.Exclusions(settings),
                    (x, cancellationToken) => Dispatcher.InvokeAsync(() =>
                    {
                        if (m_disposed) { return; }
                        var processMatchers = x.ProcessIgnoreList.Select(x => new ByProcessNameMatcher(x));
                        var classMatchers = x.ClassIgnoreList.Select(x => new ByClassNameMatcher(x));
                        m_tiling!.ExclusionMatchers = m_tiling.ExclusionMatchers
                            .Where(m => m is not ByProcessNameMatcher && m is not ByClassNameMatcher)
                            .Concat(processMatchers)
                            .Concat(classMatchers)
                            .ToArray();
                    }, DispatcherPriority.Normal, cancellationToken).Task);

                m_llkbdHook = App.Current.Services.GetRequiredService<LowLevelKeyboardHook>();

                m_lifetime.Acquire(out m_subscriptions, () => m_lifetime.SubscribeAll(
                    () => runtimeSettings.Subscribe(new NotifyUnhandledObserver<Settings>()),
                    () => startupSettings.Subscribe(new NotifyUnhandledObserver<Settings>()),
                    () => activationHotkeySettings.Subscribe(new NotifyUnhandledObserver<Unit>()),
                    () => activateOnCapsLockSetting.Subscribe(new NotifyUnhandledObserver<Unit>()),
                    () => keybindingsSettings.Subscribe(new NotifyUnhandledObserver<Unit>()),
                    () => multiMonitorObservable
                        .Concat(exclusionListSettings)
                        .Subscribe(_ => { }, OnTilingInitializationFailed)), MainWindowLifetime.ReleaseSubscriptions);

                m_lifetime.Acquire(out m_notifyIcon, () => new TaskbarIcon());
                m_lifetime.Run(() => m_notifyIcon.Icon = Files.Icon);
                m_lifetime.Run(() => m_notifyIcon.TrayLeftMouseDown += OnNotifyIconLeftMouseDown);
                m_lifetime.Run(() => m_notifyIcon.TrayRightMouseDown += OnNotifyIconRightMouseDown);
                m_lifetime.Run(() => m_notifyIcon.Visibility = Visibility.Visible);
                m_lifetime.Run(() => m_notifyIcon.TrayBalloonTipClicked += OnBalloonTipClicked);

                m_hideCountdownTimer = new CountdownTimer();
                m_contextMenu = (ContextMenu)FindResource("NotifierContextMenu");
                m_explorerHasVirtualDesktopTooltip = ExplorerFeature.HasVirtualDesktopTooltip();

                m_stopwatch = new Stopwatch();
                m_stopwatch.Start();

                m_lifetime.Acquire(out m_dispatcherTimer,
                    () => new DispatcherTimer(DispatcherPriority.Background), timer => timer.Stop());
                m_lifetime.Run(() => m_dispatcherTimer.Interval = TimeSpan.FromMilliseconds(250));
                m_lifetime.Run(() => m_dispatcherTimer.Tick += OnDispatcherTimerTick);
                m_lifetime.Run(m_dispatcherTimer.Start);

                m_lifetime.Run(() => PInvoke.SetWindowLong(new(m_hwnd), GetWindowLongPtr_nIndex.GWL_EXSTYLE,
                    (int)(WINDOWS_EX_STYLE.WS_EX_TOOLWINDOW | WINDOWS_EX_STYLE.WS_EX_TOPMOST)));

                if (App.Current.Services.GetService<IMicaProvider>() is IMicaProvider micaProvider)
                {
                    m_micaProvider = micaProvider;
                    m_lifetime.Run(() => App.Current.Resources["MicaPrimaryColor"] = m_micaProvider.PrimaryColor);
                    m_lifetime.Run(() => m_micaProvider.PrimaryColorChanged += OnMicaProviderPrimaryColorChanged);
                }

                m_lifetime.Run(() => Loaded += OnLoaded);
                m_lifetime.Run(Show);
                m_lifetime.Run(Hide);
                m_lifetime.Run(() => ((HwndSource)PresentationSource.FromVisual(this)).AddHook(WndProc));

                m_lifetime.Run(() => m_subscriptions.Add(ThemeEngineManager.Initialize(Path.GetFullPath("themes"), "custom.css")));
            }
            catch (Exception error)
            {
                m_disposed = true;
                m_lifetime.ConstructionFailed(error);
                throw;
            }
        }

        private void OnTilingInitializationFailed(Exception error)
        {
            m_lifetime.InitializationFailed(error, new NotifyUnhandledObserver<Unit>().OnError, cleanup =>
            {
                void HandleFailure()
                {
                    m_disposed = true;
                    cleanup();
                }
                if (Dispatcher.CheckAccess()) { HandleFailure(); }
                else { _ = Dispatcher.InvokeAsync(HandleFailure, DispatcherPriority.Send); }
            });
        }

        private UnmanagedResourceGuard? ApplySystemParameters()
        {
            bool windowArranging;
            try
            {
                windowArranging = SystemParameters.Instance.WindowArranging;
                SystemParameters.Instance.WindowArranging = false;
            }
            catch
            {
                return null;
            }
            return new(() =>
            {
                SystemParameters.Instance.WindowArranging = windowArranging;
            });
        }

        private void OnDispatcherTimerTick(object? sender, EventArgs e)
        {
            if (m_disposed || m_tiling == null)
            {
                return;
            }

            bool anyDiscovered = m_tiling.DiscoverWindows();
            if (anyDiscovered)
            {
                m_tiling.Refresh();
            }
        }

        private void OnBindingError(string? msg)
        {
#if DEBUG
            throw new DataException($"Binding error: {msg}");
#endif
        }

        private void OnFocusedWindowChanged(object? sender, FocusedWindowChangedEventArgs e)
        {
            if (m_disposed) { return; }
            if (e.NewFocusedWindow != null)
            {
                Dispatcher.InvokeAsync(() => m_lifetime.RunIfActive(() =>
                {
                    if (m_contextMenu != null) { m_contextMenu.IsOpen = false; }
                }));

                var currentDisplay = m_workspace.DisplayManager.Displays.FirstOrDefault(x => x.Bounds.Contains(e.NewFocusedWindow.Position.Center));
                if (currentDisplay != null)
                {
                    OnCurrentDisplayChanged(currentDisplay);
                }
            }
        }

        private void OnCurrentDisplayChanged(IDisplay currentDisplay)
        {
            if (m_currentDisplay != currentDisplay)
            {
                m_prevDisplay = m_currentDisplay;
                m_currentDisplay = currentDisplay;
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (!m_disposed && msg == Constants.WM_COPYDATA)
            {
                byte[] message = WindowCopyDataHelper.Receive(lParam);
                _ = Dispatcher.InvokeAsync(() => m_lifetime.RunIfActiveAsync(async () =>
                {
                    var actionName = Encoding.Default.GetString(message);
                    await ExecuteActionFromStringAsync(actionName);
                }));
            }
            handled = false;
            return IntPtr.Zero;
        }

        private void OnMicaProviderPrimaryColorChanged(object? sender, MicaOptionsChangedEventArgs e)
            => HandleMicaPrimaryColorChanged(
                m_lifetime,
                callback => { _ = Dispatcher.InvokeAsync(callback); },
                () =>
                {
                    if (m_disposed) { return; }
                    App.Current.Resources["MicaPrimaryColor"] = m_micaProvider.PrimaryColor;
                });

        internal static void HandleMicaPrimaryColorChanged(
            MainWindowLifetime lifetime,
            Action<Action> post,
            Action apply)
        {
            lifetime.RunIfActive(() => post(() => lifetime.RunIfActive(apply)));
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (m_disposed) { return; }
            if (m_workspace.VirtualDesktopManager.GetType().Name == "DummyVirtualDesktopManager" && Environment.OSVersion.Version.Build >= 17661 && m_notifyVirtualDesktopServiceIncompatibility)
            {
                if (new Windows.MessageBox { IconGlyph = "\xF1AD", Title = Strings.Messages_WindowsVersionNotSupported_Caption + " OS Build: " + Environment.OSVersion.Version.Build, Message = Strings.Messages_WindowsVersionNotSupported_Description }.ShowDialog() == true)
                {
                    m_logger.Warning("Unsupported OS Version: " + Environment.OSVersion.Version.ToString());
                }
            }

            if (App.Current.Services.GetService<ReleaseChecker>() is ReleaseChecker releaseChecker)
            {
                m_loadedUpdateCheck.Start(() => UpdateCheckLoopAsync(releaseChecker));
            }
        }

        private async Task UpdateCheckLoopAsync(ReleaseChecker releaseChecker)
        {
            while (m_checkForUpdates && !m_shutdownCancellation.IsCancellationRequested)
            {
                if (ShouldCheckForUpdatesToday())
                {
                    await CheckForUpdatesAsync(releaseChecker);
                }
                await Task.Delay(TimeSpan.FromHours(3), m_shutdownCancellation.Token);
            }
        }

        private async Task CheckForUpdatesAsync(ReleaseChecker releaseChecker)
        {
            try
            {
                var latestVersion = await releaseChecker.GetLatestStableVersionAsync("fancywm", "FancyWM", m_shutdownCancellation.Token);
                var currentVersion = new Version(App.Current.VersionString);
                if (!m_disposed && latestVersion > currentVersion)
                {
                    await ShowToastAsync($"{Strings.Messages_NewerVersionAvailable}: {latestVersion}", ToastDurationLong);
                }
            }
            catch (OperationCanceledException) when (m_shutdownCancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                m_logger.Error(ex, "Update check failed!");
            }
        }

        private bool ShouldCheckForUpdatesToday()
        {
#if DEBUG
            return true;
#endif
            int getBucket(int dayOfYear) => (dayOfYear * 13) % 5;
            try
            {
                var installDayOfYear = File.GetCreationTime(App.Current.AppState.Settings.FullPath).DayOfYear;
                var todayDayOfYear = DateTime.Now.DayOfYear;
                return getBucket(installDayOfYear) == getBucket(todayDayOfYear);
            }
            catch
            {
                return true;
            }
        }

        private void OnWorkspaceUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            m_logger.Error((Exception)e.ExceptionObject, "Unhandled exception thrown from " + m_workspace);
            Dispatcher.RethrowOnDispatcher((Exception)e.ExceptionObject!);
        }

        private static IEnumerable<(KeyCode[] KeyA, KeyCode KeyB)> CreateHotkeyVariants(ActivationHotkey hk)
        {
            KeyCode[] keys = [.. hk.ModifierKeys, hk.Key];
            if (hk.ModifierKeys.Length == 1)
            {
                yield return ([keys[0]], keys[1]);
                yield return ([keys[1]], keys[0]);
            }
            else if (hk.ModifierKeys.Length == 2)
            {
                yield return ([keys[0], keys[1]], keys[2]);
                yield return ([keys[0], keys[2]], keys[1]);
                yield return ([keys[1], keys[0]], keys[2]);
                yield return ([keys[1], keys[2]], keys[0]);
                yield return ([keys[2], keys[0]], keys[1]);
                yield return ([keys[2], keys[1]], keys[0]);
            }
            else
            {
                throw new NotSupportedException();
            }
        }

        private void RebindActivationHotkey(ActivationHotkey hk)
        {
            if (m_cmdHks != null)
            {
                foreach (var cmdHk in m_cmdHks)
                {
                    cmdHk.Pressed -= OnCmdSequenceBegin;
                    cmdHk.Dispose();
                }
            }

            m_cmdHks = CreateHotkeyVariants(hk)
                .Select(hk => new LowLevelHotkey(m_llkbdHook, hk.KeyA, hk.KeyB)
                {
                    HideKeyPress = false,
                    ScanOnRelease = true,
                    SideAgnostic = true,
                    ClearModifiersOnMiss = true,
                }).ToArray();

            foreach (var cmdHk in m_cmdHks)
            {
                cmdHk.Pressed += OnCmdSequenceBegin;
            }
        }

        private void RebindCapsLockHotkey(bool enabled)
        {
            if (!enabled)
            {
                if (m_capsLockHk != null)
                {
                    m_capsLockHk.Pressed -= OnCmdSequenceBegin;
                    m_capsLockHk.Dispose();
                    m_capsLockHk = null;
                }
                return;
            }

            m_capsLockHk = new LowLevelHotkey(m_llkbdHook, [], KeyCode.CapsLock)
            {
                HideKeyPress = true,
                ScanOnRelease = false,
                SideAgnostic = false,
                ClearModifiersOnMiss = true,
            };
            m_capsLockHk.Pressed += OnCmdSequenceBegin;
        }

        private void RebindDirectHotkeys(KeybindingDictionary keybindings)
        {
            foreach (var hk in m_directHks)
            {
                hk.Dispose();
            }

            var newHotkeys = new List<LowLevelHotkey>();
            var failedHotkeys = new List<Keybinding>();
            foreach (var x in keybindings
                .Where(x => x.Value?.IsDirectMode == true))
            {
                try
                {
                    var (modifiers, key) = KeyCodeHelper.GetModifierAndKeyCode(x.Value!.Keys);
                    var hk = new LowLevelHotkey(m_llkbdHook, modifiers, key)
                    {
                        HideKeyPress = true,
                        ScanOnRelease = false,
                        ClearModifiersOnMiss = false,
                        SideAgnostic = false,
                    };
                    hk.Pressed += delegate { OnDirectHotkeyPressed(x.Key); };
                    newHotkeys.Add(hk);
                }
                catch (Win32Exception)
                {
                    failedHotkeys.Add(x.Value!);
                }
                catch (ArgumentException)
                {
                    // Failed because bad keys
                    failedHotkeys.Add(x.Value!);
                }
            }

            if (failedHotkeys.Count > 0)
            {
                OnDirectHotkeyRegistrationFailed(failedHotkeys);
            }
            m_directHks = [.. newHotkeys];
        }

        private async void OnDirectHotkeyRegistrationFailed(IEnumerable<Keybinding> hotkeys)
        {
            string text = string.Join(", ", hotkeys.Select(x => string.Join(" + ", x.Keys.Select(KeyDescriptions.GetDescription))));
            await ShowToastAsync(Strings.Messages_DirectHotkeyRegistrationFailed, text, ToastDurationLong);
        }

        private async void OnDirectHotkeyPressed(BindableAction action)
            => await HandleDirectHotkeyPressedAsync(
                m_lifetime,
                action,
                ExecuteAction,
                HandleCommandExceptionAsync);

        internal delegate void DirectHotkeyActionExecutor(
            BindableAction action,
            ref string? friendlyActionName);

        internal static Task HandleDirectHotkeyPressedAsync(
            MainWindowLifetime lifetime,
            BindableAction action,
            DirectHotkeyActionExecutor executeAction,
            Func<TilingFailedException, string?, Task> presentFailure)
        {
            async Task HandleAsync()
            {
                string? friendlyActionName = null;
                try
                {
                    executeAction(action, ref friendlyActionName);
                }
                catch (TilingFailedException e)
                {
                    await presentFailure(e, friendlyActionName);
                }
            }

            return lifetime.RunIfActiveAsync(HandleAsync);
        }

        private async void OnTilingFailed(object? sender, TilingFailedEventArgs e)
            => await HandleTilingNotificationAsync(m_lifetime, async () =>
            {
                try
                {
                    if (e.FailReason == TilingError.NoValidPlacementExists)
                    {
                        if (e.FailSource == null)
                        {
                            throw new ArgumentException($"{nameof(e.FailSource)} is required for {nameof(e.FailReason)}!");
                        }

                        PlayBeepSound();
                        await ShowToastAsync(
                            $"{Strings.Messages_FloatingModeEnabledFor} {e.FailSource.Title}",
                            Strings.Messages_CannotBeResizedToFit,
                            ToastDurationLong);
                    }
                    else
                    {
                        var reason = GetTilingErrorText(e.FailReason) ?? Strings.Messages_OperationFailed;
                        var hint = !string.IsNullOrWhiteSpace(e.PresentationSafeHint)
                            ? e.PresentationSafeHint
                            : e.FailSource != null
                                ? e.FailSource.GetCachedProcessName() + " " + Strings.Common_Window
                                : null;

                        if (e.RequestsFailureSound)
                        {
                            PlayBeepSound();
                        }
                        await ShowToastAsync(reason, hint, ToastDurationLong);
                    }
                }
                catch (Exception exception)
                {
                    m_logger.Error(
                        exception,
                        "Failed to present tiling failure {FailureReason}",
                        e.FailReason);
                }
            });

        private async void OnAlgorithmicLayoutChanged(
            object? sender,
            AlgorithmicLayoutEvent e)
            => await HandleTilingNotificationAsync(m_lifetime, async () =>
            {
                try
                {
                    var notification = AlgorithmicLayoutNotificationFormatter.Format(
                        e,
                        Strings.Common_Window,
                        GetAlgorithmicDesktopLabel);
                    if (!notification.ShouldShow)
                    {
                        return;
                    }
                    if (AlgorithmicLayoutNotificationFormatter.ShouldPlayFailureSound(
                        notification,
                        m_soundOnFailure))
                    {
                        PlayBeepSound();
                    }
                    await ShowToastAsync(
                        notification.Message!,
                        notification.Hint,
                        ToastDurationLong);
                }
                catch (Exception exception)
                {
                    m_logger.Error(
                        exception,
                        "Failed to present Master + Satellites event {EventKind}; correlation={CorrelationId}",
                        e.Kind,
                        e.CorrelationId);
                }
            });

        internal static Task HandleTilingNotificationAsync(
            MainWindowLifetime lifetime,
            Func<Task> callback)
            => lifetime.RunIfActiveAsync(callback);

        private string GetAlgorithmicDesktopLabel(IVirtualDesktop? desktop)
        {
            if (desktop == null)
            {
                return GetAlgorithmicLayoutString(
                    "AlgorithmicLayout.UnknownDesktop",
                    "another desktop");
            }

            string? name = null;
            try
            {
                var candidateName = desktop.Name;
                if (!string.IsNullOrWhiteSpace(candidateName))
                {
                    name = candidateName;
                }
            }
            catch (Exception exception)
            {
                m_logger.Debug(
                    exception,
                    "Could not read the virtual desktop name for a Master + Satellites notification");
            }

            int index;
            try
            {
                index = m_workspace.VirtualDesktopManager.Desktops
                    .ToList()
                    .FindIndex(candidate => ReferenceEquals(candidate, desktop)
                        || EqualityComparer<IVirtualDesktop>.Default.Equals(
                            candidate,
                            desktop));
            }
            catch (Exception exception)
            {
                m_logger.Debug(
                    exception,
                    "Could not enumerate virtual desktops for a Master + Satellites notification");
                index = -1;
            }
            if (index >= 0)
            {
                var indexedName = string.Format(
                    GetAlgorithmicLayoutString(
                        "AlgorithmicLayout.DesktopIndex",
                        "Desktop {0}"),
                    index + 1);
                return string.IsNullOrWhiteSpace(name)
                    || string.Equals(name, indexedName, StringComparison.OrdinalIgnoreCase)
                        ? indexedName
                        : $"{name} ({indexedName})";
            }
            return name ?? GetAlgorithmicLayoutString(
                    "AlgorithmicLayout.UnknownDesktop",
                    "another desktop");
        }

        private static string GetAlgorithmicLayoutString(
            string key,
            string fallback)
        {
            return Strings.ResourceManager.GetString(key) ?? fallback;
        }

        private async void OnCommandKey(IReadOnlySet<KeyCode> keys)
            => await m_lifetime.RunDelayedIfActiveAsync(
                // Allow focus to return to the target window before movement.
                static cancellationToken => Task.Delay(50, cancellationToken),
                m_shutdownCancellation.Token,
                () => ExecuteCommandKeyAsync(keys));

        private async Task ExecuteCommandKeyAsync(IReadOnlySet<KeyCode> keys)
        {
            var matches = m_keybindings.Where(x => x.Value != null && x.Value.Keys.SetEqualsSideInsensitive(keys));
            var action = matches.FirstOrDefault().Key;
            string? friendlyActionName = null;
            try
            {
                if (matches.Any())
                {
                    ExecuteAction(action, ref friendlyActionName);
                    return;
                }
                else if (keys.Count == 1)
                {
                    var key = keys.First();
                    switch (key)
                    {
                        case KeyCode.F12:
                            OpenHelp();
                            return;
                        case KeyCode.Snapshot:
                            Debugger.Break();
                            return;
                    }
                }
                else if (keys.SetEquals([KeyCode.LeftShift, KeyCode.Snapshot]))
                {
                    throw new Exception("Program break requested!");
                }

                if (keys.Count == 2 && keys.Contains(KeyCode.LeftAlt)
                    && (keys.Contains(KeyCode.LeftShift) || keys.Contains(KeyCode.LeftCtrl) || keys.Contains(KeyCode.LeftAlt)
                    || keys.Contains(KeyCode.RightShift) || keys.Contains(KeyCode.RightCtrl) || keys.Contains(KeyCode.RightAlt)))
                {
                    return;
                }

                PlayBeepSound();
                await ShowToastAsync(Strings.Messages_UnrecognizedKeybinding, $"{Strings.Messages_NothingIsAssignedTo} [{keys.ToPrettyString()}]!", ToastDurationLong);
            }
            catch (TilingFailedException e)
            {
                await HandleCommandExceptionAsync(e, friendlyActionName);
            }
        }

        private async Task HandleCommandExceptionAsync(TilingFailedException e, string? friendlyActionName)
        {
            var messageText = friendlyActionName != null
                ? Strings.Messages_CouldNot + " " + friendlyActionName + "!"
                : Strings.Messages_OperationFailed;
            var hintText = e is AlgorithmicLayoutCommandException algorithmic
                ? algorithmic.UserHint
                : GetTilingErrorText(e.FailReason);

            PlayBeepSound();
            await ShowToastAsync(messageText, hintText, ToastDurationLong);
        }

        private async Task ExecuteActionFromStringAsync(string actionName)
        {
            BindableAction action;
            try
            {
                action = (BindableAction)Enum.Parse(typeof(BindableAction), actionName);
            }
            catch (ArgumentException)
            {
                _ = ShowToastAsync($"{Strings.Messages_ReceivedInvalidCommand} \"{actionName}\"!", ToastDurationLong);
                return;
            }

            string? friendlyActionName = null;
            try
            {
                ExecuteAction(action, ref friendlyActionName);
            }
            catch (TilingFailedException e)
            {
                await HandleCommandExceptionAsync(e, friendlyActionName);
            }
        }

        private void ExecuteAction(BindableAction action, ref string? friendlyActionName)
        {
            switch (action)
            {
                case BindableAction.CreateVerticalPanel:
                    friendlyActionName = "wrap in vertical panel";
                    OnSplitVHotkeyPressed();
                    return;
                case BindableAction.CreateHorizontalPanel:
                    friendlyActionName = "wrap in horizontal panel";
                    OnSplitHHotkeyPressed();
                    //_ = ShowInfoToastAsync("Horizontal Panel", ToastDurationShort);
                    return;
                case BindableAction.CreateStackPanel:
                    friendlyActionName = "wrap in stack panel";
                    //_ = ShowInfoToastAsync("Stack Panel", ToastDurationShort);
                    m_tiling.Stack();
                    return;
                case BindableAction.ToggleMasterSatelliteLayout:
                    friendlyActionName = "toggle Master + Satellites layout";
                    m_tiling.ToggleMasterSatelliteLayout();
                    return;
                case BindableAction.PromoteFocusedWindowToMaster:
                    friendlyActionName = "promote focused window to master";
                    m_tiling.PromoteFocusedWindowToMaster();
                    return;
                case BindableAction.SwapMasterSide:
                    friendlyActionName = "swap master side";
                    m_tiling.SwapMasterSide();
                    return;
                case BindableAction.ToggleSatelliteOrientation:
                    friendlyActionName = "toggle satellite orientation";
                    m_tiling.ToggleSatelliteOrientation();
                    return;
                case BindableAction.ResetMasterRatio:
                    friendlyActionName = "reset master ratio";
                    m_tiling.ResetMasterRatio();
                    return;
                case BindableAction.RebalanceMasterSatelliteLayout:
                    friendlyActionName = "rebalance Master + Satellites layout";
                    m_tiling.RebalanceMasterSatelliteLayout();
                    return;
                case BindableAction.RefreshWorkspace:
                    friendlyActionName = "refresh workspace";
                    m_tiling.Refresh();
                    //_ = ShowInfoToastAsync("Refresh Workspace", ToastDurationShort);
                    return;
                case BindableAction.ShowDesktop:
                    friendlyActionName = "show desktop";
                    m_tiling.ToggleDesktop();
                    return;
                case BindableAction.ToggleFloatingMode:
                    friendlyActionName = "float window";
                    m_tiling.Float();
                    //_ = ShowInfoToastAsync($"Floating Mode: {(state ? "On" : "off")}", ToastDurationShort);
                    return;
                case BindableAction.MoveFocusLeft:
                    friendlyActionName = "move focus left";
                    m_tiling.MoveFocus(Layouts.Tiling.TilingDirection.Left);
                    return;
                case BindableAction.MoveFocusRight:
                    friendlyActionName = "move focus right";
                    m_tiling.MoveFocus(Layouts.Tiling.TilingDirection.Right);
                    return;
                case BindableAction.MoveFocusUp:
                    friendlyActionName = "move focus up";
                    m_tiling.MoveFocus(Layouts.Tiling.TilingDirection.Up);
                    return;
                case BindableAction.MoveFocusDown:
                    friendlyActionName = "move focus down";
                    m_tiling.MoveFocus(Layouts.Tiling.TilingDirection.Down);
                    return;
                case BindableAction.PullWindowUp:
                    friendlyActionName = "move to upper level";
                    m_tiling.PullUp();
                    //_ = ShowInfoToastAsync("Pull Up", ToastDurationShort);
                    return;
                case BindableAction.MoveLeft:
                    friendlyActionName = "move left";
                    m_tiling.MoveWindow(Layouts.Tiling.TilingDirection.Left);
                    return;
                case BindableAction.MoveUp:
                    friendlyActionName = "move up";
                    m_tiling.MoveWindow(Layouts.Tiling.TilingDirection.Up);
                    return;
                case BindableAction.MoveRight:
                    friendlyActionName = "move right";
                    m_tiling.MoveWindow(Layouts.Tiling.TilingDirection.Right);
                    return;
                case BindableAction.MoveDown:
                    friendlyActionName = "move down";
                    m_tiling.MoveWindow(Layouts.Tiling.TilingDirection.Down);
                    return;
                case BindableAction.SwapLeft:
                    friendlyActionName = "swap left";
                    m_tiling.SwapFocus(Layouts.Tiling.TilingDirection.Left);
                    return;
                case BindableAction.SwapUp:
                    friendlyActionName = "swap up";
                    m_tiling.SwapFocus(Layouts.Tiling.TilingDirection.Up);
                    return;
                case BindableAction.SwapRight:
                    friendlyActionName = "swap right";
                    m_tiling.SwapFocus(Layouts.Tiling.TilingDirection.Right);
                    return;
                case BindableAction.SwapDown:
                    friendlyActionName = "swap down";
                    m_tiling.SwapFocus(Layouts.Tiling.TilingDirection.Down);
                    return;
                case BindableAction.DecreaseWidth:
                    friendlyActionName = "decrease width";
                    m_tiling.Resize(Layouts.Tiling.PanelOrientation.Horizontal, -ResizeDisplayPercentage);
                    return;
                case BindableAction.DecreaseHeight:
                    friendlyActionName = "decrease height";
                    m_tiling.Resize(Layouts.Tiling.PanelOrientation.Vertical, -ResizeDisplayPercentage);
                    return;
                case BindableAction.IncreaseWidth:
                    friendlyActionName = "increase width";
                    m_tiling.Resize(Layouts.Tiling.PanelOrientation.Horizontal, ResizeDisplayPercentage);
                    return;
                case BindableAction.IncreaseHeight:
                    friendlyActionName = "increase height";
                    m_tiling.Resize(Layouts.Tiling.PanelOrientation.Vertical, ResizeDisplayPercentage);
                    return;
                case BindableAction.ToggleManager:
                    friendlyActionName = "toggle window management";
                    if (m_tiling.Active)
                    {
                        m_tiling.Stop();
                        //_ = ShowInfoToastAsync("Window Management: Off", ToastDurationShort);
                    }
                    else
                    {
                        m_tiling.Start();
                        //_ = ShowInfoToastAsync("Window Management: On", ToastDurationShort);
                    }
                    return;
                case BindableAction.Cancel:
                    return;
                case BindableAction.SwitchToPreviousDesktop:
                    OnPreviousDesktopHotkeyPressed();
                    return;
                case BindableAction.SwitchToLeftDesktop:
                    OnDesktopHotkeyPressed(m_workspace.VirtualDesktopManager.Desktops.IndexOf(m_workspace.VirtualDesktopManager.CurrentDesktop) - 1);
                    return;
                case BindableAction.SwitchToRightDesktop:
                    OnDesktopHotkeyPressed(m_workspace.VirtualDesktopManager.Desktops.IndexOf(m_workspace.VirtualDesktopManager.CurrentDesktop) + 1);
                    return;
                case BindableAction.SwitchToDesktop1:
                    OnDesktopHotkeyPressed(0);
                    return;
                case BindableAction.SwitchToDesktop2:
                    OnDesktopHotkeyPressed(1);
                    return;
                case BindableAction.SwitchToDesktop3:
                    OnDesktopHotkeyPressed(2);
                    return;
                case BindableAction.SwitchToDesktop4:
                    OnDesktopHotkeyPressed(3);
                    return;
                case BindableAction.SwitchToDesktop5:
                    OnDesktopHotkeyPressed(4);
                    return;
                case BindableAction.SwitchToDesktop6:
                    OnDesktopHotkeyPressed(5);
                    return;
                case BindableAction.SwitchToDesktop7:
                    OnDesktopHotkeyPressed(6);
                    return;
                case BindableAction.SwitchToDesktop8:
                    OnDesktopHotkeyPressed(7);
                    return;
                case BindableAction.SwitchToDesktop9:
                    OnDesktopHotkeyPressed(8);
                    return;
                case BindableAction.MoveToPreviousDesktop:
                    OnMoveToPreviousDesktopHotkeyPressed();
                    return;
                case BindableAction.MoveToLeftDesktop:
                    OnMoveToDesktopHotkeyPressed(m_workspace.VirtualDesktopManager.Desktops.IndexOf(m_workspace.VirtualDesktopManager.CurrentDesktop) - 1);
                    return;
                case BindableAction.MoveToRightDesktop:
                    OnMoveToDesktopHotkeyPressed(m_workspace.VirtualDesktopManager.Desktops.IndexOf(m_workspace.VirtualDesktopManager.CurrentDesktop) + 1);
                    return;
                case BindableAction.MoveToDesktop1:
                    OnMoveToDesktopHotkeyPressed(0);
                    return;
                case BindableAction.MoveToDesktop2:
                    OnMoveToDesktopHotkeyPressed(1);
                    return;
                case BindableAction.MoveToDesktop3:
                    OnMoveToDesktopHotkeyPressed(2);
                    return;
                case BindableAction.MoveToDesktop4:
                    OnMoveToDesktopHotkeyPressed(3);
                    return;
                case BindableAction.MoveToDesktop5:
                    OnMoveToDesktopHotkeyPressed(4);
                    return;
                case BindableAction.MoveToDesktop6:
                    OnMoveToDesktopHotkeyPressed(5);
                    return;
                case BindableAction.MoveToDesktop7:
                    OnMoveToDesktopHotkeyPressed(6);
                    return;
                case BindableAction.MoveToDesktop8:
                    OnMoveToDesktopHotkeyPressed(7);
                    return;
                case BindableAction.MoveToDesktop9:
                    OnMoveToDesktopHotkeyPressed(8);
                    return;
                case BindableAction.SwitchToPreviousDisplay:
                    OnPreviousDisplayHotkeyPressed();
                    return;
                case BindableAction.SwitchToDisplay1:
                    OnSwitchToDisplayHotkeyPressed(0);
                    return;
                case BindableAction.SwitchToDisplay2:
                    OnSwitchToDisplayHotkeyPressed(1);
                    return;
                case BindableAction.SwitchToDisplay3:
                    OnSwitchToDisplayHotkeyPressed(2);
                    return;
                case BindableAction.SwitchToDisplay4:
                    OnSwitchToDisplayHotkeyPressed(3);
                    return;
                case BindableAction.SwitchToDisplay5:
                    OnSwitchToDisplayHotkeyPressed(4);
                    return;
                case BindableAction.SwitchToDisplay6:
                    OnSwitchToDisplayHotkeyPressed(5);
                    return;
                case BindableAction.SwitchToDisplay7:
                    OnSwitchToDisplayHotkeyPressed(6);
                    return;
                case BindableAction.SwitchToDisplay8:
                    OnSwitchToDisplayHotkeyPressed(7);
                    return;
                case BindableAction.SwitchToDisplay9:
                    OnSwitchToDisplayHotkeyPressed(8);
                    return;
                case BindableAction.MoveToPreviousDisplay:
                    OnMoveToPreviousDisplayHotkeyPressed();
                    return;
                case BindableAction.MoveToDisplay1:
                    OnMoveToDisplayHotkeyPressed(0);
                    return;
                case BindableAction.MoveToDisplay2:
                    OnMoveToDisplayHotkeyPressed(1);
                    return;
                case BindableAction.MoveToDisplay3:
                    OnMoveToDisplayHotkeyPressed(2);
                    return;
                case BindableAction.MoveToDisplay4:
                    OnMoveToDisplayHotkeyPressed(3);
                    return;
                case BindableAction.MoveToDisplay5:
                    OnMoveToDisplayHotkeyPressed(4);
                    return;
                case BindableAction.MoveToDisplay6:
                    OnMoveToDisplayHotkeyPressed(5);
                    return;
                case BindableAction.MoveToDisplay7:
                    OnMoveToDisplayHotkeyPressed(6);
                    return;
                case BindableAction.MoveToDisplay8:
                    OnMoveToDisplayHotkeyPressed(7);
                    return;
                case BindableAction.MoveToDisplay9:
                    OnMoveToDisplayHotkeyPressed(8);
                    return;
                default:
                    throw new NotImplementedException(action.ToString());
            }
        }

        private void PlayBeepSound()
        {
            if (m_soundOnFailure)
            {
                SystemSounds.Beep.Play();
            }
        }

        private static string? GetTilingErrorText(TilingError e)
        {
            return e switch
            {
                TilingError.NoValidPlacementExists => Strings.TilingError_NoValidPlacementExists,
                TilingError.CausesRecursiveNesting => Strings.TilingError_CausesRecursiveNesting,
                TilingError.ModifiesTopLevelPanel => Strings.TilingError_ModifiesTopLevelPanel,
                TilingError.PullsBeyondTopLevelPanel => Strings.TilingError_PullsBeyondTopLevelPanel,
                TilingError.MissingAdjacentWindow => Strings.TilingError_MissingAdjacentWindow,
                TilingError.MissingTarget => Strings.TilingError_MissingTarget,
                TilingError.TargetCannotFit => Strings.TilingError_TargetCannotFit,
                TilingError.InvalidTarget => Strings.TilingError_InvalidTarget,
                TilingError.NestingInStackPanel => Strings.TilingError_NestingInStackPanel,
                TilingError.UnsupportedInAlgorithmicLayout => Strings.ResourceManager.GetString("TilingError.UnsupportedInAlgorithmicLayout"),
                _ => null,
            };
        }

        internal static void OpenSettings()
        {
            if (App.Current.Windows.OfType<SettingsWindow>().Any())
            {
                App.Current.Windows.OfType<SettingsWindow>().First().Activate();
            }
            else
            {
                new SettingsWindow(new ViewModels.SettingsViewModel(App.Current.AppState.Settings)).Show();
            }
        }

        internal static void OpenHelp()
        {
            if (App.Current.Windows.OfType<SettingsWindow>().Any())
            {
                var window = App.Current.Windows.OfType<SettingsWindow>().First();
                window.GoToPage(typeof(HelpPage));
                window.Activate();
            }
            else
            {
                var window = new SettingsWindow(new ViewModels.SettingsViewModel(App.Current.AppState.Settings));
                window.GoToPage(typeof(HelpPage));
                window.Show();
            }
        }

        private async void OnBalloonTipClicked(object? sender, RoutedEventArgs e)
        {
            await m_lifetime.RunIfActiveAsync(async () =>
            {
                if (DateTime.UtcNow - m_reviewTooltipShown < TimeSpan.FromSeconds(10))
                {
                    new AboutWindow().Show();
                    await App.Current.AppState.Settings.SaveAsync(x =>
                    {
                        return x with { RemindToRateReview = false };
                    });
                }
            });
        }

        private async void OnCmdSequenceBegin(object? sender, EventArgs e)
        {
            if (m_disposed) { return; }
            long currentId = ++m_cmdSequenceId;

            m_logger.Debug($"Command sequence {m_cmdSequenceId} started");
            if (m_stopwatch.Elapsed >= RateReviewDelay && m_enableRateReviewRequests)
            {
                m_enableRateReviewRequests = false;
                m_reviewTooltipShown = DateTime.UtcNow;
                m_notifyIcon.ShowBalloonTip(Strings.Messages_EnjoyingFancyWM, Strings.Messages_AskForReview, BalloonIcon.None);
            }

            try
            {
                await await Dispatcher.InvokeAsync(() => m_lifetime.RunIfActiveAsync(async () =>
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(m_shutdownCancellation.Token);
                    cts.CancelAfter(m_showContextHints ? ToastDurationCommandSequenceWithContextHints : ToastDurationCommandSequence);
                    var cancellationToken = cts.Token;
                    using var keyListener = new LowLevelKeyPatternListener(m_llkbdHook);
                    void OnPatternChanged(object? patternSender, KeyPatternChangedEventArgs pattern)
                    {
                        if (m_disposed || m_shutdownCancellation.IsCancellationRequested) { return; }
                        m_logger.Debug($"Command sequence {currentId} detected");
                        cts.Cancel();
                        keyListener.Dispose();
                        m_lifetime.RunIfActive(() =>
                        {
                            if (currentId == m_cmdSequenceId) { OnCommandKey(pattern.Keys); }
                        });
                    }
                    keyListener.PatternChanged += OnPatternChanged;
                    try
                    {
                        var toast = Dispatcher.RunAsync(async () =>
                        {
                            try
                            {
                                // Yield before constructing the toast, then reject a
                                // sequence cancelled while its callback was queued.
                                await Task.Yield();
                                if (m_disposed || cancellationToken.IsCancellationRequested) { return; }
                                await ShowWaitingForActionToast(m_showContextHints, cancellationToken);
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                            }
                            catch (Exception error)
                            {
                                Dispatcher.RethrowOnDispatcher(error);
                            }
                        });

                        bool showFocus = m_showFocusDuringAction;
                        if (showFocus) { m_tiling.ShowPreviewFocus = true; }
                        await toast;
                        if (m_disposed || m_shutdownCancellation.IsCancellationRequested) { return; }
                        if (showFocus)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(300), m_shutdownCancellation.Token);
                            m_lifetime.RunIfActive(() =>
                            {
                                if (m_cmdSequenceId == currentId) { m_tiling.ShowPreviewFocus = false; }
                            });
                        }
                        m_logger.Debug($"Command sequence {currentId} ended");
                    }
                    finally
                    {
                        // The listener may already have queued PatternChanged.
                        // Detach before disposing the linked timeout source.
                        keyListener.PatternChanged -= OnPatternChanged;
                        cts.Cancel();
                    }
                }));
            }
            catch (OperationCanceledException) when (m_shutdownCancellation.IsCancellationRequested)
            {
            }
        }

        private Task ShowToastAsync(string messageText, TimeSpan duration)
        {
            return ShowToastAsync(messageText, null, duration);
        }

        private async Task ShowToastAsync(string messageText, string? hintText, TimeSpan duration)
        {
            using var cancellation = new CancellationTokenSource(duration);
            await Dispatcher.RunAsync(() => m_toasts.ShowToastAsync(new MessageBoxContent
            {
                Text = messageText,
                HintText = hintText,
            }, cancellation.Token));
        }

        private async Task ShowToastAsync(string messageText, string? hintText, CancellationToken cancellationToken)
        {
            await Dispatcher.RunAsync(() => m_toasts.ShowToastAsync(new MessageBoxContent
            {
                Text = messageText,
                HintText = hintText,
            }, cancellationToken));
        }

        private async Task ShowWaitingForActionToast(bool showContextHints, CancellationToken cancellationToken)
        {
            if (m_disposed || cancellationToken.IsCancellationRequested) { return; }
            var container = new StackPanel
            {
                Orientation = Orientation.Vertical,
            };
            if (showContextHints)
            {
                var extraContent = new ScrollViewer
                {
                    Visibility = Visibility.Collapsed,
                    Content = GetContextHintsExtraContent(),
                    VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                };

                _ = Dispatcher.RunAsync(async () =>
                {
                    try
                    {
                        await Task.Delay(ToastDurationShort, cancellationToken);
                        if (m_disposed || cancellationToken.IsCancellationRequested) { return; }

                        extraContent.Visibility = Visibility.Visible;
                        extraContent.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        var extraContentHeight = extraContent.DesiredSize.Height + 32;
                        DoubleAnimation opacityAnimation = new(0, 1, TimeSpan.FromMilliseconds(200));
                        DoubleAnimation heightAnimation = new(0, extraContentHeight, TimeSpan.FromMilliseconds(200));
                        extraContent.BeginAnimation(OpacityProperty, opacityAnimation);
                        extraContent.BeginAnimation(MaxHeightProperty, heightAnimation);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                    }
                    catch (Exception error)
                    {
                        Dispatcher.RethrowOnDispatcher(error);
                    }
                });

                container.Children.Add(extraContent);
            }

            container.Children.Add(new MessageBoxContent
            {
                Text = Strings.Messages_WaitingForAction,
                HintText = Strings.Messages_PressF12ForHelp,
            });
            await m_toasts.ShowToastAsync(container, cancellationToken);
        }


        private FrameworkElement GetContextHintsExtraContent()
        {
            var availableActions = GetAvailableActions().ToHashSet();
            var availableKeybindings = new KeybindingDictionary(m_keybindings.Where(x => availableActions.Contains(x.Key)));
            if (availableKeybindings.Any())
            {
                return new KeybindingList
                {
                    Keybindings = availableKeybindings,
                };
            }
            else
            {
                var border = new Border
                {
                    Child = new TextBlock
                    {
                        Text = Strings.Messages_NothingToDo,
                    },
                    Margin = new Thickness(0, 5, 0, 5),
                    Style = FindResource("SettingsItemStyle") as Style
                };
                return border;
            }
        }

        private IEnumerable<BindableAction> GetAvailableActions()
        {
            if (m_tiling.CanSplit(vertical: true))
            {
                yield return BindableAction.CreateVerticalPanel;
            }
            if (m_tiling.CanSplit(vertical: false))
            {
                yield return BindableAction.CreateHorizontalPanel;
            }
            if (m_tiling.CanStack())
            {
                yield return BindableAction.CreateStackPanel;
            }
            if (m_tiling.CanToggleMasterSatelliteLayout())
            {
                yield return BindableAction.ToggleMasterSatelliteLayout;
            }
            if (m_tiling.CanPromoteFocusedWindowToMaster())
            {
                yield return BindableAction.PromoteFocusedWindowToMaster;
            }
            if (m_tiling.CanSwapMasterSide())
            {
                yield return BindableAction.SwapMasterSide;
            }
            if (m_tiling.CanToggleSatelliteOrientation())
            {
                yield return BindableAction.ToggleSatelliteOrientation;
            }
            if (m_tiling.CanResetMasterRatio())
            {
                yield return BindableAction.ResetMasterRatio;
            }
            if (m_tiling.CanRebalanceMasterSatelliteLayout())
            {
                yield return BindableAction.RebalanceMasterSatelliteLayout;
            }
            //yield return BindableAction.RefreshWorkspace;
            if (m_tiling.CanFloat())
            {
                yield return BindableAction.ToggleFloatingMode;
            }
            if (m_tiling.CanMoveFocus(Layouts.Tiling.TilingDirection.Left))
            {
                yield return BindableAction.MoveFocusLeft;
            }
            if (m_tiling.CanMoveFocus(Layouts.Tiling.TilingDirection.Up))
            {
                yield return BindableAction.MoveFocusUp;
            }
            if (m_tiling.CanMoveFocus(Layouts.Tiling.TilingDirection.Right))
            {
                yield return BindableAction.MoveFocusRight;
            }
            if (m_tiling.CanMoveFocus(Layouts.Tiling.TilingDirection.Down))
            {
                yield return BindableAction.MoveFocusDown;
            }
            if (m_tiling.CanPullUp())
            {
                yield return BindableAction.PullWindowUp;
            }
            if (m_tiling.CanMoveFocus(Layouts.Tiling.TilingDirection.Left))
            {
                yield return BindableAction.MoveFocusLeft;
            }
            if (m_tiling.CanMoveFocus(Layouts.Tiling.TilingDirection.Up))
            {
                yield return BindableAction.MoveFocusUp;
            }
            if (m_tiling.CanMoveFocus(Layouts.Tiling.TilingDirection.Right))
            {
                yield return BindableAction.MoveFocusRight;
            }
            if (m_tiling.CanMoveFocus(Layouts.Tiling.TilingDirection.Down))
            {
                yield return BindableAction.MoveFocusDown;
            }
            if (m_tiling.CanMoveWindow(Layouts.Tiling.TilingDirection.Left))
            {
                yield return BindableAction.MoveLeft;
            }
            if (m_tiling.CanMoveWindow(Layouts.Tiling.TilingDirection.Up))
            {
                yield return BindableAction.MoveUp;
            }
            if (m_tiling.CanMoveWindow(Layouts.Tiling.TilingDirection.Right))
            {
                yield return BindableAction.MoveRight;
            }
            if (m_tiling.CanMoveWindow(Layouts.Tiling.TilingDirection.Down))
            {
                yield return BindableAction.MoveDown;
            }
            if (m_tiling.CanSwapFocus(Layouts.Tiling.TilingDirection.Left))
            {
                yield return BindableAction.SwapLeft;
            }
            if (m_tiling.CanSwapFocus(Layouts.Tiling.TilingDirection.Up))
            {
                yield return BindableAction.SwapUp;
            }
            if (m_tiling.CanSwapFocus(Layouts.Tiling.TilingDirection.Right))
            {
                yield return BindableAction.SwapRight;
            }
            if (m_tiling.CanSwapFocus(Layouts.Tiling.TilingDirection.Down))
            {
                yield return BindableAction.SwapDown;
            }
            if (m_tiling.CanResize(Layouts.Tiling.PanelOrientation.Horizontal, ResizeDisplayPercentage))
            {
                yield return BindableAction.IncreaseWidth;
            }
            if (m_tiling.CanResize(Layouts.Tiling.PanelOrientation.Horizontal, -ResizeDisplayPercentage))
            {
                yield return BindableAction.DecreaseWidth;
            }
            if (m_tiling.CanResize(Layouts.Tiling.PanelOrientation.Vertical, ResizeDisplayPercentage))
            {
                yield return BindableAction.IncreaseHeight;
            }
            if (m_tiling.CanResize(Layouts.Tiling.PanelOrientation.Vertical, -ResizeDisplayPercentage))
            {
                yield return BindableAction.DecreaseHeight;
            }
            if (m_prevDesktop != null)
            {
                yield return BindableAction.SwitchToPreviousDesktop;
            }
            if (m_prevDesktop != null && m_tiling.CanFloat())
            {
                yield return BindableAction.MoveToPreviousDesktop;
            }
            if (m_prevDisplay != null)
            {
                yield return BindableAction.SwitchToPreviousDisplay;
            }
            if (m_prevDisplay != null && m_tiling.CanFloat())
            {
                yield return BindableAction.MoveToPreviousDisplay;
            }

            yield return BindableAction.ShowDesktop;
            yield return BindableAction.Cancel;
            //yield return BindableAction.ToggleManager;
        }

        private void OnSplitVHotkeyPressed()
        {
            m_logger.Debug("Creating a vertical split...");
            m_tiling.Split(vertical: true);
        }

        private void OnSplitHHotkeyPressed()
        {
            m_logger.Debug("Creating a horizontal split...");
            m_tiling.Split(vertical: false);
        }

        private void OnNotifyIconLeftMouseDown(object? sender, RoutedEventArgs e)
        {
            m_lifetime.RunIfActive(() =>
            {
                m_contextMenu.IsOpen = false;
                OpenSettings();
            });
        }

        private void OnNotifyIconRightMouseDown(object? sender, RoutedEventArgs e)
        {
            m_lifetime.RunIfActive(() => m_contextMenu.IsOpen = true);
        }

        private async void OnVirtualDesktopChanged(object? sender, CurrentDesktopChangedEventArgs e)
            => await HandleVirtualDesktopChangedAsync(m_lifetime,
                () => m_logger.Debug("Virtual desktop changed..."),
                () => m_prevDesktop = e.OldDesktop,
                ExplorerFeature.HasVirtualDesktopTooltip,
                () => e.NewDesktop.Name,
                name => ShowToastAsync(name, ToastDurationShort));

        internal static async Task HandleVirtualDesktopChangedAsync(
            MainWindowLifetime lifetime,
            Action logChange,
            Action updatePrevious,
            Func<bool> hasTooltip,
            Func<string> getName,
            Func<string, Task> showToast)
        {
            await lifetime.RunIfActiveAsync(async () =>
            {
                logChange();
                updatePrevious();
                if (!hasTooltip())
                {
                    await showToast(getName());
                }
            });
        }

        private void OnVirtualDesktopRemoved(object? sender, DesktopChangedEventArgs e)
            => HandleVirtualDesktopRemoved(m_lifetime,
                () => m_prevDesktop,
                e,
                () => m_prevDesktop = null);

        internal static void HandleVirtualDesktopRemoved(
            MainWindowLifetime lifetime,
            Func<IVirtualDesktop?> getPrevious,
            DesktopChangedEventArgs e,
            Action clearPrevious)
            => lifetime.RunIfActive(() =>
            {
                if (getPrevious() == e.Source)
                {
                    clearPrevious();
                }
            });

        private void SwitchToDesktop(IVirtualDesktop desktop)
        {
            desktop.SwitchTo();
            // Make sure keyboard focus is bound to the foreground window.
            foreach (var window in m_workspace.GetCurrentDesktopSnapshot())
            {
                if (window.IsFocused)
                {
                    FocusHelper.ForceActivate(window.Handle);
                    break;
                }
            }
        }

        private async Task MoveToDesktopAsync(IVirtualDesktop desktop, IWindow window)
        {
            var formattedTitle = window.Title.Length > 15
                ? $"{window.Title[..15]}..."
                : window.Title;
            if (desktop.HasWindow(window))
            {
                PlayBeepSound();
                await ShowToastAsync($"\"{formattedTitle}\" {Strings.Messages_IsAlreadyOn} {desktop.Name}!", ToastDurationShort);
            }
            else
            {
                m_logger.Debug("Moving window to designated desktop...");
                desktop.MoveWindow(window);
                m_tiling.Refresh();
                await ShowToastAsync($"{Strings.Common_Moved} \"{formattedTitle}\" {Strings.Common_To} {desktop.Name}.", ToastDurationShort);
            }
        }

        private async void OnPreviousDesktopHotkeyPressed()
        {
            if (m_prevDesktop == null)
            {
                PlayBeepSound();
                await ShowToastAsync(Strings.Messages_NoPreviousDesktop, ToastDurationShort);
                return;
            }

            m_logger.Debug("Switching to previous desktop...");
            SwitchToDesktop(m_prevDesktop);
        }

        private async void OnMoveToPreviousDesktopHotkeyPressed()
        {
            if (m_workspace.FocusedWindow is not IWindow window)
            {
                PlayBeepSound();
                await ShowToastAsync(Strings.Messages_NoFocusedWindow, ToastDurationShort);
                return;
            }
            if (m_prevDesktop == null)
            {
                PlayBeepSound();
                await ShowToastAsync(Strings.Messages_NoPreviousDesktop, ToastDurationShort);
                return;
            }
            await MoveToDesktopAsync(m_prevDesktop, window);
        }

        private void OnDesktopHotkeyPressed(int desktopIndex)
        {
            m_logger.Debug("Switching to designated desktop...");
            var desktops = m_workspace.VirtualDesktopManager.Desktops;
            if (desktopIndex >= 0 && desktopIndex < desktops.Count)
            {
                SwitchToDesktop(desktops[desktopIndex]);
            }
            else
            {
                PlayBeepSound();
            }
        }

        private async void OnMoveToDesktopHotkeyPressed(int desktopIndex)
        {
            if (m_workspace.FocusedWindow is not IWindow window)
            {
                PlayBeepSound();
                await ShowToastAsync(Strings.Messages_NoFocusedWindow, ToastDurationShort);
                return;
            }
            var desktops = m_workspace.VirtualDesktopManager.Desktops;
            if (desktopIndex < desktops.Count)
            {
                var desktop = desktops[desktopIndex];
                var formattedTitle = window.Title.Length > 15
                    ? $"{window.Title[..15]}..."
                    : window.Title;
                if (desktop.HasWindow(window))
                {
                    PlayBeepSound();
                    await ShowToastAsync($"\"{formattedTitle}\" {Strings.Messages_IsAlreadyOn} {desktop.Name}!", ToastDurationShort);
                }
                else
                {
                    m_logger.Debug("Moving window to designated desktop...");
                    desktop.MoveWindow(window);
                    m_tiling.Refresh();
                    await ShowToastAsync($"{Strings.Common_Moved} \"{formattedTitle}\" {Strings.Common_To} {desktop.Name}.", ToastDurationShort);
                }
            }
            else
            {
                PlayBeepSound();
            }
        }

        private void SwitchToDisplay(IDisplay display)
        {
            var comparer = m_workspace.CreateSnapshotZOrderComparer();
            var mainWindow = m_workspace.GetCurrentDesktopSnapshot()
                .Where(x => x.State != WinMan.WindowState.Minimized)
                .Where(x => display.Bounds.Contains(x.Position.Center))
                .OrderBy(x => x, comparer)
                .FirstOrDefault();

            if (mainWindow == null)
            {
                PlayBeepSound();
            }
            else
            {
                FocusHelper.ForceActivate(mainWindow.Handle);
                m_tiling.Refresh();
                OnCurrentDisplayChanged(display);
            }
        }

        private async Task MoveToDisplayAsync(IDisplay display, IWindow window)
        {
            var formattedTitle = window.Title.Length > 15
                ? $"{window.Title[..15]}..."
                : window.Title;
            try
            {
                if (display.Bounds.Contains(window.Position.Center))
                {
                    PlayBeepSound();
                    await ShowToastAsync($"\"{formattedTitle}\" {Strings.Messages_IsAlreadyOn} {Strings.Common_Display} {display.Index() + 1}!", ToastDurationShort);
                }
                else
                {
                    m_logger.Debug("Moving window to designated display...");
                    var position = window.Position;
                    var displayCenter = display.Bounds.Center;

                    if (window.State != WinMan.WindowState.Restored)
                    {
                        window.SetState(WinMan.WindowState.Restored);
                    }
                    window.SetPosition(Rectangle.OffsetAndSize(displayCenter.X - position.Width / 2, displayCenter.Y - position.Height / 2, position.Width, position.Height));

                    m_tiling.Refresh();
                    OnCurrentDisplayChanged(display);

                    await ShowToastAsync($"{Strings.Common_Moved} \"{formattedTitle}\" {Strings.Common_To} {Strings.Common_Display} {display.Index() + 1}.", ToastDurationShort);
                }
            }
            catch (InvalidWindowReferenceException)
            {
                m_logger.Error("Failed to move window to displa: the window was destroyed");
            }
            catch (InvalidOperationException e)
            {
                m_logger.Error($"Failed to move window to display: {e.Message}");
            }
        }

        private async void OnPreviousDisplayHotkeyPressed()
        {
            if (m_prevDisplay == null)
            {
                PlayBeepSound();
                await ShowToastAsync(Strings.Messages_NoPreviousDisplay, ToastDurationShort);
                return;
            }

            m_logger.Debug("Switching to previous display...");
            SwitchToDisplay(m_prevDisplay);
        }

        private async void OnMoveToPreviousDisplayHotkeyPressed()
        {
            if (m_workspace.FocusedWindow is not IWindow window)
            {
                PlayBeepSound();
                await ShowToastAsync(Strings.Messages_NoFocusedWindow, ToastDurationShort);
                return;
            }
            if (m_prevDisplay == null)
            {
                PlayBeepSound();
                await ShowToastAsync(Strings.Messages_NoPreviousDisplay, ToastDurationShort);
                return;
            }
            await MoveToDisplayAsync(m_prevDisplay, window);
        }

        private void OnSwitchToDisplayHotkeyPressed(int displayIndex)
        {
            var displays = m_workspace.DisplayManager.Displays;
            if (displayIndex < displays.Count)
            {
                var display = displays[displayIndex];
                SwitchToDisplay(display);
            }
            else
            {
                PlayBeepSound();
            }
        }

        private async void OnMoveToDisplayHotkeyPressed(int displayIndex)
        {
            if (m_workspace.FocusedWindow is not IWindow window)
            {
                PlayBeepSound();
                await ShowToastAsync(Strings.Messages_NoFocusedWindow, ToastDurationShort);
                return;
            }

            var displays = m_workspace.DisplayManager.Displays;
            if (displayIndex < displays.Count)
            {
                var display = displays[displayIndex];
                await MoveToDisplayAsync(display, window);
            }
            else
            {
                PlayBeepSound();
            }
        }

        private void OnSettingsClick(object? sender, RoutedEventArgs e)
        {
            m_lifetime.RunIfActive(() =>
            {
                m_contextMenu.IsOpen = false;
                OpenSettings();
            });
        }

        private void OnExitClick(object? sender, RoutedEventArgs e)
        {
            m_lifetime.RunIfActive(() =>
            {
                m_logger.Debug("Application exit requested!");
                m_contextMenu.IsOpen = false;
                App.Current.Terminate();
            });
        }

        private void OnSponsorClick(object? sender, RoutedEventArgs e)
        {
            m_lifetime.RunIfActive(App.Sponsor);
        }

        private void OnAboutClick(object? sender, RoutedEventArgs e)
        {
            m_lifetime.RunIfActive(() =>
            {
                m_logger.Debug("Opening about window...");
                m_contextMenu.IsOpen = false;
                new AboutWindow().Show();
            });
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            e.Cancel = true;
        }

        internal Task ShutdownCompletion => m_lifetime.Completion;

#pragma warning disable CA1816 // Dispose methods should call SuppressFinalize
        public void Dispose()
#pragma warning restore CA1816 // Dispose methods should call SuppressFinalize
        {
            if (m_disposed) { return; }
            m_disposed = true;
            m_lifetime.Dispose();
        }

        private void DisposeOwnedResources(Action<Action> release)
        {
            release(FocusHelper.Stop);
            release(() => m_shutdownCancellation.Cancel());
            if (m_notifyIcon != null)
            {
                release(() => m_notifyIcon.TrayLeftMouseDown -= OnNotifyIconLeftMouseDown);
                release(() => m_notifyIcon.TrayRightMouseDown -= OnNotifyIconRightMouseDown);
                release(() => m_notifyIcon.TrayBalloonTipClicked -= OnBalloonTipClicked);
            }
            release(() => { if (m_contextMenu != null) { m_contextMenu.IsOpen = false; } });
            release(() => m_notifyIcon?.Dispose());
            release(() => m_toasts?.Dispose());
            MainWindowLifetime.DisposeSubscriptions(m_subscriptions, release);
            release(() => m_bindingErrorListener?.Dispose());
            release(() => Loaded -= OnLoaded);
            if (m_hwnd != IntPtr.Zero)
            {
                release(() => HwndSource.FromHwnd(m_hwnd)?.RemoveHook(WndProc));
            }
            if (m_micaProvider != null)
            {
                release(() => m_micaProvider.PrimaryColorChanged -= OnMicaProviderPrimaryColorChanged);
            }
            release(() => m_mvm?.Dispose());
            release(() => m_capsLockHk?.Dispose());
            foreach (var hotkey in m_cmdHks ?? []) { release(hotkey.Dispose); }
            if (m_workspace != null)
            {
                release(() => m_workspace.UnhandledException -= OnWorkspaceUnhandledException);
            }
            release(() => m_unmanagedResourceGuard?.Dispose());

            if (m_subscribedDesktopManager != null)
            {
                release(() => m_subscribedDesktopManager.CurrentDesktopChanged -= OnVirtualDesktopChanged);
                release(() => m_subscribedDesktopManager.DesktopRemoved -= OnVirtualDesktopRemoved);
            }
            if (m_workspace != null)
            {
                release(() => m_workspace.FocusedWindowChanged -= OnFocusedWindowChanged);
            }

            release(() => m_logger?.Debug($"Disposing of {nameof(MainWindow)}..."));

            release(() => m_restoreLayoutOnShutdown = m_tiling?.Active == true);
            release(() => m_tilingShutdown = m_tiling?.PrepareForShutdownAsync() ?? Task.CompletedTask);

            release(() => m_animationThread?.Dispose());

            foreach (var hk in m_directHks)
            {
                release(hk.Dispose);
            }

            if (m_dispatcherTimer != null)
            {
                release(() => m_dispatcherTimer.Tick -= OnDispatcherTimerTick);
            }
            release(() => m_dispatcherTimer?.Stop());
        }

        private Task CompleteOwnedOperationsAsync()
            => Task.WhenAll(m_mvm?.Completion ?? Task.CompletedTask,
                m_animationThread?.Completion ?? Task.CompletedTask,
                m_loadedUpdateCheck?.Completion ?? Task.CompletedTask,
                m_tilingShutdown);

        private Task DispatchCleanupAsync(Action cleanup)
        {
            if (Dispatcher.CheckAccess())
            {
                cleanup();
                return Task.CompletedTask;
            }
            return Dispatcher.InvokeAsync(cleanup, DispatcherPriority.Send).Task;
        }

        private void DisposeDependentResources(Action<Action> release)
        {
            // Both direct placement and animation updates have returned. Restore
            // original rectangles only now, so an old frame cannot overwrite them.
            if (m_restoreLayoutOnShutdown) { release(() => m_tiling?.Stop()); }
            release(() => m_logger?.Debug($"Stopping the tiling window manager..."));
            if (m_tiling != null)
            {
                release(() => m_tiling.PlacementFailed -= OnTilingFailed);
                release(() => m_tiling.AlgorithmicLayoutChanged -= OnAlgorithmicLayoutChanged);
            }
            release(() => m_tiling?.Dispose());
            release(() => m_algorithmicLayoutCoordinator?.Dispose());
            release(() => m_logger?.Debug($"Closing the workspace..."));
            release(() => m_workspace?.Dispose());
        }
    }
}
