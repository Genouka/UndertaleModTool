// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace QiuForms;

/// <summary>Pointer buttons reported by <see cref="QiuCanvas"/>.</summary>
public enum QiuPointerButton
{
    /// <summary>Left mouse button, first touch contact, or pen tip.</summary>
    Primary,

    /// <summary>Right mouse button.</summary>
    Secondary,

    /// <summary>Middle mouse button.</summary>
    Middle,
}

/// <summary>A pointer event on a <see cref="QiuCanvas"/>.</summary>
public readonly struct QiuPointerEventArgs
{
    /// <summary>Creates a pointer event.</summary>
    public QiuPointerEventArgs(double x, double y, QiuPointerButton button, bool isPressed)
    {
        X = x;
        Y = y;
        Button = button;
        IsPressed = isPressed;
    }

    /// <summary>Horizontal position in canvas coordinates (already divided by <see cref="QiuCanvas.Zoom"/>).</summary>
    public double X { get; }

    /// <summary>Vertical position in canvas coordinates (already divided by <see cref="QiuCanvas.Zoom"/>).</summary>
    public double Y { get; }

    /// <summary>Which button produced the event.</summary>
    public QiuPointerButton Button { get; }

    /// <summary><see langword="true"/> for press/drag, <see langword="false"/> for release.</summary>
    public bool IsPressed { get; }
}

/// <summary>Drawing operations a host must provide for <see cref="QiuCanvas"/>.</summary>
public interface IQiuCanvasContext
{
    /// <summary>Fills the whole canvas with the given color.</summary>
    void Clear(QiuColor color);

    /// <summary>Draws an image at the given position, scaled by <see cref="QiuCanvas.Zoom"/>.</summary>
    void DrawImage(byte[] png, double x, double y, double width, double height);

    /// <summary>Fills a rectangle.</summary>
    void FillRectangle(QiuColor color, double x, double y, double width, double height);

    /// <summary>Draws a rectangle outline.</summary>
    void DrawRectangle(QiuColor color, double x, double y, double width, double height, double thickness = 1);

    /// <summary>Draws a line.</summary>
    void DrawLine(QiuColor color, double x1, double y1, double x2, double y2, double thickness = 1);

    /// <summary>Draws text at the given position.</summary>
    void DrawText(QiuColor color, string text, double x, double y, double fontSize = 12);
}

/// <summary>An ARGB color used by <see cref="IQiuCanvasContext"/>.</summary>
public readonly struct QiuColor
{
    /// <summary>Creates a color.</summary>
    public QiuColor(byte a, byte r, byte g, byte b)
    {
        A = a;
        R = r;
        G = g;
        B = b;
    }

    /// <summary>Alpha.</summary>
    public byte A { get; }

    /// <summary>Red.</summary>
    public byte R { get; }

    /// <summary>Green.</summary>
    public byte G { get; }

    /// <summary>Blue.</summary>
    public byte B { get; }

    /// <summary>Creates an opaque color from RGB components.</summary>
    public static QiuColor FromRgb(byte r, byte g, byte b) => new(255, r, g, b);

    /// <summary>Transparent.</summary>
    public static QiuColor Transparent => new(0, 0, 0, 0);

    /// <summary>Black.</summary>
    public static QiuColor Black => FromRgb(0, 0, 0);

    /// <summary>White.</summary>
    public static QiuColor White => FromRgb(255, 255, 255);
}

/// <summary>
/// A custom-drawn area with pointer input. Scripts implement <see cref="Paint"/> and react to
/// <see cref="PointerPressed"/>/<see cref="PointerMoved"/>/<see cref="PointerReleased"/>.
/// </summary>
/// <remarks>
/// This is the escape hatch for UIs that are not a tree of standard controls (pixel editors,
/// previews). Hosts render it with their native drawing APIs and convert pointer positions to
/// canvas coordinates by dividing by <see cref="Zoom"/>.
/// </remarks>
public sealed class QiuCanvas : QiuElement
{
    private double width = 200;
    private double height = 200;

    /// <summary>Canvas width in device-independent units.</summary>
    public double Width
    {
        get => width;
        set => width = Math.Max(1, value);
    }

    /// <summary>Canvas height in device-independent units.</summary>
    public double Height
    {
        get => height;
        set => height = Math.Max(1, value);
    }

    /// <summary>Scale factor applied by the host. Pointer coordinates are divided by it.</summary>
    public double Zoom { get; set; } = 1;

    /// <summary>Draws the canvas. Called on the host's UI thread.</summary>
    public Action<IQiuCanvasContext>? Paint { get; set; }

    /// <summary>Raised when a pointer button is pressed over the canvas.</summary>
    public event Action<QiuPointerEventArgs>? PointerPressed;

    /// <summary>Raised while a pointer moves over the canvas (pressed or not).</summary>
    public event Action<QiuPointerEventArgs>? PointerMoved;

    /// <summary>Raised when a pointer button is released.</summary>
    public event Action<QiuPointerEventArgs>? PointerReleased;

    /// <summary>Requests a repaint. Hosts subscribe to <see cref="Invalidated"/>.</summary>
    public event Action<QiuCanvas>? Invalidated;

    /// <summary>Marks the canvas dirty. Call after changing what <see cref="Paint"/> draws.</summary>
    public void Invalidate() => Invalidated?.Invoke(this);

    /// <summary>Raises <see cref="PointerPressed"/>. Called by hosts.</summary>
    public void RaisePointerPressed(QiuPointerEventArgs args) => PointerPressed?.Invoke(args);

    /// <summary>Raises <see cref="PointerMoved"/>. Called by hosts.</summary>
    public void RaisePointerMoved(QiuPointerEventArgs args) => PointerMoved?.Invoke(args);

    /// <summary>Raises <see cref="PointerReleased"/>. Called by hosts.</summary>
    public void RaisePointerReleased(QiuPointerEventArgs args) => PointerReleased?.Invoke(args);
}
