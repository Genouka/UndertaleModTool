using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using UndertaleModTool.Localization;

namespace UndertaleModToolAvalonia;

/// <summary>
/// <see cref="NativeMenuBar"/> wrapper that also works on single-window platforms (such as Android),
/// where no real <see cref="Window"/> exists to host the <see cref="NativeMenu.Menu"/> that the
/// <see cref="NativeMenuBar"/> reads from the top-level.
/// <para>
/// When the top-level has no <see cref="NativeMenu"/> attached (Android), this control builds the
/// application menu in code and attaches it to the top-level. Avalonia 12's <see cref="NativeMenuBar"/>
/// then automatically renders it as a regular in-window menu (its built-in fallback for platforms
/// that do not export native menus). On desktop the top-level already has the <see cref="NativeMenu"/>
/// defined in <c>MainWindow.axaml</c>, so nothing is overridden and the native menu keeps working.
/// </para>
/// </summary>
public partial class MenuBarCompat : UserControl
{
    private bool _fallbackMenuInstalled;

    public MenuBarCompat()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        InstallFallbackMenuIfNeeded();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        InstallFallbackMenuIfNeeded();
    }

    private void InstallFallbackMenuIfNeeded()
    {
        if (_fallbackMenuInstalled)
            return;

        if (TopLevel.GetTopLevel(this) is not { } topLevel)
            return;

        // A real window already provides the native menu (desktop); keep it.
        if (NativeMenu.GetMenu(topLevel) is not null)
            return;

        if (DataContext is not MainViewModel vm)
            return;

        NativeMenu.SetMenu(topLevel, BuildMenu(vm));
        _fallbackMenuInstalled = true;
    }

    private static Binding Loc(string key) =>
        new($"[{key}]") { Source = LocalizationSource.Instance, Mode = BindingMode.OneWay };

    private static NativeMenuItem Item(string locKey, NativeMenu? submenu = null, ICommand? command = null, KeyGesture? gesture = null, bool enabled = true)
    {
        NativeMenuItem item = new();
        item.Bind(NativeMenuItem.HeaderProperty, Loc(locKey));
        item.Menu = submenu;
        item.Command = command;
        item.Gesture = gesture;
        item.IsEnabled = enabled;
        return item;
    }

    /// <summary>
    /// Mirror of the <see cref="NativeMenu"/> declared in <c>MainWindow.axaml</c>, so that
    /// single-window platforms can display the same application menu without a real window.
    /// </summary>
    private static NativeMenu BuildMenu(MainViewModel vm)
    {
        KeyModifiers modifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

        NativeMenu file = new();
        file.Items.Add(Item("Menu_File_New", command: new RelayCommand(vm.FileNew), gesture: new KeyGesture(Key.N, modifier)));
        file.Items.Add(Item("Menu_File_Open", command: new RelayCommand(vm.FileOpen), gesture: new KeyGesture(Key.O, modifier)));
        file.Items.Add(Item("Menu_File_Save", command: new RelayCommand(vm.FileSave), gesture: new KeyGesture(Key.S, modifier)));
        file.Items.Add(Item("Menu_File_Close", command: new RelayCommand(vm.FileClose)));
        file.Items.Add(new NativeMenuItemSeparator());
        file.Items.Add(Item("Menu_File_TempRun", command: new RelayCommand(vm.FileRun), gesture: new KeyGesture(Key.F5)));
        file.Items.Add(Item("Menu_File_RunOtherRunner", command: new RelayCommand(vm.FileRunWithOther)));
        file.Items.Add(new NativeMenuItemSeparator());
        file.Items.Add(Item("Menu_File_Settings", command: new RelayCommand(vm.FileSettings)));
        file.Items.Add(Item("Menu_File_Exit", command: new RelayCommand(vm.FileExit)));

        NativeMenu tools = new();
        tools.Items.Add(Item("Menu_Find_SearchInCode", command: new RelayCommand(vm.ToolsSearchInCode),
            gesture: new KeyGesture(Key.F, modifier | KeyModifiers.Shift)));
        tools.Items.Add(Item("Menu_Edit_FindReferences", command: new RelayCommand(vm.ToolsFindReferences)));

        NativeMenu scripts = new();
        scripts.Items.Add(Item("Menu_Scripts_RunOther", command: new RelayCommand(vm.ScriptsRunOtherScript)));

        NativeMenu project = new();
        project.Items.Add(Item("Menu_Project_New", command: new RelayCommand(vm.ProjectNew)));
        project.Items.Add(Item("Menu_Project_Open", command: new RelayCommand(vm.ProjectOpen)));
        project.Items.Add(Item("Menu_Project_Save", command: new RelayCommand(vm.ProjectSave)));
        project.Items.Add(new NativeMenuItemSeparator());
        project.Items.Add(Item("Menu_Project_Reload", command: new RelayCommand(vm.ProjectReload)));
        project.Items.Add(new NativeMenuItemSeparator());
        project.Items.Add(Item("Menu_Project_ViewUnexportedAssets", command: new RelayCommand(vm.ProjectViewUnexportedAssets)));
        project.Items.Add(new NativeMenuItemSeparator());
        project.Items.Add(Item("Menu_Project_Close", command: new RelayCommand(vm.ProjectClose)));

        NativeMenu help = new();
        help.Items.Add(Item("Menu_Help_GitHub", command: new RelayCommand(vm.HelpGitHub)));
        help.Items.Add(Item("Menu_Help_About", command: new RelayCommand(vm.HelpAbout), gesture: new KeyGesture(Key.F1)));

        NativeMenu menu = new();
        menu.Items.Add(Item("Menu_File", submenu: file));
        menu.Items.Add(Item("Menu_Import", enabled: false));
        menu.Items.Add(Item("Menu_Export", enabled: false));
        menu.Items.Add(Item("Menu_Tools", submenu: tools));
        menu.Items.Add(Item("Menu_Scripts", submenu: scripts));
        menu.Items.Add(Item("Menu_Project", submenu: project));
        menu.Items.Add(Item("Menu_Help", submenu: help));

        return menu;
    }
}
