using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using UndertaleModLib.Models;

namespace UndertaleModToolAvalonia;

public class LegacyTilePicker : RoomOrLegacyTilePicker
{
    public static readonly StyledProperty<UndertaleSprite?> SelectedTileSpriteProperty =
        AvaloniaProperty.Register<LegacyTilePicker, UndertaleSprite?>(nameof(SelectedTileSprite),
            defaultBindingMode: BindingMode.OneWay);

    public UndertaleSprite? SelectedTileSprite
    {
        get => GetValue(SelectedTileSpriteProperty);
        set => SetValue(SelectedTileSpriteProperty, value);
    }

    public override UndertaleTexturePageItem? GetTexturePageItem()
    {
        return SelectedTileSprite?.Textures.FirstOrDefault()?.Texture;
    }
}

public class RoomTilePicker : RoomOrLegacyTilePicker
{
    public static readonly StyledProperty<UndertaleBackground?> SelectedTileBackgroundProperty =
        AvaloniaProperty.Register<RoomTilePicker, UndertaleBackground?>(nameof(SelectedTileBackground),
            defaultBindingMode: BindingMode.OneWay);

    public UndertaleBackground? SelectedTileBackground
    {
        get => GetValue(SelectedTileBackgroundProperty);
        set => SetValue(SelectedTileBackgroundProperty, value);
    }

    public override UndertaleTexturePageItem? GetTexturePageItem()
    {
        return SelectedTileBackground?.Texture;
    }
}

public abstract class RoomOrLegacyTilePicker : TilePicker
{
    public static readonly StyledProperty<Rect?> SelectedTileSourceRectProperty =
        AvaloniaProperty.Register<RoomOrLegacyTilePicker, Rect?>(nameof(SelectedTileSourceRect),
            defaultBindingMode: BindingMode.TwoWay);

    public Rect? SelectedTileSourceRect
    {
        get => GetValue(SelectedTileSourceRectProperty);
        set => SetValue(SelectedTileSourceRectProperty, value);
    }

    public static readonly StyledProperty<uint> TileWidthProperty =
        AvaloniaProperty.Register<RoomOrLegacyTilePicker, uint>(nameof(TileWidth),
            defaultBindingMode: BindingMode.OneWay);

    public uint TileWidth
    {
        get => GetValue(TileWidthProperty);
        set => SetValue(TileWidthProperty, value);
    }

    public static readonly StyledProperty<uint> TileHeightProperty =
        AvaloniaProperty.Register<RoomOrLegacyTilePicker, uint>(nameof(TileHeight),
            defaultBindingMode: BindingMode.OneWay);

    public uint TileHeight
    {
        get => GetValue(TileHeightProperty);
        set => SetValue(TileHeightProperty, value);
    }

    public override void DrawTiles(DrawingContext context, Bitmap image)
    {
        UndertaleTexturePageItem? texturePageItem = GetTexturePageItem();
        if (texturePageItem is null)
            return;

        context.DrawImage(image,
            new Rect(texturePageItem.TargetX, texturePageItem.TargetY, texturePageItem.TargetWidth, texturePageItem.TargetHeight));

        selectedTileRect = SelectedTileSourceRect;
    }

    public abstract UndertaleTexturePageItem? GetTexturePageItem();

    public override void SelectTileAt(Point point)
    {
        UndertaleTexturePageItem? texturePageItem = GetTexturePageItem();
        if (texturePageItem is null)
            return;

        point -= translation;
        point /= scaling;

        double x = Math.Floor(point.X / TileWidth) * TileWidth;
        double y = Math.Floor(point.Y / TileHeight) * TileHeight;

        if (x < 0 || y < 0 || x + TileWidth > texturePageItem.BoundingWidth || y + TileHeight > texturePageItem.BoundingHeight)
            return;

        SelectedTileSourceRect = new(x, y, TileWidth, TileHeight);
    }

    public override void SelectTileTo(Point point)
    {
        UndertaleTexturePageItem? texturePageItem = GetTexturePageItem();
        if (texturePageItem is null)
            return;

        point -= translation;
        point /= scaling;

        double x = Math.Floor(point.X / TileWidth) * TileWidth;
        double y = Math.Floor(point.Y / TileHeight) * TileHeight;

        if (x < 0 || y < 0 || x + TileWidth > texturePageItem.BoundingWidth || y + TileHeight > texturePageItem.BoundingHeight)
            return;

        if (SelectedTileSourceRect is Rect rect)
        {
            double rectLeft = (x < rect.X) ? x : rect.Left;
            double rectTop = (y < rect.Y) ? y : rect.Top;
            double rectRight = (x >= rect.Right) ? (x + TileWidth) : rect.Right;
            double rectBottom = (y >= rect.Bottom) ? (y + TileHeight) : rect.Bottom;

            SelectedTileSourceRect = new(rectLeft, rectTop, rectRight - rectLeft, rectBottom - rectTop);
        }
        else
        {
            SelectedTileSourceRect = new(x, y, TileWidth, TileHeight);
        }
    }
}

