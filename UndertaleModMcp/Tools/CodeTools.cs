using System.ComponentModel;
using System.Collections.Generic;
using ModelContextProtocol.Server;
using UndertaleModMcp.Services;
using StringSplitOptions = System.StringSplitOptions;

namespace UndertaleModMcp.Tools;

[McpServerToolType]
public class CodeTools
{
    [McpServerTool, Description("Read a decompiled code entry GML source code")]
    public static string ReadDecompiledCode(
        [Description("The name of the code entry (e.g., gml_Script_myScript)")] string codeName,
        GameDataSession session)
    {
        return session.DecompileCode(codeName);
    }

    [McpServerTool, Description("Read a part of a decompiled code entry's GML source code")]
    public static string ReadPartOfDecompiledCode(
        [Description("The name of the code entry (e.g., gml_Script_myScript)")] string codeName,
        [Description("The starting line of the substring to read")] int startLine,
        [Description("The number of lines to read from the starting index")] int lineCount,
        GameDataSession session)
    {
        var decompiledCode = session.DecompileCode(codeName);
        var codeLines = decompiledCode.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        if (startLine < 0 || startLine >= codeLines.Length || lineCount <= 0)
        {
            return string.Empty; // Invalid parameters, return empty string
        }
        return string.Join("\n", codeLines, startLine, lineCount);
    }

    // [McpServerTool, Description("Disassemble a code entry into human-readable assembly/VM instructions")]
    // public static string DisassembleCode(
    //     [Description("The name of the code entry to disassemble")] string codeName,
    //     GameDataSession session)
    // {
    //     return session.DisassembleCode(codeName);
    // }

    [McpServerTool, Description("List all code entries in the loaded data file")]
    public static List<string> ListCodeEntries(
        [Description("Optional filter string to narrow results by name")] string? filter,
        GameDataSession session)
    {
        return session.ListCodeEntries(filter);
    }

    [McpServerTool, Description("Write new GML source code to a code entry, replacing the existing code")]
    public static CodeModifyResult WriteCode(
        [Description("The name of the code entry to replace")] string codeEntryName,
        [Description("The new GML source code to compile and replace with")] string gmlCode,
        GameDataSession session)
    {
        return session.ReplaceCode(codeEntryName, gmlCode);
    }

    [McpServerTool, Description("Find and replace text within a decompiled code entry")]
    public static CodeModifyResult FindReplaceCode(
        [Description("The name of the code entry to modify")] string codeEntryName,
        [Description("The text to search for")] string search,
        [Description("The replacement text")] string replacement,
        [Description("Whether the search should be case-sensitive")] bool caseSensitive,
        GameDataSession session)
    {
        return session.FindReplaceCode(codeEntryName, search, replacement, caseSensitive);
    }
}
