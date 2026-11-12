using System;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Views.InputMethods;
using Avalonia.Android;
using Avalonia.Platform;

namespace UndertaleModToolAvalonia.Android;

/// <summary>
/// Native game view for single-window platforms: the runner renders into this SurfaceView,
/// hosted inside the Avalonia view tree by <see cref="Avalonia.Controls.NativeControlHost"/>,
/// instead of opening a window of its own.
/// </summary>
public sealed class AndroidButterscotchSurfaceView : ButterscotchNativeSurfaceView
{
    readonly ISurfaceHolderCallback surfaceCallback;

    SurfaceView? surfaceView;
    Surface? surface;
    bool destroyed;

    public AndroidButterscotchSurfaceView()
    {
        surfaceCallback = new SurfaceCallback(this);
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        Context? context = (parent as AndroidViewControlHandle)?.View?.Context;
        if (context is null)
            return base.CreateNativeControlCore(parent);

        destroyed = false;
        surfaceView = new SurfaceView(context);
        surfaceView.Holder?.AddCallback(surfaceCallback);
        surfaceView.Focusable = true;
        surfaceView.FocusableInTouchMode = true;
        surfaceView.Touch += OnSurfaceTouch;

        // Only the native View handle crosses the boundary; the attachment adds it to the
        // AvaloniaView and positions it with the control's bounds.
        return new AndroidViewControlHandle(surfaceView);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        // Avalonia's native-control attachment owns the view and disposes it before calling
        // this, so touching the SurfaceView (or its holder) here throws ObjectDisposedException.
        // Only drop our own references; do not call base, which would dispose it again.
        destroyed = true;
        WithdrawSurface();
        surfaceView = null;
        DisposeSurface();
    }

    void DisposeSurface()
    {
        try
        {
            surface?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // The Java surface is already gone.
        }
        surface = null;
    }

    void OnSurfaceCreated(ISurfaceHolder holder)
    {
        if (destroyed)
            return;

        surface = holder.Surface;
        if (surface is null || !surface.IsValid)
        {
            WithdrawSurface();
            return;
        }

        // The runner calls ANativeWindow_fromSurface on this android.view.Surface jobject.
        PublishSurface(surface.Handle);
    }

    void OnSurfaceDestroyed()
    {
        WithdrawSurface();
        DisposeSurface();
    }

    void OnSurfaceTouch(object? sender, View.TouchEventArgs e)
    {
        // Touch handling is forwarded to the runner's input queue. Until input injection is
        // wired the event is consumed so Avalonia does not scroll/click behind the game view.
        e.Handled = true;
    }

    sealed class SurfaceCallback : Java.Lang.Object, ISurfaceHolderCallback
    {
        readonly AndroidButterscotchSurfaceView owner;

        public SurfaceCallback(AndroidButterscotchSurfaceView owner)
        {
            this.owner = owner;
        }

        public void SurfaceCreated(ISurfaceHolder holder) => owner.OnSurfaceCreated(holder);

        public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height)
        {
            // The engine sizes itself from the surface; nothing to do per size change.
        }

        public void SurfaceDestroyed(ISurfaceHolder holder) => owner.OnSurfaceDestroyed();
    }
}
