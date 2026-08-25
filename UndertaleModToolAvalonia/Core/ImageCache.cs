using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ImageMagick;
using UndertaleModLib.Models;
using UndertaleModLib.Util;

namespace UndertaleModToolAvalonia;

// Thread-safe image cache.
//
// Texture pages are decoded at most once (even under concurrent access from the render loop,
// preloading and exports) and crops are cheap memory copies out of the decoded page.
//
// Decoding never blocks the UI thread: on a cache miss the lookup returns null right away and
// starts a background decode; callers get notified through the onImageReady callback / ImageLoaded
// event so they can repaint. Pass waitForLoad: true to block until decoding finished - only valid
// off the UI thread (e.g. exports).
public class ImageCache
{
    abstract record ImageKey();
    record GMImageImageKey(GMImage GMImage) : ImageKey;
    record TexturePageItemImageKey(GMImage GMImage, ushort SourceX, ushort SourceY,
        ushort SourceWidth, ushort SourceHeight) : ImageKey;
    record TileImageKey(GMImage GMImage, ushort SourceX, ushort SourceY, ushort TargetX, ushort TargetY,
        int TileSourceX, int TileSourceY, uint Width, uint Height) : ImageKey;
    record LayerTileImageKey(GMImage GMImage, ushort SourceX, ushort SourceY, uint TileId,
        uint TileColumns, uint TileWidth, uint TileHeight, uint TileBorderX, uint TileBorderY) : ImageKey;

    sealed class PageEntry
    {
        public readonly object SyncRoot = new();
        public WeakReference<Bitmap>? Bitmap;
        public bool Loading;
        public Exception? Error;
        public List<Action>? Callbacks;
        public TaskCompletionSource? Completion;
    }

    // Limits how many texture pages are decoded in parallel.
    static readonly SemaphoreSlim DecodeGate = new(Math.Clamp(Environment.ProcessorCount - 1, 1, 4));

    readonly object cropCacheLock = new();
    readonly Dictionary<ImageKey, WeakReference<Bitmap>> cropCache = [];
    readonly ConcurrentDictionary<GMImage, PageEntry> pageEntries = [];

    /// <summary>
    /// Raised on the UI thread whenever a texture page finished (or failed) loading in the
    /// background. Subscribers typically invalidate their rendering so newly available images
    /// appear without blocking.
    /// </summary>
    public event Action? ImageLoaded;

    public Bitmap? GetImageFromGMImage(GMImage gmImage)
    {
        using MagickImage image = gmImage.GetMagickImage();
        return RunOnUIThread(() => ToBitmap(image));
    }

    public Bitmap? GetCachedImageFromGMImage(GMImage gmImage, Action? onImageReady = null, bool waitForLoad = false)
    {
        GMImageImageKey key = new(gmImage);

        Bitmap? cached = TryGetCrop(key);
        if (cached is not null)
            return cached;

        return GetCached(gmImage, waitForLoad, onImageReady, key,
            page => CropBitmap(page, 0, 0, page.PixelSize.Width, page.PixelSize.Height));
    }

    public Bitmap? GetCachedImageFromTexturePageItem(UndertaleTexturePageItem texturePageItem, Action? onImageReady = null, bool waitForLoad = false)
    {
        if (texturePageItem.TexturePage is null
            || texturePageItem.TexturePage.TextureData is null
            || texturePageItem.TexturePage.TextureData.Image is null)
            return null;

        GMImage gmImage = texturePageItem.TexturePage.TextureData.Image;

        if (texturePageItem.SourceX + texturePageItem.SourceWidth > gmImage.Width
            || texturePageItem.SourceY + texturePageItem.SourceHeight > gmImage.Height)
            return null;

        TexturePageItemImageKey key = new(
            gmImage,
            texturePageItem.SourceX,
            texturePageItem.SourceY,
            texturePageItem.SourceWidth,
            texturePageItem.SourceHeight);

        Bitmap? cached = TryGetCrop(key);
        if (cached is not null)
            return cached;

        return GetCached(gmImage, waitForLoad, onImageReady, key,
            page => CropBitmap(page,
                texturePageItem.SourceX, texturePageItem.SourceY,
                texturePageItem.SourceWidth, texturePageItem.SourceHeight));
    }

