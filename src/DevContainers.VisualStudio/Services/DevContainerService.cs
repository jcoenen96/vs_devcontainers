using System.Collections.Concurrent;
using System.Diagnostics;
using DevContainers.Core;
using DevContainers.Core.Configuration;
using DevContainers.Core.Debugging;
using DevContainers.Core.Launch;
using DevContainers.Core.Runtime;
using DevContainers.Core.Testing;
using DevContainers.VisualStudio.Settings;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.Extensibility.Shell.FileDialog;
using Microsoft.VisualStudio.ProjectSystem.Query;
using Microsoft.VisualStudio.RpcContracts.ProgressReporting;

namespace DevContainers.VisualStudio.Services;

/// <summary>
/// Connects Visual Studio (solution, prompts, progress, Output window) to the dev container sessions in Core.
/// </summary>
internal sealed class DevContainerService(VisualStudioExtensibility extensibility, PrerequisiteInstaller prerequisites) : IDisposable
{
    private const string OutputName = "Dev Containers";

    private readonly ConcurrentDictionary<string, DevContainerSession> sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim outputGate = new(1, 1);
    private OutputChannel? output;

    /// <summary>The session the user worked with last, which Stop and Open Shell act on.</summary>
    public DevContainerSession? Current { get; private set; }

    /// <summary>Opens (or rebuilds) the dev container for the current solution.</summary>
    public async Task OpenAsync(bool rebuild, bool noCache, CancellationToken cancellationToken)
    {
        var configFile = await this.ResolveConfigAsync(cancellationToken);
        if (configFile is null)
        {
            return;
        }

        var session = this.sessions.GetOrAdd(configFile.ConfigPath, _ => new DevContainerSession(configFile));
        this.Current = session;
        var settings = await this.ReadSettingsAsync(cancellationToken);
        var log = await this.GetLogAsync(cancellationToken);

        var verb = rebuild ? "Rebuilding" : "Opening";
        log($"{verb} dev container from {configFile.ConfigPath}");

        if (!await prerequisites.EnsureAsync(settings.CliPath, log, cancellationToken))
        {
            return;
        }

        if (rebuild && !await this.ConfirmVsCodeDisconnectAsync(session, settings, "Rebuilding", cancellationToken))
        {
            log("Rebuild cancelled: the container is open in VS Code.");
            return;
        }

        using var progress = await extensibility.Shell().StartProgressReportingAsync(
            $"{verb} dev container",
            new ProgressReporterOptions(isWorkCancellable: true),
            cancellationToken);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, progress.CancellationToken);

        void Report(string line)
        {
            log(line);
            progress.Report(new ProgressStatus(percentComplete: null, Truncate(line, 120)));
        }

