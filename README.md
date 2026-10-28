# Dev Containers for Visual Studio

A Visual Studio 2026 extension that brings [Dev Containers](https://containers.dev) to Visual Studio: open a
solution, and build, run and debug it inside the container described by its `devcontainer.json`, with Docker or
Podman. VS Code and JetBrains IDEs already support dev containers; this fills the gap for Visual Studio.

> [!WARNING]
> **Under development.** This project is an early prototype and not ready for production use. Features are
> incomplete, behaviour may change between versions, and things may break. Step 1 of the plan (below) is in place.

> [!NOTE]
> **Vibe coded.** This project is currently written entirely with an AI coding assistant. The code has
> automated tests but has not yet had a thorough human review. Read it before you rely on it.

## How it works

Visual Studio has no remote server like VS Code Server, so the editor stays on Windows. The workspace is mounted
into the container, and the extension runs the container, its terminal, and (next) the build and debugger
inside it.

Containers are created by the official [`@devcontainers/cli`](https://github.com/devcontainers/cli), the reference
implementation of the spec. That gives images, Dockerfiles, Docker Compose, features, lifecycle hooks and
variable substitution without reimplementing them, and containers stay compatible with the ones VS Code creates
for the same folder. [docs/devcontainer-cli-integration.md](docs/devcontainer-cli-integration.md) explains how the
extension drives the CLI.

```
src/DevContainers.Core           Config discovery, Docker/Podman detection, devcontainer CLI wrapper (net10.0, no VS dependency)
src/DevContainers.VisualStudio   The extension (VisualStudio.Extensibility, out of process)
tests/DevContainers.Core.Tests   Unit tests, plus a real-container integration test
docs/                            How the extension works
samples/dotnet-console           A small solution with a .devcontainer to try it on
```

## Using it

Requirements: Visual Studio 2026, Docker Desktop or Podman, Node.js 20+, and the devcontainer CLI. The extension
does not ship Node.js or the CLI. If they are missing when you open a container, it offers to install them for
you (Node.js with winget, the CLI with npm):

```
winget install --id OpenJS.NodeJS.LTS --exact --source winget
npm install -g @devcontainers/cli
```

Open a solution whose folder (or a parent folder up to the repository root) has `.devcontainer/devcontainer.json`,
`.devcontainer.json`, or `.devcontainer/<name>/devcontainer.json`, then use **Extensions > Dev Containers**:

| Command | What it does |
| --- | --- |
| Open in Container | Builds the container if needed and starts it. Asks which config to use when there are several. |
| Open Container Terminal | Opens a shell inside the running container, with the container's `remoteEnv` and user. |
| Rebuild Container | Removes the container and creates it again from the current config. |
| Rebuild Container Without Cache | The same, building the image with `--no-cache`. |
| Stop Container | Stops the container, and every service of a Compose based container. |

The full log is in the **Dev Containers** pane of the Output window.

Settings are under **Tools > Options > Dev Containers**: container engine (Automatic, Docker or Podman), and
paths to the engine and the CLI when they are not on PATH. Automatic uses Docker when it is running and falls
back to Podman.

## Building

```
dotnet test tests/DevContainers.Core.Tests                     # any OS
DEVCONTAINERS_INTEGRATION=1 dotnet test tests/DevContainers.Core.Tests   # also runs a real container
msbuild VsDevContainers.slnx /restore                           # Windows, produces the .vsix
```

Press F5 in Visual Studio with `DevContainers.VisualStudio` as the startup project to debug the extension in an
experimental instance. CI builds the VSIX on every pull request and runs the integration test against both
Docker and Podman.

## Plan

1. **Foundation** (this): extension skeleton, devcontainer CLI engine, Docker and Podman, open, rebuild, stop,
   terminal, output log.
2. **.NET inner loop:** build, run and debug C# inside the container, with the debugger attached through
   `docker exec` / `podman exec`.
3. **C++ inner loop:** CMake builds and gdb debugging in the container, with container headers copied locally so
   IntelliSense matches.
4. **Parity and Marketplace:** port forwarding, a `customizations.visualstudio` section, Marketplace packaging.
   The CLI stays a separate install; the extension never bundles it.
