using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using AvaloniaEdit.Rendering;
using UndertaleModLib;
using UndertaleModLib.Compiler;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;
using UndertaleModTool.Localization;
using Underanalyzer.Decompiler;
using Underanalyzer.Decompiler.AST;
using Underanalyzer.Decompiler.GameSpecific;
using static UndertaleModToolAvalonia.UndertaleCodeViewModel;

namespace UndertaleModToolAvalonia;

public partial class UndertaleCodeView : UserControl
{
    static IHighlightingDefinition? GMLHighlightingDefinitionDark = null;
    static IHighlightingDefinition? GMLHighlightingDefinitionLight = null;
    static IHighlightingDefinition? ASMHighlightingDefinitionDark = null;
    static IHighlightingDefinition? ASMHighlightingDefinitionLight = null;
    static uint HighlightingMajorVersion = 0;

    static readonly Dictionary<string, UndertaleNamedResource> ScriptsCache = new();
    static readonly Dictionary<string, UndertaleNamedResource> FunctionsCache = new();
    static readonly Dictionary<string, UndertaleNamedResource> CodeCache = new();
    static readonly Dictionary<string, UndertaleNamedResource> NamedResourcesCache = new();

    readonly List<string> codeLocalsCache = new();

    readonly NumberGenerator gmlNumberGenerator;
    readonly NameGenerator gmlNameGenerator;
    readonly NameGenerator asmNameGenerator;

    (TextLocation, TextLocation) lastCaretLocations;

    #region Touch

    const double CodeEditorFontSizeMin = 8;
    const double CodeEditorFontSizeMax = 36;
    static readonly TimeSpan CodeEditorLongPressDuration = TimeSpan.FromMilliseconds(450);
    static readonly TimeSpan CodeEditorKeyboardCheckDelay = TimeSpan.FromMilliseconds(150);
    const double CodeEditorTouchMoveThreshold = 12;

    readonly Dictionary<long, Point> codeEditorTouchPoints = new();
    TextEditor? codeEditorTouchEditor;
    long? codeEditorTouchPrimaryId;
    long? codeEditorTouchSecondaryId;
    Point codeEditorTouchStartPosition;      // relative to the editor (viewport)
    Point codeEditorTouchStartTextViewPosition; // relative to the TextView (content)
    bool codeEditorTouchPinching;
    bool codeEditorTouchLongPressFired;
    bool codeEditorTouchScrolled;
    bool codeEditorTouchSelecting;           // a long-press selection is being extended by drag
    int codeEditorSelectionAnchorOffset = -1;
    double codeEditorPinchStartDistance;
    double codeEditorPinchStartFontSize;
    DispatcherTimer? codeEditorLongPressTimer;
    DispatcherTimer? codeEditorKeyboardCheckTimer;

    #endregion

    // Change tracking
    readonly ModifiedLinesBackgroundRenderer _gmlModifiedRenderer = new();
    readonly ModifiedLinesBackgroundRenderer _asmModifiedRenderer = new();

    // Diagnostics
    GmlDiagnosticsRenderer? _diagnosticsRenderer;
    DispatcherTimer? _diagnosticsTimer;
    CancellationTokenSource? _diagnosticsCancellation;
    int _diagnosticsGeneration;
    bool _isLoadingCode;

    // Folding
    DispatcherTimer? _foldingTimer;
    readonly GmlFoldingStrategy _foldingStrategy = new();
    FoldingManager? _foldingManager;

    // Completion
    CompletionWindow? _completionWindow;

    // Hover
    Popup? _hoverPopup;
    DispatcherTimer? _hoverTimer;
    TextEditor? _hoverEditor;
    int _hoverPendingOffset = -1;
    int _hoverSectionStart = -1;
    int _hoverSectionLength = 0;
    int _lastHoverOffset = -1;
    int _pressSuppressedOffset = -1;
    const int HoverDelayMs = 250;

    // Results panel
    enum ResultMode
    {
        None,
        Errors,
        References
    }
    ResultMode _lastResultMode = ResultMode.None;
    bool _panelManuallyClosed;
    readonly List<CodeEditorResultEntry> _resultEntries = new();
    TabState _lastGMLTabState = TabState.NeedsDecompile;
    TabState _lastASMTabState = TabState.NeedsDecompile;

    private static readonly Dictionary<Color, Color> LightSyntaxColorMap = new()
    {
        [Color.FromRgb(0x5B, 0x99, 0x5B)] = Color.FromRgb(0x00, 0x80, 0x00),   // Comment
        [Colors.Yellow] = Color.FromRgb(0xA3, 0x15, 0x15),                     // String
        [Color.FromRgb(0xC0, 0xC0, 0xC0)] = Color.FromRgb(0x7A, 0x7A, 0x7A),   // TemplateStringField
        [Color.FromRgb(0xB2, 0xB1, 0xFF)] = Color.FromRgb(0x1F, 0x37, 0x7F),   // GML Identifier/Function
        [Color.FromRgb(0xFF, 0xF8, 0x99)] = Color.FromRgb(0x9A, 0x67, 0x00),   // AltIdentifier
        [Color.FromRgb(0xFF, 0x64, 0x64)] = Color.FromRgb(0x09, 0x86, 0x58),   // Number
        [Color.FromRgb(0xF9, 0xB4, 0x6F)] = Color.FromRgb(0x00, 0x00, 0xFF),   // GML keywords
        [Color.FromRgb(0xFF, 0x80, 0x80)] = Color.FromRgb(0xC0, 0x00, 0x00),   // Macros
        [Color.FromRgb(0xC1, 0xC1, 0xC1)] = Color.FromRgb(0x33, 0x33, 0x33),   // VMASM Identifier
        [Color.FromRgb(0x80, 0xA8, 0xFF)] = Color.FromRgb(0x00, 0x00, 0xFF),   // VMASM BranchOpcode
        [Color.FromRgb(0xDA, 0xDA, 0xDA)] = Color.FromRgb(0x33, 0x33, 0x33),   // VMASM Opcode
        [Color.FromRgb(0xFF, 0xB8, 0x71)] = Color.FromRgb(0x79, 0x5E, 0x26),   // VMASM Function/InternalFunction
        [Color.FromRgb(0xFF, 0x8D, 0x0A)] = Color.FromRgb(0xC0, 0x50, 0x00),   // VMASM Label
        [Color.FromRgb(0xE0, 0xB0, 0xB0)] = Color.FromRgb(0x70, 0x70, 0x70),   // VMASM addresses
        [Color.FromRgb(0x59, 0xC2, 0x59)] = Color.FromRgb(0x2E, 0x7D, 0x32),   // VMASM various
    };

    // Tinted palette brushes for hover tooltips (matches the custom app color scheme).
    static readonly SolidColorBrush HoverTextDark = new(Color.FromRgb(0xE6, 0xE8, 0xF0));
    static readonly SolidColorBrush HoverTextLight = new(Color.FromRgb(0x28, 0x24, 0x19));
    static readonly SolidColorBrush HoverSubTextDark = new(Color.FromRgb(0xA6, 0xAB, 0xC0));
    static readonly SolidColorBrush HoverSubTextLight = new(Color.FromRgb(0x6F, 0x69, 0x5B));

    public bool IsDarkTheme => ActualThemeVariant != ThemeVariant.Light;

    public UndertaleCodeView()
    {
        InitializeComponent();

        gmlNumberGenerator = new(this);
        gmlNameGenerator = new(this);
        asmNameGenerator = new(this);

        InitializeTextEditor(GMLTextEditor);
        InitializeTextEditor(ASMTextEditor);

        InitializeTextEditorTouchHandling(GMLTextEditor);
        InitializeTextEditorTouchHandling(ASMTextEditor);

        GMLTextEditor.TextArea.GotFocus += GMLTextEditor_GotFocus;
        ASMTextEditor.TextArea.GotFocus += ASMTextEditor_GotFocus;

        GMLTextEditor.TextArea.LostFocus += GMLTextEditor_LostFocus;
        ASMTextEditor.TextArea.LostFocus += ASMTextEditor_LostFocus;

        // Change tracking renderers
        if (Settings?.ChangeTrackingEnabled ?? true)
        {
            GMLTextEditor.TextArea.TextView.BackgroundRenderers.Add(_gmlModifiedRenderer);
            ASMTextEditor.TextArea.TextView.BackgroundRenderers.Add(_asmModifiedRenderer);
        }

        // Folding for the GML editor
        _foldingManager = FoldingManager.Install(GMLTextEditor.TextArea);
        _foldingTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _foldingTimer.Tick += (s, e) => { _foldingTimer.Stop(); UpdateFolding(); };

        // Diagnostics for the GML editor
        _diagnosticsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(600)
        };
        _diagnosticsTimer.Tick += (s, e) => { _diagnosticsTimer.Stop(); _ = RunDiagnosticsAsync(); };
        _diagnosticsRenderer = new GmlDiagnosticsRenderer(GMLTextEditor.TextArea.TextView);
        GMLTextEditor.TextArea.TextView.BackgroundRenderers.Add(_diagnosticsRenderer);

        InitializeHoverPopup();
        InitializeCompletion();

        // Keyboard commands
        GMLTextEditor.KeyDown += Editor_KeyDown;
        ASMTextEditor.KeyDown += Editor_KeyDown;

