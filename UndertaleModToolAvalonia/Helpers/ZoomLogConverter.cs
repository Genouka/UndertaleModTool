using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace UndertaleModToolAvalonia;

// Maps between the editor zoom factor and a slider position in [0, 1].
//
// The slider uses a logarithmic (per-segment) scale centered on 100%:
//   position 0    -> 5%
//   position 0.5  -> 100%
//   position 1    -> 3200%
// equal slider distances correspond to equal zoom ratios within each segment.
//
// Dragging the slider snaps the zoom up to whole percents (ConvertBack). Other sources
// (mouse wheel, future manual inputs) set the zoom directly and are not rounded; values
// below the slider's 5% floor simply pin the thumb to the left end without affecting them.
public class ZoomLogConverter : IValueConverter
{
    // Lowest zoom reachable through the slider only.
    public const double SliderMinZoom = 0.05;
    public const double MaxZoom = 32.0;

    static readonly double LogLowRatio = Math.Log(1.0 / SliderMinZoom); // 5% .. 100%
    static readonly double LogHighRatio = Math.Log(MaxZoom);            // 100% .. 3200%

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double zoom && zoom > 0)
        {
            double position = zoom <= 1
                ? 0.5 * (Math.Log(zoom / SliderMinZoom) / LogLowRatio)
                : 0.5 + 0.5 * (Math.Log(zoom) / LogHighRatio);

            return Math.Clamp(position, 0.0, 1.0);
        }

        return BindingOperations.DoNothing;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double position)
        {
            double zoom = position <= 0.5
                ? SliderMinZoom * Math.Exp(LogLowRatio * (position * 2))
                : Math.Exp(LogHighRatio * ((position - 0.5) * 2));

            // Snap up to whole percents while dragging (5% -> 6% -> ... -> 3200%).
            return Math.Ceiling(zoom * 100 - 1e-6) / 100;
        }

        return BindingOperations.DoNothing;
    }
}
