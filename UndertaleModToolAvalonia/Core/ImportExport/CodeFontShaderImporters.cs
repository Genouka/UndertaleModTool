using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using UndertaleModLib;
using UndertaleModLib.Compiler;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;
using UndertaleModLib.Util;

namespace UndertaleModToolAvalonia;

public partial class ImportExportService
{
    /// <summary>Imports assembly code entries (.asm files) from a folder.</summary>
    public void ImportAssembly()
    {
        EnsureDataLoaded();

        string importFolder = PromptChooseDirectory();
        if (importFolder is null)
            throw new Exception("The import folder was not set.");

        string[] dirFiles = Directory.GetFiles(importFolder);
        if (dirFiles.Length == 0)
            throw new Exception("The selected folder is empty.");
        else if (!dirFiles.Any(x => x.EndsWith(".asm")))
            throw new Exception("The selected folder doesn't contain any ASM file.");

        bool stopOnError = ScriptQuestion("Stop importing on error?");

        SetProgressBar(null, "Files", 0, dirFiles.Length);

        foreach (string file in dirFiles)
        {
            string asm = File.ReadAllText(file);
            string codeName = Path.GetFileNameWithoutExtension(file);

            if (Data.Code.ByName(codeName) is UndertaleCode code)
            {
                try
                {
                    List<UndertaleInstruction> instructions = Assembler.Assemble(asm, Data, MainThreadAction);
                    MainThreadAction(() => code.Replace(instructions));
                }
                catch (Exception e)
                {
                    if (stopOnError)
                    {
                        throw new Exception($"Error on code entry {codeName}:\n{e}");
                    }
                    else
                    {
                        ScriptError($"Error on code entry {codeName}:\n{e}");
                    }
                }
            }
            else
            {
                if (stopOnError)
                {
                    throw new Exception($"Missing code entry {codeName} (must exist before importing)");
                }
                else
                {
                    ScriptError($"Missing code entry {codeName} (must exist before importing)");
                }
            }

            IncrementProgress();
        }

        HideProgressBar();
        ScriptMessage("All files successfully imported.");
    }

    /// <summary>Imports GML code entries (.gml files) from a folder, optionally linking to objects/scripts.</summary>
    public async void ImportGML()
    {
        EnsureDataLoaded();

        string importFolder = PromptChooseDirectory();
        if (importFolder is null)
            throw new Exception("The import folder was not set.");

        string[] dirFiles = Directory.GetFiles(importFolder, "*.gml");
        if (dirFiles.Length == 0)
            throw new Exception("The selected folder doesn't contain any GML files.");

        // Ask whether they want to link code. If no, will only generate code entry.
        bool doLink = ScriptQuestion("Do you want to automatically attempt to link imported code?");

        SetProgressBar(null, "Files", 0, dirFiles.Length);

        await Task.Run(() =>
        {
            UndertaleModLib.Compiler.CodeImportGroup importGroup = new(Data)
            {
                AutoCreateAssets = doLink,
                MainThreadAction = MainThreadAction
            };
            foreach (string file in dirFiles)
            {
                IncrementProgressParallel();

                string code = File.ReadAllText(file);
                string codeName = Path.GetFileNameWithoutExtension(file);
                importGroup.QueueReplace(codeName, code);
            }
            SetProgressBar(null, "Performing final import...", dirFiles.Length, dirFiles.Length);
            importGroup.Import();
        });

        HideProgressBar();
        ScriptMessage("All files successfully imported.");
    }

