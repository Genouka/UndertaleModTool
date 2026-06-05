using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace UndertaleModTool
{
    public partial class TabControlDark : TabControl
    {
        public static readonly DependencyProperty IsTabMultiLineProperty =
            DependencyProperty.Register(nameof(IsTabMultiLine), typeof(bool), typeof(TabControlDark),
                new FrameworkPropertyMetadata(false));

        public bool IsTabMultiLine
        {
            get => (bool)GetValue(IsTabMultiLineProperty);
            set => SetValue(IsTabMultiLineProperty, value);
        }

        private Border contentPanel;

        public TabControlDark()
        {
            Loaded += TabControlDark_Loaded;
        }

        private void TabControlDark_Loaded(object sender, RoutedEventArgs e)
        {
            SetDarkMode(Settings.Instance is not null ? Settings.Instance.EnableDarkMode : false);

            // Find the ContentPanel Border from the template (named "ContentPanel" in default TabControl template)
            contentPanel = Template?.FindName("ContentPanel", this) as Border;

            // Apply background transparency if custom background is active
            if (Settings.Instance is not null && !string.IsNullOrEmpty(Settings.Instance.BackgroundImagePath))
                SetBackgroundTransparency(true);
        }

        protected override DependencyObject GetContainerForItemOverride()
        {
            return new TabItemDark();
        }

        public void SetDarkMode(bool enable)
        {
            foreach (var item in MainWindow.FindVisualChildren<TabItemDark>(this))
                item.SetDarkMode(enable);
        }

        public void SetBackgroundTransparency(bool enable)
        {
            foreach (var item in MainWindow.FindVisualChildren<TabItemDark>(this))
                item.SetBackgroundTransparency(enable);

            // The TabControl's content panel Border (named "ContentPanel" in the default template)
            // uses {x:Static SystemColors.ControlBrush} which is a static reference that doesn't
            // update with dynamic resource changes. We need to set it directly.
            if (contentPanel != null)
            {
                if (enable)
                    contentPanel.Background = Brushes.Transparent;
                else
                    contentPanel.SetResourceReference(BackgroundProperty, SystemColors.ControlBrushKey);
            }
        }
    }

    public partial class TabItemDark : TabItem
    {
        private static readonly SolidColorBrush itemHighlightDarkBrush = new(Color.FromArgb(255, 48, 48, 60));
        private static readonly Brush itemInactiveBrush = new LinearGradientBrush(
                                                            new GradientStopCollection()
                                                            {
                                                                new GradientStop(Color.FromArgb(255, 240, 240, 240), 0),
                                                                new GradientStop(Color.FromArgb(255, 229, 229, 229), 1)
                                                            }, new(0, 0), new(1, 0)
                                                          );
        private static readonly Brush itemInactiveDarkBrush = new LinearGradientBrush(
                                                                new GradientStopCollection()
                                                                {
                                                                    new GradientStop(Color.FromArgb(255, 15, 15, 15), 0),
                                                                    new GradientStop(Color.FromArgb(255, 26, 26, 26), 1)
                                                                }, new(0, 0), new(1, 0)
                                                              );
        private static readonly Brush transparentBrush = Brushes.Transparent;
        private Border border;
        private bool isBgTransparent;

        public TabItemDark()
        {
            SetResourceReference(ForegroundProperty, SystemColors.WindowTextBrushKey);

            Loaded += TabItemDark_Loaded;
        }

        private void TabItemDark_Loaded(object sender, RoutedEventArgs e)
        {
            border = MainWindow.FindVisualChild<Border>(this);
            if (Environment.OSVersion.Version.Major >= 10)
            {
                Border innerBd = MainWindow.FindVisualChild<Border>(this, "innerBorder");
                innerBd?.SetResourceReference(BackgroundProperty, SystemColors.WindowBrushKey);
            }

            SetDarkMode(Settings.Instance.EnableDarkMode);
        }

        public void SetDarkMode(bool enable)
        {
            if (isBgTransparent)
                Background = transparentBrush;
            else
                Background = enable ? itemInactiveDarkBrush : itemInactiveBrush;
        }

        public void SetBackgroundTransparency(bool enable)
        {
            isBgTransparent = enable;
            if (enable)
                Background = transparentBrush;
            else
                SetDarkMode(Settings.Instance.EnableDarkMode);
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            if (e.Property == IsMouseOverProperty
                && Settings.Instance.EnableDarkMode)
            {
                if ((bool)e.NewValue)
                    border?.SetValue(BackgroundProperty, itemHighlightDarkBrush);
                else
                    border?.ClearValue(BackgroundProperty);
            }

            base.OnPropertyChanged(e);
        }
    }
}
