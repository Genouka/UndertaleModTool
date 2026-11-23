using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.LogicalTree;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Abstraction over how windows and dialogs are shown. Desktop uses the default real-window
/// behavior. Single-window platforms (such as <c>UndertaleModToolAvalonia.Android</c>) replace
/// the handlers so that windows are simulated (modal stack + tabs) inside one real window/view.
/// </summary>
public static class WindowHost
{
    /// <summary>
    /// Replaced by single-window platforms. Receives (owner, dialog window) and produces the
    /// dialog's result value when it closes.
    /// </summary>
    public static Func<Window?, Window, Task<object?>>? ShowDialogHandler;

    /// <summary>
    /// Replaced by single-window platforms. Receives (owner, window).
    /// </summary>
    public static Action<Window?, Window>? ShowHandler;

    /// <summary>
    /// Replaced by single-window platforms. Receives a window to bring to the front; simulated
    /// window stacks are not native windows, so <see cref="Window.Activate"/> would do nothing.
    /// </summary>
    public static Action<Window>? FocusHandler;

    /// <summary>
    /// Optional factory for the main window, so desktop platforms can provide their own (e.g., a
    /// <c>MockStackWindow</c>) instead of the plain <c>MainWindow</c>.
    /// </summary>
    public static Func<MainViewModel, Window>? MainWindowFactory;

    /// <summary>
    /// Optional hook invoked for non-desktop lifetimes (e.g., Android) so the platform module can
    /// install the main view (e.g., a <c>MockStackWindow</c> as the single activity content).
    /// </summary>
    public static Action<MainViewModel>? InitializeAppHook;

    /// <summary>
    /// Resolves the <see cref="Window"/> a control is or belongs to, if any.
    /// Single-window platforms have no real window in the tree, so this can return null.
    /// </summary>
    public static Window? ResolveOwner(Control view)
        => view as Window ?? view.FindLogicalAncestorOfType<Window>();

    /// <summary>
    /// Resolves the <see cref="TopLevel"/> a control belongs to, falling back to the platform's
    /// root view when the control is currently detached from the visual tree. On single-window
    /// platforms (Android) the simulated window stack re-parents dialog content into its host,
    /// which temporarily detaches the main view, so plain <c>TopLevel.GetTopLevel</c> returns
    /// null even though a top-level exists.
    /// </summary>
    public static TopLevel? TryGetTopLevel(Control view)
    {
        if (TopLevel.GetTopLevel(view) is { } topLevel)
            return topLevel;

        return Application.Current?.ApplicationLifetime switch
        {
            IClassicDesktopStyleApplicationLifetime desktop => desktop.MainWindow,
            ISingleViewApplicationLifetime singleView => TopLevel.GetTopLevel(singleView.MainView),
            _ => null,
        };
    }

    /// <summary>Shows a dialog window, returning its typed result.</summary>
    public static async Task<TResult> ShowDialog<TResult>(Window? owner, Window dialog)
    {
        if (ShowDialogHandler is not null)
        {
            object? result = await ShowDialogHandler(owner, dialog);
            return result is TResult typedResult ? typedResult : default!;
        }

        return await dialog.ShowDialog<TResult>(owner ?? throw new InvalidOperationException(
            "No window host is installed and no owner window was found."));
    }

    /// <summary>Shows a dialog window without retrieving a result.</summary>
    public static async Task ShowDialog(Window? owner, Window dialog)
    {
        if (ShowDialogHandler is not null)
        {
            _ = await ShowDialogHandler(owner, dialog);
            return;
        }

        await dialog.ShowDialog(owner ?? throw new InvalidOperationException(
            "No window host is installed and no owner window was found."));
    }

    /// <summary>Shows a non-modal window.</summary>
    public static void Show(Window? owner, Window window)
    {
        if (ShowHandler is not null)
        {
            ShowHandler(owner, window);
            return;
        }

        window.Show(owner ?? throw new InvalidOperationException(
            "No window host is installed and no owner window was found."));
    }

    /// <summary>Brings a window to the front (or selects its page in a simulated window stack).</summary>
    public static void Focus(Window window)
    {
        if (FocusHandler is not null)
            FocusHandler(window);
        else
            window.Activate();
    }
using Avalonia.Input.Platform;
    /// <summary>
    /// Copies <paramref name="text"/> to the platform clipboard, if one is available.
    /// Clipboard access is optional on single-window/headless hosts and can be denied at runtime.
    /// </summary>
    public static async Task CopyToClipboard(Control view, string? text)
    {
        if (string.IsNullOrEmpty(text) || TryGetTopLevel(view)?.Clipboard is not { } clipboard)
            return;

        try
        {
            await clipboard.SetTextAsync(text);
        }
        catch (Exception)
        {
            // Copy is best-effort: a failed clipboard write must not fault the UI thread.
        }
    }

}