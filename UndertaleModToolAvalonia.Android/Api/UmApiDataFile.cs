using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Android.Content;
using Avalonia.Threading;
using UndertaleModLib;
using UndertaleModToolAvalonia;

namespace UndertaleModToolAvalonia.Android.Api;

/// <summary>
/// Data file access for the cross-app API: opens/saves a <c>data.win</c> by filesystem path or
/// through a <c>content://</c> URI the calling app granted (its own documents, SAF picks, ...).
/// <para>
/// Both directions run through <see cref="UndertaleIO"/> directly rather than through
/// <see cref="MainViewModel.LoadData"/>/<c>SaveData</c>, because those show the app's loader window
/// and message dialogs - neither of which may appear for an externally requested operation. The
/// view model itself is still updated (on the UI thread), so the app window reflects what the API
/// did and scripts see the loaded data.
/// </para>
/// </summary>
internal static class UmApiDataFile
{
    public static async Task<string> LoadAsync(Context context, MainViewModel vm, string pathOrUri)
    {
        await UmApiJobs.Gate.WaitAsync().ConfigureAwait(false);

        try
        {
            List<string> warnings = [];
            bool hadImportantWarnings = false;

            UndertaleData data = await Task.Run(() =>
            {
                using Stream stream = OpenReadStream(context, pathOrUri);
                return UndertaleIO.Read(
                    stream,
                    (string warning, bool isImportant) =>
                    {
                        lock (warnings)
                        {
                            warnings.Add(warning);
                            hadImportantWarnings |= isImportant;
                        }
                    },
                    null);
            }).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                vm.CloseData();
                vm.Data = data;
                vm.DataPath = IsContentUri(pathOrUri) ? null : FullPath(pathOrUri);
                vm.UpdateVersion();
            });

            JsonObject result = UmApiJson.Ok();
            result["path"] = pathOrUri;
            result["dataPath"] = vm.DataPath;
            result["importantWarnings"] = hadImportantWarnings;

            JsonArray warningArray = [];
            lock (warnings)
            {
                foreach (string warning in warnings)
                    warningArray.Add(warning);
            }
            result["warnings"] = warningArray;

            return UmApiJson.Write(result);
        }
        catch (Exception e)
        {
            return UmApiJson.Write(UmApiJson.Failure(e.Message));
        }
        finally
        {
            UmApiJobs.Gate.Release();
        }
    }

    public static async Task<string> SaveAsync(Context context, MainViewModel vm, string pathOrUri)
    {
        await UmApiJobs.Gate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (vm.Data is null)
                return UmApiJson.Write(UmApiJson.Failure("No data file is loaded; call loadDataFile first."));

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                // Mirrors MainViewModel.SaveData: recompile the project's code sources first when
                // the setting asks for it, so the saved file matches what the tool would write.
                if (vm.Settings.RecompileAllCodeSourcesOnProjectSave && vm.Project is not null)
                    await Task.Run(() => vm.Project.RecompileAllCodeSources());

                await Task.Run(() =>
                {
                    using Stream stream = OpenWriteStream(context, pathOrUri);
                    UndertaleIO.Write(stream, vm.Data!, null);
                });

                if (!IsContentUri(pathOrUri))
                    vm.DataPath = FullPath(pathOrUri);
            });

            JsonObject result = UmApiJson.Ok();
            result["path"] = pathOrUri;
            result["dataPath"] = vm.DataPath;
            return UmApiJson.Write(result);
        }
        catch (Exception e)
        {
            return UmApiJson.Write(UmApiJson.Failure(e.Message));
        }
        finally
        {
            UmApiJobs.Gate.Release();
        }
    }

    public static bool IsContentUri(string pathOrUri)
        => pathOrUri.StartsWith("content://", StringComparison.OrdinalIgnoreCase);

    static bool IsFileUri(string pathOrUri)
        => pathOrUri.StartsWith("file://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens a path / file:// / content:// source for reading (shared with the script reader).</summary>
    internal static Stream OpenReadStream(Context context, string pathOrUri)
    {
        if (IsContentUri(pathOrUri))
        {
            Stream? stream = context.ContentResolver?.OpenInputStream(global::Android.Net.Uri.Parse(pathOrUri)!);
            return stream ?? throw new FileNotFoundException("The content URI could not be opened: " + pathOrUri);
        }

        return File.OpenRead(IsFileUri(pathOrUri) ? new Uri(pathOrUri).LocalPath : pathOrUri);
    }

    static Stream OpenWriteStream(Context context, string pathOrUri)
    {
        if (IsContentUri(pathOrUri))
        {
            // "wt" truncates an existing document instead of appending to it.
            Stream? stream = context.ContentResolver?.OpenOutputStream(global::Android.Net.Uri.Parse(pathOrUri)!, "wt");
            return stream ?? throw new FileNotFoundException("The content URI could not be written: " + pathOrUri);
        }

        string path = IsFileUri(pathOrUri) ? new Uri(pathOrUri).LocalPath : pathOrUri;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        return File.Create(path);
    }

    static string FullPath(string pathOrUri)
    {
        try
        {
            return Path.GetFullPath(pathOrUri);
        }
        catch (Exception)
        {
            return pathOrUri;
        }
    }
}
