namespace DevContainers.Core.Processes;

/// <summary>Finds executables on the PATH, honouring PATHEXT on Windows.</summary>
public static class ExecutableLocator
{
    public static string? FindOnPath(string name, string? pathVariable = null, bool? isWindows = null)
    {
        var windows = isWindows ?? OperatingSystem.IsWindows();
        var path = pathVariable ?? CurrentPath(windows);
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

    /// <summary>
    /// The process PATH plus, on Windows, the machine and user PATH from the registry. Visual Studio keeps the PATH
    /// it started with, so this is what lets tools installed afterwards (by winget, for example) be found without
    /// restarting it.
    /// </summary>
    private static string CurrentPath(bool windows)
    {
        var process = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (!windows)
        {
            return process;
        }

        return string.Join(
            ';',
            process,
            Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine),
            Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User));
    }
}
