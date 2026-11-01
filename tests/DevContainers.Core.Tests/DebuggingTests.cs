using System.Text.Json.Nodes;
using DevContainers.Core.Cli;
using DevContainers.Core.Debugging;
using DevContainers.Core.Processes;
using DevContainers.Core.Runtime;
using DevContainers.Core.Testing;

namespace DevContainers.Core.Tests;

public class DebuggingTests
{
    [Fact]
    public void ToContainerPath_MapsFoldersInsideTheWorkspace()
    {
        var local = Path.Combine(Path.GetTempPath(), "repo", "samples");
        Assert.Equal("/workspaces/repo/samples", ContainerDebugger.ToContainerPath(local, "/workspaces/repo/samples", local));
        Assert.Equal("/workspaces/repo/samples/Api/Web", ContainerDebugger.ToContainerPath(local, "/workspaces/repo/samples/", Path.Combine(local, "Api", "Web")));
    }

    [Fact]
    public void LaunchProfile_ReadsNamedOrFirstProjectProfile()
    {
        using var temp = new TempFolder();
        temp.Write("Properties/launchSettings.json", """
            {
              // Comments are allowed.
              "profiles": {
                "Dev Container": { "commandName": "Executable", "commandLineArgs": "-File \"x.ps1\" -LaunchProfile \"https\"" },
                "http": { "commandName": "Project", "applicationUrl": "http://0.0.0.0:5080", "environmentVariables": { "A": "1" } },
                "https": { "commandName": "Project", "applicationUrl": "https://0.0.0.0:5443", "commandLineArgs": "--verbose --port 1" },
              }
            }
            """);

        Assert.Equal("https", LaunchProfileSettings.InnerProfileName(temp.Path));
        var first = LaunchProfileSettings.Read(temp.Path, null)!;
        Assert.Equal("http", first.Name);
        Assert.Equal("1", first.EnvironmentVariables["A"]);
        var named = LaunchProfileSettings.Read(temp.Path, "https")!;
        Assert.Equal("https://0.0.0.0:5443", named.ApplicationUrl);
        Assert.Equal("--verbose --port 1", named.CommandLineArgs);
        Assert.Null(LaunchProfileSettings.Read(temp.Path, "missing"));
    }

    [Theory]
    [InlineData("http://localhost:5049", "http://0.0.0.0:5049")]
    [InlineData("https://localhost:7183;http://localhost:5049", "https://0.0.0.0:7183;http://0.0.0.0:5049")]
    [InlineData("http://127.0.0.1:80/ ; http://[::1]:5", "http://0.0.0.0:80/;http://0.0.0.0:5")]
    [InlineData("http://*:9;http://+:8;http://0.0.0.0:7;http://localhostx:1", "http://*:9;http://+:8;http://0.0.0.0:7;http://localhostx:1")]
    public void ToContainerUrls_ListensOnEveryInterfaceInsteadOfLoopback(string urls, string expected) =>
        Assert.Equal(expected, LaunchProfileSettings.ToContainerUrls(urls));

    [Fact]
    public void ContainerPorts_AreThoseOfTheProfileTheDevContainerProfileRuns()
    {
        using var temp = new TempFolder();
        temp.Write("Properties/launchSettings.json", """
            {
              "profiles": {
                "Dev Container": { "commandName": "Executable", "commandLineArgs": "-File \"x.ps1\" -LaunchProfile \"https\"" },
                "http": { "commandName": "Project", "applicationUrl": "http://localhost:5049" },
                "https": { "commandName": "Project", "applicationUrl": "https://localhost:7183;http://localhost:5049;http://*:80" }
              }
            }
            """);
        Assert.Equal([7183, 5049], LaunchProfileSettings.ContainerPorts(temp.Path));

        using var plain = new TempFolder();
        plain.Write("Properties/launchSettings.json", """{ "profiles": { "http": { "commandName": "Project", "applicationUrl": "http://localhost:5049" } } }""");
        Assert.Empty(LaunchProfileSettings.ContainerPorts(plain.Path));
        Assert.Empty(LaunchProfileSettings.ContainerPorts(Path.Combine(plain.Path, "missing")));
    }

    [Fact]
    public void TestRun_RunsDotnetTestInTheContainer()
    {
        var spec = ContainerTestRun.CreateSpec(
            new ContainerRuntime(ContainerRuntimeKind.Docker, "docker", "29.0"),
            new UpResult("abc123", "vscode", "/workspaces/repo"),
            "/workspaces/repo",
            "My App.slnx");

        Assert.Equal(["exec", "-u", "vscode", "-w", "/workspaces/repo", "abc123", "sh", "-c"], spec.Arguments.Take(8));
        Assert.Contains("dotnet test 'My App.slnx' --property:UseArtifactsOutput=true", spec.Arguments[^1]);
        Assert.StartsWith(ContainerDebugger.BuildLock, spec.Arguments[^1]);
    }

