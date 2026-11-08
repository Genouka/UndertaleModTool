using System;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace UndertaleModTool
{
    /// <summary>
    /// Background renderer that highlights the disassembly line the Butterscotch
    /// debugger is currently stopped at (yellow tint, like a debugger current line).
    /// </summary>
    [SupportedOSPlatform("windows7.0")]
    public class DebugStoppedLineRenderer : IBackgroundRenderer
    {
        private static readonly SolidColorBrush BackgroundBrush =
            new(Color.FromArgb(0x60, 0xE8, 0xC0, 0x20));

        private readonly TextView _textView;
        private int _lineNumber = -1;

        public DebugStoppedLineRenderer(TextView textView)
        {
            _textView = textView;
        }

        public KnownLayer Layer => KnownLayer.Selection;

        /// <summary>Sets the highlighted line (1-based) or -1/0 to clear.</summary>
        public void SetLine(int lineNumber)
        {
            if (_lineNumber == lineNumber)
                return;
            _lineNumber = lineNumber;
            _textView.Redraw();
        }

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (_lineNumber < 1 || textView.Document is null)
                return;

            DocumentLine line = textView.Document.GetLineByNumber(_lineNumber);
            foreach (Rect rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, line))
            {
                drawingContext.DrawRectangle(BackgroundBrush, null, rect);
            }
        }
    }
}