    /// <summary>Imports shaders from a folder, with each subfolder being one shader.</summary>
    public void ImportShaders()
    {
        EnsureDataLoaded();

        string importFolder = PromptChooseDirectory();
        if (importFolder is null)
            throw new Exception("The import folder was not set.");

        var shadersToModify = Directory.GetDirectories(importFolder).Select(x => Path.GetFileName(x));
        foreach (string shaderName in shadersToModify)
        {
            if (Data.Shaders.ByName(shaderName) is UndertaleShader existingShader)
            {
                ImportShader(existingShader);
            }
            else
            {
                AddShader(shaderName);
            }
        }

        void ImportShaderPlaintextFile(Action<UndertaleString> stringSetter, string importDirectory, string name)
        {
            string path = Path.Join(importDirectory, $"{name}.txt");
            if (!File.Exists(path))
            {
                stringSetter(Data.Strings.MakeString(""));
                return;
            }
            stringSetter(Data.Strings.MakeString(File.ReadAllText(path)));
        }

        void ImportShaderBinaryFile(Action<UndertaleShader.UndertaleRawShaderData> dataSetter, string importDirectory, string name)
        {
            string path = Path.Join(importDirectory, $"{name}.bin");
            if (!File.Exists(path))
            {
                dataSetter(new());
                return;
            }
            dataSetter(new()
            {
                Data = File.ReadAllBytes(path),
                IsNull = false
            });
        }

        void ImportShader(UndertaleShader existingShader, string existingImportDir = null)
        {
            string localImportDir = existingImportDir ?? Paths.JoinVerifyWithinDirectory(importFolder, existingShader.Name.Content);
            string shaderTypePath = Path.Join(localImportDir, "Type.txt");
            if (File.Exists(shaderTypePath))
            {
                string shaderType = File.ReadAllText(shaderTypePath);
                if (shaderType.Contains("GLSL_ES"))
                    existingShader.Type = UndertaleShader.ShaderType.GLSL_ES;
                else if (shaderType.Contains("GLSL"))
                    existingShader.Type = UndertaleShader.ShaderType.GLSL;
                else if (shaderType.Contains("HLSL9"))
                    existingShader.Type = UndertaleShader.ShaderType.HLSL9;
                else if (shaderType.Contains("HLSL11"))
                    existingShader.Type = UndertaleShader.ShaderType.HLSL11;
                else if (shaderType.Contains("PSSL"))
                    existingShader.Type = UndertaleShader.ShaderType.PSSL;
                else if (shaderType.Contains("Cg_PSVita"))
                    existingShader.Type = UndertaleShader.ShaderType.Cg_PSVita;
                else if (shaderType.Contains("Cg_PS3"))
                    existingShader.Type = UndertaleShader.ShaderType.Cg_PS3;
                else
                    throw new Exception($"Failed to determine shader type for shader {existingShader.Name.Content}");
            }
            else
            {
                existingShader.Type = UndertaleShader.ShaderType.GLSL_ES;
            }

            ImportShaderPlaintextFile((str) => existingShader.GLSL_ES_Fragment = str, localImportDir, "GLSL_ES_Fragment");
            ImportShaderPlaintextFile((str) => existingShader.GLSL_ES_Vertex = str, localImportDir, "GLSL_ES_Vertex");
            ImportShaderPlaintextFile((str) => existingShader.GLSL_Fragment = str, localImportDir, "GLSL_Fragment");
            ImportShaderPlaintextFile((str) => existingShader.GLSL_Vertex = str, localImportDir, "GLSL_Vertex");
            ImportShaderPlaintextFile((str) => existingShader.HLSL9_Fragment = str, localImportDir, "HLSL9_Fragment");
            ImportShaderPlaintextFile((str) => existingShader.HLSL9_Vertex = str, localImportDir, "HLSL9_Vertex");
            ImportShaderBinaryFile((data) => existingShader.HLSL11_VertexData = data, localImportDir, "HLSL11_VertexData");
            ImportShaderBinaryFile((data) => existingShader.HLSL11_PixelData = data, localImportDir, "HLSL11_PixelData");
            ImportShaderBinaryFile((data) => existingShader.PSSL_VertexData = data, localImportDir, "PSSL_VertexData");
            ImportShaderBinaryFile((data) => existingShader.PSSL_PixelData = data, localImportDir, "PSSL_PixelData");
            ImportShaderBinaryFile((data) => existingShader.Cg_PSVita_VertexData = data, localImportDir, "Cg_PSVita_VertexData");
            ImportShaderBinaryFile((data) => existingShader.Cg_PSVita_PixelData = data, localImportDir, "Cg_PSVita_PixelData");
            ImportShaderBinaryFile((data) => existingShader.Cg_PS3_VertexData = data, localImportDir, "Cg_PS3_VertexData");
            ImportShaderBinaryFile((data) => existingShader.Cg_PS3_PixelData = data, localImportDir, "Cg_PS3_PixelData");

            existingShader.VertexShaderAttributes.Clear();
            string vertexShaderAttributesPath = Path.Join(localImportDir, "VertexShaderAttributes.txt");
            if (File.Exists(vertexShaderAttributesPath))
            {
                string line;
                using StreamReader file = new(vertexShaderAttributesPath);
                while ((line = file.ReadLine()) is not null)
                {
                    line = line.Trim();
                    if (line.Length == 0)
                        continue;
                    existingShader.VertexShaderAttributes.Add(new()
                    {
                        Name = Data.Strings.MakeString(line)
                    });
                }
            }

            Project?.MarkAssetForExport(existingShader);
        }

        void AddShader(string shaderName)
        {
            UndertaleShader newShader = new()
            {
                Name = Data.Strings.MakeString(shaderName)
            };
            string localImportDir = Paths.JoinVerifyWithinDirectory(importFolder, shaderName);
            ImportShader(newShader, localImportDir);
            Data.Shaders.Add(newShader);
        }
    }

