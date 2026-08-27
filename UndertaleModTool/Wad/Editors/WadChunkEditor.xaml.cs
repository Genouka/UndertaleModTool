using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UndertaleModTool.Wad;

namespace UndertaleModTool
{
    /// <summary>
    /// Shared chunk editor tab. One instance renders every chunk type; per-chunk details
    /// come from the chunk view model (<see cref="WadChunkViewModel"/>) — header info fields,
    /// typed entry rows, reflected properties and the raw-byte / STRG previews. Double-clicking
    /// an entry opens its own editor tab through the main window's standard hosting: entry
    /// models with a registered editor template (Wad*Editor) open directly, everything else
    /// opens through <see cref="WadEntryEditor"/> via the wrapper <see cref="WadEntryViewModel"/>.
    /// </summary>
    public partial class WadChunkEditor : DataUserControl
    {
        public WadChunkEditor()
        {
            InitializeComponent();
        }

        private void EntriesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (EntriesGrid?.SelectedItem is not WadEntryViewModel entry
                || Application.Current.MainWindow is not MainWindow main)
            {
                return;
            }

            string title = entry.Name ?? entry.Summary;
            if (entry.Payload is not null && main.HasEditorForAsset(entry.Payload))
            {
                // Dedicated per-entry editor (WadSpriteEditor, WadSoundEditor, …).
                main.OpenInTab(entry.Payload, true, title);
            }
            else
            {
                // Generic entry editor via the wrapper view model.
                main.OpenInTab(entry, true, title);
            }
        }
    }
}