using System.Text.Json;

namespace DevContainers.Core.Configuration;

/// <summary>How the dev container is defined.</summary>
public enum DevContainerKind
{
    Unknown,
    Image,
    Dockerfile,
    DockerCompose,
}

/// <summary>
/// The parts of devcontainer.json the extension itself needs. Everything else (features, lifecycle hooks, mounts,
/// variable substitution) is handled by the devcontainer CLI, which reads the file on its own.
/// </summary>
public sealed class DevContainerConfig
{
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string? Name { get; private init; }

    public DevContainerKind Kind { get; private init; }

    public string? Image { get; private init; }

    public string? Dockerfile { get; private init; }

    public IReadOnlyList<string> DockerComposeFiles { get; private init; } = [];

    public string? Service { get; private init; }

    public string? WorkspaceFolder { get; private init; }

    public string? RemoteUser { get; private init; }

    public IReadOnlyList<string> ForwardPorts { get; private init; } = [];

    /// <summary>Ports that <c>portsAttributes</c> marks <c>"onAutoForward": "ignore"</c>: never forwarded when detected.</summary>
    public IReadOnlySet<int> IgnoredPorts { get; private init; } = new HashSet<int>();

    /// <summary>
    /// Whether ports the app opens are forwarded when detected. False when <c>otherPortsAttributes</c> says
    /// <c>"onAutoForward": "ignore"</c>.
    /// </summary>
    public bool AutoForwardPorts { get; private init; } = true;

    /// <summary>The raw <c>customizations.visualstudio</c> section, if any, for tool specific settings.</summary>
    public JsonElement? VisualStudioCustomizations { get; private init; }

    public static DevContainerConfig Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Parses devcontainer.json content, which is JSON with comments and trailing commas.</summary>
    public static DevContainerConfig Parse(string json)
    {
        using var document = JsonDocument.Parse(json, ParseOptions);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("devcontainer.json must contain a JSON object.");
        }

        var image = GetString(root, "image");
        var dockerfile = GetString(root, "dockerFile")
            ?? (root.TryGetProperty("build", out var build) && build.ValueKind == JsonValueKind.Object ? GetString(build, "dockerfile") : null);
        var composeFiles = GetStringOrArray(root, "dockerComposeFile");

        var kind = composeFiles.Count > 0 ? DevContainerKind.DockerCompose
            : dockerfile is not null ? DevContainerKind.Dockerfile
            : image is not null ? DevContainerKind.Image
            : DevContainerKind.Unknown;

        JsonElement? vsCustomizations = null;
        if (root.TryGetProperty("customizations", out var customizations)
            && customizations.ValueKind == JsonValueKind.Object
            && customizations.TryGetProperty("visualstudio", out var vs))
        {
            vsCustomizations = vs.Clone();
        }

        return new DevContainerConfig
        {
            Name = GetString(root, "name"),
            Kind = kind,
            Image = image,
            Dockerfile = dockerfile,
            DockerComposeFiles = composeFiles,
            Service = GetString(root, "service"),
            WorkspaceFolder = GetString(root, "workspaceFolder"),
            RemoteUser = GetString(root, "remoteUser"),
            ForwardPorts = GetPorts(root),
            IgnoredPorts = GetIgnoredPorts(root),
            AutoForwardPorts = !(root.TryGetProperty("otherPortsAttributes", out var others)
                && others.ValueKind == JsonValueKind.Object
                && string.Equals(GetString(others, "onAutoForward"), "ignore", StringComparison.OrdinalIgnoreCase)),
            VisualStudioCustomizations = vsCustomizations,
        };
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IReadOnlyList<string> GetStringOrArray(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return [];
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => [value.GetString()!],
            JsonValueKind.Array => value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList(),
            _ => [],
        };
    }

    private static HashSet<int> GetIgnoredPorts(JsonElement root)
    {
        var ignored = new HashSet<int>();
        if (!root.TryGetProperty("portsAttributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object)
        {
            return ignored;
        }

        foreach (var property in attributes.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object
                || !string.Equals(GetString(property.Value, "onAutoForward"), "ignore", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A key is a port ("3000") or a range ("40000-55000").
            var range = property.Name.Split('-', 2, StringSplitOptions.TrimEntries);
            if (int.TryParse(range[0], out var from) && int.TryParse(range.Length == 2 ? range[1] : range[0], out var to) && from <= to && to - from < 70000)
            {
                for (var port = from; port <= to; port++)
                {
                    ignored.Add(port);
                }
            }
        }

        return ignored;
    }

    private static IReadOnlyList<string> GetPorts(JsonElement root)
    {
        if (!root.TryGetProperty("forwardPorts", out var ports) || ports.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        // Ports are numbers ("3000") or "service:port" strings for Compose.
        return ports.EnumerateArray()
            .Select(p => p.ValueKind switch
            {
                JsonValueKind.Number => p.GetRawText(),
                JsonValueKind.String => p.GetString(),
                _ => null,
            })
            .OfType<string>()
            .ToList();
    }
}
