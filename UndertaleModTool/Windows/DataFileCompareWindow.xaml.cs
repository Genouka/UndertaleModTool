using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace UndertaleModTool.Windows
{
    /// <summary>
    /// Interaction logic for DataFileCompareWindow.xaml
    /// </summary>
    public partial class DataFileCompareWindow : Window
    {
        private readonly DataFileCompareViewModel _viewModel;

        public DataFileCompareWindow()
        {
            InitializeComponent();
        }

        public DataFileCompareWindow(MainWindow mainWindow, string compareFilePath) : this()
        {
            _viewModel = new DataFileCompareViewModel(mainWindow, compareFilePath);
            DataContext = _viewModel;
        }

        private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsVisible || IsLoaded)
                return;

            if (Settings.Instance.EnableDarkMode)
                MainWindow.SetDarkTitleBarForWindow(this, true, false);
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await _viewModel.StartAsync();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            _viewModel?.Dispose();
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            _ = _viewModel.BrowseAsync(this);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.Cancel();
        }

        private void CategoriesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _viewModel?.OnSelectedCategoryChanged();
        }

        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            _viewModel?.SelectAll();
        }

        private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            _viewModel?.ClearSelection();
        }

        private async void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            await _viewModel.ExportStructuredDiffAsync(this);
        }

        private async void MergeButton_Click(object sender, RoutedEventArgs e)
        {
            await _viewModel.MergeSelectedAsync(this);
        }

        private void DeleteRemovedCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            _viewModel?.OnDeleteRemovedChanged();
        }

        private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_viewModel is not null && sender is TextBox textBox)
                _viewModel.FilterText = textBox.Text;
            _viewModel?.OnFilterTextChanged();
        }

        private void StatusFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _viewModel?.OnStatusFilterIndexChanged();
        }

        private void EntriesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _viewModel?.OnSelectedEntryChanged();
        }

        private void DetailsTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // The tab control also reports selection changes from the inner DataGrid; only the
            // tab switch is relevant, and the index binding already carried it over.
        }
    }
}
