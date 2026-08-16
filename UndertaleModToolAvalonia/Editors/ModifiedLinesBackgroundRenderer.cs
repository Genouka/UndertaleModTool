using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Renders a highlight behind lines that have been modified relative to the
/// originally loaded text (change tracking).
/// </summary>
public class ModifiedLinesBackgroundRenderer : AvaloniaEdit.Rendering.IBackgroundRenderer
{
    private readonly HashSet<int> _modifiedLineNumbers = new();
    private string _originalText = "";
    private TextDocument _document;

    public static readonly IBrush DefaultModifiedBrush =
        new SolidColorBrush(Color.FromArgb(40, 255, 152, 0));

    public IBrush ModifiedLineBrush { get; set; } = DefaultModifiedBrush;

    public KnownLayer Layer => KnownLayer.Background;

    public void SetOriginalText(string text, TextDocument document)
    {
        _originalText = text ?? "";
        _document = document;
        _modifiedLineNumbers.Clear();
    }

    public void MarkDirty()
    {
        UpdateDiff();
    }

    public void ClearModifiedLines()
    {
        _modifiedLineNumbers.Clear();
        _originalText = _document?.Text ?? "";
    }

    public bool HasModifiedLines => _modifiedLineNumbers.Count > 0;

    private void UpdateDiff()
    {
        _modifiedLineNumbers.Clear();

        if (_document == null || string.IsNullOrEmpty(_originalText))
            return;

        var originalLines = _originalText.Split('\n');
        var currentText = _document.Text;
        var currentLines = currentText.Split('\n');

        int n = originalLines.Length;
        int m = currentLines.Length;

        if ((long)n * m > 4_000_000L)
        {
            SimpleDiff(originalLines, currentLines);
            return;
        }

        LCSDiff(originalLines, currentLines);
    }

    private void SimpleDiff(string[] originalLines, string[] currentLines)
    {
        int maxLines = Math.Max(originalLines.Length, currentLines.Length);
        for (int i = 0; i < maxLines; i++)
        {
            string? origLine = i < originalLines.Length ? originalLines[i] : null;
            string? curLine = i < currentLines.Length ? currentLines[i] : null;
            if (origLine != curLine)
                _modifiedLineNumbers.Add(i + 1);
        }
    }

    private void LCSDiff(string[] originalLines, string[] currentLines)
    {
        int n = originalLines.Length;
        int m = currentLines.Length;

        int[,] dp = new int[n + 1, m + 1];

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                if (originalLines[i - 1] == currentLines[j - 1])
                    dp[i, j] = dp[i - 1, j - 1] + 1;
                else
                    dp[i, j] = Math.Max(dp[i - 1, j], dp[i, j - 1]);
            }
        }

        var inLCS = new bool[m];
        int ii = n, jj = m;
        while (ii > 0 && jj > 0)
        {
            if (originalLines[ii - 1] == currentLines[jj - 1])
            {
                inLCS[jj - 1] = true;
                ii--;
                jj--;
            }
            else if (dp[ii - 1, jj] > dp[ii, jj - 1])
                ii--;
            else
                jj--;
        }

        for (int i = 0; i < m; i++)
        {
            if (!inLCS[i])
                _modifiedLineNumbers.Add(i + 1);
        }
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_modifiedLineNumbers.Count == 0)
            return;

        if (textView.Document == null)
            return;

        foreach (int lineNumber in _modifiedLineNumbers)
        {
            if (lineNumber < 1 || lineNumber > textView.Document.LineCount)
                continue;

            var line = textView.Document.GetLineByNumber(lineNumber);
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, line, false))
            {
                drawingContext.DrawRectangle(ModifiedLineBrush, null,
                    new Rect(rect.X, rect.Y, textView.Bounds.Width, rect.Height));
            }
        }
    }
}
