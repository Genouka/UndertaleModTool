using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using UndertaleModLib;

namespace UndertaleModToolAvalonia;

public partial class MainView : UserControl, IView
{
    ProjectAssetsWindow? projectAssetsWindow = null;

    /// <summary>Screen width (DIPs) above which the auto drawer mode uses the expanded layout.</summary>
    private const double AutoExpandMinWidth = 700;

    /// <summary>The drawer hosting the asset explorer (the former LeftPanel).</summary>
    public DrawerPage? Drawer => DrawerPageHost;

    public MainView()
    {
        InitializeComponent();

        DataContextChanged += (_, __) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.View = this;
            }
            ApplyDrawerSettings();
        };

        Loaded += (_, __) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.OnLoaded();
            }
        };

        CommandTextBox.AddHandler(TextBox.KeyDownEvent, CommandTextBox_KeyDown_Tunnel, RoutingStrategies.Tunnel);
    }

    private TopLevel? _sizeTrackingTopLevel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Auto drawer mode follows the window size live (e.g. rotating a phone re-evaluates it).
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            _sizeTrackingTopLevel = topLevel;
            topLevel.PropertyChanged += TopLevel_PropertyChanged;
        }

        ApplyDrawerSettings();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_sizeTrackingTopLevel is { } topLevel)
        {
            topLevel.PropertyChanged -= TopLevel_PropertyChanged;
            _sizeTrackingTopLevel = null;
        }
    }

    private void TopLevel_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TopLevel.ClientSizeProperty)
            ApplyDrawerSettings();
    }

    /// <summary>
    /// Resolves the "DrawerMode" setting to a concrete drawer layout: "always expanded" is the
    /// desktop-style push layout with a permanently visible, resizable sidebar; "always collapsed"
    /// is the phone-style overlay drawer; "auto" picks by current screen width (wide → expanded).
    /// </summary>
    private void ApplyDrawerSettings()
    {
        if (DrawerPageHost is null)
            return;

        SettingsFile.DrawerModeValue setting = DataContext is MainViewModel vm
            ? vm.Settings.DrawerMode
            : SettingsFile.DrawerModeValue.Auto;

        bool expanded = setting switch
        {
            SettingsFile.DrawerModeValue.AlwaysExpanded => true,
            SettingsFile.DrawerModeValue.AlwaysCollapsed => false,
            _ => (TopLevel.GetTopLevel(this)?.ClientSize.Width ?? double.PositiveInfinity) >= AutoExpandMinWidth,
        };

        DrawerPageHost.Mode = expanded ? DrawerPage.DrawerDisplayMode.Push : DrawerPage.DrawerDisplayMode.Overlay;
        DrawerPageHost.IsCollapsible = !expanded;
        DrawerPageHost.IsDrawerOpen = expanded;
    }

    public async Task OpenSettingsDialog(IServiceProvider serviceProvider)
    {
        Window? window = WindowHost.ResolveOwner(this);
        await WindowHost.ShowDialog(window, new SettingsWindow()
        {
            DataContext = new SettingsViewModel(serviceProvider),
        });

        // The settings window writes MainVM.Settings directly and saves on close; pick up a
        // changed drawer mode right away.
        ApplyDrawerSettings();
    }

    public void OpenSearchInCode(IServiceProvider serviceProvider)
    {
        Window? window = WindowHost.ResolveOwner(this);
        WindowHost.Show(window, new SearchInCodeWindow()
        {
            DataContext = new SearchInCodeViewModel(serviceProvider),
        });
    }

    public void OpenFindReferences(IServiceProvider serviceProvider, UndertaleResource? resource = null)
    {
        Window? window = WindowHost.ResolveOwner(this);
        WindowHost.Show(window, new FindReferencesWindow()
        {
            DataContext = new FindReferencesViewModel(serviceProvider, resource),
        });
    }

    public void OpenProjectAssets(IServiceProvider serviceProvider)
    {
        Window? window = WindowHost.ResolveOwner(this);

        if (projectAssetsWindow is not null)
        {
            projectAssetsWindow.Focus();
        }
        else
        {
            projectAssetsWindow = new ProjectAssetsWindow(serviceProvider);
            projectAssetsWindow.Closed += (_, _) =>
            {
                projectAssetsWindow = null;
            };
            WindowHost.Show(window, projectAssetsWindow);
        }
    }

    public void CloseProjectAssets()
    {
        projectAssetsWindow?.Close();
        projectAssetsWindow = null;
    }

    private async void CommandTextBox_KeyDown_Tunnel(object? sender, KeyEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                e.Handled = true;
                object? result = await vm.Scripting.RunScript(vm.CommandTextBoxText);
                vm.CommandTextBoxText = result?.ToString() ?? "";
            }
    }
}
