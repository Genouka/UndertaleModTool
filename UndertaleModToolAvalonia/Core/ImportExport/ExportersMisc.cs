using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ImageMagick;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;

namespace UndertaleModToolAvalonia;

public partial class ImportExportService
{
    /// <summary>Exports all fonts (textures + glyph CSV) to a folder.</summary>
    public async void ExportAllFonts()
    {
        EnsureDataLoaded();

        string fntFolder = PromptChooseExportDirectory();
        if (fntFolder is null)
            return;

        SetProgressBar(null, "Fonts", 0, Data.Fonts.Count);

        using (TextureWorker worker = new())
        {
            await Task.Run(() => Parallel.ForEach(Data.Fonts, font =>
            {
                if (font is not null)
                {
                    worker.ExportAsPNG(font.Texture, Paths.JoinVerifyWithinDirectory(fntFolder, $"{font.Name.Content}.png"));
                    using (StreamWriter writer = new(Paths.JoinVerifyWithinDirectory(fntFolder, $"glyphs_{font.Name.Content}.csv")))
                    {
                        writer.WriteLine($"{font.DisplayName};{font.EmSize};{font.Bold};{font.Italic};{font.Charset};{font.AntiAliasing};{font.ScaleX};{font.ScaleY}");

                        foreach (var g in font.Glyphs)
                        {
                            writer.WriteLine($"{g.Character};{g.SourceX};{g.SourceY};{g.SourceWidth};{g.SourceHeight};{g.Shift};{g.Offset}");
                        }
                    }
                }

                IncrementProgressParallel();
            }));
        }

        await FinalizeExportAsync();
        HideProgressBar();
    }

    /// <summary>Exports all shaders to a folder of per-shader subfolders.</summary>
    public async void ExportAllShaders()
    {
        EnsureDataLoaded();

        string exportFolder = PromptChooseExportDirectory();
        if (exportFolder is null)
            return;

        foreach (UndertaleShader shader in Data.Shaders)
        {
            if (shader is null)
                continue;

            string exportBase = Paths.JoinVerifyWithinDirectory(exportFolder, shader.Name.Content);
            Directory.CreateDirectory(exportBase);

            File.WriteAllText(Paths.JoinVerifyWithinDirectory(exportBase, "Type.txt"), shader.Type.ToString());
            File.WriteAllText(Paths.JoinVerifyWithinDirectory(exportBase, "GLSL_ES_Fragment.txt"), shader.GLSL_ES_Fragment.Content);
            File.WriteAllText(Paths.JoinVerifyWithinDirectory(exportBase, "GLSL_ES_Vertex.txt"), shader.GLSL_ES_Vertex.Content);
            File.WriteAllText(Paths.JoinVerifyWithinDirectory(exportBase, "GLSL_Fragment.txt"), shader.GLSL_Fragment.Content);
            File.WriteAllText(Paths.JoinVerifyWithinDirectory(exportBase, "GLSL_Vertex.txt"), shader.GLSL_Vertex.Content);
            File.WriteAllText(Paths.JoinVerifyWithinDirectory(exportBase, "HLSL9_Fragment.txt"), shader.HLSL9_Fragment.Content);
            File.WriteAllText(Paths.JoinVerifyWithinDirectory(exportBase, "HLSL9_Vertex.txt"), shader.HLSL9_Vertex.Content);
            WriteShaderBinaryIfNotNull(shader.HLSL11_VertexData, exportBase, "HLSL11_VertexData.bin");
            WriteShaderBinaryIfNotNull(shader.HLSL11_PixelData, exportBase, "HLSL11_PixelData.bin");
            WriteShaderBinaryIfNotNull(shader.PSSL_VertexData, exportBase, "PSSL_VertexData.bin");
            WriteShaderBinaryIfNotNull(shader.PSSL_PixelData, exportBase, "PSSL_PixelData.bin");
            WriteShaderBinaryIfNotNull(shader.Cg_PSVita_VertexData, exportBase, "Cg_PSVita_VertexData.bin");
            WriteShaderBinaryIfNotNull(shader.Cg_PSVita_PixelData, exportBase, "Cg_PSVita_PixelData.bin");
            WriteShaderBinaryIfNotNull(shader.Cg_PS3_VertexData, exportBase, "Cg_PS3_VertexData.bin");
            WriteShaderBinaryIfNotNull(shader.Cg_PS3_PixelData, exportBase, "Cg_PS3_PixelData.bin");

            StringBuilder vertexSb = new();
            for (var i = 0; i < shader.VertexShaderAttributes.Count; i++)
            {
                vertexSb.AppendLine(shader.VertexShaderAttributes[i].Name.Content);
            }
            File.WriteAllText(Paths.JoinVerifyWithinDirectory(exportBase, "VertexShaderAttributes.txt"), vertexSb.ToString());
        }

        await FinalizeExportAsync();
    }

