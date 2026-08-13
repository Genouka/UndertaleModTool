using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using UndertaleModLib;
using UndertaleModLib.Compiler;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;
using UndertaleModTool;
using Underanalyzer;
using Underanalyzer.Compiler;
using Underanalyzer.Compiler.Errors;

namespace UndertaleModTool.Editors
{
    /// <summary>
    /// A single diagnostic produced by analyzing GML source code.
    /// </summary>
    public readonly struct GmlDiagnostic
    {
        /// <summary>1-based line number (1 if unknown).</summary>
        public int Line { get; }

        /// <summary>1-based column number (1 if unknown).</summary>
        public int Column { get; }

        /// <summary>Absolute offset into the source document (clamped to valid range).</summary>
        public int TextPosition { get; }

        /// <summary>Length of the offending span in the document.</summary>
        public int Length { get; }

        /// <summary>Human readable error message.</summary>
        public string Message { get; }

        /// <summary>Whether this is an error (as opposed to a warning).</summary>
        public bool IsError { get; }

        public GmlDiagnostic(int line, int column, int textPosition, int length, string message, bool isError = true)
        {
            Line = line;
            Column = column;
            TextPosition = textPosition;
            Length = length;
            Message = message;
            IsError = isError;
        }

        public override string ToString() => $"{Line}:{Column} {Message}";
    }

    /// <summary>
    /// A reference (occurrence) of an identifier inside a code document.
    /// </summary>
    public readonly struct GmlReference
    {
        public int TextPosition { get; }
        public int Length { get; }
        public int Line { get; }
        public string LineText { get; }

        public GmlReference(int textPosition, int length, int line, string lineText)
        {
            TextPosition = textPosition;
            Length = length;
            Line = line;
            LineText = lineText;
        }
    }

    /// <summary>
    /// Result of resolving an identifier to its definition.
    /// </summary>
    public readonly struct GmlDefinition
    {
        public enum DefinitionKind
        {
            None,
            Local,      // local variable / function parameter / static variable, defined within the current code entry
            Function,   // a game function or script
            Resource,   // an asset (object, sprite, etc.)
            Constant,   // a constant or enum value
            Builtin     // a builtin function, variable or constant
        }

        public DefinitionKind Kind { get; }

        /// <summary>Name of the resolved identifier.</summary>
        public string Name { get; }

        /// <summary>For <see cref="DefinitionKind.Local"/>: offset of the declaration within the current document; otherwise -1.</summary>
        public int LocalDeclarationOffset { get; }

        /// <summary>For <see cref="DefinitionKind.Function"/>/<see cref="DefinitionKind.Resource"/>: the resource to open.</summary>
        public UndertaleNamedResource Resource { get; }

        public GmlDefinition(DefinitionKind kind, string name, int localDeclarationOffset = -1, UndertaleNamedResource resource = null)
        {
            Kind = kind;
            Name = name;
            LocalDeclarationOffset = localDeclarationOffset;
            Resource = resource;
        }

        public static readonly GmlDefinition None = new(DefinitionKind.None, null);
    }

    /// <summary>
    /// A single completion item offered by the IntelliSense completion window.
    /// </summary>
    public class GmlCompletionItem
    {
        /// <summary>Text to insert.</summary>
        public string Text { get; }

        /// <summary>Category of the item (function, variable, constant, ...).</summary>
        public string Kind { get; }

        /// <summary>Optional type/description suffix.</summary>
        public string Type { get; }

        public GmlCompletionItem(string text, string kind, string type)
        {
            Text = text;
            Kind = kind;
            Type = type;
        }
    }

    /// <summary>
    /// Provides editor-facing language features for GML source code: diagnostics,
    /// code completion, definition resolution, and reference finding.
    /// </summary>
    public static class GmlLanguageService
    {
        // One parse context per game data, shared across all editors and reused for background analysis.
        private static readonly ConditionalWeakTable<UndertaleData, GlobalDecompileContext> _parseContexts = new();
        private static readonly object _parseContextLock = new();

        // Cached base completion list (functions, variables, constants, assets) per game data.
        private static readonly ConditionalWeakTable<UndertaleData, List<GmlCompletionItem>> _completionCache = new();
        private static readonly object _completionCacheLock = new();

