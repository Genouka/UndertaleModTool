using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using UndertaleModLib.Project;
using UndertaleModTool.Localization;

namespace UndertaleModTool.Windows
{
    public partial class ImportFromFileDialog : Window
    {
        private static readonly MainWindow mainWindow = Application.Current.MainWindow as MainWindow;

        private CrossFileImporter _importer;
        private ObservableCollection<ResourceDisplayItem> _availableItems = new();
        private ObservableCollection<ResourceDisplayItem> _selectedItems = new();
        private List<ResourceInfo> _allResources;

        public bool ImportCompleted { get; private set; }
        public CrossFileImportResult ImportResult { get; private set; }

        private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsVisible || IsLoaded)
                return;

            if (Settings.Instance.EnableDarkMode)
                MainWindow.SetDarkTitleBarForWindow(this, true, false);
        }

        public ImportFromFileDialog(UndertaleModLib.UndertaleData targetData)
        {
            InitializeComponent();

            _importer = new CrossFileImporter(targetData, mainWindow.MainThreadAction);
            AvailableList.ItemsSource = _availableItems;
            SelectedList.ItemsSource = _selectedItems;
            ImportButton.IsEnabled = false;
        }

        private async void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new()
            {
                DefaultExt = "win",
                Filter = LocalizationSource.GetString("ImportFile_FileFilter") + "|*.win;*.unx;*.ios;*.droid|All files|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                SourceFilePathTextBox.Text = dlg.FileName;

                LoaderDialog loader = new(
                    LocalizationSource.GetString("ImportFile_Loading"),
                    LocalizationSource.GetString("ImportFile_LoadingPleaseWait"))
                {
                    PreventClose = true,
                    Owner = this
                };

                string filePath = dlg.FileName;
                bool loadSuccess = false;
                Exception loadError = null;

                loader.Owner = this;
                System.Threading.Tasks.Task loadTask = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        _importer.LoadSourceFile(filePath);
                        loadSuccess = true;
                    }
                    catch (Exception ex)
                    {
                        loadError = ex;
                    }
                });

                loader.ShowDialog();
                await loadTask;

                if (!loadSuccess)
                {
                    this.ShowError(string.Format(LocalizationSource.GetString("ImportFile_LoadError"), loadError?.Message));
                    return;
                }

                PopulateResourceLists();
                ImportButton.IsEnabled = _selectedItems.Count > 0;
            }
        }

        private void PopulateResourceLists()
        {
            _availableItems.Clear();
            _selectedItems.Clear();

            try
            {
                _allResources = _importer.GetAvailableResources();
            }
            catch (Exception ex)
            {
                this.ShowError(string.Format(LocalizationSource.GetString("ImportFile_EnumerateError"), ex.Message));
                return;
            }

            foreach (ResourceInfo res in _allResources)
            {
                _availableItems.Add(new ResourceDisplayItem(res));
            }
        }

        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            List<ResourceDisplayItem> toMove = _availableItems.ToList();
            foreach (ResourceDisplayItem item in toMove)
            {
                _availableItems.Remove(item);
                _selectedItems.Add(item);
            }
            ImportButton.IsEnabled = _selectedItems.Count > 0;
        }

        private void DeselectAllButton_Click(object sender, RoutedEventArgs e)
        {
            List<ResourceDisplayItem> toMove = _selectedItems.ToList();
            foreach (ResourceDisplayItem item in toMove)
            {
                _selectedItems.Remove(item);
                _availableItems.Add(item);
            }
            ImportButton.IsEnabled = _selectedItems.Count > 0;
        }

        private void AvailableList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (AvailableList.SelectedItem is ResourceDisplayItem item)
            {
                _availableItems.Remove(item);
                _selectedItems.Add(item);
                ImportButton.IsEnabled = _selectedItems.Count > 0;
            }
        }

        private void SelectedList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SelectedList.SelectedItem is ResourceDisplayItem item)
            {
                _selectedItems.Remove(item);
                _availableItems.Add(item);
                ImportButton.IsEnabled = _selectedItems.Count > 0;
            }
        }

        private void SelectedList_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }

        private void SelectedList_Drop(object sender, DragEventArgs e)
        {
            if (AvailableList.SelectedItems.Count > 0)
            {
                List<ResourceDisplayItem> toMove = AvailableList.SelectedItems.Cast<ResourceDisplayItem>().ToList();
                foreach (ResourceDisplayItem item in toMove)
                {
                    _availableItems.Remove(item);
                    _selectedItems.Add(item);
                }
                ImportButton.IsEnabled = _selectedItems.Count > 0;
            }
        }

        private async void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedItems.Count == 0)
                return;

            NameConflictResolution resolution = NameConflictResolution.Skip;
            if (OverwriteRadio.IsChecked == true)
                resolution = NameConflictResolution.Overwrite;
            else if (RenameRadio.IsChecked == true)
                resolution = NameConflictResolution.Rename;

            bool importDeps = ImportDependenciesCheck.IsChecked == true;

            List<ResourceInfo> selectedResources = _selectedItems.Select(item => item.ResourceInfo).ToList();

            LoaderDialog loader = new(
                LocalizationSource.GetString("ImportFile_Importing"),
                LocalizationSource.GetString("ImportFile_ImportingPleaseWait"))
            {
                PreventClose = true,
                Owner = this
            };

            CrossFileImportResult result = null;
            Exception importError = null;

            System.Threading.Tasks.Task importTask = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    result = _importer.ImportResources(selectedResources, resolution, importDeps);
                }
                catch (Exception ex)
                {
                    importError = ex;
                }
            });

            loader.ShowDialog();
            await importTask;

            if (importError is not null)
            {
                this.ShowError(string.Format(LocalizationSource.GetString("ImportFile_ImportError"), importError.Message));
                return;
            }

            ImportResult = result;
            ImportCompleted = true;

            string summary = string.Format(LocalizationSource.GetString("ImportFile_Summary"),
                result.ImportedCount, result.SkippedCount, result.OverwrittenCount);

            if (result.Warnings.Count > 0 || result.Errors.Count > 0)
            {
                summary += Environment.NewLine + Environment.NewLine;

                if (result.Warnings.Count > 0)
                {
                    summary += LocalizationSource.GetString("ImportFile_Warnings") + ":" + Environment.NewLine;
                    foreach (string w in result.Warnings.Take(10))
                        summary += $"  - {w}" + Environment.NewLine;
                    if (result.Warnings.Count > 10)
                        summary += string.Format(LocalizationSource.GetString("ImportFile_AndMore"), result.Warnings.Count - 10) + Environment.NewLine;
                }

                if (result.Errors.Count > 0)
                {
                    summary += LocalizationSource.GetString("ImportFile_Errors") + ":" + Environment.NewLine;
                    foreach (string err in result.Errors.Take(10))
                        summary += $"  - {err}" + Environment.NewLine;
                    if (result.Errors.Count > 10)
                        summary += string.Format(LocalizationSource.GetString("ImportFile_AndMore"), result.Errors.Count - 10) + Environment.NewLine;
                }
            }

            this.ShowMessage(summary);

            DialogResult = true;
            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            _importer?.Dispose();
            base.OnClosing(e);
        }
    }

    public sealed class ResourceDisplayItem
    {
        public ResourceInfo ResourceInfo { get; }
        public string DisplayName { get; }
        public string FullName { get; }

        public ResourceDisplayItem(ResourceInfo resourceInfo)
        {
            ResourceInfo = resourceInfo;

            string typeName = resourceInfo.AssetType?.ToInterfaceName() ?? resourceInfo.ResourceType.Name;
            string conflictMarker = resourceInfo.ExistsInTarget ? " ⚠" : "";

            DisplayName = $"{typeName}: {resourceInfo.Name}{conflictMarker}";
            FullName = resourceInfo.ExistsInTarget
                ? $"{typeName}: {resourceInfo.Name} ({LocalizationSource.GetString("ImportFile_ExistsInTarget")})"
                : $"{typeName}: {resourceInfo.Name}";
        }

        public override string ToString() => DisplayName;
    }
}
