using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Platform.Storage;

// Aliases avoid the namespace "UndertaleModToolAvalonia.Android" shadowing "Android.*".
using AndroidUri = global::Android.Net.Uri;
using AndroidEnvironment = global::Android.OS.Environment;
using AndroidApplication = global::Android.App.Application;
using AndroidStorageManager = global::Android.OS.Storage.StorageManager;
using AndroidStorageVolume = global::Android.OS.Storage.StorageVolume;

namespace UndertaleModToolAvalonia.Android;

/// <summary>
/// Installs a storage provider on the Android top level that resolves the <c>content://</c> URIs
/// returned by the SAF pickers into real filesystem paths (e.g. <c>/storage/emulated/0/Download</c>)
/// whenever possible. Combined with the storage permission granted via
/// <see cref="StoragePermissionHelper"/>, the whole app (which is path-based: File.WriteAllText,
/// Directory.CreateDirectory, TextureWorker, ...) can then read/write external storage directly,
/// bypassing SAF streams.
/// </summary>
public sealed class RealPathStorageProviderFactory : IStorageProviderFactory
{
    public static readonly RealPathStorageProviderFactory Instance = new();

    public IStorageProvider CreateProvider(TopLevel topLevel)
    {
        IStorageProvider? native = topLevel.PlatformImpl?.TryGetFeature<IStorageProvider>();
        return native is null ? null! : new RealPathStorageProvider(native);
    }
}

/// <summary>
/// Wraps the native Android storage provider so every item coming back from a picker (and every
/// path lookup) is translated into a real-path-backed item (<see cref="RealPathStorageFile"/> /
/// <see cref="RealPathStorageFolder"/>) when a real path can be determined. Items that cannot be
/// resolved (e.g. the Downloads/cloud providers) are returned unchanged, so the existing SAF-based
/// fallbacks keep working.
/// </summary>
public sealed class RealPathStorageProvider : IStorageProvider
{
    readonly IStorageProvider _native;

    public RealPathStorageProvider(IStorageProvider native)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
    }

    public bool CanOpen => _native.CanOpen;
    public bool CanSave => _native.CanSave;
    public bool CanPickFolder => _native.CanPickFolder;

    public async Task<IReadOnlyList<IStorageFile>> OpenFilePickerAsync(FilePickerOpenOptions options)
    {
        IReadOnlyList<IStorageFile> files = await _native.OpenFilePickerAsync(options);
        return files.Select(f => ToWrappedFile(f) ?? f).ToList();
    }

    public async Task<OpenFilePickerResult> OpenFilePickerWithResultAsync(FilePickerOpenOptions options)
    {
        OpenFilePickerResult result = await _native.OpenFilePickerWithResultAsync(options);
        return result with { Files = result.Files.Select(f => ToWrappedFile(f) ?? f).ToList() };
    }

    public async Task<IStorageFile?> SaveFilePickerAsync(FilePickerSaveOptions options)
    {
        IStorageFile? file = await _native.SaveFilePickerAsync(options);
        return file is null ? null : ToWrappedFile(file) ?? file;
    }

    public async Task<SaveFilePickerResult> SaveFilePickerWithResultAsync(FilePickerSaveOptions options)
    {
        SaveFilePickerResult result = await _native.SaveFilePickerWithResultAsync(options);
        return result with { File = result.File is null ? null : ToWrappedFile(result.File) ?? result.File };
    }

    public async Task<IReadOnlyList<IStorageFolder>> OpenFolderPickerAsync(FolderPickerOpenOptions options)
    {
        IReadOnlyList<IStorageFolder> folders = await _native.OpenFolderPickerAsync(options);
        return folders.Select(f => ToWrappedFolder(f) ?? f).ToList();
    }

    public Task<IStorageBookmarkFile?> OpenFileBookmarkAsync(string bookmark)
    {
        return _native.OpenFileBookmarkAsync(bookmark);
    }

    public Task<IStorageBookmarkFolder?> OpenFolderBookmarkAsync(string bookmark)
    {
        return _native.OpenFolderBookmarkAsync(bookmark);
    }

    public async Task<IStorageFile?> TryGetFileFromPathAsync(Uri filePath)
    {
        string? local = AndroidRealPathResolver.ResolveToLocalPath(filePath);
        if (local is not null && File.Exists(local))
            return new RealPathStorageFile(new FileInfo(local));

        IStorageFile? item = await _native.TryGetFileFromPathAsync(filePath);
        return item is null ? null : ToWrappedFile(item) ?? item;
    }

    public async Task<IStorageFolder?> TryGetFolderFromPathAsync(Uri folderPath)
    {
        string? local = AndroidRealPathResolver.ResolveToLocalPath(folderPath);
        if (local is not null && Directory.Exists(local))
            return new RealPathStorageFolder(new DirectoryInfo(local));

        IStorageFolder? item = await _native.TryGetFolderFromPathAsync(folderPath);
        return item is null ? null : ToWrappedFolder(item) ?? item;
    }

    public Task<IStorageFolder?> TryGetWellKnownFolderAsync(WellKnownFolder wellKnownFolder)
    {
        // The native provider maps well-known folders to the app-specific external files dir
        // (a real local path), which is fine as-is.
        return _native.TryGetWellKnownFolderAsync(wellKnownFolder);
    }

    static RealPathStorageFile? ToWrappedFile(IStorageFile file)
    {
        string? local = AndroidRealPathResolver.ResolveToLocalPath(file.Path)
            ?? file.TryGetLocalPath();
        return local is not null && File.Exists(local)
            ? new RealPathStorageFile(new FileInfo(local))
            : null;
    }

    static RealPathStorageFolder? ToWrappedFolder(IStorageFolder folder)
    {
        string? local = AndroidRealPathResolver.ResolveToLocalPath(folder.Path)
            ?? folder.TryGetLocalPath();
        return local is not null && Directory.Exists(local)
            ? new RealPathStorageFolder(new DirectoryInfo(local))
            : null;
    }
}