    static void WriteShaderBinaryIfNotNull(UndertaleShader.UndertaleRawShaderData data, string exportBase, string filename)
    {
        if (!data.IsNull)
            File.WriteAllBytes(Paths.JoinVerifyWithinDirectory(exportBase, filename), data.Data);
    }

    /// <summary>Exports all sounds to a folder, optionally grouped by audio group.</summary>
    public async void ExportAllSounds()
    {
        EnsureDataLoaded();

        string exportedSoundsDir = PromptChooseExportDirectory();
        if (exportedSoundsDir is null)
            return;

        bool copyExternalAudio = ScriptQuestion("Export external audio files as well? (Will copy to a separate folder.)");
        bool groupedExport = false;
        if ((Data.AudioGroups?.Count ?? 0) > 0)
        {
            groupedExport = ScriptQuestion("Group sounds by audio group?");
        }

        byte[] EMPTY_WAV_FILE_BYTES = System.Convert.FromBase64String("UklGRiQAAABXQVZFZm10IBAAAAABAAIAQB8AAAB9AAAEABAAZGF0YQAAAAA=");
        string DEFAULT_AUDIOGROUP_NAME = "audiogroup_default";

        int maxCount = Data.Sounds.Count;
        SetProgressBar(null, "Sounds", 0, maxCount);
        
        Dictionary<string, IList<UndertaleEmbeddedAudio>>? loadedAudioGroups = null ;
        
        await Task.Run(DumpSounds);

        await FinalizeExportAsync();
        HideProgressBar();

        void IncProgressLocal()
        {
            if (GetProgress() < maxCount)
            {
                IncrementProgress();
            }
        }
        
        IList<UndertaleEmbeddedAudio> GetAudioGroupData(UndertaleSound sound)
        {
            loadedAudioGroups ??= new();

            string audioGroupName = sound.AudioGroup is not null ? sound.AudioGroup.Name.Content : DEFAULT_AUDIOGROUP_NAME;
            if (loadedAudioGroups.ContainsKey(audioGroupName))
            {
                return loadedAudioGroups[audioGroupName];
            }

            string relativeAudioGroupPath;
            if (sound.AudioGroup is UndertaleAudioGroup { Path.Content: string customRelativePath })
            {
                relativeAudioGroupPath = customRelativePath;
            }
            else
            {
                relativeAudioGroupPath = $"audiogroup{sound.GroupID}.dat";
            }
            string groupFilePath = Paths.JoinVerifyWithinDirectory(Path.GetDirectoryName(FilePath), relativeAudioGroupPath);
            if (!File.Exists(groupFilePath))
            {
                return null;
            }

            try
            {
                UndertaleData data;
                using (var stream = new FileStream(groupFilePath, FileMode.Open, FileAccess.Read))
                {
                    data = UndertaleIO.Read(stream, (warning, _) => ScriptWarning($"A warning occured while trying to load {audioGroupName}:\n{warning}"));
                }

                loadedAudioGroups[audioGroupName] = data.EmbeddedAudio;
                return data.EmbeddedAudio;
            }
            catch (Exception e)
            {
                ScriptError($"An error occured while trying to load {audioGroupName}:\n{e.Message}");
                return null;
            }
        }

        byte[] GetSoundData(UndertaleSound sound)
        {
            if (sound.AudioFile is not null)
            {
                return sound.AudioFile.Data;
            }

            if (sound.GroupID > Data.GetBuiltinSoundGroupID())
            {
                IList<UndertaleEmbeddedAudio> audioGroup = GetAudioGroupData(sound);
                if (audioGroup is not null)
                {
                    return audioGroup[sound.AudioID].Data;
                }
            }

            return EMPTY_WAV_FILE_BYTES;
        }

        void DumpSounds()
        {
            if (loadedAudioGroups is null) return;
            foreach (UndertaleSound sound in Data.Sounds)
            {
                if (sound is not null)
                {
                    DumpSound(sound);
                }
                else
                {
                    IncProgressLocal();
                }
            }
        }

        void DumpSound(UndertaleSound sound)
        {
            string soundName = sound.Name.Content;
            string soundFilePath;
            if (groupedExport)
            {
                soundFilePath = Paths.JoinVerifyWithinDirectory(exportedSoundsDir, sound.AudioGroup.Name.Content, soundName);
                Directory.CreateDirectory(Paths.JoinVerifyWithinDirectory(exportedSoundsDir, sound.AudioGroup.Name.Content));
            }
            else
            {
                soundFilePath = Paths.JoinVerifyWithinDirectory(exportedSoundsDir, soundName);
            }

            bool flagCompressed = sound.Flags.HasFlag(UndertaleSound.AudioEntryFlags.IsCompressed);
            bool flagEmbedded = sound.Flags.HasFlag(UndertaleSound.AudioEntryFlags.IsEmbedded);
            string audioExt = ".ogg";
            bool isEmbedded = true;
            if (flagEmbedded && !flagCompressed)
            {
                audioExt = ".wav";
            }
            else if (flagCompressed && !flagEmbedded)
            {
                audioExt = ".ogg";
            }
            else if (flagCompressed && flagEmbedded)
            {
                audioExt = ".ogg";
            }
            else if (!flagCompressed && !flagEmbedded)
            {
                isEmbedded = false;
                audioExt = ".ogg";

                if (copyExternalAudio)
                {
                    string externalFilename = sound.File.Content;
                    if (!externalFilename.Contains('.'))
                    {
                        externalFilename += ".ogg";
                    }
                    string sourcePath = Paths.JoinVerifyWithinDirectory(Path.GetDirectoryName(FilePath), externalFilename);
                    string destPath;
                    if (groupedExport)
                    {
                        destPath = Paths.JoinVerifyWithinDirectory(exportedSoundsDir, sound.AudioGroup.Name.Content, "external", soundName + audioExt);
                        Directory.CreateDirectory(Paths.JoinVerifyWithinDirectory(exportedSoundsDir, sound.AudioGroup.Name.Content, "external"));
                    }
                    else
                    {
                        destPath = Paths.JoinVerifyWithinDirectory(exportedSoundsDir, "external", soundName + audioExt);
                        Directory.CreateDirectory(Paths.JoinVerifyWithinDirectory(exportedSoundsDir, "external"));
                    }
                    File.Copy(sourcePath, destPath, true);
                }
            }
            if (isEmbedded)
            {
                File.WriteAllBytes(soundFilePath + audioExt, GetSoundData(sound));
            }

            IncProgressLocal();
        }
    }

