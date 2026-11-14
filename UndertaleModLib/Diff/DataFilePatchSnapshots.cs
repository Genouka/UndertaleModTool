using System.Text.Json.Serialization;

namespace UndertaleModLib.Diff;

/// <summary>
/// Verbatim snapshot of an <see cref="Models.UndertaleEmbeddedTexture"/>, preserving the exact
/// container bytes of the embedded image so a patched data file compares equal to the source.
/// </summary>
public sealed class PatchTextureSnapshot
{
    /// <summary>Image container format name (<see cref="Util.GMImage.ImageFormat"/>).</summary>
    public string Format { get; set; } = "";

    /// <summary>Image width in pixels.</summary>
    public int Width { get; set; }

    /// <summary>Image height in pixels.</summary>
    public int Height { get; set; }

    /// <summary>Exact image container bytes, as stored in the compared data file.</summary>
    public string Data { get; set; } = "";

    /// <summary>Known uncompressed size of the BZip2 stream (BZ2 + QOI format only).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Bz2UncompressedLength { get; set; }

    /// <inheritdoc cref="Models.UndertaleEmbeddedTexture.Scaled"/>
    public uint Scaled { get; set; }

    /// <inheritdoc cref="Models.UndertaleEmbeddedTexture.GeneratedMips"/>
    public uint GeneratedMips { get; set; }

    /// <inheritdoc cref="Models.UndertaleEmbeddedTexture.TextureWidth"/>
    public int TextureWidth { get; set; }

    /// <inheritdoc cref="Models.UndertaleEmbeddedTexture.TextureHeight"/>
    public int TextureHeight { get; set; }

    /// <inheritdoc cref="Models.UndertaleEmbeddedTexture.IndexInGroup"/>
    public int IndexInGroup { get; set; }

    /// <summary>Name of the texture group info entry referenced by this texture, if any.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string TextureGroupName { get; set; }
}

/// <summary>
/// Verbatim snapshot of a <see cref="Models.UndertaleTexturePageItem"/>: the page reference is
/// stored as the index of its texture snapshot within the patch, and all coordinates are exact.
/// </summary>
public sealed class PatchPageItemSnapshot
{
    /// <summary>Index of the backing texture snapshot within the patch texture table.</summary>
    public int TextureIndex { get; set; }

    public ushort SourceX { get; set; }
    public ushort SourceY { get; set; }
    public ushort SourceWidth { get; set; }
    public ushort SourceHeight { get; set; }
    public ushort TargetX { get; set; }
    public ushort TargetY { get; set; }
    public ushort TargetWidth { get; set; }
    public ushort TargetHeight { get; set; }
    public ushort BoundingWidth { get; set; }
    public ushort BoundingHeight { get; set; }
}
