using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

using Windows.ApplicationModel;

namespace FancyWM.Utilities
{
    internal static class Autostart
    {
        private const string StartupTaskId = "FancyWM";
        private static readonly string s_startupLinkPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "FancyWM.lnk");
        private static readonly bool s_isRunningAsUwp = new DesktopBridge.Helpers().IsRunningAsUwp();

        public static async Task<bool> IsEnabledAsync()
        {
            if (s_isRunningAsUwp)
            {
                return await IsEnabledUwpAsync();
            }
            else
            {
                return await IsEnabledLegacyAsync();
            }
        }

        public static async Task<bool> EnableAsync()
        {
            if (s_isRunningAsUwp)
            {
                return await EnableUwpAsync();
            }
            else
            {
                return await EnableLegacyAsync();
            }
        }

        public static async Task<bool> DisableAsync()
        {
            if (s_isRunningAsUwp)
            {
                return await DisableUwpAsync();
            }
            else
            {
                return await DisableLegacyAsync();
            }
        }

        private static Task<bool> IsEnabledLegacyAsync()
        {
            return Task.FromResult(IsEnabledLegacy(s_startupLinkPath, GetLegacyExecutablePath(AppContext.BaseDirectory)));
        }

        internal static string GetLegacyExecutablePath(string baseDirectory)
        {
            var guiPath = Path.Combine(baseDirectory, "FancyWM-GUI.exe");
            return Path.GetFullPath(File.Exists(guiPath) ? guiPath : Path.Combine(baseDirectory, "FancyWM.exe"));
        }

        internal static bool IsEnabledLegacy(string linkPath, string executablePath)
        {
            if (!File.Exists(linkPath) || !File.Exists(executablePath)) return false;
            return WithShortcut(linkPath, link =>
                string.Equals((string)link.TargetPath, Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrEmpty((string)link.Arguments));
        }

        internal static bool EnableLegacy(string linkPath, string executablePath)
        {
            executablePath = Path.GetFullPath(executablePath);
            if (!File.Exists(executablePath)) throw new FileNotFoundException("Cannot register the missing FancyWM executable for startup", executablePath);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(linkPath))!);
            WithShortcut(linkPath, link =>
            {
                link.TargetPath = executablePath;
                link.Arguments = string.Empty;
                link.WorkingDirectory = Path.GetDirectoryName(executablePath);
                link.IconLocation = executablePath + ",0";
                link.WindowStyle = 1;
                link.Save();
                return true;
            });
            return IsEnabledLegacy(linkPath, executablePath);
        }

        internal static bool DisableLegacy(string linkPath)
        {
            File.Delete(linkPath);
            return !File.Exists(linkPath);
        }

        private static T WithShortcut<T>(string linkPath, Func<dynamic, T> action)
        {
            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
            object? shortcut = null;
            try
            {
                shortcut = ((dynamic)shell).CreateShortcut(Path.GetFullPath(linkPath));
                return action(shortcut);
            }
            finally
            {
                if (shortcut != null) Marshal.FinalReleaseComObject(shortcut);
                Marshal.FinalReleaseComObject(shell);
            }
        }

        private static async Task<bool> IsEnabledUwpAsync()
        {
            var task = await StartupTask.GetAsync(StartupTaskId);
            return task.State == StartupTaskState.Enabled || task.State == StartupTaskState.EnabledByPolicy;
        }

        private static Task<bool> EnableLegacyAsync()
        {
            return Task.FromResult(EnableLegacy(s_startupLinkPath, GetLegacyExecutablePath(AppContext.BaseDirectory)));
        }

        private static async Task<bool> EnableUwpAsync()
        {
            var task = await StartupTask.GetAsync(StartupTaskId);
            if (task.State != StartupTaskState.Enabled && task.State != StartupTaskState.EnabledByPolicy)
            {
                var newState = await task.RequestEnableAsync();
                if (newState != StartupTaskState.Enabled)
                {
                    return false;
                }
            }
            return true;
        }

        private static Task<bool> DisableLegacyAsync()
        {
            return Task.FromResult(DisableLegacy(s_startupLinkPath));
        }

        private static async Task<bool> DisableUwpAsync()
        {
            var task = await StartupTask.GetAsync(StartupTaskId);
            if (task.State == StartupTaskState.Enabled || task.State == StartupTaskState.EnabledByPolicy)
            {
                task.Disable();
                if (task.State != StartupTaskState.Disabled)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
