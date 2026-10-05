// Cross-app API of QiuUTMTv5 (UndertaleModTool Avalonia for Android).
//
// This file is BOTH the contract the app implements and the file a client app has to copy into
// its own project (for a Gradle app: src/main/aidl/com/undertalemodtool/avalonia/android/api/).
// The .NET Android build turns it into managed proxies (IUmApi / IUmApiStub) through the
// "AndroidInterfaceDescription" build action (see the app's .csproj).
//
// Everything that has a structured result is returned as a JSON string, so the AIDL surface stays
// on plain primitives (a Binder parcel cannot carry arbitrary objects, and AIDL parcelables would
// have to be implemented on both sides).
//
// See Api/README.md for binding instructions and for the shape of every JSON document.

package com.undertalemodtool.avalonia.android.api;

interface IUmApi {
    // -- Discovery ---------------------------------------------------------------------------
    // JSON: {ok, apiVersion, appVersion, packageName, descriptor, permission, permissionLevel,
    //        serviceClass, serviceComponent, bindAction, ready, dataLoaded, dataPath, projectName,
    //        runningJobId, activeJobs, scriptRoots[{path, isBuiltIn, exists}]}
    String getApiInfo();

    // -- Scripts -----------------------------------------------------------------------------
    // JSON: {ok, count, scripts[{name, fileName, relativePath, root, isBuiltIn, fullPath}]}
    String listScripts();

    // The three start* methods return JSON: {ok, jobId, state, error}
    // scriptText: the csx source itself (at most 512 KB - Binder transactions are capped at 1 MB).
    // name:       display name / script path used in diagnostics ("inline.csx" when empty).
    // optionsJson may be null; supported keys: {"assumeYes": bool} - the answer the host gives to
    // ScriptQuestion confirmations (default false).
    String startScriptText(String scriptText, String name, String optionsJson);

    // pathOrUri: a filesystem path, a file:// URI or a content:// URI the app is allowed to read.
    String startScriptFile(String pathOrUri, String optionsJson);

    // relativePath: as returned by listScripts() - resolved inside the built-in script folders
    // and inside the user script folder (/sdcard/QiuUTMTv5/Scripts) only.
    String startBuiltinScript(String relativePath, String optionsJson);

    // -- Jobs --------------------------------------------------------------------------------
    // JSON: {id, kind, name, state, queuedAt, startedAt, finishedAt, outputLength, returnValue,
    //        returnType, consoleText, error, errorTitle, cancelRequested} or {ok:false, error}
    String getJob(String jobId);

    // Incremental log read; returns JSON {id, offset, nextOffset, eof, text}. The caller passes
    // the nextOffset of the previous call to continue reading. Strings are cut on char boundaries.
    String getJobOutput(String jobId, int fromOffset);

    // Best effort: a queued job stops immediately, a running script stops at its next
    // progress/message call (scripts that never report progress run to completion).
    boolean cancelJob(String jobId);

    // Blocks the calling (Binder) thread until the job finishes or the timeout elapses;
    // returns true when the job has finished. Call it from a worker thread, not from a UI thread.
    boolean waitForJob(String jobId, long timeoutMillis);

    // JSON: {ok, count, jobs[...]} - metadata only, no output text.
    String listJobs();
    // Removes all finished jobs from the list, returns how many were removed.
    int clearFinishedJobs();

    // -- Data files --------------------------------------------------------------------------
    // Both calls block until the file is fully read/written (a large data file can take a while).
    // JSON: {ok, path, warnings[], dataPath, error}
    String loadDataFile(String pathOrUri);

    // JSON: {ok, path, error}
    String saveDataFile(String pathOrUri);
}
