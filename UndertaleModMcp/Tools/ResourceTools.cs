using System.ComponentModel;
using System.Collections.Generic;
using ModelContextProtocol.Server;
using UndertaleModMcp.Services;

namespace UndertaleModMcp.Tools;

[McpServerToolType]
public class ResourceTools
{
    [McpServerTool, Description("List all sprite resources in the loaded game data file")]
    public static List<ResourceSummary> ListSprites(
        [Description("Optional filter string to narrow results by name")] string? filter,
        GameDataSession session)
    {
        return session.ListSprites(filter);
    }

    [McpServerTool, Description("List all sound resources in the loaded game data file")]
    public static List<string> ListSounds(GameDataSession session)
    {
        return session.ListSounds();
    }

    [McpServerTool, Description("List all room resources in the loaded game data file")]
    public static List<string> ListRooms(GameDataSession session)
    {
        return session.ListRooms();
    }

    [McpServerTool, Description("List all game object resources in the loaded game data file")]
    public static List<string> ListGameObjects(
        [Description("Optional filter string to narrow results by name")] string? filter,
        GameDataSession session)
    {
        return session.ListGameObjects(filter);
    }

    [McpServerTool, Description("Get detailed information about a specific sprite resource")]
    public static SpriteDetail GetSpriteInfo(
        [Description("The name of the sprite to inspect")] string name,
        GameDataSession session)
    {
        return session.GetSpriteInfo(name);
    }

    [McpServerTool, Description("Get detailed information about a specific room resource")]
    public static RoomDetail GetRoomInfo(
        [Description("The name of the room to inspect")] string name,
        GameDataSession session)
    {
        return session.GetRoomInfo(name);
    }

    [McpServerTool, Description("Export an embedded texture as a PNG image file")]
    public static string ExportTexture(
        [Description("The name of the embedded texture to export")] string textureName,
        [Description("The output file path for the PNG image")] string outputPath,
        GameDataSession session)
    {
        session.ExportTexture(textureName, outputPath);
        return $"Texture '{textureName}' exported to: {outputPath}";
    }
}
