using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using QiuForms;
using UndertaleModTool.Localization;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Renders <see cref="QiuForm"/>s with Avalonia. The same window is used on desktop and, through
/// <see cref="WindowHost"/>'s simulated window stack, on Android.
/// </summary>
public static class AvaloniaQiuFormsHost
{
    /// <summary>Shows <paramref name="form"/> modally and returns how it was closed.</summary>
    public static QiuDialogResult Show(QiuForm form, MainViewModel mainVM)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(mainVM);

        // Windows (and every control in them) belong to the UI thread. Scripts run on their own
        // thread, so marshal there and block the script thread until the dialog is done.
        if (!Dispatcher.UIThread.CheckAccess())
            return Dispatcher.UIThread.InvokeAsync(() => Show(form, mainVM)).GetAwaiter().GetResult();

        Task<QiuDialogResult> dialog = ShowCoreAsync(form, mainVM);
        return dialog.WaitOnDispatcherFrame();
    }

    private static async Task<QiuDialogResult> ShowCoreAsync(QiuForm form, MainViewModel mainVM)
    {
        Window? owner = mainVM.View is Control view ? WindowHost.ResolveOwner(view) : null;
        AvaloniaQiuFormWindow window = new(form);
        await WindowHost.ShowDialog(owner, window);
        return form.Result;
    }
}

/// <summary>An Avalonia window generated from a <see cref="QiuForm"/>.</summary>
internal sealed class AvaloniaQiuFormWindow : Window
{
    private sealed record FieldBinding(QiuField Field, TextBlock Error, Control Editor, Action Sync);

    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#D13438"));

    private readonly QiuForm form;
    private readonly List<(QiuElement Element, Control View)> boundElements = [];
    private readonly List<FieldBinding> fieldBindings = [];

    private Control? firstEditor;
    private bool updatingFromModel;
    private bool suppressModelSync;
    private bool resultAssigned;

    public AvaloniaQiuFormWindow(QiuForm form)
    {
        this.form = form;

        Title = form.Title;
        Width = 560;
        MinWidth = 360;
        MaxWidth = 760;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        Grid layout = new() { Margin = new Thickness(14) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        if (!string.IsNullOrEmpty(form.Message))
        {
            TextBlock message = new()
            {
                Text = form.Message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            };
            Grid.SetRow(message, 0);
            layout.Children.Add(message);
        }

        StackPanel content = new();
        BuildPanel(form.Root, content);

        ScrollViewer scroll = new()
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 620,
            Margin = new Thickness(0, 0, 0, 6),
        };
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
            Margin = new Thickness(0, 10, 0, 0),
        };

        Button submit = new()
        {
            Content = string.IsNullOrEmpty(form.SubmitText) ? LocalizationSource.GetString("Common_OK") : form.SubmitText,
            IsDefault = true,
            MinWidth = 88,
        };
        submit.Click += (_, _) => Submit();
        buttons.Children.Add(submit);

        if (form.ShowCancelButton)
        {
            Button cancel = new()
            {
                Content = string.IsNullOrEmpty(form.CancelText) ? LocalizationSource.GetString("Common_Cancel") : form.CancelText,
                MinWidth = 88,
            };
            cancel.Click += (_, _) => Cancel();
            buttons.Children.Add(cancel);
        }

        Grid.SetRow(buttons, 2);
        layout.Children.Add(buttons);

        Content = layout;

        foreach (QiuField field in form.EnumerateFields())
            field.ValueChanged += OnFieldValueChanged;

