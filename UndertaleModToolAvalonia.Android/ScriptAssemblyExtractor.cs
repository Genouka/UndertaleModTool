using System;
using System.IO;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using UndertaleModToolAvalonia;

namespace UndertaleModToolAvalonia.Android;

/// <summary>
/// Extracts the plain (non-AOT) assembly DLLs that the build packaged under
/// <c>assets/ScriptAssemblies</c> into the app's internal files directory, and points
/// <see cref="UndertaleModToolAvalonia.Scripting.ScriptAssembliesDirectory"/> at them so the
/// Roslyn scripting engine can resolve metadata references from real files. On Android the
/// assemblies normally live inside the APK (assembly store / <c>lib/*.dll.so</c>) and cannot be
/// resolved from the filesystem, which made script compilation fail with "cannot find assembly".
/// </summary>
public static class ScriptAssemblyExtractor
{
    /// <summary>Asset subfolder the build packages the DLLs into.</summary>
    private const string AssetSubfolder = "ScriptAssemblies";

    /// <summary>Folder name prefix used for the extraction directories (suffixed with the install time).</summary>
    private const string TargetFolderPrefix = "ScriptAssemblies_";

    private static Task? extraction;

    /// <summary>Install the extraction hook used by <see cref="UndertaleModToolAvalonia.Scripting.RunScript"/>.</summary>
    public static void Install()
    {
        // Warm the extraction on a background thread at app startup so the first script run usually
        // doesn't have to wait for it; the hook below only has to make sure it has finished.
        extraction = Task.Run(ExtractIntoInternalStorage);
        Scripting.PrepareScriptAssemblies = EnsureExtracted;
    }

    /// <summary>Hook invoked before a script is compiled; waits for the startup extraction if needed.</summary>
    private static void EnsureExtracted()
    {
        extraction?.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Copies <c>assets/ScriptAssemblies/*.dll</c> into internal storage, once per app install
    /// (the target folder is keyed by the app's last update time, so reinstalling or updating the
    /// APK extracts a fresh set while later calls within the same install are no-ops). Best-effort:
    /// failures leave the directory unset and the script engine falls back to its default
    /// reference behavior, so script runs surface the usual "assembly not found" compile errors
    /// instead of crashing.
    /// </summary>
    private static void ExtractIntoInternalStorage()
    {
        try
        {
            // Already prepared by an earlier invocation of this app run.
            if (!string.IsNullOrEmpty(Scripting.ScriptAssembliesDirectory) &&
                Directory.Exists(Scripting.ScriptAssembliesDirectory))
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
                // Assets not packaged (e.g. a build without the script assemblies) — nothing to do.
                return;
            }

            if (entries.Length == 0)
                return;

            long installStamp = context.PackageManager!
                .GetPackageInfo(context.PackageName!, (PackageInfoFlags)0)!
                .LastUpdateTime;

            string filesDir = context.FilesDir!.AbsolutePath;
            string targetDir = Path.Combine(filesDir, $"{TargetFolderPrefix}{installStamp}");
            Directory.CreateDirectory(targetDir);

            foreach (string entry in entries)
            {
                if (!entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    continue;

                string destination = Path.Combine(targetDir, entry);
                if (File.Exists(destination) && new FileInfo(destination).Length > 0)
                    continue; // already extracted by an earlier run of this install

                using Stream input = assets.Open(Path.Combine(AssetSubfolder, entry));
                using Stream output = File.Create(destination);
                input.CopyTo(output);
            }

            Scripting.ScriptAssembliesDirectory = targetDir;

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
            // Best-effort: script runs will report the usual "assembly not found" errors.
        }
    }
}