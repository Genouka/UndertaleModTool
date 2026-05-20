using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.TextFormatting;
using System.Windows.Navigation;
using System.Windows.Shell;
using System.Xml;
using UndertaleModLib;
using UndertaleModLib.Compiler;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;
using UndertaleModLib.Project;
using UndertaleModTool.Localization;
using Input = System.Windows.Input;

namespace UndertaleModTool
{
    /// <summary>
    /// Logika interakcji dla klasy UndertaleCodeEditor.xaml
    /// </summary>
    [SupportedOSPlatform("windows7.0")]
    public partial class UndertaleCodeEditor : DataUserControl
    {
        private static readonly MainWindow mainWindow = Application.Current.MainWindow as MainWindow;

        public UndertaleCode CurrentDisassembled = null;
        public UndertaleCode CurrentDecompiled = null;
        public List<string> CurrentLocals = new();

        public bool DecompiledFocused = false;
        public bool DecompiledChanged = false;
        public bool DecompiledYet = false;
        public bool DecompiledSkipped = false;
        public static (int Line, int Column, double ScrollPos) OverriddenDecompPos { get; set; }

        public bool DisassemblyFocused = false;
        public bool DisassemblyChanged = false;
        public bool DisassembledYet = false;
        public bool DisassemblySkipped = false;
        public static (int Line, int Column, double ScrollPos) OverriddenDisasmPos { get; set; }

        private readonly ModifiedLinesBackgroundRenderer _decompiledModifiedRenderer = new();
        private readonly ModifiedLinesBackgroundRenderer _disassemblyModifiedRenderer = new();
        private bool _isLoadingCode;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UndertaleCode, List<UndertaleInstruction>> _originalBytecodeSnapshots = new();

        private static void SaveOriginalBytecodeSnapshot(UndertaleCode code)
        {
            if (!(Settings.Instance?.ChangeTrackingEnabled ?? true))
                return;

            if (code?.Instructions == null)
                return;

            if (!_originalBytecodeSnapshots.TryGetValue(code, out _))
                _originalBytecodeSnapshots.Add(code, new List<UndertaleInstruction>(code.Instructions));
        }

        private static List<UndertaleInstruction> GetOriginalBytecodeSnapshot(UndertaleCode code)
        {
            if (code == null)
                return null;

            _originalBytecodeSnapshots.TryGetValue(code, out var snapshot);
            return snapshot;
        }

        private static bool BytecodeEquals(List<UndertaleInstruction> snapshot, UndertaleCode code)
        {
            if (snapshot == null || code?.Instructions == null)
                return snapshot == null && code?.Instructions == null;

            if (snapshot.Count != code.Instructions.Count)
                return false;

            for (int i = 0; i < snapshot.Count; i++)
            {
                if (!InstructionEquals(snapshot[i], code.Instructions[i]))
                    return false;
            }

            return true;
        }

        private static bool InstructionEquals(UndertaleInstruction a, UndertaleInstruction b)
        {
            if (ReferenceEquals(a, b))
                return true;
            if (a == null || b == null)
                return false;

            if (a.Kind != b.Kind || a.Type1 != b.Type1 || a.Type2 != b.Type2)
                return false;

            var type = UndertaleInstruction.GetInstructionType(a.Kind);

            if (type == UndertaleInstruction.InstructionType.ComparisonInstruction)
            {
                if (a.ComparisonKind != b.ComparisonKind)
                    return false;
            }
            else if (type == UndertaleInstruction.InstructionType.GotoInstruction)
            {
                if (a.JumpOffset != b.JumpOffset)
                    return false;
            }
            else if (type == UndertaleInstruction.InstructionType.PopInstruction || type == UndertaleInstruction.InstructionType.PushInstruction)
            {
                if (a.TypeInst != b.TypeInst)
                    return false;
            }
            else if (type == UndertaleInstruction.InstructionType.CallInstruction)
            {
                if (a.ArgumentsCount != b.ArgumentsCount)
                    return false;
            }
            else if (a.Kind == UndertaleInstruction.Opcode.Dup)
            {
                if (a.Extra != b.Extra)
                    return false;
            }

            if (a.ValueVariable != null || b.ValueVariable != null)
            {
                if (!ReferenceEquals(a.ValueVariable, b.ValueVariable))
                    return false;
            }
            else if (a.ValueFunction != null || b.ValueFunction != null)
            {
                if (!ReferenceEquals(a.ValueFunction, b.ValueFunction))
                    return false;
            }
            else if (a.ValueString != null || b.ValueString != null)
            {
                if (!ReferenceEquals(a.ValueString?.Resource, b.ValueString?.Resource))
                    return false;
            }
            else
            {
                if (type == UndertaleInstruction.InstructionType.PushInstruction)
                {
                    if (a.Kind == UndertaleInstruction.Opcode.PushI)
                    {
                        if (a.ValueShort != b.ValueShort)
                            return false;
                    }
                    else if (a.Type1 == UndertaleInstruction.DataType.Int64)
                    {
                        if (a.ValueLong != b.ValueLong)
                            return false;
                    }
                    else if (a.Type1 == UndertaleInstruction.DataType.Double)
                    {
                        if (a.ValueDouble != b.ValueDouble)
                            return false;
                    }
                    else if (a.Type1 == UndertaleInstruction.DataType.Int32)
                    {
                        if (a.ValueInt != b.ValueInt)
                            return false;
                    }
                }
                else if (type == UndertaleInstruction.InstructionType.BreakInstruction)
                {
                    if (a.Type1 == UndertaleInstruction.DataType.Int32 && a.ValueInt != b.ValueInt)
                        return false;
                }
            }

            return true;
        }

        public static RoutedUICommand Compile = new RoutedUICommand("Compile code", "Compile", typeof(UndertaleCodeEditor));
        public static RoutedUICommand OpenFindCommand = new RoutedUICommand("Open find", "OpenFind", typeof(UndertaleCodeEditor));
        public static RoutedUICommand OpenReplaceCommand = new RoutedUICommand("Open replace", "OpenReplace", typeof(UndertaleCodeEditor));

        private static readonly Dictionary<string, UndertaleNamedResource> NamedObjDict = new();
        private static readonly Dictionary<string, UndertaleNamedResource> ScriptsDict = new();
        private static readonly Dictionary<string, UndertaleNamedResource> FunctionsDict = new();
        private static readonly Dictionary<string, UndertaleNamedResource> CodeDict = new();

        private static double LastZoomFontSize = 14;
        public double ZoomFontSize = LastZoomFontSize;
        public static double OverriddenZoomFontSize = 0;

        public enum CodeEditorTab
        {
            Unknown,
            Disassembly,
            Decompiled
        }
        public static CodeEditorTab EditorTab { get; set; } = CodeEditorTab.Unknown;

        private System.Windows.Threading.DispatcherTimer _hoverTimer;
        private Popup _hoverPopup;
        private TextArea _hoverTextArea;
        private int _hoverSectionStart = -1;
        private int _hoverSectionLength = 0;
        private int _lastHoverOffset = -1;
        private const int HoverDelayMs = 250;

        public UndertaleCodeEditor()
        {
            InitializeComponent();

            ApplySettingsToEditors();

            // Decompiled editor styling and functionality
            DecompiledSearchReplacePanel.Initialize(DecompiledEditor.TextArea);
            DecompiledEditor.FontSize = ZoomFontSize;

            using (Stream stream = this.GetType().Assembly.GetManifestResourceStream("UndertaleModTool.Resources.GML.xshd"))
            {
                using (XmlTextReader reader = new XmlTextReader(stream))
                {
                    DecompiledEditor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
                    var def = DecompiledEditor.SyntaxHighlighting;
                    if (mainWindow.Data.GeneralInfo.Major < 2)
                    {
                        foreach (var span in def.MainRuleSet.Spans)
                        {
                            string expr = span.StartExpression.ToString();
                            if (expr == "\"" || expr == "'")
                            {
                                span.RuleSet.Spans.Clear();
                            }
                        }
                    }
                    // This was an attempt to only highlight
                    // GMS 2.3+ keywords if the game is
                    // made in such a version.
                    // However despite what StackOverflow
                    // says, this isn't working so it's just
                    // hardcoded in the XML for now
                    /*
                    if(mainWindow.Data.IsVersionAtLeast(2, 3))
                    {
                        HighlightingColor color = null;
                        foreach (var rule in def.MainRuleSet.Rules)
                        {
                            if (rule.Regex.IsMatch("if"))
                            {
                                color = rule.Color;
                                break;
                            }
                        }
                        if (color != null)
                        {
                            string[] keywords =
                            {
                                "new",
                                "function",
                                "keywords"
                            };
                            var rule = new HighlightingRule();
                            var regex = String.Format(@"\b(?>{0})\b", String.Join("|", keywords));

                            rule.Regex = new Regex(regex);
                            rule.Color = color;

                            def.MainRuleSet.Rules.Add(rule);
                        }
                    }*/
                }
            }

            DecompiledEditor.Options.ConvertTabsToSpaces = true;

            TextArea textArea = DecompiledEditor.TextArea;
            textArea.TextView.ElementGenerators.Add(new NumberGenerator(this, textArea));
            textArea.TextView.ElementGenerators.Add(new NameGenerator(this, textArea));
            if (Settings.Instance?.ChangeTrackingEnabled ?? true)
                textArea.TextView.BackgroundRenderers.Add(_decompiledModifiedRenderer);

            textArea.TextView.Options.HighlightCurrentLine = true;
            textArea.TextView.CurrentLineBackground = new SolidColorBrush(Color.FromRgb(60, 60, 60));
            textArea.TextView.CurrentLineBorder = new Pen() { Thickness = 0 };

            DecompiledEditor.Document.TextChanged += (s, e) =>
            {
                if (_isLoadingCode)
                    return;
                DecompiledFocused = true;
                DecompiledChanged = true;
                if (Settings.Instance?.ChangeTrackingEnabled ?? true)
                    _decompiledModifiedRenderer.MarkDirty();
            };

            textArea.SelectionBrush = new SolidColorBrush(Color.FromRgb(100, 100, 100));
            textArea.SelectionForeground = null;
            textArea.SelectionBorder = null;
            textArea.SelectionCornerRadius = 0;

            // Disassembly editor styling and functionality
            DisassemblySearchReplacePanel.Initialize(DisassemblyEditor.TextArea);
            DisassemblyEditor.FontSize = ZoomFontSize;

            using (Stream stream = this.GetType().Assembly.GetManifestResourceStream("UndertaleModTool.Resources.VMASM.xshd"))
            {
                using (XmlTextReader reader = new XmlTextReader(stream))
                {
                    DisassemblyEditor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
                }
            }

            textArea = DisassemblyEditor.TextArea;
            textArea.TextView.ElementGenerators.Add(new NameGenerator(this, textArea));
            if (Settings.Instance?.ChangeTrackingEnabled ?? true)
                textArea.TextView.BackgroundRenderers.Add(_disassemblyModifiedRenderer);

            textArea.TextView.Options.HighlightCurrentLine = true;
            textArea.TextView.CurrentLineBackground = new SolidColorBrush(Color.FromRgb(60, 60, 60));
            textArea.TextView.CurrentLineBorder = new Pen() { Thickness = 0 };

            DisassemblyEditor.Document.TextChanged += (s, e) =>
            {
                if (_isLoadingCode)
                    return;
                DisassemblyChanged = true;
                if (Settings.Instance?.ChangeTrackingEnabled ?? true)
                    _disassemblyModifiedRenderer.MarkDirty();
            };

            textArea.SelectionBrush = new SolidColorBrush(Color.FromRgb(100, 100, 100));
            textArea.SelectionForeground = null;
            textArea.SelectionBorder = null;
            textArea.SelectionCornerRadius = 0;

            InitializeHoverPopup();
        }

