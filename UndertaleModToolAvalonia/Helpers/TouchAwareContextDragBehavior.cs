using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.Xaml.Interactions.DragAndDrop;
using Avalonia.Xaml.Interactivity;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Touch aware replacement for <see cref="ContextDragBehavior"/>.
/// Mouse and pen/stylus input keeps the original drag-and-drop behavior (OS drag starts after the drag
/// thresholds are crossed). Touch input never hijacks the scroll gesture:
/// <list type="bullet">
/// <item>A short movement lets the ScrollViewer pan;</item>
/// <item>holding without much movement for at least <see cref="s_touchContextMenuMinHold"/> and releasing
/// before <see cref="s_touchDragDelay"/> opens the row context menu;</item>
/// <item>holding beyond <see cref="s_touchDragDelay"/> starts an in-app drag-and-drop (see
/// <see cref="InAppDragDropManager"/>) which works even where the OS drag-drop device is unavailable
/// (e.g. Android).</item>
/// </list>
/// </summary>
public class TouchAwareContextDragBehavior : StyledElementBehavior<Control>
{
    public static readonly StyledProperty<object?> ContextProperty = AvaloniaProperty.Register<TouchAwareContextDragBehavior, object?>(
        "Context", default, false, BindingMode.OneWay);

    public static readonly StyledProperty<double> HorizontalDragThresholdProperty = AvaloniaProperty.Register<TouchAwareContextDragBehavior, double>(
        "HorizontalDragThreshold", 3.0, false, BindingMode.TwoWay);

    public static readonly StyledProperty<double> VerticalDragThresholdProperty = AvaloniaProperty.Register<TouchAwareContextDragBehavior, double>(
        "VerticalDragThreshold", 3.0, false, BindingMode.TwoWay);

    public static readonly StyledProperty<IDragHandler?> HandlerProperty = AvaloniaProperty.Register<TouchAwareContextDragBehavior, IDragHandler?>(
        "Handler", default, false, BindingMode.TwoWay);

    private static readonly TimeSpan s_touchContextMenuMinHold = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan s_touchDragDelay = TimeSpan.FromMilliseconds(2000);
    private const double TouchCancelDistance = 15;

    private Point _dragStartPoint;
    private PointerPressedEventArgs? _triggerEvent;
    private bool _lock;
    private bool _captured;

    private IPointer? _touchPointer;
    private Control? _touchRoot;
    private Point _touchStartPosition;
    private Stopwatch? _touchStopwatch;
    private DispatcherTimer? _touchTimer;
    private bool _touchCancelled;
    private bool _touchTimedOut;
    private bool _touchDragging;

    public object? Context
    {
        get => GetValue(ContextProperty);
        set => SetValue(ContextProperty, value);
    }

    public double HorizontalDragThreshold
    {
        get => GetValue(HorizontalDragThresholdProperty);
        set => SetValue(HorizontalDragThresholdProperty, value);
    }

    public double VerticalDragThreshold
    {
        get => GetValue(VerticalDragThresholdProperty);
        set => SetValue(VerticalDragThresholdProperty, value);
    }

    public IDragHandler? Handler
    {
        get => GetValue(HandlerProperty);
        set => SetValue(HandlerProperty, value);
    }

    protected override void OnAttachedToVisualTree()
    {
        if (AssociatedObject is Control associatedObject)
        {
            associatedObject.AddHandler<PointerPressedEventArgs>(InputElement.PointerPressedEvent, AssociatedObject_PointerPressed, RoutingStrategies.Direct | RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: false);
            associatedObject.AddHandler<PointerReleasedEventArgs>(InputElement.PointerReleasedEvent, AssociatedObject_PointerReleased, RoutingStrategies.Direct | RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: false);
            associatedObject.AddHandler<PointerEventArgs>(InputElement.PointerMovedEvent, AssociatedObject_PointerMoved, RoutingStrategies.Direct | RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: false);
            associatedObject.AddHandler<PointerCaptureLostEventArgs>(InputElement.PointerCaptureLostEvent, AssociatedObject_CaptureLost, RoutingStrategies.Direct | RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: false);
            associatedObject.AddHandler<KeyEventArgs>(InputElement.KeyDownEvent, AssociatedObject_KeyDown, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: false);
        }

        base.OnAttachedToVisualTree();
    }

