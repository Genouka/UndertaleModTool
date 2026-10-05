// Sample .NET for Android client for the QiuUTMTv5 cross-app scripting API (see ../../README.md).
//
// Setup in the client project:
//   1. copy ../../IUmApi.aidl into the project (e.g. Api/IUmApi.aidl);
//   2. in the .csproj:  <ItemGroup><AndroidInterfaceDescription Include="Api\IUmApi.aidl" /></ItemGroup>
//      (the same mechanism the tool itself uses - it generates IUmApi + IUmApiStub in
//      namespace Com.Undertalemodtool.Avalonia.Android.Api);
//   3. in AndroidManifest.xml:
//        <uses-permission android:name="com.undertalemodtool.avalonia.android.permission.RUN_SCRIPT" />
//        <queries><package android:name="com.undertalemodtool.avalonia.android" /></queries>
//
// The permission only exists for apps signed with the tool's key (or preinstalled ones) - see the
// security section of the README.

using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.OS;
using Com.Undertalemodtool.Avalonia.Android.Api;

namespace UmApiSample;

/// <summary>Binds to QiuUTMTv5 and drives its scripting engine.</summary>
public sealed class UmApiClient : Java.Lang.Object, IServiceConnection
{
    public const string ToolPackage = "com.undertalemodtool.avalonia.android";
    public const string BindAction = ToolPackage + ".api.action.BIND_UM_API";

    readonly Context context;
    readonly TaskCompletionSource<IUmApi> connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource<string> failed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    IUmApi? api;

    public UmApiClient(Context context) => this.context = context.ApplicationContext!;

    /// <summary>Binds and waits for the service; throws when the tool refuses or is missing.</summary>
    public async Task<IUmApi> ConnectAsync(TimeSpan timeout)
    {
        Intent intent = new Intent(BindAction).SetPackage(ToolPackage)!;
        if (!context.BindService(intent, this, Bind.AutoCreate))
            throw new InvalidOperationException("bindService failed (is the tool installed and visible to this app?).");

        Task done = await Task.WhenAny(connected.Task, failed.Task, Task.Delay(timeout));
        if (done == failed.Task)
            throw new InvalidOperationException(await failed.Task);

        if (done != connected.Task)
            throw new TimeoutException("The tool did not answer the bind in time.");

        return await connected.Task;
    }

    public void Disconnect()
    {
        try
        {
            context.UnbindService(this);
        }
        catch (Exception)
        {
            // Already unbound.
        }

        api = null;
    }

    // Public (implicit) implementations of the Java callback interface - the ACW generator binds
    // these to the Java peer's onServiceConnected/onServiceDisconnected.
    public void OnServiceConnected(ComponentName? name, IBinder? service)
    {
        // A null binder means the service's own permission check rejected us.
        if (service is null)
            failed.TrySetResult("The tool rejected the bind (missing " + ToolPackage + ".permission.RUN_SCRIPT).");
        else
            connected.TrySetResult(IUmApiStub.AsInterface(service));
    }

    public void OnServiceDisconnected(ComponentName? name)
    {
        api = null;
        failed.TrySetResult("The tool's process went away.");
    }

    /// <summary>Runs csx source and returns the finished job document.</summary>
    public async Task<JsonNode> RunScriptAsync(string script, string name = "client.csx",
                                               bool assumeYes = false, Action<string>? onOutput = null,
                                               CancellationToken cancellationToken = default)
    {
        IUmApi target = api ?? throw new InvalidOperationException("Not connected.");

        JsonNode started = JsonNode.Parse(target.StartScriptText(
            script, name, new JsonObject { ["assumeYes"] = assumeYes }.ToJsonString()))!;

        if (started["ok"]?.GetValue<bool>() != true)
            throw new InvalidOperationException(started["error"]?.GetValue<string>() ?? "startScriptText failed.");

        string jobId = started["jobId"]!.GetValue<string>();
        int offset = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Blocks this worker thread for at most one second per call, so the log stays live.
            bool finished = target.WaitForJob(jobId, 1000);

            if (onOutput is not null)
            {
                JsonNode chunk = JsonNode.Parse(target.GetJobOutput(jobId, offset))!;
                string text = chunk["text"]?.GetValue<string>() ?? "";
                if (text.Length > 0)
                {
                    offset = chunk["nextOffset"]!.GetValue<int>();
                    onOutput(text);
                }
            }

            if (finished)
                return JsonNode.Parse(target.GetJob(jobId))!;
        }
    }

    /// <summary>Loads a data.win into the tool (slow - the tool blocks until the file is read).</summary>
    public Task<string> LoadDataFileAsync(string pathOrUri)
        => Task.Run(() => (api ?? throw new InvalidOperationException("Not connected.")).LoadDataFile(pathOrUri));

    public Task<string> SaveDataFileAsync(string pathOrUri)
        => Task.Run(() => (api ?? throw new InvalidOperationException("Not connected.")).SaveDataFile(pathOrUri));
}
