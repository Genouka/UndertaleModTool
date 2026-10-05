using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using UndertaleModToolAvalonia;

namespace UndertaleModToolAvalonia.Android.Api;

/// <summary>What a job was asked to do (only used for reporting).</summary>
internal enum UmApiJobKind
{
    ScriptText,
    ScriptFile,
    ScriptBuiltin,
}

internal enum UmApiJobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// One request from a client app (currently always a script run) plus everything the client can
/// observe about it: state, timestamps, progress, the captured output log and the result.
/// <para>
/// Written by the script thread (through <see cref="UmApiScriptHost"/>) and by the runner, read by
/// whoever is servicing a Binder transaction - so every piece of mutable state is behind one lock.
/// </para>
/// </summary>
internal sealed class UmApiJob
{
    readonly object gate = new();
    readonly StringBuilder output = new();
    readonly ManualResetEventSlim finished = new(false);

    bool outputTruncated;
    bool finishedOnce;
    UmApiJobState state = UmApiJobState.Queued;
    long startedAt;
    long finishedAt;
    string? returnValue;
    string? returnType;
    string? error;
    string? errorTitle;
    string consoleText = "";
    double progressValue;
    double progressMaximum;
    string? progressMessage;
    string? progressStatus;
    volatile bool cancelRequested;

    public UmApiJob(UmApiJobKind kind, string name, string source, bool assumeYes)
    {
        Id = Guid.NewGuid().ToString("N");
        Kind = kind;
        Name = name;
        Source = source;
        AssumeYes = assumeYes;
        QueuedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public string Id { get; }

    public UmApiJobKind Kind { get; }

    /// <summary>Display name of the request (script name / file name).</summary>
    public string Name { get; }

    /// <summary>Where the script came from: a path, a URI, or "&lt;inline&gt;".</summary>
    public string Source { get; }

    /// <summary>Answer the host gives to <c>ScriptQuestion</c> confirmations.</summary>
    public bool AssumeYes { get; }

    public long QueuedAt { get; }

    public bool IsFinished => finished.IsSet;

    public bool CancelRequested => cancelRequested;

    public bool HasError
    {
        get
        {
            lock (gate)
            {
                return error is not null;
            }
        }
    }

    public string? Error
    {
        get
        {
            lock (gate)
            {
                return error;
            }
        }
    }

    public string? ErrorTitle
    {
        get
        {
            lock (gate)
            {
                return errorTitle;
            }
        }
    }

    public double ProgressValue
    {
        get
        {
            lock (gate)
            {
                return progressValue;
            }
        }
    }

    public double ProgressMaximum
    {
        get
        {
            lock (gate)
            {
                return progressMaximum;
            }
        }
    }

    public UmApiJobState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    /// <summary>Blocks the calling thread; returns true when the job has finished.</summary>
    public bool WaitFor(int timeoutMillis) => finished.Wait(timeoutMillis);

    public void RequestCancel()
    {
        cancelRequested = true;
    }
    public void MarkRunning()
    {
        lock (gate)
        {
            state = UmApiJobState.Running;
            startedAt = Now();
        }
    }

    public void MarkFinished()
    {
        lock (gate)
        {
            if (finishedOnce)
                return;

            finishedOnce = true;

            // A cancellation request wins, then a recorded error, then success.
            state = cancelRequested
                ? UmApiJobState.Cancelled
                : error is not null
                    ? UmApiJobState.Failed
                    : UmApiJobState.Succeeded;

            finishedAt = Now();

            // Surface the last console text in the streamed log as well; scripts that accumulate
            // their report in the console box (the common style) would otherwise only be visible
            // through the truncated consoleText field.
            if (!outputTruncated && consoleText.Length > 0)
            {
                output.Append('\n').Append(consoleText);
            }
        }

        finished.Set();
    }

    public void SetError(string message, string? title)
    {
        lock (gate)
        {
            error = message;
            errorTitle = title;
        }
    }

    public void SetResult(object? value)
    {
        lock (gate)
        {
            if (value is null)
                return;

            returnType = value.GetType().FullName;

            if (value is string s)
            {
                returnValue = s;
                return;
            }

            // Scripts written for the desktop tool often produce structured values; give the
            // client at least a stable textual rendering of them.
            try
            {
                returnValue = Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                returnValue = value.ToString();
            }
        }
    }

    public void SetConsoleText(string text)
    {
        lock (gate)
        {
            consoleText = text ?? "";
        }
    }

    public void SetProgress(double value, double maximum, string? message, string? status)
    {
        lock (gate)
        {
            progressValue = value;
            progressMaximum = maximum;
            if (message is not null)
                progressMessage = message;
            if (status is not null)
                progressStatus = status;
        }
    }

    public void AddProgress(double amount)
    {
        lock (gate)
        {
            progressValue += amount;
        }
    }

    public void SetProgressStatus(string status)
    {
        lock (gate)
        {
            progressStatus = status;
        }
    }

    /// <summary>Appends to the captured log, honouring <see cref="UmApiContract.MaxJobOutputLength"/>.</summary>
    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        lock (gate)
        {
            if (outputTruncated)
                return;

            if (output.Length + text.Length > UmApiContract.MaxJobOutputLength)
            {
                output.Append("\n[output truncated: the job produced more than ")
                      .Append(UmApiContract.MaxJobOutputLength / 1024)
                      .Append(" KB]\n");
                outputTruncated = true;
                return;
            }

            output.Append(text);
        }
    }

