using System;
using System.Collections.Generic;

namespace UndertaleModLib.Project;

/// <summary>
/// A resource which is available to be imported from an external source file.
/// </summary>
public sealed class ImportableResource
{
    /// <summary>
    /// Name of the resource in the source file, which is the name it would be created with in game data.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// User-facing name of the resource type (for example, "Game Object").
    /// </summary>
    public required string TypeName { get; init; }

    /// <summary>
    /// Whether a resource with the same name already exists in the target game data.
    /// </summary>
    public bool ExistsInTarget { get; init; }

    /// <summary>
    /// Importer-specific data for this resource.
    /// </summary>
    public required object Payload { get; init; }
}

/// <summary>
/// An external source file which resources can be imported from into game data.
/// </summary>
public interface IResourceImportSource : IDisposable
{
    /// <summary>
    /// Notes gathered while loading the source file, which should be shown to the user before importing
    /// (for example, resource types in the source file which cannot be imported).
    /// </summary>
    IReadOnlyList<string> SourceNotices { get; }

    /// <summary>
    /// Loads a source file, replacing any previously loaded one.
    /// </summary>
    void LoadFile(string filePath);

    /// <summary>
    /// Enumerates all resources in the source file which can be imported.
    /// </summary>
    IReadOnlyList<ImportableResource> EnumerateResources();

    /// <summary>
    /// Imports the given resources into the target game data.
    /// </summary>
    /// <param name="selectedResources">Resources to import, as returned by <see cref="EnumerateResources"/>.</param>
    /// <param name="conflictResolution">How to handle resources which already exist in the target game data.</param>
    /// <param name="importDependencies">Whether resources referenced by the selection should be included automatically.</param>
    CrossFileImportResult ImportResources(IReadOnlyList<ImportableResource> selectedResources,
                                          NameConflictResolution conflictResolution, bool importDependencies);
}
