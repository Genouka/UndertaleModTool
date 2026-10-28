using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using UndertaleModTool.Localization;
using UndertaleModTool.Update;

namespace UndertaleModTool
{
    public partial class SettingsWindow : Window
    {
        private static readonly MainWindow mainWindow = Application.Current.MainWindow as MainWindow;

        /// <summary>
        /// Prevents the channel/source combo boxes from bouncing changes off each other while their
        /// selection is being synchronized.
        /// </summary>
        private bool suppressUpdateSettingSync;

        public static string GameMakerStudioPath
        {
            get => Settings.Instance.GameMakerStudioPath;
            set
            {
                Settings.Instance.GameMakerStudioPath = value;
                Settings.Save();
            }
        }

        public static string GameMakerStudio2RuntimesPath
        {
            get => Settings.Instance.GameMakerStudio2RuntimesPath;
            set
            {
                Settings.Instance.GameMakerStudio2RuntimesPath = value;
                Settings.Save();
            }
        }

        public static bool AssetOrderSwappingEnabled
        {
            get => Settings.Instance.AssetOrderSwappingEnabled;
            set
            {
                Settings.Instance.AssetOrderSwappingEnabled = value;
                Settings.Save();
            }
        }

        public static bool ShowNullEntriesInResourceTree
        {
            get => Settings.Instance.ShowNullEntriesInResourceTree;
            set
            {
                Settings.Instance.ShowNullEntriesInResourceTree = value;
                Settings.Save();

                mainWindow.UpdateTree();
            }
        }

        public static bool TempRunMessageShow
        {
            get => Settings.Instance.TempRunMessageShow;
            set
            {
                Settings.Instance.TempRunMessageShow = value;
                Settings.Save();
            }
        }

        public static bool CheckForUpdates
        {
            get => Settings.Instance.CheckForUpdates;
            set
            {
                Settings.Instance.CheckForUpdates = value;
                Settings.Save();
            }
        }

        public static string MirrorChyanCdk
        {
            get => Settings.Instance.MirrorChyanCdk;
            set
            {
                Settings.Instance.MirrorChyanCdk = value ?? "";
                Settings.Save();
            }
        }

        public static bool AutomaticFileAssociation
        {
            get => Settings.Instance.AutomaticFileAssociation;
            set
            {
                Settings.Instance.AutomaticFileAssociation = value;
                Settings.Save();

                if (!value && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    if (mainWindow.ShowQuestion(LocalizationSource.GetString("Settings_RemoveFileAssociations"), MessageBoxImage.Question, LocalizationSource.GetString("Settings_FileAssociations")) == MessageBoxResult.Yes)
                    {
                        try
                        {
                            FileAssociations.RemoveAssociations();
                        }
                        catch (Exception ex)
                        {
                            mainWindow.ScriptError(ex.ToString(), LocalizationSource.GetString("Settings_UnassociationFailed"), false);
                        }
                    }
                }
            }
        }

        public static bool WarnOnClose
        {
            get => Settings.Instance.WarnOnClose;
            set
            {
                Settings.Instance.WarnOnClose = value;
                Settings.Save();
            }
        }

        public static double GlobalGridWidth
        {
            get => Settings.Instance.GlobalGridWidth;
            set
            {
                Settings.Instance.GlobalGridWidth = value;
                Settings.Save();
            }
        }

        public static bool GridWidthEnabled
        {
            get => Settings.Instance.GridWidthEnabled;
            set
            {
                Settings.Instance.GridWidthEnabled = value;
                Settings.Save();
            }
        }

        public static double GlobalGridHeight
        {
            get => Settings.Instance.GlobalGridHeight;
            set
            {
                Settings.Instance.GlobalGridHeight = value;
                Settings.Save();
            }
        }

        public static bool GridHeightEnabled
        {
            get => Settings.Instance.GridHeightEnabled;
            set
            {
                Settings.Instance.GridHeightEnabled = value;
                Settings.Save();
            }
        }

        public static double GlobalGridThickness
        {
            get => Settings.Instance.GlobalGridThickness;
            set
            {
                Settings.Instance.GlobalGridThickness = value;
                Settings.Save();
            }
        }

        public static bool GridThicknessEnabled
        {
            get => Settings.Instance.GridThicknessEnabled;
            set
            {
                Settings.Instance.GridThicknessEnabled = value;
                Settings.Save();
            }
        }

        public static string TransparencyGridColor1
        {
            get => Settings.Instance.TransparencyGridColor1;
            set
            {
                try
                {
                    MainWindow.SetTransparencyGridColors(value, TransparencyGridColor2);

                    Settings.Instance.TransparencyGridColor1 = value;
                    Settings.Save();
                }
                catch (FormatException) { }
            }
        }

        public static string TransparencyGridColor2
        {
            get => Settings.Instance.TransparencyGridColor2;
            set
            {
                try
                {
                    MainWindow.SetTransparencyGridColors(TransparencyGridColor1, value);

                    Settings.Instance.TransparencyGridColor2 = value;
                    Settings.Save();
                }
                catch (FormatException) { }
            }
        }

