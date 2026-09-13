using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FancyWM.Controls
{
    /// <summary>
    /// Interaction logic for SvgIcon.xaml
    /// </summary>
    public partial class SvgIcon : UserControl
    {
        public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
            nameof(Icon),
            typeof(string),
            typeof(SvgIcon),
            new PropertyMetadata(null));

        public string Icon
        {
            get => (string)GetValue(IconProperty);
            set => SetValue(IconProperty, value);
        }

        public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
            nameof(Color),
            typeof(Brush),
            typeof(SvgIcon),
            new PropertyMetadata(Brushes.White));

        public Brush Color
        {
            get => (Brush)GetValue(ColorProperty);
            set => SetValue(ColorProperty, value);
        }

        // Share only frozen prototypes. Every Path keeps a mutable deep clone.
        private static readonly PathGeometry s_splitGeometry = CreateGeometry("M19 13H5c-1.1 0-2 .9-2 2v4c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2v-4c0-1.1-.9-2-2-2zm0-10H5c-1.1 0-2 .9-2 2v4c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2z");
        private static readonly PathGeometry s_stackGeometry = CreateGeometry("M12.6 18.06c-.36.28-.87.28-1.23 0l-6.15-4.78a.991.991 0 0 0-1.22 0c-.51.4-.51 1.17 0 1.57l6.76 5.26c.72.56 1.73.56 2.46 0l6.76-5.26c.51-.4.51-1.17 0-1.57l-.01-.01a.991.991 0 0 0-1.22 0l-6.15 4.79zm.63-3.02 6.76-5.26c.51-.4.51-1.18 0-1.58l-6.76-5.26c-.72-.56-1.73-.56-2.46 0L4.01 8.21c-.51.4-.51 1.18 0 1.58l6.76 5.26c.72.56 1.74.56 2.46-.01z");
        private static readonly PathGeometry s_pullUpGeometry = CreateGeometry("M3.01 13.28c-.14-2.57 1.66-4.73 4.07-5.18l-.79.78c-.39.39-.39 1.02 0 1.41.39.39 1.02.39 1.41 0l2.59-2.59c.39-.39.39-1.02 0-1.41L7.71 3.7a.9959.9959 0 0 0-1.41 0c-.39.39-.39 1.02 0 1.41l.88.88v.06C3.54 6.48.75 9.7 1.03 13.52 1.29 17.22 4.55 20 8.26 20H10c.55 0 1-.45 1-1s-.45-1-1-1H8.22c-2.7 0-5.07-2.04-5.21-4.72zM13 15v3c0 1.1.9 2 2 2h5c1.1 0 2-.9 2-2v-3c0-1.1-.9-2-2-2h-5c-1.1 0-2 .9-2 2zm7 3h-5v-3h5v3zm0-14h-5c-1.1 0-2 .9-2 2v3c0 1.1.9 2 2 2h5c1.1 0 2-.9 2-2V6c0-1.1-.9-2-2-2z");
        private static readonly PathGeometry s_floatGeometry = CreateGeometry("M18 7h-6c-.55 0-1 .45-1 1v4c0 .55.45 1 1 1h6c.55 0 1-.45 1-1V8c0-.55-.45-1-1-1zm3-4H3c-1.1 0-2 .9-2 2v14c0 1.1.9 1.98 2 1.98h18c1.1 0 2-.88 2-1.98V5c0-1.1-.9-2-2-2zm-1 16.01H4c-.55 0-1-.45-1-1V5.98c0-.55.45-1 1-1h16c.55 0 1 .45 1 1v12.03c0 .55-.45 1-1 1z");

        public static PathGeometry SplitGeometry => s_splitGeometry.Clone();
        public static PathGeometry StackGeometry => s_stackGeometry.Clone();
        public static PathGeometry PullUpGeometry => s_pullUpGeometry.Clone();
        public static PathGeometry FloatGeometry => s_floatGeometry.Clone();

        private static PathGeometry CreateGeometry(string figures)
        {
            var geometry = new PathGeometry { Figures = PathFigureCollection.Parse(figures) };
            geometry.Freeze();
            return geometry;
        }

        public SvgIcon()
        {
            InitializeComponent();
            DataContext = this;
        }
    }
}
