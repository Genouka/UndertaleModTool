using System.Windows.Controls;

namespace UndertaleModWad
{
    /// <summary>
    /// Editor for one <see cref="UndertaleModWad.WadSeqnEntry"/> (SEQN chunk entry):
    /// sequence header (playback, length, origin, volume, dimensions) and the verified
    /// parse-end offset used by the wad parser self-checks. Read-only.
    /// </summary>
    public partial class WadSequenceEditor : DataUserControl
    {
        public WadSequenceEditor()
        {
            InitializeComponent();
        }
    }
}