    public Bitmap? GetCachedImageFromTile(UndertaleRoom.Tile tile, Action? onImageReady = null, bool waitForLoad = false)
    {
        if (tile.Tpag is null || tile.Tpag.TexturePage is null || tile.Tpag.TexturePage.TextureData is null || tile.Width == 0 || tile.Height == 0)
            return null;

        GMImage gmImage = tile.Tpag.TexturePage.TextureData.Image;

        TileImageKey key = new(
            gmImage,
            tile.Tpag.SourceX,
            tile.Tpag.SourceY,
            tile.Tpag.TargetX,
            tile.Tpag.TargetY,
            tile.SourceX,
            tile.SourceY,
            tile.Width,
            tile.Height);

        Bitmap? cached = TryGetCrop(key);
        if (cached is not null)
            return cached;

        return GetCached(gmImage, waitForLoad, onImageReady, key, page =>
        {
            // Don't allow tile to exceed texture page item's borders
            int l = tile.Tpag.SourceX + Math.Max(0, tile.SourceX - tile.Tpag.TargetX);
            int t = tile.Tpag.SourceY + Math.Max(0, tile.SourceY - tile.Tpag.TargetY);
            int r = (int)Math.Min(l + tile.Width, tile.Tpag.SourceX + tile.Tpag.SourceWidth);
            int b = (int)Math.Min(t + tile.Height, tile.Tpag.SourceY + tile.Tpag.SourceHeight);

            if (l >= r || t >= b)
                return null;

            return CropBitmap(page, l, t, r - l, b - t);
        });
    }

    // Shared path for the getters above.
    //
    // Non-blocking mode: resolves the page without waiting (starting a background load on a miss)
    // and crops on the UI thread. Returns null while the page is still decoding - onImageReady /
    // ImageLoaded fire later so callers can repaint.
    //
    // Blocking mode (off-UI only): waits until the page finished decoding before cropping, so
    // exports always see complete images without ever stalling the UI thread.
    Bitmap? GetCached(GMImage pageImage, bool waitForLoad, Action? onImageReady, ImageKey key, Func<Bitmap, Bitmap?> crop)
    {
        Bitmap? page = ResolvePage(pageImage, waitForLoad, onImageReady);
        if (page is null)
            return null;

        return RunOnUIThread(() =>
        {
            Bitmap? image = crop(page);
            if (image is not null)
                StoreCrop(key, image);
            return image;
        });
    }

    public void Clear()
    {
        lock (cropCacheLock)
            cropCache.Clear();

        foreach (KeyValuePair<GMImage, PageEntry> pair in pageEntries.ToArray())
        {
            PageEntry entry = pair.Value;
            lock (entry.SyncRoot)
            {
                if (entry.Loading)
                {
                    // Let the in-flight load finish; its result will simply be dropped because the
                    // entry was removed from pageEntries below.
                    continue;
                }

                entry.Bitmap = null;
                entry.Error = new ObjectDisposedException(nameof(ImageCache));
                Monitor.PulseAll(entry.SyncRoot);
            }
        }

        pageEntries.Clear();
    }

    #region Preloading

    /// <summary>
    /// Starts background decoding of the given texture pages (if not cached yet) and returns a
    /// task that completes when they are all ready. Safe to call from any thread.
    /// </summary>
    public Task PreloadAsync(IEnumerable<GMImage?> images)
    {
        List<Task>? tasks = null;
        foreach (GMImage? image in images)
        {
            if (image is null)
                continue;

            Task task = GetOrStartPageLoad(image);
            tasks ??= [];
            tasks.Add(task);
        }

        return tasks is null ? Task.CompletedTask : Task.WhenAll(tasks);
    }

    public Task PreloadAsync(UndertaleTexturePageItem texturePageItem)
    {
        GMImage? image = texturePageItem.TexturePage?.TextureData?.Image;
        return image is null ? Task.CompletedTask : GetOrStartPageLoad(image);
    }

    /// <summary>
    /// Preloads every texture page referenced by the given room, so that opening it in an editor
    /// doesn't stall the UI thread.
    /// </summary>
    public Task PreloadAsync(UndertaleRoom room)
    {
        List<GMImage?> images = [];
        foreach (UndertaleTexturePageItem item in CollectRoomTextures(room))
            images.Add(item.TexturePage!.TextureData!.Image);
        return PreloadAsync(images);
    }

