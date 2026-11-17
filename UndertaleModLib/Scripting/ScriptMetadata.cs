using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace UndertaleModLib.Scripting;

/// <summary>
/// How script names are picked for the "Scripts" menu.
/// </summary>
public enum ScriptNameDisplayMode
{
    /// <summary>Show the script file name, including its extension.</summary>
    FileName,

    /// <summary>Show the English <c>@name</c> from the script metadata, or the file name when absent.</summary>
    EnglishName,

    /// <summary>Show the localized <c>@name:&lt;locale&gt;</c> from the script metadata, falling back to English and then the file name.</summary>
    LocalizedName,
}

/// <summary>
/// Metadata comment block of a .csx script, in the style of userscript headers.
/// </summary>
/// <remarks>
/// The block must be the first non-empty content of the file:
/// <code>
/// // ==UndertaleModTool==
/// // @name        Import GML
/// // @name:zh-CN 导入 GML
/// // @description Imports .gml files into code entries.
/// // ==/UndertaleModTool==
/// </code>
/// <c>==UserScript==</c> / <c>==/UserScript==</c> are accepted as an alias for the header tags.
/// Keys are case-insensitive; a key may be repeated per locale, with the last value winning.
/// </remarks>
public sealed class ScriptMetadata
{
    private static readonly Regex headerRegex = new(@"^//\s*==(?:UndertaleModTool|UserScript)==\s*$", RegexOptions.Compiled);
    private static readonly Regex footerRegex = new(@"^//\s*==/(?:UndertaleModTool|UserScript)==\s*$", RegexOptions.Compiled);
    private static readonly Regex metadataLineRegex = new(@"^//\s*@([A-Za-z][A-Za-z0-9_-]*)(?::([A-Za-z0-9_-]+))?\s+(.*?)\s*$",
                                                           RegexOptions.Compiled);

    private static readonly ConcurrentDictionary<string, CacheEntry> fileCache = new(StringComparer.Ordinal);
    private static readonly ScriptMetadata empty = new();

    private readonly Dictionary<string, Dictionary<string, string>> values = new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct CacheEntry(DateTime LastWriteTimeUtc, long Length, ScriptMetadata Metadata);

    /// <summary>Whether a metadata header block was found.</summary>
    public bool HasMetadata { get; private set; }

    /// <summary>The default (English) <c>@name</c>, if any.</summary>
    public string Name => GetValue("name");

    /// <summary>The default (English) <c>@description</c>, if any.</summary>
    public string Description => GetValue("description");

    /// <summary>
    /// Gets a metadata value, preferring the given culture and falling back to the default value.
    /// </summary>
    /// <param name="key">Metadata key, such as <c>name</c> or <c>description</c> (case-insensitive).</param>
    /// <param name="culture">Culture to look up, or <see langword="null"/> for the default value only.</param>
    /// <returns>The resolved value, or <see langword="null"/> if the key is absent.</returns>
    public string GetValue(string key, CultureInfo culture = null)
    {
        if (!values.TryGetValue(key, out Dictionary<string, string> byLocale))
            return null;

        if (culture is not null)
        {
            // Exact culture, then its parent chain (zh-CN -> zh-Hans -> zh).
            for (CultureInfo current = culture; current is not null; current = current.Parent)
            {
                if (current.Name.Length == 0)
                    break;
                if (byLocale.TryGetValue(NormalizeLocale(current.Name), out string value))
                    return value;
            }

            // Plain language tag, e.g. @name:zh.
            string language = culture.TwoLetterISOLanguageName;
            if (byLocale.TryGetValue(language, out string languageValue))
                return languageValue;

            // Any regional variant of the same language, e.g. @name:zh-CN for the zh-Hans UI.
            foreach (KeyValuePair<string, string> pair in byLocale)
            {
                if (pair.Key.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase))
                    return pair.Value;
            }
        }

