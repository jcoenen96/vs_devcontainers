using System.Text.Json;

namespace DevContainers.Core.Node;

/// <summary>A folder with a package.json that has scripts: a Node.js or TypeScript app the commands can run.</summary>
public sealed record NpmPackage(string Folder, string Name, IReadOnlyList<string> Scripts)
{
    /// <summary>The package name with its folder relative to the workspace, for a choice list.</summary>
    public string DisplayName(string workspaceFolder)
    {
        var relative = Path.GetRelativePath(workspaceFolder, this.Folder).Replace('\\', '/');
        return relative == "." ? this.Name : $"{this.Name} ({relative})";
    }

    /// <summary>The scripts with the usual ones to run first: dev, start, serve, watch, then the rest in file order.</summary>
    public IReadOnlyList<string> OrderedScripts()
    {
        string[] preferred = ["dev", "start", "serve", "watch"];
        return [.. preferred.Where(this.Scripts.Contains), .. this.Scripts.Where(s => !preferred.Contains(s))];
    }
}

public static class NpmPackages
{
    /// <summary>The Node.js inspector port the debug command uses, Node's default.</summary>
    public const int InspectPort = 9229;

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "dist", "out", "build", ".git", ".vs", ".angular", ".next",
    };

    /// <summary>
    /// The packages with scripts in <paramref name="workspaceFolder"/> and its subfolders (up to
    /// <paramref name="maxDepth"/> levels), the workspace root first, skipping dependency and build output folders.
    /// </summary>
    public static IReadOnlyList<NpmPackage> Find(string workspaceFolder, int maxDepth = 4)
    {
        var found = new List<NpmPackage>();
        Walk(workspaceFolder, 0);
        return found;

        void Walk(string folder, int depth)
        {
            if (Read(folder) is { } package)
            {
                found.Add(package);
            }

            if (depth >= maxDepth)
            {
                return;
            }

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(folder).Order(StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            foreach (var child in children)
            {
                if (!SkippedFolders.Contains(Path.GetFileName(child)))
                {
                    Walk(child, depth + 1);
                }
            }
        }
    }

    /// <summary>The package in <paramref name="folder"/>, or null when it has no package.json with scripts.</summary>
    public static NpmPackage? Read(string folder)
    {
        var path = Path.Combine(folder, "package.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var names = scripts.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).Select(p => p.Name).ToList();
            if (names.Count == 0)
            {
                return null;
            }

            var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : Path.GetFileName(folder);
            return new NpmPackage(folder, name, names);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
