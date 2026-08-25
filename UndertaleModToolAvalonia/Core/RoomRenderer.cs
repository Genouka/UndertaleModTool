using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ImageMagick;
using Microsoft.Extensions.DependencyInjection;
using UndertaleModLib.Models;
using UndertaleModLib.Util;
using static UndertaleModToolAvalonia.RoomRenderer.RenderCommandsBuilder;

namespace UndertaleModToolAvalonia;

public class RoomRenderer
{
    public class RenderCommandsBuilder
    {
        public interface IRenderCommand;

        public readonly record struct NinePatchData(int Left, int Top, int Right, int Bottom,
            UndertaleSprite.NineSlice.TileMode TileModeCenter,
            UndertaleSprite.NineSlice.TileMode TileModeLeft,
            UndertaleSprite.NineSlice.TileMode TileModeTop,
            UndertaleSprite.NineSlice.TileMode TileModeRight,
            UndertaleSprite.NineSlice.TileMode TileModeBottom);

        public readonly record struct BackgroundColorRenderCommand(uint RoomWidth, uint RoomHeight, uint Color)
            : IRenderCommand;
        public readonly record struct BackgroundRenderCommand(IImage Image,
            ushort SourceX, ushort SourceY, ushort SourceWidth, ushort SourceHeight,
            ushort TargetX, ushort TargetY, ushort TargetWidth, ushort TargetHeight, ushort BoundingWidth, ushort BoundingHeight,
            float X, float Y, float ScaleX, float ScaleY, uint Color, bool TiledHorizontally, bool TiledVertically, uint RoomWidth, uint RoomHeight)
            : IRenderCommand;
        public readonly record struct TileRenderCommand(IImage Image,
            ushort SourceX, ushort SourceY, ushort SourceWidth, ushort SourceHeight,
            ushort TargetX, ushort TargetY, ushort TargetWidth, ushort TargetHeight,
            int TileSourceX, int TileSourceY, float X, float Y, float ScaleX, float ScaleY)
            : IRenderCommand;
        public readonly record struct GameObjectRenderCommand(IImage Image,
            ushort SourceX, ushort SourceY, ushort SourceWidth, ushort SourceHeight,
            ushort TargetX, ushort TargetY, ushort TargetWidth, ushort TargetHeight, ushort BoundingWidth, ushort BoundingHeight,
            int X, int Y, float ScaleX, float ScaleY, uint Color, float Rotation, int OriginX, int OriginY,
            NinePatchData? NinePatch)
            : IRenderCommand;
        public readonly record struct SpriteRenderCommand(IImage Image,
            ushort SourceX, ushort SourceY, ushort SourceWidth, ushort SourceHeight,
            ushort TargetX, ushort TargetY, ushort TargetWidth, ushort TargetHeight,
            float X, float Y, float ScaleX, float ScaleY, uint Color, float Rotation, int OriginX, int OriginY)
            : IRenderCommand;
        public readonly record struct LayerTilesRenderCommand(IImage Image,
            ushort SourceX, ushort SourceY, ushort SourceWidth, ushort SourceHeight,
            ushort TargetX, ushort TargetY, ushort TargetWidth, ushort TargetHeight,
            float X, float Y, uint[][] TileData, uint TileDataW, uint TileDataH, uint TileColumns, uint TileW, uint TileH, uint OutputBorderX, uint OutputBorderY)
            : IRenderCommand;

        public readonly UndertaleRoom Room;
        public readonly List<IRenderCommand> RenderCommands = [];

        readonly MainViewModel mainVM = App.Services.GetRequiredService<MainViewModel>();
        readonly Action? onImageReady;
        readonly bool waitForImages;

