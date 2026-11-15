#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using UndertaleModLib.Models;

namespace UndertaleModLib.Diff;

public sealed class DiffReportOptions
{
    public int MaxPropertiesPerResource { get; init; } = 200;
    public int MaxCodeDiffLength { get; init; } = 20_000;
    public bool IncludeCodeText { get; init; } = true;
}

/// <summary>
/// Serializes a comparison result as a stable, machine-readable JSON document. The report
/// contains resource presence, property-level differences, code metadata and (optionally)
/// the decompiled GML diff for each changed code entry.
/// </summary>
public static class DiffReportJson
{
    public const string FormatName = "utmt-data-diff";
    public const int FormatVersion = 1;

    public static string Serialize(DataFileDiffResult result,
                                   UndertaleData baseData,
                                   UndertaleData compareData,
                                   string baseLabel,
                                   string compareLabel,
                                   IEnumerable<AssetDiffEntry>? entries = null,
                                   DiffReportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(baseData);
        ArgumentNullException.ThrowIfNull(compareData);

        options ??= new DiffReportOptions();

        List<AssetDiffEntry> selected = (entries ?? result.Categories.SelectMany(c => c.Entries))
            .Where(e => e.Change != AssetChangeKind.Unchanged)
            .ToList();

        Dictionary<string, string> titles = result.Categories.ToDictionary(c => c.Id, c => c.Title, StringComparer.Ordinal);
        CodeDiffService codeDiffs = new(baseData, compareData);

        List<DiffReportCategory> categories = [];
        foreach (IGrouping<string, AssetDiffEntry> group in selected.GroupBy(e => e.TypeId))
        {
            List<DiffReportEntry> reportEntries = [];
            foreach (AssetDiffEntry entry in group)
                reportEntries.Add(BuildEntry(entry, codeDiffs, options));

            categories.Add(new DiffReportCategory
            {
                Id = group.Key,
                Title = titles.GetValueOrDefault(group.Key, group.Key),
                Entries = reportEntries,
            });
        }

        DiffReport report = new()
        {
            Format = FormatName,
            Version = FormatVersion,
            GeneratedAt = DateTimeOffset.UtcNow,
            Base = baseLabel,
            Compare = compareLabel,
            Summary = new DiffReportSummary
            {
                Added = selected.Count(e => e.Change == AssetChangeKind.Added),
                Removed = selected.Count(e => e.Change == AssetChangeKind.Removed),
                Changed = selected.Count(e => e.Change == AssetChangeKind.Changed),
            },
            Categories = categories,
        };

        return JsonSerializer.Serialize(report, SerializerOptions);
    }

    private static DiffReportEntry BuildEntry(AssetDiffEntry entry, CodeDiffService codeDiffs, DiffReportOptions options)
    {
        DiffReportEntry reportEntry = new()
        {
            Name = entry.Name,
            Change = entry.Change,
            MergeSupported = DataFileMerger.CanMerge(entry),
        };

        if (entry.Change != AssetChangeKind.Changed ||
            entry.BaseResource is null || entry.CompareResource is null)
        {
            return reportEntry;
        }

        if (entry.BaseResource is UndertaleCode baseCode &&
            entry.CompareResource is UndertaleCode compareCode)
        {
            try
            {
                CodeDiffResult diff = codeDiffs.Compare(baseCode, compareCode);
                string? text = options.IncludeCodeText ? diff.DiffText : null;
                bool truncated = false;
                if (text is not null && text.Length > options.MaxCodeDiffLength)
                {
                    text = text[..options.MaxCodeDiffLength];
                    truncated = true;
                }

                reportEntry.Code = new DiffReportCode
                {
                    Metadata = diff.MetadataDifferences
                        .Select(d => new DiffReportProperty(d.Path, d.BaseValue, d.CompareValue))
                        .ToList(),
                    DiffText = text,
                    DiffTextTruncated = truncated,
                    BaseDecompileFailed = diff.BaseDecompileFailed,
                    CompareDecompileFailed = diff.CompareDecompileFailed,
                };
            }
            catch (Exception e)
            {
                reportEntry.Error = e.Message;
            }

            return reportEntry;
        }

        try
        {
            PropertyDiffResult properties = AssetPropertyDiff.Compare(
                entry.BaseResource, entry.CompareResource, options.MaxPropertiesPerResource);
            reportEntry.Properties = properties.Differences
                .Select(d => new DiffReportProperty(d.Path, d.BaseValue, d.CompareValue))
                .ToList();
            reportEntry.PropertiesTruncated = properties.Truncated;
        }
        catch (Exception e)
        {
            reportEntry.Error = e.Message;
        }

        return reportEntry;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed class DiffReport
    {
        public string Format { get; init; } = "";
        public int Version { get; init; }
        public DateTimeOffset GeneratedAt { get; init; }
        public string Base { get; init; } = "";
        public string Compare { get; init; } = "";
        public DiffReportSummary Summary { get; init; } = new();
        public List<DiffReportCategory> Categories { get; init; } = [];
    }

    private sealed class DiffReportSummary
    {
        public int Added { get; init; }
        public int Removed { get; init; }
        public int Changed { get; init; }
    }

    private sealed class DiffReportCategory
    {
        public string Id { get; init; } = "";
        public string Title { get; init; } = "";
        public List<DiffReportEntry> Entries { get; init; } = [];
    }

    private sealed class DiffReportEntry
    {
        public string Name { get; set; } = "";
        public AssetChangeKind Change { get; set; }
        public bool MergeSupported { get; set; }
        public List<DiffReportProperty>? Properties { get; set; }
        public bool PropertiesTruncated { get; set; }
        public DiffReportCode? Code { get; set; }
        public string? Error { get; set; }
    }

    private sealed record DiffReportProperty(string Path, string BaseValue, string CompareValue);

    private sealed class DiffReportCode
    {
        public List<DiffReportProperty> Metadata { get; init; } = [];
        public string? DiffText { get; init; }
        public bool DiffTextTruncated { get; init; }
        public bool BaseDecompileFailed { get; init; }
        public bool CompareDecompileFailed { get; init; }
    }
}
