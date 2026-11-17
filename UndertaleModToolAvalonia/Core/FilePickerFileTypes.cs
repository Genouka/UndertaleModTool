using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Platform.Storage;
using UndertaleModTool.Localization;

namespace UndertaleModToolAvalonia;

public static class FilePickerFileTypes
{
    static readonly FilePickerFileType AllSingle = new(LocalizationSource.GetString("RefType_AllFiles"))
    {
        Patterns = ["*"],
    };

    static readonly FilePickerFileType BINSingle = new(LocalizationSource.GetString("RefType_BinFiles"))
    {
        Patterns = ["*.bin"],
    };

    static readonly FilePickerFileType DataSingle = new(LocalizationSource.GetString("RefType_GameMakerDataFiles"))
    {
        Patterns = ["*.win", "*.unx", "*.ios", "*.droid", "*.wad", "audiogroup*.dat"],
    };

    static readonly FilePickerFileType PNGSingle = new(LocalizationSource.GetString("RefType_PngFiles"))
    {
        Patterns = ["*.png"],
    };

    static readonly FilePickerFileType QOISingle = new(LocalizationSource.GetString("RefType_QoiFiles"))
    {
        Patterns = ["*.qoi"],
    };

    static readonly FilePickerFileType BZ2Single = new(LocalizationSource.GetString("RefType_Bz2Files"))
    {
        Patterns = ["*.bz2"],
    };

    static readonly FilePickerFileType WAVSingle = new(LocalizationSource.GetString("RefType_WavFiles"))
    {
        Patterns = ["*.wav"],
    };

    static readonly FilePickerFileType CSSingle = new(LocalizationSource.GetString("RefType_CSFiles"))
    {
        Patterns = ["*.csx"],
    };

    public static readonly FilePickerFileType Patch = new(LocalizationSource.GetString("RefType_PatchFiles"))
    {
        Patterns = ["*.qiuumtdiff"],
    };

    static readonly FilePickerFileType JSONSingle = new(LocalizationSource.GetString("RefType_JsonFiles"))
    {
        Patterns = ["*.json"],
    };

    public static readonly IReadOnlyList<FilePickerFileType> All = [AllSingle];
    public static readonly IReadOnlyList<FilePickerFileType> BIN = [BINSingle, AllSingle];
    public static readonly IReadOnlyList<FilePickerFileType> Data = [DataSingle, AllSingle];
    public static readonly IReadOnlyList<FilePickerFileType> Image = [PNGSingle, AllSingle];
    public static readonly IReadOnlyList<FilePickerFileType> PNG = [PNGSingle, AllSingle];
    public static readonly IReadOnlyList<FilePickerFileType> QOI = [QOISingle, AllSingle];
    public static readonly IReadOnlyList<FilePickerFileType> BZ2 = [BZ2Single, AllSingle];
    public static readonly IReadOnlyList<FilePickerFileType> WAV = [WAVSingle, AllSingle];
    public static readonly IReadOnlyList<FilePickerFileType> CS = [CSSingle, AllSingle];
    public static readonly IReadOnlyList<FilePickerFileType> JSON = [JSONSingle, AllSingle];

    /// <summary>
    /// Converts a WPF/WinForms style filter, as passed by scripts (e.g. <c>"Text files (.txt)|*.txt|All files|*"</c>), into file picker types.
    /// </summary>
    public static List<FilePickerFileType> FromWindowsFilter(string filter)
    {
        string[] parts = filter.Split('|');

        List<FilePickerFileType> types = [];
        for (int i = 0; i + 1 < parts.Length; i += 2)
        {
            types.Add(new FilePickerFileType(parts[i])
            {
                Patterns = FromWindowsPatterns(parts[i + 1]),
            });
        }
        return types;
    }

    static List<string> FromWindowsPatterns(string windowsPatterns)
    {
        List<string> patterns = [];
        foreach (string pattern in windowsPatterns.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // On Windows "*.*" matches every file, but elsewhere it would skip files without an extension.
            if (pattern == "*.*")
            {
                patterns.Add("*");
                continue;
            }

            // Windows matches patterns case-insensitively but Linux file pickers don't, and scripts use e.g. "*.WAV".
            patterns.Add(pattern);
            patterns.Add(pattern.ToLowerInvariant());
            patterns.Add(pattern.ToUpperInvariant());
        }
        return patterns.Distinct().ToList();
    }
}
