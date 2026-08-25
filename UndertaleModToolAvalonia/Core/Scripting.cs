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
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.Extensions.DependencyInjection;
using Underanalyzer.Decompiler;
using UndertaleModLib;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;
using UndertaleModLib.Project;
using UndertaleModLib.Scripting;
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
    /// Name prefixes of the assemblies that scripts may bind against. The Android assembly
    /// extractor dumps every linked assembly (the whole Avalonia/Skia/SDL/interop stack included)
    /// next to the app, but scripts never use those; feeding all of them to the compiler balloons
    /// its metadata graph - a real problem on memory-constrained devices, and it deepens the
    /// binder's recursion during compilation.
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
        // Texture import/export (ImageMagick wrapper) and zip support.
        "Magick.NET",
        "ImageMagick",
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

    public Scripting(IServiceProvider serviceProvider)
    {
        MainVM = serviceProvider.GetRequiredService<MainViewModel>();
    }

    public async Task<object?> RunScript(string text, string? filePath = null)
    {
        try
        {
            MainVM.IsEnabled = false;

            // Platforms that ship the script assemblies as packaged assets (e.g. Android) extract
            // them to internal storage here, before the script is compiled, so the Roslyn scripting
            // engine can resolve metadata references from real files.
            PrepareScriptAssemblies?.Invoke();

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
                    "UndertaleModLib.Scripting")
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
                // locations are fabricated paths inside the APK (e.g. "/System.Private.CoreLib.dll")
                // and do not exist on disk, which crashed compilation with FileNotFoundException no
                // matter how many explicit references were supplied. Redirect every such conversion
                // to the extracted plain DLL copies instead (Roslyn >= 4.4 provides
                // ScriptOptions.WithCreateFromFileFunc for exactly this purpose).
                options = TryUseFileBasedAssemblyReferences(options, ScriptAssembliesDirectory);
            }
            else
            {
                options = options.AddReferences("System.Core", "UndertaleModLib");
            }

            Script<object?> script;
            ImmutableArray<Diagnostic> diagnostics;

            try
            {
                script = CSharpScript.Create(text, options, typeof(IScriptInterface));

                // Compile on a dedicated large-stack thread: Roslyn's binder recurses deeply and
                // .NET thread pool workers (especially under Mono/Android) have small stacks,
                // which ended in a native stack-overflow SIGSEGV inside libmonosgen.
                diagnostics = await RunOnDedicatedThread(() => script.Compile());
            }
            catch (Exception e)
            {
                // Compilation infrastructure failures (e.g. missing reference assemblies on
                // platforms that don't support scripting) must surface as a dialog, not escape the
                // async void command handler and crash the whole app.
                await MainVM.View!.MessageDialog(e.ToString(), title: LocalizationSource.GetString("Msg_ScriptCompilationError"));

                return null;
            }

            IEnumerable<Diagnostic> errors = diagnostics.Where((Diagnostic diagnostic) => diagnostic.Severity == DiagnosticSeverity.Error);
            if (errors.Any())
            {
                string message = String.Join("\n", errors);
                await MainVM.View!.MessageDialog(message, title: LocalizationSource.GetString("Msg_ScriptCompilationError"));

                return null;
            }

            ScriptGlobals scripting = new(this, filePath);

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
                return state.ReturnValue;
            }
            catch (ScriptException e)
            {
                await MainVM.View!.MessageDialog(e.Message, title: LocalizationSource.GetString("Msg_ErrorFromScript"));
            }
            catch (Exception e)
            {
                await MainVM.View!.MessageDialog(e.ToString(), title: LocalizationSource.GetString("Msg_ScriptExecutionError"));
            }
            finally
            {
                scripting.Dispose();
            }
        }
        finally
        {
            MainVM.IsEnabled = true;
        }

        return null;
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
    /// <see cref="Assembly.Location"/> — a fabricated in-APK path like "/System.Private.CoreLib.dll"
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
    /// extracted script-assembly copies when the original location does not exist on disk and
    /// returns an in-memory metadata reference. The actual return value is Roslyn's internal
    /// <c>MetadataImageReference</c>, hence the <see cref="object"/> signature.
    /// </summary>
    private static object CreateScriptAssemblyReference(string path,
                                                        PEStreamOptions _,
                                                        MetadataReferenceProperties properties,
                                                        string assembliesDirectory)
    {
        string localCandidate = Path.Combine(assembliesDirectory, Path.GetFileName(path));
        if (File.Exists(localCandidate))
        {
            path = localCandidate;
        }
        // Otherwise keep Roslyn's original location, which is valid on platforms where real
        // on-disk assemblies exist.

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
}

