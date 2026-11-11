using System;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using UndertaleModRunner;

namespace UndertaleModToolAvalonia;

public partial class DebugInstancesWindow : Window
{
    readonly ObservableCollection<InstanceInfo> instances = new();
    readonly ObservableCollection<VariableRow> variables = new();

    public DebugInstancesWindow()
    {
        InitializeComponent();
        InstanceGrid.ItemsSource = instances;
        VariableGrid.ItemsSource = variables;

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
            if (IsVisible && instances.Count == 0 && variables.Count == 0 && !ButterscotchDebugService.Instance.IsPaused)
                return;

            int selectedId = (InstanceGrid.SelectedItem as InstanceInfo)?.Id ?? -1;
            instances.Clear();
            foreach (InstanceInfo instance in ButterscotchDebugService.Instance.GetInstances())
                instances.Add(instance);

            InstanceInfo? reselect = null;
            foreach (InstanceInfo instance in instances)
            {
                if (instance.Id == selectedId)
                {
                    reselect = instance;
                    break;
                }
            }
            InstanceGrid.SelectedItem = reselect;
            if (reselect is null)
                variables.Clear();
        });
    }

    void InstanceGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        variables.Clear();
        if (InstanceGrid.SelectedItem is not InstanceInfo instance)
            return;

        foreach (InstanceVariableInfo variable in instance.Variables)
        {
            variables.Add(new VariableRow
            {
                Source = "instance",
                Name = string.IsNullOrEmpty(variable.Name) ? $"var_{variable.VarId}" : variable.Name,
                EditName = variable.Name,
                Value = DebugVariableEditing.FormatValue(variable.Value),
                ValueInfo = variable.Value,
                VarId = variable.VarId,
                InstanceId = instance.Id,
                Slot = 0,
                FrameIndex = 0,
            });
        }
    }
}