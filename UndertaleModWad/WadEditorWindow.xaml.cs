using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace UndertaleModWad
{
    /// <summary>
    /// Self-contained window of the WAD module: it owns the parsed file, its edit session
    /// and the resource catalog, and hosts every wad editor in its own tabs. The rest of
    /// the application reaches the module only through <see cref="ShowWad"/> (drag&amp;drop /
    /// file association) — no main-window integration, no shared DataTemplates.
    /// </summary>
    public partial class WadEditorWindow : Window
    {
        private WadDocument _document;
        private string _filePath;

        public WadEditorWindow()
        {
            InitializeComponent();
        }

        /// <summary>Document of the currently opened wad (null before a file is opened).</summary>
        public WadDocument Document => _document;

        /// <summary>
        /// Module entry point: opens <paramref name="path"/> in a wad editor window,
        /// reusing the window already showing that file (or an empty one) when possible.
        /// </summary>
        public static void ShowWad(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;

            WadEditorWindow target = Application.Current?.Windows.OfType<WadEditorWindow>()
                .FirstOrDefault(w => string.Equals(w._filePath, path, StringComparison.OrdinalIgnoreCase));

            target ??= Application.Current?.Windows.OfType<WadEditorWindow>().FirstOrDefault(w => w._document is null);
            target ??= new WadEditorWindow();

            if (!target.OpenWadFile(path))
                return;

            if (!target.IsVisible && Application.Current?.MainWindow is Window owner && !ReferenceEquals(owner, target))
                target.Owner = owner;

            target.Show();
            target.Activate();
        }

        /// <summary>True when <paramref name="payload"/> has a dedicated entry editor.</summary>
        public static bool HasEditorForEntry(object payload) => payload
            is WadSprtEntry or WadSondEntry or WadBgndEntry or WadRoomEntry or WadObjtEntry
            or WadSeqnEntry or WadShdrEntry or WadTextureEntry or WadPathEntry or WadTmlnEntry
            or WadFontEntry;

        /// <summary>Parses the file, replaces the current document and rebuilds the editor tabs.</summary>
        public bool OpenWadFile(string path)
        {
            UndertaleWadFile wad;
            try
            {
                wad = UndertaleWadFile.Load(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not open the WAD file:\n{ex.Message}", "WAD Editor",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            _document?.Dispose();
            _document = new WadDocument(wad);
            _filePath = path;

            EditorTabs.Items.Clear();
            AddRootTab(wad, Path.GetFileName(path));
            UpdateHeader();
            return true;
        }

        /// <summary>Opens <paramref name="content"/> in a new editor tab of this window.</summary>
        public void OpenInTab(object content, string title)
        {
            if (content is null)
                return;

            FrameworkElement editor = CreateEditor(content);
            var tab = new TabItem { Header = title ?? content.GetType().Name, Content = editor };
            EditorTabs.Items.Add(tab);
            EditorTabs.SelectedItem = tab;
        }

        // The root editor is wired to its view model here (with the document's edit session),
        // so it never needs to reach back into the hosting window for it.
        private void AddRootTab(UndertaleWadFile wad, string title)
        {
            var viewModel = new WadFileViewModel();
            viewModel.Attach(wad, _document?.Session);

            var tab = new TabItem { Header = title, Content = new WadFileEditor { DataContext = viewModel } };
            EditorTabs.Items.Add(tab);
            EditorTabs.SelectedItem = tab;
        }

        private FrameworkElement CreateEditor(object content)
        {
            FrameworkElement editor = content switch
            {
                UndertaleWadFile => new WadFileEditor(),
                WadChunkViewModel => new WadChunkEditor(),
                WadSprtEntry => new WadSpriteEditor(),
                WadSondEntry => new WadSoundEditor(),
                WadBgndEntry => new WadBackgroundEditor(),
                WadRoomEntry => new WadRoomEditor(),
                WadObjtEntry => new WadGameObjectEditor(),
                WadSeqnEntry => new WadSequenceEditor(),
                WadShdrEntry => new WadShaderEditor(),
                WadTextureEntry => new WadTextureEditor(),
                WadPathEntry => new WadPathEditor(),
                WadTmlnEntry => new WadTimelineEditor(),
                WadFontEntry => new WadFontEditor(),
                WadEntryViewModel => new WadEntryEditor(),
                _ => new TextBlock
                {
                    Text = "No editor is available for this entry.",
                    Margin = new Thickness(12),
                    Foreground = System.Windows.Media.Brushes.Gray,
                },
            };

            if (editor is WadGameObjectEditor gameObjectEditor)
                gameObjectEditor.Catalog = _document?.Catalog;

            editor.DataContext = content;
            return editor;
        }

        private void UpdateHeader()
        {
            PathText.Text = _document is null ? "" : Path.GetFileName(_filePath);
            InfoText.Text = _document is null
                ? "Drop a .wad file here or use Open…"
                : $"FORM {_document.Wad.FormLength:N0} bytes · {_document.Wad.ChunkHeaders.Count} chunks";
        }

        private void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Open GameMaker WAD file",
                Filter = "WAD files (*.wad)|*.wad|All files (*.*)|*.*",
            };
            if (dialog.ShowDialog(this) != true)
                return;
            OpenWadFile(dialog.FileName);
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_document is null)
                return;
            try
            {
                _document.Session.Save();
                MessageBox.Show(this, "WAD file saved. (A .bak backup of the previous file was kept next to it.)",
                    "WAD Editor", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not save the WAD file:\n{ex.Message}",
                    "WAD Editor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _document?.Dispose();
            _document = null;
        }
    }
}