#nullable enable
using System;
using System.Collections.Generic;

namespace UndertaleModLib.Diff;

/// <summary>
/// How a resource differs between two data files.
/// </summary>
public enum AssetChangeKind
{
    Unchanged,
    Added,
    Removed,
    Changed
}

/// <summary>
/// A single resource (or the general info object) in the comparison result.
/// </summary>
public sealed class AssetDiffEntry
{
    /// <summary>The key used for matching, usually the resource name.</summary>
    public required string Name { get; init; }

    /// <summary>The category id this entry belongs to, for example "Sprites".</summary>
    public required string TypeId { get; init; }

    public AssetChangeKind Change { get; init; }

    /// <summary>The resource in the base data file, if it exists there.</summary>
    public object? BaseResource { get; init; }

    /// <summary>The resource in the compared data file, if it exists there.</summary>
    public object? CompareResource { get; init; }

    public bool IsCode => BaseResource is Models.UndertaleCode || CompareResource is Models.UndertaleCode;
}

/// <summary>
/// One resource category (Sprites, Rooms, Code, ...) in the comparison result.
/// </summary>
public sealed class AssetCategoryDiff
{
    public required string Id { get; init; }

    /// <summary>Human-readable title, not localized.</summary>
    public required string Title { get; init; }

    public required IReadOnlyList<AssetDiffEntry> Entries { get; init; }

    public int AddedCount => Count(AssetChangeKind.Added);
    public int RemovedCount => Count(AssetChangeKind.Removed);
    public int ChangedCount => Count(AssetChangeKind.Changed);
    public int UnchangedCount => Count(AssetChangeKind.Unchanged);

    private int Count(AssetChangeKind kind)
    {
        int count = 0;
        foreach (AssetDiffEntry entry in Entries)
        {
            if (entry.Change == kind)
                count++;
        }
        return count;
    }
}

/// <summary>
/// The full result of comparing two data files.
/// </summary>
public sealed class DataFileDiffResult
{
    public required IReadOnlyList<AssetCategoryDiff> Categories { get; init; }

    public int AddedCount => Sum(c => c.AddedCount);
    public int RemovedCount => Sum(c => c.RemovedCount);
    public int ChangedCount => Sum(c => c.ChangedCount);
    public int UnchangedCount => Sum(c => c.UnchangedCount);

    private int Sum(Func<AssetCategoryDiff, int> selector)
    {
        int count = 0;
        foreach (AssetCategoryDiff category in Categories)
            count += selector(category);
        return count;
    }
}

/// <summary>
/// One property-level difference between two resources.
/// </summary>
public sealed record PropertyDiff(string Path, string BaseValue, string CompareValue);

/// <summary>
/// Property-level differences for a single resource pair.
/// </summary>
public sealed class PropertyDiffResult
{
    public required IReadOnlyList<PropertyDiff> Differences { get; init; }

    /// <summary>Whether the walker stopped early because too many differences were found.</summary>
    public bool Truncated { get; init; }
}

/// <summary>
/// Decompiled (or disassembled) text difference for one code entry.
/// </summary>
public sealed class CodeDiffResult
{
    public required IReadOnlyList<PropertyDiff> MetadataDifferences { get; init; }

    /// <summary>Unified diff text, empty when the two entries are textually identical.</summary>
    public required string DiffText { get; init; }

    public bool BaseDecompileFailed { get; init; }
    public bool CompareDecompileFailed { get; init; }

    public bool HasTextDifferences => DiffText.Length != 0;
}

/// <summary>
/// Progress reported while loading and comparing data files.
/// </summary>
public readonly record struct DataFileDiffProgress(int Completed, int Total, string Message)
{
    public double Percent => Total <= 0 ? 0 : Math.Clamp(Completed * 100.0 / Total, 0, 100);
}
