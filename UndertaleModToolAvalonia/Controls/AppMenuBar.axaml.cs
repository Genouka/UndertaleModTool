using System;
using System.Collections.Generic;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using UndertaleModTool.Localization;

namespace UndertaleModToolAvalonia;

/// <summary>
/// The application menu bar and the single source of truth for the application menu: builds the
/// whole menu (including the "Scripts" built-in scripts tree) in code and attaches it to the
/// top-level, then hosts a <see cref="NativeMenuBar"/> which displays it as the OS-native menu
/// bar (macOS) or as an in-window menu (Windows/Linux/Android, where no native menu bar is
/// exported).
/// <para>
/// The menu is declared only here - it used to be defined twice (in <c>MainWindow.axaml</c> for
/// desktop and again in code for the single-window fallback), and the copies had already drifted
/// apart. Items carrying both a command and a gesture also register a matching key binding on
/// the top-level, so the displayed shortcuts cannot drift from the ones that actually work: the
/// in-window menu does not process its items' gestures as application shortcuts, and on macOS
/// the exported native menu consumes the key event before window bindings are reached.
/// </para>
/// </summary>
public partial class AppMenuBar : UserControl
{
    private bool _menuInstalled;

    public AppMenuBar()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        InstallMenuIfNeeded();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        InstallMenuIfNeeded();
    }

    private void InstallMenuIfNeeded()
    {
        if (_menuInstalled)
            return;

        if (TopLevel.GetTopLevel(this) is not { } topLevel)
            return;

        if (DataContext is not MainViewModel vm)
            return;

        NativeMenu menu = BuildMenu(vm);
        NativeMenu.SetMenu(topLevel, menu);
        topLevel.KeyBindings.AddRange(CollectKeyBindings(menu));
        _menuInstalled = true;
    }

    private static Binding Loc(string key) =>
        new($"[{key}]") { Source = LocalizationSource.Instance, Mode = BindingMode.OneWay };

    private static Binding ActiveBinding(MainViewModel vm) =>
        new(nameof(MainViewModel.ProjectActive)) { Source = vm, Mode = BindingMode.OneWay };

    /// <summary>Adds a menu item with a localized header.</summary>
    private static NativeMenuItem Item(NativeMenu parent, string locKey,
        ICommand? command = null, KeyGesture? gesture = null, NativeMenu? submenu = null,
        BindingBase? isEnabled = null)
    {
        NativeMenuItem item = new()
        {
            Command = command,
            Gesture = gesture,
            Menu = submenu,
        };
        item.Bind(NativeMenuItem.HeaderProperty, Loc(locKey));
        if (isEnabled is not null)
            item.Bind(NativeMenuItem.IsEnabledProperty, isEnabled);
        parent.Items.Add(item);
        return item;
    }

    /// <summary>Adds a menu item with a literal header (no localization key exists yet).</summary>
    private static NativeMenuItem LiteralItem(NativeMenu parent, string header,
        ICommand? command = null, KeyGesture? gesture = null)
    {
        NativeMenuItem item = new()
        {
            Header = header,
            Command = command,
            Gesture = gesture,
        };
        parent.Items.Add(item);
        return item;
    }

    /// <summary>
    /// The application menu: File / Tools / Scripts / Project / Help. Keep in sync with the WPF
    /// tool's menu in <c>UndertaleModTool/MainWindow.xaml</c>.
    /// </summary>
    private static NativeMenu BuildMenu(MainViewModel vm)
    {
        KeyModifiers modifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

        NativeMenu file = new();
        Item(file, "Menu_File_New", command: new RelayCommand(vm.FileNew), gesture: new KeyGesture(Key.N, modifier));
        Item(file, "Menu_File_Open", command: new RelayCommand(vm.FileOpen), gesture: new KeyGesture(Key.O, modifier));
        Item(file, "Menu_File_Save", command: new RelayCommand(vm.FileSave), gesture: new KeyGesture(Key.S, modifier));
        Item(file, "Menu_File_Close", command: new RelayCommand(vm.FileClose));
        file.Items.Add(new NativeMenuItemSeparator());
        Item(file, "Menu_File_TempRun", command: new RelayCommand(vm.FileTempRun), gesture: new KeyGesture(Key.F5));
        LiteralItem(file, "_Run with data/project runner", command: new RelayCommand(vm.FileRun), gesture: new KeyGesture(Key.F5, KeyModifiers.Alt));
        Item(file, "Menu_File_RunOtherRunner", command: new RelayCommand(vm.FileRunWithOther));
        file.Items.Add(new NativeMenuItemSeparator());
        LiteralItem(file, "Clear audio group cache", command: new RelayCommand(vm.FileClearAudioGroupCache));
        file.Items.Add(new NativeMenuItemSeparator());
        Item(file, "Menu_File_Settings", command: new RelayCommand(vm.FileSettings), gesture: new KeyGesture(Key.F4));
        Item(file, "Menu_File_Exit", command: new RelayCommand(vm.FileExit), gesture: new KeyGesture(Key.Q, modifier));

        NativeMenu tools = new();
        Item(tools, "Menu_Find_SearchInCode", command: new RelayCommand(vm.ToolsSearchInCode),
            gesture: new KeyGesture(Key.F, modifier | KeyModifiers.Shift));
        Item(tools, "Menu_Edit_FindReferences", command: new RelayCommand(vm.ToolsFindReferences));

        NativeMenu project = new();
        Item(project, "Menu_Project_New", command: new RelayCommand(vm.ProjectNew));
        Item(project, "Menu_Project_Open", command: new RelayCommand(vm.ProjectOpen));
        Item(project, "Menu_Project_Save", command: new RelayCommand(vm.ProjectSave), isEnabled: ActiveBinding(vm));
        project.Items.Add(new NativeMenuItemSeparator());
        Item(project, "Menu_Project_Reload", command: new RelayCommand(vm.ProjectReload), isEnabled: ActiveBinding(vm));
        project.Items.Add(new NativeMenuItemSeparator());
        Item(project, "Menu_Project_ViewUnexportedAssets", command: new RelayCommand(vm.ProjectViewUnexportedAssets), isEnabled: ActiveBinding(vm));
        project.Items.Add(new NativeMenuItemSeparator());
        Item(project, "Menu_Project_Close", command: new RelayCommand(vm.ProjectClose), isEnabled: ActiveBinding(vm));

        NativeMenu help = new();
        Item(help, "Menu_Help_CheckForUpdates", command: new RelayCommand(vm.HelpCheckForUpdates));
        help.Items.Add(new NativeMenuItemSeparator());
        Item(help, "Menu_Help_GitHub", command: new RelayCommand(vm.HelpGitHub));
        Item(help, "Menu_Help_About", command: new RelayCommand(vm.HelpAbout), gesture: new KeyGesture(Key.F1));

        NativeMenu menu = new();
        Item(menu, "Menu_File", submenu: file);
        Item(menu, "Menu_Tools", submenu: tools);
        Item(menu, "Menu_Scripts", submenu: BuiltInScripts.BuildRootMenu(vm));
        Item(menu, "Menu_Project", submenu: project);
        Item(menu, "Menu_Help", submenu: help);

        return menu;
    }

    /// <summary>
    /// Collects one key binding per menu command that carries a gesture, so the shortcuts work
    /// on platforms without a native menu bar (where menu gestures are display-only).
    /// </summary>
    private static List<KeyBinding> CollectKeyBindings(NativeMenu menu)
    {
        List<KeyBinding> bindings = [];
        Collect(menu, bindings);
        return bindings;

        static void Collect(NativeMenu menu, List<KeyBinding> bindings)
        {
            foreach (NativeMenuItemBase itemBase in menu.Items)
            {
                if (itemBase is not NativeMenuItem item)
                    continue;

                if (item.Menu is { } submenu)
                    Collect(submenu, bindings);
                else if (item.Command is { } command && item.Gesture is { } gesture)
                    bindings.Add(new KeyBinding { Gesture = gesture, Command = command });
            }
        }
    }
}
