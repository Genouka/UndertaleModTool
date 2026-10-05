using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Android.Content;
using Com.Undertalemodtool.Avalonia.Android.Api;
using UndertaleModLib.Util;
using UndertaleModToolAvalonia;

namespace UndertaleModToolAvalonia.Android.Api;

/// <summary>
/// The API itself: implements the AIDL contract generated from <c>Api/IUmApi.aidl</c>.
/// <para>
/// Every method here runs on a Binder thread. Blocking is fine (<c>waitForJob</c> and the data file
/// calls do it on purpose), but nothing may touch the UI or the view model directly - that is what
/// <see cref="UmApiApp.GetViewModelAsync"/> and the dispatcher hops inside
/// <see cref="UmApiJobs"/>/<see cref="UmApiDataFile"/> are for.
/// </para>
/// <para>
/// Methods never throw at the client: every result is a JSON document that carries either the
/// payload or an <c>error</c> string (an unhandled exception over Binder would surface as an
/// opaque <c>RuntimeException</c> in the calling app).
/// </para>
/// </summary>
internal sealed class UmApiBinder : IUmApiStub
{
    const string LogTag = "UmApi";

    readonly Context context;

    public UmApiBinder(Context context)
    {
        this.context = context;
    }

    // -- discovery ---------------------------------------------------------------------------

    public override string GetApiInfo()
    {
        try
        {
            MainViewModel? vm = UmApiApp.GetViewModelAsync().GetAwaiter().GetResult();

            JsonObject info = new()
            {
                ["ok"] = true,
                ["apiVersion"] = UmApiContract.ApiVersion,
                ["appVersion"] = App.VersionString,
                ["packageName"] = UmApiContract.PackageName,
                ["descriptor"] = UmApiContract.Descriptor,
                ["permission"] = UmApiContract.Permission,
                ["permissionLevel"] = "signature|dangerous",
                ["serviceClass"] = ServiceJavaName(),
                ["serviceComponent"] = UmApiContract.PackageName + "/" + ServiceJavaName(),
                ["bindAction"] = UmApiContract.BindAction,
                ["ready"] = vm is not null,
                ["dataLoaded"] = vm?.Data is not null,
                ["dataPath"] = vm?.DataPath,
                ["projectName"] = vm?.Project?.Name,
                ["runningJobId"] = UmApiJobs.RunningJobId,
                ["activeJobs"] = UmApiJobs.ActiveCount,
            };

            JsonArray roots = [];
            foreach ((string root, bool isBuiltIn) in ScriptRoots())
                roots.Add(new JsonObject { ["path"] = root, ["isBuiltIn"] = isBuiltIn, ["exists"] = Directory.Exists(root) });
            info["scriptRoots"] = roots;

            return UmApiJson.Write(info);
        }
        catch (Exception e)
        {
            return Fail(e);
        }
    }

    public override string ListScripts()
    {
        try
        {
            JsonArray scripts = [];
            foreach ((string root, bool isBuiltIn) in ScriptRoots())
            {
                if (!Directory.Exists(root))
                    continue;

                foreach (string file in EnumerateScripts(root))
                {
                    scripts.Add(new JsonObject
                    {
                        ["name"] = Path.GetFileNameWithoutExtension(file),
                        ["fileName"] = Path.GetFileName(file),
                        ["relativePath"] = Path.GetRelativePath(root, file).Replace('\\', '/'),
                        ["root"] = root,
                        ["isBuiltIn"] = isBuiltIn,
                        ["fullPath"] = file,
                    });
                }
            }

            JsonObject result = UmApiJson.Ok();
            result["count"] = scripts.Count;
            result["scripts"] = scripts;
            return UmApiJson.Write(result);
        }
        catch (Exception e)
        {
            return Fail(e);
        }
    }

    // -- scripts -----------------------------------------------------------------------------

    public override string StartScriptText(string scriptText, string name, string optionsJson)
        => Block(async () =>
        {
            if (string.IsNullOrWhiteSpace(scriptText))
                return ErrorResult("The script text is empty.");

            if (scriptText.Length > UmApiContract.MaxScriptTextLength)
            {
                return ErrorResult($"The script text is too large ({scriptText.Length} chars); " +
                                   $"the limit is {UmApiContract.MaxScriptTextLength} chars. " +
                                   "Write the script to a file and use startScriptFile instead.");
            }

            MainViewModel? vm = await UmApiApp.GetViewModelAsync().ConfigureAwait(false);
            if (vm is null)
                return AppNotReady();

            string displayName = string.IsNullOrWhiteSpace(name) ? "inline.csx" : name;
            UmApiJob job = UmApiJobs.StartScript(
                vm, UmApiJobKind.ScriptText, displayName, "<inline>", scriptText, null,
                UmApiJson.ReadBool(optionsJson, "assumeYes", false));

            return JobStarted(job);
        });

