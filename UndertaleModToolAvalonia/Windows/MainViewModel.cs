using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    public partial UndertaleData? Data { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string? DataPath { get; set; }

    [ObservableProperty]
    public partial (uint Major, uint Minor, uint Release, uint Build) DataVersion { get; set; }

    IStorageFolder? lastDataLocation;

    // Project
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial ProjectContext? Project { get; set; }

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

        AudioPlayer.Init(f => Dispatcher.UIThread.Post(f));

        DataExplorer = new(this);

        _ = TabOpen(new DescriptionViewModel(
            LocalizationSource.GetString("Main_WelcomeHeading"),
            LocalizationSource.GetString("Main_WelcomeDescription")));
    }

    public void Initialize()
    {
        Settings = SettingsFile.Load(ServiceProvider);
        Scripting = new(ServiceProvider);

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
            // TODO: RecompileAllCodeSourcesOnProjectSave setting
            if (Project is not null)
            {
                Project.RecompileAllCodeSources();
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
                // Android SAF 输出流可能不支持随机访问(不可 Seek),而 UndertaleWriter 依赖
                // Position/Seek。先把数据写入缓存里的临时文件(可 Seek),再顺序拷贝到所选文件。
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
        await Scripting.RunScript(text, filePath);

        CommandTextBoxText = string.Format(LocalizationSource.GetString("Msg_ScriptFinished"), Path.GetFileName(filePath) ?? "Script");
    }

    void ClearProject()
    {
        Project = null;

        if (View is MainView mainView)
            mainView.CloseProjectAssets();
    }

    void SetProject(ProjectContext projectContext)
    {
        Project = projectContext;
        Project.UnexportedAssetsChanged += (s, e) =>
        {
            UpdateSelectedTabProperties();
        };

        UpdateSelectedTabProperties();
    }

    async Task<string?> AskProjectDestinationDataFile()
    {
        // Destination data file
        // TODO: Check if same as source and if empty directory
        IStorageFile? destinationDataFile = await View!.SaveFileDialog(new()
        {
            Title = LocalizationSource.GetString("Msg_SelectDestDataFile"),
            FileTypeChoices = FilePickerFileTypes.Data,
        });
        string? destinationDataPath = destinationDataFile?.TryGetLocalPath();

        return destinationDataPath;
    }

    public async void ProjectNew()
    {
        // TODO: Ask for source data file if nothing is opened
        if (Data is null || DataPath is null)
            return;

        if (!await AskProjectSave(LocalizationSource.GetString("Msg_SaveProjectBeforeCreating")))
            return;

        ClearProject();

        // Project name
        string? projectName = await View!.TextBoxDialog(LocalizationSource.GetString("Msg_ProjectName"), $"{Data.GeneralInfo?.DisplayName?.Content ?? LocalizationSource.GetString("Msg_NewMod")} Mod");
        if (projectName is null)
            return;

        // Project folder
        IReadOnlyList<IStorageFolder> projectFolderList = await View!.OpenFolderDialog(new() { Title = LocalizationSource.GetString("Msg_SelectProjectFolder") });
        string? projectFolderPath = projectFolderList.ElementAtOrDefault(0)?.TryGetLocalPath();

        if (projectFolderPath is null)
            return;

        string projectFilePath = Path.Join(projectFolderPath, "project.json");

        // Destination data file
        string? destinationDataPath = await AskProjectDestinationDataFile();
        if (destinationDataPath is null)
            return;

        ProjectContext projectContext;
        try
        {
            projectContext = new(Data, DataPath, destinationDataPath, projectFilePath, projectName.Trim(), Dispatcher.UIThread.Invoke);
        }
        catch (ProjectException e)
        {
            await View!.MessageDialog(string.Format(LocalizationSource.GetString("Msg_FailedCreateProject"), e.Message));
            return;
        }
        catch (Exception e)
        {
            await View!.MessageDialog(LocalizationSource.GetString("Msg_ErrorCreateProject") + "\n" + e);
            return;
        }

        DataPath = destinationDataPath;
        SetProject(projectContext);
    }

    public async void ProjectOpen()
    {
        // TODO: Ask for source data file if nothing is opened
        if (Data is null || DataPath is null)
            return;

        if (!await AskProjectSave(LocalizationSource.GetString("Msg_SaveProjectBeforeOpening")))
            return;

        ClearProject();

        // Project file
        IReadOnlyList<IStorageFile> projectFileList = await View!.OpenFileDialog(new()
        {
            Title = LocalizationSource.GetString("Msg_SelectProjectFile"),
            FileTypeFilter = FilePickerFileTypes.JSON,
        });
        string? projectFilePath = projectFileList.ElementAtOrDefault(0)?.TryGetLocalPath();
        if (projectFilePath is null)
            return;

        // Destination data file
        string? destinationDataPath = await AskProjectDestinationDataFile();
        if (destinationDataPath is null)
            return;

        ProjectContext projectContext;
        try
        {
            projectContext = ProjectContext.CreateWithDataFilePaths(DataPath, destinationDataPath, projectFilePath);
            projectContext.Import(Data, Settings!.EnableProjectBackup ? null : new GameFileNoOpBackup(), Dispatcher.UIThread.Invoke);
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

        DataPath = destinationDataPath;
        SetProject(projectContext);
    }

    public async void ProjectSave()
    {
        await ProjectSaveTask();
    }

    public async Task<bool> ProjectSaveTask()
    {
        if (Project is null || Data is null || DataPath is null)
            return false;

        try
        {
            Project.Export(true);
            return true;
        }
        catch (ProjectException e)
        {
            await View!.MessageDialog(string.Format(LocalizationSource.GetString("Msg_FailedSaveProject"), e.Message));
        }
        catch (Exception e)
        {
            await View!.MessageDialog(LocalizationSource.GetString("Msg_ErrorSaveProject") + "\n" + e);
        }

        return false;
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

        ClearProject();

        ProjectContext projectContext;
        try
        {
            projectContext = ProjectContext.CreateWithDataFilePaths(sourceDataPath, destinationDataPath, projectFilePath);
            projectContext.Import(Data, Settings!.EnableProjectBackup ? null : new GameFileNoOpBackup(), Dispatcher.UIThread.Invoke);
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
    }

    public async void HelpGitHub()
    {
        await View!.LaunchUriAsync(new Uri("https://github.com/UnderminersTeam/UndertaleModTool"));
    }

    public async void HelpAbout()
    {
        await View!.MessageDialog(string.Format(LocalizationSource.GetString("Msg_AboutUndertaleModTool"), App.VersionString) +
            LocalizationSource.GetString("Msg_AboutBody1") +
            "\nhttps://github.com/UnderminersTeam/UndertaleModTool" +
            "\n" + LocalizationSource.GetString("Msg_AboutBody2") +
            "\n" +
            "\n" + string.Format(LocalizationSource.GetString("Msg_AboutBody3"), App.InformationalVersionString)
            ,
            title: LocalizationSource.GetString("Msg_AboutTitle"));
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