using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;

namespace UndertaleModToolAvalonia.Android;

/// <summary>
/// Installs the Android window simulation into the shared <see cref="WindowHost"/> and wires the
/// single Avalonia view lifetime to a <see cref="MockStackWindow"/>.
/// </summary>
public static class AndroidWindowHost
{
    /// <summary>The simulated multi-window host installed as the single Android view.</summary>
    public static MockStackWindow? Host { get; private set; }

    /// <summary>
    /// Must be called before <see cref="UndertaleModToolAvalonia.App.OnFrameworkInitializationCompleted"/>
    /// runs (e.g., from the application's <c>OnCreate</c>).
    /// </summary>
    public static void Install()
    {
        WindowHost.InitializeAppHook = Initialize;
    }

    static void Initialize(MainViewModel vm)
    {
        // Replace Avalonia.Android's windowing stub with inert windows that are never shown. Their
        // rendered content is hosted by MockStackWindow, so modal dialogs and extra windows work
        // even though Android only supports a single real window.
        AvaloniaLocator.CurrentMutable
            .Bind<IWindowingPlatform>().ToConstant(new SimulatedWindowingPlatform());

        MockStackWindow host = new(vm);
        Host = host;

        WindowHost.ShowDialogHandler = (owner, dialog) => host.ShowDialogAsync(owner, dialog);
        WindowHost.ShowHandler = (owner, window) => host.Show(owner, window);

        if (Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = host;
        }
    }
}