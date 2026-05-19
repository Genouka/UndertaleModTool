using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using UndertaleModLib.Compiler;
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

        List<ISerializableProjectAsset> serializableAssets = new(allResourcesToImport.Count);
        List<SerializableCode> codeAssets = new(32);
        List<ISerializableTextureProjectAsset> textureAssets = new(32);

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

            try
            {
                ISerializableProjectAsset serializable = projectAsset.GenerateSerializableProjectAsset(sourceContext);

                if (conflictResolution == NameConflictResolution.Rename && resInfo.ExistsInTarget)
                {
                    string newName = GenerateUniqueName(resInfo.Name, resInfo.AssetType ?? SerializableAssetType.GameObject);
                    serializable.DataName = newName;
                }

                serializableAssets.Add(serializable);

                if (serializable is SerializableCode codeAsset)
                    codeAssets.Add(codeAsset);
                else if (serializable is ISerializableTextureProjectAsset textureAsset)
                    textureAssets.Add(textureAsset);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Failed to serialize resource \"{resInfo.Name}\": {ex.Message}");
            }
        }

        if (serializableAssets.Count == 0)
            return result;

        serializableAssets.Sort(CompareSerializableAssets);
        codeAssets.Sort(CompareSerializableAssets);
        textureAssets.Sort(CompareSerializableAssets);

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

        if (codeAssets.Count > 0)
        {
            CodeImportGroup importGroup = new(TargetData)
            {
                AutoCreateAssets = false,
                MainThreadAction = _mainThreadAction
            };
            foreach (SerializableCode asset in codeAssets)
            {
                try
                {
                    asset.ImportCode(targetContext, importGroup);
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Code import preparation failed for \"{asset.DataName}\": {ex.Message}");
                }
            }
            try
            {
                importGroup.Import(true);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Code compilation failed: {ex.Message}");
            }
        }

        if (textureAssets.Count > 0)
        {
            TextureGroupPacker packer = new();
            foreach (ISerializableTextureProjectAsset asset in textureAssets)
            {
                try
                {
                    asset.ImportTextures(targetContext, packer);
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Texture import failed for \"{asset.DataName}\": {ex.Message}");
                }
            }
            packer.PackPages();
            packer.ImportToData(TargetData);
        }

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
