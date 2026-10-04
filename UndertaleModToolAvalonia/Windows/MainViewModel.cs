using System;
using System.Collections;
using System.Collections.Generic;
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
    public SettingsFile Settings { get; init; }

    // Scripting
    public Scripting Scripting = null!;

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

    Dictionary<int, UndertaleData> audioGroupDataList = [];

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
    public TabsViewModel Tabs { get; set; }

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

        Settings = LoadSettings();

        WindowState = Settings.StartMaximized ? WindowState.Maximized : WindowState.Normal;

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
        Tabs = new(this);

        _ = TabOpen(new DescriptionViewModel(
            LocalizationSource.GetString("Main_WelcomeHeading"),
            LocalizationSource.GetString("Main_WelcomeDescription")));
    }

    SettingsFile LoadSettings()
    {
        (SettingsFile settingsFile, Exception? ex) = SettingsFile.Load();

        if (ex is not null)
        {
            LazyErrorMessages.Add($"{LocalizationSource.GetString("Msg_ErrorSettingsLoading")}\n{ex.Message}\n{LocalizationSource.GetString("Msg_DefaultSettingsLoaded")}");
        }

        Exception? stylesEx = SettingsFile.LoadStyles();
        if (stylesEx is not null)
        {
            LazyErrorMessages.Add($"{LocalizationSource.GetString("Msg_ErrorStylesLoading")}\n{stylesEx.Message}");
        }

        return settingsFile;
    }

    public void Initialize()
    {
        Scripting = new(ServiceProvider);

        if (!string.IsNullOrEmpty(Settings.Language))
            LocalizationSource.Instance.CurrentCulture = new System.Globalization.CultureInfo(Settings.Language);
    }

    /// <summary>
    /// Guards the one-time startup work in <see cref="OnLoaded"/>. Single-window platforms
    /// (Android) move the main view in and out of the visual tree every time a modal dialog opens
    /// or closes, which re-fires Avalonia's <c>Loaded</c> event and would otherwise re-run the
    /// startup checks (e.g. the automatic update check) on every dialog dismissal.
    /// </summary>
    bool onLoadedRan = false;

    public async void OnLoaded()
    {
        if (onLoadedRan)
            return;
        onLoadedRan = true;

        foreach (string message in LazyErrorMessages)
        {
            await View!.MessageDialog(message);
        }
        LazyErrorMessages.Clear();

        // Storage access first: on Android 10 the answer decides whether the user has to be walked
        // through the preinstall APK flow, and asking before the update check keeps the two dialogs
        // from stacking on top of each other.
        await CheckStorageAccessAsync();

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
            if (Data.GeneralInfo is not null)
                Data.GeneralInfo.PropertyChanged += DataGeneralInfoChangedHandler;

            Data.ToolInfo.InstanceIdPrefix = () => Settings.InstanceIdPrefix;
            Data.ToolInfo.DecompilerSettings = Settings.DecompileSettings;
        }

        UpdateVersion();

        DataExplorer.UpdateFromData();

        if (Data is not null)
        {
            DataExplorer.OnExpandItemOnTree?.Invoke(DataExplorer.TreeDataGridData[0]);
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

    public UndertaleData? GetAudioGroupData(int audioGroupId)
    {
        if (audioGroupDataList.TryGetValue(audioGroupId, out UndertaleData? value))
        {
            return value;
        }

        return LoadAudioGroupData(audioGroupId);
    }

    public UndertaleData? LoadAudioGroupData(int audioGroupId)
    {
        if (Data is null)
            return null;

        if (audioGroupId >= Data.AudioGroups.Count)
            return null;

        UndertaleAudioGroup audioGroup = Data.AudioGroups[audioGroupId];

        string relativePath = audioGroup.Path?.Content ?? $"audiogroup{audioGroupId}.dat";

        string path = Paths.JoinVerifyWithinDirectory(Path.GetDirectoryName(DataPath), relativePath);

        if (File.Exists(path))
        {
            try
            {
                using FileStream stream = File.OpenRead(path);

                UndertaleData audioGroupData = UndertaleIO.Read(stream,
                    (string warning, bool isImportant) =>
                    {
                        //warnings.Add(warning);
                        if (isImportant)
                        {
                            //hadImportantWarnings = true;
                        }
                    },
                    (string message) =>
                    {
                        //Dispatcher.UIThread.Post(() => w.SetText($"Opening data file... {message}"));
                    }
                );

                audioGroupDataList[audioGroupId] = audioGroupData;
                return audioGroupData;
            }
            catch (Exception e)
            {
                return null;
            }
        }

        return null;
    }

    public void UnloadAudioGroupData(int audioGroupId)
    {
        audioGroupDataList.Remove(audioGroupId);
    }

    public async Task<bool> SaveData(Stream stream)
    {
        IsEnabled = false;

        ILoaderWindow w = View!.LoaderOpen();
        w.SetText(LocalizationSource.GetString("Msg_SavingDataFile"));

        try
        {
            // Recompile all code sources before saving, if requested and a project is open (mirrors the WPF version)
            if (Settings.RecompileAllCodeSourcesOnProjectSave && Project is not null)
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
            await View!.MessageDialog(string.Format(LocalizationSource.GetString("Msg_RecompileError"), e.Message));
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

        Tabs.TabCloseAllWithoutSaving();

        ClearProject();

        Data = null;
        DataPath = null;

        audioGroupDataList.Clear();
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

        if (!await Tabs.TabSaveAll())
            return false;

        if (Project is not null)
        {
            bool saveInProjectDestination = true;

            if (!Settings.AlwaysSaveDataInProjectDestination)
            {
                var result = await View!.MessageDialog(LocalizationSource.GetString("Msg_SaveToProjectDataFileQuestion"), buttons: MessageWindow.Buttons.YesNoCancel);
                if (result == MessageWindow.Result.Yes)
                {
                    saveInProjectDestination = true;
                }
                else if (result == MessageWindow.Result.No)
                {
                    // If pressed No, continue saving as if there's no project.
                    saveInProjectDestination = false;
                }
                else
                {
                    return false;
                }
            }

            if (saveInProjectDestination)
            {
                if (!await SaveDataToFilePath(Project.SaveDataPath, useTempFile: false))
                {
                    return false;
                }
                DataPath = Project.SaveDataPath;
                return true;
            }
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

            if (await SaveDataToFilePath(path, useTempFile: true))
            {
                DataPath = path;
                lastDataLocation = await file.GetParentAsync();
                return true;
            }
        }
        catch (IOException ex)
        {
            await View!.MessageDialog(LocalizationSource.GetString("Msg_ErrorSavingDataFile") + "\n" + ex.Message);
        }

        return false;
    }

    async Task<bool> SaveDataToFilePath(string filePath, bool useTempFile = true)
    {
        bool writeFileCreated = false;

        string tempPath = filePath + "temp";
        string writeFilePath = useTempFile ? tempPath : filePath;
        var writeFileMode = useTempFile ? FileMode.CreateNew : FileMode.Create;

        try
        {
            using (FileStream stream = File.Open(writeFilePath, writeFileMode, FileAccess.Write))
            {
                writeFileCreated = true;
                await SaveData(stream);

                if (useTempFile)
                {
                    stream.Flush(flushToDisk: true);
                }
            }

            if (useTempFile)
            {
                File.Move(tempPath, filePath, overwrite: true);
            }
        }
        catch (IOException ex)
        {
            // Delete file only if it was created right now, not if it was pre-existing.
            if (writeFileCreated)
            {
                File.Delete(tempPath);
            }
            await View!.MessageDialog(LocalizationSource.GetString("Msg_ErrorSavingDataFile") + "\n" + ex.Message);
            return false;
        }

        return true;
    }

    public async void FileClose()
    {
        if (!await AskProjectSave(LocalizationSource.GetString("Msg_SaveProjectBeforeClosing")))
            return;
        if (!await AskFileSave(LocalizationSource.GetString("Msg_SaveDataFileBeforeClosing")))
            return;

        CloseData();
    }

    public async void FileTempRun()
    {
        // TODO: Ideally, if the project system is being used, this would actually not use a temp file, but instead just save it to the destination file.
        if (Data is null)
            return;

        string? runnerName = Data.GeneralInfo?.FileName?.Content;
        if (runnerName is null)
        {
            await View!.MessageDialog(LocalizationSource.GetString("Msg_FileNameNotSet"));
            return;
        }

        if (DataPath is null)
            return;

        // Save to temp

        string tempFileName = Path.GetTempFileName();

        if (!await SaveDataToFilePath(tempFileName, useTempFile: false))
        {
            return;
        }

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
            await View!.MessageDialog($"{LocalizationSource.GetString("Msg_InvalidRunner")} ({runnerPath})");
            return;
        }

        StartRunnerProcess(runnerPath, dataPath: tempFileName, workingDirectory: Path.GetDirectoryName(DataPath));
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
            await View!.MessageDialog($"{LocalizationSource.GetString("Msg_InvalidRunner")} ({runnerPath})");
            return;
        }

        StartRunnerProcess(runnerPath);
    }

    public async void FileRunWithOther()
    {
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

    void StartRunnerProcess(string runnerPath, string? dataPath = null, string? workingDirectory = null)
    {
        dataPath ??= DataPath;
        workingDirectory ??= Path.GetDirectoryName(dataPath);

        // "launcher" allows game_change data files to still access files above the data path.
        Process.Start(new ProcessStartInfo(runnerPath, $"-game \"{dataPath}\" launcher") { WorkingDirectory = workingDirectory });
    }

    public void FileClearAudioGroupCache()
    {
        audioGroupDataList.Clear();
        GC.Collect();
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

    public async void ScriptsRunScript(string filePath)
    {
        string text = File.ReadAllText(filePath);

        await Scripting.RunScript(WithLineDirective(text, filePath), filePath);

        CommandTextBoxText = string.Format(LocalizationSource.GetString("Msg_ScriptFinished"), Path.GetFileName(filePath));
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
                created.Import(Data, Settings.EnableProjectBackup ? null : new GameFileNoOpBackup(), Dispatcher.UIThread.Invoke);
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
                created.Import(Data, Settings.EnableProjectBackup ? null : new GameFileNoOpBackup(), Dispatcher.UIThread.Invoke);
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

    /// <summary>
    /// Makes sure the app can read and write shared external storage by path - the whole app is
    /// path-based, so a denial is not a cosmetic problem - and explains the Android 10 case where
    /// the runtime permission alone cannot fix it.
    /// <para>
    /// Android 10 is special: this app targets a much newer API level than 29, so Android 10 runs it
    /// under scoped storage, where <c>WRITE_EXTERNAL_STORAGE</c> no longer grants the path-based
    /// access everything here relies on. The documented way out is the legacy storage model, which
    /// Android 10 only carries over to an app that is <i>updated</i> from a build that already had
    /// it - the tiny targetSdk 29 "storage setup" (preinstall) APK published next to every release.
    /// So instead of a permission dialog that can never help, the user gets the download link and
    /// the exact order of installs.
    /// </para>
    /// </summary>
    public async Task CheckStorageAccessAsync()
    {
        if (PlatformStorageAccess.EnsureAccessAsync is not { } ensureAccess)
            return; // Desktop, or any other platform where this is not a thing.

        StorageAccessResult result;
        try
        {
            result = await ensureAccess();
        }
        catch
        {
            // Same reasoning as the update check below: a failure here must never break startup.
            return;
        }

        if (result != StorageAccessResult.NeedsPreinstallSetup)
            return;

        string url = PlatformStorageAccess.GetPreinstallSetupUrl?.Invoke() ?? UpdateChecker.ReleasesPageUrl;
        if (await View!.MessageDialog(
                string.Format(LocalizationSource.GetString("Msg_Android10StorageSetup"), url),
                title: LocalizationSource.GetString("Msg_Android10StorageSetupTitle"),
                buttons: MessageWindow.Buttons.YesNo) == MessageWindow.Result.Yes)
        {
            await View!.LaunchUriAsync(new Uri(url));
        }
    }

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

            // Reuse a download of this exact build that is already cached (for example from an
            // earlier attempt whose install never completed) instead of downloading it again.
            if (!HasCachedUpdateDownload(tempFolder, info))
            {
                // Drop the stale marker first: the file is about to be rewritten, and a partial
                // download must never be mistaken for a complete one on a later check.
                ClearCachedUpdateMarker(tempFolder);

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

                // Remember which build the cached file belongs to.
                MarkCachedUpdateComplete(tempFolder, info);
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

            // The app deliberately keeps running when the user cancels the system package
            // installer, so a build that was already downloaded stays in the app cache. Reuse it
            // instead of downloading the same build again.
            string tempFolder = Path.Join(Path.GetTempPath(), "UndertaleModToolAvalonia");
            Directory.CreateDirectory(tempFolder);
            string downloadOutput = Path.Join(tempFolder, "Update.zip.zip");

            if (!HasCachedUpdateDownload(tempFolder, info))
            {
                // Drop the stale marker first: the file is about to be rewritten, and a partial
                // download must never be mistaken for a complete one on a later check.
                ClearCachedUpdateMarker(tempFolder);

                using (HttpClient client = new() { Timeout = TimeSpan.FromMinutes(5) })
                {
                    if (!await DownloadUpdateAsync(client, info, downloadOutput, loader))
                    {
                        await View!.MessageDialog(string.Format(LocalizationSource.GetString("Msg_FailedToDownload"),
                            LocalizationSource.GetString("Msg_CheckInternetConnection")));
                        return;
                    }
                }

                // Remember which build the cached file belongs to.
                MarkCachedUpdateComplete(tempFolder, info);
            }

            // Extract the downloaded zip (single or double zipped) and locate the update APK.
            loader.SetStatus(LocalizationSource.GetString("Msg_ExtractingUpdate"));
            string updateFolder = Path.Join(tempFolder, "Update");
            if (Directory.Exists(updateFolder))
                Directory.Delete(updateFolder, true);
            await Task.Run(() => ExtractUpdateZip(downloadOutput, updateFolder));

            // Prefer the signed APK: the Android build also emits an unsigned one next to it, and
            // the system package installer rejects unsigned packages.
            string? apkPath = Directory.EnumerateFiles(updateFolder, "*-Signed.apk", SearchOption.AllDirectories).FirstOrDefault()
                ?? Directory.EnumerateFiles(updateFolder, "*.apk", SearchOption.AllDirectories).FirstOrDefault();
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

    /// <summary>
    /// Whether a previously completed download of the given build is cached in the update temp
    /// folder and can be reused. A cached file is only trusted when its marker records the same
    /// workflow run id and the file is non-empty with a zip header, so an interrupted download or
    /// a download of a different build is always fetched again.
    /// </summary>
    static bool HasCachedUpdateDownload(string tempFolder, UpdateChecker.UpdateInfo info)
    {
        string downloadOutput = Path.Join(tempFolder, "Update.zip.zip");
        string runIdFile = Path.Join(tempFolder, "Update.zip.runid");

        if (!File.Exists(downloadOutput) || !File.Exists(runIdFile))
            return false;

        try
        {
            if (File.ReadAllText(runIdFile).Trim() != info.RunId.ToString())
                return false;

            using FileStream fs = new(downloadOutput, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length <= 0)
                return false;

            // Quick sanity check that the cached file looks like a zip archive before reusing it.
            Span<byte> header = stackalloc byte[2];
            if (fs.Read(header) != 2 || header[0] != (byte)'P' || header[1] != (byte)'K')
                return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return true;
    }

    /// <summary>Removes the marker recording which build the cached download belongs to. Best effort.</summary>
    static void ClearCachedUpdateMarker(string tempFolder)
    {
        try
        {
            File.Delete(Path.Join(tempFolder, "Update.zip.runid"));
        }
        catch (IOException)
        {
            // Best effort: at worst the next check downloads the build again.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort: at worst the next check downloads the build again.
        }
    }

    /// <summary>Records that the cached download belongs to the given build. Best effort.</summary>
    static void MarkCachedUpdateComplete(string tempFolder, UpdateChecker.UpdateInfo info)
    {
        try
        {
            File.WriteAllText(Path.Join(tempFolder, "Update.zip.runid"), info.RunId.ToString());
        }
        catch (IOException)
        {
            // Best effort: without the marker the cached file is downloaded again next time, which
            // must not fail the update that was just downloaded successfully.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
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

        if (Settings.OpenNewResourceAfterCreatingIt)
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

    // Tabs
    public Task<TabItemViewModel?> TabOpen(object? item, bool inNewTab = false) => Tabs.TabOpen(item, inNewTab);

    // Bottom bar
    public void UpdateSelectedTabProperties()
    {
        if (Data is not null && Tabs.TabSelected?.Content is IUndertaleResourceViewModel vm)
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
            && Tabs.TabSelected?.Content is IUndertaleResourceViewModel vm
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
