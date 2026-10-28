namespace DevContainers.Core.Configuration;

/// <summary>A devcontainer.json file and the workspace folder it belongs to.</summary>
public sealed record DevContainerConfigFile(string WorkspaceFolder, string ConfigPath)
{
    /// <summary>Short label for pickers, relative to the workspace folder.</summary>
    public string DisplayName => Path.GetRelativePath(WorkspaceFolder, ConfigPath).Replace('\\', '/');
}

/// <summary>
/// Finds dev container configurations the same way the Dev Containers specification and VS Code do:
/// <c>.devcontainer/devcontainer.json</c>, <c>.devcontainer.json</c>, and
/// <c>.devcontainer/&lt;name&gt;/devcontainer.json</c> one level deep.
/// </summary>
public static class DevContainerConfigLocator
{
    private const string FolderName = ".devcontainer";
    private const string FileName = "devcontainer.json";

    /// <summary>Returns every configuration in <paramref name="workspaceFolder"/>, most conventional first.</summary>
    public static IReadOnlyList<DevContainerConfigFile> FindInFolder(string workspaceFolder)
    {
        var results = new List<DevContainerConfigFile>();
        var folder = Path.GetFullPath(workspaceFolder);

        var primary = Path.Combine(folder, FolderName, FileName);
        if (File.Exists(primary))
        {
            results.Add(new DevContainerConfigFile(folder, primary));
        }

        var rootFile = Path.Combine(folder, "." + FileName);
        if (File.Exists(rootFile))
        {
            results.Add(new DevContainerConfigFile(folder, rootFile));
        }

        var devcontainerFolder = Path.Combine(folder, FolderName);
        if (Directory.Exists(devcontainerFolder))
        {
            foreach (var sub in Directory.EnumerateDirectories(devcontainerFolder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var nested = Path.Combine(sub, FileName);
                if (File.Exists(nested))
                {
                    results.Add(new DevContainerConfigFile(folder, nested));
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Looks in <paramref name="startFolder"/> and then its parents, stopping after the folder that holds the
    /// repository root (<c>.git</c>). A solution often lives below the repository root where
    /// <c>.devcontainer</c> sits.
    /// </summary>
    public static IReadOnlyList<DevContainerConfigFile> FindFromFolder(string startFolder)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(startFolder)); current is not null; current = current.Parent)
        {
            var found = FindInFolder(current.FullName);
            if (found.Count > 0)
            {
                return found;
            }

            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git")))
            {
                break;
            }
        }

        return [];
    }
}
