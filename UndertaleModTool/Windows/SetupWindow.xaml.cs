using System.Globalization;
using System.Windows;
using UndertaleModTool.Localization;

namespace UndertaleModTool.Windows
{
    public partial class SetupWindow : Window
    {
        public SetupWindow()
        {
            InitializeComponent();

            Settings.Instance.SetupWindowShown = true;
            Settings.Save();

            string lang = Settings.Instance.Language ?? "en";
            for (int i = 0; i < LanguageComboBox.Items.Count; i++)
            {
                if ((LanguageComboBox.Items[i] as System.Windows.Controls.ComboBoxItem)?.Tag as string == lang)
                {
                    LanguageComboBox.SelectedIndex = i;
                    break;
                }
            }

            FileAssociationsCheckBox.IsChecked = Settings.Instance.AutomaticFileAssociation;
            CheckForUpdatesCheckBox.IsChecked = Settings.Instance.CheckForUpdates;
        }

        private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsVisible || IsLoaded)
                return;

            if (Settings.Instance.EnableDarkMode)
                MainWindow.SetDarkTitleBarForWindow(this, true, false);
        }

        private void LanguageComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (LanguageComboBox.SelectedItem is System.Windows.Controls.ComboBoxItem item)
            {
                string lang = item.Tag as string;
                if (lang != null && lang != Settings.Instance.Language)
                {
                    Settings.Instance.Language = lang;
                    Settings.Save();

                    var culture = new CultureInfo(lang);
                    LocalizationSource.Instance.CurrentCulture = culture;
                }
            }
        }

        private void FinishButton_Click(object sender, RoutedEventArgs e)
        {
            Settings.Instance.AutomaticFileAssociation = FileAssociationsCheckBox.IsChecked == true;
            Settings.Instance.CheckForUpdates = CheckForUpdatesCheckBox.IsChecked == true;
            Settings.Save();
            Close();
        }
    }
}