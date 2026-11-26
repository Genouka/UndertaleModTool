using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Database;
using Android.Util;
using AndroidUri = global::Android.Net.Uri;

namespace UndertaleModToolAvalonia.Android;

/// <summary>
/// Handles data files that reach the app through the system ("Open with" / share sheet).
///
/// The app is path-based, so the URI carried by the intent has to become a real file path before
/// it can be opened: <see cref="AndroidRealPathResolver"/> covers the external-storage provider,
/// and every other provider (Downloads, cloud apps, ...) is copied into the app cache. The work
/// runs on a worker thread - a large data file must never block the activity - and the resulting
/// path is published through <see cref="PlatformIncomingFile"/>, which also buffers it when it
/// arrives before the UI is ready (cold start).
/// </summary>
static class IncomingFileHandler
{
    const string LogTag = "QiuUTMTv5";
    const string CacheFolderName = "incoming";

    /// <summary>Cap for the file name derived from the provider's display name.</summary>
    const int MaxNameLength = 80;

    /// <summary>Copies older than this are leftovers from earlier launches and get deleted.</summary>
    static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);

    /// <summary>Guards the once-per-process stale-copy cleanup.</summary>
    static int cleanupStarted;

    /// <summary>
    /// Extracts the file URI from an <c>ACTION_VIEW</c>/<c>ACTION_SEND</c> intent and schedules
    /// its resolution. Returns immediately; does nothing for intents that carry no file.
    /// </summary>
    public static void Handle(Activity activity, Intent? intent)
    {
        if (intent is null)
            return;

        AndroidUri? uri = GetIncomingUri(intent);
        if (uri is null)
            return;

        Log.Info(LogTag, $"Incoming intent {intent.Action} carries '{uri}'.");

        _ = Task.Run(async () =>
        {
            try
            {
                string path = await ResolveAsync(activity, uri);
                Log.Info(LogTag, $"Incoming file resolved to '{path}'.");
                PlatformIncomingFile.Post(path);
            }
            catch (Exception e)
            {
                Log.Warn(LogTag, $"Incoming file '{uri}' could not be opened: {e}");
                PlatformIncomingFile.PostError(e.Message);
            }
        });

        CleanUpStaleCopies(activity);
    }

    /// <summary>
    /// The file an intent points at: the data URI of a VIEW intent, or the stream extra of a SEND
    /// intent. Both are what the manifest intent filters advertise (octet-stream data files).
    /// </summary>
    static AndroidUri? GetIncomingUri(Intent intent)
    {
        if (intent.Action == Intent.ActionView)
            return intent.Data;

        if (intent.Action == Intent.ActionSend)
        {
#pragma warning disable CA1422 // the typed overload only exists on API 33+, the old one is the fallback below it
            return OperatingSystem.IsAndroidVersionAtLeast(33)
                ? intent.GetParcelableExtra(Intent.ExtraStream, Java.Lang.Class.FromType(typeof(AndroidUri))) as AndroidUri
                : intent.GetParcelableExtra(Intent.ExtraStream) as AndroidUri;
#pragma warning restore CA1422
        }

        return null;
    }

    static async Task<string> ResolveAsync(Activity activity, AndroidUri uri)
    {
        // Reuse the existing SAF -> path mapping first: it keeps the file where the user has it,
        // which matters because the app saves changes back to the same path.
        string? localPath = AndroidRealPathResolver.ResolveToLocalPath(new Uri(uri.ToString()!));
        if (localPath is not null && File.Exists(localPath))
            return localPath;

        return await CopyToCacheAsync(activity, uri);
    }

    /// <summary>
    /// Copies the provider stream into <c>CacheDir/incoming</c> under a unique, timestamped name.
    /// The copy is written to a <c>.part</c> file first and only renamed when complete, so a
    /// partially written file is never published to the UI.
    /// </summary>
    static async Task<string> CopyToCacheAsync(Activity activity, AndroidUri uri)
    {
        string folder = Path.Combine(activity.CacheDir!.AbsolutePath, CacheFolderName);
        Directory.CreateDirectory(folder);

        string displayName = SanitizeFileName(GetDisplayName(activity, uri));
        string stem = Path.GetFileNameWithoutExtension(displayName);
        string extension = Path.GetExtension(displayName);
        string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);

        string target = Path.Combine(folder, $"{stamp}_{stem}{extension}");
        for (int i = 1; File.Exists(target); i++)
            target = Path.Combine(folder, $"{stamp}_{stem}_{i}{extension}");

        string partial = target + ".part";
        try
        {
            using Stream source = activity.ContentResolver?.OpenInputStream(uri)
                ?? throw new IOException($"The provider returned no stream for '{uri}'.");

            using (FileStream destination = new(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                await source.CopyToAsync(destination).ConfigureAwait(false);
            }

            File.Move(partial, target);
            return target;
        }
        catch
        {
            TryDelete(partial);
            TryDelete(target);
            throw;
        }
    }

    /// <summary>
    /// The provider's user-facing name (<c>OpenableColumns.DISPLAY_NAME</c>), falling back to the
    /// URI's last path segment when the provider does not answer.
    /// </summary>
    static string GetDisplayName(Activity activity, AndroidUri uri)
    {
        try
        {
            using ICursor? cursor = activity.ContentResolver?.Query(uri, ["_display_name"], null, null, null);
            if (cursor is not null && cursor.MoveToFirst())
            {
                int index = cursor.GetColumnIndex("_display_name");
                if (index >= 0 && !cursor.IsNull(index) && cursor.GetString(index) is { Length: > 0 } name)
                    return name;
            }
        }
        catch (Exception e)
        {
            // A provider that does not support the projection is not an error: the path segment
            // still gives us a usable extension.
            Log.Debug(LogTag, $"Display name lookup failed for '{uri}': {e.Message}");
        }

        return uri.LastPathSegment ?? "incoming";
    }

    /// <summary>
    /// Turns a provider-supplied name into something safe to join with the cache folder path:
    /// no directory separators, no invalid characters, bounded length, never empty.
    /// </summary>
    static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = stackalloc char[Math.Min(Math.Max(name.Length, 1), MaxNameLength)];
        int length = 0;
        foreach (char c in name)
        {
            if (length == buffer.Length)
                break;
            buffer[length++] = Array.IndexOf(invalid, c) >= 0 || c == '\\' || c == '/' ? '_' : c;
        }

        string sanitized = new string(buffer[..length]).Trim('.', ' ');
        return sanitized.Length == 0 ? "incoming" : sanitized;
    }

    /// <summary>
    /// Deletes incoming-cache copies from previous launches once per process. The file just copied
    /// is new, so it can only be hit if the system date jumps backwards; that is harmless.
    /// </summary>
    static void CleanUpStaleCopies(Activity activity)
    {
        if (Interlocked.Exchange(ref cleanupStarted, 1) != 0)
            return;

        string folder = Path.Combine(activity.CacheDir!.AbsolutePath, CacheFolderName);
        _ = Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(folder))
                    return;

                DateTime cutoff = DateTime.UtcNow - CacheLifetime;
                foreach (string file in Directory.EnumerateFiles(folder))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < cutoff)
                            File.Delete(file);
                    }
                    catch (Exception e)
                    {
                        Log.Debug(LogTag, $"Could not delete stale incoming file '{file}': {e.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                Log.Debug(LogTag, $"Incoming cache cleanup failed: {e.Message}");
            }
        });
    }

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
            // Best effort; the stale-copy cleanup removes it on a later launch.
        }
    }
}
