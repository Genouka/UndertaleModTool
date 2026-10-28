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

    async void OpenLinkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string url })
            return;

        if (WindowHost.TryGetTopLevel(this) is { } topLevel)
            await topLevel.Launcher.LaunchUriAsync(new Uri(url));
    }

    void CloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
