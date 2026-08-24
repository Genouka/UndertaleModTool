using System;
using System.Threading.Tasks;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Bridge for installing downloaded update packages on platforms that can't replace their own app
/// files (mobile builds). Desktop builds leave the callback unset - the desktop updater executable
/// is used instead. On Android the real implementation lives in
/// <c>UndertaleModToolAvalonia.Android/ApkUpdateInstaller.cs</c>.
/// </summary>
public static class PlatformUpdateInstaller
{
    /// <summary>
    /// Platform callback launching the installation of the update package at the given path (an APK
    /// on Android). Returns whether installation was started; when false, the platform side has
    /// already guided the user to grant the required permission (e.g. "install unknown apps").
    /// </summary>
    public static Func<string, Task<bool>>? InstallPackageAsync;
}
