using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace UndertaleModToolAvalonia;

public partial class ContributorsWindow : Window
{
    public ContributorsWindow()
    {
        InitializeComponent();
    }

    void OpenLinkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string url })
            return;

        if (WindowHost.TryGetTopLevel(this) is { } topLevel)
            // Fire-and-forget: an async void click handler must not fault the UI thread if
            // launching the URI fails (see IView.LaunchUriAsync for the same rationale).
            _ = topLevel.Launcher.LaunchUriAsync(new Uri(url));
    }

    void CloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
