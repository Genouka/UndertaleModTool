using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UndertaleModToolAvalonia;

namespace UndertaleModToolAvalonia.Android.Api;

/// <summary>
/// Script host used when a script is requested by another app over the cross-app API.
/// <para>
/// It is a <see cref="ScriptGlobals"/> with every user interaction replaced by capture: dialogs,
/// prompts and the progress bar never reach the screen (the API may run in a process without any
/// activity, and an external caller must not be able to pop UI into the user's face). Everything a
/// script would have shown is appended to the job's log, questions are answered with the option
/// the caller passed in, and progress is tracked for <c>getJob</c> polling.
/// </para>
/// </summary>
internal sealed class UmApiScriptHost : ScriptGlobals
{
    readonly UmApiJob job;
    readonly Scripting scripting;

    public UmApiScriptHost(UmApiJob job, Scripting scripting, string? scriptPath)
        : base(scripting, scriptPath)
    {
        this.job = job;
        this.scripting = scripting;
    }

    /// <summary>
    /// Cooperative cancellation: a script stops the next time it reports progress or a message.
    /// Scripts that never touch the host run to completion (see <c>cancelJob</c> in the README).
    /// </summary>
    void Poll()
    {
        if (job.CancelRequested)
            throw new OperationCanceledException("Cancelled by the calling app.");
    }

    void Emit(string text)
    {
        Poll();
        job.Append(text);
    }

    // -- lifecycle ---------------------------------------------------------------------------

    public override void Dispose()
    {
        // The UI-backed host closes its loader window here; this host never opened one (and must
        // not touch the dispatcher while the API walks away from a finished run).
    }

    // -- script state ------------------------------------------------------------------------

    public override object Highlighted => null!;

    public override object Selected => null!;

    public override bool CanSave => Data is not null;

    public override bool ScriptExecutionSuccess => !job.HasError;

    public override string ScriptErrorMessage => job.Error ?? "";

    public override string ScriptErrorType => job.ErrorTitle ?? "";

    public override bool IsAppClosed => false;

    public override void EnableUI()
    {
        // The app's own UI is never disabled for an API-driven run.
    }

    // -- messages and questions --------------------------------------------------------------

    public override void SetUMTConsoleText(string message)
    {
        Poll();
        job.SetConsoleText(message ?? "");
    }

    public override void ScriptMessage(string message) => Emit(message + "\n");

    public override void ScriptWarning(string message) => Emit("[warning] " + message + "\n");

    public override void ScriptError(string error, string? title = null, bool SetConsoleText = true)
    {
        job.SetError(error, title);
        Emit($"[error{(string.IsNullOrWhiteSpace(title) ? "" : " - " + title)}] {error}\n");
    }

    public override bool ScriptQuestion(string message)
    {
        bool answer = job.AssumeYes;
        Emit($"[question] {message} => {(answer ? "Yes" : "No")}\n");
        return answer;
    }

    public override string? ScriptInputDialog(string title, string label, string defaultInput,
                                              string cancelText, string submitText, bool isMultiline,
                                              bool preventClose)
    {
        Emit($"[input] {title}: {label} (no input available over the API, cancelled)\n");
        return null;
    }

    public override string? SimpleTextInput(string title, string label, string defaultValue,
                                            bool allowMultiline, bool showDialog = true)
    {
        Emit($"[input] {title}: {label} => \"{defaultValue}\" (default value returned)\n");
        return defaultValue;
    }

    public override void SimpleTextOutput(string title, string label, string message, bool allowMultiline)
        => Emit(string.IsNullOrWhiteSpace(title) ? message + "\n" : $"[{title}] {message}\n");

    public override void ScriptOpenURL(string url) => Emit("[url] " + url + "\n");

    // -- file / folder pickers ---------------------------------------------------------------

    public override string? PromptChooseDirectory()
    {
        Emit("[dialog] A folder picker was requested; the API cannot show dialogs.\n");
        return null;
    }

    public override string? PromptLoadFile(string? defaultExt, string? filter)
    {
        Emit("[dialog] A file picker was requested; the API cannot show dialogs.\n");
        return null;
    }

    public override string? PromptSaveFile(string defaultExt, string filter)
    {
        Emit("[dialog] A save-file picker was requested; the API cannot show dialogs.\n");
        return null;
    }

    // -- progress ----------------------------------------------------------------------------

    public override int GetProgress() => (int)job.ProgressValue;

    public override void SetProgress(int value)
    {
        Poll();
        job.SetProgress(value, job.ProgressMaximum, null, null);
    }

