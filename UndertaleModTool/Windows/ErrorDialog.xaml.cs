using System.Windows;
using UndertaleModTool.Localization;

namespace UndertaleModTool
{
    [PropertyChanged.AddINotifyPropertyChangedInterface]
    public partial class ErrorDialog : Window
    {
        public string DialogTitle { get; set; }
        public string Message { get; set; }
        public string ErrorText { get; set; }

        public ErrorDialog(string title, string message, string errorText)
        {
            DialogTitle = title ?? LocalizationSource.GetString("Common_Error");
            Message = message;
            ErrorText = errorText ?? message;

            InitializeComponent();
            DataContext = this;
        }

        private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsVisible || IsLoaded)
                return;

            if (Settings.Instance.EnableDarkMode)
                MainWindow.SetDarkTitleBarForWindow(this, true, false);
        }

        private void OKButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void CopyAllButton_Click(object sender, RoutedEventArgs e)
        {
            CopyAllToClipboard();
        }

        private void CopyMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (ErrorTextBox.SelectionLength > 0)
                Clipboard.SetText(ErrorTextBox.SelectedText);
        }

        private void CopyAllMenuItem_Click(object sender, RoutedEventArgs e)
        {
            CopyAllToClipboard();
        }

        private void CopyAllToClipboard()
        {
            if (!string.IsNullOrEmpty(ErrorText))
                Clipboard.SetText(ErrorText);
        }
    }
}
