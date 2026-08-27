using System.Windows.Controls;

namespace UndertaleModTool
{
    /// <summary>
    /// Generic entry editor: shows the identity (name/summary) and the reflected fields of
    /// any wad chunk entry. Bound to <see cref="UndertaleModTool.Wad.WadEntryViewModel"/>;
    /// used as the fallback for chunk entries that have no dedicated Wad*Editor.
    /// </summary>
    public partial class WadEntryEditor : DataUserControl
    {
        public WadEntryEditor()
        {
            InitializeComponent();
        }
    }
}