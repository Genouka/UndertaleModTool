#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace UndertaleModLib.Diff;

public enum TextDiffKind
{
    Equal,
    Added,
    Removed
}

public readonly record struct TextDiffLine(TextDiffKind Kind, string Text, int OldLine, int NewLine);

/// <summary>
/// Line-based diff used for decompiled GML. Uses a Myers diff for ordinary code entries and
/// falls back to a coarse "replace the changed middle" diff for very large inputs, so memory
/// use stays bounded on pathological code.
/// </summary>
public static class TextLineDiff
{
    private const int MaxMyersLines = 2000;
    private const int ContextLines = 3;

    public static IReadOnlyList<TextDiffLine> Diff(string oldText, string newText)
    {
        string[] oldLines = SplitLines(oldText);
        string[] newLines = SplitLines(newText);

        List<TextDiffLine> operations = oldLines.Length + newLines.Length <= MaxMyersLines
            ? Myers(oldLines, newLines)
            : Coarse(oldLines, newLines);

        AssignLineNumbers(operations);
        return operations;
    }

    /// <summary>Formats a unified diff with context lines. Returns an empty string when equal.</summary>
    public static string FormatUnified(IReadOnlyList<TextDiffLine> lines, int contextLines = ContextLines)
    {
        List<(int Start, int End)> hunks = [];
        int index = 0;
        while (index < lines.Count)
        {
            if (lines[index].Kind == TextDiffKind.Equal)
            {
                index++;
                continue;
            }

            int start = Math.Max(0, index - contextLines);
            int end = index;
            while (end < lines.Count)
            {
                if (lines[end].Kind != TextDiffKind.Equal)
                {
                    end++;
                    continue;
                }

                int equalRunStart = end;
                while (end < lines.Count && lines[end].Kind == TextDiffKind.Equal)
                    end++;
                if (end >= lines.Count || end - equalRunStart > contextLines * 2)
                {
                    end = Math.Min(lines.Count, equalRunStart + contextLines);
                    break;
                }
            }

            if (hunks.Count > 0 && start <= hunks[^1].End)
                hunks[^1] = (hunks[^1].Start, end);
            else
                hunks.Add((start, end));

            index = end;
        }

        if (hunks.Count == 0)
            return "";

        StringBuilder builder = new();
        int previousOldEnd = 0;
        int previousNewEnd = 0;
        foreach ((int start, int end) in hunks)
        {
            int oldStart = 0;
            int newStart = 0;
            int oldCount = 0;
            int newCount = 0;

            for (int i = start; i < end; i++)
            {
                TextDiffLine line = lines[i];
                if (line.Kind != TextDiffKind.Added)
                {
                    if (oldCount == 0)
                        oldStart = line.OldLine;
                    oldCount++;
                }
                if (line.Kind != TextDiffKind.Removed)
                {
                    if (newCount == 0)
                        newStart = line.NewLine;
                    newCount++;
                }
            }

            if (oldCount == 0)
                oldStart = previousOldEnd + 1;
            if (newCount == 0)
                newStart = previousNewEnd + 1;

            builder.Append("@@ -").Append(oldStart).Append(',').Append(oldCount)
                   .Append(" +").Append(newStart).Append(',').Append(newCount).Append(" @@\n");

            for (int i = start; i < end; i++)
            {
                TextDiffLine line = lines[i];
                builder.Append(line.Kind switch
                {
                    TextDiffKind.Added => '+',
                    TextDiffKind.Removed => '-',
                    _ => ' '
                }).Append(line.Text).Append('\n');
            }

            previousOldEnd = oldStart + oldCount - 1;
            previousNewEnd = newStart + newCount - 1;
        }

        return builder.ToString();
    }

