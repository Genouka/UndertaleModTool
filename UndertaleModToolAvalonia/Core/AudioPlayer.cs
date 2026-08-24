using System;
using System.Reflection;
using System.Runtime.InteropServices;
using SDL3;

namespace UndertaleModToolAvalonia;

public class AudioPlayer : IDisposable
{
    static Action<Action> mainThreadAction = null!;
    static Action<string>? reportError;

    static IntPtr mixer = IntPtr.Zero;
    static bool initialized;

    IntPtr audio;
    IntPtr track;

    readonly Mixer.TrackStoppedCallback trackStoppedCallback;
    GCHandle trackStoppedCallbackHandle;

    static void EnsureInitialized()
    {
        // Short-circuit only while the shared mixer is still alive; if it was ever torn down,
        // run initialization again instead of reusing a dead handle.
        if (initialized && mixer != IntPtr.Zero)
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
        trackStoppedCallback = new(OnTrackStoppped);

        // Every failure is reported as a dialog instead of being thrown: the callers construct
        // this player from async void handlers, where an exception would crash the whole app.
        // A failed player simply stays inert - it holds only zero handles, so Stop()/Dispose()
        // remain safe to call.
        try
        {
            Start(data);
        }
        catch (Exception e)
        {
            ReportError(e.Message);

            if (trackStoppedCallbackHandle.IsAllocated)
                trackStoppedCallbackHandle.Free();
        }
    }

    void Start(byte[] data)
    {
        EnsureInitialized();

        // Don't allow this be deallocated until the sound stops.
        trackStoppedCallbackHandle = GCHandle.Alloc(trackStoppedCallback);

        // Load audio
        GCHandle dataHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            IntPtr io = SDL.IOFromConstMem(dataHandle.AddrOfPinnedObject(), (nuint)data.Length);
            if (io == IntPtr.Zero)
                throw new InvalidOperationException($"{SDL.GetError()}");

            audio = Mixer.LoadAudioIO(mixer, io, predecode: true, closeio: true);

            if (audio == IntPtr.Zero)
                throw new InvalidOperationException($"{SDL.GetError()}");
        }
        finally
        {
            dataHandle.Free();
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

    /// <summary>
    /// Installs the thread-marshaling hook and the error dialog hook used by all players.
    /// </summary>
    public static void Init(Action<Action> _mainThreadAction, Action<string>? _reportError = null)
    {
        mainThreadAction = _mainThreadAction;
        reportError = _reportError;
    }

    static void ReportError(string message)
    {
        Action<string>? handler = reportError;
        if (handler is null)
            return;

        Action<Action> marshal = mainThreadAction;
        if (marshal is not null)
            marshal(() => handler(message));
        else
            handler(message);
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

        // NOTE: deliberately no Mixer.Quit() here! The mixer device is shared by every player
        // (static `mixer`) and must outlive individual tracks. Quitting it when the first track
        // stopped left all later playbacks using a dead handle - and because EnsureInitialized()
        // is latched, nothing ever recreated it ("audio only plays the first time" on Android).

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
