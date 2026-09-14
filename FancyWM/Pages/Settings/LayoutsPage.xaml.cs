using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using FancyWM.ViewModels;

namespace FancyWM.Pages.Settings
{
    /// <summary>
    /// Interaction logic for LayoutsPage.xaml.
    /// </summary>
    public partial class LayoutsPage : UserControl
    {
        private readonly SettingsViewModel m_viewModel;
        private bool m_isSubscribed;

        public LayoutsPage(SettingsViewModel viewModel)
        {
            ArgumentNullException.ThrowIfNull(viewModel);

            m_viewModel = viewModel;
            DataContext = viewModel;
            InitializeComponent();

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            UpdatePreview();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!m_isSubscribed)
            {
                m_viewModel.PropertyChanged += OnViewModelPropertyChanged;
                m_isSubscribed = true;
            }

            UpdatePreview();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (m_isSubscribed)
            {
                m_viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                m_isSubscribed = false;
            }
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(SettingsViewModel.MasterRatio)
                or nameof(SettingsViewModel.DefaultMasterSide)
                or nameof(SettingsViewModel.DefaultSatelliteOrientation)
                or nameof(SettingsViewModel.MaxSatellites)
                or nameof(SettingsViewModel.UseMixedSatellites))
            {
                UpdatePreview();
            }
        }

        private void UpdatePreview()
        {
            var plan = MasterSatellitePreviewPlan.Create(
                m_viewModel.MasterRatio,
                m_viewModel.DefaultMasterSide,
                m_viewModel.DefaultSatelliteOrientation,
                m_viewModel.MaxSatellites,
                m_viewModel.UseMixedSatellites);

            PreviewGrid.Children.Clear();
            PreviewGrid.ColumnDefinitions.Clear();

            var masterColumn = plan.IsMasterFirst ? 0 : 1;
            var satelliteColumn = plan.IsMasterFirst ? 1 : 0;
            PreviewGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(
                    plan.IsMasterFirst ? plan.MasterFraction : 1 - plan.MasterFraction,
                    GridUnitType.Star),
            });
            PreviewGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(
                    plan.IsMasterFirst ? 1 - plan.MasterFraction : plan.MasterFraction,
                    GridUnitType.Star),
            });

            var master = CreateTile("MASTER", isMaster: true);
            Grid.SetColumn(master, masterColumn);
            PreviewGrid.Children.Add(master);

            var satellites = new Grid();
            Grid.SetColumn(satellites, satelliteColumn);
            PreviewGrid.Children.Add(satellites);

            if (plan.IsMixedLayout)
            {
                satellites.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
                satellites.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                satellites.ColumnDefinitions.Add(new ColumnDefinition());
                satellites.ColumnDefinitions.Add(new ColumnDefinition());
                for (int i = 0; i < 3; i++)
                {
                    var tile = CreateTile(i == 2 ? "H" : $"V{i + 1}", isMaster: false);
                    Grid.SetRow(tile, i == 2 ? 1 : 0);
                    Grid.SetColumn(tile, i == 1 ? 1 : 0);
                    Grid.SetColumnSpan(tile, i == 2 ? 2 : 1);
                    satellites.Children.Add(tile);
                }
            }
            for (var i = 0; !plan.IsMixedLayout && i < plan.VisibleSatelliteCount; i++)
            {
                if (plan.SatellitesUseRows)
                {
                    satellites.RowDefinitions.Add(new RowDefinition());
                }
                else
                {
                    satellites.ColumnDefinitions.Add(new ColumnDefinition());
                }

                var satellite = CreateTile($"S{i + 1}", isMaster: false);
                if (plan.SatellitesUseRows)
                {
                    Grid.SetRow(satellite, i);
                }
                else
                {
                    Grid.SetColumn(satellite, i);
                }
                satellites.Children.Add(satellite);
            }

            PreviewLimitText.Text = plan.IsSatelliteCountTruncated
                ? $"Showing the first {plan.VisibleSatelliteCount} of {plan.ConfiguredSatelliteCount} satellites"
                : string.Empty;
            PreviewLimitText.Visibility = plan.IsSatelliteCountTruncated
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private Border CreateTile(string label, bool isMaster)
        {
            var tile = new Border
            {
                Style = (Style)Resources["PreviewTileStyle"],
                Child = new TextBlock
                {
                    Text = label,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = isMaster ? FontWeights.SemiBold : FontWeights.Normal,
                },
            };
            tile.SetResourceReference(
                BackgroundProperty,
                isMaster ? "SystemAccentMediumLowBrush" : "SystemControlBackgroundBaseLowBrush");
            return tile;
        }
    }
}