public class LayerTilePicker : TilePicker
{
    public static readonly StyledProperty<uint> SelectedTileDataProperty =
        AvaloniaProperty.Register<LayerTilePicker, uint>(nameof(SelectedTileData),
            defaultBindingMode: BindingMode.TwoWay);

    public uint SelectedTileData
    {
        get => GetValue(SelectedTileDataProperty);
        set => SetValue(SelectedTileDataProperty, value);
    }

    public static readonly StyledProperty<uint> TileSetColumnsProperty =
        AvaloniaProperty.Register<LayerTilePicker, uint>(nameof(TileSetColumns),
            defaultBindingMode: BindingMode.TwoWay, defaultValue: 0);

    public uint TileSetColumns
    {
        get => GetValue(TileSetColumnsProperty);
        set => SetValue(TileSetColumnsProperty, value);
    }

    public override void DrawTiles(DrawingContext context, Bitmap image)
    {
        if (DataContext is not UndertaleRoom.Layer.LayerTilesData layerTilesData
            || layerTilesData.Background is not UndertaleBackground background
            || background.Texture is not UndertaleTexturePageItem texturePageItem)
            return;

        uint tileW = background.GMS2TileWidth;
        uint tileH = background.GMS2TileHeight;
        uint borderX = background.GMS2OutputBorderX;
        uint borderY = background.GMS2OutputBorderY;
        uint tileColumns = background.GMS2TileColumns;
        uint tileCount = background.GMS2TileCount;

        ushort targetX = texturePageItem.TargetX;
        ushort targetY = texturePageItem.TargetY;
        ushort sourceX = texturePageItem.SourceX;
        ushort sourceY = texturePageItem.SourceY;

        uint visualColumns = TileSetColumns != 0 ? TileSetColumns : tileColumns;

        var sx = -targetX + borderX;
        var sy = -targetY + borderY;

        uint dx = 0;
        uint dy = 0;

        var tileColumn = 0;
        var destColumn = 0;

        for (uint i = 0; i < tileCount; i++)
        {
            context.DrawImage(image,
                new Rect(sx, sy, tileW, tileH),
                new Rect(dx, dy, tileW, tileH));

            tileColumn++;
            if (tileColumn < tileColumns)
            {
                sx += tileW + borderX * 2;
            }
            else
            {
                sx = -targetX + borderX;
                sy += tileH + borderY * 2;
                tileColumn = 0;
            }

            destColumn++;
            if (destColumn < visualColumns)
            {
                dx += tileW;
            }
            else
            {
                dx = 0;
                dy += tileH;
                destColumn = 0;
            }
        }

        uint selectedTileId = SelectedTileData & UndertaleRoomViewModel.TILE_ID;

        if (selectedTileId < tileCount)
        {
            float selectedTileX = (selectedTileId % visualColumns) * tileW;
            float selectedTileY = (selectedTileId / visualColumns) * tileH;

            selectedTileRect = new Rect(selectedTileX, selectedTileY, tileW, tileH);
        }
    }

    public override void SelectTileAt(Point point)
    {
        if (DataContext is UndertaleRoom.Layer.LayerTilesData layerTilesData)
        {
            if (layerTilesData?.Background?.Texture is not null)
            {
                UndertaleBackground background = layerTilesData.Background;

                point -= translation;
                point /= scaling;

                uint x = (uint)(point.X / background.GMS2TileWidth);
                uint y = (uint)(point.Y / background.GMS2TileHeight);

                uint visualColumns = TileSetColumns != 0 ? TileSetColumns : background.GMS2TileColumns;

                uint id = x + (y * visualColumns);

                if (x >= visualColumns)
                    return;
                if (id >= background.GMS2TileCount)
                    return;

                SelectedTileData = id;
            }
        }
    }

    public override void SelectTileTo(Point point) => SelectTileAt(point); // TODO: Multiple tile select
}

public abstract class TilePicker : Control
{
    readonly MainViewModel mainVM = App.Services.GetRequiredService<MainViewModel>();

