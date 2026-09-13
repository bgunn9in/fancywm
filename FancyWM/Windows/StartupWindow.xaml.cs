using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Windows;

using FancyWM.ViewModels;

namespace FancyWM.Windows
{
    /// <summary>
    /// Interaction logic for StartupWindow.xaml
    /// </summary>
    public partial class StartupWindow : Window
    {
        private readonly SettingsViewModel m_settingsViewModel;

        public StartupWindow(SettingsViewModel settingsViewModel)
        {
            InitializeComponent();
            m_settingsViewModel = settingsViewModel;
            DataContext = m_settingsViewModel;
        }

        private void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            Close();
            MainWindow.OpenSettings();
        }

        protected override void OnClosed(EventArgs e)
        {
            ExceptionDispatchInfo? failure = null;
            try
            {
                m_settingsViewModel.Dispose();
            }
            catch (Exception error)
            {
                failure = ExceptionDispatchInfo.Capture(error);
            }

            try
            {
                base.OnClosed(e);
            }
            catch (Exception error)
            {
                if (failure == null)
                {
                    failure = ExceptionDispatchInfo.Capture(error);
                }
                else
                {
                    AttachLaterCloseFailure(failure.SourceException, error);
                }
            }

            failure?.Throw();
        }

        private static void AttachLaterCloseFailure(Exception primary, Exception later)
        {
            try
            {
                const string key = "StartupWindow.OnClosedExceptions";
                if (primary.Data[key] is AggregateException existing)
                {
                    var failures = new List<Exception>(existing.InnerExceptions.Count + 1);
                    failures.AddRange(existing.InnerExceptions);
                    failures.Add(later);
                    primary.Data[key] = new AggregateException(failures);
                }
                else
                {
                    primary.Data[key] = new AggregateException(later);
                }
            }
            catch
            {
                // Supplemental close diagnostics must never replace the first
                // error from the ordered close sequence.
            }
        }
    }
}
