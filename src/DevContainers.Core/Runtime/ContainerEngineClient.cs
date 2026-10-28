using DevContainers.Core.Processes;

namespace DevContainers.Core.Runtime;

/// <summary>A container as reported by <c>ps</c>.</summary>
public sealed record ContainerInfo(string Id, string State, string? ComposeProject)
{
    public bool IsRunning => string.Equals(State, "running", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The few engine operations the devcontainer CLI does not offer (finding, stopping and removing containers).
/// Uses only commands that Docker and Podman share.
/// </summary>
public sealed class ContainerEngineClient(ContainerRuntime runtime, IProcessRunner runner)
{
    /// <summary>Label the devcontainer CLI puts on every dev container, holding the host workspace folder.</summary>
    public const string LocalFolderLabel = "devcontainer.local_folder";

    /// <summary>Label the devcontainer CLI puts on every dev container, holding the devcontainer.json path.</summary>
    public const string ConfigFileLabel = "devcontainer.config_file";

    private const string ComposeProjectLabel = "com.docker.compose.project";

    public ContainerRuntime Runtime => runtime;

    /// <summary>Finds the dev container(s) created for a workspace folder and config file.</summary>
    public async Task<IReadOnlyList<ContainerInfo>> FindDevContainersAsync(string workspaceFolder, string configPath, CancellationToken cancellationToken)
    {
        var result = await this.RunCheckedAsync(
            [
                "ps", "--all",
                "--filter", $"label={LocalFolderLabel}={workspaceFolder}",
                "--filter", $"label={ConfigFileLabel}={configPath}",
                "--format", "{{.ID}}\t{{.State}}",
            ],
            cancellationToken).ConfigureAwait(false);

        var containers = new List<ContainerInfo>();
        foreach (var container in ParsePs(result.StandardOutput))
        {
            // `ps` label templates differ between Docker and Podman; `inspect` exposes the same map in both.
            var project = await this.RunCheckedAsync(
                ["inspect", "--format", "{{index .Config.Labels \"" + ComposeProjectLabel + "\"}}", container.Id],
                cancellationToken).ConfigureAwait(false);
            var value = project.StandardOutput.Trim();
            containers.Add(container with { ComposeProject = value.Length > 0 && value != "<no value>" ? value : null });
        }

        return containers;
    }

    /// <summary>Stops the dev container, and for Compose based containers every service in the same project.</summary>
    public async Task<IReadOnlyList<string>> StopAsync(IReadOnlyList<ContainerInfo> containers, CancellationToken cancellationToken)
    {
        var ids = await this.ExpandComposeProjectsAsync(containers, cancellationToken).ConfigureAwait(false);
        var running = ids.Where(c => c.IsRunning).Select(c => c.Id).ToList();
        if (running.Count > 0)
        {
            await this.RunCheckedAsync(["stop", .. running], cancellationToken).ConfigureAwait(false);
        }

        return running;
    }

    /// <summary>Removes the dev container(s), stopping them first.</summary>
    public async Task<IReadOnlyList<string>> RemoveAsync(IReadOnlyList<ContainerInfo> containers, CancellationToken cancellationToken)
    {
        var ids = (await this.ExpandComposeProjectsAsync(containers, cancellationToken).ConfigureAwait(false)).Select(c => c.Id).ToList();
        if (ids.Count > 0)
        {
            await this.RunCheckedAsync(["rm", "--force", .. ids], cancellationToken).ConfigureAwait(false);
        }

        return ids;
    }

    internal static IReadOnlyList<ContainerInfo> ParsePs(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t'))
            .Where(parts => parts.Length >= 2)
            .Select(parts => new ContainerInfo(parts[0], parts[1], ComposeProject: null))
            .ToList();

    private async Task<IReadOnlyList<ContainerInfo>> ExpandComposeProjectsAsync(IReadOnlyList<ContainerInfo> containers, CancellationToken cancellationToken)
    {
        var all = new Dictionary<string, ContainerInfo>(containers.Select(c => KeyValuePair.Create(c.Id, c)));
        foreach (var project in containers.Select(c => c.ComposeProject).OfType<string>().Distinct())
        {
            var result = await this.RunCheckedAsync(
                ["ps", "--all", "--filter", $"label={ComposeProjectLabel}={project}", "--format", "{{.ID}}\t{{.State}}"],
                cancellationToken).ConfigureAwait(false);

            foreach (var sibling in ParsePs(result.StandardOutput))
            {
                all.TryAdd(sibling.Id, sibling with { ComposeProject = project });
            }
        }

        return all.Values.ToList();
    }

    private async Task<ProcessResult> RunCheckedAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var spec = new ProcessSpec(runtime.ExecutablePath, args);
        var result = await runner.RunAsync(spec, sink: null, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"'{spec}' failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
        }

        return result;
    }
}
