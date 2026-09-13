using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using FancyWM.Utilities;
using FancyWM.ViewModels;

using Serilog;

namespace FancyWM.Windows
{
    /// <summary>
    /// Interaction logic for SettingsWindow.xaml
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly ILogger m_logger = App.Current.Logger;
        private readonly SettingsViewModel m_viewModel;
        private readonly PageNavigation m_pages;

        public SettingsWindow(SettingsViewModel viewModel)
        {
            m_logger.Debug($"Initialising {nameof(SettingsWindow)}");
            m_viewModel = viewModel;
            DataContext = viewModel;
            m_pages = new PageNavigation(Dispatcher,
                type => (UIElement)Activator.CreateInstance(type, m_viewModel)!,
                page => PageContent.Child = page,
                error => m_logger.Error(error, "Failed to navigate settings pages"));

            try { InitializeComponent(); }
            catch
            {
                m_pages.Dispose();
                throw;
            }
            m_logger.Debug($"Initialised {nameof(SettingsWindow)} successfully");
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            FocusManager.SetFocusedElement(this, this);
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            Resources.Remove("MicaPrimaryColor");
        }

        protected override void OnDeactivated(EventArgs e)
        {
            base.OnDeactivated(e);
            Resources["MicaPrimaryColor"] = Colors.Transparent;
        }

        protected override void OnClosed(EventArgs e)
        {
            CompleteClose(
                m_pages.Dispose,
                error => m_logger.Error(error, "Failed to dispose settings pages"),
                () => base.OnClosed(e),
                m_viewModel.Dispose,
                () => FocusManager.SetFocusedElement(this, null),
                Keyboard.ClearFocus,
                GCHelper.ScheduleCollection);
        }

        internal static void CompleteClose(
            Action disposePages,
            Action<Exception> reportPageFailure,
            Action notifyClosed,
            Action disposeViewModel,
            Action clearLogicalFocus,
            Action clearKeyboardFocus,
            Action scheduleCollection)
        {
            ExceptionDispatchInfo? failure = null;
            List<Exception>? laterFailures = null;
            void release(Action action)
            {
                try
                {
                    action();
                }
                catch (Exception error)
                {
                    if (failure == null)
                    {
                        failure = ExceptionDispatchInfo.Capture(error);
                    }
                    else
                    {
                        (laterFailures ??= []).Add(error);
                    }
                }
            }

            try
            {
                disposePages();
            }
            catch (Exception error)
            {
                release(() => reportPageFailure(error));
            }
            release(notifyClosed);
            release(disposeViewModel);
            release(clearLogicalFocus);
            release(clearKeyboardFocus);
            release(scheduleCollection);

            if (laterFailures != null)
            {
                AttachLaterCloseFailures(failure!.SourceException, laterFailures);
            }
            failure?.Throw();
        }

        private static void AttachLaterCloseFailures(Exception primary, List<Exception> laterFailures)
        {
            try
            {
                const string key = "SettingsWindow.OnClosedExceptions";
                if (primary.Data[key] is AggregateException existing)
                {
                    laterFailures.InsertRange(0, existing.InnerExceptions);
                }
                primary.Data[key] = new AggregateException(laterFailures);
            }
            catch
            {
                // Supplemental close diagnostics must never replace the first
                // error from the ordered close sequence.
            }
        }

        private void PagesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = e.AddedItems.OfType<PageItem>().FirstOrDefault();
            if (item?.Page != null) GoToPage(item.Page);
        }

        public void GoToPage(Type pageType)
        {
            m_pages.Navigate(pageType);
        }

        private void OnQuitButtonClick(object sender, RoutedEventArgs e)
        {
            App.Current.Terminate();
        }

        private void OnSponsorButtonClick(object sender, RoutedEventArgs e)
        {
            App.Sponsor();
        }
    }
}
