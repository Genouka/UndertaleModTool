using System;
using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Java.Lang;
using Org.Libsdl.App;
using UndertaleModToolAvalonia;
using AndroidSdl = Org.Libsdl.App.SDL;

namespace UndertaleModToolAvalonia.Android;

/// <summary>
/// The Android global application. On create it bootstraps Avalonia (configuring <see cref="App"/>)
/// and installs the window simulation.
/// </summary>
[Application(AllowBackup = false, Theme = "@style/AppTheme", SupportsRtl = true)]
public class AvaloniaAndroidApp : AvaloniaAndroidApplication<App>
{
    public AvaloniaAndroidApp(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
        // Install crash handlers first: this runs before any activity is created, so crashes during
        // Application/Activity startup are caught too, and the handlers stay active for the whole
        // process lifetime (crash dialog + log file instead of a silent flash-crash).
        CrashHandler.Install(this);

        AndroidWindowHost.Install();
    }

    public override void OnCreate()
    {
        // The dedicated crash-report process (:crashreport) only hosts CrashDialogActivity, a plain
        // Android activity. Skip the Avalonia bootstrap and the script-assembly extraction there:
        // neither is needed for the crash UI, and both would only delay (or risk breaking) the
        // dialog the user is waiting for after a crash.
        if (CrashHandler.IsCrashReportProcess)
            return;

        base.OnCreate();

        // Make the scripting engine reference the plain (non-AOT) DLL copies packaged into the APK
        // assets: they are extracted to internal storage before the first script run.
        ScriptAssemblyExtractor.Install();

        // Extract the built-in utility scripts (assets/Scripts) into internal storage and point
        // the Scripts menu at them. Synchronous, because the menu is built once when the activity
        // UI is created - which still happens after this method completes.
        BuiltInScriptExtractor.Install();

        // Scripts build their scratch ("Packager") folders under ExePath and write results next to
        // it; on Android Environment.ProcessPath resolves to the read-only /system/bin, so point
        // ExePath at an app-private directory instead.
        Scripting.ExePathOverride = CacheDir?.AbsolutePath ?? FilesDir?.AbsolutePath;
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        => base.CustomizeAppBuilder(builder)
            .WithInterFont();
}