/// <summary>
/// Maps Android <c>content://</c> URIs (SAF) to real filesystem paths.
/// </summary>
static class AndroidRealPathResolver
{
    /// <summary>
    /// Resolves a <see cref="Uri"/> (file:// or SAF content:// from the external storage provider)
    /// into a real local path, or <see langword="null"/> when it cannot be resolved.
    /// </summary>
    public static string? ResolveToLocalPath(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
            return null;

        if (uri.Scheme == Uri.UriSchemeFile)
            return uri.LocalPath;

        if (uri.Scheme != "content")
            return null;

        // Only the ExternalStorageProvider ("com.android.externalstorage.documents") exposes
        // tree/document ids that map 1:1 onto real filesystem paths. Other providers (Downloads,
        // cloud storage, ...) have no real path and stay on the SAF path.
        if (!string.Equals(uri.Authority, "com.android.externalstorage.documents", StringComparison.OrdinalIgnoreCase))
            return null;

        AndroidUri? androidUri = AndroidUri.Parse(uri.ToString());
        if (androidUri is null)
            return null;

        string? documentId = null;
        try
        {
            documentId = DocumentsContract.GetTreeDocumentId(androidUri);
        }
        catch (Exception)
        {
            // Not a tree URI; try a plain document URI below.
        }
        if (documentId is null)
        {
            try
            {
                documentId = DocumentsContract.GetDocumentId(androidUri);
            }
            catch (Exception)
            {
                return null;
            }
        }
        if (string.IsNullOrEmpty(documentId))
            return null;

        // Document/tree ids look like "primary:Download/subfolder" or "1234-5678:MyFolder".
        int colon = documentId.IndexOf(':');
        if (colon <= 0)
            return null;

        string volumeId = documentId[..colon];
        string relativePath = documentId[(colon + 1)..];

        string? volumeRoot = volumeId.Equals("primary", StringComparison.OrdinalIgnoreCase)
            ? AndroidEnvironment.ExternalStorageDirectory?.AbsolutePath
            : GetSecondaryVolumeRoot(volumeId);
        if (volumeRoot is null)
            return null;

        relativePath = relativePath.TrimEnd('/');
        return relativePath.Length == 0
            ? volumeRoot
            : System.IO.Path.Combine(volumeRoot, relativePath);
    }

