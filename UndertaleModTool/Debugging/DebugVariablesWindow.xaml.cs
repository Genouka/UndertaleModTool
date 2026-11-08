using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using UndertaleModRunner;

namespace UndertaleModTool.Debugging
{
    public partial class DebugVariablesWindow : Window
    {
        private readonly ObservableCollection<VariableRow> _rows = new();

        public DebugVariablesWindow()
        {
            InitializeComponent();
            VariableList.ItemsSource = _rows;
            if (DebugSessionService.Instance is { } service)
            {
                service.Updated += Refresh;
                Closed += (_, _) => service.Updated -= Refresh;
            }
            Refresh();
        }

        private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsVisible || IsLoaded)
                return;
            if (Settings.Instance.EnableDarkMode)
                MainWindow.SetDarkTitleBarForWindow(this, true, false);
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e) => Refresh();

        private void ValueCell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount < 2)
                return;
            if ((sender as FrameworkElement)?.DataContext is not VariableRow row)
                return;

            e.Handled = true;
            EditValue(row);
        }

        private void EditValue(VariableRow row)
        {
            DebugSessionService service = DebugSessionService.Instance;
            if (service is null)
                return;
            if (!row.CanEdit)
            {
                service.Log($"Editing {row.ValueInfo?.Type ?? "unknown"} values is not supported.");
                return;
            }

            DebugEditValueWindow dialog = new(row, service) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                service.RefreshState();
                Refresh();
            }
        }

        private void Refresh()
        {
            Dispatcher.BeginInvoke(() =>
            {
                _rows.Clear();
                DebugSessionService service = DebugSessionService.Instance;
                if (service is null)
                    return;

                StackFrameInfo frame = service.State?.Stop?.Frame;
                if (frame is not null)
                {
                    foreach (LocalInfo local in frame.Locals)
                    {
                        _rows.Add(new VariableRow
                        {
                            Source = "local",
                            Name = string.IsNullOrEmpty(local.Name) ? $"var_{local.VarId}" : local.Name,
                            EditName = local.Name,
                            Value = DebugVariableEditing.FormatValue(local.Value),
                            ValueInfo = local.Value,
                            VarId = local.VarId,
                            Slot = local.Slot,
                            FrameIndex = 0
                        });
                    }
                    foreach (ArgumentInfo arg in frame.Args)
                    {
                        _rows.Add(new VariableRow
                        {
                            Source = "arg",
                            Name = $"argument[{arg.Slot}]",
                            EditName = $"argument[{arg.Slot}]",
                            Value = DebugVariableEditing.FormatValue(arg.Value),
                            ValueInfo = arg.Value,
                            VarId = -1,
                            Slot = arg.Slot,
                            FrameIndex = 0
                        });
                    }
                }

                foreach (InstanceVariableInfo global in service.GetGlobals())
                {
                    _rows.Add(new VariableRow
                    {
                        Source = "global",
                        Name = string.IsNullOrEmpty(global.Name) ? $"var_{global.VarId}" : global.Name,
                        EditName = global.Name,
                        Value = DebugVariableEditing.FormatValue(global.Value),
                        ValueInfo = global.Value,
                        VarId = global.VarId,
                        Slot = 0,
                        FrameIndex = 0
                    });
                }
            });
        }
    }
}
