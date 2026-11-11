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

    public static bool IsSupported => Factory is not null;
}

/// <summary>
/// Tab content that displays the native game view. The tab owns the surface for as long as it is
/// open: closing it stops the session, because the native view it renders into is gone.
/// </summary>
public partial class ButterscotchGameViewModel : ObservableObject, ITabContent
{
    public IButterscotchGameSurface Surface { get; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    public ButterscotchGameViewModel(IButterscotchGameSurface surface)
    {
        Surface = surface;
    }

    public void OnDetached()
    {
        App.Services.GetRequiredService<MainViewModel>().Butterscotch.Stop();
    }
}