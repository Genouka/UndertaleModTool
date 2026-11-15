#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UndertaleModLib.Models;
using UndertaleModLib.Project;

namespace UndertaleModLib.Diff;

public sealed class DataFileMergeOptions
{
    /// <summary>Delete resources which only exist in the base file.</summary>
    public bool DeleteRemoved { get; init; }

    /// <summary>Import resources referenced by the selected resources as well.</summary>
    public bool ImportDependencies { get; init; } = true;
}

public sealed class DataFileMergeResult
{
    public CrossFileImportResult Import { get; } = new();

    public int DeletedCount { get; set; }

    /// <summary>Selected resources for which the cross-file importer has no implementation.</summary>
    public List<string> Unsupported { get; } = [];

    /// <summary>Failures while deleting resources (does not include import errors).</summary>
    public List<string> DeleteErrors { get; } = [];

    public int WarningCount => Import.Warnings.Count + Unsupported.Count;
    public int ErrorCount => Import.Errors.Count + DeleteErrors.Count;
}

/// <summary>
/// Applies selected differences to the currently open data file.
/// Added and changed resources are imported from the compared data file; removed resources
/// are only deleted when explicitly requested, because they can leave dangling references.
/// </summary>
public static class DataFileMerger
{
    private static readonly HashSet<string> UnsupportedCategoryIds =
    [
        "GeneralInfo",
        "AudioGroups",
        "Timelines",
        "Extensions",
        "ParticleSystems",
        "ParticleSystemEmitters",
        "FilterEffects",
        "UINodes",
    ];

    public static DataFileMergeResult Merge(UndertaleData targetData, UndertaleData sourceData,
                                            IEnumerable<AssetDiffEntry> entries,
                                            DataFileMergeOptions? options = null,
                                            Action<Action>? mainThreadAction = null)
    {
        ArgumentNullException.ThrowIfNull(targetData);
        ArgumentNullException.ThrowIfNull(sourceData);
        ArgumentNullException.ThrowIfNull(entries);

        options ??= new DataFileMergeOptions();
        mainThreadAction ??= static action => action();

        DataFileMergeResult result = new();
        CrossFileImporter importer = new(targetData, mainThreadAction);
        try
        {
            importer.LoadSourceData(sourceData);

            Dictionary<object, ImportableResource> available = new(ReferenceEqualityComparer.Instance);
            foreach (ImportableResource importable in importer.EnumerateResources())
            {
                if (importable.Payload is ResourceInfo info && info.SourceObject is not null)
                    available[info.SourceObject] = importable;
            }

            List<ImportableResource> added = [];
            List<ImportableResource> changed = [];
            List<UndertaleCode> changedCode = [];

            foreach (AssetDiffEntry entry in entries)
            {
                if (entry.Change == AssetChangeKind.Unchanged)
                    continue;

                if (entry.Change == AssetChangeKind.Removed)
                {
                    if (options.DeleteRemoved)
                        DeleteResource(targetData, entry, result);
                    continue;
                }

                if (entry.CompareResource is null)
                    continue;

                if (entry.Change == AssetChangeKind.Changed &&
                    entry.TypeId == "GeneralInfo")
                {
                    result.Unsupported.Add($"{entry.TypeId}/{entry.Name}");
                    continue;
                }

                ImportableResource? importable = FindImportable(entry, targetData, available);
                if (importable is null)
                {
                    result.Unsupported.Add($"{entry.TypeId}/{entry.Name}");
                    continue;
                }

                if (entry.Change == AssetChangeKind.Added)
                {
                    added.Add(importable);
                }
                else
                {
                    changed.Add(importable);
                    if (entry.CompareResource is UndertaleCode code)
                        changedCode.Add(code);
                }
            }

            ImportWithResolution(importer, added, NameConflictResolution.Skip, options.ImportDependencies, result);
            ImportWithResolution(importer, changed, NameConflictResolution.Overwrite, options.ImportDependencies, result);

            // Cross-file code import recompiles GML, which can drop unused local slots. Copy the
            // source metadata back so a merged code entry compares equal after the import.
            if (changedCode.Count > 0)
            {
                mainThreadAction(() =>
                {
                    foreach (UndertaleCode sourceCode in changedCode)
                    {
                        string? name = sourceCode.Name?.Content;
                        if (name is null || targetData.Code?.ByName(name) is not UndertaleCode targetCode)
                            continue;

                        targetCode.LocalsCount = sourceCode.LocalsCount;
                        targetCode.ArgumentsCount = sourceCode.ArgumentsCount;
                        targetCode.WeirdLocalFlag = sourceCode.WeirdLocalFlag;
                    }
                });
            }
        }
        finally
        {
            importer.Dispose();
        }

        return result;
    }

    public static bool CanMerge(AssetDiffEntry entry)
    {
        if (UnsupportedCategoryIds.Contains(entry.TypeId))
            return false;

        if (entry.Change == AssetChangeKind.Removed)
            return entry.BaseResource is UndertaleResource;

        if (entry.CompareResource is null)
            return false;

        if (entry.CompareResource is UndertaleCode code)
            return code.ParentEntry is null;

        return entry.TypeId is "Sounds" or "Sprites" or "Backgrounds" or "Paths" or
            "Scripts" or "Shaders" or "Fonts" or "GameObjects" or "Rooms" or
            "AnimationCurves" or "Sequences";
    }

    private static void ImportWithResolution(CrossFileImporter importer,
                                             List<ImportableResource> resources,
                                             NameConflictResolution resolution,
                                             bool importDependencies,
                                             DataFileMergeResult result)
    {
        if (resources.Count == 0)
            return;

        try
        {
            CrossFileImportResult importResult = importer.ImportResources(resources, resolution, importDependencies);
            result.Import.ImportedCount += importResult.ImportedCount;
            result.Import.SkippedCount += importResult.SkippedCount;
            result.Import.OverwrittenCount += importResult.OverwrittenCount;
            result.Import.Errors.AddRange(importResult.Errors);
            result.Import.Warnings.AddRange(importResult.Warnings);
        }
        catch (Exception e)
        {
            result.Import.Errors.Add($"Import failed: {e.Message}");
        }
    }

    private static ImportableResource? FindImportable(AssetDiffEntry entry, UndertaleData targetData,
                                                      Dictionary<object, ImportableResource> available)
    {
        if (entry.CompareResource is UndertaleCode code)
        {
            string name = code.Name?.Content ?? entry.Name;
            bool exists = targetData.Code?.ByName(name) is not null;
            return new ImportableResource
            {
                Name = name,
                TypeName = "Code",
                ExistsInTarget = exists,
                Payload = new ResourceInfo
                {
                    Name = name,
                    AssetType = SerializableAssetType.Code,
                    ResourceType = typeof(UndertaleCode),
                    ExistsInTarget = exists,
                    SourceObject = code
                }
            };
        }

        return entry.CompareResource is not null &&
               available.TryGetValue(entry.CompareResource, out ImportableResource? importable)
            ? importable
            : null;
    }

    private static void DeleteResource(UndertaleData targetData, AssetDiffEntry entry, DataFileMergeResult result)
    {
        if (entry.BaseResource is not UndertaleResource resource)
            return;

        try
        {
            IList resourceList = targetData[resource.GetType()];
            resourceList.Remove(resource);
            result.DeletedCount++;
        }
        catch (Exception e)
        {
            result.DeleteErrors.Add($"Failed to delete {entry.TypeId}/{entry.Name}: {e.Message}");
        }
    }
}
