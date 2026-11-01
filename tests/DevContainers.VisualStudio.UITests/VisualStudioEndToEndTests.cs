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

        // The Dev Container profile's launcher (started by Visual Studio, which inherits this) writes a transcript here.
        var runLog = Path.Combine(artifacts, "vs-run-transcript.txt");
        Environment.SetEnvironmentVariable("DEVCONTAINERS_RUN_LOG", runLog);

        using var vs = VisualStudioDriver.Launch(Path.Combine(configFile.WorkspaceFolder, "HelloApi.slnx"), artifacts, this.Log);
        try
        {
            // 1. The solution loads, HelloApi is the startup project (the solution also has HelloApi.Tests, and a
            // fresh Visual Studio does not always pick the first project), and the "Dev Container" launch profile is
            // the selected one.
            vs.SetStartupProject(@"HelloApi\HelloApi.csproj", TimeSpan.FromMinutes(5));
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

            // Open in Container copied the Git settings of Windows, and Git works in the folder mounted from Windows.
            var git = await new ProcessRunner().RunAsync(
                new ProcessSpec(runtime.ExecutablePath, ["exec", "-u", "vscode", "-w", "/workspaces/vs_devcontainers", container.Id, "sh", "-c", "git config --global user.name && git status --short | wc -l"]),
                null,
                default);
            this.Log($"Git in the container: {git.StandardOutput.Trim()} {git.StandardError.Trim()}");
            Assert.True(git.Succeeded, git.StandardError);
            Assert.StartsWith("Dev Container Tester", git.StandardOutput.Trim());

            // The status window shows the container as running (a progress signal and a screenshot, not an assertion:
            // tool window content is reached through UI Automation differently between Visual Studio versions).
            vs.ClickMenu("Extensions", "Dev Containers", "Show Dev Container Status");
            vs.WaitForElement("Dev Container: running", TimeSpan.FromSeconds(30), "the status window's title");
            vs.Screenshot("02b-status-window");

            // 3. Debug > Start Without Debugging runs the app inside the container; the host reaches it on port 5080,
            // which the extension forwards (forwardPorts) while the container runs.
            // Nothing answers before that, so the answer below comes from this run.
            Assert.False(await IsApiAnsweringAsync(), "Something already answers on port 5080 before the run.");
            var runStarted = DateTime.Now;
            vs.LogDebugTarget();
            vs.ClickMenu("Debug", "Start Without Debugging");
            await Task.Delay(TimeSpan.FromSeconds(60));
            vs.Screenshot("03-starting");
            vs.LogConsoles();
            var info = await WaitForApiAsync(TimeSpan.FromMinutes(7), vs);
            vs.Screenshot("04-app-running");
            this.Log("GET /api/info: " + info);
            Assert.Contains("\"inContainer\":true", info);

            // HTTPS is forwarded too, and the app serves it with Windows' development certificate, which the
            // launcher copied into the container.
            var served = await GetServedCertificateAsync("https://localhost:5443/api/info");
            this.Log($"HTTPS: {served.Body}; certificate {served.Thumbprint}");
            Assert.Contains("\"inContainer\":true", served.Body);
            Assert.Equal(HostDevCertificateThumbprint(), served.Thumbprint);

            // The app ran in the container that Open in Container started, not in a second one.
            Assert.Equal(container.Id, Assert.Single(await engine.FindDevContainersAsync(configFile.WorkspaceFolder, configFile.ConfigPath, default)).Id);

            // 4. Stopping the app (closing its console window) keeps the container running.
            var stopped = StopLauncherWindows(runStarted);
            this.Log($"Closed {stopped} launcher window(s).");
            await Task.Delay(TimeSpan.FromSeconds(5));
            Assert.True(
                Assert.Single(await engine.FindDevContainersAsync(configFile.WorkspaceFolder, configFile.ConfigPath, default)).IsRunning,
                "The container stopped together with the app.");

            // 5. Extensions > Dev Containers > Start Debugging in Container builds the app in the container and starts
            // it under Visual Studio's debugger, which runs in the container too (vsdbg through exec).
            // The previous run still answers (closing its window leaves the app running in the container, by
            // design); the command stops it first, so wait for an answer from the debugged run.
            vs.ClickMenu("Extensions", "Dev Containers", "Start Debugging in Container");
            var debugged = await WaitForApiAsync(TimeSpan.FromMinutes(10), vs, body => body.Contains("\"debuggerAttached\":true", StringComparison.Ordinal));
            vs.Screenshot("05-debugging");
            this.Log("GET /api/info under the debugger: " + debugged);
            Assert.Contains("\"inContainer\":true", debugged);
            Assert.Contains("\"debuggerAttached\":true", debugged);
            vs.ClickMenu("Debug", "Stop Debugging");
            await WaitUntilAsync(async () => !await IsApiAnsweringAsync(), TimeSpan.FromMinutes(1), "the debugged app to stop", vs);

            // 6. Extensions > Dev Containers > Run Tests in Container runs the sample's xUnit tests in the container.
            vs.ClickMenu("Extensions", "Dev Containers", "Run Tests in Container");
            var tests = vs.WaitForPrompt(["All tests passed"], ["failed or could not run", "could not run"], TimeSpan.FromMinutes(10));
            this.Log("Visual Studio said: " + tests);
            vs.ClosePrompt();

            // 7. Extensions > Dev Containers > Stop Container stops it.
            vs.ClickMenu("Extensions", "Dev Containers", "Stop Container");
            await WaitUntilAsync(
                async () => !(await engine.FindDevContainersAsync(configFile.WorkspaceFolder, configFile.ConfigPath, default)).Any(c => c.IsRunning),
                TimeSpan.FromMinutes(2),
                "the container to stop",
                vs);
            vs.Screenshot("06-container-stopped");
        }
        catch
        {
            vs.Screenshot("failure");
            vs.LogWindows();
            vs.LogConsoles();
            this.Log(File.Exists(runLog) ? "Launcher transcript:" + Environment.NewLine + File.ReadAllText(runLog) : "The launcher wrote no transcript.");
            this.LogProcesses();
            await this.LogContainerProcessesAsync(engine, configFile);
            throw;
        }
        finally
        {
            vs.SaveOutputPane();
        }
    }

    /// <summary>
    /// The TypeScript sample, opened as a folder: Run npm Script in Container runs its dev script in the container,
    /// the ports it opens are forwarded to Windows without being listed, and Debug npm Script in Container runs it
    /// with the Node.js inspector, which Windows reaches too.
    /// </summary>
    [Fact]
    public async Task TypeScriptSample_RunsAndDebugsInDevContainer()
    {
        if (Environment.GetEnvironmentVariable("DEVCONTAINERS_UITEST") != "1")
        {
            return;
        }

        var artifacts = Path.Combine(Environment.GetEnvironmentVariable("DEVCONTAINERS_UITEST_ARTIFACTS") ?? Path.Combine(Path.GetTempPath(), "devcontainers-uitest"), "typescript");
        Directory.CreateDirectory(artifacts);
        var folder = Path.Combine(FindRepository(), "samples", "typescript-node");
        var configFile = Assert.Single(DevContainerConfigLocator.FindFromFolder(folder));
        var runtime = await new ContainerRuntimeDetector(new ProcessRunner()).DetectAsync(ContainerRuntimePreference.Auto, null, default);
        var engine = new ContainerEngineClient(runtime, new ProcessRunner());

        using var vs = VisualStudioDriver.Launch(folder, artifacts, this.Log);
        try
        {
            vs.WaitForElement("package.json", TimeSpan.FromMinutes(3), "the folder in Solution Explorer");
            vs.Screenshot("ts-01-folder-open");

            // 1. Run npm Script in Container, script 'dev': the container starts, npm installs the packages there, and
            // the server answers on Windows' localhost:3000.
            var runStarted = DateTime.Now;
            vs.ClickMenu("Extensions", "Dev Containers", "Run npm Script in Container");
            vs.Choose("dev", TimeSpan.FromMinutes(1));
            var info = await WaitForUrlAsync("http://localhost:3000/api/info", TimeSpan.FromMinutes(20), vs);
            vs.Screenshot("ts-02-running");
            this.Log("GET /api/info: " + info);
            Assert.Contains("\"inContainer\":true", info);
            Assert.Contains("\"inspector\":null", info);

            // 2. Debug npm Script in Container: the run before is replaced by one with the Node.js inspector, whose
            // port (9229, not in forwardPorts) is detected and forwarded to Windows.
            StopLauncherWindows(runStarted);
            vs.ClickMenu("Extensions", "Dev Containers", "Debug npm Script in Container");
            vs.Choose("dev", TimeSpan.FromMinutes(1));
            var debugged = await WaitForUrlAsync("http://localhost:3000/api/info", TimeSpan.FromMinutes(5), vs, body => body.Contains("ws://0.0.0.0:9229/", StringComparison.Ordinal));
            this.Log("GET /api/info with the inspector: " + debugged);
            var targets = await WaitForUrlAsync("http://localhost:9229/json/list", TimeSpan.FromMinutes(1), vs);
            this.Log("GET localhost:9229/json/list: " + targets);
            Assert.Contains("node", targets, StringComparison.OrdinalIgnoreCase);

            // Whether Visual Studio's JavaScript debugger attached by itself: reported, not asserted yet, until the
            // attach through automation is known to work on the runner's Visual Studio (docs/end-to-end-tests.md).
            await Task.Delay(TimeSpan.FromSeconds(20));
            vs.Screenshot("ts-03-debugging");
            var fallback = vs.TryReadPrompt("could not attach by itself");
            this.Log(fallback is null
                ? "The JavaScript debugger attached (no fallback prompt)."
                : "::warning title=JavaScript debugger::The debugger did not attach by itself: " + fallback);
            if (fallback is not null)
            {
                vs.ClosePrompt();
            }
            else
            {
                vs.ClickMenu("Debug", "Stop Debugging");
            }

            // 3. Stop Container.
            StopLauncherWindows(runStarted);
            vs.ClickMenu("Extensions", "Dev Containers", "Stop Container");
            await WaitUntilAsync(
                async () => !(await engine.FindDevContainersAsync(configFile.WorkspaceFolder, configFile.ConfigPath, default)).Any(c => c.IsRunning),
                TimeSpan.FromMinutes(2),
                "the container to stop",
                vs);
        }
        catch
        {
            vs.Screenshot("ts-failure");
            vs.LogWindows();
            vs.LogConsoles();
            this.LogProcesses();
            await this.LogContainerProcessesAsync(engine, configFile);
            throw;
        }
        finally
        {
            vs.SaveOutputPane();
        }
    }

    private static async Task<string> WaitForUrlAsync(string url, TimeSpan timeout, VisualStudioDriver vs, Func<string, bool>? accept = null)
    {
        string? body = null;
        await WaitUntilAsync(
            async () =>
            {
                try
                {
                    body = await Http.GetStringAsync(url);
                    return accept is null || accept(body);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    return false;
                }
            },
            timeout,
            url + " to answer",
            vs);
        return body!;
    }

    private static async Task<string> WaitForApiAsync(TimeSpan timeout, VisualStudioDriver vs, Func<string, bool>? accept = null)
    {
        string? body = null;
        await WaitUntilAsync(
            async () =>
            {
                try
                {
                    body = await Http.GetStringAsync("http://localhost:5080/api/info");
                    return accept is null || accept(body);
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

    private static async Task<bool> IsApiAnsweringAsync()
    {
        try
        {
            await Http.GetStringAsync("http://localhost:5080/api/info");
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static async Task<(string Body, string? Thumbprint)> GetServedCertificateAsync(string url)
    {
        string? thumbprint = null;
        using var handler = new HttpClientHandler
        {
            // The certificate is checked below, by thumbprint; the CI machine does not trust it.
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                thumbprint = certificate?.Thumbprint;
                return true;
            },
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        return (await client.GetStringAsync(url), thumbprint);
    }

    private static string? HostDevCertificateThumbprint()
    {
        using var store = new System.Security.Cryptography.X509Certificates.X509Store(
            System.Security.Cryptography.X509Certificates.StoreName.My,
            System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser);
        store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
        return store.Certificates
            .Where(c => c.HasPrivateKey && c.NotAfter > DateTime.Now && c.Extensions.Any(e => e.Oid?.Value == "1.3.6.1.4.1.311.84.1.1"))
            .OrderByDescending(c => c.NotAfter)
            .FirstOrDefault()?.Thumbprint;
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

    private void LogProcesses()
    {
        foreach (var name in new[] { "powershell", "pwsh", "node", "dotnet", "podman" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        this.Log($"Process {name} {process.Id}, started {process.StartTime:HH:mm:ss}, window '{process.MainWindowTitle}'");
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // Exited meanwhile.
                    }
                }
            }
        }
    }

    private async Task LogContainerProcessesAsync(ContainerEngineClient engine, DevContainerConfigFile configFile)
    {
        try
        {
            foreach (var container in await engine.FindDevContainersAsync(configFile.WorkspaceFolder, configFile.ConfigPath, default))
            {
                var ps = await new ProcessRunner().RunAsync(
                    new ProcessSpec(engine.Runtime.ExecutablePath, ["exec", container.Id, "ps", "-eo", "pid,pgid,lstart,args"]),
                    null,
                    default);
                this.Log($"Container {container.Id} ({container.State}) processes:{Environment.NewLine}{ps.StandardOutput}{ps.StandardError}");
            }
        }
        catch (Exception ex)
        {
            this.Log($"Could not list the container's processes: {ex.Message}");
        }
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
