using System.Text.RegularExpressions;
using DevContainers.Core.Runtime;

namespace DevContainers.Core.Diagnostics;

/// <summary>Something the extension can do about a problem, when the user agrees.</summary>
public enum ProblemFix
{
    None,
    StartDockerDesktop,
    StartPodmanMachine,
    OpenInContainer,
    RebuildContainer,
}

/// <summary>A failure explained for the user: what went wrong, and what to do next.</summary>
/// <param name="Summary">One sentence: what is wrong.</param>
/// <param name="NextStep">What to do about it.</param>
/// <param name="Fix">What the extension can do itself, if anything.</param>
/// <param name="HelpTopic">The heading in docs/troubleshooting.md that explains it.</param>
public sealed record DevContainerProblem(string Summary, string NextStep, ProblemFix Fix = ProblemFix.None, string? HelpTopic = null)
{
    /// <summary>The label for the button that applies <see cref="Fix"/>.</summary>
    public string? FixLabel => this.Fix switch
    {
        ProblemFix.StartDockerDesktop => "Start Docker Desktop",
        ProblemFix.StartPodmanMachine => "Start the Podman machine",
        ProblemFix.OpenInContainer => "Open in Container",
        ProblemFix.RebuildContainer => "Rebuild Container",
        _ => null,
    };
}

/// <summary>
/// Turns what failed (an exception and the error output of Docker, Podman or the devcontainer CLI) into a
/// <see cref="DevContainerProblem"/> with a next step. Unknown errors get a generic next step, never a wrong one.
/// </summary>
public static class ProblemAdvisor
{
    private static readonly (Regex Pattern, DevContainerProblem Problem)[] TextProblems =
    [
        (Match(@"no space left on device|not enough space on the disk|disk quota exceeded"),
            new("The container engine ran out of disk space.", "Free space: remove unused images and build cache with 'docker system prune' (or 'podman system prune'), or give Docker Desktop's or the Podman machine's disk more room.", HelpTopic: "Out of disk space")),
        (Match(@"wsl.*(not installed|is not enabled|update|unexpected error|0x8007|0x80370102)|HCS_E_|Wsl/Service|the windows subsystem for linux"),
            new("WSL 2, which Docker Desktop and Podman run on, is not working.", "Run 'wsl --update' and then 'wsl --shutdown' in a terminal, and start the engine again. Virtualization has to be enabled in the BIOS.", HelpTopic: "WSL is not working")),
        (Match(@"port is already allocated|address already in use|bind: (an attempt was made|only one usage)"),
            new("A port the container publishes (appPort or a Compose 'ports' entry) is already in use on this computer.", "Stop the program that uses it, or change the port. Ports in forwardPorts never block the container from starting.", HelpTopic: "A port is already in use")),
        (Match(@"pull access denied|unauthorized|authentication required|denied: requested access"),
            new("The image could not be pulled: the registry refused access.", "Check the image name, and sign in to the registry with 'docker login' (or 'podman login').", HelpTopic: "The image cannot be pulled")),
        (Match(@"tls handshake timeout|dial tcp|i/o timeout|no such host|temporary failure in name resolution|connection reset by peer|net/http: request canceled|EAI_AGAIN|ENOTFOUND|ETIMEDOUT"),
            new("The container engine could not reach the network to download an image or a feature.", "Check the internet connection, a proxy or VPN, and that the engine's VM has network access. Then try again.", HelpTopic: "No network")),
        (Match(@"no such container|is not running|container .* not found|Error: no container with"),
            new("The dev container was removed or stopped outside Visual Studio.", "Open it again; the extension creates it again when it is gone.", ProblemFix.OpenInContainer, "The container was removed")),
        (Match(@"cannot find module|SyntaxError: |ERR_REQUIRE_ESM|node: bad option|Unsupported engine|requires Node\.js"),
            new("The devcontainer CLI or Node.js is broken or too old.", "Reinstall it with 'npm install -g @devcontainers/cli' (Node.js 20 or newer), or set its path under Tools > Options > Dev Containers.", HelpTopic: "The devcontainer CLI fails")),
        (Match(@"failed to (solve|build)|error building image|Dockerfile.*(error|failed)|executor failed running|exit code: \d+.*RUN "),
            new("Building the dev container image failed.", "The Output window shows the failing build step. Fix the Dockerfile or the feature, then use Rebuild Container.", ProblemFix.RebuildContainer, "The image build fails")),
        (Match(@"postCreateCommand|postStartCommand|onCreateCommand|updateContentCommand|postAttachCommand"),
            new("A lifecycle command of devcontainer.json failed.", "The Output window shows its output. Fix the command, then use Rebuild Container (onCreate and postCreate commands run only when the container is created).", ProblemFix.RebuildContainer, "A lifecycle command fails")),
    ];

    /// <summary>Explains a failure. <paramref name="details"/> is the error output, such as the session's last error.</summary>
    public static DevContainerProblem Diagnose(Exception exception, string? details = null)
    {
        if (exception is ContainerRuntimeNotFoundException engines)
        {
            return DiagnoseEngines(engines.Engines);
        }

        var text = exception.Message + "\n" + details;
        foreach (var (pattern, problem) in TextProblems)
        {
            if (pattern.IsMatch(text))
            {
                return problem;
            }
        }

        return new DevContainerProblem(
            FirstLine(details) ?? FirstLine(exception.Message) ?? "Something went wrong.",
            "The Dev Containers pane of the Output window has the full log. If the container is in a bad state, Rebuild Container creates it again.",
            HelpTopic: null);
    }

    /// <summary>Explains why no engine could be used, preferring the one that is installed.</summary>
    public static DevContainerProblem DiagnoseEngines(IReadOnlyList<EngineProblem> engines)
    {
        var installed = engines.Where(e => e.Path is not null).ToList();
        if (installed.Count == 0)
        {
            return new(
                "Neither Docker nor Podman is installed (or on PATH).",
                "Install Docker Desktop or Podman Desktop, start it, and try again. If it is installed somewhere else, set its path under Tools > Options > Dev Containers.",
                HelpTopic: "No container engine");
        }

        if (installed.Count == 1 && installed[0].Error.Contains("Windows containers", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                "Docker runs Windows containers, and dev containers are Linux containers.",
                "Right-click the Docker Desktop icon in the notification area and choose 'Switch to Linux containers', then try again.",
                HelpTopic: "Docker runs Windows containers");
        }

        // Docker first, as Automatic prefers it, unless only Podman is installed.
        var docker = installed.FirstOrDefault(e => e.Kind == ContainerRuntimeKind.Docker);
        if (docker is not null && !IsPodmanBackedDocker(docker))
        {
            return new(
                "Docker is installed but not running.",
                "Start Docker Desktop and wait until it says it is running, then try again.",
                EngineStarter.FindDockerDesktop() is not null ? ProblemFix.StartDockerDesktop : ProblemFix.None,
                "The engine is not running");
        }

        var podman = installed.First(e => e.Kind == ContainerRuntimeKind.Podman || IsPodmanBackedDocker(e));
        return new(
            "Podman is installed but its machine is not running.",
            "Start it with 'podman machine start' (or from Podman Desktop), then try again. The first time, 'podman machine init' creates it.",
            ProblemFix.StartPodmanMachine,
            "The engine is not running");
    }

    // Podman Desktop can install a docker.exe that talks to Podman.
    private static bool IsPodmanBackedDocker(EngineProblem problem) =>
        problem.Kind == ContainerRuntimeKind.Podman || problem.Error.Contains("podman", StringComparison.OrdinalIgnoreCase);

    private static Regex Match(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string? FirstLine(string? text) =>
        text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
