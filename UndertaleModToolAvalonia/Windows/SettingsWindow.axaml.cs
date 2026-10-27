using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using UndertaleModTool.Localization;

namespace UndertaleModToolAvalonia;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        Closing += async (_, _) =>
        {
            if (DataContext is SettingsViewModel vm)
            {
                if (vm.MainVM.Settings.Save() is Exception ex)
                {
                    await vm.MainVM.View!.MessageDialog(string.Format(LocalizationSource.GetString("Msg_ErrorSettingsSaving"), ex.Message));
                }
            }
        };
    }

    async void MirrorChyanGetCdkClick(object? sender, RoutedEventArgs e)
    {
        string url = LocalizationSource.Instance.CurrentCulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? "https://mirrorchyan.com/zh/get-key"
            : "https://mirrorchyan.com/en/get-key";

        if (DataContext is SettingsViewModel vm && vm.MainVM.View is { } view)
            await view.LaunchUriAsync(new Uri(url));
    }
}