    protected override void OnDetachedFromVisualTree()
    {
        CleanupTouch();

        if (AssociatedObject is Control associatedObject)
        {
            associatedObject.RemoveHandler<PointerPressedEventArgs>(InputElement.PointerPressedEvent, AssociatedObject_PointerPressed);
            associatedObject.RemoveHandler<PointerReleasedEventArgs>(InputElement.PointerReleasedEvent, AssociatedObject_PointerReleased);
            associatedObject.RemoveHandler<PointerEventArgs>(InputElement.PointerMovedEvent, AssociatedObject_PointerMoved);
            associatedObject.RemoveHandler<PointerCaptureLostEventArgs>(InputElement.PointerCaptureLostEvent, AssociatedObject_CaptureLost);
            associatedObject.RemoveHandler<KeyEventArgs>(InputElement.KeyDownEvent, AssociatedObject_KeyDown);
        }

        base.OnDetachedFromVisualTree();
    }

    private static bool IsTouchPoint(PointerEventArgs e) => e.Pointer.Type == PointerType.Touch;

    private void AssociatedObject_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Touch input is intentionally not "handled" and mostly left alone so the scroll gesture recognizers
        // can take the swipe over. We only observe it for the hold-to-menu / hold-to-drag gestures.
        if (IsTouchPoint(e))
        {
            Released();
            _captured = false;
            BeginTouch(e);
            return;
        }

        Control? associatedObject = AssociatedObject;

