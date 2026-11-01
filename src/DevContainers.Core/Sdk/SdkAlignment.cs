using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevContainers.Core.Cli;
using DevContainers.Core.Processes;
using DevContainers.Core.Runtime;

namespace DevContainers.Core.Sdk;

/// <summary>How much an <see cref="SdkFinding"/> matters.</summary>
public enum SdkSeverity
{
    /// <summary>Worth knowing: the editor and the container may disagree in details.</summary>
    Info,

    /// <summary>Visual Studio on Windows (the editor, IntelliSense) cannot handle the projects as the container does.</summary>
    Warning,

    /// <summary>The container cannot build the projects.</summary>
    Error,
}

/// <summary>One difference between the .NET SDKs (or Node.js) on Windows and in the dev container.</summary>
public sealed record SdkFinding(SdkSeverity Severity, string Message);

/// <summary>A .NET SDK version as <c>dotnet --list-sdks</c> prints it: <c>10.0.100</c>, <c>9.0.304</c>, <c>10.0.100-rc.2.25502.107</c>.</summary>
public sealed record SdkVersion(int Major, int Minor, int Patch, string Text) : IComparable<SdkVersion>
{
    /// <summary>The feature band: 100 for 10.0.1xx.</summary>
    public int FeatureBand => this.Patch / 100 * 100;

    public bool IsPreview => this.Text.Contains('-');

    public static SdkVersion? TryParse(string? text)
    {
        var match = Regex.Match(text ?? string.Empty, @"^\s*(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z.\-]+)?");
        return match.Success
            ? new SdkVersion(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture), match.Value.Trim())
            : null;
    }

    public int CompareTo(SdkVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var result = (this.Major, this.Minor, this.Patch).CompareTo((other.Major, other.Minor, other.Patch));
        // A release sorts after its previews.
        return result != 0 ? result : other.IsPreview.CompareTo(this.IsPreview);
    }

    public override string ToString() => this.Text;
}

/// <summary>The <c>sdk</c> section of a global.json.</summary>
public sealed record GlobalJsonSdk(string Path, SdkVersion Version, string RollForward, bool AllowPrerelease)
{
    /// <summary>Whether <paramref name="installed"/> satisfies this global.json, following the .NET SDK's roll-forward rules.</summary>
    public bool IsSatisfiedBy(SdkVersion installed)
    {
        if (installed.IsPreview && !this.AllowPrerelease && !this.Version.IsPreview)
        {
            return false;
        }

        if (installed.CompareTo(this.Version) < 0)
        {
            return false;
        }

        var wanted = this.Version;
        return this.RollForward.ToLowerInvariant() switch
        {
            "disable" => installed.CompareTo(wanted) == 0,
            "patch" or "latestpatch" => installed.Major == wanted.Major && installed.Minor == wanted.Minor && installed.FeatureBand == wanted.FeatureBand,
            "feature" or "latestfeature" => installed.Major == wanted.Major && installed.Minor == wanted.Minor,
            "minor" or "latestminor" => installed.Major == wanted.Major,
            "major" or "latestmajor" => true,
            _ => installed.Major == wanted.Major && installed.Minor == wanted.Minor && installed.FeatureBand == wanted.FeatureBand,
        };
    }
}

/// <summary>
/// Compares the .NET SDKs on Windows, where Visual Studio's editor and IntelliSense load the projects, with the ones
/// in the dev container, where they build and run. They are separate installs, so they can differ: the container
/// may be unable to build a target framework, or the editor may show a different language version or different
/// analyzer warnings than the build.
/// </summary>
public static class SdkAlignment
{
    private static readonly Regex TargetFrameworkPattern = new(@"<TargetFrameworks?>([^<]*)</TargetFrameworks?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NetMajorPattern = new(@"^net(\d+)\.\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The SDKs in the output of <c>dotnet --list-sdks</c>.</summary>
    public static IReadOnlyList<SdkVersion> ParseSdkList(string output) =>
        output.Split('\n')
            .Select(line => SdkVersion.TryParse(line))
            .OfType<SdkVersion>()
            .Distinct()
            .Order()
            .ToList();

    /// <summary>
    /// The nearest global.json with an <c>sdk.version</c>, from <paramref name="folder"/> up to
    /// <paramref name="workspaceFolder"/> (the .NET SDK looks further up, but the container sees only the workspace).
    /// </summary>
    public static GlobalJsonSdk? FindGlobalJson(string folder, string workspaceFolder)
    {
        var root = System.IO.Path.GetFullPath(workspaceFolder).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        for (var current = System.IO.Path.GetFullPath(folder); current is not null; current = System.IO.Path.GetDirectoryName(current))
        {
            var path = System.IO.Path.Combine(current, "global.json");
            if (File.Exists(path) && Read(path) is { } sdk)
            {
                return sdk;
            }

            if (string.Equals(current.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar), root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                break;
            }
        }

        return null;

        static GlobalJsonSdk? Read(string path)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (!document.RootElement.TryGetProperty("sdk", out var sdk) || sdk.ValueKind != JsonValueKind.Object
                    || !sdk.TryGetProperty("version", out var v) || SdkVersion.TryParse(v.GetString()) is not { } version)
                {
                    return null;
                }

                var rollForward = sdk.TryGetProperty("rollForward", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : "latestPatch";
                var allowPrerelease = sdk.TryGetProperty("allowPrerelease", out var p) && p.ValueKind == JsonValueKind.True;
                return new GlobalJsonSdk(path, version, rollForward, allowPrerelease || version.IsPreview);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// The .NET major versions the projects in <paramref name="workspaceFolder"/> target (<c>net8.0</c> gives 8), read
    /// from the project files and Directory.Build.props. .NET Framework and netstandard targets are left out.
    /// </summary>
    public static IReadOnlySet<int> FindTargetMajors(string workspaceFolder, int maxDepth = 5)
    {
        var majors = new SortedSet<int>();
        Walk(workspaceFolder, 0);
        return majors;

        void Walk(string folder, int depth)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            foreach (var file in files)
            {
                var name = System.IO.Path.GetFileName(file);
                if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) || name.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase))
                {
                    AddTargets(file);
                }
            }

            if (depth >= maxDepth)
            {
                return;
            }

            foreach (var child in Directory.EnumerateDirectories(folder))
            {
                var childName = System.IO.Path.GetFileName(child);
                if (childName is not ("bin" or "obj" or "node_modules" or ".git" or ".vs" or "artifacts"))
                {
                    Walk(child, depth + 1);
                }
            }
        }

