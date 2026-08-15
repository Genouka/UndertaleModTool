using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ImageMagick;
using UndertaleModLib.Models;
using UndertaleModLib.Util;

namespace UndertaleModToolAvalonia;

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

    readonly Dictionary<ImageKey, WeakReference<Bitmap>> imageCache = [];

    public Bitmap? GetImageFromGMImage(GMImage gmImage)
    {
        using MagickImage image = gmImage.GetMagickImage();
        return ToBitmap(image);
    }

    public Bitmap? GetCachedImageFromGMImage(GMImage gmImage)
    {
        GMImageImageKey key = new(gmImage);

        Bitmap? image = null;
        if (imageCache.TryGetValue(key, out var reference))
            reference.TryGetTarget(out image);

        if (image is null)
        {
            image = GetImageFromGMImage(gmImage);
            if (image is not null)
                imageCache[key] = new WeakReference<Bitmap>(image);
        }

        return image;
    }

    public Bitmap? GetCachedImageFromTexturePageItem(UndertaleTexturePageItem texturePageItem)
    {
        if (texturePageItem.TexturePage is null
            || texturePageItem.TexturePage.TextureData is null
            || texturePageItem.TexturePage.TextureData.Image is null)
            return null;

        TexturePageItemImageKey key = new(
            texturePageItem.TexturePage.TextureData.Image,
            texturePageItem.SourceX,
            texturePageItem.SourceY,
            texturePageItem.SourceWidth,
            texturePageItem.SourceHeight);

        Bitmap? image = null;
        if (imageCache.TryGetValue(key, out var reference))
            reference.TryGetTarget(out image);

        if (image is null)
        {
            GMImage gmImage = texturePageItem.TexturePage.TextureData.Image;

            if (texturePageItem.SourceX + texturePageItem.SourceWidth > gmImage.Width
                || texturePageItem.SourceY + texturePageItem.SourceHeight > gmImage.Height)
                return null;

            image = CropImage(gmImage,
                texturePageItem.SourceX, texturePageItem.SourceY,
                texturePageItem.SourceWidth, texturePageItem.SourceHeight);

            if (image is null)
                return null;

            imageCache[key] = new WeakReference<Bitmap>(image);
        }

        return image;
    }

    public Bitmap? GetCachedImageFromTile(UndertaleRoom.Tile tile)
    {
        if (tile.Tpag is null || tile.Tpag.TexturePage is null || tile.Width == 0 || tile.Height == 0)
            return null;

        TileImageKey key = new(
            tile.Tpag.TexturePage.TextureData.Image,
            tile.Tpag.SourceX,
            tile.Tpag.SourceY,
            tile.Tpag.TargetX,
            tile.Tpag.TargetY,
            tile.SourceX,
            tile.SourceY,
            tile.Width,
            tile.Height);

        Bitmap? image = null;
        if (imageCache.TryGetValue(key, out var reference))
            reference.TryGetTarget(out image);

        if (image is null)
        {
            // Don't allow tile to exceed texture page item's borders
            int l = tile.Tpag.SourceX + Math.Max(0, tile.SourceX - tile.Tpag.TargetX);
            int t = tile.Tpag.SourceY + Math.Max(0, tile.SourceY - tile.Tpag.TargetY);
            int r = (int)Math.Min(l + tile.Width, tile.Tpag.SourceX + tile.Tpag.SourceWidth);
            int b = (int)Math.Min(t + tile.Height, tile.Tpag.SourceY + tile.Tpag.SourceHeight);

            if (l >= r || t >= b)
                return null;

            image = CropImage(tile.Tpag.TexturePage.TextureData.Image, l, t, r - l, b - t);

            if (image is not null)
                imageCache[key] = new WeakReference<Bitmap>(image);
        }

        return image;
    }

    public void Clear()
    {
        imageCache.Clear();
    }

    static Bitmap? CropImage(GMImage gmImage, int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return null;

        using MagickImage image = gmImage.GetMagickImage();
        using MagickImage cropped = (MagickImage)image.CloneArea(x, y, (uint)width, (uint)height);
        return ToBitmap(cropped);
    }

    static Bitmap ToBitmap(IMagickImage<byte> image)
    {
        image.Alpha(AlphaOption.Set);
        image.Format = MagickFormat.Bgra;
        image.Depth = 8;
        image.SetCompression(CompressionMethod.NoCompression);

        byte[] data = image.ToByteArray();
        int width = (int)image.Width;
        int height = (int)image.Height;

        WriteableBitmap bitmap = new(new PixelSize(width, height), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Unpremul);

        using (var framebuffer = bitmap.Lock())
        {
            IntPtr address = framebuffer.Address;
            int rowBytes = framebuffer.RowBytes;
            int stride = width * 4;

            if (rowBytes == stride)
            {
                Marshal.Copy(data, 0, address, data.Length);
            }
            else
            {
                for (int row = 0; row < height; row++)
                    Marshal.Copy(data, row * stride, IntPtr.Add(address, row * rowBytes), stride);
            }
        }

        return bitmap;
    }
}