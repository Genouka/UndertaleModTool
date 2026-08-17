using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace UndertaleModToolAvalonia.Android;

/// <summary>
/// Simulates multiple windows on single-window platforms such as Android.
/// <para>
/// Avalonia on Android only hosts a single window, so the real <see cref="Window"/> objects opened
/// by the app cannot be shown as actual windows. This control hosts each window's rendered content
/// instead:
/// <list type="bullet">
/// <item><b>Modal windows</b> (<c>ShowDialog</c>) are pushed onto an Activity-style stack. The top
/// of the stack covers the UI; the system back button (or the header back button) pops it and the
/// underlying window's result value is returned to the awaiting caller.</item>
/// <item><b>Non-modal windows</b> (<c>Show</c>) become tabs in the tab bar, and the user can switch
/// between the simulated windows at any time.</item>
/// </list>
/// </para>
/// </summary>
public class MockStackWindow : UserControl
{
    sealed class MockPage
    {
        public required Window? Window;
        public required bool IsModal;
        public required Control? Content;
        public string? Title;
        public TaskCompletionSource<object?>? Result;
        public bool IsSelected;

        /// <summary>
        /// The window's current title, read live so that <see cref="Window.Title"/> changes made
        /// after the window was opened are reflected on the tab and the modal header.
        /// </summary>
        public string EffectiveTitle =>
            Window is { } w && !string.IsNullOrWhiteSpace(w.Title)
                ? w.Title
                : !string.IsNullOrWhiteSpace(Title)
                    ? Title!
                    : "UndertaleModToolAvalonia";
    }

    static readonly FieldInfo s_dialogResultField =
        typeof(Window).GetField("_dialogResult", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Avalonia 'Window._dialogResult' field not found.");

    readonly MainViewModel mainVM;

    readonly List<MockPage> nonModalPages = [];
    readonly List<MockPage> modalStack = [];

    MockPage? selectedPage;

    MainView? mainView;

    // UI
    StackPanel tabPanel = null!;
    TextBlock captionLabel = null!;
    ContentControl activeContentHost = null!;
    Panel modalOverlay = null!;

    // Simulated-window chrome whose colors track the current theme (see ApplyThemeColors).
    Border headerBorder = null!;
    Border modalSurface = null!;
    Border modalHeader = null!;
    TextBlock modalTitle = null!;
    ContentControl modalContentHost = null!;

    public MockStackWindow(MainViewModel mainVM)
    {
        this.mainVM = mainVM;

        BuildVisualTree();
        Reset();

        // The hard-coded dark background used to make the app stay black even in light mode.
        // Re-derive all chrome colors from the active theme variant from now on.
        ApplyThemeColors();
        ActualThemeVariantChanged += (_, _) => ApplyThemeColors();
    }

    /// <summary>The simulated main page (the actual UndertaleModTool UI).</summary>
    public MainView MainView => mainView!;

    /// <summary>Number of simulated windows currently open on top of the main view.</summary>
    public int OpenWindowCount => nonModalPages.Count + modalStack.Count;

    void BuildVisualTree()
    {
        Grid root = new()
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
            },
        };

        root.Children.Add(BuildHeader());

        activeContentHost = new ContentControl();
        Grid.SetRow(activeContentHost, 1);
        root.Children.Add(activeContentHost);

        modalOverlay = new Panel
        {
            IsHitTestVisible = false,
            IsVisible = false,
        };
        Grid.SetRowSpan(modalOverlay, 2);
        root.Children.Add(modalOverlay);
        BuildModalOverlay();

