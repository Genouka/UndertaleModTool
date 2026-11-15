#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using UndertaleModLib.Compiler;
using UndertaleModLib.Models;
using UndertaleModLib.Project;
using UndertaleModLib.Project.SerializableAssets;

namespace UndertaleModLib.Diff;

/// <summary>Options for applying a data file patch.</summary>
public sealed class DataFilePatchApplyOptions
{
    /// <summary>Whether resources that the patch reports as removed are deleted from the base data.</summary>
    public bool DeleteRemoved { get; set; }

    /// <summary>Marshals actions onto the data file's main thread; pass <see langword="null"/> for non-UI usage.</summary>
    public Action<Action>? MainThreadAction { get; set; }

    /// <summary>Directory of the base data file, used to load auxiliary files (audio groups).</summary>
    public string? BaseDataDirectory { get; set; }

    /// <summary>Path of the base data file, used to verify the patch's recorded MD5. Optional.</summary>
    public string? BaseFilePath { get; set; }

    /// <summary>Directory of the output data file, where auxiliary files (audio groups, external
    /// textures) are written. When <see langword="null"/>, auxiliary files are not written.</summary>
    public string? OutputDirectory { get; set; }
}

/// <summary>The outcome of applying a data file patch.</summary>
public sealed class DataFilePatchApplyResult
{
    public int ImportedCount { get; set; }
    public int OverwrittenCount { get; set; }
    public int DeletedCount { get; set; }

    /// <summary>Entries whose post-apply fingerprint matches the patch expectation.</summary>
    public int VerifiedCount { get; set; }

    /// <summary>Entries that were imported but whose content does not match the patch expectation.</summary>
    public int MismatchedCount { get; set; }

    /// <summary>Differences the patch did not capture.</summary>
    public int UnsupportedCount { get; set; }

    /// <summary>Entries whose base file content drifted from the file the patch was created against.</summary>
    public List<string> BaseDriftWarnings { get; } = [];

    public List<string> Warnings { get; } = [];
    public List<string> Errors { get; } = [];

    /// <summary>Unsupported differences, as "TypeId/Name: reason".</summary>
    public List<string> Unsupported { get; } = [];

    /// <summary>Auxiliary files written next to the output data file (audio groups, external textures).</summary>
    public List<string> ExtraOutputFiles { get; } = [];

    /// <summary>MD5 of the compared file recorded by the patch (the intended result), if known.</summary>
    public string? ExpectedResultMd5 { get; set; }

    /// <summary>Per-diff summaries when several diffs are applied in order.</summary>
    public List<string> StepSummaries { get; } = [];

    /// <summary>Short single-line summary, suitable for a status bar.</summary>
    public string GetSummary()
    {
        string line = $"+{ImportedCount} ~{OverwrittenCount} -{DeletedCount}, {VerifiedCount} verified";
        if (MismatchedCount > 0)
        {
            line += $", {MismatchedCount} mismatched";
        }
        if (UnsupportedCount > 0)
        {
            line += $", {UnsupportedCount} unsupported";
        }
        return line;
    }
}

/// <summary>
/// Applies a data file patch archive to a loaded base data file, reconstructing the compared
/// file's content. After applying, every supported entry is re-fingerprinted and compared
/// against the fingerprint stored in the patch, so the caller learns exactly how much of the
/// compared file was reproduced.
/// </summary>
public sealed class DataFilePatchApplier
{
    private readonly UndertaleData _baseData;
    private readonly Action<Action> _mainThreadAction;

    public DataFilePatchApplier(UndertaleData baseData, Action<Action>? mainThreadAction = null)
    {
        _baseData = baseData ?? throw new ArgumentNullException(nameof(baseData));
        _mainThreadAction = mainThreadAction ?? new Action<Action>(action => action());
    }

    /// <summary>
    /// Applies the patch at <paramref name="patchFilePath"/> to the base data file held in memory.
    /// </summary>
    /// <param name="patchFilePath">Path of the patch archive.</param>
    /// <param name="options">Apply options; may be <see langword="null"/>.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    public DataFilePatchApplyResult Apply(string patchFilePath, DataFilePatchApplyOptions? options = null,
                                          IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(patchFilePath);
        if (!File.Exists(patchFilePath))
        {
            throw new FileNotFoundException("Patch file not found", patchFilePath);
        }
        options ??= new DataFilePatchApplyOptions();

        DataFilePatchApplyResult result = new();

        string workDirectory = Path.Combine(Path.GetTempPath(), "utmt-apply-" + Guid.NewGuid().ToString("N"));
        string streamedDirectory = Path.Combine(workDirectory, "streamed");
        Directory.CreateDirectory(workDirectory);
        Directory.CreateDirectory(streamedDirectory);

