#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UndertaleModLib.Models;

namespace UndertaleModLib.Diff;

/// <summary>
/// Describes one group of comparable resources in a data file.
/// </summary>
public sealed class AssetCategoryDefinition
{
    public required string Id { get; init; }
    public required string Title { get; init; }

    /// <summary>Enumerates resources of this category, or null when the data file has no such chunk.</summary>
    public required Func<UndertaleData, IEnumerable<object>?> Enumerate { get; init; }

    /// <summary>Set for categories represented by a single object instead of a resource list.</summary>
    public Func<UndertaleData, object?>? Singleton { get; init; }

    public bool IsCode { get; init; }
}

/// <summary>
/// The resource categories shown by the comparison tool. Internal tables (strings, texture
/// page items, embedded data, ...) are deliberately not listed; their content is folded into
/// the fingerprints of the resources that reference them.
/// </summary>
public static class AssetCategories
{
    public static IReadOnlyList<AssetCategoryDefinition> All { get; } =
    [
        GeneralInfo(),
        List("AudioGroups", "Audio Groups", d => d.AudioGroups),
        List("Sounds", "Sounds", d => d.Sounds),
        List("Sprites", "Sprites", d => d.Sprites),
        List("Backgrounds", "Backgrounds / Tilesets", d => d.Backgrounds),
        List("Paths", "Paths", d => d.Paths),
        List("Scripts", "Scripts", d => d.Scripts),
        List("Shaders", "Shaders", d => d.Shaders),
        List("Fonts", "Fonts", d => d.Fonts),
        List("Timelines", "Timelines", d => d.Timelines),
        List("GameObjects", "Game Objects", d => d.GameObjects),
        List("Rooms", "Rooms", d => d.Rooms),
        List("Extensions", "Extensions", d => d.Extensions),
        List("Sequences", "Sequences", d => d.Sequences),
        List("AnimationCurves", "Animation Curves", d => d.AnimationCurves),
        List("ParticleSystems", "Particle Systems", d => d.ParticleSystems),
        List("ParticleSystemEmitters", "Particle System Emitters", d => d.ParticleSystemEmitters),
        List("FilterEffects", "Filter Effects", d => d.FilterEffects),
        List("UINodes", "UI Nodes", d => d.UINodes),
        new AssetCategoryDefinition
        {
            Id = "Code",
            Title = "Code",
            Enumerate = d => d.Code,
            IsCode = true,
        },
    ];

    private static AssetCategoryDefinition GeneralInfo()
    {
        return new AssetCategoryDefinition
        {
            Id = "GeneralInfo",
            Title = "General Info",
            Enumerate = _ => null,
            Singleton = d => d.GeneralInfo,
        };
    }

    private static AssetCategoryDefinition List(string id, string title, Func<UndertaleData, IList?> getter)
    {
        return new AssetCategoryDefinition
        {
            Id = id,
            Title = title,
            Enumerate = data => getter(data)?.Cast<object>(),
        };
    }
}
