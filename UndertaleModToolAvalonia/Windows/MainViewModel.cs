using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Project;
using UndertaleModLib.Util;
using UndertaleModTool.Localization;

namespace UndertaleModToolAvalonia;

public partial class MainViewModel : ObservableObject
{
    // Set this when testing.
    public IView? View;

    // Services
    public readonly IServiceProvider ServiceProvider;

    /// <summary>Error messages to be displayed after the view has been loaded.</summary>
    public List<string> LazyErrorMessages = [];

    // Settings
    public SettingsFile? Settings { get; set; }

    // Scripting
    public Scripting Scripting = null!;

    // Built-in import/export
    public ImportExportService ImportExportService = null!;

    // Window
    public string Title => $"UndertaleModToolAvalonia by luizzeroxis by Genouka - v" +
        (App.VersionString) +
        $"{(Project?.Name is not null ? " - " + Project.Name : "")}" +
        $"{(Data?.GeneralInfo is not null ? " - " + Data.GeneralInfo.ToString() : "")}" +
        $"{(DataPath is not null ? " [" + DataPath + "]" : "")}";

    [ObservableProperty]
    public partial WindowState WindowState { get; set; } = WindowState.Maximized;

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    // Data
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(ProjectActive))]
    public partial UndertaleData? Data { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(ProjectActive))]
    public partial string? DataPath { get; set; }

    [ObservableProperty]
    public partial (uint Major, uint Minor, uint Release, uint Build) DataVersion { get; set; }

    IStorageFolder? lastDataLocation;

    // Project
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(ProjectActive))]
    public partial ProjectContext? Project { get; set; }

    /// <summary>Whether a project is currently usable (a data file is loaded and a project is set),
    /// used to enable/disable project menu items like the WPF version does.</summary>
    public bool ProjectActive => Project is not null && Data is not null && DataPath is not null;

    // Left panel
    public DataExplorerViewModel DataExplorer { get; set; }

    [ObservableProperty]
    public partial string FilterText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsSorted { get; set; } = false;

    // Tabs
    public ObservableCollection<TabItemViewModel> Tabs { get; set; } = [];

    [ObservableProperty]
    public partial TabItemViewModel? TabSelected { get; set; }

    [ObservableProperty]
    public partial int TabSelectedIndex { get; set; }

    [ObservableProperty]
    public partial bool TabIsMarkedForExport { get; set; } = false;

    [ObservableProperty]
    public partial bool TabCanMarkedForExport { get; set; } = false;

    [ObservableProperty]
    public partial string TabSelectedResourceIdString { get; set; } = "None";

    // Command text box
    [ObservableProperty]
    public partial string CommandTextBoxText { get; set; } = "";

    // Image cache
    public ImageCache ImageCache = new();

    // Internal clipboard
    public object? InternalClipboard = null;

    public MainViewModel(IServiceProvider serviceProvider)
    {
        ServiceProvider = serviceProvider;

        AudioPlayer.Init(
            f => Dispatcher.UIThread.Post(f),
            // Audio failures (SDL init, decode, playback) are reported as a message dialog
            // instead of escaping the async void play handlers and crashing the app.
            // Fire-and-forget: this callback already runs on the UI thread (posted by
            // AudioPlayer.ReportError), and blocking it here would need nested message loops,
            // which the Android dispatcher does not support.
            message => _ = View?.MessageDialog(
                $"{LocalizationSource.GetString("Msg_FailedPlayAudio")}\n{message}",
                title: LocalizationSource.GetString("Msg_AudioFailure")));

        DataExplorer = new(this);

        _ = TabOpen(new DescriptionViewModel(
            LocalizationSource.GetString("Main_WelcomeHeading"),
            LocalizationSource.GetString("Main_WelcomeDescription")));
    }

    public void Initialize()
    {
        Settings = SettingsFile.Load(ServiceProvider);
        Scripting = new(ServiceProvider);
        ImportExportService ??= new(this);

        if (!string.IsNullOrEmpty(Settings.Language))
            LocalizationSource.Instance.CurrentCulture = new System.Globalization.CultureInfo(Settings.Language);

        WindowState = Settings.StartMaximized ? WindowState.Maximized : WindowState.Normal;
    }

    public async void OnLoaded()
    {
        foreach (string message in LazyErrorMessages)
        {
            await View!.MessageDialog(message);
        }
        LazyErrorMessages.Clear();

        CheckForUpdatesAutomatically();

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.Args?.Length >= 1)
            {
                try
                {
                    using FileStream stream = File.OpenRead(desktop.Args[0]);
                    if (await LoadData(stream))
                    {
                        DataPath = stream.Name;
                    }
                }
                catch (SystemException e)
                {
                    await View!.MessageDialog(string.Format(LocalizationSource.GetString("Msg_ErrorOpeningDataFileArgument"), e.Message));
                }
            }
        }
    }

    [RelayCommand]
    public async Task OpenDroppedFiles(IEnumerable<IStorageItem>? files)
    {
        if (files is null)
            return;

        var list = files.ToList();
        if (list.Count != 1)
            return;

        if (list[0] is not IStorageFile file)
            return;

        if (!await AskFileSave(LocalizationSource.GetString("Msg_SaveDataFileBeforeOpeningNew")))
            return;

        CloseData();

        using Stream stream = await file.OpenReadAsync();

        if (await LoadData(stream))
        {
            DataPath = file.TryGetLocalPath();
            lastDataLocation = await file.GetParentAsync();
        }
    }

    partial void OnDataChanged(UndertaleData? value)
    {
        if (Data is not null)
        {
            Data.ToolInfo.InstanceIdPrefix = () => Settings?.InstanceIdPrefix;
            Data.ToolInfo.DecompilerSettings = Settings?.DecompileSettings;
        }

        UpdateVersion();

        DataExplorer.UpdateFromData();

        if (Data is not null)
        {
            if (View is MainView mainView)
                mainView.ExpandItemOnTree(DataExplorer.TreeDataGridData[0]);
        }
    }

    partial void OnFilterTextChanged(string value)
    {
        DataExplorer.SetFilter();
    }

    partial void OnIsSortedChanged(bool value)
    {
        DataExplorer.SetSort();
    }

    /// <summary>Ask if user wants to save the current file before continuing.
    /// Returns true if either it saved successfully, or if the user didn't want to save, or if there is no file loaded.</summary>
    public async Task<bool> AskFileSave(string message)
    {
        if (Data is null)
            return true;

        var result = await View!.MessageDialog(message, buttons: MessageWindow.Buttons.YesNoCancel);
        if (result == MessageWindow.Result.Yes)
        {
            if (await FileSaveTask())
            {
                return true;
            }
        }
        else if (result == MessageWindow.Result.No)
        {
            return true;
        }

        return false;
    }

    /// <summary>Ask if user wants to save the current project before continuing.
    /// Returns true if either it saved successfully, or if the user didn't want to save, or if there is no project loaded, or if the project has no unexported assets.</summary>
    public async Task<bool> AskProjectSave(string message)
    {
        if (Project is null || !Project.HasUnexportedAssets)
            return true;

        var result = await View!.MessageDialog(message, buttons: MessageWindow.Buttons.YesNoCancel);
        if (result == MessageWindow.Result.Yes)
        {
            if (await ProjectSaveTask())
            {
                return true;
            }
        }
        else if (result == MessageWindow.Result.No)
        {
            return true;
        }

        return false;
    }

    public void NewData()
    {
        CloseData();

        Data = UndertaleData.CreateNew();
        DataPath = null;
    }

    public async Task<bool> LoadData(Stream stream)
    {
        IsEnabled = false;

        ILoaderWindow w = View!.LoaderOpen();
        w.SetText(LocalizationSource.GetString("Msg_OpeningDataFile"));

        try
        {
            List<string> warnings = [];
            bool hadImportantWarnings = false;

            UndertaleData data = await Task.Run(() => UndertaleIO.Read(stream,
                (string warning, bool isImportant) =>
                {
                    warnings.Add(warning);
                    if (isImportant)
                    {
                        hadImportantWarnings = true;
                    }
                },
                (string message) =>
                {
                    Dispatcher.UIThread.Post(() => w.SetText(LocalizationSource.GetString("Msg_OpeningDataFile") + " " + message));
                })
            );

            if (warnings.Count > 0)
            {
                w.EnsureShown();
await View!.MessageDialog(LocalizationSource.GetString("Msg_WarningsOccurred") + "\n\n" +
            $"{(hadImportantWarnings ? LocalizationSource.GetString("Msg_DataLossLikely") + "\n" : "")}" +
                    $"{String.Join("\n", warnings)}");
            }

            // TODO: Add other checks for possible stuff.

            Data = data;

            return true;
        }
        catch (Exception e)
        {
            w.EnsureShown();
            await View!.MessageDialog(string.Format(LocalizationSource.GetString("Msg_ErrorOpeningDataFile"), e.Message));

            return false;
        }
        finally
        {
            IsEnabled = true;
            w.Close();
        }
    }

    public async Task<bool> SaveData(Stream stream)
    {
        IsEnabled = false;

        ILoaderWindow w = View!.LoaderOpen();
        w.SetText(LocalizationSource.GetString("Msg_SavingDataFile"));

        try
        {
            // Recompile all code sources before saving, if requested and a project is open (mirrors the WPF version)
            if (Settings!.RecompileAllCodeSourcesOnProjectSave && Project is not null)
            {
                try
                {
                    await Task.Run(() => Project.RecompileAllCodeSources());
                }
                catch (ProjectException e)
                {
                    w.EnsureShown();
                    await View!.MessageDialog(e.Message, title: LocalizationSource.GetString("Msg_RecompileError"));
                    return false;
                }
                catch (Exception e)
                {
                    w.EnsureShown();
                    await View!.MessageDialog(string.Format(LocalizationSource.GetString("Msg_RecompileErrorDetail"), e.Message),
                        title: LocalizationSource.GetString("Msg_RecompileError"));
                    return false;
                }
            }

            await Task.Run(() => UndertaleIO.Write(stream, Data, message =>
            {
                Dispatcher.UIThread.Post(() => w.SetText(LocalizationSource.GetString("Msg_SavingDataFile") + " " + message));
            }));

            return true;
        }
        catch (ProjectException e)
        {
            w.EnsureShown();
            await View!.MessageDialog($"Recompile error:\n{e.Message}");
        }
        catch (Exception e)
        {
            w.EnsureShown();
            await View!.MessageDialog(LocalizationSource.GetString("Msg_ErrorSavingDataFile") + "\n" + e.Message);
        }
        finally
        {
            IsEnabled = true;
            w.Close();
        }

        return false;
    }

    public void CloseData()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (var window in desktop.Windows.ToList())
            {
                if (window is SearchInCodeWindow or FindReferencesWindow or ProjectAssetsWindow)
                {
                    window.Close();
                }
            }
        }

        TabCloseAllWithoutSaving();

        ClearProject();

        Data = null;
        DataPath = null;
    }

    public void UpdateVersion()
    {
        DataVersion = Data is not null && Data.GeneralInfo is not null ? (Data.GeneralInfo.Major, Data.GeneralInfo.Minor, Data.GeneralInfo.Release, Data.GeneralInfo.Build) : default;
    }

    void DataGeneralInfoChangedHandler(object? sender, PropertyChangedEventArgs e)
    {
        if (Data is not null && e.PropertyName is
            nameof(UndertaleGeneralInfo.Major) or nameof(UndertaleGeneralInfo.Minor) or
            nameof(UndertaleGeneralInfo.Release) or nameof(UndertaleGeneralInfo.Build))
        {
            UpdateVersion();
        }
    }

    // Menus
    public async void FileNew()
    {
        if (await AskProjectSave(LocalizationSource.GetString("Msg_SaveProjectBeforeClosing"))
            && await AskFileSave(LocalizationSource.GetString("Msg_SaveBeforeCreatingNew")))
        {
            NewData();
        }
    }

    public async void FileOpen()
    {
        if (!await AskProjectSave(LocalizationSource.GetString("Msg_SaveProjectBeforeClosing")))
            return;
        if (!await AskFileSave(LocalizationSource.GetString("Msg_SaveDataFileBeforeOpeningNew")))
            return;

        var files = await View!.OpenFileDialog(new FilePickerOpenOptions()
        {
            Title = LocalizationSource.GetString("Msg_OpenDataFile"),
            FileTypeFilter = FilePickerFileTypes.Data,
            SuggestedStartLocation = lastDataLocation,
        });

        if (files.Count != 1)
            return;

        CloseData();

        using Stream stream = await files[0].OpenReadAsync();

        if (await LoadData(stream))
        {
            DataPath = files[0].TryGetLocalPath();
            lastDataLocation = await files[0].GetParentAsync();
        }
    }

    public async void FileSave()
    {
        await FileSaveTask();
    }

    public async Task<bool> FileSaveTask()
    {
        if (Data is null)
            return false;

        if (!await TabSaveAll())
            return false;

        if (Project is not null)
        {
            var result = await View!.MessageDialog(LocalizationSource.GetString("Msg_SaveToProjectDataFileQuestion"), buttons: MessageWindow.Buttons.YesNoCancel);
            if (result == MessageWindow.Result.Yes)
            {
                using FileStream fileStream = File.Open(Project.SaveDataPath, FileMode.Create);
                if (await SaveData(fileStream))
                {
                    return true;
                }
                return false;
            }
            else if (result != MessageWindow.Result.No)
            {
                return false;
            }
            // If pressed No, continue saving as if there's no project.
        }

        IStorageFile? file = await View!.SaveFileDialog(new FilePickerSaveOptions()
        {
            Title = LocalizationSource.GetString("Msg_SaveDataFileTitle"),
            FileTypeChoices = FilePickerFileTypes.Data,
            SuggestedFileName = Path.GetFileName(DataPath),
            SuggestedStartLocation = lastDataLocation,
        });

        if (file is null)
            return false;

        string? path = file.TryGetLocalPath();

        try
        {
            if (path is null)
            {
                // Android SAF ��������ܲ�֧���������(���� Seek),�� UndertaleWriter ����
                // Position/Seek���Ȱ�����д�뻺�������ʱ�ļ�(�� Seek),��˳�򿽱�����ѡ�ļ���
                string tempFilePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (FileStream tempStream = File.Open(tempFilePath, FileMode.CreateNew, FileAccess.ReadWrite))
                    {
                        if (!await SaveData(tempStream))
                        {
                            return false;
                        }
                        tempStream.Flush(flushToDisk: true);
                        tempStream.Position = 0;

                        using Stream destStream = await file.OpenWriteAsync();
                        await tempStream.CopyToAsync(destStream);
                        await destStream.FlushAsync();
                    }

                    lastDataLocation = await file.GetParentAsync();
                    return true;
                }
                finally
                {
                    File.Delete(tempFilePath);
                }
            }

            string tempPath = path + "temp";

            bool saved = false;

            using (FileStream stream = File.Open(tempPath, FileMode.CreateNew, FileAccess.Write))
            {
                await SaveData(stream);
                saved = true;

                stream.Flush(flushToDisk: true);
            }

            if (saved)
            {
                File.Move(tempPath, path, overwrite: true);

                DataPath = path;
                lastDataLocation = await file.GetParentAsync();
                return true;
            }
            else
            {
                File.Delete(tempPath);
            }
        }
        catch (IOException ex)
        {
            await View!.MessageDialog(LocalizationSource.GetString("Msg_ErrorSavingDataFile") + "\n" + ex.Message);
        }

        return false;
    }

    public async void FileClose()
    {
        if (!await AskProjectSave(LocalizationSource.GetString("Msg_SaveProjectBeforeClosing")))
            return;
        if (!await AskFileSave(LocalizationSource.GetString("Msg_SaveDataFileBeforeClosing")))
            return;

        CloseData();
    }

    public async void FileRun()
    {
        if (Data is null)
            return;

        string? runnerName = Data.GeneralInfo?.FileName?.Content;
        if (runnerName is null)
        {
            await View!.MessageDialog(LocalizationSource.GetString("Msg_FileNameNotSet"));
            return;
        }

        string question = $"{LocalizationSource.GetString("Msg_SaveBeforeRun")} {(DataPath is null
            ? LocalizationSource.GetString("Msg_ItMustBeSavedBeforeRunning")
            : string.Format(LocalizationSource.GetString("Msg_DataFileAtLastLocation"), DataPath))}";

        if (!await AskFileSave(question))
            return;

        if (DataPath is null)
            return;

        string? runnerPath;

        if (Project is not null)
        {
            runnerPath = Paths.TryJoinVerifyWithinDirectory(Project.SaveDirectory, $"{runnerName}.exe");
        }
        else
        {
            runnerPath = Paths.TryJoinVerifyWithinDirectory(Path.GetDirectoryName(DataPath), $"{runnerName}.exe");
        }

        if (runnerPath is null || !File.Exists(runnerPath))
        {
            await View!.MessageDialog(LocalizationSource.GetString("Msg_InvalidRunner"));
            return;
        }

        StartRunnerProcess(runnerPath);
    }

    public async void FileRunWithOther()
    {
        // NOTE: The project system would make this a lot simpler!
        if (Data is null)
            return;

        string question = $"{LocalizationSource.GetString("Msg_SaveBeforeRun")} {(DataPath is null
            ? LocalizationSource.GetString("Msg_ItMustBeSavedBeforeRunning")
            : string.Format(LocalizationSource.GetString("Msg_DataFileAtLastLocation"), DataPath))}";

        if (!await AskFileSave(question))
            return;

        if (DataPath is null)
            return;

        var files = await View!.OpenFileDialog(new FilePickerOpenOptions()
        {
            Title = LocalizationSource.GetString("Msg_OpenRunner"),
            FileTypeFilter = FilePickerFileTypes.All,
        });

        if (files.Count != 1)
            return;

        string runnerPath = files[0].TryGetLocalPath() ?? string.Empty;
        if (runnerPath == string.Empty)
            return;

        if (!File.Exists(DataPath))
            return;

        StartRunnerProcess(runnerPath);
    }

    void StartRunnerProcess(string runnerPath)
    {
        // "launcher" allows game_change data files to still access files above the data path.
        Process.Start(new ProcessStartInfo(runnerPath, $"-game \"{DataPath}\" launcher") { WorkingDirectory = Path.GetDirectoryName(DataPath) });
    }

    public async void FileSettings()
    {
        if (View is MainView mainView)
            await mainView.OpenSettingsDialog(ServiceProvider);
    }

    public void FileExit()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    public void ToolsSearchInCode()
    {
        if (View is MainView mainView)
            mainView.OpenSearchInCode(ServiceProvider);
    }

    public void ToolsFindReferences()
    {
        OpenFindReferences();
    }

    // Import commands
    public void ImportGraphics() => Task.Run(() => ImportExportService.ImportGraphics());
    public void ImportGraphicsAdvanced() => Task.Run(() => ImportExportService.ImportGraphicsAdvanced());
    public void ImportApplyBasicGraphicsMod() => Task.Run(() => ImportExportService.ApplyBasicGraphicsMod());
    public void ImportAllEmbeddedTextures() => Task.Run(() => ImportExportService.ImportAllEmbeddedTextures());
    public void ImportAllTilesets() => Task.Run(() => ImportExportService.ImportAllTilesets());
    public void ImportAllStrings() => Task.Run(() => ImportExportService.ImportAllStrings());
    public void ImportAllStringsJSON() => Task.Run(() => ImportExportService.ImportAllStringsJSON());
    public void ImportFonts() => Task.Run(() => ImportExportService.ImportFonts());
    public void ImportGMS2FontData() => Task.Run(() => ImportExportService.ImportGMS2FontData());
    public void ImportGML() => Task.Run(() => ImportExportService.ImportGML());
    public void ImportAssembly() => Task.Run(() => ImportExportService.ImportAssembly());
    public void ImportMasks() => Task.Run(() => ImportExportService.ImportMasks());
    public void ImportShaders() => Task.Run(() => ImportExportService.ImportShaders());
    public void ImportSounds() => Task.Run(() => ImportExportService.ImportSounds());
    public void ImportSingleSound() => Task.Run(() => ImportExportService.ImportSingleSound());
    public void NewTextureRepacker() => Task.Run(() => ImportExportService.NewTextureRepacker());
    public void ReduceEmbeddedTexturePages() => Task.Run(() => ImportExportService.ReduceEmbeddedTexturePages());

    // Export commands
    public void ExportAllSprites() => Task.Run(() => ImportExportService.ExportAllSprites());
    public void ExportAllTextures() => Task.Run(() => ImportExportService.ExportAllTextures());
    public void ExportAllTexturesGrouped() => Task.Run(() => ImportExportService.ExportAllTexturesGrouped());
    public void ExportAllTilesets() => Task.Run(() => ImportExportService.ExportAllTilesets());
    public void ExportAllMasks() => Task.Run(() => ImportExportService.ExportAllMasks());
    public void ExportAllEmbeddedTextures() => Task.Run(() => ImportExportService.ExportAllEmbeddedTextures());
    public void ExportAllFonts() => Task.Run(() => ImportExportService.ExportAllFonts());
    public void ExportAllShaders() => Task.Run(() => ImportExportService.ExportAllShaders());
    public void ExportAllSounds() => Task.Run(() => ImportExportService.ExportAllSounds());
    public void ExportAllStrings() => Task.Run(() => ImportExportService.ExportAllStrings());
    public void ExportAllStringsJSON() => Task.Run(() => ImportExportService.ExportAllStringsJSON());
    public void ExportAllCode() => Task.Run(() => ImportExportService.ExportAllCode());
    public void ExportAllAssembly() => Task.Run(() => ImportExportService.ExportAllAssembly());
    public void ExportSpecificCode() => Task.Run(() => ImportExportService.ExportSpecificCode());
    public void ExportSpecificSprites() => Task.Run(() => ImportExportService.ExportSpecificSprites());
    public void ExportSpritesAsGIF() => Task.Run(() => ImportExportService.ExportSpritesAsGIF());
    public void ExportTextureGroups() => Task.Run(() => ImportExportService.ExportTextureGroups());
    public void ExportAllRoomsToPNG() => Task.Run(() => ImportExportService.ExportAllRoomsToPNG());

    public async void ScriptsRunOtherScript()
    {
        var files = await View!.OpenFileDialog(new FilePickerOpenOptions()
        {
            Title = LocalizationSource.GetString("Msg_RunScriptTitle"),
            FileTypeFilter = FilePickerFileTypes.CS,
        });

        if (files.Count != 1)
            return;

        string text;
        using (Stream stream = await files[0].OpenReadAsync())
        {
            using StreamReader streamReader = new(stream);
            text = streamReader.ReadToEnd();
        }

        string? filePath = files[0].TryGetLocalPath();
        await Scripting.RunScript(WithLineDirective(text, filePath), filePath);

        CommandTextBoxText = string.Format(LocalizationSource.GetString("Msg_ScriptFinished"), Path.GetFileName(filePath) ?? "Script");
    }

    /// <summary>
    /// Runs a built-in script from the shared <c>Scripts</c> folder, mirroring the WPF tool's
    /// <c>MenuItem_RunBuiltinScript_Item_Click</c>.
    /// </summary>
    public async void ScriptsRunBuiltinScript(string path)
    {
        if (!File.Exists(path))
            path = Paths.TryJoinVerifyWithinDirectory(BuiltInScripts.GetRootDirectory(), path) ?? "";

        if (!File.Exists(path))
        {
            await View!.MessageDialog(LocalizationSource.GetString("Msg_ScriptFileNotExist"),
                title: LocalizationSource.GetString("Common_Error"));
            return;
        }

        string text;
        using (StreamReader streamReader = new(path))
        {
            text = streamReader.ReadToEnd();
        }

        await Scripting.RunScript(WithLineDirective(text, path), path);

        CommandTextBoxText = string.Format(LocalizationSource.GetString("Msg_ScriptFinished"), Path.GetFileName(path));
    }

    /// <summary>
    /// Prefixes a #line directive like the WPF version does ("#line 1 &lt;path&gt;"), so compiler
    /// diagnostics carry the real file path and line numbers instead of pointing at the synthetic
    /// script submission.
    /// </summary>
    static string WithLineDirective(string text, string? filePath)
        => filePath is not null ? $"#line 1 \"{filePath}\"\n" + text : text;

    void ClearProject()
    {
        Project = null;

        if (View is MainView mainView)
            mainView.CloseProjectAssets();
    }

    /// <summary>Assigns a new project context, replacing (and unloading) any currently open project, mirroring the WPF version's AssignNewProject.</summary>
    void SetProject(ProjectContext projectContext)
    {
        ClearProject();

        Project = projectContext;
        Project.UnexportedAssetsChanged += (s, e) =>
        {
            UpdateSelectedTabProperties();
        };

        UpdateSelectedTabProperties();
    }

    /// <summary>Asks the user to choose the destination data file for a project, checking that it's not in the same
    /// directory as the source data file and warning about empty directories. Returns null if cancelled.</summary>
    async Task<string?> AskProjectDestinationDataFile(string sourceDataPath)
    {
        // Destination data file
        IStorageFile? destinationDataFile = await View!.SaveFileDialog(new()
        {
            Title = LocalizationSource.GetString("Msg_ChooseDestinationDataFile"),
            FileTypeChoices = FilePickerFileTypes.Data,
        });
        string? destinationDataPath = destinationDataFile?.TryGetLocalPath();

        if (destinationDataPath is null)
            return null;

        // Check if the directories are the same and warn if so (note: not a fully exhaustive check, but decent)
        try
        {
            if (sourceDataPath is not null && Path.GetDirectoryName(sourceDataPath) is string sourceDirectory
                && Path.GetDirectoryName(destinationDataPath) is string destinationDirectory
                && Path.GetFullPath(destinationDirectory).Equals(Path.GetFullPath(sourceDirectory), StringComparison.OrdinalIgnoreCase))
            {
                MessageWindow.Result result = await View!.MessageDialog(LocalizationSource.GetString("Msg_SameDirectoryWarning"),
                    title: LocalizationSource.GetString("Msg_SameDirectoryWarningTitle"),
                    buttons: MessageWindow.Buttons.YesNoCancel);
                if (result != MessageWindow.Result.Yes)
                {
                    // Abort
                    return null;
                }
            }
        }
        catch (Exception)
        {
            // Ignore filesystem errors on the above check; we don't really care
        }

        // Check if the save directory is empty, and warn if so
        try
        {
            if (!Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(destinationDataPath)).Any())
            {
                await View!.MessageDialog(LocalizationSource.GetString("Msg_EmptyDirectoryWarning"));
            }
        }
        catch (Exception)
        {
            // Ignore filesystem errors on the above check; we don't really care
        }

        return destinationDataPath;
    }

    public async void ProjectNew()
    {
        if (!await AskProjectSave(LocalizationSource.GetString("Msg_SaveProjectBeforeCreating")))
            return;

        // If necessary, ask for a source data file
        if (Data is null || DataPath is null)
        {
            IReadOnlyList<IStorageFile> sourceFiles = await View!.OpenFileDialog(new FilePickerOpenOptions()
            {
                Title = LocalizationSource.GetString("Msg_ChooseSourceDataFile"),
                FileTypeFilter = FilePickerFileTypes.Data,
            });
            if (sourceFiles.Count != 1)
            {
                CommandTextBoxText = LocalizationSource.GetString("Msg_CancelledNewProject");
                return;
            }

            using Stream stream = await sourceFiles[0].OpenReadAsync();
            if (!await LoadData(stream))
            {
                CommandTextBoxText = LocalizationSource.GetString("Msg_CancelledNewProject");
                return;
            }
            DataPath = sourceFiles[0].TryGetLocalPath();
            lastDataLocation = await sourceFiles[0].GetParentAsync();

            // Upon load failure, exit
            if (Data is null || DataPath is null)
            {
                CommandTextBoxText = LocalizationSource.GetString("Msg_CancelledNewProject");
                return;
            }
        }

        // Project name
        string? projectName = await View!.TextBoxDialog(
            LocalizationSource.GetString("Msg_ChooseProjectName"),
            $"{Data.GeneralInfo?.DisplayName?.Content ?? LocalizationSource.GetString("Msg_NewMod")} Mod",
            title: LocalizationSource.GetString("Msg_ChooseNewProjectName"));
        if (projectName is null)
        {
            CommandTextBoxText = LocalizationSource.GetString("Msg_CancelledNewProject");
            return;
        }
        projectName = projectName.Trim();

        // Project folder
        IReadOnlyList<IStorageFolder> projectFolderList = await View!.OpenFolderDialog(new() { Title = LocalizationSource.GetString("Msg_SelectProjectFolder") });
        string? projectFolderPath = projectFolderList.ElementAtOrDefault(0)?.TryGetLocalPath();
        if (projectFolderPath is null)
        {
            CommandTextBoxText = LocalizationSource.GetString("Msg_CancelledNewProject");
            return;
        }

        string projectFilePath = Path.Join(projectFolderPath, "project.json");

        // Destination data file
        string? destinationDataPath = await AskProjectDestinationDataFile(DataPath!);
        if (destinationDataPath is null)
        {
            CommandTextBoxText = LocalizationSource.GetString("Msg_CancelledNewProject");
            return;
        }

        // Attempt creating project at the specified location (will fail if the folder isn't empty, etc.)
        ProjectContext projectContext;
        try
        {
            projectContext = new(Data, DataPath, destinationDataPath, projectFilePath, projectName, Dispatcher.UIThread.Invoke);
        }
        catch (ProjectException e)
        {
            await View!.MessageDialog(string.Format(LocalizationSource.GetString("Msg_FailedCreateProject"), e.Message));
            CommandTextBoxText = LocalizationSource.GetString("Msg_ProjectCreationFailed");
            return;
        }
        catch (Exception e)
        {
            await View!.MessageDialog(LocalizationSource.GetString("Msg_ErrorCreateProject") + "\n" + e);
            CommandTextBoxText = LocalizationSource.GetString("Msg_ProjectCreationFailed");
            return;
        }

        // Start using new project context
        DataPath = destinationDataPath;
        SetProject(projectContext);
        CommandTextBoxText = string.Format(LocalizationSource.GetString("Msg_ProjectCreated"), projectName);
    }

    public async void ProjectOpen()
    {
        if (!await AskProjectSave(LocalizationSource.GetString("Msg_SaveProjectBeforeOpening")))
            return;

        // Choose project file to open
        IReadOnlyList<IStorageFile> projectFileList = await View!.OpenFileDialog(new()
        {
            Title = LocalizationSource.GetString("Msg_OpenProjectFile"),
            FileTypeFilter = FilePickerFileTypes.JSON,
        });
        string? projectFilePath = projectFileList.ElementAtOrDefault(0)?.TryGetLocalPath();
        if (projectFilePath is null)
            return;

        // If necessary, ask for a source data file
        IStorageFile? sourceDataFile = null;
        if (Data is null || DataPath is null)
        {
            IReadOnlyList<IStorageFile> sourceFileList = await View!.OpenFileDialog(new FilePickerOpenOptions()
            {
                Title = LocalizationSource.GetString("Msg_ChooseSourceDataFile"),
                FileTypeFilter = FilePickerFileTypes.Data,
            });
            if (sourceFileList.Count != 1)
                return;
            sourceDataFile = sourceFileList[0];
        }

        // Destination data file
        string? destinationDataPath = await AskProjectDestinationDataFile(sourceDataFile?.TryGetLocalPath() ?? DataPath!);
        if (destinationDataPath is null)
            return;

        // Load data file if needed
        if (sourceDataFile is not null)
        {
            using Stream stream = await sourceDataFile.OpenReadAsync();
            if (!await LoadData(stream))
                return;
            DataPath = sourceDataFile.TryGetLocalPath();
            lastDataLocation = await sourceDataFile.GetParentAsync();

            // Upon load failure, exit
            if (Data is null || DataPath is null)
                return;
        }

        // Change main data file path to the save data file path (the project's destination data file)
        string loadDataPath = DataPath!;
        DataPath = destinationDataPath;

        // Attempt loading project from the specific JSON, running the potentially long import on a background thread
        ProjectContext projectContext;
        IsEnabled = false;
        try
        {
            projectContext = await Task.Run(() =>
            {
                ProjectContext created = ProjectContext.CreateWithDataFilePaths(loadDataPath, destinationDataPath, projectFilePath);
                created.Import(Data, Settings!.EnableProjectBackup ? null : new GameFileNoOpBackup(), Dispatcher.UIThread.Invoke);
                return created;
            });
        }
        catch (ProjectException e)
        {
            await View!.MessageDialog(string.Format(LocalizationSource.GetString("Msg_FailedLoadProject"), e.Message));
            CommandTextBoxText = LocalizationSource.GetString("Msg_ProjectFailedToOpen");
            return;
        }
        catch (Exception e)
        {
            await View!.MessageDialog(LocalizationSource.GetString("Msg_ErrorLoadProject") + "\n" + e);
            CommandTextBoxText = LocalizationSource.GetString("Msg_ProjectFailedToOpen");
            return;
        }
        finally
        {
            IsEnabled = true;
        }

        // Start using new project context
        SetProject(projectContext);
        CommandTextBoxText = string.Format(LocalizationSource.GetString("Msg_OpenedProject"), projectContext.Name);
    }

    public async void ProjectSave()
    {
        await ProjectSaveTask();
    }

    public async Task<bool> ProjectSaveTask()
    {
        if (Project is null || Data is null || DataPath is null)
            return false;

        // Attempt saving project on a background thread
        IsEnabled = false;
        bool success = false;
        try
        {
            await Task.Run(() => Project.Export(true));
            success = true;
        }
        catch (ProjectException e)
        {
            await View!.MessageDialog(string.Format(LocalizationSource.GetString("Msg_FailedSaveProject"), e.Message));
        }
        catch (Exception e)
        {
            await View!.MessageDialog(LocalizationSource.GetString("Msg_ErrorSaveProject") + "\n" + e);
        }
        finally
        {
            IsEnabled = true;
        }

        CommandTextBoxText = success ? LocalizationSource.GetString("Msg_SavedProjectSuccessfully") : LocalizationSource.GetString("Msg_ProjectFailedToSave");
        return success;
    }

    public async void ProjectReload()
    {
        if (Project is null)
            return;

        if (Project.LoadDataPath is null || Project.SaveDataPath is null || Project.MainFilePath is null)
            return;

        string sourceDataPath = Project.LoadDataPath;
        string destinationDataPath = Project.SaveDataPath;
        string projectFilePath = Project.MainFilePath;

        if (!await AskProjectSave(LocalizationSource.GetString("Msg_SaveProjectBeforeReloading")))
            return;

        // Attempt loading project from the specific JSON, running the potentially long import on a background thread
        ProjectContext projectContext;
        IsEnabled = false;
        try
        {
            projectContext = await Task.Run(() =>
            {
                ProjectContext created = ProjectContext.CreateWithDataFilePaths(sourceDataPath, destinationDataPath, projectFilePath);
                created.Import(Data, Settings!.EnableProjectBackup ? null : new GameFileNoOpBackup(), Dispatcher.UIThread.Invoke);
                return created;
            });
        }
        catch (ProjectException e)
        {
            await View!.MessageDialog(string.Format(LocalizationSource.GetString("Msg_FailedLoadProject"), e.Message));
            return;
        }
        catch (Exception e)
        {
            await View!.MessageDialog(LocalizationSource.GetString("Msg_ErrorLoadProject") + "\n" + e);
            return;
        }
        finally
        {
            IsEnabled = true;
        }

        DataPath = destinationDataPath;
        SetProject(projectContext);
    }

    public async void ProjectViewUnexportedAssets()
    {
        if (Project is null || Data is null || DataPath is null)
            return;

        if (View is MainView mainView)
            mainView.OpenProjectAssets(ServiceProvider);
    }

    public async void ProjectClose()
    {
        if (!await AskProjectSave(LocalizationSource.GetString("Msg_SaveProjectBeforeClosing")))
            return;

        ClearProject();
        CommandTextBoxText = LocalizationSource.GetString("Msg_ProjectClosed");
    }

    public async void HelpGitHub()
    {
        await View!.LaunchUriAsync(new Uri("https://github.com/Genouka/UndertaleModTool"));
    }

    public async void HelpAbout()
    {
        // About Window
        await View!.MessageDialog(string.Format(LocalizationSource.GetString("Msg_AboutUndertaleModTool"), App.VersionString) +
            LocalizationSource.GetString("Msg_AboutBody1") +
            "\nhttps://github.com/Genouka/UndertaleModTool" +
            "\n" + LocalizationSource.GetString("Msg_AboutBody2") +
            "\n" +
            "\n" + string.Format(LocalizationSource.GetString("Msg_AboutBody3"), App.InformationalVersionString)
            ,
            title: LocalizationSource.GetString("Msg_AboutTitle"));
    }

    // Update checking (mirrors the update check of the WPF version of UndertaleModTool)
    bool updateInProgress = false;

    /// <summary>Set right before the app closes to install an update; checked by <see cref="MainWindow.OnClosing"/>.</summary>
    public bool IsUpdating { get; private set; } = false;

    /// <summary>
    /// Automatically checks for a new nightly build on startup (if enabled in settings) and
    /// prompts the user to update when one is available.
    /// </summary>
    public async void CheckForUpdatesAutomatically()
    {
        if (Settings?.CheckForUpdates != true)
            return;
        if (!UpdateChecker.IsSupportedPlatform)
            return;

        try
        {
            using HttpClient client = UpdateChecker.CreateHttpClient();
            UpdateChecker.UpdateInfo? info = await UpdateChecker.FetchLatestBuildAsync(client);
            if (info is null || !UpdateChecker.IsNewerThanLocal(info))
                return;

            if (await View!.MessageDialog(LocalizationSource.GetString("Msg_UpdateAvailable"),
                    buttons: MessageWindow.Buttons.YesNo) == MessageWindow.Result.Yes)
            {
                await UpdateAppAsync(info);
            }
        }
        catch
        {
            // Silently ignore any errors - this is just a convenience check
        }
    }

    /// <summary>Manual "Check for updates" command from the Help menu.</summary>
    public async void HelpCheckForUpdates()
    {
        if (!UpdateChecker.IsSupportedPlatform)
        {
            await View!.MessageDialog(LocalizationSource.GetString("Msg_UpdateNotSupported"));
            return;
        }

        try
        {
            using HttpClient client = UpdateChecker.CreateHttpClient();
            UpdateChecker.UpdateInfo? info = await UpdateChecker.FetchLatestBuildAsync(client);
            if (info is null)
            {
                await View!.MessageDialog(string.Format(LocalizationSource.GetString("Msg_FailedToFindBuild"), UpdateChecker.WorkflowName));
                return;
            }

            if (!UpdateChecker.IsNewerThanLocal(info))
            {
                await View!.MessageDialog(LocalizationSource.GetString("Msg_UpToDate"));
                return;
            }

            if (await View!.MessageDialog(LocalizationSource.GetString("Msg_UpdateAvailable"),
                    buttons: MessageWindow.Buttons.YesNo) == MessageWindow.Result.Yes)
            {
                await UpdateAppAsync(info);
            }
        }
        catch (Exception e)
        {
            await View!.MessageDialog(string.Format(LocalizationSource.GetString("Msg_FailedToFetchBuild"), e.Message));
        }
    }

    /// <summary>"Update app" command from the settings window: always downloads the latest build,
    /// asking for confirmation first when the app is already up to date.</summary>
    public async void HelpUpdateApp()
    {
        if (!UpdateChecker.IsSupportedPlatform)
            return;

        try
        {
            using HttpClient client = UpdateChecker.CreateHttpClient();
            UpdateChecker.UpdateInfo? info = await UpdateChecker.FetchLatestBuildAsync(client);
            if (info is null)
            {
                await View!.MessageDialog(string.Format(LocalizationSource.GetString("Msg_FailedToFindBuild"), UpdateChecker.WorkflowName));
                return;
            }

            if (!UpdateChecker.IsNewerThanLocal(info))
            {
                if (await View!.MessageDialog(LocalizationSource.GetString("Msg_AlreadyUpToDate"),
                        buttons: MessageWindow.Buttons.YesNo) != MessageWindow.Result.Yes)
                {
                    return;
                }
            }

            await UpdateAppAsync(info);
        }
        catch (Exception e)
        {
            await View!.MessageDialog(string.Format(LocalizationSource.GetString("Msg_FailedToFetchBuild"), e.Message));
        }
    }

    /// <summary>Downloads and installs the given nightly build, then restarts the app.</summary>
    async Task UpdateAppAsync(UpdateChecker.UpdateInfo info)
    {
        if (updateInProgress)
            return;
        updateInProgress = true;
        ILoaderWindow? loader = null;
        try
        {
            // Android builds can't replace their own files - they hand the update APK
            // to the system package installer instead of running a self-updater.
            if (OperatingSystem.IsAndroid())
            {
                await UpdateAppAndroidAsync(info);
                return;
            }

            // Prepare the temp folder the updater will work from.
            string tempFolder = Path.Join(Path.GetTempPath(), "UndertaleModToolAvalonia");
            Directory.CreateDirectory(tempFolder);

            // Check that there is enough free space on the system drive.
            string sysDriveLetter = Path.GetPathRoot(Path.GetTempPath()) ?? "C:";
            try
            {
                if ((new DriveInfo(sysDriveLetter).AvailableFreeSpace / (1024.0 * 1024.0)) < 500)
                {
                    await View!.MessageDialog(string.Format(LocalizationSource.GetString("Msg_NotEnoughSpace"), sysDriveLetter));
                    return;
                }
            }
            catch
            {
                // DriveInfo can fail on some platforms; don't block the update over it.
            }

            // Download the update, showing progress in a loader window.
            loader = View!.LoaderOpen();
            loader.SetMessage(LocalizationSource.GetString("Main_Downloading"));
            loader.SetMaximum(1000);

            string downloadOutput = Path.Join(tempFolder, "Update.zip.zip");

            using (HttpClient client = new() { Timeout = TimeSpan.FromMinutes(5) })
            {
                bool downloaded = await DownloadUpdateAsync(client, info, downloadOutput, loader);
                if (!downloaded)
                {
                    await View!.MessageDialog(string.Format(LocalizationSource.GetString("Msg_FailedToDownload"),
                        LocalizationSource.GetString("Msg_CheckInternetConnection")));
                    return;
                }
            }

            // Extract the update (the downloaded file can be single or double zipped).
            loader.SetStatus(LocalizationSource.GetString("Msg_ExtractingUpdate"));
            string updateFolder = Path.Join(tempFolder, "Update");
            if (Directory.Exists(updateFolder))
                Directory.Delete(updateFolder, true);
            await Task.Run(() => ExtractUpdateZip(downloadOutput, updateFolder));

            // Copy the running executable to the temp folder - it will act as the updater
            // (it can replace the app files once this instance has exited).
            if (Environment.ProcessPath is null)
                throw new InvalidOperationException("Can't determine the app executable path.");
            string appPath = Path.GetDirectoryName(Environment.ProcessPath)!;
            string updaterFolderTemp = Path.Join(tempFolder, "Updater");
            if (Directory.Exists(updaterFolderTemp))
                Directory.Delete(updaterFolderTemp, true);
            Directory.CreateDirectory(updaterFolderTemp);
            string updaterName = OperatingSystem.IsWindows() ? "UndertaleModToolAvaloniaUpdater.exe" : "UndertaleModToolAvaloniaUpdater";
            string updaterExe = Path.Join(updaterFolderTemp, updaterName);
            File.Copy(Environment.ProcessPath, updaterExe);
            File.WriteAllText(Path.Join(updaterFolderTemp, "actualAppFolder"), appPath);

            // Close the loader, inform the user, launch the updater and exit.
            loader.Close();
            loader = null;

            await View!.MessageDialog(LocalizationSource.GetString("Msg_WillCloseToUpdate"));

            Process.Start(new ProcessStartInfo(updaterExe)
            {
                WorkingDirectory = updaterFolderTemp,
                Arguments = $"--update-install {Environment.ProcessId}",
            });

            IsUpdating = true;

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
            else
                Environment.Exit(0);
        }
        catch (Exception e)
        {
            loader?.Close();
            string errMsg = e.InnerException?.Message ?? e.Message;
            await View!.MessageDialog(string.Format(LocalizationSource.GetString("Msg_FailedToDownload"), errMsg));
        }
        finally
        {
            updateInProgress = false;
        }
    }

    /// <summary>
    /// Android update flow: the app can't replace its own installed files, so download the build
    /// zip, extract the update APK and hand it to the system package installer (which replaces the
    /// app once the user confirms). This app instance keeps running in case the user cancels.
    /// </summary>
    async Task UpdateAppAndroidAsync(UpdateChecker.UpdateInfo info)
    {
        if (PlatformUpdateInstaller.InstallPackageAsync is null)
            return;

        ILoaderWindow? loader = View!.LoaderOpen();
        try
        {
            loader.SetMessage(LocalizationSource.GetString("Main_Downloading"));
            loader.SetMaximum(1000);

            // Download the update, showing progress in a loader window.
            string tempFolder = Path.Join(Path.GetTempPath(), "UndertaleModToolAvalonia");
            Directory.CreateDirectory(tempFolder);
            string downloadOutput = Path.Join(tempFolder, "Update.zip.zip");

            using (HttpClient client = new() { Timeout = TimeSpan.FromMinutes(5) })
            {
                if (!await DownloadUpdateAsync(client, info, downloadOutput, loader))
                {
                    await View!.MessageDialog(string.Format(LocalizationSource.GetString("Msg_FailedToDownload"),
                        LocalizationSource.GetString("Msg_CheckInternetConnection")));
                    return;
                }
            }

            // Extract the downloaded zip (single or double zipped) and locate the update APK.
            loader.SetStatus(LocalizationSource.GetString("Msg_ExtractingUpdate"));
            string updateFolder = Path.Join(tempFolder, "Update");
            if (Directory.Exists(updateFolder))
                Directory.Delete(updateFolder, true);
            await Task.Run(() => ExtractUpdateZip(downloadOutput, updateFolder));

            string? apkPath = Directory.EnumerateFiles(updateFolder, "*.apk", SearchOption.AllDirectories).FirstOrDefault();
            if (apkPath is null)
            {
                // Older releases shipped without an APK inside the zip - fall back to the releases page.
                await View!.MessageDialog(LocalizationSource.GetString("Msg_UpdatePackageMissing"));
                await View!.LaunchUriAsync(new Uri(info.ReleasePageUrl));
                return;
            }

            loader.Close();
            loader = null;

            if (!await PlatformUpdateInstaller.InstallPackageAsync(apkPath))
            {
                // The platform side has opened the system settings to grant the permission first.
                await View!.MessageDialog(LocalizationSource.GetString("Msg_AndroidInstallPermission"));
                return;
            }
        }
        finally
        {
            loader?.Close();
        }
    }

    /// <summary>Downloads the update, trying the GitHub release asset first and nightly.link as a fallback.</summary>
    static async Task<bool> DownloadUpdateAsync(HttpClient client, UpdateChecker.UpdateInfo info, string downloadOutput, ILoaderWindow loader)
    {
        double bytesToMB = 1024 * 1024;
        string[] urls = [info.ReleaseDownloadUrl, info.NightlyLinkDownloadUrl];

        foreach (string url in urls)
        {
            try
            {
                using HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                    continue;

                long totalBytes = response.Content.Headers.ContentLength ?? 0;
                long bytesToUpdateProgress = Math.Max(1, totalBytes / 500);
                long bytesToProgressCounter = 0;

                using Stream contentStream = await response.Content.ReadAsStreamAsync();
                using FileStream fs = new(downloadOutput, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
                byte[] buffer = new byte[8192];
                long totalBytesDownloaded = 0;
                int bytesRead = await contentStream.ReadAsync(buffer);
                while (bytesRead > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, bytesRead));
                    totalBytesDownloaded += bytesRead;
                    bytesToProgressCounter += bytesRead;
                    if (bytesToProgressCounter >= bytesToUpdateProgress)
                    {
                        bytesToProgressCounter -= bytesToUpdateProgress;
                        long downloaded = totalBytesDownloaded;
                        string status = string.Format(LocalizationSource.GetString("Msg_DownloadedMB"),
                            (totalBytesDownloaded / bytesToMB).ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                        Dispatcher.UIThread.Post(() =>
                        {
                            loader.SetValue((int)Math.Min(1000, downloaded * 1000 / Math.Max(1, totalBytes)));
                            loader.SetStatus(status);
                        });
                    }
                    bytesRead = await contentStream.ReadAsync(buffer);
                }

                Dispatcher.UIThread.Post(() => loader.SetValue(1000));
                return true;
            }
            catch (Exception)
            {
                // Try the next download source.
            }
        }

        return false;
    }

    /// <summary>
    /// Extracts the downloaded zip into <paramref name="targetFolder"/>. GitHub Actions artifacts
    /// are re-zipped by upload-artifact (double zip), while release assets are already the inner zip.
    /// </summary>
    static void ExtractUpdateZip(string zipPath, string targetFolder)
    {
        string? innerZip = null;
        using (ZipArchive archive = ZipFile.OpenRead(zipPath))
        {
            List<string> topLevelZips = archive.Entries
                .Where(e => e.FullName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/'))
                .Select(e => e.FullName)
                .ToList();
            if (topLevelZips.Count == 1)
                innerZip = topLevelZips[0];
        }

        if (innerZip is not null)
        {
            string artifactFolder = Path.Join(Path.GetDirectoryName(zipPath)!, "Artifact");
            if (Directory.Exists(artifactFolder))
                Directory.Delete(artifactFolder, true);
            ZipFile.ExtractToDirectory(zipPath, artifactFolder, true);
            ZipFile.ExtractToDirectory(Path.Join(artifactFolder, innerZip), targetFolder, true);
        }
        else
        {
            ZipFile.ExtractToDirectory(zipPath, targetFolder, true);
        }
    }

    public async void DataItemAdd(IList list)
    {
        if (Data is null || list is null)
            return;

        UndertaleResource res = UndertaleModLibCompatibility.CreateResource(list);

        string? name = UndertaleModLibCompatibility.GetDefaultResourceName(list);
        if (name is not null)
        {
            name = await View!.TextBoxDialog(LocalizationSource.GetString("Msg_NewAssetName"), name);
            if (name is null)
                return;

            static bool IsValidAssetIdentifier(string name)
            {
                if (string.IsNullOrEmpty(name))
                    return false;

                char firstChar = name[0];
                if (!char.IsAsciiLetter(firstChar) && firstChar != '_')
                    return false;

                foreach (char c in name.Skip(1))
                    if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                        return false;

                return true;
            }

            if (!IsValidAssetIdentifier(name))
            {
                await View!.MessageDialog(string.Format(LocalizationSource.GetString("Msg_InvalidAssetName"), name));
                return;
            }
        }

        var newResources = Data.InitializeResource(res, list, name);

        if (res is UndertaleRoom room)
        {
            if (await View!.MessageDialog(LocalizationSource.GetString("Msg_AddRoomToEnd"), buttons: MessageWindow.Buttons.YesNo) == MessageWindow.Result.Yes)
                Data.GeneralInfo?.RoomOrder.Add(new(room));
        }

        list.Add(res);

        if (Project is not null && res is IProjectAsset { ProjectExportable: true } projectAsset)
        {
            Project.MarkAssetForExport(projectAsset);

            foreach (UndertaleResource newResource in newResources)
            {
                if (newResource is IProjectAsset { ProjectExportable: true } newProjectAsset)
                {
                    Project.MarkAssetForExport(newProjectAsset);
                }
            }
        }

        if (Settings!.OpenNewResourceAfterCreatingIt)
        {
            _ = TabOpen(res, inNewTab: true);
        }
    }

    public async void DataItemRemove(UndertaleResource resource)
    {
        if (Data is null)
            return;

        if (await View!.MessageDialog(string.Format(LocalizationSource.GetString("Msg_DeleteResource"), resource) + "\n" + LocalizationSource.GetString("Msg_DeleteResourceNote"),
                    buttons: MessageWindow.Buttons.YesNo) == MessageWindow.Result.Yes)
        {
            // TODO: Maybe do something about all references to this.
            Data[resource.GetType()].Remove(resource);

            if (Project is not null && resource is IProjectAsset projectAsset)
            {
                Project.UnmarkAssetForExport(projectAsset);
            }

            // TODO: Close tabs, remove histories
        }
    }

    public void OpenFindReferences(UndertaleResource? resource = null)
    {
        if (View is MainView mainView)
            mainView.OpenFindReferences(ServiceProvider, resource);
    }

    public async Task<TabItemViewModel?> TabOpen(object? item, bool inNewTab = false)
    {
        if (Data is null)
            return null;

        ITabContent? content = item switch
        {
            DescriptionViewModel vm => vm,
            "GeneralInfo" => new GeneralInfoViewModel(Data),
            "GlobalInitScripts" => new GlobalInitScriptsViewModel(Data.FORM.GLOB.List),
            "GameEndScripts" => new GameEndScriptsViewModel(Data.FORM.GMEN.List),
            UndertaleAudioGroup r => new UndertaleAudioGroupViewModel(r),
            UndertaleSound r => new UndertaleSoundViewModel(r, ServiceProvider),
            UndertaleSprite r => new UndertaleSpriteViewModel(r, ServiceProvider),
            UndertaleBackground r => new UndertaleBackgroundViewModel(r),
            UndertalePath r => new UndertalePathViewModel(r),
            UndertaleScript r => new UndertaleScriptViewModel(r),
            UndertaleShader r => new UndertaleShaderViewModel(r, ServiceProvider),
            UndertaleFont r => new UndertaleFontViewModel(r),
            UndertaleTimeline r => new UndertaleTimelineViewModel(r),
            UndertaleGameObject r => new UndertaleGameObjectViewModel(r, ServiceProvider),
            UndertaleRoom r => new UndertaleRoomViewModel(r, ServiceProvider),
            "Extensions" => new UndertaleExtensionChunkViewModel(Data.FORM.EXTN),
            UndertaleExtension r => new UndertaleExtensionViewModel(r, ServiceProvider),
            UndertaleTexturePageItem r => new UndertaleTexturePageItemViewModel(r, ServiceProvider),
            UndertaleCode r => new UndertaleCodeViewModel(r, ServiceProvider),
            UndertaleVariable r => new UndertaleVariableViewModel(r),
            UndertaleFunction r => new UndertaleFunctionViewModel(r),
            UndertaleCodeLocals r => new UndertaleCodeLocalsViewModel(r),
            UndertaleString r => new UndertaleStringViewModel(r),
            UndertaleEmbeddedTexture r => new UndertaleEmbeddedTextureViewModel(r, ServiceProvider),
            UndertaleEmbeddedAudio r => new UndertaleEmbeddedAudioViewModel(r, ServiceProvider),
            UndertaleTextureGroupInfo r => new UndertaleTextureGroupInfoViewModel(r),
            UndertaleEmbeddedImage r => new UndertaleEmbeddedImageViewModel(r),
            UndertaleAnimationCurve r => new UndertaleAnimationCurveViewModel(r),
            UndertaleParticleSystem r => new UndertaleParticleSystemViewModel(r),
            UndertaleParticleSystemEmitter r => new UndertaleParticleSystemEmitterViewModel(r),
            _ => null,
        };

        if (content is not null)
        {
            if (!inNewTab && TabSelected is not null)
            {
                if (!await TabGoTo(content))
                    return null;
                return TabSelected;
            }
            else
            {
                TabItemViewModel tab = new(content);
                Tabs.Add(tab);
                TabSelected = tab;
                tab.OnOpen();
                return tab;
            }
        }

        return null;
    }

    public async Task<bool> TabSaveAll()
    {
        bool savedAll = true;

        foreach (TabItemViewModel tab in Tabs)
        {
            if (!await tab.Save())
                savedAll = false;
        }

        return savedAll;
    }

    [RelayCommand]
    public async Task TabClose(TabItemViewModel tab)
    {
        if (!await tab.Save())
            return;

        tab.OnClose();

        TabItemViewModel? selected = TabSelected;
        int index = TabSelectedIndex;

        Tabs.Remove(tab);

        if (TabSelected != selected)
        {
            if (index >= Tabs.Count)
                index = Tabs.Count - 1;

            TabSelectedIndex = index;
        }
    }

    public async void TabCloseSelected()
    {
        if (TabSelected is not null)
            _ = TabClose(TabSelected);
    }

    public async Task TabCloseAll()
    {
        foreach (TabItemViewModel tab in Tabs.ToList())
        {
            await TabClose(tab);
        }
    }

    public void TabCloseAllWithoutSaving()
    {
        foreach (TabItemViewModel tab in Tabs.ToList())
        {
            tab.OnClose();
        }
        Tabs.Clear();
    }

    public void TabSetToPrevious()
    {
        if (TabSelectedIndex > 0)
            TabSelectedIndex--;
        else
            TabSelectedIndex = Tabs.Count - 1;
    }

    public void TabSetToNext()
    {
        if (TabSelectedIndex < Tabs.Count - 1)
            TabSelectedIndex++;
        else
            TabSelectedIndex = 0;
    }

    public async Task<bool> TabGoTo(ITabContent content)
    {
        if (TabSelected is not null)
            if (!await TabSelected.GoTo(content))
                return false;

        UpdateSelectedTabProperties();
        return true;
    }

    public async void TabGoBack()
    {
        if (TabSelected is not null)
            if (!await TabSelected.GoBack())
                return;

        UpdateSelectedTabProperties();
    }

    public async void TabGoForward()
    {
        if (TabSelected is not null)
            if (!await TabSelected.GoForward())
                return;

        UpdateSelectedTabProperties();
    }

    partial void OnTabSelectedChanged(TabItemViewModel? value)
    {
        UpdateSelectedTabProperties();
    }

    // Bottom bar
    void UpdateSelectedTabProperties()
    {
        if (Data is not null && TabSelected?.Content is IUndertaleResourceViewModel vm)
        {
            TabSelectedResourceIdString = Data.IndexOf(vm.Resource).ToString();

            if (Project is not null)
            {
                if (vm.Resource is IProjectAsset { ProjectExportable: true } projectAsset)
                {
                    TabIsMarkedForExport = Project.IsAssetMarkedForExport(projectAsset);
                    TabCanMarkedForExport = true;
                    return;
                }
            }
        }
        else
        {
            TabSelectedResourceIdString = "None";
        }

        TabIsMarkedForExport = false;
        TabCanMarkedForExport = false;
    }

    partial void OnTabIsMarkedForExportChanged(bool value)
    {
        if (Project is not null
            && TabSelected?.Content is IUndertaleResourceViewModel vm
            && vm.Resource is IProjectAsset { ProjectExportable: true } projectAsset)
        {
            if (TabIsMarkedForExport)
            {
                Project.MarkAssetForExport(projectAsset);
            }
            else
            {
                Project.UnmarkAssetForExport(projectAsset);
            }
        }
    }
}