    /// <summary>Exports all strings to a text file.</summary>
    public async void ExportAllStrings()
    {
        EnsureDataLoaded();

        string stringsPath = PromptSaveFile(".txt", "TXT files (*.txt)|*.txt|All files (*.*)|*.*");
        if (string.IsNullOrWhiteSpace(stringsPath))
            return;

        bool promptedForNewlines = false;
        bool skipNewlines = false;
        using (StreamWriter writer = new(stringsPath))
        {
            foreach (var str in Data.Strings)
            {
                if (str.Content.Contains('\n') || str.Content.Contains('\r'))
                {
                    if (!promptedForNewlines)
                    {
                        promptedForNewlines = true;
                        skipNewlines = ScriptQuestion("Export strings containing newlines? Doing so will break reimporting.");
                    }
                    if (skipNewlines)
                    {
                        continue;
                    }
                }
                writer.WriteLine(str.Content);
            }
        }

        await FinalizeExportAsync();
    }

    /// <summary>Exports all strings to a JSON file.</summary>
    public async void ExportAllStringsJSON()
    {
        EnsureDataLoaded();

        string path = PromptSaveFile(".json", "JSON files (*.json)|*.json|TXT files (*.txt)|*.txt|All files (*.*)|*.*");
        if (string.IsNullOrWhiteSpace(path))
            return;

        StringBuilder json = new("{\r\n    \"Strings\": [\r\n");
        const string
            prefix = "        ",
            suffix = ",\r\n";
        foreach (string str in Data.Strings.Select(str => str.Content))
            json.Append(
                prefix
                + JsonifyString(str)
                + suffix);
        json.Length -= suffix.Length;
        json.Append("\r\n    ]\r\n}");

        File.WriteAllText(path, json.ToString());

        string? targetName = LastExportTargetName;
        await FinalizeExportAsync();
        ScriptMessage($"Successfully exported to\n{targetName ?? path}");

        static string JsonifyString(string str)
        {
            StringBuilder sb = new();
            foreach (char ch in str)
            {
                if (ch == '\"') { sb.Append("\\\""); continue; }
                if (ch == '\\') { sb.Append("\\\\"); continue; }
                if (ch == '\b') { sb.Append("\\b"); continue; }
                if (ch == '\f') { sb.Append("\\f"); continue; }
                if (ch == '\n') { sb.Append("\\n"); continue; }
                if (ch == '\r') { sb.Append("\\r"); continue; }
                if (ch == '\t') { sb.Append("\\t"); continue; }
                if (Char.IsControl(ch))
                {
                    sb.Append("\\u" + Convert.ToByte(ch).ToString("x4"));
                    continue;
                }

                sb.Append(ch);
            }
            return "\"" + sb.ToString() + "\"";
        }
    }