        // onImageReady: invoked (on the UI thread) whenever an image that was still loading in the
        // background becomes available, so the caller can repaint. waitForImages: block until all
        // needed images are decoded - only valid off the UI thread (used by exports).
        public RenderCommandsBuilder(UndertaleRoom room, Action? onImageReady = null, bool waitForImages = false)
        {
            Room = room;
            this.onImageReady = onImageReady;
            this.waitForImages = waitForImages;

            if (!(Room.Flags.HasFlag(UndertaleRoom.RoomEntryFlags.IsGMS2) || Room.Flags.HasFlag(UndertaleRoom.RoomEntryFlags.IsGM2024_13)))
            {
                AddBackgroundColor(Room.BackgroundColor);
                AddBackgrounds(Room.Backgrounds, foregrounds: false);
                // TODO: Order tiles and game objects by depth
                AddTiles(Room.Tiles);
                AddGameObjects(Room.GameObjects);
                AddBackgrounds(Room.Backgrounds, foregrounds: true);
            }
            else
            {
                IOrderedEnumerable<UndertaleRoom.Layer> layers = Room.Layers.Reverse().OrderByDescending(x => x.LayerDepth);
                foreach (UndertaleRoom.Layer layer in layers)
                {
                    if (!layer.IsVisible)
                        continue;

                    switch (layer.LayerType)
                    {
                        case UndertaleRoom.LayerType.Path:
                        case UndertaleRoom.LayerType.Path2:
                            break;
                        case UndertaleRoom.LayerType.Background:
                            AddLayerBackground(layer);
                            break;
                        case UndertaleRoom.LayerType.Instances:
                            AddGameObjects(layer.InstancesData.Instances);
                            break;
                        case UndertaleRoom.LayerType.Assets:
                            AddTiles(layer.AssetsData.LegacyTiles, layer);
                            AddSprites(layer.AssetsData.Sprites, layer);
                            // layer.AssetsData.Sequences
                            // layer.AssetsData.NineSlices
                            // layer.AssetsData.ParticleSystems
                            // layer.AssetsData.TextItems
                            break;
                        case UndertaleRoom.LayerType.Tiles:
                            AddLayerTiles(layer);
                            break;
                            //case UndertaleRoom.LayerType.Effect:
                            // layer.EffectData
                            //break;
                    }
                }
            }
        }

        IImage? GetCachedImage(UndertaleTexturePageItem texture)
            => mainVM.ImageCache.GetCachedImageFromTexturePageItem(texture, onImageReady, waitForImages);

        IImage? GetCachedImage(UndertaleRoom.Tile tile)
            => mainVM.ImageCache.GetCachedImageFromTile(tile, onImageReady, waitForImages);

        IImage? GetCachedImage(GMImage image)
            => mainVM.ImageCache.GetCachedImageFromGMImage(image, onImageReady, waitForImages);

        void AddBackgroundColor(uint color)
        {
            RenderCommands.Add(new BackgroundColorRenderCommand(
                RoomWidth: Room.Width,
                RoomHeight: Room.Height,
                Color: color
            ));
        }

        void AddBackgrounds(IList<UndertaleRoom.Background> roomBackgrounds, bool foregrounds)
        {
            foreach (UndertaleRoom.Background roomBackground in roomBackgrounds)
            {
                if (roomBackground.Foreground == foregrounds)
                {
                    if (!roomBackground.Enabled)
                        continue;

                    UndertaleTexturePageItem? texture = roomBackground.BackgroundDefinition?.Texture;
                    if (texture is null)
                        continue;

                    IImage? image = GetCachedImage(texture);
                    if (image is null)
                        continue;

                    roomBackground.UpdateStretch();

                    RenderCommands.Add(new BackgroundRenderCommand(
                       Image: image,
                       SourceX: texture.SourceX,
                       SourceY: texture.SourceY,
                       SourceWidth: texture.SourceWidth,
                       SourceHeight: texture.SourceHeight,
                       TargetX: texture.TargetX,
                       TargetY: texture.TargetY,
                       TargetWidth: texture.TargetWidth,
                       TargetHeight: texture.TargetHeight,
                       BoundingWidth: texture.BoundingWidth,
                       BoundingHeight: texture.BoundingHeight,
                       X: roomBackground.X,
                       Y: roomBackground.Y,
                       ScaleX: roomBackground.CalcScaleX,
                       ScaleY: roomBackground.CalcScaleY,
                       Color: 0xFFFFFFFF,
                       TiledHorizontally: roomBackground.TiledHorizontally,
                       TiledVertically: roomBackground.TiledVertically,
                       RoomWidth: Room.Width,
                       RoomHeight: Room.Height
                    ));
                }
            }
        }

