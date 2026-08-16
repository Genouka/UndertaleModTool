using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Renders red squiggly underlines beneath segments in the editor that have
/// diagnostics (e.g. compile/parse errors), and also highlights the whole
/// offending line with a subtle background tint.
/// </summary>
public class GmlDiagnosticsRenderer : IBackgroundRenderer
{
    private readonly TextView _textView;
    private readonly List<TextSegment> _segments = new();
    private readonly object _lock = new();

    private static readonly IBrush ErrorBackgroundBrush = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0x00, 0x00));
    private static readonly IPen ErrorPen = CreateErrorPen();

    private static IPen CreateErrorPen()
    {
        return new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x63, 0x47)), 1.0)
        {
            DashStyle = DashStyle.Dot,
            LineCap = PenLineCap.Round
        };
    }

    /// <summary>Layer that the renderer draws on (selection layer, above text).</summary>
    public KnownLayer Layer => KnownLayer.Selection;

    public GmlDiagnosticsRenderer(TextView textView)
    {
        _textView = textView;
    }

    /// <summary>
    /// Updates the set of error segments to draw, converting diagnostics to
    /// document offsets. Should be called on the UI thread.
    /// </summary>
    public void SetDiagnostics(IEnumerable<GmlDiagnostic> diagnostics, TextDocument document)
    {
        lock (_lock)
        {
            _segments.Clear();
            if (diagnostics is not null && document is not null)
            {
                foreach (GmlDiagnostic diagnostic in diagnostics)
                {
                    int start = diagnostic.TextPosition;
                    int length = diagnostic.Length;
                    if (start < 0) continue;
                    if (length < 1) length = 1;
                    // Clamp to document bounds
                    if (start >= document.TextLength) continue;
                    if (start + length > document.TextLength)
                        length = document.TextLength - start;
                    TextSegment segment = new()
                    {
                        StartOffset = start,
                        Length = length
                    };
                    _segments.Add(segment);
                }
            }
        }

        _textView.InvalidateLayer(Layer);
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        List<TextSegment> segmentsToDraw;
        lock (_lock)
        {
            if (_segments.Count == 0)
                return;
            segmentsToDraw = new List<TextSegment>(_segments);
        }

        // Compute the visible offset range (if the visual lines are valid)
        int visibleStart = int.MinValue;
        int visibleEnd = int.MaxValue;
        if (textView.VisualLinesValid && textView.VisualLines.Count > 0)
        {
            visibleStart = textView.VisualLines[0].FirstDocumentLine.Offset;
            visibleEnd = textView.VisualLines[textView.VisualLines.Count - 1].LastDocumentLine.EndOffset;
        }

        foreach (TextSegment segment in segmentsToDraw)
        {
            // Only draw segments that intersect the visible region
            if (segment.EndOffset < visibleStart)
                continue;
            if (segment.StartOffset > visibleEnd)
                continue;

            BackgroundGeometryBuilder geoBuilder = new()
            {
                CornerRadius = 1.0
            };
            geoBuilder.AddSegment(textView, segment);
            Geometry? geometry = geoBuilder.CreateGeometry();
            if (geometry is null)
                continue;

            // Subtle line background tint + squiggly error underline
            drawingContext.DrawGeometry(ErrorBackgroundBrush, null, geometry);
            drawingContext.DrawGeometry(null, ErrorPen, geometry);
        }
    }
}