using System;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Platform hook for explicitly showing the on-screen keyboard. Avalonia only tells the platform
/// about the input method when the focused element changes; on Android a tap on an already
/// focused editor therefore does not re-open a keyboard that the IME dismissed. Platforms that
/// can drive their IME directly install a handler here.
/// </summary>
public static class PlatformSoftKeyboard
{
    /// <summary>
    /// Shows the software keyboard for the currently focused view. Returns <see langword="true"/>
    /// when the platform accepted the request, or <see langword="false"/> when no platform hook is
    /// installed (desktop) or the request could not be delivered.
    /// </summary>
    public static Func<bool>? ShowHandler;

    public static bool Show() => ShowHandler?.Invoke() == true;
}