        try
        {
            ZipFile.ExtractToDirectory(patchFilePath, workDirectory, overwriteFiles: true);

            DataFilePatchManifest manifest;
            JsonSerializerOptions jsonOptions = new()
            {
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
            };
            using (FileStream fs = new(Path.Join(workDirectory, "patch.json"), FileMode.Open, FileAccess.Read))
            {
                manifest = JsonSerializer.Deserialize<DataFilePatchManifest>(fs, jsonOptions)
                    ?? throw new InvalidDataException("Patch manifest is empty");
            }

            if (!string.Equals(manifest.FormatName, DataFilePatch.FormatName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Unsupported diff format \"{manifest.FormatName}\" (expected \"{DataFilePatch.FormatName}\")");
            }
            if (manifest.FormatVersion > DataFilePatch.FormatVersion)
            {
                throw new InvalidDataException(
                    $"Diff format version {manifest.FormatVersion} is newer than the supported version {DataFilePatch.FormatVersion}");
            }

            result.ExpectedResultMd5 = manifest.ResultFileMd5;
            if (manifest.BaseFileMd5 is not null && options.BaseFilePath is not null && File.Exists(options.BaseFilePath))
            {
                using FileStream baseStream = new(options.BaseFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                string actual = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(baseStream)).ToLowerInvariant();
                if (!string.Equals(actual, manifest.BaseFileMd5, StringComparison.OrdinalIgnoreCase))
                {
                    result.BaseDriftWarnings.Add(
                        $"base file MD5 mismatch: the patch was created for {manifest.BaseFileMd5}, the selected file is {actual}");
                }
            }

            Apply(workDirectory, streamedDirectory, manifest, options, result, progress, cancellationToken, jsonOptions);
        }
        finally
        {
            try
            {
                Directory.Delete(workDirectory, recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }

        return result;
    }

    private void Apply(string workDirectory, string streamedDirectory, DataFilePatchManifest manifest,
                       DataFilePatchApplyOptions options, DataFilePatchApplyResult result,
                       IProgress<string>? progress, CancellationToken cancellationToken, JsonSerializerOptions jsonOptions)
    {
        // The context reads auxiliary files (GML, audio) from the extraction directory and copies
        // streamed sounds into a scratch directory, which is moved next to the output file later.
        CrossFileImportContext context = new(_baseData, _mainThreadAction);
        context.SetDataFilePaths(Path.Join(workDirectory, "patch.json"), Path.Join(streamedDirectory, "data.win"));
        context.FileBackup = new GameFileBackup(streamedDirectory);

        // Base drift detection: compare the base file's current content against the fingerprints
        // captured at patch time. Drift does not block the apply, but every drifted entry is
        // reported, because the result may then not fully match the compared file.
        AssetFingerprint baseFingerprint = new();
        foreach (PatchEntry entry in manifest.Entries)
        {
            if (entry.BaseFingerprint is null || entry.Change == nameof(AssetChangeKind.Removed))
            {
                continue;
            }
            try
            {
                UndertaleNamedResource? existing = FindByName(entry);
                if (existing is null)
                {
                    if (entry.Kind != PatchEntryKind.GeneralInfo)
                    {
                        result.BaseDriftWarnings.Add($"{entry.TypeId}/{entry.Name}: missing in base file");
                    }
                    continue;
                }
                byte[] current = ComputeContentFingerprint(baseFingerprint, existing, entry.TypeId);
                if (!Convert.FromBase64String(entry.BaseFingerprint).AsSpan().SequenceEqual(current))
                {
                    result.BaseDriftWarnings.Add($"{entry.TypeId}/{entry.Name}: content differs from the base the patch was created against");
                }
            }
            catch (Exception e)
            {
                result.BaseDriftWarnings.Add($"{entry.TypeId}/{entry.Name}: drift check failed: {e.Message}");
            }
        }

        // 1. General info: every fingerprinted field, so the entry can be verified.
        PatchEntry? generalInfo = manifest.Entries.FirstOrDefault(e => e.Kind == PatchEntryKind.GeneralInfo && e.Supported);
        PatchGeneralInfoSnapshot? generalInfoSnapshot = null;
        if (generalInfo is not null)
        {
            try
            {
                using FileStream fs = new(Path.Join(workDirectory, generalInfo.AssetPath), FileMode.Open, FileAccess.Read);
                generalInfoSnapshot = JsonSerializer.Deserialize<PatchGeneralInfoSnapshot>(fs, jsonOptions);
                ApplyGeneralInfo(generalInfoSnapshot!);
            }
            catch (Exception e)
            {
                result.Errors.Add($"Failed to apply general info: {e.Message}");
            }
        }

        // 2. Code entries: find-or-create, queue GML, compile once, restore metadata.
        List<PatchEntry> codeEntries = [.. manifest.Entries.Where(e =>
            e.Supported && e.Kind == PatchEntryKind.Asset && e.AssetType == nameof(SerializableAssetType.Code))];
        if (codeEntries.Count > 0)
        {
            ApplyCodeEntries(context, workDirectory, codeEntries, result, cancellationToken);
        }

        // 2b. Anonymous sub-code entries: splice their instruction segments back into the parent blob.
        ApplySubCodeEntries(workDirectory, manifest, options, result, cancellationToken, jsonOptions);

        // 3. Restore exact textures and page items referenced by texture-backed assets.
        Dictionary<string, UndertaleEmbeddedTexture> restoredTextures = [];
        Dictionary<string, UndertaleTexturePageItem> restoredPageItems = [];
        RestoreTextures(context, workDirectory, manifest, restoredTextures, restoredPageItems, options, result, jsonOptions);
        if (generalInfoSnapshot is not null && generalInfo is not null)
        {
            ApplyOptionImages(generalInfo, generalInfoSnapshot, restoredPageItems, result);
        }

        // 4. Non-code assets: deserialize, PreImport all, assign exact textures/masks, Import all.
        List<(PatchEntry Entry, ISerializableProjectAsset Asset)> assets = [];
        foreach (PatchEntry entry in manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"{entry.TypeId}/{entry.Name}");
            if (!entry.Supported || entry.Kind != PatchEntryKind.Asset || entry.AssetType == nameof(SerializableAssetType.Code))
            {
                continue;
            }

            string assetPath = Path.Join(workDirectory, entry.AssetPath);
            ISerializableProjectAsset asset;
            try
            {
                using FileStream fs = new(assetPath, FileMode.Open, FileAccess.Read);
                asset = JsonSerializer.Deserialize<ISerializableProjectAsset>(fs, jsonOptions)!;
            }
            catch (Exception e)
            {
                result.Errors.Add($"Failed to read patch asset \"{entry.TypeId}/{entry.Name}\": {e.Message}");
                continue;
            }
            if (asset is null)
            {
                result.Errors.Add($"Patch asset \"{entry.TypeId}/{entry.Name}\" is empty");
                continue;
            }

            context.RegisterAssetDataPath(asset.DataName ?? entry.Name, asset.AssetType, assetPath);

            try
            {
                asset.PreImport(context);
            }
            catch (Exception e)
            {
                result.Errors.Add($"PreImport failed for \"{entry.TypeId}/{entry.Name}\": {e.Message}");
                continue;
            }
            assets.Add((entry, asset));
        }

        foreach ((PatchEntry entry, ISerializableProjectAsset asset) in assets)
        {
            try
            {
                AssignExactTextures(entry, asset, restoredPageItems, workDirectory);
            }
            catch (Exception e)
            {
                result.Errors.Add($"Texture restore failed for \"{entry.TypeId}/{entry.Name}\": {e.Message}");
            }
        }

        _mainThreadAction(() =>
        {
            foreach ((_, ISerializableProjectAsset asset) in assets)
            {
                try
                {
                    asset.Import(context);
                }
                catch (Exception e)
                {
                    result.Errors.Add($"Import failed for \"{asset.DataName}\": {e.Message}");
                }
            }
        });

        ApplySnapshots(workDirectory, manifest, result, progress, cancellationToken, jsonOptions);

        // 2c. Sounds embedded in external audio group files: patch the group file and link it.
        ApplyGroupSounds(workDirectory, manifest, options, result, cancellationToken, jsonOptions);

        ApplySpriteExtras(workDirectory, manifest, result, jsonOptions);

        CountImportResults(manifest, options, result);

        // 5. Removals (opt-in).
        if (options.DeleteRemoved)
        {
            DeleteRemoved(manifest, result);
        }

        // 6. High-water marks, so future insertions cannot collide with restored IDs. When the
        // general info was patched, the exact compared values were already restored above.
        if (generalInfoSnapshot is null)
        {
            _baseData.GeneralInfo.LastObj = Math.Max(_baseData.GeneralInfo.LastObj, (uint)manifest.BaseLastObj);
            _baseData.GeneralInfo.LastTile = Math.Max(_baseData.GeneralInfo.LastTile, (uint)manifest.BaseLastTile);
        }

        // 7. Verification: re-fingerprint every imported entry and compare with the patch.
        VerifyEntries(manifest, result, cancellationToken);

        progress?.Report(result.GetSummary());
    }

    private void ApplyCodeEntries(CrossFileImportContext context, string workDirectory,
                                  List<PatchEntry> codeEntries, DataFilePatchApplyResult result,
                                  CancellationToken cancellationToken)
    {
        CodeImportGroup importGroup = new(_baseData)
        {
            AutoCreateAssets = false,
            MainThreadAction = _mainThreadAction,
        };

        foreach (PatchEntry entry in codeEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                SerializableCode serializable = new();
                using (FileStream fs = new(Path.Join(workDirectory, entry.AssetPath), FileMode.Open, FileAccess.Read))
                {
                    // The GML file sits next to the JSON; Deserialize needs the JSON only.
                    JsonSerializer.Deserialize<SerializableCode>(fs);
                }

                // SerializableCode cannot be deserialized polymorphically through the public API
                // without the $type discriminator; deserialize through the interface instead.
                SerializableCode codeAsset;
                using (FileStream fs = new(Path.Join(workDirectory, entry.AssetPath), FileMode.Open, FileAccess.Read))
                {
                    codeAsset = (SerializableCode)JsonSerializer.Deserialize<ISerializableProjectAsset>(fs, ProjectContext.JsonOptions)!;
                }

                string name = codeAsset.DataName ?? entry.Name;
                context.RegisterAssetDataPath(name, SerializableAssetType.Code, Path.Join(workDirectory, entry.AssetPath));
                codeAsset.PreImport(context);
                codeAsset.ImportCode(context, importGroup);
            }
            catch (Exception e)
            {
                result.Errors.Add($"Failed to queue code \"{entry.TypeId}/{entry.Name}\": {e.Message}");
            }
        }

        try
        {
            importGroup.Import(true);
        }
        catch (Exception e)
        {
            result.Errors.Add($"Code compilation failed: {e.Message}");
        }

