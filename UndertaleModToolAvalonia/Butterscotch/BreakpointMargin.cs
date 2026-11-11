using System;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Left margin that shows breakable bytecode offsets for the disassembly view. Every
/// instruction line maps 1:1 to a bytecode offset; markers show whether a breakpoint exists at
/// that offset, and clicking a line toggles it.
/// </summary>
public sealed class BreakpointMargin : AbstractMargin
{
    const double MarkerSize = 10.0;

    static readonly IBrush BreakpointBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x30, 0x30));

    readonly UndertaleCodeView editor;

    public BreakpointMargin(UndertaleCodeView editor)
    {
        this.editor = editor;

        // Avalonia hit-tests controls by bounds, so the whole margin is clickable; the hand
        // cursor hints at that.
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        PointerPoint point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        UndertaleCodeViewModel? viewModel = editor.ViewModel;
        if (viewModel is null || TextView is null)
            return;

        int line = GetLineFromPointerPosition(point.Position.Y);
        uint? offset = editor.GetBytecodeOffsetForDisassemblyLine(line);
        if (offset is not uint value)
            return;

        ButterscotchDebugService.Instance.ToggleBreakpoint(viewModel.Code, value);
        InvalidateVisual();
        e.Handled = true;
    }

    int GetLineFromPointerPosition(double y)
    {
        TextView? textView = TextView;
        if (textView is null || textView.Document is null)
            return -1;

        VisualLine? visualLine = textView.GetVisualLineFromVisualTop(y);
        return visualLine?.FirstDocumentLine.LineNumber ?? -1;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Size size = base.MeasureOverride(availableSize);
        double height = double.IsInfinity(size.Height) ? 0 : size.Height;
        return new Size(MarkerSize + 6.0, height);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        TextView? textView = TextView;
        UndertaleCodeViewModel? viewModel = editor.ViewModel;
        if (textView is null || textView.Document is null || viewModel is null)
            return;

        ButterscotchDebugService service = ButterscotchDebugService.Instance;
        if (viewModel.MainVM.Data is null)
            return;

        int codeIndex = viewModel.MainVM.Data.Code.IndexOf(viewModel.Code);
        if (codeIndex < 0)
            return;

        double width = Bounds.Width;
        foreach (VisualLine line in textView.VisualLines)
        {
            int lineNumber = line.FirstDocumentLine.LineNumber;
            uint? offset = editor.GetBytecodeOffsetForDisassemblyLine(lineNumber);
            if (offset is not uint value || !service.HasBreakpoint(codeIndex, value))
                continue;

            Rect? rect = null;
            foreach (Rect candidate in BackgroundGeometryBuilder.GetRectsForSegment(textView, line.FirstDocumentLine))
            {
                rect = candidate;
                break;
            }
            if (rect is null)
                continue;
            double centerY = rect.Value.Y + (rect.Value.Height / 2.0);
            context.DrawEllipse(BreakpointBrush, null, new Point(width / 2.0, centerY),
                                MarkerSize / 2.0, MarkerSize / 2.0);
        }
    }


    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        base.OnTextViewChanged(oldTextView, newTextView);

        if (oldTextView is not null)
            oldTextView.VisualLinesChanged -= VisualLinesChanged;
        if (newTextView is not null)
            newTextView.VisualLinesChanged += VisualLinesChanged;
    }

    void VisualLinesChanged(object? sender, EventArgs e)
    {
        InvalidateVisual();
    }
}