        void AddTiles(IList<UndertaleRoom.Tile> roomTiles, UndertaleRoom.Layer? layer = null)
        {
            IOrderedEnumerable<UndertaleRoom.Tile> orderedRoomTiles = roomTiles.OrderByDescending(x => x.TileDepth);
            foreach (UndertaleRoom.Tile roomTile in orderedRoomTiles)
            {
                IImage? image = GetCachedImage(roomTile);
                if (image is null)
                    continue;

                UndertaleTexturePageItem? texture = roomTile.Tpag;
                if (texture is null)
                    continue;

                RenderCommands.Add(new TileRenderCommand(
                    Image: image,
                    SourceX: texture.SourceX,
                    SourceY: texture.SourceY,
                    SourceWidth: texture.SourceWidth,
                    SourceHeight: texture.SourceHeight,
                    TargetX: texture.TargetX,
                    TargetY: texture.TargetY,
                    TargetWidth: texture.TargetWidth,
                    TargetHeight: texture.TargetHeight,
                    TileSourceX: roomTile.SourceX,
                    TileSourceY: roomTile.SourceY,
                    X: (layer?.XOffset ?? 0) + roomTile.X - Math.Min(roomTile.SourceX - texture.TargetX, 0),
                    Y: (layer?.YOffset ?? 0) + roomTile.Y - Math.Min(roomTile.SourceX - texture.TargetX, 0),
                    ScaleX: roomTile.ScaleX,
                    ScaleY: roomTile.ScaleY
                ));
            }
        }

        void AddGameObjects(IList<UndertaleRoom.GameObject> roomGameObjects)
        {
            foreach (UndertaleRoom.GameObject roomGameObject in roomGameObjects)
            {
                UndertaleTexturePageItem? texture = roomGameObject.ObjectDefinition?.Sprite?.Textures?.ElementAtOrDefault(roomGameObject.ImageIndex)?.Texture;
                if (texture is null)
                    continue;

                IImage? image = GetCachedImage(texture);
                if (image is null)
                    continue;

                // image, source xywh, target xywh, x/y offset, scale x/y, color, rotation, origin x/y
                RenderCommands.Add(new GameObjectRenderCommand(
                    Image: image,
                    SourceX: texture.SourceX,
                    SourceY: texture.SourceY,
                    SourceWidth: texture.SourceWidth,
                    SourceHeight: texture.SourceHeight,
                    TargetX: texture.TargetX,
                    TargetY: texture.TargetY,
                    TargetWidth: texture.TargetWidth,
                    TargetHeight: texture.TargetHeight,
                    BoundingWidth: texture.BoundingWidth,
                    BoundingHeight: texture.BoundingHeight,
                    X: roomGameObject.X,
                    Y: roomGameObject.Y,
                    ScaleX: roomGameObject.ScaleX,
                    ScaleY: roomGameObject.ScaleY,
                    Color: roomGameObject.Color,
                    Rotation: -roomGameObject.Rotation,
                    OriginX: roomGameObject.ObjectDefinition!.Sprite.OriginX,
                    OriginY: roomGameObject.ObjectDefinition!.Sprite.OriginY,
                    NinePatch: GetNinePatchData(roomGameObject.ObjectDefinition!.Sprite.V3NineSlice, texture)
                ));
            }
        }

        void AddSprites(IList<UndertaleRoom.SpriteInstance> roomSprites, UndertaleRoom.Layer layer)
        {
            foreach (UndertaleRoom.SpriteInstance roomSprite in roomSprites)
            {
                UndertaleTexturePageItem? texture = roomSprite.Sprite?.Textures?.ElementAtOrDefault((int)roomSprite.FrameIndex)?.Texture;
                if (texture is null)
                    continue;

                IImage? image = GetCachedImage(texture);
                if (image is null)
                    continue;

                RenderCommands.Add(new SpriteRenderCommand(
                    Image: image,
                    SourceX: texture.SourceX,
                    SourceY: texture.SourceY,
                    SourceWidth: texture.SourceWidth,
                    SourceHeight: texture.SourceHeight,
                    TargetX: texture.TargetX,
                    TargetY: texture.TargetY,
                    TargetWidth: texture.TargetWidth,
                    TargetHeight: texture.TargetHeight,
                    X: layer.XOffset + roomSprite.X,
                    Y: layer.YOffset + roomSprite.Y,
                    ScaleX: roomSprite.ScaleX,
                    ScaleY: roomSprite.ScaleY,
                    Color: roomSprite.Color,
                    Rotation: -roomSprite.Rotation,
                    OriginX: roomSprite.Sprite!.OriginX,
                    OriginY: roomSprite.Sprite!.OriginY
                ));
            }
        }

