using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using ImageMagick;
using UndertaleModLib.Compiler;
using UndertaleModLib.Models;
using UndertaleModLib.Util;

namespace UndertaleModLib.Project;

/// <summary>
/// Metadata read from the <c>metadata.json</c> file of a GameMaker package.
/// </summary>
public sealed record YyPackageMetadata(string PackageId, string DisplayName, string Version,
                                       string PackageType, string IdeVersion);

/// <summary>
/// Resource types which can be imported from a GameMaker package.
/// </summary>
internal enum YyResourceKind
{
    Sprite,
    Sound,
    Object,
    Script,
    Font
}

/// <summary>
/// A single resource contained within a GameMaker package (.yymps file).
/// </summary>
internal sealed class YyResource : IDisposable
{
    public YyResourceKind Kind;
    public string Name;
    public JsonDocument Document;
    public readonly Dictionary<string, ZipArchiveEntry> Files = new(StringComparer.OrdinalIgnoreCase);

    public JsonElement Root => Document.RootElement;

    public void Dispose()
    {
        Document?.Dispose();
        Document = null;
        Files.Clear();
    }
}

/// <summary>
/// Importer for GameMaker local packages (<c>.yymps</c> files), which are ZIP archives containing
/// a subset of a GameMaker Studio 2 project.
/// </summary>
public sealed class YyPackageImporter : IResourceImportSource
{
    // Resource types which can be imported, along with the project folder they are stored in.
    private static readonly (string Folder, YyResourceKind Kind)[] SupportedFolders =
    [
        ("sprites", YyResourceKind.Sprite),
        ("sounds", YyResourceKind.Sound),
        ("objects", YyResourceKind.Object),
        ("scripts", YyResourceKind.Script),
        ("fonts", YyResourceKind.Font)
    ];

