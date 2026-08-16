using System;
using System.Collections.Generic;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;

namespace UndertaleModToolAvalonia;

/// <summary>
/// A folding strategy for GML source code. Folds blocks delimited by braces,
/// as well as <c>#region</c>...<c>#endregion</c> comment regions.
/// </summary>
public class GmlFoldingStrategy
{
    /// <summary>
    /// Recomputes the foldings for the given document, updating the folding manager.
    /// </summary>
    public void UpdateFoldings(FoldingManager manager, TextDocument document)
    {
        int firstErrorOffset;
        List<NewFolding> newFoldings = CreateNewFoldings(document, out firstErrorOffset);
        manager.UpdateFoldings(newFoldings, firstErrorOffset);
    }

    /// <summary>
    /// Computes a list of foldings based on brace matching in the document.
    /// </summary>
    public static List<NewFolding> CreateNewFoldings(TextDocument document, out int firstErrorOffset)
    {
        List<NewFolding> foldings = new();
        firstErrorOffset = -1;

        if (document is null || document.TextLength == 0)
            return foldings;

        string text = document.Text;
        Stack<int> braceStack = new();
        bool inLineComment = false;
        bool inBlockComment = false;
        bool inString = false;
        char stringQuote = '"';
        bool inTemplateString = false;
        int templateBraceDepth = 0;
        bool regionStart = false;
        int regionStartOffset = -1;

        int i = 0;
        int len = text.Length;
        while (i < len)
        {
            char c = text[i];
            char next = i + 1 < len ? text[i + 1] : '\0';

            if (inLineComment)
            {
                if (c == '\n')
                    inLineComment = false;
                i++;
                continue;
            }

            if (inBlockComment)
            {
                if (c == '*' && next == '/')
                {
                    inBlockComment = false;
                    i += 2;
                    continue;
                }
                i++;
                continue;
            }

            if (inString)
            {
                if (c == '\\')
                {
                    i += 2;
                    continue;
                }
                if (c == stringQuote)
                {
                    inString = false;
                }
                i++;
                continue;
            }

            if (inTemplateString)
            {
                if (c == '{')
                {
                    templateBraceDepth++;
                }
                else if (c == '}')
                {
                    if (templateBraceDepth == 0)
                    {
                        if (next == '"')
                        {
                            // End of template string (}" is closing quote in older GML)
                            inTemplateString = false;
                            i += 2;
                            continue;
                        }
                    }
                    else
                    {
                        templateBraceDepth--;
                    }
                }
                else if (c == '"' && templateBraceDepth == 0 && next != '{')
                {
                    inTemplateString = false;
                }
                else if (c == '\\')
                {
                    i += 2;
                    continue;
                }
                i++;
                continue;
            }

            if (c == '/' && next == '/')
            {
                inLineComment = true;
                i += 2;
                continue;
            }
            if (c == '/' && next == '*')
            {
                inBlockComment = true;
                i += 2;
                continue;
            }
            if (c == '"' || c == '\'')
            {
                if (c == '"' && i > 0 && text[i - 1] == '$')
                {
                    inTemplateString = true;
                    templateBraceDepth = 0;
                }
                else
                {
                    inString = true;
                    stringQuote = c;
                }
                i++;
                continue;
            }

            if (c == '{')
            {
                braceStack.Push(i);
                i++;
                continue;
            }
            if (c == '}')
            {
                if (braceStack.Count > 0)
                {
                    int openOffset = braceStack.Pop();
                    if (openOffset + 1 < i)
                    {
                        NewFolding folding = new(openOffset, i + 1);
                        foldings.Add(folding);
                    }
                }
                else
                {
                    if (firstErrorOffset < 0)
                        firstErrorOffset = i;
                }
                i++;
                continue;
            }

            // #region ... #endregion comment folding (GameMaker doesn't support these,
            // but some decompiled source or user comments may use them for organization)
            if (c == '#' && i + 7 <= len && string.Equals(text.Substring(i, 7), "#region", StringComparison.Ordinal))
            {
                regionStart = true;
                regionStartOffset = i;
                i += 7;
                continue;
            }
            if (c == '#' && i + 10 <= len && string.Equals(text.Substring(i, 10), "#endregion", StringComparison.Ordinal))
            {
                if (regionStart && regionStartOffset >= 0)
                {
                    foldings.Add(new NewFolding(regionStartOffset, i + 10));
                    regionStart = false;
                    regionStartOffset = -1;
                }
                i += 10;
                continue;
            }

            i++;
        }

        return foldings;
    }
}