using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

namespace UndertaleModToolAvalonia;

public class SettingsViewModel
{
    public MainViewModel MainVM { get; }

    public IReadOnlyList<string> Languages { get; } = new[] { "", "en", "zh-CN" };

    public SettingsViewModel(IServiceProvider serviceProvider)
    {
        MainVM = serviceProvider.GetRequiredService<MainViewModel>();
    }
}