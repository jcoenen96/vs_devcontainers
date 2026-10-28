namespace DevContainers.Core.Processes;

/// <summary>Finds executables on the PATH, honouring PATHEXT on Windows.</summary>
public static class ExecutableLocator
{
    public static string? FindOnPath(string name, string? pathVariable = null, bool? isWindows = null)
    {
        var windows = isWindows ?? OperatingSystem.IsWindows();
        var path = pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var separator = windows ? ';' : ':';
        var extensions = windows && !Path.HasExtension(name)
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        foreach (var directory in path.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory.Trim('"'), name + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
