using System.Windows.Controls;

namespace UndertaleModTool
{
    /// <summary>
    /// Editor for one <see cref="UndertaleModLib.Wad.WadPathEntry"/> (PATH chunk entry):
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