using System.Text.Json.Nodes;
using DevContainers.Core.Configuration;
using DevContainers.Core.Launch;

namespace DevContainers.Core.Tests;

public class CustomizationsTests
{
    [Fact]
    public void Read_KnownSettings()
    {
        var vs = DevContainerConfig.Parse("""
            {
              "image": "x",
              // JSON with comments, as devcontainer.json allows.
              "customizations": {
                "visualstudio": {
                  "startupProject": "src/Api/Api.csproj",
                  "launchProfile": "https",
                  "hotReload": true,
                  "npmScript": "dev",
                  "inspectPort": 9230,
                  "openOnLoad": "prompt"
                }
              }
            }
            """).VisualStudio;

        Assert.Equal(new VisualStudioCustomizations("src/Api/Api.csproj", "https", true, "dev", 9230, OpenOnLoad.Prompt, []), vs with { Problems = [] });
        Assert.Empty(vs.Problems);
    }

    [Fact]
    public void Read_NoSection_IsEmpty() =>
        Assert.Same(VisualStudioCustomizations.Empty, DevContainerConfig.Parse("""{ "image": "x" }""").VisualStudio);

    [Fact]
    public void Read_ReportsUnknownAndWrongSettings()
    {
        var vs = VisualStudioCustomizations.Read(System.Text.Json.JsonDocument.Parse("""
            { "hotReload": "yes", "inspectPort": 70000, "openOnLoad": "sometimes", "extensions": [], "npmScript": "" }
            """).RootElement);

        Assert.Equal(5, vs.Problems.Count);
        Assert.Contains(vs.Problems, p => p.Contains("'extensions'"));
        Assert.False(vs.HotReload);
        Assert.Equal(OpenOnLoad.Never, vs.OpenOnLoad);
    }

    [Theory]
    [InlineData("src/Api/Api.csproj", true)]
    [InlineData("./src/Api/Api.csproj", true)]
    [InlineData("src\\Api\\Api.csproj", true)]
    [InlineData("Api", true)]
    [InlineData("Web", false)]
    public void IsStartupProject_MatchesPathOrName(string setting, bool expected)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "ws");
        var vs = VisualStudioCustomizations.Empty with { StartupProject = setting };

        Assert.Equal(expected, vs.IsStartupProject(Path.Combine(workspace, "src", "Api", "Api.csproj"), workspace));
    }

    [Fact]
    public void Install_UsesTheLaunchProfileAndHotReloadFromCustomizations()
    {
        using var folder = new TempFolder();
        folder.Write(".devcontainer/devcontainer.json", """{ "image": "x", "customizations": { "visualstudio": { "launchProfile": "https", "hotReload": true } } }""");
        var project = folder.Write("Api/Api.csproj", "<Project />");
        folder.Write("Api/Properties/launchSettings.json", """
            { "profiles": { "http": { "commandName": "Project" }, "https": { "commandName": "Project" } } }
            """);
        var config = Assert.Single(DevContainerConfigLocator.FindFromFolder(folder.Path));

        var result = DevContainerLaunchProfile.Install(project, config);

        Assert.Equal("https", result.InnerLaunchProfile);
        var args = JsonNode.Parse(File.ReadAllText(result.LaunchSettingsPath))!["profiles"]![DevContainerLaunchProfile.ProfileName]!["commandLineArgs"]!.GetValue<string>();
        Assert.EndsWith("-LaunchProfile \"https\" -Watch", args);
    }
}
