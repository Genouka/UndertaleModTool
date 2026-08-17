using System;
using System.Globalization;
using System.IO;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidProcess = global::Android.OS.Process;

namespace UndertaleModToolAvalonia.Android;

/// <summary>
/// Dedicated crash-report dialog activity.
///
/// It is declared with <c>android:process=":crashreport"</c>, so it runs in its own process. That
/// is the key to surviving a "flash crash": when the .NET Android runtime decides to terminate the
/// main process right after the crash handler returns (or the app dies for any other reason), an
/// in-process dialog is destroyed before it can ever render. Because this activity lives in a
/// separate process, the system keeps it alive and the dialog shows the report even if the main
/// process is already dead.
///
/// The crash text and log path are passed as intent extras by <see cref="CrashHandler"/>. If the
/// intent is ever lost (e.g. the activity is recreated), it falls back to reading the tail of the
/// crash log file in app-private storage, which the handler always writes before anything else.
/// </summary>
[Activity(
    Exported = false,
    Theme = "@style/AppTheme",
    ExcludeFromRecents = true,
    Process = ":crashreport",
    Label = "UndertaleModToolAvalonia")]
public class CrashDialogActivity : Activity
{
    /// <summary>Maximum characters rendered inside the dialog; the full text is always in the log.</summary>
    const int MaxShownChars = 8000;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ReloadAndRender();
    }

    /// <summary>Called instead of OnCreate when the activity is reused (SINGLE_TOP).</summary>
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        ReloadAndRender();
    }

    void ReloadAndRender()
    {
        string? text = Intent?.GetStringExtra(CrashHandler.ExtraText);
        string? logPath = Intent?.GetStringExtra(CrashHandler.ExtraLogPath);

        // The intent extras may be missing (activity recreated); fall back to the crash log file,
        // whose tail always contains the most recent crash.
        if (string.IsNullOrEmpty(text))
            text = ReadLastCrashText();
        if (string.IsNullOrEmpty(logPath))
            logPath = CrashHandler.GetCrashLogPath();

        BuildUi(text ?? string.Empty, logPath ?? string.Empty);
    }

    static string ReadLastCrashText()
    {
        try
        {
            string path = CrashHandler.GetCrashLogPath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return string.Empty;

            string all = File.ReadAllText(path);
            return all.Length <= MaxShownChars ? all : all.Substring(all.Length - MaxShownChars);
        }
        catch
        {
            return string.Empty;
        }
    }

    void BuildUi(string text, string logPath)
    {
        bool zh = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        int pad = Dp(20);
        var root = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
        };
        root.SetPadding(pad, pad, pad, pad);

        var title = new TextView(this)
        {
            Text = zh ? "应用发生错误" : "Application error",
            TextSize = 20,
            Typeface = Typeface.DefaultBold,
        };
        root.AddView(title);

        string body = text;
        if (body.Length > MaxShownChars)
            body = body.Substring(0, MaxShownChars) + "\n\n…";
        if (body.Length == 0)
            body = zh
                ? "应用遇到了未处理的异常，但未能捕获到详细错误信息。"
                : "The app hit an unhandled exception, but no error details were captured.";

        var scroll = new ScrollView(this);
        var bodyText = new TextView(this)
        {
            Text = body,
            TextSize = 12,
            Typeface = Typeface.Monospace,
        };
        bodyText.SetTextIsSelectable(true);
        scroll.AddView(bodyText);
        root.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        if (!string.IsNullOrEmpty(logPath))
        {
            var logLine = new TextView(this)
            {
                Text = (zh ? "崩溃日志：" : "Crash log: ") + logPath + "\n" +
                       (zh
                           ? "（可打开该文件查看完整堆栈信息）"
                           : "(open this file for the full stack trace)"),
                TextSize = 11,
            };
            logLine.SetTextColor(new Color(unchecked((int)0xFF888888)));
            root.AddView(logLine);
        }

        var buttons = new LinearLayout(this) { Orientation = Orientation.Horizontal };

        var ok = new Button(this) { Text = zh ? "确定" : "OK" };
        ok.Click += (_, _) => Finish();
        buttons.AddView(ok, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        var exit = new Button(this) { Text = zh ? "退出应用" : "Exit app" };
        exit.Click += (_, _) => AndroidProcess.KillProcess(AndroidProcess.MyPid());
        buttons.AddView(exit, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        root.AddView(buttons);
        SetContentView(root);
    }

    int Dp(int value) => (int)(Resources!.DisplayMetrics!.Density * value + 0.5f);
}
