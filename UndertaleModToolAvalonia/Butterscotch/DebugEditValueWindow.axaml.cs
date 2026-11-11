using Avalonia.Controls;
using Avalonia.Interactivity;

namespace UndertaleModToolAvalonia;

public partial class DebugEditValueWindow : Window
{
    readonly VariableRow? row;
    readonly ButterscotchDebugService? service;

    public DebugEditValueWindow()
    {
        InitializeComponent();
    }

    public DebugEditValueWindow(VariableRow row, ButterscotchDebugService service)
        : this()
    {
        this.row = row;
        this.service = service;
        ValueBox.Text = DebugVariableEditing.FormatValue(row.ValueInfo);

        Opened += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    void OkButton_Click(object? sender, RoutedEventArgs e)
    {
        if (row is null || service is null)
        {
            Close(false);
            return;
        }

        if (!DebugVariableEditing.Commit(service, row, ValueBox.Text, out string? error))
        {
            ErrorText.Text = error ?? "";
            ErrorText.IsVisible = true;
            ValueBox.Focus();
            ValueBox.SelectAll();
            return;
        }

        Close(true);
    }

    void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(false);
}