using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Underanalyzer.Decompiler;
using UndertaleModLib;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;
using UndertaleModLib.Compiler;
using UndertaleModLib.Util;

namespace UndertaleModMcp.Services;

public class GameDataSession : IDisposable
{
    public UndertaleData? Data { get; private set; }
    public string? FilePath { get; private set; }
    public GlobalDecompileContext? DecompileContext { get; private set; }

    private readonly object _lock = new();
    private bool _disposed;

    public bool IsLoaded => Data != null;

    public void Load(string path)
    {
        lock (_lock)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"Data file not found: {path}");

            DisposeCurrent();

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            Data = UndertaleIO.Read(fs, (warning, isImportant) =>
            {
                Console.Error.WriteLine($"[WARN] {(isImportant ? "IMPORTANT " : "")}{warning}");
            }, message => Console.WriteLine($"[INFO] {message}"));
            FilePath = path;
            DecompileContext = new GlobalDecompileContext(Data);
            GlobalDecompileContext.BuildGlobalFunctionCache(Data);
        }
    }

    public void Save(string? outputPath = null)
    {
        lock (_lock)
        {
            EnsureLoaded();

            string dest = outputPath ?? FilePath
                ?? throw new InvalidOperationException("No file path available for saving");

            string tempPath = dest + ".temp";
            try
            {
                using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write);
                UndertaleIO.Write(fs, Data, message => Console.WriteLine($"[INFO] {message}"));
                File.Move(tempPath, dest, true);
            }
            catch
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
                throw;
            }
        }
    }

    public GameInfo GetGameInfo()
    {
        lock (_lock)
        {
            EnsureLoaded();
            var gi = Data!.GeneralInfo;
            return new GameInfo(
                gi.Name?.Content ?? "",
                Data.IsGameMaker2(),
                Data.IsYYC(),
                gi.BytecodeVersion,
                gi.Config?.Content ?? "",
                Data.Sounds.Count,
                Data.Sprites.Count,
                Data.Backgrounds.Count,
                Data.Paths.Count,
                Data.Scripts.Count,
                Data.Shaders.Count,
                Data.Fonts.Count,
                Data.Timelines.Count,
                Data.GameObjects.Count,
                Data.Rooms.Count,
                Data.Extensions.Count,
                Data.TexturePageItems.Count,
                Data.Code?.Count ?? 0,
                Data.Variables?.Count ?? 0,
                Data.Functions?.Count ?? 0,
                Data.Strings.Count,
                Data.EmbeddedTextures.Count,
                Data.EmbeddedAudio.Count
            );
        }
    }

    public string DecompileCode(string codeName)
    {
        lock (_lock)
        {
            EnsureLoaded();
            var code = Data!.Code.ByName(codeName);
            if (code == null)
                throw new KeyNotFoundException($"Code entry '{codeName}' not found");
            DecompileContext!.PrepareForCompilation(false);
            var ctx = new DecompileContext(DecompileContext, code);
            return ctx.DecompileToString();
        }
    }

    public string DisassembleCode(string codeName)
    {
        lock (_lock)
        {
            EnsureLoaded();
            var code = Data!.Code.ByName(codeName);
            if (code == null)
                throw new KeyNotFoundException($"Code entry '{codeName}' not found");
            return UndertaleModLib.Decompiler.Disassembler.Disassemble(code, Data.Variables, Data.CodeLocals.For(code));
        }
    }

    public List<string> ListCodeEntries(string? filter = null)
    {
        lock (_lock)
        {
            EnsureLoaded();
            var entries = Data!.Code.Select(c => c.Name.Content).ToList();
            if (!string.IsNullOrEmpty(filter))
                entries = entries.Where(n => n.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            return entries;
        }
    }

    public CodeModifyResult ReplaceCode(string codeEntryName, string gmlCode)
    {
        lock (_lock)
        {
            EnsureLoaded();
            DecompileContext!.PrepareForCompilation(true);
            var group = new CodeImportGroup(Data!, DecompileContext);
            group.QueueReplace(codeEntryName, gmlCode);
            var result = group.Import(false);
            return new CodeModifyResult(result.Successful, result.Errors?.Select(e => e.ToString() ?? "").ToList() ?? []);
        }
    }

    public CodeModifyResult FindReplaceCode(string codeEntryName, string search, string replacement, bool caseSensitive = true)
    {
        lock (_lock)
        {
            EnsureLoaded();
            DecompileContext!.PrepareForCompilation(true);
            var group = new CodeImportGroup(Data!, DecompileContext);
            group.QueueFindReplace(codeEntryName, search, replacement, caseSensitive);
            var result = group.Import(false);
            return new CodeModifyResult(result.Successful, result.Errors?.Select(e => e.ToString() ?? "").ToList() ?? []);
        }
    }

    public List<ResourceSummary> ListSprites(string? filter = null)
    {
        lock (_lock)
        {
            EnsureLoaded();
            return Data!.Sprites.Select(s => new ResourceSummary(s.Name.Content, s.Textures.Count))
                .Where(r => string.IsNullOrEmpty(filter) || r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    public List<string> ListSounds()
    {
        lock (_lock)
        {
            EnsureLoaded();
            return Data!.Sounds.Select(s => s.Name.Content).ToList();
        }
    }

    public List<string> ListRooms()
    {
        lock (_lock)
        {
            EnsureLoaded();
            return Data!.Rooms.Select(r => r.Name.Content).ToList();
        }
    }

    public List<string> ListGameObjects(string? filter = null)
    {
        lock (_lock)
        {
            EnsureLoaded();
            var objects = Data!.GameObjects.Select(o => o.Name.Content).ToList();
            if (!string.IsNullOrEmpty(filter))
                objects = objects.Where(n => n.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            return objects;
        }
    }

    public SpriteDetail GetSpriteInfo(string name)
    {
        lock (_lock)
        {
            EnsureLoaded();
            var sprite = Data!.Sprites.ByName(name);
            if (sprite == null)
                throw new KeyNotFoundException($"Sprite '{name}' not found");
            return new SpriteDetail(
                sprite.Name.Content,
                (int)sprite.Width,
                (int)sprite.Height,
                sprite.OriginX,
                sprite.OriginY,
                (int)sprite.SepMasks,
                sprite.Textures.Count,
                sprite.GMS2PlaybackSpeed,
                (int)sprite.GMS2PlaybackSpeedType
            );
        }
    }

    public RoomDetail GetRoomInfo(string name)
    {
        lock (_lock)
        {
            EnsureLoaded();
            var room = Data!.Rooms.ByName(name);
            if (room == null)
                throw new KeyNotFoundException($"Room '{name}' not found");
            return new RoomDetail(
                room.Name.Content,
                (int)room.Width,
                (int)room.Height,
                (int)room.Flags,
                room.Persistent,
                room.CreationCodeId?.Name?.Content ?? "",
                room.GameObjects.Count,
                room.Layers.Count,
                room.Tiles.Count
            );
        }
    }

    public void ExportTexture(string textureName, string outputPath)
    {
        lock (_lock)
        {
            EnsureLoaded();
            var texture = Data!.EmbeddedTextures.ByName(textureName);
            if (texture == null)
                throw new KeyNotFoundException($"Embedded texture '{textureName}' not found");
            if (texture.TextureData.Image is not GMImage image)
                throw new InvalidOperationException($"Texture '{textureName}' has no image data");
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            image.SavePng(fs);
        }
    }

    public List<StringSearchResult> SearchStrings(string query)
    {
        lock (_lock)
        {
            EnsureLoaded();
            return Data!.Strings
                .Where(s => s.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select((s, i) => new StringSearchResult(i, s.Content))
                .Take(100)
                .ToList();
        }
    }

    public List<CodeSearchResult> SearchCodeText(string query, int maxResults = 50)
    {
        lock (_lock)
        {
            EnsureLoaded();
            DecompileContext!.PrepareForCompilation(false);
            var results = new List<CodeSearchResult>();
            foreach (var code in Data!.Code)
            {
                if (results.Count >= maxResults) break;
                try
                {
                    var ctx = new DecompileContext(DecompileContext, code);
                    var text = ctx.DecompileToString();
                    var lines = text.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            results.Add(new CodeSearchResult(code.Name.Content, i + 1, lines[i].Trim()));
                            if (results.Count >= maxResults) goto done;
                        }
                    }
                }
                catch { }
            }
            done:
            return results;
        }
    }

    public string? FindResourceByName(string name)
    {
        lock (_lock)
        {
            EnsureLoaded();
            var res = Data!.ByName(name);
            return res?.Name?.Content;
        }
    }

    public List<string> ListAllStrings()
    {
        lock (_lock)
        {
            EnsureLoaded();
            return Data!.Strings.Select(s => s.Content).ToList();
        }
    }

    public void CreateNewData()
    {
        lock (_lock)
        {
            DisposeCurrent();
            Data = UndertaleData.CreateNew();
            FilePath = null;
            DecompileContext = new GlobalDecompileContext(Data);
        }
    }

    public void ExportAllCodeToDirectory(string outputDir)
    {
        lock (_lock)
        {
            EnsureLoaded();
            Directory.CreateDirectory(outputDir);
            DecompileContext!.PrepareForCompilation(false);
            foreach (var code in Data!.Code)
            {
                try
                {
                    var ctx = new DecompileContext(DecompileContext, code);
                    var text = ctx.DecompileToString();
                    var safeName = string.Join("_", code.Name.Content.Split(Path.GetInvalidFileNameChars()));
                    File.WriteAllText(Path.Combine(outputDir, $"{safeName}.gml"), text);
                }
                catch { }
            }
        }
    }

    private void EnsureLoaded()
    {
        if (Data == null)
            throw new InvalidOperationException("No data file loaded. Call 'load_data_file' first.");
    }

    private void DisposeCurrent()
    {
        Data?.Dispose();
        Data = null;
        FilePath = null;
        DecompileContext = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            DisposeCurrent();
        }
    }
}

public record GameInfo(
    string Name,
    bool IsGMS2,
    bool IsYYC,
    uint BytecodeVersion,
    string Config,
    int SoundCount,
    int SpriteCount,
    int BackgroundCount,
    int PathCount,
    int ScriptCount,
    int ShaderCount,
    int FontCount,
    int TimelineCount,
    int GameObjectCount,
    int RoomCount,
    int ExtensionCount,
    int TexturePageItemCount,
    int CodeCount,
    int VariableCount,
    int FunctionCount,
    int StringCount,
    int EmbeddedTextureCount,
    int EmbeddedAudioCount
);

public record CodeModifyResult(bool Success, List<string> Errors);

public record ResourceSummary(string Name, int Detail);

public record SpriteDetail(
    string Name,
    int Width,
    int Height,
    int OriginX,
    int OriginY,
    int SepMasks,
    int TextureCount,
    float PlaybackSpeed,
    int PlaybackSpeedType
);

public record RoomDetail(
    string Name,
    int Width,
    int Height,
    int Flags,
    bool Persistent,
    string CreationCodeId,
    int GameObjectCount,
    int LayerCount,
    int TileCount
);

public record StringSearchResult(int Index, string Content);

public record CodeSearchResult(string CodeEntryName, int LineNumber, string LineContent);
