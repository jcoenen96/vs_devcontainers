using System.Collections.Concurrent;
using System.Diagnostics;
using DevContainers.Core;
using DevContainers.Core.Configuration;
using DevContainers.Core.Runtime;
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
internal sealed class DevContainerService(VisualStudioExtensibility extensibility) : IDisposable
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
            await session.StopAsync(await this.ReadSettingsAsync(cancellationToken), log, cancellationToken);
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

    public void Dispose()
    {
        this.output?.Dispose();
        this.outputGate.Dispose();
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

    private async Task<DevContainerSettings> ReadSettingsAsync(CancellationToken cancellationToken)
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

    private async Task<Action<string>> GetLogAsync(CancellationToken cancellationToken)
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

    private static string ShortId(string id) => id.Length > 12 ? id[..12] : id;

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