        // Compilation regenerates locals; restore the compared file's bytecode metadata so a
        // re-comparison resolves the entry (same behavior as DataFileMerger).
        foreach (PatchEntry entry in codeEntries)
        {
            if (entry.Code is null)
            {
                continue;
            }
            UndertaleCode? target = _baseData.Code?.ByName(entry.Name);
            if (target is null)
            {
                continue;
            }
            target.LocalsCount = (uint)entry.Code.LocalsCount;
            target.ArgumentsCount = (ushort)entry.Code.ArgumentsCount;
            target.WeirdLocalFlag = entry.Code.WeirdLocalFlag;
        }
    }

    private void RestoreTextures(CrossFileImportContext context, string workDirectory, DataFilePatchManifest manifest,
                                 Dictionary<string, UndertaleEmbeddedTexture> restoredTextures,
                                 Dictionary<string, UndertaleTexturePageItem> restoredPageItems,
                                 DataFilePatchApplyOptions options,
                                 DataFilePatchApplyResult result, JsonSerializerOptions jsonOptions)
    {
        PatchTextureSnapshot[] textures;
        PatchPageItemSnapshot[] pageItems;
        try
        {
            textures = DeserializeArray<PatchTextureSnapshot>(Path.Join(workDirectory, "textures.json"), jsonOptions) ?? [];
            pageItems = DeserializeArray<PatchPageItemSnapshot>(Path.Join(workDirectory, "pageitems.json"), jsonOptions) ?? [];
        }
        catch (Exception e)
        {
            result.Errors.Add($"Failed to read texture snapshots: {e.Message}");
            return;
        }
        if (textures.Length == 0)
        {
            return;
        }

        _mainThreadAction(() =>
        {
            foreach (PatchTextureSnapshot snapshot in textures)
            {
                try
                {
                    UndertaleEmbeddedTexture texture = new()
                    {
                        Name = _baseData.Strings.MakeString($"Texture {_baseData.EmbeddedTextures.Count}"),
                        Scaled = snapshot.Scaled,
                        GeneratedMips = snapshot.GeneratedMips,
                        TextureWidth = snapshot.TextureWidth,
                        TextureHeight = snapshot.TextureHeight,
                        IndexInGroup = snapshot.IndexInGroup,
                        TextureLoaded = true,
                        TextureData = new UndertaleEmbeddedTexture.TexData
                        {
                            // External textures keep the placeholder image in memory (the actual
                            // content lives in the external file written next to the output).
                            Image = snapshot.Format == "External" ? new Util.GMImage(1, 1) : CreateImage(snapshot),
                        },
                    };
                    if (snapshot.Format == "External")
                    {
                        texture.TextureExternal = true;
                        WriteExternalTexture(snapshot, options, result);
                    }
                    _baseData.EmbeddedTextures.Add(texture);
                    restoredTextures[$"{Array.IndexOf(textures, snapshot)}"] = texture;
                }
                catch (Exception e)
                {
                    result.Errors.Add($"Failed to restore texture snapshot: {e.Message}");
                }
            }

            foreach (PatchPageItemSnapshot snapshot in pageItems)
            {
                try
                {
                    UndertaleEmbeddedTexture? texture = restoredTextures.TryGetValue($"{snapshot.TextureIndex}", out var value)
                        ? value : null;
                    if (texture is null)
                    {
                        throw new InvalidOperationException("Backing texture snapshot missing");
                    }
                    UndertaleTexturePageItem pageItem = new()
                    {
                        Name = _baseData.Strings.MakeString($"PageItem {_baseData.TexturePageItems.Count}"),
                        SourceX = snapshot.SourceX,
                        SourceY = snapshot.SourceY,
                        SourceWidth = snapshot.SourceWidth,
                        SourceHeight = snapshot.SourceHeight,
                        TargetX = snapshot.TargetX,
                        TargetY = snapshot.TargetY,
                        TargetWidth = snapshot.TargetWidth,
                        TargetHeight = snapshot.TargetHeight,
                        BoundingWidth = snapshot.BoundingWidth,
                        BoundingHeight = snapshot.BoundingHeight,
                        TexturePage = texture,
                    };
                    _baseData.TexturePageItems.Add(pageItem);
                    restoredPageItems[$"{Array.IndexOf(pageItems, snapshot)}"] = pageItem;
                }
                catch (Exception e)
                {
                    result.Errors.Add($"Failed to restore page item snapshot: {e.Message}");
                }
            }

            // Texture group info (2022.9+): ensure every restored texture belongs to a group
            // with the same name as in the compared file, then refresh the group links.
            if (textures.Any(t => t.TextureGroupName is not null))
            {
                EnsureChunk(_baseData, "TGIN");
            }
            if (_baseData.TextureGroupInfo is not null)
            {
                foreach ((string index, UndertaleEmbeddedTexture texture) in restoredTextures)
                {
                    string groupName = textures[int.Parse(index)].TextureGroupName;
                    if (groupName is null)
                    {
                        continue;
                    }
                    UndertaleTextureGroupInfo? group = _baseData.TextureGroupInfo.FirstOrDefault(g => g.Name?.Content == groupName);
                    if (group is null)
                    {
                        group = new UndertaleTextureGroupInfo()
                        {
                            Name = _baseData.Strings.MakeString(groupName),
                        };
                        _baseData.TextureGroupInfo.Add(group);
                    }
                    if (!group.TexturePages.Any(p => p.Resource == texture))
                    {
                        group.TexturePages.Add(new UndertaleResourceById<UndertaleEmbeddedTexture, UndertaleChunkTXTR>()
                        {
                            Resource = texture
                        });
                    }
                    PatchTextureSnapshot snapshot = textures[int.Parse(index)];
                    if (snapshot.TextureGroupDirectory is not null)
                    {
                        group.Directory = _baseData.Strings.MakeString(snapshot.TextureGroupDirectory);
                    }
                    if (snapshot.TextureGroupExtension is not null)
                    {
                        group.Extension = _baseData.Strings.MakeString(snapshot.TextureGroupExtension);
                    }
                    texture.TextureInfo = group;
                }
            }
        });
    }

    private static void WriteExternalTexture(PatchTextureSnapshot snapshot, DataFilePatchApplyOptions options,
                                             DataFilePatchApplyResult result)
    {
        if (snapshot.ExternalPath is null)
        {
            return;
        }
        if (options.OutputDirectory is null)
        {
            result.Warnings.Add($"External texture \"{snapshot.ExternalPath}\" was not written (no output directory)");
            return;
        }

        string destination = Path.Join(options.OutputDirectory, snapshot.ExternalPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? options.OutputDirectory);
        File.WriteAllBytes(destination, Convert.FromBase64String(snapshot.Data));
        result.ExtraOutputFiles.Add(destination);
    }

    private static T[]? DeserializeArray<T>(string path, JsonSerializerOptions options)
    {
        if (!File.Exists(path))
        {
            return [];
        }
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read);
        return JsonSerializer.Deserialize<T[]>(fs, options);
    }

    private static Util.GMImage CreateImage(PatchTextureSnapshot snapshot)
    {
        byte[] data = Convert.FromBase64String(snapshot.Data);
        return snapshot.Format switch
        {
            nameof(Util.GMImage.ImageFormat.Png) => Util.GMImage.FromPng(data),
            nameof(Util.GMImage.ImageFormat.Qoi) => Util.GMImage.FromQoi(data),
            nameof(Util.GMImage.ImageFormat.Bz2Qoi) => Util.GMImage.FromBz2Qoi(data, snapshot.Width, snapshot.Height,
                snapshot.Bz2UncompressedLength ?? -1),
            nameof(Util.GMImage.ImageFormat.Dds) => Util.GMImage.FromDds(data),
            nameof(Util.GMImage.ImageFormat.Unknown) => Util.GMImage.FromUnknown(data),
            _ => throw new InvalidOperationException($"Unsupported texture format \"{snapshot.Format}\""),
        };
    }

    private static List<byte[]> LoadMasks(PatchEntry entry, string workDirectory)
    {
        List<byte[]> masks = new(entry.Masks.Count);
        foreach (string maskPath in entry.Masks)
        {
            masks.Add(File.ReadAllBytes(Path.Join(workDirectory, maskPath)));
        }
        return masks;
    }

    private void AssignExactTextures(PatchEntry entry, ISerializableProjectAsset asset,
                                     Dictionary<string, UndertaleTexturePageItem> restoredPageItems,
                                     string workDirectory)
    {
        if (entry.Textures.Count == 0)
        {
            return;
        }

        UndertaleTexturePageItem[] items = new UndertaleTexturePageItem[entry.Textures.Count];
        foreach (PatchTextureRef textureRef in entry.Textures)
        {
            if (!restoredPageItems.TryGetValue(Path.GetFileNameWithoutExtension(textureRef.PageItemPath), out UndertaleTexturePageItem? pageItem))
            {
                throw new InvalidOperationException($"Missing restored page item for \"{textureRef.PageItemPath}\"");
            }
            items[textureRef.FrameIndex] = pageItem;
        }

        switch (asset)
        {
            case SerializableSprite sprite:
                sprite._textureImages = [.. items];
                break;
            case SerializableBackground background:
                background._textureImage = items[0];
                break;
            case SerializableFont font:
                font._textureImage = items[0];
                break;
        }

        if (asset is SerializableSprite maskedSprite)
        {
            maskedSprite._maskData = LoadMasks(entry, workDirectory);
        }
    }

    /// <summary>Snapshot categories and the chunk that owns their resource list.</summary>
    private static readonly Dictionary<string, string> SnapshotCategoryChunks = new()
    {
        ["AudioGroups"] = "AGRP",
        ["Timelines"] = "TMLN",
        ["Extensions"] = "EXTN",
        ["ParticleSystems"] = "PSYS",
        ["ParticleSystemEmitters"] = "PSEM",
        ["FilterEffects"] = "FEDS",
        ["UINodes"] = "UILR",
    };

    /// <summary>
    /// Creates a missing resource chunk. The strings chunk is kept last so newly added chunks
    /// can reference strings; string references are only patched up when the strings chunk
    /// itself is written, so every string-referencing chunk has to precede it.
    /// </summary>
    private static void EnsureChunk(UndertaleData data, string chunkName)
    {
        if (data.FORM.Chunks.ContainsKey(chunkName))
        {
            return;
        }
        if (!UndertaleChunkFORM.ChunkConstructors.TryGetValue(chunkName, out Func<UndertaleChunk>? factory))
        {
            throw new PatchSnapshotException($"Unknown chunk \"{chunkName}\"");
        }

        UndertaleChunk chunk = factory();
        data.FORM.Chunks[chunkName] = chunk;
        data.FORM.ChunksTypeDict[chunk.GetType()] = chunk;

        MoveStringsChunkLast(data);
    }

    /// <summary>
    /// Appends the strings chunk to the end of the chunk order. <see cref="Dictionary{TKey,TValue}"/>
    /// reuses freed slots, so the dictionary is rebuilt to guarantee the order.
    /// </summary>
    private static void MoveStringsChunkLast(UndertaleData data)
    {
        List<KeyValuePair<string, UndertaleChunk>> entries = [.. data.FORM.Chunks];
        int index = entries.FindIndex(static e => e.Key == "STRG");
        if (index < 0 || index == entries.Count - 1)
        {
            return;
        }

        KeyValuePair<string, UndertaleChunk> strings = entries[index];
        entries.RemoveAt(index);
        entries.Add(strings);

        data.FORM.Chunks.Clear();
        foreach (KeyValuePair<string, UndertaleChunk> entry in entries)
        {
            data.FORM.Chunks.Add(entry.Key, entry.Value);
        }
    }

    private UndertaleString MakeString(string? value) => _baseData.Strings.MakeString(value ?? "");

    private readonly List<string> _generalInfoWarnings = [];

    private void ApplyGeneralInfo(PatchGeneralInfoSnapshot snapshot)
    {
        UndertaleGeneralInfo info = _baseData.GeneralInfo;
        info.IsDebuggerDisabled = snapshot.IsDebuggerDisabled;
        info.BytecodeVersion = snapshot.BytecodeVersion;
        info.Padding = snapshot.Padding;
        info.FileName = MakeString(snapshot.FileName);
        info.Config = MakeString(snapshot.Config);
        info.LastObj = snapshot.LastObj;
        info.LastTile = snapshot.LastTile;
        info.GameID = snapshot.GameID;
        info.DirectPlayGuid = Guid.Parse(snapshot.DirectPlayGuid ?? Guid.Empty.ToString("D"));
        info.Name = MakeString(snapshot.Name);
        info.Major = snapshot.Major;
        info.Minor = snapshot.Minor;
        info.Release = snapshot.Release;
        info.Build = snapshot.Build;
        info.Branch = (UndertaleGeneralInfo.BranchType)snapshot.Branch;
        info.DefaultWindowWidth = snapshot.DefaultWindowWidth;
        info.DefaultWindowHeight = snapshot.DefaultWindowHeight;
        info.Info = (UndertaleGeneralInfo.InfoFlags)snapshot.Info;
        info.LicenseMD5 = Convert.FromBase64String(snapshot.LicenseMD5 ?? "");
        info.LicenseCRC32 = snapshot.LicenseCRC32;
        info.Timestamp = snapshot.Timestamp;
        info.DisplayName = MakeString(snapshot.DisplayName);
        info.ActiveTargets = snapshot.ActiveTargets;
        info.FunctionClassifications = (UndertaleGeneralInfo.FunctionClassification)snapshot.FunctionClassifications;
        info.SteamAppID = snapshot.SteamAppID;
        info.DebuggerPort = snapshot.DebuggerPort;
        info.RoomOrder.Clear();
        foreach (string roomName in snapshot.RoomOrder)
        {
            UndertaleRoom? room = _baseData.Rooms?.ByName(roomName);
            if (room is null)
            {
                _generalInfoWarnings.Add($"Room order entry \"{roomName}\" was not found");
                continue;
            }
            info.RoomOrder.Add(new UndertaleResourceById<UndertaleRoom, UndertaleChunkROOM> { Resource = room });
        }
        info.GMS2RandomUID.Clear();
        info.GMS2RandomUID.AddRange(snapshot.GMS2RandomUID);
        info.GMS2FPS = snapshot.GMS2FPS;
        info.GMS2AllowStatistics = snapshot.GMS2AllowStatistics;
        info.GMS2GameGUID = Convert.FromBase64String(snapshot.GMS2GameGUID ?? "");
        info.InfoTimestampOffset = snapshot.InfoTimestampOffset;

        UndertaleOptions? options = _baseData.Options;
        if (options is null)
        {
            return;
        }
        options.ShaderExtensionFlag = snapshot.ShaderExtensionFlag;
        options.ShaderExtensionVersion = snapshot.ShaderExtensionVersion;
        options.Info = (UndertaleOptions.OptionsFlags)snapshot.OptionsInfo;
        options.Scale = snapshot.OptionsScale;
        options.WindowColor = snapshot.WindowColor;
        options.ColorDepth = snapshot.ColorDepth;
        options.Resolution = snapshot.Resolution;
        options.Frequency = snapshot.Frequency;
        options.VertexSync = snapshot.VertexSync;
        options.Priority = snapshot.Priority;
        options.LoadAlpha = snapshot.LoadAlpha;
        options.NewFormat = snapshot.NewFormat;
        options.Constants.Clear();
        foreach (PatchOptionConstant constant in snapshot.Constants)
        {
            options.Constants.Add(new UndertaleOptions.Constant
            {
                Name = MakeString(constant.Name),
                Value = MakeString(constant.Value),
            });
        }
    }

    private void ApplyOptionImages(PatchEntry entry, PatchGeneralInfoSnapshot snapshot,
                                   Dictionary<string, UndertaleTexturePageItem> restoredPageItems,
                                   DataFilePatchApplyResult result)
    {
        foreach (string warning in _generalInfoWarnings)
        {
            result.Warnings.Add(warning);
        }
        _generalInfoWarnings.Clear();

        UndertaleOptions? options = _baseData.Options;
        if (options is null)
        {
            return;
        }

        foreach (PatchTextureRef textureRef in entry.Textures)
        {
            if (!restoredPageItems.TryGetValue(Path.GetFileNameWithoutExtension(textureRef.PageItemPath), out UndertaleTexturePageItem? pageItem))
            {
                continue;
            }
            UndertaleSprite.TextureEntry? target = textureRef.FrameIndex switch
            {
                0 => options.BackImage,
                1 => options.FrontImage,
                2 => options.LoadImage,
                _ => null,
            };
            if (target is not null)
            {
                target.Texture = pageItem;
            }
        }
    }

    private void ApplySpriteExtras(string workDirectory, DataFilePatchManifest manifest,
                                   DataFilePatchApplyResult result, JsonSerializerOptions jsonOptions)
    {
        foreach (PatchEntry entry in manifest.Entries)
        {
            if (!entry.Supported || entry.SpriteExtraPath is null)
            {
                continue;
            }
            try
            {
                PatchSpineSpriteSnapshot snapshot;
                using (FileStream fs = new(Path.Join(workDirectory, entry.SpriteExtraPath), FileMode.Open, FileAccess.Read))
                {
                    snapshot = JsonSerializer.Deserialize<PatchSpineSpriteSnapshot>(fs, jsonOptions)
                        ?? throw new PatchSnapshotException("Spine snapshot is empty");
                }

                _mainThreadAction(() =>
                {
                    UndertaleSprite sprite = _baseData.Sprites.ByName(entry.Name)
                        ?? throw new PatchSnapshotException("Sprite was not found after import");
                    sprite.SpineVersion = snapshot.SpineVersion;
                    sprite.SpineJSON = snapshot.SpineJSON;
                    sprite.SpineAtlas = snapshot.SpineAtlas;
                    sprite.SpineTextures ??= [];
                    sprite.SpineTextures.Clear();
                    foreach (PatchSpineTextureEntry texture in snapshot.SpineTextures)
                    {
                        sprite.SpineTextures.Add(new UndertaleSpineTextureEntry
                        {
                            PageWidth = texture.PageWidth,
                            PageHeight = texture.PageHeight,
                            TexBlob = texture.TexBlob is null ? null! : Convert.FromBase64String(texture.TexBlob),
                            TextureEntryLength = texture.TextureEntryLength,
                        });
                    }
                });
            }
            catch (Exception e)
            {
                result.Errors.Add($"Sprite extra restore failed for \"{entry.TypeId}/{entry.Name}\": {e.Message}");
            }
        }
    }

    private void ApplyGroupSounds(string workDirectory, DataFilePatchManifest manifest,
                                  DataFilePatchApplyOptions options, DataFilePatchApplyResult result,
                                  CancellationToken cancellationToken, JsonSerializerOptions jsonOptions)
    {
        List<PatchEntry> entries = [.. manifest.Entries.Where(e =>
            e.Supported && e.Kind == PatchEntryKind.GroupSound && e.SnapshotPath is not null &&
            e.Change != nameof(AssetChangeKind.Removed))];
        if (entries.Count == 0)
        {
            return;
        }

        EnsureChunk(_baseData, "SOND");

        foreach (PatchEntry entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                PatchGroupSoundSnapshot snapshot;
                using (FileStream fs = new(Path.Join(workDirectory, entry.SnapshotPath), FileMode.Open, FileAccess.Read))
                {
                    snapshot = JsonSerializer.Deserialize<PatchGroupSoundSnapshot>(fs, jsonOptions)
                        ?? throw new PatchSnapshotException("Group sound snapshot is empty");
                }

                SerializableSound soundAsset;
                using (FileStream fs = new(Path.Join(workDirectory, snapshot.SoundJsonPath), FileMode.Open, FileAccess.Read))
                {
                    soundAsset = (SerializableSound)JsonSerializer.Deserialize<ISerializableProjectAsset>(fs, ProjectContext.JsonOptions)!;
                }

                _mainThreadAction(() =>
                {
                    ApplyGroupSound(workDirectory, snapshot, soundAsset!, entry, options, result);
                });
            }
            catch (Exception e)
            {
                result.Errors.Add($"Group sound restore failed for \"{entry.TypeId}/{entry.Name}\": {e.Message}");
            }
        }
    }

    private void ApplyGroupSound(string workDirectory, PatchGroupSoundSnapshot snapshot, SerializableSound soundAsset,
                                 PatchEntry entry, DataFilePatchApplyOptions options, DataFilePatchApplyResult result)
    {
        UndertaleSound sound = _baseData.Sounds.ByName(entry.Name) ?? new UndertaleSound
        {
            Name = _baseData.Strings.MakeString(entry.Name),
        };
        if (_baseData.Sounds.ByName(entry.Name) is null)
        {
            _baseData.Sounds.Add(sound);
        }

        sound.File = _baseData.Strings.MakeString(soundAsset.Filename ?? "");
        sound.Type = _baseData.Strings.MakeString(soundAsset.Type ?? "");
        sound.Volume = soundAsset.Volume;
        sound.Pitch = soundAsset.Pitch;
        sound.Effects = soundAsset.Effects;
        sound.AudioLength = soundAsset.AudioLength;
        sound.Preload = true;

        int groupIndex = _baseData.GetBuiltinSoundGroupID();
        if (!string.IsNullOrEmpty(snapshot.GroupName))
        {
            groupIndex = -1;
            if (_baseData.AudioGroups is { } groups)
            {
                for (int i = 0; i < groups.Count; i++)
                {
                    if (groups[i]?.Name?.Content == snapshot.GroupName)
                    {
                        groupIndex = i;
                        break;
                    }
                }
            }
            if (groupIndex < 0)
            {
                throw new PatchSnapshotException($"Audio group \"{snapshot.GroupName}\" was not found in the patched data file");
            }
        }

        string audioBase = Path.Join(workDirectory,
            Path.Join(Path.GetDirectoryName(snapshot.SoundJsonPath) ?? "",
                      Path.GetFileNameWithoutExtension(snapshot.SoundJsonPath)));
        string? audioFile = null;
        foreach (string extension in new[] { "wav", "ogg", "mp3" })
        {
            string attempt = audioBase + "." + extension;
            if (File.Exists(attempt))
            {
                audioFile = attempt;
                break;
            }
        }
        if (audioFile is null)
        {
            throw new PatchSnapshotException("Exported group sound audio file was not found");
        }
        bool isLossless = audioFile.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);

        if (soundAsset.Streamed)
        {
            if (isLossless)
            {
                throw new PatchSnapshotException("Streamed sound has a lossless audio file");
            }
            sound.Flags = UndertaleSound.AudioEntryFlags.Regular;
            sound.AudioFile = null;
            sound.AudioID = -1;
            sound.GroupID = groupIndex;
            if (_baseData.AudioGroups is { } streamedGroups && groupIndex < streamedGroups.Count)
            {
                sound.AudioGroup = streamedGroups[groupIndex];
            }

            string streamedName = soundAsset.Filename ?? entry.Name;
            if (options.OutputDirectory is not null)
            {
                string destination = Path.Join(options.OutputDirectory, streamedName);
                Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? options.OutputDirectory);
                File.Copy(audioFile, destination, true);
                result.ExtraOutputFiles.Add(destination);
            }
            return;
        }

        sound.Flags = soundAsset.DecompressOnLoad
            ? UndertaleSound.AudioEntryFlags.IsEmbedded | UndertaleSound.AudioEntryFlags.IsCompressed |
              UndertaleSound.AudioEntryFlags.IsDecompressedOnLoad | UndertaleSound.AudioEntryFlags.Regular
            : isLossless
                ? UndertaleSound.AudioEntryFlags.IsEmbedded | UndertaleSound.AudioEntryFlags.Regular
                : UndertaleSound.AudioEntryFlags.IsCompressed | UndertaleSound.AudioEntryFlags.Regular;
        if (soundAsset.DecompressOnLoad && isLossless)
        {
            throw new PatchSnapshotException("Decompress-on-load sound has a lossless audio file");
        }

        // Seed the group file from the base directory, or from the compared file's captured copy.
        string groupFileName = _baseData.AudioGroups is { } audioGroups && groupIndex < audioGroups.Count &&
                               audioGroups[groupIndex]?.Path?.Content is { Length: > 0 } groupPath
            ? groupPath
            : $"audiogroup{groupIndex}.dat";
        UndertaleData? groupData = null;
        string? baseGroupFile = options.BaseDataDirectory is null
            ? null
            : Path.Join(options.BaseDataDirectory, groupFileName);
        if (baseGroupFile is not null && File.Exists(baseGroupFile))
        {
            using FileStream fs = new(baseGroupFile, FileMode.Open, FileAccess.Read);
            groupData = UndertaleIO.Read(fs, (_, _) => { }, _ => { });
        }
        else if (snapshot.GroupFilePath is not null)
        {
            using FileStream fs = new(Path.Join(workDirectory, snapshot.GroupFilePath), FileMode.Open, FileAccess.Read);
            groupData = UndertaleIO.Read(fs, (_, _) => { }, _ => { });
        }
        if (groupData is null)
        {
            throw new PatchSnapshotException($"Audio group file \"{groupFileName}\" was not found");
        }

        try
        {
            UndertaleEmbeddedAudio audio = new()
            {
                Name = groupData.Strings.MakeString($"EmbeddedSound {groupData.EmbeddedAudio.Count}"),
                Data = File.ReadAllBytes(audioFile),
            };
            groupData.EmbeddedAudio.Add(audio);

            sound.AudioFile = null;
            sound.GroupID = groupIndex;
            sound.AudioID = groupData.EmbeddedAudio.Count - 1;
            if (_baseData.AudioGroups is { } groupsWithIndex && groupIndex < groupsWithIndex.Count)
            {
                sound.AudioGroup = groupsWithIndex[groupIndex];
            }

            if (options.OutputDirectory is not null)
            {
                string destination = Path.Join(options.OutputDirectory, groupFileName);
                Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? options.OutputDirectory);
                using FileStream fs = new(destination, FileMode.Create, FileAccess.Write);
                UndertaleIO.Write(fs, groupData);
                result.ExtraOutputFiles.Add(destination);
            }
            else
            {
                result.Warnings.Add($"Audio group file \"{groupFileName}\" was not written (no output directory)");
            }
        }
        finally
        {
            groupData.Dispose();
        }
    }

    private void ApplySubCodeEntries(string workDirectory, DataFilePatchManifest manifest,
                                     DataFilePatchApplyOptions options, DataFilePatchApplyResult result,
                                     CancellationToken cancellationToken, JsonSerializerOptions jsonOptions)
    {
        List<PatchEntry> entries = [.. manifest.Entries.Where(e =>
            e.Supported && e.Kind == PatchEntryKind.SubCode && e.SnapshotPath is not null)];
        if (entries.Count == 0)
        {
            return;
        }

        // Parents whose GML is patched are recompiled, which regenerates their child entries;
        // splicing into them would conflict with the freshly compiled blob.
        HashSet<string> recompiledParents = new(
            manifest.Entries
                .Where(e => e.Supported && e.Kind == PatchEntryKind.Asset &&
                            e.AssetType == nameof(SerializableAssetType.Code) &&
                            e.Change != nameof(AssetChangeKind.Removed))
                .Select(e => e.Name),
            StringComparer.Ordinal);

        foreach (PatchEntry entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                PatchSubCodeSnapshot snapshot;
                using (FileStream fs = new(Path.Join(workDirectory, entry.SnapshotPath), FileMode.Open, FileAccess.Read))
                {
                    snapshot = JsonSerializer.Deserialize<PatchSubCodeSnapshot>(fs, jsonOptions)
                        ?? throw new PatchSnapshotException("Sub-code snapshot is empty");
                }

                if (recompiledParents.Contains(snapshot.ParentEntry))
                {
                    continue;
                }

                UndertaleCode parent = _baseData.Code?.ByName(snapshot.ParentEntry)
                    ?? throw new PatchSnapshotException($"Parent code entry \"{snapshot.ParentEntry}\" was not found");
                UndertaleCode? child = _baseData.Code?.ByName(entry.Name);

                if (entry.Change == nameof(AssetChangeKind.Removed))
                {
                    if (child is null || child.ParentEntry != parent)
                    {
                        continue;
                    }
                    if (!options.DeleteRemoved)
                    {
                        result.Warnings.Add($"{entry.TypeId}/{entry.Name}: kept because deletions are disabled");
                        continue;
                    }
                    _mainThreadAction(() =>
                    {
                        RemoveSubCodeBlock(parent, child!);
                        parent.ChildEntries.Remove(child!);
                        _baseData.Code!.Remove(child!);
                    });
                    continue;
                }

                _mainThreadAction(() =>
                {
                    UndertaleCode currentChild = child ?? new UndertaleCode { Name = _baseData.Strings.MakeString(entry.Name) };
                    if (child is null)
                    {
                        child = currentChild;
                        _baseData.Code!.Add(currentChild);
                    }
                    if (currentChild.ParentEntry is not null && currentChild.ParentEntry != parent)
                    {
                        throw new PatchSnapshotException("Sub-code entry is attached to a different parent");
                    }

                    // Attach new children only after the block has been inserted, so the
                    // splice takes the insertion path instead of replacing bytes at offset 0.
                    bool attached = currentChild.ParentEntry == parent;
                    SpliceSubCodeBlock(_baseData, parent, currentChild, snapshot);
                    if (!attached)
                    {
                        currentChild.ParentEntry = parent;
                        parent.ChildEntries.Add(currentChild);

                        // The code chunk writes blobs in list order and sub-entries bind to the
                        // blob written right before them, so the new child has to sit directly
                        // after its parent's blob group.
                        UndertalePointerList<UndertaleCode> codeList = _baseData.Code!;
                        int groupEnd = codeList.IndexOf(parent!);
                        foreach (UndertaleCode sibling in parent.ChildEntries)
                        {
                            if (sibling == child)
                            {
                                continue;
                            }
                            int index = codeList.IndexOf(sibling);
                            if (index > groupEnd)
                            {
                                groupEnd = index;
                            }
                        }
                        codeList.Remove(child);
                        codeList.Insert(Math.Min(groupEnd + 1, codeList.Count), child);
                    }

                    currentChild.LocalsCount = (uint)snapshot.LocalsCount;
                    currentChild.ArgumentsCount = (ushort)snapshot.ArgumentsCount;
                    currentChild.WeirdLocalFlag = snapshot.WeirdLocalFlag;
                });
            }
            catch (Exception e)
            {
                result.Errors.Add($"Sub-code restore failed for \"{entry.TypeId}/{entry.Name}\": {e.Message}");
            }
        }
    }

    /// <summary>
    /// Replaces (or inserts) the instruction block of a sub-code entry inside the parent blob
    /// and renumbers the sibling offsets. The blob is a flat instruction list; the root owns the
    /// bytes before the first sub-entry, and each sub-entry owns the bytes up to the next one.
    /// </summary>
    private static void SpliceSubCodeBlock(UndertaleData data, UndertaleCode parent, UndertaleCode child,
                                           PatchSubCodeSnapshot snapshot)
    {
        List<UndertaleInstruction> instructions = parent.Instructions;
        bool alreadyAttached = child.ParentEntry == parent && parent.ChildEntries.Contains(child);
        List<UndertaleInstruction> decoded = DecodeInstructions(data, snapshot.Instructions);

        // Existing siblings, in blob order, with their instruction index and count.
        List<UndertaleCode> ordered = [.. parent.ChildEntries];
        if (!alreadyAttached && ordered.Contains(child))
        {
            ordered.Remove(child);
        }
        ordered.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));

        List<(int Start, int Count)> blocks = new(ordered.Count + 1);
        foreach (UndertaleCode sibling in ordered)
        {
            int siblingStart = CodeFingerprint.IndexAtOffset(instructions, sibling.Offset, out _);
            if (siblingStart < 0)
            {
                throw new PatchSnapshotException("Sibling code block does not start on an instruction boundary");
            }
            blocks.Add((siblingStart, 0));
        }
        for (int i = 0; i < blocks.Count; i++)
        {
            int nextStart = i + 1 < blocks.Count ? blocks[i + 1].Start : instructions.Count;
            blocks[i] = (blocks[i].Start, nextStart - blocks[i].Start);
        }

        int targetBlockIndex;
        int insertionIndex;
        if (alreadyAttached)
        {
            int current = CodeFingerprint.IndexAtOffset(instructions, child.Offset, out _);
            targetBlockIndex = ordered.IndexOf(child);
            if (targetBlockIndex < 0 || current < 0)
            {
                throw new PatchSnapshotException("Sub-code block does not start on an instruction boundary");
            }
            // Replace the current block contents with the snapshot's instructions.
            (int start, int count) = blocks[targetBlockIndex];
            instructions.RemoveRange(start, count);
            instructions.InsertRange(start, decoded);
            blocks[targetBlockIndex] = (start, decoded.Count);
            int delta = decoded.Count - count;
            for (int i = targetBlockIndex + 1; i < blocks.Count; i++)
            {
                blocks[i] = (blocks[i].Start + delta, blocks[i].Count);
            }
            insertionIndex = targetBlockIndex;
        }
        else
        {
            insertionIndex = CodeFingerprint.IndexAtOffset(instructions, snapshot.Offset, out _);
            if (insertionIndex < 0)
            {
                throw new PatchSnapshotException("Sub-code offset does not land on an instruction boundary");
            }

            // Determine the position of the new block among the existing ones.
            int position = blocks.Count;
            for (int i = 0; i < blocks.Count; i++)
            {
                if (blocks[i].Start >= insertionIndex)
                {
                    position = i;
                    break;
                }
            }
            instructions.InsertRange(insertionIndex, decoded);
            for (int i = 0; i < blocks.Count; i++)
            {
                if (blocks[i].Start >= insertionIndex)
                {
                    blocks[i] = (blocks[i].Start + decoded.Count, blocks[i].Count);
                }
            }
            blocks.Insert(position, (insertionIndex, decoded.Count));
            ordered.Insert(position, child);
            targetBlockIndex = position;
        }

        // Renumber every child's offset from the new block layout (instruction sizes are
        // counted in 4-byte words, offsets are byte offsets).
        Dictionary<int, uint> wordAt = [];
        uint running = 0;
        for (int i = 0; i < instructions.Count; i++)
        {
            wordAt[i] = running;
            running += CodeFingerprint.GetInstructionSize(instructions[i]);
        }
        wordAt[instructions.Count] = running;

        for (int i = 0; i < blocks.Count; i++)
        {
            UndertaleCode? target = i < ordered.Count ? ordered[i] : null;
            if (target is null)
            {
                throw new PatchSnapshotException("Sub-code block ordering is inconsistent");
            }
            target.Offset = wordAt[blocks[i].Start] * 4;
        }
    }

    /// <summary>
    /// Removes the instruction block belonging to a sub-code entry and renumbers the siblings.
    /// </summary>
    private static void RemoveSubCodeBlock(UndertaleCode parent, UndertaleCode child)
    {
        List<UndertaleInstruction> instructions = parent.Instructions;
        List<UndertaleCode> ordered = [.. parent.ChildEntries];
        ordered.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));

        int targetIndex = ordered.IndexOf(child);
        if (targetIndex < 0)
        {
            return;
        }

        int start = CodeFingerprint.IndexAtOffset(instructions, child.Offset, out _);
        if (start < 0)
        {
            throw new PatchSnapshotException("Sub-code block does not start on an instruction boundary");
        }

        int end = targetIndex + 1 < ordered.Count
            ? CodeFingerprint.IndexAtOffset(instructions, ordered[targetIndex + 1].Offset, out _)
            : instructions.Count;
        if (end < 0)
        {
            throw new PatchSnapshotException("Sub-code block end does not land on an instruction boundary");
        }

        instructions.RemoveRange(start, end - start);
        ordered.RemoveAt(targetIndex);

        Dictionary<int, uint> wordAt = [];
        uint running = 0;
        for (int i = 0; i < instructions.Count; i++)
        {
            wordAt[i] = running;
            running += CodeFingerprint.GetInstructionSize(instructions[i]);
        }
        wordAt[instructions.Count] = running;

        foreach (UndertaleCode sibling in ordered)
        {
            int index = CodeFingerprint.IndexAtOffset(instructions, sibling.Offset, out _);
            if (index >= 0)
            {
                sibling.Offset = wordAt[index] * 4;
            }
        }
    }

    private static List<UndertaleInstruction> DecodeInstructions(UndertaleData data,
                                                                 List<PatchInstructionSnapshot> snapshots)
    {
        List<UndertaleInstruction> instructions = new(snapshots.Count);
        foreach (PatchInstructionSnapshot snapshot in snapshots)
        {
            UndertaleInstruction instruction = new()
            {
                Kind = (UndertaleInstruction.Opcode)snapshot.Kind,
                RawFirstWord = snapshot.FirstWord,
                RawPrimitive = snapshot.Primitive,
            };
            if (snapshot.ValueString is not null)
            {
                UndertaleString text = data.Strings.MakeString(snapshot.ValueString, out int stringIndex);
                instruction.ValueString = new UndertaleResourceById<UndertaleString, UndertaleChunkSTRG>(text, stringIndex);
            }
            if (snapshot.ValueVariable is not null)
            {
                UndertaleVariable variable = data.Variables.ByName(snapshot.ValueVariable);
                if (variable is null)
                {
                    UndertaleString name = data.Strings.MakeString(snapshot.ValueVariable, out int stringIndex);
                    variable = new UndertaleVariable { Name = name, NameStringID = stringIndex };
                    data.Variables.Add(variable);
                }
                instruction.ValueVariable = variable;
            }
            if (snapshot.ValueFunction is not null)
            {
                UndertaleFunction function = data.Functions.ByName(snapshot.ValueFunction);
                if (function is null)
                {
                    UndertaleString name = data.Strings.MakeString(snapshot.ValueFunction, out int stringIndex);
                    function = new UndertaleFunction { Name = name, NameStringID = stringIndex };
                    data.Functions.Add(function);
                }
                instruction.ValueFunction = function;
            }
            instructions.Add(instruction);
        }
        return instructions;
    }

    private void ApplySnapshots(string workDirectory, DataFilePatchManifest manifest,
                                DataFilePatchApplyResult result, IProgress<string>? progress,
                                CancellationToken cancellationToken, JsonSerializerOptions jsonOptions)
    {
        List<PatchEntry> entries = [.. manifest.Entries.Where(e =>
            e.Supported && e.Kind == PatchEntryKind.Snapshot && e.SnapshotPath is not null)];
        if (entries.Count == 0)
        {
            return;
        }

        // Pass 1: find or create the target resource shells, so references between entries can
        // resolve by name during the second pass.
        Dictionary<PatchEntry, UndertaleNamedResource> targets = new(ReferenceEqualityComparer.Instance);
        foreach (PatchEntry entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"{entry.TypeId}/{entry.Name}");
            try
            {
                if (SnapshotCategoryChunks.TryGetValue(entry.TypeId, out string? chunkName))
                {
                    EnsureChunk(_baseData, chunkName);
                }
                Type resourceType = ResolveResourceType(entry.AssetType, entry.ResourceTypeName, entry.TypeId)
                    ?? throw new PatchSnapshotException($"Unknown resource type for \"{entry.TypeId}/{entry.Name}\"");
                UndertaleNamedResource? target = FindByName(entry);
                if (target is null)
                {
                    target = (UndertaleNamedResource)(Activator.CreateInstance(resourceType)
                        ?? throw new PatchSnapshotException($"Cannot instantiate {resourceType.FullName}"));
                    target.Name = _baseData.Strings.MakeString(entry.Name);
                    System.Collections.IList list = _baseData[resourceType]
                        ?? throw new PatchSnapshotException($"Data file has no {resourceType.Name} list");
                    _mainThreadAction(() => list.Add(target));
                }
                targets[entry] = target;
            }
            catch (Exception e)
            {
                result.Errors.Add($"Snapshot restore failed for \"{entry.TypeId}/{entry.Name}\": {e.Message}");
            }
        }

        // Pass 2: decode the captured state onto the shells.
        foreach (PatchEntry entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!targets.TryGetValue(entry, out UndertaleNamedResource? target))
            {
                continue;
            }
            try
            {
                JsonNode snapshot;
                using (FileStream fs = new(Path.Join(workDirectory, entry.SnapshotPath), FileMode.Open, FileAccess.Read))
                {
                    snapshot = JsonNode.Parse(fs)
                        ?? throw new PatchSnapshotException("Snapshot file is empty");
                }
                _mainThreadAction(() => PatchSnapshotCodec.DecodeInto(_baseData, target, snapshot));
            }
            catch (Exception e)
            {
                result.Errors.Add($"Snapshot restore failed for \"{entry.TypeId}/{entry.Name}\": {e.Message}");
            }
        }
    }

    private void CountImportResults(DataFilePatchManifest manifest, DataFilePatchApplyOptions options,
                                    DataFilePatchApplyResult result)
    {
        foreach (PatchEntry entry in manifest.Entries)
        {
            if (!entry.Supported && entry.Kind != PatchEntryKind.Removed)
            {
                result.UnsupportedCount++;
                result.Unsupported.Add($"{entry.TypeId}/{entry.Name}: {entry.UnsupportedReason}");
                continue;
            }
            switch (entry.Change)
            {
                case nameof(AssetChangeKind.Added):
                    result.ImportedCount++;
                    break;
                case nameof(AssetChangeKind.Changed):
                    result.OverwrittenCount++;
                    break;
                case nameof(AssetChangeKind.Removed):
                    if (options.DeleteRemoved)
                    {
                        result.DeletedCount++;
                    }
                    else
                    {
                        result.Warnings.Add($"{entry.TypeId}/{entry.Name}: kept because deletions are disabled");
                    }
                    break;
            }
        }
    }

    private void DeleteRemoved(DataFilePatchManifest manifest, DataFilePatchApplyResult result)
    {
        foreach (PatchEntry entry in manifest.Entries)
        {
            if (entry.Change != nameof(AssetChangeKind.Removed))
            {
                continue;
            }

            try
            {
                Type? resourceType = ResolveResourceType(entry.AssetType, entry.ResourceTypeName, entry.TypeId);
                if (resourceType is null)
                {
                    result.Errors.Add($"Failed to delete \"{entry.TypeId}/{entry.Name}\": unknown resource type");
                    continue;
                }

                System.Collections.IList list = _baseData[resourceType];
                object? found = null;
                foreach (object item in list)
                {
                    if (item is UndertaleNamedResource named && string.Equals(named.Name?.Content, entry.Name, StringComparison.Ordinal))
                    {
                        found = item;
                        break;
                    }
                }
                if (found is null)
                {
                    result.Warnings.Add($"Could not find \"{entry.TypeId}/{entry.Name}\" to delete");
                    continue;
                }

                if (found is UndertaleAudioGroup)
                {
                    result.Warnings.Add("Deleted an audio group; remaining sounds that reference audio groups by index may now point to the wrong group");
                }

                // Code entries are referenced by global init scripts; remove those references
                // first so the file writer does not run into unwritten pointers.
                if (found is UndertaleCode code && _baseData.GlobalInitScripts is not null)
                {
                    for (int i = _baseData.GlobalInitScripts.Count - 1; i >= 0; i--)
                    {
                        if (_baseData.GlobalInitScripts[i]?.Code == code)
                        {
                            _baseData.GlobalInitScripts.RemoveAt(i);
                        }
                    }
                }

                list.Remove(found);
            }
            catch (Exception e)
            {
                result.Errors.Add($"Failed to delete \"{entry.TypeId}/{entry.Name}\": {e.Message}");
            }
        }
    }

    private void VerifyEntries(DataFilePatchManifest manifest, DataFilePatchApplyResult result,
                               CancellationToken cancellationToken)
    {
        AssetFingerprint fingerprint = new();
        foreach (PatchEntry entry in manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.Supported || entry.Kind == PatchEntryKind.Removed || entry.CompareFingerprint is null)
            {
                continue;
            }
            try
            {
                object? target = entry.Kind == PatchEntryKind.GeneralInfo ? _baseData.GeneralInfo : FindByName(entry);
                if (target is null)
                {
                    result.MismatchedCount++;
                    result.Warnings.Add($"Verification failed for \"{entry.TypeId}/{entry.Name}\": not found after apply");
                    continue;
                }

                byte[] actual = ComputeContentFingerprint(fingerprint, target, entry.TypeId);
                if (Convert.FromBase64String(entry.CompareFingerprint).AsSpan().SequenceEqual(actual))
                {
                    result.VerifiedCount++;
                }
                else
                {
                    result.MismatchedCount++;
                    result.Warnings.Add($"Verification mismatch for \"{entry.TypeId}/{entry.Name}\"");
                }
            }
            catch (Exception e)
            {
                result.MismatchedCount++;
                result.Warnings.Add($"Verification failed for \"{entry.TypeId}/{entry.Name}\": {e.Message}");
            }
        }
    }

    private byte[] ComputeContentFingerprint(AssetFingerprint fingerprint, object resource, string typeId)
    {
        if (resource is UndertaleCode code && typeId == "Code")
        {
            return CodeFingerprint.Compute(code, _baseData);
        }
        return resource is UndertaleResource typed ? fingerprint.Compute(typed) : fingerprint.ComputeObject(resource);
    }

    private UndertaleNamedResource? FindByName(PatchEntry entry)
    {
        Type? resourceType = ResolveResourceType(entry.AssetType, entry.ResourceTypeName, entry.TypeId);
        if (resourceType is null)
        {
            // Singletons (general info) have no resource list to search.
            return null;
        }
        System.Collections.IList list = _baseData[resourceType];
        if (list is null)
        {
            return null;
        }
        foreach (object item in list)
        {
            if (item is UndertaleNamedResource named && string.Equals(named.Name?.Content, entry.Name, StringComparison.Ordinal))
            {
                return named;
            }
        }
        return null;
    }

    private static Type? ResolveResourceType(string assetTypeName, string? resourceTypeName, string typeId)
    {
        if (!string.IsNullOrEmpty(resourceTypeName))
        {
            Type? resolved = typeof(DataFilePatchApplier).Assembly.GetType(resourceTypeName);
            if (resolved is not null)
            {
                return resolved;
            }
        }
        if (assetTypeName is not null && Enum.TryParse(assetTypeName, out SerializableAssetType assetType))
        {
            return assetType switch
            {
                SerializableAssetType.Sprite => typeof(UndertaleSprite),
                SerializableAssetType.Background => typeof(UndertaleBackground),
                SerializableAssetType.Sound => typeof(UndertaleSound),
                SerializableAssetType.Path => typeof(UndertalePath),
                SerializableAssetType.Script => typeof(UndertaleScript),
                SerializableAssetType.Shader => typeof(UndertaleShader),
                SerializableAssetType.Font => typeof(UndertaleFont),
                SerializableAssetType.GameObject => typeof(UndertaleGameObject),
                SerializableAssetType.Room => typeof(UndertaleRoom),
                SerializableAssetType.AnimationCurve => typeof(UndertaleAnimationCurve),
                SerializableAssetType.Sequence => typeof(UndertaleSequence),
                SerializableAssetType.Code => typeof(UndertaleCode),
                _ => null,
            };
        }
        return null;
    }
}
