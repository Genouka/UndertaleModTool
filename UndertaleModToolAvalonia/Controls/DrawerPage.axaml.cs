using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Transformation;

namespace UndertaleModToolAvalonia;

/// <summary>
/// A page whose side panel - the <see cref="DrawerContent"/> - can be slid in from the left edge
/// over or beside the <see cref="MainContent"/> (a navigation-drawer layout).
/// <para>
/// The drawer replaces MainView's fixed left panel, which consumed screen space at all times and
/// left too little room for the tab area on narrow screens (Android). <see cref="IsDrawerOpen"/>
/// drives the whole state: the hosting page toggles it with its own button (hidden when the
/// drawer is not <see cref="IsCollapsible"/>), tapping the scrim closes it (overlay mode), and on
/// Android the system back button closes it too. Desktop uses <see cref="DrawerDisplayMode.Push"/>
/// with a non-collapsible drawer whose width the drag handle resizes, keeping the classic
/// two-column layout; Android uses <see cref="DrawerDisplayMode.Overlay"/> with it closed to save
/// screen space.
/// </para>
/// </summary>
public partial class DrawerPage : UserControl
{
    public enum DrawerDisplayMode
    {
        /// <summary>The drawer overlays the main content with a light-dismiss scrim.</summary>
        Overlay,

        /// <summary>The main content is pushed aside so page and drawer sit side by side.</summary>
        Push,
    }

    private const double MinDrawerWidth = 120;
    private const double MinMainContentWidth = 200;

    public static readonly StyledProperty<object?> MainContentProperty =
        AvaloniaProperty.Register<DrawerPage, object?>(nameof(MainContent));

    public static readonly StyledProperty<object?> DrawerContentProperty =
        AvaloniaProperty.Register<DrawerPage, object?>(nameof(DrawerContent));

    public static readonly StyledProperty<bool> IsDrawerOpenProperty =
        AvaloniaProperty.Register<DrawerPage, bool>(nameof(IsDrawerOpen), defaultValue: true);

    public static readonly StyledProperty<double> DrawerWidthProperty =
        AvaloniaProperty.Register<DrawerPage, double>(nameof(DrawerWidth), defaultValue: 320d);

    public static readonly StyledProperty<DrawerDisplayMode> ModeProperty =
        AvaloniaProperty.Register<DrawerPage, DrawerDisplayMode>(nameof(Mode), defaultValue: DrawerDisplayMode.Push);

    public static readonly StyledProperty<bool> IsCollapsibleProperty =
        AvaloniaProperty.Register<DrawerPage, bool>(nameof(IsCollapsible), defaultValue: true);

    public static readonly StyledProperty<bool> IsEdgeSwipeEnabledProperty =
        AvaloniaProperty.Register<DrawerPage, bool>(nameof(IsEdgeSwipeEnabled), defaultValue: true);

    /// <summary>The content the page shows while the drawer is closed.</summary>
    public object? MainContent
    {
        get => GetValue(MainContentProperty);
        set => SetValue(MainContentProperty, value);
    }

    /// <summary>The content of the slide-in side panel.</summary>
    public object? DrawerContent
    {
        get => GetValue(DrawerContentProperty);
        set => SetValue(DrawerContentProperty, value);
    }

    /// <summary>Whether the drawer is currently slid in.</summary>
    public bool IsDrawerOpen
    {
        get => GetValue(IsDrawerOpenProperty);
        set => SetValue(IsDrawerOpenProperty, value);
    }

    /// <summary>The drawer's width (also the initial width for drag resizing).</summary>
    public double DrawerWidth
    {
        get => GetValue(DrawerWidthProperty);
        set => SetValue(DrawerWidthProperty, value);
    }

    /// <summary>How the open drawer relates to the main content.</summary>
    public DrawerDisplayMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <summary>
    /// Whether the drawer can be slid out at all. When false it is forced open and the toggle
    /// button in the hosting page should be hidden (the desktop push layout keeps the panel
    /// permanently visible, resized with the drag handle instead).
    /// </summary>
    public bool IsCollapsible
    {
        get => GetValue(IsCollapsibleProperty);
        set => SetValue(IsCollapsibleProperty, value);
    }

    /// <summary>
    /// Whether the edge-swipe-to-open gesture is active. Hosts whose main content is itself
    /// drag-pannable (e.g. the room editor canvas) should turn it off so a pan starting near the
    /// left edge does not open the drawer.
    /// </summary>
    public bool IsEdgeSwipeEnabled
    {
        get => GetValue(IsEdgeSwipeEnabledProperty);
        set => SetValue(IsEdgeSwipeEnabledProperty, value);
    }

