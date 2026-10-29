using System.Text.Json;
using DevContainers.Core.Processes;
using DevContainers.Core.Runtime;

namespace DevContainers.Core.Cli;

/// <summary>Options for <c>devcontainer up</c>.</summary>
public sealed record UpOptions(string WorkspaceFolder, string ConfigPath)
{
    /// <summary>Recreate the container even if one exists (Rebuild).</summary>
    public bool RemoveExistingContainer { get; init; }

    /// <summary>Build the image without the layer cache (Rebuild Without Cache).</summary>
    public bool BuildNoCache { get; init; }

    /// <summary>
    /// The labels that find (or, for a new container, are put on) the container. Passing the labels of an existing
    /// container makes <c>up</c> reuse it, even when another tool spelled the path differently.
    /// </summary>
    public DevContainerIdentity? Identity { get; init; }
}

/// <summary>What <c>devcontainer up</c> reports on success.</summary>
public sealed record UpResult(string ContainerId, string? RemoteUser, string RemoteWorkspaceFolder);

/// <summary>The CLI ran but reported an error outcome.</summary>
public sealed class DevContainerCliException(string message, string? description = null) : Exception(message)
{
    public string? Description { get; } = description;
}

/// <summary>
/// Wraps the reference implementation of the Dev Containers specification (<c>@devcontainers/cli</c>).
/// The CLI handles images, Dockerfiles, Compose, features, lifecycle hooks and variable substitution, so the
/// extension gets the full spec without reimplementing it.
/// </summary>
public sealed class DevContainerCli(DevContainerCliCommand command, ContainerRuntime runtime, IProcessRunner runner)
{
    public async Task<UpResult> UpAsync(UpOptions options, Action<string>? log, CancellationToken cancellationToken)
    {
        var args = new List<string>
        {
            "up",
            "--workspace-folder", options.WorkspaceFolder,
            "--config", options.ConfigPath,
            "--log-format", "json",
        };
        args.AddRange(this.RuntimeArguments());
        args.AddRange(options.Identity?.ToCliArguments() ?? []);
        if (options.RemoveExistingContainer)
        {
            args.Add("--remove-existing-container");
        }

        if (options.BuildNoCache)
        {
            args.Add("--build-no-cache");
        }

        var result = await this.RunAsync(args, log, cancellationToken).ConfigureAwait(false);
        return ParseUpResult(result);
    }

    /// <summary>Runs a command in the dev container as the remote user, with the container's environment.</summary>
    public Task<ProcessResult> ExecAsync(
        string workspaceFolder,
        string configPath,
        IReadOnlyList<string> command,
        IProcessOutputSink? sink,
        CancellationToken cancellationToken,
        DevContainerIdentity? identity = null) =>
        runner.RunAsync(this.CreateExecSpec(workspaceFolder, configPath, command, identity), sink, cancellationToken);

    /// <summary>
    /// Builds the <c>devcontainer exec</c> command line without running it, for callers that need their own
    /// process (an interactive terminal window).
    /// </summary>
    public ProcessSpec CreateExecSpec(string workspaceFolder, string configPath, IReadOnlyList<string> command, DevContainerIdentity? identity = null)
    {
        if (command.Count == 0)
        {
            throw new ArgumentException("A command is required.", nameof(command));
        }

        var args = new List<string>
        {
            "exec",
            "--workspace-folder", workspaceFolder,
            "--config", configPath,
        };
        args.AddRange(this.RuntimeArguments());
        args.AddRange(identity?.ToCliArguments() ?? []);
        args.AddRange(command);
        return this.CreateSpec(args);
    }

    public ProcessSpec CreateSpec(IEnumerable<string> arguments) => command.CreateSpec(arguments);

    internal static UpResult ParseUpResult(ProcessResult result)
    {
        // The outcome is the last JSON object on stdout. Anything before it is noise from subprocesses.
        var outcomeLine = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.StartsWith('{'));

        if (outcomeLine is null)
        {
            var detail = result.StandardError.Trim();
            throw new DevContainerCliException(
                $"devcontainer up exited with code {result.ExitCode} without a result.",
                detail.Length > 0 ? detail : null);
        }

        using var document = JsonDocument.Parse(outcomeLine);
        var root = document.RootElement;
        var outcome = GetString(root, "outcome");
        if (outcome != "success")
        {
            throw new DevContainerCliException(
                GetString(root, "message") ?? $"devcontainer up failed with outcome '{outcome}'.",
                GetString(root, "description"));
        }

        return new UpResult(
            GetString(root, "containerId") ?? throw new DevContainerCliException("devcontainer up did not return a container id."),
            GetString(root, "remoteUser"),
            GetString(root, "remoteWorkspaceFolder") ?? throw new DevContainerCliException("devcontainer up did not return the workspace folder."));
    }

    private IEnumerable<string> RuntimeArguments()
    {
        yield return "--docker-path";
        yield return runtime.ExecutablePath;
        if (runtime.ComposePath is not null)
        {
            yield return "--docker-compose-path";
            yield return runtime.ComposePath;
        }
    }

    private async Task<ProcessResult> RunAsync(IEnumerable<string> args, Action<string>? log, CancellationToken cancellationToken)
    {
        var sink = log is null ? null : new LogSink(log);
        return await runner.RunAsync(this.CreateSpec(args), sink, cancellationToken).ConfigureAwait(false);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed class LogSink(Action<string> log) : IProcessOutputSink
    {
        public void OnStandardOutput(string line)
        {
            // stdout carries only the final JSON result, which the caller reports itself.
        }

        public void OnStandardError(string line)
        {
            if (DevContainerLogFormatter.Format(line) is { } text)
            {
                log(text);
            }
        }
    }
}
