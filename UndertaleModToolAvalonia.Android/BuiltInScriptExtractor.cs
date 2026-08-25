using System;
using System.IO;
using System.Linq;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using UndertaleModToolAvalonia;

namespace UndertaleModToolAvalonia.Android;

/// <summary>
/// Copies the built-in utility scripts packaged under <c>assets/Scripts</c> (see
/// <c>BundleBuiltInScriptsToAssets</c> in the project file) into the app's internal files
/// directory, and points <see cref="BuiltInScripts"/> at that copy so the Scripts menu can list
/// and run them - there is no executable directory to read from on Android. It also registers the
/// user's public-storage script folder (<c>/sdcard/QiuUTMTv5/Scripts</c>) as an additional source.
/// </summary>
public static class BuiltInScriptExtractor
{
    /// <summary>Asset subfolder the build packages the scripts into.</summary>
    private const string AssetSubfolder = "Scripts";

    /// <summary>Folder name prefix used for the extraction directories (suffixed with the install time).</summary>
    private const string TargetFolderPrefix = "BuiltInScripts_";

    /// <summary>
    /// Extracts the scripts synchronously. Must run before the UI is created (Application
    /// <c>OnCreate</c>, which precedes any Activity), because the menu is built once at startup
    /// and there is no per-open callback to refresh it later.
    /// </summary>
    public static void Install()
    {
        // Register the user-provided script folder in public storage up front - independently of
        // the asset extraction below, which may early-return. Listing it requires the "All files
        // access" grant (requested by the main activity); the menu builder skips the directory
        // silently while it is missing or still inaccessible.
        string externalRoot = global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath ?? "/sdcard";
        string externalScripts = Path.Combine(externalRoot, "QiuUTMTv5", "Scripts");
        if (!BuiltInScripts.ExtraRootDirectories.Contains(externalScripts))
            BuiltInScripts.ExtraRootDirectories.Add(externalScripts);

        try
        {
            // Already prepared by an earlier invocation of this app run.
            if (!string.IsNullOrEmpty(BuiltInScripts.RootDirectoryOverride) &&
                Directory.Exists(BuiltInScripts.RootDirectoryOverride))
            {
                return;
            }

            Context context = Application.Context!;
            AssetManager assets = context.Assets!;

            string[] entries;
            try
            {
                entries = assets.List(AssetSubfolder) ?? [];
            }
            catch (Java.IO.FileNotFoundException)
            {
                // Assets not packaged - nothing to do.
                return;
            }

            if (entries.Length == 0)
                return;

            long installStamp = context.PackageManager!
                .GetPackageInfo(context.PackageName!, (PackageInfoFlags)0)!
                .LastUpdateTime;

            string filesDir = context.FilesDir!.AbsolutePath;
            string targetDir = Path.Combine(filesDir, $"{TargetFolderPrefix}{installStamp}");

            ExtractTree(assets, AssetSubfolder, targetDir);

            BuiltInScripts.RootDirectoryOverride = targetDir;

            // Drop extraction folders from previous app versions kept in internal storage.
            foreach (string stale in Directory.GetDirectories(filesDir, $"{TargetFolderPrefix}*"))
            {
                if (!string.Equals(stale, targetDir, StringComparison.Ordinal))
                {
                    try
                    {
                        Directory.Delete(stale, recursive: true);
                    }
                    catch (Exception)
                    {
                        // Best-effort cleanup.
                    }
                }
            }
        }
        catch (Exception)
        {
            // Best-effort: without the extracted scripts the menu simply reports that no scripts
            // were found; user-provided scripts still run through "Run other script...".
        }
    }

    /// <summary>
    /// Recursively copies an asset directory tree. <see cref="AssetManager.List"/> does not tell
    /// files from directories, so each entry is probed by opening it as a file first.
    /// </summary>
    private static void ExtractTree(AssetManager assets, string assetDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (string entry in assets.List(assetDir) ?? [])
        {
            string assetPath = $"{assetDir}/{entry}";
            string destination = Path.Combine(targetDir, entry);

            bool isFile;
            try
            {
                using (_ = assets.Open(assetPath))
                    isFile = true;
            }
            catch (Java.IO.FileNotFoundException)
            {
                isFile = false;
            }

            if (isFile)
            {
                if (File.Exists(destination) && new FileInfo(destination).Length > 0)
                    continue; // already extracted by an earlier run of this install

                using Stream input = assets.Open(assetPath);
                using Stream output = File.Create(destination);
                input.CopyTo(output);
            }
            else
            {
                ExtractTree(assets, assetPath, destination);
            }
        }
    }
}
