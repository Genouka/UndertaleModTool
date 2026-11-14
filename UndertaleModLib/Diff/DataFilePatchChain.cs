using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace UndertaleModLib.Diff;

/// <summary>One diff file with its parsed manifest, used for ordering and chained apply.</summary>
public sealed class PatchChainEntry
{
    public PatchChainEntry(string path, DataFilePatchManifest manifest)
    {
        Path = path;
        Manifest = manifest;
    }

    /// <summary>Full path of the diff file.</summary>
    public string Path { get; }

    /// <summary>File name of the diff file, used for alphabetical ordering.</summary>
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>The parsed manifest.</summary>
    public DataFilePatchManifest Manifest { get; }
}

/// <summary>
/// Applies several data file diffs in sequence. The default order chains diffs by MD5
/// (a diff's result MD5 matching the next diff's base MD5); unmatched diffs keep the
/// alphabetical file-name order.
/// </summary>
public static class DataFilePatchChain
{
    /// <summary>
    /// Reads and validates the manifest of one diff file.
    /// </summary>
    public static DataFilePatchManifest ReadManifest(string patchFilePath)
    {
        using ZipArchive archive = ZipFile.OpenRead(patchFilePath);
        ZipArchiveEntry? entry = archive.GetEntry("patch.json")
            ?? throw new InvalidDataException($"Diff file \"{patchFilePath}\" has no patch.json");
        using Stream stream = entry.Open();
        DataFilePatchManifest manifest = JsonSerializer.Deserialize<DataFilePatchManifest>(stream, JsonOptions)
            ?? throw new InvalidDataException($"Diff file \"{patchFilePath}\" has an empty manifest");
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
        return manifest;
    }

    /// <summary>Reads and validates the manifests of several diff files.</summary>
    public static List<PatchChainEntry> ReadManifests(IEnumerable<string> patchFilePaths)
    {
        return [.. patchFilePaths.Select(path => new PatchChainEntry(path, ReadManifest(path)))];
    }

    /// <summary>
    /// Orders diffs for sequential application. Chains are formed where a diff's recorded result
    /// MD5 equals another diff's recorded base MD5; chains start at diffs whose base MD5 is not
    /// produced by any of the selected diffs. Diffs that cannot be chained (missing MD5s,
    /// ambiguous links, cycles) keep the alphabetical file-name order, after the chains.
    /// </summary>
    public static List<PatchChainEntry> SortByChain(IEnumerable<PatchChainEntry> entries)
    {
        List<PatchChainEntry> alphabetical = [.. entries
            .OrderBy(e => e.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.FileName, StringComparer.Ordinal)];
        if (alphabetical.Count <= 1)
        {
            return alphabetical;
        }

        // Unique predecessor/successor lookups; duplicate MD5s make a link ambiguous.
        Dictionary<string, List<PatchChainEntry>> baseOwners = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<PatchChainEntry>> resultOwners = new(StringComparer.OrdinalIgnoreCase);
        foreach (PatchChainEntry entry in alphabetical)
        {
            if (entry.Manifest.BaseFileMd5 is { Length: > 0 } baseMd5)
            {
                GetList(baseOwners, baseMd5).Add(entry);
            }
            if (entry.Manifest.ResultFileMd5 is { Length: > 0 } resultMd5)
            {
                GetList(resultOwners, resultMd5).Add(entry);
            }
        }

        bool HasPredecessor(PatchChainEntry entry)
        {
            return entry.Manifest.BaseFileMd5 is { Length: > 0 } baseMd5 &&
                   resultOwners.TryGetValue(baseMd5, out List<PatchChainEntry>? owners) &&
                   owners.Any(owner => !ReferenceEquals(owner, entry));
        }

        PatchChainEntry? Next(PatchChainEntry entry)
        {
            if (entry.Manifest.ResultFileMd5 is not { Length: > 0 } resultMd5 ||
                !baseOwners.TryGetValue(resultMd5, out List<PatchChainEntry>? owners))
            {
                return null;
            }
            List<PatchChainEntry> candidates = owners.Where(owner => !ReferenceEquals(owner, entry)).ToList();
            return candidates.Count == 1 ? candidates[0] : null; // ambiguous: don't guess
        }

        // A diff participates in the MD5 chain when some other diff produces its base or
        // consumes its result. Everything else is "unmatched" and stays alphabetical.
        bool IsLinked(PatchChainEntry entry)
        {
            if (entry.Manifest.BaseFileMd5 is { Length: > 0 } baseMd5 &&
                resultOwners.TryGetValue(baseMd5, out List<PatchChainEntry>? producers) &&
                producers.Any(owner => !ReferenceEquals(owner, entry)))
            {
                return true;
            }
            return entry.Manifest.ResultFileMd5 is { Length: > 0 } resultMd5 &&
                   baseOwners.TryGetValue(resultMd5, out List<PatchChainEntry>? consumers) &&
                   consumers.Any(owner => !ReferenceEquals(owner, entry));
        }

        List<PatchChainEntry> ordered = [];
        HashSet<PatchChainEntry> visited = new();
        foreach (PatchChainEntry start in alphabetical.Where(entry => IsLinked(entry) && !HasPredecessor(entry)))
        {
            if (!visited.Add(start))
            {
                continue;
            }
            ordered.Add(start);
            PatchChainEntry? next = Next(start);
            while (next is not null && visited.Add(next))
            {
                ordered.Add(next);
                next = Next(next);
            }
        }

        // Unmatched, cyclic or ambiguous diffs: alphabetical order, after the chains.
        ordered.AddRange(alphabetical.Where(entry => !visited.Contains(entry)));
        return ordered;
    }

