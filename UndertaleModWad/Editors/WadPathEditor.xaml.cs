using System.Windows.Controls;

namespace UndertaleModWad
{
    /// <summary>
    /// Editor for one <see cref="UndertaleModWad.WadPathEntry"/> (PATH chunk entry):
    /// path kind, closed flag, precision and point count. Read-only.
    /// </summary>
    public partial class WadPathEditor : DataUserControl
    {
        public WadPathEditor()
        {
            InitializeComponent();
        }
    }
}