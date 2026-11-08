using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using UndertaleModRunner;

namespace UndertaleModTool.Debugging
{
    public partial class DebugCallStackWindow : Window
    {
        private readonly ObservableCollection<StackFrameInfo> _frames = new();

        public DebugCallStackWindow()
        {
            InitializeComponent();
            FrameList.ItemsSource = _frames;
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

        private void Refresh()
        {
            Dispatcher.BeginInvoke(() =>
            {
                _frames.Clear();
                foreach (StackFrameInfo frame in DebugSessionService.Instance?.GetCallStack() ?? Array.Empty<StackFrameInfo>())
                    _frames.Add(frame);
            });
        }

        private void FrameList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FrameList.SelectedItem is not StackFrameInfo frame)
                return;

            MainWindow mainWindow = DebugSessionService.Instance?.MainWindow;
            if (mainWindow is null)
                return;

            e.Handled = true;
#pragma warning disable CA1416 // The WPF project targets Windows.
            mainWindow.Activate();
            mainWindow.OpenCodeEntry(frame.CodeIndex, UndertaleCodeEditor.CodeEditorTab.Disassembly);
#pragma warning restore CA1416
        }
    }
}
