using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Background renderer that highlights the disassembly line the Butterscotch debugger is
/// currently stopped at (yellow tint, like a debugger current line).
/// </summary>
public sealed class DebugStoppedLineRenderer : IBackgroundRenderer
{
    static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xE8, 0xC0, 0x20));

    readonly TextView textView;
    int lineNumber = -1;

    public DebugStoppedLineRenderer(TextView textView)
    {
        this.textView = textView;
    }

    public KnownLayer Layer => KnownLayer.Selection;

    /// <summary>Sets the highlighted line (1-based) or -1/0 to clear.</summary>
    public void SetLine(int lineNumber)
    {
        if (this.lineNumber == lineNumber)
            return;
        this.lineNumber = lineNumber;
        textView.Redraw();
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (lineNumber < 1 || textView.Document is null || lineNumber > textView.Document.LineCount)
            return;

        DocumentLine line = textView.Document.GetLineByNumber(lineNumber);
        foreach (Rect rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, line))
            drawingContext.FillRectangle(BackgroundBrush, rect);
    }
}