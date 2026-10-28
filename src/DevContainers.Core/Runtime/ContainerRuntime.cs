using DevContainers.Core.Processes;

namespace DevContainers.Core.Runtime;

public enum ContainerRuntimeKind
{
    Docker,
    Podman,
}

/// <summary>Which container engine the user wants. <see cref="Auto"/> prefers Docker and falls back to Podman.</summary>
public enum ContainerRuntimePreference
{
    Auto,
    Docker,
    Podman,
}

/// <summary>A container engine CLI that was found and answered.</summary>
public sealed record ContainerRuntime(ContainerRuntimeKind Kind, string ExecutablePath, string ServerVersion)
{
    /// <summary>Path to a standalone compose tool, when the engine needs one (podman-compose).</summary>
    public string? ComposePath { get; init; }
}

/// <summary>Thrown when no usable container engine is available.</summary>
public sealed class ContainerRuntimeNotFoundException(string message) : Exception(message);

/// <summary>Finds a working Docker or Podman installation.</summary>
public sealed class ContainerRuntimeDetector(IProcessRunner runner, Func<string, string?>? findOnPath = null)
{
    private readonly Func<string, string?> findOnPath = findOnPath ?? (name => ExecutableLocator.FindOnPath(name));

    public async Task<ContainerRuntime> DetectAsync(ContainerRuntimePreference preference, string? explicitPath, CancellationToken cancellationToken)
    {
        var candidates = preference switch
        {
            ContainerRuntimePreference.Docker => new[] { ContainerRuntimeKind.Docker },
            ContainerRuntimePreference.Podman => new[] { ContainerRuntimeKind.Podman },
            _ => new[] { ContainerRuntimeKind.Docker, ContainerRuntimeKind.Podman },
        };

        var problems = new List<string>();
        foreach (var kind in candidates)
        {
            var name = kind == ContainerRuntimeKind.Docker ? "docker" : "podman";
            var path = !string.IsNullOrWhiteSpace(explicitPath) && candidates.Length == 1 ? explicitPath : this.findOnPath(name);
            if (path is null)
            {
                problems.Add($"{name}: not found on PATH");
                continue;
            }

            var version = await this.TryGetServerVersionAsync(path, cancellationToken).ConfigureAwait(false);
            if (version.Error is not null)
            {
                problems.Add($"{name}: {version.Error}");
                continue;
            }

            return new ContainerRuntime(kind, path, version.Value!)
            {
                ComposePath = kind == ContainerRuntimeKind.Podman ? this.findOnPath("podman-compose") : null,
            };
        }

        throw new ContainerRuntimeNotFoundException(
            "No running container engine was found. Install and start Docker Desktop or Podman. Details: " + string.Join("; ", problems));
    }

    private async Task<(string? Value, string? Error)> TryGetServerVersionAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            // Docker and Podman both accept this template. It fails when the daemon or Podman machine is not running.
            var result = await runner.RunAsync(
                new ProcessSpec(path, ["version", "--format", "{{.Server.Version}}"]),
                sink: null,
                cancellationToken).ConfigureAwait(false);

            var version = result.StandardOutput.Trim();
            if (!result.Succeeded || version.Length == 0)
            {
                var detail = result.StandardError.Trim();
                return (null, detail.Length > 0 ? FirstLine(detail) : "the engine is not running");
            }

            return (FirstLine(version), null);
        }
        catch (ProcessStartException ex)
        {
            return (null, ex.Message);
        }
    }

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();
}
