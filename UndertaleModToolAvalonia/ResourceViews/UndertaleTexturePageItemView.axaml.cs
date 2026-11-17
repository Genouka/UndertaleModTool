using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using UndertaleModLib.Models;

namespace UndertaleModToolAvalonia;

public partial class UndertaleTexturePageItemView : UserControl
{
    UndertaleTexturePageItem? texturePageItem;
    IDisposable? itemChangedSubscription;

    public UndertaleTexturePageItemView()
    {
        InitializeComponent();

        // Zooming the texture with Ctrl+scroll resizes it, so the highlight has to follow.
        TextureImage.SizeChanged += (_, _) => UpdateItemHighlight();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        itemChangedSubscription?.Dispose();
        itemChangedSubscription = null;

        texturePageItem = (DataContext as UndertaleTexturePageItemViewModel)?.TexturePageItem;
        if (texturePageItem is not null)
        {
            // Any layout-affecting change (the highlight geometry depends on all of them) repaints
            // the highlight; UpdateItemHighlight is cheap, so filtering by property name isn't needed.
            itemChangedSubscription = SubscribeToItemChanges(texturePageItem);
        }

        UpdateItemHighlight();
    }

    void UpdateItemHighlight()
    {
        // Width is -1 when the texture has no image.
        int textureWidth = texturePageItem?.TexturePage?.TextureData?.Width ?? -1;
        if (texturePageItem is null || textureWidth <= 0)
        {
            ItemOutline.IsVisible = false;
            DimOverlay.IsVisible = false;
            return;
        }

        double scale = TextureImage.Bounds.Width / textureWidth;
        Rect itemRect = new(
            texturePageItem.SourceX * scale,
            texturePageItem.SourceY * scale,
            texturePageItem.SourceWidth * scale,
            texturePageItem.SourceHeight * scale);

        Canvas.SetLeft(ItemOutline, itemRect.X);
        Canvas.SetTop(ItemOutline, itemRect.Y);
        ItemOutline.Width = itemRect.Width;
        ItemOutline.Height = itemRect.Height;
        ItemOutline.IsVisible = true;

        // Even-odd filling leaves a hole where the item is, so only the rest of the texture is dimmed.
        DimOverlay.Data = new GeometryGroup
        {
            FillRule = FillRule.EvenOdd,
            Children =
            {
                new RectangleGeometry(new Rect(TextureImage.Bounds.Size)),
                new RectangleGeometry(itemRect),
            },
        };
        DimOverlay.IsVisible = true;
    }

    IDisposable SubscribeToItemChanges(UndertaleTexturePageItem item)
    {
        PropertyChangedEventHandler handler = (_, _) => UpdateItemHighlight();
        item.PropertyChanged += handler;
        return new AnonymousDisposable(() => item.PropertyChanged -= handler);
    }

    sealed class AnonymousDisposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