    public DrawerPage()
    {
        InitializeComponent();

        // Edge-swipe-to-open gesture, observed on the tunneling route so it also works when the
        // swipe starts on top of main content (the press itself is not consumed, so ordinary
        // taps on whatever sits near the edge keep working).
        AddHandler(PointerPressedEvent, DrawerEdge_PointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, DrawerEdge_PointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, DrawerEdge_PointerCaptureLost, RoutingStrategies.Tunnel);

        UpdateVisualState();
    }

    public void OpenDrawer() => IsDrawerOpen = true;

    public void CloseDrawer() => IsDrawerOpen = false;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.Property == IsDrawerOpenProperty || e.Property == ModeProperty || e.Property == DrawerWidthProperty)
            UpdateVisualState();
        else if (e.Property == IsCollapsibleProperty && !IsCollapsible && !IsDrawerOpen)
            IsDrawerOpen = true; // a non-collapsible drawer is always shown
    }

    /// <summary>
    /// Push mode reserves a column for the drawer by shifting the main content and shows the drag
    /// handle on the drawer's edge, so nothing gets covered; overlay mode keeps the main content
    /// full-width and dims it with the scrim.
    /// </summary>
    private void UpdateVisualState()
    {
        if (DrawerRoot is null || Scrim is null || MainPresenter is null || PushSplitter is null)
            return;

        bool open = IsDrawerOpen;
        bool push = Mode == DrawerDisplayMode.Push;

        DrawerRoot.RenderTransform = TransformOperations.Parse(open
            ? "translateX(0)"
            : string.Create(CultureInfo.InvariantCulture, $"translateX(-{DrawerWidth}px)"));
        Scrim.IsVisible = open && !push;
        PushSplitter.IsVisible = open && push;
        PushSplitter.Margin = new Thickness(DrawerWidth - 3, 0, 0, 0);
        MainPresenter.Margin = open && push ? new Thickness(DrawerWidth, 0, 0, 0) : default;
    }

    private void Scrim_Tapped(object? sender, TappedEventArgs e) => CloseDrawer();

    private const double EdgeSwipeWidth = 32;
    private const double MinEdgeSwipeDistance = 48;

    private IPointer? _edgeSwipePointer;
    private Point _edgeSwipeOrigin;

    private bool CanEdgeSwipe() => IsEdgeSwipeEnabled && IsCollapsible && !IsDrawerOpen && Mode == DrawerDisplayMode.Overlay;

    private void DrawerEdge_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_edgeSwipePointer is not null || !CanEdgeSwipe() || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        Point origin = e.GetPosition(this);
        if (origin.X > EdgeSwipeWidth)
            return;

        _edgeSwipePointer = e.Pointer;
        _edgeSwipeOrigin = origin;
    }

    private void DrawerEdge_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer, _edgeSwipePointer))
            return;

        _edgeSwipePointer = null;

        Point pos = e.GetPosition(this);
        double dx = pos.X - _edgeSwipeOrigin.X;
        double dy = pos.Y - _edgeSwipeOrigin.Y;

        // A mostly-horizontal swipe inward from the edge opens the drawer; vertical drags are
        // left to whatever the user is touching (e.g. scrolling the content).
        if (dx >= MinEdgeSwipeDistance && dx > Math.Abs(dy) * 2)
            OpenDrawer();
    }

    private void DrawerEdge_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (ReferenceEquals(e.Pointer, _edgeSwipePointer))
            _edgeSwipePointer = null;
    }

    private Point _splitterDragOrigin;
    private double _splitterStartDrawerWidth;

    private void PushSplitter_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _splitterDragOrigin = e.GetPosition(this);
        _splitterStartDrawerWidth = DrawerWidth;
        e.Pointer.Capture(PushSplitter);
        e.Handled = true;
    }

    private void PushSplitter_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer.Captured, PushSplitter))
            return;

        double dx = e.GetPosition(this).X - _splitterDragOrigin.X;
        double maxWidth = Math.Max(MinDrawerWidth, Bounds.Width - MinMainContentWidth);
        DrawerWidth = Math.Clamp(_splitterStartDrawerWidth + dx, MinDrawerWidth, maxWidth);
        e.Handled = true;
    }

    private void PushSplitter_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (ReferenceEquals(e.Pointer.Captured, PushSplitter))
        {
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }
}