        void AddLayerBackground(UndertaleRoom.Layer layer)
        {
            if (!layer.BackgroundData.Visible)
                return;

            if (layer.BackgroundData.Sprite is null)
            {
                AddBackgroundColor(layer.BackgroundData.Color);
                return;
            }

            UndertaleTexturePageItem? texture = layer.BackgroundData.Sprite?.Textures?.ElementAtOrDefault((int)layer.BackgroundData.FirstFrame)?.Texture;
            if (texture is null)
                return;

            IImage? image = GetCachedImage(texture);
            if (image is null)
                return;

            layer.BackgroundData.UpdateScale();

            // image, source xywh, target xywh, x/y offset, scale x/y, color, tile h/v, parent w/h
            RenderCommands.Add(new BackgroundRenderCommand(
                Image: image,
                SourceX: texture.SourceX,
                SourceY: texture.SourceY,
                SourceWidth: texture.SourceWidth,
                SourceHeight: texture.SourceHeight,
                TargetX: texture.TargetX,
                TargetY: texture.TargetY,
                TargetWidth: texture.TargetWidth,
                TargetHeight: texture.TargetHeight,
                BoundingWidth: texture.BoundingWidth,
                BoundingHeight: texture.BoundingHeight,
                X: layer.XOffset,
                Y: layer.YOffset,
                ScaleX: layer.BackgroundData.CalcScaleX,
                ScaleY: layer.BackgroundData.CalcScaleY,
                Color: layer.BackgroundData.Color,
                TiledHorizontally: layer.BackgroundData.TiledHorizontally,
                TiledVertically: layer.BackgroundData.TiledVertically,
                RoomWidth: Room.Width,
                RoomHeight: Room.Height
            ));
        }

        void AddLayerTiles(UndertaleRoom.Layer layer)
        {
            UndertaleTexturePageItem? texture = layer.TilesData.Background?.Texture;
            if (texture is null)
                return;

            GMImage? gmImage = texture.TexturePage?.TextureData?.Image;
            if (gmImage is null)
                return;

            IImage? image = GetCachedImage(gmImage);
            if (image is null)
                return;

            // image, source xywh, target xywh, x/y offset, tilesdata, tile columns, tile w/h, border x/y
            RenderCommands.Add(new LayerTilesRenderCommand(
                Image: image,
                SourceX: texture.SourceX,
                SourceY: texture.SourceY,
                SourceWidth: texture.SourceWidth,
                SourceHeight: texture.SourceHeight,
                TargetX: texture.TargetX,
                TargetY: texture.TargetY,
                TargetWidth: texture.TargetWidth,
                TargetHeight: texture.TargetHeight,
                X: layer.XOffset,
                Y: layer.YOffset,
                TileData: layer.TilesData.TileData.Select(x => x.ToArray()).ToArray(),
                TileDataW: layer.TilesData.TilesX,
                TileDataH: layer.TilesData.TilesY,
                TileColumns: layer.TilesData.Background!.GMS2TileColumns,
                TileW: layer.TilesData.Background!.GMS2TileWidth,
                TileH: layer.TilesData.Background!.GMS2TileHeight,
                OutputBorderX: layer.TilesData.Background!.GMS2OutputBorderX,
                OutputBorderY: layer.TilesData.Background!.GMS2OutputBorderY
            ));
        }

        static NinePatchData? GetNinePatchData(UndertaleSprite.NineSlice? nineSlice, UndertaleTexturePageItem texturePageItem)
        {
            if (nineSlice is null)
                return null;

            return new(
                nineSlice.Left - texturePageItem.TargetX,
                nineSlice.Top - texturePageItem.TargetY,
                texturePageItem.BoundingWidth - nineSlice.Right - texturePageItem.TargetX,
                texturePageItem.BoundingHeight - nineSlice.Bottom - texturePageItem.TargetY,
                nineSlice.TileModes[0],
                nineSlice.TileModes[1],
                nineSlice.TileModes[2],
                nineSlice.TileModes[3],
                nineSlice.TileModes[4]);
        }
    }

    public void RenderCommands(List<RenderCommandsBuilder.IRenderCommand> renderCommands, DrawingContext context)
    {
        foreach (var renderCommand in renderCommands)
        {
            switch (renderCommand)
            {
                case BackgroundColorRenderCommand c:
                    RenderBackgroundColorRenderCommand(c, context);
                    break;
                case BackgroundRenderCommand c:
                    RenderBackgroundRenderCommand(c, context);
                    break;
                case TileRenderCommand c:
                    RenderTileRenderCommand(c, context);
                    break;
                case GameObjectRenderCommand c:
                    RenderGameObjectRenderCommand(c, context);
                    break;
                case SpriteRenderCommand c:
                    RenderSpriteRenderCommand(c, context);
                    break;
                case LayerTilesRenderCommand c:
                    RenderLayerTilesRenderCommand(c, context);
                    break;
            }
        }
    }

