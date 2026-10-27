using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace UndertaleModToolAvalonia;

public partial class SettingsViewModel : ObservableObject
{
    public MainViewModel MainVM { get; }

    // TODO: The languages list should be moved to LocalizationSource, but now keep it here.
    public IReadOnlyList<string> Languages { get; } = new[] { "", "en", "zh-Hans", "ja", "pt", "ko", "tr", "ar-EG" };

    public enum IndentStyleValue
    {
        FourSpaces = 0,
        TwoSpaces = 1,
        Tabs = 2,
        Custom = 3,
    }

    public IndentStyleValue IndentStyle
    {
        get;
        set
        {
            field = value;

            MainVM.Settings.DecompileSettings.IndentString = value switch
            {
                IndentStyleValue.FourSpaces => "    ",
                IndentStyleValue.TwoSpaces => "  ",
                IndentStyleValue.Tabs => "\t",
                IndentStyleValue.Custom => MainVM.Settings.DecompileSettings.IndentString,
                _ => throw new NotImplementedException(),
            };

            IsCustomIndentStyle = value is IndentStyleValue.Custom;
        }
    }

    /// <summary>Selected update channel as a combo box index (Stable = 0, Nightly = 1).</summary>
    public int UpdateChannelIndex
    {
        get => (int)MainVM.Settings.UpdateChannel;
        set
        {
            if (value is < 0 or > 1)
                return;

            UpdateChannel channel = (UpdateChannel)value;
            MainVM.Settings.UpdateChannel = channel;

            // Nightly builds are only published on GitHub.
            if (channel == UpdateChannel.Nightly)
                MainVM.Settings.UpdateSource = UpdateSource.GitHub;

            OnPropertyChanged();
            OnPropertyChanged(nameof(UpdateSourceIndex));
            OnPropertyChanged(nameof(IsMirrorChyanSource));
        }
    }

    /// <summary>Selected download source as a combo box index (GitHub = 0, MirrorChyan = 1).</summary>
    public int UpdateSourceIndex
    {
        get => (int)MainVM.Settings.UpdateSource;
        set
        {
            if (value is < 0 or > 1)
                return;

            UpdateSource source = (UpdateSource)value;
            MainVM.Settings.UpdateSource = source;

            // MirrorChyan only mirrors stable releases.
            if (source == UpdateSource.MirrorChyan)
                MainVM.Settings.UpdateChannel = UpdateChannel.Stable;

            OnPropertyChanged();
            OnPropertyChanged(nameof(UpdateChannelIndex));
            OnPropertyChanged(nameof(IsMirrorChyanSource));
        }
    }

    /// <summary>Whether the MirrorChyan CDK controls are relevant for the selected source.</summary>
    public bool IsMirrorChyanSource => MainVM.Settings.UpdateSource == UpdateSource.MirrorChyan;

    [ObservableProperty]
    public partial bool IsCustomIndentStyle { get; set; }

    public SettingsViewModel(IServiceProvider serviceProvider)
    {
        MainVM = serviceProvider.GetRequiredService<MainViewModel>();

        // Nightly builds are only available from GitHub; ignore a stale MirrorChyan selection.
        if (MainVM.Settings.UpdateChannel == UpdateChannel.Nightly)
            MainVM.Settings.UpdateSource = UpdateSource.GitHub;

        IndentStyle = MainVM.Settings.DecompileSettings.IndentString switch
        {
            "    " => IndentStyleValue.FourSpaces,
            "  " => IndentStyleValue.TwoSpaces,
            "\t" => IndentStyleValue.Tabs,
            _ => IndentStyleValue.Custom,
        };

        IsCustomIndentStyle = (IndentStyle is IndentStyleValue.Custom);
    }
}