using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using UndertaleModTool.Localization;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Non-modal debugger control panel. Opened while a Butterscotch session is active; stays open
/// afterwards so the session can be restarted.
/// </summary>
public partial class DebugPanelWindow : Window
{
    static readonly IBrush RunningBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43));
    static readonly IBrush PausedBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xC0, 0x20));
    static readonly IBrush StoppedBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));

    public DebugPanelWindow()
    {
        InitializeComponent();

        Opened += (_, _) =>
        {
            ButterscotchDebugService service = Service;
            service.Updated += UpdateUi;
            service.StatusUpdated += UpdateUi;
            UpdateUi();
        };
        Closed += (_, _) =>
        {
            ButterscotchDebugService service = Service;
            service.Updated -= UpdateUi;
            service.StatusUpdated -= UpdateUi;
        };
    }

    static ButterscotchDebugService Service => ButterscotchDebugService.Instance;

    void UpdateUi()
    {
        ButterscotchDebugService service = Service;
        bool active = service.IsActive;
        bool paused = service.IsPaused;
        bool running = active && !paused;

        StatusDot.Fill = active ? (paused ? PausedBrush : RunningBrush) : StoppedBrush;

        if (!active)
        {
            StatusText.Text = LocalizationSource.GetString("Debug_Panel_Status_NoSession");
            DetailText.IsVisible = false;
        }
        else if (paused)
        {
            string frameBoundary = LocalizationSource.GetString("Debug_Panel_Status_FrameBoundary");
            string codeName = service.State.Stop?.Frame?.CodeName ?? frameBoundary;
            StatusText.Text = string.Format(
                LocalizationSource.GetString("Debug_Panel_Status_Paused"),
                service.State.FrameCount,
                codeName);
            DetailText.Text = codeName;
            DetailText.IsVisible = true;
        }
        else
        {
            StatusText.Text = string.Format(
                LocalizationSource.GetString("Debug_Panel_Status_Running"),
                service.State?.FrameCount ?? 0);
            DetailText.IsVisible = false;
        }

        ContinueButton.IsEnabled = paused;
        PauseButton.IsEnabled = running;
        StepIntoButton.IsEnabled = paused;
        StepOverButton.IsEnabled = paused;
        StepOutButton.IsEnabled = paused;
        StepFrameButton.IsEnabled = paused;
        StopButton.IsEnabled = active;
        HotReloadButton.IsEnabled = active && paused;
        BreakpointsButton.IsEnabled = active;
        VariablesButton.IsEnabled = active;
        InstancesButton.IsEnabled = active;
        CallStackButton.IsEnabled = active;
        OutputButton.IsEnabled = true;
    }

    void ContinueButton_Click(object? sender, RoutedEventArgs e) => Service.Resume();
    void PauseButton_Click(object? sender, RoutedEventArgs e) => Service.Pause();
    void StepIntoButton_Click(object? sender, RoutedEventArgs e) => Service.StepInto();
    void StepOverButton_Click(object? sender, RoutedEventArgs e) => Service.StepOver();
    void StepOutButton_Click(object? sender, RoutedEventArgs e) => Service.StepOut();
    void StepFrameButton_Click(object? sender, RoutedEventArgs e) => Service.StepFrame();

    void StopButton_Click(object? sender, RoutedEventArgs e)
    {
        Service.Stop();
        UpdateUi();
    }

    void HotReloadButton_Click(object? sender, RoutedEventArgs e) => Service.HotReloadCurrentTab();

    void BreakpointsButton_Click(object? sender, RoutedEventArgs e) => ShowWindow(new DebugBreakpointsWindow());
    void VariablesButton_Click(object? sender, RoutedEventArgs e) => ShowWindow(new DebugVariablesWindow());
    void InstancesButton_Click(object? sender, RoutedEventArgs e) => ShowWindow(new DebugInstancesWindow());
    void CallStackButton_Click(object? sender, RoutedEventArgs e) => ShowWindow(new DebugCallStackWindow());
    void OutputButton_Click(object? sender, RoutedEventArgs e) => ShowWindow(new DebugOutputWindow());

    void ShowWindow(Window window)
        => WindowHost.Show(WindowHost.ResolveOwner(this), window);
}