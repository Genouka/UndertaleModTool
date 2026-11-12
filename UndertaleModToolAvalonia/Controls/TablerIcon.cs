using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Renders a Tabler outline icon (<see cref="TablerIcons"/>). The stroke follows the inherited
/// <see cref="TemplatedControl.Foreground"/>, so the icon dims with a disabled button just like
/// its text would. The default template is in App.axaml.
/// </summary>
public class TablerIcon : TemplatedControl
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<TablerIcon, Geometry?>(nameof(Data));

    /// <summary>The 24x24 stroke geometry to draw.</summary>
    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }
}
