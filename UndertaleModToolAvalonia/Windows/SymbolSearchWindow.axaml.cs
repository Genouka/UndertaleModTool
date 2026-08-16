using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using UndertaleModLib;
using UndertaleModLib.Models;

namespace UndertaleModToolAvalonia;

public partial class SymbolSearchWindow : Window
{
    public SymbolSearchWindow()
    {
        InitializeComponent();

        FilterTextBox.TextChanged += (_, _) =>
        {
            if (DataContext is SymbolSearchViewModel vm)
                vm.Filter = FilterTextBox.Text ?? "";
        };

        FilterTextBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Up)
            {
                e.Handled = true;
                MoveSelection(-1);
            }
            else if (e.Key == Key.Down)
            {
                e.Handled = true;
                MoveSelection(1);
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Complete();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };

        SymbolsListBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Complete();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    void MoveSelection(int delta)
    {
        if (DataContext is not SymbolSearchViewModel vm)
            return;

        var items = vm.Resources;
        if (items.Count == 0)
            return;

        int index = items.IndexOf(vm.SelectedResource);
        int newIndex = index + delta;
        if (newIndex < 0) newIndex = 0;
        if (newIndex >= items.Count) newIndex = items.Count - 1;

        vm.SelectedResource = items[newIndex];
        SymbolsListBox.ScrollIntoView(items[newIndex]);
    }

    void Complete()
    {
        if (DataContext is SymbolSearchViewModel vm && vm.SelectedResource is not null)
            Close();
    }

    private void ListBox_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (SymbolsListBox.SelectedItem is UndertaleNamedResource)
            Close();
    }
}