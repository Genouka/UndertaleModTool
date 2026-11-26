using System;
using System.Collections.Generic;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Bridge for data files the operating system hands to the app: on Android these are "Open with"
/// (<c>ACTION_VIEW</c>) and share-sheet (<c>ACTION_SEND</c>) intents.
/// <para>
/// The platform resolves the incoming URI on a worker thread (which may take a while for large
/// files, because an unresolvable <c>content://</c> URI is copied into the app cache) and calls
/// <see cref="Post"/> with the resulting local path. Paths that arrive before the UI subscribed
/// (cold start) wait in a queue and are delivered as soon as the main view model attaches.
/// </para>
/// <para>
/// The bridge itself is deliberately free of Avalonia and Android types: it is a plain queue plus
/// callbacks, so the shared UI can consume it and the platform code can fill it.
/// </para>
/// </summary>
public static class PlatformIncomingFile
{
    static readonly object Gate = new();
    static readonly Queue<string> pendingPaths = new();
    static readonly Queue<string> pendingErrors = new();
    static Action<string>? fileHandler;
    static Action<string>? errorHandler;

    /// <summary>
    /// Subscribes the single UI consumer. Any path/error that was already posted is delivered
    /// synchronously from here (before this method returns), the rest when they arrive later.
    /// </summary>
    public static void Attach(Action<string> onFile, Action<string> onError)
    {
        ArgumentNullException.ThrowIfNull(onFile);
        ArgumentNullException.ThrowIfNull(onError);

        string[] files;
        string[] errors;
        lock (Gate)
        {
            fileHandler = onFile;
            errorHandler = onError;
            files = pendingPaths.ToArray();
            errors = pendingErrors.ToArray();
            pendingPaths.Clear();
            pendingErrors.Clear();
        }

        foreach (string error in errors)
            onError(error);
        foreach (string path in files)
            onFile(path);
    }

    /// <summary>Drops the subscription; later posts queue up for the next <see cref="Attach"/>.</summary>
    public static void Detach()
    {
        lock (Gate)
        {
            fileHandler = null;
            errorHandler = null;
        }
    }

    /// <summary>
    /// Publishes a readable local path. Safe to call from any thread, including before the UI
    /// attaches; never throws (a delivery handler that throws must not kill the copy task).
    /// </summary>
    public static void Post(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        Action<string>? handler;
        lock (Gate)
        {
            if (fileHandler is null)
            {
                pendingPaths.Enqueue(path);
                return;
            }
            handler = fileHandler;
        }

        try
        {
            handler(path);
        }
        catch (Exception)
        {
            // The consumer reports its own failures; a throwing subscriber must not take down the
            // background intent resolution that called us.
        }
    }

    /// <summary>
    /// Publishes a failure (the incoming URI could not be resolved into a readable file), as a
    /// user-presentable message. Queued like <see cref="Post"/> when the UI is not attached yet.
    /// </summary>
    public static void PostError(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        Action<string>? handler;
        lock (Gate)
        {
            if (errorHandler is null)
            {
                pendingErrors.Enqueue(message);
                return;
            }
            handler = errorHandler;
        }

        try
        {
            handler(message);
        }
        catch (Exception)
        {
            // See Post.
        }
    }
}
