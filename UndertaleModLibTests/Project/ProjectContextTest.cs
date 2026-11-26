using System;
using System.IO;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Project;
using Xunit;

namespace UndertaleModLibTests.Project;

public class ProjectContextTest
{
    static string CreateRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "utmt-project-context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    static ProjectContext CreateProject(string root, string projectFolder, string destinationDataPath)
        => new(null!, Path.Combine(root, "source", "data.win"), destinationDataPath,
               Path.Combine(projectFolder, "project.json"), "Test Mod");

    [Fact]
    public void DestinationDataFileInProjectFolderDoesNotBlockCreation()
    {
        // Android's SAF file picker creates the chosen file immediately, so the destination data
        // file can already be inside the project folder when the project context is constructed.
        string root = CreateRoot();
        try
        {
            string projectFolder = Path.Combine(root, "project");
            Directory.CreateDirectory(projectFolder);
            string destinationDataPath = Path.Combine(projectFolder, "data.win");
            File.WriteAllBytes(destinationDataPath, []);

            ProjectContext project = CreateProject(root, projectFolder, destinationDataPath);

            Assert.Equal(Path.GetFullPath(projectFolder), project.MainDirectory);
            Assert.True(File.Exists(Path.Combine(projectFolder, "project.json")));
            Assert.True(File.Exists(destinationDataPath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void OtherFilesInProjectFolderStillBlockCreation()
    {
        string root = CreateRoot();
        try
        {
            string projectFolder = Path.Combine(root, "project");
            Directory.CreateDirectory(projectFolder);
            File.WriteAllText(Path.Combine(projectFolder, "unrelated.txt"), "x");

            ProjectException exception = Assert.Throws<ProjectException>(() =>
                CreateProject(root, projectFolder, Path.Combine(root, "dest", "data.win")));
            Assert.Contains("not empty", exception.Message);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ExistingProjectFileStillBlocksCreation()
    {
        string root = CreateRoot();
        try
        {
            string projectFolder = Path.Combine(root, "project");
            Directory.CreateDirectory(projectFolder);
            File.WriteAllText(Path.Combine(projectFolder, "project.json"), "{}");

            ProjectException exception = Assert.Throws<ProjectException>(() =>
                CreateProject(root, projectFolder, Path.Combine(root, "dest", "data.win")));
            Assert.Contains("already exists", exception.Message);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TryGetAssetFilePathFindsExistingProjectFiles()
    {
        string root = CreateRoot();
        try
        {
            UndertaleData data = UndertaleData.CreateNew();
            UndertaleCode code = new() { Name = data.Strings.MakeString("gml_Object_obj_Test_Create_0") };
            data.Code.Add(code);
            UndertaleCode unexportedCode = new() { Name = data.Strings.MakeString("gml_Object_obj_Other_Create_0") };
            data.Code.Add(unexportedCode);
            UndertaleCode loadedCode = new() { Name = data.Strings.MakeString("gml_Object_obj_Loaded_Create_0") };
            data.Code.Add(loadedCode);

            ProjectContext project = new(
                data,
                Path.Combine(root, "source", "data.win"),
                Path.Combine(root, "dest", "data.win"),
                Path.Combine(root, "project", "project.json"),
                "Test Mod");

            // Nothing was written yet, so there is no file to point at.
            Assert.False(project.TryGetAssetFilePath(code, out _));

            project.MarkAssetForExport(code);
            project.Export(false);

            Assert.True(project.TryGetAssetFilePath(code, out string path));
            Assert.True(File.Exists(path));
            Assert.Equal(".gml", Path.GetExtension(path));

            // Assets that were not part of the export have no file to point at.
            Assert.False(project.TryGetAssetFilePath(unexportedCode, out _));

            // An asset whose file was recorded when the project was loaded, but which was not
            // exported during this session, is resolved from the recorded path.
            string loadedDirectory = Path.Combine(root, "loaded");
            Directory.CreateDirectory(loadedDirectory);
            string loadedJsonPath = Path.Combine(loadedDirectory, "gml_Object_obj_Loaded_Create_0.json");
            File.WriteAllText(loadedJsonPath, "{}");
            string loadedSourcePath = Path.ChangeExtension(loadedJsonPath, "gml");
            File.WriteAllText(loadedSourcePath, "// loaded source");
            project.RegisterAssetDataPath(loadedCode.Name.Content, SerializableAssetType.Code, loadedJsonPath);

            Assert.True(project.TryGetAssetFilePath(loadedCode, out string loadedPath));
            Assert.Equal(loadedSourcePath, loadedPath);
            Assert.True(File.Exists(loadedPath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
