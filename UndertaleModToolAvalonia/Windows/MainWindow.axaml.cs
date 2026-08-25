using System;
using System.Linq;
using Avalonia.Controls;
using UndertaleModTool.Localization;

namespace UndertaleModToolAvalonia;

    public partial class MainWindow : Window
    {
        private bool _scriptsMenuPopulated;

        public MainWindow()
        {
            InitializeComponent();
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);

            // Must happen BEFORE the window is shown: Avalonia's in-window native menu renderer
            // snapshots each NativeMenuItem.Menu when it builds the displayed MenuItem (no change
            // tracking), so populating any later has no effect - clicking "Scripts" would do
            // nothing. DataContext is assigned right after construction, before Show().
            PopulateScriptsMenu();
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);

            // Fallback for the unlikely case that DataContext arrived only after opening.
            PopulateScriptsMenu();
        }

        private void PopulateScriptsMenu()
        {
            if (_scriptsMenuPopulated || DataContext is not MainViewModel vm)
                return;

            // The XAML declares the Scripts root as an empty placeholder - the only top-level
            // entry without a submenu or command (a dynamically built submenu cannot be expressed
            // in XAML, and NativeMenuItem supports no x:Name field). Fill it with the built-in
            // scripts. The WPF tool fills this menu lazily on submenu open; Avalonia's native
            // menu API has no per-open callback, so it is built once.
            if (NativeMenu.GetMenu(this) is { } root
                && root.Items.OfType<NativeMenuItem>().FirstOrDefault(i =>
                    i is not NativeMenuItemSeparator && i.Menu is null && i.Command is null) is { } scriptsItem)
            {
                scriptsItem.Menu = BuiltInScripts.BuildRootMenu(vm);
                _scriptsMenuPopulated = true;
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
