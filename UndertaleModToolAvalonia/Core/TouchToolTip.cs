using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Enables press-and-hold tooltips on touch screens.
/// <para>
/// Avalonia's tooltip service only opens a tip from pointer <em>hover</em> (raw
/// <see cref="Avalonia.Input.Raw.RawPointerEventType.Move"/> events), which a touch screen never
/// produces, so a plain <see cref="ToolTip.TipProperty"/> stays invisible on Android. The
/// <see cref="InputElement.Holding"/> gesture is raised after the platform's long-press timeout
/// instead, so it opens the tip explicitly and closes it when the finger lifts or the gesture
/// turns into a scroll.
/// </para>
/// </summary>
public static class TouchToolTip
{
    /// <summary>The control whose long-press tip is open, if any.</summary>
    static Control? s_openTip;

    /// <summary>
    /// Shows <paramref name="text"/> as a tooltip while <paramref name="target"/> is held down.
    /// </summary>
    public static void Enable(Control target, string text)
    {
        TextBlock tip = new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
        };
        ToolTip.SetTip(target, tip);
        ToolTip.SetPlacement(target, PlacementMode.BottomEdgeAlignedLeft);
        // Pointer devices (DeX, Chromebooks) still get the hover tip after the usual delay.
        ToolTip.SetShowDelay(target, 350);

        target.AddHandler(InputElement.HoldingEvent, (_, e) =>
        {
            if (e.HoldingState == HoldingState.Started)
            {
                TopLevel? topLevel = TopLevel.GetTopLevel(target);

                // Cap the tip to the screen: a window title is routinely wider than a phone.
                double screenWidth = topLevel?.ClientSize.Width ?? 360;
                tip.MaxWidth = Math.Max(120, screenWidth - 32);

                // Align the tip away from the closer screen edge, or a wide title would be
                // clipped on the side the tab sits.
                bool alignRight = topLevel is not null
                    && target.TranslatePoint(new Point(target.Bounds.Width / 2, 0), topLevel) is { } center
                    && center.X > screenWidth / 2;
                ToolTip.SetPlacement(target, alignRight
                    ? PlacementMode.BottomEdgeAlignedRight
                    : PlacementMode.BottomEdgeAlignedLeft);

                Close();
                s_openTip = target;

                PlatformHaptics.OnLongPress();
                ToolTip.SetIsOpen(target, true);
            }
            else
            {
                CloseIfOpen(target);
            }
        }, RoutingStrategies.Bubble);
    }

    /// <summary>
    /// Closes the tip a long press opened, if any. Call this before the owning control is
    /// discarded: nothing else would close a tip whose target never saw the matching release.
    /// </summary>
    public static void Close()
    {
        if (s_openTip is { } tip)
            CloseIfOpen(tip);
    }

    static void CloseIfOpen(Control target)
    {
        ToolTip.SetIsOpen(target, false);
        if (ReferenceEquals(s_openTip, target))
            s_openTip = null;
    }
}
