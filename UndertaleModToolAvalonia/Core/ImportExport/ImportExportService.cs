using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.CodeAnalysis;
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
    /// <summary>
    /// Platform hook supplying a writable application cache directory. On Android there is no
    /// executable directory (<c>Environment.ProcessPath</c> points into the read-only
    /// <c>/system/bin</c>), so the import/export services derive their scratch folders (the
    /// "Packager" working directories) from the app's cache directory instead. Wired by the
    /// Android head at startup.
    /// </summary>
    public static Func<string?>? PlatformCacheDirectoryProvider { get; set; }

    protected string? ExePath {
        get
        {
            if (OperatingSystem.IsAndroid())
            {
                return PlatformCacheDirectoryProvider?.Invoke()
                    ?? Environment.GetEnvironmentVariable("TMPDIR")
                    ?? Path.GetTempPath();
            }
            return Path.GetDirectoryName(Environment.ProcessPath);
        }
    }

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

            string? localPath = folders[0].TryGetLocalPath();
            if (localPath is null)
            {
                // Android SAF folders are content:// URIs without a local path. The path-based
                // importers cannot read them; say so explicitly instead of silently doing nothing.
                await mainVM.View!.MessageDialog(
                    "The selected folder cannot be accessed directly on this platform (no local file path). Importing from it is not supported yet.",
                    title: LocalizationSource.GetString("Msg_ScriptMessageTitle"));
            }
            return localPath;
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

            if (file is null)
                return null;

            string? localPath = file.TryGetLocalPath();
            if (localPath is not null)
            {
                ClearPendingExport();
                return localPath;
            }

            // The picked file has no local path (Android SAF content:// URI): stage the export
            // into a temporary local file and copy it into the picked file in FinalizeExportAsync.
            string tempFile = Path.Combine(Path.GetTempPath(), "umt-export-" + Guid.NewGuid().ToString("N") + ".tmp");

            ClearPendingExport();
            pendingExportFile = file;
            pendingExportTempFile = tempFile;
            pendingExportTargetName = file.Name;
            return tempFile;
        });
    }

    // ---- Export staging for platforms without local paths (Android SAF) ----
    //
    // The exporters below are all path-based (File.WriteAllText, Path.Join, TextureWorker...),
    // which cannot address Android SAF folders/files (content:// URIs, no local path). To make
    // them work on Android, the picked target is "staged": the export writes into a temporary
    // local directory/file, and FinalizeExportAsync() copies the result into the folder/file the
    // user actually picked using the SAF stream API (the same one the file-save flow uses).

    private IStorageFolder? pendingExportFolder;
    private string? pendingExportTempDir;

    private IStorageFile? pendingExportFile;
    private string? pendingExportTempFile;

    private string? pendingExportTargetName;

    private void ClearPendingExport()
    {
        if (pendingExportTempDir is not null)
        {
            try
            {
                if (Directory.Exists(pendingExportTempDir))
                    Directory.Delete(pendingExportTempDir, recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
        if (pendingExportTempFile is not null)
        {
            try
            {
                if (File.Exists(pendingExportTempFile))
                    File.Delete(pendingExportTempFile);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
        pendingExportFolder?.Dispose();
        pendingExportFile?.Dispose();
        pendingExportFolder = null;
        pendingExportTempDir = null;
        pendingExportFile = null;
        pendingExportTempFile = null;
        pendingExportTargetName = null;
    }

    /// <summary>
    /// Prompts for a directory to export into. On platforms where the picked folder has no local
    /// path (e.g. Android SAF returns a <c>content://</c> folder), the export is staged into a
    /// temporary local directory and <see cref="FinalizeExportAsync"/> copies the result into the
    /// picked folder afterwards, keeping the path-based exporters working.
    /// </summary>
    protected string? PromptChooseExportDirectory()
    {
        return RunOnUI(async () =>
        {
            IReadOnlyList<IStorageFolder> folders = await mainVM.View!.OpenFolderDialog(new()
            {
                Title = LocalizationSource.GetString("Msg_SelectDirectory"),
            });

            if (folders.Count != 1)
                return null;

            string? localPath = folders[0].TryGetLocalPath();
            if (localPath is not null)
            {
                ClearPendingExport();
                return localPath;
            }

            string tempDir = Path.Combine(Path.GetTempPath(), "umt-export-" + Guid.NewGuid().ToString("N"));

            ClearPendingExport();
            Directory.CreateDirectory(tempDir);
            pendingExportFolder = folders[0];
            pendingExportTempDir = tempDir;
            pendingExportTargetName = folders[0].Name;
            return tempDir;
        });
    }

    /// <summary>
    /// Copies a staged export (see <see cref="PromptChooseExportDirectory"/> and
    /// <see cref="PromptSaveFile"/>) into the SAF folder/file the user picked, then cleans up the
    /// staging area. A no-op when no SAF target is pending (e.g. on desktop).
    /// </summary>
    protected async Task FinalizeExportAsync()
    {
        if (pendingExportFolder is { } folder && pendingExportTempDir is { } tempDir)
        {
            pendingExportFolder = null;
            pendingExportTempDir = null;
            pendingExportTargetName = null;

            try
            {
                if (Directory.Exists(tempDir))
                {
                    int totalFiles = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories).Length;
                    SetProgressBar(null, "Copying to selected folder...", 0, totalFiles);
                    await CopyDirectoryToStorageFolderAsync(tempDir, folder);
                    HideProgressBar();
                }
            }
            catch (Exception e)
            {
                ScriptError($"Failed to copy the exported files to the selected folder:\n{e.Message}");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, recursive: true);
                }
                catch
                {
                    // Best effort cleanup only.
                }
                folder.Dispose();
            }
            return;
        }

        if (pendingExportFile is { } file && pendingExportTempFile is { } tempFile)
        {
            pendingExportFile = null;
            pendingExportTempFile = null;
            pendingExportTargetName = null;

            try
            {
                if (File.Exists(tempFile))
                {
                    using Stream destStream = await file.OpenWriteAsync();
                    await using FileStream srcStream = File.OpenRead(tempFile);
                    await srcStream.CopyToAsync(destStream);
                    await destStream.FlushAsync();
                }
            }
            catch (Exception e)
            {
                ScriptError($"Failed to copy the exported file:\n{e.Message}");
            }
            finally
            {
                try
                {
                    if (File.Exists(tempFile))
                        File.Delete(tempFile);
                }
                catch
                {
                    // Best effort cleanup only.
                }
                file.Dispose();
            }
        }
    }

    /// <summary>A display name for the last picked SAF export target, or null (desktop/local path).</summary>
    protected string? LastExportTargetName => pendingExportTargetName;

    private static async Task CopyDirectoryToStorageFolderAsync(string sourceDir, IStorageFolder destFolder)
    {
        foreach (string subDir in Directory.GetDirectories(sourceDir))
        {
            string name = SanitizeFileName(Path.GetFileName(subDir));
            IStorageFolder? sub = await destFolder.CreateFolderAsync(name);
            if (sub is null)
                continue;
            try
            {
                await CopyDirectoryToStorageFolderAsync(subDir, sub);
            }
            finally
            {
                sub.Dispose();
            }
        }

        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string name = SanitizeFileName(Path.GetFileName(file));
            IStorageFile? destFile = await destFolder.CreateFileAsync(name);
            if (destFile is null)
                continue;
            try
            {
                using Stream destStream = await destFile.OpenWriteAsync();
                await using FileStream srcStream = File.OpenRead(file);
                await srcStream.CopyToAsync(destStream);
                await destStream.FlushAsync();
            }
            finally
            {
                destFile.Dispose();
            }
        }
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "_";

        char[] invalid = Path.GetInvalidFileNameChars();
        char[] chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (char.IsControl(chars[i]) || Array.IndexOf(invalid, chars[i]) >= 0)
                chars[i] = '_';
        }
        return new string(chars);
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