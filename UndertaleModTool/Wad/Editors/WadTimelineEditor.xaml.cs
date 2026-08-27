using System.Windows.Controls;

namespace UndertaleModTool
{
    /// <summary>
    /// Editor for one <see cref="UndertaleModLib.Wad.WadTmlnEntry"/> (TMLN chunk entry):
    /// timeline name and its moment list (frame → script/event). Read-only.
    /// </summary>
    public partial class WadTimelineEditor : DataUserControl
    {
        public WadTimelineEditor()
        {
            InitializeComponent();
        }
    }
}