using System;
using System.IO;
using System.Text.Json;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace UndertaleModToolAvalonia;

public partial class SettingsFile
{
    static readonly string roamingAppData = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UndertaleModToolAvalonia");

    public SettingsFile() { }

    public static (SettingsFile settingsFile, Exception? exception) Load()
    {
        SettingsFile? settings = null;

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
                    // NOTE: Check for upgrades here.
                    settings.Version = App.VersionString;
                }
            }
            catch (Exception ex)
            {
                return (new SettingsFile(), ex);
            }
        }

        settings ??= new SettingsFile();
        return (settings, null);
    }

    public static Exception? LoadStyles()
    {
        // Load Styles.xaml
        string stylesPath = Path.Join(roamingAppData, "Styles.xaml");

        if (File.Exists(stylesPath))
        {
            Styles styles;
            try
            {
                string xaml = File.ReadAllText(stylesPath);
                styles = AvaloniaRuntimeXamlLoader.Parse<Styles>(xaml);
            }
            catch (Exception ex)
            {
                return ex;
            }

            if (App.CurrentCustomStyles is not null)
                App.Current!.Styles.Remove(App.CurrentCustomStyles);

            App.CurrentCustomStyles = styles;
            App.Current!.Styles.Add(styles);
        }

        return null;
    }

    public Exception? Save()
    {
        Directory.CreateDirectory(roamingAppData);

        string json = JsonSerializer.Serialize(this, new JsonSerializerOptions()
        {
            WriteIndented = true,
        });

        try
        {
            File.WriteAllText(Path.Join(roamingAppData, "Settings.json"), json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex;
        }
        return null;
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

    /// <summary>
    /// Show the room editor's debug overlay (pointer/view info, render time, hovered items).
    /// Disabled by default since it is only useful for diagnosing editor issues.
    /// </summary>
    public bool ShowRoomEditorDebugInfo { get; set; } = false;

    public bool EnableSelectAnyLayerByDefault { get; set; } = true;

    public bool EnableProjectBackup { get; set; } = true;

    /// <summary>
    /// Recompile all GML code sources when saving the data file with a project open,
    /// so the exported data file contains up-to-date code (mirrors the WPF version's setting).
    /// </summary>
    public bool RecompileAllCodeSourcesOnProjectSave { get; set; } = false;

    /// <summary>Check for a newer nightly build automatically when the app starts.</summary>
    public bool CheckForUpdates { get; set; } = true;

    public enum DrawerModeValue
    {
        /// <summary>Expanded (push layout) on wide screens, collapsed (overlay drawer) on narrow ones.</summary>
        Auto = 0,
        /// <summary>Desktop-style layout: the explorer sidebar is permanently visible and resizable.</summary>
        AlwaysExpanded = 1,
        /// <summary>Phone-style layout: the explorer is a slide-in drawer opened by button, edge swipe or back gesture.</summary>
        AlwaysCollapsed = 2,
    }

    /// <summary>
    /// How the asset explorer sidebar (the former fixed left panel) is displayed.
    /// </summary>
    public DrawerModeValue DrawerMode { get; set; } = DrawerModeValue.Auto;

    public bool AlwaysSaveDataInProjectDestination { get; set; } = true;

    public string InstanceIdPrefix { get; set; } = "inst_";

    public string Language { get; set; } = "";

    public Underanalyzer.Decompiler.DecompileSettings DecompileSettings { get; set; } = new();
}