        Loaded += UndertaleCodeView_Loaded;
        Unloaded += UndertaleCodeView_Unloaded;
        ActualThemeVariantChanged += (s, e) => ApplyAllThemes();
    }

    public SettingsFile? Settings => (DataContext as UndertaleCodeViewModel)?.MainVM.Settings;

    private void UndertaleCodeView_Loaded(object? sender, RoutedEventArgs e)
    {
        ApplySettingsToEditors();
        ApplyAllThemes();
    }

    private void UndertaleCodeView_Unloaded(object? sender, RoutedEventArgs e)
    {
        _diagnosticsCancellation?.Cancel();
        _diagnosticsTimer?.Stop();
        _foldingTimer?.Stop();
        StopCodeEditorKeyboardCheckTimer();
        CloseCompletionWindow();
        CloseHoverPopup();
    }

    private void ApplyAllThemes()
    {
        if (GMLTextEditor is null)
            return;
        bool isDark = IsDarkTheme;
        ApplySyntaxThemeToEditors(isDark);
        ApplyEditorTheme(isDark);
        ApplyResultsPanelTheme(isDark);
        CompletionItemStyle.IsDark = isDark;
    }

    private void ApplySyntaxThemeToEditors(bool isDark)
    {
        var vm = DataContext as UndertaleCodeViewModel;
        if (vm?.MainVM.Settings is null)
            return;

        if (vm.MainVM.Settings.EnableSyntaxHighlighting)
        {
            GMLTextEditor.SyntaxHighlighting = GetHighlightingDefinition("GML", isDark);
            ASMTextEditor.SyntaxHighlighting = GetHighlightingDefinition("ASM", isDark);

            if (!GMLTextEditor.TextArea.TextView.ElementGenerators.Contains(gmlNumberGenerator))
                GMLTextEditor.TextArea.TextView.ElementGenerators.Add(gmlNumberGenerator);
            if (!GMLTextEditor.TextArea.TextView.ElementGenerators.Contains(gmlNameGenerator))
                GMLTextEditor.TextArea.TextView.ElementGenerators.Add(gmlNameGenerator);
            if (!ASMTextEditor.TextArea.TextView.ElementGenerators.Contains(asmNameGenerator))
                ASMTextEditor.TextArea.TextView.ElementGenerators.Add(asmNameGenerator);
        }
        else
        {
            GMLTextEditor.SyntaxHighlighting = null;
            ASMTextEditor.SyntaxHighlighting = null;
            GMLTextEditor.TextArea.TextView.ElementGenerators.Remove(gmlNumberGenerator);
            GMLTextEditor.TextArea.TextView.ElementGenerators.Remove(gmlNameGenerator);
            ASMTextEditor.TextArea.TextView.ElementGenerators.Remove(asmNameGenerator);
        }
    }

    static IHighlightingDefinition GetHighlightingDefinition(string name, bool isDark)
    {
        if (name == "GML")
        {
            if (isDark)
                return GMLHighlightingDefinitionDark ??= LoadHighlightingDefinition("GML", isDark: true);
            return GMLHighlightingDefinitionLight ??= LoadHighlightingDefinition("GML", isDark: false);
        }
        else
        {
            if (isDark)
                return ASMHighlightingDefinitionDark ??= LoadHighlightingDefinition("ASM", isDark: true);
            return ASMHighlightingDefinitionLight ??= LoadHighlightingDefinition("ASM", isDark: false);
        }
    }

    static IHighlightingDefinition LoadHighlightingDefinition(string name, bool isDark)
    {
        using (XmlReader reader = XmlReader.Create(AssetLoader.Open(new Uri($"avares://{Assembly.GetExecutingAssembly().FullName}/Assets/Syntax{name}.xshd"))))
        {
            IHighlightingDefinition definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);

            // Remove string escaping rule from GMS1, since it doesn't have that.
            if (HighlightingMajorVersion < 2)
            {
                foreach (HighlightingSpan span in definition.MainRuleSet.Spans)
                {
                    string expression = span.StartExpression.ToString();
                    if (expression == "\"" || expression == "'")
                        span.RuleSet.Spans.Clear();
                }
            }

            if (!isDark)
                ApplySyntaxTheme(definition);

            return definition;
        }
    }

    static void ApplySyntaxTheme(IHighlightingDefinition def)
    {
        void Rewrite(HighlightingColor? color)
        {
            if (color?.Foreground == null)
                return;
            Color? source = color.Foreground.GetColor(null);
            if (source.HasValue && LightSyntaxColorMap.TryGetValue(source.Value, out Color light))
                color.Foreground = new SimpleHighlightingBrush(light);
        }

        foreach (HighlightingColor color in def.NamedHighlightingColors)
            Rewrite(color);
        foreach (HighlightingRule rule in def.MainRuleSet.Rules)
            Rewrite(rule.Color);
        foreach (HighlightingSpan span in def.MainRuleSet.Spans)
        {
            Rewrite(span.SpanColor);
            Rewrite(span.StartColor);
            Rewrite(span.EndColor);
        }
    }

    private void ApplyEditorTheme(bool isDark)
    {
        void ApplyTo(TextEditor editor)
        {
            editor.Foreground = isDark
                ? new SolidColorBrush(Color.FromRgb(0xC9, 0xCD, 0xDC))
                : new SolidColorBrush(Color.FromRgb(0x28, 0x24, 0x19));
            editor.Background = isDark
                ? new SolidColorBrush(Color.FromRgb(0x1B, 0x1E, 0x29))
                : new SolidColorBrush(Color.FromRgb(0xFA, 0xF8, 0xF3));
            editor.LineNumbersForeground = isDark
                ? new SolidColorBrush(Color.FromRgb(0x76, 0x7C, 0x92))
                : new SolidColorBrush(Color.FromRgb(0x9A, 0x93, 0x84));

            TextArea textArea = editor.TextArea;
            if (textArea is null)
                return;

            textArea.TextView.CurrentLineBackground = isDark
                ? new SolidColorBrush(Color.FromRgb(0x23, 0x27, 0x38))
                : new SolidColorBrush(Color.FromRgb(0xED, 0xE9, 0xDF));
            textArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
            textArea.SelectionBrush = isDark
                ? new SolidColorBrush(Color.FromRgb(0x3D, 0x4A, 0x78))
                : new SolidColorBrush(Color.FromRgb(0xC3, 0xD2, 0xEA));
            textArea.SelectionForeground = null;
            textArea.SelectionBorder = null;
            textArea.SelectionCornerRadius = 0;
        }

        ApplyTo(GMLTextEditor);
        ApplyTo(ASMTextEditor);
    }

    private void ApplyResultsPanelTheme(bool isDark)
    {
        if (ResultsPanel is null)
            return;

        Color panelBg = isDark ? Color.FromRgb(0x22, 0x26, 0x33) : Color.FromRgb(0xEF, 0xEC, 0xE4);
        Color panelBorder = isDark ? Color.FromRgb(0x3A, 0x3F, 0x50) : Color.FromRgb(0xC9, 0xC3, 0xB4);
        Color panelText = isDark ? Color.FromRgb(0xE6, 0xE8, 0xF0) : Color.FromRgb(0x28, 0x24, 0x19);
        ResultsPanel.Background = new SolidColorBrush(panelBg);
        ResultsPanel.BorderBrush = new SolidColorBrush(panelBorder);
        ResultsHeaderText.Foreground = new SolidColorBrush(panelText);
        ResultsListBox.Background = isDark
            ? new SolidColorBrush(Color.FromRgb(0x1D, 0x21, 0x2D))
            : new SolidColorBrush(Color.FromRgb(0xF7, 0xF4, 0xEE));
        ResultsListBox.Foreground = new SolidColorBrush(panelText);
    }

    private void ApplySettingsToEditors()
    {
        var settings = Settings;
        if (settings is null)
            return;

        WordWrapCheck.IsChecked = settings.CodeEditorWordWrap;
        ShowWhitespaceCheck.IsChecked = settings.CodeEditorShowWhitespace;
        ShowHoverInfoCheck.IsChecked = settings.CodeEditorShowHoverInfo;
        AutoDiagnosticsCheck.IsChecked = settings.CodeEditorAutoDiagnostics;

        ApplyWordWrapToEditors(settings.CodeEditorWordWrap);
        ApplyWhitespaceToEditors(settings.CodeEditorShowWhitespace);
        ApplyAutoDiagnosticsState();

        double fontSize = settings.CodeEditorFontSize > 0 ? settings.CodeEditorFontSize : 12;
        GMLTextEditor.FontSize = fontSize;
        ASMTextEditor.FontSize = fontSize;
    }

    private void ApplyWordWrapToEditors(bool value)
    {
        GMLTextEditor.WordWrap = value;
        ASMTextEditor.WordWrap = value;
    }

    private void ApplyWhitespaceToEditors(bool value)
    {
        GMLTextEditor.Options.ShowSpaces = value;
        GMLTextEditor.Options.ShowTabs = value;
        ASMTextEditor.Options.ShowSpaces = value;
        ASMTextEditor.Options.ShowTabs = value;
    }

    private void WordWrapCheck_Changed(object? sender, RoutedEventArgs e)
    {
        if (GMLTextEditor is null) return;
        bool value = WordWrapCheck.IsChecked ?? true;
        ApplyWordWrapToEditors(value);
        if (Settings is not null)
        {
            Settings.CodeEditorWordWrap = value;
            Settings.Save();
        }
    }

    private void ShowWhitespaceCheck_Changed(object? sender, RoutedEventArgs e)
    {
        if (GMLTextEditor is null) return;
        bool value = ShowWhitespaceCheck.IsChecked ?? false;
        ApplyWhitespaceToEditors(value);
        if (Settings is not null)
        {
            Settings.CodeEditorShowWhitespace = value;
            Settings.Save();
        }
    }

    private void ShowHoverInfoCheck_Changed(object? sender, RoutedEventArgs e)
    {
        if (Settings is null) return;
        bool value = ShowHoverInfoCheck.IsChecked ?? true;
        Settings.CodeEditorShowHoverInfo = value;
        Settings.Save();
        if (!value)
            CloseHoverPopup();
    }

    private void AutoDiagnosticsCheck_Changed(object? sender, RoutedEventArgs e)
    {
        if (GMLTextEditor is null)
            return;
        if (Settings is null) return;
        bool value = AutoDiagnosticsCheck.IsChecked ?? true;
        Settings.CodeEditorAutoDiagnostics = value;
        Settings.Save();
        ApplyAutoDiagnosticsState();
    }

    private void ApplyAutoDiagnosticsState()
    {
        bool enabled = Settings?.CodeEditorAutoDiagnostics ?? true;
        if (!enabled)
        {
            _diagnosticsTimer?.Stop();
            _diagnosticsCancellation?.Cancel();
            _diagnosticsRenderer?.SetDiagnostics(Array.Empty<GmlDiagnostic>(), GMLTextEditor?.Document);
            if (_lastResultMode == ResultMode.Errors)
                HideResultsPanel();
            return;
        }

        if (GMLTextEditor is null || !IsLoaded)
            return;

        _ = RunDiagnosticsAsync();
    }

    protected override async void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == DataContextProperty)
        {
            if (change.OldValue is UndertaleCodeViewModel oldVM)
            {
                oldVM.PropertyChanged -= DataContext_PropertyChanged;
            }

            if (change.NewValue is not UndertaleCodeViewModel vm)
                return;

            vm.PropertyChanged += DataContext_PropertyChanged;

            // Reset diagnostics/references from the previously shown code entry
            _diagnosticsCancellation?.Cancel();
            _diagnosticsTimer?.Stop();
            _diagnosticsRenderer?.SetDiagnostics(Array.Empty<GmlDiagnostic>(), GMLTextEditor.Document);
            _panelManuallyClosed = false;
            HideResultsPanel();
            CloseCompletionWindow();

            if (vm.MainVM.Settings!.EnableSyntaxHighlighting)
            {
                // Reload highlighting if major version changed
                if (HighlightingMajorVersion != vm.MainVM.Data!.GeneralInfo.Major)
                {
                    GMLHighlightingDefinitionDark = null;
                    GMLHighlightingDefinitionLight = null;
                    ASMHighlightingDefinitionDark = null;
                    ASMHighlightingDefinitionLight = null;
                }

                HighlightingMajorVersion = vm.MainVM.Data!.GeneralInfo.Major;
            }

            ApplySyntaxThemeToEditors(IsDarkTheme);
            ApplyEditorTheme(IsDarkTheme);
            ApplyResultsPanelTheme(IsDarkTheme);

            UpdateHighlightingCache();

            GMLTextEditor.TextArea.Caret.Location = default;
            ASMTextEditor.TextArea.Caret.Location = default;
            lastCaretLocations = default;

            vm.GMLTabState = TabState.NeedsDecompile;
            vm.ASMTabState = TabState.NeedsDecompile;

            await GoToLastGoToLocation();
            await vm.DecompileCurrent();

            GMLTextEditor.Document.UndoStack.ClearAll();
            ASMTextEditor.Document.UndoStack.ClearAll();
        }
    }

    private void DataContext_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (DataContext is UndertaleCodeViewModel vm)
        {
            switch (e.PropertyName)
            {
                case nameof(UndertaleCodeViewModel.IsCodeProcessing):
                    if (vm.IsCodeProcessing)
                    {
                        // Save carets
                        lastCaretLocations = (GMLTextEditor.TextArea.Caret.Location, ASMTextEditor.TextArea.Caret.Location);
                    }
                    else
                    {
                        // Load carets
                        GMLTextEditor.TextArea.Caret.Location = lastCaretLocations.Item1;
                        ASMTextEditor.TextArea.Caret.Location = lastCaretLocations.Item2;
                    }
                    break;

                case nameof(UndertaleCodeViewModel.LastGoToLocation):
                    _ = GoToLastGoToLocation();
                    break;

                case nameof(UndertaleCodeViewModel.GMLTabState):
                    // After a fresh decompile the whole text was replaced -> reset change tracking
                    if (_lastGMLTabState != TabState.Ok && vm.GMLTabState == TabState.Ok)
                        _gmlModifiedRenderer.SetOriginalText(GMLTextEditor.Text, GMLTextEditor.Document);
                    _lastGMLTabState = vm.GMLTabState;
                    break;

                case nameof(UndertaleCodeViewModel.ASMTabState):
                    if (_lastASMTabState != TabState.Ok && vm.ASMTabState == TabState.Ok)
                        _asmModifiedRenderer.SetOriginalText(ASMTextEditor.Text, ASMTextEditor.Document);
                    _lastASMTabState = vm.ASMTabState;
                    break;
            }
        }
    }

    #region Touch handling

    void InitializeTextEditorTouchHandling(TextEditor editor)
    {
        editor.AddHandler<PointerPressedEventArgs>(InputElement.PointerPressedEvent, EditorTouch_PointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        editor.AddHandler<PointerEventArgs>(InputElement.PointerMovedEvent, EditorTouch_PointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        editor.AddHandler<PointerReleasedEventArgs>(InputElement.PointerReleasedEvent, EditorTouch_PointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        editor.AddHandler<PointerCaptureLostEventArgs>(InputElement.PointerCaptureLostEvent, EditorTouch_PointerCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);

        // Tunnel-stage guards: while a touch selection is being extended, the editor's own
        // gesture recognizers (ScrollViewer panning, AvaloniaEdit's drag handling, hover popups)
        // would compete with the selection, so swallow the pointer events before they reach them.
        editor.AddHandler<PointerPressedEventArgs>(InputElement.PointerPressedEvent, EditorTouch_PointerPressedTunnel, RoutingStrategies.Tunnel);
        editor.AddHandler<PointerEventArgs>(InputElement.PointerMovedEvent, EditorTouch_PointerMovedTunnel, RoutingStrategies.Tunnel);
        editor.AddHandler<PointerReleasedEventArgs>(InputElement.PointerReleasedEvent, EditorTouch_PointerReleasedTunnel, RoutingStrategies.Tunnel);
    }

    void EditorTouch_PointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (codeEditorTouchSelecting)
            e.Handled = true;
    }

    void EditorTouch_PointerMovedTunnel(object? sender, PointerEventArgs e)
    {
        if (codeEditorTouchSelecting)
            e.Handled = true;
    }

    void EditorTouch_PointerReleasedTunnel(object? sender, PointerReleasedEventArgs e)
    {
        if (codeEditorTouchSelecting)
            e.Handled = true;
    }

    /// <summary>
    /// Touch gestures on the code editor (Android / other touch platforms):
    /// <list type="bullet">
    /// <item>one-finger scroll is left to the ScrollViewer (the editor is one);</item>
    /// <item>tap positions the caret (fallback so it works even when the ScrollViewer competes
    /// for the touch) and keeps the soft keyboard visible;</item>
    /// <item>two-finger pinch changes the font size (persisted in settings);</item>
    /// <item>long-press selects the word under the finger; dragging while holding extends the
    /// selection, releasing opens the edit menu (undo/redo/select all/cut/copy/paste).</item>
    /// </list>
    /// </summary>
    void EditorTouch_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TextEditor editor || e.Pointer.Type != PointerType.Touch)
            return;

        long id = e.Pointer.Id;
        Point pos = e.GetPosition(editor);
        Point posInTextView = e.GetPosition(editor.TextArea.TextView);

        codeEditorTouchPoints[id] = pos;

        if (codeEditorTouchPrimaryId is null)
        {
            // Fresh gesture: clear any state left behind by a previous one (a missed release
            // could otherwise leave a stale secondary pointer id that blocks the next pinch).
            codeEditorTouchPoints.Clear();
            codeEditorTouchPoints[id] = pos;
            codeEditorTouchPrimaryId = id;
            codeEditorTouchEditor = editor;
            codeEditorTouchStartPosition = pos;
            codeEditorTouchStartTextViewPosition = posInTextView;
            codeEditorTouchPinching = false;
            codeEditorTouchLongPressFired = false;
            codeEditorTouchScrolled = false;
            codeEditorTouchSelecting = false;
            codeEditorSelectionAnchorOffset = -1;
            StartCodeEditorLongPressTimer();
        }
        else if (codeEditorTouchSecondaryId is null && !codeEditorTouchPinching)
        {
            // Second finger: begin pinch zoom immediately (a two-finger tap without movement
            // applies a scale factor of ~1, so it is harmless). A long-press selection in
            // progress is cancelled in favour of the pinch.
            codeEditorTouchSelecting = false;
            codeEditorSelectionAnchorOffset = -1;
            codeEditorTouchSecondaryId = id;
            StopCodeEditorLongPressTimer();
            codeEditorTouchPinching = true;
            if (codeEditorTouchPrimaryId is long primaryId
                && codeEditorTouchPoints.TryGetValue(primaryId, out Point p1))
            {
                codeEditorPinchStartDistance = Distance(pos, p1);
            }
            else
            {
                codeEditorPinchStartDistance = 1;
            }
            codeEditorPinchStartFontSize = editor.FontSize > 0 ? editor.FontSize : 12;
        }
    }

    void EditorTouch_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not TextEditor editor || e.Pointer.Type != PointerType.Touch)
            return;

        long id = e.Pointer.Id;
        if (!codeEditorTouchPoints.ContainsKey(id))
            return;

        Point pos = e.GetPosition(editor);
        codeEditorTouchPoints[id] = pos;

        if (codeEditorTouchPinching)
        {
            if (codeEditorTouchPrimaryId is long primaryId && codeEditorTouchSecondaryId is long secondaryId
                && codeEditorTouchPoints.TryGetValue(primaryId, out Point p1) && codeEditorTouchPoints.TryGetValue(secondaryId, out Point p2))
            {
                double distance = Distance(p1, p2);
                if (distance > 1 && codeEditorPinchStartDistance > 1)
                {
                    double factor = distance / codeEditorPinchStartDistance;
                    double size = Math.Clamp(codeEditorPinchStartFontSize * factor, CodeEditorFontSizeMin, CodeEditorFontSizeMax);
                    if (size != editor.FontSize)
                        SetCodeEditorFontSize(size);
                }
            }
            e.Handled = true;
            return;
        }

        if (codeEditorTouchSelecting)
        {
            // Long-press selection: dragging the held finger extends the selection.
            // (The tunnel guard above already blocks the ScrollViewer and hover logic.)
            if (id == codeEditorTouchPrimaryId)
            {
                int selOffset = GetOffsetFromPointer(e, editor.TextArea);
                if (selOffset >= 0)
                    ExtendCodeEditorSelection(editor.TextArea, selOffset);
            }
            return;
        }

        // Single finger: if it starts moving this is a scroll, so cancel the long-press and the
        // upcoming tap.
        if (id == codeEditorTouchPrimaryId && !codeEditorTouchLongPressFired
            && Distance(pos, codeEditorTouchStartPosition) > CodeEditorTouchMoveThreshold)
        {
            codeEditorTouchScrolled = true;
            StopCodeEditorLongPressTimer();
        }
    }

    void EditorTouch_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not TextEditor editor || e.Pointer.Type != PointerType.Touch)
            return;

        long id = e.Pointer.Id;
        Point pos = e.GetPosition(editor);

        if (codeEditorTouchPinching)
        {
            codeEditorTouchPoints.Remove(id);
            if (id == codeEditorTouchPrimaryId) codeEditorTouchPrimaryId = null;
            if (id == codeEditorTouchSecondaryId) codeEditorTouchSecondaryId = null;

            if (codeEditorTouchPoints.Count < 2)
            {
                codeEditorTouchPinching = false;
                SaveCodeEditorFontSize();

                if (codeEditorTouchPoints.Count == 1)
                {
                    var remaining = codeEditorTouchPoints.First();
                    codeEditorTouchPrimaryId = remaining.Key;
                    // The secondary pointer must be cleared too: when the primary finger lifts
                    // first it used to stay set (pointing at the same remaining finger), and the
                    // stale id then silently disabled every later pinch.
                    codeEditorTouchSecondaryId = null;
                    codeEditorTouchStartPosition = remaining.Value;
                    codeEditorTouchSelecting = false;
                    codeEditorSelectionAnchorOffset = -1;
                }
                else
                {
                    codeEditorTouchPrimaryId = null;
                    codeEditorTouchSecondaryId = null;
                }
            }
            return;
        }

        if (id != codeEditorTouchPrimaryId)
        {
            codeEditorTouchPoints.Remove(id);
            return;
        }

        codeEditorTouchPoints.Remove(id);
        codeEditorTouchPrimaryId = null;
        codeEditorTouchSecondaryId = null;

        if (codeEditorTouchLongPressFired)
        {
            codeEditorTouchLongPressFired = false;

            // A long-press selection was started in CodeEditorLongPressTimer_Tick: extend it to
            // the release point (covers a drag while the finger is still down) and then show the
            // edit menu so the selection can be cut/copied.
            if (codeEditorTouchSelecting)
            {
                int endOffset = GetOffsetFromPointer(e, editor.TextArea);
                if (endOffset >= 0)
                    ExtendCodeEditorSelection(editor.TextArea, endOffset);
                codeEditorTouchSelecting = false;
                codeEditorSelectionAnchorOffset = -1;
                OpenCodeEditorTouchMenu(editor, pos);
            }
            return;
        }

        StopCodeEditorLongPressTimer();

        if (codeEditorTouchScrolled)
        {
            // This release ends a scroll gesture, not a tap.
            codeEditorTouchScrolled = false;
            return;
        }

        // Tap: focus and place the caret right under the finger. A tap also collapses any
        // previous touch selection.
        bool wasFocused = editor.TextArea.IsFocused;
        editor.TextArea.Focus();
        int offset = GetOffsetFromPointer(e, editor.TextArea);
        if (offset >= 0)
        {
            editor.CaretOffset = offset;
            editor.TextArea.ClearSelection();
        }
        EnsureCodeEditorKeyboardVisible(editor, wasFocused);
    }

    void EditorTouch_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (sender is not TextEditor editor || e.Pointer.Type != PointerType.Touch)
            return;

        // The ScrollViewer took over the gesture (scroll) or the touch was cancelled.
        ResetCodeEditorTouchState();
    }

    void ResetCodeEditorTouchState()
    {
        StopCodeEditorLongPressTimer();
        codeEditorTouchPoints.Clear();
        codeEditorTouchPrimaryId = null;
        codeEditorTouchSecondaryId = null;
        codeEditorTouchPinching = false;
        codeEditorTouchLongPressFired = false;
        codeEditorTouchScrolled = false;
        codeEditorTouchSelecting = false;
        codeEditorSelectionAnchorOffset = -1;
        codeEditorTouchEditor = null;
    }

    void StartCodeEditorLongPressTimer()
    {
        StopCodeEditorLongPressTimer();
        codeEditorLongPressTimer = new DispatcherTimer(CodeEditorLongPressDuration, DispatcherPriority.Background, (_, _) => CodeEditorLongPressTimer_Tick());
        codeEditorLongPressTimer.Start();
    }

    void StopCodeEditorLongPressTimer()
    {
        codeEditorLongPressTimer?.Stop();
        codeEditorLongPressTimer = null;
    }

    void CodeEditorLongPressTimer_Tick()
    {
        if (codeEditorTouchLongPressFired)
            return;

        if (codeEditorTouchPoints.Count != 1)
            return;

        if (codeEditorTouchEditor is not { } editor)
            return;

        if (codeEditorTouchPrimaryId is not long id || !codeEditorTouchPoints.TryGetValue(id, out _))
            return;

        codeEditorTouchLongPressFired = true;
        StopCodeEditorLongPressTimer();

        editor.TextArea.Focus();

        int offset = GetOffsetFromTextViewPoint(editor.TextArea, codeEditorTouchStartTextViewPosition);
        if (offset < 0)
            return;

        editor.CaretOffset = offset;

        // Start a touch text selection: select the word under the finger. Dragging while the
        // finger is still down extends the selection (see EditorTouch_PointerMoved / the tunnel
        // guards), and releasing shows the edit menu with cut/copy enabled.
        (int start, int end) = GetWordBoundsAtOffset(editor.TextArea, offset);
        codeEditorTouchSelecting = true;
        codeEditorSelectionAnchorOffset = start;
        editor.TextArea.Selection = Selection.Create(editor.TextArea, start, end);

        PlatformHaptics.OnLongPress();
    }

    void ExtendCodeEditorSelection(TextArea textArea, int offset)
    {
        if (textArea.Document is null)
            return;

        int clamped = Math.Clamp(offset, 0, textArea.Document.TextLength);
        if (codeEditorSelectionAnchorOffset < 0)
            codeEditorSelectionAnchorOffset = clamped;

        int start = Math.Min(codeEditorSelectionAnchorOffset, clamped);
        int end = Math.Max(codeEditorSelectionAnchorOffset, clamped);
        textArea.Selection = Selection.Create(textArea, start, end);
        textArea.Caret.Offset = end;
    }

    static (int start, int end) GetWordBoundsAtOffset(TextArea textArea, int offset)
    {
        if (textArea.Document is not { } doc)
            return (offset, offset);

        string text = doc.Text;
        if (text.Length == 0)
            return (offset, offset);

        int clamped = Math.Clamp(offset, 0, text.Length);
        int start = clamped;
        while (start > 0 && IsWordChar(text[start - 1]))
            start--;
        int end = clamped;
        while (end < text.Length && IsWordChar(text[end]))
            end++;
        return (start, end);
    }

    /// <summary>
    /// Keeps the soft keyboard visible after a touch tap that did not change focus. When the
    /// editor was already focused, <c>Focus()</c> is a no-op and the platform is not told about
    /// the tap at all, so on Android the keyboard can retract even though the IME client stays
    /// attached - a state that plain taps can never re-show (the platform only shows the input
    /// pane when the client is (re)applied). Re-asserting the client here, now and once more
    /// shortly after the gesture, brings the keyboard back.
    /// </summary>
    void EnsureCodeEditorKeyboardVisible(TextEditor editor, bool wasFocusedBeforeTap)
    {
        if (!wasFocusedBeforeTap)
            return; // the fresh focus-gain path already re-applies the IME client

        // Re-check shortly after the gesture as well: the platform may hide the keyboard
        // asynchronously (e.g. an IME restart triggered by the caret move).
        StopCodeEditorKeyboardCheckTimer();
        codeEditorKeyboardCheckTimer = new DispatcherTimer(CodeEditorKeyboardCheckDelay, DispatcherPriority.Background, (_, _) => CodeEditorKeyboardCheckTimer_Tick(editor));
        codeEditorKeyboardCheckTimer.Start();

        CodeEditorKeyboardCheck(editor);
    }

    void CodeEditorKeyboardCheckTimer_Tick(TextEditor editor)
    {
        codeEditorKeyboardCheckTimer?.Stop();
        codeEditorKeyboardCheckTimer = null;
        CodeEditorKeyboardCheck(editor);
    }

    void CodeEditorKeyboardCheck(TextEditor editor)
    {
        if (!editor.TextArea.IsFocused)
            return;

        IInputPane? pane = GetInputPane(editor);
        if (pane is null)
            return; // no software keyboard on this platform
        if (pane.State == InputPaneState.Open)
            return; // the keyboard is already visible

        // The editor is focused but the soft keyboard is not shown: re-apply the IME client so
        // the platform shows the input pane again (Android shows the keyboard on SetClient).
        InputMethod.SetIsInputMethodEnabled(editor.TextArea, false);
        InputMethod.SetIsInputMethodEnabled(editor.TextArea, true);
    }

    static IInputPane? GetInputPane(Visual visual)
        => (TopLevel.GetTopLevel(visual)?.PlatformImpl as IOptionalFeatureProvider)?.TryGetFeature(typeof(IInputPane)) as IInputPane;

    void StopCodeEditorKeyboardCheckTimer()
    {
        codeEditorKeyboardCheckTimer?.Stop();
        codeEditorKeyboardCheckTimer = null;
    }

    void SetCodeEditorFontSize(double size)
    {
        if (GMLTextEditor is not null)
            GMLTextEditor.FontSize = size;
        if (ASMTextEditor is not null)
            ASMTextEditor.FontSize = size;

        if (Settings is not null && Math.Abs(Settings.CodeEditorFontSize - size) > 0.01)
        {
            Settings.CodeEditorFontSize = size;
            // Save deferred until the pinch ends to avoid writing the settings file on every frame.
        }
    }

    void SaveCodeEditorFontSize()
    {
        if (Settings is null)
            return;

        double size = codeEditorTouchEditor?.FontSize ?? Settings.CodeEditorFontSize;
        if (Math.Abs(Settings.CodeEditorFontSize - size) > 0.01)
        {
            Settings.CodeEditorFontSize = size;
            Settings.Save();
        }
    }

    void OpenCodeEditorTouchMenu(TextEditor editor, Point positionInEditor)
    {
        // PlacementMode.Pointer tracks the last pointer position, which for a long-press is the
        // holding finger, so the menu appears right at the touch point.
        ContextMenu contextMenu = new()
        {
            Placement = PlacementMode.Pointer,
        };

        TextArea textArea = editor.TextArea;

        MenuItem undoItem = new()
        {
            Header = LocalizationSource.GetString("Common_Undo"),
            IsEnabled = editor.CanUndo,
        };
        undoItem.Click += (_, _) => editor.Undo();

        MenuItem redoItem = new()
        {
            Header = LocalizationSource.GetString("Common_Redo"),
            IsEnabled = editor.CanRedo,
        };
        redoItem.Click += (_, _) => editor.Redo();

        MenuItem selectAllItem = new()
        {
            Header = LocalizationSource.GetString("Common_SelectAll"),
        };
        selectAllItem.Click += (_, _) => editor.SelectAll();

        MenuItem cutItem = new()
        {
            Header = LocalizationSource.GetString("Common_Cut"),
            IsEnabled = textArea.Selection.Length > 0,
        };
        cutItem.Click += (_, _) => editor.Cut();

        MenuItem copyItem = new()
        {
            Header = LocalizationSource.GetString("Common_Copy"),
            IsEnabled = textArea.Selection.Length > 0,
        };
        copyItem.Click += (_, _) => editor.Copy();

        MenuItem pasteItem = new()
        {
            Header = LocalizationSource.GetString("Common_Paste"),
        };
        pasteItem.Click += (_, _) => TryPaste(editor);

        contextMenu.Items.Add(undoItem);
        contextMenu.Items.Add(redoItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(selectAllItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(cutItem);
        contextMenu.Items.Add(copyItem);
        contextMenu.Items.Add(pasteItem);

        contextMenu.Open(editor);
    }

    static void TryPaste(TextEditor editor)
    {
        try
        {
            editor.Paste();
        }
        catch (Exception)
        {
            // E.g. clipboard unavailable; nothing sensible to do.
        }
    }

    static double Distance(Point a, Point b)
    {
        Vector v = b - a;
        return v.Length;
    }

    int GetOffsetFromTextViewPoint(TextArea textArea, Point posInTextView)
    {
        if (textArea.Document is null)
            return -1;

        Point pos = posInTextView + textArea.TextView.ScrollOffset;

        TextViewPosition? textViewPos = textArea.TextView.GetPosition(pos);
        if (textViewPos == null) return -1;

        int line = textViewPos.Value.Line;
        int column = textViewPos.Value.Column;

        if (line < 1 || line > textArea.Document.LineCount) return -1;

        var docLine = textArea.Document.GetLineByNumber(line);
        return docLine.Offset + Math.Min(column - 1, docLine.Length);
    }

    #endregion

    static void InitializeTextEditor(TextEditor textEditor)
    {
        textEditor.Options.ConvertTabsToSpaces = true;
        textEditor.Options.HighlightCurrentLine = true;
    }

    void UpdateHighlightingCache()
    {
        if (DataContext is not UndertaleCodeViewModel vm)
            return;

        ScriptsCache.Clear();
        FunctionsCache.Clear();
        CodeCache.Clear();
        NamedResourcesCache.Clear();
        codeLocalsCache.Clear();

        if (!vm.MainVM.Settings!.EnableSyntaxHighlighting)
            return;

        UndertaleData? data = vm.MainVM.Data;
        if (data is null)
            return;

        foreach (var script in data.Scripts)
        {
            if (script is null || script.Name is null)
                continue;
            ScriptsCache[script.Name.Content] = script;
        }

        foreach (var function in data.Functions)
        {
            if (function is null || function.Name is null)
                continue;
            FunctionsCache[function.Name.Content] = function;
        }

        foreach (var code in data.Code)
        {
            if (code is null || code.Name is null)
                continue;
            CodeCache[code.Name.Content] = code;
        }

        // NOTE: Remember to add new types
        IEnumerable?[] objLists = [
            data.Sounds,
            data.Sprites,
            data.Backgrounds,
            data.Paths,
            data.Scripts,
            data.Fonts,
            data.GameObjects,
            data.Rooms,
            data.Extensions,
            data.Shaders,
            data.Timelines,
            data.AnimationCurves,
            data.Sequences,
            data.AudioGroups
        ];

        foreach (IEnumerable? list in objLists)
        {
            if (list is null)
                continue;

            foreach (var obj in list)
            {
                if (obj is UndertaleNamedResource namedObj && namedObj.Name is not null)
                    NamedResourcesCache[namedObj.Name.Content] = namedObj;
            }
        }

        UndertaleCodeLocals? locals = data.CodeLocals?.ByName(vm.Code.Name?.Content);
        if (locals != null)
        {
            foreach (var local in locals.Locals)
                codeLocalsCache.Add(local.Name.Content);
            codeLocalsCache.Sort();
        }

        GMLTextEditor.TextArea.TextView.Redraw();
        ASMTextEditor.TextArea.TextView.Redraw();
    }

    public async Task GoToLastGoToLocation()
    {
        if (DataContext is not UndertaleCodeViewModel vm)
            return;

        if (vm.LastGoToLocation is not (Tab tab, int line, int column) location)
            return;

        vm.SelectedTab = location.Tab;

        await vm.DecompileCurrent();

        TextEditor textEditor = (location.Tab == Tab.GML) ? GMLTextEditor : ASMTextEditor;

        textEditor.TextArea.Caret.Location = new(location.Line, location.Column);
        textEditor.TextArea.Focus();

        void OnLayoutUpdated(object? sender, EventArgs e)
        {
            textEditor.ScrollTo(location.Line, location.Column);
            textEditor.LayoutUpdated -= OnLayoutUpdated;
        }

        textEditor.LayoutUpdated += OnLayoutUpdated;

        // HACK: I don't know how to check if the layout has updated already here or not, so I just invalidate it to call the above function.
        textEditor.InvalidateMeasure();

        vm.LastGoToLocation = null;
    }

    private void GMLTextEditor_GotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (DataContext is not UndertaleCodeViewModel vm)
            return;

        vm.GMLFocused = true;

        UpdateHighlightingCache();
    }

    private void ASMTextEditor_GotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (DataContext is not UndertaleCodeViewModel vm)
            return;

        vm.ASMFocused = true;

        UpdateHighlightingCache();
    }

    private void GMLTextEditor_LostFocus(object? sender, FocusChangedEventArgs e)
    {
        if (DataContext is not UndertaleCodeViewModel vm)
            return;

        if (e.NavigationMethod == NavigationMethod.Unspecified)
            return;

        if (vm.GMLFocused && vm.MainVM.Settings!.AutomaticallyCompileAndDecompileCodeOnLostFocus)
        {
            vm.GMLFocused = false;
            vm.CompileAndDecompileTab(Tab.GML);
        }
    }

    private void ASMTextEditor_LostFocus(object? sender, FocusChangedEventArgs e)
    {
        if (DataContext is not UndertaleCodeViewModel vm)
            return;

        if (e.NavigationMethod == NavigationMethod.Unspecified)
            return;

        if (vm.ASMFocused && vm.MainVM.Settings!.AutomaticallyCompileAndDecompileCodeOnLostFocus)
        {
            vm.ASMFocused = false;
            vm.CompileAndDecompileTab(Tab.ASM);
        }
    }

    private void GMLTextEditor_TextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not UndertaleCodeViewModel vm)
            return;

        if (!vm.IsCodeProcessing)
        {
            vm.GMLTabState = TabState.NeedsCompile;
            vm.MainVM.Project?.MarkAssetForExport(vm.Code);
        }

        if (_isLoadingCode)
            return;

        if (vm.MainVM.Settings?.ChangeTrackingEnabled ?? true)
            _gmlModifiedRenderer.MarkDirty();

        if (!vm.IsCodeProcessing)
        {
            _foldingTimer?.Stop();
            _foldingTimer?.Start();

            if (vm.MainVM.Settings?.CodeEditorAutoDiagnostics ?? true)
            {
                _diagnosticsTimer?.Stop();
                _diagnosticsTimer?.Start();
            }
        }
    }

    private void ASMTextEditor_TextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not UndertaleCodeViewModel vm)
            return;

        if (!vm.IsCodeProcessing)
        {
            vm.ASMTabState = TabState.NeedsCompile;
        }

        if (_isLoadingCode)
            return;

        if (vm.MainVM.Settings?.ChangeTrackingEnabled ?? true)
            _asmModifiedRenderer.MarkDirty();
    }

    // ---------- Folding ----------

    void UpdateFolding()
    {
        if (_foldingManager is null)
            return;
        try
        {
            _foldingStrategy.UpdateFoldings(_foldingManager, GMLTextEditor.Document);
        }
        catch
        {
            // Ignore folding errors
        }
    }

    // ---------- Diagnostics ----------

    private async Task RunDiagnosticsAsync()
    {
        if (DataContext is not UndertaleCodeViewModel vm)
            return;

        if (!(vm.MainVM.Settings?.CodeEditorAutoDiagnostics ?? true))
        {
            _diagnosticsRenderer?.SetDiagnostics(Array.Empty<GmlDiagnostic>(), GMLTextEditor.Document);
            return;
        }

        if (vm.IsCodeProcessing)
            return;

        UndertaleCode code = vm.Code;
        if (code is null || code.ParentEntry is not null)
        {
            ApplyDiagnostics(Array.Empty<GmlDiagnostic>());
            return;
        }

        string docText = GMLTextEditor.Text;
        string? codeName = code.Name?.Content;
        UndertaleData data = vm.MainVM.Data;
        if (data is null)
        {
            ApplyDiagnostics(Array.Empty<GmlDiagnostic>());
            return;
        }

        // Warm up the shared parse context on the UI thread (creation builds asset lookups).
        try
        {
            GmlLanguageService.GetParseContext(data);
        }
        catch
        {
            ApplyDiagnostics(Array.Empty<GmlDiagnostic>());
            return;
        }

        // Cancel any earlier diagnostic run
        _diagnosticsCancellation?.Cancel();
        CancellationTokenSource cts = _diagnosticsCancellation = new CancellationTokenSource();
        int generation = ++_diagnosticsGeneration;

        IReadOnlyList<GmlDiagnostic> result;
        try
        {
            result = await Task.Run(() => GmlLanguageService.ParseDiagnostics(data, docText, codeName), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            result = Array.Empty<GmlDiagnostic>();
        }

        if (generation != _diagnosticsGeneration || cts.IsCancellationRequested)
            return;
        if (_isLoadingCode)
            return;

        ApplyDiagnostics(result);
    }

    private void ApplyDiagnostics(IReadOnlyList<GmlDiagnostic> diagnostics)
    {
        if (GMLTextEditor.Document is null)
            return;

        _diagnosticsRenderer?.SetDiagnostics(diagnostics, GMLTextEditor.Document);

        if (diagnostics is null || diagnostics.Count == 0)
        {
            // Don't hide the panel while it's showing manual reference results,
            // and don't re-open it if the user closed it manually.
            if (_lastResultMode == ResultMode.References || _panelManuallyClosed)
                return;
            HideResultsPanel();
            return;
        }

        // If the user closed the results panel manually, keep it closed (only update squiggles).
        if (_panelManuallyClosed)
            return;

        _resultEntries.Clear();
        foreach (GmlDiagnostic diag in diagnostics)
        {
            _resultEntries.Add(new CodeEditorResultEntry
            {
                Offset = diag.TextPosition,
                Line = diag.Line,
                Column = diag.Column,
                Display = $"{diag.Line},{diag.Column}: {diag.Message}",
                IsReference = false
            });
        }

        ShowResultsPanel(string.Format(LocalizationSource.GetString("Editor_ErrorsFound"), diagnostics.Count), ResultMode.Errors);
    }

    // ---------- Completion ----------

    private void InitializeCompletion()
    {
        GMLTextEditor.TextArea.TextEntered += OnEditorTextEntered;
        GMLTextEditor.TextArea.TextView.ScrollOffsetChanged += (s, e) => CloseCompletionWindow();
    }

    private void CloseCompletionWindow()
    {
        _completionWindow?.Close();
        _completionWindow = null;
    }

    private void OnEditorTextEntered(object? sender, TextInputEventArgs e)
    {
        if (sender is not TextArea)
            return;
        TextEditor editor = GMLTextEditor;
        if (editor is null)
            return;

        if (_completionWindow is not null && e.Text.Length > 0 && (char.IsLetterOrDigit(e.Text[0]) || e.Text[0] == '_'))
            return;

        if (e.Text.Length <= 0)
            return;

        char typed = e.Text[0];
        if (typed == '.')
        {
            OpenCompletionWindow(editor, true);
            return;
        }

        if (!char.IsLetterOrDigit(typed) && typed != '_')
        {
            CloseCompletionWindow();
            return;
        }

        // Don't complete inside line comments
        int caret = editor.CaretOffset;
        if (caret > 0 && editor.Document is not null)
        {
            DocumentLine line = editor.Document.GetLineByOffset(Math.Min(caret, editor.Document.TextLength));
            string lineText = editor.Document.GetText(line.Offset, Math.Max(0, caret - line.Offset));
            if (lineText.Contains("//"))
            {
                CloseCompletionWindow();
                return;
            }
        }

        // Only start completing after the first identifier character has been typed
        // (the word the user is typing must already exist before the caret)
        string? text = editor.Document?.Text;
        if (text is null || caret <= 0)
            return;
        char prev = text[caret - 1];
        if (!char.IsLetterOrDigit(prev) && prev != '_')
            return;

        OpenCompletionWindow(editor, false);
    }

    private void OpenCompletionWindow(TextEditor editor, bool memberAccess)
    {
        // Close the previous window before opening a new one
        CloseCompletionWindow();

        if (DataContext is not UndertaleCodeViewModel vm)
            return;
        if (vm.IsCodeProcessing)
            return;

        UndertaleData data = vm.MainVM.Data;
        if (data is null)
            return;

        string? code = editor.Document?.Text;
        if (code is null)
            return;

        int caret = editor.CaretOffset;

        List<GmlCompletionItem> items;
        try
        {
            items = GmlLanguageService.GetCompletionItems(data, code, caret, codeLocalsCache).ToList();
        }
        catch
        {
            return;
        }

        if (items.Count == 0)
            return;

        CompletionWindow window = new(editor.TextArea)
        {
            MaxHeight = 320,
            CloseAutomatically = true
        };
        window.CompletionList.IsFiltering = true;
        foreach (GmlCompletionItem item in items)
            window.CompletionList.CompletionData.Add(new GmlCompletionData(item));

        // Show the typed word in gray inside the completion box
        int wordStart = caret;
        string docText = code;
        while (wordStart > 0 && wordStart - 1 < docText.Length && IsWordChar(docText[wordStart - 1]))
            wordStart--;
        window.StartOffset = wordStart;

        _completionWindow = window;
        window.Closed += (s2, e2) => _completionWindow = null;
        window.Show();
    }

    // ---------- Hover ----------

    // TEMPORARY hover diagnostics (remove after debugging)
    internal static void HoverDbg(string msg)
    {
        try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "UTMT_HoverDebug.log"),
            $"{DateTime.Now:HH:mm:ss.fff} {msg}\n"); } catch { }
    }

    private void InitializeHoverPopup()
    {
        // A plain native popup without light dismiss: like WPF's StaysOpen tooltip. Neither a
        // native popup HWND nor an in-window light-dismiss overlay may swallow input here.
        // Closing is handled explicitly (offset change / scroll / any press), see below.
        _hoverPopup = new Popup
        {
            IsLightDismissEnabled = false,
            Placement = PlacementMode.Pointer,
            PlacementTarget = GMLTextEditor
        };

        _hoverTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(HoverDelayMs)
        };
        _hoverTimer.Tick += HoverTimer_Tick;

        GMLTextEditor.TextArea.PointerMoved += TextArea_PointerMoved;
        GMLTextEditor.TextArea.PointerExited += TextArea_PointerExited;
        ASMTextEditor.TextArea.PointerMoved += TextArea_PointerMoved;
        ASMTextEditor.TextArea.PointerExited += TextArea_PointerExited;

        // Tunnel stage so a press is seen even when a visual line element (number/name click)
        // handles it first; any press inside an editor dismisses the hover popup immediately.
        GMLTextEditor.TextArea.AddHandler(InputElement.PointerPressedEvent, TextArea_PointerPressedCloseHover, RoutingStrategies.Tunnel);
        ASMTextEditor.TextArea.AddHandler(InputElement.PointerPressedEvent, TextArea_PointerPressedCloseHover, RoutingStrategies.Tunnel);

        GMLTextEditor.TextArea.TextView.ScrollOffsetChanged += (s, e) => { _hoverTimer.Stop(); CloseHoverPopup(); };
        ASMTextEditor.TextArea.TextView.ScrollOffsetChanged += (s, e) => { _hoverTimer.Stop(); CloseHoverPopup(); };
    }

    private TopLevel? _globalDismissTopLevel;

    private void HookGlobalDismiss(TextEditor editor)
    {
        var topLevel = TopLevel.GetTopLevel(editor);
        if (topLevel == null || _globalDismissTopLevel != null)
            return;
        _globalDismissTopLevel = topLevel;
        topLevel.AddHandler(InputElement.PointerPressedEvent, GlobalDismissPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void UnhookGlobalDismiss()
    {
        if (_globalDismissTopLevel == null)
            return;
        _globalDismissTopLevel.RemoveHandler(InputElement.PointerPressedEvent, GlobalDismissPointerPressed);
        _globalDismissTopLevel = null;
    }

    private void GlobalDismissPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        UnhookGlobalDismiss();
        _hoverTimer?.Stop();
        CloseHoverPopup();
    }

    private void TextArea_PointerPressedCloseHover(object? sender, PointerPressedEventArgs e)
    {
        _hoverTimer?.Stop();
        CloseHoverPopup();
        // Suppress the tooltip until the pointer moves to a different offset, so it does not
        // pop back up over a click target or an opened context menu while the mouse is still.
        if (sender is TextArea textArea)
            _pressSuppressedOffset = GetOffsetFromPointer(e, textArea);
        _lastHoverOffset = -1;
    }

    private void TextArea_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (DataContext is not UndertaleCodeViewModel vm)
            return;

        if (!(vm.MainVM.Settings?.CodeEditorShowHoverInfo ?? true))
        {
            CloseHoverPopup();
            return;
        }

        if (sender is not TextArea textArea)
            return;

        _hoverEditor = (textArea == GMLTextEditor.TextArea) ? GMLTextEditor : ASMTextEditor;

        int currentOffset = GetOffsetFromPointer(e, textArea);

        // After a press, do not show the tooltip again until the pointer reaches a different offset.
        if (_pressSuppressedOffset >= 0)
        {
            if (currentOffset == _pressSuppressedOffset)
                return;
            _pressSuppressedOffset = -1;
        }

        if (_hoverPopup!.IsOpen && _hoverSectionStart >= 0 && currentOffset >= 0)
        {
            if (currentOffset >= _hoverSectionStart && currentOffset < _hoverSectionStart + _hoverSectionLength)
                return;
        }

        // High report-rate mice emit many moves per character cell; only restart the delay timer
        // when the offset actually changed, otherwise the popup can never appear.
        if (currentOffset == _lastHoverOffset && _hoverTimer!.IsEnabled)
            return;

        _lastHoverOffset = currentOffset;
        _hoverPendingOffset = currentOffset;
        _hoverTimer!.Stop();
        CloseHoverPopup();
        if (currentOffset >= 0)
            _hoverTimer.Start();
    }

    private void TextArea_PointerExited(object? sender, PointerEventArgs e)
    {
        // Only reset pending hover state; do not close an already visible popup here. When the
        // native popup window appears under the stationary cursor, Win32 raises a synthetic
        // pointer leave immediately afterwards, which used to close the tooltip the moment it
        // opened. The popup is dismissed by offset change / scroll / press instead.
        _hoverEditor = null;
        _hoverPendingOffset = -1;
        _lastHoverOffset = -1;
        _pressSuppressedOffset = -1;
        _hoverTimer?.Stop();
    }

    private int GetOffsetFromPointer(PointerEventArgs e, TextArea textArea)
    {
        Point pos = e.GetPosition(textArea.TextView);
        pos = pos + textArea.TextView.ScrollOffset;

        TextViewPosition? textViewPos = textArea.TextView.GetPosition(pos);
        if (textViewPos == null) return -1;

        int line = textViewPos.Value.Line;
        int column = textViewPos.Value.Column;

        if (line < 1 || line > textArea.Document.LineCount) return -1;

        var docLine = textArea.Document.GetLineByNumber(line);
        return docLine.Offset + Math.Min(column - 1, docLine.Length);
    }

    private void HoverTimer_Tick(object? sender, EventArgs e)
    {
        _hoverTimer!.Stop();

        if (_hoverPopup!.IsOpen)
            return;

        TextEditor? editor = _hoverEditor;
        if (editor == null) { HoverDbg("tick: no editor"); return; }

        if (DataContext is not UndertaleCodeViewModel vm)
        {
            HoverDbg("tick: DataContext not VM");
            return;
        }
        UndertaleData? data = vm.MainVM.Data;
        if (data == null) { HoverDbg("tick: no data"); return; }

        int offset = _hoverPendingOffset;
        if (offset < 0 || offset >= editor.Document.TextLength)
        {
            HoverDbg($"tick: bad offset {offset} (len={editor.Document.TextLength})");
            return;
        }

        try
        {
            int sectionStart = -1, sectionLength = 0;
            var hoverContent = BuildHoverContent(editor, offset, data, ref sectionStart, ref sectionLength);
            if (hoverContent == null)
            {
                HoverDbg($"tick: content null at offset {offset}");
                return;
            }

            _hoverSectionStart = sectionStart;
            _hoverSectionLength = sectionLength;
            _hoverPopup.Child = hoverContent;
            _hoverPopup.PlacementTarget = editor;
            HookGlobalDismiss(editor);
            _hoverPopup.IsOpen = true;
            HoverDbg($"tick: POPUP OPENED section=[{sectionStart}..{sectionStart + sectionLength})");
        }
        catch (Exception ex)
        {
            HoverDbg("tick: EXCEPTION " + ex);
        }
    }

    private void CloseHoverPopup()
    {
        UnhookGlobalDismiss();
        if (_hoverPopup != null && _hoverPopup.IsOpen)
            _hoverPopup.IsOpen = false;
        _hoverSectionStart = -1;
        _hoverSectionLength = 0;
        // Note: _lastHoverOffset intentionally kept here so that the pointer-moved handler can
        // suppress timer restarts for repeated moves over the same offset (high report-rate
        // mice emit several moves per character cell). It is reset when the pointer leaves.
    }

    private Border? BuildHoverContent(TextEditor editor, int offset, UndertaleData data, ref int sectionStart, ref int sectionLength)
    {
        IHighlighter? highlighter = editor.TextArea.TextView.GetService(typeof(IHighlighter)) as IHighlighter;
        if (highlighter == null) { HoverDbg("build: no highlighter"); return null; }

        int lineNum = editor.Document.GetLineByOffset(offset).LineNumber;
        HighlightedLine highlighted;
        try
        {
            highlighted = highlighter.HighlightLine(lineNum);
        }
        catch (Exception ex)
        {
            HoverDbg("build: HighlightLine threw " + ex.Message);
            return null;
        }

        var docLine = editor.Document.GetLineByNumber(lineNum);
        int lineStartOffset = docLine.Offset;
        int lineEndOffset = docLine.EndOffset;
        string lineText = editor.Document.GetText(docLine.Offset, docLine.Length);

        foreach (var section in highlighted.Sections)
        {
            if (section.Offset < lineStartOffset || section.Offset > lineEndOffset)
                continue;

            if (offset < section.Offset || offset >= section.Offset + section.Length)
                continue;

            string sectionText = editor.Document.GetText(section.Offset, section.Length);
            HoverDbg($"build: matched section '{section.Color.Name}' text='{sectionText}'");

            if (section.Color.Name == "Number")
            {
                sectionStart = section.Offset;
                sectionLength = section.Length;
                return AppendInferredArgumentHover(BuildNumberHoverContent(sectionText, data), sectionText, lineText, offset - lineStartOffset, data);
            }

            if (section.Color.Name == "Identifier" || section.Color.Name == "Function")
            {
                sectionStart = section.Offset;
                sectionLength = section.Length;
                return AppendInferredFunctionTypesHover(BuildNameHoverContent(sectionText, data, section.Color.Name == "Function"), sectionText, data);
            }
        }

        var names = string.Join(",", highlighted.Sections.Select(s => s.Color?.Name));
        HoverDbg($"build: no matching section at offset {offset} (sections: {names})");
        return null;
    }

    private Border? BuildNumberHoverContent(string numText, UndertaleData data)
    {
        if (!int.TryParse(numText, out int id))
            return null;

        List<UndertaleNamedResource?> possibleObjects = new();
        if (id >= 0)
        {
            if (id < data.Sprites.Count && data.Sprites[id] != null) possibleObjects.Add(data.Sprites[id]);
            if (id < data.Rooms.Count && data.Rooms[id] != null) possibleObjects.Add(data.Rooms[id]);
            if (id < data.GameObjects.Count && data.GameObjects[id] != null) possibleObjects.Add(data.GameObjects[id]);
            if (id < data.Backgrounds.Count && data.Backgrounds[id] != null) possibleObjects.Add(data.Backgrounds[id]);
            if (id < data.Scripts.Count && data.Scripts[id] != null) possibleObjects.Add(data.Scripts[id]);
            if (id < data.Paths.Count && data.Paths[id] != null) possibleObjects.Add(data.Paths[id]);
            if (id < data.Fonts.Count && data.Fonts[id] != null) possibleObjects.Add(data.Fonts[id]);
            if (id < data.Sounds.Count && data.Sounds[id] != null) possibleObjects.Add(data.Sounds[id]);
            if (id < data.Shaders.Count && data.Shaders[id] != null) possibleObjects.Add(data.Shaders[id]);
            if (id < data.Timelines.Count && data.Timelines[id] != null) possibleObjects.Add(data.Timelines[id]);
            if (id < (data.AnimationCurves?.Count ?? 0) && data.AnimationCurves[id] != null) possibleObjects.Add(data.AnimationCurves[id]);
            if (id < (data.Sequences?.Count ?? 0) && data.Sequences[id] != null) possibleObjects.Add(data.Sequences[id]);
            if (id < (data.ParticleSystems?.Count ?? 0) && data.ParticleSystems[id] != null) possibleObjects.Add(data.ParticleSystems[id]);
        }

        StackPanel panel = new() { MaxWidth = 320 };
        bool isDarkMode = IsDarkTheme;
        IBrush textBrush = isDarkMode ? HoverTextDark : HoverTextLight;
        IBrush subTextBrush = isDarkMode ? HoverSubTextDark : HoverSubTextLight;

        if (possibleObjects.Count > 0)
        {
            foreach (UndertaleNamedResource? obj in possibleObjects)
            {
                StackPanel row = new() { Orientation = Orientation.Horizontal };

                if (obj is UndertaleSprite sprite && sprite.Textures.Count > 0)
                {
                    var textureEntry = sprite.Textures[0];
                    if (textureEntry?.Texture != null)
                    {
                        try
                        {
                            Bitmap? imgSrc = (DataContext as UndertaleCodeViewModel)?.MainVM.ImageCache.GetCachedImageFromTexturePageItem(textureEntry.Texture);
                            if (imgSrc != null)
                            {
                                Image img = new()
                                {
                                    Source = imgSrc,
                                    MaxWidth = 64,
                                    MaxHeight = 64,
                                    Stretch = Stretch.Uniform,
                                    Margin = new Thickness(0, 2, 8, 2),
                                    VerticalAlignment = VerticalAlignment.Center
                                };
                                row.Children.Add(img);
                            }
                        }
                        catch { }
                    }
                }

                TextBlock text = new()
                {
                    Text = obj?.ToString().Replace("_", "__") ?? "",
                    Foreground = textBrush,
                    VerticalAlignment = VerticalAlignment.Center
                };
                row.Children.Add(text);
                panel.Children.Add(row);
            }
        }

        if (id > 0x00050000)
        {
            StackPanel colorRow = new() { Orientation = Orientation.Horizontal };
            Rectangle colorRect = new()
            {
                Width = 16,
                Height = 16,
                Fill = new SolidColorBrush(Color.FromRgb((byte)((id >> 16) & 0xFF), (byte)((id >> 8) & 0xFF), (byte)(id & 0xFF))),
                Stroke = isDarkMode ? Brushes.Gray : Brushes.DarkGray,
                StrokeThickness = 1,
                Margin = new Thickness(0, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            colorRow.Children.Add(colorRect);

            TextBlock colorText = new()
            {
                Text = string.Format(LocalizationSource.GetString("Editor_Color"), "0x" + id.ToString("X6")),
                Foreground = textBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            colorRow.Children.Add(colorText);
            panel.Children.Add(colorRow);
        }

        BuiltinList list = data.BuiltinList;
        var constKey = list.Constants.FirstOrDefault(x => x.Value == (double)id).Key;
        if (constKey != null)
        {
            TextBlock constText = new()
            {
                Text = string.Format(LocalizationSource.GetString("Editor_ConstantLabel"), constKey),
                Foreground = textBrush
            };
            panel.Children.Add(constText);
        }

        TextBlock numLabel = new()
        {
            Text = string.Format(LocalizationSource.GetString("Editor_NumberLabel"), id),
            Foreground = subTextBrush
        };
        panel.Children.Add(numLabel);

        if (panel.Children.Count == 1 && panel.Children[0] == numLabel)
            return null;

        return CreateHoverBorder(panel);
    }

    private Border? BuildNameHoverContent(string nameText, UndertaleData data, bool isFunc)
    {
        UndertaleNamedResource? val = null;

        if (isFunc)
        {
            if (!data.IsVersionAtLeast(2, 3))
                ScriptsCache.TryGetValue(nameText, out val);
            if (val == null)
            {
                FunctionsCache.TryGetValue(nameText, out val);
                if (data.IsVersionAtLeast(2, 3) && val != null)
                {
                    if (val.Name is not null && CodeCache.TryGetValue(val.Name.Content, out _))
                        val = null;
                }
            }
            if (val == null)
            {
                if (data.BuiltinList.Functions.ContainsKey(nameText) || GmlSpecLoader.GetFunction(nameText) != null)
                {
                    return BuildBuiltinFunctionHover(nameText);
                }
            }
        }
        else
        {
            NamedResourcesCache.TryGetValue(nameText, out val);
            if (data.IsVersionAtLeast(2, 3) && val is UndertaleScript)
                val = null;
        }

        if (val == null)
        {
            if (data.BuiltinList.Constants.ContainsKey(nameText) || GmlSpecLoader.GetConstant(nameText) != null)
            {
                return BuildBuiltinConstantHover(nameText);
            }

            if (data.BuiltinList.GlobalVars.ContainsKey(nameText) ||
                data.BuiltinList.InstanceVars.ContainsKey(nameText) ||
                data.BuiltinList.GlobalArrayVars.ContainsKey(nameText) ||
                GmlSpecLoader.GetVariable(nameText) != null)
            {
                return BuildBuiltinVariableHover(nameText);
            }

            if (!isFunc && GmlSpecLoader.GetFunction(nameText) != null)
            {
                return BuildBuiltinFunctionHover(nameText);
            }

            return null;
        }

        if (val is UndertaleFunction && GmlSpecLoader.GetFunction(nameText) != null)
        {
            return BuildBuiltinFunctionHover(nameText);
        }

        if (GmlSpecLoader.GetConstant(nameText) != null)
        {
            return BuildBuiltinConstantHover(nameText);
        }

        if (GmlSpecLoader.GetVariable(nameText) != null)
        {
            return BuildBuiltinVariableHover(nameText);
        }

        StackPanel panel = new() { MaxWidth = 320 };
        bool isDarkMode = IsDarkTheme;
        IBrush textBrush = isDarkMode ? HoverTextDark : HoverTextLight;
        IBrush subTextBrush = isDarkMode ? HoverSubTextDark : HoverSubTextLight;

        if (val is UndertaleSprite sprite && sprite.Textures.Count > 0)
        {
            var textureEntry = sprite.Textures[0];
            if (textureEntry?.Texture != null)
            {
                try
                {
                    Bitmap? imgSrc = (DataContext as UndertaleCodeViewModel)?.MainVM.ImageCache.GetCachedImageFromTexturePageItem(textureEntry.Texture);
                    if (imgSrc != null)
                    {
                        Image img = new()
                        {
                            Source = imgSrc,
                            MaxWidth = 128,
                            MaxHeight = 128,
                            Stretch = Stretch.Uniform,
                            Margin = new Thickness(0, 2, 0, 4)
                        };
                        panel.Children.Add(img);
                    }
                }
                catch { }
            }
        }

        TextBlock nameBlock = new()
        {
            Text = val.ToString().Replace("_", "__"),
            Foreground = textBrush,
            FontWeight = FontWeight.Bold
        };
        panel.Children.Add(nameBlock);

        TextBlock typeBlock = new()
        {
            Text = val.GetType().Name,
            Foreground = subTextBrush,
            FontSize = 11
        };
        panel.Children.Add(typeBlock);

        int resourceId = GetResourceId(data, val);
        if (resourceId >= 0)
        {
            TextBlock idBlock = new()
            {
                Text = string.Format(LocalizationSource.GetString("Editor_ResourceIdLabel"), resourceId),
                Foreground = subTextBrush,
                FontSize = 11
            };
            panel.Children.Add(idBlock);
        }

        return CreateHoverBorder(panel);
    }

    private static int GetResourceId(UndertaleData data, UndertaleNamedResource val)
    {
        if (val is UndertaleSound sound) return data.Sounds.IndexOf(sound);
        if (val is UndertaleSprite sprite) return data.Sprites.IndexOf(sprite);
        if (val is UndertaleBackground bg) return data.Backgrounds.IndexOf(bg);
        if (val is UndertalePath path) return data.Paths.IndexOf(path);
        if (val is UndertaleScript script) return data.Scripts.IndexOf(script);
        if (val is UndertaleFont font) return data.Fonts.IndexOf(font);
        if (val is UndertaleGameObject go) return data.GameObjects.IndexOf(go);
        if (val is UndertaleRoom room) return data.Rooms.IndexOf(room);
        if (val is UndertaleExtension ext) return data.Extensions.IndexOf(ext);
        if (val is UndertaleShader shader) return data.Shaders.IndexOf(shader);
        if (val is UndertaleTimeline tl) return data.Timelines.IndexOf(tl);
        if (val is UndertaleAnimationCurve ac) return data.AnimationCurves?.IndexOf(ac) ?? -1;
        if (val is UndertaleSequence seq) return data.Sequences?.IndexOf(seq) ?? -1;
        if (val is UndertaleAudioGroup ag) return data.AudioGroups.IndexOf(ag);
        return -1;
    }

    private Border? AppendInferredArgumentHover(Border? hoverContent, string numText, string lineText, int offsetInLine, UndertaleData data)
    {
        if (!int.TryParse(numText, out int numberValue))
            return hoverContent;

        if (data?.GameSpecificRegistry?.MacroResolver is not GlobalMacroTypeResolver resolver)
            return hoverContent;

        if (!TryFindCallArgument(lineText, offsetInLine, out string? functionName, out int argIndex))
            return hoverContent;

        if (GetResolvedFunctionArgType(resolver, functionName) is not IMacroTypeFunctionArgs argsType)
            return hoverContent;

        IMacroType?[]? perArgTypes = FunctionArgTypeInference.GetFunctionArgumentTypes(argsType);
        if (perArgTypes is null || argIndex >= perArgTypes.Length || perArgTypes[argIndex] is not IMacroType argType)
            return hoverContent;

        bool isDarkMode = IsDarkTheme;
        IBrush textBrush = isDarkMode ? HoverTextDark : HoverTextLight;
        IBrush typeBrush = isDarkMode
            ? new SolidColorBrush(Color.FromRgb(78, 201, 176))
            : new SolidColorBrush(Color.FromRgb(0, 128, 0));

        StackPanel? panel = hoverContent?.Child as StackPanel;
        if (panel is null)
        {
            panel = new StackPanel { MaxWidth = 320 };
        }

        TextBlock typeBlock = new()
        {
            Text = string.Format(LocalizationSource.GetString("Editor_InferredTypeLabel"), DescribeMacroType(argType)),
            Foreground = typeBrush,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(typeBlock);

        string? valueName = ResolveMacroValueName(argType, numberValue, data);
        if (valueName != null)
        {
            TextBlock valueBlock = new()
            {
                Text = string.Format(LocalizationSource.GetString("Editor_ArgValueLabel"), numberValue, valueName),
                Foreground = textBrush,
                FontWeight = FontWeight.Bold
            };
            panel.Children.Add(valueBlock);
        }

        if (hoverContent is null)
            return CreateHoverBorder(panel);
        return hoverContent;
    }

    private Border? AppendInferredFunctionTypesHover(Border? hoverContent, string? functionName, UndertaleData data)
    {
        if (hoverContent is null || functionName is null)
            return hoverContent;

        if (data?.GameSpecificRegistry?.MacroResolver is not GlobalMacroTypeResolver resolver)
            return hoverContent;

        if (GetResolvedFunctionArgType(resolver, functionName) is not IMacroTypeFunctionArgs argsType)
            return hoverContent;

        IMacroType?[]? perArgTypes = FunctionArgTypeInference.GetFunctionArgumentTypes(argsType);
        if (perArgTypes is null || perArgTypes.Length == 0)
            return hoverContent;

        string description = string.Join(", ", perArgTypes.Select(t => t is null ? "?" : DescribeMacroType(t)));
        if (description.Length > 120)
            description = description.Substring(0, 117) + "...";

        bool isDarkMode = IsDarkTheme;
        IBrush typeBrush = isDarkMode
            ? new SolidColorBrush(Color.FromRgb(78, 201, 176))
            : new SolidColorBrush(Color.FromRgb(0, 128, 0));

        StackPanel? panel = hoverContent.Child as StackPanel;
        if (panel is null)
            return hoverContent;

        TextBlock typesBlock = new()
        {
            Text = string.Format(LocalizationSource.GetString("Editor_ArgTypesLabel"), description),
            Foreground = typeBrush,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(typesBlock);

        return hoverContent;
    }

    private IMacroType? GetResolvedFunctionArgType(GlobalMacroTypeResolver resolver, string functionName)
    {
        string? entryName = (DataContext as UndertaleCodeViewModel)?.Code?.Name?.Content;
        return resolver.GetResolvedFunctionArgumentTypes(entryName, functionName, out _);
    }

    private static bool TryFindCallArgument(string lineText, int offsetInLine, out string? functionName, out int argIndex)
    {
        functionName = null;
        argIndex = -1;
        if (string.IsNullOrEmpty(lineText) || offsetInLine <= 0 || offsetInLine > lineText.Length)
            return false;

        // Find the nearest '(' before the offset whose matching ')' is at or beyond the offset
        int open = -1;
        for (int i = offsetInLine - 1; i >= 0; i--)
        {
            if (lineText[i] != '(')
                continue;

            int depth = 1;
            for (int j = i + 1; j < lineText.Length; j++)
            {
                char c = lineText[j];
                if (c == '(')
                    depth++;
                else if (c == ')')
                    depth--;

                if (depth == 0)
                {
                    if (j >= offsetInLine)
                    {
                        open = i;
                    }
                    break;
                }
            }
            if (open >= 0)
                break;
        }
        if (open < 0)
            return false;

        // Extract the identifier immediately before '(' as the function name
        int nameStart = open;
        while (nameStart > 0 && (char.IsLetterOrDigit(lineText[nameStart - 1]) || lineText[nameStart - 1] == '_' || lineText[nameStart - 1] == '$'))
            nameStart--;
        if (nameStart == open)
            return false;
        functionName = lineText.Substring(nameStart, open - nameStart);

        // Count top-level commas between the '(' and the offset
        int nesting = 0;
        int commas = 0;
        for (int i = open + 1; i < offsetInLine; i++)
        {
            char c = lineText[i];
            if (c == '(' || c == '[' || c == '{')
                nesting++;
            else if (c == ')' || c == ']' || c == '}')
                nesting--;
            else if (c == ',' && nesting == 0)
                commas++;
        }
        argIndex = commas;
        return true;
    }

    private static string DescribeMacroType(IMacroType type)
    {
        switch (type)
        {
            case FunctionArgsMacroType args:
                return args.ArgumentCount + " args";
            case UnionMacroType union:
                return DescribeMacroTypeList(union.GetTypes(), " | ");
            case IntersectMacroType intersect:
                return DescribeMacroTypeList(intersect.GetTypes(), " & ");
            case EnumMacroType enumType:
                return "Enum." + enumType.Name;
            case AssetMacroType asset:
                return "Asset." + asset.Type;
            case MatchMacroType match:
                return DescribeConditional(match.ConditionalTypeName, match.ConditionalValue, match.InnerType);
            case MatchNotMacroType match:
                return DescribeConditional(match.ConditionalTypeName, match.ConditionalValue, match.InnerType, negated: true);
            case ConditionalMacroType conditional:
                return conditional.InnerType != null ? DescribeMacroType(conditional.InnerType) : "Conditional";
            case ConstantsMacroType:
                return "Constants";
            case VirtualKeyMacroType:
                return "VirtualKey";
            case InstanceMacroType:
                return "Instance";
            case ColorMacroType:
                return "Color";
            case BooleanMacroType:
                return "Boolean";
            case NoneMacroType:
                return "None";
            case ArrayInitMacroType:
                return "ArrayInit";
            default:
                return type.GetType().Name;
        }
    }

    private static string DescribeConditional(string? typeName, string? value, IMacroType? innerType, bool negated = false)
    {
        string condition = (typeName ?? "?") + (negated ? " \u2260 " : " = ") + (value ?? "?");
        return innerType != null ? condition + ": " + DescribeMacroType(innerType) : condition;
    }

    private static string DescribeMacroTypeList(IReadOnlyList<IMacroType> types, string separator)
    {
        string description = string.Join(separator, types.Select(DescribeMacroType));
        if (description.Length > 60)
            description = description.Substring(0, 57) + "...";
        return description;
    }

    private static string? ResolveMacroValueName(IMacroType type, int value, UndertaleData data)
    {
        try
        {
            GlobalDecompileContext parseContext = GmlLanguageService.GetParseContext(data);
            UndertaleCode? anyCode = data.Code?.FirstOrDefault(x => x != null);
            if (anyCode is null)
                return null;

            DecompileContext context = new(parseContext, anyCode, new DecompileSettings());
            ASTCleaner cleaner = new(context);
            Int16Node node = new((short)value, true);
            IExpressionNode resolved = node.ResolveMacroType(cleaner, type);
            return resolved switch
            {
                MacroValueNode macro => macro.ValueName,
                AssetReferenceNode assetRef => parseContext.GetAssetName(assetRef.AssetType, assetRef.AssetId),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private Border CreateHoverBorder(Control content)
    {
        bool isDarkMode = IsDarkTheme;
        IBrush bgBrush = isDarkMode
            ? new SolidColorBrush(Color.FromRgb(0x26, 0x2A, 0x38))
            : new SolidColorBrush(Color.FromRgb(0xEF, 0xEC, 0xE4));
        IBrush borderBrush = isDarkMode
            ? new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x50))
            : new SolidColorBrush(Color.FromRgb(0xC9, 0xC3, 0xB4));

        return new Border
        {
            Background = bgBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 6, 8, 6),
            Child = content
        };
    }

    private Border? BuildBuiltinFunctionHover(string nameText)
    {
        bool isDarkMode = IsDarkTheme;
        IBrush textBrush = isDarkMode ? HoverTextDark : HoverTextLight;
        IBrush subTextBrush = isDarkMode ? HoverSubTextDark : HoverSubTextLight;
        IBrush paramBrush = isDarkMode
            ? new SolidColorBrush(Color.FromRgb(86, 156, 214))
            : new SolidColorBrush(Color.FromRgb(0, 0, 200));
        IBrush typeBrush = isDarkMode
            ? new SolidColorBrush(Color.FromRgb(78, 201, 176))
            : new SolidColorBrush(Color.FromRgb(0, 128, 0));

        var specFunc = GmlSpecLoader.GetFunction(nameText);

        StackPanel panel = new() { MaxWidth = 400 };

        StackPanel sigPanel = new() { Orientation = Orientation.Horizontal };
        TextBlock nameBlock = new()
        {
            Text = nameText,
            Foreground = textBrush,
            FontWeight = FontWeight.Bold
        };
        sigPanel.Children.Add(nameBlock);

        string parameters = "(";
        if (specFunc != null)
        {
            for (int i = 0; i < specFunc.Parameters.Count; i++)
            {
                var p = specFunc.Parameters[i];
                if (i > 0)
                    parameters += ", ";
                if (p.Optional)
                    parameters += "[";
                parameters += p.Name + ": " + p.Type;
                if (p.Optional)
                    parameters += "]";
            }
        }
        parameters += ")";
        if (specFunc != null && !string.IsNullOrEmpty(specFunc.ReturnType) && specFunc.ReturnType != "Undefined")
            parameters += " \u2192 " + specFunc.ReturnType;

        sigPanel.Children.Add(new TextBlock
        {
            Text = parameters,
            Foreground = paramBrush,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(sigPanel);

        if (specFunc != null && !string.IsNullOrEmpty(specFunc.Description))
        {
            panel.Children.Add(new Separator
            {
                Margin = new Thickness(0, 4, 0, 4),
                Background = isDarkMode
                    ? new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x50))
                    : new SolidColorBrush(Color.FromRgb(0xC9, 0xC3, 0xB4))
            });

            TextBlock descBlock = new()
            {
                Text = specFunc.Description,
                Foreground = subTextBrush,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11
            };
            panel.Children.Add(descBlock);

            if (specFunc.Parameters.Count > 0)
            {
                bool hasParamDesc = specFunc.Parameters.Any(p => !string.IsNullOrEmpty(p.Description));
                if (hasParamDesc)
                {
                    panel.Children.Add(new Separator
                    {
                        Margin = new Thickness(0, 4, 0, 2),
                        Background = isDarkMode
                            ? new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x50))
                            : new SolidColorBrush(Color.FromRgb(0xC9, 0xC3, 0xB4))
                    });

                    foreach (var p in specFunc.Parameters)
                    {
                        if (string.IsNullOrEmpty(p.Description)) continue;

                        TextBlock paramBlock = new()
                        {
                            Text = p.Name + ": " + p.Type + " \u2014 " + p.Description,
                            Foreground = subTextBrush,
                            TextWrapping = TextWrapping.Wrap,
                            FontSize = 11
                        };
                        panel.Children.Add(paramBlock);
                    }
                }
            }
        }
        else
        {
            TextBlock labelBlock = new()
            {
                Text = LocalizationSource.GetString("Editor_BuiltinFunction"),
                Foreground = subTextBrush,
                FontSize = 11
            };
            panel.Children.Add(labelBlock);
        }

        return CreateHoverBorder(panel);
    }

    private Border? BuildBuiltinVariableHover(string nameText)
    {
        bool isDarkMode = IsDarkTheme;
        IBrush textBrush = isDarkMode ? HoverTextDark : HoverTextLight;
        IBrush subTextBrush = isDarkMode ? HoverSubTextDark : HoverSubTextLight;
        IBrush typeBrush = isDarkMode
            ? new SolidColorBrush(Color.FromRgb(78, 201, 176))
            : new SolidColorBrush(Color.FromRgb(0, 128, 0));

        var specVar = GmlSpecLoader.GetVariable(nameText);

        StackPanel panel = new() { MaxWidth = 400 };

        string signature = nameText;
        if (specVar != null && !string.IsNullOrEmpty(specVar.Type))
            signature += ": " + specVar.Type;
        if (specVar != null)
        {
            string access = "";
            if (specVar.CanGet && specVar.CanSet) access = " { get; set; }";
            else if (specVar.CanGet) access = " { get; }";
            else if (specVar.CanSet) access = " { set; }";
            signature += access;
        }

        TextBlock sigBlock = new()
        {
            Text = signature,
            Foreground = textBrush,
            TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(sigBlock);

        if (specVar != null && !string.IsNullOrEmpty(specVar.Description))
        {
            panel.Children.Add(new Separator
            {
                Margin = new Thickness(0, 4, 0, 4),
                Background = isDarkMode
                    ? new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x50))
                    : new SolidColorBrush(Color.FromRgb(0xC9, 0xC3, 0xB4))
            });

            TextBlock descBlock = new()
            {
                Text = specVar.Description,
                Foreground = subTextBrush,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11
            };
            panel.Children.Add(descBlock);
        }
        else
        {
            TextBlock labelBlock = new()
            {
                Text = LocalizationSource.GetString("Editor_BuiltinVariable"),
                Foreground = subTextBrush,
                FontSize = 11
            };
            panel.Children.Add(labelBlock);
        }

        return CreateHoverBorder(panel);
    }

    private Border? BuildBuiltinConstantHover(string nameText)
    {
        bool isDarkMode = IsDarkTheme;
        IBrush textBrush = isDarkMode ? HoverTextDark : HoverTextLight;
        IBrush subTextBrush = isDarkMode ? HoverSubTextDark : HoverSubTextLight;
        IBrush typeBrush = isDarkMode
            ? new SolidColorBrush(Color.FromRgb(78, 201, 176))
            : new SolidColorBrush(Color.FromRgb(0, 128, 0));

        var specConst = GmlSpecLoader.GetConstant(nameText);

        StackPanel panel = new() { MaxWidth = 400 };

        string signature = nameText;
        if (specConst != null && !string.IsNullOrEmpty(specConst.Type))
            signature += ": " + specConst.Type;
        if (specConst != null && !string.IsNullOrEmpty(specConst.Class))
            signature += " (" + specConst.Class + ")";

        TextBlock sigBlock = new()
        {
            Text = signature,
            Foreground = textBrush,
            TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(sigBlock);

        if (specConst != null && !string.IsNullOrEmpty(specConst.Description))
        {
            panel.Children.Add(new Separator
            {
                Margin = new Thickness(0, 4, 0, 4),
                Background = isDarkMode
                    ? new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x50))
                    : new SolidColorBrush(Color.FromRgb(0xC9, 0xC3, 0xB4))
            });

            TextBlock descBlock = new()
            {
                Text = specConst.Description,
                Foreground = subTextBrush,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11
            };
            panel.Children.Add(descBlock);
        }
        else
        {
            TextBlock labelBlock = new()
            {
                Text = string.Format(LocalizationSource.GetString("Editor_ConstantLabel"), nameText),
                Foreground = subTextBrush,
                FontSize = 11
            };
            panel.Children.Add(labelBlock);
        }

        return CreateHoverBorder(panel);
    }

    // ---------- Commands (F12 / Shift+F12 / Ctrl+F / Ctrl+H / Ctrl+T) ----------

    private void Editor_KeyDown(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (ctrl && e.Key == Key.F)
        {
            e.Handled = true;
            OpenSearchPanel((TextEditor)sender!, replace: false);
        }
        else if (ctrl && e.Key == Key.H)
        {
            e.Handled = true;
            OpenSearchPanel((TextEditor)sender!, replace: true);
        }
        else if (e.Key == Key.F12 && !ctrl)
        {
            e.Handled = true;
            if (shift)
                HandleFindReferences();
            else
                HandleGoToDefinition();
        }
        else if (ctrl && e.Key == Key.T)
        {
            e.Handled = true;
            _ = HandleFindSymbol();
        }
    }

    private void OpenSearchPanel(TextEditor editor, bool replace)
    {
        var panel = editor.SearchPanel;
        if (panel is null)
            return;
        panel.IsReplaceMode = replace;
        panel.Open();
    }

    private void HandleGoToDefinition()
    {
        if (DataContext is not UndertaleCodeViewModel vm)
            return;
        if (vm.SelectedTab != Tab.GML)
            return;

        UndertaleData data = vm.MainVM.Data;
        if (data is null)
            return;

        string code = GMLTextEditor.Text;
        int offset = GMLTextEditor.CaretOffset;
        if (offset < 0 || offset > code.Length)
            return;

        GmlDefinition definition = GmlLanguageService.ResolveDefinition(
            data, code, offset, codeLocalsCache, vm.Code, NamedResourcesCache, ScriptsCache, FunctionsCache, CodeCache);

        switch (definition.Kind)
        {
            case GmlDefinition.DefinitionKind.Local:
            {
                int declOffset = definition.LocalDeclarationOffset;
                if (declOffset < 0)
                    break;
                if (declOffset < code.Length)
                    NavigateToEntry(declOffset);
                break;
            }
            case GmlDefinition.DefinitionKind.Function:
            {
                if (definition.LocalDeclarationOffset >= 0)
                {
                    NavigateToEntry(definition.LocalDeclarationOffset);
                    break;
                }
                if (definition.Resource is not null)
                    _ = vm.MainVM.TabOpen(definition.Resource, false);
                break;
            }
            case GmlDefinition.DefinitionKind.Resource:
            {
                if (definition.Resource is not null)
                    _ = vm.MainVM.TabOpen(definition.Resource, false);
                break;
            }
        }
    }

    private void HandleFindReferences()
    {
        if (DataContext is not UndertaleCodeViewModel vm)
            return;
        if (vm.SelectedTab != Tab.GML)
            return;

        string code = GMLTextEditor.Text;
        int offset = GMLTextEditor.CaretOffset;
        if (offset < 0 || offset > code.Length)
            return;

        IReadOnlyList<GmlReference> references = GmlLanguageService.FindReferences(code, offset, out string? symbol);
        if (symbol is null)
            return;

        _resultEntries.Clear();
        foreach (GmlReference reference in references)
        {
            _resultEntries.Add(new CodeEditorResultEntry
            {
                Offset = reference.TextPosition,
                Line = reference.Line,
                Column = 1,
                Display = $"{reference.Line}: {reference.LineText}",
                IsReference = true
            });
        }

        if (references.Count == 0)
        {
            ShowResultsPanel(string.Format(LocalizationSource.GetString("Editor_NoReferences"), symbol), ResultMode.References);
            return;
        }

        ShowResultsPanel(string.Format(LocalizationSource.GetString("Editor_ReferencesFound"), symbol, references.Count), ResultMode.References);
    }

    private async Task HandleFindSymbol()
    {
        if (DataContext is not UndertaleCodeViewModel vm)
            return;
        UndertaleData data = vm.MainVM.Data;
        if (data is null)
            return;

        Window? owner = WindowHost.ResolveOwner(this);
        SymbolSearchViewModel symbolVM = new(data);
        SymbolSearchWindow window = new()
        {
            DataContext = symbolVM
        };

        bool result = await WindowHost.ShowDialog<bool>(owner, window);
        if (result && symbolVM.SelectedResource is not null)
        {
            _ = vm.MainVM.TabOpen(symbolVM.SelectedResource, false);
        }
    }

    private void NavigateToEntry(int offset)
    {
        if (offset < 0 || offset > GMLTextEditor.Document.TextLength)
            return;
        GMLTextEditor.TextArea.Focus();
        GMLTextEditor.CaretOffset = offset;
        GMLTextEditor.ScrollToLine(GMLTextEditor.Document.GetLineByOffset(offset).LineNumber);
    }

    // ---------- Results panel ----------

    private void ShowResultsPanel(string header, ResultMode mode)
    {
        _lastResultMode = mode;
        _panelManuallyClosed = false;
        ResultsHeaderText.Text = header;
        ResultsListBox.ItemsSource = null;
        ResultsListBox.ItemsSource = _resultEntries;
        ResultsPanel.IsVisible = true;
    }

    private void HideResultsPanel()
    {
        _lastResultMode = ResultMode.None;
        _resultEntries.Clear();
        ResultsListBox.ItemsSource = null;
        ResultsPanel.IsVisible = false;
    }

    private void ResultsListBox_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ResultsListBox.SelectedItem is CodeEditorResultEntry entry)
            NavigateToEntry(entry.Offset);
    }

    private void ResultsCloseButton_Click(object? sender, RoutedEventArgs e)
    {
        _panelManuallyClosed = true;
        HideResultsPanel();
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// A single entry in the results panel (diagnostics or references).
    /// </summary>
    public class CodeEditorResultEntry
    {
        public int Offset { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public string Display { get; set; } = "";
        public bool IsReference { get; set; }
    }

    /// <summary>
    /// Completion item implementation shown by the AvaloniaEdit completion window.
    /// </summary>
    private sealed class GmlCompletionData : ICompletionData
    {
        private readonly GmlCompletionItem _item;

        public GmlCompletionData(GmlCompletionItem item)
        {
            _item = item;
        }

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            textArea.Document.Replace(completionSegment.Offset, completionSegment.Length, _item.Text);
        }

        public IImage Image => null!;

        public string Text => _item.Text;

        public object Content => new TextBlock
        {
            Text = _item.Text,
            Foreground = CompletionItemStyle.GetBrush(_item.Kind),
            VerticalAlignment = VerticalAlignment.Center
        };

        public object? Description => _item.Type;

        public double Priority => 0;
    }

    /// <summary>
    /// Colors used to visually distinguish completion item categories.
    /// </summary>
    private static class CompletionItemStyle
    {
        public static bool IsDark = true;

        public static IBrush GetBrush(string kind)
        {
            return new SolidColorBrush(GetColor(kind, IsDark));
        }

        private static Color GetColor(string kind, bool dark)
        {
            (Color DarkColor, Color LightColor) = kind switch
            {
                "function" => (Color.FromRgb(0xA5, 0x73, 0xE8), Color.FromRgb(0x7A, 0x3D, 0xB8)),
                "constant" => (Color.FromRgb(0xC9, 0xA6, 0xF2), Color.FromRgb(0x8A, 0x5C, 0xB8)),
                "variable" => (Color.FromRgb(0xC9, 0xA6, 0xF2), Color.FromRgb(0x8A, 0x5C, 0xB8)),
                "local" => (Color.FromRgb(0x8F, 0xC7, 0xFF), Color.FromRgb(0x1F, 0x6F, 0xB2)),
                "instance_var" => (Color.FromRgb(0x4A, 0x7A, 0xDE), Color.FromRgb(0x12, 0x3C, 0x96)),
                "global_var" => (Color.FromRgb(0x4D, 0xC9, 0xC9), Color.FromRgb(0x00, 0x8C, 0x8C)),
                "keyword" => (Color.FromRgb(0xF9, 0xB4, 0x6F), Color.FromRgb(0xB0, 0x5A, 0x00)),
                "user" => (Color.FromRgb(0x9D, 0xA5, 0xB4), Color.FromRgb(0x56, 0x59, 0x5E)),
                _ => (Color.FromRgb(0xFF, 0x6B, 0x6B), Color.FromRgb(0xC0, 0x19, 0x19)) // scripts and all asset kinds
            };
            return dark ? DarkColor : LightColor;
        }
    }

    // TODO: This code was mostly copied over, so it would be great if it could be made nicer. Or maybe do things differently.
    public class NumberGenerator : VisualLineElementGenerator
    {
        readonly UndertaleCodeView codeView;
        readonly ContextMenu contextMenu = new();

        // <offset, length>
        readonly Dictionary<int, int> lineNumberSections = [];

        public NumberGenerator(UndertaleCodeView codeView)
        {
            this.codeView = codeView;
            contextMenu.Placement = PlacementMode.Pointer;
        }

        public override void StartGeneration(ITextRunConstructionContext context)
        {
            base.StartGeneration(context);

            // Find sections of line that are highlighted as numbers
            lineNumberSections.Clear();

            DocumentLine documentLine = context.VisualLine.FirstDocumentLine;
            if (documentLine.Length != 0)
            {
                int line = documentLine.LineNumber;

                IHighlighter highlighter = (IHighlighter)CurrentContext.TextView.GetService(typeof(IHighlighter));
                HighlightedLine highlightedLine = highlighter.HighlightLine(line);

                foreach (var section in highlightedLine.Sections)
                {
                    if (section.Color.Name == "Number")
                        lineNumberSections[section.Offset] = section.Length;
                }
            }
        }

        public override int GetFirstInterestedOffset(int startOffset)
        {
            foreach ((int offset, _) in lineNumberSections)
            {
                if (startOffset <= offset)
                    return offset;
            }
            return -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            if (!lineNumberSections.TryGetValue(offset, out int length))
                return null;

            TextDocument document = CurrentContext.Document;
            TextView textView = CurrentContext.TextView;
            TextEditor textEditor = (TextEditor)textView.GetService(typeof(TextEditor));

            UndertaleCodeViewModel codeViewModel = (UndertaleCodeViewModel)codeView.DataContext!;
            UndertaleData data = codeViewModel.MainVM.Data!;

            string text = document.GetText(offset, length);
            ClickVisualLineText visualLine = new(text, CurrentContext.VisualLine, length);

            visualLine.Clicked += bool (string text, MouseButton button, bool controlKey) =>
            {
                if (!textEditor.TextArea.IsFocused)
                    return false;

                if (button != MouseButton.Right)
                    return false;

                if (!int.TryParse(text, out int id))
                    return false;

                int documentOffset = visualLine.ParentVisualLine.StartOffset + visualLine.RelativeTextOffset;

                List<UndertaleNamedResource?> possibleObjects = [];

                if (id >= 0)
                {
                    // NOTE: Remember to add new types
                    if (id < data.Sprites.Count)
                        possibleObjects.Add(data.Sprites[id]);
                    if (id < data.Rooms.Count)
                        possibleObjects.Add(data.Rooms[id]);
                    if (id < data.GameObjects.Count)
                        possibleObjects.Add(data.GameObjects[id]);
                    if (id < data.Backgrounds.Count)
                        possibleObjects.Add(data.Backgrounds[id]);
                    if (id < data.Scripts.Count)
                        possibleObjects.Add(data.Scripts[id]);
                    if (id < data.Paths.Count)
                        possibleObjects.Add(data.Paths[id]);
                    if (id < data.Fonts.Count)
                        possibleObjects.Add(data.Fonts[id]);
                    if (id < data.Sounds.Count)
                        possibleObjects.Add(data.Sounds[id]);
                    if (id < data.Shaders.Count)
                        possibleObjects.Add(data.Shaders[id]);
                    if (id < data.Timelines.Count)
                        possibleObjects.Add(data.Timelines[id]);
                    if (id < data.AnimationCurves?.Count)
                        possibleObjects.Add(data.AnimationCurves[id]);
                    if (id < data.Sequences?.Count)
                        possibleObjects.Add(data.Sequences[id]);
                    if (id < data.ParticleSystems?.Count)
                        possibleObjects.Add(data.ParticleSystems[id]);
                }

                contextMenu.Items.Clear();

                foreach (UndertaleNamedResource? obj in possibleObjects)
                {
                    if (obj?.Name is null)
                        continue;

                    MenuItem item = new();
                    item.Header = obj.ToString()?.Replace("_", "__");
                    item.Click += (_, _) =>
                    {
                        document.Replace(documentOffset, text.Length, obj.Name.Content, null);
                    };
                    contextMenu.Items.Add(item);
                }

                if (id >= 0)
                {
                    string color = "0x" + id.ToString("X6");

                    MenuItem item = new();
                    item.Header = color + " " + LocalizationSource.GetString("Editor_ColorSuffix");
                    item.Click += (_, _) =>
                    {
                        document.Replace(documentOffset, text.Length, color, null);
                    };
                    contextMenu.Items.Add(item);
                }

                BuiltinList list = data.BuiltinList;

                MenuItem constantsMenuItem = new();
                constantsMenuItem.Header = LocalizationSource.GetString("Editor_ConstantsMenu");

                foreach (var (constantName, constantValue) in list.Constants)
                {
                    if (constantValue == id)
                    {
                        MenuItem item = new();
                        item.Header = constantName.Replace("_", "__");
                        item.Click += (_, _) =>
                        {
                            document.Replace(documentOffset, text.Length, constantName, null);
                        };
                        constantsMenuItem.Items.Add(item);
                    }
                }

                if (constantsMenuItem.Items.Count > 0)
                    contextMenu.Items.Add(constantsMenuItem);

                contextMenu.Items.Add(new MenuItem() { Header = id + " " + LocalizationSource.GetString("Editor_NumberSuffix"), IsEnabled = false });

                codeViewModel.GMLFocused = false;
                codeViewModel.ASMFocused = false;

                contextMenu.Open(textEditor);

                return true;
            };

            return visualLine;
        }
    }

    public class NameGenerator : VisualLineElementGenerator
    {
        static readonly SolidColorBrush FunctionBrushDark = new(Color.FromRgb(0xFF, 0xB8, 0x71));
        static readonly SolidColorBrush FunctionBrushLight = new(Color.FromRgb(0x79, 0x5E, 0x26));
        static readonly SolidColorBrush GlobalBrushDark = new(Color.FromRgb(0xF9, 0x7B, 0xF9));
        static readonly SolidColorBrush GlobalBrushLight = new(Color.FromRgb(0x9A, 0x5E, 0x9A));
        static readonly SolidColorBrush ConstantBrushDark = new(Color.FromRgb(0xFF, 0x80, 0x80));
        static readonly SolidColorBrush ConstantBrushLight = new(Color.FromRgb(0xC0, 0x00, 0x00));
        static readonly SolidColorBrush InstanceBrushDark = new(Color.FromRgb(0x58, 0xE3, 0x5A));
        static readonly SolidColorBrush InstanceBrushLight = new(Color.FromRgb(0x2E, 0x7D, 0x32));
        static readonly SolidColorBrush LocalBrushDark = new(Color.FromRgb(0xFF, 0xF8, 0x99));
        static readonly SolidColorBrush LocalBrushLight = new(Color.FromRgb(0x9A, 0x67, 0x00));

        readonly UndertaleCodeView codeView;
        readonly ContextMenu contextMenu = new();

        // <offset, length>
        readonly Dictionary<int, int> lineNameSections = [];

        public NameGenerator(UndertaleCodeView codeView)
        {
            this.codeView = codeView;
            contextMenu.Placement = PlacementMode.Pointer;
        }

        SolidColorBrush FunctionBrush => codeView.IsDarkTheme ? FunctionBrushDark : FunctionBrushLight;
        SolidColorBrush GlobalBrush => codeView.IsDarkTheme ? GlobalBrushDark : GlobalBrushLight;
        SolidColorBrush ConstantBrush => codeView.IsDarkTheme ? ConstantBrushDark : ConstantBrushLight;
        SolidColorBrush InstanceBrush => codeView.IsDarkTheme ? InstanceBrushDark : InstanceBrushLight;
        SolidColorBrush LocalBrush => codeView.IsDarkTheme ? LocalBrushDark : LocalBrushLight;

        public override void StartGeneration(ITextRunConstructionContext context)
        {
            base.StartGeneration(context);

            // Find sections of line that are highlighted as identifiers or functions
            lineNameSections.Clear();

            DocumentLine documentLine = context.VisualLine.FirstDocumentLine;
            if (documentLine.Length != 0)
            {
                int line = documentLine.LineNumber;

                IHighlighter highlighter = (IHighlighter)CurrentContext.TextView.GetService(typeof(IHighlighter));
                HighlightedLine highlightedLine = highlighter.HighlightLine(line);

                foreach (var section in highlightedLine.Sections)
                {
                    if (section.Color.Name == "Identifier" || section.Color.Name == "Function")
                        lineNameSections[section.Offset] = section.Length;
                }
            }
        }

        public override int GetFirstInterestedOffset(int startOffset)
        {
            foreach ((int offset, _) in lineNameSections)
            {
                if (startOffset <= offset)
                    return offset;
            }
            return -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            if (!lineNameSections.TryGetValue(offset, out int length))
                return null;

            TextDocument document = CurrentContext.Document;
            TextView textView = CurrentContext.TextView;
            TextEditor textEditor = (TextEditor)textView.GetService(typeof(TextEditor));

            UndertaleCodeViewModel codeViewModel = (UndertaleCodeViewModel)codeView.DataContext!;
            UndertaleData data = codeViewModel.MainVM.Data!;

            string text = document.GetText(offset, length);

            bool isFunction = (offset + length + 1 < CurrentContext.VisualLine.LastDocumentLine.EndOffset) &&
                (document.GetCharAt(offset + length) == '(');

            UndertaleNamedResource? namedResource = null;
            bool nonResourceReference = false;

            // Process the content of this identifier/function
            if (isFunction)
            {
                namedResource = null;

                if (!data.IsVersionAtLeast(2, 3)) // in GMS2.3 every custom "function" is in fact a member variable and scripts are never referenced directly
                    ScriptsCache.TryGetValue(text, out namedResource);

                if (namedResource == null)
                {
                    FunctionsCache.TryGetValue(text, out namedResource);
                    if (data.IsVersionAtLeast(2, 3))
                    {
                        if (namedResource != null)
                        {
                            if (namedResource.Name is not null && CodeCache.TryGetValue(namedResource.Name.Content, out _))
                                namedResource = null; // in GMS2.3 every custom "function" is in fact a member variable, and the names in functions make no sense (they have the gml_Script_ prefix)
                        }
                        else
                        {
                            // Resolve 2.3 sub-functions for their parent entry
                            if (data.GlobalFunctions?.TryGetFunction(text, out Underanalyzer.IGMFunction? f) == true)
                            {
                                ScriptsCache.TryGetValue(f.Name.Content, out namedResource);
                                namedResource = (namedResource as UndertaleScript)?.Code?.ParentEntry;
                            }
                        }
                    }
                }
                if (namedResource == null)
                {
                    if (data.BuiltinList.Functions.ContainsKey(text))
                    {
                        ColorVisualLineText res = new(text, CurrentContext.VisualLine, length, FunctionBrush);
                        res.Bold = true;
                        return res;
                    }
                }
            }
            else
            {
                NamedResourcesCache.TryGetValue(text, out namedResource);
                if (data.IsVersionAtLeast(2, 3))
                {
                    if (namedResource is UndertaleScript)
                        namedResource = null; // in GMS2.3 scripts are never referenced directly

                    if (data.GlobalFunctions?.TryGetFunction(text, out Underanalyzer.IGMFunction? globalFunc) == true &&
                        globalFunc is UndertaleFunction utGlobalFunc)
                    {
                        // Try getting script that this function reference belongs to
                        if (NamedResourcesCache.TryGetValue("gml_Script_" + text, out namedResource) && namedResource is UndertaleScript script)
                        {
                            // Highlight like a function as well
                            namedResource = script.Code;
                            isFunction = true;
                        }
                    }

                    if (namedResource == null)
                    {
                        // Try to get basic function
                        if (FunctionsCache.TryGetValue(text, out namedResource))
                        {
                            isFunction = true;
                        }
                    }

                    if (namedResource == null)
                    {
                        // Try resolving to room instance ID
                        string instanceIdPrefix = data.ToolInfo.InstanceIdPrefix();
                        if (text.StartsWith(instanceIdPrefix) &&
                            int.TryParse(text[instanceIdPrefix.Length..], out int id) && id >= 100000)
                        {
                            // TODO: We currently mark this as a non-resource reference, but ideally
                            // we resolve this to the room that this instance ID occurs in.
                            // However, we should only do this when actually clicking on it.
                            nonResourceReference = true;
                        }
                    }
                }
            }
            if (namedResource == null && !nonResourceReference)
            {
                // Check for variable name colors
                if (offset >= 7)
                {
                    if (document.GetText(offset - 7, 7) == "global.")
                    {
                        return new ColorVisualLineText(text, CurrentContext.VisualLine, length, GlobalBrush);
                    }
                }
                if (data.BuiltinList.Constants.ContainsKey(text))
                    return new ColorVisualLineText(text, CurrentContext.VisualLine, length, ConstantBrush);
                if (data.BuiltinList.GlobalVars.ContainsKey(text) ||
                    data.BuiltinList.InstanceVars.ContainsKey(text) ||
                    data.BuiltinList.GlobalArrayVars.ContainsKey(text))
                    return new ColorVisualLineText(text, CurrentContext.VisualLine, length, InstanceBrush);
                if (codeView.codeLocalsCache.BinarySearch(text) >= 0)
                    return new ColorVisualLineText(text, CurrentContext.VisualLine, length, LocalBrush);
                return null;
            }

            ClickVisualLineText visualLine = new(text, CurrentContext.VisualLine, length, isFunction ? FunctionBrush : ConstantBrush);
            if (isFunction)
            {
                // Make function references bold as well as a different color
                visualLine.Bold = true;
            }
            if (namedResource is not null)
            {
                // Add click operation when we have a resource
                visualLine.Clicked += bool (string text, MouseButton button, bool controlKey) =>
                {
                    if (!textEditor.TextArea.IsFocused)
                        return false;

                    if (button == MouseButton.Right)
                    {
                        contextMenu.Items.Clear();

                        MenuItem openMenuItem = new();
                        openMenuItem.Header = LocalizationSource.GetString("Common_Open");
                        openMenuItem.Click += (_, _) =>
                        {
                            textEditor.TextArea.Focus();
                            _ = codeViewModel.MainVM.TabOpen(namedResource, false);
                        };
                        contextMenu.Items.Add(openMenuItem);

                        MenuItem openInNewTabMenuItem = new();
                        openInNewTabMenuItem.Header = LocalizationSource.GetString("Common_OpenInNewTab");
                        openInNewTabMenuItem.Click += (_, _) =>
                        {
                            textEditor.TextArea.Focus();
                            _ = codeViewModel.MainVM.TabOpen(namedResource, true);
                        };
                        contextMenu.Items.Add(openInNewTabMenuItem);

                        codeViewModel.GMLFocused = false;
                        codeViewModel.ASMFocused = false;

                        contextMenu.Open(textEditor);
                        return true;
                    }
                    if (button == MouseButton.Middle || (button == MouseButton.Left && controlKey))
                    {
                        textEditor.TextArea.Focus();
                        _ = codeViewModel.MainVM.TabOpen(namedResource, true);
                        return true;
                    }
                    return false;
                };
            }

            return visualLine;
        }
    }

    public class ColorVisualLineText : VisualLineText
    {
        private string Text { get; set; }
        private Brush? ForegroundBrush { get; set; }
        public bool Bold { get; set; } = false;

        public ColorVisualLineText(string text, VisualLine parentVisualLine, int length, Brush? foregroundBrush) : base(parentVisualLine, length)
        {
            Text = text;
            ForegroundBrush = foregroundBrush;
        }

        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
        {
            if (ForegroundBrush != null)
                TextRunProperties.SetForegroundBrush(ForegroundBrush);
            if (Bold)
                TextRunProperties.SetTypeface(new Typeface(TextRunProperties.Typeface.FontFamily, FontStyle.Normal, FontWeight.Bold, FontStretch.Normal));
            return base.CreateTextRun(startVisualColumn, context);
        }

        protected override VisualLineText CreateInstance(int length)
        {
            return new ColorVisualLineText(Text, ParentVisualLine, length, null);
        }
    }

    public class ClickVisualLineText : VisualLineText
    {
        public delegate bool ClickHandler(string text, MouseButton button, bool controlKey);
        public event ClickHandler? Clicked;

        private string Text { get; set; }
        private Brush? ForegroundBrush { get; set; }
        public bool Bold { get; set; } = false;

        public ClickVisualLineText(string text, VisualLine parentVisualLine, int length, Brush? foregroundBrush = null) : base(parentVisualLine, length)
        {
            Text = text;
            ForegroundBrush = foregroundBrush;
        }

        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
        {
            if (ForegroundBrush != null)
                TextRunProperties.SetForegroundBrush(ForegroundBrush);
            if (Bold)
                TextRunProperties.SetTypeface(new Typeface(TextRunProperties.Typeface.FontFamily, FontStyle.Normal, FontWeight.Bold, FontStretch.Normal));
            return base.CreateTextRun(startVisualColumn, context);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            MouseButton button = e.GetCurrentPoint(null).Properties.PointerUpdateKind.GetMouseButton();

            if (Clicked != null)
            {
                bool controlKey = e.KeyModifiers.HasFlag(KeyModifiers.Control);
                if (Clicked(Text, button, controlKey))
                {
                    e.Handled = true;
                }
            }
        }

        protected override VisualLineText CreateInstance(int length)
        {
            ClickVisualLineText res = new(Text, ParentVisualLine, length);
            res.Clicked += Clicked;
            return res;
        }
    }
}