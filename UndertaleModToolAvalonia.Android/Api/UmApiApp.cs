using System;
using System.Diagnostics;
using System.Threading.Tasks;
using UndertaleModToolAvalonia;

namespace UndertaleModToolAvalonia.Android.Api;

/// <summary>
/// Bridges the API (which runs on Binder threads, possibly before the app finished starting) to the
/// one shared <see cref="MainViewModel"/>.
/// <para>
/// Android creates a process' content providers first and its <c>Application</c> afterwards; a
/// client can therefore reach the API service while Avalonia is still starting up. The service
/// waits here until <see cref="NotifyReady"/> was called (end of
/// <c>AvaloniaAndroidApp.OnCreate</c>) instead of failing a request that arrived a few milliseconds
/// too early.
/// </para>
/// </summary>
internal static class UmApiApp
{
    static readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Called once Avalonia (and the shared view model) finished starting.</summary>
    public static void NotifyReady() => ready.TrySetResult(true);

    /// <summary>
    /// Returns the app's view model, or null when the app did not finish starting in time (in
    /// which case the caller reports "the tool is still starting" to the client).
    /// </summary>
    public static async Task<MainViewModel?> GetViewModelAsync(int timeoutMillis = 20000)
    {
        if (!ready.Task.IsCompleted)
            await Task.WhenAny(ready.Task, Task.Delay(Math.Max(1000, timeoutMillis))).ConfigureAwait(false);

        // The view model is resolvable as soon as App.Services exists, but its scripting engine is
        // only created by MainViewModel.Initialize(), so that is the real "app is usable" signal.
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (true)
        {
            if (App.Services?.GetService(typeof(MainViewModel)) is MainViewModel vm && vm.Scripting is not null)
                return vm;

            if (stopwatch.ElapsedMilliseconds > 5000)
                return null;

            await Task.Delay(25).ConfigureAwait(false);
        }
    }
}
