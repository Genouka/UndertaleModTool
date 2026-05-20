using System.ComponentModel;
using System.Collections.Generic;
using ModelContextProtocol.Server;
using UndertaleModMcp.Services;

namespace UndertaleModMcp.Tools;

[McpServerToolType]
public class CodeTools
{
    [McpServerTool, Description("Decompile a code entry (GML bytecode) back into readable GML source code")]
    public static string DecompileCode(
        [Description("The name of the code entry to decompile (e.g., gml_Script_myScript)")] string codeName,
        GameDataSession session)
    {
        return session.DecompileCode(codeName);
    }

    [McpServerTool, Description("Disassemble a code entry into human-readable assembly/VM instructions")]
    public static string DisassembleCode(
        [Description("The name of the code entry to disassemble")] string codeName,
        GameDataSession session)
    {
        return session.DisassembleCode(codeName);
    }

    [McpServerTool, Description("List all code entries in the loaded data file")]
    public static List<string> ListCodeEntries(
        [Description("Optional filter string to narrow results by name")] string? filter,
        GameDataSession session)
    {
        return session.ListCodeEntries(filter);
    }

    [McpServerTool, Description("Replace an entire code entry with new GML source code")]
    public static CodeModifyResult ReplaceCode(
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
