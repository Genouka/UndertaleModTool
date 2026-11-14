using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
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
    public Action<Action> MainThreadAction { get; set; }
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

    public DataFilePatchApplier(UndertaleData baseData, Action<Action> mainThreadAction = null)
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
    public DataFilePatchApplyResult Apply(string patchFilePath, DataFilePatchApplyOptions options = null,
                                          IProgress<string> progress = null, CancellationToken cancellationToken = default)
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
                       IProgress<string> progress, CancellationToken cancellationToken, JsonSerializerOptions jsonOptions)
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
            UndertaleNamedResource existing = FindByName(entry);
            if (existing is null)
            {
                result.BaseDriftWarnings.Add($"{entry.TypeId}/{entry.Name}: missing in base file");
                continue;
            }
            byte[] current = ComputeContentFingerprint(baseFingerprint, existing, entry.TypeId);
            if (!Convert.FromBase64String(entry.BaseFingerprint).AsSpan().SequenceEqual(current))
            {
                result.BaseDriftWarnings.Add($"{entry.TypeId}/{entry.Name}: content differs from the base the patch was created against");
            }
        }

        // 1. General info (best effort).
        PatchEntry generalInfo = manifest.Entries.FirstOrDefault(e => e.Kind == PatchEntryKind.GeneralInfo && e.Supported);
        if (generalInfo is not null)
        {
            try
            {
                string path = Path.Join(workDirectory, generalInfo.AssetPath);
                PatchGeneralInfoSnapshot snapshot;
                using (FileStream fs = new(path, FileMode.Open, FileAccess.Read))
                {
                    snapshot = JsonSerializer.Deserialize<PatchGeneralInfoSnapshot>(fs, jsonOptions);
                }
                if (snapshot.Options is not null)
                {
                    SerializableGeneralInfoOptions generalOptions =
                        JsonSerializer.Deserialize<SerializableGeneralInfoOptions>(snapshot.Options, jsonOptions);
                    generalOptions.ImportTo(_baseData);
                }
                if (snapshot.GMS2RandomUID is not null)
                {
                    _baseData.GeneralInfo.GMS2RandomUID.Clear();
                    _baseData.GeneralInfo.GMS2RandomUID.AddRange(snapshot.GMS2RandomUID);
                }
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

        // 3. Restore exact textures and page items referenced by texture-backed assets.
        Dictionary<string, UndertaleEmbeddedTexture> restoredTextures = [];
        Dictionary<string, UndertaleTexturePageItem> restoredPageItems = [];
        RestoreTextures(context, workDirectory, manifest, restoredTextures, restoredPageItems, result, jsonOptions);

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
                asset = JsonSerializer.Deserialize<ISerializableProjectAsset>(fs, jsonOptions);
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

        CountImportResults(manifest, options, result);

        // 5. Removals (opt-in).
        if (options.DeleteRemoved)
        {
            DeleteRemoved(manifest, result);
        }

        // 6. High-water marks, so future insertions cannot collide with restored IDs.
        _baseData.GeneralInfo.LastObj = Math.Max(_baseData.GeneralInfo.LastObj, (uint)manifest.BaseLastObj);
        _baseData.GeneralInfo.LastTile = Math.Max(_baseData.GeneralInfo.LastTile, (uint)manifest.BaseLastTile);

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
                    codeAsset = (SerializableCode)JsonSerializer.Deserialize<ISerializableProjectAsset>(fs, ProjectContext.JsonOptions);
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
            UndertaleCode target = _baseData.Code?.ByName(entry.Name);
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
                            Image = CreateImage(snapshot),
                        },
                    };
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
                    UndertaleEmbeddedTexture texture = restoredTextures.TryGetValue($"{snapshot.TextureIndex}", out UndertaleEmbeddedTexture value)
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
            if (_baseData.TextureGroupInfo is not null)
            {
                foreach ((string index, UndertaleEmbeddedTexture texture) in restoredTextures)
                {
                    string groupName = textures[int.Parse(index)].TextureGroupName;
                    if (groupName is null)
                    {
                        continue;
                    }
                    UndertaleTextureGroupInfo group = _baseData.TextureGroupInfo.FirstOrDefault(g => g.Name?.Content == groupName);
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
                    texture.TextureInfo = group;
                }
            }
        });
    }

    private static T[] DeserializeArray<T>(string path, JsonSerializerOptions options)
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
            if (!restoredPageItems.TryGetValue(Path.GetFileNameWithoutExtension(textureRef.PageItemPath), out UndertaleTexturePageItem pageItem))
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
                Type resourceType = ResolveResourceType(entry.AssetType, entry.TypeId);
                if (resourceType is null)
                {
                    result.Errors.Add($"Failed to delete \"{entry.TypeId}/{entry.Name}\": unknown resource type");
                    continue;
                }

                System.Collections.IList list = _baseData[resourceType];
                object found = null;
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
                UndertaleNamedResource target = FindByName(entry);
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

    private UndertaleNamedResource FindByName(PatchEntry entry)
    {
        Type resourceType = ResolveResourceType(entry.AssetType, entry.TypeId);
        if (resourceType is null)
        {
            // Fall back to the aggregate ByName search.
            return _baseData.ByName(entry.Name);
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

    private static Type ResolveResourceType(string assetTypeName, string typeId)
    {
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