    public static IEnumerable<UndertaleTexturePageItem> CollectRoomTextures(UndertaleRoom room)
    {
        HashSet<UndertaleTexturePageItem> textures = new();

        void Add(UndertaleTexturePageItem? texture)
        {
            if (texture?.TexturePage?.TextureData?.Image is not null)
                textures.Add(texture);
        }

        void AddGameObject(UndertaleRoom.GameObject gameObject)
        {
            Add(gameObject.ObjectDefinition?.Sprite?.Textures?.ElementAtOrDefault(gameObject.ImageIndex)?.Texture);
        }

        void AddTiles(IEnumerable<UndertaleRoom.Tile>? tiles)
        {
            if (tiles is null)
                return;
            foreach (UndertaleRoom.Tile? tile in tiles)
                Add(tile?.Tpag);
        }

        void AddSpriteInstances(IEnumerable<UndertaleRoom.SpriteInstance>? sprites)
        {
            if (sprites is null)
                return;
            foreach (UndertaleRoom.SpriteInstance? sprite in sprites)
                Add(sprite?.Sprite?.Textures?.ElementAtOrDefault((int)sprite.FrameIndex)?.Texture);
        }

        if (!(room.Flags.HasFlag(UndertaleRoom.RoomEntryFlags.IsGMS2) || room.Flags.HasFlag(UndertaleRoom.RoomEntryFlags.IsGM2024_13)))
        {
            foreach (UndertaleRoom.Background? background in room.Backgrounds)
                Add(background?.BackgroundDefinition?.Texture);
            foreach (UndertaleRoom.Tile? tile in room.Tiles)
                Add(tile?.Tpag);
            foreach (UndertaleRoom.GameObject? gameObject in room.GameObjects)
                AddGameObject(gameObject!);
        }
        else
        {
            foreach (UndertaleRoom.Layer layer in room.Layers)
            {
                switch (layer.LayerType)
                {
                    case UndertaleRoom.LayerType.Background:
                        Add(layer.BackgroundData?.Sprite?.Textures?.ElementAtOrDefault((int)layer.BackgroundData.FirstFrame)?.Texture);
                        break;
                    case UndertaleRoom.LayerType.Instances:
                        foreach (UndertaleRoom.GameObject? gameObject in layer.InstancesData.Instances)
                            AddGameObject(gameObject!);
                        break;
                    case UndertaleRoom.LayerType.Assets:
                        AddTiles(layer.AssetsData.LegacyTiles);
                        AddSpriteInstances(layer.AssetsData.Sprites);
                        break;
                    case UndertaleRoom.LayerType.Tiles:
                        Add(layer.TilesData?.Background?.Texture);
                        break;
                }
            }
        }

        return textures;
    }

    #endregion

    #region Page loading

