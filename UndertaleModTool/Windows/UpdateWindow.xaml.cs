using System.Windows;
using System.Windows.Media;
using UndertaleModTool.Localization;
using UndertaleModTool.Update;

namespace UndertaleModTool
{
    /// <summary>
    /// Update confirmation dialog showing the installed and offered versions together with the
    /// release notes of the offered build.
    /// </summary>
    public partial class UpdateWindow : Window
    {
        /// <summary>Designer-only constructor.</summary>
        public UpdateWindow()
        {
            InitializeComponent();
        }

        public UpdateWindow(UpdatePackage package, string currentVersion, bool isNewer)
        {
            InitializeComponent();

            string title = LocalizationSource.GetString(
                isNewer ? "Msg_UpdateDialogTitle" : "Msg_UpdateDialogTitleLatest");
            Title = $"{title} - UndertaleModTool";
            HeaderTitleText.Text = title;
            HeaderSubtitleText.Text = LocalizationSource.GetString(
                isNewer ? "Msg_UpdateDialogSubtitle" : "Msg_UpdateDialogSubtitleLatest");

            CurrentVersionText.Text = string.IsNullOrWhiteSpace(currentVersion) ? "?" : currentVersion;
            LatestVersionText.Text = string.IsNullOrWhiteSpace(package.VersionLabel) ? "?" : package.VersionLabel;
            PrimaryButton.Content = LocalizationSource.GetString(
                isNewer ? "Msg_UpdateDialogUpdateNow" : "Msg_UpdateDialogReinstall");

            string channel = LocalizationSource.GetString(
                package.Channel == UpdateChannel.Stable
                    ? "Settings_UpdateChannelStable"
                    : "Settings_UpdateChannelNightly");
            string source = LocalizationSource.GetString(
                package.Source == UpdateSource.GitHub
                    ? "Settings_UpdateSourceGitHub"
                    : "Settings_UpdateSourceMirrorChyan");
            BadgeText.Text = $"{channel} · {source}";

            string changelog = ChangelogFormatter.ToPlainText(package.Changelog);
            if (changelog.Length == 0)
            {
                ChangelogBox.Text = LocalizationSource.GetString("Msg_UpdateDialogNoChangelog");
                ChangelogBox.Foreground = (Brush)FindResource(SystemColors.GrayTextBrushKey);
            }
            else
            {
                ChangelogBox.Text = changelog;
            }
        }

        private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsVisible || IsLoaded)
                return;

            if (Settings.Instance?.EnableDarkMode == true)
                MainWindow.SetDarkTitleBarForWindow(this, true, false);
        }

        private void UpdateButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void LaterButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
