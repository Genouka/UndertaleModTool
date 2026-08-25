using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Data;
using CommunityToolkit.Mvvm.Input;
using UndertaleModTool.Localization;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Built-in utility scripts (the <c>Scripts</c> folder shared with the WPF UndertaleModTool) and
/// the "Scripts" application menu that lists them - a port of the WPF tool's
/// <c>MenuItem_RunScript_SubmenuOpened</c> / <c>MenuItem_RunBuiltinScript_Item_Click</c>.
/// <para>
/// The WPF version populates each submenu lazily when it opens; Avalonia's native menu API offers
/// no per-open callback, so the whole tree is built once, eagerly, at menu creation time. Unlike
/// WPF, subfolders are included when they contain .csx files anywhere below them, so no dead-end
/// submenus can appear.
/// </para>
/// </summary>
public static class BuiltInScripts
{
    /// <summary>
    /// Platform override for the scripts root directory. Android points this at the internal
    /// storage copy extracted from the APK assets (<see cref="Android.BuiltInScriptExtractor"/>);
    /// desktop uses the default <c>{exe directory}/Scripts</c>.
    /// </summary>
    public static string? RootDirectoryOverride { get; set; }

    /// <summary>
    /// Additional script directories listed after the built-in ones (each separated by a menu
    /// separator, skipped entirely while missing). Android registers its public-storage folder
    /// (<c>/sdcard/QiuUTMTv5/Scripts</c>) here so users can drop in their own scripts without
    /// reinstalling the app.
    /// </summary>
    public static List<string> ExtraRootDirectories { get; } = [];

    /// <summary>The directory the built-in scripts are loaded from.</summary>
    public static string GetRootDirectory()
        => RootDirectoryOverride
           ?? Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "Scripts");

    /// <summary>Builds the full content of the root "Scripts" menu.</summary>
    public static NativeMenu BuildRootMenu(MainViewModel vm)
    {
        NativeMenu menu = [];
        string rootDir = GetRootDirectory();

        try
        {
            if (!Directory.Exists(rootDir))
            {
                // Same as the WPF tool: report the missing path, but keep "Run other script..."
                // reachable.
                menu.Items.Add(DisabledItem(string.Format(LocalizationSource.GetString("Msg_PathNotExist"), rootDir)));
                menu.Items.Add(RunOtherItem(vm));
                return menu;
            }

            FillMenu(menu, vm, rootDir);

            // Extra sources (if any are present) follow after a separator each.
            foreach (string extraDir in ExtraRootDirectories)
            {
                NativeMenu? extraMenu = null;
                try
                {
                    if (!Directory.Exists(extraDir))
                        continue;

                    extraMenu = [];
                    FillMenu(extraMenu, vm, extraDir);
                }
                catch (Exception)
                {
                    // Inaccessible for now (e.g. storage permission not granted yet) - skip it,
                    // keeping both the built-in entries and any other sources intact.
                    extraMenu = null;
                }

                if (extraMenu is null || extraMenu.Items.Count == 0)
                    continue;

                if (menu.Items.Count > 0)
                    menu.Items.Add(new NativeMenuItemSeparator());
                foreach (NativeMenuItemBase item in extraMenu.Items)
                    menu.Items.Add(item);
            }

            if (menu.Items.Count == 0)
                menu.Items.Add(DisabledItem(LocalizationSource.GetString("Msg_NoScriptsFound")));

            // The root menu also carries the "Run other script..." entry.
            menu.Items.Add(RunOtherItem(vm));
        }
        catch (Exception e)
        {
            menu.Items.Clear();
            menu.Items.Add(DisabledItem(e.ToString()));
        }

        return menu;
    }

    /// <summary>Adds one submenu level: every direct .csx file, then every script-bearing subfolder.</summary>
    private static void FillMenu(NativeMenu menu, MainViewModel vm, string directory)
    {
        foreach (string file in Directory.EnumerateFiles(directory, "*.csx").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            menu.Items.Add(new NativeMenuItem
            {
                Header = EscapeHeader(Path.GetFileName(file)),
                Command = new RelayCommand(() => vm.ScriptsRunBuiltinScript(file)),
            });
        }

        foreach (string subDir in Directory.EnumerateDirectories(directory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            if (!ContainsCsx(subDir))
                continue;

            NativeMenu subMenu = [];
            FillMenu(subMenu, vm, subDir);
            if (subMenu.Items.Count == 0)
                subMenu.Items.Add(DisabledItem(LocalizationSource.GetString("Msg_NoScriptsFound")));

            menu.Items.Add(new NativeMenuItem
            {
                Header = EscapeHeader(Path.GetFileName(subDir)),
                Menu = subMenu,
            });
        }
    }

    private static bool ContainsCsx(string directory)
        => Directory.EnumerateFiles(directory, "*.csx", SearchOption.AllDirectories).Any();

    private static NativeMenuItem RunOtherItem(MainViewModel vm)
    {
        NativeMenuItem item = new();
        item.Bind(NativeMenuItem.HeaderProperty, Loc("Msg_RunOtherScript"));
        item.Command = new RelayCommand(vm.ScriptsRunOtherScript);
        return item;
    }

    private static NativeMenuItem DisabledItem(string header)
        => new() { Header = header, IsEnabled = false };

    private static Binding Loc(string key)
        => new($"[{key}]") { Source = LocalizationSource.Instance, Mode = BindingMode.OneWay };

    /// <summary>
    /// Doubles underscores like the WPF tool does ("_" starts a keyboard mnemonic in menus).
    /// </summary>
    private static string EscapeHeader(string name)
        => name.Replace("_", "__");
}