    /// <summary>
    /// Reads a chunk of the log starting at <paramref name="fromOffset"/> (a char offset the client
    /// received as <c>nextOffset</c> from an earlier call).
    /// </summary>
    public string ReadOutput(int fromOffset, out int nextOffset, out bool eof)
    {
        lock (gate)
        {
            int start = Math.Clamp(fromOffset, 0, output.Length);
            int count = Math.Min(UmApiContract.MaxOutputChunkLength, output.Length - start);

            // Never cut a surrogate pair in half - the client would see a broken character.
            if (count > 0 && count < output.Length - start &&
                char.IsHighSurrogate(output[start + count - 1]))
            {
                count--;
            }

            nextOffset = start + count;
            eof = nextOffset >= output.Length && IsFinished;

            return count <= 0 ? "" : output.ToString(start, count);
        }
    }

    public JsonObject ToJson(bool includeDetails = true)
    {
        lock (gate)
        {
            JsonObject o = new()
            {
                ["id"] = Id,
                ["kind"] = Kind switch
                {
                    UmApiJobKind.ScriptText => "script-text",
                    UmApiJobKind.ScriptFile => "script-file",
                    UmApiJobKind.ScriptBuiltin => "script-builtin",
                    _ => "unknown",
                },
                ["name"] = Name,
                ["source"] = Source,
                ["state"] = StateName(state),
                ["queuedAt"] = QueuedAt,
                ["startedAt"] = startedAt,
                ["finishedAt"] = finishedAt,
                ["outputLength"] = output.Length,
                ["progress"] = progressValue,
                ["progressMaximum"] = progressMaximum,
                ["progressMessage"] = progressMessage,
                ["progressStatus"] = progressStatus,
                ["cancelRequested"] = cancelRequested,
                ["assumeYes"] = AssumeYes,
            };

            if (includeDetails)
            {
                o["returnValue"] = returnValue;
                o["returnType"] = returnType;
                o["error"] = error;
                o["errorTitle"] = errorTitle;
                o["consoleText"] = Truncate(consoleText, UmApiContract.MaxConsoleTextLength);
            }

            return o;
        }
    }

    public static string StateName(UmApiJobState state) => state switch
    {
        UmApiJobState.Queued => "Queued",
        UmApiJobState.Running => "Running",
        UmApiJobState.Succeeded => "Succeeded",
        UmApiJobState.Failed => "Failed",
        UmApiJobState.Cancelled => "Cancelled",
        _ => "Unknown",
    };

    static string Truncate(string text, int max)
        => text.Length <= max ? text : text.Substring(0, max) + "\n[truncated]";

    static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>
/// The process-wide queue of API requests. Only one runs at a time: a script run and a data file
/// load/save both mutate the single shared <see cref="MainViewModel"/>, so they are serialized
/// through <see cref="Gate"/>. Every client app talks to the same queue.
/// </summary>
internal static class UmApiJobs
{
    /// <summary>Serializes everything that touches the loaded data / scripting engine.</summary>
    public static readonly SemaphoreSlim Gate = new(1, 1);

    const int MaxRetainedJobs = 64;

    static readonly object listLock = new();
    static readonly List<UmApiJob> jobs = [];

    public static UmApiJob? Find(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return null;

        lock (listLock)
        {
            return jobs.Find(j => string.Equals(j.Id, id, StringComparison.Ordinal));
        }
    }

    public static List<UmApiJob> Snapshot()
    {
        lock (listLock)
        {
            return [.. jobs];
        }
    }

    public static int ClearFinished()
    {
        lock (listLock)
        {
            return jobs.RemoveAll(j => j.IsFinished);
        }
    }

    public static string? RunningJobId
    {
        get
        {
            lock (listLock)
            {
                foreach (UmApiJob job in jobs)
                {
                    if (!job.IsFinished && job.State == UmApiJobState.Running)
                        return job.Id;
                }
            }

            return null;
        }
    }

    public static int ActiveCount
    {
        get
        {
            lock (listLock)
            {
                int count = 0;
                foreach (UmApiJob job in jobs)
                {
                    if (!job.IsFinished)
                        count++;
                }
                return count;
            }
        }
    }

    /// <summary>Creates a job and starts it in the background.</summary>
    public static UmApiJob StartScript(MainViewModel vm, UmApiJobKind kind, string name, string source,
                                       string scriptText, string? filePath, bool assumeYes)
    {
        UmApiJob job = new(kind, name, source, assumeYes);

        lock (listLock)
        {
            // Keep the list bounded by dropping the oldest finished jobs first.
            while (jobs.Count >= MaxRetainedJobs)
            {
                int index = jobs.FindIndex(j => j.IsFinished);
                if (index < 0)
                    break; // everything is still queued/running - keep growing

                jobs.RemoveAt(index);
            }

            jobs.Add(job);
        }

        _ = ExecuteAsync(vm, job, scriptText, filePath);
        return job;
    }

    static async Task ExecuteAsync(MainViewModel vm, UmApiJob job, string scriptText, string? filePath)
    {
        await Gate.WaitAsync().ConfigureAwait(false);

        try
        {
            // Cancelled while it was queued: never start it.
            if (job.IsFinished)
                return;

            job.MarkRunning();

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                // Roslyn's scripting engine reports failures of its own (compile errors, thrown
                // script exceptions) through this handler instead of a dialog; the host captures
                // everything a script itself would have shown.
                await vm.Scripting.RunScript(
                    scriptText,
                    filePath,
                    (scripting, path) => new UmApiScriptHost(job, scripting, path),
                    (message, title) =>
                    {
                        job.SetError(message, title);
                        return Task.CompletedTask;
                    });
            });
        }
        catch (Exception e)
        {
            job.SetError(e.ToString(), "API");
        }
        finally
        {
            job.MarkFinished();
            Gate.Release();
        }
    }
}
