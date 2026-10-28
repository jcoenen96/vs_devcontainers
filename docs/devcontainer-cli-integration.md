# How the extension uses the devcontainer CLI

The extension does not implement the [Dev Containers specification](https://containers.dev/implementors/spec/)
itself. It drives the reference implementation, [`@devcontainers/cli`](https://github.com/devcontainers/cli),
which is the same engine VS Code's Dev Containers extension is built on. This page explains how the two connect.

## Why the CLI

- **Full spec support for free.** Images, Dockerfiles, Docker Compose, features, lifecycle hooks
  (`onCreateCommand`, `postCreateCommand`, `postStartCommand`, ...), `remoteEnv`, `remoteUser`, mounts and
  variable substitution are all handled by the CLI.
- **Compatible containers.** The CLI labels containers the same way for every tool, so a container created by
  VS Code for a folder is found and reused by Visual Studio, and the other way round.
- **Docker and Podman.** The CLI talks to the engine through its command line, so pointing it at `podman`
  instead of `docker` is enough.

What the CLI does not offer (finding, stopping and removing containers) the extension does with the engine's
own CLI. See [Stopping and removing](#stopping-and-removing).

## Layers

```
Visual Studio (devenv.exe)
  │  commands, Output window, prompts, settings   (VisualStudio.Extensibility RPC)
  ▼
DevContainers.VisualStudio                         out-of-process extension host, .NET 10
  │  Services/DevContainerService.cs
  ▼
DevContainers.Core                                 plain .NET library, no Visual Studio dependency
  │  DevContainerSession → DevContainerCli / ContainerEngineClient → ProcessRunner
  ▼
node devcontainer.js up|exec ...    docker|podman ps|stop|rm|inspect ...
  │                                   │
  └──────────────► Docker or Podman ◄─┘
```

`DevContainers.Core` holds all of the CLI logic, so it is unit tested on any OS and integration tested against
real containers in CI.

## Step by step: Open in Container

1. **Find the configuration** (`Configuration/DevContainerConfigLocator.cs`). Starting from the solution folder
   (or a folder the user picks when no solution is open), the locator looks for the spec's locations:
   `.devcontainer/devcontainer.json`, `.devcontainer.json`, and `.devcontainer/<name>/devcontainer.json`. If
   none is found it moves to the parent folder, stopping at the repository root (the folder with `.git`). The
   folder where the config was found becomes the **workspace folder**. With several configs, the user picks one.

2. **Pick the container engine** (`Runtime/ContainerRuntime.cs`). The setting is Automatic, Docker or Podman.
   For each candidate the detector finds the executable (setting or `PATH`) and runs
   `<engine> version --format {{.Server.Version}}`. That fails when Docker Desktop or the Podman machine is not
   running, so Automatic uses Docker when it answers and falls back to Podman. For Podman, `podman-compose` is
   used for Compose when it is on `PATH`.

3. **Find the CLI** (`Cli/DevContainerCliLocator.cs`). In order: the path from the settings, `devcontainer` on
   `PATH`, then the global npm folder (`%APPDATA%\npm\node_modules\@devcontainers\cli`). On Windows the CLI is
   always started as `node <path>\devcontainer.js` and never through npm's `devcontainer.cmd` shim, because a
   `.cmd` file runs through `cmd.exe`, which re-parses arguments and mangles paths with spaces or special
   characters.

4. **Run `devcontainer up`** (`Cli/DevContainerCli.cs`):

   ```
   node devcontainer.js up
     --workspace-folder <workspace folder>
     --config <path to devcontainer.json>
     --log-format json
     --docker-path <docker.exe or podman.exe>
     [--docker-compose-path <podman-compose>]
     --id-label devcontainer.local_folder=<folder> --id-label devcontainer.config_file=<config>
     [--remove-existing-container]   # Rebuild
     [--build-no-cache]              # Rebuild Without Cache
   ```

   The `--id-label` values are the labels of the folder's existing container, also one VS Code created; see
   [Sharing the container with VS Code](#sharing-the-container-with-vs-code). If that container exists, `up`
   starts it if needed and reuses it. Otherwise it builds
   the image, applies features, creates the container, mounts the workspace folder (by default at
   `/workspaces/<folder name>`) and runs the lifecycle hooks.

5. **Stream the log.** With `--log-format json`, the CLI writes one JSON object per line to **stderr**.
   `DevContainerLogFormatter` turns them into readable lines for the **Dev Containers** Output pane and the
   progress bar:

   | CLI event | Shown as |
   | --- | --- |
   | `{"type":"start","text":"Run: docker build ..."}` | `> Run: docker build ...` |
   | `{"type":"raw","text":"Step 1/5 ...\n"}` | the text as is (build and hook output) |
   | `{"type":"text","text":"..."}` | the text, if not empty |
   | `{"type":"progress","name":"Running postCreateCommand...","status":"succeeded"}` | `[succeeded] Running postCreateCommand...` |
   | `{"type":"stop", ...}` | hidden (it repeats the matching `start`) |

6. **Read the result.** When it finishes, the CLI prints one JSON object to **stdout**:

   ```json
   {"outcome":"success","containerId":"7630ca86cf27...","remoteUser":"vscode","remoteWorkspaceFolder":"/workspaces/app"}
   ```

   or, on failure:

   ```json
   {"outcome":"error","message":"Dev container config (...) not found.","description":"..."}
   ```

   The last JSON line on stdout is parsed into `UpResult`, or into a `DevContainerCliException` whose message
   and description are shown to the user. If there is no JSON at all (Node crashed, wrong path), stderr is
   shown instead.

Cancelling from the Visual Studio progress bar kills the CLI process and all of its children.

## Running commands in the container

`devcontainer exec` runs a command as the container's `remoteUser`, in the workspace folder, with `remoteEnv`
and the user's login environment applied:

```
node devcontainer.js exec --workspace-folder <folder> --config <config> --docker-path <engine> --id-label ... <command...>
```

The **Dev Container** launch profile uses `exec` to run `dotnet run` in the container when you press Run; see
[run-in-dev-container.md](run-in-dev-container.md).

**Open Container Terminal** uses this to start an interactive shell in a new console window, preferring bash:
`/bin/sh -c "if command -v bash ...; then exec bash -l; else exec sh -l; fi"`.

## Sharing the container with VS Code

Visual Studio and VS Code use one container per folder, not one each. Two containers for the same folder would
cost twice the memory, and the IDEs would fight over the ports in `appPort`.

The devcontainer CLI finds "the" container of a folder by two labels, and puts them on the containers it creates:

| Label | Value |
| --- | --- |
| `devcontainer.local_folder` | the workspace folder on the host |
| `devcontainer.config_file` | the path of devcontainer.json |

The CLI compares them as exact strings, but tools spell the same path differently. VS Code writes Windows paths
with a lowercase drive letter (`c:\repos\app`), while Visual Studio and PowerShell use `C:\repos\app`. Left alone,
the CLI would not see VS Code's container and would create a second one. So before `up`:

1. `ContainerEngineClient.FindDevContainersAsync` lists every dev container
   (`<engine> ps --all --filter label=devcontainer.local_folder`) and reads their labels with one
   `<engine> inspect`. It compares them as paths (`DevContainerIdentity.SamePath`): on Windows without case and
   with either separator, and never with a trailing separator. Running containers come first.
2. If one matches, its exact label values go to `up` and `exec` as `--id-label`, so the CLI uses that container.
   **Open in Container** logs `Reusing dev container <id> (running)`.
3. Otherwise new labels are written the way VS Code writes them (`DevContainerIdentity.ForNewContainer`, lowercase
   drive letter), so VS Code later reuses the container Visual Studio created.

The Run button's launcher script does the same lookup in PowerShell, with
`<engine> inspect --format '{{.State.Running}} {{json .Config.Labels}}'`.

Nothing in Visual Studio stops the container behind VS Code's back:

- Stopping the app (the Run button's stop) stops only the app's process group in the container; the container keeps
  running. See [run-in-dev-container.md](run-in-dev-container.md).
- **Stop Container** and **Rebuild Container** first check whether VS Code has the container open, and ask before
  going on. VS Code runs its server inside the container while a window is attached, so the check is
  `<engine> exec <id> sh -c "grep -l -s '[v]scode-server' /proc/[0-9]*/cmdline | head -n 1"` (the brackets keep
  grep from matching itself).

## Stopping and removing

The CLI has no `stop` or `down` command, so the extension finds the container by its labels, as described in
[Sharing the container with VS Code](#sharing-the-container-with-vs-code), and uses the engine's CLI.

For Compose based configs, the other services (a database, for example) must stop too. The same `inspect` call
reads the `com.docker.compose.project` label, and the client then includes every container with
`label=com.docker.compose.project=<project>`. It uses `inspect` rather than a `ps` label template because Docker
and Podman format `ps` labels differently, while both expose the same `.Config.Labels` map.

**Stop Container** runs `<engine> stop` on the running ones, which keeps the container so the next open is fast.
Removal (`<engine> rm --force`) is used for cleanup. Rebuilding goes through `up --remove-existing-container`.

## State and concurrency

`DevContainerSession` (one per devcontainer.json) owns the state: `NotStarted → Starting → Running`,
`Stopping → Stopped`, or `Failed` with the error text. A semaphore makes operations on one session run one at a
time, so clicking **Open in Container** twice while the image builds does not start a second build. The engine
is detected again on every operation, so starting Docker Desktop or switching to Podman takes effect without
restarting Visual Studio.

## Requirements and settings

| Requirement | Why |
| --- | --- |
| Node.js 20+ | The CLI is a Node.js program. |
| `@devcontainers/cli` from npm | The CLI itself. CI pins 0.90.0. |
| Docker Desktop or Podman, running | The CLI and the extension call the engine's CLI. |

The extension does not ship the CLI or Node.js. It relies on the user's installation, so they stay up to date
with the user's own tools.

### Installing missing prerequisites

Before a container is opened or rebuilt, `PrerequisiteInstaller` (extension) asks `PrerequisiteChecker` (Core)
whether Node.js, the CLI and winget can be found. When something is missing, the user sees which tools are
missing and the exact commands that would install them, with **Install now** and **Cancel**:

```
winget install --id OpenJS.NodeJS.LTS --exact --source winget --accept-source-agreements --accept-package-agreements
npm install -g @devcontainers/cli
```

Node.js comes from winget. The CLI is published to npm only (there is no winget package for it), so it is
installed with the npm that comes with Node.js. Only the missing steps are included.

**Install now** runs the commands in a visible PowerShell window (`InstallPlan.ToPowerShellScript`, passed with
`-EncodedCommand`), so the user sees the progress and any Windows permission prompt. The script reloads `PATH`
from the registry after each step, because the window does not otherwise see the Node.js that winget just
installed. Visual Studio waits for the window to close, checks again, and continues opening the container.

Visual Studio keeps the `PATH` it was started with. To find tools installed afterwards without a restart,
`ExecutableLocator` on Windows searches the process `PATH` plus the machine and user `PATH` from the registry.

If winget itself is missing (no App Installer), the prompt explains how to install Node.js from nodejs.org and
lists the commands to run by hand.

Settings under **Tools > Options > Dev Containers**: container engine (Automatic, Docker or Podman), container
engine path, and devcontainer CLI path (to `devcontainer.js`).

## Testing the integration

- Unit tests (`tests/DevContainers.Core.Tests`) cover the CLI arguments, result parsing, log formatting, CLI and
  engine discovery, label matching and Compose stop, using a fake process runner.
- `IntegrationTests` runs a real container through open, exec, reopen and stop, and checks that a container
  created with differently spelled labels (as VS Code would) is reused and detected as open in VS Code, when
  `DEVCONTAINERS_INTEGRATION=1` is set. `DEVCONTAINERS_RUNTIME=docker|podman` picks the engine. CI runs it
  against both.

## Known gaps

- Port forwarding (`forwardPorts`) is parsed but not applied yet.
- `customizations.visualstudio` is read but no settings are defined for it yet.
- The prerequisite check runs when a container is started from the Dev Containers menu. Starting the
  application (F5) inside the container comes with step 2 of the plan and will run the same check.
