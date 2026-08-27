using System.Windows.Controls;

namespace UndertaleModTool
{
    /// <summary>
    /// Editable editor for one <see cref="UndertaleModLib.Wad.WadFontEntry"/> (FONT chunk
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