using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;

namespace UndertaleModToolAvalonia;

/// <summary>
/// A windowing platform whose windows are never shown on screen. It is used by single-window
/// platforms (such as Android) so that <see cref="Window"/> objects can still be created; their
/// rendered content is then hosted inside a <see cref="MockStackWindow"/> (or any other host)
/// instead of being shown as a real native window.
/// </summary>
public sealed class SimulatedWindowingPlatform : IWindowingPlatform
{
    public IWindowImpl CreateWindow() => new SimulatedWindowImpl();

    public IWindowImpl CreateEmbeddableWindow() => new SimulatedWindowImpl();

    public ITopLevelImpl CreateEmbeddableTopLevel() => new SimulatedWindowImpl();

    public ITrayIconImpl? CreateTrayIcon() => null;

    public void GetWindowsZOrder(ReadOnlySpan<IWindowImpl> windows, Span<long> zOrder)
    {
        for (int i = 0; i < windows.Length && i < zOrder.Length; i++)
            zOrder[i] = i;
    }

    internal static object? TryGetFeature(Type featureType)
    {
        if (featureType == typeof(IScreenImpl))
        {
            return AvaloniaLocator.Current.GetService<IScreenImpl>()
                ?? new SimulatedScreenImpl();
        }

        return null;
    }
}

public sealed class SimulatedScreenImpl : IScreenImpl
{
    readonly IReadOnlyList<Screen> screens = [new SimulatedScreen(new PlatformHandle((nint)0x5343, "SimulatedScreen"))];

    public int ScreenCount => screens.Count;

    public IReadOnlyList<Screen> AllScreens => screens;

    public Action? Changed { get; set; }

    public Screen? ScreenFromPoint(PixelPoint point) => screens[0];
    public Screen? ScreenFromRect(PixelRect bounds) => screens[0];
    public Screen? ScreenFromTopLevel(ITopLevelImpl topLevel) => screens[0];
    public Screen? ScreenFromWindow(IWindowBaseImpl window) => screens[0];
    public Task<bool> RequestScreenDetails() => Task.FromResult(false);
}

public sealed class SimulatedScreen : PlatformScreen
{
    public SimulatedScreen(IPlatformHandle handle)
        : base(handle)
    {
        Bounds = WorkingArea = new PixelRect(0, 0, 1920, 1080);
        Scaling = 1;
        IsPrimary = true;
        DisplayName = "Simulated";
    }
}

public sealed class SimulatedWindowImpl : IWindowImpl, IOptionalFeatureProvider
{
    static readonly IWindowIconImpl s_icon = new SimulatedWindowIcon();
    static readonly ICursorImpl s_cursor = new SimulatedCursor();

    // Lazily created per top-level. Avalonia's renderer requires a non-null Compositor.
    static readonly Lazy<Compositor> s_compositor = new(() =>
        AvaloniaLocator.Current.GetService<Compositor>()
            ?? new Compositor(AvaloniaLocator.Current.GetService<IPlatformGraphics>()));

    public AcrylicPlatformCompensationLevels AcrylicCompensationLevels { get; set; }
    public Size ClientSize { get; set; } = new(1280, 720);
    public Action? Closed { get; set; }
    public Compositor Compositor => s_compositor.Value;
    public double DesktopScaling { get; set; } = 1;
    public IPlatformHandle Handle { get; } = new PlatformHandle((nint)0x4D4F434B, "SimulatedWindow");
    public Action<RawInputEventArgs>? Input { get; set; }
    public Action? LostFocus { get; set; }
    public Action<Rect>? Paint { get; set; }
    public double RenderScaling { get; set; } = 1;
    public Action<Size, WindowResizeReason>? Resized { get; set; }
    public Action<double>? ScalingChanged { get; set; }
    public IPlatformRenderSurface[] Surfaces { get; } = [];
    public WindowTransparencyLevel TransparencyLevel { get; set; }
    public Action<WindowTransparencyLevel>? TransparencyLevelChanged { get; set; }

    public Action? Activated { get; set; }
    public Action? Deactivated { get; set; }
    public Size? FrameSize { get; set; }
    public Size MaxAutoSizeHint { get; set; } = new(3840, 2160);
    public PixelPoint Position { get; set; }
    public Action<PixelPoint>? PositionChanged { get; set; }

    public PlatformAllowedWindowActions AllowedWindowActions { get; set; } = PlatformAllowedWindowActions.All;
    public Action<PlatformAllowedWindowActions>? AllowedWindowActionsChanged { get; set; }
    public Func<WindowCloseReason, bool>? Closing { get; set; }
    public Action? DrawnDecorationsRequestChanged { get; set; }
    public Action<bool>? ExtendClientAreaToDecorationsChanged { get; set; }
    public Thickness ExtendedMargins { get; set; }
    public Action? GotInputWhenDisabled { get; set; }
    public bool IsClientAreaExtendedToDecorations { get; set; }
    public bool NeedsManagedDecorations { get; set; }
    public Thickness OffScreenMargin { get; set; }
    public PlatformRequestedDrawnDecoration RequestedDrawnDecorations { get; set; }
    public WindowState WindowState { get; set; }
    public Action<WindowState>? WindowStateChanged { get; set; }
    public bool WindowStateGetterIsUsable { get; set; }

