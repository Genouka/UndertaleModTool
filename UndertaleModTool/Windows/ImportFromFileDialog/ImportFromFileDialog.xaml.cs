using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using UndertaleModLib.Project;
using UndertaleModTool.Localization;

namespace UndertaleModTool.Windows
{
    public partial class ImportFromFileDialog : Window
    {
        private readonly IResourceImportSource _source;
        private readonly string _fileFilter;
        private ObservableCollection<ResourceDisplayItem> _availableItems = new();
        private ObservableCollection<ResourceDisplayItem> _selectedItems = new();
        private readonly ListCollectionView _availableView;
        private readonly ListCollectionView _selectedView;
        private IReadOnlyList<ImportableResource> _allResources;

        public bool ImportCompleted { get; private set; }
        public CrossFileImportResult ImportResult { get; private set; }

        private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsVisible || IsLoaded)
                return;

            if (Settings.Instance.EnableDarkMode)
                MainWindow.SetDarkTitleBarForWindow(this, true, false);
        }

        public ImportFromFileDialog(IResourceImportSource source, string fileFilter)
        {
            InitializeComponent();

            _source = source ?? throw new ArgumentNullException(nameof(source));
            _fileFilter = fileFilter;
            _availableView = new ListCollectionView(_availableItems);
            _selectedView = new ListCollectionView(_selectedItems);
            AvailableList.ItemsSource = _availableView;
            SelectedList.ItemsSource = _selectedView;
            ImportButton.IsEnabled = false;
        }

        private void AvailableSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter(_availableView, AvailableSearchBox.Text);
        }

        private void SelectedSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter(_selectedView, SelectedSearchBox.Text);
        }

        private static void ApplyFilter(ICollectionView view, string filterText)
        {
            string normalized = filterText?.Trim();
            view.Filter = string.IsNullOrEmpty(normalized)
                ? null
                : (obj) => obj is ResourceDisplayItem item &&
                           item.DisplayName.IndexOf(normalized, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private async void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new()
            {
                Filter = _fileFilter
            };

            if (dlg.ShowDialog() != true)
                return;

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

            loader.Show();
            Task loadTask = Task.Run(() =>
            {
                try
                {
                    _source.LoadFile(filePath);
                    loadSuccess = true;
                }
                catch (Exception ex)
                {
                    loadError = ex;
                }
            });

            try
            {
                await loadTask;
            }
            finally
            {
                loader.TryClose();
            }

            if (!loadSuccess)
            {
                this.ShowError(string.Format(LocalizationSource.GetString("ImportFile_LoadError"), loadError?.Message));
                return;
            }

            PopulateResourceLists();
            ImportButton.IsEnabled = _selectedItems.Count > 0;
        }

        private void PopulateResourceLists()
        {
            AvailableSearchBox.Text = "";
            SelectedSearchBox.Text = "";
            _availableItems.Clear();
            _selectedItems.Clear();

            try
            {
                _allResources = _source.EnumerateResources();
            }
            catch (Exception ex)
            {
                this.ShowError(string.Format(LocalizationSource.GetString("ImportFile_EnumerateError"), ex.Message));
                return;
            }

            foreach (ImportableResource res in _allResources)
            {
                _availableItems.Add(new ResourceDisplayItem(res));
            }

            IReadOnlyList<string> notices = _source.SourceNotices;
            SourceNoticesText.Text = notices.Count > 0 ? string.Join(Environment.NewLine, notices) : string.Empty;
            SourceNoticesText.Visibility = notices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            // Only move the items currently visible through the search filter
            List<ResourceDisplayItem> toMove = _availableView.Cast<ResourceDisplayItem>().ToList();
            foreach (ResourceDisplayItem item in toMove)
            {
                _availableItems.Remove(item);
                _selectedItems.Add(item);
            }
            ImportButton.IsEnabled = _selectedItems.Count > 0;
        }

        private void DeselectAllButton_Click(object sender, RoutedEventArgs e)
        {
            // Only move the items currently visible through the search filter
            List<ResourceDisplayItem> toMove = _selectedView.Cast<ResourceDisplayItem>().ToList();
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

            List<ImportableResource> selectedResources = _selectedItems.Select(item => item.Resource).ToList();

            LoaderDialog loader = new(
                LocalizationSource.GetString("ImportFile_Importing"),
                LocalizationSource.GetString("ImportFile_ImportingPleaseWait"))
            {
                PreventClose = true,
                Owner = this
            };

            CrossFileImportResult result = null;
            Exception importError = null;

            loader.Show();
            Task importTask = Task.Run(() =>
            {
                try
                {
                    result = _source.ImportResources(selectedResources, resolution, importDeps);
                }
                catch (Exception ex)
                {
                    importError = ex;
                }
            });

            try
            {
                await importTask;
            }
            finally
            {
                loader.TryClose();
            }

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
            _source?.Dispose();
            base.OnClosing(e);
        }
    }

    public sealed class ResourceDisplayItem
    {
        public ImportableResource Resource { get; }
        public string DisplayName { get; }
        public string FullName { get; }

        public ResourceDisplayItem(ImportableResource resource)
        {
            Resource = resource;

            string conflictMarker = resource.ExistsInTarget ? " ⚠" : "";

            DisplayName = $"{resource.TypeName}: {resource.Name}{conflictMarker}";
            FullName = resource.ExistsInTarget
                ? $"{resource.TypeName}: {resource.Name} ({LocalizationSource.GetString("ImportFile_ExistsInTarget")})"
                : $"{resource.TypeName}: {resource.Name}";
        }

        public override string ToString() => DisplayName;
    }
}
