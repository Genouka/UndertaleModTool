using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModTool.Localization;

namespace UndertaleModToolAvalonia;

public partial class DataExplorerViewModel : ObservableObject
{
    public MainViewModel MainVM;

    [ObservableProperty]
    public partial ObservableCollection<Item> TreeDataGridData { get; set; } = [];

    readonly List<ObservableCollectionView> observableCollectionViewList = [];

    public DataExplorerViewModel(MainViewModel mainVM)
    {
        MainVM = mainVM;
    }

    public void UpdateFromData()
    {
        TreeDataGridData.Clear();

        observableCollectionViewList.Clear();

        if (MainVM.Data is null)
            return;

        Item dataItem = new()
        {
            Value = MainVM.Data,
            Text = LocalizationSource.GetString("Tree_Data"),
            Children = [],
        };

        void AddItem(object? item, string value, string text)
        {
            if (item is not null)
                dataItem.Children.Add(new() { Value = value, Text = text });
        }

        void AddList<T>(IList<T?>? list, string value, string text) where T : class?
        {
            if (list is not null)
                dataItem.Children.Add(new() { Tag = "list", Value = value, Text = text, Children = CreateListObservableCollectionView(list) });
        }

        AddItem(MainVM.Data.GeneralInfo, "GeneralInfo", LocalizationSource.GetString("Tree_GeneralInfo"));
        AddItem(MainVM.Data.GlobalInitScripts, "GlobalInitScripts", LocalizationSource.GetString("Tree_GlobalInit"));
        AddItem(MainVM.Data.GameEndScripts, "GameEndScripts", LocalizationSource.GetString("Tree_GameEndScripts"));

        AddList(MainVM.Data.AudioGroups, "AudioGroups", LocalizationSource.GetString("Tree_AudioGroups"));
        AddList(MainVM.Data.Sounds, "Sounds", LocalizationSource.GetString("Tree_Sounds"));
        AddList(MainVM.Data.Sprites, "Sprites", LocalizationSource.GetString("Tree_Sprites"));
        AddList(MainVM.Data.Backgrounds, "Backgrounds", LocalizationSource.GetString("Tree_BackgroundsTilesets"));
        AddList(MainVM.Data.Paths, "Paths", LocalizationSource.GetString("Tree_Paths"));
        AddList(MainVM.Data.Scripts, "Scripts", LocalizationSource.GetString("Tree_Scripts"));
        AddList(MainVM.Data.Shaders, "Shaders", LocalizationSource.GetString("Tree_Shaders"));
        AddList(MainVM.Data.Fonts, "Fonts", LocalizationSource.GetString("Tree_Fonts"));
        AddList(MainVM.Data.Timelines, "Timelines", LocalizationSource.GetString("Tree_Timelines"));
        AddList(MainVM.Data.GameObjects, "GameObjects", LocalizationSource.GetString("Tree_GameObjects"));
        AddList(MainVM.Data.Rooms, "Rooms", LocalizationSource.GetString("Tree_Rooms"));
        AddList(MainVM.Data.Extensions, "Extensions", LocalizationSource.GetString("Tree_Extensions"));
        AddList(MainVM.Data.TexturePageItems, "TexturePageItems", LocalizationSource.GetString("Tree_TexturePageItems"));
        AddList(MainVM.Data.Code, "Code", LocalizationSource.GetString("Tree_Code"));
        AddList(MainVM.Data.Variables, "Variables", LocalizationSource.GetString("Tree_Variables"));
        AddList(MainVM.Data.Functions, "Functions", LocalizationSource.GetString("Tree_Functions"));
        AddList(MainVM.Data.CodeLocals, "CodeLocals", LocalizationSource.GetString("Tree_CodeLocals"));
        AddList(MainVM.Data.Strings, "Strings", LocalizationSource.GetString("Tree_Strings"));
        AddList(MainVM.Data.EmbeddedTextures, "EmbeddedTextures", LocalizationSource.GetString("Tree_EmbeddedTextures"));
        AddList(MainVM.Data.EmbeddedAudio, "EmbeddedAudio", LocalizationSource.GetString("Tree_EmbeddedAudio"));
        AddList(MainVM.Data.TextureGroupInfo, "TextureGroupInformation", LocalizationSource.GetString("Tree_TextureGroupInfo"));
        AddList(MainVM.Data.EmbeddedImages, "EmbeddedImages", LocalizationSource.GetString("Tree_EmbeddedImages"));
        AddList(MainVM.Data.AnimationCurves, "AnimationCurves", LocalizationSource.GetString("Tree_ParticleSystems"));
        AddList(MainVM.Data.ParticleSystems, "ParticleSystems", LocalizationSource.GetString("Tree_ParticleSystems"));
        AddList(MainVM.Data.ParticleSystemEmitters, "ParticleSystemEmitters", LocalizationSource.GetString("Tree_ParticleSystemEmitters"));

        TreeDataGridData.Add(dataItem);
    }

    ObservableCollectionView<T?, Item>.CustomObservableCollection<Item>? CreateListObservableCollectionView<T>(IList<T?>? list) where T : class?
    {
        if (list is not null)
        {
            ObservableCollectionView<T?, Item> view = new(list,
                transform: x => new Item() { Text = "", Value = x },
                filter: item => AssetNameContainsText(item.Value, MainVM.FilterText));

            observableCollectionViewList.Add(view);

            return view.Output;
        }
        return null;
    }

    public void SetFilter()
    {
        foreach (ObservableCollectionView view in observableCollectionViewList)
        {
            view.SetFilter(item => AssetNameContainsText(((Item)item!).Value, MainVM.FilterText));
        }
    }

    public void SetSort()
    {
        Comparison<object?>? comparison = null;
        if (MainVM.IsSorted)
        {
            comparison = static (a, b) =>
            {
                string? aName = AssetGetName(((Item)a!).Value);
                string? bName = AssetGetName(((Item)b!).Value);

                if (aName is null && bName is null) return 0;
                if (aName is null) return 1;
                if (bName is null) return -1;

                return aName.CompareTo(bName, StringComparison.Ordinal);
            };
        }

        foreach (ObservableCollectionView view in observableCollectionViewList)
        {
            view.SetSort(comparison);
        }
    }

    static bool AssetNameContainsText(object? asset, string text)
    {
        if (text == "")
            return true;

        string? name = AssetGetName(asset);

        if (name is null)
            return false;

        return name.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    static string? AssetGetName(object? asset)
    {
        return asset switch
        {
            UndertaleNamedResource namedResource => namedResource.Name?.Content,
            UndertaleString _string => _string.Content,
            _ => null,
        };
    }

    public partial class Item : ObservableObject
    {
        [ObservableProperty]
        public partial string Text { get; set; } = "<unset text!>";
        public object? Value { get; set; }
        public object? Tag { get; set; }

        [ObservableProperty]
        public partial IList<Item>? Children { get; set; }
    }
}
