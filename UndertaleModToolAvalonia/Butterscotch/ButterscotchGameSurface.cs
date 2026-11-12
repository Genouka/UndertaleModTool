using System;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using UndertaleModRunner;

namespace UndertaleModToolAvalonia;

/// <summary>
/// A native view that the Butterscotch runner can render into. Single-window platforms (Android)
/// provide one through <see cref="ButterscotchSurfaceHost.Factory"/>; the host creates a native
/// view (<c>NativeControlHost</c>), and once its surface exists it reports the handle the engine
/// should use instead of creating a top-level window.
/// </summary>
public interface IButterscotchGameSurface
{
    /// <summary>The control to place in the game tab; it hosts the native view.</summary>
    Control View { get; }

    /// <summary>How <see cref="NativeWindowHandle"/> must be interpreted by the runner.</summary>
    BsdbgEmbedWindowKind Kind { get; }

    /// <summary>Native window (Android: <c>android.view.Surface</c> jobject) to render into.</summary>
    IntPtr NativeWindowHandle { get; }

    bool IsReady { get; }

    event Action? Ready;
}

/// <summary>
/// Platform hook for the game view. Desktop leaves the factory null and the embedded runner opens
/// its own window (matching the WPF tool); single-window platforms install a factory at startup.
/// </summary>
public static class ButterscotchSurfaceHost
{
    /// <summary>Replaced by single-window platform modules (Android).</summary>
    public static Func<IButterscotchGameSurface?>? Factory;

    /// <summary>Installed by platforms that can hide their system chrome (Android).</summary>
    public static Action<bool>? FullscreenRequested;

    public static bool IsSupported => Factory is not null;

    public static bool SupportsFullscreen => FullscreenRequested is not null;


    /// <summary>Turns touches on the game view into the left mouse button (single-window hosts).</summary>
    public static bool TouchAsMouse = true;

    public static void RequestFullscreen(bool fullscreen) => FullscreenRequested?.Invoke(fullscreen);
}

/// <summary>
/// Tab content that displays the native game view. The tab owns the surface for as long as it is
/// open: closing it stops the session, because the native view it renders into is gone.
/// </summary>
public partial class ButterscotchGameViewModel : ObservableObject, ITabContent
{
    readonly bool subscribed;

    public IButterscotchGameSurface Surface { get; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    /// <summary>Session state shown next to the transport buttons.</summary>
    [ObservableProperty]
    public partial string DebugStatus { get; set; } = "";

    /// <summary>Whether the session is paused; drives the pause/resume transport icon.</summary>
    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    /// <summary>Whether the in-tab debug bar is shown (debug sessions).</summary>
    [ObservableProperty]
    public partial bool ShowDebugPanel { get; set; }

    [ObservableProperty]
    public partial bool IsFullscreen { get; set; }

    /// <summary>Shows the on-screen gamepad over the game view.</summary>
    [ObservableProperty]
    public partial bool ShowVirtualGamepad { get; set; }

    /// <summary>Shows the on-screen key bar over the game view.</summary>
    [ObservableProperty]
    public partial bool ShowVirtualKeypad { get; set; }

    /// <summary>Only single-window platforms can go fullscreen.</summary>
    public bool ShowFullscreen => ButterscotchSurfaceHost.SupportsFullscreen;

    public ButterscotchGameViewModel(IButterscotchGameSurface surface, bool showDebugPanel = false)
    {
        Surface = surface;
        ShowDebugPanel = showDebugPanel;
        try
        {
            MainViewModel main = App.Services.GetRequiredService<MainViewModel>();
            ShowVirtualGamepad = main.Settings.ButterscotchVirtualGamepad;
            ShowVirtualKeypad = main.Settings.ButterscotchVirtualKeypad;
            ButterscotchSurfaceHost.TouchAsMouse = main.Settings.ButterscotchTouchAsMouse;
        }
        catch (InvalidOperationException)
        {
        }
        try
        {
            ButterscotchDebugService.Instance.Updated += OnDebugUpdated;
            ButterscotchDebugService.Instance.StatusUpdated += OnDebugUpdated;
            subscribed = true;
        }
        catch (InvalidOperationException)
        {
            // No service container (design time); the bar simply stays empty.
        }
    }

    void OnDebugUpdated()
    {
        ButterscotchDebugService service = ButterscotchDebugService.Instance;
        DebugState? state = service.State;
        if (!service.IsActive || state is null)
        {
            DebugStatus = "";
            IsPaused = false;
            return;
        }

        DebugStatus = $"{(state.Paused ? "Paused" : "Running")} · frame {state.FrameCount}";
        IsPaused = state.Paused;
    }

    public void OnDetached()
    {
        if (subscribed)
        {
            try
            {
                ButterscotchDebugService.Instance.Updated -= OnDebugUpdated;
                ButterscotchDebugService.Instance.StatusUpdated -= OnDebugUpdated;
            }
            catch (InvalidOperationException)
            {
            }
        }

        if (IsFullscreen)
            ButterscotchSurfaceHost.RequestFullscreen(false);

        App.Services.GetRequiredService<MainViewModel>().Butterscotch.Stop();
    }
}