        // GML keywords, used both for completion and for filtering out document words.
        private static readonly HashSet<string> _keywords = new(StringComparer.Ordinal)
        {
            "if", "then", "else", "switch", "case", "default", "break", "continue", "exit", "return",
            "while", "for", "repeat", "do", "until", "with", "var", "globalvar", "not", "and", "or",
            "xor", "div", "mod", "enum", "try", "catch", "finally", "throw", "new", "delete",
            "function", "static", "constructor", "begin", "end"
        };

        private static readonly Regex _wordRegex = new(@"\b[A-Za-z_][A-Za-z0-9_]*\b", RegexOptions.Compiled);

        /// <summary>
        /// Gets (or creates) a cached <see cref="GlobalDecompileContext"/> for the given game data,
        /// prepared for compilation. Used by the background parser for diagnostics.
        /// </summary>
        public static GlobalDecompileContext GetParseContext(UndertaleData data)
        {
            lock (_parseContextLock)
            {
                if (!_parseContexts.TryGetValue(data, out GlobalDecompileContext context))
                {
                    context = new GlobalDecompileContext(data);
                    try
                    {
                        context.PrepareForCompilation(false);
                    }
                    catch
                    {
                        // Some game data cannot be prepared for compilation; diagnostics will be unavailable.
                    }
                    _parseContexts.Add(data, context);
                }
                return context;
            }
        }

        /// <summary>
        /// Mirrors <c>UndertaleModLib.Compiler.CompileGroup</c>'s script-kind guessing logic.
        /// </summary>
        public static CompileScriptKind GuessScriptKindFromName(string codeName)
        {
            if (codeName is null)
                return CompileScriptKind.Script;
            if (codeName.StartsWith("gml_GlobalScript_", StringComparison.Ordinal))
                return CompileScriptKind.GlobalScript;
            if (codeName.StartsWith("gml_Object", StringComparison.Ordinal))
                return CompileScriptKind.ObjectEvent;
            if (codeName.StartsWith("gml_Room", StringComparison.Ordinal))
                return CompileScriptKind.RoomCreationCode;
            if (codeName.StartsWith("Timeline", StringComparison.Ordinal))
                return CompileScriptKind.Timeline;
            return CompileScriptKind.Script;
        }

        /// <summary>
        /// Parses the given GML source code and returns a list of diagnostics (errors) with positions.
        /// </summary>
        /// <remarks>
        /// Uses Underanalyzer's parser on a shared game context. Errors that carry position information
        /// are mapped back into <see cref="GmlDiagnostic"/> entries. Runs synchronously; call from a
        /// background thread to avoid blocking the UI.
        /// </remarks>
        public static IReadOnlyList<GmlDiagnostic> ParseDiagnostics(UndertaleData data, string code, string codeName)
        {
            if (data is null || code is null)
                return Array.Empty<GmlDiagnostic>();

            GlobalDecompileContext context;
            try
            {
                context = GetParseContext(data);
            }
            catch
            {
                return Array.Empty<GmlDiagnostic>();
            }

            CompileScriptKind scriptKind = GuessScriptKindFromName(codeName);

            List<GmlDiagnostic> diagnostics = null;
            try
            {
                CompileContext compileContext = new(code, scriptKind, null, context);
                compileContext.Parse();

                if (compileContext.HasErrors)
                {
                    diagnostics = new List<GmlDiagnostic>(compileContext.Errors.Count);
                    foreach (ICompileError error in compileContext.Errors)
                    {
                        int line = 1, column = 1, textPos = -1;
                        if (error is IPositionedCompileError positioned)
                        {
                            line = positioned.Line ?? 1;
                            column = positioned.Column ?? 1;
                            textPos = positioned.TextPosition ?? -1;
                        }

                        // Clamp text position into the document
                        int length = 1;
                        if (textPos < 0 || textPos >= code.Length)
                        {
                            textPos = Math.Min(Math.Max(textPos, 0), code.Length);
                        }

                        // Expand the span to the full word (or to end of line) for a nicer squiggle
                        if (textPos < code.Length)
                        {
                            int start = textPos;
                            int end = textPos;
                            while (start > 0 && IsWordChar(code[start - 1])) start--;
                            while (end < code.Length && IsWordChar(code[end])) end++;
                            if (end - start > 1)
                            {
                                textPos = start;
                                length = end - start;
                            }
                            else
                            {
                                length = Math.Max(1, end - textPos);
                            }
                        }
                        else
                        {
                            length = 1;
                        }

                        string message;
                        try
                        {
                            message = error.GenerateMessage();
                        }
                        catch
                        {
                            message = error.BaseMessage ?? "Compilation error";
                        }

                        diagnostics.Add(new GmlDiagnostic(line, column, textPos, length, message, true));
                    }
                }
            }
            catch (Exception ex)
            {
                // Unexpected analysis failure; surface as a single diagnostic rather than crashing the editor
                diagnostics ??= new List<GmlDiagnostic>();
                diagnostics.Add(new GmlDiagnostic(1, 1, 0, 1, "Analysis error: " + ex.Message, true));
            }

            return diagnostics ?? (IReadOnlyList<GmlDiagnostic>)Array.Empty<GmlDiagnostic>();
        }

