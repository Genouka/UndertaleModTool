using UndertaleModLib.Models;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Binding-friendly wrapper over <see cref="UndertaleAnimationCurve.Channel.Point"/>, whose members
/// are fields rather than properties and therefore cannot be used with Avalonia bindings directly.
/// Writes are immediately reflected into the wrapped point.
/// </summary>
public sealed class ChannelPointWrapper
{
    readonly UndertaleAnimationCurve.Channel.Point point;

    public ChannelPointWrapper(UndertaleAnimationCurve.Channel.Point point)
    {
        this.point = point;
    }

    public UndertaleAnimationCurve.Channel.Point Point => point;

    public float X { get => point.X; set => point.X = value; }
    public float Value { get => point.Value; set => point.Value = value; }
    public float BezierX0 { get => point.BezierX0; set => point.BezierX0 = value; }
    public float BezierY0 { get => point.BezierY0; set => point.BezierY0 = value; }
    public float BezierX1 { get => point.BezierX1; set => point.BezierX1 = value; }
    public float BezierY1 { get => point.BezierY1; set => point.BezierY1 = value; }
}