    /// <summary>Imports all strings from a text file.</summary>
    public void ImportAllStrings()
    {
        EnsureDataLoaded();

        string stringsPath = PromptLoadFile("");
        if (string.IsNullOrWhiteSpace(stringsPath))
            throw new Exception("The import file was not set.");

        int file_length = 0;
        string line = "";
        using (StreamReader reader = new(stringsPath))
        {
            while ((line = reader.ReadLine()) is not null)
            {
                file_length += 1;
            }
        }

        int validStringsCount = 0;
        foreach (var str in Data.Strings)
        {
            if (str.Content.Contains("\n") || str.Content.Contains("\r"))
                continue;
            validStringsCount += 1;
        }

        if (file_length < validStringsCount)
        {
            ScriptError("ERROR 0: Unexpected end of file at line: " + file_length.ToString() + ". Expected file length was: " + validStringsCount.ToString() + ". No changes have been made.", "Error");
            return;
        }
        else if (file_length > validStringsCount)
        {
            ScriptError("ERROR 1: Line count exceeds expected count. Current count: " + file_length.ToString() + ". Expected count: " + validStringsCount.ToString() + ". No changes have been made.", "Error");
            return;
        }

        using (StreamReader reader = new(stringsPath))
        {
            int line_no = 1;
            line = "";
            foreach (var str in Data.Strings)
            {
                if (str.Content.Contains("\n") || str.Content.Contains("\r"))
                    continue;
                if (!((line = reader.ReadLine()) is not null))
                {
                    ScriptError("ERROR 2: Unexpected end of file at line: " + line_no.ToString() + ". Expected file length was: " + validStringsCount.ToString() + ". No changes have been made.", "Error");
                    return;
                }
                line_no += 1;
            }
        }

        using (StreamReader reader = new(stringsPath))
        {
            int line_no = 1;
            line = "";
            foreach (var str in Data.Strings)
            {
                if (str.Content.Contains("\n") || str.Content.Contains("\r"))
                    continue;
                if ((line = reader.ReadLine()) is not null)
                    str.Content = line;
                else
                {
                    ScriptError("ERROR 3: Unexpected end of file at line: " + line_no.ToString() + ". Expected file length was: " + validStringsCount.ToString() + ". All lines within the file have been applied. Please check for errors.", "Error");
                    return;
                }
                line_no += 1;
            }
        }
    }

    /// <summary>Imports all strings from a JSON file.</summary>
    public void ImportAllStringsJSON()
    {
        EnsureDataLoaded();

        string path = PromptLoadFile("");
        if (string.IsNullOrWhiteSpace(path))
            throw new Exception("The import file was not set.");

        string file = File.ReadAllText(path);
        JsonElement json = JsonSerializer.Deserialize<JsonElement>(file);
        JsonElement.ArrayEnumerator array = json.GetProperty("Strings").EnumerateArray();
        int i = 0;
        foreach (JsonElement elmnt in array)
            Data.Strings[i++].Content = elmnt.ToString();
        ScriptMessage("Successfully imported");
    }

