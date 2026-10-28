using DevContainers.Core.Configuration;

namespace DevContainers.Core.Tests;

public class ConfigurationTests
{
    [Fact]
    public void Parse_AcceptsCommentsAndTrailingCommas()
    {
        var config = DevContainerConfig.Parse("""
            // Line comment
            {
              /* block comment */
              "name": "C# app",
              "image": "mcr.microsoft.com/devcontainers/dotnet:8.0", // trailing
              "forwardPorts": [5000, "db:5432"],
              "remoteUser": "vscode",
            }
            """);

        Assert.Equal("C# app", config.Name);
        Assert.Equal(DevContainerKind.Image, config.Kind);
        Assert.Equal(["5000", "db:5432"], config.ForwardPorts);
        Assert.Equal("vscode", config.RemoteUser);
    }

    [Fact]
    public void Parse_DetectsDockerfileFromBuildSection()
    {
        var config = DevContainerConfig.Parse("""{ "build": { "dockerfile": "Dockerfile", "context": ".." } }""");

        Assert.Equal(DevContainerKind.Dockerfile, config.Kind);
        Assert.Equal("Dockerfile", config.Dockerfile);
    }

    [Fact]
    public void Parse_DetectsLegacyDockerFileProperty()
    {
        var config = DevContainerConfig.Parse("""{ "dockerFile": "Dockerfile" }""");

        Assert.Equal(DevContainerKind.Dockerfile, config.Kind);
    }

    [Fact]
    public void Parse_DetectsComposeWithSingleOrMultipleFiles()
    {
        var single = DevContainerConfig.Parse("""{ "dockerComposeFile": "compose.yml", "service": "app", "workspaceFolder": "/src" }""");
        var multiple = DevContainerConfig.Parse("""{ "dockerComposeFile": ["a.yml", "b.yml"], "service": "app" }""");

        Assert.Equal(DevContainerKind.DockerCompose, single.Kind);
        Assert.Equal(["compose.yml"], single.DockerComposeFiles);
        Assert.Equal("app", single.Service);
        Assert.Equal("/src", single.WorkspaceFolder);
        Assert.Equal(["a.yml", "b.yml"], multiple.DockerComposeFiles);
    }

    [Fact]
    public void Parse_ExposesVisualStudioCustomizations()
    {
        var config = DevContainerConfig.Parse("""{ "image": "x", "customizations": { "visualstudio": { "startupProject": "App" }, "vscode": {} } }""");

        Assert.NotNull(config.VisualStudioCustomizations);
        Assert.Equal("App", config.VisualStudioCustomizations!.Value.GetProperty("startupProject").GetString());
    }

    [Fact]
    public void Parse_RejectsNonObjectRoot()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => DevContainerConfig.Parse("[]"));
    }

    [Fact]
    public void Locator_FindsAllConventionalLocationsInOrder()
    {
        using var temp = new TempFolder();
        var nestedB = temp.Write(".devcontainer/b/devcontainer.json");
        var nestedA = temp.Write(".devcontainer/a/devcontainer.json");
        var root = temp.Write(".devcontainer.json");
        var primary = temp.Write(".devcontainer/devcontainer.json");

        var found = DevContainerConfigLocator.FindInFolder(temp.Path);

        Assert.Equal([primary, root, nestedA, nestedB], found.Select(f => f.ConfigPath));
        Assert.All(found, f => Assert.Equal(temp.Path, f.WorkspaceFolder));
        Assert.Equal(".devcontainer/a/devcontainer.json", found[2].DisplayName);
    }

    [Fact]
    public void Locator_WalksUpFromSolutionFolderToRepositoryRoot()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(Path.Combine(temp.Path, ".git"));
        var config = temp.Write(".devcontainer/devcontainer.json");
        var solutionFolder = Path.Combine(temp.Path, "src", "App");
        Directory.CreateDirectory(solutionFolder);

        var found = Assert.Single(DevContainerConfigLocator.FindFromFolder(solutionFolder));

        Assert.Equal(config, found.ConfigPath);
        Assert.Equal(temp.Path, found.WorkspaceFolder);
    }

    [Fact]
    public void Locator_DoesNotSearchAboveRepositoryRoot()
    {
        using var temp = new TempFolder();
        temp.Write(".devcontainer/devcontainer.json");
        var repo = Path.Combine(temp.Path, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        Assert.Empty(DevContainerConfigLocator.FindFromFolder(repo));
    }
}
