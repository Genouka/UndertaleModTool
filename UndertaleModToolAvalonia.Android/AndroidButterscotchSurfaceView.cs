using System;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Views;
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

    public AndroidButterscotchSurfaceView()
    {
        surfaceCallback = new SurfaceCallback(this);
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        Context? context = (parent as AndroidViewControlHandle)?.View?.Context;
        if (context is null)
            return base.CreateNativeControlCore(parent);

        surfaceView = new SurfaceView(context);
        surfaceView.Holder?.AddCallback(surfaceCallback);

        // Only the native View handle crosses the boundary; the attachment adds it to the
        // AvaloniaView and positions it with the control's bounds.
        return new AndroidViewControlHandle(surfaceView);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        surfaceView?.Holder?.RemoveCallback(surfaceCallback);
        surfaceView = null;
        WithdrawSurface();
        surface?.Dispose();
        surface = null;
        base.DestroyNativeControlCore(control);
    }

    void OnSurfaceCreated(ISurfaceHolder holder)
    {
        surface = holder.Surface;
        // The runner calls ANativeWindow_fromSurface on this android.view.Surface jobject.
        PublishSurface(surface?.Handle ?? IntPtr.Zero);
    }

    void OnSurfaceDestroyed()
    {
        WithdrawSurface();
        surface?.Dispose();
        surface = null;
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