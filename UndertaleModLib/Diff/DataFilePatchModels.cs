using System;
using System.Collections.Generic;

namespace UndertaleModLib.Diff;

/// <summary>
/// The manifest (<c>patch.json</c>) of a data file patch archive. Together with the archived
/// asset files it captures every difference between two data files, so that applying the patch
/// to the base file reconstructs the compared file.
/// </summary>
public sealed class DataFilePatchManifest
{
    /// <summary>The patch format identifier. Always <see cref="DataFilePatch.FormatName"/>.</summary>
    public string FormatName { get; set; } = DataFilePatch.FormatName;

    /// <summary>The patch format version. Always <see cref="DataFilePatch.FormatVersion"/>.</summary>
    public int FormatVersion { get; set; } = DataFilePatch.FormatVersion;

    /// <summary>Human-readable description of the base (target) data file.</summary>
    public string BaseLabel { get; set; } = "";

    /// <summary>Human-readable description of the compared (source) data file.</summary>
    public string CompareLabel { get; set; } = "";

    /// <summary>ISO 8601 UTC timestamp of when the patch was generated.</summary>
    public string GeneratedAt { get; set; } = "";

    /// <summary>Aggregate counts of the captured differences.</summary>
    public PatchSummary Summary { get; set; } = new();

    /// <summary>Object ID high-water marks of the compared data file, for consistent future insertions.</summary>
    public ulong BaseLastObj { get; set; }

    /// <summary>Tile ID high-water mark of the compared data file.</summary>
    public ulong BaseLastTile { get; set; }

    /// <summary>All captured differences, in comparison order.</summary>
    public List<PatchEntry> Entries { get; set; } = [];
}

/// <summary>Aggregate counts of a patch.</summary>
public sealed class PatchSummary
{
    public int AddedCount { get; set; }
    public int RemovedCount { get; set; }
    public int ChangedCount { get; set; }
}

/// <summary>
/// One resource difference captured in a patch.
/// </summary>
public sealed class PatchEntry
{
    /// <summary>The comparison category id, for example "Sprites".</summary>
    public string TypeId { get; set; } = "";

    /// <summary>The resource name.</summary>
    public string Name { get; set; } = "";

    /// <summary>How the resource differs: <see cref="AssetChangeKind"/> name.</summary>
    public string Change { get; set; } = "";

    /// <summary>What the patch captures for this entry: an asset, general info, or nothing.</summary>
    public PatchEntryKind Kind { get; set; } = PatchEntryKind.Asset;

    /// <summary>Whether the difference can be reproduced by applying the patch.</summary>
    public bool Supported { get; set; } = true;

    /// <summary>When not supported, the reason why.</summary>
    public string UnsupportedReason { get; set; }

    /// <summary>Serializable asset type name (for example "Sprite"), or <see langword="null"/>.</summary>
    public string AssetType { get; set; }

    /// <summary>Path (inside the patch) of the serialized asset JSON, relative to the archive root.</summary>
    public string AssetPath { get; set; }

    /// <summary>Exact texture snapshots backing this asset; empty for assets without textures.</summary>
    public List<PatchTextureRef> Textures { get; set; } = [];

    /// <summary>Paths (inside the patch) of verbatim collision mask blobs, in mask order.</summary>
    public List<string> Masks { get; set; } = [];

    /// <summary>Bytecode metadata for code entries, restored after recompilation.</summary>
    public PatchCodeMeta Code { get; set; }

    /// <summary>Base64 SHA-256-like fingerprint of the compared resource, for post-apply verification.</summary>
    public string CompareFingerprint { get; set; }

    /// <summary>Base64 fingerprint of the base resource at patch time, used to detect base drift. <see langword="null"/> for added resources.</summary>
    public string BaseFingerprint { get; set; }
}

/// <summary>What a patch entry captures.</summary>
public enum PatchEntryKind
{
    /// <summary>A named resource captured as a serialized project asset.</summary>
    Asset,

    /// <summary>The general info/options object, imported best-effort.</summary>
    GeneralInfo,

    /// <summary>A resource that only exists in the base file, deleted on request.</summary>
    Removed,

    /// <summary>A difference the patch cannot reproduce (reported as unsupported).</summary>
    Unsupported
}

/// <summary>A reference to one verbatim texture snapshot, keyed by frame index.</summary>
public sealed class PatchTextureRef
{
    /// <summary>Frame index within the owning asset (0 for single-texture assets).</summary>
    public int FrameIndex { get; set; }

    /// <summary>Path (inside the patch) of the verbatim <see cref="Models.UndertaleTexturePageItem"/> snapshot.</summary>
    public string PageItemPath { get; set; }

    /// <summary>Path (inside the patch) of the verbatim <see cref="Models.UndertaleEmbeddedTexture"/> snapshot.</summary>
    public string TexturePath { get; set; }
}

/// <summary>
/// Verbatim general info/options snapshot. The project's options asset covers the
/// user-editable fields; the extra members below are required so a patched file can
/// compare equal to the compared file.
/// </summary>
public sealed class PatchGeneralInfoSnapshot
{
    /// <summary>The project options snapshot (JSON) carrying the user-editable fields.</summary>
    public string Options { get; set; }

    /// <inheritdoc cref="Models.UndertaleGeneralInfo.GMS2RandomUID"/>
    public List<long> GMS2RandomUID { get; set; }
}

/// <summary>Bytecode metadata of a code entry, restored after GML recompilation.</summary>
public sealed class PatchCodeMeta
{
    public int LocalsCount { get; set; }
    public int ArgumentsCount { get; set; }
    public bool WeirdLocalFlag { get; set; }
}

/// <summary>Static helpers of the data file patch format.</summary>
public static class DataFilePatch
{
    /// <summary>The patch format identifier.</summary>
    public const string FormatName = "utmt-data-patch";

    /// <summary>The patch format version.</summary>
    public const int FormatVersion = 1;

    /// <summary>The canonical patch file extension (without dot).</summary>
    public const string FileExtension = "utmt-patch";
}
