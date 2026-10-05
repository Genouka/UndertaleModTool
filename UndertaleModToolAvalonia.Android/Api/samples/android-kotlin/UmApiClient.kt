// Sample client for the QiuUTMTv5 cross-app scripting API (see ../../README.md).
//
// This file is meant to be copied into a Kotlin Android app; it needs
//   * the AIDL contract at src/main/aidl/com/undertalemodtool/avalonia/android/api/IUmApi.aidl,
//   * <uses-permission android:name="com.undertalemodtool.avalonia.android.permission.RUN_SCRIPT" />,
//   * <queries><package android:name="com.undertalemodtool.avalonia.android" /></queries>   (API 30+)
// in the app's manifest, and org.json (part of the Android platform).
//
// Nothing here is Android-version specific beyond the package-visibility <queries> entry.

package com.example.umapi

import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.ServiceConnection
import android.content.pm.PackageManager
import android.os.IBinder
import android.util.Log
import com.undertalemodtool.avalonia.android.api.IUmApi
import org.json.JSONObject
import java.util.concurrent.Executors

/** Constants a client needs without depending on the tool's APK. */
object UmApi {
    const val PACKAGE = "com.undertalemodtool.avalonia.android"
    const val PERMISSION = "$PACKAGE.permission.RUN_SCRIPT"
    const val ACTION_BIND = "$PACKAGE.api.action.BIND_UM_API"

    /** Same limit the service enforces for startScriptText (Binder transactions are capped at 1 MB). */
    const val MAX_INLINE_SCRIPT_CHARS = 512 * 1024
}

/**
 * Small wrapper around the bound service.
 *
 * Callbacks are invoked on a background thread; hop to the main thread before touching views.
 *
 * ```kotlin
 * val client = UmApiClient(context)
 * client.connect { error ->
 *     if (error != null) { show(error); return@connect }
 *     client.runScript("ScriptMessage(\"hello from the other app\");\n5 + 1") { job, error ->
 *         Log.i("demo", "done: $job / $error")
 *         client.disconnect()
 *     }
 * }
 * ```
 */
class UmApiClient(private val context: Context) {

    private val executor = Executors.newSingleThreadExecutor()
    private var api: IUmApi? = null
    private var connection: ServiceConnection? = null

    /** Is the tool installed and does it let us bind? (Also true when we may not have access yet.) */
    fun isToolInstalled(): Boolean = try {
        context.packageManager.getPackageInfo(UmApi.PACKAGE, 0); true
    } catch (e: PackageManager.NameNotFoundException) {
        false
    }

    /** Do we hold the permission the service requires? */
    fun hasPermission(): Boolean =
        context.checkSelfPermission(UmApi.PERMISSION) == PackageManager.PERMISSION_GRANTED

    /**
     * Binds to the API service. [onConnected] receives `null` on success, or a message describing
     * why the API is not usable (not installed / no permission / no such service).
     */
    fun connect(onConnected: (String?) -> Unit) {
        if (api != null) { onConnected(null); return }

        if (!isToolInstalled()) {
            onConnected("The tool is not installed."); return
        }
        if (!hasPermission()) {
            onConnected("The app does not hold ${UmApi.PERMISSION} (needs the same signing key, " +
                        "or protectionLevel=dangerous on the tool's side)."); return
        }

        val intent = Intent(UmApi.ACTION_BIND).setPackage(UmApi.PACKAGE)
        val resolved = context.packageManager.queryIntentServices(intent, 0)
        if (resolved.isNullOrEmpty()) {
            // Either the tool is too old to have the API, or API 30+ package visibility hides it
            // (add <queries><package android:name="..."/></queries> to *this* app's manifest).
            onConnected("No API service found - is the tool up to date, and is the <queries> entry present?")
            return
        }

        val conn = object : ServiceConnection {
            override fun onServiceConnected(name: ComponentName?, service: IBinder?) {
                val binder = service ?: run {
                    onConnected("The tool refused the bind (permission check failed)."); return
                }
                api = IUmApi.Stub.asInterface(binder)
                onConnected(null)
            }

            override fun onServiceDisconnected(name: ComponentName?) {
                api = null
            }
        }

        connection = conn
        if (!context.bindService(intent, conn, Context.BIND_AUTO_CREATE)) {
            connection = null
            onConnected("bindService failed.")
        }
    }

    fun disconnect() {
        connection?.let { runCatching { context.unbindService(it) } }
        connection = null
        api = null
        executor.shutdown()
    }

    // -- scripts -----------------------------------------------------------------------------

    /** Scripts known to the tool: the built-in ones plus everything in /sdcard/QiuUTMTv5/Scripts. */
    fun listScripts(onResult: (JSONObject?, String?) -> Unit) = call(onResult) { it.listScripts() }

    fun apiInfo(onResult: (JSONObject?, String?) -> Unit) = call(onResult) { it.getApiInfo() }

