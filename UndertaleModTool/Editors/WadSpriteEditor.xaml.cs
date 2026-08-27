using System.Windows.Controls;

namespace UndertaleModTool
{
    /// <summary>
    /// Editor for one <see cref="UndertaleModLib.Wad.WadSprtEntry"/> (SPRT chunk entry):
    /// the 22-field sprite header, frame and tail sizes. Read-only — the wad is a runtime
    /// asset package.
    /// </summary>
    public partial class WadSpriteEditor : DataUserControl
    {
        public WadSpriteEditor()
        {
            InitializeComponent();
        }
    }
}