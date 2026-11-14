#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UndertaleModLib.Models;
using UndertaleModLib.Util;

namespace UndertaleModLib.Diff;

/// <summary>
/// Produces property-level differences for two matching resources.
/// </summary>
public static class AssetPropertyDiff
{
    private const int DefaultMaxDifferences = 5000;
    private const int MaxDepth = 128;

    public static PropertyDiffResult Compare(object? baseValue, object? compareValue, int maxDifferences = DefaultMaxDifferences)
    {
        Walker walker = new(maxDifferences);
        walker.CompareValue("", baseValue, compareValue, 0);
        return new PropertyDiffResult
        {
            Differences = walker.Differences,
            Truncated = walker.Truncated
        };
    }

    private sealed class Walker
    {
        private readonly int _maxDifferences;
        private readonly HashSet<(object Base, object Compare)> _seen = new(new ReferencePairComparer());

        public List<PropertyDiff> Differences { get; } = [];
        public bool Truncated { get; private set; }

        public Walker(int maxDifferences)
        {
            _maxDifferences = Math.Max(1, maxDifferences);
        }

        public void CompareValue(string path, object? baseValue, object? compareValue, int depth)
        {
            if (Truncated)
                return;
            if (depth > MaxDepth)
            {
                Add(path, "<max-depth>", "<max-depth>");
                return;
            }

            baseValue = Normalize(baseValue);
            compareValue = Normalize(compareValue);

            switch (baseValue, compareValue)
            {
                case (null, null):
                    return;
                case (null, _):
                case (_, null):
                    Add(path, Format(baseValue), Format(compareValue));
                    return;

                case (UndertaleString baseString, UndertaleString compareString):
                    if (!string.Equals(baseString.Content, compareString.Content, StringComparison.Ordinal))
                        Add(path, Format(baseString), Format(compareString));
                    return;

                case (string baseText, string compareText):
                    if (!string.Equals(baseText, compareText, StringComparison.Ordinal))
                        Add(path, Format(baseText), Format(compareText));
                    return;

                case (byte[] baseBytes, byte[] compareBytes):
                    if (!BytesEqual(baseBytes, compareBytes))
                        Add(path, Format(baseBytes), Format(compareBytes));
                    return;

                case (GMImage baseImage, GMImage compareImage):
                    if (!ImagesEqual(baseImage, compareImage))
                        Add(path, Format(baseImage), Format(compareImage));
                    return;

                case (Enum baseEnum, Enum compareEnum):
                    if (baseEnum.GetType() != compareEnum.GetType() ||
                        !string.Equals(baseEnum.ToString(), compareEnum.ToString(), StringComparison.Ordinal))
                    {
                        Add(path, Format(baseEnum), Format(compareEnum));
                    }
                    return;

                case (IList baseList, IList compareList):
                    CompareLists(path, baseList, compareList, depth);
                    return;

                case (IFormattable baseFormattable, IFormattable compareFormattable)
                    when IsScalar(baseValue.GetType()) && baseValue.GetType() == compareValue.GetType():
                    if (!string.Equals(Format(baseFormattable), Format(compareFormattable), StringComparison.Ordinal))
                        Add(path, Format(baseValue), Format(compareValue));
                    return;

                case (_, _) when IsScalar(baseValue.GetType()) && baseValue.GetType() == compareValue.GetType():
                    if (!Equals(baseValue, compareValue))
                        Add(path, Format(baseValue), Format(compareValue));
                    return;
            }

            if (depth > 0 &&
                IsNamedReference(baseValue, out string? baseReference) &&
                IsNamedReference(compareValue, out string? compareReference))
            {
                if (!string.Equals(baseReference, compareReference, StringComparison.Ordinal))
                    Add(path, baseReference, compareReference);
                return;
            }

            if (baseValue.GetType() != compareValue.GetType())
            {
                Add(path, Format(baseValue), Format(compareValue));
                return;
            }

            if (!baseValue.GetType().IsValueType)
            {
                if (!_seen.Add((baseValue, compareValue)))
                    return;
            }

            Dictionary<string, object?> baseMembers = AssetMemberWalker.ReadMembers(baseValue)
                .ToDictionary(m => m.Name, m => m.Value, StringComparer.Ordinal);
            Dictionary<string, object?> compareMembers = AssetMemberWalker.ReadMembers(compareValue)
                .ToDictionary(m => m.Name, m => m.Value, StringComparer.Ordinal);

            foreach ((string name, object? memberBaseValue) in baseMembers)
            {
                if (Truncated)
                    return;

                string memberPath = JoinPath(path, name);
                if (!compareMembers.TryGetValue(name, out object? memberCompareValue))
                    Add(memberPath, Format(memberBaseValue), "<missing>");
                else
                    CompareValue(memberPath, memberBaseValue, memberCompareValue, depth + 1);
            }

            foreach ((string name, object? memberCompareValue) in compareMembers)
            {
                if (Truncated)
                    return;
                if (!baseMembers.ContainsKey(name))
                    Add(JoinPath(path, name), "<missing>", Format(memberCompareValue));
            }
        }

