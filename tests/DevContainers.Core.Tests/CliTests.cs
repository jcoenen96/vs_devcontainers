using DevContainers.Core.Cli;
using DevContainers.Core.Processes;
using DevContainers.Core.Runtime;

namespace DevContainers.Core.Tests;

public class CliTests
{
    private static readonly ContainerRuntime Docker = new(ContainerRuntimeKind.Docker, "/usr/bin/docker", "27.0");

    [Fact]
    public void Locator_RunsWindowsShimThroughNode()
    {
        var prefix = Path.Combine(Path.GetTempPath(), "npm");
        var shim = Path.Combine(prefix, "devcontainer.cmd");
        var script = Path.Combine(prefix, "node_modules", "@devcontainers", "cli", "devcontainer.js");
        var locator = new DevContainerCliLocator(
            findOnPath: name => name switch { "devcontainer" => shim, "node" => @"C:\node\node.exe", _ => null },
            fileExists: path => path == shim || path == script,
            isWindows: true,
            appDataFolder: null);

        var command = locator.Locate();

        Assert.Equal(@"C:\node\node.exe", command.FileName);
        Assert.Equal([script], command.PrefixArguments);
    }

    [Fact]
    public void Locator_FallsBackToGlobalNpmFolderOnWindows()
    {
        var script = Path.Combine(@"C:\Users\me\AppData\Roaming", "npm", "node_modules", "@devcontainers", "cli", "devcontainer.js");
        var locator = new DevContainerCliLocator(
            findOnPath: name => name == "node" ? "node.exe" : null,
            fileExists: path => path == script,
            isWindows: true,
            appDataFolder: @"C:\Users\me\AppData\Roaming");

        Assert.Equal([script], locator.Locate().PrefixArguments);
    }

    [Fact]
    public void Locator_RunsUnixExecutableDirectly()
    {
        var locator = new DevContainerCliLocator(
            findOnPath: name => name == "devcontainer" ? "/usr/local/bin/devcontainer" : null,
            fileExists: _ => true,
            isWindows: false);

        var command = locator.Locate();

        Assert.Equal("/usr/local/bin/devcontainer", command.FileName);
        Assert.Empty(command.PrefixArguments);
    }

    [Fact]
    public void Locator_ExplainsHowToInstallWhenMissing()
    {
        var locator = new DevContainerCliLocator(findOnPath: _ => null, fileExists: _ => false, isWindows: false);

        var ex = Assert.Throws<DevContainerCliNotFoundException>(() => locator.Locate());
        Assert.Contains("npm install -g @devcontainers/cli", ex.Message);
    }

    [Fact]
    public void Locator_RequiresNodeForScript()
    {
        var locator = new DevContainerCliLocator(findOnPath: _ => null, fileExists: _ => true, isWindows: false);

        var ex = Assert.Throws<DevContainerCliNotFoundException>(() => locator.Locate("/opt/cli/devcontainer.js"));
        Assert.Contains("Node.js", ex.Message);
    }

    [Fact]
    public void ParseUpResult_ReadsSuccess()
    {
        var result = DevContainerCli.ParseUpResult(new ProcessResult(
            0,
            """{"outcome":"success","containerId":"abc","remoteUser":"vscode","remoteWorkspaceFolder":"/workspaces/app"}""" + "\n",
            string.Empty));

        Assert.Equal(new UpResult("abc", "vscode", "/workspaces/app"), result);
    }

    [Fact]
    public void ParseUpResult_ThrowsWithCliMessageOnError()
    {
        var ex = Assert.Throws<DevContainerCliException>(() => DevContainerCli.ParseUpResult(new ProcessResult(
            1,
            """{"outcome":"error","message":"Dev container config (x) not found.","description":"details"}""",
            string.Empty)));

        Assert.Equal("Dev container config (x) not found.", ex.Message);
        Assert.Equal("details", ex.Description);
    }

    [Fact]
    public void ParseUpResult_ThrowsWhenCliPrintsNoResult()
    {
        var ex = Assert.Throws<DevContainerCliException>(() => DevContainerCli.ParseUpResult(new ProcessResult(1, string.Empty, "node: bad option")));

        Assert.Equal("node: bad option", ex.Description);
    }

