using DevContainers.Core.Cli;
using DevContainers.Core.Prerequisites;

namespace DevContainers.Core.Tests;

public class PrerequisiteTests
{
    private static readonly DevContainerCliCommand Cli = new("node", ["devcontainer.js"]);

    [Fact]
    public void Checker_ReportsReadyWhenCliIsFound()
    {
        var checker = new PrerequisiteChecker(
            findOnPath: name => name switch { "node" => "/bin/node", "devcontainer" => "/bin/devcontainer", _ => null },
            cliLocator: new DevContainerCliLocator(name => name == "devcontainer" ? "/bin/devcontainer" : null, _ => true, isWindows: false));

        var status = checker.Check(explicitCliPath: null);

        Assert.True(status.IsReady);
        Assert.Equal("/bin/node", status.NodePath);
    }

    [Fact]
    public void Checker_ReportsMissingNodeAndCli()
    {
        var checker = new PrerequisiteChecker(
            findOnPath: name => name == "winget" ? @"C:\winget.exe" : null,
            cliLocator: new DevContainerCliLocator(_ => null, _ => false, isWindows: true, appDataFolder: null));

        var status = checker.Check(explicitCliPath: null);

        Assert.False(status.IsReady);
        Assert.True(status.NodeMissing);
        Assert.True(status.CliMissing);
        Assert.Equal(@"C:\winget.exe", status.WingetPath);
    }

    [Fact]
    public void Plan_InstallsNodeWithWingetThenCliWithNpm()
    {
        var plan = InstallPlan.For(new PrerequisiteStatus(NodePath: null, NpmPath: null, Cli: null, WingetPath: "winget.exe"));

        Assert.True(plan.CanRun);
        Assert.Equal(["Node.js", "the devcontainer CLI"], plan.Missing);
        Assert.Equal(
            [
                "winget install --id OpenJS.NodeJS.LTS --exact --source winget --accept-source-agreements --accept-package-agreements",
                "npm install -g @devcontainers/cli",
            ],
            plan.Commands);
    }

    [Fact]
    public void Plan_OnlyInstallsCliWhenNodeIsPresent()
    {
        var plan = InstallPlan.For(new PrerequisiteStatus("node.exe", "npm.cmd", Cli: null, WingetPath: null));

        Assert.True(plan.CanRun);
        Assert.Equal(["npm install -g @devcontainers/cli"], plan.Commands);
    }

    [Fact]
    public void Plan_CannotRunWithoutWingetWhenNodeIsMissing()
    {
        var plan = InstallPlan.For(new PrerequisiteStatus(NodePath: null, NpmPath: null, Cli: null, WingetPath: null));

        Assert.False(plan.CanRun);
        Assert.Equal(2, plan.Commands.Count);
    }

    [Fact]
    public void Plan_IsEmptyWhenEverythingIsInstalled()
    {
        var plan = InstallPlan.For(new PrerequisiteStatus("node.exe", "npm.cmd", Cli, "winget.exe"));

        Assert.Empty(plan.Commands);
        Assert.False(plan.CanRun);
    }

    [Fact]
    public void Script_RunsEachCommandAndReloadsPath()
    {
        var plan = InstallPlan.For(new PrerequisiteStatus(NodePath: null, NpmPath: null, Cli: null, WingetPath: "winget.exe"));

        var script = plan.ToPowerShellScript();
        var spec = plan.ToProcessSpec();

        Assert.Contains("Invoke-Step 'winget install --id OpenJS.NodeJS.LTS", script);
        Assert.Contains("Invoke-Step 'npm install -g @devcontainers/cli'", script);
        Assert.Contains("GetEnvironmentVariable('Path', 'Machine')", script);
        Assert.True(script.IndexOf("winget", StringComparison.Ordinal) < script.IndexOf("npm install", StringComparison.Ordinal));
        Assert.Equal("powershell.exe", spec.FileName);
        Assert.Equal("-EncodedCommand", spec.Arguments[^2]);
        Assert.Equal(script, System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(spec.Arguments[^1])));
    }
}
