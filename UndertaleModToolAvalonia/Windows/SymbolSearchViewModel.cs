using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModTool.Localization;

namespace UndertaleModToolAvalonia;

public partial class SymbolSearchViewModel : ObservableObject
{
    public UndertaleData Data { get; }

    readonly List<UndertaleNamedResource> allResources = new();
    List<UndertaleNamedResource> filteredResources = new();

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    [ObservableProperty]
    public partial List<UndertaleNamedResource> Resources { get; set; } = new();

    [ObservableProperty]
    public partial UndertaleNamedResource? SelectedResource { get; set; }

    [ObservableProperty]
    public partial string Header { get; set; } = "";

    public SymbolSearchViewModel(UndertaleData data)
    {
        Data = data;

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
                if (obj is UndertaleNamedResource named && named.Name?.Content is { Length: > 0 })
                    allResources.Add(named);
            }
        }

        Header = string.Format(LocalizationSource.GetString("Editor_SymbolSearch"), allResources.Count);
        ApplyFilter();
    }

    partial void OnFilterChanged(string value)
    {
        ApplyFilter();
    }

    void ApplyFilter()
    {
        string filter = Filter;
        if (string.IsNullOrEmpty(filter))
        {
            Resources = new List<UndertaleNamedResource>(allResources);
        }
        else
        {
            Resources = allResources
                .Where(r => r.Name!.Content.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }
}