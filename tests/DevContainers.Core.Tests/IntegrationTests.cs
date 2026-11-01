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

    [Fact]
    public async Task Open_ReusesContainerCreatedByAnotherTool()
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
        temp.Write(".devcontainer/devcontainer.json", """{ "image": "docker.io/library/alpine:3.20" }""");
        var configFile = Assert.Single(DevContainerConfigLocator.FindFromFolder(temp.Path));
        var engineRuntime = await new Runtime.ContainerRuntimeDetector(new Processes.ProcessRunner()).DetectAsync(settings.Runtime, null, default);
        var engine = new Runtime.ContainerEngineClient(engineRuntime, new Processes.ProcessRunner());
        var cli = new Cli.DevContainerCli(new Cli.DevContainerCliLocator().Locate(), engineRuntime, new Processes.ProcessRunner());

        try
        {
            // Another tool (VS Code) created the container, spelling the folder differently in its labels.
            var foreign = new Runtime.DevContainerIdentity(configFile.WorkspaceFolder + Path.DirectorySeparatorChar, configFile.ConfigPath);
            var created = await cli.UpAsync(new Cli.UpOptions(configFile.WorkspaceFolder, configFile.ConfigPath) { Identity = foreign }, null, default);

            var session = new DevContainerSession(configFile);
            var log = new List<string>();
            var opened = await session.OpenAsync(settings, log.Add, default);

            Assert.Equal(created.ContainerId, opened.ContainerId);
            Assert.Equal(foreign, session.Identity);
            Assert.Contains(log, l => l.StartsWith("Reusing dev container", StringComparison.Ordinal));
            Assert.Single(await engine.FindDevContainersAsync(configFile.WorkspaceFolder, configFile.ConfigPath, default));

            // While a VS Code server runs in the container, Visual Studio knows the container is shared.
            Assert.False(await session.IsOpenInVsCodeAsync(settings, default));
            var server = await new Processes.ProcessRunner().RunAsync(
                new Processes.ProcessSpec(engineRuntime.ExecutablePath, ["exec", "-d", created.ContainerId, "sh", "-c", "mkdir -p /tmp/.vscode-server/bin && cp /bin/sleep /tmp/.vscode-server/bin/sleep && exec /tmp/.vscode-server/bin/sleep 300"]),
                null,
                default);
            Assert.True(server.Succeeded, server.StandardError);
            await Task.Delay(500);
            Assert.True(await session.IsOpenInVsCodeAsync(settings, default));
        }
        finally
        {
            await engine.RemoveAsync(await engine.FindDevContainersAsync(configFile.WorkspaceFolder, configFile.ConfigPath, default), default);
        }
    }
}
