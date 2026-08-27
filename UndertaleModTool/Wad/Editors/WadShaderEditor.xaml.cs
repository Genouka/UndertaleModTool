using System.Windows.Controls;

namespace UndertaleModTool
{
    /// <summary>
    /// Editor for one <see cref="UndertaleModLib.Wad.WadShdrEntry"/> (SHDR chunk entry):
    /// shader name and the vertex/fragment/metadata blob offsets plus sizes. Read-only.
    /// </summary>
    public partial class WadShaderEditor : DataUserControl
    {
        public WadShaderEditor()
        {
            InitializeComponent();
        }
    }
}