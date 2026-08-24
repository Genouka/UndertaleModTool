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
                // Reference every DLL next to the app directly, and let #r / name-based references
                // resolve from that directory: the default resolver only looks at the trusted
                // platform assemblies, which on Android are stored inside the APK and cannot be
                // resolved from the filesystem.
                IEnumerable<MetadataReference> references = Directory
                    .EnumerateFiles(ScriptAssembliesDirectory, "*.dll", SearchOption.TopDirectoryOnly)
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
                ScriptState<object?> state = await script.RunAsync(scripting);
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
        loaderWindow?.Close();
        loaderWindow = null;
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
        mainVM.IsEnabled = true;
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
        mainVM.NewData();
        return true;
    }

    public string? PromptChooseDirectory()
    {
        IReadOnlyList<IStorageFolder> folders = Task.Run(() => mainVM.View!.OpenFolderDialog(new()
        {
            Title = LocalizationSource.GetString("Msg_SelectDirectory"),
        })).Result;

        if (folders.Count != 1)
            return null;

        return folders[0].TryGetLocalPath();
    }

    public string? PromptLoadFile(string? defaultExt, string? filter)
    {
        // TODO: filter
        var files = Task.Run(() => mainVM.View!.OpenFileDialog(new FilePickerOpenOptions()
        {
            Title = LocalizationSource.GetString("Msg_LoadFile"),
            FileTypeFilter = FilePickerFileTypes.All,
        })).Result;

        if (files.Count != 1)
            return null;

        return files[0].TryGetLocalPath();
    }

    public string? PromptSaveFile(string defaultExt, string filter)
    {
        // TODO: filter
        var file = Task.Run(() => mainVM.View!.SaveFileDialog(new FilePickerSaveOptions()
        {
            Title = LocalizationSource.GetString("Msg_SaveFile"),
            FileTypeChoices = FilePickerFileTypes.All,
            DefaultExtension = defaultExt,
        })).Result;

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
        mainVM.View!.MessageDialog(error, title ?? LocalizationSource.GetString("Common_Error")).WaitOnDispatcherFrame();

        if (SetConsoleText)
        {
            mainVM.CommandTextBoxText = error;
        }
    }

    public string? ScriptInputDialog(string title, string label, string defaultInput, string cancelText, string submitText, bool isMultiline, bool preventClose)
    {
        // TODO: cancelText, submitText, preventClose
        return mainVM.View!.TextBoxDialog(label, defaultInput, title: title, isMultiline: isMultiline).WaitOnDispatcherFrame();
    }

    public void ScriptMessage(string message)
    {
        mainVM.View!.MessageDialog(message, title: LocalizationSource.GetString("Msg_ScriptMessageTitle")).WaitOnDispatcherFrame();
    }

    public void ScriptOpenURL(string url)
    {
        mainVM.View!.LaunchUriAsync(new(url)).Wait();
    }

    public bool ScriptQuestion(string message)
    {
        return mainVM.View!.MessageDialog(message, LocalizationSource.GetString("Msg_ScriptQuestionTitle"), MessageWindow.Buttons.YesNo).WaitOnDispatcherFrame() == MessageWindow.Result.Yes;
    }

    public void ScriptWarning(string message)
    {
        mainVM.View!.MessageDialog(message, title: LocalizationSource.GetString("Msg_ScriptWarningTitle")).WaitOnDispatcherFrame();
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
        mainVM.CommandTextBoxText = message;
    }

    public string? SimpleTextInput(string title, string label, string defaultValue, bool allowMultiline, bool showDialog = true)
    {
        // TODO: showDialog
        return mainVM.View!.TextBoxDialog(label, defaultValue, title: title, isMultiline: allowMultiline).WaitOnDispatcherFrame();
    }

    public void SimpleTextOutput(string title, string label, string message, bool allowMultiline)
    {
        mainVM.View!.TextBoxDialog(label, message, title: title, isMultiline: allowMultiline, isReadOnly: true).WaitOnDispatcherFrame();
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