    /** Runs csx source. [assumeYes] answers `ScriptQuestion` prompts inside the script. */
    fun runScript(script: String, name: String = "inline.csx", assumeYes: Boolean = false,
                  onOutput: ((String) -> Unit)? = null,
                  onFinished: (JSONObject?, String?) -> Unit) {
        val target = api ?: run { onFinished(null, "Not connected."); return }
        executor.execute {
            val options = JSONObject().put("assumeYes", assumeYes).toString()
            val started = parse(target.startScriptText(script, name, options))
            val jobId = started?.optString("jobId").orEmpty()
            if (started == null || jobId.isEmpty()) {
                onFinished(null, started?.optString("error") ?: "startScriptText failed.")
                return@execute
            }
            awaitJob(target, jobId, onOutput, onFinished)
        }
    }

    /** Runs a script from the tool's script folders (see [listScripts] for the relative paths). */
    fun runBuiltinScript(relativePath: String, assumeYes: Boolean = false,
                         onOutput: ((String) -> Unit)? = null,
                         onFinished: (JSONObject?, String?) -> Unit) {
        val target = api ?: run { onFinished(null, "Not connected."); return }
        executor.execute {
            val options = JSONObject().put("assumeYes", assumeYes).toString()
            val started = parse(target.startBuiltinScript(relativePath, options))
            val jobId = started?.optString("jobId").orEmpty()
            if (started == null || jobId.isEmpty()) {
                onFinished(null, started?.optString("error") ?: "startBuiltinScript failed.")
                return@execute
            }
            awaitJob(target, jobId, onOutput, onFinished)
        }
    }

    /**
     * Runs a script from a path or a content:// URI this app granted to the tool. URI grants can be
     * passed with Intent.FLAG_GRANT_READ_URI_PERMISSION when the tool is started, otherwise share
     * the file path.
     */
    fun runScriptFile(pathOrUri: String, assumeYes: Boolean = false,
                      onOutput: ((String) -> Unit)? = null,
                      onFinished: (JSONObject?, String?) -> Unit) {
        val target = api ?: run { onFinished(null, "Not connected."); return }
        executor.execute {
            val options = JSONObject().put("assumeYes", assumeYes).toString()
            val started = parse(target.startScriptFile(pathOrUri, options))
            val jobId = started?.optString("jobId").orEmpty()
            if (started == null || jobId.isEmpty()) {
                onFinished(null, started?.optString("error") ?: "startScriptFile failed.")
                return@execute
            }
            awaitJob(target, jobId, onOutput, onFinished)
        }
    }

    fun cancelJob(jobId: String): Boolean = runCatching { api?.cancelJob(jobId) == true }.getOrDefault(false)

    // -- data files --------------------------------------------------------------------------

    /** Blocking, potentially slow call - always run it off the main thread. */
    fun loadDataFile(pathOrUri: String, onFinished: (JSONObject?, String?) -> Unit) =
        call(onFinished) { it.loadDataFile(pathOrUri) }

    fun saveDataFile(pathOrUri: String, onFinished: (JSONObject?, String?) -> Unit) =
        call(onFinished) { it.saveDataFile(pathOrUri) }

    // -- internals ---------------------------------------------------------------------------

    /** Waits for a job, streaming its log, and reports the final job document. */
    private fun awaitJob(target: IUmApi, jobId: String, onOutput: ((String) -> Unit)?,
                         onFinished: (JSONObject?, String?) -> Unit) {
        var offset = 0
        while (true) {
            // waitForJob blocks this worker thread for at most a second per call, so the log stays
            // live; a real client can simply poll instead.
            val finished = runCatching { target.waitForJob(jobId, 1000) }.getOrDefault(false)

            if (onOutput != null) {
                val chunk = parse(target.getJobOutput(jobId, offset))
                val text = chunk?.optString("text").orEmpty()
                if (text.isNotEmpty()) {
                    offset = chunk?.optInt("nextOffset", offset) ?: offset
                    onOutput(text)
                }
            }

            if (finished) {
                val job = parse(target.getJob(jobId))
                if (job == null || !job.optBoolean("ok", false)) {
                    onFinished(null, job?.optString("error") ?: "getJob failed.")
                } else {
                    onFinished(job, if (job.optString("state") == "Succeeded") null
                                    else job.optString("error").ifEmpty { job.optString("state") })
                }
                return
            }
        }
    }

    private fun call(onResult: (JSONObject?, String?) -> Unit, body: (IUmApi) -> String) {
        val target = api ?: run { onResult(null, "Not connected."); return }
        executor.execute {
            val json = parse(runCatching { body(target) }.getOrElse { e ->
                onResult(null, e.message ?: e.toString()); return@execute
            })
            if (json == null) onResult(null, "The tool returned an unreadable response.")
            else if (!json.optBoolean("ok", false)) onResult(null, json.optString("error"))
            else onResult(json, null)
        }
    }

    private fun parse(json: String?): JSONObject? = try {
        if (json.isNullOrEmpty()) null else JSONObject(json)
    } catch (e: Exception) {
        Log.w("UmApiClient", "Bad JSON from the tool: $json", e); null
    }
}
