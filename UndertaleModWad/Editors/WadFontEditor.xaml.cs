using System.Windows.Controls;

namespace UndertaleModWad
{
    /// <summary>
    /// Editable editor for one <see cref="UndertaleModWad.WadFontEntry"/> (FONT chunk
    /// entry): name and style fields are editable, the glyph data remains read-only.
    /// </summary>
    public partial class WadFontEditor : DataUserControl
    {
        public WadFontEditor()
        {
            InitializeComponent();
        }
    }
}