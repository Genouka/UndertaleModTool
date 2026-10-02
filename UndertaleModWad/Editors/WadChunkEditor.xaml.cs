using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace UndertaleModWad
{
    /// <summary>
    /// Shared chunk editor tab. One instance renders every chunk type; per-chunk details
    /// come from the chunk view model (<see cref="WadChunkViewModel"/>) — header info fields,
    /// typed entry rows, reflected properties and the raw-byte / STRG previews. Double-clicking
    /// an entry opens its own editor tab through the hosting <see cref="WadEditorWindow"/>:
    /// entry models with a dedicated editor open directly, everything else opens through
    /// <see cref="WadEntryEditor"/> via the wrapper <see cref="WadEntryViewModel"/>.
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
                || Window.GetWindow(this) is not WadEditorWindow host)
            {
                return;
            }

            string title = entry.Name ?? entry.Summary;
            if (entry.Payload is not null && WadEditorWindow.HasEditorForEntry(entry.Payload))
            {
                // Dedicated per-entry editor (WadSpriteEditor, WadSoundEditor, …).
                host.OpenInTab(entry.Payload, title);
            }
            else
            {
                // Generic entry editor via the wrapper view model.
                host.OpenInTab(entry, title);
            }
        }
    }
}
