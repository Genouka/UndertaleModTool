using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Widget;
using Java.Lang;
using UndertaleModTool.Localization;

// Aliases avoid the namespace "UndertaleModToolAvalonia.Android" shadowing "Android.*".
using AndroidEnv = global::Android.OS.Environment;
using AndroidProcess = global::Android.OS.Process;

namespace UndertaleModToolAvalonia.Android;

/// <summary>
/// Global crash handler for the Android app.
///
/// The previous approach (a bare <see cref="AndroidEnvironment.UnhandledExceptionRaiser"/>
/// subscription that only posted a dialog) never worked: the .NET Android runtime terminates the
/// process right after the event handlers return unless <c>RaiseThrowableEventArgs.Handled</c> is
/// set to <see langword="true"/>, so the dialog was destroyed before it could ever be rendered and
/// the app just flash-crashed.
///
/// Setting <c>Handled = true</c> fixes that for exceptions the runtime lets the process survive,
/// but not for every crash the app can hit (exceptions escaping JNI frames, Java-side uncaught
/// exceptions, or any path where the runtime or the OS still kills the process). To make the crash
/// dialog dependable in ALL of those cases, this handler:
/// <list type="bullet">
/// <item>Sets <c>Handled = true</c> so the process survives long enough for the report.</item>
/// <item>Writes the exception to a crash log file first (app-private storage, plus a best-effort
/// copy in public Downloads), so nothing is lost even if no dialog can be shown.</item>
/// <item>Launches <see cref="CrashDialogActivity"/> — a dedicated activity that runs in its own
/// process (<c>:crashreport</c>). Because it is a separate process, the dialog keeps rendering even
/// when the main process is terminated right after this handler returns (the "flash crash" case),
/// which no in-process dialog can survive.</item>
/// <item>Falls back to an in-process <see cref="AlertDialog"/> (or a toast) when the activity
/// cannot be launched, e.g. very early in startup.</item>
/// <item>Also covers exceptions the Android runtime does not route through
/// <see cref="AndroidEnvironment.UnhandledExceptionRaiser"/>: unobserved task exceptions,
/// <see cref="AppDomain.UnhandledException"/>, and the Java default uncaught-exception handler.</item>
/// </list>
/// </summary>
public static class CrashHandler
{
    /// <summary>logcat tag used for crash output.</summary>
    const string LogTag = "QiuUTMT";

    /// <summary>Maximum characters shown inside the in-process fallback dialog; the full text always goes to the log.</summary>
    const int MaxDialogChars = 3000;

    /// <summary>Maximum characters carried in the crash-dialog intent extras (kept well under the Binder transaction limit).</summary>
    const int MaxIntentText = 16000;

    /// <summary>Intent extra carrying the (possibly truncated) crash text.</summary>
    public const string ExtraText = "QiuUTMT.crash_text";

    /// <summary>Intent extra carrying the path of the crash log file.</summary>
    public const string ExtraLogPath = "QiuUTMT.crash_log_path";

    /// <summary>Crash reports with the same text within this window are treated as one crash (dedupe).</summary>
    static readonly long DedupeWindowMs = 1500;

    static readonly Handler s_mainHandler = new(Looper.MainLooper!);

    /// <summary>The most recently created activity, used to attach the fallback dialog to a live window.</summary>
    static Activity? s_currentActivity;

    /// <summary>Reference identity of the last crash we showed a report for (deduplicates the same
    /// exception being routed through several handlers at once).</summary>
    static object? s_lastShownError;

    /// <summary>Text of the last crash we showed a report for, and when (dedupe window).</summary>
    static string? s_lastShownText;
    static long s_lastShownTicks;

    static bool s_coreInstalled;

    /// <summary>True when this process is the dedicated <c>:crashreport</c> process.</summary>
    static bool s_crashProcess;

    // Strong references keep the Java-side peers alive: the framework holds only a JNI reference
    // to these callbacks, and a collected managed wrapper would crash on the next invocation.
    static readonly JavaUncaughtExceptionHandler s_javaUncaughtExceptionHandler = new();
    static readonly ActivityLifecycleCallbacks s_lifecycleCallbacks = new();

    /// <summary>
    /// Must be called as early as possible (from the <see cref="Application"/> constructor): installs
    /// the global crash handlers and starts tracking the foreground activity.
    /// </summary>
    public static void Install(Application app)
    {
        s_crashProcess = DetectCrashReportProcess();
        RegisterCoreHandlers();
        try
        {
            app.RegisterActivityLifecycleCallbacks(s_lifecycleCallbacks);
        }
        catch (System.Exception e)
        {
            Log.Error(LogTag, "Failed to register activity lifecycle callbacks: " + e);
        }
    }

