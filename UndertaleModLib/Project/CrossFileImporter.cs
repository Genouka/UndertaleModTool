using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImageMagick;
using Underanalyzer.Decompiler;
using UndertaleModLib.Compiler;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;
using UndertaleModLib.Project.SerializableAssets;
using UndertaleModLib.Util;

namespace UndertaleModLib.Project;

public sealed class CrossFileImportResult
{
    public int ImportedCount { get; set; }
    public int SkippedCount { get; set; }
    public int OverwrittenCount { get; set; }
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
}

public enum NameConflictResolution
{
    Skip,
    Overwrite,
    Rename
}

public sealed class CrossFileImporter : IDisposable
{
    public UndertaleData TargetData { get; }
    public UndertaleData SourceData { get; private set; }
    public string SourceFilePath { get; private set; }

    private readonly Action<Action> _mainThreadAction;
    private bool _disposed;

    public CrossFileImporter(UndertaleData targetData, Action<Action> mainThreadAction = null)
    {
        TargetData = targetData ?? throw new ArgumentNullException(nameof(targetData));
        _mainThreadAction = mainThreadAction ?? new Action<Action>(f => f());
    }

    public void LoadSourceFile(string filePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Source data file not found: {filePath}");

        SourceFilePath = filePath;
        SourceData?.Dispose();

        using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        SourceData = UndertaleIO.Read(fs, (string warning, bool isImportant) => { }, (_) => { });
    }

    public List<ResourceInfo> GetAvailableResources()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (SourceData is null)
            throw new InvalidOperationException("No source file loaded. Call LoadSourceFile first.");

        List<ResourceInfo> resources = new(256);

        AddNamedResources(resources, SourceData.Sounds, SerializableAssetType.Sound);
        AddNamedResources(resources, SourceData.Sprites, SerializableAssetType.Sprite);
        AddNamedResources(resources, SourceData.Backgrounds, SerializableAssetType.Background);
        AddNamedResources(resources, SourceData.Paths, SerializableAssetType.Path);
        AddNamedResources(resources, SourceData.Scripts, SerializableAssetType.Script);
        AddNamedResources(resources, SourceData.Shaders, null);
        AddNamedResources(resources, SourceData.Fonts, SerializableAssetType.Font);
        AddNamedResources(resources, SourceData.Timelines, null);
        AddNamedResources(resources, SourceData.GameObjects, SerializableAssetType.GameObject);
        AddNamedResources(resources, SourceData.Rooms, SerializableAssetType.Room);
        AddNamedResources(resources, SourceData.Extensions, null);
        AddNamedResources(resources, SourceData.AnimationCurves, SerializableAssetType.AnimationCurve);
        AddNamedResources(resources, SourceData.Sequences, SerializableAssetType.Sequence);
        AddNamedResources(resources, SourceData.ParticleSystems, null);