    /// <summary>
    /// Applies the given diffs to the data file in order and aggregates the results.
    /// The recorded base MD5 is verified against the base file only for the first diff; for
    /// later diffs the chain link between the previous result MD5 and the next base MD5 is
    /// checked instead and reported as a warning when broken.
    /// </summary>
    /// <param name="baseData">The data file the first diff is applied to; mutated in place.</param>
    /// <param name="orderedPatchPaths">Diff paths in application order.</param>
    /// <param name="options">Apply options; the file-based base MD5 check uses them for the first diff.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    public static DataFilePatchApplyResult ApplyMany(UndertaleData baseData, IReadOnlyList<string> orderedPatchPaths,
                                                     DataFilePatchApplyOptions? options = null,
                                                     IProgress<string>? progress = null,
                                                     CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseData);
        ArgumentNullException.ThrowIfNull(orderedPatchPaths);
        options ??= new DataFilePatchApplyOptions();
        if (orderedPatchPaths.Count == 0)
        {
            throw new ArgumentException("At least one diff is required", nameof(orderedPatchPaths));
        }

        DataFilePatchApplyResult aggregate = new();
        string? previousResultMd5 = null;
        string? previousName = null;

        for (int i = 0; i < orderedPatchPaths.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string patchPath = orderedPatchPaths[i];
            string name = Path.GetFileName(patchPath);
            DataFilePatchManifest manifest = ReadManifest(patchPath);

            if (i > 0 && manifest.BaseFileMd5 is { Length: > 0 } baseMd5)
            {
                if (previousResultMd5 is { Length: > 0 } resultMd5 &&
                    !string.Equals(baseMd5, resultMd5, StringComparison.OrdinalIgnoreCase))
                {
                    aggregate.Warnings.Add(
                        $"\"{name}\" expects a base file with MD5 {baseMd5}, but \"{previousName}\" produces {resultMd5}");
                }
                else if (previousResultMd5 is null)
                {
                    aggregate.Warnings.Add($"Cannot verify the chain link before \"{name}\" (no recorded MD5)");
                }
            }

            DataFilePatchApplyOptions perPatch = new()
            {
                DeleteRemoved = options.DeleteRemoved,
                MainThreadAction = options.MainThreadAction,
                BaseDataDirectory = options.BaseDataDirectory,
                OutputDirectory = options.OutputDirectory,
                // Only the first diff applies to the on-disk base file; later diffs apply to the
                // patched result, whose bytes differ, so the MD5 check cannot match.
                BaseFilePath = i == 0 ? options.BaseFilePath : null,
            };

            progress?.Report(name);
            DataFilePatchApplyResult result = new DataFilePatchApplier(baseData, options.MainThreadAction)
                .Apply(patchPath, perPatch, progress, cancellationToken);

            aggregate.ImportedCount += result.ImportedCount;
            aggregate.OverwrittenCount += result.OverwrittenCount;
            aggregate.DeletedCount += result.DeletedCount;
            aggregate.VerifiedCount += result.VerifiedCount;
            aggregate.MismatchedCount += result.MismatchedCount;
            aggregate.UnsupportedCount += result.UnsupportedCount;
            aggregate.Warnings.AddRange(result.Warnings.Select(w => $"[{name}] {w}"));
            aggregate.Errors.AddRange(result.Errors.Select(e => $"[{name}] {e}"));
            aggregate.Unsupported.AddRange(result.Unsupported.Select(u => $"[{name}] {u}"));
            aggregate.BaseDriftWarnings.AddRange(result.BaseDriftWarnings.Select(d => $"[{name}] {d}"));
            foreach (string file in result.ExtraOutputFiles)
            {
                if (!aggregate.ExtraOutputFiles.Contains(file, StringComparer.OrdinalIgnoreCase))
                {
                    aggregate.ExtraOutputFiles.Add(file);
                }
            }

            aggregate.StepSummaries.Add($"{name}: {result.GetSummary()}");
            previousResultMd5 = manifest.ResultFileMd5;
            previousName = name;
        }

        aggregate.ExpectedResultMd5 = previousResultMd5;
        return aggregate;
    }

    private static List<PatchChainEntry> GetList(Dictionary<string, List<PatchChainEntry>> map, string key)
    {
        if (!map.TryGetValue(key, out List<PatchChainEntry>? list))
        {
            list = [];
            map[key] = list;
        }
        return list;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };
}
