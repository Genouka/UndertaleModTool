using System;
using System.Linq;
using Avalonia.Controls;
using UndertaleModTool.Localization;

namespace UndertaleModToolAvalonia;

    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);

            // The XAML declares the Scripts root as an empty placeholder - the only top-level
            // entry without a submenu or command (a dynamically built submenu cannot be expressed
            // in XAML, and NativeMenuItem supports no x:Name field). Fill it with the built-in
            // scripts. The WPF tool fills this menu lazily on submenu open; Avalonia's native
            // menu API has no per-open callback, so it is built once here.
            if (DataContext is MainViewModel vm
                && NativeMenu.GetMenu(this) is { } root
                && root.Items.OfType<NativeMenuItem>().FirstOrDefault(i =>
                    i is not NativeMenuItemSeparator && i.Menu is null && i.Command is null) is { } scriptsItem)
            {
                scriptsItem.Menu = BuiltInScripts.BuildRootMenu(vm);
            }
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
