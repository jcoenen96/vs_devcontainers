using DevContainers.Core.Cli;
using DevContainers.Core.Configuration;
using DevContainers.Core.Processes;
using DevContainers.Core.Runtime;

namespace DevContainers.Core;

public enum DevContainerState
{
    NotStarted,
    Starting,
    Running,
    Stopping,
    Stopped,
    Failed,
}

/// <summary>
/// The dev container for one workspace. Serializes operations so a second click on "Open in Container" while
/// one is building does not start a second build.
/// </summary>
public sealed class DevContainerSession(
    DevContainerConfigFile configFile,
    IProcessRunner? runner = null,
    DevContainerCliLocator? cliLocator = null)
{
    private readonly IProcessRunner runner = runner ?? new ProcessRunner();
    private readonly DevContainerCliLocator cliLocator = cliLocator ?? new DevContainerCliLocator();
    private readonly SemaphoreSlim gate = new(1, 1);

    public event EventHandler<DevContainerState>? StateChanged;

    public DevContainerConfigFile ConfigFile { get; } = configFile;

    public DevContainerState State { get; private set; }

    public ContainerRuntime? Runtime { get; private set; }

    /// <summary>Set while the container is running.</summary>
    public UpResult? Container { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Builds (if needed) and starts the dev container.</summary>
    public Task<UpResult> OpenAsync(DevContainerSettings settings, Action<string> log, CancellationToken cancellationToken) =>
        this.UpAsync(settings, new UpOptions(this.ConfigFile.WorkspaceFolder, this.ConfigFile.ConfigPath), log, cancellationToken);

    /// <summary>Removes the existing container and creates it again from the current configuration.</summary>
    public Task<UpResult> RebuildAsync(DevContainerSettings settings, bool noCache, Action<string> log, CancellationToken cancellationToken) =>
        this.UpAsync(
            settings,
            new UpOptions(this.ConfigFile.WorkspaceFolder, this.ConfigFile.ConfigPath) { RemoveExistingContainer = true, BuildNoCache = noCache },
            log,
            cancellationToken);

    /// <summary>Stops the container (and its Compose siblings) but keeps it, so the next open is fast.</summary>
    public async Task StopAsync(DevContainerSettings settings, Action<string> log, CancellationToken cancellationToken)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.SetState(DevContainerState.Stopping);
            var engine = new ContainerEngineClient(await this.GetRuntimeAsync(settings, cancellationToken).ConfigureAwait(false), this.runner);
            var containers = await engine.FindDevContainersAsync(this.ConfigFile.WorkspaceFolder, this.ConfigFile.ConfigPath, cancellationToken).ConfigureAwait(false);
            var stopped = await engine.StopAsync(containers, cancellationToken).ConfigureAwait(false);
            log(stopped.Count == 0 ? "No running dev container was found." : $"Stopped {stopped.Count} container(s).");
            this.Container = null;
            this.SetState(DevContainerState.Stopped);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.Fail(ex);
            throw;
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <summary>The command line for an interactive login shell in the running container.</summary>
    public ProcessSpec CreateShellSpec(DevContainerSettings settings)
    {
        if (this.State != DevContainerState.Running || this.Runtime is null)
        {
            throw new InvalidOperationException("The dev container is not running. Open it first.");
        }

        var cli = new DevContainerCli(this.cliLocator.Locate(settings.CliPath), this.Runtime, this.runner);
        return cli.CreateExecSpec(
            this.ConfigFile.WorkspaceFolder,
            this.ConfigFile.ConfigPath,
            ["/bin/sh", "-c", "if command -v bash >/dev/null 2>&1; then exec bash -l; else exec sh -l; fi"]);
    }

    private async Task<UpResult> UpAsync(DevContainerSettings settings, UpOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.SetState(DevContainerState.Starting);
            var runtime = await this.GetRuntimeAsync(settings, cancellationToken).ConfigureAwait(false);
            log($"Using {runtime.Kind} {runtime.ServerVersion} ({runtime.ExecutablePath}).");

            var cli = new DevContainerCli(this.cliLocator.Locate(settings.CliPath), runtime, this.runner);
            var result = await cli.UpAsync(options, log, cancellationToken).ConfigureAwait(false);

            this.Container = result;
            this.SetState(DevContainerState.Running);
            return result;
        }
        catch (OperationCanceledException)
        {
            this.SetState(DevContainerState.NotStarted);
            throw;
        }
        catch (Exception ex)
        {
            this.Fail(ex);
            throw;
        }
        finally
        {
            this.gate.Release();
        }
    }

    private async Task<ContainerRuntime> GetRuntimeAsync(DevContainerSettings settings, CancellationToken cancellationToken)
    {
        // Detect every time: the user may have started Docker Desktop or switched to Podman since the last run.
        this.Runtime = await new ContainerRuntimeDetector(this.runner)
            .DetectAsync(settings.Runtime, settings.RuntimePath, cancellationToken)
            .ConfigureAwait(false);
        return this.Runtime;
    }

    private void Fail(Exception ex)
    {
        this.LastError = ex is DevContainerCliException { Description: { } description } && description != ex.Message
            ? $"{ex.Message}{Environment.NewLine}{description}"
            : ex.Message;
        this.SetState(DevContainerState.Failed);
    }

    private void SetState(DevContainerState state)
    {
        if (state != DevContainerState.Failed)
        {
            this.LastError = null;
        }

        this.State = state;
        this.StateChanged?.Invoke(this, state);
    }
}