public class ScriptGlobals : IScriptInterface, IDisposable
{
    private readonly MainViewModel mainVM;
    private readonly string? scriptPath;

    private ILoaderWindow? loaderWindow;
    private int loaderValue;

    public ScriptGlobals(Scripting scripting, string? scriptPath)
    {
        mainVM = scripting.MainVM;
        this.scriptPath = scriptPath;
    }

    public void Dispose()
    {
        // Runs on whatever thread the script ended on; closing the simulated window touches the
        // visual tree, so it must be marshaled to the UI thread.
        Dispatcher.UIThread.Invoke(CloseLoaderWindow);
    }

    public UndertaleData? Data => mainVM.Data;

    public ProjectContext? Project => mainVM.Project;

    public string? FilePath => mainVM.DataPath;

    public string? ScriptPath => scriptPath;

    public object Highlighted => throw new NotImplementedException();

    public object Selected => throw new NotImplementedException();

    public bool CanSave => throw new NotImplementedException();

    public bool ScriptExecutionSuccess => throw new NotImplementedException();

    public string ScriptErrorMessage => throw new NotImplementedException();

    public string? ExePath => Path.GetDirectoryName(Environment.ProcessPath);

    public string ScriptErrorType => throw new NotImplementedException();

    public bool IsAppClosed => throw new NotImplementedException();

    public Action<Action> MainThreadAction => Dispatcher.UIThread.Invoke;

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

    public void AddProgress(int amount)
    {
        loaderValue += amount;

        Dispatcher.UIThread.Post(() =>
        {
            loaderWindow?.SetValue(loaderValue);
        });
    }

    public void AddProgressParallel(int amount)
    {
        Interlocked.Add(ref loaderValue, amount);

        Dispatcher.UIThread.Post(() =>
        {
            loaderWindow?.SetValue(loaderValue);
        }, DispatcherPriority.Background);
    }

    public void ChangeSelection(object newSelection, bool inNewTab = false)
    {
        // TODO: Implement
    }

    public Task ClickableSearchOutput(string title, string query, int resultsCount, IOrderedEnumerable<KeyValuePair<string, List<(int lineNum, string codeLine)>>> resultsDict, bool showInDecompiledView, IOrderedEnumerable<string>? failedList = null)
    {
        throw new NotImplementedException();
    }

    public Task ClickableSearchOutput(string title, string query, int resultsCount, IDictionary<string, List<(int lineNum, string codeLine)>> resultsDict, bool showInDecompiledView, IEnumerable<string>? failedList = null)
    {
        throw new NotImplementedException();
    }

