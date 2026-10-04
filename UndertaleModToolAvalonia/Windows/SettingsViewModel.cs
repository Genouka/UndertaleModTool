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

    [ObservableProperty]
    public partial bool IsCustomIndentStyle { get; set; }

    public SettingsViewModel(IServiceProvider serviceProvider)
    {
        MainVM = serviceProvider.GetRequiredService<MainViewModel>();

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