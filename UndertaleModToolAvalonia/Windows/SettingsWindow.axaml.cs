using System;
using System.Globalization;
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

    async void MirrorChyanPromoClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && vm.MainVM.View is { } view)
            await view.LaunchUriAsync(new Uri(MirrorChyanClient.GetProjectPageUrl()));
    }

    void LanguageSelectionChanged(object? sender, SelectionChangedEventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                if (DataContext is not SettingsViewModel vm)
                    return;

                vm.RefreshLanguageDependentState();

                // Apply the language immediately so localized script names resolve for the
                // rebuilt Scripts menu; "" falls back to the system language.
                string language = vm.MainVM.Settings.Language;
                LocalizationSource.Instance.CurrentCulture = string.IsNullOrEmpty(language)
                    ? CultureInfo.InstalledUICulture
                    : new CultureInfo(language);

                BuiltInScripts.NotifyScriptsChanged();
            });
}