    public void EnableUI()
    {
        Dispatcher.UIThread.Invoke(() => mainVM.IsEnabled = true);
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

    public int GetProgress()
    {
        return loaderValue;
    }

    public void HideProgressBar()
    {
        Dispatcher.UIThread.Invoke(CloseLoaderWindow);
    }

    void CloseLoaderWindow()
    {
        loaderWindow?.Close();
        loaderWindow = null;
    }

    public void IncrementProgress()
    {
        loaderValue++;

        Dispatcher.UIThread.Post(() =>
        {
            loaderWindow?.SetValue(loaderValue);
        });
    }

    public void IncrementProgressParallel()
    {
        Interlocked.Increment(ref loaderValue);

        Dispatcher.UIThread.Post(() =>
        {
            loaderWindow?.SetValue(loaderValue);
        }, DispatcherPriority.Background);
    }

    public void InitializeScriptDialog()
    {
        // TODO: Implement
    }

    public bool LintUMTScript(string path)
    {
        throw new NotImplementedException();
    }

    public bool MakeNewDataFile()
    {
        Dispatcher.UIThread.Invoke(() => mainVM.NewData());
        return true;
    }

    public string? PromptChooseDirectory()
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

    public string? PromptLoadFile(string? defaultExt, string? filter)
    {
        // TODO: filter
        var files = ShowDialogBlocking(() => mainVM.View!.OpenFileDialog(new FilePickerOpenOptions()
        {
            Title = LocalizationSource.GetString("Msg_LoadFile"),
            FileTypeFilter = FilePickerFileTypes.All,
        }));

        if (files is null || files.Count != 1)
            return null;

        return files[0].TryGetLocalPath();
    }

    public string? PromptSaveFile(string defaultExt, string filter)
    {
        // TODO: filter
        var file = ShowDialogBlocking(() => mainVM.View!.SaveFileDialog(new FilePickerSaveOptions()
        {
            Title = LocalizationSource.GetString("Msg_SaveFile"),
            FileTypeChoices = FilePickerFileTypes.All,
            DefaultExtension = defaultExt,
        }));

        if (file is null)
            return null;

        return file.TryGetLocalPath();
    }

    public bool RunUMTScript(string path)
    {
        throw new NotImplementedException();
    }

    public void ScriptError(string error, string? title = null, bool SetConsoleText = true)
    {
        ShowDialogBlocking(() => mainVM.View!.MessageDialog(error, title ?? LocalizationSource.GetString("Common_Error")));

        if (SetConsoleText)
        {
            Dispatcher.UIThread.Invoke(() => mainVM.CommandTextBoxText = error);
        }
    }

    public string? ScriptInputDialog(string title, string label, string defaultInput, string cancelText, string submitText, bool isMultiline, bool preventClose)
    {
        // TODO: cancelText, submitText, preventClose
        return ShowDialogBlocking(() => mainVM.View!.TextBoxDialog(label, defaultInput, title: title, isMultiline: isMultiline));
    }

    public void ScriptMessage(string message)
    {
        ShowDialogBlocking(() => mainVM.View!.MessageDialog(message, title: LocalizationSource.GetString("Msg_ScriptMessageTitle")));
    }

    public void ScriptOpenURL(string url)
    {
        ShowDialogBlocking(() => mainVM.View!.LaunchUriAsync(new(url)));
    }

    public bool ScriptQuestion(string message)
    {
        return ShowDialogBlocking(() => mainVM.View!.MessageDialog(message, LocalizationSource.GetString("Msg_ScriptQuestionTitle"), MessageWindow.Buttons.YesNo)) == MessageWindow.Result.Yes;
    }

    public void ScriptWarning(string message)
    {
        ShowDialogBlocking(() => mainVM.View!.MessageDialog(message, title: LocalizationSource.GetString("Msg_ScriptWarningTitle")));
    }

    public void SetFinishedMessage(bool isFinishedMessageEnabled)
    {
        // TODO: Implement
    }

    public void SetProgress(int value)
    {
        loaderValue = value;

        Dispatcher.UIThread.Post(() =>
        {
            loaderWindow?.SetValue(loaderValue);
        });
    }

    public void SetProgressBar(string message, string status, double progressValue, double maxValue)
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

    public void SetProgressBar()
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            loaderWindow ??= mainVM.View!.LoaderOpen();
            loaderWindow.EnsureShown();
        });
    }

    public void SetUMTConsoleText(string message)
    {
        Dispatcher.UIThread.Invoke(() => mainVM.CommandTextBoxText = message);
    }

    public string? SimpleTextInput(string title, string label, string defaultValue, bool allowMultiline, bool showDialog = true)
    {
        // TODO: showDialog
        return ShowDialogBlocking(() => mainVM.View!.TextBoxDialog(label, defaultValue, title: title, isMultiline: allowMultiline));
    }

    public void SimpleTextOutput(string title, string label, string message, bool allowMultiline)
    {
        ShowDialogBlocking(() => mainVM.View!.TextBoxDialog(label, message, title: title, isMultiline: allowMultiline, isReadOnly: true));
    }

    public void StartProgressBarUpdater()
    {
        // TODO: Implement
    }

    public Task StopProgressBarUpdater()
    {
        // TODO: Implement
        return Task.CompletedTask;
    }

    public void UpdateProgressBar(string message, string status, double progressValue, double maxValue)
    {
        SetProgressBar(message, status, progressValue, maxValue);
    }

    public void UpdateProgressStatus(string status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            loaderWindow?.SetTextToMessageAndStatus(status: status);
        });
    }

    public void UpdateProgressValue(double progressValue)
    {
        loaderValue = (int)progressValue;

        Dispatcher.UIThread.Post(() =>
        {
            loaderWindow?.SetValue(loaderValue);
        });
    }
}
