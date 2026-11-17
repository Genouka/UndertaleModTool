using System;
using System.IO;
using System.Threading.Tasks;
using ImageMagick;
using Microsoft.Extensions.DependencyInjection;
using UndertaleModLib.Models;
using UndertaleModLib.Util;

namespace UndertaleModToolAvalonia;

public static class ImportExport
{
    public static async Task ImportEmbeddedAudio(UndertaleEmbeddedAudio embeddedAudio, Stream stream)
    {
        byte[] bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes);

        embeddedAudio.Data = bytes;
    }

    public static async Task ExportEmbeddedAudio(UndertaleEmbeddedAudio embeddedAudio, Stream stream)
    {
        await stream.WriteAsync(embeddedAudio.Data);
    }

    public static async Task ImportEmbeddedTexture(UndertaleEmbeddedTexture embeddedTexture, Stream stream)
    {
        byte[] bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes);

        GMImage gmImage = GMImage.FromPng(bytes, verifyHeader: true);
        gmImage.ConvertToFormat(embeddedTexture.TextureData.Image.Format);

        embeddedTexture.TextureData.Image = gmImage;
        embeddedTexture.TextureWidth = gmImage.Width;
        embeddedTexture.TextureHeight = gmImage.Height;
    }

    public static async Task ExportEmbeddedTexture(UndertaleEmbeddedTexture embeddedTexture, Stream stream)
    {
        using MagickImage image = embeddedTexture.TextureData.Image.GetMagickImage();
        await stream.WriteAsync(image.ToByteArray());
    }

    public static async Task ExportEmbeddedTextureAsPNG(UndertaleEmbeddedTexture embeddedTexture, Stream stream)
    {
        embeddedTexture.TextureData.Image.SavePng(stream);
        await Task.CompletedTask;
    }

    public static async Task ExportRoomAsPNG(UndertaleRoom room, Stream stream)
    {
        // NOTE: This is a CPU bitmap, unlike the rendering done for the UI preview.
        using MagickImage image = new(MagickColors.Transparent, room.Width, room.Height);

        // Warm the image cache in the background, then render on a worker thread (with blocking
        // image loads) so the UI thread is never stalled by texture decoding.
        await App.Services.GetRequiredService<MainViewModel>().ImageCache.PreloadAsync(room);
        await Task.Run(() =>
        {
            RoomRenderer renderer = new();
            renderer.RenderCommands(new RoomRenderer.RenderCommandsBuilder(room, waitForImages: true).RenderCommands, image);
        });

        image.Format = MagickFormat.Png;
        await image.WriteAsync(stream);
    }

    public static async Task ImportSpriteCollisionMaskData(UndertaleSprite sprite, int collisionMaskIndex, Stream stream, MainViewModel mainVM)
    {
        byte[] bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes);

        (int width, int height) = sprite.CalculateMaskDimensions(mainVM.Data);
        UndertaleSprite.MaskEntry maskEntry = new(bytes, width, height);

        sprite.CollisionMasks[collisionMaskIndex] = maskEntry;
    }

    public static async Task ExportSpriteCollisionMaskData(UndertaleSprite sprite, int collisionMaskIndex, Stream stream)
    {
        await stream.WriteAsync(sprite.CollisionMasks[collisionMaskIndex].Data);
    }

    public static async Task ImportTexturePageItem(UndertaleTexturePageItem texturePageItem, Stream stream)
    {
        using MagickImage image = ReadBGRAImageFromStream(stream);

        var format = texturePageItem.TexturePage.TextureData.Image.Format;
        if (format == GMImage.ImageFormat.Dds)
            throw new InvalidOperationException("Can't import into DDS texture");

        if (image.Width != texturePageItem.SourceWidth || image.Height != texturePageItem.SourceHeight)
            throw new InvalidOperationException($"Size of image ({image.Width},{image.Height}) does not match texture page item source size ({(texturePageItem.SourceWidth)},{texturePageItem.SourceHeight})");

        texturePageItem.ReplaceTexture(image);
    }

    public static async Task ExportTexturePageItemAsPNG(UndertaleTexturePageItem texturePageItem, Stream stream, bool includePadding)
    {
        using var textureWorker = new TextureWorker();
        using IMagickImage<byte> image = textureWorker.GetTextureFor(texturePageItem, texturePageItem.Name.Content, includePadding);
        image.Write(stream, MagickFormat.Png);
        await Task.CompletedTask;
    }

    static MagickImage ReadBGRAImageFromStream(Stream stream)
    {
        MagickReadSettings settings = new()
        {
            ColorSpace = ColorSpace.sRGB,
        };
        MagickImage image = new(stream, settings);
        image.Alpha(AlphaOption.Set);
        image.Format = MagickFormat.Bgra;
        image.Depth = 8;
        image.SetCompression(CompressionMethod.NoCompression);
        return image;
    }
}