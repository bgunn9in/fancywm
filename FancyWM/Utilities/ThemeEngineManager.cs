using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using FancyWM.ThemeEngine.Wpf;
using ModernWpf;

namespace FancyWM.Utilities
{
    internal sealed partial class ThemeEngineManager : IDisposable
    {
        private const string DefaultCssFileName = "_default.css";
        private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(300);
        private readonly object m_lock = new();
        private readonly TimeProvider m_clock;
        private readonly Func<string, CancellationToken, Task<string>> m_read;
        private readonly Func<string, IReadOnlyDictionary<string, CssValue>> m_convert;
        private readonly Action<IReadOnlyDictionary<string, CssValue>> m_apply;
        private readonly Func<Action, CancellationToken, Task> m_dispatch;
        private readonly Func<string, Action, IDisposable> m_watch;
        private readonly Func<string, CancellationToken, Task> m_writeDefault;
        private readonly Action<string> m_ensureFile;
        private readonly Action<Exception> m_reportError;
        private readonly CancellationTokenSource m_shutdown = new();
        private readonly CancellationToken m_cancellation;
        private readonly string m_themeDir;
        private IDisposable? m_defaultSubscription, m_watcher;
        private ITimer? m_timer;
        private TaskCompletionSource? m_idle;
        private string m_themeFilePath, m_defaultCss, m_lastWrittenDefaultCss, m_lastAppliedCss;
        private IReadOnlyDictionary<string, CssValue> m_lastAppliedResources;
        private TimeSpan m_delay;
        private long m_queuedTimestamp, m_generation;
        private bool m_pending, m_workerRunning, m_ready, m_disposed, m_shutdownCanceled, m_shutdownDisposed;

        // The caller owns this lifetime. Initial resources are available before return.
        public static ThemeEngineManager Initialize(string themeDir, string themeFileName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(themeDir);
            ArgumentException.ThrowIfNullOrWhiteSpace(themeFileName);
            var dispatcher = App.Current.Dispatcher;
            dispatcher.VerifyAccess();
            themeDir = Path.GetFullPath(themeDir);
            Directory.CreateDirectory(themeDir);
            string path = Path.Combine(themeDir, themeFileName);
            Action<Exception> report = error => App.Current.Logger.Warning(error, "Theme reload failed");
            var host = new FrameworkElement();
            string CaptureDefaultCss()
            {
                dispatcher.VerifyAccess();
                return GetDefaultCss(host.FindResource,
                    ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Dark,
                    Environment.OSVersion.Version.Build >= 22000);
            }
            string defaults = CaptureDefaultCss();
            string defaultPath = Path.Combine(themeDir, DefaultCssFileName);
            File.WriteAllText(defaultPath, defaults, Encoding.UTF8);
            EnsureCustomThemeFileExists(path);
            string custom;
            try { custom = ReadOnce(path); }
            catch (Exception error) { report(error); custom = ""; }
            var themeChanges = Observable.FromEventPattern<TypedEventHandler<ThemeManager, object>, object>(
                handler => ThemeManager.Current.ActualApplicationThemeChanged += handler,
                handler => ThemeManager.Current.ActualApplicationThemeChanged -= handler);
            var accentChanges = Observable.FromEventPattern<TypedEventHandler<ThemeManager, object>, object>(
                handler => ThemeManager.Current.ActualAccentColorChanged += handler,
                handler => ThemeManager.Current.ActualAccentColorChanged -= handler);
            var converter = new CssToWpfResourceConverter();
            var owner = new ThemeEngineManager(path, defaults, custom,
                themeChanges.Merge(accentChanges).Select(_ => CaptureDefaultCss()),
                (file, changed) => WatchFile(file, changed, report),
                ReadOnceAsync, css => converter.Convert(HtmlTemplate, css),
                resources => { dispatcher.VerifyAccess(); CssManager.ApplyTheme(resources); },
                (apply, token) => dispatcher.InvokeAsync(apply, DispatcherPriority.Normal, token).Task,
                TimeProvider.System, report,
                (css, token) => File.WriteAllTextAsync(defaultPath, css, Encoding.UTF8, token),
                EnsureCustomThemeFileExists);
            // Reconcile the read/watcher setup gap, including an initially locked file.
            owner.RequestReload(debounce: false);
            return owner;
        }

