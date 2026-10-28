using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Platform.Storage;

namespace UndertaleModToolAvalonia;

public interface IView
{
    private Control View => (Control)this;

    public async Task<IReadOnlyList<IStorageFile>> OpenFileDialog(FilePickerOpenOptions options)
    {
        if (WindowHost.TryGetTopLevel(View)?.StorageProvider is not { } storageProvider)
            return [];

        return await storageProvider.OpenFilePickerAsync(options);
    }

    public async Task<IStorageFile?> SaveFileDialog(FilePickerSaveOptions options)
    {
        if (WindowHost.TryGetTopLevel(View)?.StorageProvider is not { } storageProvider)
            return null;

        return await storageProvider.SaveFilePickerAsync(options);
    }

    public async Task<IReadOnlyList<IStorageFolder>> OpenFolderDialog(FolderPickerOpenOptions options)
    {
        if (WindowHost.TryGetTopLevel(View)?.StorageProvider is not { } storageProvider)
            return [];

        return await storageProvider.OpenFolderPickerAsync(options);
    }

    public async Task<bool> LaunchUriAsync(Uri uri)
    {
        // A view without a top-level (detached by a simulated window stack, or a headless
        // host) cannot launch anything; returning false keeps async event handlers from
        // faulting, which would surface as an unhandled exception on the UI thread.
        if (WindowHost.TryGetTopLevel(View) is not { } topLevel)
            return false;

        return await topLevel.Launcher.LaunchUriAsync(uri);
    }

    public async Task<MessageWindow.Result> MessageDialog(string message, string? title = null, MessageWindow.Buttons buttons = MessageWindow.Buttons.OK)
    {
        Window? window = WindowHost.ResolveOwner(View);
        return await WindowHost.ShowDialog<MessageWindow.Result>(window, new MessageWindow(message, title, buttons));
    }

    public async Task<string?> TextBoxDialog(string message, string text = "", string? title = null, bool isMultiline = false, bool isReadOnly = false)
    {
        Window? window = WindowHost.ResolveOwner(View);
        return await WindowHost.ShowDialog<string?>(window, new TextBoxWindow(message, text, title, isMultiline, isReadOnly));
    }

    public ILoaderWindow LoaderOpen()
    {
        Window? window = WindowHost.ResolveOwner(View);
        LoaderWindow loaderWindow = new();
        loaderWindow.ShowDelayed(window);
        return loaderWindow;
    }

    public IInputElement? GetFocusedElement()
        => WindowHost.TryGetTopLevel(View)?.FocusManager?.GetFocusedElement();
}