    /// <summary>
    /// Maps a removable-volume id (e.g. "1234-5678") to its mount path, or null if not mounted.
    /// Secondary volumes are mounted directly under <c>/storage/&lt;id&gt;</c> on most devices.
    /// </summary>
    static string? GetSecondaryVolumeRoot(string volumeId)
    {
        // Common mount point for removable volumes on API 24+.
        string direct = System.IO.Path.Combine("/storage", volumeId);
        if (Directory.Exists(direct))
            return direct;

        // volume.Directory is only available from API 30+; before that /storage/<id> is the answer.
        if (!OperatingSystem.IsAndroidVersionAtLeast(30))
            return null;

        try
        {
            AndroidStorageManager? storageManager = AndroidApplication.Context.GetSystemService(Context.StorageService) as AndroidStorageManager;
            if (storageManager is null)
                return null;

            foreach (AndroidStorageVolume volume in storageManager.StorageVolumes)
            {
                string? uuid = volume.Uuid?.Replace("-", "");
                if (!string.Equals(uuid, volumeId, StringComparison.OrdinalIgnoreCase))
                    continue;

                // volume.Directory is only available from API 30+. For API 24-29 the
                // /storage/<id> path above is the best available answer.
                Java.IO.File? directory = volume.Directory;
                if (directory is not null && directory.Exists())
                    return directory.AbsolutePath;
            }
        }
        catch (Exception)
        {
            // Volume enumeration failure: fall back to SAF.
        }
        return null;
    }
}

/// <summary>
/// Base class for real-path-backed storage items. <see cref="Path"/> returns a <c>file://</c> URI
/// (mirroring Avalonia's BclStorageItem URI format), so <c>TryGetLocalPath()</c> works and the
/// path-based app code can use the items directly.
/// </summary>
public abstract class RealPathStorageItem : IStorageBookmarkItem, IStorageItem
{
    protected readonly FileSystemInfo FileSystemInfo;

    protected RealPathStorageItem(FileSystemInfo fileSystemInfo)
    {
        FileSystemInfo = fileSystemInfo;
    }

    public string Name => FileSystemInfo.Name;

    public bool CanBookmark => true;

    public Uri Path => ToFileUri(FileSystemInfo);

    public Task<StorageItemProperties> GetBasicPropertiesAsync()
    {
        if (FileSystemInfo.Exists)
        {
            long size = FileSystemInfo is FileInfo fileInfo ? fileInfo.Length : 0;
            return Task.FromResult(new StorageItemProperties(
                (ulong)size, FileSystemInfo.CreationTimeUtc, FileSystemInfo.LastWriteTimeUtc));
        }
        return Task.FromResult(new StorageItemProperties());
    }

    public Task<IStorageFolder?> GetParentAsync()
    {
        DirectoryInfo? parent = FileSystemInfo switch
        {
            FileInfo fileInfo => fileInfo.Directory,
            DirectoryInfo directoryInfo => directoryInfo.Parent,
            _ => null,
        };
        return Task.FromResult<IStorageFolder?>(parent is null ? null : new RealPathStorageFolder(parent));
    }

    public Task DeleteAsync()
    {
        if (FileSystemInfo is DirectoryInfo directoryInfo)
            directoryInfo.Delete(recursive: true);
        else
            FileSystemInfo.Delete();
        return Task.CompletedTask;
    }

    public Task<IStorageItem?> MoveAsync(IStorageFolder destination)
    {
        string? destinationPath = destination?.TryGetLocalPath();
        if (string.IsNullOrEmpty(destinationPath))
            return Task.FromResult<IStorageItem?>(null);

        string target = System.IO.Path.Combine(destinationPath, FileSystemInfo.Name);
        if (FileSystemInfo is DirectoryInfo directoryInfo)
        {
            directoryInfo.MoveTo(target);
            return Task.FromResult<IStorageItem?>(new RealPathStorageFolder(new DirectoryInfo(target)));
        }
        ((FileInfo)FileSystemInfo).MoveTo(target);
        return Task.FromResult<IStorageItem?>(new RealPathStorageFile(new FileInfo(target)));
    }

