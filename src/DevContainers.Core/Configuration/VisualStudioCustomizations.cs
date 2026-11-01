using System.Text.Json;

namespace DevContainers.Core.Configuration;

/// <summary>Whether Visual Studio offers to open the dev container when the solution loads.</summary>
public enum OpenOnLoad
{
    /// <summary>Never; use Open in Container. The default.</summary>
    Never,

    /// <summary>Ask, as VS Code's "Reopen in Container" notification does.</summary>
    Prompt,

    /// <summary>Open it without asking.</summary>
    Always,
}

/// <summary>
/// The <c>customizations.visualstudio</c> section of devcontainer.json: settings for this extension, as
/// <c>customizations.vscode</c> holds VS Code's. Other tools ignore it. Documented in docs/customizations.md.
/// </summary>
/// <param name="StartupProject">The project Start Debugging in Container and Add Run Profile pick without asking: a project file path relative to the workspace folder, or a project name.</param>
/// <param name="LaunchProfile">The project launch profile whose settings apply in the container, instead of the first one.</param>
/// <param name="HotReload">Whether the Dev Container launch profile runs the app with <c>dotnet watch</c>, which applies code changes while it runs.</param>
/// <param name="NpmScript">The package.json script the npm commands run without asking.</param>
/// <param name="InspectPort">The Node.js inspector port for Debug npm Script in Container (default 9229).</param>
/// <param name="OpenOnLoad">Whether to offer to open the container when the solution loads.</param>
/// <param name="Problems">Settings that are not known or have the wrong type, to report.</param>
public sealed record VisualStudioCustomizations(
    string? StartupProject,
    string? LaunchProfile,
    bool HotReload,
    string? NpmScript,
    int? InspectPort,
    OpenOnLoad OpenOnLoad,
    IReadOnlyList<string> Problems)
{
    public static VisualStudioCustomizations Empty { get; } = new(null, null, false, null, null, OpenOnLoad.Never, []);

    public static VisualStudioCustomizations Read(JsonElement? section)
    {
        if (section is not { ValueKind: JsonValueKind.Object } element)
        {
            return section is null or { ValueKind: JsonValueKind.Null } ? Empty : Empty with { Problems = ["customizations.visualstudio must be an object."] };
        }

        var problems = new List<string>();
        string? startupProject = null, launchProfile = null, npmScript = null;
        bool hotReload = false;
        int? inspectPort = null;
        var openOnLoad = OpenOnLoad.Never;
        foreach (var property in element.EnumerateObject())
        {
            var value = property.Value;
            switch (property.Name)
            {
                case "startupProject":
                    startupProject = String(value);
                    break;
                case "launchProfile":
                    launchProfile = String(value);
                    break;
                case "npmScript":
                    npmScript = String(value);
                    break;
                case "hotReload":
                    if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        hotReload = value.GetBoolean();
                    }
                    else
                    {
                        problems.Add("hotReload must be true or false.");
                    }

                    break;
                case "inspectPort":
                    if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var port) && port is > 0 and < 65536)
                    {
                        inspectPort = port;
                    }
                    else
                    {
                        problems.Add("inspectPort must be a port number.");
                    }

                    break;
                case "openOnLoad":
                    if (value.ValueKind == JsonValueKind.String && Enum.TryParse<OpenOnLoad>(value.GetString(), ignoreCase: true, out var open))
                    {
                        openOnLoad = open;
                    }
                    else
                    {
                        problems.Add("openOnLoad must be \"never\", \"prompt\" or \"always\".");
                    }

                    break;
                default:
                    problems.Add($"Unknown setting '{property.Name}'.");
                    break;
            }

            string? String(JsonElement v)
            {
                if (v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                {
                    return v.GetString()!.Trim();
                }

                problems.Add($"{property.Name} must be a non-empty string.");
                return null;
            }
        }

        return new(startupProject, launchProfile, hotReload, npmScript, inspectPort, openOnLoad, problems);
    }

    /// <summary>
    /// Whether <paramref name="projectPath"/> is the <see cref="StartupProject"/>: the same project file relative to
    /// <paramref name="workspaceFolder"/>, or the same project name.
    /// </summary>
    public bool IsStartupProject(string projectPath, string workspaceFolder)
    {
        if (this.StartupProject is not { } wanted)
        {
            return false;
        }

        var relative = Path.GetRelativePath(workspaceFolder, projectPath).Replace('\\', '/');
        var normalized = wanted.Replace('\\', '/').TrimStart('.', '/');
        return string.Equals(relative, normalized, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileNameWithoutExtension(projectPath), wanted, StringComparison.OrdinalIgnoreCase);
    }
}