    /// <summary>True when the current process is the dedicated <c>:crashreport</c> process.</summary>
    public static bool IsCrashReportProcess => s_crashProcess;

    /// <summary>
    /// Path of the app-private crash log file (<c>filesDir/utmt_crash.log</c>), or an empty string
    /// when it cannot be determined. Shared between the main process and the crash-report process.
    /// </summary>
    public static string GetCrashLogPath()
    {
        try
        {
            string? filesDir = Application.Context?.FilesDir?.AbsolutePath;
            return filesDir is null ? string.Empty : System.IO.Path.Combine(filesDir, "utmt_crash.log");
        }
        catch
        {
            return string.Empty;
        }
    }

    static bool DetectCrashReportProcess()
    {
        try
        {
            // /proc/self/cmdline holds "<package>[:<processName>]" for app processes; the dedicated
            // crash-report process is "<package>:crashreport".
            string cmdline = File.ReadAllText("/proc/self/cmdline").TrimEnd('\0');
            return cmdline.EndsWith(":crashreport", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    static void RegisterCoreHandlers()
    {
        if (s_coreInstalled)
            return;
        s_coreInstalled = true;

        // Primary path: managed unhandled exceptions (UI thread, background threads, JNI frames).
        // Setting Handled=true is what actually keeps the process alive long enough for the report.
        AndroidEnvironment.UnhandledExceptionRaiser += OnUnhandledException;

        // Safeguards for exceptions the Android runtime does not route through the event above.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => HandleCrash(e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            HandleCrash(e.Exception);
        };
        Java.Lang.Thread.DefaultUncaughtExceptionHandler = s_javaUncaughtExceptionHandler;
    }

    static void OnUnhandledException(object? sender, RaiseThrowableEventArgs args)
    {
        args.Handled = true; // prevent the runtime from aborting the process before the report shows
        HandleCrash(args.Exception);
    }

    /// <summary>
    /// Handles any unhandled error: writes the crash log first, then shows the crash report
    /// (dedicated activity, or dialog/toast as fallback). Never throws.
    /// </summary>
    static void HandleCrash(object? error, string? renderedText = null)
    {
        try
        {
            string text = renderedText ?? error?.ToString() ?? "(no exception information available)";
            Log.Error(LogTag, "UndertaleModToolAvalonia crashed:\n" + text);

            string logPath = WriteCrashLog(text);

            // The same exception instance can be routed through several handlers (e.g. the Android
            // runtime event plus AppDomain.UnhandledException); only show one report for it.
            long now = System.Environment.TickCount64;
            if (error is not null && ReferenceEquals(error, s_lastShownError))
                return;
            if (now - s_lastShownTicks < DedupeWindowMs && string.Equals(text, s_lastShownText, StringComparison.Ordinal))
                return;
            s_lastShownError = error;
            s_lastShownText = text;
            s_lastShownTicks = now;

            // Primary path: dedicated activity in its own process, so the dialog survives even if
            // this process is terminated right after we return. In the crash-report process itself
            // we never relaunch (would recurse); its own crashes fall back to dialog/toast.
            if (!s_crashProcess && TryLaunchCrashActivity(text, logPath))
                return;

            ShowCrashDialog(text, logPath);
        }
        catch
        {
            // The crash handler itself must never crash.
        }
    }

    /// <summary>
    /// Launches <see cref="CrashDialogActivity"/> (separate <c>:crashreport</c> process) with the
    /// crash text and log path. Returns true when the launch was accepted.
    /// </summary>
    static bool TryLaunchCrashActivity(string text, string logPath)
    {
        try
        {
            Context? context = Application.Context;
            if (context is null)
                return false;

            string payload = text.Length > MaxIntentText ? text.Substring(0, MaxIntentText) : text;
            Intent intent = new(context, typeof(CrashDialogActivity));
            intent.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
            intent.PutExtra(ExtraText, payload);
            intent.PutExtra(ExtraLogPath, logPath);
            context.StartActivity(intent);
            Log.Info(LogTag, "CrashHandler: launched CrashDialogActivity with crash report");
            return true;
        }
        catch (System.Exception e)
        {
            Log.Error(LogTag, "CrashHandler: failed to launch CrashDialogActivity: " + e);
            return false;
        }
    }

    static void ShowCrashDialog(string text, string logPath)
    {
        Activity? activity = s_currentActivity;
        if (activity is null || activity.IsFinishing)
        {
            ShowToast(logPath);
            return;
        }

        if (Looper.MyLooper() == Looper.MainLooper)
            DoShowDialog(activity, text, logPath);
        else
            s_mainHandler.Post(() => DoShowDialog(activity, text, logPath));
    }

    static void DoShowDialog(Activity activity, string text, string logPath)
    {
        try
        {
            if (activity.IsFinishing || activity.IsDestroyed)
            {
                ShowToast(logPath);
                return;
            }

            string truncated = text.Length > MaxDialogChars
                ? text.Substring(0, MaxDialogChars) + "\n\n...\n\n(" + L("Crash_FullLogNote", "Full details in the crash log:") + " " + logPath + ")"
                : text + "\n\n" + L("Crash_FullLogNote", "Full details in the crash log:") + " " + logPath;

            var builder = new AlertDialog.Builder(activity);
            builder.SetTitle(L("Msg_UnhandledException", "Application error"));
            builder.SetMessage(truncated);
            builder.SetCancelable(false);
            builder.SetPositiveButton(L("Common_OK", "OK"), (_, _) => { });
            builder.SetNegativeButton(IsChineseLocale ? "退出应用" : "Exit app", (_, _) =>
                AndroidProcess.KillProcess(AndroidProcess.MyPid()));
            builder.Show();
        }
        catch (System.Exception e)
        {
            Log.Error(LogTag, "Failed to show crash dialog: " + e);
            ShowToast(logPath);
        }
    }

    static void ShowToast(string logPath)
    {
        Action show = () =>
        {
            try
            {
                string message = IsChineseLocale
                    ? "应用崩溃，详细信息已写入崩溃日志：\n" + logPath
                    : "The app crashed. Full details were written to the crash log:\n" + logPath;
                Toast.MakeText(Application.Context, message, ToastLength.Long)?.Show();
            }
            catch
            {
                // ignore
            }
        };

        if (Looper.MyLooper() == Looper.MainLooper)
            show();
        else
            s_mainHandler.Post(show);
    }

    /// <summary>
    /// Appends the crash text to a log file. Always writes to app-private storage; additionally
    /// writes a user-visible copy in public Downloads when storage access is granted. Returns the
    /// path that was written (preferring the user-visible one), or an empty string on failure.
    /// </summary>
    static string WriteCrashLog(string text)
    {
        string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] UndertaleModToolAvalonia crashed:\n{text}\n\n";
        string? path = null;

        try
        {
            string? filesDir = Application.Context?.FilesDir?.AbsolutePath;
            if (filesDir is not null)
            {
                path = System.IO.Path.Combine(filesDir, "utmt_crash.log");
                System.IO.Directory.CreateDirectory(filesDir);
                File.AppendAllText(path, entry);
            }
        }
        catch (System.Exception e)
        {
            Log.Error(LogTag, "Failed to write crash log to files dir: " + e);
        }

        try
        {
            Java.IO.File? downloads = AndroidEnv.GetExternalStoragePublicDirectory(AndroidEnv.DirectoryDownloads);
            string? downloadsPath = downloads?.AbsolutePath;
            if (downloadsPath is not null)
            {
                string publicPath = System.IO.Path.Combine(downloadsPath, "QiuUTMTv5_crash.log");
                System.IO.Directory.CreateDirectory(downloadsPath);
                File.AppendAllText(publicPath, entry);
                path = publicPath; // prefer telling the user about the copy they can actually browse
            }
        }
        catch
        {
            // External storage may be unavailable or permission may not be granted; files-dir copy stays.
        }

        return path ?? string.Empty;
    }

    static bool IsChineseLocale =>
        CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>Localized string lookup that can never break the crash handler.</summary>
    static string L(string key, string fallback)
    {
        try
        {
            string? s = LocalizationSource.GetString(key);
            return string.IsNullOrEmpty(s) || s == key ? fallback : s;
        }
        catch
        {
            return fallback;
        }
    }

    sealed class ActivityLifecycleCallbacks : Java.Lang.Object, Application.IActivityLifecycleCallbacks
    {
        public void OnActivityCreated(Activity activity, Bundle? savedInstanceState) => s_currentActivity = activity;

        public void OnActivityDestroyed(Activity activity)
        {
            if (ReferenceEquals(s_currentActivity, activity))
                s_currentActivity = null;
        }

        public void OnActivityPaused(Activity activity) { }
        public void OnActivityResumed(Activity activity) { }
        public void OnActivitySaveInstanceState(Activity activity, Bundle outState) { }
        public void OnActivityStarted(Activity activity) { }
        public void OnActivityStopped(Activity activity) { }
    }

    sealed class JavaUncaughtExceptionHandler : Java.Lang.Object, Java.Lang.Thread.IUncaughtExceptionHandler
    {
        public void UncaughtException(Java.Lang.Thread? thread, Java.Lang.Throwable? ex)
            => HandleCrash(ex, Log.GetStackTraceString(ex) ?? ex?.ToString() ?? "(no exception information available)");
    }
}
