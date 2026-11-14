using System;
using Avalonia.Controls;

namespace UndertaleModToolAvalonia;

public partial class DataFileCompareWindow : Window
{
    public DataFileCompareWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is DataFileCompareViewModel viewModel)
            _ = viewModel.StartAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        (DataContext as IDisposable)?.Dispose();
    }
}
