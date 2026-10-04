using System;
using Avalonia.Controls;
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
}
