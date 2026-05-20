using System.ComponentModel;
using System.Collections.Generic;
using ModelContextProtocol.Server;
using UndertaleModMcp.Services;

namespace UndertaleModMcp.Tools;

[McpServerToolType]
public class DataFileTools
{
    [McpServerTool, Description("Load a GameMaker Studio data file (data.win, data.unx, data.ios, data.droid) into memory for editing")]
    public static string LoadDataFile(
        [Description("Full path to the GameMaker data file")] string filePath,
        GameDataSession session)
    {
        session.Load(filePath);
        var info = session.GetGameInfo();
        return $"Successfully loaded data file from: {filePath}\n" +
               $"Game Name: {info.Name}\n" +
               $"Is GMS2: {info.IsGMS2}\n" +
               $"Is YYC: {info.IsYYC}\n" +
               $"Bytecode Version: {info.BytecodeVersion}\n" +
               $"Config: {info.Config}\n" +
               $"Resources: {info.SpriteCount} sprites, {info.SoundCount} sounds, {info.GameObjectCount} objects, {info.RoomCount} rooms, {info.CodeCount} code entries";
    }

    [McpServerTool, Description("Save the currently loaded data file to disk")]
    public static string SaveDataFile(
        [Description("Output path for saving (defaults to original file path)")] string? outputPath,
        GameDataSession session)
    {
        session.Save(outputPath);
        return $"Data file saved successfully to: {outputPath ?? session.FilePath}";
    }

    [McpServerTool, Description("Get basic information about the currently loaded game data file")]
    public static GameInfo GetGameInfo(GameDataSession session)
    {
        return session.GetGameInfo();
    }

    [McpServerTool, Description("Create a new blank GameMaker data file in memory")]
    public static string CreateNewDataFile(GameDataSession session)
    {
        session.CreateNewData();
        return "Created a new blank GameMaker data file. Use save_data_file to persist it.";
    }
}