    /// <summary>Imports a GM font (.yy file) as an UndertaleFont.</summary>
    public void ImportGMS2FontData()
    {
        EnsureDataLoaded();

        ScriptMessage(
            "ImportGMS2FontData by Dobby233Liu\n" +
            "This can import GM font asset data to your mod\n" +
            "(Designed for the data IDE v2023.8.2.108 generates)\n" +
            "Select the .yy file of the GM font asset you want to import");

        string importFile = PromptLoadFile(".yy");
        if (importFile is null)
        {
            ScriptError("Import cancelled.");
            return;
        }

        JsonDocument fontData;
        using (StreamReader file = File.OpenText(importFile))
        {
            fontData = JsonDocument.Parse(file.ReadToEnd());
        }

        using (fontData)
        {
            JsonElement root = fontData.RootElement;
            string? fontDataName = null;
            if (root.TryGetProperty("name", out JsonElement nameProp))
                fontDataName = nameProp.GetString();

            string fontPath = Path.GetDirectoryName(importFile);
            string yyFilename = Path.GetFileNameWithoutExtension(importFile);
            string fontName = fontDataName ?? yyFilename;
            string fontTexturePath = Paths.JoinVerifyWithinDirectory(fontPath, yyFilename + ".png");
            // Failsafe to use font name
            if (!File.Exists(fontTexturePath))
                fontTexturePath = Paths.JoinVerifyWithinDirectory(fontPath, fontName + ".png");
            // If we still can't find the texture
            if (!File.Exists(fontTexturePath))
                throw new Exception(
                    $"Could not find a texture file for the selected font.\n" +
                    $"Try renaming the correct texture file to\n" +
                    $"{yyFilename}.png\n" +
                    $"and putting it in the same directory as the .yy file.");

            bool tginExists = Data.TextureGroupInfo is not null;
            // Default to putting the font into the default texgroup
            UndertaleTextureGroupInfo fontTexGroup = null;
            if (tginExists)
                fontTexGroup = Data.TextureGroupInfo.ByName("Default");

            UndertaleFont font = Data.Fonts.ByName(fontName);
            if (font is null)
            {
                font = new UndertaleFont()
                {
                    Name = Data.Strings.MakeString(fontName)
                };
                Data.Fonts.Add(font);
            }

            // Get texture properties
            (int parsedWidth, int parsedHeight) = TextureWorker.GetImageSizeFromFile(fontTexturePath);
            if (parsedWidth == -1 || parsedHeight == -1)
                throw new Exception("Invalid font texture image");
            ushort width = (ushort)parsedWidth;
            ushort height = (ushort)parsedHeight;

            UndertaleEmbeddedTexture texture = new()
            {
                Name = new UndertaleString($"Texture {Data.EmbeddedTextures.Count}"),
            };
            texture.TextureData.Image = GMImage.FromPng(File.ReadAllBytes(fontTexturePath));
            Data.EmbeddedTextures.Add(texture);

            UndertaleTexturePageItem texturePageItem = new()
            {
                Name = new UndertaleString($"PageItem {Data.TexturePageItems.Count}"),
                TexturePage = texture,
                SourceX = 0,
                SourceY = 0,
                SourceWidth = width,
                SourceHeight = height,
                TargetX = 0,
                TargetY = 0,
                TargetWidth = width,
                TargetHeight = height,
                BoundingWidth = width,
                BoundingHeight = height
            };
            Data.TexturePageItems.Add(texturePageItem);

            font.DisplayName = Data.Strings.MakeString(GetString(root, "fontName"));
            font.Texture = texturePageItem;
            font.Bold = GetBool(root, "bold");
            font.Italic = GetBool(root, "italic");
            font.EmSize = GetUInt(root, "size");
            font.EmSizeIsFloat = Data.IsVersionAtLeast(2, 3);
            font.Charset = GetByte(root, "charset");
            font.AntiAliasing = GetByte(root, "AntiAlias");
            font.ScaleX = 1;
            font.ScaleY = 1;
            if (root.TryGetProperty("ascender", out JsonElement ascender))
                font.Ascender = (uint)ascender.GetInt32();
            if (root.TryGetProperty("ascenderOffset", out JsonElement ascenderOffset))
                font.AscenderOffset = ascenderOffset.GetInt32();
            if (root.TryGetProperty("usesSDF", out JsonElement usesSDF) && usesSDF.GetBoolean()
                && root.TryGetProperty("sdfSpread", out JsonElement sdfSpread))
                font.SDFSpread = (uint)sdfSpread.GetInt32();
            if (root.TryGetProperty("lineHeight", out JsonElement lineHeight))
                font.LineHeight = (uint)lineHeight.GetInt32();

            // FIXME: Too complicated?
            List<int> charRangesUppersAndLowers = new();
            if (root.TryGetProperty("ranges", out JsonElement ranges))
            {
                foreach (JsonElement range in ranges.EnumerateArray())
                {
                    if (range.TryGetProperty("upper", out JsonElement upper))
                        charRangesUppersAndLowers.Add(upper.GetInt32());
                    if (range.TryGetProperty("lower", out JsonElement lower))
                        charRangesUppersAndLowers.Add(lower.GetInt32());
                }
            }
            charRangesUppersAndLowers.Sort();
            font.RangeStart = (ushort)charRangesUppersAndLowers.DefaultIfEmpty(0).FirstOrDefault();
            font.RangeEnd = (uint)charRangesUppersAndLowers.DefaultIfEmpty(0xFFFF).LastOrDefault();

            List<UndertaleFont.Glyph> glyphs = new();
            if (root.TryGetProperty("glyphs", out JsonElement glyphsProp))
            {
                foreach (JsonProperty glyphKVEntry in glyphsProp.EnumerateObject())
                {
                    var glyphData = glyphKVEntry.Value;
                    glyphs.Add(new UndertaleFont.Glyph()
                    {
                        Character = GetUShort(glyphData, "character"),
                        SourceX = GetUShort(glyphData, "x"),
                        SourceY = GetUShort(glyphData, "y"),
                        SourceWidth = GetUShort(glyphData, "w"),
                        SourceHeight = GetUShort(glyphData, "h"),
                        Shift = GetShort(glyphData, "shift"),
                        Offset = GetShort(glyphData, "offset"),
                    });
                }
            }
            // Sort glyphs like UndertaleFontEditor to be safe
            glyphs.Sort((x, y) => x.Character.CompareTo(y.Character));
            font.Glyphs.Clear();
            foreach (var glyph in glyphs)
                font.Glyphs.Add(glyph);

            glyphs = font.Glyphs.ToList();
            // TODO: applyKerning??
            if (root.TryGetProperty("kerningPairs", out JsonElement kerningPairs))
            {
                foreach (JsonElement kerningPair in kerningPairs.EnumerateArray())
                {
                    var first = GetUShort(kerningPair, "first");
                    var glyph = glyphs.FirstOrDefault(x => x.Character == first);
                    glyph.Kerning.Add(new UndertaleFont.Glyph.GlyphKerning()
                    {
                        Character = GetShort(kerningPair, "second"),
                        ShiftModifier = GetShort(kerningPair, "amount"),
                    });
                }
            }

            ScriptMessage("Import complete.");
        }
    }

    static string GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out JsonElement v) ? v.GetString() ?? "" : "";

    static bool GetBool(JsonElement el, string name)
        => el.TryGetProperty(name, out JsonElement v) && v.GetBoolean();

    static uint GetUInt(JsonElement el, string name)
        => el.TryGetProperty(name, out JsonElement v) ? checked((uint)v.GetInt64()) : 0;

    static byte GetByte(JsonElement el, string name)
        => el.TryGetProperty(name, out JsonElement v) ? v.GetByte() : (byte)0;

    static ushort GetUShort(JsonElement el, string name)
        => el.TryGetProperty(name, out JsonElement v) ? v.GetUInt16() : (ushort)0;

    static short GetShort(JsonElement el, string name)
        => el.TryGetProperty(name, out JsonElement v) ? v.GetInt16() : (short)0;
}