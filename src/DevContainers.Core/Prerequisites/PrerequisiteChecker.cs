using DevContainers.Core.Cli;
using DevContainers.Core.Processes;

namespace DevContainers.Core.Prerequisites;

/// <summary>Which of the tools the devcontainer CLI needs are installed.</summary>
public sealed record PrerequisiteStatus(string? NodePath, string? NpmPath, DevContainerCliCommand? Cli, string? WingetPath)
{
    public bool IsReady => this.Cli is not null;

    public bool NodeMissing => this.NodePath is null;

    public bool CliMissing => this.Cli is null;
}

/// <summary>
/// Checks for Node.js and the devcontainer CLI. The extension does not ship the CLI; when something is missing it
/// offers to install Node.js with winget and the CLI with npm (the CLI is published to npm only, not to winget).
/// </summary>
public sealed class PrerequisiteChecker(Func<string, string?>? findOnPath = null, DevContainerCliLocator? cliLocator = null)
{
    private readonly Func<string, string?> findOnPath = findOnPath ?? (name => ExecutableLocator.FindOnPath(name));
    private readonly DevContainerCliLocator cliLocator = cliLocator ?? new DevContainerCliLocator(findOnPath);

    public PrerequisiteStatus Check(string? explicitCliPath)
    {
        DevContainerCliCommand? cli;
        try
        {
            cli = this.cliLocator.Locate(explicitCliPath);
        }
        catch (DevContainerCliNotFoundException)
        {
            cli = null;
        }

        return new PrerequisiteStatus(this.findOnPath("node"), this.findOnPath("npm"), cli, this.findOnPath("winget"));
    }
}
