using System.Collections.Generic;
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

    /// <summary>Relative path of the external texture file (2022.9+), when the texture is external.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ExternalPath { get; set; }

    /// <summary>Texture group directory, written into the group info for external textures.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string TextureGroupDirectory { get; set; }

    /// <summary>Texture group file extension, written into the group info for external textures.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string TextureGroupExtension { get; set; }
}

/// <summary>
/// A sound whose audio is stored in an external audio group file. The sound metadata is carried
/// by the project's serializable sound (including its exported audio file); the group file's
/// audio bytes are captured separately so the output group file can be patched.
/// </summary>
public sealed class PatchGroupSoundSnapshot
{
    /// <summary>Path (inside the patch) of the serializable sound JSON.</summary>
    public string SoundJsonPath { get; set; } = "";

    /// <summary>Path (inside the patch) of the group audio file exported for this sound.</summary>
    public string AudioPath { get; set; } = "";

    /// <summary>The sound's audio index inside the compared file's audio group.</summary>
    public int AudioId { get; set; }

    /// <summary>Audio group index in the compared data file.</summary>
    public int GroupIndex { get; set; }

    /// <summary>Audio group name, used to resolve the group index in the base file.</summary>
    public string GroupName { get; set; } = "";

    /// <summary>Path (inside the patch) of the compared file's whole audio group file, used when
    /// the base file has no such group file yet.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string GroupFilePath { get; set; }
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

/// <summary>
/// Verbatim state of one <see cref="Models.UndertaleInstruction"/>. The packed opcode word and
/// primitive value are stored raw; references are stored by name/content and re-linked on apply.
/// Remaining-occurrence offsets are derived by the writer and are not stored.
/// </summary>
public sealed class PatchInstructionSnapshot
{
    /// <summary>The opcode (<see cref="Models.UndertaleInstruction.Opcode"/> as a number).</summary>
    public byte Kind { get; set; }

    /// <summary>Raw packed opcode/type/argument word.</summary>
    public uint FirstWord { get; set; }

    /// <summary>Raw packed primitive value.</summary>
    public int Primitive { get; set; }

    /// <summary>Referenced string content, when the instruction pushes a string.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ValueString { get; set; }

    /// <summary>Referenced variable name, when the instruction references a variable.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ValueVariable { get; set; }

    /// <summary>Referenced function name, when the instruction calls a function.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ValueFunction { get; set; }
}

/// <summary>
/// Verbatim state of an anonymous sub-code entry: the instructions of its segment inside the
/// parent's blob, and the metadata restored after splicing.
/// </summary>
public sealed class PatchSubCodeSnapshot
{
    /// <summary>Name of the root code entry that owns the blob.</summary>
    public string ParentEntry { get; set; } = "";

    /// <summary>Byte offset of this entry's segment inside the parent blob.</summary>
    public uint Offset { get; set; }

    public int LocalsCount { get; set; }
    public int ArgumentsCount { get; set; }
    public bool WeirdLocalFlag { get; set; }

    public List<PatchInstructionSnapshot> Instructions { get; set; } = [];
}

/// <summary>
/// Spine-specific sprite data, which the project serialization does not cover.
/// </summary>
public sealed class PatchSpineSpriteSnapshot
{
    public int SpineVersion { get; set; }
    public string SpineJSON { get; set; }
    public string SpineAtlas { get; set; }
    public List<PatchSpineTextureEntry> SpineTextures { get; set; } = [];
}

/// <summary>One Spine atlas texture entry.</summary>
public sealed class PatchSpineTextureEntry
{
    public int PageWidth { get; set; }
    public int PageHeight { get; set; }
    public string TexBlob { get; set; }
    public int TextureEntryLength { get; set; }
}