        void AddTargets(string file)
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            foreach (Match match in TargetFrameworkPattern.Matches(text))
            {
                foreach (var framework in match.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var net = NetMajorPattern.Match(framework);
                    if (net.Success && int.Parse(net.Groups[1].Value, CultureInfo.InvariantCulture) is var major and >= 5)
                    {
                        majors.Add(major);
                    }
                }
            }
        }
    }

    /// <summary>The differences that matter between the SDKs on Windows (<paramref name="host"/>) and in the container.</summary>
    /// <param name="host">The SDKs on Windows; null when the host's dotnet could not be asked.</param>
    /// <param name="container">The SDKs in the container; empty when it has none.</param>
    /// <param name="targetMajors">The .NET versions the projects target.</param>
    /// <param name="globalJson">The workspace's global.json, if any.</param>
    public static IReadOnlyList<SdkFinding> Compare(IReadOnlyList<SdkVersion>? host, IReadOnlyList<SdkVersion> container, IReadOnlySet<int> targetMajors, GlobalJsonSdk? globalJson)
    {
        var findings = new List<SdkFinding>();
        if (targetMajors.Count == 0 && globalJson is null)
        {
            return findings;
        }

        if (container.Count == 0)
        {
            findings.Add(new(SdkSeverity.Error,
                "The dev container has no .NET SDK, so the projects cannot build or run there. Use a .NET image " +
                "(mcr.microsoft.com/devcontainers/dotnet) or add the feature ghcr.io/devcontainers/features/dotnet:2 to devcontainer.json, then rebuild the container."));
            return findings;
        }

        var containerMax = container.Max()!;
        foreach (var major in targetMajors.Where(m => m > containerMax.Major))
        {
            findings.Add(new(SdkSeverity.Error,
                $"The projects target net{major}.0, but the newest .NET SDK in the dev container is {containerMax}, which cannot build it (NETSDK1045). " +
                $"Use a .NET {major} image or set the version of the dotnet feature (\"version\": \"{major}.0\") in devcontainer.json, then rebuild the container."));
        }

        if (host is not null)
        {
            if (host.Count == 0)
            {
                findings.Add(new(SdkSeverity.Warning,
                    "No .NET SDK is installed on Windows. Visual Studio's editor loads the projects with the SDK on Windows, so IntelliSense, the error list and " +
                    "Solution Explorer cannot work with them. Install the .NET SDK with the Visual Studio Installer or 'winget install Microsoft.DotNet.SDK." + (targetMajors.Count > 0 ? targetMajors.Max() : containerMax.Major) + "'."));
            }
            else
            {
                var hostMax = host.Max()!;
                foreach (var major in targetMajors.Where(m => m > hostMax.Major))
                {
                    findings.Add(new(SdkSeverity.Warning,
                        $"The projects target net{major}.0, but the newest .NET SDK on Windows is {hostMax}. The projects build in the container, but Visual Studio's editor cannot load them: " +
                        $"no IntelliSense and errors in the error list. Install the .NET {major} SDK on Windows ('winget install Microsoft.DotNet.SDK.{major}')."));
                }

                if (findings.Count == 0 && globalJson is null && (hostMax.Major, hostMax.Minor, hostMax.FeatureBand) != (containerMax.Major, containerMax.Minor, containerMax.FeatureBand))
                {
                    findings.Add(new(SdkSeverity.Info,
                        $"The editor uses .NET SDK {hostMax} on Windows and the container builds with {containerMax}. Compiler and analyzer warnings can differ between the error list and the build. " +
                        $"A global.json with \"sdk\": {{ \"version\": \"{Lower(hostMax, containerMax)}\", \"rollForward\": \"latestFeature\" }} keeps them on one feature band, if both have it."));
                }
            }
        }

        if (globalJson is not null)
        {
            var name = System.IO.Path.GetFileName(globalJson.Path);
            if (!container.Any(globalJson.IsSatisfiedBy))
            {
                findings.Add(new(SdkSeverity.Error,
                    $"{name} asks for .NET SDK {globalJson.Version} (rollForward {globalJson.RollForward}), but the dev container has {string.Join(", ", container)}. The build fails there with 'A compatible .NET SDK was not found'. " +
                    $"Install that SDK in the container (the dotnet feature's \"version\": \"{globalJson.Version}\") and rebuild it, or loosen rollForward in global.json."));
            }

            if (host is { Count: > 0 } && !host.Any(globalJson.IsSatisfiedBy))
            {
                findings.Add(new(SdkSeverity.Warning,
                    $"{name} asks for .NET SDK {globalJson.Version} (rollForward {globalJson.RollForward}), which is not installed on Windows ({string.Join(", ", host)}). Visual Studio's editor cannot load the projects. " +
                    $"Install it on Windows, or loosen rollForward in global.json."));
            }
        }

        return findings;

        static SdkVersion Lower(SdkVersion a, SdkVersion b) => a.CompareTo(b) <= 0 ? a : b;
    }

    /// <summary>Compares the major Node.js versions on Windows and in the container, for TypeScript apps.</summary>
    public static SdkFinding? CompareNode(string? hostVersion, string? containerVersion)
    {
        static int? Major(string? version) =>
            Regex.Match(version ?? string.Empty, @"v?(\d+)\.") is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;

        var (host, container) = (Major(hostVersion), Major(containerVersion));
        if (container is null || host is null || host == container)
        {
            return null;
        }

        return new(SdkSeverity.Info,
            $"Node.js is {hostVersion!.Trim()} on Windows and {containerVersion!.Trim()} in the dev container. The apps run with the container's; the editor's TypeScript service and " +
            $"tools started on Windows use Windows'. Set @types/node to the container's major version ({container}) so IntelliSense shows the APIs the app gets.");
    }

    /// <summary>
    /// Asks Windows and the container for their SDKs and compares them with what the workspace's projects need.
    /// Never throws for a failed check; returns no findings then.
    /// </summary>
    public static async Task<IReadOnlyList<SdkFinding>> CheckAsync(IProcessRunner runner, ContainerRuntime runtime, UpResult container, string workspaceFolder, CancellationToken cancellationToken)
    {
        var findings = new List<SdkFinding>();
        var targets = FindTargetMajors(workspaceFolder);
        var globalJson = FindGlobalJson(workspaceFolder, workspaceFolder);
        var hasPackageJson = File.Exists(System.IO.Path.Combine(workspaceFolder, "package.json"));
        if (targets.Count == 0 && globalJson is null && !hasPackageJson)
        {
            return findings;
        }

        if (targets.Count > 0 || globalJson is not null)
        {
            // The container's global.json view is the same files, so run the listing from the workspace's root.
            var inContainer = await RunAsync(runner, ExecSpec(runtime, container, "command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks; true"), cancellationToken).ConfigureAwait(false);
            if (inContainer is not null)
            {
                var onHost = await RunAsync(runner, new ProcessSpec("dotnet", ["--list-sdks"]), cancellationToken).ConfigureAwait(false);
                findings.AddRange(Compare(onHost is null ? [] : ParseSdkList(onHost), ParseSdkList(inContainer), targets, globalJson));
            }
        }

        if (hasPackageJson)
        {
            var containerNode = await RunAsync(runner, ExecSpec(runtime, container, "node --version 2>/dev/null; true"), cancellationToken).ConfigureAwait(false);
            var hostNode = await RunAsync(runner, new ProcessSpec("node", ["--version"]), cancellationToken).ConfigureAwait(false);
            if (CompareNode(hostNode, containerNode) is { } node)
            {
                findings.Add(node);
            }
        }

        return findings;
    }

    private static ProcessSpec ExecSpec(ContainerRuntime runtime, UpResult container, string command) =>
        new(runtime.ExecutablePath, ["exec", "-u", container.RemoteUser ?? "root", "-w", container.RemoteWorkspaceFolder, container.ContainerId, "sh", "-lc", command]);

    private static async Task<string?> RunAsync(IProcessRunner runner, ProcessSpec spec, CancellationToken cancellationToken)
    {
        try
        {
            var result = await runner.RunAsync(spec, null, cancellationToken).ConfigureAwait(false);
            return result.Succeeded ? result.StandardOutput : null;
        }
        catch (ProcessStartException)
        {
            return null;
        }
    }
}
