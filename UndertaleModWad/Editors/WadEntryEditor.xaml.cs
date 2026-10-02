using System.Windows.Controls;

namespace UndertaleModWad
{
    /// <summary>
    /// Generic entry editor: shows the identity (name/summary) and the reflected fields of
    /// any wad chunk entry. Bound to <see cref="UndertaleModWad.WadEntryViewModel"/>;
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