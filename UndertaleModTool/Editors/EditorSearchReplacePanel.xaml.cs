#pragma warning disable CA1416

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using UndertaleModTool.Localization;

namespace UndertaleModTool
{
    public partial class EditorSearchReplacePanel : UserControl
    {
        private TextArea _textArea;
        private SearchHighlightRenderer _renderer;
        private List<(int StartOffset, int EndOffset)> _results = new();

        public bool IsReplaceMode
        {
            get => ReplacePanel.Visibility == Visibility.Visible;
            set => ReplacePanel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        public EditorSearchReplacePanel()
        {
            InitializeComponent();
        }

        public void Initialize(TextArea textArea)
        {
            _textArea = textArea;
            _renderer = new SearchHighlightRenderer
            {
                MarkerBrush = new SolidColorBrush(Color.FromRgb(90, 90, 90))
            };

            textArea.DocumentChanged += TextArea_DocumentChanged;
        }

        private void TextArea_DocumentChanged(object sender, EventArgs e)
        {
            DoSearch(false);
        }

        public void Open(bool replaceMode = false)
        {
            Visibility = Visibility.Visible;
            IsReplaceMode = replaceMode;

            if (_textArea.Selection.Length > 0)
            {
                string selected = _textArea.Selection.GetText();
                if (!selected.Contains('\n') && selected.Length <= 256)
                    SearchTextBox.Text = selected;
            }

            SearchTextBox.Focus();
            SearchTextBox.SelectAll();

            if (!string.IsNullOrEmpty(SearchTextBox.Text))
                DoSearch(true);
        }

        public void ClosePanel()
        {
            Visibility = Visibility.Collapsed;
            _results.Clear();
            _textArea.TextView.BackgroundRenderers.Remove(_renderer);
            _textArea.TextView.InvalidateLayer(KnownLayer.Selection);
            _textArea.Focus();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            ClosePanel();
        }

        private void ToggleReplaceButton_Click(object sender, RoutedEventArgs e)
        {
            IsReplaceMode = !IsReplaceMode;
            if (IsReplaceMode)
                ReplaceTextBox.Focus();
            else
                SearchTextBox.Focus();
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            DoSearch(true);
        }

        private void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    FindPrevious();
                else
                    FindNext();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                ClosePanel();
            }
        }