        if (e.GetCurrentPoint(associatedObject).Properties.IsLeftButtonPressed && IsEnabled)
        {
            if (e.Source is Control source)
            {
                if (associatedObject?.DataContext == source.DataContext)
                {
                    if ((e.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Alt)) == 0)
                    {
                        ISelectable? selectable = source as ISelectable
                            ?? source.Parent as ISelectable
                            ?? source.FindLogicalAncestorOfType<ISelectable>();
                        if (selectable is not null && selectable.IsSelected)
                        {
                            e.Handled = true;
                        }
                    }

                    _dragStartPoint = e.GetPosition(null);
                    _triggerEvent = e;
                    _lock = true;
                    _captured = true;
                    return;
                }
            }
        }

        e.Handled = false;
    }

    private void AssociatedObject_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_captured)
        {
            if (e.Pointer.Type != PointerType.Touch
                && e.InitialPressMouseButton == MouseButton.Left
                && _triggerEvent is not null)
            {
                Released();
            }

            _captured = false;
        }
    }

    private async void AssociatedObject_PointerMoved(object? sender, PointerEventArgs e)
    {
        var currentPoint = e.GetCurrentPoint(AssociatedObject);

        if (!_captured
            || IsTouchPoint(e)
            || !currentPoint.Properties.IsLeftButtonPressed
            || !IsEnabled
            || _triggerEvent is null)
        {
            return;
        }

        Point position = e.GetPosition(null);
        Point delta = _dragStartPoint - position;

        if ((Math.Abs(delta.X) > HorizontalDragThreshold || Math.Abs(delta.Y) > VerticalDragThreshold) && _lock)
        {
            _lock = false;

            PointerPressedEventArgs triggerEvent = _triggerEvent;
            object? context = Context ?? AssociatedObject?.DataContext;

            OnBeforeDragDrop(sender, triggerEvent, context);
            await DoDragDrop(triggerEvent, context);
            OnAfterDragDrop(sender, triggerEvent, context);

            _triggerEvent = null;
        }
    }

    private void AssociatedObject_CaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        Released();
        _captured = false;
    }

    private void AssociatedObject_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Released();
            _captured = false;
        }
    }

    private void Released()
    {
        _triggerEvent = null;
        _lock = false;
    }

    #region Touch gestures

    private void BeginTouch(PointerPressedEventArgs e)
    {
        if (_touchPointer is not null)
            return;

        _touchPointer = e.Pointer;
        _touchStopwatch = Stopwatch.StartNew();
        _touchCancelled = false;
        _touchTimedOut = false;
        _touchDragging = false;

        if (AssociatedObject is Control assoc)
        {
            _touchRoot = TopLevel.GetTopLevel(assoc)?.Content as Control;
            if (_touchRoot is not null)
            {
                _touchStartPosition = e.GetPosition(_touchRoot);
                _touchRoot.AddHandler(InputElement.PointerMovedEvent, TouchRoot_PointerMoved, RoutingStrategies.Direct | RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
                _touchRoot.AddHandler(InputElement.PointerReleasedEvent, TouchRoot_PointerReleased, RoutingStrategies.Direct | RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
                _touchRoot.AddHandler(InputElement.PointerCaptureLostEvent, TouchRoot_PointerCaptureLost, RoutingStrategies.Direct | RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            }
        }

        _touchTimer?.Stop();
        _touchTimer = new DispatcherTimer(s_touchDragDelay, DispatcherPriority.Background, (_, _) => TouchTimer_Tick());
        _touchTimer.Start();
    }

    private void TouchRoot_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (e.Pointer != _touchPointer)
            return;

        // While an in-app drag is running, feed its position to InAppDragDropManager.
        if (_touchDragging)
        {
            InAppDragDropManager.Update(_touchRoot is not null ? e.GetPosition(_touchRoot) : e.GetPosition(null));
            return;
        }

        if (_touchCancelled)
            return;

        Point position = e.GetPosition(null);
        double dx = position.X - _touchStartPosition.X;
        double dy = position.Y - _touchStartPosition.Y;
        if (Math.Sqrt((dx * dx) + (dy * dy)) > TouchCancelDistance)
        {
            // The user is scrolling - give the gesture back to the ScrollViewer.
            _touchCancelled = true;
            _touchTimer?.Stop();
        }
    }

    private void TouchRoot_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer != _touchPointer)
            return;

        _touchTimer?.Stop();

        if (_touchDragging)
        {
            InAppDragDropManager.End(_touchRoot is not null ? e.GetPosition(_touchRoot) : null, cancel: false);
            CleanupTouch();
            return;
        }

        if (!_touchCancelled && !_touchTimedOut
            && _touchStopwatch is not null && _touchStopwatch.Elapsed >= s_touchContextMenuMinHold)
        {
            OpenTouchContextMenu();
        }

        CleanupTouch();
    }

    private void TouchRoot_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (e.Pointer != _touchPointer)
            return;

        _touchTimer?.Stop();

        if (_touchDragging)
        {
            InAppDragDropManager.End(null, cancel: true);
        }

        CleanupTouch();
    }

    private void TouchTimer_Tick()
    {
        if (_touchCancelled || _touchPointer is null || AssociatedObject is not Control assoc)
            return;

        _touchTimer?.Stop();

        // Crossed the drag-delay threshold: from now on the gesture is a drag intent, not a menu one,
        // so the context menu must never open on the following release.
        _touchTimedOut = true;

        object? context = Context ?? assoc.DataContext;
        string? contextKey = InAppDragDropManager.AddContext(context);

        var dataTransfer = new DataTransfer();
        if (contextKey is not null)
        {
            dataTransfer.Add(DataTransferItem.Create<string>(ContextDropBehaviorBase.ContextDataTransferFormat, contextKey));
        }

        _touchDragging = InAppDragDropManager.Begin(assoc, _touchPointer, dataTransfer, contextKey, GetDragLabel(), _touchStartPosition);
    }

    private void OpenTouchContextMenu()
    {
        if (AssociatedObject is Control assoc && assoc.ContextMenu is { } menu)
            menu.Open(assoc);
    }

    private string? GetDragLabel()
    {
        object? dataContext = AssociatedObject?.DataContext;
        if (dataContext is null)
            return null;

        PropertyInfo? textProperty = dataContext.GetType().GetProperty("Text");
        if (textProperty?.GetValue(dataContext) is string text)
            return text;

        return dataContext.ToString();
    }

    private void CleanupTouch()
    {
        _touchTimer?.Stop();
        _touchTimer = null;
        _touchStopwatch = null;
        _touchPointer = null;
        _touchCancelled = false;
        _touchTimedOut = false;
        _touchDragging = false;

        if (_touchRoot is not null)
        {
            _touchRoot.RemoveHandler(InputElement.PointerMovedEvent, TouchRoot_PointerMoved);
            _touchRoot.RemoveHandler(InputElement.PointerReleasedEvent, TouchRoot_PointerReleased);
            _touchRoot.RemoveHandler(InputElement.PointerCaptureLostEvent, TouchRoot_PointerCaptureLost);
            _touchRoot = null;
        }
    }

    #endregion

    private async Task DoDragDrop(PointerPressedEventArgs triggerEvent, object? value)
    {
        var dataTransfer = new DataTransfer();
        string? contextKey = InAppDragDropManager.AddContext(value);

        if (contextKey is not null)
        {
            dataTransfer.Add(DataTransferItem.Create<string>(ContextDropBehaviorBase.ContextDataTransferFormat, contextKey));
        }

        KeyModifiers modifiers = triggerEvent.KeyModifiers;
        DragDropEffects allowedEffects = (modifiers & KeyModifiers.Alt) != 0 ? DragDropEffects.Link
            : (modifiers & KeyModifiers.Shift) != 0 ? DragDropEffects.Move
            : (modifiers & KeyModifiers.Control) != 0 ? DragDropEffects.Copy
            : DragDropEffects.Move;

        try
        {
            await DragDrop.DoDragDropAsync(triggerEvent, dataTransfer, allowedEffects);
        }
        finally
        {
            InAppDragDropManager.RemoveContext(contextKey);
        }
    }

    private void OnBeforeDragDrop(object? sender, PointerEventArgs e, object? context)
    {
        Handler?.BeforeDragDrop(sender, e, context);
    }

    private void OnAfterDragDrop(object? sender, PointerEventArgs e, object? context)
    {
        Handler?.AfterDragDrop(sender, e, context);
    }
}