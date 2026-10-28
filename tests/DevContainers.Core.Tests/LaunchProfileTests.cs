using System.Text.Json.Nodes;
using DevContainers.Core.Configuration;
using DevContainers.Core.Launch;

namespace DevContainers.Core.Tests;

public class LaunchProfileTests
{
    [Fact]
    public void Install_PutsProfileFirstAndKeepsTheOthers()
    {
        using var temp = new TempFolder();
        temp.Write(".devcontainer/devcontainer.json");
        var project = temp.Write("Api/Api.csproj", "<Project />");
        temp.Write("Api/Properties/launchSettings.json", """
            {
              // Comments and trailing commas are allowed in launchSettings.json.
              "profiles": {
                "IIS Express": { "commandName": "IISExpress" },
                "https": { "commandName": "Project", "applicationUrl": "https://localhost:7001" },
                "http": { "commandName": "Project" },
              },
              "iisSettings": { "windowsAuthentication": false },
            }
            """);

        var result = DevContainerLaunchProfile.Install(project, Config(temp));

        var root = ReadJson(result.LaunchSettingsPath);
        var profiles = root["profiles"]!.AsObject();
        Assert.Equal(["Dev Container", "IIS Express", "https", "http"], profiles.Select(p => p.Key));
        Assert.Equal("https://localhost:7001", (string?)profiles["https"]!["applicationUrl"]);
        Assert.NotNull(root["iisSettings"]);
        Assert.Equal("https", result.InnerLaunchProfile);

        var profile = profiles["Dev Container"]!;
        Assert.Equal("Executable", (string?)profile["commandName"]);
        Assert.Equal("powershell.exe", (string?)profile["executablePath"]);
        Assert.Equal("$(ProjectDir)", (string?)profile["workingDirectory"]);
        Assert.Equal(
            """-NoProfile -ExecutionPolicy Bypass -File "$(ProjectDir)..\.devcontainer\vs-run.ps1" -LaunchProfile "https" """.TrimEnd(),
            (string?)profile["commandLineArgs"]);
    }

    [Fact]
    public void Install_WritesTheLauncherScriptNextToDevcontainerJson()
    {
        using var temp = new TempFolder();
        temp.Write(".devcontainer/devcontainer.json");
        var project = temp.Write("src/App/App.csproj", "<Project />");

        var result = DevContainerLaunchProfile.Install(project, Config(temp));

        Assert.Equal(Path.Combine(temp.Path, ".devcontainer", "vs-run.ps1"), result.ScriptPath);
        Assert.Equal(DevContainerLaunchProfile.ReadScript(), File.ReadAllText(result.ScriptPath));
        Assert.Contains(
            """$(ProjectDir)..\..\.devcontainer\vs-run.ps1""",
            (string?)ReadJson(result.LaunchSettingsPath)["profiles"]!["Dev Container"]!["commandLineArgs"]);
    }

    [Fact]
    public void Install_CreatesLaunchSettingsWithoutInnerProfile()
    {
        using var temp = new TempFolder();
        temp.Write(".devcontainer/devcontainer.json");
        var project = temp.Write("App/App.csproj", "<Project />");

        var result = DevContainerLaunchProfile.Install(project, Config(temp));

        Assert.Null(result.InnerLaunchProfile);
        var root = ReadJson(result.LaunchSettingsPath);
        Assert.Equal("https://json.schemastore.org/launchsettings.json", (string?)root["$schema"]);
        Assert.DoesNotContain("-LaunchProfile", (string?)root["profiles"]!["Dev Container"]!["commandLineArgs"]);
    }

    [Fact]
    public void Install_TwiceKeepsOneProfile()
    {
        using var temp = new TempFolder();
        temp.Write(".devcontainer/devcontainer.json");
        var project = temp.Write("App/App.csproj", "<Project />");
        temp.Write("App/Properties/launchSettings.json", """{ "profiles": { "http": { "commandName": "Project" } } }""");

        DevContainerLaunchProfile.Install(project, Config(temp));
        var result = DevContainerLaunchProfile.Install(project, Config(temp));

        Assert.Equal(["Dev Container", "http"], ReadJson(result.LaunchSettingsPath)["profiles"]!.AsObject().Select(p => p.Key));
        Assert.Equal("http", result.InnerLaunchProfile);
    }

    [Fact]
    public void Install_PassesNonDefaultConfig()
    {
        using var temp = new TempFolder();
        var configPath = temp.Write(".devcontainer/gpu/devcontainer.json");
        var project = temp.Write("App/App.csproj", "<Project />");

        var result = DevContainerLaunchProfile.Install(project, new DevContainerConfigFile(temp.Path, configPath));

        Assert.Contains(
            """-Config "$(ProjectDir)..\.devcontainer\gpu\devcontainer.json" """.TrimEnd(),
            (string?)ReadJson(result.LaunchSettingsPath)["profiles"]!["Dev Container"]!["commandLineArgs"]);
    }

    [Fact]
    public void Install_RejectsProjectOutsideWorkspace()
    {
        using var temp = new TempFolder();
        temp.Write("repo/.devcontainer/devcontainer.json");
        var project = temp.Write("other/App.csproj", "<Project />");

        Assert.Throws<InvalidOperationException>(() => DevContainerLaunchProfile.Install(
            project,
            new DevContainerConfigFile(Path.Combine(temp.Path, "repo"), Path.Combine(temp.Path, "repo", ".devcontainer", "devcontainer.json"))));
    }

    [Theory]
    [InlineData("samples/dotnet-webapi/.devcontainer/vs-run.ps1")]
    [InlineData("samples/dotnet-console/.devcontainer/vs-run.ps1")]
    public void Samples_ShipTheCurrentLauncherScript(string relativePath)
    {
        // The samples carry a copy of the script, as a user's repository would. Regenerate it with the
        // "Add Run Profile for Dev Container" command (or copy src/DevContainers.Core/Launch/vs-run.ps1) after a change.
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "VsDevContainers.slnx")))
        {
            repository = repository.Parent;
        }

        Assert.NotNull(repository);
        Assert.Equal(
            DevContainerLaunchProfile.ReadScript().ReplaceLineEndings(),
            File.ReadAllText(Path.Combine(repository!.FullName, relativePath)).ReplaceLineEndings());
    }

    private static DevContainerConfigFile Config(TempFolder temp) =>
        new(temp.Path, Path.Combine(temp.Path, ".devcontainer", "devcontainer.json"));

    private static JsonObject ReadJson(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();
}
