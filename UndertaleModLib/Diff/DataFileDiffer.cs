#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UndertaleModLib.Models;

namespace UndertaleModLib.Diff;

/// <summary>
/// Compares two loaded data files, producing resource presence and modification results.
/// </summary>
public sealed class DataFileDiffer
{
    private const int ProgressInterval = 64;

    private readonly UndertaleData _baseData;
    private readonly UndertaleData _compareData;
    private readonly AssetFingerprint _baseFingerprint = new();
    private readonly AssetFingerprint _compareFingerprint = new();

    public DataFileDiffer(UndertaleData baseData, UndertaleData compareData)
    {
        _baseData = baseData ?? throw new ArgumentNullException(nameof(baseData));
        _compareData = compareData ?? throw new ArgumentNullException(nameof(compareData));
    }

    public DataFileDiffResult Compare(IProgress<DataFileDiffProgress>? progress = null,
                                      CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AssetCategoryDefinition> definitions = AssetCategories.All;
        List<AssetCategoryDiff> categories = new(definitions.Count);

        for (int i = 0; i < definitions.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssetCategoryDefinition definition = definitions[i];
            progress?.Report(new DataFileDiffProgress(i, definitions.Count, definition.Title));

            AssetCategoryDiff category = CompareCategory(definition, i, definitions.Count, progress, cancellationToken);
            categories.Add(category);
        }

        progress?.Report(new DataFileDiffProgress(definitions.Count, definitions.Count, "Done"));
        return new DataFileDiffResult { Categories = categories };
    }

    private AssetCategoryDiff CompareCategory(AssetCategoryDefinition definition, int categoryIndex,
                                              int categoryCount, IProgress<DataFileDiffProgress>? progress,
                                              CancellationToken cancellationToken)
    {
        Dictionary<string, Item> baseItems = BuildItems(definition, _baseData);
        Dictionary<string, Item> compareItems = BuildItems(definition, _compareData);

        SortedSet<string> keys = new(baseItems.Keys, StringComparer.Ordinal);
        keys.UnionWith(compareItems.Keys);

        List<AssetDiffEntry> entries = new(keys.Count);
        int processed = 0;
        foreach (string key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool hasBase = baseItems.TryGetValue(key, out Item baseItem);
            bool hasCompare = compareItems.TryGetValue(key, out Item compareItem);

            AssetChangeKind change;
            if (!hasBase)
            {
                change = AssetChangeKind.Added;
            }
            else if (!hasCompare)
            {
                change = AssetChangeKind.Removed;
            }
            else
            {
                change = AreEqual(definition, baseItem.Value, compareItem.Value)
                    ? AssetChangeKind.Unchanged
                    : AssetChangeKind.Changed;
            }

            entries.Add(new AssetDiffEntry
            {
                Name = hasBase ? baseItem.DisplayName : compareItem.DisplayName,
                TypeId = definition.Id,
                Change = change,
                BaseResource = hasBase ? baseItem.Value : null,
                CompareResource = hasCompare ? compareItem.Value : null,
            });

            if (++processed % ProgressInterval == 0)
            {
                progress?.Report(new DataFileDiffProgress(
                    categoryIndex, categoryCount,
                    $"{definition.Title} ({processed}/{keys.Count})"));
            }
        }

        return new AssetCategoryDiff
        {
            Id = definition.Id,
            Title = definition.Title,
            Entries = entries,
        };
    }

    private bool AreEqual(AssetCategoryDefinition definition, object baseValue, object compareValue)
    {
        try
        {
            if (definition.IsCode && baseValue is UndertaleCode baseCode && compareValue is UndertaleCode compareCode)
            {
                byte[] baseHash = CodeFingerprint.Compute(baseCode, _baseData);
                byte[] compareHash = CodeFingerprint.Compute(compareCode, _compareData);
                return baseHash.AsSpan().SequenceEqual(compareHash);
            }

            byte[] baseFingerprint = baseValue is UndertaleResource baseResource
                ? _baseFingerprint.Compute(baseResource)
                : _baseFingerprint.ComputeObject(baseValue);
            byte[] compareFingerprint = compareValue is UndertaleResource compareResource
                ? _compareFingerprint.Compute(compareResource)
                : _compareFingerprint.ComputeObject(compareValue);

            return baseFingerprint.AsSpan().SequenceEqual(compareFingerprint);
        }
        catch (Exception)
        {
            // A resource that cannot be walked is reported as changed instead of
            // aborting the whole comparison.
            return false;
        }
    }

    private static Dictionary<string, Item> BuildItems(AssetCategoryDefinition definition, UndertaleData data)
    {
        Dictionary<string, Item> items = new(StringComparer.Ordinal);
        Dictionary<string, int> occurrences = new(StringComparer.Ordinal);

        if (definition.Singleton is not null)
        {
            object? value = definition.Singleton(data);
            if (value is not null)
                items["General Info"] = new Item("General Info", value);
            return items;
        }

        int index = 0;
        foreach (object? value in definition.Enumerate(data) ?? [])
        {
            if (value is null)
            {
                index++;
                continue;
            }

            string displayName = GetDisplayName(value, index);
            string key = displayName;
            if (occurrences.TryGetValue(displayName, out int occurrence))
            {
                occurrence++;
                occurrences[displayName] = occurrence;
                key = displayName + "#" + occurrence;
            }
            else
            {
                occurrences[displayName] = 1;
            }

            items[key] = new Item(displayName, value);
            index++;
        }

        return items;
    }

    private static string GetDisplayName(object value, int index)
    {
        if (value is UndertaleNamedResource named && !string.IsNullOrEmpty(named.Name?.Content))
            return named.Name!.Content;
        return "#" + index;
    }

    private readonly record struct Item(string DisplayName, object Value);
}
