using System;
using System.Windows;
using System.Windows.Media;
using UndertaleModTool.Localization;

namespace UndertaleModTool.Debugging
{
    /// <summary>
    /// Non-modal debugger control panel. Opened while a Butterscotch session is
    /// active; stays open afterwards so the session can be restarted.
    /// </summary>
    public partial class DebugPanelWindow : Window
    {
        private static readonly Brush RunningBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43));
        private static readonly Brush PausedBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xC0, 0x20));
        private static readonly Brush StoppedBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));


        public DebugPanelWindow()
        {
            InitializeComponent();
            Loaded += DebugPanelWindow_Loaded;
            Unloaded += DebugPanelWindow_Unloaded;
            Closed += (_, _) => DebugPanelWindow_Unloaded(this, null);
        }

        private void DebugPanelWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (Settings.Instance.EnableDarkMode)
                MainWindow.SetDarkTitleBarForWindow(this, true, false);

            if (DebugSessionService.Instance is { } service)
                service.Updated += UpdateUi;
            UpdateUi();
        }

        private void DebugPanelWindow_Unloaded(object sender, RoutedEventArgs e)
        {
            if (DebugSessionService.Instance is { } service)
                service.Updated -= UpdateUi;
        }

        private DebugSessionService Service => DebugSessionService.Instance;

        private void UpdateUi()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(UpdateUi);
                return;
            }

            DebugSessionService service = Service;
            bool active = service?.IsActive == true;
            bool paused = service?.IsPaused == true;
            bool running = active && !paused;

            StatusDot.Fill = active ? (paused ? PausedBrush : RunningBrush) : StoppedBrush;

            if (!active)
            {
                StatusText.Text = LocalizationSource.GetString("Debug_Panel_Status_NoSession");
                DetailText.Visibility = Visibility.Collapsed;
            }
            else if (paused)
            {
                StatusText.Text = string.Format(
                    LocalizationSource.GetString("Debug_Panel_Status_Paused"),
                    service.State.FrameCount,
                    service.State.Stop?.Frame?.CodeName
                        ?? LocalizationSource.GetString("Debug_Panel_Status_FrameBoundary"));
                DetailText.Text = service.State.Stop?.Frame?.CodeName
                    ?? LocalizationSource.GetString("Debug_Panel_Status_FrameBoundary");
                DetailText.Visibility = Visibility.Visible;
            }
            else
            {
                StatusText.Text = string.Format(
                    LocalizationSource.GetString("Debug_Panel_Status_Running"),
                    service.State?.FrameCount ?? 0);
                DetailText.Visibility = Visibility.Collapsed;
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

        private void ContinueButton_Click(object sender, RoutedEventArgs e) => Service?.Resume();
        private void PauseButton_Click(object sender, RoutedEventArgs e) => Service?.Pause();
        private void StepIntoButton_Click(object sender, RoutedEventArgs e) => Service?.StepInto();
        private void StepOverButton_Click(object sender, RoutedEventArgs e) => Service?.StepOver();
        private void StepOutButton_Click(object sender, RoutedEventArgs e) => Service?.StepOut();
        private void StepFrameButton_Click(object sender, RoutedEventArgs e) => Service?.StepFrame();

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            Service?.Stop();
            UpdateUi();
        }

        private void HotReloadButton_Click(object sender, RoutedEventArgs e) => Service?.HotReloadCurrentTab();

        private void BreakpointsButton_Click(object sender, RoutedEventArgs e) =>
            ShowWindow(() => new DebugBreakpointsWindow());

        private void VariablesButton_Click(object sender, RoutedEventArgs e) =>
            ShowWindow(() => new DebugVariablesWindow());

        private void InstancesButton_Click(object sender, RoutedEventArgs e) =>
            ShowWindow(() => new DebugInstancesWindow());

        private void CallStackButton_Click(object sender, RoutedEventArgs e) =>
            ShowWindow(() => new DebugCallStackWindow());

        private void OutputButton_Click(object sender, RoutedEventArgs e) =>
            ShowWindow(() => new DebugOutputWindow());

        private void ShowWindow<T>(Func<T> factory) where T : Window
        {
            T window = factory();
            window.Owner = Owner ?? Window.GetWindow(this);
            window.Show();
        }
    }
}
