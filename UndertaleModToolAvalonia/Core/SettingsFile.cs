using System;
using System.IO;
using System.Text.Json;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using UndertaleModTool.Localization;

namespace UndertaleModToolAvalonia;

public partial class SettingsFile
{
    public MainViewModel MainVM = null!;

    public SettingsFile() { }
    public SettingsFile(IServiceProvider serviceProvider)
    {
        MainVM = serviceProvider.GetRequiredService<MainViewModel>();
    }

    public static SettingsFile Load(IServiceProvider serviceProvider)
    {
        MainViewModel mainVM = serviceProvider.GetRequiredService<MainViewModel>();

        SettingsFile? settings = null;

        string roamingAppData = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UndertaleModToolAvalonia");

        // Load Settings.json
        string settingsPath = Path.Join(roamingAppData, "Settings.json");

        if (File.Exists(settingsPath))
        {
            try
            {
                string json = File.ReadAllText(settingsPath);
                settings = JsonSerializer.Deserialize<SettingsFile>(json, new JsonSerializerOptions()
                {
                    AllowTrailingCommas = true,
                });

                if (settings is not null)
                {
                    // Check for upgrades here.
                    settings.MainVM = mainVM;
                    settings.Version = App.VersionString;
                }
            }
            catch (Exception e)
            {
                mainVM.LazyErrorMessages.Add($"{LocalizationSource.GetString("Msg_ErrorSettingsLoading")}\n{e.Message}\n{LocalizationSource.GetString("Msg_DefaultSettingsLoaded")}");
            }
        }

        settings ??= new SettingsFile(serviceProvider);

        // Load Styles.xaml
        string stylesPath = Path.Join(roamingAppData, "Styles.xaml");

        if (File.Exists(stylesPath))
        {
            try
            {
                string xaml = File.ReadAllText(stylesPath);
                Styles styles = AvaloniaRuntimeXamlLoader.Parse<Styles>(xaml);

                if (App.CurrentCustomStyles is not null)
                    App.Current!.Styles.Remove(App.CurrentCustomStyles);

                App.CurrentCustomStyles = styles;
                App.Current!.Styles.Add(styles);
            }
            catch (Exception e)
            {
                mainVM.LazyErrorMessages.Add($"{LocalizationSource.GetString("Msg_ErrorStylesLoading")}\n{e.Message}");
            }
        }

        return settings;
    }

    public async void Save()
    {
        string roamingAppData = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UndertaleModToolAvalonia");
        Directory.CreateDirectory(roamingAppData);

        string json = JsonSerializer.Serialize(this, new JsonSerializerOptions()
        {
            WriteIndented = true,
        });

        try
        {
            File.WriteAllText(Path.Join(roamingAppData, "Settings.json"), json);
        }
        catch (Exception e)
        {
            await MainVM.View!.MessageDialog(string.Format(LocalizationSource.GetString("Msg_ErrorSettingsSaving"), e.Message));
        }
    }

    public string Version { get; set; } = App.VersionString;

    public enum ThemeValue
    {
        SystemDefault = 0,
        Light = 1,
        Dark = 2,
    }

    public ThemeValue Theme
    {
        get;
        set
        {
            field = value;
            App.Current?.RequestedThemeVariant = value switch
            {
                ThemeValue.SystemDefault => ThemeVariant.Default,
                ThemeValue.Light => ThemeVariant.Light,
                ThemeValue.Dark => ThemeVariant.Dark,
                _ => throw new NotImplementedException(),
            };
        }
    }

    public bool StartMaximized { get; set; } = true;

    public bool OpenNewResourceAfterCreatingIt { get; set; } = false;
    public bool EnableSyntaxHighlighting { get; set; } = true;
    public bool AutomaticallyCompileAndDecompileCodeOnLostFocus { get; set; } = true;

    // Code editor options
    public bool CodeEditorWordWrap { get; set; } = true;
    public bool CodeEditorShowWhitespace { get; set; } = false;
    public bool CodeEditorShowHoverInfo { get; set; } = true;
    public bool CodeEditorAutoDiagnostics { get; set; } = true;
    public bool ChangeTrackingEnabled { get; set; } = true;

    /// <summary>
    /// Code editor font size in DIPs. Used on touch platforms (pinch-to-zoom) but also applies on
    /// desktop so the setting is shared everywhere.
    /// </summary>
    public double CodeEditorFontSize { get; set; } = 12;

    public bool EnableRoomGridByDefault { get; set; } = false;
    public uint DefaultRoomGridWidth { get; set; } = 20;
    public uint DefaultRoomGridHeight { get; set; } = 20;

    public bool EnableSelectAnyLayerByDefault { get; set; } = true;

    public bool EnableProjectBackup { get; set; } = true;

    public string InstanceIdPrefix { get; set; } = "inst_";

    public string Language { get; set; } = "";

    public Underanalyzer.Decompiler.DecompileSettings DecompileSettings { get; set; } = new();
}
