using System.Collections.Generic;
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
}