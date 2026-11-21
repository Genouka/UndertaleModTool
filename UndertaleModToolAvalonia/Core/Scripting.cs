using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QiuForms;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Underanalyzer.Decompiler;
using UndertaleModLib;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;
using UndertaleModLib.Project;
using UndertaleModLib.Scripting;
using UndertaleModLib.Util;
using UndertaleModTool.Localization;

namespace UndertaleModToolAvalonia;

public class Scripting
{
    public readonly MainViewModel MainVM;

    /// <summary>
    /// Optional directory containing plain DLL copies of the assemblies the script engine should
    /// reference. Android sets this to the internal-storage folder the packaged assets were
    /// extracted into; on Android the assemblies normally live inside the APK and cannot be
    /// resolved from the filesystem, which made script compilation fail with "cannot find
    /// assembly".
    /// </summary>
    public static string? ScriptAssembliesDirectory { get; set; }

    /// <summary>
    /// Optional platform hook invoked right before a script is compiled. The Android head uses it
    /// to extract the packaged script assemblies from the app assets into
    /// <see cref="ScriptAssembliesDirectory"/> (internal storage) on first use.
    /// </summary>
    public static Action? PrepareScriptAssemblies { get; set; }

    /// <summary>
    /// Optional override for the directory scripts see as <c>ExePath</c>. Platforms where the real
    /// executable location is not usable set it to an app-private directory: scripts create their
    /// scratch ("Packager") folders under <c>ExePath</c> and write results next to it, while on
    /// Android <see cref="Environment.ProcessPath"/> resolves to the read-only /system/bin.
    /// </summary>
    public static string? ExePathOverride { get; set; }

    /// <summary>
    /// Name prefixes of the assemblies that scripts may bind against. This is the single source
    /// of truth for the script reference surface - the Android packager ships exactly the
    /// assemblies matching these prefixes (see BundleScriptAssembliesToAssets in the Android
    /// csproj, which keeps its own prefix list in sync) and the extractor copies them next to the
    /// app. Everything else in the Avalonia/Skia/SDL/interop stack is neither packaged nor handed
    /// to the compiler: feeding all of it in balloons the compiler's metadata graph - a real
    /// problem on memory-constrained devices, and it deepens the binder's recursion.
    /// </summary>
    static readonly string[] ScriptReferencePrefixes =
    [
        // Base class libraries and dynamic-language support.
        "System",
        "mscorlib",
        "netstandard",
        "Microsoft.CSharp",
        // The mod tool itself: engine, decompiler, localization, UI globals (IScriptInterface).
        "UndertaleModLib",
        "Underanalyzer",
        "UndertaleModToolAvalonia",
        "UndertaleModToolLocalization",
        // The toolkit-neutral form model scripts build against (see QiuForms/README.md).
        "QiuForms",
        // Texture import/export (ImageMagick wrapper) and zip support. The Magick.NET assemblies
        // have to be listed in full. A prefix only matches at a "." boundary, so the bare
        // "Magick.NET" entry this list used to carry matched the abstractions (Magick.NET.Core)
        // but not the implementation: the flavor assembly is named with a hyphen
        // (Magick.NET-Q8-AnyCPU), and that is where the MagickImage class the scripts ask for
        // lives - hence the "MagickImage could not be found" compile error they used to hit.
        // Only the AnyCPU flavor may be listed: the per-architecture packages
        // (Magick.NET-Q8-arm64/x64) contain the same 97 ImageMagick types, so referencing them as
        // well is an ambiguous-type error. AnyCPU is also the identity the tool's own assemblies
        // bind to (UndertaleModLib and UndertaleModToolAvalonia reference "Magick.NET-Q8-AnyCPU"),
        // which keeps the script and the app on the same types at run time. ("ImageMagick" is a
        // namespace rather than an assembly name, so that entry was dropped.)
        "Magick.NET.Core",
        "Magick.NET-Q8-AnyCPU",
        "ICSharpCode.SharpZipLib",
        "Newtonsoft",
    ];

