using Avalonia.Media;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Stroke geometries from Tabler Icons (https://tabler.io/icons), the outline set: 24x24 view box,
/// 2px stroke, round caps and joins. They are drawn by <see cref="TablerIcon"/>; the names match
/// the icon names in the upstream library.
/// </summary>
/// <remarks>
/// Tabler Icons is MIT licensed, Copyright (c) 2020-2026 Paweł Kuna.
/// The full license text is in Assets/TABLER-ICONS-LICENSE.txt.
/// </remarks>
public static class TablerIcons
{
    /// <summary>player-play</summary>
    public static Geometry PlayerPlay { get; } = StreamGeometry.Parse("M7 4v16l13 -8l-13 -8");

    /// <summary>player-pause</summary>
    public static Geometry PlayerPause { get; } = StreamGeometry.Parse(
        "M6 6a1 1 0 0 1 1 -1h2a1 1 0 0 1 1 1v12a1 1 0 0 1 -1 1h-2a1 1 0 0 1 -1 -1l0 -12 " +
        "M14 6a1 1 0 0 1 1 -1h2a1 1 0 0 1 1 1v12a1 1 0 0 1 -1 1h-2a1 1 0 0 1 -1 -1l0 -12");

    /// <summary>step-into</summary>
    public static Geometry StepInto { get; } = StreamGeometry.Parse(
        "M12 3l0 12 M16 11l-4 4 M8 11l4 4 M11 20a1 1 0 1 0 2 0a1 1 0 1 0 -2 0");

    /// <summary>step-out</summary>
    public static Geometry StepOut { get; } = StreamGeometry.Parse(
        "M12 3l0 12 M16 7l-4 -4 M8 7l4 -4 M11 20a1 1 0 1 0 2 0a1 1 0 1 0 -2 0");

    /// <summary>arrow-ramp-right-2, used for "step over" (Tabler has no step-over icon)</summary>
    public static Geometry StepOver { get; } = StreamGeometry.Parse(
        "M6 3v8.707 M16 14l4 -4l-4 -4 M6 21c0 -6.075 4.925 -11 11 -11h3");

    /// <summary>player-track-next</summary>
    public static Geometry PlayerTrackNext { get; } = StreamGeometry.Parse(
        "M3 5v14l8 -7l-8 -7 M14 5v14l8 -7l-8 -7");

    /// <summary>refresh</summary>
    public static Geometry Refresh { get; } = StreamGeometry.Parse(
        "M20 11a8.1 8.1 0 0 0 -15.5 -2m-.5 -4v4h4 " +
        "M4 13a8.1 8.1 0 0 0 15.5 2m.5 4v-4h-4");

    /// <summary>player-stop</summary>
    public static Geometry PlayerStop { get; } = StreamGeometry.Parse(
        "M5 7a2 2 0 0 1 2 -2h10a2 2 0 0 1 2 2v10a2 2 0 0 1 -2 2h-10a2 2 0 0 1 -2 -2l0 -10");

    /// <summary>maximize</summary>
    public static Geometry Maximize { get; } = StreamGeometry.Parse(
        "M4 8v-2a2 2 0 0 1 2 -2h2 M4 16v2a2 2 0 0 0 2 2h2 " +
        "M16 4h2a2 2 0 0 1 2 2v2 M16 20h2a2 2 0 0 0 2 -2v-2");

    /// <summary>x</summary>
    public static Geometry X { get; } = StreamGeometry.Parse("M18 6l-12 12 M6 6l12 12");

    /// <summary>menu-2</summary>
    public static Geometry Menu { get; } = StreamGeometry.Parse("M4 6l16 0 M4 12l16 0 M4 18l16 0");

    /// <summary>arrow-up</summary>
    public static Geometry ArrowUp { get; } = StreamGeometry.Parse(
        "M12 5l0 14 M18 11l-6 -6 M6 11l6 -6");

    /// <summary>arrow-down</summary>
    public static Geometry ArrowDown { get; } = StreamGeometry.Parse(
        "M12 5l0 14 M18 13l-6 6 M6 13l6 6");

    /// <summary>arrow-left</summary>
    public static Geometry ArrowLeft { get; } = StreamGeometry.Parse(
        "M5 12l14 0 M5 12l6 6 M5 12l6 -6");

    /// <summary>arrow-right</summary>
    public static Geometry ArrowRight { get; } = StreamGeometry.Parse(
        "M5 12l14 0 M13 18l6 -6 M13 6l6 6");

    /// <summary>chevron-up, used for the virtual d-pad</summary>
    public static Geometry ChevronUp { get; } = StreamGeometry.Parse("M6 15l6 -6l6 6");

    /// <summary>chevron-down</summary>
    public static Geometry ChevronDown { get; } = StreamGeometry.Parse("M6 9l6 6l6 -6");

    /// <summary>chevron-left</summary>
    public static Geometry ChevronLeft { get; } = StreamGeometry.Parse("M15 6l-6 6l6 6");

    /// <summary>chevron-right</summary>
    public static Geometry ChevronRight { get; } = StreamGeometry.Parse("M9 6l6 6l-6 6");
}
