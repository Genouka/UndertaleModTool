using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace UndertaleModWad
{
    /// <summary>
    /// Root WAD editor: file info, chunk table and an entry preview of the selected chunk.
    /// The hosting <see cref="WadEditorWindow"/> hands in a <see cref="WadFileViewModel"/>
    /// (wired to the document's edit session); when a raw <see cref="UndertaleWadFile"/> is
    /// set instead, this editor builds a view model for it. Double-clicking a chunk opens
    /// its dedicated editor tab in the same window.
    /// </summary>
    public partial class WadFileEditor : DataUserControl
    {
        private WadFileViewModel _viewModel;

        public WadFileEditor()
        {
            InitializeComponent();
        }

        private void WadFileEditor_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // Already bound to a prepared view model (the usual path).
            if (DataContext is WadFileViewModel)
            {
                _viewModel = DataContext as WadFileViewModel;
                return;
            }

            if (DataContext is not UndertaleWadFile wad)
                return;

            WadEditSession session = (Window.GetWindow(this) as WadEditorWindow)?.Document?.Session;
            _viewModel ??= new WadFileViewModel();
            _viewModel.Attach(wad, session);
            DataContext = _viewModel;
        }

        private void ChunkList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ChunkList?.SelectedItem is WadChunkViewModel chunk
                && Window.GetWindow(this) is WadEditorWindow host)
            {
                host.OpenInTab(chunk, chunk.Title);
            }
        }
    }
}
