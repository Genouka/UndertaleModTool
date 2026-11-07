using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using UndertaleModTool.Localization;

namespace UndertaleModTool
{
    /// <summary>
    /// Editor for user-defined GML code snippets.
    /// </summary>
    public partial class SnippetsWindow : Window
    {
        private readonly List<CodeSnippet> _snippets = new();
        private bool _loading;

        public SnippetsWindow()
        {
            InitializeComponent();

            if (Settings.Instance?.CodeSnippets is { } saved)
            {
                foreach (CodeSnippet snippet in saved)
                {
                    if (snippet is not null)
                        _snippets.Add(snippet.Clone());
                }
            }

            SnippetsList.ItemsSource = _snippets;
            if (_snippets.Count > 0)
                SnippetsList.SelectedIndex = 0;
            UpdateEditorState();
        }

        private CodeSnippet SelectedSnippet => SnippetsList.SelectedItem as CodeSnippet;

        private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsVisible || IsLoaded)
                return;

            if (Settings.Instance.EnableDarkMode)
                MainWindow.SetDarkTitleBarForWindow(this, true, false);
        }

        private void UpdateEditorState()
        {
            bool enabled = SelectedSnippet is not null;
            NameBox.IsEnabled = enabled;
            ShortcutBox.IsEnabled = enabled;
            BodyBox.IsEnabled = enabled;
        }

        private void SnippetsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            _loading = true;
            CodeSnippet snippet = SelectedSnippet;
            NameBox.Text = snippet?.Name ?? "";
            ShortcutBox.Text = snippet?.Shortcut ?? "";
            BodyBox.Text = snippet?.Body ?? "";
            _loading = false;
            UpdateEditorState();
        }

        private void NameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_loading || SelectedSnippet is null)
                return;
            SelectedSnippet.Name = NameBox.Text;
        }

        private void ShortcutBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_loading || SelectedSnippet is null)
                return;
            SelectedSnippet.Shortcut = ShortcutBox.Text;
        }

        private void BodyBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_loading || SelectedSnippet is null)
                return;
            SelectedSnippet.Body = BodyBox.Text;
        }

        private void ShortcutBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            {
                e.Handled = true;
                return;
            }

            if (key is Key.Back or Key.Delete)
            {
                ShortcutBox.Text = "";
                e.Handled = true;
                return;
            }

            // Let Tab/Escape work normally when no modifier is held.
            if ((key == Key.Tab || key == Key.Escape) && Keyboard.Modifiers == ModifierKeys.None)
                return;

            if (key == Key.None)
            {
                e.Handled = true;
                return;
            }

            ShortcutBox.Text = CodeSnippet.ModifiersToString(Keyboard.Modifiers) + CodeSnippet.KeyToString(key);
            e.Handled = true;
        }

        private void NewButton_Click(object sender, RoutedEventArgs e)
        {
            CodeSnippet snippet = new() { Name = GenerateDefaultName() };
            _snippets.Add(snippet);
            SnippetsList.Items.Refresh();
            SnippetsList.SelectedItem = snippet;
            NameBox.Focus();
            NameBox.SelectAll();
        }

        private string GenerateDefaultName()
        {
            string baseName = LocalizationSource.GetString("Sn_DefaultName");
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "snippet";

            string name = baseName;
            int suffix = 2;
            while (_snippets.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
                name = baseName + " " + suffix++;
            return name;
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            CodeSnippet snippet = SelectedSnippet;
            if (snippet is null)
                return;

            int index = SnippetsList.SelectedIndex;
            _snippets.Remove(snippet);
            SnippetsList.Items.Refresh();

            if (_snippets.Count > 0)
                SnippetsList.SelectedIndex = Math.Min(index, _snippets.Count - 1);
            else
                SnippetsList_SelectionChanged(null, null);
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (Settings.Instance is null)
                return;

            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> shortcuts = new(StringComparer.OrdinalIgnoreCase);

            foreach (CodeSnippet snippet in _snippets)
            {
                string name = (snippet.Name ?? "").Trim();
                if (name.Length == 0)
                {
                    ShowValidationError(LocalizationSource.GetString("Sn_Error_NameRequired"));
                    return;
                }
                if (!names.Add(name))
                {
                    ShowValidationError(string.Format(LocalizationSource.GetString("Sn_Error_DuplicateName"), name));
                    return;
                }
                snippet.Name = name;

                string shortcut = (snippet.Shortcut ?? "").Trim();
                if (shortcut.Length == 0)
                {
                    snippet.Shortcut = "";
                    continue;
                }

                if (!CodeSnippet.TryParseShortcut(shortcut, out ModifierKeys modifiers, out Key key))
                {
                    ShowValidationError(string.Format(LocalizationSource.GetString("Sn_Error_InvalidShortcut"), shortcut));
                    return;
                }
                if (!CodeSnippet.IsShortcutSafe(modifiers, key))
                {
                    ShowValidationError(LocalizationSource.GetString("Sn_Error_ModifierRequired"));
                    return;
                }
                if (CodeSnippet.IsReservedEditorShortcut(modifiers, key))
                {
                    ShowValidationError(string.Format(LocalizationSource.GetString("Sn_Error_Conflict"), shortcut));
                    return;
                }

                string canonical = CodeSnippet.ModifiersToString(modifiers) + CodeSnippet.KeyToString(key);
                if (!shortcuts.Add(canonical))
                {
                    ShowValidationError(string.Format(LocalizationSource.GetString("Sn_Error_DuplicateShortcut"), canonical));
                    return;
                }
                snippet.Shortcut = canonical;
            }

            Settings.Instance.CodeSnippets = _snippets.Select(s => s.Clone()).ToList();
            Settings.Save();
            this.ShowMessage(LocalizationSource.GetString("Sn_Saved"), LocalizationSource.GetString("Sn_Title"));
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void ShowValidationError(string message)
        {
            this.ShowError(message, LocalizationSource.GetString("Sn_Title"));
        }
    }
}