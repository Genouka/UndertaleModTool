using System.ComponentModel;
using System.Collections.Generic;
using ModelContextProtocol.Server;
using UndertaleModMcp.Services;

namespace UndertaleModMcp.Tools;

[McpServerToolType]
public class SearchTools
{
    [McpServerTool, Description("Search the string table for strings matching a query")]
    public static List<StringSearchResult> SearchStrings(
        [Description("The query string to search for in the string table")] string query,
        GameDataSession session)
    {
        return session.SearchStrings(query);
    }

    [McpServerTool, Description("Find a named resource by its exact name across all resource types")]
    public static string? FindResourceByName(
        [Description("The exact name of the resource to find")] string name,
        GameDataSession session)
    {
        return session.FindResourceByName(name);
    }

    [McpServerTool, Description("Search within decompiled code for text content across all code entries")]
    public static List<CodeSearchResult> SearchCodeText(
        [Description("The query string to search for in decompiled code")] string query,
        [Description("Maximum number of results to return")] int maxResults,
        GameDataSession session)
    {
        return session.SearchCodeText(query, maxResults);
    }
}
