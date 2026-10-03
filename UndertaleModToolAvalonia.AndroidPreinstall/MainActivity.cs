using System;
using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

// Alias avoids any confusion with the app's own "UndertaleModToolAvalonia.Android" namespace; this
// assembly is separate, so a plain using would work too, but the alias keeps it obvious which
// environment is meant.
using AndroidEnvironment = global::Android.OS.Environment;
using AndroidUri = global::Android.Net.Uri;
using AndroidSettings = global::Android.Provider.Settings;

namespace UndertaleModToolAvalonia.AndroidPreinstall;

/// <summary>
/// The single screen of the storage-setup ("preinstall" / 前置) APK.
/// <para>
/// It does exactly one thing: while the package is still running under the legacy external storage
/// model (this APK targets API 29, see Properties/AndroidManifest.xml), it asks the user for
/// <c>WRITE_EXTERNAL_STORAGE</c> and then tells them to install the real app straight on top of it.
/// Android then carries both the permission grant and the legacy storage model over to the real
/// app, which cannot obtain them on Android 10 by itself because it targets a much newer API level.
/// </para>
/// <para>
/// Nothing here writes any file and nothing here is an "update" from the app's point of view: the
/// two APKs share an application id and a version code on purpose (see build/AndroidApp.props), so
/// the helper can be installed over the app and the app over the helper, without uninstalling and
/// without losing app data.
/// </para>
/// </summary>
[Activity(
    Label = "QiuUTMTv5 存储权限助手",
    Theme = "@android:style/Theme.Material.Light",
    MainLauncher = true,
    Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
public class MainActivity : Activity
{
    /// <summary>Request code for the legacy WRITE_EXTERNAL_STORAGE runtime permission.</summary>
    const int RequestCode = 0x5330; // "St0"

    const string StateRequested = "storagePermissionRequested";

    /// <summary>
    /// Where the real app is downloaded from. Mirrors the release channel in
    /// build/AndroidApp.props: the stable release workflow builds this APK with
    /// <c>-p:AndroidPreinstallChannel=stable</c>, which points at the newest stable release instead
    /// of the rolling nightly one.
    /// </summary>
    static string DownloadPageUrl =>
#if ANDROID_PREINSTALL_STABLE
        "https://github.com/Genouka/UndertaleModTool/releases/latest";
#else
        "https://github.com/Genouka/UndertaleModTool/releases/tag/nightly";
#endif

    static readonly Color SuccessColor = Color.ParseColor("#2E7D32");
    static readonly Color WarningColor = Color.ParseColor("#C62828");
    static readonly Color BodyColor = Color.ParseColor("#37474F");

    TextView? _statusView;
    Button? _grantButton;
    bool _permissionRequested;

    /// <summary>Android 10 (API 29) is the only version this helper is meant for.</summary>
    static bool IsAndroid10 =>
        OperatingSystem.IsAndroidVersionAtLeast(29) && !OperatingSystem.IsAndroidVersionAtLeast(30);

    bool HasStoragePermission =>
        CheckSelfPermission(Manifest.Permission.WriteExternalStorage) == Permission.Granted;

    /// <summary>Whether the package is currently running under the legacy (path-based) storage model.</summary>
    static bool IsLegacyStorageActive =>
        OperatingSystem.IsAndroidVersionAtLeast(29) && AndroidEnvironment.IsExternalStorageLegacy;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _permissionRequested = savedInstanceState?.GetBoolean(StateRequested) ?? false;

        SetContentView(BuildContentView());
        UpdateStatus();

        if (IsAndroid10 && !HasStoragePermission)
            RequestStoragePermission();
    }

    protected override void OnSaveInstanceState(Bundle outState)
    {
        base.OnSaveInstanceState(outState);
        outState.PutBoolean(StateRequested, _permissionRequested);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == RequestCode)
            UpdateStatus();
    }

    protected override void OnResume()
    {
        // The user may have flipped the permission in the system settings in the meantime.
        base.OnResume();
        UpdateStatus();
    }

    /// <summary>Builds the whole screen in code - this APK deliberately has no layout resources.</summary>
    View BuildContentView()
    {
        LinearLayout root = new(this) { Orientation = Orientation.Vertical };
        int padding = Dp(20);
        root.SetPadding(padding, padding, padding, padding);

        root.AddView(CreateLabel(
            "存储权限助手 / Storage Setup",
            22,
            Color.ParseColor("#000000"),
            TypefaceStyle.Bold,
            bottomMargin: 12));

        root.AddView(CreateLabel(
            "本工具只做一件事：在 Android 10 上为本应用取得存储权限，然后把正式版 APK 覆盖安装回来。\n" +
            "This helper does one thing only: it grants the app's storage permission on Android 10, " +
            "after which the normal APK is installed straight on top of it.\n\n" +
            "使用步骤 / Steps:\n" +
            "1. 点击下方按钮，允许存储权限。/ Tap the button below and allow the storage permission.\n" +
            "2. 覆盖安装正式版 QiuUTMTv5 APK（不要卸载本应用）。/ Install the normal QiuUTMTv5 APK " +
            "on top of this one (do NOT uninstall).\n" +
            "3. 打开正式版即可正常读写存储。/ Open the normal app - shared storage now works.",
            14,
            BodyColor,
            TypefaceStyle.Normal,
            bottomMargin: 16));

        _statusView = CreateLabel(string.Empty, 16, BodyColor, TypefaceStyle.Bold, bottomMargin: 16);
        root.AddView(_statusView);

        _grantButton = new Button(this) { Text = "申请存储权限 / Grant storage permission" };
        _grantButton.Click += (_, _) => RequestStoragePermission();
        root.AddView(_grantButton, CreateButtonParams());

        Button downloadButton = new(this) { Text = "打开发布页面 / Open download page" };
        downloadButton.Click += (_, _) => OpenDownloadPage();
        root.AddView(downloadButton, CreateButtonParams());

        Button settingsButton = new(this) { Text = "打开系统设置 / Open app settings" };
        settingsButton.Click += (_, _) => OpenAppSettings();
        root.AddView(settingsButton, CreateButtonParams());

        ScrollView scroll = new(this);
        scroll.AddView(root, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        return scroll;
    }

    void UpdateStatus()
    {
        if (_statusView is null)
            return;

        if (!IsAndroid10)
        {
            // Safety net: the real app only offers this APK to Android 10 users. On anything newer
            // the legacy storage model is not available at all, so there is nothing to grant here.
            _statusView.Text =
                "⚠️ 本工具仅适用于 Android 10。你的设备不需要它，请直接安装正式版 APK。\n" +
                "⚠️ This helper only applies to Android 10. Your device does not need it - install the " +
                "normal APK directly.";
            _statusView.SetTextColor(WarningColor);
            if (_grantButton is not null)
                _grantButton.Enabled = false;
            return;
        }

        if (HasStoragePermission && IsLegacyStorageActive)
        {
            _statusView.Text =
                "✅ 已获得存储权限，旧版存储模型已启用。\n" +
                "✅ Storage permission granted, legacy storage model enabled.\n\n" +
                "现在请覆盖安装正式版 APK（不要卸载本应用）。\n" +
                "Now install the normal APK on top of this one (do not uninstall).";
            _statusView.SetTextColor(SuccessColor);
            if (_grantButton is not null)
                _grantButton.Enabled = false;
            return;
        }

        if (HasStoragePermission)
        {
            _statusView.Text =
                "⚠️ 存储权限已获得，但系统没有启用旧版存储模型。\n" +
                "⚠️ The storage permission is granted, but the legacy storage model is not active.";
            _statusView.SetTextColor(WarningColor);
            return;
        }

        _statusView.Text = _permissionRequested && !ShouldShowRequestPermissionRationale(Manifest.Permission.WriteExternalStorage)
            ? "⚠️ 存储权限被拒绝。请在系统设置中手动允许本应用的存储权限。\n" +
              "⚠️ The storage permission was denied. Please allow storage for this app in the system settings."
            : "尚未获得存储权限，请点击下方按钮授权。\nNo storage permission yet - tap the button below to grant it.";
        _statusView.SetTextColor(WarningColor);
    }

    void RequestStoragePermission()
    {
        if (!IsAndroid10)
            return;

        _permissionRequested = true;
        RequestPermissions(new[] { Manifest.Permission.WriteExternalStorage }, RequestCode);
    }

    void OpenDownloadPage()
    {
        try
        {
            StartActivity(new Intent(Intent.ActionView, AndroidUri.Parse(DownloadPageUrl)));
        }
        catch (ActivityNotFoundException)
        {
            // No browser at all: the step-by-step instructions stay on screen, which is enough.
        }
    }

    void OpenAppSettings()
    {
        try
        {
            StartActivity(new Intent(AndroidSettings.ActionApplicationDetailsSettings,
                AndroidUri.Parse("package:" + PackageName)));
        }
        catch (ActivityNotFoundException)
        {
            // Nothing sensible left to open.
        }
    }

    TextView CreateLabel(string text, double textSize, Color color, TypefaceStyle style, double bottomMargin)
    {
        TextView view = new(this) { Text = text, TextSize = (float)textSize };
        view.SetTextColor(color);
        view.SetTypeface(null, style);
        view.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent)
        {
            BottomMargin = Dp(bottomMargin),
        };
        return view;
    }

    LinearLayout.LayoutParams CreateButtonParams()
    {
        return new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent)
        {
            BottomMargin = Dp(8),
        };
    }

    int Dp(double value) => (int)Math.Round(value * (Resources?.DisplayMetrics?.Density ?? 1));
}