        Content = root;
    }

    Control BuildHeader()
    {
        Border border = new()
        {
            Padding = new Thickness(8, 4),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        headerBorder = border;

        DockPanel dock = new();
        border.Child = dock;

        captionLabel = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        DockPanel.SetDock(captionLabel, Dock.Right);
        dock.Children.Add(captionLabel);

        tabPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ScrollViewer tabScroller = new()
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = tabPanel,
        };
        dock.Children.Add(tabScroller);

        return border;
    }

    void BuildModalOverlay()
    {
        Border overlay = new();
        modalSurface = overlay;

        DockPanel dock = new();

        modalHeader = new Border
        {
            Padding = new Thickness(8, 4),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        Grid grid = new()
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
        };
        modalHeader.Child = grid;

        Button backButton = new()
        {
            Content = "\u2190", // �?            FontSize = 16,
            Classes = { "accent" },
        };
        backButton.Click += (_, _) => PopTopModal();
        Grid.SetColumn(backButton, 0);
        grid.Children.Add(backButton);

        modalTitle = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(modalTitle, 1);
        grid.Children.Add(modalTitle);

        Button closeButton = new()
        {
            Content = "\u2715", // �?            FontSize = 14,
        };
        closeButton.Click += (_, _) => PopTopModal();
        Grid.SetColumn(closeButton, 2);
        grid.Children.Add(closeButton);

        DockPanel.SetDock(modalHeader, Dock.Top);
        dock.Children.Add(modalHeader);

        modalContentHost = new ContentControl();
        dock.Children.Add(modalContentHost);

        overlay.Child = dock;
        modalOverlay.Children.Add(overlay);
    }

    void Reset()
    {
        nonModalPages.Clear();
        modalStack.Clear();

        mainView ??= new MainView { DataContext = mainVM };

        MockPage mainPage = new()
        {
            Window = null,
            Title = mainVM.Title,
            IsModal = false,
            Content = mainView,
        };

        nonModalPages.Add(mainPage);
        selectedPage = mainPage;

        UpdateStack();
    }

    /// <summary>
    /// Shows a dialog window and completes when it is dismissed, producing its result value.
    /// </summary>
    public Task<object?> ShowDialogAsync(Window? owner, Window dialog)
    {
        TaskCompletionSource<object?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PushModalPage(dialog, tcs);
        return tcs.Task;
    }

    /// <summary>Shows a non-modal window as a tab in the simulated window stack.</summary>
    public void Show(Window? owner, Window window)
    {
        PushPage(window, isModal: false);
    }

    void PushModalPage(Window window, TaskCompletionSource<object?> result)
    {
        MockPage page = CreatePage(window, isModal: true);
        page.Result = result;

        window.Closing += (_, _) => CloseModalPage(page);
        modalStack.Add(page);

        UpdateStack();
    }

    void PushPage(Window window, bool isModal)
    {
        MockPage page = CreatePage(window, isModal);

        if (isModal)
        {
            window.Closing += (_, _) => RemovePage(page);
            modalStack.Add(page);
        }
        else
        {
            window.Closing += (_, _) => RemovePage(page);
            nonModalPages.Add(page);
            selectedPage = page;
        }

        UpdateStack();
    }

    MockPage CreatePage(Window window, bool isModal)
    {
        Control? content = window.Content as Control;

        // The content may have only *inherited* its DataContext from the window. Once it is
        // re-parented into this host (whose DataContext is null) that inheritance is broken, which
        // would leave every control inside the window without a DataContext and thus unresponsive
        // (bound actions never fire). Propagate the window's DataContext explicitly so the bindings
        // keep working after the content moves into the mock window stack.
        if (content is not null && window.DataContext is not null)
            content.DataContext = window.DataContext;

        MockPage page = new()
        {
            Window = window,
            IsModal = isModal,
            Content = content ?? new TextBlock
            {
                Text = window.Title ?? "",
                Margin = new Thickness(16),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        // Keep the tab title / modal header in sync with the window title at runtime.
        if (window is not null)
            window.PropertyChanged += OnPageWindowPropertyChanged;

        return page;
    }

    void OnPageWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.TitleProperty)
            UpdateStack();
    }

    void CloseModalPage(MockPage page)
    {
        if (!modalStack.Remove(page))
            return;

        TaskCompletionSource<object?>? result = page.Result;
        page.Result = null;

        result?.TrySetResult(page.Window is { } window ? GetDialogResult(window) : null);
        UpdateStack();
    }

    void RemovePage(MockPage page)
    {
        nonModalPages.Remove(page);
        modalStack.Remove(page);

        if (ReferenceEquals(selectedPage, page))
        {
            selectedPage = nonModalPages.Count > 0 ? nonModalPages[0] : null;
        }

        UpdateStack();
    }

    /// <summary>Selects a non-modal simulated window tab.</summary>
    void SelectPage(MockPage page)
    {
        if (!nonModalPages.Contains(page))
            return;

        selectedPage = page;
        UpdateStack();
    }

    /// <summary>
    /// Pops the top modal simulated window, returning default result. Called by the system back
    /// button or the header close/back buttons.
    /// </summary>
    public void PopTopModal()
    {
        if (modalStack.Count == 0)
            return;

        MockPage top = modalStack[^1];
        top.Window?.Close();
    }

    /// <summary>
    /// Activity-like back navigation: pops the topmost modal window if any; otherwise returns to the
    /// main page; otherwise shuts the application down.
    /// </summary>
    public void HandleBack()
    {
        if (modalStack.Count > 0)
        {
            PopTopModal();
            return;
        }

        if (selectedPage is not null && !ReferenceEquals(selectedPage, nonModalPages[0]))
        {
            SelectPage(nonModalPages[0]);
            return;
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    void UpdateStack()
    {
        MockPage? active = modalStack.Count > 0 ? modalStack[^1] : selectedPage;
        Control? activeContent = active?.Content;

        activeContentHost.Content = modalStack.Count == 0 ? activeContent : null;

        bool showModal = modalStack.Count > 0;
        modalOverlay.IsVisible = showModal;
        modalOverlay.IsHitTestVisible = showModal;

        if (showModal && active is not null)
        {
            modalTitle.Text = active.EffectiveTitle;
            modalContentHost.Content = active.Content;
        }
        else
        {
            modalContentHost.Content = null;
        }

        RebuildTabs();

        if (modalStack.Count > 0)
            captionLabel.Text = $"{modalStack.Count} active dialog(s)";
        else
            captionLabel.Text = "";
    }

    void RebuildTabs()
    {
        tabPanel.Children.Clear();

        for (int i = 0; i < nonModalPages.Count; i++)
        {
            MockPage page = nonModalPages[i];
            bool isSelected = ReferenceEquals(page, selectedPage) && modalStack.Count == 0;
            page.IsSelected = isSelected;

            Button tab = new()
            {
                Content = page.EffectiveTitle,
                Margin = new Thickness(0, 0, 4, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            if (isSelected)
                tab.Classes.Add("accent");

            MockPage captured = page;
            tab.Click += (_, _) => SelectPage(captured);
            tabPanel.Children.Add(tab);

            if (i > 0)
            {
                Button close = new()
                {
                    Content = "\u2715",
                    Margin = new Thickness(-2, 0, 6, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                close.Click += (_, _) => captured.Window?.Close();
                tabPanel.Children.Add(close);
            }
        }
    }

    /// <summary>
    /// Repaints the simulated-window chrome to match the active theme variant (light/dark).
    /// The app used to paint a hard-coded dark background here, which made the Android UI stay
    /// black even when the light theme was selected. The colors are now taken from the same
    /// Avalonia Fluent theme brushes the regular (desktop) windows use, so the single Android
    /// window surface follows the theme like everything else.
    /// </summary>
    void ApplyThemeColors()
    {
        bool isDark = ActualThemeVariant != ThemeVariant.Light;

        SolidColorBrush? Resolve(string key)
        {
            if (App.Current is { } app &&
                app.TryFindResource(key, ActualThemeVariant, out object? value) &&
                value is SolidColorBrush brush)
            {
                return brush;
            }

            return null;
        }

        // App surface: the Fluent theme's window-surface brush (white in light mode,
        // black in dark mode). Falls back to equivalents of the old hard-coded color.
        SolidColorBrush surfaceBrush = Resolve("SystemControlBackgroundAltHighBrush")
            ?? new SolidColorBrush(isDark ? Color.FromRgb(32, 32, 32) : Colors.White);
        Background = surfaceBrush;

        // Hairline separators: translucent foreground ink (dark theme → white, light → black).
        headerBorder.BorderBrush = new SolidColorBrush(
            isDark ? Color.FromArgb(32, 255, 255, 255) : Color.FromArgb(32, 0, 0, 0));
        modalHeader.BorderBrush = new SolidColorBrush(
            isDark ? Color.FromArgb(48, 255, 255, 255) : Color.FromArgb(48, 0, 0, 0));

        // Secondary caption text ("N active dialog(s)").
        captionLabel.Foreground = Resolve("SystemControlForegroundBaseMediumBrush")
            ?? new SolidColorBrush(isDark ? Color.FromRgb(180, 180, 180) : Color.FromRgb(90, 90, 90));

        // Surface behind modal dialogs: a slightly contrasting chrome surface.
        modalSurface.Background = Resolve("SystemControlBackgroundChromeMediumLowBrush")
            ?? new SolidColorBrush(isDark ? Color.FromRgb(26, 26, 26) : Color.FromRgb(245, 245, 245));

        // Keep the Android status bar the same color as the app surface. Avalonia applies
        // SystemBarColor to the native status bar on Android (see TopLevel.SystemBarColor).
        if (TopLevel.GetTopLevel(this) is { } topLevel)
            TopLevel.SetSystemBarColor(topLevel, surfaceBrush);
    }

    /// <summary>
    /// Reads the result value that was passed to <see cref="Window.Close(object?)"/>.
    /// The field is not exposed publicly by Avalonia, so it is read reflectively here.
    /// </summary>
    static object? GetDialogResult(Window window)
    {
        try
        {
            return s_dialogResultField.GetValue(window);
        }
        catch
        {
            return null;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // The top-level (and the native status bar) is only reachable once attached;
        // re-apply so the system bar color is pushed to the platform.
        ApplyThemeColors();

        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not null)
            topLevel.BackRequested += OnBackRequested;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not null)
            topLevel.BackRequested -= OnBackRequested;
    }

    void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        HandleBack();
    }
}