using System;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using UndertaleModLib;

namespace UndertaleModToolAvalonia;

public partial class App : Application
{
    public static string VersionString = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?.?.?.?";
    public static string InformationalVersionString = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "<unknown>";

    public static IServiceProvider Services = null!;
    public static IStyle? CurrentCustomStyles = null;

    public override void Initialize()
    {
        // Avalonia controls listen to collection notifications directly (they bypass the binding
        // engine, which already posts property changes to the UI thread), so lists mutated from a
        // non-UI thread - scripts run on a dedicated thread - would otherwise crash the data
        // grid/items controls with "The calling thread cannot access this object". Route every
        // notification through the dispatcher, synchronously, so the mutating thread waits until
        // the UI has processed each change and the collection state is consistent while it does.
        UndertaleListNotifications.Dispatcher = static action =>
        {
            if (Dispatcher.UIThread.CheckAccess())
                action();
            else
                Dispatcher.UIThread.Invoke(action);
        };

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Dependency injection.
        ServiceCollection collection = new();
        collection.AddSingleton<MainViewModel>();

        Services = collection.BuildServiceProvider();

        MainViewModel vm = Services.GetRequiredService<MainViewModel>();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (WindowHost.MainWindowFactory is not null)
            {
                desktop.MainWindow = WindowHost.MainWindowFactory(vm);
            }
            else
            {
                desktop.MainWindow = new MainWindow
                {
                    DataContext = vm,
                };
            }

            Window.WindowOpenedEvent.AddClassHandler(typeof(Window), (source, e) =>
            {
                if (source is Window window)
                {
                    bool isDark = (ActualThemeVariant == ThemeVariant.Dark);
                    window.SetDarkTitleBar(isDark);
                }
            });

            ActualThemeVariantChanged += (s, e) =>
            {
                bool isDark = (ActualThemeVariant == ThemeVariant.Dark);
                foreach (var window in desktop.Windows)
                {
                    window.SetDarkTitleBar(isDark);
                }
            };
        }
        else if (WindowHost.InitializeAppHook is not null)
        {
            // Single-window platforms (e.g., Android) install the simulated main window here.
            WindowHost.InitializeAppHook(vm);
        }

        base.OnFrameworkInitializationCompleted();

        // Start the asynchronous view-model startup only after the window/root view exists, so the
        // loading overlay can render while settings/styles are read off the UI thread.
        _ = vm.InitializeAsync();
    }
}