        public static bool EnableDarkMode
        {
            get => Settings.Instance.EnableDarkMode;
            set
            {
                Settings.Instance.EnableDarkMode = value;
                Settings.Save();

                MainWindow.SetDarkMode(value);

                if (value)
                    mainWindow.ShowWarning(LocalizationSource.GetString("Settings_DarkModeMsgBoxWarning"));
            }
        }

        public static string BackgroundImagePath
        {
            get => Settings.Instance.BackgroundImagePath;
            set
            {
                Settings.Instance.BackgroundImagePath = value;
                Settings.Save();
                MainWindow.ApplyCustomBackground();
            }
        }

        public static double BackgroundOpacity
        {
            get => Settings.Instance.BackgroundOpacity;
            set
            {
                Settings.Instance.BackgroundOpacity = value;
                Settings.Save();
                MainWindow.ApplyCustomBackground();
            }
        }

        public static string BackgroundStretchMode
        {
            get => Settings.Instance.BackgroundStretchMode;
            set
            {
                Settings.Instance.BackgroundStretchMode = value;
                Settings.Save();
                MainWindow.ApplyCustomBackground();
            }
        }

        public static bool ShowDebuggerOption
        {
            get => Settings.Instance.ShowDebuggerOption;
            set
            {
                Settings.Instance.ShowDebuggerOption = value;
                Settings.Save();

                mainWindow.RunGMSDebuggerItem.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public static bool RememberWindowPlacements
        {
            get => Settings.Instance.RememberWindowPlacements;
            set
            {
                Settings.Instance.RememberWindowPlacements = value;
                Settings.Save();
            }
        }

        public static bool CodeEditorShowCutCopyPaste
        {
            get => Settings.Instance.CodeEditorShowCutCopyPaste;
            set
            {
                Settings.Instance.CodeEditorShowCutCopyPaste = value;
                Settings.Save();
            }
        }

        public static bool RecompileAllCodeSourcesOnProjectSave
        {
            get => Settings.Instance.RecompileAllCodeSourcesOnProjectSave;
            set
            {
                Settings.Instance.RecompileAllCodeSourcesOnProjectSave = value;
                Settings.Save();
            }
        }

        public static bool ChangeTrackingEnabled
        {
            get => Settings.Instance.ChangeTrackingEnabled;
            set
            {
                Settings.Instance.ChangeTrackingEnabled = value;
                Settings.Save();

                if (!value)
                {
                    var mainWindow = Application.Current.MainWindow as MainWindow;
                    mainWindow?.ChangeTracker?.ClearAll();
                    mainWindow?.UpdateTree();
                }
            }
        }

        public static DecompilerSettings DecompilerSettings => Settings.Instance.DecompilerSettings;

        public static string InstanceIdPrefix
        {
            get => Settings.Instance.InstanceIdPrefix;
            set
            {
                Settings.Instance.InstanceIdPrefix = value;
                Settings.Save();
            }
        }

        public bool UpdateButtonEnabled
        {
            get => UpdateAppButton.IsEnabled;
            set => UpdateAppButton.IsEnabled = value;
        }

#if DEBUG
        public static Visibility UpdaterButtonVisibility => Visibility.Visible;
#else
        public static Visibility UpdaterButtonVisibility => Visibility.Hidden;
#endif

        public SettingsWindow()
        {
            InitializeComponent();
            this.DataContext = this;
            Settings.Load();

            // The nightly channel is always served by GitHub, and MirrorChyan only mirrors stable releases.
            bool updateSettingsChanged = false;
            if (Settings.Instance.UpdateChannel == UpdateChannel.Nightly && Settings.Instance.UpdateSource != UpdateSource.GitHub)
            {
                Settings.Instance.UpdateSource = UpdateSource.GitHub;
                updateSettingsChanged = true;
            }
            if (Settings.Instance.UpdateSource == UpdateSource.MirrorChyan && Settings.Instance.UpdateChannel != UpdateChannel.Stable)
            {
                Settings.Instance.UpdateChannel = UpdateChannel.Stable;
                updateSettingsChanged = true;
            }
            if (updateSettingsChanged)
                Settings.Save();

            suppressUpdateSettingSync = true;
            try
            {
                UpdateChannelComboBox.SelectedIndex = Settings.Instance.UpdateChannel == UpdateChannel.Stable ? 1 : 0;
                UpdateSourceComboBox.SelectedIndex = Settings.Instance.UpdateSource == UpdateSource.MirrorChyan ? 1 : 0;
            }
            finally
            {
                suppressUpdateSettingSync = false;
            }
            UpdateCdkControlsEnabled();
            MirrorChyanCdkBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();

            string lang = Settings.Instance.Language ?? "en";
            for (int i = 0; i < LanguageComboBox.Items.Count; i++)
            {
                if ((LanguageComboBox.Items[i] as ComboBoxItem)?.Tag as string == lang)
                {
                    LanguageComboBox.SelectedIndex = i;
                    break;
                }
            }

            CodeEditorThemeComboBox.SelectedIndex = (int)Settings.Instance.CodeEditorTheme;
        }

        private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsVisible || IsLoaded)
                return;

            if (Settings.Instance.EnableDarkMode)
                MainWindow.SetDarkTitleBarForWindow(this, true, false);
        }

