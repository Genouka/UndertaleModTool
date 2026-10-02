using System.Windows.Controls;

namespace UndertaleModWad
{
    /// <summary>
    /// Editor for one <see cref="UndertaleModWad.WadRoomEntry"/> (ROOM chunk entry):
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