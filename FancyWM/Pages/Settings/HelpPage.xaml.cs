using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

using FancyWM.ViewModels;

using Windows.System;

namespace FancyWM.Pages.Settings
{
    /// <summary>
    /// Interaction logic for HelpPage.xaml
    /// </summary>
    public partial class HelpPage : UserControl, IDisposable
    {
        private IDisposable? m_browserOwner;

        public HelpPage(SettingsViewModel viewModel)
        {
            DataContext = viewModel;
            try
            {
                InitializeComponent();
                m_browserOwner = Browser;
            }
            catch
            {
                Browser?.Dispose();
                throw;
            }
        }

        internal HelpPage(IDisposable browserOwner)
        {
            m_browserOwner = browserOwner;
        }

        public void Dispose()
        {
            Dispatcher.VerifyAccess();
            var browser = m_browserOwner;
            m_browserOwner = null;
            try
            {
                Content = null;
                DataContext = null;
            }
            finally { browser?.Dispose(); }
        }

        private void OpenUrl(object sender, RoutedEventArgs e)
        {
            var hyperlink = (Hyperlink)sender;
            _ = Launcher.LaunchUriAsync(hyperlink.NavigateUri);
        }

        private void OpenDataDir(object sender, RoutedEventArgs e)
        {
            _ = Launcher.LaunchUriAsync(new Uri(Directory.GetCurrentDirectory()));
        }
    }
}
