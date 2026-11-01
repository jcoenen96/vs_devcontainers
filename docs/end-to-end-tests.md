# End-to-end tests in Visual Studio

`tests/DevContainers.VisualStudio.UITests` tests the extension the way a user uses it: in a real Visual Studio 2026,
with the extension installed from its `.vsix`, against real Linux containers. The CI workflow
[`e2e-windows.yml`](../.github/workflows/e2e-windows.yml) runs it on every pull request.

## What the test does

1. Starts Visual Studio with `samples/dotnet-webapi/HelloApi.slnx` and waits until the **Dev Container** launch
   profile shows in the toolbar. First-run dialogs (sign in, theme) are skipped.
2. **Extensions > Dev Containers > Open in Container**, then waits for the "The dev container is running" prompt.
   An error prompt fails the test with its text. Checks with the engine that exactly one dev container runs for the
   folder.
3. **Debug > Start Without Debugging**, then calls `http://localhost:5080/api/info` from Windows until it answers
   with `"inContainer":true`, and checks that the app runs in the same container.
4. Closes the launcher's console window, like stopping the app, and checks that the container keeps running.
5. **Extensions > Dev Containers > Stop Container**, and checks that the container stops.

Screenshots of every step, Visual Studio's `ActivityLog.xml`, the extension host logs and the installer logs are
uploaded as the `e2e-windows` artifact of the workflow run, also when the test fails.

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
