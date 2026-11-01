using DevContainers.Core.Configuration;
using DevContainers.Core.Forwarding;
using DevContainers.Core.Processes;
using DevContainers.Core.Runtime;

namespace DevContainers.VisualStudio.Services;

/// <summary>
/// Forwards the <c>forwardPorts</c> of the open solution's dev container, and the ports that programs in it listen on
/// (unless <c>portsAttributes</c> says to ignore them), to <c>localhost</c> while that container runs, whoever started it: Open in Container, the Run button's launcher, or VS Code. Checks every few seconds,
/// and stops forwarding when the container stops or another solution is opened.
/// </summary>
internal sealed class PortForwardingService(DevContainerService devContainers) : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim gate = new(1, 1);
    private PortForwarder? forwarder;
    private readonly HashSet<PortSpec> failed = [];
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
            var config = configFile is null ? null : ReadConfig(configFile);
            if (configFile is null || config is null)
            {
                this.Stop();
                return;
            }

            var listed = config.ForwardPorts
                .Select(p => PortSpec.TryParse(p, out var spec) ? spec : null)
                .OfType<PortSpec>()
                .Distinct()
                .ToList();
            if (listed.Count == 0 && !config.AutoForwardPorts)
            {
                this.Stop();
                return;
            }

            var settings = await devContainers.ReadSettingsAsync(cancellationToken);
            this.runtime ??= await new ContainerRuntimeDetector(new ProcessRunner()).DetectAsync(settings.Runtime, settings.RuntimePath, cancellationToken);
            var engine = new ContainerEngineClient(this.runtime, new ProcessRunner());
            var container = (await engine.FindDevContainersAsync(configFile.WorkspaceFolder, configFile.ConfigPath, cancellationToken)).FirstOrDefault(c => c.IsRunning);
            var log = await devContainers.GetLogAsync(cancellationToken);
            if (container is null)
            {
                if (this.forwarder is not null)
                {
                    this.Stop();
                    log("Stopped forwarding ports: the dev container stopped.");
                }

                return;
            }

            if (this.forwarder?.ContainerId != container.Id)
            {
                this.Stop();
                this.forwarder = new PortForwarder(this.runtime, container.Id, log);
            }

            // What listens in the container: ports the app opened (forwarded without being listed, as VS Code does)
            // and the address each one listens on.
            var listening = await ContainerPortScanner.ScanAsync(new ProcessRunner(), this.runtime, container.Id, cancellationToken);
            this.forwarder.SetContainerListeners(listening);
            var detected = config.AutoForwardPorts
                ? listening.Select(l => l.Port)
                    .Where(port => !config.IgnoredPorts.Contains(port) && !listed.Any(p => p.Host == "localhost" && p.Port == port))
                    .Distinct()
                    .Select(port => new PortSpec("localhost", port))
                : [];

            // A port that could not be forwarded (in use on this computer) is not tried again every round, only once
            // the configuration or the container changes.
            var key = container.Id + "|" + string.Join(",", listed);
            if (key != this.forwardedKey)
            {
                this.forwardedKey = key;
                this.failed.Clear();
            }

            var wanted = listed.Concat(detected).Where(p => !this.failed.Contains(p)).ToList();
            foreach (var port in this.forwarder.Update(wanted))
            {
                var how = listed.Contains(port.Spec) ? string.Empty : " (detected)";
                if (port.IsForwarded)
                {
                    log($"Forwarding port {port.Spec}{how} of the dev container to http://localhost:{port.Spec.Port}");
                }
                else
                {
                    this.failed.Add(port.Spec);
                    log($"Not forwarding port {port.Spec}{how}: {port.Problem} (published by appPort, forwarded by VS Code, or used by a program on Windows?).");
                }
            }

            this.Ports = this.forwarder.Forwarded.Select(p => new ForwardedPort(p, true, null)).ToList();
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

    private static DevContainerConfig? ReadConfig(DevContainerConfigFile configFile)
    {
        try
        {
            return DevContainerConfig.Load(configFile.ConfigPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // Being edited: forwarding pauses until the file parses again.
            return null;
        }
    }

    private void Stop()
    {
        this.forwarder?.Dispose();
        this.forwarder = null;
        this.forwardedKey = null;
        this.failed.Clear();
        this.Ports = [];
    }
}
