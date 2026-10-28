using DevContainers.VisualStudio.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.Extensibility;

namespace DevContainers.VisualStudio;

/// <summary>Entry point of the Dev Containers extension. Runs out of process on the VisualStudio.Extensibility model.</summary>
[VisualStudioContribution]
internal sealed class DevContainersExtension : Extension
{
    public override ExtensionConfiguration ExtensionConfiguration => new()
    {
        Metadata = new(
            id: "DevContainers.VisualStudio.6f1c2b0e-4d1a-4b8e-9f0a-2a7d3c5e8b91",
            version: this.ExtensionAssemblyVersion,
            publisherName: "Joris Coenen",
            displayName: "Dev Containers",
            description: "Open, build and run your solution inside a dev container defined by devcontainer.json, with Docker or Podman.")
        {
            Tags = ["devcontainer", "docker", "podman", "containers", "remote"],

            // Visual Studio 2026 (version 18) is the minimum supported version.
            InstallationTargetVersion = "[18.0,)",

            // Visual Studio 2026 runs out-of-process extensions on .NET 10.
            DotnetTargetVersions = ["net10.0"],
        },
    };

    protected override void InitializeServices(IServiceCollection serviceCollection)
    {
        base.InitializeServices(serviceCollection);
        serviceCollection.AddSingleton<PrerequisiteInstaller>();
        serviceCollection.AddSingleton<DevContainerService>();
    }
}
