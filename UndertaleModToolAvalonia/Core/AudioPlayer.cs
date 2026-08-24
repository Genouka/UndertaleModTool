using System;
using System.Reflection;
using System.Runtime.InteropServices;
using SDL3;

namespace UndertaleModToolAvalonia;

public class AudioPlayer : IDisposable
{
    static Action<Action> mainThreadAction = null!;

    static IntPtr mixer = IntPtr.Zero;
    static bool initialized;

    IntPtr audio;
    IntPtr track;

    readonly Mixer.TrackStoppedCallback trackStoppedCallback;
    GCHandle trackStoppedCallbackHandle;

    static void EnsureInitialized()
    {
        if (initialized)
            return;
        initialized = true;

        if ((SDL.WasInit(SDL.InitFlags.Audio) & SDL.InitFlags.Audio) == 0)
        {
            SDL.SetHint(SDL.Hints.AppName, Assembly.GetExecutingAssembly().GetName().Name ?? "");

            if (!SDL.Init(SDL.InitFlags.Audio))
                throw new InvalidOperationException($"{SDL.GetError()}");
        }

        if (!Mixer.Init())
            throw new InvalidOperationException($"{SDL.GetError()}");

        if (mixer == IntPtr.Zero)
        {
            mixer = Mixer.CreateMixerDevice(SDL.AudioDeviceDefaultPlayback, IntPtr.Zero);
            if (mixer == IntPtr.Zero)
                throw new InvalidOperationException($"{SDL.GetError()}");
        }
    }

    public AudioPlayer(byte[] data)
    {
        // SDL/SDL_mixer are initialized lazily on first playback. On Android the SDL Java bridge
        // needs an Activity context (installed by the Android host) before any SDL call that may
        // use Android file I/O can succeed, and that context only exists once the Activity is up.
        EnsureInitialized();
        // Don't allow this be deallocated until the sound stops.
        trackStoppedCallback = new(OnTrackStoppped);
        trackStoppedCallbackHandle = GCHandle.Alloc(trackStoppedCallback);

        // Load audio
        GCHandle dataHandle = GCHandle.Alloc(data, GCHandleType.Pinned);

        IntPtr io = SDL.IOFromConstMem(dataHandle.AddrOfPinnedObject(), (nuint)data.Length);
        if (io == IntPtr.Zero)
            throw new InvalidOperationException($"{SDL.GetError()}");

        audio = Mixer.LoadAudioIO(mixer, io, predecode: true, closeio: true);

        dataHandle.Free();

        if (audio == IntPtr.Zero)
        {
            // TODO: Show some kind of error
            return;
        }

        // Create track and play
        track = Mixer.CreateTrack(mixer);
        if (track == IntPtr.Zero)
            throw new InvalidOperationException($"{SDL.GetError()}");

        if (!Mixer.SetTrackAudio(track, audio))
            throw new InvalidOperationException($"{SDL.GetError()}");

        if (!Mixer.PlayTrack(track, 0))
            throw new InvalidOperationException($"{SDL.GetError()}");

        if (!Mixer.SetTrackStoppedCallback(track, trackStoppedCallback, IntPtr.Zero))
            throw new InvalidOperationException($"{SDL.GetError()}");
    }

    public static void Init(Action<Action> _mainThreadAction)
    {
        mainThreadAction = _mainThreadAction;
    }

    public void Stop()
    {
        Dispose();
    }

    public void Dispose()
    {
        // If those are null, nothing happens. They also don't call the track stopped callback.
        Mixer.DestroyTrack(track);
        Mixer.DestroyAudio(audio);
        Mixer.Quit();

        if (trackStoppedCallbackHandle.IsAllocated)
            trackStoppedCallbackHandle.Free();

        track = IntPtr.Zero;
        audio = IntPtr.Zero;

        GC.SuppressFinalize(this);
    }

    void OnTrackStoppped(IntPtr userdata, IntPtr track)
    {
        // The callback happens in a separate thread, so we defer to the main thread.
        mainThreadAction(() =>
        {
            Dispose();
        });
    }
}
