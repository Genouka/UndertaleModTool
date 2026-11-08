using System;
using System.Collections.ObjectModel;
using System.Windows;
using UndertaleModRunner;

namespace UndertaleModTool.Debugging
{
    public sealed class VariableRow
    {
        public string Source { get; set; }
        public string Name { get; set; }
        public string Value { get; set; }
        public int VarId { get; set; }
    }

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
                            Value = local.Value?.ToString() ?? "undefined",
                            VarId = local.VarId
                        });
                    }
                    foreach (ArgumentInfo arg in frame.Args)
                    {
                        _rows.Add(new VariableRow
                        {
                            Source = "arg",
                            Name = $"argument[{arg.Slot}]",
                            Value = arg.Value?.ToString() ?? "undefined",
                            VarId = -1
                        });
                    }
                }

                foreach (InstanceVariableInfo global in service.GetGlobals())
                {
                    _rows.Add(new VariableRow
                    {
                        Source = "global",
                        Name = string.IsNullOrEmpty(global.Name) ? $"var_{global.VarId}" : global.Name,
                        Value = global.Value?.ToString() ?? "undefined",
                        VarId = global.VarId
                    });
                }
            });
        }
    }
}
