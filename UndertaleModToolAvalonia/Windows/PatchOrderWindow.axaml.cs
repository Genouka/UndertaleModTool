using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using UndertaleModLib.Diff;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Lets the user review and adjust the order in which several diffs are applied.
/// The list starts in the default order (MD5 chain, then alphabetical for unmatched diffs).
/// </summary>
public partial class PatchOrderWindow : Window
{
    private readonly List<PatchChainEntry> _initialOrder;

    /// <summary>File names in the currently displayed order.</summary>
    public ObservableCollection<PatchChainEntry> Items { get; }

    public PatchOrderWindow()
    {
        InitializeComponent();
        Items = [];
        DataContext = this;
    }

    public PatchOrderWindow(IReadOnlyList<PatchChainEntry> entries) : this()
    {
        _initialOrder = DataFilePatchChain.SortByChain(entries);
        foreach (PatchChainEntry entry in _initialOrder)
        {
            Items.Add(entry);
        }

        Loaded += (_, _) =>
        {
            if (Items.Count > 0)
            {
                OrderList.SelectedIndex = 0;
            }
        };
    }

    private void MoveUp_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        int index = OrderList.SelectedIndex;
        if (index <= 0)
        {
            return;
        }

        (Items[index - 1], Items[index]) = (Items[index], Items[index - 1]);
        OrderList.SelectedIndex = index - 1;
    }

    private void MoveDown_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        int index = OrderList.SelectedIndex;
        if (index < 0 || index >= Items.Count - 1)
        {
            return;
        }

        (Items[index + 1], Items[index]) = (Items[index], Items[index + 1]);
        OrderList.SelectedIndex = index + 1;
    }

    private void AutoSort_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
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

    private void Ok_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close(Items.Select(entry => entry.Path).ToList());
    }

    private void Cancel_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close(null);
    }
}