    [Fact]
    public void LaunchJson_RunsVsdbgInTheContainerAndMapsSources()
    {
        var debugger = new ContainerDebugger(
            new FakeProcessRunner(_ => FakeProcessRunner.Ok()),
            new ContainerRuntime(ContainerRuntimeKind.Podman, @"C:\Program Files\Podman\podman.exe", "5.0"),
            new UpResult("abc123", "vscode", "/workspaces/repo/samples"));
        var target = new DebugTarget(
            "/home/vscode/.vs-devcontainer/build/bin/Api/debug/Api.dll",
            "/workspaces/repo/samples/Api",
            new Dictionary<string, string> { ["ASPNETCORE_URLS"] = "https://0.0.0.0:5443" },
            ["--flag"]);

        var json = JsonNode.Parse(debugger.CreateLaunchJson("/home/vscode/.vsdbg/vsdbg", target, @"C:\src\repo\samples", "Api (dev container)"))!;

        Assert.Equal(@"C:\Program Files\Podman\podman.exe", (string?)json["adapter"]);
        Assert.Equal("exec -i -u vscode -w /workspaces/repo/samples/Api abc123 /home/vscode/.vsdbg/vsdbg --interpreter=vscode", (string?)json["adapterArgs"]);
        var configuration = json["configurations"]![0]!;
        Assert.Equal("coreclr", (string?)configuration["type"]);
        Assert.Equal("launch", (string?)configuration["request"]);
        Assert.Equal(target.Program, (string?)configuration["program"]);
        Assert.Equal("https://0.0.0.0:5443", (string?)configuration["env"]!["ASPNETCORE_URLS"]);
        Assert.Equal("--flag", (string?)configuration["args"]![0]);
        Assert.Equal(@"C:\src\repo\samples", (string?)configuration["sourceFileMap"]!["/workspaces/repo/samples"]);
    }

    /// <summary>Builds the sample API in a real .NET dev container image. Runs only with DEVCONTAINERS_INTEGRATION=1.</summary>
    [Fact]
    public async Task Build_ReturnsTheProgramInTheContainer()
    {
        if (Environment.GetEnvironmentVariable("DEVCONTAINERS_INTEGRATION") != "1")
        {
            return;
        }

        var runtime = await new ContainerRuntimeDetector(new ProcessRunner()).DetectAsync(
            Enum.TryParse<ContainerRuntimePreference>(Environment.GetEnvironmentVariable("DEVCONTAINERS_RUNTIME"), ignoreCase: true, out var preference) ? preference : ContainerRuntimePreference.Auto,
            null,
            default);
        var samples = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "dotnet-webapi"));

        // As root, so the container can write to the mounted folder whoever owns it on this machine.
        var run = await new ProcessRunner().RunAsync(
            new ProcessSpec(runtime.ExecutablePath, ["run", "-d", "--rm", "-v", $"{samples}:/workspaces/sample", "mcr.microsoft.com/devcontainers/dotnet:10.0", "sleep", "600"]),
            null,
            default);
        Assert.True(run.Succeeded, run.StandardError);
        var id = run.StandardOutput.Trim();

        try
        {
            var debugger = new ContainerDebugger(new ProcessRunner(), runtime, new UpResult(id, "root", "/workspaces/sample"));
            var log = new List<string>();
            var target = await debugger.BuildAsync("/workspaces/sample/HelloApi", LaunchProfileSettings.Read(Path.Combine(samples, "HelloApi"), "https"), log.Add, default);

            Assert.Equal("/root/.vs-devcontainer/build/bin/HelloApi/debug/HelloApi.dll", target.Program);
            Assert.Equal("/workspaces/sample/HelloApi", target.WorkingDirectory);
            Assert.Equal("https://0.0.0.0:5443;http://0.0.0.0:5080", target.Environment["ASPNETCORE_URLS"]);
            Assert.Contains(log, l => l.Contains("Build succeeded", StringComparison.Ordinal));
        }
        finally
        {
            await new ProcessRunner().RunAsync(new ProcessSpec(runtime.ExecutablePath, ["rm", "-f", id]), null, default);
        }
    }

    [Fact]
    public async Task Tests_RunInTheContainer()
    {
        if (Environment.GetEnvironmentVariable("DEVCONTAINERS_INTEGRATION") != "1")
        {
            return;
        }

        var runtime = await new ContainerRuntimeDetector(new ProcessRunner()).DetectAsync(
            Enum.TryParse<ContainerRuntimePreference>(Environment.GetEnvironmentVariable("DEVCONTAINERS_RUNTIME"), ignoreCase: true, out var preference) ? preference : ContainerRuntimePreference.Auto,
            null,
            default);
        var samples = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "dotnet-webapi"));
        var run = await new ProcessRunner().RunAsync(
            new ProcessSpec(runtime.ExecutablePath, ["run", "-d", "--rm", "-v", $"{samples}:/workspaces/sample", "mcr.microsoft.com/devcontainers/dotnet:10.0", "sleep", "600"]),
            null,
            default);
        Assert.True(run.Succeeded, run.StandardError);
        var id = run.StandardOutput.Trim();

        try
        {
            var log = new List<string>();
            var passed = await ContainerTestRun.RunAsync(new ProcessRunner(), runtime, new UpResult(id, "root", "/workspaces/sample"), "/workspaces/sample", "HelloApi.slnx", log.Add, default);

            Assert.True(passed, string.Join(Environment.NewLine, log));
            Assert.Contains(log, l => l.Contains("Passed!", StringComparison.Ordinal));
        }
        finally
        {
            await new ProcessRunner().RunAsync(new ProcessSpec(runtime.ExecutablePath, ["rm", "-f", id]), null, default);
        }
    }
}