    [Fact]
    public async Task UpAsync_PassesRuntimeAndRebuildFlags()
    {
        var runner = new FakeProcessRunner(_ => FakeProcessRunner.Ok("""{"outcome":"success","containerId":"c","remoteWorkspaceFolder":"/w"}"""));
        var podman = new ContainerRuntime(ContainerRuntimeKind.Podman, "/usr/bin/podman", "5.0") { ComposePath = "/usr/bin/podman-compose" };
        var cli = new DevContainerCli(new DevContainerCliCommand("node", ["cli.js"]), podman, runner);

        await cli.UpAsync(new UpOptions("/src", "/src/.devcontainer/devcontainer.json") { RemoveExistingContainer = true, BuildNoCache = true }, null, default);

        var call = Assert.Single(runner.Calls);
        Assert.Equal("node", call.FileName);
        Assert.Equal(
            [
                "cli.js", "up",
                "--workspace-folder", "/src",
                "--config", "/src/.devcontainer/devcontainer.json",
                "--log-format", "json",
                "--docker-path", "/usr/bin/podman",
                "--docker-compose-path", "/usr/bin/podman-compose",
                "--remove-existing-container",
                "--build-no-cache",
            ],
            call.Arguments);
    }

    [Fact]
    public async Task UpAsync_StreamsReadableLog()
    {
        var stderr = string.Join('\n',
            """{"type":"start","level":2,"text":"Run: docker build"}""",
            """{"type":"raw","level":3,"text":"Step 1/3\n"}""",
            """{"type":"stop","level":2,"text":"Run: docker build"}""",
            """{"type":"progress","name":"Running postCreateCommand...","status":"succeeded"}""");
        var runner = new FakeProcessRunner(_ => new ProcessResult(0, """{"outcome":"success","containerId":"c","remoteWorkspaceFolder":"/w"}""", stderr));
        var cli = new DevContainerCli(new DevContainerCliCommand("devcontainer", []), Docker, runner);
        var log = new List<string>();

        await cli.UpAsync(new UpOptions("/src", "/src/.devcontainer.json"), log.Add, default);

        Assert.Equal(["> Run: docker build", "Step 1/3", "[succeeded] Running postCreateCommand..."], log);
    }

    [Fact]
    public void CreateExecSpec_PutsCommandAfterCliOptions()
    {
        var cli = new DevContainerCli(new DevContainerCliCommand("node", ["cli.js"]), Docker, new FakeProcessRunner(_ => FakeProcessRunner.Ok()));

        var spec = cli.CreateExecSpec("/src", "/src/.devcontainer.json", ["dotnet", "--info"]);

        Assert.Equal(
            ["cli.js", "exec", "--workspace-folder", "/src", "--config", "/src/.devcontainer.json", "--docker-path", "/usr/bin/docker", "dotnet", "--info"],
            spec.Arguments);
        Assert.Throws<ArgumentException>(() => cli.CreateExecSpec("/src", "/src/.devcontainer.json", []));
    }

    [Fact]
    public async Task UpAndExec_PassIdLabels()
    {
        var runner = new FakeProcessRunner(_ => FakeProcessRunner.Ok("""{"outcome":"success","containerId":"c","remoteWorkspaceFolder":"/w"}"""));
        var cli = new DevContainerCli(new DevContainerCliCommand("devcontainer", []), Docker, runner);
        var identity = new DevContainerIdentity(@"c:\src", @"c:\src\.devcontainer\devcontainer.json");
        string[] idLabels =
        [
            "--id-label", @"devcontainer.local_folder=c:\src",
            "--id-label", @"devcontainer.config_file=c:\src\.devcontainer\devcontainer.json",
        ];

        await cli.UpAsync(new UpOptions(@"C:\src", @"C:\src\.devcontainer\devcontainer.json") { Identity = identity }, null, default);
        var exec = cli.CreateExecSpec(@"C:\src", @"C:\src\.devcontainer\devcontainer.json", ["true"], identity);

        Assert.Equal(idLabels, runner.Calls[0].Arguments.Skip(9));
        Assert.Equal([.. idLabels, "true"], exec.Arguments.Skip(7));
    }

    [Fact]
    public void LogFormatter_PassesThroughPlainText()
    {
        Assert.Equal("plain", DevContainerLogFormatter.Format("plain"));
        Assert.Equal("{not json", DevContainerLogFormatter.Format("{not json"));
        Assert.Null(DevContainerLogFormatter.Format("""{"type":"text","text":""}"""));
    }
}
