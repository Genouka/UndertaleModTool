using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace UndertaleModTool.Debugging
{
    public partial class DebugOutputWindow : Window
    {
        public DebugOutputWindow()
        {
            InitializeComponent();
            LogList.ItemsSource = DebugSessionService.Instance?.LogLines;
            if (DebugSessionService.Instance is { } service)
            {
                service.LogAdded += OnLogAdded;
                Closed += (_, _) => service.LogAdded -= OnLogAdded;
            }
        }

        private void OnLogAdded(string _)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (LogList.Items.Count > 0)
                    LogList.ScrollIntoView(LogList.Items[^1]);
            });
        }

        private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsVisible || IsLoaded)
                return;
            if (Settings.Instance.EnableDarkMode)
                MainWindow.SetDarkTitleBarForWindow(this, true, false);
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            DebugSessionService.Instance?.LogLines.Clear();
        }

        private void LogList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject source)
                return;
            if (ItemsControl.ContainerFromElement(LogList, source) is not ListBoxItem item)
                return;

            if (!item.IsSelected)
            {
                LogList.SelectedItems.Clear();
                item.IsSelected = true;
            }
        }

        private void CopySelected_Click(object sender, RoutedEventArgs e)
        {
            string[] lines = LogList.SelectedItems.Cast<object>().Select(item => item?.ToString() ?? "").ToArray();
            CopyLines(lines);
        }

        private void CopyAll_Click(object sender, RoutedEventArgs e)
        {
            string[] lines = LogList.Items.Cast<object>().Select(item => item?.ToString() ?? "").ToArray();
            CopyLines(lines);
        }

        private static void CopyLines(string[] lines)
        {
            if (lines.Length == 0)
                return;

            StringBuilder builder = new();
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                    builder.AppendLine();
                builder.Append(lines[i]);
            }

            try
            {
                Clipboard.SetText(builder.ToString());
            }
            catch (Exception ex)
            {
                DebugSessionService.Instance?.Log("Could not copy output: " + ex.Message);
            }
        }
    }
}