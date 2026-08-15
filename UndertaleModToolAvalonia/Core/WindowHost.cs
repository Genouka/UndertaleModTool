using System;
using System.Threading.Tasks;
using Avalonia.Controls;
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
    /// Resolves the <see cref="Window"/> an interactive control belongs to, if any.
    /// Single-window platforms have no real window in the tree, so this can return null.
    /// </summary>
    public static Window? ResolveOwner(Control view)
        => view.FindLogicalAncestorOfType<Window>();

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
}