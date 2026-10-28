using Avalonia.Controls;
using UndertaleModTool.Localization;

namespace UndertaleModToolAvalonia;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!e.IsProgrammatic)
        {
            if (DataContext is MainViewModel vm && vm.Data is not null && !vm.IsUpdating)
            {
                e.Cancel = true;

                async void AskSaveBeforeClose()
                {
                    if (await vm.AskProjectSave(LocalizationSource.GetString("Msg_SaveProjectBeforeQuitting"))
                        && await vm.AskFileSave(LocalizationSource.GetString("Msg_SaveDataFileBeforeQuitting")))
                        Close();
                }

                AskSaveBeforeClose();
            }
        }

        base.OnClosing(e);
    }
}
