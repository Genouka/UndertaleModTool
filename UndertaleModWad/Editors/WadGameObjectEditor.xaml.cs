using System.Windows;
using System.Windows.Controls;

namespace UndertaleModWad
{
    /// <summary>
    /// Editable editor for one <see cref="WadObjtEntry"/> (OBJT chunk entry), written in
    /// the same style as UndertaleGameObjectEditor: the raw parent index is presented as a
    /// pick list of object names resolved through the wad catalog ("references handled
    /// internally"); name/flag edits write to the model and are turned into byte patches by
    /// the wad edit session when the file is saved.
    /// </summary>
    public partial class WadGameObjectEditor : DataUserControl
    {
        private bool _syncing;

        public WadGameObjectEditor()
        {
            InitializeComponent();
        }

        /// <summary>Name catalog used to present parent indices as object names.</summary>
        public WadResourceCatalog Catalog { get; set; }

        private WadResourceCatalog ResolveCatalog()
            => Catalog ?? (Window.GetWindow(this) as WadEditorWindow)?.Document?.Catalog;

        private void WadGameObjectEditor_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (DataContext is not WadObjtEntry obj)
                return;
            if (ResolveCatalog()?.Objects is not { } objects)
                return;

            _syncing = true;
            try
            {
                // Parent references are indices into the object list; show names instead.
                ParentBox.ItemsSource = objects;
                ParentBox.SelectedIndex = obj.ParentIndex < (uint)objects.Count ? (int)obj.ParentIndex : -1;
            }
            finally
            {
                _syncing = false;
            }
        }

        private void ParentBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing)
                return;
            if (DataContext is WadObjtEntry obj && ParentBox.SelectedItem is WadResourceRef selected)
                obj.ParentIndex = (uint)selected.Index;
        }
    }
}
