using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using UndertaleModLib.Models;
using UndertaleModLib.Project;
using UndertaleModLib.Project.SerializableAssets;

namespace UndertaleModLib.Diff;

/// <summary>
/// Builds a data file patch archive from the result of comparing two loaded data files.
/// The patch contains, per difference, the serialized project form of the compared resource
/// plus verbatim payload snapshots (texture container bytes, collision masks) whose fidelity
/// the project serialization alone cannot guarantee.
/// </summary>
public sealed class DataFilePatchBuilder
{
    private static readonly HashSet<string> UnsupportedCategoryIds =
    [
        "AudioGroups",
        "Timelines",
        "Extensions",
        "ParticleSystems",
        "ParticleSystemEmitters",
        "FilterEffects",
        "UINodes",
    ];

    private readonly UndertaleData _baseData;
    private readonly UndertaleData _compareData;
    private readonly string _compareFilePath;
    private readonly Action<Action> _mainThreadAction;

    /// <summary>Fingerprints of the compared data, cached across all entries of one build.</summary>
    private readonly AssetFingerprint _compareFingerprint = new();
    private readonly AssetFingerprint _baseFingerprint = new();

    /// <summary>Verbatim texture snapshots, deduplicated by compared-object identity.</summary>
    private readonly Dictionary<object, int> _textureSnapshotIds = new(ReferenceEqualityComparer.Instance);
    private readonly List<PatchTextureSnapshot> _textureSnapshots = [];

    /// <summary>Verbatim page item snapshots, deduplicated by compared-object identity.</summary>
    private readonly Dictionary<object, int> _pageItemSnapshotIds = new(ReferenceEqualityComparer.Instance);
    private readonly List<PatchPageItemSnapshot> _pageItemSnapshots = [];

    /// <summary>
    /// Creates a patch builder.
    /// </summary>
    /// <param name="baseData">The base (target) data file the diff was run against.</param>
    /// <param name="compareData">The compared (source) data file whose content the patch carries.</param>
    /// <param name="compareFilePath">Path the compared file was loaded from, for streamed sound
    /// export; may be <see langword="null"/> when the compared file has no directory.</param>
    /// <param name="mainThreadAction">Marshals actions onto the data file's main thread; pass
    /// <see langword="null"/> for non-UI usage.</param>
    public DataFilePatchBuilder(UndertaleData baseData, UndertaleData compareData,
                                string compareFilePath = null, Action<Action> mainThreadAction = null)
    {
        _baseData = baseData ?? throw new ArgumentNullException(nameof(baseData));
        _compareData = compareData ?? throw new ArgumentNullException(nameof(compareData));
        _compareFilePath = compareFilePath;
        _mainThreadAction = mainThreadAction ?? new Action<Action>(action => action());
    }

    /// <summary>
    /// Builds a patch covering every non-unchanged difference and writes it as a ZIP archive.
    /// </summary>
    /// <param name="result">The comparison result to capture.</param>
    /// <param name="patchFilePath">Destination path of the patch archive.</param>
    /// <param name="baseLabel">Human-readable base file description.</param>
    /// <param name="compareLabel">Human-readable compared file description.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    public void Build(DataFileDiffResult result, string patchFilePath,
                      string baseLabel, string compareLabel,
                      IProgress<string> progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrEmpty(patchFilePath);

