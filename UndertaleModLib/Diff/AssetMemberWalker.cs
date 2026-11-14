#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UndertaleModLib.Models;

namespace UndertaleModLib.Diff;

/// <summary>
/// Reads the serialized state of an UndertaleModLib object in a deterministic order.
/// Only public, writable properties and public mutable fields are considered; computed
/// read-only properties are ignored because they usually mirror other state or require context.
/// </summary>
internal static class AssetMemberWalker
{
    private static readonly ConcurrentDictionary<Type, MemberAccessor[]> Cache = new();

    /// <summary>
    /// State that is not part of the data file, or that is derived from resource list order.
    /// </summary>
    private static readonly HashSet<(Type, string)> SkipMembers =
    [
        (typeof(UndertaleSound), nameof(UndertaleSound.AudioID)),
        (typeof(UndertaleSound), nameof(UndertaleSound.GroupID)),
        (typeof(UndertaleEmbeddedTexture), nameof(UndertaleEmbeddedTexture.TextureExternal)),
        (typeof(UndertaleEmbeddedTexture), nameof(UndertaleEmbeddedTexture.TextureLoaded)),
    ];

    internal readonly record struct MemberValue(string Name, object? Value);

    internal static IEnumerable<MemberValue> ReadMembers(object value)
    {
        Type type = value.GetType();
        foreach (MemberAccessor accessor in GetMembers(type))
        {
            object? memberValue;
            try
            {
                memberValue = accessor.Get(value);
            }
            catch (Exception e)
            {
                memberValue = new UnreadableMember(e.Message);
            }

            yield return new MemberValue(accessor.Name, memberValue);
        }
    }

    private static MemberAccessor[] GetMembers(Type type)
    {
        return Cache.GetOrAdd(type, static t =>
        {
            List<MemberAccessor> members = [];

            foreach (PropertyInfo property in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || !property.CanWrite)
                    continue;
                if (property.GetMethod is not { IsPublic: true } getter ||
                    property.SetMethod is not { IsPublic: true })
                {
                    continue;
                }
                if (getter.GetParameters().Length != 0)
                    continue;
                if (Attribute.IsDefined(property, typeof(ObsoleteAttribute)))
                    continue;
                if (SkipMembers.Contains((property.DeclaringType ?? t, property.Name)))
                    continue;

                members.Add(new MemberAccessor(property.Name, property.GetValue));
            }

            foreach (FieldInfo field in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.IsStatic || field.IsInitOnly || field.IsLiteral)
                    continue;
                if (typeof(Delegate).IsAssignableFrom(field.FieldType))
                    continue;
                if (Attribute.IsDefined(field, typeof(ObsoleteAttribute)))
                    continue;
                if (SkipMembers.Contains((field.DeclaringType ?? t, field.Name)))
                    continue;

                FieldInfo captured = field;
                members.Add(new MemberAccessor(field.Name, captured.GetValue));
            }

            return members
                .OrderBy(m => m.Name, StringComparer.Ordinal)
                .ToArray();
        });
    }

    private sealed record MemberAccessor(string Name, Func<object, object?> Get);

    /// <summary>
    /// Marker used when a property getter throws; its message is included in fingerprints
    /// so a broken member still compares consistently.
    /// </summary>
    internal sealed record UnreadableMember(string Message);
}
