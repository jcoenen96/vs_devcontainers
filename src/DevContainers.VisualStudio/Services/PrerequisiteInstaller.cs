using System.Diagnostics;
using DevContainers.Core.Prerequisites;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.RpcContracts.ProgressReporting;

namespace DevContainers.VisualStudio.Services;

/// <summary>
/// Makes sure Node.js and the devcontainer CLI are installed before a dev container is started. The extension does
/// not ship them: when they are missing it recommends installing them with winget (Node.js) and npm (the CLI,
/// which is published to npm only) and runs the installation when the user agrees.
/// </summary>
internal sealed class PrerequisiteInstaller(VisualStudioExtensibility extensibility)
{
    private const int InstallChoice = 0;
    private const int Dismissed = -1;

    private readonly PrerequisiteChecker checker = new();

    /// <summary>Returns true when the tools are available, installing them first if the user agrees.</summary>
    public async Task<bool> EnsureAsync(string? cliPath, Action<string> log, CancellationToken cancellationToken)
    {
        var status = this.checker.Check(cliPath);
        if (status.IsReady)
        {
            return true;
        }

        var plan = InstallPlan.For(status);
        var commands = string.Join("\n", plan.Commands.Select(c => "    " + c));
        var missing = string.Join(" and ", plan.Missing);
        log($"Missing prerequisites: {missing}.");

        if (!plan.CanRun)
        {
            log("winget was not found, so the prerequisites cannot be installed automatically.");
            await extensibility.Shell().ShowPromptAsync(
                $"Dev containers need {missing}, which are not installed, and winget (App Installer) was not found to install them.\n\n" +
                $"Install App Installer from the Microsoft Store, or install Node.js LTS from https://nodejs.org, then run:\n\n{commands}",
                PromptOptions.OK,
                cancellationToken);
            return false;
        }

        var choices = new ChoiceResultCollection<int>
        {
            { "Install now", InstallChoice },
            { "Cancel", Dismissed },
        };
        var choice = await extensibility.Shell().ShowPromptAsync(
            $"Dev containers need {missing}, which are not installed.\n\n" +
            $"Visual Studio can install them for you with these commands:\n\n{commands}\n\n" +
            "A window opens to show the progress. Windows may ask for permission to install Node.js.",
            new PromptOptions<int>(choices, defaultChoiceIndex: 0, dismissedReturns: Dismissed) { Title = "Dev Containers" },
            cancellationToken);

        if (choice != InstallChoice)
        {
            log("Installation of the prerequisites was declined.");
            return false;
        }

        if (!await this.RunAsync(plan, log, cancellationToken))
        {
            return false;
        }

        var after = this.checker.Check(cliPath);
        if (after.IsReady)
        {
            log("Prerequisites installed.");
            return true;
        }

        var remaining = string.Join("\n", InstallPlan.For(after).Commands.Select(c => "    " + c));
        log("The prerequisites are still missing after the installation.");
        await extensibility.Shell().ShowPromptAsync(
            $"The installation finished, but {string.Join(" and ", InstallPlan.For(after).Missing)} still could not be found.\n\n" +
            $"Run these commands yourself, then try again:\n\n{remaining}",
            PromptOptions.OK,
            cancellationToken);
        return false;
    }

    private async Task<bool> RunAsync(InstallPlan plan, Action<string> log, CancellationToken cancellationToken)
    {
        var spec = plan.ToProcessSpec();
        var startInfo = new ProcessStartInfo(spec.FileName)
        {
            // A console program started without a console of its own gets a new, visible console window.
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        foreach (var arg in spec.Arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (var command in plan.Commands)
        {
            log("> " + command);
        }

        using var progress = await extensibility.Shell().StartProgressReportingAsync(
            "Installing dev container prerequisites",
            new ProgressReporterOptions(isWorkCancellable: false),
            cancellationToken);
        progress.Report(new ProgressStatus(percentComplete: null, "Waiting for the installation window to finish"));

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            log("Could not start powershell.exe to run the installation.");
            return false;
        }

        await process.WaitForExitAsync(cancellationToken);
        log($"Installation finished with exit code {process.ExitCode}.");
        return process.ExitCode == 0;
    }
}
