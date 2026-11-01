namespace DevContainers.Core.Runtime;

/// <summary>
/// The labels that identify the dev container of a workspace: <c>devcontainer.local_folder</c> and
/// <c>devcontainer.config_file</c>. The devcontainer CLI and VS Code both find "the" container of a folder by these
/// labels, so passing the exact values of an existing container (<c>--id-label</c>) makes Visual Studio reuse it,
/// including one that VS Code created, instead of creating a second container for the same folder.
/// </summary>
public sealed record DevContainerIdentity(string LocalFolder, string ConfigFile)
{
    /// <summary>The <c>--id-label</c> arguments for <c>devcontainer up</c> and <c>devcontainer exec</c>.</summary>
    public IReadOnlyList<string> ToCliArguments() =>
    [
        "--id-label", $"{ContainerEngineClient.LocalFolderLabel}={this.LocalFolder}",
        "--id-label", $"{ContainerEngineClient.ConfigFileLabel}={this.ConfigFile}",
    ];

    /// <summary>
    /// Labels for a container that does not exist yet, written the way VS Code writes them (a file system path
    /// with a lowercase drive letter on Windows), so VS Code finds and reuses the container too.
    /// </summary>
    public static DevContainerIdentity ForNewContainer(string workspaceFolder, string configPath, bool? isWindows = null)
    {
        var windows = isWindows ?? OperatingSystem.IsWindows();
        return new(ToLabelPath(workspaceFolder, windows), ToLabelPath(configPath, windows));
    }

    /// <summary>
    /// Whether two label paths name the same file or folder. Windows paths compare without case and with either
    /// separator, which is how containers created by different tools (C:\ or c:\) still match.
    /// </summary>
    public static bool SamePath(string? a, string? b, bool? isWindows = null)
    {
        if (a is null || b is null)
        {
            return false;
        }

        var windows = isWindows ?? OperatingSystem.IsWindows();
        return string.Equals(Normalize(a, windows), Normalize(b, windows), windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string ToLabelPath(string path, bool windows)
    {
        var normalized = Normalize(path, windows);
        return windows && normalized.Length >= 2 && normalized[1] == ':' ? char.ToLowerInvariant(normalized[0]) + normalized[1..] : normalized;
    }

    private static string Normalize(string path, bool windows)
    {
        var separator = windows ? '\\' : '/';
        var normalized = windows ? path.Replace('/', '\\') : path;
        var trimmed = normalized.TrimEnd(separator);

        // Keep the root itself ("/" or "c:\").
        return trimmed.Length == 0 || (windows && trimmed.Length == 2 && trimmed[1] == ':') ? trimmed + separator : trimmed;
    }
}