        string tempDirectory = Path.Combine(Path.GetTempPath(), "utmt-patch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<PatchEntry> entries = BuildEntries(result, tempDirectory, progress, cancellationToken);
            List<PatchTextureSnapshot> textures;
            List<PatchPageItemSnapshot> pageItems;
            lock (_textureSnapshots)
            lock (_pageItemSnapshots)
            {
                textures = new List<PatchTextureSnapshot>(_textureSnapshots);
                pageItems = new List<PatchPageItemSnapshot>(_pageItemSnapshots);
            }

            DataFilePatchManifest manifest = new()
            {
                BaseLabel = baseLabel ?? "",
                CompareLabel = compareLabel ?? "",
                GeneratedAt = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Summary = new PatchSummary
                {
                    AddedCount = result.AddedCount,
                    RemovedCount = result.RemovedCount,
                    ChangedCount = result.ChangedCount,
                },
                BaseLastObj = _compareData.GeneralInfo?.LastObj ?? 0,
                BaseLastTile = _compareData.GeneralInfo?.LastTile ?? 0,
                Entries = entries,
            };

            JsonSerializerOptions options = CreateJsonOptions();
            JsonSerializer.Serialize(textures, options);
            WriteJson(Path.Join(tempDirectory, "textures.json"), textures, options);
            WriteJson(Path.Join(tempDirectory, "pageitems.json"), pageItems, options);
            WriteJson(Path.Join(tempDirectory, "patch.json"), manifest, options);

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(patchFilePath))
            {
                File.Delete(patchFilePath);
            }
            ZipFile.CreateFromDirectory(tempDirectory, patchFilePath, CompressionLevel.Optimal, false);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // Best effort cleanup; the patch itself is already complete.
            }
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
    }

    private static void WriteJson<T>(string path, T value, JsonSerializerOptions options)
    {
        using FileStream fs = new(path, FileMode.Create, FileAccess.Write);
        JsonSerializer.Serialize(fs, value, options);
    }

    private List<PatchEntry> BuildEntries(DataFileDiffResult result, string tempDirectory,
                                          IProgress<string> progress, CancellationToken cancellationToken)
    {
        CrossFileImportContext sourceContext = new(_compareData, _mainThreadAction);
        if (_compareFilePath is not null)
        {
            sourceContext.SetDataFilePaths(_compareFilePath, tempDirectory);
        }

        List<PatchEntry> entries = [];
        int processed = 0;
        int total = result.Categories.Sum(c => c.Entries.Count);

        foreach (AssetCategoryDiff category in result.Categories)
        {
            foreach (AssetDiffEntry diffEntry in category.Entries)
            {
                if (diffEntry.Change == AssetChangeKind.Unchanged)
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"{diffEntry.TypeId}/{diffEntry.Name}");
                entries.Add(CaptureEntry(sourceContext, tempDirectory, diffEntry));
                processed++;
                if (processed % 32 == 0)
                {
                    progress?.Report($"{processed}/{total}");
                }
            }
        }

        return entries;
    }

    private PatchEntry CaptureEntry(CrossFileImportContext sourceContext, string tempDirectory,
                                    AssetDiffEntry diffEntry)
    {
        PatchEntry entry = new()
        {
            TypeId = diffEntry.TypeId,
            Name = diffEntry.Name,
            Change = diffEntry.Change.ToString(),
        };

        // Fingerprints for verification and base-drift detection.
        if (diffEntry.Change == AssetChangeKind.Removed)
        {
            entry.Kind = PatchEntryKind.Removed;
            entry.CompareFingerprint = null;
        }
        else
        {
            entry.CompareFingerprint = FingerprintToBase64(FingerprintObject(_compareFingerprint, diffEntry.CompareResource, diffEntry));
        }
        if (diffEntry.BaseResource is not null)
        {
            entry.BaseFingerprint = FingerprintToBase64(FingerprintObject(_baseFingerprint, diffEntry.BaseResource, diffEntry));
        }

        if (diffEntry.Change == AssetChangeKind.Removed)
        {
            entry.AssetType = GetAssetTypeName(diffEntry);
            return entry;
        }

        // General info is captured best-effort through the project's options snapshot.
        if (diffEntry.TypeId == "GeneralInfo")
        {
            entry.Kind = PatchEntryKind.GeneralInfo;
            entry.AssetPath = "general-info.json";
            SerializableGeneralInfoOptions options = new()
            {
                FileName = _compareData.GeneralInfo?.FileName?.Content,
                Config = _compareData.GeneralInfo?.Config?.Content,
                GameID = _compareData.GeneralInfo?.GameID,
                Name = _compareData.GeneralInfo?.Name?.Content,
                DefaultWindowWidth = _compareData.GeneralInfo?.DefaultWindowWidth,
                DefaultWindowHeight = _compareData.GeneralInfo?.DefaultWindowHeight,
                GeneralInfo = _compareData.GeneralInfo?.Info,
                DisplayName = _compareData.GeneralInfo?.DisplayName?.Content,
                SteamAppID = _compareData.GeneralInfo?.SteamAppID,
                GMS2FPS = _compareData.GeneralInfo?.GMS2FPS,
                OptionsInfo = _compareData.Options?.Info,
                VertexSync = _compareData.Options?.VertexSync,
            };
            PatchGeneralInfoSnapshot snapshot = new()
            {
                Options = JsonSerializer.Serialize(options, CreateJsonOptions()),
                GMS2RandomUID = _compareData.GeneralInfo?.GMS2RandomUID is { } uids ? [.. uids] : null,
            };
            WriteJson(Path.Join(tempDirectory, entry.AssetPath), snapshot, CreateJsonOptions());
            return entry;
        }

        // Categories the project serialization cannot capture are reported as unsupported.
        if (UnsupportedCategoryIds.Contains(diffEntry.TypeId))
        {
            entry.Supported = false;
            entry.Kind = PatchEntryKind.Unsupported;
            entry.UnsupportedReason = "The resource category cannot be serialized";
            return entry;
        }

        object resource = diffEntry.CompareResource;
        if (resource is not IProjectAsset { ProjectExportable: true } projectAsset)
        {
            entry.Supported = false;
            entry.Kind = PatchEntryKind.Unsupported;
            entry.UnsupportedReason = "The resource does not support project serialization";
            return entry;
        }

        // Code entries are carried as decompiled GML (disassembly fallback would not compile),
        // except anonymous sub-entries which have no source of their own.
        if (resource is UndertaleCode code)
        {
            if (code.ParentEntry is not null)
            {
                entry.Supported = false;
                entry.Kind = PatchEntryKind.Unsupported;
                entry.UnsupportedReason = "The code entry is an anonymous sub-entry";
                return entry;
            }

            entry.Kind = PatchEntryKind.Asset;
            entry.AssetType = nameof(SerializableAssetType.Code);
            entry.AssetPath = CaptureCode(sourceContext, tempDirectory, code, entry);
            return entry;
        }

        // Sound entries: only builtin-group embedded sounds and streamed sounds are carried;
        // audio-group sounds reference external audio group data files.
        if (resource is UndertaleSound sound)
        {
            bool builtinGroup = sound.AudioGroup is null || sound.GroupID == _compareData.GetBuiltinSoundGroupID();
            if (!IsStreamed(sound) && sound.AudioFile is null)
            {
                entry.Supported = false;
                entry.Kind = PatchEntryKind.Unsupported;
                entry.UnsupportedReason = "The sound is embedded in an external audio group";
                return entry;
            }
            if (!builtinGroup && sound.AudioFile is not null && !IsStreamed(sound))
            {
                entry.Supported = false;
                entry.Kind = PatchEntryKind.Unsupported;
                entry.UnsupportedReason = "The sound is embedded in an external audio group";
                return entry;
            }
        }

        ISerializableProjectAsset serializable = projectAsset.GenerateSerializableProjectAsset(sourceContext);
        entry.Kind = PatchEntryKind.Asset;
        entry.AssetType = serializable.AssetType.ToString();
        (string assetPath, List<PatchTextureRef> textures, List<string> masks) =
            SerializeAsset(sourceContext, tempDirectory, diffEntry, serializable);
        entry.AssetPath = assetPath;
        entry.Textures = textures;
        entry.Masks = masks;
        return entry;
    }

    private static bool IsStreamed(UndertaleSound sound)
    {
        return sound.Flags == UndertaleSound.AudioEntryFlags.Regular;
    }

    private string CaptureCode(CrossFileImportContext sourceContext, string tempDirectory,
                               UndertaleCode code, PatchEntry entry)
    {
        string friendlyName = MakeFilename(code.Name?.Content ?? entry.Name);
        string assetPath = Path.Join("assets", "code", $"{friendlyName}.json");
        string destinationFile = Path.Join(tempDirectory, assetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFile));

        SerializableCode serializable = new();
        serializable.PopulateFromData(sourceContext, code);

        // SerializableCode.Serialize decompiles through the context's source cache; populate it
        // with the compared file's decompiled source so the patch carries the exact GML.
        string source;
        try
        {
            source = new Underanalyzer.Decompiler.DecompileContext(
                new UndertaleModLib.Decompiler.GlobalDecompileContext(_compareData),
                code, _compareData.ToolInfo.DecompilerSettings).DecompileToString();
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"Failed to decompile code \"{code.Name?.Content}\": {e.Message}", e);
        }
        sourceContext.UpdateCodeSource(code, source);

        serializable.Serialize(sourceContext, destinationFile);

        entry.Code = new PatchCodeMeta
        {
            LocalsCount = (int)code.LocalsCount,
            ArgumentsCount = code.ArgumentsCount,
            WeirdLocalFlag = code.WeirdLocalFlag,
        };
        return assetPath.Replace('\\', '/');
    }

    private (string AssetPath, List<PatchTextureRef> Textures, List<string> Masks) SerializeAsset(
        CrossFileImportContext sourceContext, string tempDirectory,
        AssetDiffEntry diffEntry, ISerializableProjectAsset serializable)
    {
        string folder = serializable.AssetType.ToFilesystemNamePlural();
        string friendlyName = MakeFilename(serializable.DataName ?? diffEntry.Name);

        // The patch stores exact texture/mask payloads itself, so suppress the lossy PNG exports
        // of the project serialization and reference the snapshots from the manifest instead.
        List<PatchTextureRef> textureRefs = [];
        List<string> maskPaths = [];
        if (serializable is SerializableSprite sprite)
        {
            textureRefs = CaptureSpriteTextures((UndertaleSprite)diffEntry.CompareResource);
            maskPaths = CaptureMasks(tempDirectory, (UndertaleSprite)diffEntry.CompareResource, folder, friendlyName);
            sprite.TextureCount = 0;
            sprite.MaskCount = 0;
        }
        else if (serializable is SerializableBackground background)
        {
            textureRefs = CaptureSingleTexture(((UndertaleBackground)diffEntry.CompareResource).Texture);
            background.SkipTextureExport = true;
        }
        else if (serializable is SerializableFont font)
        {
            textureRefs = CaptureSingleTexture(((UndertaleFont)diffEntry.CompareResource).Texture);
            font.SkipTextureExport = true;
        }

        string relativePath = serializable.AssetType == SerializableAssetType.Sound
            ? Path.Join("assets", folder, friendlyName, $"{friendlyName}.json")
            : Path.Join("assets", folder, $"{friendlyName}.json");
        string destinationFile = Path.Join(tempDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFile));

        serializable.Serialize(sourceContext, destinationFile);
        return (relativePath.Replace('\\', '/'), textureRefs, maskPaths);
    }

    private List<PatchTextureRef> CaptureSingleTexture(UndertaleTexturePageItem pageItem)
    {
        return pageItem is null ? [] : [CaptureTextureRef(pageItem, 0)];
    }

    /// <summary>Captures texture references for every frame of a sprite.</summary>
    internal List<PatchTextureRef> CaptureSpriteTextures(UndertaleSprite sprite)
    {
        List<PatchTextureRef> refs = [];
        for (int i = 0; i < sprite.Textures.Count; i++)
        {
            UndertaleTexturePageItem pageItem = sprite.Textures[i]?.Texture;
            if (pageItem is null)
            {
                continue;
            }
            refs.Add(CaptureTextureRef(pageItem, i));
        }
        return refs;
    }

    private PatchTextureRef CaptureTextureRef(UndertaleTexturePageItem pageItem, int frameIndex)
    {
        UndertaleEmbeddedTexture texture = pageItem.TexturePage;
        if (texture?.TextureData?.Image is null)
        {
            throw new InvalidOperationException($"Texture page item \"{pageItem.Name?.Content}\" has no image data");
        }
        if (texture.TextureExternal)
        {
            throw new InvalidOperationException("External textures are not supported");
        }

        int textureId = GetTextureSnapshotId(texture);
        int pageItemId = GetPageItemSnapshotId(pageItem, textureId);
        string texturePath = $"textures/{textureId}.json";
        string pageItemPath = $"pageitems/{pageItemId}.json";

        return new PatchTextureRef
        {
            FrameIndex = frameIndex,
            PageItemPath = pageItemPath,
            TexturePath = texturePath,
        };
    }

    private int GetTextureSnapshotId(UndertaleEmbeddedTexture texture)
    {
        if (_textureSnapshotIds.TryGetValue(texture, out int id))
        {
            return id;
        }

        Util.GMImage image = texture.TextureData.Image;
        PatchTextureSnapshot snapshot = new()
        {
            Format = image.Format.ToString(),
            Width = image.Width,
            Height = image.Height,
            Data = Convert.ToBase64String(image.GetData()),
            Bz2UncompressedLength = image.Bz2UncompressedLength,
            Scaled = texture.Scaled,
            GeneratedMips = texture.GeneratedMips,
            TextureWidth = texture.TextureWidth,
            TextureHeight = texture.TextureHeight,
            IndexInGroup = texture.IndexInGroup,
            TextureGroupName = texture.TextureInfo?.Name?.Content,
        };

        lock (_textureSnapshots)
        {
            id = _textureSnapshots.Count;
            _textureSnapshots.Add(snapshot);
        }
        _textureSnapshotIds[texture] = id;
        return id;
    }

    private int GetPageItemSnapshotId(UndertaleTexturePageItem pageItem, int textureId)
    {
        if (_pageItemSnapshotIds.TryGetValue(pageItem, out int id))
        {
            return id;
        }

        PatchPageItemSnapshot snapshot = new()
        {
            TextureIndex = textureId,
            SourceX = pageItem.SourceX,
            SourceY = pageItem.SourceY,
            SourceWidth = pageItem.SourceWidth,
            SourceHeight = pageItem.SourceHeight,
            TargetX = pageItem.TargetX,
            TargetY = pageItem.TargetY,
            TargetWidth = pageItem.TargetWidth,
            TargetHeight = pageItem.TargetHeight,
            BoundingWidth = pageItem.BoundingWidth,
            BoundingHeight = pageItem.BoundingHeight,
        };

        lock (_pageItemSnapshots)
        {
            id = _pageItemSnapshots.Count;
            _pageItemSnapshots.Add(snapshot);
        }
        _pageItemSnapshotIds[pageItem] = id;
        return id;
    }

    private List<string> CaptureMasks(string tempDirectory, UndertaleSprite sprite, string folder, string friendlyName)
    {
        List<string> paths = [];
        if (sprite.CollisionMasks.Count == 0)
        {
            return paths;
        }

        string dir = Path.Join(tempDirectory, "masks", folder, friendlyName);
        Directory.CreateDirectory(dir);
        for (int i = 0; i < sprite.CollisionMasks.Count; i++)
        {
            byte[] data = sprite.CollisionMasks[i]?.Data;
            if (data is null)
            {
                continue;
            }
            string relative = $"masks/{folder}/{friendlyName}/{i}.bin".Replace('\\', '/');
            File.WriteAllBytes(Path.Join(tempDirectory, relative), data);
            paths.Add(relative);
        }
        return paths;
    }

    private byte[] FingerprintObject(AssetFingerprint fingerprint, object resource, AssetDiffEntry entry)
    {
        if (resource is UndertaleCode code && entry.TypeId == "Code")
        {
            // Code entries are verified through the normalized instruction fingerprint; the
            // applier computes it against the patched data, the builder against the compared data.
            return CodeFingerprint.Compute(code, _compareData);
        }
        return resource switch
        {
            UndertaleResource typed => fingerprint.Compute(typed),
            _ => fingerprint.ComputeObject(resource),
        };
    }

    private static string FingerprintToBase64(byte[] fingerprint)
    {
        return Convert.ToBase64String(fingerprint);
    }

    private static string GetAssetTypeName(AssetDiffEntry entry)
    {
        return entry.BaseResource switch
        {
            UndertaleSprite => nameof(SerializableAssetType.Sprite),
            UndertaleBackground => nameof(SerializableAssetType.Background),
            UndertaleSound => nameof(SerializableAssetType.Sound),
            UndertalePath => nameof(SerializableAssetType.Path),
            UndertaleScript => nameof(SerializableAssetType.Script),
            UndertaleShader => nameof(SerializableAssetType.Shader),
            UndertaleFont => nameof(SerializableAssetType.Font),
            UndertaleGameObject => nameof(SerializableAssetType.GameObject),
            UndertaleRoom => nameof(SerializableAssetType.Room),
            UndertaleAnimationCurve => nameof(SerializableAssetType.AnimationCurve),
            UndertaleSequence => nameof(SerializableAssetType.Sequence),
            UndertaleCode => nameof(SerializableAssetType.Code),
            _ => null,
        };
    }

    /// <summary>Converts a data name into a filesystem-friendly identifier.</summary>
    internal static string MakeFilename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "unknown";
        }

        // Keep it conservative: reserve characters that are invalid in paths.
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name.Length > 100 ? name[..100] : name;
    }
}