    /// <summary>Exports all embedded textures as PNG files (i.png).</summary>
    public async void ExportAllEmbeddedTextures()
    {
        EnsureDataLoaded();

        string texturesFolder = PromptChooseExportDirectory();
        if (texturesFolder is null)
            return;

        SetProgressBar(null, "Embedded Textures", 0, Data.EmbeddedTextures.Count);

        await Task.Run(() =>
        {
            for (int i = 0; i < Data.EmbeddedTextures.Count; i++)
            {
                try
                {
                    using FileStream fs = new(Paths.JoinVerifyWithinDirectory(texturesFolder, $"{i}.png"), FileMode.Create);
                    Data.EmbeddedTextures[i].TextureData.Image.SavePng(fs);
                }
                catch (Exception ex)
                {
                    ScriptMessage($"Failed to export file: {ex.Message}");
                }

                IncrementProgress();
            }
        });

        await FinalizeExportAsync();
        HideProgressBar();
    }

    /// <summary>Exports sprites as animated GIF files.</summary>
    public async void ExportSpritesAsGIF()
    {
        EnsureDataLoaded();

        string folder = PromptChooseExportDirectory();
        if (folder is null)
            return;

        string filter = SimpleTextInput("Filter sprites", "String that the sprite names must start with (or leave blank to export all):", "", false) ?? "";

        using TextureWorker worker = new();
        IList<UndertaleSprite> sprites = Data.Sprites;
        if (filter != "")
        {
            sprites = new List<UndertaleSprite>();
            foreach (UndertaleSprite sprite in Data.Sprites)
            {
                if (sprite is null)
                    continue;
                if (sprite.Name.Content.StartsWith(filter))
                {
                    sprites.Add(sprite);
                }
            }
        }

        SetProgressBar(null, "Exporting sprites to GIF...", 0, sprites.Count);

        await Task.Run(() =>
        {
            Parallel.ForEach(sprites, sprite =>
            {
                IncrementProgressParallel();
                ExtractSprite(sprite, folder, worker);
            });
        });

        await FinalizeExportAsync();
        HideProgressBar();

        void ExtractSprite(UndertaleSprite sprite, string folderPath, TextureWorker textureWorker)
        {
            if (sprite is null)
                return;

            using MagickImageCollection gif = new();
            bool anyValidFrames = false;
            for (int picCount = 0; picCount < sprite.Textures.Count; picCount++)
            {
                if (sprite.Textures[picCount]?.Texture != null)
                {
                    IMagickImage<byte> image = textureWorker.GetTextureFor(sprite.Textures[picCount].Texture, sprite.Name.Content + " (frame " + picCount + ")", true);
                    image.GifDisposeMethod = GifDisposeMethod.Previous;
                    // the animation delay unit seems to be 100 per second, not milliseconds (1000 per second)
                    if (sprite.IsSpecialType && Data.IsGameMaker2())
                    {
                        if (sprite.GMS2PlaybackSpeed == 0f)
                        {
                            image.AnimationDelay = 10;
                        }
                        else if (sprite.GMS2PlaybackSpeedType is AnimSpeedType.FramesPerGameFrame)
                        {
                            image.AnimationDelay = (uint)Math.Max((int)(Math.Round(100f / (sprite.GMS2PlaybackSpeed * Data.GeneralInfo.GMS2FPS))), 1);
                        }
                        else
                        {
                            image.AnimationDelay = (uint)Math.Max((int)(Math.Round(100 / sprite.GMS2PlaybackSpeed)), 1);
                        }
                    }
                    else
                    {
                        image.AnimationDelay = 3; // 30fps
                    }
                    gif.Add(image);
                    anyValidFrames = true;
                }
            }
            if (!anyValidFrames)
                return;
            gif.Optimize();
            gif.Write(Path.Join(folder, sprite.Name.Content + ".gif"));
        }
    }

