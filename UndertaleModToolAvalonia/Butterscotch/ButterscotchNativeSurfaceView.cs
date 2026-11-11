using System;
using Avalonia.Controls;
using UndertaleModRunner;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Base class for the platform native game views used on single-window systems. A subclass
/// creates the native view in <see cref="NativeControlHost.CreateNativeControlCore"/> and calls
/// <see cref="PublishSurface"/> once the engine has something to render into; the runner then
/// draws into that surface instead of opening a window of its own.
/// </summary>
public abstract class ButterscotchNativeSurfaceView : NativeControlHost
{
    IntPtr nativeWindowHandle;
    bool surfaceReady;

    /// <summary>Raised when a renderable surface becomes available (and on every re-creation).</summary>
    public event Action? SurfaceReady;

    /// <summary>True once <see cref="NativeWindowHandle"/> addresses a live surface.</summary>
    public bool IsSurfaceReady => surfaceReady;

    /// <summary>Handle the runner renders into; zero until the surface exists.</summary>
    public IntPtr NativeWindowHandle => nativeWindowHandle;

    /// <summary>How the runner must interpret <see cref="NativeWindowHandle"/>.</summary>
    public virtual BsdbgEmbedWindowKind WindowKind => BsdbgEmbedWindowKind.AndroidSurface;

    /// <summary>Publishes a newly created native surface; zero withdraws it.</summary>
    protected void PublishSurface(IntPtr handle)
    {
        nativeWindowHandle = handle;
        surfaceReady = handle != IntPtr.Zero;
        if (surfaceReady)
            SurfaceReady?.Invoke();
    }

    /// <summary>Marks the surface as gone, so a late start cannot render into freed memory.</summary>
    protected void WithdrawSurface()
    {
        nativeWindowHandle = IntPtr.Zero;
        surfaceReady = false;
    }

    /// <summary>Exposes this control as the game surface that a game tab displays.</summary>
    public IButterscotchGameSurface AsGameSurface() => new HostedSurface(this);

    sealed class HostedSurface : IButterscotchGameSurface
    {
        readonly ButterscotchNativeSurfaceView view;

        public HostedSurface(ButterscotchNativeSurfaceView view)
        {
            this.view = view;
        }

        public Control View => view;

        public BsdbgEmbedWindowKind Kind => view.WindowKind;

        public IntPtr NativeWindowHandle => view.NativeWindowHandle;

        public bool IsReady => view.IsSurfaceReady;

        public event Action? Ready
        {
            add => view.SurfaceReady += value;
            remove => view.SurfaceReady -= value;
        }
    }
}
