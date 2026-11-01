using DevContainers.Core.Cli;
using DevContainers.Core.Processes;
using DevContainers.Core.Runtime;

namespace DevContainers.Core.Launch;

/// <summary>
/// Copies this computer's settings into a running dev container, the same way the Run button's launcher does
/// before each run: NuGet feeds and credentials, the HTTPS development certificate, and Git name, email and line
/// ending settings. Runs the launcher script (embedded in this assembly) with <c>-SyncOnly</c>, so there is one
/// implementation.
/// </summary>
public static class HostStateSync
{
    public static ProcessSpec CreateSpec(string scriptPath, ContainerRuntime runtime, UpResult container, string workspaceFolder, string? projectFolder = null) =>
        new(
            OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh",
            [
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-SyncOnly",
                "-EnginePath", runtime.ExecutablePath,
                "-ContainerId", container.ContainerId,
                "-ContainerUser", container.RemoteUser ?? "root",
                "-WorkspaceFolder", workspaceFolder,
                .. (projectFolder is null ? Array.Empty<string>() : ["-Project", projectFolder]),
            ]);

    /// <summary>Writes the embedded launcher script to a temporary folder and returns its path.</summary>
    public static string WriteScript()
    {
        var folder = Path.Combine(Path.GetTempPath(), "vs-devcontainers");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, DevContainerLaunchProfile.ScriptFileName);
        File.WriteAllText(path, DevContainerLaunchProfile.ReadScript());
        return path;
    }

    /// <summary>Runs the copy. Never throws for a failed copy: the container works without it, and the log says why.</summary>
    public static async Task RunAsync(IProcessRunner runner, ContainerRuntime runtime, UpResult container, string workspaceFolder, Action<string> log, CancellationToken cancellationToken, string? projectFolder = null)
    {
        try
        {
            var result = await runner.RunAsync(CreateSpec(WriteScript(), runtime, container, workspaceFolder, projectFolder), new LineSink(log), cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                log($"Copying settings from this computer into the container failed (exit code {result.ExitCode}).");
            }
        }
        catch (ProcessStartException ex)
        {
            log($"Settings from this computer were not copied into the container: {ex.Message}");
        }
        catch (IOException ex)
        {
            log($"Settings from this computer were not copied into the container: {ex.Message}");
        }
    }

    private sealed class LineSink(Action<string> log) : IProcessOutputSink
    {
        public void OnStandardOutput(string line)
        {
            if (line.Length > 0)
            {
                log(line);
            }
        }

        public void OnStandardError(string line)
        {
            if (line.Length > 0)
            {
                log(line);
            }
        }
    }
}
