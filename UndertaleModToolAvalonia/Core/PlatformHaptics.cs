using System;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Touch haptic feedback bridge for the shared UI.
/// Desktop builds leave the callbacks unset (the calls become no-ops). Single-window platforms such
/// as Android register real implementations (see <c>UndertaleModToolAvalonia.Android/MainActivity.cs</c>),
/// which route to the platform's haptic feedback APIs. All calls are expected on the UI thread.
/// </summary>
public static class PlatformHaptics
{
    /// <summary>Platform callback performing a long-press haptic pulse.</summary>
    public static Action? LongPressFeedback;

    /// <summary>Platform callback performing a light click/tap haptic pulse.</summary>
    public static Action? TapFeedback;

    /// <summary>Invoked when a long-press gesture is recognized anywhere in the UI.</summary>
    public static void OnLongPress() => LongPressFeedback?.Invoke();

    /// <summary>Invoked when a light click/tap gesture is recognized anywhere in the UI.</summary>
    public static void OnTap() => TapFeedback?.Invoke();
}