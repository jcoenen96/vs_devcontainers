# Performance: where the files live

Visual Studio edits the files on Windows, and the container sees them through a bind mount. With Docker Desktop and
Podman, the container runs in a WSL 2 virtual machine, so every file access from the container crosses from Linux
to the Windows file system. That is the slow part. Builds, `npm install` and file watching are the operations that
touch many files. This page explains what the extension already does about it, and what you can do.

VS Code's *Clone Repository in Container Volume* avoids the crossing by keeping the files only in the container.
That does not fit Visual Studio, whose editor, design-time builds and Git tools run on Windows and need the files
there, so the extension does not offer it.

## What the extension does already

| Hot spot | What happens |
| --- | --- |
| `obj/` and `bin/` | Builds in the container write to `~/.vs-devcontainer/build` inside the container (MSBuild's artifacts output), not to the mounted folder. Only the sources are read through the mount. |
| `node_modules` | Recommended in a volume of the container (see [typescript.md](typescript.md#node_modules-in-a-volume)): tens of thousands of small files stay on Linux. |
| File watching | `dotnet watch` and Node.js watchers poll instead of waiting for file events, which do not cross the mount. |
| NuGet packages | Restored into the container user's `~/.nuget/packages`, inside the container. Mount a volume on it to keep them across rebuilds: `"source=nuget-packages,target=/home/vscode/.nuget/packages,type=volume"`. |

## What you can do

- **Exclude the folder from Windows Defender's real-time scan** (or use a Dev Drive with Defender in performance
  mode). Defender scans every file the container reads through the mount, and that often costs more than the mount.
- **Give the engine's VM enough memory and CPUs.** Docker Desktop: *Settings > Resources* (with WSL 2, in
  `%UserProfile%\.wslconfig`). Podman: `podman machine set --cpus 4 --memory 8192`.
- **Keep generated folders out of the mount** with volumes, as above for `node_modules` and NuGet packages; the same
  works for other caches (`.angular/cache`, `.next`).
- **Reuse the container.** Stopping the app keeps the container running, and the next run starts in seconds. Only
  **Rebuild Container** after changing the image, the Dockerfile or features.

## Repositories inside WSL

You can also keep the repository in the WSL distro's own file system (`\\wsl.localhost\Ubuntu\home\you\repo`)
instead of on a Windows drive. Then the container reads the files without crossing to Windows, which is what
Docker's documentation recommends for speed. Visual Studio opens solutions and folders from that path, but its
editor, design-time builds and Git tools now cross the other way, so Visual Studio itself gets slower.

| Engine | With the repository in WSL |
| --- | --- |
| Docker Desktop (WSL 2 backend) | Docker Desktop can bind-mount paths inside a WSL distro that it integrates with (*Settings > Resources > WSL integration*). |
| Podman | The Podman machine is its own WSL distro and does not see the files of other distros. Keep the repository on a Windows drive, or inside the Podman machine. |

**Not yet verified:** the extension and the launcher have not been tested with a workspace on a `\\wsl.localhost`
path. The devcontainer CLI has to pass that path to the engine as a mount source, and the extension compares
folder paths to find the container. Please report what you see in the
[issues](https://github.com/jcoenen96/vs_devcontainers/issues).

When it is worth it: large repositories with heavy builds in the container, where you mostly edit and only build in
the container. For most solutions, the defaults above with a Defender exclusion are faster overall.