        private void ReplaceTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                ReplaceCurrent();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                ClosePanel();
            }
        }

        private void FindNextButton_Click(object sender, RoutedEventArgs e)
        {
            FindNext();
        }

        private void FindPrevButton_Click(object sender, RoutedEventArgs e)
        {
            FindPrevious();
        }

        private void ReplaceButton_Click(object sender, RoutedEventArgs e)
        {
            ReplaceCurrent();
        }

        private void ReplaceAllButton_Click(object sender, RoutedEventArgs e)
        {
            ReplaceAll();
        }

        private void DoSearch(bool changeSelection)
        {
            if (Visibility != Visibility.Visible)
                return;

            _results.Clear();
            _renderer.Results.Clear();

            if (string.IsNullOrEmpty(SearchTextBox.Text) || _textArea.Document is null)
            {
                _textArea.TextView.InvalidateLayer(KnownLayer.Selection);
                return;
            }

            bool matchCase = MatchCaseButton.IsChecked ?? false;
            bool wholeWords = WholeWordsButton.IsChecked ?? false;
            bool useRegex = UseRegexButton.IsChecked ?? false;
            string pattern = SearchTextBox.Text;

            try
            {
                var regexOptions = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
                string regexPattern = useRegex ? pattern : Regex.Escape(pattern);
                if (wholeWords)
                    regexPattern = @"\b" + regexPattern + @"\b";

                var regex = new Regex(regexPattern, regexOptions);

                int offset = _textArea.Caret.Offset;
                if (changeSelection)
                    _textArea.ClearSelection();

                foreach (Match match in regex.Matches(_textArea.Document.Text))
                {
                    var result = (match.Index, match.Index + match.Length);
                    _results.Add(result);
                    _renderer.Results.Add(result);

                    if (changeSelection && match.Index >= offset)
                    {
                        SelectResult(match.Index, match.Length);
                        changeSelection = false;
                    }
                }
            }
            catch (ArgumentException)
            {
                _textArea.TextView.InvalidateLayer(KnownLayer.Selection);
                return;
            }

            if (!_textArea.TextView.BackgroundRenderers.Contains(_renderer))
                _textArea.TextView.BackgroundRenderers.Add(_renderer);

            _textArea.TextView.InvalidateLayer(KnownLayer.Selection);
        }

        private void FindNext()
        {
            if (_results.Count == 0)
            {
                DoSearch(true);
                return;
            }

            int caretOffset = _textArea.Caret.Offset;
            int idx = _results.FindIndex(r => r.StartOffset > caretOffset);
            if (idx < 0)
                idx = 0;

            var next = _results[idx];
            SelectResult(next.StartOffset, next.EndOffset - next.StartOffset);
        }

        private void FindPrevious()
        {
            if (_results.Count == 0)
            {
                DoSearch(true);
                return;
            }

            int caretOffset = _textArea.Caret.Offset;
            int idx = _results.FindLastIndex(r => r.StartOffset < caretOffset);
            if (idx < 0)
                idx = _results.Count - 1;

            var prev = _results[idx];
            SelectResult(prev.StartOffset, prev.EndOffset - prev.StartOffset);
        }

        private void SelectResult(int startOffset, int length)
        {
            _textArea.Caret.Offset = startOffset;
            _textArea.Selection = Selection.Create(_textArea, startOffset, startOffset + length);
            _textArea.Caret.BringCaretToView();
            _textArea.Caret.Show();
        }

        private void ReplaceCurrent()
        {
            if (string.IsNullOrEmpty(SearchTextBox.Text))
                return;

            if (_results.Count == 0)
            {
                DoSearch(true);
                return;
            }

            string replacement = ReplaceTextBox.Text ?? "";
            int caretOffset = _textArea.Caret.Offset;

            int idx = _results.FindIndex(r => r.StartOffset == caretOffset);
            if (idx < 0)
                idx = 0;

            var current = _results[idx];

            int length = current.EndOffset - current.StartOffset;
            _textArea.Document.Replace(current.StartOffset, length, replacement);
            DoSearch(true);
        }

        private void ReplaceAll()
        {
            if (string.IsNullOrEmpty(SearchTextBox.Text))
                return;

            DoSearch(false);

            if (_results.Count == 0)
                return;

            string replacement = ReplaceTextBox.Text ?? "";

            using (_textArea.Document.RunUpdate())
            {
                int offsetAdjust = 0;
                foreach (var result in _results)
                {
                    int adjustedStart = result.StartOffset + offsetAdjust;
                    int length = result.EndOffset - result.StartOffset;
                    _textArea.Document.Replace(adjustedStart, length, replacement);
                    offsetAdjust += replacement.Length - length;
                }
            }

            DoSearch(true);
        }
    }

    public class SearchHighlightRenderer : IBackgroundRenderer
    {
        public List<(int StartOffset, int EndOffset)> Results { get; } = new();
        public Brush MarkerBrush { get; set; } = Brushes.LightGreen;
        public double MarkerCornerRadius { get; set; } = 3.0;

        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (textView.Document is null || Results.Count == 0)
                return;

            textView.EnsureVisualLines();

            foreach (var result in Results)
            {
                var segment = new TextSegment { StartOffset = result.StartOffset, EndOffset = result.EndOffset };
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                {
                    drawingContext.DrawRoundedRectangle(MarkerBrush, null, rect, MarkerCornerRadius, MarkerCornerRadius);
                }
            }
        }
    }
}
