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
for the same folder. Visual Studio and VS Code share that one container: Visual Studio reuses the container VS Code
created (and the other way round), and stopping the app never stops the container.
[docs/devcontainer-cli-integration.md](docs/devcontainer-cli-integration.md) explains how the
extension drives the CLI.

```
src/DevContainers.Core           Config discovery, Docker/Podman detection, devcontainer CLI wrapper (net10.0, no VS dependency)
src/DevContainers.VisualStudio   The extension (VisualStudio.Extensibility, out of process)
tests/DevContainers.Core.Tests   Unit tests, plus a real-container integration test
tests/DevContainers.VisualStudio.UITests   Drives a real Visual Studio end to end (docs/end-to-end-tests.md)
docs/                            How the extension works
samples/dotnet-console           A small solution with a .devcontainer to try it on
samples/dotnet-webapi            A Web API with a .devcontainer, to test reaching the app in the container from the host
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
| Open in Container | Starts the folder's container, reusing the one VS Code uses if there is one, and builds it only when there is none. Asks which config to use when there are several. |
| Open Container Terminal | Opens a shell inside the running container, with the container's `remoteEnv` and user. |
| Add Run Profile for Dev Container | Adds a **Dev Container** launch profile to a project, so the Run button builds and runs it inside the container. See [docs/run-in-dev-container.md](docs/run-in-dev-container.md). |
| Rebuild Container | Removes the container and creates it again from the current config. Asks first when VS Code has it open. |
| Rebuild Container Without Cache | The same, building the image with `--no-cache`. |
| Stop Container | Stops the container, and every service of a Compose based container. Asks first when VS Code has it open. |

The full log is in the **Dev Containers** pane of the Output window.

Settings are under **Tools > Options > Dev Containers**: container engine (Automatic, Docker or Podman), and
paths to the engine and the CLI when they are not on PATH. Automatic uses Docker when it is running and falls
back to Podman.

## Build and install it yourself

The extension is not on the Marketplace yet, so you build the `.vsix` (or download it from CI) and install it.

### What you need

- Visual Studio 2026 with the **Visual Studio extension development** workload (Visual Studio Installer >
  Modify > Workloads). It provides the tools that package the `.vsix` and the experimental instance for F5.
- The .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`) if it is not already installed with Visual Studio.
- Docker Desktop or Podman. Node.js and the devcontainer CLI are offered for installation the first time you open
  a container (see [Using it](#using-it)).

### 1. Get the .vsix

**Download it:** every pull request and push to `main` runs CI, which uploads the extension as the
`DevContainers.VisualStudio.vsix` artifact. Open the run under the repository's **Actions** tab, download the
artifact and unzip it.

**Or build it:**

1. Clone the repository: `git clone https://github.com/jcoenen96/vs_devcontainers.git`
2. Open `VsDevContainers.slnx` in Visual Studio 2026, pick the **Release** configuration and choose
   **Build > Build Solution**. Or, from a **Developer PowerShell for VS 2026** in the repository folder:

   ```
   msbuild VsDevContainers.slnx /restore /p:Configuration=Release
   ```

3. The extension is at `src\DevContainers.VisualStudio\bin\Release\net10.0-windows\DevContainers.VisualStudio.vsix`.

### 2. Install it

1. Close every Visual Studio window.
2. Double-click `DevContainers.VisualStudio.vsix`. The VSIX Installer opens; select Visual Studio 2026 and choose
   **Install**.
3. Start Visual Studio. **Extensions > Manage Extensions > Installed** now lists **Dev Containers**, and the
   **Extensions** menu has a **Dev Containers** submenu.

To update, install a newer `.vsix` the same way. To remove it, use **Manage Extensions > Installed > Dev
Containers > Uninstall** and restart Visual Studio.

### 3. Try it

1. Make sure Docker Desktop or Podman is running.
2. Open `samples\dotnet-console\HelloContainer.slnx` from the repository.
3. Choose **Extensions > Dev Containers > Open in Container**. If Node.js or the devcontainer CLI is missing,
   choose **Install now** and wait for the window to close.
4. Follow the build in **View > Output**, "Show output from: Dev Containers". When the container is running,
   **Extensions > Dev Containers > Open Container Terminal** gives you a shell inside it. Try `dotnet run
   --project HelloContainer`: it prints the container's Linux version.
5. Use the Run button: open `samples\dotnet-webapi\HelloApi.slnx`, keep the **Dev Container** profile selected and
   press Ctrl+F5. The Web API builds and runs inside the container; open <http://localhost:5080/api/info> on
   Windows to call it. [Its README](samples/dotnet-webapi/README.md) has the details.

### Debugging the extension

Open `VsDevContainers.slnx`, set `DevContainers.VisualStudio` as the startup project and press **F5**. A second
Visual Studio instance (the experimental instance) starts with the extension loaded, without installing it into
your normal Visual Studio. Breakpoints in the extension and in `DevContainers.Core` are hit there.

### Running the tests

```
dotnet test tests/DevContainers.Core.Tests                                 # any OS
DEVCONTAINERS_INTEGRATION=1 dotnet test tests/DevContainers.Core.Tests     # also runs a real container
```

The integration test needs Docker or Podman and the devcontainer CLI. Set `DEVCONTAINERS_RUNTIME=podman` to run
it against Podman. On Windows PowerShell, set the variables with `$env:DEVCONTAINERS_INTEGRATION = "1"` first.
CI runs the tests against both Docker and Podman and builds the VSIX on every pull request.

## Plan

1. **Foundation** (done): extension skeleton, devcontainer CLI engine, Docker and Podman, open, rebuild, stop,
   terminal, output log.
2. **.NET inner loop:** the Run button builds and runs the project inside the container through a launch profile
   (done). Next: attach the debugger to the app in the container.
3. **C++ inner loop:** CMake builds and gdb debugging in the container, with container headers copied locally so
   IntelliSense matches.
4. **Parity and Marketplace:** port forwarding, a `customizations.visualstudio` section, Marketplace packaging.
   The CLI stays a separate install; the extension never bundles it.