    static bool IsScriptReferenceCandidate(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        foreach (string prefix in ScriptReferencePrefixes)
        {
            if (name.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    // State of the last script run, read by scripts through IScriptInterface.
    public bool ScriptExecutionSuccess { get; private set; } = true;
    public string ScriptErrorMessage { get; private set; } = "";
    public string ScriptErrorType { get; private set; } = "";

    // A script can turn off the "finished" message for its own run only.
    public bool FinishedMessageEnabled = true;

    public bool IsAppClosed;

    public Scripting(MainViewModel mainVM)
    {
        MainVM = mainVM;
    }

    /// <summary>
    /// Compiles and runs a script.
    /// </summary>
    /// <param name="text">The script source (typically prefixed with a <c>#line</c> directive).</param>
    /// <param name="filePath">Path the script was loaded from, used for diagnostics and
    /// <see cref="IScriptInterface.ScriptPath"/>.</param>
    /// <param name="hostFactory">
    /// Optional factory for the host object the script runs with (the object a script sees as
    /// <c>this</c>/globals, normally a <see cref="ScriptGlobals"/>). Hosts without a UI - the
    /// Android cross-app API - pass a capturing host so that an externally requested script never
    /// opens dialogs; passing null uses the regular UI-backed host.
    /// </param>
    /// <param name="errorHandler">
    /// Optional handler for the error dialogs <see cref="RunScript"/> itself shows (compilation
    /// failures and script exceptions). UI-less hosts pass one to capture the text into their own
    /// result; null opens the regular dialog through <c>MainVM.View</c>.
    /// </param>
    public async Task<object?> RunScript(string text, string? filePath = null,
                                         Func<Scripting, string?, IScriptInterface>? hostFactory = null,
                                         Func<string, string?, Task>? errorHandler = null)
    {
        // Report failures through the caller's handler when it has one, as a dialog otherwise.
        Task ReportErrorAsync(string message, string title)
            => errorHandler is { } handler
                ? handler(message, title)
                : MainVM.View!.MessageDialog(message, title: title);

        ClearError();

        // A script can run another script with RunUMTScript, and the UI must stay disabled until the outer one is done.
        bool wasEnabled = MainVM.IsEnabled;

        try
        {
            MainVM.IsEnabled = false;

            // Platforms that ship the script assemblies as packaged assets (e.g. Android) extract
            // them to internal storage here, before the script is compiled, so the Roslyn scripting
            // engine can resolve metadata references from real files.
            PrepareScriptAssemblies?.Invoke();

            Script<object?> script;
            try
            {
                script = CreateScript(text, filePath);
            }
            catch (Exception e)
            {
                // Compilation infrastructure failures (e.g. missing reference assemblies on
                // platforms that don't support scripting) must surface as a dialog, not escape the
                // async void command handler and crash the whole app.
                SetError(e.Message, "CompilationErrorException");
                await ReportErrorAsync(e.ToString(), LocalizationSource.GetString("Msg_ScriptCompilationError"));

                return null;
            }

            if (!await CompileScript(script, ReportErrorAsync))
                return null;

            // Platforms that drive scripts without a UI (the Android cross-app API) pass their own
            // capturing host; everything else runs with the regular UI-backed host.
            IScriptInterface scripting = hostFactory?.Invoke(this, filePath) ?? new ScriptGlobals(this, filePath);

            try
            {
                // Execute the script off the UI thread: scripts block inside synchronous dialog
                // calls (ScriptMessage, PromptChooseDirectory, ...). Blocking the UI thread itself
                // is impossible on Android - its dispatcher supports no nested message loops
                // (PushFrame throws PlatformNotSupportedException) - and the UI must stay free
                // anyway for the Android SAF pickers to launch their intent and deliver results.
                // The big stack also covers deeply recursive decompiler calls made by scripts.
                ScriptState<object?> state = await RunOnDedicatedThread(
                    () => script.RunAsync(scripting).GetAwaiter().GetResult());

                // A nested script may have failed and set the error state; this script itself succeeded.
                ClearError();

                string defaultMessage = filePath is not null
                    ? string.Format(LocalizationSource.GetString("Main_ScriptFinished"), Path.GetFileName(filePath))
                    : "";
                ShowFinishedMessage(state.ReturnValue?.ToString() ?? defaultMessage);

                return state.ReturnValue;
            }
            catch (ScriptException e)
            {
                SetError(e.Message, "Exception");
                await ReportErrorAsync(e.Message, LocalizationSource.GetString("Msg_ErrorFromScript"));
            }
            catch (Exception e)
            {
                SetError(e.Message, "Exception");
                await ReportErrorAsync(e.ToString(), LocalizationSource.GetString("Msg_ScriptExecutionError"));
            }
            finally
            {
                (scripting as IDisposable)?.Dispose();
            }
        }
        finally
        {
            MainVM.IsEnabled = wasEnabled;
        }

        return null;
    }

    /// <summary>
    /// Compiles a script without running it. Returns whether it compiled without errors.
    /// </summary>
    public async Task<bool> LintScript(string text, string? filePath = null)
    {
        ClearError();

        Task ReportErrorAsync(string message, string title)
            => MainVM.View!.MessageDialog(message, title: title);

        try
        {
            PrepareScriptAssemblies?.Invoke();
            return await CompileScript(CreateScript(text, filePath), ReportErrorAsync);
        }
        catch (Exception e)
        {
            SetError(e.Message, "CompilationErrorException");
            await ReportErrorAsync(e.ToString(), LocalizationSource.GetString("Msg_ScriptCompilationError"));
            return false;
        }
    }

    /// <summary>
    /// Builds the script compilation options: the imports every script sees, the platform's
    /// reference set, and the source-path settings used for diagnostics.
    /// </summary>
    Script<object?> CreateScript(string text, string? filePath)
    {
        ScriptOptions options = ScriptOptions.Default
            .AddImports(
                "System",
                "System.Collections.Generic",
                "System.Linq",
                "System.IO",
                "System.Text",
                "System.Text.RegularExpressions",
                "System.Threading.Tasks",
                "UndertaleModLib",
                "UndertaleModLib.Compiler",
                "UndertaleModLib.Decompiler",
                "UndertaleModLib.Models",
                "UndertaleModLib.Scripting",
                "QiuForms")
            .WithFilePath(filePath)
            .WithFileEncoding(Encoding.Default)
            .WithEmitDebugInformation(true);

        if (!string.IsNullOrEmpty(ScriptAssembliesDirectory) && Directory.Exists(ScriptAssembliesDirectory))
        {
            // Reference the relevant DLLs next to the app directly, and let #r / name-based
            // references resolve from that directory: the default resolver only looks at the
            // trusted platform assemblies, which on Android are stored inside the APK and
            // cannot be resolved from the filesystem.
            IEnumerable<MetadataReference> references = Directory
                .EnumerateFiles(ScriptAssembliesDirectory, "*.dll", SearchOption.TopDirectoryOnly)
                .Where(IsScriptReferenceCandidate)
                .Select(path => MetadataReference.CreateFromFile(path));
            options = options
                .AddReferences(references)
                .WithMetadataResolver(ScriptMetadataResolver.Default.WithSearchPaths(ScriptAssembliesDirectory));

            // Roslyn's scripting engine always converts two assemblies to metadata references
            // through Assembly.Location: the core library (typeof(object).Assembly) and the
            // globals-type host assembly (IScriptInterface's UndertaleModLib). On Android those
            // locations are names inside the APK, not filesystem paths: "/System.Private.CoreLib.dll"
            // without AOT, but just "System.Private.CoreLib" (no extension) with Mono AOT, plus
            // bare relative names like "UndertaleModLib.dll". None of them exist on disk, which
            // crashed compilation with FileNotFoundException no matter how many explicit
            // references were supplied. Redirect every such conversion to the extracted plain
            // DLL copies instead (Roslyn >= 4.4 provides ScriptOptions.WithCreateFromFileFunc
            // for exactly this purpose).
            options = TryUseFileBasedAssemblyReferences(options, ScriptAssembliesDirectory);
        }
        else
        {
            options = options.AddReferences(
                "System.Core",
                "UndertaleModLib",
                "QiuForms",
                // Used by bundled scripts: System.Timers (AutoBackup) and System.Text.Json (the JSON importers and exporters).
                "System.ComponentModel.TypeConverter",
                "System.Text.Json",
                "System.Text.Encodings.Web");
        }

        return CSharpScript.Create(text, options, typeof(IScriptInterface));
    }

    /// <summary>
    /// Compiles <paramref name="script"/>, reporting errors through
    /// <paramref name="reportError"/> and recording them in the script state.
    /// </summary>
    async Task<bool> CompileScript(Script<object?> script, Func<string, string?, Task> reportError)
    {
        // Compile on a dedicated large-stack thread: Roslyn's binder recurses deeply and
        // .NET thread pool workers (especially under Mono/Android) have small stacks,
        // which ended in a native stack-overflow SIGSEGV inside libmonosgen.
        ImmutableArray<Diagnostic> diagnostics = await RunOnDedicatedThread(() => script.Compile());

        IEnumerable<Diagnostic> errors = diagnostics.Where((Diagnostic diagnostic) => diagnostic.Severity == DiagnosticSeverity.Error);
        if (!errors.Any())
            return true;

        string message = String.Join("\n", errors);
        SetError(message, "CompilationErrorException");
        await reportError(message, LocalizationSource.GetString("Msg_ScriptCompilationError"));

        return false;
    }

    void ClearError()
    {
        ScriptExecutionSuccess = true;
        ScriptErrorMessage = "";
        ScriptErrorType = "";
    }

    void SetError(string message, string type)
    {
        ScriptExecutionSuccess = false;
        ScriptErrorMessage = message;
        ScriptErrorType = type;
        MainVM.CommandTextBoxText = message;
    }

    void ShowFinishedMessage(string message)
    {
        if (FinishedMessageEnabled)
            MainVM.CommandTextBoxText = message;
        else
            FinishedMessageEnabled = true;
    }

    /// <summary>
    /// Runs <paramref name="func"/> on a dedicated background thread with a large stack.
    /// Roslyn compilation recurses deeply while binding symbols, and .NET thread pool workers
    /// (particularly under Mono on Android) provide far smaller stacks than desktop main
    /// threads - deep recursion there ends in an uncatchable native stack-overflow SIGSEGV.
    /// A dedicated thread reserves the needed stack without touching the thread pool.
    /// </summary>
    static Task<T> RunOnDedicatedThread<T>(Func<T> func, int maxStackSize = 64 * 1024 * 1024)
    {
        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Thread thread = new(() =>
        {
            try
            {
                completion.SetResult(func());
            }
            catch (Exception e)
            {
                completion.SetException(e);
            }
        }, maxStackSize)
        {
            IsBackground = true,
            Name = "UMT.ScriptCompile",
        };
        thread.Start();

        return completion.Task;
    }

    /// <summary>
    /// Redirects Roslyn's implicit "Assembly → metadata reference" conversions (used for the core
    /// library <c>typeof(object).Assembly</c> and for the globals-type host assembly) away from
    /// <see cref="Assembly.Location"/> — an in-APK name like "/System.Private.CoreLib.dll" without
    /// AOT, or a bare name like "System.Private.CoreLib" (no extension) under Mono AOT
    /// on Android — to the plain DLL copies inside <paramref name="assembliesDirectory"/>.
    /// Roslyn exposes <c>ScriptOptions.WithCreateFromFileFunc</c> for exactly this purpose; it is
    /// internal and its return type is inaccessible, so the delegate is assembled via expression
    /// trees and the hook is skipped silently if unavailable.
    /// </summary>
    private static ScriptOptions TryUseFileBasedAssemblyReferences(ScriptOptions options, string assembliesDirectory)
    {
        try
        {
            PropertyInfo? property = typeof(ScriptOptions).GetProperty(
                "CreateFromFileFunc",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo? withFunc = typeof(ScriptOptions).GetMethod(
                "WithCreateFromFileFunc",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (property is null || withFunc is null || !property.CanWrite ||
                withFunc.GetParameters().Length != 1 ||
                withFunc.GetParameters()[0].ParameterType != property.PropertyType)
            {
                return options;
            }

            // Delegate shape (Roslyn >= 5.x): Func<string path, PEStreamOptions, MetadataReferenceProperties, MetadataImageReference>.
            ParameterExpression pathParameter = Expression.Parameter(typeof(string), "path");
            ParameterExpression optionsParameter = Expression.Parameter(typeof(PEStreamOptions), "peStreamOptions");
            ParameterExpression propertiesParameter = Expression.Parameter(typeof(MetadataReferenceProperties), "properties");

            MethodInfo createReference = typeof(Scripting).GetMethod(
                nameof(CreateScriptAssemblyReference),
                BindingFlags.NonPublic | BindingFlags.Static,
                binder: null,
                [typeof(string), typeof(PEStreamOptions), typeof(MetadataReferenceProperties), typeof(string)],
                modifiers: null)!;

            MethodCallExpression body = Expression.Call(
                createReference,
                pathParameter,
                optionsParameter,
                propertiesParameter,
                Expression.Constant(assembliesDirectory));

            LambdaExpression lambda = Expression.Lambda(
                property.PropertyType,
                Expression.Convert(body, property.PropertyType.GetGenericArguments()[^1]),
                pathParameter,
                optionsParameter,
                propertiesParameter);

            object? result = withFunc.Invoke(options, [lambda.Compile()]);
            return result as ScriptOptions ?? options;
        }
        catch
        {
            return options;
        }
    }

    /// <summary>
    /// Body of the <c>CreateFromFileFunc</c> delegate: resolves <paramref name="path"/> against the
    /// extracted script-assembly copies when the runtime-reported location does not exist on disk
    /// and returns an in-memory metadata reference. The actual return value is Roslyn's internal
    /// <c>MetadataImageReference</c>, hence the <see cref="object"/> signature.
    /// </summary>
    private static object CreateScriptAssemblyReference(string path,
                                                        PEStreamOptions _,
                                                        MetadataReferenceProperties properties,
                                                        string assembliesDirectory)
    {
        path = ResolveExtractedPath(path, assembliesDirectory) ?? path;
        Stream stream = File.OpenRead(path);
        try
        {
            return MetadataReference.CreateFromStream(stream, properties, null, path);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Maps an assembly location reported by the runtime onto the extracted plain DLL copy, or
    /// null when no extracted copy matches (desktop: real on-disk assemblies are returned as-is).
    /// Under Mono AOT the reported locations need normalization: the core library is reported
    /// without its ".dll" extension (e.g. "System.Private.CoreLib"), the host assembly as a bare
    /// relative file name, and AOT native images live under names like
    /// "libaot-System.Private.CoreLib.dll.so" - none of which exist on the filesystem.
    /// </summary>
    private static string? ResolveExtractedPath(string path, string assembliesDirectory)
    {
        string fileName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(fileName))
            return null;

        // Fast path: the reported file name matches the extracted copy directly.
        string candidate = Path.Combine(assembliesDirectory, fileName);
        if (File.Exists(candidate))
            return candidate;

        string baseName = fileName;
        if (baseName.EndsWith(".dll.so", StringComparison.OrdinalIgnoreCase))
            baseName = baseName[..^3]; // AOT native image ("libaot-Foo.dll.so")
        else if (baseName.EndsWith(".so", StringComparison.OrdinalIgnoreCase))
            baseName = baseName[..^3]; // non-AOT native image ("libFoo.dll.so")
        baseName = TrimAotPrefix(baseName);
        if (!baseName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            baseName += ".dll";

        candidate = Path.Combine(assembliesDirectory, baseName);
        if (File.Exists(candidate))
            return candidate;

        // Android's filesystem is case-sensitive while the reported name may differ in case, so
        // fall back to matching by simple name.
        return Directory
            .EnumerateFiles(assembliesDirectory, Path.GetFileNameWithoutExtension(baseName) + ".dll",
                            SearchOption.TopDirectoryOnly)
            .FirstOrDefault();
    }

    /// <summary>Strips the Mono AOT native-image name prefixes ("libaot-" / "lib").</summary>
    private static string TrimAotPrefix(string name)
    {
        if (name.StartsWith("libaot-", StringComparison.OrdinalIgnoreCase))
            return name["libaot-".Length..];
        if (name.StartsWith("lib", StringComparison.OrdinalIgnoreCase))
            return name[3..];
        return name;
    }
}

public class ScriptGlobals : IScriptInterface, IDisposable
{
    private readonly Scripting scripting;
    private readonly MainViewModel mainVM;
    private readonly string? scriptPath;

    private ILoaderWindow? loaderWindow;
    private int loaderValue;

    // While the progress bar updater runs, progress changes aren't posted one by one to the UI thread;
    // a timer shows the latest value instead, which is cheaper for scripts that make many changes.
    private DispatcherTimer? progressBarUpdater;
    private volatile bool isProgressBarUpdaterRunning;

    public ScriptGlobals(Scripting scripting, string? scriptPath)
    {
        this.scripting = scripting;
        mainVM = scripting.MainVM;
        this.scriptPath = scriptPath;
    }

    public virtual void Dispose()
    {
        progressBarUpdater?.Stop();
        progressBarUpdater = null;
        isProgressBarUpdaterRunning = false;

        // Runs on whatever thread the script ended on; closing the simulated window touches the
        // visual tree, so it must be marshaled to the UI thread.
        Dispatcher.UIThread.Invoke(CloseLoaderWindow);
    }

    public UndertaleData? Data => mainVM.Data;

    public ProjectContext? Project => mainVM.Project;

    public string? FilePath => mainVM.DataPath;

    public string? ScriptPath => scriptPath;

    // The item selected in the data explorer tree.
    public virtual object? Highlighted => mainVM.DataExplorer.HighlightedValue;

    // The resource open in the current tab. Tabs that aren't a resource, like General info, give null.
    public virtual object? Selected => (mainVM.Tabs.TabSelected?.Content as IUndertaleResourceViewModel)?.Resource;

    public virtual bool CanSave => mainVM.Data is not null;

    public virtual bool ScriptExecutionSuccess => scripting.ScriptExecutionSuccess;

    public virtual string ScriptErrorMessage => scripting.ScriptErrorMessage;

    public string? ExePath => Scripting.ExePathOverride ?? Path.GetDirectoryName(Environment.ProcessPath);

    public virtual string ScriptErrorType => scripting.ScriptErrorType;

    public virtual bool IsAppClosed => scripting.IsAppClosed;

    public virtual Action<Action> MainThreadAction => Dispatcher.UIThread.Invoke;

    /// <summary>
    /// Shows a dialog on the UI thread and blocks the caller until it is dismissed. Scripts run
    /// off the UI thread (see <see cref="Scripting.RunScript"/>), while windows must be created
    /// and shown there; blocking is only legal from a non-UI thread. On the UI thread itself
    /// (desktop) nested dispatcher frames are pumped instead, as before.
    /// </summary>
    static TResult ShowDialogBlocking<TResult>(Func<Task<TResult>> dialog)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return dialog().WaitOnDispatcherFrame();

        return Dispatcher.UIThread.InvokeAsync(dialog).GetAwaiter().GetResult();
    }

    static void ShowDialogBlocking(Func<Task> dialog)
    {
        if (Dispatcher.UIThread.CheckAccess())
            dialog().WaitOnDispatcherFrame();
        else
            Dispatcher.UIThread.InvokeAsync(dialog).GetAwaiter().GetResult();
    }

    void PostProgressValue(DispatcherPriority priority)
    {
        if (isProgressBarUpdaterRunning)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            loaderWindow?.SetValue(loaderValue);
        }, priority);
    }

    public virtual void AddProgress(int amount)
    {
        loaderValue += amount;
        PostProgressValue(DispatcherPriority.Default);
    }

    public virtual void AddProgressParallel(int amount)
    {
        Interlocked.Add(ref loaderValue, amount);
        PostProgressValue(DispatcherPriority.Background);
    }

    public void ChangeSelection(object newSelection, bool inNewTab = false)
    {
        // Wait so the script continues only after the tab is open (which may ask to save the current tab), like the WPF version.
        WaitOnUIThread(() => mainVM.Tabs.TabOpen(newSelection, inNewTab));
    }

    public virtual Task ClickableSearchOutput(string title, string query, int resultsCount, IOrderedEnumerable<KeyValuePair<string, List<(int lineNum, string codeLine)>>> resultsDict, bool showInDecompiledView, IOrderedEnumerable<string>? failedList = null)
    {
        ShowSearchResults(title, query, resultsDict, showInDecompiledView, failedList);
        return Task.CompletedTask;
    }

    public virtual Task ClickableSearchOutput(string title, string query, int resultsCount, IDictionary<string, List<(int lineNum, string codeLine)>> resultsDict, bool showInDecompiledView, IEnumerable<string>? failedList = null)
    {
        ShowSearchResults(title, query, resultsDict, showInDecompiledView, failedList);
        return Task.CompletedTask;
    }

    // Shows the results in a "Search in code" window, where they can be opened like the window's own results.
    // Search scripts may gather results inside Task.Run and call this from there, but windows must be created on the UI thread.
    void ShowSearchResults(string title, string query, IEnumerable<KeyValuePair<string, List<(int lineNum, string codeLine)>>> results, bool showInDecompiledView, IEnumerable<string>? failedList)
    {
        if (mainVM.View is not MainView mainView)
            return;

        WaitOnUIThread(() =>
        {
            SearchInCodeViewModel searchVM = new(mainVM.ServiceProvider);
            searchVM.ShowResults(query, results, inAssembly: !showInDecompiledView, failedCount: failedList?.Count() ?? 0);
            mainView.OpenSearchInCode(searchVM, title);
            return Task.FromResult(true);
        });
    }

    public virtual void EnableUI()
    {
        Dispatcher.UIThread.Invoke(() => mainVM.IsEnabled = true);
    }

    /// <inheritdoc/>
    [Obsolete("SyncBinding() is deprecated and does not make data mutations thread-safe. Use MainThreadAction to mutate data on the main thread instead.")]
    public virtual void SyncBinding(string resourceType, bool enable)
    {
        // Avalonia has no collection synchronization equivalent; list notifications are already
        // marshaled to the UI thread by UndertaleListNotifications.
        ScriptingUtil.WarnDeprecatedScriptAPI(nameof(SyncBinding),
                                              "Use MainThreadAction to mutate data on the main thread instead.");
    }

    /// <inheritdoc/>
    [Obsolete("DisableAllSyncBindings() is deprecated. Use MainThreadAction to mutate data on the main thread instead.")]
    public virtual void DisableAllSyncBindings()
    {
        // Avalonia has no collection synchronization equivalent; list notifications are already
        // marshaled to the UI thread by UndertaleListNotifications.
        ScriptingUtil.WarnDeprecatedScriptAPI(nameof(DisableAllSyncBindings),
                                              "Use MainThreadAction to mutate data on the main thread instead.");
    }

    public string GetDecompiledText(string codeName, GlobalDecompileContext? context = null, IDecompileSettings? settings = null)
    {
        return GetDecompiledText(mainVM.Data!.Code.ByName(codeName), context, settings);
    }

    public string GetDecompiledText(UndertaleCode code, GlobalDecompileContext? context = null, IDecompileSettings? settings = null)
    {
        context ??= new(mainVM.Data);
        settings ??= mainVM.Data!.ToolInfo.DecompilerSettings;

        return new DecompileContext(context, code, settings).DecompileToString();
    }

    public string GetDisassemblyText(string codeName)
    {
        return GetDisassemblyText(mainVM.Data!.Code.ByName(codeName));
    }

    public string GetDisassemblyText(UndertaleCode code)
    {
        return code.Disassemble(mainVM.Data!.Variables, mainVM.Data!.CodeLocals?.For(code));
    }

    public virtual int GetProgress()
    {
        return loaderValue;
    }

    public virtual void HideProgressBar()
    {
        Dispatcher.UIThread.Invoke(CloseLoaderWindow);
    }

    void CloseLoaderWindow()
    {
        loaderWindow?.Close();
        loaderWindow = null;
    }

    public virtual void IncrementProgress()
    {
        loaderValue++;
        PostProgressValue(DispatcherPriority.Default);
    }

    public virtual void IncrementProgressParallel()
    {
        Interlocked.Increment(ref loaderValue);
        PostProgressValue(DispatcherPriority.Background);
    }

    public virtual void InitializeScriptDialog()
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            loaderWindow ??= mainVM.View!.LoaderOpen();
            loaderWindow.SetMessage("Script in progress...");
            loaderWindow.SetStatus("Please wait...");
        });
    }

    public virtual bool LintUMTScript(string path)
    {
        if (!File.Exists(path))
        {
            ScriptError(path + " does not exist!");
            return false;
        }

        string text = File.ReadAllText(path);
        return WaitOnUIThread(() => scripting.LintScript(text, path));
    }

    // Scripts can call this from a background thread (e.g. LintAllScripts.csx lints inside Task.Run), but scripts and dialogs run on the UI thread.
    static T WaitOnUIThread<T>(Func<Task<T>> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return action().WaitOnDispatcherFrame();

        return Dispatcher.UIThread.InvokeAsync(action).GetAwaiter().GetResult();
    }

    public virtual bool MakeNewDataFile()
    {
        Dispatcher.UIThread.Invoke(() => mainVM.NewData());
        return true;
    }

    public virtual string? PromptChooseDirectory()
    {
        // The dialog is shown on the UI thread while this (script) thread blocks: the Android SAF
        // pickers need the main thread to launch their intent and deliver the result.
        IReadOnlyList<IStorageFolder>? folders = ShowDialogBlocking(() => mainVM.View!.OpenFolderDialog(new()
        {
            Title = LocalizationSource.GetString("Msg_SelectDirectory"),
        }));

        if (folders is null || folders.Count != 1)
            return null;

        return folders[0].TryGetLocalPath();
    }

    public virtual string? PromptLoadFile(string? defaultExt, string? filter)
    {
        var files = ShowDialogBlocking(() => mainVM.View!.OpenFileDialog(new FilePickerOpenOptions()
        {
            Title = LocalizationSource.GetString("Msg_LoadFile"),
            FileTypeFilter = ScriptFileTypes(filter),
        }));

        if (files is null || files.Count != 1)
            return null;

        return files[0].TryGetLocalPath();
    }

    public virtual string? PromptSaveFile(string defaultExt, string filter)
    {
        var file = ShowDialogBlocking(() => mainVM.View!.SaveFileDialog(new FilePickerSaveOptions()
        {
            Title = LocalizationSource.GetString("Msg_SaveFile"),
            FileTypeChoices = ScriptFileTypes(filter),
            DefaultExtension = ScriptDefaultExtension(defaultExt),
        }));

        if (file is null)
            return null;

        return file.TryGetLocalPath();
    }

    // Scripts that pass no filter get the data file filter, like in the WPF version.
    static IReadOnlyList<FilePickerFileType> ScriptFileTypes(string? filter)
    {
        if (filter is null)
            return FilePickerFileTypes.Data;

        List<FilePickerFileType> types = FilePickerFileTypes.FromWindowsFilter(filter);
        if (types.Count == 0)
            return FilePickerFileTypes.All;

        return types;
    }

    // Scripts pass extensions both with and without the dot ("txt", ".txt"), and null means a data file, like in the WPF version.
    static string? ScriptDefaultExtension(string? defaultExt)
    {
        if (defaultExt is null)
            return ".win";
        if (defaultExt == "")
            return null;

        return defaultExt.StartsWith('.') ? defaultExt : "." + defaultExt;
    }

    public virtual bool RunUMTScript(string path)
    {
        if (!File.Exists(path))
        {
            ScriptError(path + " does not exist!");
            return false;
        }

        // Errors are already shown by RunScript, so unlike the WPF version, no second error message is shown here.
        string text = File.ReadAllText(path);
        WaitOnUIThread(() => scripting.RunScript(text, path));
        return scripting.ScriptExecutionSuccess;
    }

    public virtual void ScriptError(string error, string? title = null, bool SetConsoleText = true)
    {
        ShowDialogBlocking(() => mainVM.View!.MessageDialog(error, title ?? LocalizationSource.GetString("Common_Error")));

        if (SetConsoleText)
        {
            Dispatcher.UIThread.Invoke(() => mainVM.CommandTextBoxText = error);
        }
    }

    public virtual string? ScriptInputDialog(string title, string label, string defaultInput, string cancelText, string submitText, bool isMultiline, bool preventClose)
    {
        return ShowDialogBlocking(() => mainVM.View!.TextBoxDialog(label, defaultInput, title: title, isMultiline: isMultiline,
            okText: submitText, cancelText: cancelText, preventClose: preventClose));
    }

    /// <inheritdoc/>
    public virtual bool SupportsQiuForms => true;

    /// <inheritdoc/>
    public virtual QiuDialogResult ShowQiuForm(QiuForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return AvaloniaQiuFormsHost.Show(form, mainVM);
    }

    public virtual void ScriptMessage(string message)
    {
        ShowDialogBlocking(() => mainVM.View!.MessageDialog(message, title: LocalizationSource.GetString("Msg_ScriptMessageTitle")));
    }

    public void ExportRoomAsPNG(UndertaleRoom room, string filePath)
    {
        ArgumentNullException.ThrowIfNull(room);

        // The room renderer shares the app's image cache with the open editors, and that cache isn't thread-safe.
        // It renders into memory first so a failed render doesn't leave a broken PNG behind, or replace a good one.
        MemoryStream png = new();
        WaitOnUIThread(async () =>
        {
            await ImportExport.ExportRoomAsPNG(room, png);
            return true;
        });
        File.WriteAllBytes(filePath, png.ToArray());
    }

    public virtual void ScriptOpenURL(string url)
    {
        ShowDialogBlocking(() => mainVM.View!.LaunchUriAsync(new(url)));
    }

    public virtual bool ScriptQuestion(string message)
    {
        return ShowDialogBlocking(() => mainVM.View!.MessageDialog(message, LocalizationSource.GetString("Msg_ScriptQuestionTitle"), MessageWindow.Buttons.YesNo)) == MessageWindow.Result.Yes;
    }

    public virtual void ScriptWarning(string message)
    {
        ShowDialogBlocking(() => mainVM.View!.MessageDialog(message, title: LocalizationSource.GetString("Msg_ScriptWarningTitle")));
    }

    public virtual void SetFinishedMessage(bool isFinishedMessageEnabled)
    {
        scripting.FinishedMessageEnabled = isFinishedMessageEnabled;
    }

    public virtual void SetProgress(int value)
    {
        loaderValue = value;
        PostProgressValue(DispatcherPriority.Default);
    }

    public virtual void SetProgressBar(string message, string status, double progressValue, double maxValue)
    {
        loaderValue = (int)progressValue;

        Dispatcher.UIThread.Invoke(() =>
        {
            loaderWindow ??= mainVM.View!.LoaderOpen();
            loaderWindow.EnsureShown();
            loaderWindow.SetMessage(message);
            loaderWindow.SetStatus(status);
            loaderWindow.SetValue(loaderValue);
            loaderWindow.SetMaximum((int)maxValue);
        });
    }

    public virtual void SetProgressBar()
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            loaderWindow ??= mainVM.View!.LoaderOpen();
            loaderWindow.EnsureShown();
        });
    }

    public virtual void SetUMTConsoleText(string message)
    {
        Dispatcher.UIThread.Invoke(() => mainVM.CommandTextBoxText = message);
    }

    public virtual string? SimpleTextInput(string title, string label, string defaultValue, bool allowMultiline, bool showDialog = true)
    {
        // Like the WPF version, a non-blocking input window lets the script continue right away, so there's no input to return.
        if (!showDialog)
        {
            mainVM.View!.TextBoxShow(label, defaultValue, title: title, isMultiline: allowMultiline);
            return null;
        }

        return ShowDialogBlocking(() => mainVM.View!.TextBoxDialog(label, defaultValue, title: title, isMultiline: allowMultiline));
    }

    public virtual void SimpleTextOutput(string title, string label, string message, bool allowMultiline)
    {
        ShowDialogBlocking(() => mainVM.View!.TextBoxDialog(label, message, title: title, isMultiline: allowMultiline, isReadOnly: true));
    }

    public virtual void StartProgressBarUpdater()
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            if (progressBarUpdater is not null)
                return;

            // About 30 times per second, like the WPF version.
            progressBarUpdater = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background,
                (_, _) => loaderWindow?.SetValue(loaderValue));
            progressBarUpdater.Start();
            isProgressBarUpdaterRunning = true;
        });
    }

    public virtual Task StopProgressBarUpdater()
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            if (progressBarUpdater is null)
                return;

            progressBarUpdater.Stop();
            progressBarUpdater = null;
            isProgressBarUpdaterRunning = false;

            // Show the final value, which the timer may not have shown yet.
            loaderWindow?.SetValue(loaderValue);
        });

        return Task.CompletedTask;
    }

    public virtual void UpdateProgressBar(string message, string status, double progressValue, double maxValue)
    {
        SetProgressBar(message, status, progressValue, maxValue);
    }

    public virtual void UpdateProgressStatus(string status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            loaderWindow?.SetTextToMessageAndStatus(status: status);
        });
    }

    public virtual void UpdateProgressValue(double progressValue)
    {
        loaderValue = (int)progressValue;
        PostProgressValue(DispatcherPriority.Default);
    }
}
