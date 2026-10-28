using System.Text;
using DevContainers.Core.Processes;

namespace DevContainers.Core.Prerequisites;

/// <summary>The steps that install whatever is missing, as shown to the user and as a script to run.</summary>
public sealed class InstallPlan
{
    /// <summary>winget id of the Node.js long-term support release.</summary>
    public const string NodePackageId = "OpenJS.NodeJS.LTS";

    public const string CliPackage = "@devcontainers/cli";

    private InstallPlan(IReadOnlyList<string> commands, IReadOnlyList<string> missing, bool canRun)
    {
        this.Commands = commands;
        this.Missing = missing;
        this.CanRun = canRun;
    }

    /// <summary>The commands, in order, as a user would type them.</summary>
    public IReadOnlyList<string> Commands { get; }

    /// <summary>Names of the missing tools, for the prompt.</summary>
    public IReadOnlyList<string> Missing { get; }

    /// <summary>False when Node.js is missing and winget is not available to install it.</summary>
    public bool CanRun { get; }

    public static InstallPlan For(PrerequisiteStatus status)
    {
        var commands = new List<string>();
        var missing = new List<string>();

        if (status.NodeMissing)
        {
            missing.Add("Node.js");
            commands.Add($"winget install --id {NodePackageId} --exact --source winget --accept-source-agreements --accept-package-agreements");
        }

        if (status.CliMissing)
        {
            missing.Add("the devcontainer CLI");
            commands.Add($"npm install -g {CliPackage}");
        }

        return new InstallPlan(commands, missing, canRun: commands.Count > 0 && (!status.NodeMissing || status.WingetPath is not null));
    }

    /// <summary>
    /// A PowerShell script that runs the commands in a visible window, so the user sees winget and npm progress and
    /// any prompts (such as UAC). PATH is reloaded after winget, because the new Node.js is not on this window's PATH.
    /// </summary>
    public string ToPowerShellScript()
    {
        var lines = new List<string>
        {
            "$ErrorActionPreference = 'Stop'",
            "$Host.UI.RawUI.WindowTitle = 'Dev Containers: installing prerequisites'",
            "function Invoke-Step([string] $command) {",
            "  Write-Host \"> $command\" -ForegroundColor Cyan",
            "  Invoke-Expression $command",
            "  if ($LASTEXITCODE -ne 0) { throw \"'$command' failed with exit code $LASTEXITCODE.\" }",
            "  $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')",
            "}",
            "try {",
        };
        lines.AddRange(this.Commands.Select(c => $"  Invoke-Step '{c.Replace("'", "''", StringComparison.Ordinal)}'"));
        lines.AddRange(
        [
            "  Write-Host 'Done. Returning to Visual Studio...' -ForegroundColor Green",
            "  Start-Sleep -Seconds 3",
            "} catch {",
            "  Write-Host $_ -ForegroundColor Red",
            "  Read-Host 'Installation failed. Press Enter to close'",
            "  exit 1",
            "}",
        ]);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Starts <c>powershell.exe</c> with the script. The script is passed base64 encoded (UTF-16LE, as
    /// <c>-EncodedCommand</c> expects) so its quotes survive Windows command-line parsing.
    /// </summary>
    public ProcessSpec ToProcessSpec() =>
        new(
            "powershell.exe",
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(this.ToPowerShellScript()))]);
}
