using Avalonia.Controls;
using UndertaleModTool.Localization;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Update confirmation dialog showing the installed and offered versions together with the
/// release notes of the offered build.
/// </summary>
public partial class UpdateWindow : Window
{
    public string WindowTitle { get; } = "";
    public string HeaderTitle { get; } = "";
    public string HeaderSubtitle { get; } = "";
    public string BadgeText { get; } = "";
    public string CurrentVersion { get; } = "";
    public string LatestVersion { get; } = "";
    public string Changelog { get; } = "";
    public string PrimaryButtonText { get; } = "";

    /// <summary>Designer-only constructor.</summary>
    public UpdateWindow()
    {
        InitializeComponent();
    }

    public UpdateWindow(UpdateInfo info, string currentVersion, bool isNewer)
    {
        string title = LocalizationSource.GetString(
            isNewer ? "Msg_UpdateDialogTitle" : "Msg_UpdateDialogTitleLatest");

        WindowTitle = $"{title} - UndertaleModTool";
        HeaderTitle = title;
        HeaderSubtitle = LocalizationSource.GetString(
            isNewer ? "Msg_UpdateDialogSubtitle" : "Msg_UpdateDialogSubtitleLatest");

        CurrentVersion = string.IsNullOrWhiteSpace(currentVersion) ? "?" : currentVersion;
        LatestVersion = string.IsNullOrWhiteSpace(info.Version) ? "?" : info.Version;
        PrimaryButtonText = LocalizationSource.GetString(
            isNewer ? "Msg_UpdateDialogUpdateNow" : "Msg_UpdateDialogReinstall");

        string channel = LocalizationSource.GetString(
            info.Channel == UpdateChannel.Stable
                ? "Settings_UpdateChannelStable"
                : "Settings_UpdateChannelNightly");
        string source = LocalizationSource.GetString(
            info.Source == UpdateSource.GitHub
                ? "Settings_UpdateSourceGitHub"
                : "Settings_UpdateSourceMirrorChyan");
        BadgeText = $"{channel} · {source}";

        string changelog = ChangelogFormatter.ToPlainText(info.Changelog);
        Changelog = changelog.Length == 0
            ? LocalizationSource.GetString("Msg_UpdateDialogNoChangelog")
            : changelog;

        InitializeComponent();
    }

    public void UpdateClick()
    {
        Close(true);
    }

    public void LaterClick()
    {
        Close(false);
    }
}
