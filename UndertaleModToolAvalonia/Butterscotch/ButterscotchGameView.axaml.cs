using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace UndertaleModToolAvalonia;

public partial class ButterscotchGameView : UserControl
{
    public ButterscotchGameView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }

    void OnSizeChanged(object? sender, SizeChangedEventArgs e) => ApplyOrientation(e.NewSize);

    /// <summary>
    /// Portrait keeps the debug actions in a strip along the bottom; landscape moves them to a
    /// column on the left, where they do not cover the game.
    /// </summary>
    void ApplyOrientation(Size size)
    {
        bool landscape = size.Width > size.Height;

        DebugActions.Orientation = landscape ? Orientation.Vertical : Orientation.Horizontal;
        DebugActions.HorizontalAlignment = landscape ? HorizontalAlignment.Left : HorizontalAlignment.Center;

        DebugBar.HorizontalAlignment = landscape ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        DebugBar.VerticalAlignment = landscape ? VerticalAlignment.Center : VerticalAlignment.Bottom;
    }

    static ButterscotchDebugService Debug => ButterscotchDebugService.Instance;

    void OnPauseResume(object? sender, RoutedEventArgs e)
    {
        if (Debug.IsPaused)
            Debug.Resume();
        else
            Debug.Pause();
    }

    void OnStepInto(object? sender, RoutedEventArgs e) => Debug.StepInto();

    void OnStepOver(object? sender, RoutedEventArgs e) => Debug.StepOver();

    void OnStepOut(object? sender, RoutedEventArgs e) => Debug.StepOut();

    void OnStepFrame(object? sender, RoutedEventArgs e) => Debug.StepFrame();

    void OnHotReload(object? sender, RoutedEventArgs e) => Debug.HotReloadCurrentTab();

    void OnStop(object? sender, RoutedEventArgs e) => Debug.Stop();

    void OnToggleFullscreen(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ButterscotchGameViewModel viewModel)
            return;

        viewModel.IsFullscreen = !viewModel.IsFullscreen;
        ButterscotchSurfaceHost.RequestFullscreen(viewModel.IsFullscreen);
    }
}
