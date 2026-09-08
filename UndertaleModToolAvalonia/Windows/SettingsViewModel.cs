using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

namespace UndertaleModToolAvalonia;

public class SettingsViewModel
{
    public MainViewModel MainVM { get; }

    // TODO: The languages list should be moved to LocalizationSource, but now keep it here.
    public IReadOnlyList<string> Languages { get; } = new[] { "", "en", "zh-Hans", "ja", "pt" };

    public SettingsViewModel(IServiceProvider serviceProvider)
    {
        MainVM = serviceProvider.GetRequiredService<MainViewModel>();
    }
}