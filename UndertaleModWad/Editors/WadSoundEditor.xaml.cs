using System.Windows.Controls;

namespace UndertaleModWad
{
    /// <summary>
    /// Editor for one <see cref="UndertaleModWad.WadSondEntry"/> (SOND chunk entry):
    /// name, audio format code (0/1 legacy, 100 streamed-new, 101 compressed-new,
    /// 103 uncompressed-new), audio file name and the remaining header fields. Read-only.
    /// </summary>
    public partial class WadSoundEditor : DataUserControl
    {
        public WadSoundEditor()
        {
            InitializeComponent();
        }
    }
}