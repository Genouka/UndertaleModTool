using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using UndertaleModTool.Debugging;

namespace UndertaleModTool.Debugging
{
    public partial class DebugBreakpointsWindow : Window
    {
        public DebugBreakpointsWindow()
        {
            InitializeComponent();
            BreakpointList.ItemsSource = DebugSessionService.Instance?.Breakpoints;
        }

        private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsVisible || IsLoaded)
                return;
            if (Settings.Instance.EnableDarkMode)
                MainWindow.SetDarkTitleBarForWindow(this, true, false);
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (BreakpointList.SelectedItem is BreakpointEntry entry)
            {
                DebugSessionService.Instance.Breakpoints.Remove(entry);
                DebugSessionService.Instance.ApplyBreakpoints();
            }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            DebugSessionService.Instance?.ClearBreakpoints();
        }
    }
}
