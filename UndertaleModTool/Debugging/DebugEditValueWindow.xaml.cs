using System.Windows;

namespace UndertaleModTool.Debugging
{
    public partial class DebugEditValueWindow : Window
    {
        private readonly VariableRow _row;
        private readonly DebugSessionService _service;

        public DebugEditValueWindow(VariableRow row, DebugSessionService service)
        {
            InitializeComponent();
            _row = row;
            _service = service;
            ValueBox.Text = DebugVariableEditing.FormatValue(row.ValueInfo);
            Loaded += (_, _) =>
            {
                ValueBox.Focus();
                ValueBox.SelectAll();
            };
        }

        private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsVisible || IsLoaded)
                return;
            if (Settings.Instance.EnableDarkMode)
                MainWindow.SetDarkTitleBarForWindow(this, true, false);
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (!DebugVariableEditing.Commit(_service, _row, ValueBox.Text, out string error))
            {
                ErrorText.Text = error;
                ErrorText.Visibility = Visibility.Visible;
                ValueBox.Focus();
                ValueBox.SelectAll();
                return;
            }

            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
