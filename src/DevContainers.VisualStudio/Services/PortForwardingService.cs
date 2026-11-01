using DevContainers.Core.Configuration;
using DevContainers.Core.Forwarding;
using DevContainers.Core.Processes;
using DevContainers.Core.Runtime;

namespace DevContainers.VisualStudio.Services;

/// <summary>
/// Forwards the <c>forwardPorts</c> of the open solution's dev container to <c>localhost</c> while that container
/// runs, whoever started it: Open in Container, the Run button's launcher, or VS Code. Checks every few seconds,
/// and stops forwarding when the container stops or another solution is opened.
/// </summary>
internal sealed class PortForwardingService(DevContainerService devContainers) : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim gate = new(1, 1);
    private PortForwarder? forwarder;
    private string? forwardedKey;
    private ContainerRuntime? runtime;

    /// <summary>The ports forwarded right now, for the Output window and the status command.</summary>
    public IReadOnlyList<ForwardedPort> Ports { get; private set; } = [];

    /// <summary>Runs until Visual Studio shuts the extension down.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await this.RefreshAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // No engine yet, the solution is closing, ...: try again on the next round.
                System.Diagnostics.Debug.WriteLine($"Dev Containers port forwarding: {ex.Message}");
            }

            try
            {
                await Task.Delay(Interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Checks the open solution's dev container now, for example right after Open in Container.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await this.gate.WaitAsync(cancellationToken);
        try
        {
            var configFile = await devContainers.FindCurrentConfigAsync(cancellationToken);
            var ports = configFile is null ? [] : ReadPorts(configFile);
            if (configFile is null || ports.Count == 0)
            {
                this.Stop();
                return;
            }

            var settings = await devContainers.ReadSettingsAsync(cancellationToken);
            this.runtime ??= await new ContainerRuntimeDetector(new ProcessRunner()).DetectAsync(settings.Runtime, settings.RuntimePath, cancellationToken);
            var engine = new ContainerEngineClient(this.runtime, new ProcessRunner());
            var container = (await engine.FindDevContainersAsync(configFile.WorkspaceFolder, configFile.ConfigPath, cancellationToken)).FirstOrDefault(c => c.IsRunning);
            if (container is null)
            {
                if (this.forwarder is not null)
                {
                    this.Stop();
                    (await devContainers.GetLogAsync(cancellationToken))("Stopped forwarding ports: the dev container stopped.");
                }

                return;
            }

            var key = container.Id + "|" + string.Join(",", ports);
            if (key == this.forwardedKey)
            {
                return;
            }

            this.Stop();
            var log = await devContainers.GetLogAsync(cancellationToken);
            this.forwarder = new PortForwarder(this.runtime, container.Id, log);
            this.forwardedKey = key;
            this.Ports = this.forwarder.Start(ports);
            foreach (var port in this.Ports)
            {
                log(port.IsForwarded
                    ? $"Forwarding port {port.Spec} of the dev container to http://localhost:{port.Spec.Port}"
                    : $"Not forwarding port {port.Spec}: {port.Problem} (published by appPort or forwarded by VS Code?).");
            }
        }
        finally
        {
            this.gate.Release();
        }
    }

    public void Dispose()
    {
        this.Stop();
        this.gate.Dispose();
    }

    private static IReadOnlyList<PortSpec> ReadPorts(DevContainerConfigFile configFile)
    {
        try
        {
            return DevContainerConfig.Load(configFile.ConfigPath).ForwardPorts
                .Select(p => PortSpec.TryParse(p, out var spec) ? spec : null)
                .OfType<PortSpec>()
                .Distinct()
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // Being edited: forwarding pauses until the file parses again.
            return [];
        }
    }

    private void Stop()
    {
        this.forwarder?.Dispose();
        this.forwarder = null;
        this.forwardedKey = null;
        this.Ports = [];
    }
}