    public override void AddProgress(int amount)
    {
        Poll();
        job.AddProgress(amount);
    }

    public override void AddProgressParallel(int amount)
    {
        // Called from worker threads inside scripts; keep it lock-free apart from the job's own
        // counter (no cancellation poll here - a throwing worker would only lose thread-pool work).
        job.AddProgress(amount);
    }

    public override void IncrementProgress()
    {
        Poll();
        job.AddProgress(1);
    }

    public override void IncrementProgressParallel() => job.AddProgress(1);

    public override void SetProgressBar(string message, string status, double progressValue, double maxValue)
    {
        Poll();
        job.SetProgress(progressValue, maxValue, message, status);
    }

    public override void SetProgressBar()
    {
        // Progress is reported through the job; there is no window to create.
    }

    public override void UpdateProgressBar(string message, string status, double progressValue, double maxValue)
        => SetProgressBar(message, status, progressValue, maxValue);

    public override void UpdateProgressValue(double progressValue)
    {
        Poll();
        job.SetProgress(progressValue, job.ProgressMaximum, null, null);
    }

    public override void UpdateProgressStatus(string status)
    {
        Poll();
        job.SetProgressStatus(status);
    }

    public override void HideProgressBar()
    {
    }

    public override void InitializeScriptDialog()
    {
    }

    public override void SetFinishedMessage(bool isFinishedMessageEnabled)
    {
    }

    public override void StartProgressBarUpdater()
    {
    }

    public override Task StopProgressBarUpdater() => Task.CompletedTask;

    // -- misc --------------------------------------------------------------------------------

    public override bool LintUMTScript(string path) => true;

    public override bool MakeNewDataFile()
    {
        // NewData() replaces the loaded data and closes tabs, i.e. it touches the visual tree, so
        // it has to run on the UI thread (which is pumping while the script runs on its own thread).
        Avalonia.Threading.Dispatcher.UIThread.Invoke(() => scripting.MainVM.NewData());
        Emit("[data] A new empty data file was created.\n");
        return true;
    }

    /// <summary>Scripts sometimes call other scripts; run it inline against the same job.</summary>
    public override bool RunUMTScript(string path)
    {
        Poll();

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Emit("[error] Script to run was not found: " + path + "\n");
            return false;
        }

        string text = File.ReadAllText(path);
        Emit("[script] Running " + path + "\n");

        // The nested run goes through the same scripting engine; its continuations run on the
        // thread pool (this thread has no synchronization context), so waiting here cannot deadlock.
        scripting.RunScript(
            WithLineDirective(text, path),
            path,
            (engine, scriptPath) => new UmApiScriptHost(job, engine, scriptPath),
            (message, title) =>
            {
                job.SetError(message, title);
                job.Append($"[error{(string.IsNullOrWhiteSpace(title) ? "" : " - " + title)}] {message}\n");
                return Task.CompletedTask;
            }).GetAwaiter().GetResult();

        return true;
    }

    public override Task ClickableSearchOutput(
        string title, string query, int resultsCount,
        IOrderedEnumerable<KeyValuePair<string, List<(int lineNum, string codeLine)>>> resultsDict,
        bool showInDecompiledView, IOrderedEnumerable<string>? failedList = null)
    {
        ReportSearch(title, query, resultsCount, resultsDict.Sum(pair => pair.Value.Count), failedList);
        return Task.CompletedTask;
    }

    public override Task ClickableSearchOutput(
        string title, string query, int resultsCount,
        IDictionary<string, List<(int lineNum, string codeLine)>> resultsDict,
        bool showInDecompiledView, IEnumerable<string>? failedList = null)
    {
        ReportSearch(title, query, resultsCount, resultsDict.Sum(pair => pair.Value.Count), failedList);
        return Task.CompletedTask;
    }

    void ReportSearch(string title, string query, int resultsCount, int lines, IEnumerable<string>? failedList)
    {
        List<string> failed = failedList is null ? [] : [.. failedList];
        Emit($"[search] {title}: \"{query}\" - {resultsCount} result(s), {lines} line(s)" +
             (failed.Count > 0 ? $", {failed.Count} file(s) failed: {string.Join(", ", failed)}" : "") + "\n");
    }

    /// <summary>Mirrors MainViewModel's <c>#line</c> prefixing so diagnostics carry the real path.</summary>
    static string WithLineDirective(string text, string? filePath)
        => filePath is not null ? $"#line 1 \"{filePath.Replace('"', '\'')}\"\n" + text : text;
}