        /// <summary>
        /// Gets the identifier word at the given offset.
        /// </summary>
        public static string GetWordAtOffset(string code, int offset, out int wordStart, out int wordEnd)
        {
            wordStart = -1;
            wordEnd = -1;
            if (code is null || offset < 0 || offset > code.Length)
                return null;

            int start = offset;
            int end = offset;
            while (start > 0 && IsWordChar(code[start - 1])) start--;
            while (end < code.Length && IsWordChar(code[end])) end++;
            if (start == end)
                return null;
            wordStart = start;
            wordEnd = end;
            return code.Substring(start, end - start);
        }

        /// <summary>
        /// Resolves the identifier at the given offset to its definition.
        /// </summary>
        public static GmlDefinition ResolveDefinition(UndertaleData data, string code, int offset, IList<string> locals,
                                                      UndertaleCode currentCode, IDictionary<string, UndertaleNamedResource> resources,
                                                      IDictionary<string, UndertaleNamedResource> scripts,
                                                      IDictionary<string, UndertaleNamedResource> functions,
                                                      IDictionary<string, UndertaleNamedResource> codeDict)
        {
            string word = GetWordAtOffset(code, offset, out int wordStart, out int wordEnd);
            if (word is null || data is null)
                return GmlDefinition.None;

            // 1. Local variables (declared with "var", function parameters, static locals, or known game locals)
            if (locals is not null && locals.Contains(word))
            {
                int declOffset = FindLocalDeclaration(code, word, wordStart);
                return new GmlDefinition(GmlDefinition.DefinitionKind.Local, word, declOffset);
            }

            // 2. Functions / scripts
            if (IsFunctionCall(code, wordStart, wordEnd))
            {
                UndertaleNamedResource val = null;
                if (!data.IsVersionAtLeast(2, 3))
                    scripts?.TryGetValue(word, out val);
                if (val is null)
                    functions?.TryGetValue(word, out val);
                if (val is not null && data.IsVersionAtLeast(2, 3) && val is UndertaleScript)
                {
                    // In GMS2.3, script assets are never called directly; resolve sub-functions to their parent entry
                    if (codeDict is not null && codeDict.TryGetValue(val.Name.Content, out UndertaleNamedResource parentCode))
                        val = parentCode;
                }
                if (val is not null)
                {
                    if (data.IsVersionAtLeast(2, 3) && val is UndertaleFunction f)
                    {
                        if (codeDict is not null && codeDict.TryGetValue(f.Name.Content, out UndertaleNamedResource parentCode))
                            val = parentCode;
                    }
                    return new GmlDefinition(GmlDefinition.DefinitionKind.Function, word, resource: val);
                }

                // GMS2.3+ global functions defined in global scripts
                if (data.GlobalFunctions?.TryGetFunction(word, out IGMFunction globalFunc) == true)
                {
                    if (scripts is not null && scripts.TryGetValue("gml_Script_" + word, out UndertaleNamedResource script))
                        return new GmlDefinition(GmlDefinition.DefinitionKind.Function, word, resource: (script as UndertaleScript)?.Code?.ParentEntry ?? script);
                    if (globalFunc is UndertaleFunction utFunc && functions is not null)
                        return new GmlDefinition(GmlDefinition.DefinitionKind.Function, word, resource: utFunc);
                }
            }

            // 3. Resources (objects, sprites, etc.)
            if (resources is not null && resources.TryGetValue(word, out UndertaleNamedResource resource))
            {
                if (data.IsVersionAtLeast(2, 3) && resource is UndertaleScript)
                    resource = null;
                if (resource is not null)
                    return new GmlDefinition(GmlDefinition.DefinitionKind.Resource, word, resource: resource);
            }

            // 4. Constants / enums
            if (data.BuiltinList?.Constants?.ContainsKey(word) == true)
                return new GmlDefinition(GmlDefinition.DefinitionKind.Constant, word);

            // 5. Function that is defined in the current document (function foo() { ... })
            int localFunctionOffset = FindFunctionDeclaration(code, word, currentCode?.Name?.Content);
            if (localFunctionOffset >= 0)
                return new GmlDefinition(GmlDefinition.DefinitionKind.Function, word, localFunctionOffset);

            // 6. Builtin functions / variables
            if (data.BuiltinList?.Functions?.ContainsKey(word) == true ||
                data.BuiltinList?.InstanceVars?.ContainsKey(word) == true ||
                data.BuiltinList?.GlobalVars?.ContainsKey(word) == true ||
                data.BuiltinList?.GlobalArrayVars?.ContainsKey(word) == true ||
                GmlSpecLoader.GetVariable(word) is not null)
            {
                return new GmlDefinition(GmlDefinition.DefinitionKind.Builtin, word);
            }

            return GmlDefinition.None;
        }

