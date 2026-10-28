using System.Diagnostics;
using DevContainers.Core.Configuration;
using DevContainers.Core.Processes;
using DevContainers.Core.Runtime;
using Xunit.Abstractions;

namespace DevContainers.VisualStudio.UITests;

/// <summary>
/// Drives a real Visual Studio with the extension installed, the way a user would: open the sample Web API, use
/// Extensions > Dev Containers > Open in Container, start the app with Debug > Start Without Debugging, call it from
/// the host, stop it, and stop the container. Runs only when DEVCONTAINERS_UITEST=1 is set, on a Windows machine with
/// Visual Studio 2026, the extension, the devcontainer CLI and Podman or Docker (Linux containers).
/// </summary>
public sealed class VisualStudioEndToEndTests(ITestOutputHelper output)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    [Fact]
    public async Task SampleApi_RunsInDevContainerFromVisualStudio()
    {
        if (Environment.GetEnvironmentVariable("DEVCONTAINERS_UITEST") != "1")
        {
            return;
        }

        var artifacts = Environment.GetEnvironmentVariable("DEVCONTAINERS_UITEST_ARTIFACTS") ?? Path.Combine(Path.GetTempPath(), "devcontainers-uitest");
        Directory.CreateDirectory(artifacts);
        var configFile = Assert.Single(DevContainerConfigLocator.FindFromFolder(Path.Combine(FindRepository(), "samples", "dotnet-webapi")));
        var runtime = await new ContainerRuntimeDetector(new ProcessRunner()).DetectAsync(ContainerRuntimePreference.Auto, null, default);
        var engine = new ContainerEngineClient(runtime, new ProcessRunner());
        this.Log($"Container engine: {runtime.Kind} {runtime.ServerVersion}. Workspace: {configFile.WorkspaceFolder}");

        using var vs = VisualStudioDriver.Launch(Path.Combine(configFile.WorkspaceFolder, "HelloApi.slnx"), artifacts, this.Log);
        try
        {
            // 1. The solution loads and the "Dev Container" launch profile is the selected one.
            // Only a progress signal: the toolbar's naming differs between Visual Studio versions, and the steps below
            // check the outcome themselves.
            vs.WaitForElement("Dev Container", TimeSpan.FromMinutes(3), "the 'Dev Container' launch profile in the toolbar");
            vs.Screenshot("01-solution-loaded");

            // 2. Extensions > Dev Containers > Open in Container builds and starts the container.
            vs.ClickMenu("Extensions", "Dev Containers", "Open in Container");
            var prompt = vs.WaitForPrompt(
                ["The dev container is running"],
                ["could not be started", "missing", "No devcontainer.json"],
                TimeSpan.FromMinutes(25));
            vs.Screenshot("02-container-running");
            this.Log("Visual Studio said: " + prompt);
            vs.ClosePrompt();

            var container = Assert.Single(await engine.FindDevContainersAsync(configFile.WorkspaceFolder, configFile.ConfigPath, default));
            Assert.True(container.IsRunning, $"Container {container.Id} is {container.State}.");

            // 3. Debug > Start Without Debugging runs the app inside the container; the host reaches it on port 5080.
            var runStarted = DateTime.Now;
            vs.ClickMenu("Debug", "Start Without Debugging");
            var info = await WaitForApiAsync(TimeSpan.FromMinutes(10), vs);
            vs.Screenshot("03-app-running");
            this.Log("GET /api/info: " + info);
            Assert.Contains("\"inContainer\":true", info);

            // The app ran in the container that Open in Container started, not in a second one.
            Assert.Equal(container.Id, Assert.Single(await engine.FindDevContainersAsync(configFile.WorkspaceFolder, configFile.ConfigPath, default)).Id);

            // 4. Stopping the app (closing its console window) keeps the container running.
            var stopped = StopLauncherWindows(runStarted);
            this.Log($"Closed {stopped} launcher window(s).");
            await Task.Delay(TimeSpan.FromSeconds(5));
            Assert.True(
                Assert.Single(await engine.FindDevContainersAsync(configFile.WorkspaceFolder, configFile.ConfigPath, default)).IsRunning,
                "The container stopped together with the app.");

            // 5. Extensions > Dev Containers > Stop Container stops it.
            vs.ClickMenu("Extensions", "Dev Containers", "Stop Container");
            await WaitUntilAsync(
                async () => !(await engine.FindDevContainersAsync(configFile.WorkspaceFolder, configFile.ConfigPath, default)).Any(c => c.IsRunning),
                TimeSpan.FromMinutes(2),
                "the container to stop",
                vs);
            vs.Screenshot("04-container-stopped");
        }
        catch
        {
            vs.Screenshot("failure");
            vs.LogWindows();
            throw;
        }
        finally
        {
            vs.SaveOutputPane();
        }
    }

    private static async Task<string> WaitForApiAsync(TimeSpan timeout, VisualStudioDriver vs)
    {
        string? body = null;
        await WaitUntilAsync(
            async () =>
            {
                try
                {
                    body = await Http.GetStringAsync("http://localhost:5080/api/info");
                    return true;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    return false;
                }
            },
            timeout,
            "http://localhost:5080/api/info to answer",
            vs);
        return body!;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string what, VisualStudioDriver vs)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out after {timeout} waiting for {what}.");
            }

            vs.DismissKnownDialogs();
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>Closes the console window(s) the Dev Container profile opened, like a user stopping the app.</summary>
    private static int StopLauncherWindows(DateTime since)
    {
        var count = 0;
        foreach (var process in Process.GetProcessesByName("powershell"))
        {
            using (process)
            {
                try
                {
                    if (process.StartTime >= since.AddSeconds(-5))
                    {
                        // Closing a console window ends every process attached to it.
                        process.Kill(entireProcessTree: true);
                        count++;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Already gone, or not ours.
                }
            }
        }

        return count;
    }

    private static string FindRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VsDevContainers.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        output.WriteLine(line);
        Console.WriteLine(line);
    }
}
