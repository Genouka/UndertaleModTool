using System.Windows.Controls;

namespace UndertaleModWad
{
    /// <summary>
    /// Editor for one <see cref="UndertaleModWad.WadBgndEntry"/> (BGND chunk entry):
    /// tileset header fields (tile grid, columns, frames, sprite link) and tail sizes.
    /// Read-only.
    /// </summary>
    public partial class WadBackgroundEditor : DataUserControl
    {
        public WadBackgroundEditor()
        {
            InitializeComponent();
        }
    }
}