    Task GetOrStartPageLoad(GMImage gmImage)
    {
        PageEntry entry = pageEntries.GetOrAdd(gmImage, static _ => new PageEntry());

        lock (entry.SyncRoot)
        {
            if (entry.Bitmap?.TryGetTarget(out Bitmap? target) == true && target is not null)
                return Task.CompletedTask;
            if (entry.Error is not null)
                return Task.FromException(entry.Error);
            if (entry.Loading)
                return (entry.Completion ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

            entry.Loading = true;
            entry.Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        BeginPageLoad(entry, gmImage);
        return entry.Completion.Task;
    }

    // Returns the decoded page bitmap, or null if it isn't available yet.
    //
    // Non-blocking (waitForLoad = false): returns immediately on a miss; starts a background load
    // and registers onImageReady so the caller can repaint when it completes. Safe on any thread.
    //
    // Blocking (waitForLoad = true): must NOT be called on the UI thread; waits until the load
    // finished and throws if decoding failed.
    Bitmap? ResolvePage(GMImage gmImage, bool waitForLoad, Action? onImageReady)
    {
        PageEntry entry = pageEntries.GetOrAdd(gmImage, static _ => new PageEntry());

        while (true)
        {
            lock (entry.SyncRoot)
            {
                if (entry.Bitmap?.TryGetTarget(out Bitmap? target) == true && target is not null)
                    return target;

                if (entry.Error is not null)
                {
                    if (waitForLoad)
                        throw entry.Error;
                    return null;
                }

                if (!entry.Loading)
                {
                    entry.Loading = true;
                    entry.Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                else
                {
                    if (onImageReady is not null)
                        (entry.Callbacks ??= []).Add(onImageReady);

                    if (!waitForLoad)
                        return null;

                    Monitor.Wait(entry.SyncRoot);
                    continue;
                }
            }

            BeginPageLoad(entry, gmImage);

            if (!waitForLoad)
            {
                // The load was just started; behave like any other in-flight page.
                return ResolvePage(gmImage, false, onImageReady);
            }
        }
    }

    void BeginPageLoad(PageEntry entry, GMImage gmImage)
    {
        _ = Task.Run(async () =>
        {
            byte[]? bgra = null;
            int width = 0, height = 0;
            Exception? error = null;

            try
            {
                await DecodeGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    using MagickImage image = gmImage.GetMagickImage();
                    image.Alpha(AlphaOption.Set);
                    image.Format = MagickFormat.Bgra;
                    image.Depth = 8;
                    image.SetCompression(CompressionMethod.NoCompression);

                    width = (int)image.Width;
                    height = (int)image.Height;
                    bgra = image.ToByteArray();
                }
                finally
                {
                    DecodeGate.Release();
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }

            // Bitmaps are only ever touched on the UI thread.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Bitmap? bitmap = null;
                try
                {
                    if (error is null && bgra is not null)
                        bitmap = CreateBitmap(bgra, width, height);
                }
                catch
                {
                    bitmap = null;
                }

                List<Action>? callbacks;
                TaskCompletionSource? completion;

                lock (entry.SyncRoot)
                {
                    entry.Error = error ?? (bitmap is null ? new InvalidOperationException("Failed to decode texture page.") : null);
                    if (bitmap is not null)
                        entry.Bitmap = new WeakReference<Bitmap>(bitmap);
                    entry.Loading = false;

                    callbacks = entry.Callbacks;
                    entry.Callbacks = null;
                    completion = entry.Completion;
                    entry.Completion = null;

                    Monitor.PulseAll(entry.SyncRoot);
                }

                if (error is null)
                    ImageLoaded?.Invoke();

                if (callbacks is not null)
                {
                    foreach (Action callback in callbacks)
                    {
                        try
                        {
                            callback();
                        }
                        catch
                        {
                            // Never let one subscriber break the others.
                        }
                    }
                }

                if (completion is not null)
                {
                    if (error is not null)
                        completion.TrySetException(error);
                    else
                        completion.TrySetResult();
                }
            });
        });
    }

    #endregion

    #region Crop cache helpers

    Bitmap? TryGetCrop(ImageKey key)
    {
        lock (cropCacheLock)
        {
            if (cropCache.TryGetValue(key, out WeakReference<Bitmap>? reference))
            {
                if (reference.TryGetTarget(out Bitmap? image))
                    return image;

                cropCache.Remove(key);
            }
        }

        return null;
    }

    void StoreCrop(ImageKey key, Bitmap image)
    {
        lock (cropCacheLock)
            cropCache[key] = new WeakReference<Bitmap>(image);
    }

    #endregion

    #region Bitmap utilities

    static T RunOnUIThread<T>(Func<T> func)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return func();
        return Dispatcher.UIThread.InvokeAsync(func).GetTask().GetAwaiter().GetResult();
    }

    // Crops a region of a decoded page. Both bitmaps are BGRA8888 WriteableBitmaps created by this
    // cache, so copying pixels directly is safe and much cheaper than going through Magick again.
    // Must be called on the UI thread.
    static Bitmap? CropBitmap(Bitmap page, int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0 || x < 0 || y < 0)
            return null;

        PixelSize pageSize = page.PixelSize;
        if (x + width > pageSize.Width || y + height > pageSize.Height)
            return null;

        WriteableBitmap result = new(new PixelSize(width, height), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Unpremul);

        using (ILockedFramebuffer src = ((WriteableBitmap)page).Lock())
        using (ILockedFramebuffer dst = result.Lock())
        {
            int stride = width * 4;
            byte[] rowBuffer = new byte[stride];

            for (int row = 0; row < height; row++)
            {
                Marshal.Copy(IntPtr.Add(src.Address, (y + row) * src.RowBytes + x * 4), rowBuffer, 0, stride);
                Marshal.Copy(rowBuffer, 0, IntPtr.Add(dst.Address, row * dst.RowBytes), stride);
            }
        }

        return result;
    }

    // Must be called on the UI thread.
    static Bitmap CreateBitmap(byte[] bgra, int width, int height)
    {
        WriteableBitmap bitmap = new(new PixelSize(width, height), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Unpremul);

        using (ILockedFramebuffer framebuffer = bitmap.Lock())
        {
            int stride = width * 4;
            nint address = framebuffer.Address;

            if (framebuffer.RowBytes == stride)
            {
                Marshal.Copy(bgra, 0, address, bgra.Length);
            }
            else
            {
                for (int row = 0; row < height; row++)
                    Marshal.Copy(bgra, row * stride, IntPtr.Add(address, row * framebuffer.RowBytes), stride);
            }
        }

        return bitmap;
    }

    static Bitmap ToBitmap(IMagickImage<byte> image)
    {
        image.Alpha(AlphaOption.Set);

        byte[] data = image.ToByteArray();
        return CreateBitmap(data, (int)image.Width, (int)image.Height);
    }

    #endregion
}