        internal ThemeEngineManager(string themeFilePath, string initialDefaultCss, string initialCustomCss,
            IObservable<string> defaultCssChanges,
            Func<string, Action, IDisposable> watch,
            Func<string, CancellationToken, Task<string>> read,
            Func<string, IReadOnlyDictionary<string, CssValue>> convert,
            Action<IReadOnlyDictionary<string, CssValue>> apply,
            Func<Action, CancellationToken, Task> dispatch,
            TimeProvider clock, Action<Exception> reportError,
            Func<string, CancellationToken, Task>? writeDefault = null, Action<string>? ensureFile = null)
        {
            m_themeFilePath = themeFilePath;
            m_themeDir = Path.GetDirectoryName(themeFilePath)!;
            m_defaultCss = m_lastWrittenDefaultCss = initialDefaultCss;
            m_clock = clock;
            m_read = read;
            m_convert = convert;
            m_apply = apply;
            m_dispatch = dispatch;
            m_watch = watch;
            m_writeDefault = writeDefault ?? ((_, _) => Task.CompletedTask);
            m_ensureFile = ensureFile ?? (_ => { });
            m_reportError = reportError;
            m_cancellation = m_shutdown.Token;
            m_lastAppliedCss = Combine(initialDefaultCss, initialCustomCss);
            m_lastAppliedResources = null!;
            try
            {
                m_lastAppliedResources = m_convert(m_lastAppliedCss);
                m_apply(m_lastAppliedResources);
                var subscription = defaultCssChanges.Subscribe(OnDefaultCssChanged, ReportError);
                lock (m_lock)
                {
                    if (m_disposed) subscription.Dispose();
                    else m_defaultSubscription = subscription;
                }
                var watcher = m_watch(themeFilePath, () => OnFileChanged(themeFilePath));
                lock (m_lock)
                {
                    if (m_disposed) watcher.Dispose();
                    else m_watcher = watcher;
                    m_ready = true;
                    StartWorkerCore();
                }
            }
            catch { Dispose(); throw; }
        }

        internal Task Completion { get { lock (m_lock) return m_idle?.Task ?? Task.CompletedTask; } }

        public void SetTheme(string themeFileName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(themeFileName);
            string path = Path.Combine(m_themeDir, themeFileName);
            lock (m_lock) { if (m_disposed) return; }
            m_ensureFile(path);
            var watcher = m_watch(path, () => OnFileChanged(path));
            IDisposable? previous;
            Exception? failure;
            lock (m_lock)
            {
                if (m_disposed) { watcher.Dispose(); return; }
                previous = m_watcher;
                m_watcher = watcher;
                m_themeFilePath = path;
                failure = QueueReloadCore(TimeSpan.Zero);
            }
            previous?.Dispose();
            if (failure != null) ReportError(failure);
        }

