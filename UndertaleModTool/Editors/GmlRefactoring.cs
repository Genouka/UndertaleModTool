using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace UndertaleModTool.Editors
{
    /// <summary>
    /// Token-based GML source refactorings used by the code editor's context menu.
    /// The helpers here never touch game data; they only rewrite the supplied source text.
    /// </summary>
    internal static class GmlRefactoring
    {
        /// <summary>
        /// A replacement to apply to a text document, plus the selection that should be left afterwards.
        /// </summary>
        public readonly struct TextEdit
        {
            public int Offset { get; }
            public int Length { get; }
            public string NewText { get; }
            public int SelectionOffset { get; }
            public int SelectionLength { get; }

            public TextEdit(int offset, int length, string newText, int selectionOffset, int selectionLength)
            {
                Offset = offset;
                Length = length;
                NewText = newText;
                SelectionOffset = selectionOffset;
                SelectionLength = selectionLength;
            }
        }

        private enum TokenKind
        {
            Identifier,
            Number,
            String,
            Comment,
            Symbol
        }

        private sealed class Token
        {
            public TokenKind Kind;
            public int Start;
            public int End;
            public string Text;
        }

        private sealed class SwitchCase
        {
            public bool IsDefault;
            public int ExprStart = -1;
            public int ExprEnd = -1;
            public int BodyStart = -1;
            public int BodyEnd = -1;
            public bool StripBreak;
            public int BreakToken = -1;
        }

        private readonly struct Branch
        {
            public readonly int ConditionStart;
            public readonly int ConditionEnd;
            public readonly int BodyStart;
            public readonly int BodyEnd;

            public Branch(int conditionStart, int conditionEnd, int bodyStart, int bodyEnd)
            {
                ConditionStart = conditionStart;
                ConditionEnd = conditionEnd;
                BodyStart = bodyStart;
                BodyEnd = bodyEnd;
            }
        }

        /// <summary>
        /// Converts the innermost switch statement at the selection/caret into an if-else chain.
        /// </summary>
        public static bool TrySwitchToIfElse(string documentText, int selectionStart, int selectionLength,
                                             out TextEdit edit, out string error)
        {
            edit = default;
            error = null;

            List<Token> tokens = Tokenize(documentText);
            int switchIndex = FindConstruct(tokens, "switch", selectionStart, selectionLength, out int endIndex);
            if (switchIndex < 0)
            {
                error = "no switch statement found";
                return false;
            }

            int paren = NextCode(tokens, switchIndex + 1);
            int parenClose = FindMatching(tokens, paren, "(", ")");
            int openBrace = NextCode(tokens, parenClose + 1);
            int closeBrace = FindMatching(tokens, openBrace, "{", "}");
            if (parenClose < 0 || openBrace < 0 || closeBrace < 0 || closeBrace != endIndex)
            {
                error = "malformed switch statement";
                return false;
            }

            string switchExpression = Slice(documentText, tokens, NextCode(tokens, paren + 1), LastCodeBefore(tokens, parenClose)).Trim();
            if (switchExpression.Length == 0)
            {
                error = "switch has an empty expression";
                return false;
            }

            if (!ParseSwitchCases(tokens, openBrace, closeBrace, out List<SwitchCase> cases, out error))
                return false;

            foreach (SwitchCase c in cases)
            {
                if (!ValidateCaseBreak(tokens, c, out bool stripBreak, out int breakToken))
                {
                    error = "a case body contains a 'break' that cannot be represented in an if-else chain";
                    return false;
                }
                c.StripBreak = stripBreak;
                c.BreakToken = breakToken;
            }

            string baseIndent = GetLineIndent(documentText, tokens[switchIndex].Start);
            string indentUnit = baseIndent.Contains('\t') ? "\t" : "    ";
            string newline = DetectNewline(documentText);

            string EffectiveBody(int index)
            {
                SwitchCase c = cases[index];
                string body = GetCaseBodyText(documentText, tokens, c, c.StripBreak, c.BreakToken);
                if (c.StripBreak || EndsWithTerminator(tokens, c))
                    return body;
                if (index + 1 < cases.Count)
                {
                    string next = EffectiveBody(index + 1);
                    if (body.Length == 0)
                        return next;
                    if (next.Length == 0)
                        return body;
                    return body + "\n" + next;
                }
                return body;
            }

            StringBuilder sb = new();
            bool first = true;
            for (int i = 0; i < cases.Count; i++)
            {
                SwitchCase c = cases[i];
                if (c.IsDefault)
                    continue;

                if (c.ExprStart < 0 || c.ExprEnd < c.ExprStart || HasTopLevelComma(tokens, c.ExprStart, c.ExprEnd))
                {
                    error = "a case label uses an unsupported expression";
                    return false;
                }

                string value = Slice(documentText, tokens, c.ExprStart, c.ExprEnd).Trim();
                if (value.Length == 0)
                {
                    error = "a case label is empty";
                    return false;
                }

                string body = Reindent(EffectiveBody(i), baseIndent + indentUnit, newline);
                if (!first)
                    sb.Append(baseIndent).Append("else ");
                first = false;
                sb.Append("if ((").Append(switchExpression).Append(") == (").Append(value).Append("))").Append(newline);
                sb.Append(baseIndent).Append('{').Append(newline);
                if (body.Length > 0)
                    sb.Append(body).Append(newline);
                sb.Append(baseIndent).Append('}').Append(newline);
            }

            int defaultIndex = cases.FindIndex(c => c.IsDefault);
            if (defaultIndex >= 0)
            {
                string body = Reindent(EffectiveBody(defaultIndex), baseIndent + indentUnit, newline);
                if (!first)
                    sb.Append(baseIndent).Append("else").Append(newline);
                sb.Append(baseIndent).Append('{').Append(newline);
                if (body.Length > 0)
                    sb.Append(body).Append(newline);
                sb.Append(baseIndent).Append('}').Append(newline);
            }

            if (first && defaultIndex < 0)
            {
                error = "switch has no cases";
                return false;
            }

            string newText = sb.ToString().TrimEnd('\r', '\n');
            int offset = tokens[switchIndex].Start;
            int length = tokens[closeBrace].End - offset;
            edit = new TextEdit(offset, length, newText, offset, 0);
            return true;
        }

        /// <summary>
        /// Converts an if-else-if chain of simple equality comparisons into a switch statement.
        /// </summary>
        public static bool TryIfElseToSwitch(string documentText, int selectionStart, int selectionLength,
                                             out TextEdit edit, out string error)
        {
            edit = default;
            error = null;

            List<Token> tokens = Tokenize(documentText);
            int ifIndex = FindConstruct(tokens, "if", selectionStart, selectionLength, out int endIndex);
            if (ifIndex < 0)
            {
                error = "no if statement found";
                return false;
            }

            List<Branch> branches = new();
            int defaultStart = -1;
            int defaultEnd = -1;
            int cursor = ifIndex;
            while (true)
            {
                int paren = NextCode(tokens, cursor + 1);
                if (!IsSymbol(tokens, paren, "("))
                {
                    error = "malformed if statement";
                    return false;
                }
                int parenClose = FindMatching(tokens, paren, "(", ")");
                if (parenClose < 0)
                {
                    error = "malformed if condition";
                    return false;
                }

                int bodyStart = NextCode(tokens, parenClose + 1);
                int bodyEnd = ParseStatementEnd(tokens, bodyStart);
                if (bodyStart < 0 || bodyEnd < bodyStart)
                {
                    error = "malformed if body";
                    return false;
                }

                branches.Add(new Branch(NextCode(tokens, paren + 1), LastCodeBefore(tokens, parenClose), bodyStart, bodyEnd));

                int elseIndex = NextCode(tokens, bodyEnd + 1);
                if (!IsWord(tokens, elseIndex, "else"))
                    break;

                int afterElse = NextCode(tokens, elseIndex + 1);
                if (IsWord(tokens, afterElse, "if"))
                {
                    cursor = afterElse;
                    continue;
                }

                defaultStart = afterElse;
                defaultEnd = ParseStatementEnd(tokens, defaultStart);
                if (defaultStart < 0 || defaultEnd < defaultStart)
                {
                    error = "malformed else body";
                    return false;
                }
                break;
            }


            string subject = null;
            string normalizedSubject = null;
            List<string> values = new(branches.Count);
            foreach (Branch b in branches)
            {
                if (!TrySplitEquality(documentText, tokens, b.ConditionStart, b.ConditionEnd,
                                      out string branchSubject, out string branchValue))
                {
                    error = "each condition must be a simple '==' comparison";
                    return false;
                }

                string normalized = NormalizeExpression(branchSubject);
                if (subject is null)
                {
                    subject = branchSubject.Trim();
                    normalizedSubject = normalized;
                }
                else if (normalized != normalizedSubject)
                {
                    error = "conditions compare different subjects";
                    return false;
                }

                values.Add(branchValue.Trim());
            }

            string baseIndent = GetLineIndent(documentText, tokens[ifIndex].Start);
            string indentUnit = baseIndent.Contains('\t') ? "\t" : "    ";
            string newline = DetectNewline(documentText);
            string caseBodyIndent = baseIndent + indentUnit + indentUnit;

            StringBuilder sb = new();
            sb.Append(baseIndent).Append("switch (").Append(subject).Append(')').Append(newline);
            sb.Append(baseIndent).Append('{').Append(newline);

            for (int i = 0; i < branches.Count; i++)
            {
                Branch b = branches[i];
                string body = GetStatementBodyText(documentText, tokens, b.BodyStart, b.BodyEnd);
                bool hasTerminator = StatementEndsWithTerminator(tokens, b.BodyStart, b.BodyEnd);
                string reindented = Reindent(body, caseBodyIndent, newline);

                sb.Append(baseIndent).Append(indentUnit).Append("case ").Append(values[i]).Append(':').Append(newline);
                if (reindented.Length > 0)
                    sb.Append(reindented).Append(newline);
                if (!hasTerminator)
                    sb.Append(caseBodyIndent).Append("break;").Append(newline);
            }

            if (defaultStart >= 0)
            {
                string body = GetStatementBodyText(documentText, tokens, defaultStart, defaultEnd);
                bool hasTerminator = StatementEndsWithTerminator(tokens, defaultStart, defaultEnd);
                string reindented = Reindent(body, caseBodyIndent, newline);

                sb.Append(baseIndent).Append(indentUnit).Append("default:").Append(newline);
                if (reindented.Length > 0)
                    sb.Append(reindented).Append(newline);
                if (!hasTerminator)
                    sb.Append(caseBodyIndent).Append("break;").Append(newline);
            }

            sb.Append(baseIndent).Append('}');

            int lastToken = defaultEnd >= 0 ? defaultEnd : branches[^1].BodyEnd;
            int offset = tokens[ifIndex].Start;
            int length = tokens[lastToken].End - offset;
            edit = new TextEdit(offset, length, sb.ToString(), offset, 0);
            return true;
        }

        /// <summary>
        /// Wraps the selection (or inserts at the caret) with the requested statement template.
        /// Supported templates: <c>for</c>, <c>while</c>, <c>repeat</c>, <c>with</c>, <c>until</c>,
        /// <c>if</c>, <c>block</c>.
        /// </summary>
        public static bool TrySurround(string documentText, int selectionStart, int selectionLength, string template,
                                       out TextEdit edit, out string error)
        {
            edit = default;
            error = null;

            if (template is not ("for" or "while" or "repeat" or "with" or "until" or "if" or "block"))
            {
                error = "unknown template";
                return false;
            }

            string newline = DetectNewline(documentText);
            int lineStart = GetLineStart(documentText, selectionStart);
            string baseIndent = GetLineIndent(documentText, selectionStart);
            string indentUnit = baseIndent.Contains('\t') ? "\t" : "    ";
            int insertOffset = selectionStart;

            // If the selection/caret starts after indentation only, replace that indentation too,
            // so the generated template is not indented twice and wrapped lines re-indent correctly.
            string linePrefix = documentText.Substring(lineStart, selectionStart - lineStart);
            if (linePrefix.All(c => c == ' ' || c == '\t'))
            {
                insertOffset = lineStart;
                baseIndent = linePrefix;
                indentUnit = baseIndent.Contains('\t') ? "\t" : "    ";
            }

            int replaceLength = selectionLength + (selectionStart - insertOffset);
            string selected = selectionLength > 0
                ? documentText.Substring(insertOffset, replaceLength)
                : null;
            string body = selected is null ? "" : Reindent(selected, baseIndent + indentUnit, newline);

            bool appendNewline = false;
            if (selectionLength == 0 && insertOffset != selectionStart)
            {
                int lineEnd = selectionStart;
                while (lineEnd < documentText.Length &&
                       documentText[lineEnd] != '\n' && documentText[lineEnd] != '\r')
                {
                    lineEnd++;
                }
                appendNewline = documentText.Substring(selectionStart, lineEnd - selectionStart).Trim().Length > 0;
            }

            StringBuilder sb = new();
            int placeholderOffset = -1;
            int placeholderLength = 0;
            int bodyCaretOffset = -1;

            void AppendPlaceholder(string placeholder)
            {
                placeholderOffset = sb.Length;
                placeholderLength = placeholder.Length;
                sb.Append(placeholder);
            }

            void AppendBlock()
            {
                sb.Append(baseIndent).Append('{').Append(newline);
                bodyCaretOffset = sb.Length;
                if (body.Length > 0)
                    sb.Append(body).Append(newline);
                sb.Append(baseIndent).Append('}');
            }

            switch (template)
            {
                case "if":
                    sb.Append(baseIndent).Append("if (");
                    AppendPlaceholder("true");
                    sb.Append(')').Append(newline);
                    AppendBlock();
                    break;

                case "for":
                    sb.Append(baseIndent).Append("for (var i = 0; i < ");
                    AppendPlaceholder("10");
                    sb.Append("; i++)").Append(newline);
                    AppendBlock();
                    break;

                case "while":
                    sb.Append(baseIndent).Append("while (");
                    AppendPlaceholder("true");
                    sb.Append(')').Append(newline);
                    AppendBlock();
                    break;

                case "repeat":
                    sb.Append(baseIndent).Append("repeat (");
                    AppendPlaceholder("1");
                    sb.Append(')').Append(newline);
                    AppendBlock();
                    break;

                case "with":
                    sb.Append(baseIndent).Append("with (");
                    AppendPlaceholder("all");
                    sb.Append(')').Append(newline);
                    AppendBlock();
                    break;

                case "until":
                    sb.Append(baseIndent).Append("do").Append(newline);
                    AppendBlock();
                    sb.Append(newline).Append(baseIndent).Append("until (");
                    AppendPlaceholder("true");
                    sb.Append(')');
                    break;

                default:
                    AppendBlock();
                    break;
            }

            string newText = sb.ToString();
            if (appendNewline)
                newText += newline;

            int selectionOffset;
            int selectionLen;
            if (placeholderOffset >= 0)
            {
                selectionOffset = insertOffset + placeholderOffset;
                selectionLen = placeholderLength;
            }
            else
            {
                selectionOffset = insertOffset + Math.Max(0, bodyCaretOffset);
                selectionLen = 0;
            }

            edit = new TextEdit(insertOffset, replaceLength, newText, selectionOffset, selectionLen);
            return true;
        }

        /// <summary>
        /// Re-indents a block of source text so that every line uses <paramref name="indent"/> as its base indentation.
        /// </summary>
        public static string IndentBlock(string text, string indent, string newline)
        {
            return Reindent(text, indent, newline);
        }

        /// <summary>
        /// How occurrences of an identifier should be matched during a rename.
        /// </summary>
        public enum IdentifierMatch
        {
            /// <summary>Any identifier occurrence.</summary>
            Any,
            /// <summary>Only bare identifiers (not preceded by a '.', so not a member access).</summary>
            Bare,
            /// <summary>Only identifiers not using the <c>global.</c> prefix.</summary>
            NonGlobal,
            /// <summary>Only identifiers using the <c>global.</c> prefix, or declared with <c>globalvar</c>.</summary>
            Global
        }

        /// <summary>
        /// Finds all identifier tokens (outside of strings and comments) matching <paramref name="name"/>.
        /// </summary>
        public static IReadOnlyList<(int Offset, int Length)> FindIdentifierOccurrences(string text, string name,
                                                                                          IdentifierMatch match = IdentifierMatch.Any)
        {
            List<(int Offset, int Length)> result = new();
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(name))
                return result;

            foreach (Token token in Tokenize(text))
            {
                if (token.Kind != TokenKind.Identifier || token.Text != name)
                    continue;
                if (!MatchesContext(text, token.Start, match))
                    continue;
                result.Add((token.Start, token.End - token.Start));
            }
            return result;
        }

        /// <summary>
        /// Replaces all identifier tokens (outside of strings and comments) matching <paramref name="oldName"/>.
        /// </summary>
        public static string ReplaceIdentifier(string text, string oldName, string newName,
                                               IdentifierMatch match = IdentifierMatch.Any)
        {
            IReadOnlyList<(int Offset, int Length)> occurrences = FindIdentifierOccurrences(text, oldName, match);
            if (occurrences.Count == 0)
                return text;

            StringBuilder sb = new(text.Length + (newName.Length - oldName.Length) * occurrences.Count);
            int last = 0;
            foreach ((int offset, int length) in occurrences)
            {
                sb.Append(text, last, offset - last);
                sb.Append(newName);
                last = offset + length;
            }
            sb.Append(text, last, text.Length - last);
            return sb.ToString();
        }

        private static bool MatchesContext(string text, int tokenStart, IdentifierMatch match)
        {
            if (match == IdentifierMatch.Any)
                return true;

            bool precededByDot = false;
            int previous = tokenStart - 1;
            while (previous >= 0 && char.IsWhiteSpace(text[previous]))
                previous--;
            if (previous >= 0 && text[previous] == '.')
                precededByDot = true;

            bool precededByGlobalDot = false;
            bool precededByGlobalVar = false;
            if (previous >= 0)
            {
                int wordEnd = previous + 1;
                int wordStart = previous;
                while (wordStart >= 0 && (char.IsLetterOrDigit(text[wordStart]) || text[wordStart] == '_'))
                    wordStart--;
                string previousWord = text.Substring(wordStart + 1, wordEnd - wordStart - 1);
                precededByGlobalVar = previousWord == "globalvar";

                if (precededByDot)
                {
                    int beforeDot = previous - 1;
                    while (beforeDot >= 0 && char.IsWhiteSpace(text[beforeDot]))
                        beforeDot--;
                    int dotWordEnd = beforeDot + 1;
                    int dotWordStart = beforeDot;
                    while (dotWordStart >= 0 && (char.IsLetterOrDigit(text[dotWordStart]) || text[dotWordStart] == '_'))
                        dotWordStart--;
                    precededByGlobalDot = text.Substring(dotWordStart + 1, dotWordEnd - dotWordStart - 1) == "global";
                }
            }

            return match switch
            {
                IdentifierMatch.Bare => !precededByDot,
                IdentifierMatch.NonGlobal => !precededByGlobalDot,
                IdentifierMatch.Global => precededByGlobalDot || precededByGlobalVar,
                _ => true
            };
        }

        // ---------------------------------------------------------------------
        // Tokenizer
        // ---------------------------------------------------------------------

        private static readonly string[] MultiCharSymbols =
        {
            "??=", "&&", "||", "==", "!=", "<=", ">=", "++", "--",
            "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<", ">>", "??"
        };

        private static List<Token> Tokenize(string text)
        {
            List<Token> tokens = new(text.Length / 4);
            int i = 0;
            int n = text.Length;
            while (i < n)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                int start = i;

                if (c == '/' && i + 1 < n && text[i + 1] == '/')
                {
                    i += 2;
                    while (i < n && text[i] != '\n')
                        i++;
                    tokens.Add(new Token { Kind = TokenKind.Comment, Start = start, End = i, Text = text.Substring(start, i - start) });
                    continue;
                }

                if (c == '/' && i + 1 < n && text[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < n && !(text[i] == '*' && text[i + 1] == '/'))
                        i++;
                    i = Math.Min(n, i + 2);
                    tokens.Add(new Token { Kind = TokenKind.Comment, Start = start, End = i, Text = text.Substring(start, i - start) });
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    char quote = c;
                    i++;
                    while (i < n)
                    {
                        if (text[i] == '\\' && i + 1 < n)
                        {
                            i += 2;
                            continue;
                        }
                        if (text[i] == quote)
                        {
                            i++;
                            break;
                        }
                        i++;
                    }
                    tokens.Add(new Token { Kind = TokenKind.String, Start = start, End = i, Text = text.Substring(start, i - start) });
                    continue;
                }

                if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(text[i + 1])))
                {
                    i++;
                    while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '.' || text[i] == '_'))
                        i++;
                    tokens.Add(new Token { Kind = TokenKind.Number, Start = start, End = i, Text = text.Substring(start, i - start) });
                    continue;
                }

                if (char.IsLetter(c) || c == '_')
                {
                    i++;
                    while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                        i++;
                    tokens.Add(new Token { Kind = TokenKind.Identifier, Start = start, End = i, Text = text.Substring(start, i - start) });
                    continue;
                }

                string op = null;
                foreach (string candidate in MultiCharSymbols)
                {
                    if (i + candidate.Length <= n && string.CompareOrdinal(text, i, candidate, 0, candidate.Length) == 0)
                    {
                        op = candidate;
                        break;
                    }
                }
                if (op is not null)
                {
                    i += op.Length;
                    tokens.Add(new Token { Kind = TokenKind.Symbol, Start = start, End = i, Text = op });
                    continue;
                }

                i++;
                tokens.Add(new Token { Kind = TokenKind.Symbol, Start = start, End = i, Text = text.Substring(start, 1) });
            }
            return tokens;
        }

        // ---------------------------------------------------------------------
        // Token / statement helpers
        // ---------------------------------------------------------------------

        private static int NextCode(List<Token> tokens, int index)
        {
            while (index < tokens.Count && tokens[index].Kind == TokenKind.Comment)
                index++;
            return index;
        }

        private static int LastCodeBefore(List<Token> tokens, int index)
        {
            index--;
            while (index >= 0 && tokens[index].Kind == TokenKind.Comment)
                index--;
            return index;
        }

        private static bool IsSymbol(List<Token> tokens, int index, string symbol)
        {
            return index >= 0 && index < tokens.Count &&
                   tokens[index].Kind == TokenKind.Symbol && tokens[index].Text == symbol;
        }

        private static bool IsWord(List<Token> tokens, int index, string word)
        {
            return index >= 0 && index < tokens.Count &&
                   tokens[index].Kind == TokenKind.Identifier && tokens[index].Text == word;
        }

        private static int FindMatching(List<Token> tokens, int openIndex, string openSymbol, string closeSymbol)
        {
            if (openIndex < 0 || openIndex >= tokens.Count || !IsSymbol(tokens, openIndex, openSymbol))
                return -1;

            int depth = 0;
            for (int i = openIndex; i < tokens.Count; i++)
            {
                if (tokens[i].Kind != TokenKind.Symbol)
                    continue;
                if (tokens[i].Text == openSymbol)
                    depth++;
                else if (tokens[i].Text == closeSymbol)
                {
                    depth--;
                    if (depth == 0)
                        return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Returns the index of the last token of the statement starting at <paramref name="index"/>, or -1.
        /// </summary>
        private static int ParseStatementEnd(List<Token> tokens, int index)
        {
            index = NextCode(tokens, index);
            if (index < 0 || index >= tokens.Count)
                return -1;

            Token first = tokens[index];
            if (first.Kind == TokenKind.Symbol)
            {
                if (first.Text == "{")
                    return FindMatching(tokens, index, "{", "}");
            }
            else if (first.Kind == TokenKind.Identifier)
            {
                switch (first.Text)
                {
                    case "if":
                    {
                        int paren = NextCode(tokens, index + 1);
                        if (!IsSymbol(tokens, paren, "("))
                            return -1;
                        int parenClose = FindMatching(tokens, paren, "(", ")");
                        if (parenClose < 0)
                            return -1;
                        int bodyEnd = ParseStatementEnd(tokens, parenClose + 1);
                        if (bodyEnd < 0)
                            return -1;
                        int elseIndex = NextCode(tokens, bodyEnd + 1);
                        if (IsWord(tokens, elseIndex, "else"))
                        {
                            int elseEnd = ParseStatementEnd(tokens, elseIndex + 1);
                            if (elseEnd >= 0)
                                return elseEnd;
                        }
                        return bodyEnd;
                    }

                    case "switch":
                    {
                        int paren = NextCode(tokens, index + 1);
                        if (!IsSymbol(tokens, paren, "("))
                            return -1;
                        int parenClose = FindMatching(tokens, paren, "(", ")");
                        if (parenClose < 0)
                            return -1;
                        int openBrace = NextCode(tokens, parenClose + 1);
                        if (!IsSymbol(tokens, openBrace, "{"))
                            return -1;
                        return FindMatching(tokens, openBrace, "{", "}");
                    }

                    case "for":
                    case "while":
                    case "with":
                    case "repeat":
                    {
                        int paren = NextCode(tokens, index + 1);
                        if (!IsSymbol(tokens, paren, "("))
                            return -1;
                        int parenClose = FindMatching(tokens, paren, "(", ")");
                        if (parenClose < 0)
                            return -1;
                        return ParseStatementEnd(tokens, parenClose + 1);
                    }

                    case "do":
                    {
                        int bodyEnd = ParseStatementEnd(tokens, index + 1);
                        if (bodyEnd < 0)
                            return -1;
                        int untilIndex = NextCode(tokens, bodyEnd + 1);
                        if (!IsWord(tokens, untilIndex, "until"))
                            return bodyEnd;
                        int paren = NextCode(tokens, untilIndex + 1);
                        if (!IsSymbol(tokens, paren, "("))
                            return bodyEnd;
                        int parenClose = FindMatching(tokens, paren, "(", ")");
                        return parenClose >= 0 ? parenClose : bodyEnd;
                    }

                    case "else":
                        return ParseStatementEnd(tokens, index + 1);

                    case "function":
                    {
                        int j = NextCode(tokens, index + 1);
                        if (j < tokens.Count && tokens[j].Kind == TokenKind.Identifier)
                            j = NextCode(tokens, j + 1);
                        if (!IsSymbol(tokens, j, "("))
                            return -1;
                        int parenClose = FindMatching(tokens, j, "(", ")");
                        if (parenClose < 0)
                            return -1;
                        int openBrace = NextCode(tokens, parenClose + 1);
                        if (IsWord(tokens, openBrace, "constructor"))
                            openBrace = NextCode(tokens, openBrace + 1);
                        if (!IsSymbol(tokens, openBrace, "{"))
                            return -1;
                        return FindMatching(tokens, openBrace, "{", "}");
                    }
                }
            }

            // Generic statement/expression: end at a top-level ';', or just before a top-level '}'.
            int parenDepth = 0;
            int bracketDepth = 0;
            int braceDepth = 0;
            for (int i = index; i < tokens.Count; i++)
            {
                if (tokens[i].Kind != TokenKind.Symbol)
                    continue;

                switch (tokens[i].Text)
                {
                    case "(": parenDepth++; break;
                    case ")": parenDepth--; break;
                    case "[": bracketDepth++; break;
                    case "]": bracketDepth--; break;
                    case "{": braceDepth++; break;
                    case "}":
                        if (braceDepth == 0)
                            return i > index ? i - 1 : index;
                        braceDepth--;
                        break;
                    case ";":
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                            return i;
                        break;
                }
            }
            return tokens.Count - 1;
        }

        private static int FindConstruct(List<Token> tokens, string keyword, int selectionStart, int selectionLength, out int endIndex)
        {
            endIndex = -1;
            int selectionEnd = selectionStart + selectionLength;
            bool hasSelection = selectionLength > 0;

            int best = -1;
            int bestEnd = -1;

            if (hasSelection)
            {
                int bestStart = int.MaxValue;
                for (int i = 0; i < tokens.Count; i++)
                {
                    if (tokens[i].Kind != TokenKind.Identifier || tokens[i].Text != keyword)
                        continue;
                    int end = ParseStatementEnd(tokens, i);
                    if (end < i)
                        continue;
                    int startPos = tokens[i].Start;
                    int endPos = tokens[end].End;
                    if (startPos >= selectionStart && endPos <= selectionEnd && startPos < bestStart)
                    {
                        best = i;
                        bestEnd = end;
                        bestStart = startPos;
                    }
                }
                if (best >= 0)
                {
                    endIndex = bestEnd;
                    return best;
                }
            }

            int bestSpan = int.MaxValue;
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Kind != TokenKind.Identifier || tokens[i].Text != keyword)
                    continue;
                int end = ParseStatementEnd(tokens, i);
                if (end < i)
                    continue;
                int startPos = tokens[i].Start;
                int endPos = tokens[end].End;
                if (startPos <= selectionStart && selectionEnd <= endPos)
                {
                    int span = endPos - startPos;
                    if (span < bestSpan)
                    {
                        best = i;
                        bestEnd = end;
                        bestSpan = span;
                    }
                }
            }

            endIndex = bestEnd;
            return best;
        }

        // ---------------------------------------------------------------------
        // Switch parsing / conversion
        // ---------------------------------------------------------------------

        private static bool ParseSwitchCases(List<Token> tokens, int openBrace, int closeBrace,
                                             out List<SwitchCase> cases, out string error)
        {
            cases = new List<SwitchCase>();
            error = null;

            int current = -1;
            int paren = 0;
            int bracket = 0;
            int brace = 0;
            int ternary = 0;

            for (int i = openBrace + 1; i < closeBrace; i++)
            {
                if (tokens[i].Kind == TokenKind.Comment)
                    continue;

                bool atTop = paren == 0 && bracket == 0 && brace == 0;

                if (tokens[i].Kind == TokenKind.Symbol)
                {
                    switch (tokens[i].Text)
                    {
                        case "(": paren++; continue;
                        case ")": paren--; continue;
                        case "[": bracket++; continue;
                        case "]": bracket--; continue;
                        case "{": brace++; continue;
                        case "}": brace--; continue;
                        case "?": if (atTop) ternary++; continue;
                        case ":":
                            if (ternary > 0)
                                ternary--;
                            continue;
                    }
                }

                if (!atTop || tokens[i].Kind != TokenKind.Identifier ||
                    (tokens[i].Text != "case" && tokens[i].Text != "default"))
                {
                    continue;
                }

                if (current >= 0)
                {
                    int previousEnd = LastCodeBefore(tokens, i);
                    cases[current].BodyEnd = previousEnd;
                    if (cases[current].BodyStart < 0 || previousEnd < cases[current].BodyStart)
                        cases[current].BodyStart = cases[current].BodyEnd = -1;
                }

                int colon = FindCaseColon(tokens, i + 1, closeBrace,
                                          out int exprStart, out int exprEnd);
                if (colon < 0)
                {
                    error = "case label without ':'";
                    return false;
                }

                SwitchCase parsed = new()
                {
                    IsDefault = tokens[i].Text == "default",
                    ExprStart = exprStart,
                    ExprEnd = exprEnd
                };
                int bodyStart = NextCode(tokens, colon + 1);
                if (bodyStart < closeBrace)
                {
                    parsed.BodyStart = bodyStart;
                    parsed.BodyEnd = -1;
                }
                cases.Add(parsed);
                current = cases.Count - 1;

                i = colon;
                paren = bracket = brace = ternary = 0;
            }

            if (current >= 0)
            {
                int previousEnd = LastCodeBefore(tokens, closeBrace);
                cases[current].BodyEnd = previousEnd;
                if (cases[current].BodyStart < 0 || previousEnd < cases[current].BodyStart)
                    cases[current].BodyStart = cases[current].BodyEnd = -1;
            }

            if (cases.Count == 0)
            {
                error = "switch has no cases";
                return false;
            }
            if (cases.Count(c => c.IsDefault) > 1)
            {
                error = "switch has multiple default labels";
                return false;
            }
            return true;
        }

        private static int FindCaseColon(List<Token> tokens, int start, int limit,
                                         out int exprStart, out int exprEnd)
        {
            exprStart = -1;
            exprEnd = -1;

            int paren = 0;
            int bracket = 0;
            int brace = 0;
            int ternary = 0;

            for (int i = start; i < limit; i++)
            {
                if (tokens[i].Kind == TokenKind.Comment)
                    continue;

                if (exprStart < 0)
                    exprStart = i;

                if (tokens[i].Kind != TokenKind.Symbol)
                {
                    exprEnd = i;
                    continue;
                }

                switch (tokens[i].Text)
                {
                    case "(": paren++; break;
                    case ")": paren--; break;
                    case "[": bracket++; break;
                    case "]": bracket--; break;
                    case "{": brace++; break;
                    case "}": brace--; break;
                    case "?":
                        if (paren == 0 && bracket == 0 && brace == 0)
                            ternary++;
                        break;
                    case ":":
                        if (paren == 0 && bracket == 0 && brace == 0)
                        {
                            if (ternary > 0)
                            {
                                ternary--;
                            }
                            else
                            {
                                exprEnd = LastCodeBefore(tokens, i);
                                if (exprEnd < start)
                                    exprStart = exprEnd = -1;
                                return i;
                            }
                        }
                        break;
                }
            }
            return -1;
        }

        private static bool ValidateCaseBreak(List<Token> tokens, SwitchCase switchCase,
                                              out bool stripBreak, out int breakToken)
        {
            stripBreak = false;
            breakToken = -1;

            if (switchCase.BodyStart < 0)
                return true;

            int lastStart = -1;
            int i = switchCase.BodyStart;
            while (i <= switchCase.BodyEnd)
            {
                i = NextCode(tokens, i);
                if (i < 0 || i > switchCase.BodyEnd)
                    break;
                lastStart = i;
                int statementEnd = ParseStatementEnd(tokens, i);
                if (statementEnd < 0 || statementEnd > switchCase.BodyEnd)
                    statementEnd = switchCase.BodyEnd;
                i = statementEnd + 1;
            }

            if (lastStart >= 0 && IsWord(tokens, lastStart, "break"))
            {
                int semicolon = NextCode(tokens, lastStart + 1);
                if (IsSymbol(tokens, semicolon, ";") && NextCode(tokens, semicolon + 1) > switchCase.BodyEnd)
                {
                    stripBreak = true;
                    breakToken = lastStart;
                }
            }

            return !ContainsBreak(tokens, switchCase.BodyStart, switchCase.BodyEnd, breakToken);
        }

        private static bool ContainsBreak(List<Token> tokens, int start, int end, int excludedToken)
        {
            int i = start;
            while (i <= end)
            {
                i = NextCode(tokens, i);
                if (i < 0 || i > end)
                    break;

                if (i == excludedToken)
                {
                    i = NextCode(tokens, i + 1);
                    continue;
                }

                if (tokens[i].Kind == TokenKind.Identifier)
                {
                    string word = tokens[i].Text;
                    if (word == "break")
                        return true;

                    if (word is "for" or "while" or "repeat" or "with" or "do" or "switch" or "function")
                    {
                        int statementEnd = ParseStatementEnd(tokens, i);
                        if (statementEnd < 0 || statementEnd > end)
                            statementEnd = end;
                        i = statementEnd + 1;
                        continue;
                    }
                }

                if (IsSymbol(tokens, i, "{"))
                {
                    int close = FindMatching(tokens, i, "{", "}");
                    if (close < 0 || close > end)
                        close = end;
                    if (ContainsBreak(tokens, i + 1, close - 1, excludedToken))
                        return true;
                    i = close + 1;
                    continue;
                }

                i++;
            }
            return false;
        }

        private static bool EndsWithTerminator(List<Token> tokens, SwitchCase switchCase)
        {
            if (switchCase.BodyStart < 0)
                return false;

            int lastStart = -1;
            int i = switchCase.BodyStart;
            while (i <= switchCase.BodyEnd)
            {
                i = NextCode(tokens, i);
                if (i < 0 || i > switchCase.BodyEnd)
                    break;
                lastStart = i;
                int statementEnd = ParseStatementEnd(tokens, i);
                if (statementEnd < 0 || statementEnd > switchCase.BodyEnd)
                    statementEnd = switchCase.BodyEnd;
                i = statementEnd + 1;
            }

            if (lastStart < 0 || tokens[lastStart].Kind != TokenKind.Identifier)
                return false;

            string word = tokens[lastStart].Text;
            return word is "break" or "return" or "exit" or "continue" or "throw";
        }

        private static string GetCaseBodyText(string text, List<Token> tokens, SwitchCase switchCase,
                                              bool stripBreak, int breakToken)
        {
            if (switchCase.BodyStart < 0 || switchCase.BodyEnd < switchCase.BodyStart)
                return "";

            int start = tokens[switchCase.BodyStart].Start;
            int end = tokens[switchCase.BodyEnd].End;
            if (stripBreak && breakToken >= 0)
                end = tokens[breakToken].Start;
            if (end <= start)
                return "";
            return text.Substring(start, end - start);
        }

        private static bool HasTopLevelComma(List<Token> tokens, int start, int end)
        {
            int paren = 0;
            int bracket = 0;
            int brace = 0;
            for (int i = start; i <= end; i++)
            {
                if (tokens[i].Kind != TokenKind.Symbol)
                    continue;

                switch (tokens[i].Text)
                {
                    case "(": paren++; break;
                    case ")": paren--; break;
                    case "[": bracket++; break;
                    case "]": bracket--; break;
                    case "{": brace++; break;
                    case "}": brace--; break;
                    case ",":
                        if (paren == 0 && bracket == 0 && brace == 0)
                            return true;
                        break;
                }
            }
            return false;
        }

        // ---------------------------------------------------------------------
        // If parsing / conversion
        // ---------------------------------------------------------------------

        private static bool TrySplitEquality(string text, List<Token> tokens, int start, int end,
                                             out string subject, out string value)
        {
            subject = null;
            value = null;
            if (start < 0 || end < start)
                return false;

            int paren = 0;
            int bracket = 0;
            int equalityIndex = -1;
            int equalityCount = 0;

            for (int i = start; i <= end; i++)
            {
                if (tokens[i].Kind != TokenKind.Symbol)
                    continue;

                switch (tokens[i].Text)
                {
                    case "(": paren++; break;
                    case ")": paren--; break;
                    case "[": bracket++; break;
                    case "]": bracket--; break;
                    case "&&":
                    case "||":
                        if (paren == 0 && bracket == 0)
                            return false;
                        break;
                    case "==":
                        if (paren == 0 && bracket == 0)
                        {
                            equalityCount++;
                            equalityIndex = i;
                        }
                        break;
                }
            }

            if (equalityCount != 1 || equalityIndex <= start || equalityIndex >= end)
                return false;

            int subjectEnd = LastCodeBefore(tokens, equalityIndex);
            int valueStart = NextCode(tokens, equalityIndex + 1);
            if (subjectEnd < start || valueStart > end)
                return false;

            subject = text.Substring(tokens[start].Start, tokens[subjectEnd].End - tokens[start].Start).Trim();
            value = text.Substring(tokens[valueStart].Start, tokens[end].End - tokens[valueStart].Start).Trim();
            return subject.Length > 0 && value.Length > 0;
        }

        private static string GetStatementBodyText(string text, List<Token> tokens, int start, int end)
        {
            if (start < 0 || end < start)
                return "";

            int bodyStart = start;
            int bodyEnd = end;
            if (IsSymbol(tokens, start, "{") && FindMatching(tokens, start, "{", "}") == end)
            {
                bodyStart = NextCode(tokens, start + 1);
                bodyEnd = LastCodeBefore(tokens, end);
                if (bodyStart < 0 || bodyEnd < bodyStart)
                    return "";
            }

            return text.Substring(tokens[bodyStart].Start, tokens[bodyEnd].End - tokens[bodyStart].Start);
        }

        private static bool StatementEndsWithTerminator(List<Token> tokens, int start, int end)
        {
            if (start < 0 || end < start)
                return false;

            int bodyStart = start;
            int bodyEnd = end;
            if (IsSymbol(tokens, start, "{") && FindMatching(tokens, start, "{", "}") == end)
            {
                bodyStart = NextCode(tokens, start + 1);
                bodyEnd = LastCodeBefore(tokens, end);
                if (bodyStart < 0 || bodyEnd < bodyStart)
                    return false;
            }

            int lastStart = -1;
            int i = bodyStart;
            while (i <= bodyEnd)
            {
                i = NextCode(tokens, i);
                if (i < 0 || i > bodyEnd)
                    break;
                lastStart = i;
                int statementEnd = ParseStatementEnd(tokens, i);
                if (statementEnd < 0 || statementEnd > bodyEnd)
                    statementEnd = bodyEnd;
                i = statementEnd + 1;
            }

            if (lastStart < 0 || tokens[lastStart].Kind != TokenKind.Identifier)
                return false;

            string word = tokens[lastStart].Text;
            return word is "break" or "return" or "exit" or "continue" or "throw";
        }

        // ---------------------------------------------------------------------
        // Text helpers
        // ---------------------------------------------------------------------

        private static string Slice(string text, List<Token> tokens, int start, int end)
        {
            if (start < 0 || end < start || start >= tokens.Count || end >= tokens.Count)
                return "";
            int startPos = tokens[start].Start;
            int endPos = tokens[end].End;
            if (endPos <= startPos)
                return "";
            return text.Substring(startPos, endPos - startPos);
        }

        private static int GetLineStart(string text, int offset)
        {
            int lineStart = Math.Clamp(offset, 0, text.Length);
            while (lineStart > 0 && text[lineStart - 1] != '\n')
                lineStart--;
            return lineStart;
        }

        private static string GetLineIndent(string text, int offset)
        {
            int lineStart = GetLineStart(text, offset);
            int i = lineStart;
            while (i < text.Length && i < offset && (text[i] == ' ' || text[i] == '\t'))
                i++;
            return text.Substring(lineStart, i - lineStart);
        }

        private static string DetectNewline(string text)
        {
            return text.Contains("\r\n") ? "\r\n" : "\n";
        }

        /// <summary>
        /// Re-indents text: strips the common indentation of all lines and prefixes every line with <paramref name="indent"/>.
        /// </summary>
        private static string Reindent(string text, string indent, string newline)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');

            int first = 0;
            int last = lines.Length - 1;
            while (first <= last && lines[first].Trim().Length == 0)
                first++;
            while (last >= first && lines[last].Trim().Length == 0)
                last--;
            if (first > last)
                return "";

            string common = null;
            for (int i = first; i <= last; i++)
            {
                string line = lines[i];
                if (line.Trim().Length == 0)
                    continue;

                int whitespaceLength = 0;
                while (whitespaceLength < line.Length && (line[whitespaceLength] == ' ' || line[whitespaceLength] == '\t'))
                    whitespaceLength++;
                string whitespace = line.Substring(0, whitespaceLength);

                if (common is null)
                {
                    common = whitespace;
                }
                else
                {
                    int k = 0;
                    while (k < common.Length && k < whitespace.Length && common[k] == whitespace[k])
                        k++;
                    common = common.Substring(0, k);
                }
            }
            common ??= "";

            StringBuilder sb = new();
            for (int i = first; i <= last; i++)
            {
                string line = lines[i];
                if (line.Trim().Length == 0)
                {
                    if (i > first)
                        sb.Append(newline);
                    continue;
                }

                string trimmed = line.StartsWith(common, StringComparison.Ordinal)
                    ? line.Substring(common.Length)
                    : line.TrimStart();

                if (i > first)
                    sb.Append(newline);
                sb.Append(indent).Append(trimmed);
            }
            return sb.ToString();
        }

        private static string NormalizeExpression(string expression)
        {
            StringBuilder sb = new(expression.Length);
            bool inString = false;
            char quote = '\0';

            for (int i = 0; i < expression.Length; i++)
            {
                char c = expression[i];
                if (inString)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < expression.Length)
                    {
                        sb.Append(expression[++i]);
                        continue;
                    }
                    if (c == quote)
                        inString = false;
                }
                else
                {
                    if (char.IsWhiteSpace(c))
                        continue;
                    if (c == '"' || c == '\'')
                    {
                        inString = true;
                        quote = c;
                    }
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }
}