        Opened += (_, _) =>
        {
            if (firstEditor is { IsVisible: true, IsEnabled: true })
                firstEditor.Focus();
        };
        Closing += (_, _) =>
        {
            if (!resultAssigned)
                form.Result = QiuDialogResult.Closed;
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && form.ShowCancelButton)
            {
                Cancel();
                e.Handled = true;
            }
        };

        RefreshStates();
    }

    private void BuildPanel(QiuPanel panel, Panel host)
    {
        foreach (QiuElement child in panel.Children)
        {
            Control view = BuildElement(child);
            if (panel.Spacing > 0)
                view.Margin = new Thickness(0, 0, 0, panel.Spacing);
            host.Children.Add(view);
        }

        Register(panel, host);
    }

    private Control BuildElement(QiuElement element)
    {
        switch (element)
        {
            case QiuField field:
                return BuildField(field);

            case QiuGroup group:
            {
                StackPanel inner = new() { Spacing = group.Spacing };
                BuildPanel(group, inner);

                StackPanel boxContent = new() { Spacing = 4 };
                boxContent.Children.Add(new TextBlock { Text = group.Title, FontWeight = FontWeight.SemiBold });
                boxContent.Children.Add(inner);

                Border box = new()
                {
                    BorderBrush = new SolidColorBrush(Color.Parse("#66808080")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8),
                    Child = boxContent,
                };
                Register(group, box);
                return box;
            }

            case QiuPanel panel:
            {
                StackPanel inner = new()
                {
                    Orientation = panel.Orientation == QiuOrientation.Horizontal ? Orientation.Horizontal : Orientation.Vertical,
                    Spacing = panel.Spacing,
                };
                BuildPanel(panel, inner);
                Register(panel, inner);
                return inner;
            }

            case QiuLabel label:
            {
                TextBlock text = new()
                {
                    Text = label.Text,
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = label.Heading ? FontWeight.SemiBold : FontWeight.Normal,
                    FontSize = label.Heading ? 15 : 12,
                    Margin = new Thickness(0, label.Heading ? 8 : 2, 0, 2),
                };
                label.TextChanged += changed =>
                {
                    text.Text = changed.Text;
                    RefreshStates();
                };
                Register(label, text);
                return text;
            }

            case QiuSeparator:
            {
                Separator line = new() { Margin = new Thickness(0, 6, 0, 6) };
                Register(element, line);
                return line;
            }

            case QiuButton button:
            {
                Button control = new()
                {
                    Content = button.Text,
                    IsDefault = button.IsDefault,
                    MinWidth = 88,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                control.Click += (_, _) => button.Click?.Invoke(form);
                Register(button, control);
                return control;
            }

            case QiuListBox listBox:
            {
                StackPanel container = new() { Spacing = 3 };
                if (!string.IsNullOrEmpty(listBox.Caption))
                    container.Children.Add(new TextBlock { Text = listBox.Caption });

                ListBox control = new()
                {
                    Height = listBox.Height > 0 ? listBox.Height : 200,
                    Width = listBox.Width > 0 ? listBox.Width : double.NaN,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };

                void Refresh()
                {
                    control.Items.Clear();
                    foreach (string item in listBox.Items)
                        control.Items.Add(item);
                    control.SelectedIndex = listBox.SelectedIndex;
                }

                Refresh();
                listBox.ItemsChanged += _ =>
                {
                    updatingFromModel = true;
                    try { Refresh(); }
                    finally { updatingFromModel = false; }
                };
                listBox.SelectionChanged += _ =>
                {
                    if (updatingFromModel || control.SelectedIndex == listBox.SelectedIndex)
                        return;

                    updatingFromModel = true;
                    try { control.SelectedIndex = listBox.SelectedIndex; }
                    finally { updatingFromModel = false; }
                };
                control.SelectionChanged += (_, _) =>
                {
                    if (!updatingFromModel && control.SelectedIndex >= 0)
                        listBox.SelectedIndex = control.SelectedIndex;
                };
                control.DoubleTapped += (_, _) =>
                {
                    if (listBox.SelectedItem is not null)
                        listBox.RaiseItemActivated();
                };

                container.Children.Add(control);
                Register(listBox, container);
                return container;
            }

            case QiuTreeView treeView:
            {
                StackPanel container = new() { Spacing = 3 };
                if (!string.IsNullOrEmpty(treeView.Caption))
                    container.Children.Add(new TextBlock { Text = treeView.Caption });

                TreeView control = new()
                {
                    Height = treeView.Height > 0 ? treeView.Height : 200,
                    Width = treeView.Width > 0 ? treeView.Width : double.NaN,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };

                void Refresh()
                {
                    control.Items.Clear();
                    TreeViewItem root = BuildNode(treeView.Root);
                    root.IsExpanded = true;
                    control.Items.Add(root);
                }

                static TreeViewItem BuildNode(QiuTreeNode node)
                {
                    TreeViewItem item = new() { Header = node.Text, Tag = node, IsExpanded = node.IsExpanded };
                    foreach (QiuTreeNode child in node.Children)
                        item.Items.Add(BuildNode(child));
                    return item;
                }

                Refresh();
                treeView.ItemsChanged += _ =>
                {
                    updatingFromModel = true;
                    try { Refresh(); }
                    finally { updatingFromModel = false; }
                };
                control.SelectionChanged += (_, _) =>
                {
                    if (!updatingFromModel && control.SelectedItem is TreeViewItem { Tag: QiuTreeNode node })
                        treeView.SelectedNode = node;
                };
                control.DoubleTapped += (_, _) =>
                {
                    if (treeView.SelectedNode is not null)
                        treeView.RaiseItemActivated();
                };

                container.Children.Add(control);
                Register(treeView, container);
                return container;
            }

            case QiuCanvas canvas:
            {
                QiuCanvasControl control = new(canvas)
                {
                    Width = canvas.Width,
                    Height = canvas.Height,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                Register(canvas, control);
                return control;
            }

            case QiuImage image:
            {
                Image control = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
                if (image.MaxHeight > 0)
                    control.MaxHeight = image.MaxHeight;
                try
                {
                    control.Source = LoadImage(image);
                }
                catch
                {
                    // A broken image must not prevent the rest of the form from opening.
                }
                Register(image, control);
                return control;
            }

            default:
                throw new NotSupportedException($"Unsupported QiuForms element: {element.GetType().Name}");
        }
    }

    private Control BuildField(QiuField field)
    {
        StackPanel container = new() { Spacing = 3 };
        Control editor;
        Action sync;

        if (field is QiuCheckBox check)
        {
            CheckBox box = new() { Content = check.Label, IsChecked = check.Value };
            box.PropertyChanged += (_, e) =>
            {
                if (e.Property == ToggleButton.IsCheckedProperty && !updatingFromModel)
                    SetFromUi(() => check.SetValueFromHost(box.IsChecked == true));
            };
            editor = box;
            sync = () =>
            {
                bool value = check.Value;
                if (box.IsChecked != value)
                    box.IsChecked = value;
            };
            firstEditor ??= box;
        }
        else if (field is QiuSlider slider)
        {
            TextBlock label = new() { Text = $"{slider.Label}: {slider.Value}" };
            container.Children.Add(label);

            Slider control = new()
            {
                Minimum = slider.Minimum,
                Maximum = slider.Maximum,
                Value = slider.Value,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                Width = 240,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            control.PropertyChanged += (_, e) =>
            {
                if (e.Property != Slider.ValueProperty)
                    return;

                int value = (int)Math.Round(control.Value);
                label.Text = $"{slider.Label}: {value}";
                if (!updatingFromModel)
                    SetFromUi(() => slider.SetValueFromHost(value));
            };

            editor = control;
            sync = () =>
            {
                control.Value = slider.Value;
                label.Text = $"{slider.Label}: {slider.Value}";
            };
            firstEditor ??= control;
        }
        else
        {
            if (field.ShowLabel)
                container.Children.Add(new TextBlock { Text = field.Label });
            (editor, sync) = CreateEditor(field);
            container.Children.Add(editor);
        }

        if (!string.IsNullOrEmpty(field.ToolTip))
            ToolTip.SetTip(container, field.ToolTip);

        TextBlock error = new()
        {
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
            FontSize = 11,
            Foreground = ErrorBrush,
        };
        container.Children.Add(error);

        boundElements.Add((field, container));
        fieldBindings.Add(new FieldBinding(field, error, editor, sync));
        return container;
    }

    private (Control Editor, Action Sync) CreateEditor(QiuField field)
    {
        switch (field)
        {
            case QiuTextBox textBox:
            {
                TextBox box = new()
                {
                    Text = textBox.Value,
                    AcceptsReturn = textBox.Multiline,
                    TextWrapping = textBox.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    MinHeight = 26,
                };
                if (textBox.MaxLength > 0)
                    box.MaxLength = textBox.MaxLength;
                if (textBox.Multiline)
                    box.MinHeight = 76;
                if (!string.IsNullOrEmpty(textBox.Placeholder))
                    box.Watermark = textBox.Placeholder;

                box.TextChanged += (_, _) =>
                {
                    if (updatingFromModel)
                        return;

                    SetFromUi(() => textBox.SetValueFromHost(box.Text ?? ""));
                };

                firstEditor ??= box;
                return (box, () =>
                {
                    string value = textBox.Value;
                    if (box.Text != value)
                        box.Text = value;
                });
            }

            case QiuNumberBox numberBox:
            {
                TextBox box = new()
                {
                    Text = FormatNumber(numberBox),
                    MinHeight = 26,
                    MinWidth = 140,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };

                box.TextChanged += (_, _) =>
                {
                    if (updatingFromModel)
                        return;

                    double value = double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double parsed)
                        ? parsed
                        : double.NaN;
                    SetFromUi(() => numberBox.SetValueFromHost(value));
                };

                firstEditor ??= box;
                return (box, () =>
                {
                    string formatted = FormatNumber(numberBox);
                    if (box.Text != formatted)
                        box.Text = formatted;
                });
            }

            case QiuChoice choice:
            {
                if (choice.UseRadioGroup)
                {
                    StackPanel group = new() { Spacing = 2 };
                    List<RadioButton> radios = [];

                    for (int i = 0; i < choice.Items.Count; i++)
                    {
                        int index = i;
                        RadioButton radio = new()
                        {
                            Content = choice.Items[i],
                            GroupName = "qiu_" + Guid.NewGuid().ToString("N"),
                            IsChecked = choice.Value == index,
                        };
                        radio.PropertyChanged += (_, e) =>
                        {
                            if (e.Property == ToggleButton.IsCheckedProperty
                                && radio.IsChecked == true
                                && !updatingFromModel)
                            {
                                SetFromUi(() => choice.SetValueFromHost(index));
                            }
                        };
                        radios.Add(radio);
                        group.Children.Add(radio);
                    }

                    firstEditor ??= radios[0];
                    return (group, () =>
                    {
                        for (int i = 0; i < radios.Count; i++)
                            radios[i].IsChecked = choice.Value == i;
                    });
                }

                ComboBox combo = new()
                {
                    ItemsSource = choice.Items,
                    SelectedIndex = choice.Value,
                    MinWidth = 180,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                combo.SelectionChanged += (_, _) =>
                {
                    if (!updatingFromModel && combo.SelectedIndex >= 0)
                        SetFromUi(() => choice.SetValueFromHost(combo.SelectedIndex));
                };

                firstEditor ??= combo;
                return (combo, () =>
                {
                    if (combo.SelectedIndex != choice.Value)
                        combo.SelectedIndex = choice.Value;
                });
            }

            default:
                throw new NotSupportedException($"Unsupported QiuForms field: {field.GetType().Name}");
        }
    }

    private void SetFromUi(Action action)
    {
        suppressModelSync = true;
        try
        {
            action();
        }
        finally
        {
            suppressModelSync = false;
        }
    }

    private void OnFieldValueChanged(QiuField field)
    {
        int index = fieldBindings.FindIndex(binding => ReferenceEquals(binding.Field, field));
        if (index >= 0)
        {
            FieldBinding binding = fieldBindings[index];
            binding.Error.IsVisible = false;
            field.ClearError();

            if (!suppressModelSync)
            {
                updatingFromModel = true;
                try
                {
                    binding.Sync();
                }
                finally
                {
                    updatingFromModel = false;
                }
            }
        }

        RefreshStates();
    }

    private void Register(QiuElement element, Control view) => boundElements.Add((element, view));

    private void RefreshStates()
    {
        foreach ((QiuElement element, Control view) in boundElements)
        {
            view.IsEnabled = element.EffectiveIsEnabled;
            view.IsVisible = element.EffectiveIsVisible;
        }
    }

    private void Submit()
    {
        if (!form.Validate())
        {
            ShowErrors();
            fieldBindings.Find(binding => ReferenceEquals(binding.Field, form.FirstInvalid))?.Editor.Focus();
            return;
        }

        resultAssigned = true;
        form.Result = QiuDialogResult.Submit;
        Close();
    }

    private void Cancel()
    {
        resultAssigned = true;
        form.Result = QiuDialogResult.Cancel;
        Close();
    }

    private void ShowErrors()
    {
        foreach (FieldBinding binding in fieldBindings)
        {
            binding.Error.Text = binding.Field.Error ?? "";
            binding.Error.IsVisible = binding.Field.Error is not null;
        }
    }

    private static string FormatNumber(QiuNumberBox field)
    {
        if (double.IsNaN(field.Value))
            return "";

        string format = field.Integer
            ? "0"
            : field.Decimals <= 0 ? "0" : "0." + new string('0', field.Decimals);

        return field.Value.ToString(format, CultureInfo.CurrentCulture);
    }

    private static IImage LoadImage(QiuImage image)
    {
        if (image.Png is not null)
            return new Bitmap(new MemoryStream(image.Png));

        return new Bitmap(image.FilePath!);
    }
}
/// <summary>Avalonia control that renders a <see cref="QiuCanvas"/> and forwards pointer input.</summary>
internal sealed class QiuCanvasControl : Control
{
    private readonly QiuCanvas canvas;

    public QiuCanvasControl(QiuCanvas canvas)
    {
        this.canvas = canvas;
        canvas.Invalidated += _ => InvalidateVisual();
        PointerPressed += (_, e) => Forward(e, QiuPointerPhase.Pressed);
        PointerMoved += (_, e) => Forward(e, QiuPointerPhase.Moved);
        PointerReleased += (_, e) => Forward(e, QiuPointerPhase.Released);
    }

    private enum QiuPointerPhase
    {
        Pressed,
        Moved,
        Released,
    }

    private void Forward(PointerEventArgs e, QiuPointerPhase phase)
    {
        Point position = e.GetPosition(this) / canvas.Zoom;
        PointerPoint point = e.GetCurrentPoint(this);
        QiuPointerButton button = point.Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => QiuPointerButton.Secondary,
            PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => QiuPointerButton.Middle,
            _ => QiuPointerButton.Primary,
        };

        bool isPressed = phase switch
        {
            QiuPointerPhase.Pressed => true,
            QiuPointerPhase.Released => false,
            _ => point.Properties.IsLeftButtonPressed || point.Properties.IsRightButtonPressed,
        };

        QiuPointerEventArgs args = new(position.X, position.Y, button, isPressed);
        switch (phase)
        {
            case QiuPointerPhase.Pressed:
                canvas.RaisePointerPressed(args);
                break;
            case QiuPointerPhase.Released:
                canvas.RaisePointerReleased(args);
                break;
            default:
                canvas.RaisePointerMoved(args);
                break;
        }
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        canvas.Paint?.Invoke(new AvaloniaCanvasContext(context, Bounds.Size));
    }

    private sealed class AvaloniaCanvasContext : IQiuCanvasContext
    {
        private readonly DrawingContext context;
        private readonly Size size;

        public AvaloniaCanvasContext(DrawingContext context, Size size)
        {
            this.context = context;
            this.size = size;
        }

        public void Clear(QiuColor color) =>
            context.FillRectangle(Brush(color), new Rect(size));

        public void DrawImage(byte[] png, double x, double y, double width, double height)
        {
            using MemoryStream stream = new(png);
            Bitmap bitmap = new(stream);
            context.DrawImage(bitmap, new Rect(x, y, width, height));
        }

        public void FillRectangle(QiuColor color, double x, double y, double width, double height) =>
            context.FillRectangle(Brush(color), new Rect(x, y, width, height));

        public void DrawRectangle(QiuColor color, double x, double y, double width, double height, double thickness = 1) =>
            context.DrawRectangle(null, Pen(color, thickness), new Rect(x, y, width, height));

        public void DrawLine(QiuColor color, double x1, double y1, double x2, double y2, double thickness = 1) =>
            context.DrawLine(Pen(color, thickness), new Point(x1, y1), new Point(x2, y2));

        public void DrawText(QiuColor color, string text, double x, double y, double fontSize = 12)
        {
            FormattedText formatted = new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), fontSize, Brush(color));
            context.DrawText(formatted, new Point(x, y));
        }

        private static IBrush Brush(QiuColor color) =>
            new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));

        private static IPen Pen(QiuColor color, double thickness) =>
            new Pen(Brush(color), thickness);
    }
}