    public override string StartScriptFile(string pathOrUri, string optionsJson)
        => Block(async () =>
        {
            if (string.IsNullOrWhiteSpace(pathOrUri))
                return ErrorResult("A path or URI is required.");

            MainViewModel? vm = await UmApiApp.GetViewModelAsync().ConfigureAwait(false);
            if (vm is null)
                return AppNotReady();

            string source = Path.GetFileName(pathOrUri);
            if (string.IsNullOrEmpty(source))
                source = pathOrUri;

            string text = await Task.Run(() => ReadScript(pathOrUri)).ConfigureAwait(false);
            string scriptText = WithLineDirective(text, pathOrUri);
            string? roslynPath = UmApiDataFile.IsContentUri(pathOrUri) ? null : pathOrUri;

            UmApiJob job = UmApiJobs.StartScript(
                vm, UmApiJobKind.ScriptFile, source, pathOrUri, scriptText, roslynPath,
                UmApiJson.ReadBool(optionsJson, "assumeYes", false));

            return JobStarted(job);
        });

    public override string StartBuiltinScript(string relativePath, string optionsJson)
        => Block(async () =>
        {
            string? path = ResolveScriptPath(relativePath);
            if (path is null)
            {
                return ErrorResult($"No script matches \"{relativePath}\" inside the script folders. " +
                                   "Call listScripts to see the available relativePath values.");
            }

            MainViewModel? vm = await UmApiApp.GetViewModelAsync().ConfigureAwait(false);
            if (vm is null)
                return AppNotReady();

            string text = await Task.Run(() => File.ReadAllText(path)).ConfigureAwait(false);

            UmApiJob job = UmApiJobs.StartScript(
                vm, UmApiJobKind.ScriptBuiltin, Path.GetFileName(path), path,
                WithLineDirective(text, path), path,
                UmApiJson.ReadBool(optionsJson, "assumeYes", false));

            return JobStarted(job);
        });

    // -- jobs --------------------------------------------------------------------------------

    public override string GetJob(string jobId)
    {
        try
        {
            UmApiJob? job = UmApiJobs.Find(jobId);
            if (job is null)
                return ErrorResult("No job with id " + jobId + ".");

            JsonObject result = job.ToJson();
            result["ok"] = true;
            return UmApiJson.Write(result);
        }
        catch (Exception e)
        {
            return Fail(e);
        }
    }

    public override string GetJobOutput(string jobId, int fromOffset)
    {
        try
        {
            UmApiJob? job = UmApiJobs.Find(jobId);
            if (job is null)
                return ErrorResult("No job with id " + jobId + ".");

            string text = job.ReadOutput(fromOffset, out int nextOffset, out bool eof);

            JsonObject result = UmApiJson.Ok();
            result["id"] = job.Id;
            result["state"] = UmApiJob.StateName(job.State);
            result["offset"] = Math.Max(0, fromOffset);
            result["nextOffset"] = nextOffset;
            result["eof"] = eof;
            result["text"] = text;
            return UmApiJson.Write(result);
        }
        catch (Exception e)
        {
            return Fail(e);
        }
    }

    public override bool CancelJob(string jobId)
    {
        try
        {
            UmApiJob? job = UmApiJobs.Find(jobId);
            if (job is null || job.IsFinished)
                return false;

            job.RequestCancel();

            // A job that has not started yet never will: finish it now so waitForJob returns.
            if (job.State == UmApiJobState.Queued)
                job.MarkFinished();

            return true;
        }
        catch (Exception e)
        {
            global::Android.Util.Log.Warn(LogTag, "cancelJob failed: " + e.Message);
            return false;
        }
    }

    public override bool WaitForJob(string jobId, long timeoutMillis)
    {
        try
        {
            UmApiJob? job = UmApiJobs.Find(jobId);
            if (job is null)
                return false;

            if (job.IsFinished)
                return true;

            return job.WaitFor((int)Math.Clamp(timeoutMillis, 0, 600_000));
        }
        catch (Exception e)
        {
            global::Android.Util.Log.Warn(LogTag, "waitForJob failed: " + e.Message);
            return false;
        }
    }

    public override string ListJobs()
    {
        try
        {
            JsonArray jobs = [];
            foreach (UmApiJob job in UmApiJobs.Snapshot())
            {
                JsonObject entry = job.ToJson(includeDetails: false);
                entry["ok"] = true;
                jobs.Add(entry);
            }

            JsonObject result = UmApiJson.Ok();
            result["count"] = jobs.Count;
            result["jobs"] = jobs;
            return UmApiJson.Write(result);
        }
        catch (Exception e)
        {
            return Fail(e);
        }
    }

