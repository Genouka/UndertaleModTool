using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Microsoft.Extensions.DependencyInjection;
using UndertaleModLib.Models;
using UndertaleModLib.Util;

namespace UndertaleModToolAvalonia;

public class ImageViewer : Control
{
    public static readonly StyledProperty<object?> ImageProperty =
        AvaloniaProperty.Register<ImageViewer, object?>(nameof(Image));

    public object? Image
    {
        get => GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }

    public static readonly StyledProperty<IList<object?>> BindingsProperty =
        AvaloniaProperty.Register<ImageViewer, IList<object?>>(nameof(Bindings));

    public IList<object?> Bindings
    {
        get => GetValue(BindingsProperty);
        set => SetValue(BindingsProperty, value);
    }

    readonly MainViewModel mainVM = App.Services.GetRequiredService<MainViewModel>();

    double scaling = 1;

    public ImageViewer()
    {
        ClipToBounds = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ImageProperty)
        {
            if (Image is UndertaleTexturePageItem)
            {
                // Bind these values to a property so we can get updates when they change.
                IList<BindingBase> bindings =
                [
                    new Binding("Image.TexturePage.TextureData.Image")
                        {Source = this},
                    new Binding("Image.SourceX")
                        {Source = this},
                    new Binding("Image.SourceY")
                        {Source = this},
                    new Binding("Image.SourceWidth")
                        {Source = this},
                    new Binding("Image.SourceHeight")
                        {Source = this},
                    new Binding("Image.TargetX")
                        {Source = this},
                    new Binding("Image.TargetY")
                        {Source = this},
                    new Binding("Image.TargetWidth")
                        {Source = this},
                    new Binding("Image.TargetHeight")
                        {Source = this},
                    new Binding("Image.BoundingWidth")
                        {Source = this},
                    new Binding("Image.BoundingHeight")
                        {Source = this},
                ];

                MultiBinding multiBinding = new()
                {
                    Bindings = bindings,
                    Converter = new FuncMultiValueConverter<object?, IList<object?>>(x => new List<object?>(x))
                };

                Bind(BindingsProperty, multiBinding);
            }
            else
            {
                // NOTE: Unbind?
            }

            Invalidate();
        }
        else if (change.Property == BindingsProperty)
        {
            Invalidate();
        }
    }

    void Invalidate()
    {
        Size size = GetSize();
        Width = size.Width;
        Height = size.Height;

        InvalidateMeasure();
        InvalidateVisual();
    }

    Size GetSize()
    {
        if (Image is UndertaleTexturePageItem texturePageItem)
            return new Size(texturePageItem.BoundingWidth, texturePageItem.BoundingHeight) * scaling;
        else if (Image is GMImage gmImage)
            return new Size(gmImage.Width, gmImage.Height) * scaling;
        else if (Image is UndertaleSprite.MaskEntry maskEntry)
            return new Size(maskEntry.Width, maskEntry.Height) * scaling;

        return new Size(0, 0);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return GetSize();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Delta.Y > 0)
            {
                scaling *= 2;
            }
            else if (e.Delta.Y < 0)
            {
                scaling /= 2;
            }

            Invalidate();
            e.Handled = true;
        }
    }

    public override void Render(DrawingContext context)
    {
        Size size = GetSize();

        PaintCheckerPattern(context, new Rect(0, 0, size.Width, size.Height));

        using (context.PushTransform(Matrix.CreateScale(scaling, scaling)))
        {
            RenderImage(context);
        }
    }

    void RenderImage(DrawingContext context)
    {
        if (Image is UndertaleTexturePageItem texturePageItem)
        {
            if (texturePageItem.TexturePage is not null)
            {
                Bitmap? image = mainVM.ImageCache.GetCachedImageFromTexturePageItem(texturePageItem);

                if (image is not null)
                {
                    context.DrawImage(image,
                        new Rect(texturePageItem.TargetX, texturePageItem.TargetY, texturePageItem.TargetWidth, texturePageItem.TargetHeight));
                }
            }
        }
        else if (Image is GMImage gmImage)
        {
            Bitmap? image = mainVM.ImageCache.GetCachedImageFromGMImage(gmImage);
            if (image is not null)
            {
                context.DrawImage(image, new Rect(0, 0, image.PixelSize.Width, image.PixelSize.Height));
            }
        }
        else if (Image is UndertaleSprite.MaskEntry maskEntry)
        {
            context.DrawImage(CreateMaskBitmap(maskEntry), new Rect(0, 0, maskEntry.Width, maskEntry.Height));
        }
    }

    static Bitmap CreateMaskBitmap(UndertaleSprite.MaskEntry maskEntry)
    {
        int width = maskEntry.Width;
        int height = maskEntry.Height;
        int rowWidth = (maskEntry.Width + 7) / 8;

        WriteableBitmap bitmap = new(new PixelSize(width, height), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Unpremul);

        using (var framebuffer = bitmap.Lock())
        {
            nint address = framebuffer.Address;
            int rowBytes = framebuffer.RowBytes;
            int stride = width * 4;

            byte[] row = new byte[stride];
            for (int y = 0; y < height; y++)
            {
                Array.Clear(row);
                int byteRowIndex = y * rowWidth;
                for (int x = 0; x < width; x++)
                {
                    int byteIndex = byteRowIndex + (x / 8);
                    int bitIndex = x % 8;
                    bool solid = (maskEntry.Data[byteIndex] & (1 << (7 - bitIndex))) != 0;
                    byte value = solid ? (byte)255 : (byte)0;

                    int i = x * 4;
                    row[i] = value;
                    row[i + 1] = value;
                    row[i + 2] = value;
                    row[i + 3] = 255;
                }

                System.Runtime.InteropServices.Marshal.Copy(row, 0, nint.Add(address, y * rowBytes), stride);
            }
        }

        return bitmap;
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