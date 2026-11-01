using DevContainers.Core.Cli;
using DevContainers.Core.Diagnostics;
using DevContainers.Core.Processes;
using DevContainers.Core.Runtime;

namespace DevContainers.Core.Tests;

public class DiagnosticsTests
{
    [Fact]
    public void Diagnose_NoEngineInstalled_SaysToInstallOne()
    {
        var problem = ProblemAdvisor.Diagnose(new ContainerRuntimeNotFoundException("x", [
            new EngineProblem(ContainerRuntimeKind.Docker, null, "not found on PATH"),
            new EngineProblem(ContainerRuntimeKind.Podman, null, "not found on PATH"),
        ]));

        Assert.Contains("Neither Docker nor Podman", problem.Summary);
        Assert.Equal(ProblemFix.None, problem.Fix);
        Assert.Equal("No container engine", problem.HelpTopic);
    }

    [Fact]
    public void Diagnose_PodmanInstalledButStopped_OffersToStartTheMachine()
    {
        var problem = ProblemAdvisor.Diagnose(new ContainerRuntimeNotFoundException("x", [
            new EngineProblem(ContainerRuntimeKind.Docker, null, "not found on PATH"),
            new EngineProblem(ContainerRuntimeKind.Podman, @"C:\podman.exe", "Cannot connect to Podman. Please verify your connection to the Linux system"),
        ]));

        Assert.Equal(ProblemFix.StartPodmanMachine, problem.Fix);
        Assert.Equal("Start the Podman machine", problem.FixLabel);
    }

    [Fact]
    public void Diagnose_DockerNotRunning_SaysToStartDockerDesktop()
    {
        var problem = ProblemAdvisor.Diagnose(new ContainerRuntimeNotFoundException("x", [
            new EngineProblem(ContainerRuntimeKind.Docker, @"C:\docker.exe", "failed to connect to the docker API at npipe:////./pipe/docker_engine"),
            new EngineProblem(ContainerRuntimeKind.Podman, null, "not found on PATH"),
        ]));

        Assert.Contains("Docker is installed but not running", problem.Summary);
        // Offered only where Docker Desktop is installed, which it is not on the test machine.
        Assert.Equal(EngineStarter.FindDockerDesktop() is null ? ProblemFix.None : ProblemFix.StartDockerDesktop, problem.Fix);
    }

    [Fact]
    public void Diagnose_DockerInWindowsContainersMode_SaysToSwitch()
    {
        var problem = ProblemAdvisor.Diagnose(new ContainerRuntimeNotFoundException("x", [
            new EngineProblem(ContainerRuntimeKind.Docker, @"C:\docker.exe", "the engine runs Windows containers; dev containers need Linux containers"),
        ]));

        Assert.Contains("Switch to Linux containers", problem.NextStep);
    }

    [Theory]
    [InlineData("write /var/lib/docker/tmp: no space left on device", "Out of disk space", ProblemFix.None)]
    [InlineData("Bind for 0.0.0.0:5080 failed: port is already allocated", "A port is already in use", ProblemFix.None)]
    [InlineData("pull access denied for myimage, repository does not exist", "The image cannot be pulled", ProblemFix.None)]
    [InlineData("Get \"https://mcr.microsoft.com/v2/\": dial tcp: lookup mcr.microsoft.com: no such host", "No network", ProblemFix.None)]
    [InlineData("Error response from daemon: No such container: abc123", "The container was removed", ProblemFix.OpenInContainer)]
    [InlineData("Error: Cannot find module '/usr/lib/node_modules/@devcontainers/cli/dist/spec-node/devContainersSpecCLI.js'", "The devcontainer CLI fails", ProblemFix.None)]
    [InlineData("ERROR: failed to solve: process \"/bin/sh -c apt-get install nope\" did not complete successfully: exit code: 100", "The image build fails", ProblemFix.RebuildContainer)]
    [InlineData("The postCreateCommand in the devcontainer.json failed.", "A lifecycle command fails", ProblemFix.RebuildContainer)]
    [InlineData("WSL 2 requires an update to its kernel component", "WSL is not working", ProblemFix.None)]
    public void Diagnose_RecognisesCommonFailures(string error, string topic, ProblemFix fix)
    {
        var problem = ProblemAdvisor.Diagnose(new DevContainerCliException("devcontainer up failed.", error), error);

        Assert.Equal(topic, problem.HelpTopic);
        Assert.Equal(fix, problem.Fix);
    }

