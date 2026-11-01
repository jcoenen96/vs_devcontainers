using System.Diagnostics;
using DevContainers.Core.Processes;
using DevContainers.Core.Runtime;

namespace DevContainers.Core.Diagnostics;

/// <summary>The state of the default Podman machine, which runs Podman's containers on Windows and macOS.</summary>
public enum PodmanMachineState
{
    Unknown,
    NoMachine,
    Stopped,
    Starting,
    Running,
}

/// <summary>
/// Starts a container engine that is installed but not running (Docker Desktop, a Podman machine), and tells
/// whether one is still starting, so a command can wait for it instead of failing.
/// </summary>
public sealed class EngineStarter(IProcessRunner runner, Func<bool>? isDockerDesktopRunning = null)
{
    private readonly Func<bool> isDockerDesktopRunning = isDockerDesktopRunning ?? (() => Process.GetProcessesByName("Docker Desktop").Length > 0);

    /// <summary>Docker Desktop's program on Windows, if it is installed.</summary>
    public static string? FindDockerDesktop()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "Docker Desktop.exe");
        return File.Exists(path) ? path : null;
    }

    /// <summary>The default Podman machine's state.</summary>
    public async Task<PodmanMachineState> GetPodmanMachineStateAsync(string podmanPath, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessSpec(podmanPath, ["machine", "inspect", "--format", "{{.State}}"]), null, cancellationToken).ConfigureAwait(false);
        var output = (result.StandardOutput + result.StandardError).Trim().ToLowerInvariant();
        return output switch
        {
            _ when !result.Succeeded && (output.Contains("does not exist") || output.Contains("no such") || output.Contains("not found")) => PodmanMachineState.NoMachine,
            _ when !result.Succeeded => PodmanMachineState.Unknown,
            _ when output.StartsWith("running", StringComparison.Ordinal) => PodmanMachineState.Running,
            _ when output.StartsWith("starting", StringComparison.Ordinal) => PodmanMachineState.Starting,
            _ when output.StartsWith("stopped", StringComparison.Ordinal) || output.StartsWith("configured", StringComparison.Ordinal) => PodmanMachineState.Stopped,
            _ => PodmanMachineState.Unknown,
        };
    }

    /// <summary>
    /// Whether an engine that failed to answer is on its way up: Docker Desktop's app is running but its engine is
    /// not answering yet, or the Podman machine says it is starting.
    /// </summary>
    public async Task<bool> IsStartingAsync(IReadOnlyList<EngineProblem> engines, CancellationToken cancellationToken)
    {
        foreach (var engine in engines.Where(e => e.Path is not null))
        {
            if (engine.Kind == ContainerRuntimeKind.Docker && this.isDockerDesktopRunning())
            {
                return true;
            }

            if (engine.Kind == ContainerRuntimeKind.Podman && await this.GetPodmanMachineStateAsync(engine.Path!, cancellationToken).ConfigureAwait(false) == PodmanMachineState.Starting)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Starts the default Podman machine, creating it first when there is none.</summary>
    public async Task StartPodmanMachineAsync(string podmanPath, Action<string> log, CancellationToken cancellationToken)
    {
        var sink = new LogSink(log);
        if (await this.GetPodmanMachineStateAsync(podmanPath, cancellationToken).ConfigureAwait(false) == PodmanMachineState.NoMachine)
        {
            log("Creating the Podman machine (podman machine init). This downloads its image the first time.");
            var init = await runner.RunAsync(new ProcessSpec(podmanPath, ["machine", "init"]), sink, cancellationToken).ConfigureAwait(false);
            if (!init.Succeeded)
            {
                throw new InvalidOperationException("Creating the Podman machine failed: " + init.StandardError.Trim());
            }
        }

        log("Starting the Podman machine (podman machine start).");
        var start = await runner.RunAsync(new ProcessSpec(podmanPath, ["machine", "start"]), sink, cancellationToken).ConfigureAwait(false);
        if (!start.Succeeded && !start.StandardError.Contains("already running", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Starting the Podman machine failed: " + start.StandardError.Trim());
        }
    }

    /// <summary>Starts Docker Desktop. It takes a while to answer; wait with <see cref="ContainerRuntimeDetector.WaitAsync"/>.</summary>
    public static void StartDockerDesktop()
    {
        var path = FindDockerDesktop() ?? throw new InvalidOperationException("Docker Desktop is not installed in the usual folder. Start it yourself.");
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }

    private sealed class LogSink(Action<string> log) : IProcessOutputSink
    {
        public void OnStandardOutput(string line)
        {
            if (line.Trim().Length > 0)
            {
                log(line);
            }
        }

        public void OnStandardError(string line) => this.OnStandardOutput(line);
    }
}
