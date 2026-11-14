using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace UndertaleModToolAvalonia.Android;

/// <summary>
/// Magick.Native 的 Android 镜像由 <c>StageMagickNativeLibraries</c>（见 csproj）以
/// <c>libMagick.Native-Q8-&lt;abi&gt;.so</c> 的名字打包，而 Magick.NET 的 DllImport 请求的是
/// <c>Magick.Native-Q8-&lt;abi&gt;.dll</c>：dotnet/android 的 DSO 缓存与 Mono 的探测序列都拼不出
/// 两者之间的映射，所以这里显式注册解析器把请求重定向到打包名。
/// 必须在任何 Magick P/Invoke（首次构造 MagickImage）之前调用 <see cref="Register"/>。
/// </summary>
internal static class MagickNativeLibraryResolver
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
            return;
        _registered = true;
        NativeLibrary.SetDllImportResolver(typeof(ImageMagick.MagickImage).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // Magick.NET 的 flavor 程序集用完整 dll 名做 DllImport（"Magick.Native-Q8-arm64.dll"）。
        if (!libraryName.StartsWith("Magick.Native-Q8-", StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        string baseName = libraryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? libraryName[..^4]
            : libraryName;

        // TryLoad("libMagick.Native-Q8-<abi>") 让 Mono 按平台规则补 ".so" 后缀得到打包名。
        return NativeLibrary.TryLoad("lib" + baseName, out IntPtr handle) ? handle : IntPtr.Zero;
    }
}
