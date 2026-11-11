using Avalonia.Controls;
using Avalonia.Interactivity;

namespace UndertaleModToolAvalonia;

public partial class DebugBreakpointsWindow : Window
{
    public DebugBreakpointsWindow()
    {
        InitializeComponent();
        BreakpointList.ItemsSource = ButterscotchDebugService.Instance.Breakpoints;
    }

    void RemoveButton_Click(object? sender, RoutedEventArgs e)
    {
        ButterscotchDebugService service = ButterscotchDebugService.Instance;
        if (BreakpointList.SelectedItem is BreakpointEntry entry)
        {
            service.Breakpoints.Remove(entry);
            service.ApplyBreakpoints();
        }
    }

    void ClearButton_Click(object? sender, RoutedEventArgs e)
        => ButterscotchDebugService.Instance.ClearBreakpoints();
}