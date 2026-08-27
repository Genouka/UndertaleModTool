using System.Windows.Controls;

namespace UndertaleModTool
{
    /// <summary>
    /// Editor for one <see cref="UndertaleModLib.Wad.WadTextureEntry"/> (TXTR chunk entry):
    /// texture id, dimensions, backend format code, blob size and the QOI import size.
    /// Read-only.
    /// </summary>
    public partial class WadTextureEditor : DataUserControl
    {
        public WadTextureEditor()
        {
            InitializeComponent();
        }
    }
}