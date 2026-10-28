using DevContainers.Core.Configuration;

namespace DevContainers.Core.Tests;

/// <summary>
/// Runs a real container through the devcontainer CLI. Needs Docker or Podman and the CLI, so it only runs when
/// DEVCONTAINERS_INTEGRATION=1 is set. DEVCONTAINERS_RUNTIME=docker|podman picks the engine.
/// </summary>
public class IntegrationTests
{
    [Fact]
    public async Task OpenExecAndStop_RealContainer()
    {
        if (Environment.GetEnvironmentVariable("DEVCONTAINERS_INTEGRATION") != "1")
        {
            return;
        }

        var settings = DevContainerSettings.Default with
        {
            Runtime = Enum.TryParse<Runtime.ContainerRuntimePreference>(Environment.GetEnvironmentVariable("DEVCONTAINERS_RUNTIME"), ignoreCase: true, out var runtime)
                ? runtime
                : Runtime.ContainerRuntimePreference.Auto,
        };

        using var temp = new TempFolder();
        temp.Write(".devcontainer/devcontainer.json", """
            {
              // Lifecycle hooks and comments go straight through to the CLI.
              "image": "docker.io/library/alpine:3.20",
              "postCreateCommand": "echo created > /tmp/marker",
            }
            """);
        temp.Write("hello.txt", "from the host");

        var configFile = Assert.Single(DevContainerConfigLocator.FindFromFolder(temp.Path));
        var session = new DevContainerSession(configFile);
        var log = new List<string>();
        var states = new List<DevContainerState>();
        session.StateChanged += (_, state) => states.Add(state);

        try
        {
            var up = await session.OpenAsync(settings, log.Add, default);
            Assert.Equal(DevContainerState.Running, session.State);
            Assert.StartsWith("/workspaces/", up.RemoteWorkspaceFolder, StringComparison.Ordinal);

            var cli = new Cli.DevContainerCli(new Cli.DevContainerCliLocator().Locate(), session.Runtime!, new Processes.ProcessRunner());
            var exec = await cli.ExecAsync(configFile.WorkspaceFolder, configFile.ConfigPath, ["cat", "hello.txt", "/tmp/marker"], null, default);
            Assert.True(exec.Succeeded, exec.StandardError);
            Assert.Contains("from the host", exec.StandardOutput);
            Assert.Contains("created", exec.StandardOutput);

            // A second open reuses the container instead of building again.
            var again = await session.OpenAsync(settings, log.Add, default);
            Assert.Equal(up.ContainerId, again.ContainerId);

            await session.StopAsync(settings, log.Add, default);
            Assert.Equal(DevContainerState.Stopped, session.State);
            Assert.Contains("Stopped 1 container(s).", log);
        }
        finally
        {
            if (session.Runtime is not null)
            {
                var engine = new Runtime.ContainerEngineClient(session.Runtime, new Processes.ProcessRunner());
                await engine.RemoveAsync(await engine.FindDevContainersAsync(configFile.WorkspaceFolder, configFile.ConfigPath, default), default);
            }
        }

        Assert.Equal(
            [DevContainerState.Starting, DevContainerState.Running, DevContainerState.Starting, DevContainerState.Running, DevContainerState.Stopping, DevContainerState.Stopped],
            states);
    }
}