    public override int ClearFinishedJobs()
    {
        try
        {
            return UmApiJobs.ClearFinished();
        }
        catch (Exception e)
        {
            global::Android.Util.Log.Warn(LogTag, "clearFinishedJobs failed: " + e.Message);
            return 0;
        }
    }

    // -- data files --------------------------------------------------------------------------

    public override string LoadDataFile(string pathOrUri)
        => Block(async () =>
        {
            if (string.IsNullOrWhiteSpace(pathOrUri))
                return ErrorResult("A path or URI is required.");

            MainViewModel? vm = await UmApiApp.GetViewModelAsync().ConfigureAwait(false);
            if (vm is null)
                return AppNotReady();

            return await UmApiDataFile.LoadAsync(context, vm, pathOrUri).ConfigureAwait(false);
        });

    public override string SaveDataFile(string pathOrUri)
        => Block(async () =>
        {
            if (string.IsNullOrWhiteSpace(pathOrUri))
                return ErrorResult("A path or URI is required.");

            MainViewModel? vm = await UmApiApp.GetViewModelAsync().ConfigureAwait(false);
            if (vm is null)
                return AppNotReady();

            return await UmApiDataFile.SaveAsync(context, vm, pathOrUri).ConfigureAwait(false);
        });

    // -- helpers -----------------------------------------------------------------------------

    /// <summary>Runs asynchronous work on the Binder thread and turns any failure into JSON.</summary>
    static string Block(Func<Task<string>> body)
    {
        try
        {
            return body().GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            return Fail(e);
        }
    }

    static string Fail(Exception e)
    {
        global::Android.Util.Log.Warn(LogTag, "API call failed: " + e);
        return ErrorResult(e.Message);
    }

    static string ErrorResult(string message) => UmApiJson.Write(UmApiJson.Failure(message));

    static string AppNotReady()
        => ErrorResult("The tool is still starting up; try again in a moment.");

    static string JobStarted(UmApiJob job)
    {
        JsonObject result = UmApiJson.Ok();
        result["jobId"] = job.Id;
        result["state"] = UmApiJob.StateName(job.State);
        return UmApiJson.Write(result);
    }

    /// <summary>The folders <c>listScripts</c> and <c>startBuiltinScript</c> work with.</summary>
    static IEnumerable<(string Root, bool IsBuiltIn)> ScriptRoots()
    {
        string builtIn = BuiltInScripts.GetRootDirectory();
        if (!string.IsNullOrEmpty(builtIn))
            yield return (builtIn, true);

        foreach (string extra in BuiltInScripts.ExtraRootDirectories)
        {
            if (!string.IsNullOrEmpty(extra))
                yield return (extra, false);
        }
    }

    static IEnumerable<string> EnumerateScripts(string root)
    {
        List<string> files = [];

        try
        {
            files.AddRange(Directory.EnumerateFiles(root, "*.csx", SearchOption.AllDirectories));
        }
        catch (Exception)
        {
            // An unreadable folder (permissions, race with a deletion) is skipped rather than
            // failing the whole listing.
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    /// <summary>
    /// Resolves a built-in/user script reference. Uses the same containment check as the app's own
    /// script menu, so ".." cannot escape the script folders.
    /// </summary>
    static string? ResolveScriptPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        string normalized = relativePath.Replace('\\', '/').TrimStart('/');

        foreach ((string root, _) in ScriptRoots())
        {
            string? candidate = Paths.TryJoinVerifyWithinDirectory(root, normalized);
            if (candidate is not null && File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    string ReadScript(string pathOrUri)
    {
        if (UmApiDataFile.IsContentUri(pathOrUri) || pathOrUri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            using Stream stream = UmApiDataFile.OpenReadStream(context, pathOrUri);
            using StreamReader reader = new(stream);
            return reader.ReadToEnd();
        }

        return File.ReadAllText(pathOrUri);
    }

    static string WithLineDirective(string text, string path)
        => $"#line 1 \"{path.Replace('"', '\'')}\"\n" + text;

    /// <summary>
    /// The generated Java class name of the API service (the .NET Android build gives managed types
    /// a <c>crc64...</c> name), so a client that would rather use an explicit component than the
    /// bind action can learn it at runtime.
    /// </summary>
    static string ServiceJavaName()
    {
        try
        {
            return Java.Lang.Class.FromType(typeof(UmApiService)).Name ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }
}
