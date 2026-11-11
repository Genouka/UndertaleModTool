using System;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using UndertaleModRunner;

namespace UndertaleModToolAvalonia;

public partial class DebugVariablesWindow : Window
{
    readonly ObservableCollection<VariableRow> rows = new();

    public DebugVariablesWindow()
    {
        InitializeComponent();
        VariableGrid.ItemsSource = rows;

        ButterscotchDebugService service = ButterscotchDebugService.Instance;
        service.Updated += Refresh;
        Closed += (_, _) => service.Updated -= Refresh;
        Refresh();
    }

    void RefreshButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Refresh();

    async void VariableGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (VariableGrid.SelectedItem is not VariableRow row)
            return;

        e.Handled = true;
        await EditValue(row);
    }

    async System.Threading.Tasks.Task EditValue(VariableRow row)
    {
        ButterscotchDebugService service = ButterscotchDebugService.Instance;
        if (!row.CanEdit)
        {
            service.Log($"Editing {row.ValueInfo?.Type ?? "unknown"} values is not supported.");
            return;
        }

        DebugEditValueWindow dialog = new(row, service);
        if (await WindowHost.ShowDialog<bool>(this, dialog))
        {
            service.RefreshState();
            Refresh();
        }
    }

    void Refresh()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible && rows.Count > 0)
                return;

            rows.Clear();
            ButterscotchDebugService service = ButterscotchDebugService.Instance;

            StackFrameInfo? frame = service.State?.Stop?.Frame;
            if (frame is not null)
            {
                foreach (LocalInfo local in frame.Locals)
                {
                    rows.Add(new VariableRow
                    {
                        Source = "local",
                        Name = string.IsNullOrEmpty(local.Name) ? $"var_{local.VarId}" : local.Name,
                        EditName = local.Name,
                        Value = DebugVariableEditing.FormatValue(local.Value),
                        ValueInfo = local.Value,
                        VarId = local.VarId,
                        Slot = local.Slot,
                        FrameIndex = 0,
                    });
                }
                foreach (ArgumentInfo arg in frame.Args)
                {
                    rows.Add(new VariableRow
                    {
                        Source = "arg",
                        Name = $"argument[{arg.Slot}]",
                        EditName = $"argument[{arg.Slot}]",
                        Value = DebugVariableEditing.FormatValue(arg.Value),
                        ValueInfo = arg.Value,
                        VarId = -1,
                        Slot = arg.Slot,
                        FrameIndex = 0,
                    });
                }
            }

            foreach (InstanceVariableInfo global in service.GetGlobals())
            {
                rows.Add(new VariableRow
                {
                    Source = "global",
                    Name = string.IsNullOrEmpty(global.Name) ? $"var_{global.VarId}" : global.Name,
                    EditName = global.Name,
                    Value = DebugVariableEditing.FormatValue(global.Value),
                    ValueInfo = global.Value,
                    VarId = global.VarId,
                    Slot = 0,
                    FrameIndex = 0,
                });
            }
        });
    }
}