    private static void AssignLineNumbers(List<TextDiffLine> operations)
    {
        int oldLine = 0;
        int newLine = 0;
        for (int i = 0; i < operations.Count; i++)
        {
            TextDiffLine line = operations[i];
            switch (line.Kind)
            {
                case TextDiffKind.Removed:
                    operations[i] = line with { OldLine = ++oldLine, NewLine = 0 };
                    break;
                case TextDiffKind.Added:
                    operations[i] = line with { OldLine = 0, NewLine = ++newLine };
                    break;
                default:
                    operations[i] = line with { OldLine = ++oldLine, NewLine = ++newLine };
                    break;
            }
        }
    }

    private static string[] SplitLines(string text)
    {
        if (text.Length == 0)
            return [];
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    private static List<TextDiffLine> Coarse(string[] oldLines, string[] newLines)
    {
        int prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length &&
               string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < oldLines.Length - prefix && suffix < newLines.Length - prefix &&
               string.Equals(oldLines[^(suffix + 1)], newLines[^(suffix + 1)], StringComparison.Ordinal))
        {
            suffix++;
        }

        List<TextDiffLine> result = new(oldLines.Length + newLines.Length);
        for (int i = 0; i < prefix; i++)
            result.Add(new(TextDiffKind.Equal, oldLines[i], 0, 0));
        for (int i = prefix; i < oldLines.Length - suffix; i++)
            result.Add(new(TextDiffKind.Removed, oldLines[i], 0, 0));
        for (int i = prefix; i < newLines.Length - suffix; i++)
            result.Add(new(TextDiffKind.Added, newLines[i], 0, 0));
        for (int i = oldLines.Length - suffix; i < oldLines.Length; i++)
            result.Add(new(TextDiffKind.Equal, oldLines[i], 0, 0));
        return result;
    }

    private static List<TextDiffLine> Myers(string[] oldLines, string[] newLines)
    {
        int n = oldLines.Length;
        int m = newLines.Length;
        int max = n + m;
        int[] v = new int[2 * max + 1];
        List<int[]> trace = new(max + 1);
        int offset = max;

        for (int d = 0; d <= max; d++)
        {
            for (int k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[k - 1 + offset] < v[k + 1 + offset]))
                    x = v[k + 1 + offset];
                else
                    x = v[k - 1 + offset] + 1;

                int y = x - k;
                while (x < n && y < m && string.Equals(oldLines[x], newLines[y], StringComparison.Ordinal))
                {
                    x++;
                    y++;
                }

                v[k + offset] = x;

                if (x >= n && y >= m)
                {
                    trace.Add((int[])v.Clone());
                    return Backtrack(trace, oldLines, newLines, d, offset);
                }
            }

            trace.Add((int[])v.Clone());
        }

        return Coarse(oldLines, newLines);
    }

    private static List<TextDiffLine> Backtrack(List<int[]> trace, string[] oldLines, string[] newLines,
                                                int finalDistance, int offset)
    {
        List<TextDiffLine> result = [];
        int x = oldLines.Length;
        int y = newLines.Length;

        for (int d = finalDistance; d >= 0; d--)
        {
            int[] v = trace[d];
            int k = x - y;
            int previousK;
            int previousX;
            int previousY;

            if (d == 0)
            {
                previousX = 0;
                previousY = 0;
            }
            else
            {
                if (k == -d || (k != d && v[k - 1 + offset] < v[k + 1 + offset]))
                    previousK = k + 1;
                else
                    previousK = k - 1;

                previousX = v[previousK + offset];
                previousY = previousX - previousK;
            }

            while (x > previousX && y > previousY)
            {
                result.Add(new(TextDiffKind.Equal, oldLines[x - 1], 0, 0));
                x--;
                y--;
            }

            if (d > 0)
            {
                if (x == previousX)
                {
                    result.Add(new(TextDiffKind.Added, newLines[y - 1], 0, 0));
                    y--;
                }
                else
                {
                    result.Add(new(TextDiffKind.Removed, oldLines[x - 1], 0, 0));
                    x--;
                }
            }
        }

        result.Reverse();
        return result;
    }
}
