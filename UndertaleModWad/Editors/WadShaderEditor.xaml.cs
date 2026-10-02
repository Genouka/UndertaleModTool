using System.Windows.Controls;

namespace UndertaleModWad
{
    /// <summary>
    /// Editor for one <see cref="UndertaleModWad.WadShdrEntry"/> (SHDR chunk entry):
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