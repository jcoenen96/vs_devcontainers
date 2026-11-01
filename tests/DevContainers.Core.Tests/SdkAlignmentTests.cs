using DevContainers.Core.Cli;
using DevContainers.Core.Diagnostics;
using DevContainers.Core.Processes;
using DevContainers.Core.Runtime;
using DevContainers.Core.Sdk;

namespace DevContainers.Core.Tests;

public class SdkAlignmentTests
{
    private static IReadOnlyList<SdkVersion> Sdks(params string[] versions) => SdkAlignment.ParseSdkList(string.Join('\n', versions.Select(v => $"{v} [/usr/share/dotnet/sdk]")));

    private static SortedSet<int> Targets(params int[] majors) => [.. majors];

    [Fact]
    public void ParseSdkList_ReadsAndSortsVersions()
    {
        var sdks = SdkAlignment.ParseSdkList("""
            9.0.304 [C:\Program Files\dotnet\sdk]
            10.0.100-rc.2.25502.107 [C:\Program Files\dotnet\sdk]
            10.0.100 [C:\Program Files\dotnet\sdk]
            8.0.414 [C:\Program Files\dotnet\sdk]

            """);

        Assert.Equal(["8.0.414", "9.0.304", "10.0.100-rc.2.25502.107", "10.0.100"], sdks.Select(s => s.Text));
        Assert.Equal(400, sdks[0].FeatureBand);
    }

    [Fact]
    public void Compare_SameSdks_NothingToSay() =>
        Assert.Empty(SdkAlignment.Compare(Sdks("10.0.100"), Sdks("10.0.103"), Targets(10), null));

    [Fact]
    public void Compare_ContainerTooOldForTheTarget_IsAnError()
    {
        var finding = Assert.Single(SdkAlignment.Compare(Sdks("10.0.100"), Sdks("8.0.414"), Targets(10), null));

        Assert.Equal(SdkSeverity.Error, finding.Severity);
        Assert.Contains("net10.0", finding.Message);
        Assert.Contains("NETSDK1045", finding.Message);
    }

    [Fact]
    public void Compare_NoSdkInContainer_SaysWhichFeatureToAdd()
    {
        var finding = Assert.Single(SdkAlignment.Compare(Sdks("10.0.100"), [], Targets(10), null));

        Assert.Equal(SdkSeverity.Error, finding.Severity);
        Assert.Contains("ghcr.io/devcontainers/features/dotnet", finding.Message);
    }

    [Fact]
    public void Compare_HostTooOld_WarnsAboutIntelliSense()
    {
        var finding = Assert.Single(SdkAlignment.Compare(Sdks("9.0.304"), Sdks("10.0.100"), Targets(10), null));

        Assert.Equal(SdkSeverity.Warning, finding.Severity);
        Assert.Contains("IntelliSense", finding.Message);
        Assert.Contains("Microsoft.DotNet.SDK.10", finding.Message);
    }

    [Fact]
    public void Compare_NoSdkOnHost_Warns() =>
        Assert.Equal(SdkSeverity.Warning, Assert.Single(SdkAlignment.Compare([], Sdks("10.0.100"), Targets(10), null)).Severity);

    [Fact]
    public void Compare_DifferentFeatureBands_IsANote()
    {
        var finding = Assert.Single(SdkAlignment.Compare(Sdks("10.0.200"), Sdks("10.0.100"), Targets(10), null));

        Assert.Equal(SdkSeverity.Info, finding.Severity);
        Assert.Contains("\"version\": \"10.0.100\"", finding.Message);
    }

    [Fact]
    public void Compare_NoDotnetProjects_NothingToSay() =>
        Assert.Empty(SdkAlignment.Compare([], [], Targets(), null));

    [Theory]
    [InlineData("10.0.100", "latestPatch", "10.0.105", true)]
    [InlineData("10.0.100", "latestPatch", "10.0.200", false)]
    [InlineData("10.0.100", "latestFeature", "10.0.200", true)]
    [InlineData("10.0.100", "latestFeature", "11.0.100", false)]
    [InlineData("10.0.100", "latestMajor", "11.0.100", true)]
    [InlineData("10.0.105", "disable", "10.0.106", false)]
    [InlineData("10.0.105", "latestPatch", "10.0.104", false)]
    [InlineData("10.0.100", "latestPatch", "10.0.101-preview.1", false)]
    public void GlobalJson_RollForward(string version, string rollForward, string installed, bool satisfied) =>
        Assert.Equal(satisfied, new GlobalJsonSdk("global.json", SdkVersion.TryParse(version)!, rollForward, false).IsSatisfiedBy(SdkVersion.TryParse(installed)!));