        private static string Combine(string defaults, string custom) => defaults + "\n" + custom;
        private void OnDefaultCssChanged(string css)
        {
            Exception? failure;
            lock (m_lock)
            {
                if (m_disposed || StringComparer.Ordinal.Equals(css, m_defaultCss)) return;
                m_defaultCss = css;
                failure = QueueReloadCore(TimeSpan.Zero);
            }
            if (failure != null) ReportError(failure);
        }
        private void OnFileChanged(string path)
        {
            Exception? failure;
            lock (m_lock)
            {
                if (m_disposed || !StringComparer.OrdinalIgnoreCase.Equals(path, m_themeFilePath)) return;
                failure = QueueReloadCore(DebounceDelay);
            }
            if (failure != null) ReportError(failure);
        }
        internal void RequestReload(bool debounce = true)
        {
            Exception? failure;
            lock (m_lock)
            {
                if (m_disposed) return;
                failure = QueueReloadCore(debounce ? DebounceDelay : TimeSpan.Zero);
            }
            if (failure != null) ReportError(failure);
        }
        private Exception? QueueReloadCore(TimeSpan delay)
        {
            m_generation++;
            m_pending = true;
            m_idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            m_queuedTimestamp = m_clock.GetTimestamp();
            m_delay = delay;
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    if (m_timer == null)
                        m_timer = m_clock.CreateTimer(_ => OnDebounceElapsed(), null, delay, Timeout.InfiniteTimeSpan);
                    else if (!m_timer.Change(delay, Timeout.InfiniteTimeSpan))
                        throw new InvalidOperationException("Could not rearm the theme reload timer");
                }
                else
                {
                    m_timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    StartWorkerCore();
                }
            }
            catch (Exception error)
            {
                // A failed debounce must not strand the pending reload or its completion.
                m_delay = TimeSpan.Zero;
                StartWorkerCore();
                return error;
            }
            return null;
        }
        private void OnDebounceElapsed()
        {
            Exception? failure = null;
            lock (m_lock)
            {
                if (m_disposed || !m_pending) return;
                var remaining = RemainingDelayCore();
                if (remaining > TimeSpan.Zero)
                {
                    try
                    {
                        if (!m_timer!.Change(remaining, Timeout.InfiniteTimeSpan))
                            throw new InvalidOperationException("Could not rearm the theme reload timer");
                    }
                    catch (Exception error) { failure = error; m_delay = TimeSpan.Zero; }
                }
                if (remaining <= TimeSpan.Zero || failure != null) StartWorkerCore();
            }
            if (failure != null) ReportError(failure);
        }
        private void StartWorkerCore()
        {
            if (m_disposed || !m_ready || m_workerRunning || !m_pending || RemainingDelayCore() > TimeSpan.Zero) return;
            m_workerRunning = true;
            // One bounded drain owns all reads/conversions, including superseded parses.
            _ = Task.Run(ProcessPendingAsync);
        }
        private TimeSpan RemainingDelayCore() => m_delay - m_clock.GetElapsedTime(m_queuedTimestamp);
        private async Task ProcessPendingAsync()
        {
            while (true)
            {
                string defaults, path;
                long generation;
                lock (m_lock)
                {
                    if (m_disposed || !m_pending || RemainingDelayCore() > TimeSpan.Zero)
                    {
                        m_workerRunning = false;
                        CompleteIdleCore();
                        return;
                    }
                    m_pending = false;
                    defaults = m_defaultCss;
                    path = m_themeFilePath;
                    generation = m_generation;
                }
                try
                {
                    if (!StringComparer.Ordinal.Equals(defaults, m_lastWrittenDefaultCss))
                    {
                        await m_writeDefault(defaults, m_cancellation).ConfigureAwait(false);
                        m_lastWrittenDefaultCss = defaults;
                    }
                    string custom = await ReadWithRetryAsync(path).ConfigureAwait(false);
                    string combined = Combine(defaults, custom);
                    lock (m_lock)
                    {
                        if (m_disposed || generation != m_generation
                            || StringComparer.Ordinal.Equals(combined, m_lastAppliedCss)) continue;
                    }
                    var resources = m_convert(combined);
                    lock (m_lock) { if (m_disposed || generation != m_generation) continue; }
                    await m_dispatch(() =>
                    {
                        IReadOnlyDictionary<string, CssValue> previous;
                        lock (m_lock)
                        {
                            if (m_disposed || generation != m_generation) return;
                            previous = m_lastAppliedResources;
                        }
                        try { m_apply(resources); }
                        catch (Exception applyError)
                        {
                            lock (m_lock) { if (m_disposed) throw; }
                            // A binding failure may follow CssManager's dictionary exchange.
                            try { m_apply(previous); }
                            catch (Exception restoreError) { throw new AggregateException(applyError, restoreError); }
                            throw;
                        }
                        lock (m_lock)
                        {
                            if (m_disposed) return;
                            m_lastAppliedCss = combined;
                            m_lastAppliedResources = resources;
                        }
                    }, m_cancellation).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (m_cancellation.IsCancellationRequested) { }
                catch (Exception error) { ReportError(error); }
            }
        }
        private async Task<string> ReadWithRetryAsync(string path)
        {
            for (int attempt = 0; ; attempt++)
            {
                m_cancellation.ThrowIfCancellationRequested();
                try { return await m_read(path, m_cancellation).ConfigureAwait(false); }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(80), m_clock, m_cancellation).ConfigureAwait(false);
                }
            }
        }
        private void CompleteIdleCore()
        {
            if (!m_disposed && m_pending) return;
            if (m_disposed)
            {
                // The global CssManager may still own the currently visible dictionary.
                // A retained disposed token must not retain an obsolete theme after replacement.
                m_lastAppliedResources = null!;
                m_lastAppliedCss = m_lastWrittenDefaultCss = m_defaultCss = string.Empty;
            }
            if (m_disposed && m_shutdownCanceled && !m_shutdownDisposed)
            {
                m_shutdownDisposed = true;
                m_shutdown.Dispose();
            }
            var completion = m_idle;
            m_idle = null;
            completion?.TrySetResult();
        }
        public void Dispose()
        {
            IDisposable? subscription, watcher;
            ITimer? timer;
            lock (m_lock)
            {
                if (m_disposed) return;
                m_disposed = true;
                m_generation++;
                m_pending = false;
                subscription = m_defaultSubscription;
                watcher = m_watcher;
                timer = m_timer;
                m_defaultSubscription = null;
                m_watcher = null;
                m_timer = null;
            }
            try { m_shutdown.Cancel(); }
            finally
            {
                try { subscription?.Dispose(); }
                finally
                {
                    try { watcher?.Dispose(); }
                    finally
                    {
                        try { timer?.Dispose(); }
                        finally
                        {
                            lock (m_lock)
                            {
                                m_shutdownCanceled = true;
                                if (!m_workerRunning) CompleteIdleCore();
                            }
                        }
                    }
                }
            }
        }
        private void ReportError(Exception error)
        {
            try { m_reportError(error); }
            catch { /* A diagnostic sink must not strand the owned worker. */ }
        }
        private static void EnsureCustomThemeFileExists(string path)
        {
            if (!File.Exists(path) && Path.GetFileName(path) != DefaultCssFileName)
                File.WriteAllText(path, "/* Custom theme - add your overrides below. See _default.css for examples rules. */\n", Encoding.UTF8);
        }
        private static string ReadOnce(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        private static async Task<string> ReadOnceAsync(string path, CancellationToken token)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(token).ConfigureAwait(false);
        }
        private static IDisposable WatchFile(string path, Action changed, Action<Exception> report)
        {
            var watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!)
            {
                Filter = Path.GetFileName(path),
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                IncludeSubdirectories = false,
            };
            FileSystemEventHandler onChange = (_, _) => changed();
            RenamedEventHandler onRename = (_, _) => changed();
            ErrorEventHandler onError = (_, args) => { report(args.GetException()); changed(); };
            try
            {
                watcher.Changed += onChange;
                watcher.Created += onChange;
                watcher.Deleted += onChange;
                watcher.Renamed += onRename;
                watcher.Error += onError;
                watcher.EnableRaisingEvents = true;
                return Disposable.Create(() =>
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Changed -= onChange;
                    watcher.Created -= onChange;
                    watcher.Deleted -= onChange;
                    watcher.Renamed -= onRename;
                    watcher.Error -= onError;
                    watcher.Dispose();
                });
            }
            catch { watcher.Dispose(); throw; }
        }
    }
}
