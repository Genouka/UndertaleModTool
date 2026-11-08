using System;
using System.Windows;
using UndertaleModTool.Debugging;

namespace UndertaleModTool.Debugging
{
    public partial class DebugOutputWindow : Window
    {
        public DebugOutputWindow()
        {
            InitializeComponent();
            LogList.ItemsSource = DebugSessionService.Instance?.LogLines;
            DebugSessionService.Instance.LogAdded += OnLogAdded;
            Closed += (_, _) => DebugSessionService.Instance.LogAdded -= OnLogAdded;
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
    }
}
