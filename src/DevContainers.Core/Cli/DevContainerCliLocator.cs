using DevContainers.Core.Processes;

namespace DevContainers.Core.Cli;

/// <summary>How to start the devcontainer CLI: an executable plus arguments that come before the CLI's own.</summary>
public sealed record DevContainerCliCommand(string FileName, IReadOnlyList<string> PrefixArguments)
{
    public ProcessSpec CreateSpec(IEnumerable<string> arguments) => new(FileName, [.. PrefixArguments, .. arguments]);
}

/// <summary>Thrown when the devcontainer CLI or Node.js cannot be found.</summary>
public sealed class DevContainerCliNotFoundException(string message) : Exception(message);

/// <summary>
/// Finds the <c>@devcontainers/cli</c> package. The CLI is always run as <c>node devcontainer.js</c> on Windows
/// so arguments never pass through <c>cmd.exe</c>, which is what npm's <c>devcontainer.cmd</c> shim would do.
/// </summary>
public sealed class DevContainerCliLocator(
    Func<string, string?>? findOnPath = null,
    Func<string, bool>? fileExists = null,
    bool? isWindows = null,
    string? appDataFolder = null)
{
    private const string PackageScript = "devcontainer.js";

    private readonly Func<string, string?> findOnPath = findOnPath ?? (name => ExecutableLocator.FindOnPath(name));
    private readonly Func<string, bool> fileExists = fileExists ?? File.Exists;
    private readonly bool isWindows = isWindows ?? OperatingSystem.IsWindows();
    private readonly string? appDataFolder = appDataFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <param name="explicitPath">A user configured path to <c>devcontainer.js</c> or a <c>devcontainer</c> executable.</param>
    public DevContainerCliCommand Locate(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return this.FromPath(explicitPath)
                ?? throw new DevContainerCliNotFoundException($"The configured devcontainer CLI path '{explicitPath}' does not exist.");
        }

        var onPath = this.findOnPath("devcontainer");
        if (onPath is not null && this.FromPath(onPath) is { } fromPath)
        {
            return fromPath;
        }

        if (this.isWindows && this.appDataFolder is not null)
        {
            var globalScript = Path.Combine(this.appDataFolder, "npm", "node_modules", "@devcontainers", "cli", PackageScript);
            if (this.FromPath(globalScript) is { } global)
            {
                return global;
            }
        }

        throw new DevContainerCliNotFoundException(
            "The devcontainer CLI was not found. Install Node.js 20 or later and run 'npm install -g @devcontainers/cli'.");
    }

    private DevContainerCliCommand? FromPath(string path)
    {
        if (!this.fileExists(path))
        {
            return null;
        }

        var extension = Path.GetExtension(path);
        if (extension.Equals(".js", StringComparison.OrdinalIgnoreCase))
        {
            return new DevContainerCliCommand(this.FindNode(), [path]);
        }

        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            // npm puts the shim next to node_modules in the global prefix folder.
            var script = Path.Combine(Path.GetDirectoryName(path)!, "node_modules", "@devcontainers", "cli", PackageScript);
            return this.fileExists(script) ? new DevContainerCliCommand(this.FindNode(), [script]) : null;
        }

        return new DevContainerCliCommand(path, []);
    }

    private string FindNode() =>
        this.findOnPath("node")
        ?? throw new DevContainerCliNotFoundException("Node.js was not found on PATH. The devcontainer CLI needs Node.js 20 or later.");
}
