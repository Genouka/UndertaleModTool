using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace UndertaleModToolAvalonia;

public partial class SpriteImportOptionsWindow : Window
{
    public bool IsSpecialType { get; private set; }
    public uint SpecialVersion { get; private set; }
    public float AnimationSpeed { get; private set; }
    public int PlaybackType { get; private set; }
    public string OriginPosition { get; private set; } = "Top Left";
    public bool Succeeded { get; private set; }

    public SpriteImportOptionsWindow(bool gms2)
    {
        InitializeComponent();

        PlaybackTypeBox.SelectedIndex = 0;
        OriginPositionBox.SelectedIndex = 0;

        IsSpecialBox.IsEnabled = gms2;
        SpecialVersionBox.IsEnabled = gms2;
        AnimationSpeedBox.IsEnabled = gms2;

        IsSpecialBox.IsCheckedChanged += (o, e) =>
        {
            bool isEnabled = IsSpecialBox.IsChecked == true;
            SpecialVersionBox.IsEnabled = gms2 && isEnabled;
            AnimationSpeedBox.IsEnabled = gms2 && isEnabled;
        };
    }

    public void ConfirmButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!float.TryParse(AnimationSpeedBox.Text, out float speed))
        {
            ShortMessage("Please use a number in the animation speed.");
            return;
        }

        if (!uint.TryParse(SpecialVersionBox.Text, out uint version))
        {
            ShortMessage("Please use a number in the special version.");
            return;
        }

        IsSpecialType = IsSpecialBox.IsChecked == true;
        SpecialVersion = version;
        AnimationSpeed = speed;
        PlaybackType = PlaybackTypeBox.SelectedIndex;
        OriginPosition = ((ComboBoxItem?)OriginPositionBox.SelectedItem)?.Content?.ToString() ?? "Top Left";
        Succeeded = true;
        Close();
    }

    public void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    async void ShortMessage(string text)
    {
        Window? owner = WindowHost.ResolveOwner(this) ?? this;
        await WindowHost.ShowDialog(owner, new MessageWindow(text));
    }
}