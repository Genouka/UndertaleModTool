#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using UndertaleModLib.Models;

namespace UndertaleModLib.Diff;

/// <summary>
/// Thrown when a resource cannot be captured or restored by the generic snapshot codec,
/// because a member (or resource reference) falls outside the supported value model.
/// </summary>
public sealed class PatchSnapshotException : Exception
{
    public PatchSnapshotException(string message) : base(message)
    {
    }

    public PatchSnapshotException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// Generic, whitelist-based capture and restore of resource state for the patch engine.
/// Values are encoded as a JSON tree with explicit tags, and the codec refuses to encode
/// or decode anything outside the supported value model instead of guessing, so a patch
/// never silently corrupts a resource.
/// </summary>
internal static class PatchSnapshotCodec
{
    /// <summary>Types that may appear as nested objects inside a snapshot.</summary>
    private static readonly HashSet<Type> NestedObjectTypes =
    [
        typeof(UndertaleTimeline.UndertaleTimelineMoment),
        typeof(UndertaleGameObject.Event),
        typeof(UndertaleGameObject.EventAction),
        typeof(UndertaleExtensionFile),
        typeof(UndertaleExtensionFunction),
        typeof(UndertaleExtensionFunctionArg),
        typeof(UndertaleExtensionOption),
        typeof(UndertaleGameObject.UndertalePhysicsVertex),
        typeof(UndertaleRoom.EffectProperty),
        typeof(UndertaleUINode),
        typeof(UndertaleUILayer),
        typeof(UndertaleUIFlexPanel),
        typeof(UndertaleUIGameObject),
        typeof(UndertaleUISequenceInstance),
        typeof(UndertaleUISpriteInstance),
        typeof(UndertaleUITextItemInstance),
        typeof(UndertaleUIEffectLayer),
        typeof(UndertaleRoom.GameObject),
        typeof(UndertaleRoom.SpriteInstance),
        typeof(UndertaleRoom.SequenceInstance),
        typeof(UndertaleRoom.TextItemInstance),
    ];

    /// <summary>Named resource types that are stored as references (resolved by name on restore).</summary>
    private static readonly Dictionary<Type, Func<UndertaleData, IList?>> ReferenceLists = new()
    {
        [typeof(UndertaleCode)] = d => d.Code,
        [typeof(UndertaleGameObject)] = d => d.GameObjects,
        [typeof(UndertaleSprite)] = d => d.Sprites,
        [typeof(UndertaleBackground)] = d => d.Backgrounds,
        [typeof(UndertaleSound)] = d => d.Sounds,
        [typeof(UndertalePath)] = d => d.Paths,
        [typeof(UndertaleScript)] = d => d.Scripts,
        [typeof(UndertaleShader)] = d => d.Shaders,
        [typeof(UndertaleFont)] = d => d.Fonts,
        [typeof(UndertaleTimeline)] = d => d.Timelines,
        [typeof(UndertaleRoom)] = d => d.Rooms,
        [typeof(UndertaleExtension)] = d => d.Extensions,
        [typeof(UndertaleSequence)] = d => d.Sequences,
        [typeof(UndertaleAnimationCurve)] = d => d.AnimationCurves,
        [typeof(UndertaleParticleSystem)] = d => d.ParticleSystems,
        [typeof(UndertaleParticleSystemEmitter)] = d => d.ParticleSystemEmitters,
        [typeof(UndertaleFilterEffect)] = d => d.FilterEffects,
        [typeof(UndertaleUIRootNode)] = d => d.UINodes,
        [typeof(UndertaleAudioGroup)] = d => d.AudioGroups,
    };

    /// <summary>
    /// Encodes the walked state of a resource as a snapshot tree.
    /// </summary>
    /// <param name="data">The data file the resource belongs to (unused for encoding, reserved for context).</param>
    /// <param name="root">The resource to capture.</param>
    public static JsonNode Encode(UndertaleData data, object root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root is UndertaleObject && NestedObjectTypes.Contains(root.GetType()))
        {
            return EncodeObject(root);
        }
        // Snapshot roots are always captured as full objects, even when they are named
        // resources (references are only used for members pointing at other resources).
        return EncodeObject(root);
    }