    [Fact]
    public void Diagnose_UnknownFailure_KeepsTheErrorAndPointsToTheLog()
    {
        var problem = ProblemAdvisor.Diagnose(new InvalidOperationException("Something odd."), "first line\nsecond line");

        Assert.Equal("first line", problem.Summary);
        Assert.Contains("Output window", problem.NextStep);
        Assert.Null(problem.HelpTopic);
    }

    [Theory]
    [InlineData(0, "Running\n", PodmanMachineState.Running)]
    [InlineData(0, "Starting\n", PodmanMachineState.Starting)]
    [InlineData(0, "Stopped\n", PodmanMachineState.Stopped)]
    [InlineData(125, "Error: podman-machine-default: VM does not exist", PodmanMachineState.NoMachine)]
    [InlineData(125, "Error: something else", PodmanMachineState.Unknown)]
    public async Task PodmanMachineState_IsReadFromInspect(int exitCode, string output, PodmanMachineState expected)
    {
        var runner = new FakeProcessRunner(_ => exitCode == 0 ? FakeProcessRunner.Ok(output) : new ProcessResult(exitCode, string.Empty, output));

        Assert.Equal(expected, await new EngineStarter(runner, () => false).GetPodmanMachineStateAsync("podman", default));
        Assert.Equal(["machine", "inspect", "--format", "{{.State}}"], runner.Calls.Single().Arguments);
    }

    [Fact]
    public async Task StartPodmanMachine_CreatesTheMachineWhenThereIsNone()
    {
        var runner = new FakeProcessRunner(spec => spec.Arguments[1] == "inspect"
            ? new ProcessResult(125, string.Empty, "VM does not exist")
            : FakeProcessRunner.Ok());

        await new EngineStarter(runner, () => false).StartPodmanMachineAsync("podman", _ => { }, default);

        Assert.Equal(["inspect", "init", "start"], runner.Calls.Select(c => c.Arguments[1]));
    }

    [Fact]
    public async Task IsStarting_WhenDockerDesktopRunsOrPodmanMachineStarts()
    {
        var docker = new[] { new EngineProblem(ContainerRuntimeKind.Docker, "docker", "not answering") };
        var podman = new[] { new EngineProblem(ContainerRuntimeKind.Podman, "podman", "not answering") };
        var starting = new FakeProcessRunner(_ => FakeProcessRunner.Ok("Starting"));
        var stopped = new FakeProcessRunner(_ => FakeProcessRunner.Ok("Stopped"));

        Assert.True(await new EngineStarter(stopped, () => true).IsStartingAsync(docker, default));
        Assert.False(await new EngineStarter(stopped, () => false).IsStartingAsync(docker, default));
        Assert.True(await new EngineStarter(starting, () => false).IsStartingAsync(podman, default));
        Assert.False(await new EngineStarter(stopped, () => false).IsStartingAsync(podman, default));
    }

    [Fact]
    public async Task WaitAsync_KeepsTryingUntilTheEngineAnswers()
    {
        var attempts = 0;
        var runner = new FakeProcessRunner(_ => ++attempts < 3 ? FakeProcessRunner.Fail("Cannot connect to Podman") : FakeProcessRunner.Ok("5.6.1 linux"));

        var runtime = await new ContainerRuntimeDetector(runner, name => name == "podman" ? "/usr/bin/podman" : null)
            .WaitAsync(ContainerRuntimePreference.Podman, null, TimeSpan.FromSeconds(10), default, TimeSpan.FromMilliseconds(10));

        Assert.Equal("5.6.1", runtime.ServerVersion);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task WaitAsync_GivesUpAfterTheTimeout()
    {
        var runner = new FakeProcessRunner(_ => FakeProcessRunner.Fail("Cannot connect to Podman"));

        var ex = await Assert.ThrowsAsync<ContainerRuntimeNotFoundException>(() => new ContainerRuntimeDetector(runner, name => name == "podman" ? "/usr/bin/podman" : null)
            .WaitAsync(ContainerRuntimePreference.Podman, null, TimeSpan.FromMilliseconds(50), default, TimeSpan.FromMilliseconds(10)));

        Assert.Equal(ContainerRuntimeKind.Podman, Assert.Single(ex.Engines).Kind);
    }
}
