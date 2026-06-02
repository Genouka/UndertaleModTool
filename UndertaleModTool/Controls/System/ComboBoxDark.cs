using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace UndertaleModTool
{
    /// <summary>
    /// A standard combo box which compatible with the dark mode.
    /// </summary>
    public partial class ComboBoxDark : ComboBox
    {
        // Setting "Foreground" implicitly breaks internal "IsEnabled" style trigger,
        // so this has to be handled manually.
        private static readonly SolidColorBrush disabledTextBrush = new(Color.FromArgb(255, 131, 131, 131));

        private ToggleButton toggleButton;

        /// <summary>Initializes a new instance of the combo box.</summary>
        public ComboBoxDark()
        {
            // Even though this will be called again in "OnPropertyChanged()", it's required.
            SetResourceReference(ForegroundProperty, "CustomTextBrush");
            SetResourceReference(BackgroundProperty, "CustomControlBrush");

            Loaded += ComboBox_Loaded;
            DropDownOpened += ComboBox_DropDownOpened;
        }

        private void ComboBox_Loaded(object sender, RoutedEventArgs e)
        {
            // The Aero2 theme ComboBox template uses ComboBoxChrome inside a ToggleButton
            // to render the background. ComboBoxChrome.Background is bound to ToggleButton.Background
            // via TemplateBinding, so setting ToggleButton.Background propagates to ComboBoxChrome.
            toggleButton = MainWindow.FindVisualChild<ToggleButton>(this);
            if (toggleButton is not null)
            {
                toggleButton.SetResourceReference(BackgroundProperty, "CustomControlBrush");
            }
        }

        private void ComboBox_DropDownOpened(object sender, EventArgs e)
        {
            Popup popup = MainWindow.FindVisualChild<Popup>(this);
            var content = MainWindow.FindVisualChild<Border>(popup?.Child);
            if (content is null)
                return;

            content.SetResourceReference(ForegroundProperty, SystemColors.ControlTextBrushKey);
            content.SetResourceReference(BackgroundProperty, SystemColors.WindowBrushKey);
        }

        /// <inheritdoc/>
        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            if (e.Property == IsEnabledProperty)
            {
                if ((bool)e.NewValue)
                {
                    SetResourceReference(ForegroundProperty, "CustomTextBrush");
                    SetResourceReference(BackgroundProperty, "CustomControlBrush");
                    toggleButton?.SetResourceReference(BackgroundProperty, "CustomControlBrush");
                }
                else
                {
                    Foreground = disabledTextBrush;
                }
            }

            base.OnPropertyChanged(e);
        }
    }
}