    private static JsonNode? EncodeValue(object? value, bool isMember)
    {
        switch (value)
        {
            case null:
                return null;

            case UndertaleString text:
                return new JsonObject { ["s"] = text.Content };

            case byte[] bytes:
                return new JsonObject { ["b64"] = Convert.ToBase64String(bytes) };

            case Guid guid:
                return new JsonObject { ["guid"] = guid.ToString("D") };

            case UndertaleFlexValue flex:
                return new JsonObject { ["flex"] = new JsonArray(flex.Value, (int)flex.Unit) };

            case string text:
                return JsonValue.Create(text);

            case bool boolean:
                return JsonValue.Create(boolean);

            case Enum enumeration:
                return JsonValue.Create(Convert.ToUInt64(Convert.ChangeType(enumeration, Enum.GetUnderlyingType(enumeration.GetType()))));

            case byte or sbyte or short or ushort or int or uint or long or ulong:
                return JsonValue.Create(Convert.ToInt64(value));

            case float single:
                return JsonValue.Create(single);

            case double number:
                return JsonValue.Create(number);

            // Nested object types take precedence: several of them are also named resources
            // (extension options, UI layers/panels) but are part of the owning object's state.
            case UndertaleObject when NestedObjectTypes.Contains(value.GetType()):
                return EncodeObject(value);

            case UndertaleNamedResource named when IsReferenceable(value):
                return EncodeReference(named);

            case UndertaleNamedResource:
                throw new PatchSnapshotException($"Named resource type {value.GetType().FullName} is not in the snapshot reference whitelist");

            case AssetMemberWalker.UnreadableMember unreadable:
                throw new PatchSnapshotException($"Member could not be read: {unreadable.Message}");

            case IList list:
                return EncodeList(list);

            default:
                throw new PatchSnapshotException($"Type {value.GetType().FullName} is not supported by the snapshot codec");
        }
    }

    private static bool IsReferenceable(object value)
    {
        return ReferenceLists.ContainsKey(value.GetType());
    }

    private static JsonNode EncodeReference(UndertaleNamedResource named)
    {
        string? name = named.Name?.Content;
        if (string.IsNullOrEmpty(name))
        {
            throw new PatchSnapshotException($"Referenced {named.GetType().Name} has no name to reference it by");
        }
        return new JsonObject { ["ref"] = (named.GetType().FullName ?? named.GetType().Name) + ":" + name };
    }

    private static JsonNode EncodeList(IList list)
    {
        JsonArray array = [];
        foreach (object? item in list)
        {
            array.Add(EncodeValue(UnwrapReference(item), isMember: true));
        }
        return array;
    }