        private void ApplySettingsToEditors()
        {
            var settings = Settings.Instance;
            if (settings == null) return;

            WordWrapCheck.IsChecked = settings.CodeEditorWordWrap;
            ShowWhitespaceCheck.IsChecked = settings.CodeEditorShowWhitespace;
            ShowHoverInfoCheck.IsChecked = settings.CodeEditorShowHoverInfo;

            DecompiledEditor.WordWrap = settings.CodeEditorWordWrap;
            DisassemblyEditor.WordWrap = settings.CodeEditorWordWrap;

            DecompiledEditor.Options.ShowSpaces = settings.CodeEditorShowWhitespace;
            DecompiledEditor.Options.ShowTabs = settings.CodeEditorShowWhitespace;
            DisassemblyEditor.Options.ShowSpaces = settings.CodeEditorShowWhitespace;
            DisassemblyEditor.Options.ShowTabs = settings.CodeEditorShowWhitespace;
        }

        private void WordWrapCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (DecompiledEditor == null) return;
            bool value = WordWrapCheck.IsChecked ?? true;
            DecompiledEditor.WordWrap = value;
            DisassemblyEditor.WordWrap = value;
            if (Settings.Instance != null)
            {
                Settings.Instance.CodeEditorWordWrap = value;
                Settings.Save();
            }
        }