    public void RenderCommands(List<RenderCommandsBuilder.IRenderCommand> renderCommands, MagickImage canvas)
    {
        foreach (var renderCommand in renderCommands)
        {
            switch (renderCommand)
            {
                case BackgroundColorRenderCommand c:
                    RenderBackgroundColorRenderCommand(c, canvas);
                    break;
                case BackgroundRenderCommand c:
                    RenderBackgroundRenderCommand(c, canvas);
                    break;
                case TileRenderCommand c:
                    RenderTileRenderCommand(c, canvas);
                    break;
                case GameObjectRenderCommand c:
                    RenderGameObjectRenderCommand(c, canvas);
                    break;
                case SpriteRenderCommand c:
                    RenderSpriteRenderCommand(c, canvas);
                    break;
                case LayerTilesRenderCommand c:
                    RenderLayerTilesRenderCommand(c, canvas);
                    break;
            }
        }
    }

    // DrawingContext rendering

    void RenderBackgroundColorRenderCommand(BackgroundColorRenderCommand c, DrawingContext ctx)
    {
        ctx.FillRectangle(new SolidColorBrush(UndertaleColor.ToColor(c.Color)), new Rect(0, 0, c.RoomWidth, c.RoomHeight));
    }

    void RenderBackgroundRenderCommand(BackgroundRenderCommand c, DrawingContext ctx)
    {
        var w = c.BoundingWidth * c.ScaleX;
        var h = c.BoundingHeight * c.ScaleY;

        var startX = c.TiledHorizontally ? ((c.X % w) - w) : c.X;
        var startY = c.TiledVertically ? ((c.Y % h) - h) : c.Y;

        var endX = c.TiledHorizontally ? c.RoomWidth : (startX + w);
        var endY = c.TiledVertically ? c.RoomHeight : (startY + h);

        using (c.TiledHorizontally || c.TiledVertically ? ctx.PushClip(new Rect(0, 0, c.RoomWidth, c.RoomHeight)) : default)
        {
            for (var x = startX; x < endX; x += w)
            {
                for (var y = startY; y < endY; y += h)
                {
                    using (ctx.PushTransform(Matrix.CreateTranslation(x, y)))
                    using (ctx.PushTransform(Matrix.CreateScale(c.ScaleX, c.ScaleY)))
                    {
                        DrawTinted(ctx, c.Image, new Rect(c.TargetX, c.TargetY, c.TargetWidth, c.TargetHeight), c.Color);
                    }
                }
            }
        }
    }

    void RenderTileRenderCommand(TileRenderCommand c, DrawingContext ctx)
    {
        double width = c.Image.Size.Width;
        double height = c.Image.Size.Height;

        using (ctx.PushTransform(Matrix.CreateTranslation(c.X, c.Y)))
        using (ctx.PushTransform(Matrix.CreateScale(c.ScaleX, c.ScaleY)))
        {
            ctx.DrawImage(c.Image, new Rect(0, 0, width, height), new Rect(0, 0, width, height));
        }
    }

    void RenderGameObjectRenderCommand(GameObjectRenderCommand c, DrawingContext ctx)
    {
        using (ctx.PushTransform(Matrix.CreateTranslation(c.X, c.Y)))
        using (ctx.PushTransform(Matrix.CreateRotation(c.Rotation * (Math.PI / 180.0))))
        {
            if (c.NinePatch is NinePatchData ninePatch)
            {
                // Width = scaled bounding width - unscaled left + right side (which is bounding width - target width), similarly for height
                double destX = (-c.OriginX * c.ScaleX) + c.TargetX;
                double destY = (-c.OriginY * c.ScaleY) + c.TargetY;
                double destW = (c.BoundingWidth * c.ScaleX) - (c.BoundingWidth - c.TargetWidth);
                double destH = (c.BoundingHeight * c.ScaleY) - (c.BoundingHeight - c.TargetHeight);

                double l = Math.Max(ninePatch.Left, 0);
                double t = Math.Max(ninePatch.Top, 0);
                double r = Math.Min(ninePatch.Right, c.Image.Size.Width);
                double b = Math.Min(ninePatch.Bottom, c.Image.Size.Height);

                DrawNinePatch(ctx, c.Image, l, t, r, b, destX, destY, destW, destH, c.Color);
            }
            else
            {
                using (ctx.PushTransform(Matrix.CreateScale(c.ScaleX, c.ScaleY)))
                {
                    double destX = -c.OriginX + c.TargetX;
                    double destY = -c.OriginY + c.TargetY;
                    DrawTinted(ctx, c.Image, new Rect(destX, destY, c.TargetWidth, c.TargetHeight), c.Color);
                }
            }
        }
    }

