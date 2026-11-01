using DevContainers.VisualStudio.Services;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;

namespace DevContainers.VisualStudio.Commands;

/// <summary>The "Dev Containers" menu under Extensions.</summary>
internal static class DevContainerMenu
{
    [VisualStudioContribution]
    public static MenuConfiguration Menu => new("%DevContainers.Menu.DisplayName%")
    {
        Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
        Children =
        [
            MenuChild.Command<OpenInContainerCommand>(),
            MenuChild.Command<StartDebuggingInContainerCommand>(),
            MenuChild.Command<RunTestsInContainerCommand>(),
            MenuChild.Command<RunNpmScriptInContainerCommand>(),
            MenuChild.Command<DebugNpmScriptInContainerCommand>(),
            MenuChild.Command<OpenTerminalCommand>(),
            MenuChild.Command<AddLaunchProfileCommand>(),
            MenuChild.Separator,
            MenuChild.Command<RebuildContainerCommand>(),
            MenuChild.Command<RebuildContainerNoCacheCommand>(),
            MenuChild.Separator,
            MenuChild.Command<StopContainerCommand>(),
        ],
    };
}

[VisualStudioContribution]
internal sealed class OpenInContainerCommand(DevContainerService service) : Command
{
    public override CommandConfiguration CommandConfiguration => new("%DevContainers.OpenInContainer.DisplayName%")
    {
        Icon = new(ImageMoniker.KnownValues.Run, IconSettings.IconAndText),
    };

    public override Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken) =>
        service.OpenAsync(rebuild: false, noCache: false, cancellationToken);
}

[VisualStudioContribution]
internal sealed class StartDebuggingInContainerCommand(DevContainerService service) : Command
{
    public override CommandConfiguration CommandConfiguration => new("%DevContainers.Debug.DisplayName%");

    public override Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken) =>
        service.DebugAsync(cancellationToken);
}

[VisualStudioContribution]
internal sealed class RunTestsInContainerCommand(DevContainerService service) : Command
{
    public override CommandConfiguration CommandConfiguration => new("%DevContainers.RunTests.DisplayName%");

    public override Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken) =>
        service.RunTestsAsync(cancellationToken);
}

[VisualStudioContribution]
internal sealed class RunNpmScriptInContainerCommand(DevContainerService service) : Command
{
    public override CommandConfiguration CommandConfiguration => new("%DevContainers.RunNpm.DisplayName%");

    public override Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken) =>
        service.RunNpmScriptAsync(debug: false, cancellationToken);
}

[VisualStudioContribution]
internal sealed class DebugNpmScriptInContainerCommand(DevContainerService service) : Command
{
    public override CommandConfiguration CommandConfiguration => new("%DevContainers.DebugNpm.DisplayName%");

    public override Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken) =>
        service.RunNpmScriptAsync(debug: true, cancellationToken);
}

[VisualStudioContribution]
internal sealed class RebuildContainerCommand(DevContainerService service) : Command
{
    public override CommandConfiguration CommandConfiguration => new("%DevContainers.Rebuild.DisplayName%")
    {
        Icon = new(ImageMoniker.KnownValues.Refresh, IconSettings.IconAndText),
    };

    public override Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken) =>
        service.OpenAsync(rebuild: true, noCache: false, cancellationToken);
}

[VisualStudioContribution]
internal sealed class RebuildContainerNoCacheCommand(DevContainerService service) : Command
{
    public override CommandConfiguration CommandConfiguration => new("%DevContainers.RebuildNoCache.DisplayName%");

    public override Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken) =>
        service.OpenAsync(rebuild: true, noCache: true, cancellationToken);
}

[VisualStudioContribution]
internal sealed class StopContainerCommand(DevContainerService service) : Command
{
    public override CommandConfiguration CommandConfiguration => new("%DevContainers.Stop.DisplayName%")
    {
        Icon = new(ImageMoniker.KnownValues.Stop, IconSettings.IconAndText),
    };

    public override Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken) =>
        service.StopAsync(cancellationToken);
}

[VisualStudioContribution]
internal sealed class OpenTerminalCommand(DevContainerService service) : Command
{
    public override CommandConfiguration CommandConfiguration => new("%DevContainers.OpenTerminal.DisplayName%")
    {
        Icon = new(ImageMoniker.KnownValues.Console, IconSettings.IconAndText),
    };

    public override Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken) =>
        service.OpenTerminalAsync(cancellationToken);
}

[VisualStudioContribution]
internal sealed class AddLaunchProfileCommand(DevContainerService service) : Command
{
    public override CommandConfiguration CommandConfiguration => new("%DevContainers.AddLaunchProfile.DisplayName%")
    {
        Icon = new(ImageMoniker.KnownValues.Settings, IconSettings.IconAndText),
    };

    public override Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken) =>
        service.AddLaunchProfileAsync(cancellationToken);
}