        private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LanguageComboBox.SelectedItem is ComboBoxItem item)
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

        private void CodeEditorThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CodeEditorThemeComboBox?.SelectedItem is ComboBoxItem item
                && Enum.TryParse(item.Tag as string, out CodeEditorThemeKind theme)
                && Settings.Instance is not null)
            {
                Settings.Instance.CodeEditorTheme = theme;
                Settings.Save();

                MainWindow.ApplyCodeEditorThemes();
            }
        }

        private void UpdateChannelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressUpdateSettingSync)
                return;
            if (UpdateChannelComboBox?.SelectedItem is not ComboBoxItem item
                || !Enum.TryParse(item.Tag as string, out UpdateChannel channel)
                || Settings.Instance is null)
            {
                return;
            }

            Settings.Instance.UpdateChannel = channel;

            // Nightly builds are always downloaded from GitHub.
            if (channel == UpdateChannel.Nightly && Settings.Instance.UpdateSource != UpdateSource.GitHub)
            {
                Settings.Instance.UpdateSource = UpdateSource.GitHub;
                suppressUpdateSettingSync = true;
                try
                {
                    UpdateSourceComboBox.SelectedIndex = 0;
                }
                finally
                {
                    suppressUpdateSettingSync = false;
                }
            }

            Settings.Save();
            UpdateCdkControlsEnabled();
        }

        private void UpdateSourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressUpdateSettingSync)
                return;
            if (UpdateSourceComboBox?.SelectedItem is not ComboBoxItem item
                || !Enum.TryParse(item.Tag as string, out UpdateSource source)
                || Settings.Instance is null)
            {
                return;
            }

            Settings.Instance.UpdateSource = source;

            // MirrorChyan only mirrors stable releases.
            if (source == UpdateSource.MirrorChyan && Settings.Instance.UpdateChannel != UpdateChannel.Stable)
            {
                Settings.Instance.UpdateChannel = UpdateChannel.Stable;
                suppressUpdateSettingSync = true;
                try
                {
                    UpdateChannelComboBox.SelectedIndex = 1;
                }
                finally
                {
                    suppressUpdateSettingSync = false;
                }
            }

            Settings.Save();
            UpdateCdkControlsEnabled();
        }

        private void UpdateCdkControlsEnabled()
        {
            bool enabled = Settings.Instance.UpdateSource == UpdateSource.MirrorChyan;
            MirrorChyanCdkBox.IsEnabled = enabled;
            MirrorChyanGetCdkButton.IsEnabled = enabled;
        }

        private void MirrorChyanGetCdkButton_Click(object sender, RoutedEventArgs e)
        {
            string language = Settings.Instance.Language ?? "en";
            string url = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? "https://mirrorchyan.com/zh/get-key"
                : "https://mirrorchyan.com/en/get-key";

            MainWindow.OpenBrowser(url);
        }

        private void AppDataButton_Click(object sender, RoutedEventArgs e)
        {
            MainWindow.OpenFolder(Settings.AppDataFolder);
        }

        private void ClearRecentFilesButton_Click(object sender, RoutedEventArgs e)
        {
            if (Settings.Instance.RecentFiles is null || Settings.Instance.RecentFiles.Count == 0)
            {
                mainWindow.ShowWarning(LocalizationSource.GetString("Settings_ClearRecentFiles_None"));
                return;
            }

            if (mainWindow.ShowQuestion(LocalizationSource.GetString("Settings_ClearRecentFiles_Confirm"), MessageBoxImage.Question, LocalizationSource.GetString("Settings_ClearRecentFiles")) == MessageBoxResult.Yes)
            {
                MainWindow.ClearRecentFiles();
            }
        }

        private void UpdateAppButton_Click(object sender, RoutedEventArgs e)
        {
            ((MainWindow)Owner).UpdateApp(this);
        }

        private void GMLSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            GMLSettingsWindow settings = new(Settings.Instance);
            settings.Owner = this;
            settings.ShowDialog();
            Settings.Save();
        }

        private void BackgroundBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = LocalizationSource.GetString("Settings_BackgroundSelectTitle"),
                Filter = LocalizationSource.GetString("Settings_BackgroundImageFilter")
            };

            if (dialog.ShowDialog() == true)
            {
                Settings.Instance.BackgroundImagePath = dialog.FileName;
                Settings.Save();
                MainWindow.ApplyCustomBackground();
                BackgroundPathBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            }
        }

        private void BackgroundClearButton_Click(object sender, RoutedEventArgs e)
        {
            Settings.Instance.BackgroundImagePath = "";
            Settings.Save();
            MainWindow.ApplyCustomBackground();
            BackgroundPathBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        }
    }
}
