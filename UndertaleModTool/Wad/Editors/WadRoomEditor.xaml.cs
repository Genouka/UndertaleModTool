using System.Windows.Controls;

namespace UndertaleModTool
{
    /// <summary>
    /// Editor for one <see cref="UndertaleModLib.Wad.WadRoomEntry"/> (ROOM chunk entry):
    /// room size, flags, creation-code ref and the view/instance/layer/component lists.
    /// Read-only.
    /// </summary>
    public partial class WadRoomEditor : DataUserControl
    {
        public WadRoomEditor()
        {
            InitializeComponent();
        }
    }
}