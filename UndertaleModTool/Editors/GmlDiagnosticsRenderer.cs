using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace UndertaleModTool.Editors
{
    /// <summary>
    /// Renders red squiggly underlines beneath segments in the editor that have
    /// diagnostics (e.g. compile/parse errors), and also highlights the whole
    /// offending line with a subtle background tint.
    /// </summary>
    [SupportedOSPlatform("windows7.0")]
    public class GmlDiagnosticsRenderer : IBackgroundRenderer
    {
        private readonly TextView _textView;
        private readonly List<TextSegment> _segments = new();
        private readonly object _lock = new();

        private static readonly SolidColorBrush ErrorBackgroundBrush = new(Color.FromArgb(0x14, 0xFF, 0x00, 0x00));
        private static readonly Pen ErrorPen = CreateErrorPen();

        private static Pen CreateErrorPen()
        {
            Pen pen = new(Brushes.Tomato, 1.0);
            pen.DashStyle = DashStyles.Dot;
            pen.Freeze();
            return pen;
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
        public void SetDiagnostics(IEnumerable<GmlDiagnostic> diagnostics, IDocument document)
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

        /// <inheritdoc/>
        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            List<TextSegment> segmentsToDraw;
            lock (_lock)
            {
                if (_segments.Count == 0)
                    return;
                segmentsToDraw = new List<TextSegment>(_segments);
            }

            bool isDark = Settings.Instance?.EnableDarkMode ?? true;

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
                Geometry geometry = geoBuilder.CreateGeometry();
                if (geometry is null)
                    continue;

                // Subtle line background tint + squiggly error underline
                drawingContext.DrawGeometry(isDark ? ErrorBackgroundBrush : ErrorBackgroundBrush, null, geometry);
                drawingContext.DrawGeometry(null, ErrorPen, geometry);
            }
        }
    }
}