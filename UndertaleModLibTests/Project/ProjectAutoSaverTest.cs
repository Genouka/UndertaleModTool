using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UndertaleModLib.Project;
using Xunit;

namespace UndertaleModLibTests.Project;

public class ProjectAutoSaverTest
{
    static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Condition was not reached in time");
            await Task.Delay(10);
        }
    }

    [Fact]
    public void RejectsNonPositiveInterval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ProjectAutoSaver(TimeSpan.Zero, () => false, () => Task.CompletedTask));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ProjectAutoSaver(TimeSpan.FromSeconds(-1), () => false, () => Task.CompletedTask));
    }

    [Fact]
    public void RejectsMissingDelegates()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ProjectAutoSaver(TimeSpan.FromSeconds(1), null!, () => Task.CompletedTask));
        Assert.Throws<ArgumentNullException>(() =>
            new ProjectAutoSaver(TimeSpan.FromSeconds(1), () => false, null!));
    }

    [Fact]
    public async Task SavesOnlyWhenPendingChanges()
    {
        bool pending = false;
        int saves = 0;
        DateTimeOffset? savedAt = null;

        using ProjectAutoSaver saver = new(TimeSpan.FromMinutes(5),
            () => pending,
            () => { saves++; return Task.CompletedTask; },
            onSaved: at => savedAt = at);

        Assert.False(await saver.SaveIfPendingAsync());
        Assert.Equal(0, saves);
        Assert.Null(savedAt);

        pending = true;
        Assert.True(await saver.SaveIfPendingAsync());
        Assert.Equal(1, saves);
        Assert.NotNull(savedAt);

        // No new work; a second call must not save again.
        pending = false;
        Assert.False(await saver.SaveIfPendingAsync());
        Assert.Equal(1, saves);
    }

    [Fact]
    public async Task ConcurrentCallsProduceSingleSave()
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource saveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int saves = 0;

        using ProjectAutoSaver saver = new(TimeSpan.FromMinutes(5),
            () => true,
            async () =>
            {
                Interlocked.Increment(ref saves);
                saveStarted.TrySetResult();
                await gate.Task;
            });

        Task<bool> first = saver.SaveIfPendingAsync();
        await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(saver.IsSaving);

        // Second caller must not start a second save; it gives up immediately.
        Assert.False(await saver.SaveIfPendingAsync());
        Assert.Equal(1, Volatile.Read(ref saves));

        gate.SetResult();
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(saver.IsSaving);
        Assert.Equal(1, Volatile.Read(ref saves));

        // Once the gate is released, a later call can save again.
        Assert.True(await saver.SaveIfPendingAsync());
        Assert.Equal(2, Volatile.Read(ref saves));
    }

    [Fact]
    public async Task ExceptionsAreReportedAndNextTriggerSaves()
    {
        int attempts = 0;
        int errors = 0;
        Exception? captured = null;

        using ProjectAutoSaver saver = new(TimeSpan.FromMinutes(5),
            () => true,
            () =>
            {
                attempts++;
                if (attempts == 1)
                    throw new InvalidOperationException("boom");
                return Task.CompletedTask;
            },
            onError: e => { errors++; captured = e; });

        Assert.False(await saver.SaveIfPendingAsync());
        Assert.Equal(1, errors);
        Assert.IsType<InvalidOperationException>(captured);

        // The failure did not poison the saver: the next trigger saves normally.
        Assert.True(await saver.SaveIfPendingAsync());
        Assert.Equal(2, attempts);
        Assert.Equal(1, errors);
    }

    [Fact]
    public async Task PeriodicLoopKeepsRunningAfterErrors()
    {
        int errors = 0;

        using ProjectAutoSaver saver = new(TimeSpan.FromMilliseconds(25),
            () => true,
            () => throw new InvalidOperationException("boom"),
            onError: _ => Interlocked.Increment(ref errors));

        saver.Start();
        Assert.True(saver.IsRunning);

        await WaitUntilAsync(() => Volatile.Read(ref errors) >= 2, TimeSpan.FromSeconds(2));
        Assert.True(saver.IsRunning);
    }

    [Fact]
    public async Task StopCancelsPeriodicLoop()
    {
        int saves = 0;

        using ProjectAutoSaver saver = new(TimeSpan.FromMilliseconds(25),
            () => true,
            () => { Interlocked.Increment(ref saves); return Task.CompletedTask; });

        saver.Start();
        Assert.True(saver.IsRunning);
        await WaitUntilAsync(() => Volatile.Read(ref saves) > 0, TimeSpan.FromSeconds(2));

        saver.Stop();
        Assert.False(saver.IsRunning);

        // Let a tick that was already in flight finish, then confirm nothing new starts.
        await Task.Delay(100);
        int afterStop = Volatile.Read(ref saves);
        await Task.Delay(200);
        Assert.Equal(afterStop, Volatile.Read(ref saves));
    }

    [Fact]
    public async Task StopThenStartResumesPeriodicLoop()
    {
        // The UI pauses auto-save around whole-data-file saves and restarts it afterwards.
        int saves = 0;

        using ProjectAutoSaver saver = new(TimeSpan.FromMilliseconds(25),
            () => true,
            () => { Interlocked.Increment(ref saves); return Task.CompletedTask; });

        saver.Start();
        await WaitUntilAsync(() => Volatile.Read(ref saves) > 0, TimeSpan.FromSeconds(2));

        saver.Stop();
        await Task.Delay(100);
        int afterStop = Volatile.Read(ref saves);

        saver.Start();
        Assert.True(saver.IsRunning);
        await WaitUntilAsync(() => Volatile.Read(ref saves) > afterStop, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task DisposeCancelsPeriodicLoop()
    {
        int saves = 0;

        ProjectAutoSaver saver = new(TimeSpan.FromMilliseconds(25),
            () => true,
            () => { Interlocked.Increment(ref saves); return Task.CompletedTask; });

        saver.Start();
        await WaitUntilAsync(() => Volatile.Read(ref saves) > 0, TimeSpan.FromSeconds(2));

        saver.Dispose();
        Assert.False(saver.IsRunning);
        Assert.Throws<ObjectDisposedException>(() => saver.Start());

        await Task.Delay(100);
        int afterDispose = Volatile.Read(ref saves);
        await Task.Delay(200);
        Assert.Equal(afterDispose, Volatile.Read(ref saves));
    }

    [Fact]
    public async Task ForProjectWiresPendingStateAndExport()
    {
        // No directory needs to exist for this: the context below is never imported, so the export
        // guard fails before any file access. This keeps the test hermetic.
        string directory = Path.GetTempPath();
        ProjectContext project = ProjectContext.CreateWithDirectories(directory, directory,
            Path.Combine(directory, "utmt-autosave-" + Guid.NewGuid().ToString("N") + ".json"));

        Exception? capturedError = null;
        DateTimeOffset? savedAt = null;
        using ProjectAutoSaver saver = ProjectAutoSaver.ForProject(project, TimeSpan.FromMinutes(5),
            onError: e => capturedError = e,
            onSaved: at => savedAt = at);

        // Nothing marked for export: the pending delegate keeps the saver idle.
        Assert.False(await saver.SaveIfPendingAsync());
        Assert.Null(capturedError);

        // Marking an asset makes the project dirty, so the save delegate runs. That delegate is
        // Export(false), and an unimported context rejects it - proving the wiring end to end.
        Assert.True(project.MarkAssetForExport(new FakeProjectAsset()));
        Assert.True(project.HasUnexportedAssets);

        Assert.False(await saver.SaveIfPendingAsync());
        InvalidOperationException error = Assert.IsType<InvalidOperationException>(capturedError);
        Assert.Contains("not yet been imported", error.Message);
        Assert.Null(savedAt);

        // A failed auto-save keeps the pending mark, so the next tick retries.
        Assert.True(project.HasUnexportedAssets);
        Assert.False(await saver.SaveIfPendingAsync());
        Assert.IsType<InvalidOperationException>(capturedError);
    }

    /// <summary>
    /// Minimal project asset used to make a project "dirty" without importing real game data.
    /// </summary>
    sealed class FakeProjectAsset : IProjectAsset
    {
        public string ProjectName => "Fake Asset";

        public SerializableAssetType ProjectAssetType => SerializableAssetType.Code;

        public bool ProjectExportable => true;

        public ISerializableProjectAsset GenerateSerializableProjectAsset(ProjectContext projectContext)
            => throw new NotSupportedException("Export is expected to fail before this point in the test");
    }
}
