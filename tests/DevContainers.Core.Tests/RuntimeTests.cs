using DevContainers.Core.Processes;
using DevContainers.Core.Runtime;

namespace DevContainers.Core.Tests;

public class RuntimeTests
{
    private static readonly Func<string, string?> BothInstalled = name => name switch
    {
        "docker" => "/usr/bin/docker",
        "podman" => "/usr/bin/podman",
        _ => null,
    };

    [Fact]
    public async Task Detect_PrefersDockerWhenRunning()
    {
        var runner = new FakeProcessRunner(_ => FakeProcessRunner.Ok("27.3.1\n"));

        var runtime = await new ContainerRuntimeDetector(runner, BothInstalled).DetectAsync(ContainerRuntimePreference.Auto, null, default);

        Assert.Equal(new ContainerRuntime(ContainerRuntimeKind.Docker, "/usr/bin/docker", "27.3.1"), runtime);
    }

    [Fact]
    public async Task Detect_FallsBackToPodmanWhenDockerDaemonIsDown()
    {
        var runner = new FakeProcessRunner(spec => spec.FileName.EndsWith("docker", StringComparison.Ordinal)
            ? FakeProcessRunner.Fail("Cannot connect to the Docker daemon")
            : FakeProcessRunner.Ok("5.2.0"));
        Func<string, string?> find = name => name == "podman-compose" ? "/usr/bin/podman-compose" : BothInstalled(name);

        var runtime = await new ContainerRuntimeDetector(runner, find).DetectAsync(ContainerRuntimePreference.Auto, null, default);

        Assert.Equal(ContainerRuntimeKind.Podman, runtime.Kind);
        Assert.Equal("/usr/bin/podman-compose", runtime.ComposePath);
    }

    [Fact]
    public async Task Detect_HonoursExplicitPreferenceAndPath()
    {
        var runner = new FakeProcessRunner(_ => FakeProcessRunner.Ok("5.2.0"));

        var runtime = await new ContainerRuntimeDetector(runner, BothInstalled).DetectAsync(ContainerRuntimePreference.Podman, @"D:\tools\podman.exe", default);

        Assert.Equal(@"D:\tools\podman.exe", runtime.ExecutablePath);
        Assert.Equal(@"D:\tools\podman.exe", Assert.Single(runner.Calls).FileName);
    }

    [Fact]
    public async Task Detect_ExplainsEveryFailure()
    {
        var runner = new FakeProcessRunner(_ => FakeProcessRunner.Fail("Cannot connect to the Docker daemon\nmore"));
        Func<string, string?> find = name => name == "docker" ? "/usr/bin/docker" : null;

        var ex = await Assert.ThrowsAsync<ContainerRuntimeNotFoundException>(
            () => new ContainerRuntimeDetector(runner, find).DetectAsync(ContainerRuntimePreference.Auto, null, default));

        Assert.Contains("docker: Cannot connect to the Docker daemon;", ex.Message);
        Assert.Contains("podman: not found on PATH", ex.Message);
    }

    [Fact]
    public async Task Engine_StopsWholeComposeProject()
    {
        var runner = new FakeProcessRunner(spec => string.Join(' ', spec.Arguments) switch
        {
            var a when a.StartsWith("ps --all --filter label=devcontainer.local_folder", StringComparison.Ordinal) => FakeProcessRunner.Ok("app1\trunning\n"),
            var a when a.StartsWith("inspect", StringComparison.Ordinal) => FakeProcessRunner.Ok("myproj\n"),
            var a when a.StartsWith("ps --all --filter label=com.docker.compose.project=myproj", StringComparison.Ordinal) => FakeProcessRunner.Ok("app1\trunning\ndb1\trunning\nold\texited\n"),
            _ => FakeProcessRunner.Ok(),
        });
        var engine = new ContainerEngineClient(new ContainerRuntime(ContainerRuntimeKind.Docker, "docker", "27"), runner);

        var containers = await engine.FindDevContainersAsync("/src", "/src/.devcontainer/devcontainer.json", default);
        var stopped = await engine.StopAsync(containers, default);

        Assert.Equal("myproj", Assert.Single(containers).ComposeProject);
        Assert.Equal(["app1", "db1"], stopped);
        Assert.Equal(["stop", "app1", "db1"], runner.Calls[^1].Arguments);
    }

    [Fact]
    public void Engine_ParsesPsOutput()
    {
        var containers = ContainerEngineClient.ParsePs("abc\trunning\r\ndef\texited\n\n");

        Assert.Equal([new ContainerInfo("abc", "running", null), new ContainerInfo("def", "exited", null)], containers);
        Assert.True(containers[0].IsRunning);
        Assert.False(containers[1].IsRunning);
    }
}
