using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Project;
using UndertaleModLib.Scripting;
using UndertaleModTool.Localization;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Built-in equivalents of the UMT "Resource Importers" / "Resource Exporters" scripts,
/// implemented natively instead of being run through <see cref="Scripting"/>.
/// </summary>
public partial class ImportExportService
{
    private readonly MainViewModel mainVM;
    private ILoaderWindow? progressWindow;
    private int progressValue;

    public ImportExportService(MainViewModel mainVM)
    {
        this.mainVM = mainVM;
    }

    // ---- Data access ----

    protected UndertaleData Data => mainVM.Data ?? throw new InvalidOperationException("No data file loaded.");
    protected ProjectContext? Project => mainVM.Project;
    protected string? FilePath => mainVM.DataPath;
    protected string? ExePath => Path.GetDirectoryName(Environment.ProcessPath);

    protected void EnsureDataLoaded()
    {
        if (mainVM.Data is null)
            throw new ScriptException("No data file loaded.");
    }

    // ---- Thread marshaling ----

    protected void MainThreadAction(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Invoke(action);
    }

    // ---- Dialogs ----

    protected T RunOnUI<T>(Func<Task<T>> func)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return func().GetAwaiter().GetResult();

        T result = default!;
        Exception? error = null;
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                result = await func();
            }
            catch (Exception e)
            {
                error = e;
            }
        }).GetAwaiter().GetResult();

        if (error is not null)
            throw error;
        return result;
    }

    protected string? PromptChooseDirectory()
    {
        return RunOnUI(async () =>
        {
            IReadOnlyList<IStorageFolder> folders = await mainVM.View!.OpenFolderDialog(new()
            {
                Title = LocalizationSource.GetString("Msg_SelectDirectory"),
            });

            if (folders.Count != 1)
                return null;

            return folders[0].TryGetLocalPath();
        });
    }

    protected string? PromptLoadFile(string filter)
    {
        return RunOnUI(async () =>
        {
            IReadOnlyList<IStorageFile> files = await mainVM.View!.OpenFileDialog(new FilePickerOpenOptions()
            {
                Title = LocalizationSource.GetString("Msg_LoadFile"),
                FileTypeFilter = FilePickerFileTypes.All,
            });

            if (files.Count != 1)
                return null;

            return files[0].TryGetLocalPath();
        });
    }

    protected string? PromptSaveFile(string defaultExt, string filter)
    {
        return RunOnUI(async () =>
        {
            IStorageFile? file = await mainVM.View!.SaveFileDialog(new FilePickerSaveOptions()
            {
                Title = LocalizationSource.GetString("Msg_SaveFile"),
                FileTypeChoices = FilePickerFileTypes.All,
                DefaultExtension = defaultExt,
            });

            return file?.TryGetLocalPath();
        });
    }

    protected bool ScriptQuestion(string message)
    {
        return RunOnUI(async () =>
            await mainVM.View!.MessageDialog(message, LocalizationSource.GetString("Msg_ScriptQuestionTitle"),
                MessageWindow.Buttons.YesNo) == MessageWindow.Result.Yes);
    }

    protected void ScriptMessage(string message)
    {
        RunOnUI(async () => await mainVM.View!.MessageDialog(message, title: LocalizationSource.GetString("Msg_ScriptMessageTitle")));
    }

    protected void ScriptWarning(string message)
    {
        RunOnUI(async () => await mainVM.View!.MessageDialog(message, title: LocalizationSource.GetString("Msg_ScriptWarningTitle")));
    }

    protected void ScriptError(string error, string? title = null, bool SetConsoleText = true)
    {
        RunOnUI(async () => await mainVM.View!.MessageDialog(error, title ?? LocalizationSource.GetString("Common_Error")));

        if (SetConsoleText)
            mainVM.CommandTextBoxText = error;
    }

    protected string? SimpleTextInput(string title, string label, string defaultValue, bool multiline)
    {
        return RunOnUI(async () =>
            await mainVM.View!.TextBoxDialog(label, defaultValue, title: title, isMultiline: multiline));
    }

    protected void ChangeSelection(object newSelection)
    {
        _ = mainVM.TabOpen(newSelection, inNewTab: true);
    }

    // ---- Progress ----

    private void OpenProgressWindowIfNeeded()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (progressWindow is null)
            {
                progressWindow = mainVM.View!.LoaderOpen();
                progressWindow.EnsureShown();
            }
        });
    }

    protected void SetProgressBar(object? message, string status, double progressValue, double maxValue)
    {
        OpenProgressWindowIfNeeded();

        Dispatcher.UIThread.Post(() =>
        {
            if (progressWindow is null)
                return;
            if (message is string msg)
                progressWindow.SetMessage(msg);
            progressWindow.SetStatus(status);
            progressWindow.SetMaximum((int)maxValue);
            progressWindow.SetValue((int)progressValue);
        });
    }

    protected void UpdateProgressBar(object? message, string status, double progressValue, double maxValue)
    {
        SetProgressBar(message, status, progressValue, maxValue);
    }

    protected void UpdateProgressStatus(string status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            progressWindow?.SetStatus(status);
        });
    }

    protected void UpdateProgressValue(double progressValue)
    {
        this.progressValue = (int)progressValue;

        Dispatcher.UIThread.Post(() =>
        {
            progressWindow?.SetValue(this.progressValue);
        });
    }

    protected void HideProgressBar()
    {
        Dispatcher.UIThread.Post(() =>
        {
            progressWindow?.Close();
            progressWindow = null;
        });
    }

    protected void StartProgressBarUpdater()
    {
        // The Avalonia loader window updates itself; no updater required.
    }

    protected Task StopProgressBarUpdater()
    {
        return Task.CompletedTask;
    }

    protected void IncrementProgress()
    {
        Interlocked.Increment(ref progressValue);

        Dispatcher.UIThread.Post(() =>
        {
            progressWindow?.SetValue(progressValue);
        });
    }

    protected void IncrementProgressParallel()
    {
        IncrementProgress();
    }

    protected void AddProgress(int amount)
    {
        Interlocked.Add(ref progressValue, amount);

        Dispatcher.UIThread.Post(() =>
        {
            progressWindow?.SetValue(progressValue);
        });
    }

    protected void AddProgressParallel(int amount)
    {
        AddProgress(amount);
    }

    protected int GetProgress()
    {
        return progressValue;
    }
}