        return byLocale.TryGetValue("", out string defaultValue) ? defaultValue : null;
    }

    /// <summary>
    /// Gets the localized <c>@name</c>, falling back to the default name.
    /// </summary>
    /// <param name="culture">Culture to look up, or <see langword="null"/> for the current UI culture.</param>
    /// <returns>The resolved name, or <see langword="null"/> if no <c>@name</c> is present.</returns>
    public string GetLocalizedName(CultureInfo culture = null)
        => GetValue("name", culture ?? CultureInfo.CurrentUICulture);

    /// <summary>
    /// Gets the localized <c>@description</c>, falling back to the default description.
    /// </summary>
    /// <param name="culture">Culture to look up, or <see langword="null"/> for the current UI culture.</param>
    /// <returns>The resolved description, or <see langword="null"/> if no <c>@description</c> is present.</returns>
    public string GetLocalizedDescription(CultureInfo culture = null)
        => GetValue("description", culture ?? CultureInfo.CurrentUICulture);

    /// <summary>
    /// Picks the name to show for a script, honoring <paramref name="mode"/> and falling back to the file name.
    /// </summary>
    /// <param name="filePath">Path of the script file.</param>
    /// <param name="mode">Which kind of name to prefer.</param>
    /// <param name="culture">Culture used for <see cref="ScriptNameDisplayMode.LocalizedName"/>.</param>
    /// <returns>A non-empty display name.</returns>
    public string GetDisplayName(string filePath, ScriptNameDisplayMode mode, CultureInfo culture = null)
    {
        string fileName = Path.GetFileName(filePath);
        if (mode == ScriptNameDisplayMode.FileName)
            return fileName;

        string englishName = GetValue("name");
        if (mode == ScriptNameDisplayMode.EnglishName)
            return string.IsNullOrWhiteSpace(englishName) ? fileName : englishName;

        string localizedName = GetValue("name", culture ?? CultureInfo.CurrentUICulture);
        if (!string.IsNullOrWhiteSpace(localizedName))
            return localizedName;
        return string.IsNullOrWhiteSpace(englishName) ? fileName : englishName;
    }

    /// <summary>
    /// Parses a metadata block from script text. Returns an empty instance when no block is present.
    /// </summary>
    /// <param name="text">Contents of a .csx script.</param>
    /// <returns>Parsed metadata; never <see langword="null"/>.</returns>
    public static ScriptMetadata Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
            return empty;

        ScriptMetadata result = new();
        bool inBlock = false;

        using StringReader reader = new(text);
        while (reader.ReadLine() is string line)
        {
            string trimmed = line.Trim();

            if (!inBlock)
            {
                // The metadata block must be the first non-empty content.
                if (trimmed.Length == 0)
                    continue;
                if (!headerRegex.IsMatch(trimmed))
                    return empty;

                inBlock = true;
                result.HasMetadata = true;
                continue;
            }

            if (footerRegex.IsMatch(trimmed))
                break;

            Match match = metadataLineRegex.Match(trimmed);
            if (!match.Success)
                continue;

            string key = match.Groups[1].Value.ToLowerInvariant();
            string locale = match.Groups[2].Success ? NormalizeLocale(match.Groups[2].Value) : "";
            string value = match.Groups[3].Value.Trim();
            if (value.Length == 0)
                continue;

            if (!result.values.TryGetValue(key, out Dictionary<string, string> byLocale))
                result.values[key] = byLocale = new(StringComparer.OrdinalIgnoreCase);
            byLocale[locale] = value;
        }

        return result;
    }

    /// <summary>
    /// Reads and parses metadata from a script file, caching by last write time and length.
    /// I/O and parse failures yield an empty instance, so menu construction never fails.
    /// </summary>
    /// <param name="filePath">Path of the script file.</param>
    /// <returns>Parsed metadata; never <see langword="null"/>.</returns>
    public static ScriptMetadata FromFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return empty;

        try
        {
            FileInfo info = new(filePath);
            if (!info.Exists)
                return empty;

            if (fileCache.TryGetValue(filePath, out CacheEntry cached) &&
                cached.LastWriteTimeUtc == info.LastWriteTimeUtc && cached.Length == info.Length)
            {
                return cached.Metadata;
            }

            ScriptMetadata metadata = Parse(File.ReadAllText(filePath));
            fileCache[filePath] = new(info.LastWriteTimeUtc, info.Length, metadata);
            return metadata;
        }
        catch (Exception)
        {
            return empty;
        }
    }

    /// <summary>Clears the metadata file cache.</summary>
    public static void ClearCache() => fileCache.Clear();

    private static string NormalizeLocale(string locale)
        => locale.Replace('_', '-').ToLowerInvariant();
}
