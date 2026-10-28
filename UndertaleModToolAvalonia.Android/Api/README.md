# Cross-app API (`IUmApi`)

QiuUTMTv5 (the Avalonia Android build of UndertaleModTool) exposes a small bound service so that
**other apps on the same device can run csx scripts** and load/save data files through the tool's
own scripting engine - including while the tool is not on screen.

| | |
|---|---|
| Permission | `com.undertalemodtool.avalonia.android.permission.RUN_SCRIPT` (`signature\|dangerous` - see [Security](#security)) |
| Bind action | `com.undertalemodtool.avalonia.android.api.action.BIND_UM_API` |
| AIDL package | `com.undertalemodtool.avalonia.android.api` |
| Interface | `IUmApi` (contract file: [`IUmApi.aidl`](../api/umapi/src/main/aidl/com/undertalemodtool/avalonia/android/api/IUmApi.aidl), in the `api` submodule) |
| API version | `1` (reported by `getApiInfo`, bump on incompatible changes) |
| Implementation | [`UmApiService.cs`](UmApiService.cs), [`UmApiBinder.cs`](UmApiBinder.cs), [`UmApiJobs.cs`](UmApiJob.cs), [`UmApiScriptHost.cs`](UmApiScriptHost.cs), [`UmApiDataFile.cs`](UmApiDataFile.cs) |

Everything with a structured result is a **JSON string** - the AIDL surface itself stays on
primitives (`String`/`int`/`long`/`boolean`) so that no parcelable has to be implemented on both
sides. Every method that returns a JSON document returns an object with `ok: true`/`ok: false` plus
`error` on failure (the three methods that return `boolean`/`int` - `cancelJob`, `waitForJob`,
`clearFinishedJobs` - report their result directly). No method throws across Binder: an unhandled
managed exception would reach a Java client as an opaque `RuntimeException`, so every entry point
catches and reports instead.

> **The client side lives in its own repository.** The contract, the generated AIDL classes and a
> ready-made Java client are the SDK [**QiuUTMTv5-API**](https://github.com/Genouka/QiuUTMTv5-API)
> (Apache-2.0), checked into this repository as the **`api` git submodule**: its
> `umapi/src/main/aidl/com/undertalemodtool/avalonia/android/api/IUmApi.aidl` is the canonical
> contract that the service below implements, so the two sides cannot drift apart. Read
> [the SDK README](https://github.com/Genouka/QiuUTMTv5-API#readme) for the client-side setup and
> for the JSON shape of every reply.

## Quick start (client app)

```kotlin
// 1. the SDK, from the public Maven repository published by its CI (no token needed)
repositories { maven { url = uri("https://genouka.github.io/QiuUTMTv5-API/") } }
dependencies { implementation("com.undertalemodtool.avalonia:qiuutmtv5-api:0.9.2.0") }
```

```xml
<!-- 2. AndroidManifest.xml: the permission, plus package visibility (on Android 11+ a missing
     <queries> entry makes the bind fail as if the tool were not installed) -->
<uses-permission android:name="com.undertalemodtool.avalonia.android.permission.RUN_SCRIPT" />
<queries>
    <package android:name="com.undertalemodtool.avalonia.android" />
</queries>
```

```java
// 3. bind and run something
UmApiClient client = new UmApiClient(context);
client.connect(10_000, new UmApiClient.OnConnectedListener() {
    @Override public void onConnected(UmApiClient api) {
        api.runScriptAsync("SetUMTConsoleText(\"hello\");", "demo.csx", false,
            chunk -> Log.i("demo", chunk),
            (job, error) -> Log.i("demo", error != null ? error.getMessage() : job.state));
    }
    @Override public void onConnectionFailed(String message) { Log.w("demo", message); }
});
```

A client that cannot consume an AAR (.NET for Android, for instance) copies
`api/umapi/src/main/aidl/com/undertalemodtool/avalonia/android/api/IUmApi.aidl` into its own project
and binds the raw interface - see
[`api/samples/dotnet-android`](https://github.com/Genouka/QiuUTMTv5-API/tree/main/samples/dotnet-android).

## Method reference

| Method | What it does |
|---|---|
| `getApiInfo()` | tool status: `apiVersion`, `ready`, `dataLoaded`, `serviceComponent`, `scriptRoots`, ... |
| `listScripts()` | every csx file the tool can run (built-in and user folders) |
| `startScriptText(text, name, options)` | queue an inline script (at most 512 KB) |
| `startScriptFile(pathOrUri, options)` | queue a script from a path, `file://` or `content://` URI |
| `startBuiltinScript(relativePath, options)` | queue a script from `listScripts()`, resolved inside the script folders only |
| `getJob(jobId)` | current state, progress, `returnValue`, `consoleText`, `error` |
| `getJobOutput(jobId, fromOffset)` | incremental log read; pass `nextOffset` back in to continue |
| `waitForJob(jobId, timeoutMillis)` | block the caller's thread until the job finished or the timeout elapsed |
| `cancelJob(jobId)` | cooperative cancel: a queued job stops at once, a running script at its next progress call |
| `listJobs()` / `clearFinishedJobs()` | every remembered job (metadata only) / forget the finished ones |
| `loadDataFile(pathOrUri)` / `saveDataFile(pathOrUri)` | synchronous load/save through `UndertaleIO` |

`options` is a JSON string whose only key today is `assumeYes` - the answer the host gives to
`ScriptQuestion` confirmations (default `false`). Every method returns a JSON document carrying
`ok`, and `error` when it failed (except `cancelJob`, `waitForJob` and `clearFinishedJobs`, which
return their value directly); the SDK turns `ok: false` into an `UmApiException`. The exact shapes -
the `state`/`kind` enums, the chunk format of `getJobOutput`, the output caps - are in
[the SDK README](https://github.com/Genouka/QiuUTMTv5-API#api-reference).
## Execution model

* **One job at a time.** The scripting engine, the loaded `UndertaleData` and the view model are
  shared, single-instance state, so every mutating operation (scripts, load, save) is serialized
  through one process-wide queue. Requests from different client apps share that queue.
* **Jobs run on the UI dispatcher** (which pumps even without an activity, because
  `AndroidDispatcherImpl` installs an idle handler on the main looper) while the script body itself
  runs on the dedicated 64 MB-stack thread the scripting engine creates. Progress and output from
  the script never touch the UI.
* **Headless by construction.** An API-driven script gets `UmApiScriptHost`, a `ScriptGlobals`
  subclass that captures everything the desktop tool would have shown: `ScriptMessage`,
  `ScriptWarning`, `ScriptError`, search output and progress go to the job log; `ScriptQuestion`
  answers with `assumeYes`; file/folder pickers and input dialogs return "cancelled" (the script
  sees that in its log line). The real UI is never disabled and no dialog is ever posted.
* Scripts see exactly the same globals as a script started from the tool's own script menu
  (`Data`, `Project`, `Settings`, all the helper methods), and `RunUMTScript` chains run inline
  against the same job.
* The service lives in the app's main process; if Android kills that process, running jobs and the
  loaded data are gone (jobs are not persisted).

## Security

The service is `android:exported="true"` but protected by the app's own permission:

```xml
<permission android:name="com.undertalemodtool.avalonia.android.permission.RUN_SCRIPT"
            android:protectionLevel="signature|dangerous" ... />
<service android:name="crc64xxxxxxxxxxxxxxxx.UmApiService"
         android:exported="true"
         android:permission="com.undertalemodtool.avalonia.android.permission.RUN_SCRIPT" ... />
```

`signature|dangerous` is a **bitmask**: `PROTECTION_SIGNATURE (0x2) | PROTECTION_DANGEROUS (0x1)`,
which Android reads as `PROTECTION_SIGNATURE_OR_SYSTEM (0x3)`. Practical consequences:

* Apps **signed with the same certificate** as this app get the permission at install time (no user
  prompt) - that is the intended way to let a companion app drive the tool.
* Preinstalled/system apps also qualify.
* A normal third-party app **cannot** obtain it: it is not offered as a runtime permission the user
  can grant, and it is not a normal permission granted at install time.

If you want user-grantable access instead, change the protection level in
`Properties/AndroidManifest.xml` to `dangerous` - then any app that declares the permission in its
manifest gets it after the user confirms the prompt (and, on Android 6+, it can be revoked in
settings). Nothing else in the code has to change; the runtime check in `UmApiSecurity` accepts a
caller that either holds the permission or is signed with the same certificate, and rejects
everything else in `Service.onBind` (the manifest already filters `bindService`, the check is the
belt-and-braces fallback for `adb` calls and manifest merges).

Scripts run with the app's own privileges: they can read/write anything the tool can (all-files
access, if granted) and can modify the user's data files. Only hand out the permission to code you
trust.

## Limitations

* Cancellation is cooperative (see `cancelJob`).
* `waitForJob`, `loadDataFile` and `saveDataFile` block the calling thread; use a worker thread.
* Only one mutating operation runs at a time, and a long script delays a client's load/save.
* No file/folder/input dialogs: such scripts get "cancelled" answers and should be driven from the
  tool's own UI instead.
* The API is not exposed to the tool's own `:crashreport` process, and it does not work if the app's
  process cannot be started.
* `getJobOutput` chunks are capped at 64 KB and the log at 8 MB per job.

## 中文速览

QiuUTMTv5（安卓版 UndertaleModTool）暴露一个受权限保护的绑定式 Service：其他应用可以在工具进程里运行 `csx`
脚本、实时读取脚本输出、读写数据文件。

* **客户端文档在 SDK 仓库**：[QiuUTMTv5-API](https://github.com/Genouka/QiuUTMTv5-API)（Apache-2.0），
  以 **`api` 子模块**的形式放在本仓库里。契约
  [`IUmApi.aidl`](https://github.com/Genouka/QiuUTMTv5-API/blob/main/umapi/src/main/aidl/com/undertalemodtool/avalonia/android/api/IUmApi.aidl)
  由它定义，本仓库的服务实现直接编译这份文件，因此两边不会出现不一致。
* **依赖**：`com.undertalemodtool.avalonia:qiuutmtv5-api:<版本>`，公开 Maven 仓库
  `https://genouka.github.io/QiuUTMTv5-API/`（GitHub Packages 同样有产物，但读取需要 token）。
* **接入清单**：声明
  `<uses-permission android:name="com.undertalemodtool.avalonia.android.permission.RUN_SCRIPT" />`
  和 `<queries><package android:name="com.undertalemodtool.avalonia.android" /></queries>`；权限是
  `signature|dangerous`，实际等于 signature-or-system（只有同一证书签名的应用和预装应用能拿到，详见
  [Security](#security)）。
* **调用方式**：绑定 action `com.undertalemodtool.avalonia.android.api.action.BIND_UM_API`；方法返回 JSON
  （`ok` / `error`），脚本与文件操作都是异步任务（jobId 轮询、增量读日志、协作式取消），方法一览见
  [Method reference](#method-reference)。
* **执行模型**：绑定会拉起工具进程，但**不会打开任何界面**，也不会弹出任何对话框——脚本里的消息、警告、提问、进度
  全部被捕获进任务日志；所有请求串行执行，一次一个任务。
* **本仓库这一侧**是服务的实现（[`UmApiService.cs`](UmApiService.cs)、[`UmApiBinder.cs`](UmApiBinder.cs)、
  [`UmApiJob.cs`](UmApiJob.cs)、[`UmApiScriptHost.cs`](UmApiScriptHost.cs)、[`UmApiDataFile.cs`](UmApiDataFile.cs)），
  执行模型、安全模型与已知限制见上面几节。