    void RenderSpriteRenderCommand(SpriteRenderCommand c, DrawingContext ctx)
    {
        using (ctx.PushTransform(Matrix.CreateTranslation(c.X, c.Y)))
        using (ctx.PushTransform(Matrix.CreateRotation(c.Rotation * (Math.PI / 180.0))))
        using (ctx.PushTransform(Matrix.CreateScale(c.ScaleX, c.ScaleY)))
        {
            DrawTinted(ctx, c.Image, new Rect(-c.OriginX + c.TargetX, -c.OriginY + c.TargetY, c.TargetWidth, c.TargetHeight), c.Color);
        }
    }

    void RenderLayerTilesRenderCommand(LayerTilesRenderCommand c, DrawingContext ctx)
    {
        for (uint y = 0; y < c.TileDataH; y++)
        {
            uint[] row = y < c.TileData.Length ? c.TileData[y] : null!;
            if (row is null)
                continue;

            for (uint x = 0; x < c.TileDataW && x < row.Length; x++)
            {
                uint tile = row[x];
                uint tileId = tile & UndertaleRoomViewModel.TILE_ID;

                if (tileId == 0)
                    continue;

                uint tileOrientation = tile >> 28;

                float posX = c.X + (x * c.TileW);
                float posY = c.Y + (y * c.TileH);

                uint tileX = tileId % c.TileColumns;
                uint tileY = tileId / c.TileColumns;

                float xx = c.SourceX + (tileX * (c.TileW + c.OutputBorderX * 2) + c.OutputBorderX);
                float yy = c.SourceY + (tileY * (c.TileH + c.OutputBorderY * 2) + c.OutputBorderY);

                Matrix m = Matrix.Identity;
                if ((tileOrientation & 1) != 0)
                    m = m * new Matrix(-1, 0, 0, 1, c.TileW, 0);
                if (((tileOrientation >> 1) & 1) != 0)
                    m = m * new Matrix(1, 0, 0, -1, 0, c.TileH);
                if (((tileOrientation >> 2) & 1) != 0)
                    m = m * new Matrix(0, 1, -1, 0, c.TileH, 0);

                using (ctx.PushClip(new Rect(posX, posY, c.TileW, c.TileH)))
                using (ctx.PushTransform(Matrix.CreateTranslation(posX, posY) * m))
                {
                    ctx.DrawImage(c.Image, new Rect(xx, yy, c.TileW, c.TileH), new Rect(0, 0, c.TileW, c.TileH));
                }
            }
        }
    }

    static void DrawTinted(DrawingContext ctx, IImage image, Rect destination, uint tint)
    {
        ctx.DrawImage(image, destination, destination);

        if (tint != 0xFFFFFFFF)
        {
            Color color = UndertaleColor.ToColor(tint);
            if (color.A > 0)
            {
                Color overlay = new((byte)(color.A / 2), color.R, color.G, color.B);
                ctx.FillRectangle(new SolidColorBrush(overlay), destination);
            }
        }
    }