    /// <summary>Exports texture groups (TGIN) to a folder.</summary>
    public async void ExportTextureGroups()
    {
        EnsureDataLoaded();

        if (Data.TextureGroupInfo is null)
        {
            ScriptError("Texture group info is not present in the opened game.");
            return;
        }

        string mainOutputFolder = PromptChooseExportDirectory();
        if (mainOutputFolder is null)
            return;

        bool padding = ScriptQuestion("Use padding?");
        int processTgin = 0;

        using (TextureWorker worker = new())
        {
            await Task.Run(() =>
            {
                foreach (UndertaleTextureGroupInfo tgin in Data.TextureGroupInfo)
                {
                    if (tgin is null)
                        continue;
                    int progress = 0;
                    int sum = 0;
                    if (tgin.TexturePages != null)
                        sum += tgin.TexturePages.Count;
                    if (tgin.Sprites != null)
                        sum += tgin.Sprites.Count;
                    if (tgin.Fonts != null)
                        sum += tgin.Fonts.Count;
                    if (tgin.Tilesets != null)
                        sum += tgin.Tilesets.Count;
                    UpdateProgressBar(null, $"Processing \"{tgin.Name.Content}\" (TGIN Group {processTgin++})", progress, sum);
                    string outputFolder = Paths.JoinVerifyWithinDirectory(mainOutputFolder, tgin.Name.Content);
                    Directory.CreateDirectory(outputFolder);
                    if (tgin.TexturePages != null)
                    {
                        for (var i = 0; i < tgin.TexturePages.Count; i++)
                        {
                            UpdateProgressBar(null, $"Processing \"{tgin.Name.Content}\" EmbeddedTextures (TGIN Group {processTgin})", progress++, sum);
                            DumpEmbeddedTexturePage(outputFolder, tgin.TexturePages[i].Resource);
                        }
                    }
                    if (tgin.Sprites != null)
                    {
                        for (var i = 0; i < tgin.Sprites.Count; i++)
                        {
                            UpdateProgressBar(null, $"Processing \"{tgin.Name.Content}\" Sprites (TGIN Group {processTgin})", progress++, sum);
                            DumpSprite(outputFolder, tgin.Sprites[i].Resource);
                        }
                    }
                    if (tgin.Fonts != null)
                    {
                        for (var i = 0; i < tgin.Fonts.Count; i++)
                        {
                            UpdateProgressBar(null, $"Processing \"{tgin.Name.Content}\" Fonts (TGIN Group {processTgin})", progress++, sum);
                            DumpFont(outputFolder, tgin.Fonts[i].Resource);
                        }
                    }
                    if (tgin.Tilesets != null)
                    {
                        for (var i = 0; i < tgin.Tilesets.Count; i++)
                        {
                            UpdateProgressBar(null, $"Processing \"{tgin.Name.Content}\" Tilesets (TGIN Group {processTgin})", progress++, sum);
                            DumpTileset(outputFolder, tgin.Tilesets[i].Resource);
                        }
                    }
                }
            });

            void DumpEmbeddedTexturePage(string outputFolder, UndertaleEmbeddedTexture Emb)
            {
                string exportedTexturesFolder = Path.Join(outputFolder, "EmbeddedTextures");
                Directory.CreateDirectory(exportedTexturesFolder);
                try
                {
                    using (FileStream fs = new(Paths.JoinVerifyWithinDirectory(exportedTexturesFolder, $"{Data.EmbeddedTextures.IndexOf(Emb)}.png"), FileMode.Create))
                        Emb.TextureData.Image.SavePng(fs);
                }
                catch (Exception ex)
                {
                    ScriptMessage("Failed to export file: " + ex.Message);
                }
            }

            void DumpSprite(string outputFolder, UndertaleSprite spr)
            {
                for (int i = 0; i < spr.Textures.Count; i++)
                {
                    if (spr.Textures[i]?.Texture != null)
                    {
                        string exportedTexturesFolder = Path.Join(outputFolder, "Sprites");
                        Directory.CreateDirectory(exportedTexturesFolder);
                        UndertaleTexturePageItem tex = spr.Textures[i].Texture;
                        worker.ExportAsPNG(tex, Paths.JoinVerifyWithinDirectory(exportedTexturesFolder, $"{spr.Name.Content}_{i}.png"), null, padding);
                    }
                }
            }

            void DumpFont(string outputFolder, UndertaleFont fnt)
            {
                if (fnt.Texture != null)
                {
                    string exportedTexturesFolder = Path.Join(outputFolder, "Fonts");
                    Directory.CreateDirectory(exportedTexturesFolder);
                    UndertaleTexturePageItem tex = fnt.Texture;
                    worker.ExportAsPNG(tex, Paths.JoinVerifyWithinDirectory(exportedTexturesFolder, $"{fnt.Name.Content}.png"));
                }
            }

            void DumpTileset(string outputFolder, UndertaleBackground Tile)
            {
                if (Tile.Texture != null)
                {
                    string exportedTexturesFolder = Path.Join(outputFolder, "Tilesets");
                    Directory.CreateDirectory(exportedTexturesFolder);
                    UndertaleTexturePageItem tex = Tile.Texture;
                    worker.ExportAsPNG(tex, Paths.JoinVerifyWithinDirectory(exportedTexturesFolder, $"{Tile.Name.Content}.png"));
                }
            }
        }

        await FinalizeExportAsync();
        HideProgressBar();
        ScriptMessage("All graphics texture groups successfully exported.");
    }

    /// <summary>Exports all rooms as PNG images.</summary>
    public async void ExportAllRoomsToPNG()
    {
        EnsureDataLoaded();

        string exportedTexturesFolder = PromptChooseExportDirectory();
        if (exportedTexturesFolder is null)
            throw new Exception("The export folder was not set, stopping script.");

        bool displayGrid = ScriptQuestion("Draw room grid?");

        int roomCount = Data.Rooms.Count;
        SetProgressBar(null, "Rooms Exported", 0, roomCount);

        // NOTE: The original script used the WPF room renderer; this is the CPU-based Avalonia equivalent.
        for (int i = 0; i < roomCount; i++)
        {
            UndertaleRoom room = Data.Rooms[i];
            string roomName = room.Name.Content;
            string path = Path.Join(exportedTexturesFolder, roomName + ".png");
            try
            {
                using (FileStream file = File.OpenWrite(path))
                {
                    await ImportExport.ExportRoomAsPNG(room, file);
                }
            }
            catch (Exception e)
            {
                throw new Exception($"An error occurred while exporting room \"{roomName}\".\n{e}");
            }

            IncrementProgress();
        }

        await FinalizeExportAsync();
        HideProgressBar();
        ScriptMessage("Exported successfully.");
    }
}