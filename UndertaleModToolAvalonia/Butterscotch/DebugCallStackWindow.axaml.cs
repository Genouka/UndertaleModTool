using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using UndertaleModRunner;

namespace UndertaleModToolAvalonia;

public partial class DebugCallStackWindow : Window
{
    readonly System.Collections.ObjectModel.ObservableCollection<StackFrameInfo> frames = new();

    public DebugCallStackWindow()
    {
        InitializeComponent();
        FrameList.ItemsSource = frames;

        ButterscotchDebugService service = ButterscotchDebugService.Instance;
        service.Updated += Refresh;
        Closed += (_, _) => service.Updated -= Refresh;
        Refresh();
    }

    void Refresh()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible)
                return;

            frames.Clear();
            foreach (StackFrameInfo frame in ButterscotchDebugService.Instance.GetCallStack())
                frames.Add(frame);
        });
    }

    async void FrameList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (FrameList.SelectedItem is not StackFrameInfo frame)
            return;

        e.Handled = true;
        await ButterscotchDebugService.Instance.OpenCodeEntry(frame.CodeIndex);
    }
}