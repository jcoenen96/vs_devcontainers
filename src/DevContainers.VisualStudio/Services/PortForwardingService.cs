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

    /// <summary>The open solution's dev container as last checked, for the status window.</summary>
    public ContainerStatus Status { get; private set; } = ContainerStatus.None;

    /// <summary>Raised when <see cref="Status"/> changes.</summary>
    public event EventHandler<ContainerStatus>? StatusChanged;

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
                this.Publish(configFile is null ? ContainerStatus.None : this.Status with { Problem = "devcontainer.json cannot be read (being edited?)." });
                return;
            }

            var settings = await devContainers.ReadSettingsAsync(cancellationToken);
            try
            {
                this.runtime ??= await new ContainerRuntimeDetector(new ProcessRunner()).DetectAsync(settings.Runtime, settings.RuntimePath, cancellationToken);
            }
            catch (ContainerRuntimeNotFoundException ex)
            {
                this.Stop();
                this.Publish(new ContainerStatus(configFile, null, null, false, [], ex.Message));
                throw;
            }

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

                this.Publish(new ContainerStatus(configFile, this.runtime, null, false, [], null));
                devContainers.OfferToOpenOnLoad(configFile, config.VisualStudio);
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
                this.Publish(new ContainerStatus(configFile, this.runtime, container.Id, true, [], null));
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

            this.Ports = this.forwarder.Forwarded.Select(p => new ForwardedPort(p, true, null))
                .Concat(this.failed.Select(p => new ForwardedPort(p, false, "in use on this computer")))
                .OrderBy(p => p.Spec.Port)
                .ToList();
            this.Publish(new ContainerStatus(configFile, this.runtime, container.Id, true, this.Ports, null));
        }
        finally
        {
            this.gate.Release();
        }
    }

    private void Publish(ContainerStatus status)
    {
        if (status.Equals(this.Status))
        {
            return;
        }

        this.Status = status;
        this.StatusChanged?.Invoke(this, status);
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

/// <summary>The open solution's dev container, as the port forwarding last saw it.</summary>
/// <param name="ConfigFile">The configuration, or null when the solution has none.</param>
/// <param name="Runtime">The container engine.</param>
/// <param name="ContainerId">The running container, or null.</param>
/// <param name="IsRunning">Whether it runs.</param>
/// <param name="Ports">The forwarded ports, and the ones that could not be forwarded.</param>
/// <param name="Problem">What keeps the check from working, if anything.</param>
internal sealed record ContainerStatus(DevContainerConfigFile? ConfigFile, ContainerRuntime? Runtime, string? ContainerId, bool IsRunning, IReadOnlyList<ForwardedPort> Ports, string? Problem)
{
    public static ContainerStatus None { get; } = new(null, null, null, false, [], null);

    public bool Equals(ContainerStatus? other) =>
        other is not null
        && this.ConfigFile?.ConfigPath == other.ConfigFile?.ConfigPath
        && this.Runtime?.Kind == other.Runtime?.Kind
        && this.ContainerId == other.ContainerId
        && this.IsRunning == other.IsRunning
        && this.Problem == other.Problem
        && this.Ports.SequenceEqual(other.Ports);

    public override int GetHashCode() => HashCode.Combine(this.ConfigFile?.ConfigPath, this.ContainerId, this.IsRunning, this.Ports.Count);
}
