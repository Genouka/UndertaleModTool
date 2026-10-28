using System;
using System.IO;
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
}
