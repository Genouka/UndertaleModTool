using System;
using System.Collections.Generic;
using System.ComponentModel;
using ModelContextProtocol.Server;
using UndertaleModMcp.Services;

namespace UndertaleModMcp.Resources;

[McpServerResourceType]
public class GameDataResources
{
    [McpServerResource(UriTemplate = "gamedata://info", Name = "Game Information",
        MimeType = "application/json")]
    [Description("Basic information about the currently loaded GameMaker data file")]
    public static string GetGameInfo(GameDataSession session)
    {
        if (!session.IsLoaded)
            return """{"error": "No data file loaded"}""";
        var info = session.GetGameInfo();
        return System.Text.Json.JsonSerializer.Serialize(info);
    }

    [McpServerResource(UriTemplate = "gamedata://strings", Name = "String Table",
        MimeType = "text/plain")]
    [Description("All strings in the game data string table")]
    public static string GetStrings(GameDataSession session)
    {
        if (!session.IsLoaded)
            return "No data file loaded";
        return string.Join("\n", session.ListAllStrings());
    }

    [McpServerResource(UriTemplate = "gamedata://code/{name}", Name = "Decompiled Code",
        MimeType = "text/x-gml")]
    [Description("Decompiled GML source code for a specific code entry")]
    public static string GetCode(string name, GameDataSession session)
    {
        if (!session.IsLoaded)
            return "No data file loaded";
        try
        {
            return session.DecompileCode(name);
        }
        catch (KeyNotFoundException ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}
