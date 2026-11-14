using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using UndertaleModLib.Diff;

namespace UndertaleModTool.Windows
{
    /// <summary>
    /// Lets the user review and adjust the order in which several diffs are applied.
    /// The list starts in the default order (MD5 chain, then alphabetical for unmatched diffs).
    /// </summary>
    public partial class PatchOrderWindow : Window
    {
        private readonly List<PatchChainEntry> _initialOrder;

        /// <summary>Entries in the currently displayed order.</summary>
        public ObservableCollection<PatchChainEntry> Items { get; }

        /// <summary>The confirmed order, or <see langword="null"/> when cancelled.</summary>
        public IReadOnlyList<string>? OrderedPaths { get; private set; }

        public PatchOrderWindow(IReadOnlyList<PatchChainEntry> entries)
        {
            InitializeComponent();
            _initialOrder = DataFilePatchChain.SortByChain(entries);
            Items = new(_initialOrder);
            DataContext = this;
            Loaded += (_, _) =>
            {
                if (Items.Count > 0)
                {
                    OrderList.SelectedIndex = 0;
                }
            };
        }

        private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsVisible || IsLoaded)
                return;

            if (Settings.Instance.EnableDarkMode)
                MainWindow.SetDarkTitleBarForWindow(this, true, false);
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            int index = OrderList.SelectedIndex;
            if (index <= 0)
                return;

            (Items[index - 1], Items[index]) = (Items[index], Items[index - 1]);
            OrderList.SelectedIndex = index - 1;
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            int index = OrderList.SelectedIndex;
            if (index < 0 || index >= Items.Count - 1)
                return;

            (Items[index + 1], Items[index]) = (Items[index], Items[index + 1]);
            OrderList.SelectedIndex = index + 1;
        }

        private void AutoSort_Click(object sender, RoutedEventArgs e)
        {
            List<PatchChainEntry> sorted = DataFilePatchChain.SortByChain(_initialOrder);
            Items.Clear();
            foreach (PatchChainEntry entry in sorted)
            {
                Items.Add(entry);
            }
            if (Items.Count > 0)
            {
                OrderList.SelectedIndex = 0;
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            OrderedPaths = Items.Select(entry => entry.Path).ToList();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
