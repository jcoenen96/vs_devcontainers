using DevContainers.Core.Cli;
using DevContainers.Core.Debugging;
using DevContainers.Core.Processes;
using DevContainers.Core.Runtime;

namespace DevContainers.Core.Testing;

/// <summary>
/// Runs <c>dotnet test</c> inside a running dev container, for a solution or a project, with the same build output
/// as the Run button and the debugger, so the tests use the container's SDK and runtime.
/// </summary>
public static class ContainerTestRun
{
    /// <summary>The <c>docker exec</c> / <c>podman exec</c> that runs the tests.</summary>
    /// <param name="runtime">The container engine.</param>
    /// <param name="container">The running dev container.</param>
    /// <param name="remoteFolder">Where to run, in the container.</param>
    /// <param name="target">The solution or project file to test, relative to <paramref name="remoteFolder"/>.</param>
    public static ProcessSpec CreateSpec(ContainerRuntime runtime, UpResult container, string remoteFolder, string target) =>
        new(
            runtime.ExecutablePath,
            [
                "exec", "-u", container.RemoteUser ?? "root", "-w", remoteFolder, container.ContainerId,
                "sh", "-c", $"{ContainerDebugger.BuildLock}dotnet test '{target.Replace("'", "'\\''")}' {ContainerDebugger.ArtifactsArguments}",
            ]);

    /// <summary>Runs the tests and streams their output to <paramref name="log"/>. True when they all passed.</summary>
    public static async Task<bool> RunAsync(IProcessRunner runner, ContainerRuntime runtime, UpResult container, string remoteFolder, string target, Action<string> log, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(CreateSpec(runtime, container, remoteFolder, target), new LineSink(log), cancellationToken).ConfigureAwait(false);
        return result.Succeeded;
    }

    private sealed class LineSink(Action<string> log) : IProcessOutputSink
    {
        public void OnStandardOutput(string line) => log(line);

        public void OnStandardError(string line) => log(line);
    }
}
