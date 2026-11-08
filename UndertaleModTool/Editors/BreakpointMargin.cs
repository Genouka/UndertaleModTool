using System;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using UndertaleModTool.Debugging;

namespace UndertaleModTool
{
    /// <summary>
    /// Left margin that shows breakable bytecode offsets for the disassembly view.
    /// Every instruction line maps 1:1 to a bytecode offset; markers show whether a
    /// breakpoint exists at that offset. Clicking a marker toggles the breakpoint.
    /// </summary>
    [SupportedOSPlatform("windows7.0")]
    public class BreakpointMargin : AbstractMargin
    {
        private readonly UndertaleCodeEditor _editor;
        private const double MarkerSize = 10.0;

        public static readonly SolidColorBrush BreakpointBrush =
            new(Color.FromRgb(0xE0, 0x30, 0x30));

        public BreakpointMargin(UndertaleCodeEditor editor)
        {
            _editor = editor;
        }

        protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
        {
            // Make the whole margin clickable.
            return new PointHitTestResult(this, hitTestParameters.HitPoint);
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);

            if (e.ChangedButton != MouseButton.Left)
                return;

            if (_editor?.CurrentDisassembled is null || TextView is null)
                return;

            int line = GetLineFromMousePosition(e.GetPosition(this));
            if (line < 1)
                return;

            uint? offset = _editor.GetBytecodeOffsetForDisassemblyLine(line);
            if (offset is not uint value)
                return;

            DebugSessionService.Instance?.ToggleBreakpoint(_editor.CurrentDisassembled, value);
            InvalidateVisual();
            e.Handled = true;
        }

        private int GetLineFromMousePosition(Point position)
        {
            TextView textView = TextView;
            if (textView is null || textView.Document is null)
                return -1;

            VisualLine visualLine = textView.GetVisualLineFromVisualTop(position.Y);
            return visualLine?.FirstDocumentLine?.LineNumber ?? -1;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            Size size = base.MeasureOverride(availableSize);
            return new Size(MarkerSize + 6.0, size.Height);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            TextView textView = TextView;
            if (textView is null || textView.Document is null)
                return;

            DebugSessionService service = DebugSessionService.Instance;
            UndertaleModLib.Models.UndertaleCode code = _editor?.CurrentDisassembled;
            if (service is null || code is null)
                return;

            int codeIndex = service.MainWindow.Data?.Code.IndexOf(code) ?? -1;
            if (codeIndex < 0)
                return;

            Size renderSize = RenderSize;
            foreach (VisualLine line in textView.VisualLines)
            {
                int lineNumber = line.FirstDocumentLine.LineNumber;
                uint? offset = _editor.GetBytecodeOffsetForDisassemblyLine(lineNumber);
                if (offset is not uint value)
                    continue;

                if (!service.HasBreakpoint(codeIndex, value))
                    continue;

                Rect rect = BackgroundGeometryBuilder.GetRectsForSegment(textView, line.FirstDocumentLine).First();
                double centerY = rect.Y + rect.Height / 2.0;

                drawingContext.DrawEllipse(
                    BreakpointBrush,
                    null,
                    new Point(renderSize.Width / 2.0, centerY),
                    MarkerSize / 2.0,
                    MarkerSize / 2.0);
            }
        }

        protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
        {
            base.OnTextViewChanged(oldTextView, newTextView);

            if (oldTextView is not null)
                oldTextView.VisualLinesChanged -= VisualLinesChanged;
            if (newTextView is not null)
                newTextView.VisualLinesChanged += VisualLinesChanged;
        }

        private void VisualLinesChanged(object sender, EventArgs e)
        {
            InvalidateVisual();
        }

    }
}
