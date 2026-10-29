using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevContainers.Core.Configuration;

namespace DevContainers.Core.Launch;

/// <summary>What <see cref="DevContainerLaunchProfile.Install"/> wrote.</summary>
public sealed record LaunchProfileResult(string ScriptPath, string LaunchSettingsPath, string? InnerLaunchProfile);

/// <summary>
/// Adds a "Dev Container" launch profile to a project, so the Run button in Visual Studio starts the project inside
/// its dev container. The profile runs <c>.devcontainer/vs-run.ps1</c>, which is written next to devcontainer.json.
/// Both files belong in source control: the profile then works for everyone right after cloning.
/// </summary>
public static class DevContainerLaunchProfile
{
    public const string ProfileName = "Dev Container";

    public const string ScriptFileName = "vs-run.ps1";

    private const string ScriptResource = "DevContainers.Core.Launch.vs-run.ps1";

    // Relaxed escaping keeps the quotes in commandLineArgs readable (\" instead of \u0022).
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The launcher script that ships with the extension.</summary>
    public static string ReadScript()
    {
        using var stream = typeof(DevContainerLaunchProfile).Assembly.GetManifestResourceStream(ScriptResource)
            ?? throw new InvalidOperationException($"Embedded resource {ScriptResource} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Writes the launcher script and adds (or replaces) the profile, as the first one of the project.</summary>
    public static LaunchProfileResult Install(string projectFile, DevContainerConfigFile config)
    {
        var projectFolder = Path.GetDirectoryName(Path.GetFullPath(projectFile))!;
        var workspace = Path.GetFullPath(config.WorkspaceFolder);
        if (!IsInside(projectFolder, workspace))
        {
            throw new InvalidOperationException($"The project {projectFile} is not inside the dev container's folder {workspace}.");
        }

        var scriptPath = Path.Combine(workspace, ".devcontainer", ScriptFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        File.WriteAllText(scriptPath, ReadScript());

        var settingsPath = Path.Combine(projectFolder, "Properties", "launchSettings.json");
        var root = File.Exists(settingsPath)
            ? JsonNode.Parse(File.ReadAllText(settingsPath), documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                ?? throw new InvalidOperationException($"{settingsPath} does not contain a JSON object.")
            : new JsonObject { ["$schema"] = "https://json.schemastore.org/launchsettings.json" };

        var existing = root["profiles"] as JsonObject ?? [];
        var inner = existing
            .Where(p => p.Key != ProfileName && p.Value?["commandName"]?.GetValue<string>() == "Project")
            .Select(p => p.Key)
            .FirstOrDefault();

        // Visual Studio selects the first profile when nobody picked one yet, so after cloning the Run button
        // starts the app in the container.
        var profiles = new JsonObject
        {
            [ProfileName] = CreateProfile(projectFolder, scriptPath, workspace, config.ConfigPath, inner),
        };
        foreach (var (name, value) in existing.Where(p => p.Key != ProfileName).ToList())
        {
            profiles[name] = value?.DeepClone();
        }

        root["profiles"] = profiles;
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, root.ToJsonString(WriteOptions) + Environment.NewLine);

        return new LaunchProfileResult(scriptPath, settingsPath, inner);
    }

    internal static JsonObject CreateProfile(string projectFolder, string scriptPath, string workspace, string configPath, string? innerProfile)
    {
        // $(ProjectDir) is expanded by Visual Studio and ends with a backslash. Paths stay relative to it, so the
        // profile works wherever the repository is cloned.
        var arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"$(ProjectDir){RelativeWindowsPath(projectFolder, scriptPath)}\"";

        var defaultConfig = Path.Combine(workspace, ".devcontainer", "devcontainer.json");
        if (!string.Equals(Path.GetFullPath(configPath), defaultConfig, StringComparison.OrdinalIgnoreCase))
        {
            arguments += $" -Config \"$(ProjectDir){RelativeWindowsPath(projectFolder, configPath)}\"";
        }

        if (innerProfile is not null)
        {
            arguments += $" -LaunchProfile \"{innerProfile}\"";
        }

        return new JsonObject
        {
            ["commandName"] = "Executable",
            ["executablePath"] = "powershell.exe",
            ["commandLineArgs"] = arguments,
            ["workingDirectory"] = "$(ProjectDir)",
        };
    }

    private static string RelativeWindowsPath(string from, string to) =>
        Path.GetRelativePath(from, Path.GetFullPath(to)).Replace('/', '\\');

    private static bool IsInside(string folder, string parent)
    {
        var relative = Path.GetRelativePath(parent, folder);
        return relative == "." || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }
}