    protected Vector translation;
    protected double scaling = 1;

    protected Color selectedColor;

    protected Rect? selectedTileRect = null;

    Point translationMoveOffset;

    public TilePicker()
    {
        ClipToBounds = true;
    }

    protected override void OnInitialized()
    {
        Color color = this.GetSolidColorBrushResource("SystemControlHighlightAccentBrush").Color;
        selectedColor = new Color(128, color.R, color.G, color.B);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var pointerPoint = e.GetCurrentPoint(this);
        if (pointerPoint.Properties.IsLeftButtonPressed)
        {
            SelectTileAt(pointerPoint.Position);
        }
        else if (pointerPoint.Properties.IsMiddleButtonPressed)
        {
            TranslationMoveOnPressed(pointerPoint.Position);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        //
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var pointerPoint = e.GetCurrentPoint(this);
        if (pointerPoint.Properties.IsLeftButtonPressed)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                SelectTileTo(pointerPoint.Position);
            }
            else
            {
                SelectTileAt(pointerPoint.Position);
            }
        }
        else if (pointerPoint.Properties.IsMiddleButtonPressed)
        {
            TranslationMoveOnMoved(pointerPoint.Position);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var pointerPosition = e.GetPosition(this);

            if (e.Delta.Y > 0)
            {
                translation *= 2;
                translation -= pointerPosition;
                scaling *= 2;
            }
            else if (e.Delta.Y < 0)
            {
                scaling /= 2;
                translation += pointerPosition;
                translation /= 2;
            }

            translation = new(Math.Round(translation.X), Math.Round(translation.Y));
            e.Handled = true;
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        PaintCheckerPattern(context, new Rect(0, 0, Bounds.Width, Bounds.Height));

        Bitmap? image = GetTexturePageItemBitmap();
        if (image is null)
        {
            base.Render(context);
            return;
        }

        selectedTileRect = null;

        using (context.PushTransform(Matrix.CreateTranslation(translation.X, translation.Y)))
        using (context.PushTransform(Matrix.CreateScale(scaling, scaling)))
        {
            DrawTiles(context, image);

            if (selectedTileRect is Rect rect)
            {
                double s = 1 / scaling;
                rect = rect.Inflate(s);

                SolidColorBrush brush = new(selectedColor);
                context.DrawRectangle(new Pen(brush), rect, 0);

                rect = rect.Inflate(s);
                context.DrawRectangle(new Pen(brush), rect, 0);
            }
        }

        base.Render(context);

        TopLevel topLevel = TopLevel.GetTopLevel(this)!;
        topLevel.RequestAnimationFrame(_ =>
        {
            InvalidateVisual();
        });
    }

    Bitmap? GetTexturePageItemBitmap()
    {
        if (this is RoomOrLegacyTilePicker roomOrLegacyTilePicker)
        {
            UndertaleTexturePageItem? texturePageItem = roomOrLegacyTilePicker.GetTexturePageItem();
            if (texturePageItem is not null)
                return mainVM.ImageCache.GetCachedImageFromTexturePageItem(texturePageItem);
        }
        else if (DataContext is UndertaleRoom.Layer.LayerTilesData layerTilesData
            && layerTilesData.Background is UndertaleBackground background
            && background.Texture is UndertaleTexturePageItem texturePageItem)
        {
            return mainVM.ImageCache.GetCachedImageFromTexturePageItem(texturePageItem);
        }

        return null;
    }

    public abstract void DrawTiles(DrawingContext context, Bitmap image);
    public abstract void SelectTileAt(Point point);
    public abstract void SelectTileTo(Point point);

    void TranslationMoveOnPressed(Point point)
    {
        translationMoveOffset = point - translation;
    }

    void TranslationMoveOnMoved(Point point)
    {
        translation = point - translationMoveOffset;
        InvalidateVisual();
    }

    static void PaintCheckerPattern(DrawingContext context, Rect bounds)
    {
        int gridSize = 8;
        SolidColorBrush brush1 = new(Color.FromRgb(102, 102, 102));
        SolidColorBrush brush2 = new(Color.FromRgb(153, 153, 153));

        context.FillRectangle(brush1, bounds);

        for (int x = 0; x < (int)bounds.Width / gridSize; x++)
            for (int y = 0; y < (int)bounds.Height / gridSize; y++)
            {
                if ((x + y) % 2 != 0)
                    context.FillRectangle(brush2, new Rect(x * gridSize, y * gridSize, gridSize, gridSize));
            }
    }
}