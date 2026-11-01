# End-to-end tests in Visual Studio

`tests/DevContainers.VisualStudio.UITests` tests the extension the way a user uses it: in a real Visual Studio 2026,
with the extension installed from its `.vsix`, against real Linux containers. The CI workflow
[`e2e-windows.yml`](../.github/workflows/e2e-windows.yml) runs it on every pull request against Podman, and every
night (and on demand, **Actions > End-to-end (Visual Studio on Windows) > Run workflow**) against both Podman and Docker Desktop.

## What the tests do

### The sample Web API

1. Starts Visual Studio with `samples/dotnet-webapi/HelloApi.slnx` and waits until the **Dev Container** launch
   profile shows in the toolbar. First-run dialogs (sign in, theme) are skipped.
2. **Extensions > Dev Containers > Open in Container**, then waits for the "The dev container is running" prompt.
   An error prompt fails the test with its text. Checks with the engine that exactly one dev container runs for the
   folder.
3. **Debug > Start Without Debugging**, then calls `http://localhost:5080/api/info` from Windows, through the
   extension's port forwarding, until it answers with `"inContainer":true`, and checks that the app runs in the
   same container.
4. Closes the launcher's console window, like stopping the app, and checks that the container keeps running.
5. **Extensions > Dev Containers > Start Debugging in Container**, then checks that `/api/info` answers with
   `"debuggerAttached":true` (the app runs under Visual Studio's debugger in the container), and stops debugging.
   Starting debugging stops the run from step 3 itself, which still answers after its window closed.
6. **Extensions > Dev Containers > Run Tests in Container**, and waits for "All tests passed in the dev container"
   (the sample's xUnit tests, built and run in the container).
7. **Extensions > Dev Containers > Stop Container**, and checks that the container stops.

### The TypeScript sample

1. Starts Visual Studio with the folder `samples/typescript-node` (**Open Folder**).
2. **Extensions > Dev Containers > Run npm Script in Container**, picks `dev`, and calls
   `http://localhost:3000/api/info` from Windows until it answers with `"inContainer":true`: the container started,
   npm installed the packages in it, and port 3000 is forwarded.
3. **Debug npm Script in Container**, picks `dev`, and checks that the app now runs with the Node.js inspector and
   that Windows reaches the inspector on `localhost:9229`, a port the app opened that is not in `forwardPorts`
   (port detection).
4. Whether Visual Studio's JavaScript debugger attached by itself is **reported, not asserted** yet: the run log
   says so, and a fallback prompt becomes a warning annotation on the run. It turns into an assertion once the
   attach through Visual Studio's automation is known to work on the runner's Visual Studio.
5. **Stop Container**.

Screenshots of every step, Visual Studio's `ActivityLog.xml`, the extension host logs and the installer logs are
uploaded as the `e2e-windows-<engine>` artifact of the workflow run, also when a test fails.

## How Visual Studio is driven

Playwright drives browsers, not desktop apps, so the test uses Windows UI Automation through
[FlaUI](https://github.com/FlaUI/FlaUI): it finds menu items, prompts and buttons by name, the same accessibility
tree screen readers use. `VisualStudioDriver` wraps it: menus, waiting for prompts, dismissing first-run dialogs,
screenshots.

## The CI machine

- **Runner:** `windows-2025-vs2026`, GitHub's Windows Server 2025 image with Visual Studio Enterprise 2026 and the
  extension development workload.
- **Containers:** Docker on GitHub's Windows runners only runs Windows containers, which cannot run dev containers.
  The workflow installs Podman from its latest release and starts a Podman machine, which runs Linux containers in
  WSL2. WSL2 needs nested virtualization, which GitHub says is "technically possible" on its runners but not
  officially supported, so this step may break when GitHub changes its machines. The extension's **Automatic**
  engine setting skips the runner's Docker because it runs Windows containers, and picks Podman.
- **Docker Desktop (nightly):** installed from Docker's download with `--quiet --accept-license --backend=wsl-2`,
  started, and switched to its Linux engine (`docker context use desktop-linux`), since the runner's own Docker
  runs Windows containers. Same nested virtualization caveat as Podman. Docker Desktop's license allows this for
  open source projects and small companies; check it for yours before copying the job.
- **Tools:** Node.js and `@devcontainers/cli` (pinned like the other jobs), installed before the test so the
  prerequisite prompt does not show.

## Running it yourself

On a Windows machine with Visual Studio 2026, the extension installed, the devcontainer CLI, and Docker Desktop
(Linux containers) or Podman running. Close Visual Studio first; the test starts its own instance.

```
$env:DEVCONTAINERS_UITEST = "1"
dotnet test tests/DevContainers.VisualStudio.UITests
```

Do not use the mouse or keyboard while it runs. `DEVCONTAINERS_UITEST_ARTIFACTS` sets the folder for screenshots and
logs (default `%TEMP%\devcontainers-uitest`). `VS_DEVENV` points at a specific `devenv.exe`.