        private void ShowWhitespaceCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (DecompiledEditor == null) return;
            bool value = ShowWhitespaceCheck.IsChecked ?? false;
            DecompiledEditor.Options.ShowSpaces = value;
            DecompiledEditor.Options.ShowTabs = value;
            DisassemblyEditor.Options.ShowSpaces = value;
            DisassemblyEditor.Options.ShowTabs = value;
            if (Settings.Instance != null)
            {
                Settings.Instance.CodeEditorShowWhitespace = value;
                Settings.Save();
            }
        }

        private void ShowHoverInfoCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (Settings.Instance == null) return;
            bool value = ShowHoverInfoCheck.IsChecked ?? true;
            Settings.Instance.CodeEditorShowHoverInfo = value;
            Settings.Save();
            if (!value)
                CloseHoverPopup();
        }

        private void InitializeHoverPopup()
        {
            _hoverPopup = new Popup
            {
                StaysOpen = true,
                AllowsTransparency = true,
                Placement = PlacementMode.Mouse,
                PopupAnimation = PopupAnimation.None
            };

            _hoverTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(HoverDelayMs)
            };
            _hoverTimer.Tick += HoverTimer_Tick;

            DecompiledEditor.TextArea.MouseMove += TextArea_MouseMove;
            DecompiledEditor.TextArea.MouseLeave += TextArea_MouseLeave;
            DisassemblyEditor.TextArea.MouseMove += TextArea_MouseMove;
            DisassemblyEditor.TextArea.MouseLeave += TextArea_MouseLeave;

            DecompiledEditor.TextArea.TextView.ScrollOffsetChanged += TextView_ScrollOffsetChanged;
            DisassemblyEditor.TextArea.TextView.ScrollOffsetChanged += TextView_ScrollOffsetChanged;
        }

        private void TextView_ScrollOffsetChanged(object sender, EventArgs e)
        {
            _hoverTimer.Stop();
            CloseHoverPopup();
        }

        private void TextArea_MouseMove(object sender, MouseEventArgs e)
        {
            if (!Settings.Instance.CodeEditorShowHoverInfo)
            {
                CloseHoverPopup();
                return;
            }

            _hoverTextArea = sender as TextArea;

            int currentOffset = GetOffsetFromMousePosition(_hoverTextArea);

            if (_hoverPopup.IsOpen && _hoverSectionStart >= 0 && currentOffset >= 0)
            {
                if (currentOffset >= _hoverSectionStart && currentOffset < _hoverSectionStart + _hoverSectionLength)
                    return;
            }

            if (currentOffset == _lastHoverOffset && _hoverTimer.IsEnabled)
                return;

            _lastHoverOffset = currentOffset;
            _hoverTimer.Stop();
            CloseHoverPopup();
            _hoverTimer.Start();
        }

        private void TextArea_MouseLeave(object sender, MouseEventArgs e)
        {
            _hoverTextArea = null;
            _lastHoverOffset = -1;
            _hoverTimer.Stop();
            CloseHoverPopup();
        }

        private int GetOffsetFromMousePosition(TextArea textArea)
        {
            Point pos = Mouse.GetPosition(textArea.TextView);
            pos.X += textArea.TextView.ScrollOffset.X;
            pos.Y += textArea.TextView.ScrollOffset.Y;

            TextViewPosition? textViewPos = textArea.TextView.GetPosition(pos);
            if (textViewPos == null) return -1;

            int line = textViewPos.Value.Line;
            int column = textViewPos.Value.Column;

            if (line < 1 || line > textArea.Document.LineCount) return -1;

            var docLine = textArea.Document.GetLineByNumber(line);
            return docLine.Offset + Math.Min(column - 1, docLine.Length);
        }

        private void HoverTimer_Tick(object sender, EventArgs e)
        {
            _hoverTimer.Stop();

            if (_hoverPopup.IsOpen)
                return;

            TextArea textArea = _hoverTextArea;
            if (textArea == null) return;

            UndertaleData data = mainWindow.Data;
            if (data == null) return;

            int offset = GetOffsetFromMousePosition(textArea);
            if (offset < 0 || offset >= textArea.Document.TextLength) return;

            int sectionStart = -1, sectionLength = 0;
            var hoverContent = BuildHoverContent(textArea, offset, data, ref sectionStart, ref sectionLength);
            if (hoverContent == null) return;

            _hoverSectionStart = sectionStart;
            _hoverSectionLength = sectionLength;
            _hoverPopup.Child = hoverContent;
            _hoverPopup.IsOpen = true;
        }

        private void CloseHoverPopup()
        {
            if (_hoverPopup != null && _hoverPopup.IsOpen)
                _hoverPopup.IsOpen = false;
            _hoverSectionStart = -1;
            _hoverSectionLength = 0;
            _lastHoverOffset = -1;
        }

        private Border BuildHoverContent(TextArea textArea, int offset, UndertaleData data, ref int sectionStart, ref int sectionLength)
        {
            IHighlighter highlighter = textArea.GetService(typeof(IHighlighter)) as IHighlighter;
            if (highlighter == null)
            {
                TextEditor editor = textArea == DecompiledEditor.TextArea ? DecompiledEditor : DisassemblyEditor;
                highlighter = editor.TextArea.GetService(typeof(IHighlighter)) as IHighlighter;
            }
            if (highlighter == null) return null;

            int lineNum = textArea.Document.GetLineByOffset(offset).LineNumber;
            HighlightedLine highlighted;
            try
            {
                highlighted = highlighter.HighlightLine(lineNum);
            }
            catch
            {
                return null;
            }

            var docLine = textArea.Document.GetLineByNumber(lineNum);
            int lineStartOffset = docLine.Offset;
            int lineEndOffset = docLine.EndOffset;

            foreach (var section in highlighted.Sections)
            {
                if (section.Offset < lineStartOffset || section.Offset > lineEndOffset)
                    continue;

                if (offset < section.Offset || offset >= section.Offset + section.Length)
                    continue;

                string sectionText = textArea.Document.GetText(section.Offset, section.Length);

                if (section.Color.Name == "Number")
                {
                    sectionStart = section.Offset;
                    sectionLength = section.Length;
                    return BuildNumberHoverContent(sectionText, data);
                }

                if (section.Color.Name == "Identifier" || section.Color.Name == "Function")
                {
                    sectionStart = section.Offset;
                    sectionLength = section.Length;
                    return BuildNameHoverContent(sectionText, data, section.Color.Name == "Function");
                }
            }

            return null;
        }

        private Border BuildNumberHoverContent(string numText, UndertaleData data)
        {
            if (!int.TryParse(numText, out int id))
                return null;

            List<UndertaleObject> possibleObjects = new();
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
            bool isDarkMode = Settings.Instance.EnableDarkMode;
            Brush textBrush = isDarkMode ? Brushes.White : Brushes.Black;
            Brush subTextBrush = isDarkMode ? Brushes.LightGray : Brushes.DarkGray;

            if (possibleObjects.Count > 0)
            {
                foreach (UndertaleObject obj in possibleObjects)
                {
                    DockPanel row = new();

                    if (obj is UndertaleSprite sprite && sprite.Textures.Count > 0)
                    {
                        var textureEntry = sprite.Textures[0];
                        if (textureEntry?.Texture != null)
                        {
                            try
                            {
                                var loader = new UndertaleCachedImageLoader();
                                ImageSource imgSrc = loader.Convert(textureEntry.Texture, null, null, null) as ImageSource;
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
                                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
                                    DockPanel.SetDock(img, Dock.Left);
                                    row.Children.Add(img);
                                }
                            }
                            catch { }
                        }
                    }

                    TextBlock text = new()
                    {
                        Text = obj.ToString().Replace("_", "__"),
                        Foreground = textBrush,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    row.Children.Add(text);
                    panel.Children.Add(row);
                }
            }

            if (id > 0x00050000)
            {
                DockPanel colorRow = new();
                System.Windows.Shapes.Rectangle colorRect = new()
                {
                    Width = 16,
                    Height = 16,
                    Fill = new SolidColorBrush(Color.FromRgb((byte)((id >> 16) & 0xFF), (byte)((id >> 8) & 0xFF), (byte)(id & 0xFF))),
                    Stroke = isDarkMode ? Brushes.Gray : Brushes.DarkGray,
                    StrokeThickness = 1,
                    Margin = new Thickness(0, 2, 8, 2),
                    VerticalAlignment = VerticalAlignment.Center
                };
                DockPanel.SetDock(colorRect, Dock.Left);
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

        private Border BuildNameHoverContent(string nameText, UndertaleData data, bool isFunc)
        {
            UndertaleNamedResource val = null;

            if (isFunc)
            {
                if (!data.IsVersionAtLeast(2, 3))
                    ScriptsDict.TryGetValue(nameText, out val);
                if (val == null)
                {
                    FunctionsDict.TryGetValue(nameText, out val);
                    if (data.IsVersionAtLeast(2, 3) && val != null)
                    {
                        if (CodeDict.TryGetValue(val.Name.Content, out _))
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
                NamedObjDict.TryGetValue(nameText, out val);
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
            bool isDarkMode = Settings.Instance.EnableDarkMode;
            Brush textBrush = isDarkMode ? Brushes.White : Brushes.Black;
            Brush subTextBrush = isDarkMode ? Brushes.LightGray : Brushes.DarkGray;

            if (val is UndertaleSprite sprite && sprite.Textures.Count > 0)
            {
                var textureEntry = sprite.Textures[0];
                if (textureEntry?.Texture != null)
                {
                    try
                    {
                        var loader = new UndertaleCachedImageLoader();
                        ImageSource imgSrc = loader.Convert(textureEntry.Texture, null, null, null) as ImageSource;
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
                            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
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
                FontWeight = FontWeights.Bold
            };
            panel.Children.Add(nameBlock);

            TextBlock typeBlock = new()
            {
                Text = val.GetType().Name,
                Foreground = subTextBrush,
                FontSize = 11
            };
            panel.Children.Add(typeBlock);

            return CreateHoverBorder(panel);
        }

        private Border CreateHoverBorder(UIElement content)
        {
            bool isDarkMode = Settings.Instance.EnableDarkMode;
            Brush bgBrush = isDarkMode
                ? new SolidColorBrush(Color.FromRgb(45, 45, 48))
                : new SolidColorBrush(Color.FromRgb(240, 240, 240));
            Brush borderBrush = isDarkMode
                ? new SolidColorBrush(Color.FromRgb(80, 80, 80))
                : new SolidColorBrush(Color.FromRgb(180, 180, 180));

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

        private Border BuildBuiltinFunctionHover(string nameText)
        {
            bool isDarkMode = Settings.Instance.EnableDarkMode;
            Brush textBrush = isDarkMode ? Brushes.White : Brushes.Black;
            Brush subTextBrush = isDarkMode ? Brushes.LightGray : Brushes.DarkGray;
            Brush paramBrush = isDarkMode
                ? new SolidColorBrush(Color.FromRgb(86, 156, 214))
                : new SolidColorBrush(Color.FromRgb(0, 0, 200));
            Brush typeBrush = isDarkMode
                ? new SolidColorBrush(Color.FromRgb(78, 201, 176))
                : new SolidColorBrush(Color.FromRgb(0, 128, 0));

            var specFunc = GmlSpecLoader.GetFunction(nameText);

            StackPanel panel = new() { MaxWidth = 400 };

            TextBlock sigBlock = new() { TextWrapping = TextWrapping.Wrap };
            sigBlock.Inlines.Add(new Run { Text = nameText, Foreground = textBrush, FontWeight = FontWeights.Bold });
            sigBlock.Inlines.Add(new Run { Text = "(", Foreground = textBrush });

            if (specFunc != null)
            {
                for (int i = 0; i < specFunc.Parameters.Count; i++)
                {
                    var p = specFunc.Parameters[i];
                    if (i > 0)
                        sigBlock.Inlines.Add(new Run { Text = ", ", Foreground = textBrush });

                    if (p.Optional)
                        sigBlock.Inlines.Add(new Run { Text = "[", Foreground = subTextBrush });

                    sigBlock.Inlines.Add(new Run { Text = p.Name, Foreground = paramBrush });
                    sigBlock.Inlines.Add(new Run { Text = ": ", Foreground = subTextBrush });
                    sigBlock.Inlines.Add(new Run { Text = p.Type, Foreground = typeBrush });

                    if (p.Optional)
                        sigBlock.Inlines.Add(new Run { Text = "]", Foreground = subTextBrush });
                }
            }

            sigBlock.Inlines.Add(new Run { Text = ")", Foreground = textBrush });

            if (specFunc != null && !string.IsNullOrEmpty(specFunc.ReturnType) && specFunc.ReturnType != "Undefined")
            {
                sigBlock.Inlines.Add(new Run { Text = " → ", Foreground = subTextBrush });
                sigBlock.Inlines.Add(new Run { Text = specFunc.ReturnType, Foreground = typeBrush });
            }

            panel.Children.Add(sigBlock);

            if (specFunc != null && !string.IsNullOrEmpty(specFunc.Description))
            {
                panel.Children.Add(new Separator
                {
                    Margin = new Thickness(0, 4, 0, 4),
                    Background = isDarkMode
                        ? new SolidColorBrush(Color.FromRgb(80, 80, 80))
                        : new SolidColorBrush(Color.FromRgb(180, 180, 180))
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
                                ? new SolidColorBrush(Color.FromRgb(80, 80, 80))
                                : new SolidColorBrush(Color.FromRgb(180, 180, 180))
                        });

                        foreach (var p in specFunc.Parameters)
                        {
                            if (string.IsNullOrEmpty(p.Description)) continue;

                            TextBlock paramBlock = new()
                            {
                                TextWrapping = TextWrapping.Wrap,
                                FontSize = 11
                            };
                            paramBlock.Inlines.Add(new Run { Text = p.Name, Foreground = paramBrush, FontWeight = FontWeights.Medium });
                            paramBlock.Inlines.Add(new Run { Text = ": " + p.Type, Foreground = typeBrush });
                            paramBlock.Inlines.Add(new Run { Text = " — " + p.Description, Foreground = subTextBrush });
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

        private Border BuildBuiltinVariableHover(string nameText)
        {
            bool isDarkMode = Settings.Instance.EnableDarkMode;
            Brush textBrush = isDarkMode ? Brushes.White : Brushes.Black;
            Brush subTextBrush = isDarkMode ? Brushes.LightGray : Brushes.DarkGray;
            Brush typeBrush = isDarkMode
                ? new SolidColorBrush(Color.FromRgb(78, 201, 176))
                : new SolidColorBrush(Color.FromRgb(0, 128, 0));

            var specVar = GmlSpecLoader.GetVariable(nameText);

            StackPanel panel = new() { MaxWidth = 400 };

            TextBlock sigBlock = new() { TextWrapping = TextWrapping.Wrap };
            sigBlock.Inlines.Add(new Run { Text = nameText, Foreground = textBrush, FontWeight = FontWeights.Bold });

            if (specVar != null && !string.IsNullOrEmpty(specVar.Type))
            {
                sigBlock.Inlines.Add(new Run { Text = ": ", Foreground = subTextBrush });
                sigBlock.Inlines.Add(new Run { Text = specVar.Type, Foreground = typeBrush });
            }

            if (specVar != null)
            {
                string access = "";
                if (specVar.CanGet && specVar.CanSet) access = " { get; set; }";
                else if (specVar.CanGet) access = " { get; }";
                else if (specVar.CanSet) access = " { set; }";
                if (!string.IsNullOrEmpty(access))
                    sigBlock.Inlines.Add(new Run { Text = access, Foreground = subTextBrush });
            }

            panel.Children.Add(sigBlock);

            if (specVar != null && !string.IsNullOrEmpty(specVar.Description))
            {
                panel.Children.Add(new Separator
                {
                    Margin = new Thickness(0, 4, 0, 4),
                    Background = isDarkMode
                        ? new SolidColorBrush(Color.FromRgb(80, 80, 80))
                        : new SolidColorBrush(Color.FromRgb(180, 180, 180))
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

        private Border BuildBuiltinConstantHover(string nameText)
        {
            bool isDarkMode = Settings.Instance.EnableDarkMode;
            Brush textBrush = isDarkMode ? Brushes.White : Brushes.Black;
            Brush subTextBrush = isDarkMode ? Brushes.LightGray : Brushes.DarkGray;
            Brush typeBrush = isDarkMode
                ? new SolidColorBrush(Color.FromRgb(78, 201, 176))
                : new SolidColorBrush(Color.FromRgb(0, 128, 0));

            var specConst = GmlSpecLoader.GetConstant(nameText);

            StackPanel panel = new() { MaxWidth = 400 };

            TextBlock sigBlock = new() { TextWrapping = TextWrapping.Wrap };
            sigBlock.Inlines.Add(new Run { Text = nameText, Foreground = textBrush, FontWeight = FontWeights.Bold });

            if (specConst != null && !string.IsNullOrEmpty(specConst.Type))
            {
                sigBlock.Inlines.Add(new Run { Text = ": ", Foreground = subTextBrush });
                sigBlock.Inlines.Add(new Run { Text = specConst.Type, Foreground = typeBrush });
            }

            if (specConst != null && !string.IsNullOrEmpty(specConst.Class))
            {
                sigBlock.Inlines.Add(new Run { Text = " (" + specConst.Class + ")", Foreground = subTextBrush });
            }

            panel.Children.Add(sigBlock);

            if (specConst != null && !string.IsNullOrEmpty(specConst.Description))
            {
                panel.Children.Add(new Separator
                {
                    Margin = new Thickness(0, 4, 0, 4),
                    Background = isDarkMode
                        ? new SolidColorBrush(Color.FromRgb(80, 80, 80))
                        : new SolidColorBrush(Color.FromRgb(180, 180, 180))
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

        private void UndertaleCodeEditor_Unloaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is UndertaleCode oldObj)
            {
                oldObj.PropertyChanged -= OnCodePropertyChanged;
            }

            OverriddenDecompPos = default;
            OverriddenDisasmPos = default;
            OverriddenZoomFontSize = 0;
        }

        private void UndertaleCodeEditor_Loaded(object sender, RoutedEventArgs e)
        {
            FillInCodeViewer();
        }
        private void FillInCodeViewer(bool overrideFirst = false)
        {
            UndertaleCode code = DataContext as UndertaleCode;
            if (DisassemblyTab.IsSelected && code != CurrentDisassembled)
            {
                if (!overrideFirst)
                {
                    DisassembleCode(code, !DisassembledYet);
                    DisassembledYet = true;
                }
                else
                    DisassembleCode(code, true);
            }
            if (DecompiledTab.IsSelected && code != CurrentDecompiled)
            {
                if (!overrideFirst)
                {
                    _ = DecompileCode(code, !DecompiledYet);
                    DecompiledYet = true;
                }
                else
                    _ = DecompileCode(code, true);
            }
            if (DecompiledTab.IsSelected)
            {
                // Re-populate local variables when in decompiled code, fixing #1320
                PopulateCurrentLocals(mainWindow.Data, code);
            }
        }

        private async void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UndertaleCode code = this.DataContext as UndertaleCode;
            if (code == null)
                return;

            DecompiledSearchReplacePanel.ClosePanel();
            DisassemblySearchReplacePanel.ClosePanel();

            await DecompiledLostFocusBody(sender, null);
            DisassemblyEditor_LostFocus(sender, null);

            if (!IsLoaded)
            {
                // If it's not loaded, then "FillInCodeViewer()" will be executed on load.
                // This prevents a bug with freezing on code opening.
                return;
            }

            FillInCodeViewer();
        }

        private async void UserControl_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is UndertaleCode oldObj)
            {
                oldObj.PropertyChanged -= OnCodePropertyChanged;
            }
            if (e.NewValue is UndertaleCode newObj)
            {
                newObj.PropertyChanged += OnCodePropertyChanged;
            }

            UndertaleCode code = this.DataContext as UndertaleCode;
            if (code == null)
                return;

            FillObjectDicts();

            // compile/disassemble previously edited code (save changes)
            if (DecompiledTab.IsSelected && DecompiledFocused && DecompiledChanged &&
                CurrentDecompiled is not null && CurrentDecompiled != code)
            {
                DecompiledSkipped = true;
                await DecompiledLostFocusBody(sender, null);
            }
            else if (DisassemblyTab.IsSelected && DisassemblyFocused && DisassemblyChanged &&
                     CurrentDisassembled is not null && CurrentDisassembled != code)
            {
                DisassemblySkipped = true;
                DisassemblyEditor_LostFocus(sender, null);
            }

            await DecompiledLostFocusBody(sender, null);
            DisassemblyEditor_LostFocus(sender, null);

            DecompiledYet = false;
            DisassembledYet = false;
            CurrentDecompiled = null;
            CurrentDisassembled = null;

            if (EditorTab != CodeEditorTab.Unknown) // If opened from the code search results "link"
            {
                if (EditorTab == CodeEditorTab.Disassembly && code != CurrentDisassembled)
                {
                    if (CodeModeTabs.SelectedItem != DisassemblyTab)
                        CodeModeTabs.SelectedItem = DisassemblyTab;
                    else
                        DisassembleCode(code, true);
                }

                if (EditorTab == CodeEditorTab.Decompiled && code != CurrentDecompiled)
                {
                    if (CodeModeTabs.SelectedItem != DecompiledTab)
                        CodeModeTabs.SelectedItem = DecompiledTab;
                    else
                        _ = DecompileCode(code, true);
                }

                EditorTab = CodeEditorTab.Unknown;
            }
            else
                FillInCodeViewer(true);
        }

        private void OnCodePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            OnAssetUpdated();
        }

        private void OnAssetUpdated()
        {
            if (mainWindow.Project is null || !mainWindow.IsSelectedProjectExportable)
            {
                return;
            }
            Dispatcher.BeginInvoke(() =>
            {
                if (DataContext is UndertaleCode obj)
                {
                    mainWindow.Project?.MarkAssetForExport(obj);
                }
            });
        }

        public static readonly RoutedEvent CtrlKEvent = EventManager.RegisterRoutedEvent(
            "CtrlK", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(UndertaleCodeEditor));

        private async Task CompileCommandBody(object sender, EventArgs e)
        {
            if (DecompiledFocused)
            {
                await DecompiledLostFocusBody(sender, new RoutedEventArgs(CtrlKEvent));
            }
            else if (DisassemblyFocused)
            {
                DisassemblyEditor_LostFocus(sender, new RoutedEventArgs(CtrlKEvent));
                DisassemblyEditor_GotFocus(sender, null);
            }
        }
        private void Command_Compile(object sender, EventArgs e)
        {
            _ = CompileCommandBody(sender, e);
        }

        private void Command_OpenFind(object sender, ExecutedRoutedEventArgs e)
        {
            if (DecompiledTab.IsSelected)
                DecompiledSearchReplacePanel.Open(false);
            else
                DisassemblySearchReplacePanel.Open(false);
        }

        private void Command_OpenReplace(object sender, ExecutedRoutedEventArgs e)
        {
            if (DecompiledTab.IsSelected)
                DecompiledSearchReplacePanel.Open(true);
            else
                DisassemblySearchReplacePanel.Open(true);
        }
        public async Task SaveChanges()
        {
            await CompileCommandBody(null, null);
        }

        public void RestoreState(CodeTabState tabState)
        {
            if (tabState.IsDecompiledOpen)
                CodeModeTabs.SelectedItem = DecompiledTab;
            else
                CodeModeTabs.SelectedItem = DisassemblyTab;

            ZoomFontSize = tabState.ZoomFontSize;
            LastZoomFontSize = ZoomFontSize;

            TextEditor textEditor = DecompiledEditor;
            textEditor.FontSize = ZoomFontSize;
            textEditor.UpdateLayout();
            (int linePos, int columnPos, double scrollPos) = tabState.DecompiledCodePosition;
            RestoreCaretPosition(textEditor, linePos, columnPos, scrollPos);

            textEditor = DisassemblyEditor;
            textEditor.FontSize = ZoomFontSize;
            textEditor.UpdateLayout();
            (linePos, columnPos, scrollPos) = tabState.DisassemblyCodePosition;
            RestoreCaretPosition(textEditor, linePos, columnPos, scrollPos);
        }
        private static void RestoreCaretPosition(TextEditor textEditor, int linePos, int columnPos, double scrollPos)
        {
            if (linePos <= textEditor.LineCount)
            {
                if (linePos == -1)
                    linePos = textEditor.Document.LineCount;

                int lineLen = textEditor.Document.GetLineByNumber(linePos).Length;
                textEditor.TextArea.Caret.Line = linePos;
                if (columnPos != -1)
                    textEditor.TextArea.Caret.Column = columnPos;
                else
                    textEditor.TextArea.Caret.Column = lineLen + 1;

                textEditor.ScrollToLine(linePos);
                if (scrollPos != -1)
                    textEditor.ScrollToVerticalOffset(scrollPos);
            }
            else
            {
                textEditor.CaretOffset = textEditor.Text.Length;
                textEditor.ScrollToEnd();
            }
        }
        public static void ChangeLineNumber(int lineNum, CodeEditorTab editorTab)
        {
            if (lineNum < 1)
                return;

            if (editorTab == CodeEditorTab.Unknown)
            {
                Debug.WriteLine($"The \"{nameof(editorTab)}\" argument of \"{nameof(ChangeLineNumber)}()\" is \"{nameof(CodeEditorTab.Unknown)}\".");
                return;
            }

            if (editorTab == CodeEditorTab.Decompiled)
                OverriddenDecompPos = (lineNum, -1, -1);
            else
                OverriddenDisasmPos = (lineNum, -1, -1);
            OverriddenZoomFontSize = LastZoomFontSize;
        }
        public static void ChangeLineNumber(int lineNum, TextEditor textEditor)
        {
            if (lineNum < 1)
                return;

            if (textEditor is null)
            {
                Debug.WriteLine($"The \"{nameof(textEditor)}\" argument of \"{nameof(ChangeLineNumber)}()\" is null.");
                return;
            }

            RestoreCaretPosition(textEditor, lineNum, -1, -1);
        }

        private static void FillObjectDicts()
        {
            var data = mainWindow.Data;
            var objLists = new IEnumerable[] {
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
            };

            NamedObjDict.Clear();
            ScriptsDict.Clear();
            FunctionsDict.Clear();
            CodeDict.Clear();

            foreach (var list in objLists)
            {
                if (list is null)
                    continue;

                foreach (var obj in list)
                {
                    if (obj is not UndertaleNamedResource namedObj)
                        continue;

                    NamedObjDict[namedObj.Name.Content] = namedObj;
                }
            }
            foreach (var scr in data.Scripts)
            {
                if (scr is null)
                    continue;

                ScriptsDict[scr.Name.Content] = scr;
            }
            foreach (var func in data.Functions)
            {
                if (func is null)
                    continue;

                FunctionsDict[func.Name.Content] = func;
            }
            foreach (var code in data.Code)
            {
                if (code is null)
                    continue;

                CodeDict[code.Name.Content] = code;
            }
        }

        private void DisassembleCode(UndertaleCode code, bool first)
        {
            string text;

            int currLine = 1;
            int currColumn = 1;
            double scrollPos = 0;
            if (!first)
            {
                var caret = DisassemblyEditor.TextArea.Caret;
                currLine = caret.Line;
                currColumn = caret.Column;
                scrollPos = DisassemblyEditor.VerticalOffset;
            }
            else if (OverriddenDisasmPos != default)
            {
                currLine = OverriddenDisasmPos.Line;
                currColumn = OverriddenDisasmPos.Column;
                scrollPos = OverriddenDisasmPos.ScrollPos;

                OverriddenDisasmPos = default;
            }
            if (OverriddenZoomFontSize != 0)
            {
                ZoomFontSize = OverriddenZoomFontSize;
                LastZoomFontSize = ZoomFontSize;
                OverriddenZoomFontSize = 0;
            }

            DisassemblyEditor.TextArea.ClearSelection();
            if (code.ParentEntry != null)
            {
                DisassemblyEditor.IsReadOnly = true;
                text = "; " + string.Format(LocalizationSource.GetString("Msg_CodeEntryReference"), code.ParentEntry.Name.Content);
                DisassemblyChanged = false;
            }
            else
            {
                DisassemblyEditor.IsReadOnly = false;

                try
                {
                    var data = mainWindow.Data;
                    text = code.Disassemble(data.Variables, data.CodeLocals?.For(code), data.CodeLocals is null);

                    CurrentLocals.Clear();
                }
                catch (Exception ex)
                {
                    DisassemblyEditor.IsReadOnly = true;

                    string exStr = ex.ToString();
                    exStr = String.Join("\n;", exStr.Split('\n'));
                    text = $";  EXCEPTION!\n;   {exStr}\n";
                }
            }

            DisassemblyEditor.Document.BeginUpdate();
            _isLoadingCode = true;
            try
            {
                DisassemblyEditor.Document.Text = text;
            }
            finally
            {
                _isLoadingCode = false;
            }

            DisassemblyEditor.FontSize = ZoomFontSize;
            if (!DisassemblyEditor.IsReadOnly)
                RestoreCaretPosition(DisassemblyEditor, currLine, currColumn, scrollPos);

            DisassemblyEditor.Document.EndUpdate();

            if (first)
                DisassemblyEditor.Document.UndoStack.ClearAll();

            CurrentDisassembled = code;
            DisassemblyChanged = false;
            if (Settings.Instance?.ChangeTrackingEnabled ?? true)
                _disassemblyModifiedRenderer.SetOriginalText(DisassemblyEditor.Text, DisassemblyEditor.Document);
            SaveOriginalBytecodeSnapshot(code);
        }

        public static Dictionary<string, string> gettext = null;
        private static void UpdateGettext(UndertaleData data, UndertaleCode gettextCode)
        {
            gettext = new Dictionary<string, string>();
            GlobalDecompileContext context = new(data);
            string[] decompilationOutput;
            try
            {
                decompilationOutput =
                    new Underanalyzer.Decompiler.DecompileContext(context, gettextCode, data.ToolInfo.DecompilerSettings).DecompileToString().Split('\n');
            }
            catch (Exception)
            {
                decompilationOutput = Array.Empty<string>();
            }
            Regex textdataRegex = new("^ds_map_add\\(global\\.text_data_en, \\\"(.*)\\\", \\\"(.*)\\\"\\)", RegexOptions.Compiled);
            Regex textdataRegex2 = new("^ds_map_add\\(global\\.text_data_en, \\\"(.*)\\\", '(.*)'\\)", RegexOptions.Compiled);
            foreach (var line in decompilationOutput)
            {
                Match m = textdataRegex.Match(line);
                if (m.Success)
                {
                    try
                    {
                        if (!data.IsGameMaker2() && m.Groups[2].Value.Contains("'\"'"))
                        {
                            gettext.Add(m.Groups[1].Value, $"\"{m.Groups[2].Value}\"");
                        }
                        else
                        {
                            gettext.Add(m.Groups[1].Value, m.Groups[2].Value);
                        }
                    }
                    catch (ArgumentException)
                    {
                        mainWindow.ShowError(string.Format(LocalizationSource.GetString("Msg_DuplicateKeyInTextdata"), m.Groups[1].Value));
                    }
                    catch
                    {
                        mainWindow.ShowError(LocalizationSource.GetString("Msg_UnknownErrorInTextdata"));
                    }
                }
                else
                {
                    m = textdataRegex2.Match(line);
                    if (m.Success)
                    {
                        try
                        {
                            if (!data.IsGameMaker2() && m.Groups[2].Value.Contains("\"'\""))
                            {
                                gettext.Add(m.Groups[1].Value, $"\'{m.Groups[2].Value}\'");
                            }
                            else
                            {
                                gettext.Add(m.Groups[1].Value, m.Groups[2].Value);
                            }
                        }
                        catch (ArgumentException)
                        {
                            mainWindow.ShowError(string.Format(LocalizationSource.GetString("Msg_DuplicateKeyInTextdata"), m.Groups[1].Value));
                        }
                        catch
                        {
                            mainWindow.ShowError(LocalizationSource.GetString("Msg_UnknownErrorInTextdata"));
                        }
                    }
                }
            }
        }

        public static Dictionary<string, string> gettextJSON = null;
        private static readonly Regex gettextRegex = new(@"scr_gettext\(\""(.*?)\""\)(?!(.*?\/\/.*?$))", RegexOptions.Compiled);
        private static readonly Regex getlangRegex = new(@"scr_84_get_lang_string(?:.*?)\(\""(.*?)\""\)(?!(.*?\/\/.*?$))", RegexOptions.Compiled);
        private string UpdateGettextJSON(string json)
        {
            try
            {
                gettextJSON = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
            }
            catch (Exception e)
            {
                gettextJSON = new Dictionary<string, string>();
                return string.Format(LocalizationSource.GetString("Msg_FailedToParseLangFile"), e.Message);
            }
            return null;
        }

        private async Task DecompileCode(UndertaleCode code, bool first, LoaderDialog existingDialog = null)
        {
            DecompiledEditor.IsReadOnly = true;

            int currLine = 1;
            int currColumn = 1;
            double scrollPos = 0;
            if (!first)
            {
                var caret = DecompiledEditor.TextArea.Caret;
                currLine = caret.Line;
                currColumn = caret.Column;
                scrollPos = DecompiledEditor.VerticalOffset;
            }
            else if (OverriddenDecompPos != default)
            {
                currLine = OverriddenDecompPos.Line;
                currColumn = OverriddenDecompPos.Column;
                scrollPos = OverriddenDecompPos.ScrollPos;

                OverriddenDecompPos = default;
            }
            if (OverriddenZoomFontSize != 0)
            {
                ZoomFontSize = OverriddenZoomFontSize;
                LastZoomFontSize = ZoomFontSize;
                OverriddenZoomFontSize = 0;
            }

            DecompiledEditor.TextArea.ClearSelection();

            if (code.ParentEntry != null)
            {
                _isLoadingCode = true;
                try
                {
                    DecompiledEditor.Text = "// " + string.Format(LocalizationSource.GetString("Msg_CodeEntryReference"), code.ParentEntry.Name.Content);
                }
                finally
                {
                    _isLoadingCode = false;
                }
                DecompiledChanged = false;
                CurrentDecompiled = code;
                if (Settings.Instance?.ChangeTrackingEnabled ?? true)
                    _decompiledModifiedRenderer.SetOriginalText(DecompiledEditor.Text, DecompiledEditor.Document);
                SaveOriginalBytecodeSnapshot(code);
                existingDialog?.TryClose();
            }
            else
            {
                LoaderDialog dialog;
                if (existingDialog != null)
                {
                    dialog = existingDialog;
                    dialog.Message = LocalizationSource.GetString("Msg_DecompilingPleaseWait");
                }
                else
                {
                    dialog = new LoaderDialog(LocalizationSource.GetString("Dialog_Decompileing"), LocalizationSource.GetString("Msg_DecompilingPleaseWait"));
                    dialog.Owner = Window.GetWindow(this);
                    try
                    {
                        _ = Dispatcher.BeginInvoke(new Action(() => { if (!dialog.IsClosed) dialog.TryShowDialog(); }));
                    }
                    catch
                    {
                        // This is still a problem in rare cases for some unknown reason
                    }
                }

                bool openSaveDialog = false;

                UndertaleCode gettextCode = null;
                if (gettext == null)
                    gettextCode = mainWindow.Data.Code.ByName("gml_Script_textdata_en");

                string dataPath = Path.GetDirectoryName(mainWindow.FilePath);
                string gettextJsonPath = null;
                if (dataPath is not null)
                {
                    gettextJsonPath = Path.Join(dataPath, "lang", "lang_en.json");
                    if (!File.Exists(gettextJsonPath))
                        gettextJsonPath = Path.Join(dataPath, "lang", "lang_en_ch1.json");
                }

                var dataa = mainWindow.Data;
                Task t = Task.Run(() =>
                {
                    string decompiled = null;
                    Exception e = null;
                    try
                    {
                        // First, try to retrieve source from project (if available)
                        if (mainWindow.Project is null || !mainWindow.Project.TryGetCodeSource(code, out decompiled))
                        {
                            // Source isn't available - perform decompile
                            GlobalDecompileContext context = new(dataa);
                            decompiled = new Underanalyzer.Decompiler.DecompileContext(context, code, dataa.ToolInfo.DecompilerSettings).DecompileToString();
                        }
                    }
                    catch (Exception ex)
                    {
                        e = ex;
                    }

                    if (gettextCode != null)
                        UpdateGettext(dataa, gettextCode);

                    try
                    {
                        if (gettextJSON == null && gettextJsonPath != null && File.Exists(gettextJsonPath))
                        {
                            string err = UpdateGettextJSON(File.ReadAllText(gettextJsonPath));
                            if (err != null)
                                e = new Exception(err);
                        }
                    }
                    catch (Exception exc)
                    {
                        mainWindow.ShowError(exc.ToString());
                    }

                    // Add `// string` at the end of lines with `scr_gettext()` or `scr_84_get_lang_string()`
                    if (decompiled is not null)
                    {
                        StringReader decompLinesReader;
                        StringBuilder decompLinesBuilder;
                        Dictionary<string, string> currDict = null;
                        Regex currRegex = null;
                        if (gettext is not null && decompiled.Contains("scr_gettext"))
                        {
                            currDict = gettext;
                            currRegex = gettextRegex;
                        }
                        else if (gettextJSON is not null && decompiled.Contains("scr_84_get_lang_string"))
                        {
                            currDict = gettextJSON;
                            currRegex = getlangRegex;
                        }

                        if (currDict is not null && currRegex is not null)
                        {
                            decompLinesReader = new(decompiled);
                            decompLinesBuilder = new();
                            string line;
                            while ((line = decompLinesReader.ReadLine()) is not null)
                            {
                                // Not `currRegex.Match()`, because one line could contain several calls
                                // if non-decompiled source code is being used.
                                var matches = currRegex.Matches(line).Where(m => m.Success).ToArray();
                                if (matches.Length > 0)
                                {
                                    decompLinesBuilder.Append($"{line} // ");

                                    for (int i = 0; i < matches.Length; i++)
                                    {
                                        Match match = matches[i];
                                        if (!currDict.TryGetValue(match.Groups[1].Value, out string text))
                                            text = "<localization fetch error>";

                                        if (i != matches.Length - 1) // If not the last
                                            decompLinesBuilder.Append($"{text}; ");
                                        else
                                            decompLinesBuilder.Append(text + '\n');
                                    }
                                }
                                else
                                {
                                    decompLinesBuilder.Append(line + '\n');
                                }
                            }

                            decompiled = decompLinesBuilder.ToString();
                        }
                    }

                    Dispatcher.Invoke(() =>
                    {
                        if (DataContext != code)
                            return;

                        _isLoadingCode = true;
                        try
                        {
                            DecompiledEditor.Document.BeginUpdate();
                            if (e != null)
                                DecompiledEditor.Document.Text = "/* EXCEPTION!\n   " + e.ToString() + "\n*/";
                            else if (decompiled != null)
                            {
                                DecompiledEditor.Document.Text = decompiled;
                                PopulateCurrentLocals(dataa, code);

                                DecompiledEditor.FontSize = ZoomFontSize;
                                RestoreCaretPosition(DecompiledEditor, currLine, currColumn, scrollPos);

                                if (existingDialog is not null)
                                {
                                    openSaveDialog = mainWindow.IsSaving;
                                }
                            }

                            DecompiledEditor.Document.EndUpdate();
                            DecompiledEditor.IsReadOnly = false;
                            if (first)
                                DecompiledEditor.Document.UndoStack.ClearAll();

                            DecompiledChanged = false;

                            CurrentDecompiled = code;
                            if (Settings.Instance?.ChangeTrackingEnabled ?? true)
                                _decompiledModifiedRenderer.SetOriginalText(DecompiledEditor.Text, DecompiledEditor.Document);
                            SaveOriginalBytecodeSnapshot(code);
                        }
                        finally
                        {
                            _isLoadingCode = false;
                        }
                        dialog.TryClose();
                    });
                });
                await t;
                dialog.Close();

                mainWindow.IsSaving = false;

                if (openSaveDialog)
                    await mainWindow.DoSaveDialog();
            }
        }

        private void PopulateCurrentLocals(UndertaleData data, UndertaleCode code)
        {
            CurrentLocals.Clear();

            // Look up locals for given code entry's name, for syntax highlighting
            var locals = data.CodeLocals?.ByName(code.Name.Content);
            if (locals != null)
            {
                foreach (var local in locals.Locals)
                    CurrentLocals.Add(local.Name.Content);
            }
        }

        private void DecompiledEditor_GotFocus(object sender, RoutedEventArgs e)
        {
            if (DecompiledEditor.IsReadOnly)
                return;
            DecompiledFocused = true;
        }

        private static string Truncate(string value, int maxChars)
        {
            return value.Length <= maxChars ? value : value.Substring(0, maxChars) + "...";
        }

        private async Task DecompiledLostFocusBody(object sender, RoutedEventArgs e)
        {
            if (!DecompiledFocused)
                return;
            if (DecompiledEditor.IsReadOnly)
                return;
            DecompiledFocused = false;

            if (!DecompiledChanged)
                return;

            UndertaleCode code;
            if (DecompiledSkipped)
            {
                code = CurrentDecompiled;
                DecompiledSkipped = false;
            }
            else
                code = this.DataContext as UndertaleCode;

            if (code == null)
            {
                if (IsLoaded)
                    code = CurrentDecompiled; // switched to the tab with different object type
                else
                    return;                   // probably loaded another data.win or something.
            }

            if (code.ParentEntry != null)
                return;

            // Check to make sure this isn't an element inside of the textbox, or another tab
            IInputElement elem = Keyboard.FocusedElement;
            if (elem is UIElement)
            {
                if (e != null && e.RoutedEvent?.Name != "CtrlK" && (elem as UIElement).IsDescendantOf(DecompiledEditor))
                    return;
            }

            // Get source code from editor
            string sourceCode = DecompiledEditor.Text;

            // Before compiling, update project source code and mark as exportable, if applicable
            if (mainWindow.Project is ProjectContext project && mainWindow.IsSelectedProjectExportable)
            {
                project.UpdateCodeSource(code, sourceCode);
                project.MarkAssetForExport(code);
            }

            // Create compiling dialog
            LoaderDialog dialog = new(LocalizationSource.GetString("Dialog_Compiling"), LocalizationSource.GetString("Msg_CompilingPleaseWait"))
            {
                Owner = Window.GetWindow(this)
            };
            try
            {
                _ = Dispatcher.BeginInvoke(() => 
                { 
                    if (!dialog.IsClosed) 
                        dialog.TryShowDialog(); 
                });
            }
            catch
            {
                // This is still a problem in rare cases for some unknown reason
            }

            CompileResult compileResult = new();
            string rootException = null;
            var originalSnapshot = GetOriginalBytecodeSnapshot(code);
            var dispatcher = Dispatcher;
            Task t = Task.Run(() =>
            {
                try
                {
                    CompileGroup group = new(mainWindow.Data)
                    {
                        MainThreadAction = (f) => { dispatcher.Invoke(() => f()); }
                    };
                    group.QueueCodeReplace(code, sourceCode);
                    compileResult = group.Compile();
                }
                catch (Exception ex)
                {
                    rootException = ex.ToString();
                }
            });
            await t;

            if (rootException is not null)
            {
                dialog.TryClose();
                mainWindow.ShowError(Truncate(rootException, 512), LocalizationSource.GetString("Dialog_CompilerError"));
                return;
            }

            if (!compileResult.Successful)
            {
                dialog.TryClose();
                mainWindow.ShowError(Truncate(compileResult.PrintAllErrors(false), 512), LocalizationSource.GetString("Dialog_CompilerError"));
                return;
            }

            if (Settings.Instance?.ChangeTrackingEnabled ?? true)
            {
                if (!BytecodeEquals(originalSnapshot, code))
                    mainWindow.ChangeTracker.MarkModified(code);
                else
                    mainWindow.ChangeTracker.UnmarkModified(code);
            }

            _decompiledModifiedRenderer.ClearModifiedLines();
            DecompiledChanged = false;

            // Invalidate gettext if necessary
            if (code.Name.Content == "gml_Script_textdata_en")
                gettext = null;

            // Show new code, decompiled.
            CurrentDisassembled = null;
            CurrentDecompiled = null;

            // Tab switch
            if (e == null)
            {
                dialog.TryClose();
                return;
            }

            // Decompile new code
            await DecompileCode(code, false, dialog);
        }
        private void DecompiledEditor_LostFocus(object sender, RoutedEventArgs e)
        {
            _ = DecompiledLostFocusBody(sender, e);
        }

        private void DisassemblyEditor_GotFocus(object sender, RoutedEventArgs e)
        {
            if (DisassemblyEditor.IsReadOnly)
                return;
            DisassemblyFocused = true;
        }

        private void DisassemblyEditor_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!DisassemblyFocused)
                return;
            if (DisassemblyEditor.IsReadOnly)
                return;
            DisassemblyFocused = false;

            if (!DisassemblyChanged)
                return;

            UndertaleCode code;
            if (DisassemblySkipped)
            {
                code = CurrentDisassembled;
                DisassemblySkipped = false;
            }
            else
                code = this.DataContext as UndertaleCode;

            if (code == null)
            {
                if (IsLoaded)
                    code = CurrentDisassembled; // switched to the tab with different object type
                else
                    return;                     // probably loaded another data.win or something.
            }

            // Check to make sure this isn't an element inside of the textbox, or another tab
            IInputElement elem = Keyboard.FocusedElement;
            if (elem is UIElement)
            {
                if (e != null && e.RoutedEvent?.Name != "CtrlK" && (elem as UIElement).IsDescendantOf(DisassemblyEditor))
                    return;
            }

            UndertaleData data = mainWindow.Data;
            var originalSnapshot = GetOriginalBytecodeSnapshot(code);
            try
            {
                var instructions = Assembler.Assemble(DisassemblyEditor.Text, data);
                code.Replace(instructions);
            }
            catch (Exception ex)
            {
                mainWindow.ShowError(ex.ToString(), LocalizationSource.GetString("Dialog_AssemblerError"));
                return;
            }

            if (Settings.Instance?.ChangeTrackingEnabled ?? true)
            {
                if (!BytecodeEquals(originalSnapshot, code))
                    mainWindow.ChangeTracker.MarkModified(code);
                else
                    mainWindow.ChangeTracker.UnmarkModified(code);
            }

            _disassemblyModifiedRenderer.ClearModifiedLines();
            DisassemblyChanged = false;

            // Get rid of old code
            CurrentDisassembled = null;
            CurrentDecompiled = null;

            // Tab switch
            if (e == null)
                return;

            // Disassemble new code
            DisassembleCode(code, false);

            // Code was modified, so mark it for export in project if we need to
            if (mainWindow.Project is ProjectContext project && project.TryGetCodeSource(code, out _))
            {
                // The user really shouldn't be editing disassembly - warn them about this in detail
                mainWindow.ShowWarning(LocalizationSource.GetString("Msg_EditingDisassemblyProjectWarning"));
            }

            if (!DisassemblyEditor.IsReadOnly)
            {
                if (mainWindow.IsSaving)
                {
                    mainWindow.IsSaving = false;

                    _ = mainWindow.DoSaveDialog();
                }
            }
        }

        public class NumberGenerator : VisualLineElementGenerator
        {
            private readonly IHighlighter highlighterInst;
            private readonly UndertaleCodeEditor codeEditorInst;

            // <offset, length>
            private readonly Dictionary<int, int> lineNumberSections = new();

            public NumberGenerator(UndertaleCodeEditor codeEditorInst, TextArea textAreaInst)
            {
                this.codeEditorInst = codeEditorInst;

                highlighterInst = textAreaInst.GetService(typeof(IHighlighter)) as IHighlighter;
            }

            public override void StartGeneration(ITextRunConstructionContext context)
            {
                lineNumberSections.Clear();

                var docLine = context.VisualLine.FirstDocumentLine;
                if (docLine.Length != 0)
                {
                    int line = docLine.LineNumber;
                    var highlighter = highlighterInst;
                    
                    HighlightedLine highlighted;
                    try
                    {
                        highlighted = highlighter.HighlightLine(line);
                    }
                    catch
                    {
                        Debug.WriteLine($"(NumberGenerator) Code editor line {line} highlight error.");
                        base.StartGeneration(context);
                        return;
                    }

                    foreach (var section in highlighted.Sections)
                    {
                        if (section.Color.Name == "Number")
                            lineNumberSections[section.Offset] = section.Length;
                    }
                }

                base.StartGeneration(context);
            }

            /// Gets the first offset >= startOffset where the generator wants to construct
            /// an element.
            /// Return -1 to signal no interest.
            public override int GetFirstInterestedOffset(int startOffset)
            {
                foreach (var section in lineNumberSections)
                {
                    if (startOffset <= section.Key)
                        return section.Key;
                }

                return -1;
            }

            /// Constructs an element at the specified offset.
            /// May return null if no element should be constructed.
            public override VisualLineElement ConstructElement(int offset)
            {
                int numLength = -1;
                if (!lineNumberSections.TryGetValue(offset, out numLength))
                    return null;

                var doc = CurrentContext.Document;
                string numText = doc.GetText(offset, numLength); 

                var line = new ClickVisualLineText(numText, CurrentContext.VisualLine, numLength);
                
                line.Clicked += (text, inNewTab) =>
                {
                    if (int.TryParse(text, out int id))
                    {
                        codeEditorInst.DecompiledFocused = true;
                        UndertaleData data = mainWindow.Data;

                        List<UndertaleObject> possibleObjects = new List<UndertaleObject>();
                        if (id >= 0)
                        {
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
                            if (id < (data.AnimationCurves?.Count ?? 0))
                                possibleObjects.Add(data.AnimationCurves[id]);
                            if (id < (data.Sequences?.Count ?? 0))
                                possibleObjects.Add(data.Sequences[id]);
                            if (id < (data.ParticleSystems?.Count ?? 0))
                                possibleObjects.Add(data.ParticleSystems[id]);
                        }

                        ContextMenuDark contextMenu = new();
                        foreach (UndertaleObject obj in possibleObjects)
                        {
                            if (obj is null)
                            {
                                continue;
                            }
                            MenuItemDark item = new();
                            item.Header = obj.ToString().Replace("_", "__");
                            item.PreviewMouseDown += (sender2, ev2) =>
                            {
                                if (ev2.ChangedButton != Input.MouseButton.Left
                                    && ev2.ChangedButton != Input.MouseButton.Middle)
                                    return;

                                if (ev2.ChangedButton == Input.MouseButton.Middle)
                                {
                                    mainWindow.Focus();
                                    mainWindow.ChangeSelection(obj, true);
                                    
                                }
                                else if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                                    mainWindow.ChangeSelection(obj);
                                else
                                {
                                    doc.Replace(line.ParentVisualLine.StartOffset + line.RelativeTextOffset,
                                                text.Length, (obj as UndertaleNamedResource).Name.Content, null);
                                    codeEditorInst.DecompiledChanged = true;
                                }
                            };
                            contextMenu.Items.Add(item);
                        }
                        if (id > 0x00050000)
                        {
                            MenuItemDark item = new();
                            item.Header = "0x" + id.ToString("X6") + " " + LocalizationSource.GetString("Editor_Color");
                            item.Click += (sender2, ev2) =>
                            {
                                if (!((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift))
                                {
                                    doc.Replace(line.ParentVisualLine.StartOffset + line.RelativeTextOffset,
                                                text.Length, "0x" + id.ToString("X6"), null);
                                    codeEditorInst.DecompiledChanged = true;
                                }
                            };
                            contextMenu.Items.Add(item);
                        }
                        BuiltinList list = mainWindow.Data.BuiltinList;
                        var myKey = list.Constants.FirstOrDefault(x => x.Value == (double)id).Key;
                        if (myKey != null)
                        {
                            MenuItemDark item = new();
                            item.Header = myKey.Replace("_", "__") + " " + LocalizationSource.GetString("Editor_Constant");
                            item.Click += (sender2, ev2) =>
                            {
                                if (!((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift))
                                {
                                    doc.Replace(line.ParentVisualLine.StartOffset + line.RelativeTextOffset,
                                                text.Length, myKey, null);
                                    codeEditorInst.DecompiledChanged = true;
                                }
                            };
                            contextMenu.Items.Add(item);
                        }
                        contextMenu.Items.Add(new MenuItemDark() { Header = id + " " + LocalizationSource.GetString("Editor_Number"), IsEnabled = false });

                        contextMenu.IsOpen = true;
                    }
                };

                return line;
            }
        }

        public class NameGenerator : VisualLineElementGenerator
        {
            private readonly IHighlighter highlighterInst;
            private readonly TextEditor textEditorInst;
            private readonly UndertaleCodeEditor codeEditorInst;

            private static readonly SolidColorBrush FunctionBrush = new(Color.FromRgb(0xFF, 0xB8, 0x71));
            private static readonly SolidColorBrush GlobalBrush = new(Color.FromRgb(0xF9, 0x7B, 0xF9));
            private static readonly SolidColorBrush ConstantBrush = new(Color.FromRgb(0xFF, 0x80, 0x80));
            private static readonly SolidColorBrush InstanceBrush = new(Color.FromRgb(0x58, 0xE3, 0x5A));
            private static readonly SolidColorBrush LocalBrush = new(Color.FromRgb(0xFF, 0xF8, 0x99));

            private static ContextMenuDark contextMenu;

            // <offset, length>
            private readonly Dictionary<int, int> lineNameSections = new();

            public NameGenerator(UndertaleCodeEditor codeEditorInst, TextArea textAreaInst)
            {
                this.codeEditorInst = codeEditorInst;

                highlighterInst = textAreaInst.GetService(typeof(IHighlighter)) as IHighlighter;
                textEditorInst = textAreaInst.GetService(typeof(TextEditor)) as TextEditor;

                var menuItem = new MenuItemDark()
                {
                    Header = LocalizationSource.GetString("Menu_OpenInNewTab")
                };
                menuItem.Click += (sender, _) =>
                {
                    mainWindow.ChangeSelection((sender as FrameworkElement).DataContext, true);
                };
                contextMenu = new()
                {
                    Items = { menuItem },
                    Placement = PlacementMode.MousePoint
                };
            }

            public override void StartGeneration(ITextRunConstructionContext context)
            {
                lineNameSections.Clear();

                var docLine = context.VisualLine.FirstDocumentLine;
                if (docLine.Length != 0)
                {
                    int line = docLine.LineNumber;
                    var highlighter = highlighterInst;

                    HighlightedLine highlighted;
                    try
                    {
                        highlighted = highlighter.HighlightLine(line);
                    }
                    catch
                    {
                        Debug.WriteLine($"(NameGenerator) Code editor line {line} highlight error.");
                        base.StartGeneration(context);
                        return;
                    }

                    foreach (var section in highlighted.Sections)
                    {
                        if (section.Color.Name == "Identifier" || section.Color.Name == "Function")
                            lineNameSections[section.Offset] = section.Length;
                    }
                }

                base.StartGeneration(context);
            }

            /// Gets the first offset >= startOffset where the generator wants to construct
            /// an element.
            /// Return -1 to signal no interest.
            public override int GetFirstInterestedOffset(int startOffset)
            {
                foreach (var section in lineNameSections)
                {
                    if (startOffset <= section.Key)
                        return section.Key;
                }

                return -1;
            }

            /// Constructs an element at the specified offset.
            /// May return null if no element should be constructed.
            public override VisualLineElement ConstructElement(int offset)
            {
                int nameLength = -1;
                if (!lineNameSections.TryGetValue(offset, out nameLength))
                    return null;

                var doc = CurrentContext.Document;
                string nameText = doc.GetText(offset, nameLength);

                UndertaleData data = mainWindow.Data;
                bool func = (offset + nameLength + 1 < CurrentContext.VisualLine.LastDocumentLine.EndOffset) &&
                            (doc.GetCharAt(offset + nameLength) == '(');
                UndertaleNamedResource val = null;
                bool nonResourceReference = false;

                var editor = textEditorInst;

                // Process the content of this identifier/function
                if (func)
                {
                    val = null;
                    if (!data.IsVersionAtLeast(2, 3)) // in GMS2.3 every custom "function" is in fact a member variable and scripts are never referenced directly
                        ScriptsDict.TryGetValue(nameText, out val);
                    if (val == null)
                    {
                        FunctionsDict.TryGetValue(nameText, out val);
                        if (data.IsVersionAtLeast(2, 3))
                        {
                            if (val != null)
                            {
                                if (CodeDict.TryGetValue(val.Name.Content, out _))
                                    val = null; // in GMS2.3 every custom "function" is in fact a member variable, and the names in functions make no sense (they have the gml_Script_ prefix)
                            }
                            else
                            {
                                // Resolve 2.3 sub-functions for their parent entry
                                if (data.GlobalFunctions?.TryGetFunction(nameText, out Underanalyzer.IGMFunction f) == true)
                                {
                                    ScriptsDict.TryGetValue(f.Name.Content, out val);
                                    val = (val as UndertaleScript)?.Code?.ParentEntry;
                                }
                            }
                        }
                    }
                    if (val == null)
                    {
                        if (data.BuiltinList.Functions.ContainsKey(nameText))
                        {
                            var res = new ColorVisualLineText(nameText, CurrentContext.VisualLine, nameLength,
                                                              FunctionBrush);
                            res.Bold = true;
                            return res;
                        }
                    }
                }
                else
                {
                    NamedObjDict.TryGetValue(nameText, out val);
                    if (data.IsVersionAtLeast(2, 3))
                    { 
                        if (val is UndertaleScript)
                            val = null; // in GMS2.3 scripts are never referenced directly

                        if (data.GlobalFunctions?.TryGetFunction(nameText, out Underanalyzer.IGMFunction globalFunc) == true &&
                            globalFunc is UndertaleFunction utGlobalFunc)
                        {
                            // Try getting script that this function reference belongs to
                            if (NamedObjDict.TryGetValue("gml_Script_" + nameText, out val) && val is UndertaleScript script)
                            {
                                // Highlight like a function as well
                                val = script.Code;
                                func = true;
                            }
                        }

                        if (val == null)
                        {
                            // Try to get basic function
                            if (FunctionsDict.TryGetValue(nameText, out val))
                            {
                                func = true;
                            }
                        }

                        if (val == null)
                        {
                            // Try resolving to room instance ID
                            string instanceIdPrefix = data.ToolInfo.InstanceIdPrefix();
                            if (nameText.StartsWith(instanceIdPrefix) &&
                                int.TryParse(nameText[instanceIdPrefix.Length..], out int id) && id >= 100000)
                            {
                                // TODO: We currently mark this as a non-resource reference, but ideally
                                // we resolve this to the room that this instance ID occurs in.
                                // However, we should only do this when actually clicking on it.
                                nonResourceReference = true;
                            }
                        }
                    }
                }
                if (val == null && !nonResourceReference)
                {
                    // Check for variable name colors
                    if (offset >= 7)
                    {
                        if (doc.GetText(offset - 7, 7) == "global.")
                        {
                            return new ColorVisualLineText(nameText, CurrentContext.VisualLine, nameLength,
                                                           GlobalBrush);
                        }
                    }
                    if (data.BuiltinList.Constants.ContainsKey(nameText))
                        return new ColorVisualLineText(nameText, CurrentContext.VisualLine, nameLength,
                                                       ConstantBrush);
                    if (data.BuiltinList.GlobalVars.ContainsKey(nameText) ||
                        data.BuiltinList.InstanceVars.ContainsKey(nameText) ||
                        data.BuiltinList.GlobalArrayVars.ContainsKey(nameText))
                        return new ColorVisualLineText(nameText, CurrentContext.VisualLine, nameLength,
                                                       InstanceBrush);
                    if (codeEditorInst?.CurrentLocals?.Contains(nameText) == true)
                        return new ColorVisualLineText(nameText, CurrentContext.VisualLine, nameLength,
                                                       LocalBrush);
                    return null;
                }

                var line = new ClickVisualLineText(nameText, CurrentContext.VisualLine, nameLength,
                                                   func ? FunctionBrush : ConstantBrush);
                if (func)
                {
                    // Make function references bold as well as a different color
                    line.Bold = true;
                }
                if (val is not null)
                {
                    // Add click operation when we have a resource
                    line.Clicked += async (text, button) =>
                    {
                        await codeEditorInst?.SaveChanges();

                        if (button == Input.MouseButton.Right)
                        {
                            contextMenu.DataContext = val;
                            contextMenu.IsOpen = true;
                        }
                        else
                            mainWindow.ChangeSelection(val, button == Input.MouseButton.Middle);
                    };
                }

                return line;
            }
        }

        public class ColorVisualLineText : VisualLineText
        {
            private string Text { get; set; }
            private Brush ForegroundBrush { get; set; }

            public bool Bold { get; set; } = false;

            /// <summary>
            /// Creates a visual line text element with the specified length.
            /// It uses the <see cref="ITextRunConstructionContext.VisualLine"/> and its
            /// <see cref="VisualLineElement.RelativeTextOffset"/> to find the actual text string.
            /// </summary>
            public ColorVisualLineText(string text, VisualLine parentVisualLine, int length, Brush foregroundBrush)
                : base(parentVisualLine, length)
            {
                Text = text;
                ForegroundBrush = foregroundBrush;
            }

            public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
            {
                if (ForegroundBrush != null)
                    TextRunProperties.SetForegroundBrush(ForegroundBrush);
                if (Bold)
                    TextRunProperties.SetTypeface(new Typeface(TextRunProperties.Typeface.FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal));
                return base.CreateTextRun(startVisualColumn, context);
            }

            protected override VisualLineText CreateInstance(int length)
            {
                return new ColorVisualLineText(Text, ParentVisualLine, length, null);
            }
        }

        public class ClickVisualLineText : VisualLineText
        {

            public delegate void ClickHandler(string text, Input.MouseButton button);

            public event ClickHandler Clicked;

            private string Text { get; set; }
            private Brush ForegroundBrush { get; set; }

            public bool Bold { get; set; } = false;

            /// <summary>
            /// Creates a visual line text element with the specified length.
            /// It uses the <see cref="ITextRunConstructionContext.VisualLine"/> and its
            /// <see cref="VisualLineElement.RelativeTextOffset"/> to find the actual text string.
            /// </summary>
            public ClickVisualLineText(string text, VisualLine parentVisualLine, int length, Brush foregroundBrush = null)
                : base(parentVisualLine, length)
            {
                Text = text;
                ForegroundBrush = foregroundBrush;
            }


            public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
            {
                if (ForegroundBrush != null)
                    TextRunProperties.SetForegroundBrush(ForegroundBrush);
                if (Bold)
                    TextRunProperties.SetTypeface(new Typeface(TextRunProperties.Typeface.FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal));
                return base.CreateTextRun(startVisualColumn, context);
            }

            bool LinkIsClickable()
            {
                if (string.IsNullOrEmpty(Text))
                    return false;
                return (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            }


            protected override void OnQueryCursor(QueryCursorEventArgs e)
            {
                if (LinkIsClickable())
                {
                    e.Handled = true;
                    e.Cursor = Cursors.Hand;
                }
            }

            protected override void OnMouseDown(MouseButtonEventArgs e)
            {
                if (e.Handled)
                    return;
                if ((e.ChangedButton == Input.MouseButton.Left && LinkIsClickable())
                    || e.ChangedButton == Input.MouseButton.Middle || e.ChangedButton == Input.MouseButton.Right)
                {
                    if (Clicked != null)
                    {
                        Clicked(Text, e.ChangedButton);
                        e.Handled = true;
                    }
                }
            }

            protected override VisualLineText CreateInstance(int length)
            {
                var res = new ClickVisualLineText(Text, ParentVisualLine, length);
                res.Clicked += Clicked;
                return res;
            }
        }

        private void ZoomChange(bool zoomingIn)
        {
            bool fontSizeChanged = false;
            TextView view1 = DecompiledEditor.TextArea.TextView;
            TextViewPosition? position1 = view1.GetPosition(new Point(0.0, view1.ScrollOffset.Y + 0.5));
            TextView view2 = DisassemblyEditor.TextArea.TextView;
            TextViewPosition? position2 = view2.GetPosition(new Point(0.0, view2.ScrollOffset.Y + 0.5));
            if (zoomingIn)
            {
                if (ZoomFontSize < 100)
                {
                    ZoomFontSize += 1;
                    fontSizeChanged = true;
                }
            }
            else
            {
                if (ZoomFontSize > 5)
                {
                    ZoomFontSize -= 1;
                    fontSizeChanged = true;
                }
            }
            if (fontSizeChanged)
            {
                DecompiledEditor.FontSize = ZoomFontSize;
                DisassemblyEditor.FontSize = ZoomFontSize;
                LastZoomFontSize = ZoomFontSize;
                if (position1.HasValue)
                {
                    DecompiledEditor.UpdateLayout();
                    DecompiledEditor.ScrollTo(position1.Value.Line, -1, VisualYPosition.LineTop, 0.0, 0.0);
                }
                if (position2.HasValue)
                {
                    DisassemblyEditor.UpdateLayout();
                    DisassemblyEditor.ScrollTo(position2.Value.Line, -1, VisualYPosition.LineTop, 0.0, 0.0);
                }
            }
        }

        private void Grid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                ZoomChange(e.Delta > 0);
            }
        }

        private void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.OemPlus || e.Key == Key.Add)
            {
                if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    e.Handled = true;
                    ZoomChange(true);
                }
            }
            else if ((e.Key == Key.OemMinus || e.Key == Key.Subtract))
            {
                if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    e.Handled = true;
                    ZoomChange(false);
                }
            }
        }
    }
}
