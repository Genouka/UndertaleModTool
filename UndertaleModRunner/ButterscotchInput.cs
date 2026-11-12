using System;

namespace UndertaleModRunner;

/// <summary>
/// Feeds device input into the embedded runner. Single-window hosts have no input pump of their
/// own, so they queue events here and the runner applies them on the game thread at the start of
/// the next frame.
/// </summary>
public static class ButterscotchInput
{
    /// <summary>Left mouse button index as used by the engine (GML_MB_LEFT).</summary>
    public const int LeftButton = 1;

    /// <summary>
    /// Reports a pointer position in surface pixels. <paramref name="down"/> only changes the
    /// button state when it is set, so a move does not repeat button events.
    /// </summary>
    public static void Pointer(double x, double y, bool? down = null)
    {
        NativeMethods.Bsdbg_InjectMousePos(x, y);
        if (down.HasValue)
            NativeMethods.Bsdbg_InjectMouseButton(LeftButton, down.Value ? 1 : 0);
    }

    /// <summary>GML virtual key code (ASCII for letters/digits, VK_* otherwise).</summary>
    public static void Key(int gmlKey, bool down) =>
        NativeMethods.Bsdbg_InjectKey(gmlKey, down ? 1 : 0);

    /// <summary>Text input for the keyboard string functions.</summary>
    public static void Text(uint character) => NativeMethods.Bsdbg_InjectKeyText(character);

    public static void Wheel(double offset) => NativeMethods.Bsdbg_InjectMouseWheel(offset);

    /// <summary>Gamepad button indexes follow the engine slot layout (0..16, 6/7 = triggers).</summary>
    public static void GamepadButton(int button, bool down, float value = 1.0f) =>
        NativeMethods.Bsdbg_InjectGamepadButton(button, down ? 1 : 0, value);

    /// <summary>Axis indexes 0..3 are LH, LV, RH, RV in [-1, 1].</summary>
    public static void GamepadAxis(int axis, float value) =>
        NativeMethods.Bsdbg_InjectGamepadAxis(axis, value);

    // Engine gamepad button indexes (mirrors runner_gamepad.h).
    public const int PadFace1 = 0;
    public const int PadFace2 = 1;
    public const int PadFace3 = 2;
    public const int PadFace4 = 3;
    public const int PadLeftShoulder = 4;
    public const int PadRightShoulder = 5;
    public const int PadSelect = 8;
    public const int PadStart = 9;
    public const int PadUp = 12;
    public const int PadDown = 13;
    public const int PadLeft = 14;
    public const int PadRight = 15;

    // Axis indexes.
    public const int AxisLeftH = 0;
    public const int AxisLeftV = 1;
    public const int AxisRightH = 2;
    public const int AxisRightV = 3;
}
