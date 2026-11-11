using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace UndertaleModToolAvalonia;

public partial class DebugOutputWindow : Window
{
    public DebugOutputWindow()
    {
        InitializeComponent();
        LogList.ItemsSource = ButterscotchDebugService.Instance.LogLines;

        ButterscotchDebugService service = ButterscotchDebugService.Instance;
        service.LogAdded += OnLogAdded;
        Closed += (_, _) => service.LogAdded -= OnLogAdded;
    }

    void OnLogAdded(string _)
    {
        Dispatcher.UIThread.Post(() =>
        {
            int count = LogList.ItemCount;
            if (count > 0)
                LogList.ScrollIntoView(count - 1);
        });
    }

    void ClearButton_Click(object? sender, RoutedEventArgs e)
        => ButterscotchDebugService.Instance.LogLines.Clear();

    void CopySelected_Click(object? sender, RoutedEventArgs e)
    {
        string[] lines = LogList.SelectedItems is { } selected
            ? selected.Cast<object>().Select(item => item?.ToString() ?? "").ToArray()
            : Array.Empty<string>();
        _ = CopyLines(lines);
    }

    void CopyAll_Click(object? sender, RoutedEventArgs e)
    {
        string[] lines = LogList.Items.Cast<object>().Select(item => item?.ToString() ?? "").ToArray();
        _ = CopyLines(lines);
    }

    async Task CopyLines(string[] lines)
    {
        if (lines.Length == 0)
            return;

        StringBuilder builder = new();
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                builder.AppendLine();
            builder.Append(lines[i]);
        }

        try
        {
            IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
                await clipboard.SetTextAsync(builder.ToString());
        }
        catch (Exception ex)
        {
            ButterscotchDebugService.Instance.Log("Could not copy output: " + ex.Message);
        }
    }
}