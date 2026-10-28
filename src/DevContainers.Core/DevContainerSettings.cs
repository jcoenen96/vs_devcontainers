using DevContainers.Core.Runtime;

namespace DevContainers.Core;

/// <summary>User settings that affect how containers are started.</summary>
public sealed record DevContainerSettings
{
    public static DevContainerSettings Default { get; } = new();

    public ContainerRuntimePreference Runtime { get; init; } = ContainerRuntimePreference.Auto;

    /// <summary>Path to the docker or podman executable when it is not on PATH.</summary>
    public string? RuntimePath { get; init; }

    /// <summary>Path to <c>devcontainer.js</c> or a <c>devcontainer</c> executable when it is not on PATH.</summary>
    public string? CliPath { get; init; }
}