        private void CompareLists(string path, IList baseList, IList compareList, int depth)
        {
            if (baseList.Count != compareList.Count)
            {
                string countPath = path.Length == 0 ? "Count" : path + ".Count";
                Add(countPath, baseList.Count.ToString(CultureInfo.InvariantCulture),
                    compareList.Count.ToString(CultureInfo.InvariantCulture));
            }

            int shared = Math.Min(baseList.Count, compareList.Count);
            for (int i = 0; i < shared && !Truncated; i++)
                CompareValue($"{path}[{i}]", baseList[i], compareList[i], depth + 1);

            for (int i = shared; i < baseList.Count && !Truncated; i++)
                Add($"{path}[{i}]", Format(baseList[i]), "<missing>");
            for (int i = shared; i < compareList.Count && !Truncated; i++)
                Add($"{path}[{i}]", "<missing>", Format(compareList[i]));
        }

        private void Add(string path, string? baseValue, string? compareValue)
        {
            if (Truncated)
                return;
            if (Differences.Count >= _maxDifferences)
            {
                Truncated = true;
                return;
            }

            Differences.Add(new PropertyDiff(
                path.Length == 0 ? "<value>" : path,
                baseValue ?? "<null>",
                compareValue ?? "<null>"));
        }
    }

    private static object? Normalize(object? value)
    {
        if (value is null)
            return null;
        Type type = value.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(UndertaleResourceById<,>))
            return type.GetProperty("Resource")?.GetValue(value);
        return value;
    }

    private static bool IsNamedReference(object value, out string reference)
    {
        if (value is UndertaleNamedResource named && !string.IsNullOrEmpty(named.Name?.Content))
        {
            reference = $"{named.GetType().Name}:{named.Name!.Content}";
            return true;
        }

        reference = "";
        return false;
    }

    private static bool IsScalar(Type type)
    {
        return type.IsPrimitive || type.IsEnum || type == typeof(decimal) ||
               type == typeof(DateTime) || type == typeof(TimeSpan) || type == typeof(Guid);
    }

    private static bool BytesEqual(byte[] a, byte[] b)
    {
        return a.Length == b.Length && a.AsSpan().SequenceEqual(b);
    }

    private static bool ImagesEqual(GMImage a, GMImage b)
    {
        if (a.Format != b.Format || a.Width != b.Width || a.Height != b.Height)
            return false;
        return BytesEqual(a.GetData() ?? [], b.GetData() ?? []);
    }

    private static string Format(object? value)
    {
        switch (value)
        {
            case null:
                return "<missing>";
            case UndertaleString text:
                return Quote(text.Content);
            case string text:
                return Quote(text);
            case byte[] bytes:
                return $"{bytes.Length} bytes (sha256:{ShortHash(bytes)})";
            case GMImage image:
                byte[] imageData = image.GetData() ?? [];
                return $"{image.Format} {image.Width}x{image.Height} " +
                       $"({imageData.Length} bytes, sha256:{ShortHash(imageData)})";
            case AssetMemberWalker.UnreadableMember unreadable:
                return $"<error: {unreadable.Message}>";
            case UndertaleNamedResource named:
                return string.IsNullOrEmpty(named.Name?.Content)
                    ? $"<{named.GetType().Name}>"
                    : $"{named.GetType().Name}:{named.Name!.Content}";
            case IList list:
                return $"<{list.GetType().Name}[{list.Count}]>";
            case Enum enumValue:
                return $"{enumValue.GetType().Name}.{enumValue}";
            case IFormattable formattable:
                return formattable.ToString(null, CultureInfo.InvariantCulture) ?? "";
            case var _ when IsScalar(value.GetType()):
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            default:
                return $"<{value.GetType().Name}>";
        }
    }

    private static string Quote(string? text)
    {
        if (text is null)
            return "<null>";
        if (text.Length > 200)
            text = text[..200] + "…";
        return "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
    }

    private static string ShortHash(byte[] data)
    {
        return Convert.ToHexString(SHA256.HashData(data))[..12].ToLowerInvariant();
    }

    private static string JoinPath(string path, string member)
    {
        return path.Length == 0 ? member : path + "." + member;
    }

    private sealed class ReferencePairComparer : IEqualityComparer<(object Base, object Compare)>
    {
        public bool Equals((object Base, object Compare) x, (object Base, object Compare) y)
        {
            return ReferenceEquals(x.Base, y.Base) && ReferenceEquals(x.Compare, y.Compare);
        }

        public int GetHashCode((object Base, object Compare) obj)
        {
            return HashCode.Combine(
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Base),
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Compare));
        }
    }
}
