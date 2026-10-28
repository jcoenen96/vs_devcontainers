using System.Text.Json;

namespace DevContainers.Core.Cli;

/// <summary>Turns the CLI's <c>--log-format json</c> lines into readable text for an output pane.</summary>
public static class DevContainerLogFormatter
{
    /// <summary>Returns the text to show for one log line, or null to hide it.</summary>
    public static string? Format(string line)
    {
        if (line.Length == 0 || line[0] != '{')
        {
            return line;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            var text = root.TryGetProperty("text", out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;

            switch (type)
            {
                case "raw":
                    // Output of commands run by the CLI (docker build, lifecycle hooks). Already has its own newlines.
                    return text?.TrimEnd('\r', '\n');
                case "text":
                    return string.IsNullOrWhiteSpace(text) ? null : text;
                case "start":
                    return text is null ? null : "> " + text;
                case "progress":
                    var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
                    return name is null ? null : $"[{status}] {name}";
                default:
                    // "stop" events repeat the matching "start".
                    return null;
            }
        }
        catch (JsonException)
        {
            return line;
        }
    }
}
