using System.Text.Json;
using System.Text.Json.Nodes;
using DevContainers.Core.Cli;
using DevContainers.Core.Processes;
using DevContainers.Core.Runtime;

namespace DevContainers.Core.Debugging;

/// <summary>What the debugger starts in the container, and where.</summary>
public sealed record DebugTarget(string Program, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment, IReadOnlyList<string> Arguments);

/// <summary>The parts of a launchSettings.json profile (commandName "Project") that <c>dotnet run</c> applies.</summary>
public sealed record LaunchProfileSettings(string Name, string? ApplicationUrl, IReadOnlyDictionary<string, string> EnvironmentVariables, string? CommandLineArgs)
{
    /// <summary>
    /// Reads a profile from the project's Properties/launchSettings.json: the one named, or the first with
    /// commandName "Project". Null when there is none.
    /// </summary>
    public static LaunchProfileSettings? Read(string projectFolder, string? name)
    {
        var path = Path.Combine(projectFolder, "Properties", "launchSettings.json");
        if (!File.Exists(path))
        {
            return null;
        }

        var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (root?["profiles"] is not JsonObject profiles)
        {
            return null;
        }

        var match = profiles.FirstOrDefault(p =>
            name is not null ? p.Key == name : (p.Value?["commandName"] as JsonValue)?.ToString() == "Project");
        if (match.Value is not JsonObject profile)
        {
            return null;
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (profile["environmentVariables"] is JsonObject variables)
        {
            foreach (var (key, value) in variables)
            {
                if (value is JsonValue v)
                {
                    environment[key] = v.ToString();
                }
            }
        }

        return new LaunchProfileSettings(match.Key, (profile["applicationUrl"] as JsonValue)?.ToString(), environment, (profile["commandLineArgs"] as JsonValue)?.ToString());
    }

    /// <summary>
    /// The inner profile the "Dev Container" profile runs (its <c>-LaunchProfile "name"</c>), if it names one.
    /// </summary>
    public static string? InnerProfileName(string projectFolder)
    {
        var path = Path.Combine(projectFolder, "Properties", "launchSettings.json");
        if (!File.Exists(path))
        {
            return null;
        }

        var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var arguments = (root?["profiles"]?[Launch.DevContainerLaunchProfile.ProfileName]?["commandLineArgs"] as JsonValue)?.ToString();
        var match = arguments is null ? null : System.Text.RegularExpressions.Regex.Match(arguments, "-LaunchProfile\\s+\"([^\"]+)\"");
        return match is { Success: true } ? match.Groups[1].Value : null;
    }
}

/// <summary>
/// Prepares a .NET project for debugging inside a running dev container: installs the .NET debugger (vsdbg) in the
/// container, builds the project there, and describes the debug session for Visual Studio. Visual Studio then
/// runs vsdbg through <c>docker exec -i</c> / <c>podman exec -i</c> and talks to it over stdin and stdout.
/// </summary>
public sealed class ContainerDebugger(IProcessRunner runner, ContainerRuntime runtime, UpResult container)
{
    /// <summary>Visual Studio's engine id for vsdbg (CoreCLR debugging through the Debug Adapter Protocol).</summary>
    public const string CoreClrEngineGuid = "541B8A8A-6081-4506-9F0A-1CE771DEBC04";

    /// <summary>Same artifacts output as the Run button's launcher, so both reuse one build.</summary>
    private const string ArtifactsArguments = "--property:UseArtifactsOutput=true --property:ArtifactsPath=$HOME/.vs-devcontainer/build";

    private string User => container.RemoteUser ?? "root";

    /// <summary>The container path of a folder inside the local workspace folder.</summary>
    public static string ToContainerPath(string localWorkspace, string remoteWorkspace, string localPath)
    {
        var relative = Path.GetRelativePath(localWorkspace, localPath).Replace('\\', '/');
        return relative == "." ? remoteWorkspace : remoteWorkspace.TrimEnd('/') + "/" + relative;
    }

    /// <summary>Installs vsdbg into <c>~/.vsdbg</c> unless it is there already, and returns its path.</summary>
    public async Task<string> EnsureDebuggerAsync(Action<string> log, CancellationToken cancellationToken)
    {
        const string script =
            "set -e; d=$HOME/.vsdbg; " +
            "if [ ! -x $d/vsdbg ]; then " +
            "  echo 'Installing the .NET debugger (vsdbg) in the container'; " +
            "  if command -v curl >/dev/null 2>&1; then curl -sSL https://aka.ms/getvsdbgsh -o /tmp/getvsdbg.sh; " +
            "  elif command -v wget >/dev/null 2>&1; then wget -q https://aka.ms/getvsdbgsh -O /tmp/getvsdbg.sh; " +
            "  else echo 'The container has neither curl nor wget to download the .NET debugger with.' >&2; exit 1; fi; " +
            "  sh /tmp/getvsdbg.sh -v latest -l $d >/dev/null; rm -f /tmp/getvsdbg.sh; " +
            "fi; echo $d/vsdbg";
        var result = await this.ExecAsync(["sh", "-c", script], null, log, cancellationToken).ConfigureAwait(false);
        var lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!result.Succeeded || lines.Length == 0)
        {
            throw new InvalidOperationException("The .NET debugger could not be installed in the container: " + result.StandardError.Trim());
        }

