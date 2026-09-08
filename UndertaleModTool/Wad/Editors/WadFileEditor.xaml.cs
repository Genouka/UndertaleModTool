using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UndertaleModLib.Wad;
using UndertaleModTool.Wad;

namespace UndertaleModTool
{
    /// <summary>
    /// Root WAD editor: file info, chunk table and an entry preview of the selected chunk.
    /// The main window hands the parsed <see cref="UndertaleWadFile"/> through the
    /// DataContext; this editor owns a <see cref="WadFileViewModel"/> and rebinds to it.
    /// Double-clicking a chunk opens its dedicated editor tab through the main window's
    /// standard tab hosting (<see cref="MainWindow.OpenInTab"/> + DataTemplates).
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
            if (DataContext is not UndertaleWadFile wad)
                return;
            _viewModel ??= new WadFileViewModel();
            // Carry the shared byte-level edit session so chunk raw_edit works from this
            // root editor too (the document's session is keyed to this exact wad instance).
            WadEditSession session = null;
            if (Application.Current.MainWindow is MainWindow main && ReferenceEquals(main.CurrentWadDocument?.Wad, wad))
                session = main.CurrentWadDocument.Session;
            _viewModel.Attach(wad, session);
            DataContext = _viewModel;
        }

        private void ChunkList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ChunkList?.SelectedItem is WadChunkViewModel chunk
                && Application.Current.MainWindow is MainWindow main)
            {
                main.OpenInTab(chunk, true, chunk.Title);
            }
        }
    }
}