    [Fact]
    public void Compare_GlobalJsonNotInContainer_IsAnError()
    {
        var globalJson = new GlobalJsonSdk("/w/global.json", SdkVersion.TryParse("9.0.300")!, "latestFeature", false);

        var findings = SdkAlignment.Compare(Sdks("9.0.304", "10.0.100"), Sdks("10.0.100"), Targets(9), globalJson);

        var finding = Assert.Single(findings);
        Assert.Equal(SdkSeverity.Error, finding.Severity);
        Assert.Contains("9.0.300", finding.Message);
    }

    [Fact]
    public void FindGlobalJson_AndTargets_ReadTheWorkspace()
    {
        using var folder = new TempFolder();
        File.WriteAllText(Path.Combine(folder.Path, "global.json"), """{ "sdk": { "version": "10.0.100", "rollForward": "latestFeature" } }""");
        Directory.CreateDirectory(Path.Combine(folder.Path, "src", "App"));
        File.WriteAllText(Path.Combine(folder.Path, "src", "App", "App.csproj"), "<Project><PropertyGroup><TargetFrameworks>net8.0;net10.0-windows;netstandard2.0;net48</TargetFrameworks></PropertyGroup></Project>");
        Directory.CreateDirectory(Path.Combine(folder.Path, "src", "App", "obj"));
        File.WriteAllText(Path.Combine(folder.Path, "src", "App", "obj", "Stale.csproj"), "<TargetFramework>net6.0</TargetFramework>");

        var globalJson = SdkAlignment.FindGlobalJson(Path.Combine(folder.Path, "src", "App"), folder.Path);

        Assert.NotNull(globalJson);
        Assert.Equal("10.0.100", globalJson.Version.Text);
        Assert.Equal("latestFeature", globalJson.RollForward);
        Assert.Equal([8, 10], SdkAlignment.FindTargetMajors(folder.Path));
    }

    [Fact]
    public void CompareNode_DifferentMajors_IsANote()
    {
        Assert.Null(SdkAlignment.CompareNode("v22.11.0", "v22.23.4\n"));
        Assert.Null(SdkAlignment.CompareNode(null, "v22.23.4"));
        Assert.Contains("@types/node", SdkAlignment.CompareNode("v20.18.0", "v22.23.4")!.Message);
    }

    [Fact]
    public async Task CheckAsync_AsksBothSides()
    {
        using var folder = new TempFolder();
        File.WriteAllText(Path.Combine(folder.Path, "App.csproj"), "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var runner = new FakeProcessRunner(spec => spec.FileName == "dotnet"
            ? FakeProcessRunner.Ok("9.0.304 [C:\\dotnet\\sdk]\n")
            : FakeProcessRunner.Ok("10.0.100 [/usr/share/dotnet/sdk]\n"));

        var findings = await SdkAlignment.CheckAsync(
            runner,
            new ContainerRuntime(ContainerRuntimeKind.Docker, "docker", "27.0"),
            new UpResult("abc", "vscode", "/workspaces/app"),
            folder.Path,
            CancellationToken.None);

        Assert.Equal(SdkSeverity.Warning, Assert.Single(findings).Severity);
        Assert.Contains(runner.Calls, c => c.FileName == "docker" && c.Arguments.Contains("abc"));
    }

    [Fact]
    public void Diagnose_SdkMismatchInBuildOutput_OffersRebuild()
    {
        var problem = ProblemAdvisor.Diagnose(new InvalidOperationException("Build failed"), "error NETSDK1045: The current .NET SDK does not support targeting .NET 12.0.");

        Assert.Equal(ProblemFix.RebuildContainer, problem.Fix);
        Assert.Contains(".NET SDK in the dev container", problem.Summary);
    }
}