    public IPopupImpl CreatePopup() => new SimulatedPopupImpl();

    public Point PointToClient(PixelPoint point) => new(point.X, point.Y);

    public PixelPoint PointToScreen(Point point) => new((int)point.X, (int)point.Y);

    public void SetCursor(ICursorImpl? cursor) { }

    public void SetFrameThemeVariant(PlatformThemeVariant? variant) { }

    public void SetInputRoot(IInputRoot inputRoot) { }

    public void SetTransparencyLevelHint(IReadOnlyList<WindowTransparencyLevel> transparencyLevel) { }

    public void Activate() { }

    public void Hide() { }

    public void Show(bool activate, bool isDialog) { }

    public void SetTopmost(bool topmost) { }

    public void BeginMoveDrag(PointerPressedEventArgs e) { }

    public void BeginResizeDrag(WindowEdge edge, PointerPressedEventArgs e) { }

    public void CanResize(bool canResize) { }

    public void Move(PixelPoint point) => Position = point;

    public void Resize(Size clientSize, WindowResizeReason reason)
    {
        ClientSize = clientSize;
        Resized?.Invoke(clientSize, reason);
    }

    public void SetCanMaximize(bool value) { }

    public void SetCanMinimize(bool value) { }

    public void SetEnabled(bool enable) { }

    public void SetExtendClientAreaTitleBarHeightHint(double titleBarHeight) { }

    public void SetExtendClientAreaToDecorationsHint(bool extendIntoClientArea) { }

    public void SetIcon(IWindowIconImpl? icon) { }

    public void SetMinMaxSize(Size minSize, Size maxSize) { }

    public void SetParent(IWindowImpl? parent) { }

    public void SetShadowExtents(Thickness extents) { }

    public void SetTitle(string? title) { }

    public void SetWindowDecorations(WindowDecorations decorations) { }

    public void ShowTaskbarIcon(bool value) { }

    public void Dispose()
    {
        Closed?.Invoke();
        Closed = null;
    }

    public object? TryGetFeature(Type featureType)
        => SimulatedWindowingPlatform.TryGetFeature(featureType);
}

public sealed class SimulatedPopupImpl : IPopupImpl
{
    static readonly Lazy<Compositor> s_popupCompositor = new(() =>
        AvaloniaLocator.Current.GetService<Compositor>()
            ?? new Compositor(AvaloniaLocator.Current.GetService<IPlatformGraphics>()));

    public IPopupPositioner PopupPositioner { get; } = new SimulatedPopupPositioner();

    public AcrylicPlatformCompensationLevels AcrylicCompensationLevels { get; set; }
    public Size ClientSize { get; set; } = new(1, 1);
    public Action? Closed { get; set; }
    public Compositor Compositor => s_popupCompositor.Value;
    public double DesktopScaling { get; set; } = 1;
    public IPlatformHandle Handle { get; } = new PlatformHandle((nint)0x504F5000, "SimulatedPopup");
    public Action<RawInputEventArgs>? Input { get; set; }
    public Action? LostFocus { get; set; }
    public Action<Rect>? Paint { get; set; }
    public double RenderScaling { get; set; } = 1;
    public Action<Size, WindowResizeReason>? Resized { get; set; }
    public Action<double>? ScalingChanged { get; set; }
    public IPlatformRenderSurface[] Surfaces { get; } = [];
    public WindowTransparencyLevel TransparencyLevel { get; set; }
    public Action<WindowTransparencyLevel>? TransparencyLevelChanged { get; set; }

    public Action? Activated { get; set; }
    public Action? Deactivated { get; set; }
    public Size? FrameSize { get; set; }
    public Size MaxAutoSizeHint { get; set; } = new(1, 1);
    public PixelPoint Position { get; set; }
    public Action<PixelPoint>? PositionChanged { get; set; }

    public void Activate() { }
    public void Hide() { }
    public void Show(bool activate, bool isDialog) { }
    public void SetTopmost(bool topmost) { }

    public IPopupImpl CreatePopup() => new SimulatedPopupImpl();

    public Point PointToClient(PixelPoint point) => new(point.X, point.Y);
    public PixelPoint PointToScreen(Point point) => new((int)point.X, (int)point.Y);
    public void SetCursor(ICursorImpl? cursor) { }
    public void SetFrameThemeVariant(PlatformThemeVariant? variant) { }
    public void SetInputRoot(IInputRoot inputRoot) { }
    public void SetTransparencyLevelHint(IReadOnlyList<WindowTransparencyLevel> transparencyLevel) { }

    public void SetWindowManagerAddShadowHint(bool shadow) { }
    public void TakeFocus() { }

    public void Dispose()
    {
        Closed?.Invoke();
        Closed = null;
    }

    public object? TryGetFeature(Type featureType)
        => SimulatedWindowingPlatform.TryGetFeature(featureType);
}

public sealed class SimulatedPopupPositioner : IPopupPositioner
{
    public void Update(PopupPositionerParameters parameters) { }
}

public sealed class SimulatedCursor : ICursorImpl
{
    public void Dispose() { }
}

public sealed class SimulatedWindowIcon : IWindowIconImpl
{
    public void Save(Stream output) { }
}