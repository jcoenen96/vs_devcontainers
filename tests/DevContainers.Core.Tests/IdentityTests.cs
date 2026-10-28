using DevContainers.Core.Processes;
using DevContainers.Core.Runtime;

namespace DevContainers.Core.Tests;

public class IdentityTests
{
    [Theory]
    [InlineData(@"C:\src\app", @"c:\src\app", true)]
    [InlineData(@"C:\src\app\", @"c:/src/app", true)]
    [InlineData(@"C:\", @"c:", true)]
    [InlineData(@"C:\src\app", @"C:\src\app2", false)]
    public void SamePath_OnWindowsIgnoresCaseAndSeparators(string a, string b, bool expected) =>
        Assert.Equal(expected, DevContainerIdentity.SamePath(a, b, isWindows: true));

    [Theory]
    [InlineData("/src/app", "/src/app/", true)]
    [InlineData("/src/app", "/src/App", false)]
    [InlineData("/", "/", true)]
    public void SamePath_OnUnixIsCaseSensitive(string a, string b, bool expected) =>
        Assert.Equal(expected, DevContainerIdentity.SamePath(a, b, isWindows: false));

    [Fact]
    public void ForNewContainer_WritesPathsLikeVsCode()
    {
        var identity = DevContainerIdentity.ForNewContainer(@"C:\Repos\App\", @"C:/Repos/App/.devcontainer/devcontainer.json", isWindows: true);

        Assert.Equal(@"c:\Repos\App", identity.LocalFolder);
        Assert.Equal(@"c:\Repos\App\.devcontainer\devcontainer.json", identity.ConfigFile);
    }

    [Fact]
    public async Task Find_MatchesLabelVariantsAndPrefersRunning()
    {
        var runner = new FakeProcessRunner(spec => spec.Arguments[0] switch
        {
            "ps" => FakeProcessRunner.Ok("old\texited\nvscode\trunning\nother\trunning\nlegacy\texited\n"),
            "inspect" => FakeProcessRunner.Ok(string.Join('\n',
                "/src/\t/src/.devcontainer/devcontainer.json\t<no value>",
                "/src\t/src/.devcontainer/devcontainer.json\t",
                "/other\t/other/.devcontainer/devcontainer.json\t",
                "/src\t<no value>\t<no value>") + "\n"),
            _ => FakeProcessRunner.Fail("unexpected"),
        });
        var engine = new ContainerEngineClient(new ContainerRuntime(ContainerRuntimeKind.Docker, "docker", "27"), runner);

        var found = await engine.FindDevContainersAsync("/src", "/src/.devcontainer/devcontainer.json", default);

        Assert.Equal(["vscode", "old", "legacy"], found.Select(c => c.Id));
        Assert.Equal(new DevContainerIdentity("/src", "/src/.devcontainer/devcontainer.json"), found[0].Identity);
        Assert.Equal(new DevContainerIdentity("/src/", "/src/.devcontainer/devcontainer.json"), found[1].Identity);
        Assert.Null(found[2].Identity);
        Assert.All(found, c => Assert.Null(c.ComposeProject));
        Assert.Equal(["ps", "--all", "--filter", "label=devcontainer.local_folder", "--format", "{{.ID}}\t{{.State}}"], runner.Calls[0].Arguments);
        Assert.Equal(["old", "vscode", "other", "legacy"], runner.Calls[1].Arguments.Skip(3));
    }

    [Fact]
    public async Task Find_SkipsInspectWhenNothingIsLabelled()
    {
        var runner = new FakeProcessRunner(_ => FakeProcessRunner.Ok());
        var engine = new ContainerEngineClient(new ContainerRuntime(ContainerRuntimeKind.Podman, "podman", "5"), runner);

        Assert.Empty(await engine.FindDevContainersAsync("/src", "/src/.devcontainer/devcontainer.json", default));
        Assert.Single(runner.Calls);
    }

    [Theory]
    [InlineData(0, "/proc/42/cmdline\n", true)]
    [InlineData(0, "", false)]
    [InlineData(1, "", false)]
    public async Task IsVsCodeAttached_LooksForTheVsCodeServer(int exitCode, string stdout, bool expected)
    {
        var runner = new FakeProcessRunner(_ => new ProcessResult(exitCode, stdout, string.Empty));
        var engine = new ContainerEngineClient(new ContainerRuntime(ContainerRuntimeKind.Docker, "docker", "27"), runner);

        Assert.Equal(expected, await engine.IsVsCodeAttachedAsync("abc", default));
        Assert.Equal(["exec", "abc", "sh", "-c"], runner.Calls[0].Arguments.Take(4));
    }
}