        return lines[^1];
    }

    /// <summary>Builds the project in the container (Debug) and returns what to start.</summary>
    public async Task<DebugTarget> BuildAsync(string remoteProjectFolder, LaunchProfileSettings? profile, Action<string> log, CancellationToken cancellationToken)
    {
        log($"Building {remoteProjectFolder} in the container");
        var build = await this.ExecAsync(["sh", "-c", $"dotnet build . -c Debug {ArtifactsArguments}"], remoteProjectFolder, log, cancellationToken, stream: true).ConfigureAwait(false);
        if (!build.Succeeded)
        {
            throw new InvalidOperationException("The build in the container failed. The Output window has the build log.");
        }

        var properties = await this.ExecAsync(
            ["sh", "-c", $"dotnet msbuild . -p:Configuration=Debug {ArtifactsArguments} -getProperty:TargetPath -getProperty:RunWorkingDirectory"],
            remoteProjectFolder,
            log,
            cancellationToken).ConfigureAwait(false);
        using var json = JsonDocument.Parse(properties.StandardOutput);
        var values = json.RootElement.GetProperty("Properties");
        var program = values.GetProperty("TargetPath").GetString()!;
        var workingDirectory = values.GetProperty("RunWorkingDirectory").GetString() is { Length: > 0 } wd ? wd : remoteProjectFolder;

        // dotnet run applies the profile like this: its variables, and applicationUrl as ASPNETCORE_URLS.
        var environment = new Dictionary<string, string>(profile?.EnvironmentVariables ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        if (profile?.ApplicationUrl is { Length: > 0 } urls)
        {
            environment["ASPNETCORE_URLS"] = urls;
        }

        var arguments = profile?.CommandLineArgs is { Length: > 0 } args ? args.Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
        return new DebugTarget(program, workingDirectory, environment, arguments);
    }

    /// <summary>
    /// The launch.json for Visual Studio's <c>DebugAdapterHost.Launch</c> command: vsdbg in the container as the
    /// debug adapter, and source paths in the container mapped to the workspace folder on this computer.
    /// </summary>
    public string CreateLaunchJson(string vsdbgPath, DebugTarget target, string localWorkspace, string name)
    {
        var environment = new JsonObject();
        foreach (var (key, value) in target.Environment)
        {
            environment[key] = value;
        }

        var root = new JsonObject
        {
            ["version"] = "0.2.0",
            ["adapter"] = runtime.ExecutablePath,
            ["adapterArgs"] = string.Join(' ', new[] { "exec", "-i", "-u", this.User, "-w", Quote(target.WorkingDirectory), container.ContainerId, Quote(vsdbgPath), "--interpreter=vscode" }),
            ["configurations"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = name,
                    ["type"] = "coreclr",
                    ["request"] = "launch",
                    ["program"] = target.Program,
                    ["args"] = new JsonArray(target.Arguments.Select(a => (JsonNode)a).ToArray()),
                    ["cwd"] = target.WorkingDirectory,
                    ["env"] = environment,
                    ["stopAtEntry"] = false,
                    ["justMyCode"] = true,
                    ["sourceFileMap"] = new JsonObject { [container.RemoteWorkspaceFolder] = localWorkspace },
                },
            },
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Stops a previous run of the project that the Run button's launcher started, so its port is free.</summary>
    public Task StopLauncherRunAsync(string relativeProject, CancellationToken cancellationToken)
    {
        // Same pid file as vs-run.ps1.
        var pidFile = "/tmp/vs-run-" + System.Text.RegularExpressions.Regex.Replace(relativeProject, "[^A-Za-z0-9_.-]", "_").Trim('.') + ".pid";
        return this.ExecAsync(["sh", "-c", $"if [ -f {pidFile} ]; then kill -TERM -$(cat {pidFile}) 2>/dev/null && sleep 1; rm -f {pidFile}; fi; true"], null, _ => { }, cancellationToken);
    }

    private static string Quote(string value) => value.Any(char.IsWhiteSpace) ? "\"" + value + "\"" : value;

    private Task<ProcessResult> ExecAsync(IReadOnlyList<string> command, string? workingDirectory, Action<string> log, CancellationToken cancellationToken, bool stream = false)
    {
        var args = new List<string> { "exec", "-u", this.User };
        if (workingDirectory is not null)
        {
            args.AddRange(["-w", workingDirectory]);
        }

        args.Add(container.ContainerId);
        args.AddRange(command);
        return runner.RunAsync(new ProcessSpec(runtime.ExecutablePath, args), stream ? new LogSink(log) : new ErrorSink(log), cancellationToken);
    }

    private sealed class LogSink(Action<string> log) : IProcessOutputSink
    {
        public void OnStandardOutput(string line) => log(line);

        public void OnStandardError(string line) => log(line);
    }

    private sealed class ErrorSink(Action<string> log) : IProcessOutputSink
    {
        public void OnStandardOutput(string line)
        {
            if (line.StartsWith("Installing", StringComparison.Ordinal))
            {
                log(line);
            }
        }

        public void OnStandardError(string line) => log(line);
    }
}
