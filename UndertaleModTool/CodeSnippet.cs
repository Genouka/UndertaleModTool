using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using System.Windows.Input;

namespace UndertaleModTool
{
    /// <summary>
    /// A user-defined GML code snippet that can be inserted from the code editor's
    /// context menu or with an optional keyboard shortcut.
    /// </summary>
    public class CodeSnippet : INotifyPropertyChanged
    {
        private string _name = "";
        private string _shortcut = "";
        private string _body = "";

        /// <summary>
        /// Display name of the snippet. Also used as its context-menu label.
        /// </summary>
        public string Name
        {
            get => _name;
            set
            {
                if (_name == value)
                    return;
                _name = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            }
        }

        /// <summary>
        /// Keyboard shortcut in a form such as <c>Ctrl+Alt+S</c>, or an empty string for no shortcut.
        /// </summary>
        public string Shortcut
        {
            get => _shortcut;
            set
            {
                if (_shortcut == value)
                    return;
                _shortcut = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Shortcut)));
            }
        }

        /// <summary>
        /// GML source text inserted by the snippet.
        /// </summary>
        public string Body
        {
            get => _body;
            set
            {
                if (_body == value)
                    return;
                _body = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Body)));
            }
        }

        /// <inheritdoc/>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Creates a copy of this snippet.
        /// </summary>
        public CodeSnippet Clone()
        {
            return new CodeSnippet
            {
                Name = Name,
                Shortcut = Shortcut,
                Body = Body
            };
        }

        // ---------------------------------------------------------------------
        // Shortcut formatting / parsing
        // ---------------------------------------------------------------------

        /// <summary>
        /// Formats modifier keys in the canonical order used by snippets: Ctrl, Shift, Alt, Windows.
        /// </summary>
        public static string ModifiersToString(ModifierKeys modifiers)
        {
            StringBuilder sb = new();
            if ((modifiers & ModifierKeys.Control) != 0)
                sb.Append("Ctrl+");
            if ((modifiers & ModifierKeys.Shift) != 0)
                sb.Append("Shift+");
            if ((modifiers & ModifierKeys.Alt) != 0)
                sb.Append("Alt+");
            if ((modifiers & ModifierKeys.Windows) != 0)
                sb.Append("Win+");
            return sb.ToString();
        }

        /// <summary>
        /// Formats a key for display in a shortcut string.
        /// </summary>
        public static string KeyToString(Key key)
        {
            if (key >= Key.D0 && key <= Key.D9)
                return ((int)(key - Key.D0)).ToString();
            if (key >= Key.NumPad0 && key <= Key.NumPad9)
                return "Num" + (int)(key - Key.NumPad0);

            return key switch
            {
                Key.Escape => "Esc",
                Key.Back => "Backspace",
                Key.Return => "Enter",
                Key.Space => "Space",
                _ => key.ToString()
            };
        }

        /// <summary>
        /// Tries to parse a shortcut string such as <c>Ctrl+Alt+S</c>.
        /// </summary>
        public static bool TryParseShortcut(string shortcut, out ModifierKeys modifiers, out Key key)
        {
            modifiers = ModifierKeys.None;
            key = Key.None;

            if (string.IsNullOrWhiteSpace(shortcut))
                return false;

            string[] parts = shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return false;

            bool hasKey = false;
            foreach (string rawPart in parts)
            {
                string part = rawPart.Trim();
                if (part.Length == 0)
                    return false;

                switch (part.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control":
                        if ((modifiers & ModifierKeys.Control) != 0)
                            return false;
                        modifiers |= ModifierKeys.Control;
                        continue;

                    case "shift":
                        if ((modifiers & ModifierKeys.Shift) != 0)
                            return false;
                        modifiers |= ModifierKeys.Shift;
                        continue;

                    case "alt":
                        if ((modifiers & ModifierKeys.Alt) != 0)
                            return false;
                        modifiers |= ModifierKeys.Alt;
                        continue;

                    case "win":
                    case "windows":
                        if ((modifiers & ModifierKeys.Windows) != 0)
                            return false;
                        modifiers |= ModifierKeys.Windows;
                        continue;
                }

                if (hasKey)
                    return false;
                if (!TryParseKey(part, out key))
                    return false;
                hasKey = true;
            }

            return hasKey && key != Key.None && !IsModifierKey(key);
        }

        private static bool TryParseKey(string token, out Key key)
        {
            key = Key.None;

            if (token.Length == 1 && char.IsDigit(token[0]))
            {
                key = Key.D0 + (token[0] - '0');
                return true;
            }

            if (token.Length == 4 &&
                token.StartsWith("Num", StringComparison.OrdinalIgnoreCase) &&
                char.IsDigit(token[3]))
            {
                key = Key.NumPad0 + (token[3] - '0');
                return true;
            }

            if (string.Equals(token, "Esc", StringComparison.OrdinalIgnoreCase))
            {
                key = Key.Escape;
                return true;
            }
            if (string.Equals(token, "Enter", StringComparison.OrdinalIgnoreCase))
            {
                key = Key.Return;
                return true;
            }
            if (string.Equals(token, "Backspace", StringComparison.OrdinalIgnoreCase))
            {
                key = Key.Back;
                return true;
            }

            return Enum.TryParse(token, true, out key) && key != Key.None && !IsModifierKey(key);
        }

        private static bool IsModifierKey(Key key)
        {
            return key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
        }

        /// <summary>
        /// Whether the shortcut is unlikely to interfere with normal typing: it must use Ctrl or Alt,
        /// unless the key is a function key.
        /// </summary>
        public static bool IsShortcutSafe(ModifierKeys modifiers, Key key)
        {
            if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0)
                return true;
            return key >= Key.F1 && key <= Key.F24;
        }

        // Editor and application shortcuts that snippet shortcuts must not shadow.
        private static readonly string[] ReservedShortcutStrings =
        {
            // Main window
            "Ctrl+N", "Ctrl+O", "Ctrl+S", "Ctrl+Q", "Ctrl+C", "Ctrl+P", "Ctrl+W",
            "Ctrl+Shift+W", "Ctrl+Shift+T", "Ctrl+Tab", "Ctrl+Shift+Tab", "Ctrl+Shift+F",
            "F5", "Alt+F5", "Shift+F5",
            // Code editor navigation/find
            "Ctrl+K", "Ctrl+F", "Ctrl+H", "Ctrl+T", "F12", "Shift+F12",
            // Standard text editing
            "Ctrl+Z", "Ctrl+Y", "Ctrl+Shift+Z", "Ctrl+A", "Ctrl+X", "Ctrl+V"
        };

        /// <summary>
        /// Checks whether the shortcut is already used by the editor or the main window.
        /// </summary>
        public static bool IsReservedEditorShortcut(ModifierKeys modifiers, Key key)
        {
            foreach (string reserved in ReservedShortcutStrings)
            {
                if (TryParseShortcut(reserved, out ModifierKeys reservedModifiers, out Key reservedKey) &&
                    reservedModifiers == modifiers && reservedKey == key)
                {
                    return true;
                }
            }
            return false;
        }

        // ---------------------------------------------------------------------
        // Insertion helpers
        // ---------------------------------------------------------------------

        /// <summary>
        /// Normalizes the snippet body's newlines and indents every line after the first with
        /// <paramref name="baseIndent"/>. The first line is inserted at the caret as-is.
        /// </summary>
        public static string ApplyIndent(string body, string baseIndent, string newline)
        {
            if (string.IsNullOrEmpty(body))
                return "";

            string normalized = body.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');

            StringBuilder sb = new(normalized.Length + baseIndent.Length * Math.Max(0, lines.Length - 1));
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(newline);
                    if (lines[i].Length > 0)
                        sb.Append(baseIndent);
                }
                sb.Append(lines[i]);
            }
            return sb.ToString();
        }
    }
}