    public Task<string?> SaveBookmarkAsync()
    {
        return Task.FromResult<string?>(FileSystemInfo.FullName);
    }

    public Task ReleaseBookmarkAsync()
    {
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        // No unmanaged state; items are lightweight path handles.
    }

    /// <summary>
    /// Mirrors Avalonia's <c>StorageProviderHelpers.UriFromFilePath</c> so the built-in
    /// <c>TryGetLocalPath()</c> extension recognizes the path.
    /// </summary>
    static Uri ToFileUri(FileSystemInfo fileSystemInfo)
    {
        string path = fileSystemInfo.FullName;
        StringBuilder builder = new(path);
        builder.Replace("%", "%25").Replace("[", "%5B").Replace("]", "%5D");
        if (fileSystemInfo is DirectoryInfo && !builder.ToString().EndsWith('/'))
            builder.Append('/');
        return new UriBuilder("file", string.Empty) { Path = builder.ToString() }.Uri;
    }
}

/// <summary>A real-path-backed <see cref="IStorageFile"/>.</summary>
public sealed class RealPathStorageFile : RealPathStorageItem, IStorageBookmarkFile, IStorageFile
{
    public RealPathStorageFile(FileInfo fileInfo)
        : base(fileInfo)
    {
    }

    FileInfo FileInfo => (FileInfo)FileSystemInfo;

    public Task<Stream> OpenReadAsync()
    {
        return Task.FromResult<Stream>(FileInfo.OpenRead());
    }

    public Task<Stream> OpenWriteAsync()
    {
        return Task.FromResult<Stream>(new FileStream(FileInfo.FullName, FileMode.Create, FileAccess.Write, FileShare.Write));
    }
}

/// <summary>A real-path-backed <see cref="IStorageFolder"/>.</summary>
public sealed class RealPathStorageFolder : RealPathStorageItem, IStorageBookmarkFolder, IStorageFolder
{
    readonly DirectoryInfo _directoryInfo;

    public RealPathStorageFolder(DirectoryInfo directoryInfo)
        : base(directoryInfo)
    {
        _directoryInfo = directoryInfo;
    }

    public async IAsyncEnumerable<IStorageItem> GetItemsAsync()
    {
        foreach (DirectoryInfo subDirectory in _directoryInfo.EnumerateDirectories())
            yield return new RealPathStorageFolder(subDirectory);
        foreach (FileInfo file in _directoryInfo.EnumerateFiles())
            yield return new RealPathStorageFile(file);
        await Task.CompletedTask;
    }

    public Task<IStorageFolder?> GetFolderAsync(string name)
    {
        string path = System.IO.Path.Combine(_directoryInfo.FullName, name);
        return Task.FromResult<IStorageFolder?>(
            Directory.Exists(path) ? new RealPathStorageFolder(new DirectoryInfo(path)) : null);
    }

    public Task<IStorageFile?> GetFileAsync(string name)
    {
        string path = System.IO.Path.Combine(_directoryInfo.FullName, name);
        return Task.FromResult<IStorageFile?>(
            File.Exists(path) ? new RealPathStorageFile(new FileInfo(path)) : null);
    }

    public Task<IStorageFile?> CreateFileAsync(string name)
    {
        FileInfo fileInfo = new(System.IO.Path.Combine(_directoryInfo.FullName, name));
        using (fileInfo.Create())
        {
        }
        return Task.FromResult<IStorageFile?>(new RealPathStorageFile(fileInfo));
    }

    public Task<IStorageFolder?> CreateFolderAsync(string name)
    {
        DirectoryInfo subDirectory = _directoryInfo.CreateSubdirectory(name);
        return Task.FromResult<IStorageFolder?>(new RealPathStorageFolder(subDirectory));
    }
}