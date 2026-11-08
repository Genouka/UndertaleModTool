using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using UndertaleModRunner;

namespace UndertaleModTool.Debugging
{
    public partial class DebugInstancesWindow : Window
    {
        private readonly ObservableCollection<InstanceInfo> _instances = new();
        private readonly ObservableCollection<VariableRow> _variables = new();

        public DebugInstancesWindow()
        {
            InitializeComponent();
            InstanceList.ItemsSource = _instances;
            VariableList.ItemsSource = _variables;
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
                int selectedId = (InstanceList.SelectedItem as InstanceInfo)?.Id ?? -1;
                _instances.Clear();
                foreach (InstanceInfo instance in DebugSessionService.Instance?.GetInstances() ?? Array.Empty<InstanceInfo>())
                    _instances.Add(instance);

                InstanceList.SelectedItem = null;
                foreach (InstanceInfo instance in _instances)
                {
                    if (instance.Id == selectedId)
                    {
                        InstanceList.SelectedItem = instance;
                        break;
                    }
                }
            });
        }

        private void InstanceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _variables.Clear();
            if (InstanceList.SelectedItem is not InstanceInfo instance)
                return;

            foreach (InstanceVariableInfo variable in instance.Variables)
            {
                _variables.Add(new VariableRow
                {
                    Source = "instance",
                    Name = string.IsNullOrEmpty(variable.Name) ? $"var_{variable.VarId}" : variable.Name,
                    Value = variable.Value?.ToString() ?? "undefined",
                    VarId = variable.VarId
                });
            }
        }
    }
}
