using System.Windows.Controls;

namespace UndertaleModWad
{
    /// <summary>
    /// Editor for one <see cref="UndertaleModWad.WadTmlnEntry"/> (TMLN chunk entry):
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