#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UndertaleModLib.Models;
using UndertaleModLib.Util;

namespace UndertaleModLib.Diff;

/// <summary>
/// Computes a structural SHA-256 fingerprint of a resource. Named resources referenced from
/// another resource are represented by their type and name, so resource list reordering and
/// cached IDs do not change a fingerprint. Unnamed resources (texture page items, embedded
/// textures/audio, strings, ...) are hashed by content.
/// </summary>
public sealed class AssetFingerprint
{
    private const int MaxDepth = 128;

    /// <summary>
    /// Resource types whose identity comes from their list index rather than from a name.
    /// Their members are hashed even though they carry an index-derived display name, so
    /// content changes become visible to resources that reference them.
    /// </summary>
    private static readonly HashSet<Type> PositionalResourceTypes =
    [
        typeof(UndertaleTexturePageItem),
        typeof(UndertaleEmbeddedTexture),
        typeof(UndertaleEmbeddedAudio),
    ];

    private readonly Dictionary<object, byte[]> _cache = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object> _path = new(ReferenceEqualityComparer.Instance);

    /// <summary>Computes the fingerprint of a top-level resource, including its own definition.</summary>
    public byte[] Compute(UndertaleResource resource)
    {
        return HashObject(resource, isRoot: true, depth: 0);
    }

    /// <summary>Computes the fingerprint of any top-level object (used for the general info object).</summary>
    public byte[] ComputeObject(object value)
    {
        return HashObject(value, isRoot: true, depth: 0);
    }

    private byte[] HashObject(object value, bool isRoot, int depth)
    {
        if (!isRoot && _cache.TryGetValue(value, out byte[]? cached))
            return cached;

        if (!_path.Add(value))
        {
            using IncrementalHash cycleHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendString(cycleHash, "cycle");
            return cycleHash.GetHashAndReset();
        }

        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendString(hash, value.GetType().FullName ?? value.GetType().Name);
            foreach (AssetMemberWalker.MemberValue member in AssetMemberWalker.ReadMembers(value))
            {
                AppendString(hash, member.Name);
                AppendValue(hash, member.Value, depth + 1);
            }

            byte[] result = hash.GetHashAndReset();
            if (!isRoot)
                _cache[value] = result;
            return result;
        }
        finally
        {
            _path.Remove(value);
        }
    }

    private void AppendValue(IncrementalHash hash, object? value, int depth)
    {
        if (depth > MaxDepth)
        {
            AppendString(hash, "<max-depth>");
            return;
        }

        switch (value)
        {
            case null:
                AppendString(hash, "<null>");
                return;

            case string text:
                AppendString(hash, text);
                return;

            case UndertaleString undertaleString:
                AppendString(hash, undertaleString.Content ?? "<null-string>");
                return;

            case byte[] bytes:
                AppendInt32(hash, bytes.Length);
                hash.AppendData(SHA256.HashData(bytes));
                return;

            case GMImage image:
                AppendString(hash, "image");
                AppendString(hash, image.Format.ToString());
                AppendInt32(hash, image.Width);
                AppendInt32(hash, image.Height);
                byte[] imageData = image.GetData() ?? [];
                AppendInt32(hash, imageData.Length);
                hash.AppendData(SHA256.HashData(imageData));
                return;

            case AssetMemberWalker.UnreadableMember unreadable:
                AppendString(hash, "<error>");
                AppendString(hash, unreadable.Message);
                return;

            case { } when IsResourceById(value):
                AppendValue(hash, GetResourceByIdValue(value), depth);
                return;

            case UndertaleNamedResource named:
                string? name = named.Name?.Content;
                if (!string.IsNullOrEmpty(name))
                {
                    // Positional internal resources (texture pages, embedded textures, embedded
                    // audio) are matched by index, not by name, and their content participates in
                    // the fingerprint of every resource that references them. Hash their members
                    // while skipping the index-derived name.
                    if (PositionalResourceTypes.Contains(named.GetType()))
                    {
                        AppendString(hash, "positional");
                        AppendString(hash, named.GetType().FullName ?? named.GetType().Name);
                        foreach (AssetMemberWalker.MemberValue member in AssetMemberWalker.ReadMembers(named))
                        {
                            if (member.Name == nameof(UndertaleNamedResource.Name))
                                continue;
                            AppendString(hash, member.Name);
                            AppendValue(hash, member.Value, depth + 1);
                        }
                        return;
                    }

                    AppendString(hash, "ref");
                    AppendString(hash, named.GetType().FullName ?? named.GetType().Name);
                    AppendString(hash, name);
                    return;
                }
                break; // Unnamed resource: hash its contents.

            case IList list:
                AppendString(hash, "list");
                AppendInt32(hash, list.Count);
                for (int i = 0; i < list.Count; i++)
                    AppendValue(hash, list[i], depth + 1);
                return;

            case UndertaleResource:
            case UndertaleObject:
                byte[] childHash = HashObject(value, isRoot: false, depth);
                AppendString(hash, "object");
                hash.AppendData(childHash);
                return;

            case Enum enumValue:
                AppendString(hash, "enum");
                AppendString(hash, enumValue.GetType().FullName ?? enumValue.GetType().Name);
                AppendString(hash, enumValue.ToString());
                return;

            case IFormattable formattable:
                AppendString(hash, "value");
                AppendString(hash, formattable.ToString(null, CultureInfo.InvariantCulture) ?? "");
                return;
        }

        // Plain objects and structs (including legacy model nested classes).
        if (IsScalar(value.GetType()))
        {
            AppendString(hash, "value");
            AppendString(hash, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
            return;
        }

        byte[] objectHash = HashObject(value, isRoot: false, depth);
        AppendString(hash, "object");
        hash.AppendData(objectHash);
    }

    private static object? GetResourceByIdValue(object value)
    {
        Type type = value.GetType();
        return type.GetProperty("Resource")?.GetValue(value);
    }

    private static bool IsResourceById(object value)
    {
        Type type = value.GetType();
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(UndertaleResourceById<,>);
    }

    private static bool IsScalar(Type type)
    {
        return type.IsPrimitive || type.IsEnum || type == typeof(decimal) ||
               type == typeof(DateTime) || type == typeof(TimeSpan) || type == typeof(Guid);
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        hash.AppendData(buffer);
    }
}
