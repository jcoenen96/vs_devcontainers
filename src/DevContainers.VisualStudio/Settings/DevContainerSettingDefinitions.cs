using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Settings;

namespace DevContainers.VisualStudio.Settings;

/// <summary>Settings shown under Tools > Options > Dev Containers.</summary>
internal static class DevContainerSettingDefinitions
{
    [VisualStudioContribution]
    internal static SettingCategory Category { get; } = new("devContainers", "%DevContainers.Settings.Category%")
    {
        Description = "%DevContainers.Settings.Category.Description%",
    };

    [VisualStudioContribution]
    internal static Setting.Enum Runtime { get; } = new(
        "containerRuntime",
        "%DevContainers.Settings.Runtime%",
        Category,
        [
            new EnumSettingEntry("auto", "%DevContainers.Settings.Runtime.Auto%"),
            new EnumSettingEntry("docker", "%DevContainers.Settings.Runtime.Docker%"),
            new EnumSettingEntry("podman", "%DevContainers.Settings.Runtime.Podman%"),
        ],
        defaultValue: "auto")
    {
        Description = "%DevContainers.Settings.Runtime.Description%",
    };

    [VisualStudioContribution]
    internal static Setting.String RuntimePath { get; } = new("containerRuntimePath", "%DevContainers.Settings.RuntimePath%", Category, defaultValue: string.Empty)
    {
        Description = "%DevContainers.Settings.RuntimePath.Description%",
    };

    [VisualStudioContribution]
    internal static Setting.String CliPath { get; } = new("devcontainerCliPath", "%DevContainers.Settings.CliPath%", Category, defaultValue: string.Empty)
    {
        Description = "%DevContainers.Settings.CliPath.Description%",
    };
}
