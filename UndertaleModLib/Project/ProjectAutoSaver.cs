using System;
using System.Threading;
using System.Threading.Tasks;

namespace UndertaleModLib.Project;

/// <summary>
/// Periodically writes pending project assets to disk, so that a crash or a killed process loses
/// at most one auto-save interval of work.
/// </summary>
/// <remarks>
/// <para>
/// Saving reuses the incremental <see cref="ProjectContext.Export(bool)"/> path with
/// <see langword="false"/>: only assets that are marked for export are written, and the marks stay
/// in place so a later manual save writes the same assets again. This is deliberately *not* a
/// whole-data-file serialization - writing <c>data.win</c> takes seconds on large games, which is
/// exactly what the project workflow exists to avoid.
/// </para>
/// <para>
/// The class is UI-agnostic: it only knows about <see cref="Task"/>, the callbacks supplied by the
/// caller, and (through <see cref="ForProject"/>) a <see cref="ProjectContext"/>. Callbacks are
/// invoked on whatever thread finishes the save, so UI code is responsible for marshalling.
/// </para>
/// </remarks>
public sealed class ProjectAutoSaver : IDisposable
{
    private readonly TimeSpan _interval;
    private readonly Func<bool> _hasPendingChanges;
    private readonly Func<Task> _saveAsync;
    private readonly Action<Exception> _onError;
    private readonly Action<DateTimeOffset> _onSaved;

    // Single-flight gate, held for the whole duration of a save. Overlapping periodic ticks and
    // explicit SaveIfPendingAsync calls therefore never export at the same time; a caller that
    // finds the gate taken gives up instead of queueing another save.
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private readonly object _lifecycleLock = new();
    private CancellationTokenSource _loopCancellation;
    private Task _loopTask;
    private bool _disposed;

    /// <summary>
    /// Creates an auto-saver around caller-supplied delegates.
    /// </summary>
    /// <param name="interval">Time between automatic save attempts. Must be positive.</param>
    /// <param name="hasPendingChanges">Returns whether there is anything worth saving.</param>
    /// <param name="saveAsync">Performs the actual save.</param>
    /// <param name="onError">Optional handler for save failures; the periodic loop keeps running afterwards.</param>
    /// <param name="onSaved">Optional handler invoked with the completion time of a successful save.</param>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="interval"/> is not positive.</exception>
    /// <exception cref="ArgumentNullException">When <paramref name="hasPendingChanges"/> or <paramref name="saveAsync"/> is null.</exception>
    public ProjectAutoSaver(TimeSpan interval, Func<bool> hasPendingChanges, Func<Task> saveAsync,
                            Action<Exception> onError = null, Action<DateTimeOffset> onSaved = null)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "Auto-save interval must be positive");
        }

        _interval = interval;
        _hasPendingChanges = hasPendingChanges ?? throw new ArgumentNullException(nameof(hasPendingChanges));
        _saveAsync = saveAsync ?? throw new ArgumentNullException(nameof(saveAsync));
        _onError = onError;
        _onSaved = onSaved;
    }

    /// <summary>
    /// Whether a periodic loop is currently active.
    /// </summary>
    public bool IsRunning
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _loopCancellation is not null;
            }
        }
    }

    /// <summary>
    /// Whether a save is in flight right now.
    /// </summary>
    public bool IsSaving => _saveGate.CurrentCount == 0;

    /// <summary>
    /// Creates an auto-saver for the given project: pending state comes from
    /// <see cref="ProjectContext.HasUnexportedAssets"/>, and saving calls
    /// <see cref="ProjectContext.Export(bool)"/> with <see langword="false"/> (incremental export
    /// that keeps the marked assets in place) on a worker thread.
    /// </summary>
    public static ProjectAutoSaver ForProject(ProjectContext project, TimeSpan interval,
                                              Action<Exception> onError = null, Action<DateTimeOffset> onSaved = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new ProjectAutoSaver(interval, () => project.HasUnexportedAssets, () => Task.Run(() => project.Export(false)),
                                    onError, onSaved);
    }

    /// <summary>
    /// Starts the periodic loop. Does nothing if it is already running.
    /// </summary>
    /// <exception cref="ObjectDisposedException">When called after <see cref="Dispose"/>.</exception>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loopTask is not null)
            {
                return;
            }

            CancellationTokenSource cancellation = new();
            CancellationToken cancellationToken = cancellation.Token;
            _loopCancellation = cancellation;
            _loopTask = Task.Run(() => RunLoopAsync(cancellation, cancellationToken));
        }
    }

    /// <summary>
    /// Stops the periodic loop and cancels its next tick. A save that is already in flight is
    /// allowed to finish. Does not block.
    /// </summary>
    public void Stop()
    {
        CancellationTokenSource cancellation;
        lock (_lifecycleLock)
        {
            cancellation = _loopCancellation;
            _loopCancellation = null;
            _loopTask = null;
        }

        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    /// <summary>
    /// Stops the periodic loop. Safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }

        Stop();

        // The single-flight gate is intentionally not disposed: a save started just before Dispose
        // may still be releasing it, and a SemaphoreSlim holds no resources worth reclaiming.
    }

    /// <summary>
    /// Saves if there are pending changes and no save is currently in flight.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when this call performed a successful save; <see langword="false"/>
    /// when there was nothing to save, another save was already in flight (this call does not wait
    /// for it), or the save failed (the exception is passed to the error callback).
    /// </returns>
    public async Task<bool> SaveIfPendingAsync()
    {
        if (!_hasPendingChanges())
        {
            return false;
        }

        // Never queue saves: bail out immediately instead of stacking a second export behind the
        // one that is already running.
        if (!await _saveGate.WaitAsync(0).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            // Re-check inside the gate: the save that just finished may have cleared the marks.
            if (!_hasPendingChanges())
            {
                return false;
            }

            try
            {
                Task saveTask = _saveAsync();
                if (saveTask is not null)
                {
                    await saveTask.ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                // Background saving must never be fatal; report and let the next tick retry.
                ReportError(e);
                return false;
            }

            NotifySaved(DateTimeOffset.Now);
            return true;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task RunLoopAsync(CancellationTokenSource cancellation, CancellationToken cancellationToken)
    {
        try
        {
            using PeriodicTimer timer = new(_interval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await SaveIfPendingAsync().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    // Covers failures in the pending-state callback; keep ticking either way.
                    ReportError(e);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stop/Dispose was requested; this is the expected exit path.
        }
        catch (Exception e)
        {
            // Safety net for the timer itself, so a background loop can never take down the app.
            ReportError(e);
        }
        finally
        {
            // If the loop ended on its own, make IsRunning reflect that (Stop clears these too).
            lock (_lifecycleLock)
            {
                if (ReferenceEquals(_loopCancellation, cancellation))
                {
                    _loopCancellation = null;
                    _loopTask = null;
                }
            }
        }
    }

    private void ReportError(Exception exception)
    {
        try
        {
            _onError?.Invoke(exception);
        }
        catch (Exception)
        {
            // A broken error callback must not stop future auto-saves.
        }
    }

    private void NotifySaved(DateTimeOffset savedAt)
    {
        try
        {
            _onSaved?.Invoke(savedAt);
        }
        catch (Exception)
        {
            // A broken status callback must not turn a successful save into a failure.
        }
    }
}