        /// <summary>
        /// Finds all occurrences of the identifier at the given offset within the document.
        /// </summary>
        public static IReadOnlyList<GmlReference> FindReferences(string code, int offset, out string symbolName)
        {
            symbolName = GetWordAtOffset(code, offset, out _, out _);
            List<GmlReference> references = null;
            if (symbolName is null || code is null)
                return Array.Empty<GmlReference>();

            string pattern = "\\b" + Regex.Escape(symbolName) + "\\b";
            MatchCollection matches = Regex.Matches(code, pattern);
            references = new List<GmlReference>(matches.Count);
            foreach (Match match in matches)
            {
                int line = 1;
                int lineStart = 0;
                for (int i = 0; i < match.Index && i < code.Length; i++)
                {
                    if (code[i] == '\n')
                    {
                        line++;
                        lineStart = i + 1;
                    }
                }
                int lineEnd = code.IndexOf('\n', lineStart);
                if (lineEnd < 0) lineEnd = code.Length;
                string lineText = code.Substring(lineStart, lineEnd - lineStart).Trim();
                references.Add(new GmlReference(match.Index, match.Length, line, lineText));
            }
            return references;
        }

        /// <summary>
        /// Builds the list of completion items relevant at the given offset.
        /// </summary>
        public static IReadOnlyList<GmlCompletionItem> GetCompletionItems(UndertaleData data, string code, int offset, IList<string> locals)
        {
            List<GmlCompletionItem> items = new(256);

            // Find the word being typed
            int wordStart = offset;
            while (wordStart > 0 && wordStart - 1 < code.Length && IsWordChar(code[wordStart - 1]))
                wordStart--;
            string typed = code.Substring(wordStart, offset - wordStart);
            bool afterDot = wordStart > 0 && code[wordStart - 1] == '.';

            // Start from the cached game-wide symbol list
            if (data is not null)
                items.AddRange(GetBaseCompletionItems(data));

            // Locals
            if (locals is not null)
                foreach (string local in locals)
                    AddItem(items, local, "local", null);

            // User-defined identifiers found in the current document
            if (!string.IsNullOrEmpty(code))
            {
                foreach (Match match in _wordRegex.Matches(code))
                {
                    string w = match.Value;
                    if (_keywords.Contains(w)) continue;
                    if (w.Length < 2) continue;
                    AddItem(items, w, "user", null);
                }
            }

            // Keywords
            foreach (string keyword in _keywords)
                AddItem(items, keyword, "keyword", null);

            // Deduplicate, keeping the first (highest priority) occurrence
            Dictionary<string, GmlCompletionItem> dedup = new(StringComparer.Ordinal);
            foreach (GmlCompletionItem item in items)
            {
                if (!dedup.ContainsKey(item.Text))
                    dedup.Add(item.Text, item);
            }

            // Filter by typed prefix
            List<GmlCompletionItem> result = new(dedup.Count);
            if (afterDot)
            {
                foreach (GmlCompletionItem item in dedup.Values)
                {
                    if (item.Kind == "function") continue;
                    result.Add(item);
                }
            }
            else if (typed.Length > 0)
            {
                foreach (GmlCompletionItem item in dedup.Values)
                {
                    if (item.Text.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                        result.Add(item);
                }
            }
            else
            {
                result.AddRange(dedup.Values);
            }

            result.Sort((a, b) =>
            {
                int cmp = PriorityOf(a.Kind).CompareTo(PriorityOf(b.Kind));
                if (cmp != 0) return cmp;
                return string.CompareOrdinal(a.Text, b.Text);
            });

            // Limit to a reasonable number
            if (result.Count > 800)
                result.RemoveRange(800, result.Count - 800);

            return result;
        }

        /// <summary>
        /// Builds (once, then caches) the game-wide symbol list used as the base of code completion.
        /// </summary>
        private static List<GmlCompletionItem> GetBaseCompletionItems(UndertaleData data)
        {
            lock (_completionCacheLock)
            {
                if (_completionCache.TryGetValue(data, out List<GmlCompletionItem> cached))
                    return cached;
            }

            List<GmlCompletionItem> items = new(512);

            BuiltinList builtins = data.BuiltinList;

            // Functions
            if (builtins?.Functions is not null)
                foreach (var kvp in builtins.Functions)
                    AddItem(items, kvp.Key, "function", null);

            // Instance / global / global-array variables
            if (builtins?.InstanceVars is not null)
                foreach (var kvp in builtins.InstanceVars)
                    AddItem(items, kvp.Key, "variable", null);
            if (builtins?.GlobalVars is not null)
                foreach (var kvp in builtins.GlobalVars)
                    AddItem(items, kvp.Key, "variable", null);
            if (builtins?.GlobalArrayVars is not null)
                foreach (var kvp in builtins.GlobalArrayVars)
                    AddItem(items, kvp.Key, "variable", null);
            if (builtins?.InstanceLimitedVars is not null)
                foreach (var kvp in builtins.InstanceLimitedVars)
                    AddItem(items, kvp.Key, "variable", null);

            // Constants
            if (builtins?.Constants is not null)
                foreach (var kvp in builtins.Constants)
                    AddItem(items, kvp.Key, "constant", null);

            // GMS2.3+ global functions
            if (data.GlobalFunctions is not null && data.Functions is not null)
            {
                foreach (var func in data.Functions)
                {
                    string name = func?.Name?.Content;
                    if (name is null) continue;
                    if (name.StartsWith("gml_Script_", StringComparison.Ordinal)) continue;
                    if (builtins?.Functions?.ContainsKey(name) == true) continue;
                    AddItem(items, name, "function", null);
                }
            }

            // Scripts (referenced by name before GMS2.3)
            if (!data.IsVersionAtLeast(2, 3) && data.Scripts is not null)
                foreach (var script in data.Scripts)
                    AddItem(items, script?.Name?.Content, "script", null);

            // Assets
            AddAssetNames(items, data.GameObjects, "object");
            AddAssetNames(items, data.Sprites, "sprite");
            AddAssetNames(items, data.Sounds, "sound");
            AddAssetNames(items, data.Backgrounds, "background");
            AddAssetNames(items, data.Paths, "path");
            AddAssetNames(items, data.Rooms, "room");
            AddAssetNames(items, data.Fonts, "font");
            AddAssetNames(items, data.Timelines, "timeline");
            AddAssetNames(items, data.Shaders, "shader");
            AddAssetNames(items, data.AnimationCurves, "animcurve");
            AddAssetNames(items, data.Sequences, "sequence");
            AddAssetNames(items, data.ParticleSystems, "particlesystem");

            // GmlSpec functions/variables/constants (supplementary to the builtin list)
            GmlSpecLoader.EnsureLoaded();
            bool chinese = Settings.Instance?.Language is not null &&
                           Settings.Instance.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            foreach (var kvp in chinese ? GmlSpecLoader.GetAllFunctionsZh() : GmlSpecLoader.GetAllFunctionsEn())
            {
                if (builtins?.Functions?.ContainsKey(kvp.Key) == true) continue;
                if (builtins?.Constants?.ContainsKey(kvp.Key) == true) continue;
                AddItem(items, kvp.Key, "function", kvp.Value?.ReturnType);
            }
            foreach (var kvp in chinese ? GmlSpecLoader.GetAllVariablesZh() : GmlSpecLoader.GetAllVariablesEn())
            {
                if (builtins?.InstanceVars?.ContainsKey(kvp.Key) == true) continue;
                if (builtins?.GlobalVars?.ContainsKey(kvp.Key) == true) continue;
                if (builtins?.GlobalArrayVars?.ContainsKey(kvp.Key) == true) continue;
                AddItem(items, kvp.Key, "variable", kvp.Value?.Type);
            }
            foreach (var kvp in chinese ? GmlSpecLoader.GetAllConstantsZh() : GmlSpecLoader.GetAllConstantsEn())
            {
                if (builtins?.Constants?.ContainsKey(kvp.Key) == true) continue;
                AddItem(items, kvp.Key, "constant", null);
            }

            lock (_completionCacheLock)
            {
                _completionCache.Remove(data);
                _completionCache.Add(data, items);
            }

            return items;
        }

        private static int PriorityOf(string kind) => kind switch
        {
            "local" => 0,
            "user" => 1,
            "function" => 2,
            "script" => 3,
            "variable" => 4,
            "constant" => 5,
            "object" => 6,
            "sprite" => 6,
            "sound" => 6,
            "background" => 6,
            "path" => 6,
            "room" => 6,
            "font" => 6,
            "timeline" => 6,
            "shader" => 6,
            "animcurve" => 6,
            "sequence" => 6,
            "particlesystem" => 6,
            "keyword" => 7,
            _ => 8
        };

        private static void AddAssetNames<T>(List<GmlCompletionItem> items, IList<T> list, string kind) where T : UndertaleNamedResource
        {
            if (list is null) return;
            foreach (T item in list)
            {
                if (item?.Name?.Content is string name)
                    AddItem(items, name, kind, null);
            }
        }

        private static void AddItem(List<GmlCompletionItem> items, string name, string kind, string type)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (name.Length > 64) return;
            items.Add(new GmlCompletionItem(name, kind, type));
        }

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        private static bool IsFunctionCall(string code, int wordStart, int wordEnd)
        {
            return wordEnd < code.Length && code[wordEnd] == '(';
        }

        /// <summary>
        /// Tries to find the offset of the declaration of the given local variable within the code document.
        /// </summary>
        private static int FindLocalDeclaration(string code, string name, int wordStart)
        {
            // Patterns: "var name", "var name =", "name = value", function parameter "function(args... name ...)"
            string[] patterns = { "var " + name, "static " + name, "function", name + " =" };
            foreach (Match match in Regex.Matches(code, "\\b" + Regex.Escape(name) + "\\b"))
            {
                if (match.Index >= wordStart)
                    break; // only look at declarations before the usage
                string before = code.Substring(Math.Max(0, match.Index - 8), Math.Min(8, match.Index));
                if (before.Contains("var ") || before.Contains("static "))
                    return match.Index;
            }
            return -1;
        }

        /// <summary>
        /// Tries to find the offset of a function declaration ("function name(...)") with the given name.
        /// </summary>
        private static int FindFunctionDeclaration(string code, string name, string currentCodeName)
        {
            MatchCollection matches = Regex.Matches(code, "\\bfunction\\s+(" + Regex.Escape(name) + ")\\s*\\(");
            foreach (Match match in matches)
            {
                if (match.Groups[1].Index >= 0)
                    return match.Groups[1].Index;
            }
            return -1;
        }
    }
}