    /// <summary>
    /// Converts an <c>UndertaleResourceById</c> wrapper to the resource it points to, so
    /// reference lists are snapshotted by name (matching the fingerprint normalization).
    /// </summary>
    private static object? UnwrapReference(object? value)
    {
        if (value is null)
        {
            return null;
        }
        Type type = value.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(UndertaleResourceById<,>))
        {
            PropertyInfo? resource = type.GetProperty("Resource");
            return resource?.GetValue(value);
        }
        return value;
    }

    private static JsonNode EncodeObject(object value)
    {
        JsonObject members = [];
        foreach (AssetMemberWalker.MemberValue member in AssetMemberWalker.ReadMembers(value))
        {
            members[member.Name] = EncodeValue(UnwrapReference(member.Value), isMember: true);
        }
        return new JsonObject
        {
            ["o"] = value.GetType().FullName ?? value.GetType().Name,
            ["m"] = members,
        };
    }

    /// <summary>
    /// Restores the snapshot tree onto an existing target object (or into an existing list
    /// element). Members present in the snapshot are overwritten; nothing else is touched.
    /// </summary>
    public static void DecodeInto(UndertaleData data, object target, JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (node is not JsonObject obj || obj["m"] is not JsonObject members)
        {
            throw new PatchSnapshotException("Snapshot node is not an object with members");
        }
        DecodeMembers(data, target, members);
    }

    private static void DecodeMembers(UndertaleData data, object target, JsonObject members)
    {
        // Apply "Name" last: forwarding properties (for example UndertaleUIRootNode.Name)
        // require their inner node to exist first.
        List<KeyValuePair<string, JsonNode?>> ordered = [];
        KeyValuePair<string, JsonNode?>? nameMember = null;
        foreach (KeyValuePair<string, JsonNode?> pair in members)
        {
            if (pair.Key == nameof(UndertaleNamedResource.Name))
            {
                nameMember = pair;
                continue;
            }
            ordered.Add(pair);
        }
        if (nameMember is not null)
        {
            ordered.Add(nameMember.Value);
        }

        foreach (KeyValuePair<string, JsonNode?> pair in ordered)
        {
            Type memberType = GetMemberType(target.GetType(), pair.Key);
            object? value = DecodeValue(data, pair.Value, memberType);
            AssetMemberWalker.WriteMember(target, pair.Key, value);
        }
    }

    private static Type GetMemberType(Type type, string name)
    {
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.Name == name && property.CanWrite)
            {
                return property.PropertyType;
            }
        }
        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.Name == name && !field.IsInitOnly)
            {
                return field.FieldType;
            }
        }
        throw new MissingMemberException(type.FullName, name);
    }

    private static object? DecodeValue(UndertaleData data, JsonNode? node, Type targetType)
    {
        Type? resourceType = GetResourceElementType(targetType);

        if (node is null)
        {
            return null;
        }

        if (node is JsonObject obj)
        {
            if (obj["s"] is JsonValue stringNode && targetType == typeof(UndertaleString))
            {
                return data.Strings.MakeString((string?)stringNode ?? "");
            }
            if (obj["b64"] is JsonValue base64 && targetType == typeof(byte[]))
            {
                return Convert.FromBase64String((string?)base64 ?? "");
            }
            if (obj["guid"] is JsonValue guidNode && targetType == typeof(Guid))
            {
                return Guid.Parse((string?)guidNode ?? Guid.Empty.ToString("D"));
            }
            if (obj["flex"] is JsonArray flex && targetType == typeof(UndertaleFlexValue))
            {
                float value = (float?)flex[0] ?? 0;
                UndertaleFlexValue.UnitKind unit = (UndertaleFlexValue.UnitKind)((int?)flex[1] ?? 0);
                return new UndertaleFlexValue(value, unit);
            }
            if (obj["ref"] is JsonValue referenceNode)
            {
                string? reference = (string?)referenceNode;
                if (reference is null)
                {
                    return null;
                }
                object? resolved = ResolveReference(data, reference);
                return WrapReference(targetType, resolved);
            }
            if (obj["o"] is JsonValue typeNode && obj["m"] is JsonObject members)
            {
                Type concrete = ResolveType((string?)typeNode ?? "");
                object instance = Activator.CreateInstance(concrete)
                    ?? throw new PatchSnapshotException($"Cannot instantiate {concrete.FullName}");
                DecodeMembers(data, instance, members);
                return instance;
            }
            throw new PatchSnapshotException($"Snapshot object cannot be decoded into {targetType.FullName}");
        }

        if (node is JsonArray array)
        {
            return DecodeList(data, array, targetType);
        }

        JsonValue scalar = (JsonValue)node;
        return DecodeScalar(scalar, targetType);
    }

    private static object DecodeList(UndertaleData data, JsonArray array, Type targetType)
    {
        Type listType = targetType;
        if (!typeof(IList).IsAssignableFrom(listType))
        {
            throw new PatchSnapshotException($"Snapshot list cannot be decoded into {targetType.FullName}");
        }

        object list = Activator.CreateInstance(listType)
            ?? throw new PatchSnapshotException($"Cannot instantiate list type {listType.FullName}");
        Type elementType = GetListElementType(listType);

        foreach (JsonNode? item in array)
        {
            object? value = DecodeValue(data, item, elementType);
            ((IList)list).Add(value);
        }
        return list;
    }

    private static Type GetListElementType(Type listType)
    {
        if (listType.IsGenericType)
        {
            Type[] arguments = listType.GetGenericArguments();
            if (arguments.Length > 0)
            {
                return arguments[0];
            }
        }
        foreach (Type implemented in listType.GetInterfaces())
        {
            if (implemented.IsGenericType && implemented.GetGenericTypeDefinition() == typeof(IList<>))
            {
                return implemented.GetGenericArguments()[0];
            }
        }
        throw new PatchSnapshotException($"Cannot determine element type of {listType.FullName}");
    }

    /// <summary>
    /// Returns the resource type of an <c>UndertaleResourceById&lt;T, ChunkT&gt;</c> wrapper, or null.
    /// </summary>
    private static Type? GetResourceElementType(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(UndertaleResourceById<,>))
        {
            return type.GetGenericArguments()[0];
        }
        return null;
    }

    private static object WrapReference(Type targetType, object? resolved)
    {
        Type? elementType = GetResourceElementType(targetType);
        if (elementType is null)
        {
            return resolved!;
        }

        object wrapper = Activator.CreateInstance(targetType)
            ?? throw new PatchSnapshotException($"Cannot instantiate {targetType.FullName}");
        targetType.GetProperty("Resource")!.SetValue(wrapper, resolved);
        return wrapper;
    }

    private static object? ResolveReference(UndertaleData data, string reference)
    {
        int separator = reference.IndexOf(':');
        if (separator <= 0)
        {
            throw new PatchSnapshotException($"Malformed snapshot reference \"{reference}\"");
        }

        Type type = ResolveType(reference[..separator]);
        if (!ReferenceLists.TryGetValue(type, out Func<UndertaleData, IList?>? listGetter))
        {
            throw new PatchSnapshotException($"Snapshot reference type {type.FullName} has no resource list");
        }

        string name = reference[(separator + 1)..];
        IList? list = listGetter(data);
        if (list is not null)
        {
            foreach (object? item in list)
            {
                if (item is UndertaleNamedResource named && named.Name?.Content == name)
                {
                    return named;
                }
            }
        }
        throw new PatchSnapshotException($"Referenced {type.Name} \"{name}\" was not found in the data file");
    }

    private static Type ResolveType(string name)
    {
        Type? type = typeof(PatchSnapshotCodec).Assembly.GetType(name);
        if (type is null)
        {
            throw new PatchSnapshotException($"Unknown snapshot type \"{name}\"");
        }
        return type;
    }

    private static object DecodeScalar(JsonValue value, Type targetType)
    {
        Type effective = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (effective == typeof(bool))
        {
            return value.GetValue<bool>();
        }
        if (effective == typeof(float))
        {
            return value.GetValue<float>();
        }
        if (effective == typeof(double))
        {
            return value.GetValue<double>();
        }
        if (effective == typeof(string))
        {
            return value.GetValue<string>();
        }
        if (effective.IsEnum)
        {
            object numeric = DecodeScalar(value, Enum.GetUnderlyingType(effective));
            return Enum.ToObject(effective, numeric);
        }
        if (effective == typeof(byte))
        {
            return checked((byte)value.GetValue<long>());
        }
        if (effective == typeof(sbyte))
        {
            return checked((sbyte)value.GetValue<long>());
        }
        if (effective == typeof(short))
        {
            return checked((short)value.GetValue<long>());
        }
        if (effective == typeof(ushort))
        {
            return checked((ushort)value.GetValue<long>());
        }
        if (effective == typeof(int))
        {
            return checked((int)value.GetValue<long>());
        }
        if (effective == typeof(uint))
        {
            return checked((uint)value.GetValue<long>());
        }
        if (effective == typeof(long))
        {
            return value.GetValue<long>();
        }
        if (effective == typeof(ulong))
        {
            return checked((ulong)value.GetValue<long>());
        }
        throw new PatchSnapshotException($"Scalar value cannot be decoded into {targetType.FullName}");
    }
}
