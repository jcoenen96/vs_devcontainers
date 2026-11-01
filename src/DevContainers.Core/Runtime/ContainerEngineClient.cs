using DevContainers.Core.Processes;

namespace DevContainers.Core.Runtime;

/// <summary>A container as reported by <c>ps</c>, with the dev container labels when they were read.</summary>
public sealed record ContainerInfo(string Id, string State, string? ComposeProject)
{
    /// <summary>The exact <c>devcontainer.local_folder</c> and <c>devcontainer.config_file</c> values on the container.</summary>
    public DevContainerIdentity? Identity { get; init; }

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

    /// <summary>
    /// Finds the dev container(s) of a workspace folder and config file, whichever tool created them. VS Code, the
    /// devcontainer CLI and Visual Studio may spell the same path differently in the labels (<c>C:\</c> or
    /// <c>c:\</c>, a trailing separator), so the labels are compared as paths instead of with an exact filter.
    /// Running containers come first.
    /// </summary>
    public async Task<IReadOnlyList<ContainerInfo>> FindDevContainersAsync(string workspaceFolder, string configPath, CancellationToken cancellationToken)
    {
        var result = await this.RunCheckedAsync(
            ["ps", "--all", "--filter", $"label={LocalFolderLabel}", "--format", "{{.ID}}\t{{.State}}"],
            cancellationToken).ConfigureAwait(false);
        var candidates = ParsePs(result.StandardOutput);
        if (candidates.Count == 0)
        {
            return [];
        }

        // `ps` label templates differ between Docker and Podman; `inspect` exposes the same map in both. It prints
        // one line per container, in the order given.
        var labels = await this.RunCheckedAsync(
            ["inspect", "--format", LabelTemplate(LocalFolderLabel) + "\t" + LabelTemplate(ConfigFileLabel) + "\t" + LabelTemplate(ComposeProjectLabel), .. candidates.Select(c => c.Id)],
            cancellationToken).ConfigureAwait(false);
        var lines = labels.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();

        var containers = new List<ContainerInfo>();
        for (var i = 0; i < candidates.Count && i < lines.Count; i++)
        {
            var parts = lines[i].Split('\t');
            var folder = LabelValue(parts, 0);
            var config = LabelValue(parts, 1);

            // Containers from old CLI versions have no config label; the folder alone identifies them.
            if (folder is null || !DevContainerIdentity.SamePath(folder, workspaceFolder) || (config is not null && !DevContainerIdentity.SamePath(config, configPath)))
            {
                continue;
            }

            containers.Add(candidates[i] with
            {
                ComposeProject = LabelValue(parts, 2),
                Identity = config is null ? null : new DevContainerIdentity(folder, config),
            });
        }

        return containers.OrderByDescending(c => c.IsRunning).ToList();
    }

    /// <summary>
    /// Whether a VS Code window is attached to the container: VS Code runs its server (<c>vscode-server</c> or
    /// <c>vscode-server-insiders</c>) inside the container while a window has it open.
    /// </summary>
    public async Task<bool> IsVsCodeAttachedAsync(string containerId, CancellationToken cancellationToken)
    {
        // The [v] keeps grep from matching its own command line.
        var result = await runner.RunAsync(
            new ProcessSpec(runtime.ExecutablePath, ["exec", containerId, "sh", "-c", "grep -l -s '[v]scode-server' /proc/[0-9]*/cmdline | head -n 1"]),
            sink: null,
            cancellationToken).ConfigureAwait(false);
        return result.Succeeded && result.StandardOutput.Trim().Length > 0;
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

    private static string LabelTemplate(string label) => "{{index .Config.Labels \"" + label + "\"}}";

    private static string? LabelValue(string[] parts, int index)
    {
        // Docker prints "<no value>" for a missing label, Podman an empty string.
        var value = index < parts.Length ? parts[index].Trim() : string.Empty;
        return value.Length > 0 && value != "<no value>" ? value : null;
    }

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
