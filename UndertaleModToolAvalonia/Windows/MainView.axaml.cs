using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using UndertaleModLib;

namespace UndertaleModToolAvalonia;

public partial class MainView : UserControl, IView
{
    ProjectAssetsWindow? projectAssetsWindow = null;

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

    public async Task OpenSettingsDialog(IServiceProvider serviceProvider)
    {
        Window? window = WindowHost.ResolveOwner(this);
        await WindowHost.ShowDialog(window, new SettingsWindow()
        {
            DataContext = new SettingsViewModel(serviceProvider),
        });
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