    // GameMaker project folder names for resource types which are not supported (used for user notices).
    private static readonly HashSet<string> KnownResourceFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "sprites", "sounds", "objects", "scripts", "fonts", "backgrounds", "tilesets", "paths", "rooms",
        "shaders", "sequences", "animcurves", "notes", "extensions", "timelines", "particles"
    };

    /// <summary>
    /// Game data the package resources are imported into.
    /// </summary>
    public UndertaleData TargetData { get; }

    /// <summary>
    /// Path of the currently loaded package, or <see langword="null"/> if none is loaded.
    /// </summary>
    public string PackagePath { get; private set; }

    /// <summary>
    /// Metadata of the currently loaded package, or <see langword="null"/> if none is loaded.
    /// </summary>
    public YyPackageMetadata Metadata { get; private set; }

    private readonly Action<Action> _mainThreadAction;
    private readonly List<YyResource> _resources = new();
    private readonly List<string> _notices = new();
    private ZipArchive _archive;
    private bool _disposed;

    /// <inheritdoc/>
    public IReadOnlyList<string> SourceNotices => _notices;

    public YyPackageImporter(UndertaleData targetData, Action<Action> mainThreadAction = null)
    {
        TargetData = targetData ?? throw new ArgumentNullException(nameof(targetData));
        _mainThreadAction = mainThreadAction ?? new Action<Action>(f => f());
    }

    /// <inheritdoc/>
    public void LoadFile(string filePath) => LoadPackage(filePath);

    /// <summary>
    /// Loads a GameMaker package, replacing any previously loaded package.
    /// </summary>
    public void LoadPackage(string filePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Package file not found: {filePath}", filePath);

        UnloadPackage();

        PackagePath = filePath;
        _archive = ZipFile.OpenRead(filePath);

        Metadata = ReadMetadata(_archive);
        BuildResourceIndex();
    }

    private void UnloadPackage()
    {
        foreach (YyResource resource in _resources)
            resource.Dispose();
        _resources.Clear();
        _notices.Clear();
        _archive?.Dispose();
        _archive = null;
        PackagePath = null;
        Metadata = null;
    }

    private static YyPackageMetadata ReadMetadata(ZipArchive archive)
    {
        ZipArchiveEntry entry = archive.GetEntry("metadata.json");
        if (entry is null)
            return null;

        using JsonDocument document = ReadJson(entry);
        JsonElement root = document.RootElement;
        return new YyPackageMetadata(GetString(root, "package_id") ?? "",
                                     GetString(root, "display_name") ?? "",
                                     GetString(root, "version") ?? "",
                                     GetString(root, "package_type") ?? "",
                                     GetString(root, "ide_version") ?? "");
    }

    private void BuildResourceIndex()
    {
        Dictionary<string, YyResourceKind> supportedFolders = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string folder, YyResourceKind kind) in SupportedFolders)
            supportedFolders.Add(folder, kind);

        Dictionary<string, YyResource> resourcesByKey = new(StringComparer.OrdinalIgnoreCase);
        List<(string Key, string RelativePath, ZipArchiveEntry Entry)> files = new();
        Dictionary<string, int> unsupportedCounts = new(StringComparer.OrdinalIgnoreCase);

        foreach (ZipArchiveEntry entry in _archive.Entries)
        {
            // Packages use forward slashes, but be tolerant of archives made on Windows
            string path = entry.FullName.Replace('\\', '/');
            string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;

            string folder = parts[0];
            if (!supportedFolders.TryGetValue(folder, out YyResourceKind kind))
            {
                // Count resources of unsupported types, based on their main resource file
                if (parts.Length == 3 && parts[2].EndsWith(".yy", StringComparison.OrdinalIgnoreCase) &&
                    KnownResourceFolders.Contains(folder))
                {
                    unsupportedCounts.TryGetValue(folder, out int count);
                    unsupportedCounts[folder] = count + 1;
                }
                continue;
            }

            if (parts.Length == 2)
                continue;

            string resourceName = parts[1];
            string key = $"{folder}/{resourceName}";

            if (parts.Length == 3 && parts[2].EndsWith(".yy", StringComparison.OrdinalIgnoreCase))
            {
                YyResource resource = new()
                {
                    Kind = kind,
                    Name = resourceName
                };
                try
                {
                    resource.Document = ReadJson(entry);
                }
                catch (Exception ex)
                {
                    _notices.Add($"Failed to read \"{path}\": {ex.Message}");
                    resource.Dispose();
                    continue;
                }
                resourcesByKey[key] = resource;
            }
            else if (parts.Length > 2)
            {
                files.Add((key, path[(key.Length + 1)..], entry));
            }
        }

        foreach ((string key, string relativePath, ZipArchiveEntry entry) in files)
        {
            if (resourcesByKey.TryGetValue(key, out YyResource resource))
                resource.Files[relativePath] = entry;
        }

        _resources.AddRange(resourcesByKey.Values
            .OrderBy(static r => Array.FindIndex(SupportedFolders, f => f.Kind == r.Kind))
            .ThenBy(static r => r.Name, StringComparer.OrdinalIgnoreCase));

        if (unsupportedCounts.Count > 0)
        {
            string summary = string.Join(", ", unsupportedCounts
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair => $"{pair.Key} ({pair.Value})"));
            _notices.Add($"Resources of unsupported types will be skipped: {summary}");
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<ImportableResource> EnumerateResources()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_archive is null)
            throw new InvalidOperationException("No package loaded. Call LoadPackage first.");

        List<ImportableResource> resources = new(_resources.Count);
        foreach (YyResource resource in _resources)
        {
            resources.Add(new ImportableResource
            {
                Name = resource.Name,
                TypeName = KindToAssetType(resource.Kind).ToInterfaceName(),
                ExistsInTarget = ExistsInTarget(resource),
                Payload = resource
            });
        }
        return resources;
    }

    /// <inheritdoc/>
    public CrossFileImportResult ImportResources(IReadOnlyList<ImportableResource> selectedResources,
                                                 NameConflictResolution conflictResolution,
                                                 bool importDependencies)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_archive is null)
            throw new InvalidOperationException("No package loaded.");

        CrossFileImportResult result = new();

        HashSet<YyResource> selected = new();
        foreach (ImportableResource resource in selectedResources)
        {
            if (resource.Payload is not YyResource yyResource)
                throw new ArgumentException($"Resource \"{resource.Name}\" is not from a YYMPs package.", nameof(selectedResources));

            selected.Add(yyResource);
        }

        HashSet<YyResource> resourcesToImport = new(selected);
        if (importDependencies)
            CollectDependencies(selected, resourcesToImport, result.Warnings);

        List<YyImportEntry> entries = new(resourcesToImport.Count);
        foreach (YyResource resource in _resources)
        {
            if (!resourcesToImport.Contains(resource))
                continue;

            string targetName = resource.Name;
            bool overwrite = false;
            if (ExistsInTarget(resource))
            {
                switch (conflictResolution)
                {
                    case NameConflictResolution.Skip:
                        result.SkippedCount++;
                        continue;
                    case NameConflictResolution.Rename:
                        targetName = GenerateUniqueName(resource.Name, resource.Kind);
                        break;
                    case NameConflictResolution.Overwrite:
                        overwrite = true;
                        result.OverwrittenCount++;
                        break;
                }
            }

            if (resource.Kind is YyResourceKind.Script or YyResourceKind.Object && TargetData.Code is null)
            {
                result.Errors.Add($"Cannot import \"{resource.Name}\": the target game data has no code entries.");
                continue;
            }

            entries.Add(new YyImportEntry(resource, targetName, overwrite));
        }

        if (entries.Count == 0)
            return result;

        // Decode images and audio first, as this is potentially slow and touches no game data
        TextureGroupPacker packer = new();
        Dictionary<YyImportEntry, List<UndertaleTexturePageItem>> spriteTextures = new();
        Dictionary<YyImportEntry, UndertaleTexturePageItem> fontTextures = new();
        Dictionary<YyImportEntry, (string FileName, byte[] Data)> soundFiles = new();
        DecodePackageAssets(entries, packer, spriteTextures, fontTextures, soundFiles, result);

        List<(UndertaleCode Code, string Source)> codeQueue = new();

        _mainThreadAction(() =>
        {
            // Objects are created first, so that references between them (parents, collisions) resolve
            foreach (YyImportEntry entry in entries)
            {
                if (entry.Resource.Kind != YyResourceKind.Object)
                    continue;

                try
                {
                    ImportObjectShell(entry);
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Failed to create object \"{entry.TargetName}\": {ex.Message}");
                }
            }

            foreach (YyImportEntry entry in entries)
            {
                try
                {
                    switch (entry.Resource.Kind)
                    {
                        case YyResourceKind.Sprite:
                            ImportSprite(entry, spriteTextures.GetValueOrDefault(entry), result);
                            break;
                        case YyResourceKind.Sound:
                            ImportSound(entry, soundFiles.GetValueOrDefault(entry), result);
                            break;
                        case YyResourceKind.Font:
                            ImportFont(entry, fontTextures.GetValueOrDefault(entry), result);
                            break;
                        case YyResourceKind.Script:
                            ImportScript(entry, codeQueue, result);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Failed to import \"{entry.Resource.Name}\": {ex.Message}");
                }
            }

            // Objects are populated last, once all sprites, fonts and sounds exist
            foreach (YyImportEntry entry in entries)
            {
                if (entry.Object is null)
                    continue;

                try
                {
                    ImportObject(entry, codeQueue, result);
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Failed to import object \"{entry.TargetName}\": {ex.Message}");
                }
            }
        });

        try
        {
            packer.PackPages();
            _mainThreadAction(() => packer.ImportToData(TargetData));
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Texture import failed: {ex.Message}");
        }

        if (codeQueue.Count > 0)
        {
            CodeImportGroup importGroup = new(TargetData)
            {
                AutoCreateAssets = false,
                MainThreadAction = _mainThreadAction
            };

            foreach ((UndertaleCode code, string source) in codeQueue)
                importGroup.QueueReplace(code, source);

            try
            {
                importGroup.Import(true);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Code compilation failed: {ex.Message}");
            }
        }

        return result;
    }

    private void DecodePackageAssets(List<YyImportEntry> entries, TextureGroupPacker packer,
                                     Dictionary<YyImportEntry, List<UndertaleTexturePageItem>> spriteTextures,
                                     Dictionary<YyImportEntry, UndertaleTexturePageItem> fontTextures,
                                     Dictionary<YyImportEntry, (string FileName, byte[] Data)> soundFiles,
                                     CrossFileImportResult result)
    {
        foreach (YyImportEntry entry in entries)
        {
            YyResource resource = entry.Resource;
            switch (resource.Kind)
            {
                case YyResourceKind.Sprite:
                {
                    List<UndertaleTexturePageItem> textures = new();
                    if (resource.Root.TryGetProperty("frames", out JsonElement frames) &&
                        frames.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement frame in frames.EnumerateArray())
                        {
                            string frameName = GetString(frame, "name");
                            if (string.IsNullOrEmpty(frameName))
                            {
                                textures.Add(null);
                                continue;
                            }

                            ZipArchiveEntry imageEntry = FindFile(resource, $"{frameName}.png");
                            if (imageEntry is null)
                            {
                                result.Warnings.Add($"Sprite \"{resource.Name}\": frame \"{frameName}.png\" was not found; the frame will be empty.");
                                textures.Add(null);
                                continue;
                            }

                            try
                            {
                                textures.Add(packer.AddImage(LoadImage(imageEntry), TextureGroupPacker.BorderFlags.Enabled));
                            }
                            catch (Exception ex)
                            {
                                result.Warnings.Add($"Sprite \"{resource.Name}\": failed to import frame \"{frameName}\": {ex.Message}");
                                textures.Add(null);
                            }
                        }
                    }
                    else
                    {
                        result.Warnings.Add($"Sprite \"{resource.Name}\": no frames were found.");
                    }
                    spriteTextures[entry] = textures;
                    break;
                }
                case YyResourceKind.Font:
                {
                    ZipArchiveEntry imageEntry = FindFile(resource, $"{resource.Name}.png");
                    if (imageEntry is null)
                    {
                        result.Warnings.Add($"Font \"{resource.Name}\": texture image was not found.");
                        break;
                    }

                    try
                    {
                        // Font textures must not be cropped, as glyph coordinates refer to the full image
                        fontTextures[entry] = packer.AddImage(LoadImage(imageEntry),
                                                              TextureGroupPacker.BorderFlags.Enabled, allowCrop: false);
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add($"Font \"{resource.Name}\": failed to import texture: {ex.Message}");
                    }
                    break;
                }
                case YyResourceKind.Sound:
                {
                    string fileName = GetString(resource.Root, "soundFile");
                    ZipArchiveEntry audioEntry = !string.IsNullOrEmpty(fileName) ? FindFile(resource, fileName) : null;
                    if (audioEntry is null)
                    {
                        KeyValuePair<string, ZipArchiveEntry> fallback =
                            resource.Files.FirstOrDefault(static file => IsAudioFile(file.Key));
                        audioEntry = fallback.Value;
                        if (audioEntry is not null)
                            fileName = fallback.Key;
                    }

                    if (audioEntry is null)
                    {
                        result.Warnings.Add($"Sound \"{resource.Name}\": no audio file was found.");
                        break;
                    }

                    using Stream stream = audioEntry.Open();
                    using MemoryStream memoryStream = new();
                    stream.CopyTo(memoryStream);
                    soundFiles[entry] = (fileName, memoryStream.ToArray());
                    break;
                }
            }
        }
    }

    private void ImportSprite(YyImportEntry entry, List<UndertaleTexturePageItem> textures, CrossFileImportResult result)
    {
        JsonElement root = entry.Resource.Root;
        UndertaleSprite sprite = entry.Overwrite ? TargetData.Sprites.ByName(entry.TargetName) : null;
        if (sprite is null)
        {
            sprite = new UndertaleSprite
            {
                Name = TargetData.Strings.MakeString(entry.TargetName)
            };
            TargetData.Sprites.Add(sprite);
        }

        uint width = GetUInt32(root, "width", 0);
        uint height = GetUInt32(root, "height", 0);

        sprite.Width = width;
        sprite.Height = height;
        sprite.MarginLeft = GetInt32(root, "bbox_left", 0);
        sprite.MarginRight = GetInt32(root, "bbox_right", (int)width - 1);
        sprite.MarginTop = GetInt32(root, "bbox_top", 0);
        sprite.MarginBottom = GetInt32(root, "bbox_bottom", (int)height - 1);
        sprite.BBoxMode = GetUInt32(root, "bboxMode", 0);
        sprite.SepMasks = GetCollisionMaskType(GetInt32(root, "collisionKind", 1), entry.TargetName, result);
        sprite.Transparent = true;
        sprite.Smooth = false;
        sprite.Preload = true;

        JsonElement sequence = GetProperty(root, "sequence");
        int originType = GetInt32(root, "origin", 0);
        int originX = 0, originY = 0;
        if (sequence.ValueKind == JsonValueKind.Object)
        {
            originX = GetInt32(sequence, "xorigin", 0);
            originY = GetInt32(sequence, "yorigin", 0);
        }
        else if (originType == 1)
        {
            originX = (int)(width / 2);
            originY = (int)(height / 2);
        }
        else if (originType == 2)
        {
            result.Warnings.Add($"Sprite \"{entry.TargetName}\": a custom origin was defined, but no sequence data was found; the origin is set to the top left.");
        }
        sprite.OriginX = originX;
        sprite.OriginY = originY;

        // GameMaker 2 sprites store animation playback information in a special sprite layout
        if (TargetData.IsGameMaker2())
        {
            sprite.IsSpecialType = true;
            sprite.SVersion = 1;
            sprite.SSpriteType = UndertaleSprite.SpriteType.Normal;
            sprite.V2Sequence = null;
            sprite.V3NineSlice = null;
            sprite.GMS2PlaybackSpeed = GetSingle(sequence, "playbackSpeed", 15f);
            sprite.GMS2PlaybackSpeedType = GetInt32(sequence, "playbackSpeedType", 0) == 0
                ? AnimSpeedType.FramesPerSecond
                : AnimSpeedType.FramesPerGameFrame;
        }
        else
        {
            sprite.IsSpecialType = false;
        }

        sprite.Textures.Clear();
        if (textures is not null)
        {
            foreach (UndertaleTexturePageItem texture in textures)
                sprite.Textures.Add(texture is null ? null : new UndertaleSprite.TextureEntry { Texture = texture });
        }

        sprite.CollisionMasks.Clear();
        result.ImportedCount++;
    }

    private void ImportFont(YyImportEntry entry, UndertaleTexturePageItem texture, CrossFileImportResult result)
    {
        JsonElement root = entry.Resource.Root;
        UndertaleFont font = entry.Overwrite ? TargetData.Fonts.ByName(entry.TargetName) : null;
        if (font is null)
        {
            font = new UndertaleFont
            {
                Name = TargetData.Strings.MakeString(entry.TargetName)
            };
            TargetData.Fonts.Add(font);
        }

        font.DisplayName = TargetData.Strings.MakeString(GetString(root, "fontName") ?? entry.TargetName);
        font.EmSizeIsFloat = TargetData.IsVersionAtLeast(2);
        font.EmSize = GetSingle(root, "size", 12f);
        font.Bold = GetBool(root, "bold");
        font.Italic = GetBool(root, "italic");
        font.RangeStart = (ushort)GetInt32(root, "first", 0);
        font.RangeEnd = (uint)GetInt32(root, "last", 0);
        font.Charset = (byte)GetInt32(root, "charset", 0);
        font.AntiAliasing = (byte)GetInt32(root, "AntiAlias", 0);
        font.Ascender = (uint)GetInt32(root, "ascender", 0);
        font.LineHeight = (uint)GetInt32(root, "lineHeight", 0);
        font.AscenderOffset = GetInt32(root, "ascenderOffset", 0);
        font.ScaleX = 1;
        font.ScaleY = 1;
        font.Texture = texture;

        font.Glyphs.Clear();
        if (root.TryGetProperty("glyphs", out JsonElement glyphs) && glyphs.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in glyphs.EnumerateObject())
            {
                JsonElement glyph = property.Value;
                font.Glyphs.Add(new UndertaleFont.Glyph
                {
                    Character = (ushort)GetInt32(glyph, "character", 0),
                    SourceX = (ushort)GetInt32(glyph, "x", 0),
                    SourceY = (ushort)GetInt32(glyph, "y", 0),
                    SourceWidth = (ushort)GetInt32(glyph, "w", 0),
                    SourceHeight = (ushort)GetInt32(glyph, "h", 0),
                    Shift = (short)GetInt32(glyph, "shift", 0),
                    Offset = (short)GetInt32(glyph, "offset", 0)
                });
            }
        }
        else
        {
            result.Warnings.Add($"Font \"{entry.TargetName}\": no glyph data was found.");
        }

        if (texture is null)
            result.Warnings.Add($"Font \"{entry.TargetName}\": the font texture could not be imported; the font will not render correctly.");

        result.ImportedCount++;
    }

    private void ImportSound(YyImportEntry entry, (string FileName, byte[] Data) audioData, CrossFileImportResult result)
    {
        if (audioData.FileName is null || audioData.Data is null)
            return;

        YyResource resource = entry.Resource;
        JsonElement root = resource.Root;

        string fileName = audioData.FileName;

        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is not (".wav" or ".ogg" or ".mp3"))
        {
            result.Warnings.Add($"Sound \"{entry.TargetName}\": audio format \"{extension}\" is not supported.");
            return;
        }

        UndertaleSound sound = entry.Overwrite ? TargetData.Sounds.ByName(entry.TargetName) : null;
        if (sound is null)
        {
            sound = new UndertaleSound
            {
                Name = TargetData.Strings.MakeString(entry.TargetName)
            };
            TargetData.Sounds.Add(sound);
        }

        sound.Type = TargetData.Strings.MakeString(extension);
        sound.File = TargetData.Strings.MakeString(fileName);
        sound.Volume = GetSingle(root, "volume", 1f);
        sound.Pitch = 0f;
        sound.Effects = 0;
        sound.Preload = true;
        sound.Flags = extension == ".wav"
            ? UndertaleSound.AudioEntryFlags.IsEmbedded | UndertaleSound.AudioEntryFlags.Regular
            : UndertaleSound.AudioEntryFlags.IsCompressed | UndertaleSound.AudioEntryFlags.Regular;
        sound.AudioLength = TargetData.IsVersionAtLeast(2024, 6) ? GetSingle(root, "duration", 0f) : 0f;
        sound.AudioGroup = null;
        sound.GroupID = TargetData.GetBuiltinSoundGroupID();

        UndertaleEmbeddedAudio audio = sound.AudioFile;
        if (audio is null)
        {
            audio = new UndertaleEmbeddedAudio();
            sound.AudioFile = audio;
            sound.AudioID = TargetData.EmbeddedAudio.Count;
            TargetData.EmbeddedAudio.Add(audio);
        }
        audio.Data = audioData.Data;

        result.ImportedCount++;
    }

    private void ImportScript(YyImportEntry entry, List<(UndertaleCode Code, string Source)> codeQueue,
                              CrossFileImportResult result)
    {
        YyResource resource = entry.Resource;
        string source = ReadTextFile(resource, $"{resource.Name}.gml");
        if (source is null)
        {
            result.Warnings.Add($"Script \"{resource.Name}\": no GML source file was found.");
            return;
        }

        bool globalScripts = TargetData.IsVersionAtLeast(2, 3);
        UndertaleCode code = null;
        UndertaleScript script = null;

        if (entry.Overwrite)
        {
            if (globalScripts)
                code = TargetData.Code.ByName($"gml_GlobalScript_{entry.TargetName}");
            script = TargetData.Scripts?.ByName(entry.TargetName);
            code ??= script?.Code;
        }

        if (code is null)
        {
            string codeName = globalScripts
                ? $"gml_GlobalScript_{entry.TargetName}"
                : $"gml_Script_{entry.TargetName}";
            code = UndertaleCode.CreateEmptyEntry(TargetData, codeName);
        }

        if (globalScripts)
        {
            if (TargetData.GlobalInitScripts is not null &&
                !TargetData.GlobalInitScripts.Any(globalInit => globalInit.Code == code))
            {
                TargetData.GlobalInitScripts.Add(new UndertaleGlobalInit { Code = code });
            }
        }
        else
        {
            if (TargetData.Scripts is null)
            {
                result.Errors.Add($"Script \"{entry.TargetName}\": the target game data has no scripts list.");
                return;
            }

            if (script is null)
            {
                script = new UndertaleScript
                {
                    Name = TargetData.Strings.MakeString(entry.TargetName)
                };
                TargetData.Scripts.Add(script);
            }
            script.Code = code;
            script.IsConstructor = false;
        }

        codeQueue.Add((code, source));
        result.ImportedCount++;
    }

    private void ImportObjectShell(YyImportEntry entry)
    {
        UndertaleGameObject obj = entry.Overwrite ? TargetData.GameObjects.ByName(entry.TargetName) : null;
        if (obj is null)
        {
            obj = new UndertaleGameObject
            {
                Name = TargetData.Strings.MakeString(entry.TargetName)
            };
            TargetData.GameObjects.Add(obj);
        }
        entry.Object = obj;
    }

    private void ImportObject(YyImportEntry entry, List<(UndertaleCode Code, string Source)> codeQueue,
                              CrossFileImportResult result)
    {
        YyResource resource = entry.Resource;
        JsonElement root = resource.Root;
        UndertaleGameObject obj = entry.Object;

        obj.Sprite = ResolveSprite(GetReferenceName(root, "spriteId"), entry, result);
        obj.TextureMaskId = ResolveSprite(GetReferenceName(root, "spriteMaskId"), entry, result);
        obj.ParentId = ResolveObject(GetReferenceName(root, "parentObjectId"), entry, result);
        obj.Visible = GetBool(root, "visible", true);
        obj.Solid = GetBool(root, "solid");
        obj.Managed = GetBool(root, "managed");
        obj.Persistent = GetBool(root, "persistent");
        obj.Depth = GetInt32(root, "depth", 0);

        obj.UsesPhysics = GetBool(root, "physicsObject");
        obj.IsSensor = GetBool(root, "physicsSensor");
        obj.CollisionShape = (CollisionShapeFlags)GetUInt32(root, "physicsShape", 0);
        obj.Group = GetUInt32(root, "physicsGroup", 0);
        obj.Density = GetSingle(root, "physicsDensity", 0.5f);
        obj.Restitution = GetSingle(root, "physicsRestitution", 0.1f);
        obj.LinearDamping = GetSingle(root, "physicsLinearDamping", 0.1f);
        obj.AngularDamping = GetSingle(root, "physicsAngularDamping", 0.1f);
        obj.Friction = GetSingle(root, "physicsFriction", 0.2f);
        obj.Awake = GetBool(root, "physicsStartAwake");
        obj.Kinematic = GetBool(root, "physicsKinematic");

        obj.PhysicsVertices.Clear();
        if (root.TryGetProperty("physicsShapePoints", out JsonElement shapePoints) &&
            shapePoints.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement point in shapePoints.EnumerateArray())
            {
                obj.PhysicsVertices.Add(new UndertaleGameObject.UndertalePhysicsVertex
                {
                    X = GetSingle(point, "x", 0f),
                    Y = GetSingle(point, "y", 0f)
                });
            }
        }

        // Rebuild the event list from scratch, as package events replace the object's events
        obj.Events.Clear();
        for (int i = 0; i < UndertaleGameObject.EventTypeCount; i++)
            obj.Events.InternalAdd(new UndertalePointerList<UndertaleGameObject.Event>());

        int skippedDnDEvents = 0;
        if (root.TryGetProperty("eventList", out JsonElement eventList) && eventList.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement eventElement in eventList.EnumerateArray())
            {
                int typeValue = GetInt32(eventElement, "eventType", -1);
                if (typeValue < 0 || typeValue >= UndertaleGameObject.EventTypeCount)
                {
                    result.Warnings.Add($"Object \"{entry.TargetName}\": unknown event type {typeValue} was skipped.");
                    continue;
                }

                EventType eventType = (EventType)typeValue;
                int eventNumber = GetInt32(eventElement, "eventNum", 0);
                string collisionObjectName = null;
                if (eventType == EventType.Collision)
                {
                    collisionObjectName = GetReferenceName(eventElement, "collisionObjectId");
                    if (string.IsNullOrEmpty(collisionObjectName))
                    {
                        result.Warnings.Add($"Object \"{entry.TargetName}\": a collision event without a target object was skipped.");
                        continue;
                    }
                }

                if (GetBool(eventElement, "isDnD"))
                {
                    skippedDnDEvents++;
                    continue;
                }

                string eventFileName = eventType == EventType.Collision
                    ? $"Collision_{collisionObjectName}.gml"
                    : $"{eventType}_{eventNumber}.gml";
                if (!resource.Files.TryGetValue(eventFileName, out ZipArchiveEntry gmlEntry))
                    continue;

                string source = ReadText(gmlEntry);
                if (source is null)
                    continue;

                uint subtype = (uint)eventNumber;
                if (eventType == EventType.Collision)
                {
                    UndertaleGameObject collisionObject = ResolveObject(collisionObjectName, entry, result);
                    if (collisionObject is null)
                        continue;

                    subtype = (uint)TargetData.GameObjects.IndexOf(collisionObject);
                }

                string codeName = $"gml_Object_{obj.Name.Content}_{eventType}_{subtype}";
                UndertaleCode code = TargetData.Code.ByName(codeName);
                if (code is null)
                    code = UndertaleCode.CreateEmptyEntry(TargetData, codeName);

                CodeImportGroup.LinkEvent(obj, code, eventType, subtype, _mainThreadAction);
                codeQueue.Add((code, source));
            }
        }

        if (skippedDnDEvents > 0)
            result.Warnings.Add($"Object \"{entry.TargetName}\": {skippedDnDEvents} drag-and-drop event(s) were skipped, as they have no GML source.");

        if (root.TryGetProperty("properties", out JsonElement properties) &&
            properties.ValueKind == JsonValueKind.Array && properties.GetArrayLength() > 0)
        {
            result.Warnings.Add($"Object \"{entry.TargetName}\": object variable definitions are not imported.");
        }

        result.ImportedCount++;
    }

    private UndertaleSprite ResolveSprite(string name, YyImportEntry entry, CrossFileImportResult result)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        UndertaleSprite sprite = TargetData.Sprites?.ByName(name);
        if (sprite is null)
            result.Warnings.Add($"Object \"{entry.TargetName}\": sprite \"{name}\" was not found.");

        return sprite;
    }

    private UndertaleGameObject ResolveObject(string name, YyImportEntry entry, CrossFileImportResult result)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        UndertaleGameObject obj = TargetData.GameObjects?.ByName(name);
        if (obj is null)
            result.Warnings.Add($"Object \"{entry.TargetName}\": object \"{name}\" was not found.");

        return obj;
    }

    private void CollectDependencies(HashSet<YyResource> initialResources, HashSet<YyResource> allResources,
                                     List<string> warnings)
    {
        Queue<YyResource> queue = new(initialResources);

        while (queue.Count > 0)
        {
            YyResource resource = queue.Dequeue();
            if (resource.Kind != YyResourceKind.Object)
                continue;

            List<string> spriteNames =
            [
                GetReferenceName(resource.Root, "spriteId"),
                GetReferenceName(resource.Root, "spriteMaskId")
            ];
            List<string> objectNames = [GetReferenceName(resource.Root, "parentObjectId")];

            if (resource.Root.TryGetProperty("eventList", out JsonElement eventList) &&
                eventList.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement eventElement in eventList.EnumerateArray())
                {
                    if (GetInt32(eventElement, "eventType", -1) != (int)EventType.Collision)
                        continue;

                    objectNames.Add(GetReferenceName(eventElement, "collisionObjectId"));
                }
            }

            foreach (string spriteName in spriteNames)
            {
                YyResource dependency = FindResource(YyResourceKind.Sprite, spriteName);
                if (dependency is null)
                {
                    if (!string.IsNullOrEmpty(spriteName) && TargetData.Sprites?.ByName(spriteName) is null)
                        warnings.Add($"Sprite \"{spriteName}\" referenced by object \"{resource.Name}\" was not found.");
                    continue;
                }

                if (allResources.Add(dependency))
                {
                    warnings.Add($"Auto-included dependency: \"{dependency.Name}\" (Sprite)");
                    queue.Enqueue(dependency);
                }
            }

            foreach (string objectName in objectNames)
            {
                YyResource dependency = FindResource(YyResourceKind.Object, objectName);
                if (dependency is null)
                {
                    if (!string.IsNullOrEmpty(objectName) && TargetData.GameObjects?.ByName(objectName) is null)
                        warnings.Add($"Object \"{objectName}\" referenced by object \"{resource.Name}\" was not found.");
                    continue;
                }

                if (allResources.Add(dependency))
                {
                    warnings.Add($"Auto-included dependency: \"{dependency.Name}\" (Game Object)");
                    queue.Enqueue(dependency);
                }
            }
        }
    }

    private YyResource FindResource(YyResourceKind kind, string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        foreach (YyResource resource in _resources)
        {
            if (resource.Kind == kind && string.Equals(resource.Name, name, StringComparison.Ordinal))
                return resource;
        }
        return null;
    }

    private bool ExistsInTarget(YyResource resource)
    {
        return resource.Kind switch
        {
            YyResourceKind.Sprite => TargetData.Sprites?.ByName(resource.Name) is not null,
            YyResourceKind.Sound => TargetData.Sounds?.ByName(resource.Name) is not null,
            YyResourceKind.Object => TargetData.GameObjects?.ByName(resource.Name) is not null,
            YyResourceKind.Script => TargetData.Scripts?.ByName(resource.Name) is not null
                                     || (TargetData.IsVersionAtLeast(2, 3) &&
                                         TargetData.Code?.ByName($"gml_GlobalScript_{resource.Name}") is not null),
            YyResourceKind.Font => TargetData.Fonts?.ByName(resource.Name) is not null,
            _ => false
        };
    }

    private string GenerateUniqueName(string baseName, YyResourceKind kind)
    {
        string candidate = baseName + "_imported";
        int suffix = 1;

        while (ExistsInTargetByName(kind, candidate))
        {
            candidate = $"{baseName}_imported{suffix}";
            suffix++;
        }

        return candidate;
    }

    private bool ExistsInTargetByName(YyResourceKind kind, string name)
    {
        return kind switch
        {
            YyResourceKind.Sprite => TargetData.Sprites?.ByName(name) is not null,
            YyResourceKind.Sound => TargetData.Sounds?.ByName(name) is not null,
            YyResourceKind.Object => TargetData.GameObjects?.ByName(name) is not null,
            YyResourceKind.Script => TargetData.Scripts?.ByName(name) is not null
                                     || (TargetData.IsVersionAtLeast(2, 3) &&
                                         TargetData.Code?.ByName($"gml_GlobalScript_{name}") is not null),
            YyResourceKind.Font => TargetData.Fonts?.ByName(name) is not null,
            _ => false
        };
    }

    private static UndertaleSprite.SepMaskType GetCollisionMaskType(int collisionKind, string resourceName,
                                                                    CrossFileImportResult result)
    {
        // GameMaker project collision kinds: 0 = precise, 1 = rectangle, 2 = ellipse, 3 = diamond,
        // 4 = precise per frame, 5 = rotated rectangle
        switch (collisionKind)
        {
            case 1:
                return UndertaleSprite.SepMaskType.AxisAlignedRect;
            case 5:
                return UndertaleSprite.SepMaskType.RotatedRect;
            default:
                result.Warnings.Add($"Sprite \"{resourceName}\": collision masks are not generated when importing a package; " +
                                    "a precise collision mask will be created by the game from the sprite bounding box.");
                return UndertaleSprite.SepMaskType.Precise;
        }
    }

    private static SerializableAssetType KindToAssetType(YyResourceKind kind)
    {
        return kind switch
        {
            YyResourceKind.Sprite => SerializableAssetType.Sprite,
            YyResourceKind.Sound => SerializableAssetType.Sound,
            YyResourceKind.Object => SerializableAssetType.GameObject,
            YyResourceKind.Script => SerializableAssetType.Script,
            YyResourceKind.Font => SerializableAssetType.Font,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static ZipArchiveEntry FindFile(YyResource resource, string fileName)
    {
        return resource.Files.TryGetValue(fileName, out ZipArchiveEntry entry) ? entry : null;
    }

    private static bool IsAudioFile(string fileName)
    {
        return fileName.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
    }

    private static MagickImage LoadImage(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        return new MagickImage(stream);
    }

    private static string ReadTextFile(YyResource resource, string fileName)
    {
        ZipArchiveEntry entry = FindFile(resource, fileName);
        return entry is null ? null : ReadText(entry);
    }

    private static string ReadText(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static JsonDocument ReadJson(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string text = reader.ReadToEnd();

        return JsonDocument.Parse(text, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
    }

    private static JsonElement GetProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return default;

        return element.TryGetProperty(propertyName, out JsonElement value) ? value : default;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        JsonElement value = GetProperty(element, propertyName);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool GetBool(JsonElement element, string propertyName, bool defaultValue = false)
    {
        JsonElement value = GetProperty(element, propertyName);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => defaultValue
        };
    }

    private static int GetInt32(JsonElement element, string propertyName, int defaultValue = 0)
    {
        JsonElement value = GetProperty(element, propertyName);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) ? result : defaultValue;
    }

    private static uint GetUInt32(JsonElement element, string propertyName, uint defaultValue = 0)
    {
        JsonElement value = GetProperty(element, propertyName);
        return value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out uint result) ? result : defaultValue;
    }

    private static float GetSingle(JsonElement element, string propertyName, float defaultValue = 0f)
    {
        JsonElement value = GetProperty(element, propertyName);
        return value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out float result) ? result : defaultValue;
    }

    private static string GetReferenceName(JsonElement element, string propertyName)
    {
        JsonElement value = GetProperty(element, propertyName);
        return value.ValueKind == JsonValueKind.Object ? GetString(value, "name") : null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            UnloadPackage();
            _disposed = true;
        }
    }

    private sealed class YyImportEntry
    {
        public YyResource Resource { get; }
        public string TargetName { get; }
        public bool Overwrite { get; }
        public UndertaleGameObject Object { get; set; }

        public YyImportEntry(YyResource resource, string targetName, bool overwrite)
        {
            Resource = resource;
            TargetName = targetName;
            Overwrite = overwrite;
        }
    }
}
