using System.Windows;

namespace UndertaleModWad
{
    /// <summary>
    /// Minimal base control for the wad editors: prevents WPF binding errors (and
    /// unnecessary "DataContextChanged" firing) when switching to an incompatible data type.
    /// </summary>
    public partial class DataUserControl : System.Windows.Controls.UserControl
    {
        public DataUserControl()
        {
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            // prevent WPF binding errors (and unnecessary "DataContextChanged" firing) when switching to incompatible data type
            if (e.NewValue is null && e.Property == DataContextProperty)
                return;

            base.OnPropertyChanged(e);
        }
    }
}
