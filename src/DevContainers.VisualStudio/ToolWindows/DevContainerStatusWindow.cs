using DevContainers.VisualStudio.Services;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;

namespace DevContainers.VisualStudio.ToolWindows;

/// <summary>
/// The "Dev Container" tool window: whether the solution's container runs, on which engine, and which ports are
/// forwarded, with buttons for the common commands. Its title shows the state, so it doubles as a status indicator
/// when docked as a tab.
/// </summary>
[VisualStudioContribution]
internal sealed class DevContainerStatusWindow : ToolWindow
{
    private readonly PortForwardingService forwarding;
    private readonly DevContainerStatusViewModel model;

    public DevContainerStatusWindow(PortForwardingService forwarding, DevContainerService service)
    {
        this.forwarding = forwarding;
        this.model = new DevContainerStatusViewModel(service, forwarding);
        this.Title = this.model.Title;
        this.model.TitleChanged += (_, title) => this.Title = title;
    }

    public override ToolWindowConfiguration ToolWindowConfiguration => new()
    {
        Placement = ToolWindowPlacement.DocumentWell,
        AllowAutoCreation = true,
    };

    public override Task<IRemoteUserControl> GetContentAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IRemoteUserControl>(new DevContainerStatusControl(this.model));

    public override async Task OnShowAsync(CancellationToken cancellationToken)
    {
        // Show the current state right away rather than after the next round of checks.
        try
        {
            await this.forwarding.RefreshAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The window shows the problem from the status.
        }

        this.model.Update(this.forwarding.Status);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.model.Dispose();
        }

        base.Dispose(disposing);
    }
}