        try
        {
            var result = rebuild
                ? await session.RebuildAsync(settings, noCache, Report, linked.Token)
                : await session.OpenAsync(settings, Report, linked.Token);

            log($"Dev container is running: {ShortId(result.ContainerId)}, workspace {result.RemoteWorkspaceFolder}, user {result.RemoteUser ?? "(default)"}.");
            await extensibility.Shell().ShowPromptAsync(
                $"The dev container is running ({session.Runtime?.Kind}).\n\nYour solution folder is mounted at {result.RemoteWorkspaceFolder}. Use 'Open Container Terminal' for a shell inside it.",
                PromptOptions.OK,
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            log("Cancelled.");
        }
        catch (Exception ex)
        {
            await this.ReportFailureAsync("The dev container could not be started.", session.LastError ?? ex.Message, cancellationToken);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var session = this.Current;
        if (session is null)
        {
            var configFile = await this.ResolveConfigAsync(cancellationToken);
            if (configFile is null)
            {
                return;
            }

            session = this.sessions.GetOrAdd(configFile.ConfigPath, _ => new DevContainerSession(configFile));
        }

        var log = await this.GetLogAsync(cancellationToken);
        log($"Stopping dev container for {session.ConfigFile.WorkspaceFolder}");
        try
        {
            var settings = await this.ReadSettingsAsync(cancellationToken);
            if (!await this.ConfirmVsCodeDisconnectAsync(session, settings, "Stopping", cancellationToken))
            {
                log("Stop cancelled: the container is open in VS Code.");
                return;
            }

            await session.StopAsync(settings, log, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await this.ReportFailureAsync("The dev container could not be stopped.", ex.Message, cancellationToken);
        }
    }

    /// <summary>Opens an interactive shell in the running container in a new console window.</summary>
    public async Task OpenTerminalAsync(CancellationToken cancellationToken)
    {
        var session = this.Current;
        if (session is not { State: DevContainerState.Running })
        {
            await extensibility.Shell().ShowPromptAsync("No dev container is running. Use 'Open in Container' first.", PromptOptions.OK, cancellationToken);
            return;
        }

        try
        {
            var spec = session.CreateShellSpec(await this.ReadSettingsAsync(cancellationToken));
            var startInfo = new ProcessStartInfo(spec.FileName)
            {
                // A console program started without a console of its own gets a new console window.
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = session.ConfigFile.WorkspaceFolder,
            };
            foreach (var arg in spec.Arguments)
            {
                startInfo.ArgumentList.Add(arg);
            }

            Process.Start(startInfo)?.Dispose();
        }
        catch (Exception ex)
        {
            await this.ReportFailureAsync("The container terminal could not be opened.", ex.Message, cancellationToken);
        }
    }

    /// <summary>
    /// Adds the "Dev Container" launch profile and launcher script to a project, so the Run button starts it in the
    /// dev container.
    /// </summary>
    public async Task AddLaunchProfileAsync(CancellationToken cancellationToken)
    {
        var configFile = await this.ResolveConfigAsync(cancellationToken);
        if (configFile is null)
        {
            return;
        }

        var chosen = await this.ChooseProjectAsync(configFile, "Which project should the Run button start in the dev container?", preferLaunchProfile: false, cancellationToken);
        if (chosen is null)
        {
            return;
        }

        var log = await this.GetLogAsync(cancellationToken);
        try
        {
            var result = DevContainerLaunchProfile.Install(chosen.Value.Path, configFile);
            log($"Added the '{DevContainerLaunchProfile.ProfileName}' launch profile to {result.LaunchSettingsPath}" +
                (result.InnerLaunchProfile is null ? "." : $", running the '{result.InnerLaunchProfile}' profile inside the container."));
            log($"Wrote the launcher script {result.ScriptPath}.");

            await extensibility.Shell().ShowPromptAsync(
                $"{chosen.Value.Name} now has a '{DevContainerLaunchProfile.ProfileName}' launch profile.\n\n" +
                $"Pick '{DevContainerLaunchProfile.ProfileName}' in the list next to the Run button and start the project: it builds and runs inside the dev container.\n\n" +
                "Commit Properties/launchSettings.json and .devcontainer/vs-run.ps1, so the profile works for everyone right after cloning.",
                PromptOptions.OK,
                cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            await this.ReportFailureAsync("The launch profile could not be added.", ex.Message, cancellationToken);
        }
    }

    /// <summary>
    /// Starts the project under the debugger inside the dev container: opens the container, installs the .NET
    /// debugger there, builds the project in the container and hands Visual Studio a debug session that runs
    /// vsdbg through docker/podman exec. Breakpoints in the solution's files bind as usual.
    /// </summary>
    public async Task DebugAsync(CancellationToken cancellationToken)
    {
        var configFile = await this.ResolveConfigAsync(cancellationToken);
        if (configFile is null)
        {
            return;
        }

        var project = await this.ChooseProjectAsync(configFile, "Which project should be debugged in the dev container?", preferLaunchProfile: true, cancellationToken);
        var solution = (await extensibility.Workspaces().QuerySolutionAsync(s => s.With(x => x.Path), cancellationToken)).FirstOrDefault()?.Path;
        if (project is null || solution is null)
        {
            return;
        }

        var session = this.sessions.GetOrAdd(configFile.ConfigPath, _ => new DevContainerSession(configFile));
        this.Current = session;
        var settings = await this.ReadSettingsAsync(cancellationToken);
        var log = await this.GetLogAsync(cancellationToken);
        log($"Debugging {project.Value.Name} in the dev container from {configFile.ConfigPath}");
        if (!await prerequisites.EnsureAsync(settings.CliPath, log, cancellationToken))
        {
            return;
        }

        using var progress = await extensibility.Shell().StartProgressReportingAsync(
            $"Starting {project.Value.Name} in the dev container",
            new ProgressReporterOptions(isWorkCancellable: true),
            cancellationToken);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, progress.CancellationToken);

        void Report(string line)
        {
            log(line);
            progress.Report(new ProgressStatus(percentComplete: null, Truncate(line, 120)));
        }

        try
        {
            var up = await session.OpenAsync(settings, Report, linked.Token);
            var projectFolder = Path.GetDirectoryName(project.Value.Path)!;
            var runner = new Core.Processes.ProcessRunner();
            await HostStateSync.RunAsync(runner, session.Runtime!, up, configFile.WorkspaceFolder, Report, linked.Token, projectFolder);

            var debugger = new ContainerDebugger(runner, session.Runtime!, up);
            var relative = Path.GetRelativePath(configFile.WorkspaceFolder, projectFolder).Replace('\\', '/');
            await debugger.StopLauncherRunAsync(relative == "." ? "." : relative, linked.Token);
            var vsdbg = await debugger.EnsureDebuggerAsync(Report, linked.Token);
            var profile = LaunchProfileSettings.Read(projectFolder, LaunchProfileSettings.InnerProfileName(projectFolder));
            var target = await debugger.BuildAsync(ContainerDebugger.ToContainerPath(configFile.WorkspaceFolder, up.RemoteWorkspaceFolder, projectFolder), profile, Report, linked.Token);

            var launchJson = Path.Combine(Path.GetTempPath(), "vs-devcontainers", $"launch-{project.Value.Name}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(launchJson)!);
            await File.WriteAllTextAsync(launchJson, debugger.CreateLaunchJson(vsdbg, target, configFile.WorkspaceFolder, $"{project.Value.Name} (dev container)"), linked.Token);

            log($"Starting the debugger: {target.Program}" + (profile is null ? string.Empty : $" with the '{profile.Name}' profile's settings"));
            await VisualStudioAutomation.ExecuteCommandAsync(solution, "DebugAdapterHost.Launch", $"/LaunchJson:\"{launchJson}\" /EngineGuid:{ContainerDebugger.CoreClrEngineGuid}", linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            log("Cancelled.");
        }
        catch (Exception ex)
        {
            await this.ReportFailureAsync("Debugging in the dev container could not start.", ex is InvalidOperationException ? ex.Message : session.LastError ?? ex.Message, cancellationToken);
        }
    }

    /// <summary>Runs the solution's tests (dotnet test) inside the dev container, with the output in the Output window.</summary>
    public async Task RunTestsAsync(CancellationToken cancellationToken)
    {
        var configFile = await this.ResolveConfigAsync(cancellationToken);
        var solution = (await extensibility.Workspaces().QuerySolutionAsync(s => s.With(x => x.Path), cancellationToken)).FirstOrDefault()?.Path;
        if (configFile is null || solution is null)
        {
            return;
        }

        var log = await this.GetLogAsync(cancellationToken);
        if (!IsInside(solution, configFile.WorkspaceFolder))
        {
            await extensibility.Shell().ShowPromptAsync(
                $"The solution {Path.GetFileName(solution)} is not inside {configFile.WorkspaceFolder}, the folder the dev container mounts, so its tests cannot run there.",
                PromptOptions.OK,
                cancellationToken);
            return;
        }

        var session = this.sessions.GetOrAdd(configFile.ConfigPath, _ => new DevContainerSession(configFile));
        this.Current = session;
        var settings = await this.ReadSettingsAsync(cancellationToken);
        if (!await prerequisites.EnsureAsync(settings.CliPath, log, cancellationToken))
        {
            return;
        }

        using var progress = await extensibility.Shell().StartProgressReportingAsync(
            $"Running the tests of {Path.GetFileName(solution)} in the dev container",
            new ProgressReporterOptions(isWorkCancellable: true),
            cancellationToken);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, progress.CancellationToken);

        void Report(string line)
        {
            log(line);
            progress.Report(new ProgressStatus(percentComplete: null, Truncate(line, 120)));
        }

        try
        {
            var up = await session.OpenAsync(settings, Report, linked.Token);
            var folder = Path.GetDirectoryName(solution)!;
            log($"Running dotnet test {Path.GetFileName(solution)} in the dev container");
            var passed = await ContainerTestRun.RunAsync(
                new Core.Processes.ProcessRunner(),
                session.Runtime!,
                up,
                ContainerDebugger.ToContainerPath(configFile.WorkspaceFolder, up.RemoteWorkspaceFolder, folder),
                Path.GetFileName(solution),
                log,
                linked.Token);
            await extensibility.Shell().ShowPromptAsync(
                passed
                    ? "All tests passed in the dev container."
                    : "Tests failed or could not run in the dev container. The Dev Containers pane of the Output window has the details.",
                PromptOptions.OK,
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            log("Cancelled.");
        }
        catch (Exception ex)
        {
            await this.ReportFailureAsync("The tests could not run in the dev container.", session.LastError ?? ex.Message, cancellationToken);
        }
    }

    /// <summary>
    /// The ports of the launch profiles that the solution's "Dev Container" profiles run, which are forwarded with
    /// <c>forwardPorts</c>.
    /// </summary>
    internal async Task<IReadOnlyList<int>> GetLaunchProfilePortsAsync(DevContainerConfigFile configFile, CancellationToken cancellationToken)
    {
        var projects = await extensibility.Workspaces().QueryProjectsAsync(project => project.With(p => p.Path), cancellationToken);
        return projects
            .Select(p => p.Path)
            .Where(path => path is not null && IsProjectFile(path) && IsInside(path, configFile.WorkspaceFolder))
            .SelectMany(path => LaunchProfileSettings.ContainerPorts(Path.GetDirectoryName(path)!))
            .Distinct()
            .ToList();
    }

    public void Dispose()
    {
        this.output?.Dispose();
        this.outputGate.Dispose();
    }

    /// <summary>
    /// Visual Studio and VS Code share one container per folder. Stopping or rebuilding it disconnects a VS Code
    /// window that has it open, so ask first.
    /// </summary>
    private async Task<bool> ConfirmVsCodeDisconnectAsync(DevContainerSession session, DevContainerSettings settings, string action, CancellationToken cancellationToken)
    {
        bool openInVsCode;
        try
        {
            openInVsCode = await session.IsOpenInVsCodeAsync(settings, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The check is a courtesy; the operation itself reports a missing engine properly.
            return true;
        }

        return !openInVsCode || await extensibility.Shell().ShowPromptAsync(
            $"VS Code has this dev container open.\n\n{action} the container disconnects VS Code too. Continue?",
            PromptOptions.OKCancel,
            cancellationToken);
    }

    /// <summary>
    /// The dev container configuration the open solution uses, without asking: the one last opened, else the first
    /// found for the solution folder. Null when no solution is open or it has none.
    /// </summary>
    internal async Task<DevContainerConfigFile?> FindCurrentConfigAsync(CancellationToken cancellationToken)
    {
        var folder = await this.GetSolutionFolderAsync(cancellationToken);
        if (folder is null)
        {
            return null;
        }

        var found = DevContainerConfigLocator.FindFromFolder(folder);
        var current = this.Current?.ConfigFile;
        return current is not null && found.Any(f => string.Equals(f.ConfigPath, current.ConfigPath, StringComparison.OrdinalIgnoreCase))
            ? current
            : found.FirstOrDefault();
    }

    /// <summary>
    /// Picks a C# project inside the dev container's folder: the only one, else (with
    /// <paramref name="preferLaunchProfile"/>) the only one with a "Dev Container" launch profile, else asks.
    /// </summary>
    private async Task<(string Name, string Path)?> ChooseProjectAsync(DevContainerConfigFile configFile, string question, bool preferLaunchProfile, CancellationToken cancellationToken)
    {
        var projects = await extensibility.Workspaces().QueryProjectsAsync(
            project => project.With(p => p.Name).With(p => p.Path),
            cancellationToken);
        var candidates = projects
            .Where(p => p.Path is { } path && IsProjectFile(path) && IsInside(path, configFile.WorkspaceFolder))
            .Select(p => (Name: p.Name ?? Path.GetFileNameWithoutExtension(p.Path!), Path: p.Path!))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0)
        {
            await extensibility.Shell().ShowPromptAsync(
                $"No .NET project (C#, F# or Visual Basic) of this solution is inside {configFile.WorkspaceFolder}, the folder the dev container mounts.",
                PromptOptions.OK,
                cancellationToken);
            return null;
        }

        if (preferLaunchProfile && candidates.Count > 1)
        {
            var withProfile = candidates.Where(c => LaunchProfileSettings.Read(System.IO.Path.GetDirectoryName(c.Path)!, DevContainerLaunchProfile.ProfileName) is not null).ToList();
            if (withProfile.Count == 1)
            {
                return withProfile[0];
            }
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        var choices = new ChoiceResultCollection<int>();
        for (var i = 0; i < candidates.Count; i++)
        {
            choices.Add(candidates[i].Name, i);
        }

        var index = await extensibility.Shell().ShowPromptAsync(
            question,
            new PromptOptions<int>(choices, defaultChoiceIndex: 0, dismissedReturns: -1) { Title = "Dev Containers" },
            cancellationToken);
        return index < 0 ? null : candidates[index];
    }

    private async Task<DevContainerConfigFile?> ResolveConfigAsync(CancellationToken cancellationToken)
    {
        var startFolder = await this.GetSolutionFolderAsync(cancellationToken)
            ?? await extensibility.Shell().ShowOpenFolderDialogAsync(
                new FolderDialogOptions { Title = "Select the folder that contains .devcontainer" },
                cancellationToken);

        if (startFolder is null)
        {
            return null;
        }

        var found = DevContainerConfigLocator.FindFromFolder(startFolder);
        switch (found.Count)
        {
            case 0:
                await extensibility.Shell().ShowPromptAsync(
                    $"No devcontainer.json was found in {startFolder} or its parent folders up to the repository root.\n\nAdd .devcontainer/devcontainer.json to define a dev container.",
                    PromptOptions.OK,
                    cancellationToken);
                return null;
            case 1:
                return found[0];
            default:
                var choices = new ChoiceResultCollection<int>();
                for (var i = 0; i < found.Count; i++)
                {
                    choices.Add(DescribeConfig(found[i]), i);
                }

                var index = await extensibility.Shell().ShowPromptAsync(
                    "This workspace has several dev container configurations. Which one should be used?",
                    new PromptOptions<int>(choices, defaultChoiceIndex: 0, dismissedReturns: -1) { Title = "Dev Containers" },
                    cancellationToken);
                return index >= 0 ? found[index] : null;
        }
    }

    private async Task<string?> GetSolutionFolderAsync(CancellationToken cancellationToken)
    {
        var solutions = await extensibility.Workspaces().QuerySolutionAsync(
            solution => solution.With(s => s.Directory),
            cancellationToken);

        var directory = solutions.FirstOrDefault()?.Directory;
        return string.IsNullOrEmpty(directory) ? null : directory;
    }

    internal async Task<DevContainerSettings> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        var settingsApi = extensibility.Settings();
        var runtime = await settingsApi.ReadEffectiveValueAsync(DevContainerSettingDefinitions.Runtime, cancellationToken);
        var runtimePath = await settingsApi.ReadEffectiveValueAsync(DevContainerSettingDefinitions.RuntimePath, cancellationToken);
        var cliPath = await settingsApi.ReadEffectiveValueAsync(DevContainerSettingDefinitions.CliPath, cancellationToken);

        return new DevContainerSettings
        {
            Runtime = runtime.ValueOrDefault("auto") switch
            {
                "docker" => ContainerRuntimePreference.Docker,
                "podman" => ContainerRuntimePreference.Podman,
                _ => ContainerRuntimePreference.Auto,
            },
            RuntimePath = NullIfEmpty(runtimePath.ValueOrDefault(string.Empty)),
            CliPath = NullIfEmpty(cliPath.ValueOrDefault(string.Empty)),
        };
    }

    internal async Task<Action<string>> GetLogAsync(CancellationToken cancellationToken)
    {
        await this.outputGate.WaitAsync(cancellationToken);
        try
        {
            this.output ??= await extensibility.Views().Output.CreateOutputChannelAsync(OutputName, cancellationToken);
        }
        finally
        {
            this.outputGate.Release();
        }

        var writer = this.output.Writer;
        return line =>
        {
            lock (writer)
            {
                writer.WriteLine(line);
                writer.Flush();
            }
        };
    }

    private async Task ReportFailureAsync(string title, string detail, CancellationToken cancellationToken)
    {
        var log = await this.GetLogAsync(cancellationToken);
        log($"Error: {detail}");
        await extensibility.Shell().ShowPromptAsync(
            $"{title}\n\n{detail}\n\nSee the '{OutputName}' pane in the Output window for the full log.",
            PromptOptions.ErrorConfirm,
            cancellationToken);
    }

    private static string DescribeConfig(DevContainerConfigFile file)
    {
        try
        {
            var name = DevContainerConfig.Load(file.ConfigPath).Name;
            return name is null ? file.DisplayName : $"{name} ({file.DisplayName})";
        }
        catch (Exception)
        {
            // An invalid file still gets listed; the CLI reports the parse error when it is opened.
            return file.DisplayName;
        }
    }

    private static bool IsProjectFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj";

    private static bool IsInside(string path, string folder) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string ShortId(string id) => id.Length > 12 ? id[..12] : id;

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