        return resources;
    }

    private void AddNamedResources<T>(List<ResourceInfo> list, IList<T> sourceList, SerializableAssetType? assetType) where T : UndertaleNamedResource
    {
        if (sourceList is null) return;

        foreach (T resource in sourceList)
        {
            if (resource?.Name?.Content is null) continue;

            bool existsInTarget = assetType.HasValue && ResourceExistsInTarget(resource.Name.Content, assetType.Value);

            list.Add(new ResourceInfo
            {
                Name = resource.Name.Content,
                AssetType = assetType,
                ResourceType = typeof(T),
                ExistsInTarget = existsInTarget,
                SourceObject = resource
            });
        }
    }

    private bool ResourceExistsInTarget(string name, SerializableAssetType assetType)
    {
        return assetType switch
        {
            SerializableAssetType.Sound => TargetData.Sounds?.ByName(name) is not null,
            SerializableAssetType.Sprite => TargetData.Sprites?.ByName(name) is not null,
            SerializableAssetType.Background => TargetData.Backgrounds?.ByName(name) is not null,
            SerializableAssetType.Path => TargetData.Paths?.ByName(name) is not null,
            SerializableAssetType.Script => TargetData.Scripts?.ByName(name) is not null,
            SerializableAssetType.Font => TargetData.Fonts?.ByName(name) is not null,
            SerializableAssetType.GameObject => TargetData.GameObjects?.ByName(name) is not null,
            SerializableAssetType.Room => TargetData.Rooms?.ByName(name) is not null,
            SerializableAssetType.AnimationCurve => TargetData.AnimationCurves?.ByName(name) is not null,
            SerializableAssetType.Sequence => TargetData.Sequences?.ByName(name) is not null,
            _ => false
        };
    }

    public CrossFileImportResult ImportResources(
        List<ResourceInfo> selectedResources,
        NameConflictResolution conflictResolution,
        bool importDependencies)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (SourceData is null)
            throw new InvalidOperationException("No source file loaded.");

        CrossFileImportResult result = new();

        HashSet<ResourceInfo> allResourcesToImport = new(selectedResources);

        if (importDependencies)
        {
            CollectDependencies(selectedResources, allResourcesToImport, result.Warnings);
        }

        List<ResourceInfo> codeResources = new();
        List<ResourceInfo> textureResources = new();
        List<ResourceInfo> otherResources = new();
        List<ISerializableProjectAsset> serializableAssets = new();

        CrossFileImportContext sourceContext = new(SourceData, _mainThreadAction);

        foreach (ResourceInfo resInfo in allResourcesToImport)
        {
            if (resInfo.SourceObject is not IProjectAsset projectAsset)
            {
                result.Warnings.Add($"Resource \"{resInfo.Name}\" does not support project serialization and was skipped.");
                continue;
            }

            if (!projectAsset.ProjectExportable)
            {
                result.Warnings.Add($"Resource \"{resInfo.Name}\" is not exportable and was skipped.");
                continue;
            }

            if (resInfo.ExistsInTarget)
            {
                switch (conflictResolution)
                {
                    case NameConflictResolution.Skip:
                        result.SkippedCount++;
                        continue;
                    case NameConflictResolution.Rename:
                        break;
                    case NameConflictResolution.Overwrite:
                        result.OverwrittenCount++;
                        break;
                }
            }

            if (resInfo.AssetType == SerializableAssetType.Code)
            {
                codeResources.Add(resInfo);
                continue;
            }

            if (resInfo.SourceObject is UndertaleSprite or UndertaleBackground or UndertaleFont)
            {
                textureResources.Add(resInfo);
            }

            otherResources.Add(resInfo);
        }

        foreach (ResourceInfo resInfo in otherResources)
        {
            try
            {
                ISerializableProjectAsset serializable = ((IProjectAsset)resInfo.SourceObject).GenerateSerializableProjectAsset(sourceContext);

                if (conflictResolution == NameConflictResolution.Rename && resInfo.ExistsInTarget)
                {
                    string newName = GenerateUniqueName(resInfo.Name, resInfo.AssetType ?? SerializableAssetType.GameObject);
                    serializable.DataName = newName;
                }

                serializableAssets.Add(serializable);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Failed to serialize resource \"{resInfo.Name}\": {ex.Message}");
            }
        }

        if (serializableAssets.Count == 0 && codeResources.Count == 0 && textureResources.Count == 0)
            return result;

        serializableAssets.Sort(CompareSerializableAssets);

        CrossFileImportContext targetContext = new(TargetData, _mainThreadAction);

        _mainThreadAction(() =>
        {
            foreach (ISerializableProjectAsset asset in serializableAssets)
            {
                try
                {
                    asset.PreImport(targetContext);
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"PreImport failed for \"{asset.DataName}\": {ex.Message}");
                }
            }
        });

        ImportCodeResources(codeResources, targetContext, conflictResolution, result);

        ImportTextureResources(textureResources, targetContext, conflictResolution, result);

        _mainThreadAction(() =>
        {
            foreach (ISerializableProjectAsset asset in serializableAssets)
            {
                try
                {
                    asset.Import(targetContext);
                    result.ImportedCount++;
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Import failed for \"{asset.DataName}\": {ex.Message}");
                }
            }
        });

        return result;
    }

    private void ImportCodeResources(
        List<ResourceInfo> codeResources,
        CrossFileImportContext targetContext,
        NameConflictResolution conflictResolution,
        CrossFileImportResult result)
    {
        if (codeResources.Count == 0) return;

        GlobalDecompileContext decompileContext = new(SourceData);

        CodeImportGroup importGroup = new(TargetData)
        {
            AutoCreateAssets = false,
            MainThreadAction = _mainThreadAction
        };

        foreach (ResourceInfo resInfo in codeResources)
        {
            if (resInfo.SourceObject is not UndertaleCode sourceCode) continue;

            UndertaleCode targetCode = TargetData.Code.ByName(resInfo.Name) as UndertaleCode;

            if (targetCode is not null)
            {
                switch (conflictResolution)
                {
                    case NameConflictResolution.Skip:
                        result.SkippedCount++;
                        continue;
                    case NameConflictResolution.Overwrite:
                        result.OverwrittenCount++;
                        break;
                    case NameConflictResolution.Rename:
                        string newName = GenerateUniqueName(resInfo.Name, SerializableAssetType.Code);
                        UndertaleString renameStr = null;
                        _mainThreadAction(() => renameStr = TargetData.Strings.MakeString(newName));
                        targetCode = new UndertaleCode()
                        {
                            Name = renameStr
                        };
                        _mainThreadAction(() => TargetData.Code.Add(targetCode));
                        if (TargetData.CodeLocals is not null)
                        {
                            _mainThreadAction(() => UndertaleCodeLocals.CreateEmptyEntry(TargetData, targetCode.Name));
                        }
                        break;
                }
            }
            else
            {
                UndertaleString newStr = null;
                _mainThreadAction(() => newStr = TargetData.Strings.MakeString(resInfo.Name));
                targetCode = new UndertaleCode()
                {
                    Name = newStr
                };
                _mainThreadAction(() => TargetData.Code.Add(targetCode));
                if (TargetData.CodeLocals is not null)
                {
                    _mainThreadAction(() => UndertaleCodeLocals.CreateEmptyEntry(TargetData, targetCode.Name));
                }
            }

            try
            {
                string source;
                try
                {
                    source = new DecompileContext(decompileContext, sourceCode, SourceData.ToolInfo.DecompilerSettings).DecompileToString();
                }
                catch
                {
                    source = sourceCode.Disassemble(SourceData.Variables, SourceData.CodeLocals?.For(sourceCode));
                }

                importGroup.QueueReplace(targetCode, source);

                const string globalScriptPrefix = "gml_GlobalScript_";
                if (targetCode.Name.Content.StartsWith(globalScriptPrefix, StringComparison.Ordinal))
                {
                    bool alreadyExists = TargetData.GlobalInitScripts.Any(gi => gi.Code == targetCode);
                    if (!alreadyExists)
                    {
                        _mainThreadAction(() => TargetData.GlobalInitScripts.Add(new UndertaleGlobalInit()
                        {
                            Code = targetCode
                        }));
                    }
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Failed to decompile code \"{resInfo.Name}\": {ex.Message}");
            }
        }

        try
        {
            importGroup.Import(true);
            result.ImportedCount += codeResources.Count;
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Code compilation failed: {ex.Message}");
        }
    }

    private void ImportTextureResources(
        List<ResourceInfo> textureResources,
        CrossFileImportContext targetContext,
        NameConflictResolution conflictResolution,
        CrossFileImportResult result)
    {
        if (textureResources.Count == 0) return;

        TextureGroupPacker packer = new();
        using TextureWorker textureWorker = new();

        foreach (ResourceInfo resInfo in textureResources)
        {
            try
            {
                if (resInfo.SourceObject is UndertaleSprite sourceSprite)
                {
                    ImportSpriteTextures(sourceSprite, packer, textureWorker, conflictResolution, result);
                }
                else if (resInfo.SourceObject is UndertaleBackground sourceBg)
                {
                    ImportBackgroundTextures(sourceBg, packer, textureWorker, conflictResolution, result);
                }
                else if (resInfo.SourceObject is UndertaleFont sourceFont)
                {
                    ImportFontTextures(sourceFont, packer, textureWorker, conflictResolution, result);
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Texture import failed for \"{resInfo.Name}\": {ex.Message}");
            }
        }

        packer.PackPages();
        _mainThreadAction(() => packer.ImportToData(TargetData));
    }

    private void ImportSpriteTextures(UndertaleSprite sourceSprite, TextureGroupPacker packer, TextureWorker textureWorker, NameConflictResolution conflictResolution, CrossFileImportResult result)
    {
        UndertaleSprite targetSprite = TargetData.Sprites.ByName(sourceSprite.Name.Content) as UndertaleSprite;
        if (targetSprite is null) return;

        List<UndertaleSprite.TextureEntry> newTextureEntries = new(sourceSprite.Textures.Count);
        foreach (var texEntry in sourceSprite.Textures)
        {
            if (texEntry.Texture is null) continue;

            UndertaleTexturePageItem newItem = CopyTexturePageItem(texEntry.Texture, packer, textureWorker);
            if (newItem is not null)
            {
                newTextureEntries.Add(new UndertaleSprite.TextureEntry()
                {
                    Texture = newItem
                });
            }
        }

        List<UndertaleSprite.MaskEntry> newMasks = new(sourceSprite.CollisionMasks);
        _mainThreadAction(() =>
        {
            targetSprite.Textures.Clear();
            foreach (var entry in newTextureEntries)
                targetSprite.Textures.Add(entry);

            targetSprite.CollisionMasks.Clear();
            foreach (var mask in newMasks)
                targetSprite.CollisionMasks.Add(mask);
        });
    }

    private void ImportBackgroundTextures(UndertaleBackground sourceBg, TextureGroupPacker packer, TextureWorker textureWorker, NameConflictResolution conflictResolution, CrossFileImportResult result)
    {
        UndertaleBackground targetBg = TargetData.Backgrounds.ByName(sourceBg.Name.Content) as UndertaleBackground;
        if (targetBg is null) return;

        if (sourceBg.Texture is not null)
        {
            UndertaleTexturePageItem newItem = CopyTexturePageItem(sourceBg.Texture, packer, textureWorker);
            if (newItem is not null)
            {
                targetBg.Texture = newItem;
            }
        }
    }

    private void ImportFontTextures(UndertaleFont sourceFont, TextureGroupPacker packer, TextureWorker textureWorker, NameConflictResolution conflictResolution, CrossFileImportResult result)
    {
        result.Warnings.Add($"Font \"{sourceFont.Name.Content}\" texture was not imported. Font textures require manual setup.");
    }

    private UndertaleTexturePageItem CopyTexturePageItem(UndertaleTexturePageItem sourceItem, TextureGroupPacker packer, TextureWorker textureWorker)
    {
        if (sourceItem is null || sourceItem.TexturePage is null) return null;

        try
        {
            IMagickImage<byte> image = textureWorker.GetTextureFor(sourceItem, sourceItem.ToString());
            if (image is null) return null;

            MagickImage magickImage;
            if (image is MagickImage mi)
                magickImage = mi;
            else
                magickImage = new MagickImage(image);

            return packer.AddImage(magickImage, TextureGroupPacker.BorderFlags.Enabled);
        }
        catch
        {
            return null;
        }
    }

    private void CollectDependencies(
        List<ResourceInfo> initialResources,
        HashSet<ResourceInfo> allResources,
        List<string> warnings)
    {
        Queue<ResourceInfo> queue = new(initialResources);
        HashSet<string> visited = new(allResources.Select(r => $"{r.ResourceType.Name}:{r.Name}"));

        while (queue.Count > 0)
        {
            ResourceInfo resInfo = queue.Dequeue();

            List<ResourceInfo> dependencies = GetDependencies(resInfo);
            foreach (ResourceInfo dep in dependencies)
            {
                string depKey = $"{dep.ResourceType.Name}:{dep.Name}";
                if (visited.Add(depKey))
                {
                    allResources.Add(dep);
                    queue.Enqueue(dep);
                    warnings.Add($"Auto-included dependency: \"{dep.Name}\" ({dep.AssetType?.ToInterfaceName() ?? dep.ResourceType.Name})");
                }
            }
        }
    }

    private List<ResourceInfo> GetDependencies(ResourceInfo resInfo)
    {
        List<ResourceInfo> deps = new();

        if (resInfo.SourceObject is UndertaleGameObject obj)
        {
            AddNamedDependency(deps, obj.Sprite, SerializableAssetType.Sprite);
            AddNamedDependency(deps, obj.ParentId, SerializableAssetType.GameObject);
            AddNamedDependency(deps, obj.TextureMaskId, SerializableAssetType.Sprite);

            foreach (var category in obj.Events)
            {
                foreach (var ev in category)
                {
                    foreach (var action in ev.Actions)
                    {
                        if (action.CodeId is not null)
                        {
                            AddNamedDependency(deps, action.CodeId, SerializableAssetType.Code);
                        }
                    }
                }
            }
        }
        else if (resInfo.SourceObject is UndertaleSprite sprite)
        {
            if (sprite.V2Sequence is not null)
            {
                AddNamedDependency(deps, sprite.V2Sequence, SerializableAssetType.Sequence);
            }
        }
        else if (resInfo.SourceObject is UndertaleRoom room)
        {
            foreach (var objInst in room.GameObjects)
            {
                AddNamedDependency(deps, objInst.ObjectDefinition, SerializableAssetType.GameObject);
            }
        }

        return deps;
    }

    private void AddNamedDependency<T>(List<ResourceInfo> deps, T resource, SerializableAssetType? assetType) where T : UndertaleNamedResource
    {
        if (resource is null) return;

        string name = resource.Name?.Content;
        if (name is null) return;

        bool existsInTarget = assetType.HasValue && ResourceExistsInTarget(name, assetType.Value);

        deps.Add(new ResourceInfo
        {
            Name = name,
            AssetType = assetType,
            ResourceType = typeof(T),
            ExistsInTarget = existsInTarget,
            SourceObject = resource
        });
    }

    private string GenerateUniqueName(string baseName, SerializableAssetType assetType)
    {
        string candidate = baseName + "_imported";
        int suffix = 1;

        while (ResourceExistsInTarget(candidate, assetType))
        {
            candidate = $"{baseName}_imported{suffix}";
            suffix++;
        }

        return candidate;
    }

    private static int CompareSerializableAssets(ISerializableProjectAsset a, ISerializableProjectAsset b)
    {
        if (a.OverrideOrder < b.OverrideOrder) return -1;
        if (a.OverrideOrder > b.OverrideOrder) return 1;
        return string.Compare(a.DataName, b.DataName, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            SourceData?.Dispose();
            SourceData = null;
            _disposed = true;
        }
    }
}

public sealed record ResourceInfo
{
    public string Name { get; init; }
    public SerializableAssetType? AssetType { get; init; }
    public Type ResourceType { get; init; }
    public bool ExistsInTarget { get; init; }
    public UndertaleNamedResource SourceObject { get; init; }
}

internal sealed class CrossFileImportContext : ProjectContext
{
    internal CrossFileImportContext(UndertaleData data, Action<Action> mainThreadAction)
        : base(data, data, mainThreadAction)
    {
    }
}
