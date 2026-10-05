# Cross-app API (`IUmApi`)

QiuUTMTv5 (the Avalonia Android build of UndertaleModTool) exposes a small bound service so that
**other apps on the same device can run csx scripts** and load/save data files through the tool's
own scripting engine - including while the tool is not on screen.

| | |
|---|---|
| Permission | `com.undertalemodtool.avalonia.android.permission.RUN_SCRIPT` (`signature\|dangerous` - see [Security](#security)) |
| Bind action | `com.undertalemodtool.avalonia.android.api.action.BIND_UM_API` |
| AIDL package | `com.undertalemodtool.avalonia.android.api` |
| Interface | `IUmApi` (contract file: [`IUmApi.aidl`](IUmApi.aidl)) |
| API version | `1` (reported by `getApiInfo`, bump on incompatible changes) |
| Implementation | [`UmApiService.cs`](UmApiService.cs), [`UmApiBinder.cs`](UmApiBinder.cs), [`UmApiJobs.cs`](UmApiJob.cs), [`UmApiScriptHost.cs`](UmApiScriptHost.cs), [`UmApiDataFile.cs`](UmApiDataFile.cs) |

Everything with a structured result is a **JSON string** - the AIDL surface itself stays on
primitives (`String`/`int`/`long`/`boolean`) so that no parcelable has to be implemented on both
sides. Every method that returns a JSON document returns an object with `ok: true`/`ok: false` plus
`error` on failure (the three methods that return `boolean`/`int` - `cancelJob`, `waitForJob`,
`clearFinishedJobs` - report their result directly). No method throws across Binder: an unhandled
managed exception would reach a Java client as an opaque `RuntimeException`, so every entry point
catches and reports instead.

## Quick start (client app)

1. Add the permission to your manifest:

   ```xml
   <uses-permission android:name="com.undertalemodtool.avalonia.android.permission.RUN_SCRIPT" />

   <!-- Android 11+ package visibility: without this the tool is invisible to your app and both
        queryIntentServices() and bindService() fail. -->
   <queries>
       <package android:name="com.undertalemodtool.avalonia.android" />
   </queries>
   ```

2. Copy [`IUmApi.aidl`](IUmApi.aidl) into your project as
   `src/main/aidl/com/undertalemodtool/avalonia/android/api/IUmApi.aidl` (Gradle compiles it into
   `IUmApi` + `IUmApi.Stub`). A .NET for Android client does the same by adding the file with the
   `AndroidInterfaceDescription` build action - exactly what this app does.

3. Bind and call - `bindService` with the **action** (the service's Java class name is a generated
   `crc64...` name, so the action is the stable identifier; `getApiInfo().serviceComponent` reports
   the concrete component if you prefer an explicit one):

   ```kotlin
   val intent = Intent(UmApi.ACTION_BIND).setPackage(UmApi.PACKAGE)
   bindService(intent, connection, Context.BIND_AUTO_CREATE)
   ```

A ready-to-run Kotlin wrapper (discovery, script run, output streaming, cancellation) is in
[`samples/android-kotlin/UmApiClient.kt`](samples/android-kotlin/UmApiClient.kt), and the same
client for a .NET for Android app in
[`samples/dotnet-android/UmApiClient.cs`](samples/dotnet-android/UmApiClient.cs).

Binding to the service **starts the tool's process** if it is not running. No activity is started:
the API works headless, and an external call never brings the tool's UI to the foreground.

## Method reference

### `String getApiInfo()`

```json
{ "ok": true, "apiVersion": "1", "appVersion": "0.9.2.0",
  "packageName": "com.undertalemodtool.avalonia.android",
  "descriptor": "com.undertalemodtool.avalonia.android.api.IUmApi",
  "permission": "com.undertalemodtool.avalonia.android.permission.RUN_SCRIPT",
  "permissionLevel": "signature|dangerous",
  "serviceClass": "crc64xxxxxxxxxxxxxxxx.UmApiService",
  "serviceComponent": "com.undertalemodtool.avalonia.android/crc64xxxxxxxxxxxxxxxx.UmApiService",
  "bindAction": "com.undertalemodtool.avalonia.android.api.action.BIND_UM_API",
  "ready": true, "dataLoaded": false, "dataPath": null, "projectName": null,
  "runningJobId": null, "activeJobs": 0,
  "scriptRoots": [ { "path": "/data/user/0/.../files/Scripts", "isBuiltIn": true, "exists": true } ] }
```

`ready: false` means the app is still starting (Avalonia boots in `Application.onCreate`, which can
finish after Android delivered the bind); the call itself waits up to 20 s for that. Every other
method returns `{"ok":false,"error":"The tool is still starting up; ..."}` in that case.

### `String listScripts()`

```json
{ "ok": true, "count": 12,
  "scripts": [ { "name": "Clear_All_Flags", "fileName": "Clear_All_Flags.csx",
                 "relativePath": "Clear_All_Flags.csx", "root": "/data/user/0/.../files/Scripts",
                 "isBuiltIn": true, "fullPath": "/data/user/0/.../files/Scripts/Clear_All_Flags.csx" } ] }
```

Roots are the built-in script folder (extracted from the APK assets into internal storage) and the
user folder `/sdcard/QiuUTMTv5/Scripts`. Nothing else is scanned, and `startBuiltinScript` refuses
paths that escape those folders (`..`, absolute paths).

### `String startScriptText(String scriptText, String name, String optionsJson)`

### `String startScriptFile(String pathOrUri, String optionsJson)`

### `String startBuiltinScript(String relativePath, String optionsJson)`

All three queue a script run and return immediately:

```json
{ "ok": true, "jobId": "9f2c...", "state": "Queued" }
```

* `startScriptText` takes the csx source directly (max **512 KB** - a Binder transaction is capped
  at 1 MB; write bigger scripts to a file and use `startScriptFile`).
* `startScriptFile` accepts a filesystem path, a `file://` URI or a `content://` URI the calling app
  granted to the tool (its own documents, a SAF pick, ...).
* `optionsJson` may be `null`/empty; supported keys:

  | key | default | meaning |
  |---|---|---|
  | `assumeYes` | `false` | answer given to `ScriptQuestion` confirmations. `false` makes a script that asks "are you sure?" stop or skip its work; `true` lets an automation client run confirm-only scripts unattended. |

### `String getJob(String jobId)`

```json
{ "ok": true, "id": "9f2c...", "kind": "script-text", "name": "inline.csx", "source": "<inline>",
  "state": "Succeeded", "queuedAt": 1730000000000, "startedAt": 1730000000050, "finishedAt": 1730000001234,
  "outputLength": 812, "progress": 1.0, "progressMaximum": 1.0,
  "progressMessage": "Saving...", "progressStatus": "Writing data file",
  "cancelRequested": false, "assumeYes": false,
  "returnValue": "42", "returnType": "System.Int32",
  "error": null, "errorTitle": null, "consoleText": "..." }
```

`state` is `Queued` → `Running` → `Succeeded` | `Failed` | `Cancelled`. `Failed` means the script
reported an error (`ScriptError`) or the engine threw (compile error, exception) - the message is in
`error`, with `errorTitle` when the script supplied one. `consoleText` is the last value the script
set with `SetUMTConsoleText` (truncated at 64 KB), and it is also appended to the job's log when the
job finishes.

### `String getJobOutput(String jobId, int fromOffset)`

Streams the captured log (messages, warnings, errors, questions, progress notes) in chunks:

```json
{ "ok": true, "id": "9f2c...", "state": "Running", "offset": 0, "nextOffset": 4096,
  "eof": false, "text": "..." }
```

Pass the previous `nextOffset` as the next `fromOffset` (`0` to start over). Chunks are at most
64 KB and never cut a surrogate pair. `eof` is true once the job has finished and everything has
been read. The whole log is capped at 8 MB per job; beyond that a truncation notice is appended.

### `boolean cancelJob(String jobId)`

Cooperative: a job that has not started yet is cancelled immediately; a running script stops at its
next progress/message call. A script that never talks to the host (no progress, no output) runs to
completion - cancellation is best effort, not preemption.

### `boolean waitForJob(String jobId, long timeoutMillis)`

Blocks the **calling Binder thread** until the job finishes (returns `true`) or the timeout elapses
(returns `false`). `timeoutMillis` is clamped to 0..600000. Call it from a worker thread; calling it
on a UI thread freezes that UI for up to the timeout.

### `String listJobs()` / `int clearFinishedJobs()`

`listJobs` returns `{"ok":true,"count":n,"jobs":[...]}` with the metadata-only form of `getJob`
(no output text / console text). At most 64 jobs are retained; the oldest finished ones are dropped
first. `clearFinishedJobs` removes every finished job and returns how many were removed.

### `String loadDataFile(String pathOrUri)` / `String saveDataFile(String pathOrUri)`

```json
{ "ok": true, "path": "/sdcard/QiuUTMTv5/data.win", "dataPath": "/sdcard/QiuUTMTv5/data.win",
  "importantWarnings": false, "warnings": [] }
```

Both run **synchronously** (a large data file can take seconds) and both block - call them from a
worker thread. They read/write through `UndertaleIO` directly instead of the app's own
open/save flow, so no loader window or message dialog ever appears; the loaded data is still
installed into the app (`loadDataFile` closes the current document first, exactly like using the
tool's own *Open* command, and the running app window updates accordingly).

`saveDataFile` writes the data that is currently loaded - call `loadDataFile` (or have the user open
a file) first. `content://` URIs are supported in both directions; the resolver's `"wt"` mode is
used so an existing document is truncated instead of appended to.

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

* 其他应用通过**绑定 Service**（action `com.undertalemodtool.avalonia.android.api.action.BIND_UM_API`）
  调用 `IUmApi`，需要声明权限 `com.undertalemodtool.avalonia.android.permission.RUN_SCRIPT`
  （`signature|dangerous`，即 sig-or-system：同签名应用直接获得，普通第三方应用无法通过弹窗获取；
  想改成用户可授权，把清单里的 `protectionLevel` 改成 `dangerous` 即可）。
* 把 [`IUmApi.aidl`](IUmApi.aidl) 复制到客户端工程的
  `src/main/aidl/com/undertalemodtool/avalonia/android/api/` 下即可（.NET for Android 客户端用
  `AndroidInterfaceDescription` 构建项）。
* 所有返回值都是 JSON 字符串：先 `startScriptText` / `startScriptFile` / `startBuiltinScript`
  拿到 `jobId`，再 `waitForJob` 等待、`getJob` 查看状态与返回值、`getJobOutput` 增量拉取日志
  （`getJobOutput` 上限 64 KB/次），`cancelJob` 尽力取消。
* 外部调用**不会弹出任何界面**：脚本里的对话框、进度条、报错都被捕获进任务日志，
  `ScriptQuestion` 按 `optionsJson` 里的 `assumeYes`（默认 false）回答；绑定 Service 可以
  在应用没有界面的情况下启动进程并跑脚本。
* `loadDataFile` / `saveDataFile` 是同步阻塞调用（大文件可能几秒），请放在工作线程里；
  它们直接走 `UndertaleIO`，不会打开加载窗口。
* 所有会修改数据的操作（脚本、加载、保存）在同一进程里串行执行，来自不同应用的请求共享队列。
