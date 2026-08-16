using System;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactions.DragAndDrop;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Runs a drag-and-drop operation fully inside the Avalonia process, without relying on the OS drag-drop
/// device (which the Android backend does not implement). It draws a small ghost that follows the pointer
/// and synthesizes <c>DragDrop.DragEnter/DragOver/Drop/DragLeave</c> routed events against the top-most
/// element under the pointer, so existing <see cref="ContextDropBehavior"/> targets keep working.
/// The drag loop itself is driven by the caller through <see cref="Update"/> and <see cref="End"/>, passing
/// positions in root-content coordinates.
/// </summary>
internal static class InAppDragDropManager
{
    private static Control? s_root;
    private static IPointer? s_pointer;
    private static IDataTransfer? s_data;
    private static string? s_contextKey;
    private static Border? s_pill;
    private static AdornerLayer? s_layer;
    private static Interactive? s_currentTarget;
    private static Point s_lastPosition;
    private static bool s_active;

    public static bool IsActive => s_active;

    private static readonly MethodInfo s_contextStoreAdd = GetContextStoreMethod("Add");
    private static readonly MethodInfo s_contextStoreRemove = GetContextStoreMethod("Remove");

    private static MethodInfo GetContextStoreMethod(string methodName)
    {
        Type? storeType = typeof(ContextDropBehaviorBase).Assembly.GetType("Avalonia.Xaml.Interactions.DragAndDrop.DragDropContextStore");
        MethodInfo? method = storeType?.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Unable to resolve DragDropContextStore.{methodName}");
        return method;
    }

    public static string? AddContext(object? value)
    {
        return value is null ? null : (string?)s_contextStoreAdd.Invoke(null, [value]);
    }

    public static void RemoveContext(string? key)
    {
        s_contextStoreRemove.Invoke(null, [key]);
    }

    public static bool Begin(Control host, IPointer pointer, IDataTransfer data, string? contextKey, string? label, Point rootPosition)
    {
        if (s_active)
            return false;

        Control? root = TopLevel.GetTopLevel(host)?.Content as Control;
        if (root is null)
            return false;

        s_root = root;
        s_pointer = pointer;
        s_data = data;
        s_contextKey = contextKey;
        s_currentTarget = null;
        s_lastPosition = rootPosition;
        s_active = true;

        s_layer = AdornerLayer.GetAdornerLayer(root);
        if (s_layer is not null)
        {
            s_pill = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(200, 60, 60, 60)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(220, 180, 180, 180)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3, 8, 3),
                IsHitTestVisible = false,
                Child = new TextBlock() { Text = label, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center },
            };
            s_layer.Children.Add(s_pill);
        }

        // Take the gesture over from the scroll views while we drag, so the finger movement only feeds us.
        pointer.Capture(root);

        UpdatePill(rootPosition);

        return true;
    }

    public static void Update(Point rootPosition)
    {
        if (!s_active || s_root is null)
            return;

        s_lastPosition = rootPosition;
        UpdatePill(rootPosition);

        IInputElement? hit = s_root.InputHitTest(rootPosition);
        Interactive? hitInteractive = hit as Interactive;
        Interactive? target = FindDropTarget(hitInteractive);

        if (!ReferenceEquals(target, s_currentTarget))
        {
            if (s_currentTarget is not null)
                RaiseLeave(s_currentTarget);

            s_currentTarget = target;

            if (target is not null)
                RaiseEnter(hitInteractive ?? target);
        }

        if (target is not null)
            RaiseOver(hitInteractive ?? target);
    }

    public static void End(Point? rootPosition, bool cancel)
    {
        if (!s_active)
            return;

        try
        {
            Point position = rootPosition ?? s_lastPosition;
            s_lastPosition = position;

            if (!cancel && s_currentTarget is not null && s_root is not null)
            {
                IInputElement? hit = s_root.InputHitTest(position);
                RaiseDrop(hit as Interactive ?? s_currentTarget);
            }
            else if (cancel && s_currentTarget is not null && s_root is not null)
            {
                RaiseLeave(s_currentTarget);
            }
        }
        finally
        {
            Cleanup();
        }
    }

    private static Interactive? FindDropTarget(Interactive? hit)
    {
        Control? element = hit as Control;
        while (element is not null)
        {
            if (DragDrop.GetAllowDrop(element))
                return element;
            element = element.Parent as Control;
        }
        return null;
    }

    private static void RaiseEnter(Interactive source)
    {
        Raise(source, DragDrop.DragEnterEvent);
    }

    private static void RaiseOver(Interactive source)
    {
        Raise(source, DragDrop.DragOverEvent);
    }

    private static void RaiseLeave(Interactive source)
    {
        Raise(source, DragDrop.DragLeaveEvent);
    }

    private static void RaiseDrop(Interactive source)
    {
        Raise(source, DragDrop.DropEvent);
    }

    private static void Raise(Interactive source, RoutedEvent<DragEventArgs> routedEvent)
    {
        Point localPosition = ((Visual)s_root!).TranslatePoint(s_lastPosition, source) ?? new Point(0, 0);
        source.RaiseEvent(new DragEventArgs(routedEvent, s_data!, source, localPosition, KeyModifiers.None));
    }

    private static void UpdatePill(Point rootPosition)
    {
        if (s_pill is not null && s_layer is not null)
        {
            Canvas.SetLeft(s_pill, rootPosition.X + 12);
            Canvas.SetTop(s_pill, rootPosition.Y + 12);
        }
    }

    private static void Cleanup()
    {
        if (s_pill is not null && s_layer is not null)
            s_layer.Children.Remove(s_pill);

        s_pointer?.Capture(null);

        if (s_contextKey is not null)
            RemoveContext(s_contextKey);

        s_root = null;
        s_pointer = null;
        s_data = null;
        s_contextKey = null;
        s_pill = null;
        s_layer = null;
        s_currentTarget = null;
        s_active = false;
    }
}