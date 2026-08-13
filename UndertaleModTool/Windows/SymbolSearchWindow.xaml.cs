using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModTool.Editors;

namespace UndertaleModTool.Windows
{
    /// <summary>
    /// Describes a single symbol that can be navigated to from the symbol search window.
    /// </summary>
    public class SymbolEntry
    {
        public string Name { get; set; }
        public string Kind { get; set; }
        public UndertaleNamedResource Resource { get; set; }
    }

    /// <summary>
    /// Interaction logic for the find symbol (Ctrl+T) window.
    /// </summary>
    public partial class SymbolSearchWindow : Window
    {
        private readonly UndertaleData _data;
        private readonly List<SymbolEntry> _allSymbols = new();
        private readonly List<SymbolEntry> _filteredSymbols = new();

        /// <summary>
        /// Resource the user chose to navigate to, or null if they only picked a name/builtin.
        /// </summary>
        public UndertaleNamedResource SelectedResource { get; private set; }

        public SymbolSearchWindow(UndertaleData data)
        {
            InitializeComponent();
            _data = data;

            BuildSymbolList();
            ApplyFilter();
        }

        private void BuildSymbolList()
        {
            if (_data is null)
                return;

            // Game functions (from the game's function list / global functions)
            if (_data.GlobalFunctions is not null)
            {
                foreach (UndertaleFunction func in _data.Functions)
                {
                    if (func?.Name?.Content is string name && !name.StartsWith("gml_Script_", StringComparison.Ordinal))
                        _allSymbols.Add(new SymbolEntry { Name = name, Kind = "function", Resource = func });
                }
            }

            // Scripts (GMS < 2.3 style scripts; for 2.3 they are backing assets for functions)
            if (_data.Scripts is not null)
            {
                foreach (UndertaleScript script in _data.Scripts)
                {
                    if (script?.Name?.Content is string name)
                        _allSymbols.Add(new SymbolEntry { Name = name.Replace("gml_Script_", "", StringComparison.Ordinal), Kind = "script", Resource = script });
                }
            }

            // All assets that can appear in code
            AddAssets(_data.GameObjects, "object");
            AddAssets(_data.Sprites, "sprite");
            AddAssets(_data.Sounds, "sound");
            AddAssets(_data.Backgrounds, "background");
            AddAssets(_data.Paths, "path");
            AddAssets(_data.Rooms, "room");
            AddAssets(_data.Fonts, "font");
            AddAssets(_data.Timelines, "timeline");
            AddAssets(_data.Shaders, "shader");
            AddAssets(_data.AnimationCurves, "animcurve");
            AddAssets(_data.Sequences, "sequence");
            AddAssets(_data.ParticleSystems, "particlesystem");

            // Builtin functions / variables / constants (not navigable, but useful to find)
            if (_data.BuiltinList is not null)
            {
                if (_data.BuiltinList.Functions is not null)
                    foreach (string name in _data.BuiltinList.Functions.Keys)
                        _allSymbols.Add(new SymbolEntry { Name = name, Kind = "builtin" });
                if (_data.BuiltinList.Constants is not null)
                    foreach (string name in _data.BuiltinList.Constants.Keys)
                        _allSymbols.Add(new SymbolEntry { Name = name, Kind = "constant" });
                if (_data.BuiltinList.GlobalVars is not null)
                    foreach (string name in _data.BuiltinList.GlobalVars.Keys)
                        _allSymbols.Add(new SymbolEntry { Name = name, Kind = "global" });
                if (_data.BuiltinList.InstanceVars is not null)
                    foreach (string name in _data.BuiltinList.InstanceVars.Keys)
                        _allSymbols.Add(new SymbolEntry { Name = name, Kind = "instance" });
            }

            // GmlSpec supplementary functions
            GmlSpecLoader.EnsureLoaded();
            bool zh = UndertaleModTool.Settings.Instance?.Language?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true;
            foreach (var kvp in zh ? GmlSpecLoader.GetAllFunctionsZh() : GmlSpecLoader.GetAllFunctionsEn())
                _allSymbols.Add(new SymbolEntry { Name = kvp.Key, Kind = "builtin" });

            _allSymbols.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        }

        private void AddAssets<T>(IList<T> list, string kind) where T : UndertaleNamedResource
        {
            if (list is null)
                return;
            foreach (T asset in list)
            {
                if (asset?.Name?.Content is string name)
                    _allSymbols.Add(new SymbolEntry { Name = name, Kind = kind, Resource = asset });
            }
        }

        private void ApplyFilter()
        {
            _filteredSymbols.Clear();
            string filter = SearchBox?.Text?.Trim() ?? "";
            if (filter.Length == 0)
            {
                _filteredSymbols.AddRange(_allSymbols);
            }
            else
            {
                foreach (SymbolEntry entry in _allSymbols)
                {
                    if (entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        _filteredSymbols.Add(entry);
                }
            }

            if (ResultsList is not null)
            {
                ResultsList.ItemsSource = null;
                ResultsList.ItemsSource = _filteredSymbols;
            }
        }

        private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible && SearchBox != null)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
            }
        }

        private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            ApplyFilter();
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ChooseSelection();
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                if (ResultsList.Items.Count > 0)
                {
                    ResultsList.Focus();
                    ResultsList.SelectedIndex = 0;
                }
                e.Handled = true;
            }
        }

        private void ResultsList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ChooseSelection();
                e.Handled = true;
            }
        }

        private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ResultsList.SelectedItem is SymbolEntry)
                ChooseSelection();
        }

        private void ChooseSelection()
        {
            if (ResultsList.SelectedItem is SymbolEntry entry)
            {
                SelectedResource = entry.Resource;
                DialogResult = true;
                Close();
            }
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            ChooseSelection();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}