    static void DrawNinePatch(DrawingContext ctx, IImage image, double l, double t, double r, double b,
        double destX, double destY, double destW, double destH, uint tint)
    {
        double imageW = image.Size.Width;
        double imageH = image.Size.Height;

        // Clamp the source slice positions into the image bounds
        l = Math.Min(Math.Max(l, 0), imageW);
        r = Math.Min(Math.Max(r, 0), imageW);
        t = Math.Min(Math.Max(t, 0), imageH);
        b = Math.Min(Math.Max(b, 0), imageH);

        double[] srcCols = { 0, l, r, imageW };
        double[] srcRows = { 0, t, b, imageH };
        double[] destCols = { 0, l, destW - (imageW - r), destW };
        double[] destRows = { 0, t, destH - (imageH - b), destH };

        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                double sw = srcCols[col + 1] - srcCols[col];
                double sh = srcRows[row + 1] - srcRows[row];
                double dw = destCols[col + 1] - destCols[col];
                double dh = destRows[row + 1] - destRows[row];

                if (dw <= 0 || dh <= 0)
                    continue;

                DrawTinted(ctx, image,
                    new Rect(destX + destCols[col], destY + destRows[row], dw, dh),
                    tint);
            }
        }
    }

    // Magick.NET rendering (for exports)

    void RenderBackgroundColorRenderCommand(BackgroundColorRenderCommand c, MagickImage canvas)
    {
        Color color = UndertaleColor.ToColor(c.Color);
        using var fill = new MagickImage(MagickColor.FromRgba(color.R, color.G, color.B, color.A), c.RoomWidth, c.RoomHeight);
        canvas.Composite(fill, 0, 0, CompositeOperator.Copy);
    }

    void RenderBackgroundRenderCommand(BackgroundRenderCommand c, MagickImage canvas)
    {
        double w = c.BoundingWidth * c.ScaleX;
        double h = c.BoundingHeight * c.ScaleY;

        double startX = c.TiledHorizontally ? ((c.X % w) - w) : c.X;
        double startY = c.TiledVertically ? ((c.Y % h) - h) : c.Y;

        double endX = c.TiledHorizontally ? c.RoomWidth : (startX + w);
        double endY = c.TiledVertically ? c.RoomHeight : (startY + h);

        using var magick = ToMagick(c.Image);

        for (double x = startX; x < endX; x += w)
        {
            for (double y = startY; y < endY; y += h)
            {
                CompositeRegion(canvas, magick,
                    c.TargetX, c.TargetY, c.TargetWidth, c.TargetHeight,
                    x, y, c.ScaleX, c.ScaleY, c.Color);
            }
        }
    }

    void RenderTileRenderCommand(TileRenderCommand c, MagickImage canvas)
    {
        using var magick = ToMagick(c.Image);
        CompositeRegion(canvas, magick,
            0, 0, c.Image.Size.Width, c.Image.Size.Height,
            c.X, c.Y, c.ScaleX, c.ScaleY, 0xFFFFFFFF);
    }

    void RenderGameObjectRenderCommand(GameObjectRenderCommand c, MagickImage canvas)
    {
        using var magick = ToMagick(c.Image);

        if (c.NinePatch is NinePatchData ninePatch)
        {
            double destX = (-c.OriginX * c.ScaleX) + c.TargetX;
            double destY = (-c.OriginY * c.ScaleY) + c.TargetY;
            double destW = (c.BoundingWidth * c.ScaleX) - (c.BoundingWidth - c.TargetWidth);
            double destH = (c.BoundingHeight * c.ScaleY) - (c.BoundingHeight - c.TargetHeight);

            double imageW = c.Image.Size.Width;
            double imageH = c.Image.Size.Height;

            double l = Math.Min(Math.Max(ninePatch.Left, 0), imageW);
            double t = Math.Min(Math.Max(ninePatch.Top, 0), imageH);
            double r = Math.Min(Math.Max(ninePatch.Right, 0), imageW);
            double b = Math.Min(Math.Max(ninePatch.Bottom, 0), imageH);

            double[] srcCols = { 0, l, r, imageW };
            double[] srcRows = { 0, t, b, imageH };
            double[] destCols = { 0, l, destW - (imageW - r), destW };
            double[] destRows = { 0, t, destH - (imageH - b), destH };

            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 3; col++)
                {
                    double sw = srcCols[col + 1] - srcCols[col];
                    double sh = srcRows[row + 1] - srcRows[row];
                    double dw = destCols[col + 1] - destCols[col];
                    double dh = destRows[row + 1] - destRows[row];

                    if (dw <= 0 || dh <= 0)
                        continue;

                    CompositeRegion(canvas, magick,
                        srcCols[col], srcRows[row], sw, sh,
                        destX + destCols[col], destY + destRows[row],
                        dw / sw, dh / sh, c.Color);
                }
            }
        }
        else
        {
            CompositeRotated(canvas, magick, c.TargetWidth, c.TargetHeight,
                c.X, c.Y, c.ScaleX, c.ScaleY, c.TargetX, c.TargetY, c.OriginX, c.OriginY, c.Rotation, c.Color);
        }
    }

    void RenderSpriteRenderCommand(SpriteRenderCommand c, MagickImage canvas)
    {
        using var magick = ToMagick(c.Image);
        CompositeRotated(canvas, magick, c.TargetWidth, c.TargetHeight,
            c.X, c.Y, c.ScaleX, c.ScaleY, c.TargetX, c.TargetY, c.OriginX, c.OriginY, c.Rotation, c.Color);
    }

    void RenderLayerTilesRenderCommand(LayerTilesRenderCommand c, MagickImage canvas)
    {
        using var magick = ToMagick(c.Image);

        for (uint y = 0; y < c.TileDataH; y++)
        {
            uint[] row = y < c.TileData.Length ? c.TileData[y] : null!;
            if (row is null)
                continue;

            for (uint x = 0; x < c.TileDataW && x < row.Length; x++)
            {
                uint tile = row[x];
                uint tileId = tile & UndertaleRoomViewModel.TILE_ID;

                if (tileId == 0)
                    continue;

                uint tileOrientation = tile >> 28;

                float posX = c.X + (x * c.TileW);
                float posY = c.Y + (y * c.TileH);

                uint tileX = tileId % c.TileColumns;
                uint tileY2 = tileId / c.TileColumns;

                float xx = c.SourceX + (tileX * (c.TileW + c.OutputBorderX * 2) + c.OutputBorderX);
                float yy = c.SourceY + (tileY2 * (c.TileH + c.OutputBorderY * 2) + c.OutputBorderY);

                using MagickImage tileImage = (MagickImage)magick.CloneArea((int)xx, (int)yy, c.TileW, c.TileH);

                if ((tileOrientation & 1) != 0)
                    tileImage.Flop();
                if (((tileOrientation >> 1) & 1) != 0)
                    tileImage.Flip();
                if (((tileOrientation >> 2) & 1) != 0)
                    tileImage.Rotate(90);

                canvas.Composite(tileImage, (int)posX, (int)posY, CompositeOperator.Over);
            }
        }
    }

    static MagickImage ToMagick(IImage image)
    {
        using var stream = new System.IO.MemoryStream();
        ((Bitmap)image).Save(stream, new PngBitmapEncoderOptions());
        stream.Position = 0;
        MagickImage magick = new(stream, new MagickReadSettings() { ColorSpace = ColorSpace.sRGB });
        magick.Format = MagickFormat.Bgra;
        magick.Depth = 8;
        magick.SetCompression(CompressionMethod.NoCompression);
        return magick;
    }

    static void CompositeRegion(MagickImage canvas, MagickImage src,
        double srcX, double srcY, double srcW, double srcH,
        double destX, double destY, double scaleX, double scaleY, uint tint)
    {
        using MagickImage region = (MagickImage)src.CloneArea((int)srcX, (int)srcY, (uint)Math.Max(1, srcW), (uint)Math.Max(1, srcH));

        int w = (int)Math.Max(1, Math.Round(srcW * scaleX));
        int h = (int)Math.Max(1, Math.Round(srcH * scaleY));

        if (region.Width != w || region.Height != h)
            region.InterpolativeResize((uint)w, (uint)h, PixelInterpolateMethod.Bilinear);

        ApplyTint(region, tint);

        canvas.Composite(region, (int)Math.Round(destX), (int)Math.Round(destY), CompositeOperator.Over);
    }

    static void CompositeRotated(MagickImage canvas, MagickImage src,
        double srcW, double srcH, double posX, double posY,
        double scaleX, double scaleY, double targetX, double targetY,
        int originX, int originY, double rotationDegrees, uint tint)
    {
        using var image = (MagickImage)src.Clone();

        int w = (int)Math.Max(1, Math.Round(srcW * scaleX));
        int h = (int)Math.Max(1, Math.Round(srcH * scaleY));

        if (image.Width != w || image.Height != h)
            image.InterpolativeResize((uint)w, (uint)h, PixelInterpolateMethod.Bilinear);

        ApplyTint(image, tint);

        double rad = rotationDegrees * (Math.PI / 180.0);
        double cosA = Math.Cos(rad);
        double sinA = Math.Sin(rad);

        // The pixel that should land at (posX, posY) in room coordinates
        double pivotRelX = (targetX + originX * scaleX) - (w / 2.0);
        double pivotRelY = (targetY + originY * scaleY) - (h / 2.0);

        if (rotationDegrees != 0)
            image.Rotate(rotationDegrees);

        double pivotX = (image.Width / 2.0) + (pivotRelX * cosA - pivotRelY * sinA);
        double pivotY = (image.Height / 2.0) + (pivotRelX * sinA + pivotRelY * cosA);

        canvas.Composite(image,
            (int)Math.Round(posX + targetX - pivotX),
            (int)Math.Round(posY + targetY - pivotY),
            CompositeOperator.Over);
    }

    static void ApplyTint(IMagickImage<byte> image, uint tint)
    {
        if (tint == 0xFFFFFFFF)
            return;

        Color color = UndertaleColor.ToColor(tint);
        if (color.A == 0)
            return;

        using var overlay = new MagickImage(MagickColor.FromRgba(color.R, color.G, color.B, color.A), image.Width, image.Height);
        image.Composite(overlay, CompositeOperator.Modulate);
    }
}