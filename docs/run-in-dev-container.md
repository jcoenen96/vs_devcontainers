# Running a project in its dev container with the Run button

A project gets a **Dev Container** launch profile. With that profile selected, the Run button in Visual Studio
builds and runs the project inside the dev container instead of on Windows. The profile lives in the project's
`Properties/launchSettings.json` and runs a small launcher script, `.devcontainer/vs-run.ps1`. Both are committed
with the project, so the profile works for everyone right after cloning.

## Adding the profile to a project

**Extensions > Dev Containers > Add Run Profile for Dev Container**, then pick the project if the solution has
several. The command:

1. writes `.devcontainer/vs-run.ps1` into the folder that holds the dev container configuration,
2. adds the **Dev Container** profile to `Properties/launchSettings.json` (creating the file if needed), as the
   **first** profile: Visual Studio selects the first profile when nobody picked one yet, so after cloning the Run
   button starts the app in the container,
3. keeps every other profile, so you can still run on Windows by picking another profile.

Running the command again updates the script and the profile. Commit both files.

The two samples in this repository already have the profile:
[`samples/dotnet-webapi`](../samples/dotnet-webapi/README.md) and `samples/dotnet-console`.

## The profile

For the sample API it looks like this:

```json
"Dev Container": {
  "commandName": "Executable",
  "executablePath": "powershell.exe",
  "commandLineArgs": "-NoProfile -ExecutionPolicy Bypass -File \"$(ProjectDir)..\\.devcontainer\\vs-run.ps1\" -LaunchProfile \"http\"",
  "workingDirectory": "$(ProjectDir)"
}
```

- `commandName: Executable` makes Visual Studio start a program instead of the project itself.
- `$(ProjectDir)` is expanded by Visual Studio to the project folder, so the paths work wherever the repository
  is cloned.
- `-LaunchProfile "http"` names a regular profile of the project (`commandName: Project`) that `dotnet run` uses
  **inside** the container, for its URL and environment variables. The command picks the first such profile;
  without one, `dotnet run` runs without a profile.
- `-Config` is added when the configuration is not `.devcontainer/devcontainer.json`, for example
  `.devcontainer/<name>/devcontainer.json`.

`dotnet run` on the host ignores `Executable` profiles, so it keeps using the project's own profiles.

## What the launcher script does

`vs-run.ps1` runs in a console window that Visual Studio opens. It works with Windows PowerShell 5.1 (built into
Windows) and PowerShell 7.

1. **Prerequisites.** If the devcontainer CLI is not found, it lists the commands that install it (Node.js LTS with
   winget, the CLI with npm) and asks `Install them now? [Y/n]`. After installing it reloads `PATH`, so no restart
   is needed.
2. **Container engine.** Docker when it is running, otherwise Podman. Set the environment variable
   `DEVCONTAINERS_RUNTIME` to `docker` or `podman` to choose.
3. **Start the container** with `devcontainer up`, the same as **Open in Container**. It first looks for the
   folder's existing container, including one VS Code created, and reuses it (`Reusing the existing dev container
   of this folder`). Only when there is none does it build the image and create the container. The build log
   shows in the window.
4. **Stop the previous run.** Stopping the app in Visual Studio closes the window on Windows, but the app inside
   the container keeps running and would hold its port. Each run records its process group in
   `/tmp/vs-run-<project>.pid` inside the container; the next run stops that group first.
5. **Run the project** inside the container with `devcontainer exec`, as the container's user and with its
   environment: `dotnet run --project <project> --launch-profile <profile>`. The output shows in the window, and
   the window's exit code is the app's. The build output goes to `~/.vs-devcontainer/build` inside the container
   (MSBuild's artifacts output), not to `obj/` and `bin/` in your folder: Windows bind mounts (Podman, and Docker
   through WSL) do not let the container's user set file times there, which fails the build with MSB3374, and the
   host's Visual Studio builds use those folders too.

## Stopping the app keeps the container

Stopping the app in Visual Studio stops only the app. The container keeps running, so:

- the next run starts in seconds, without starting the container again,
- a VS Code window that has the same container open keeps working. VS Code and Visual Studio share one container
  for the folder; the launcher never creates a second one and never stops it. How the container is found is
  explained in [devcontainer-cli-integration.md](devcontainer-cli-integration.md#sharing-the-container-with-vs-code).

To stop the container itself, use **Extensions > Dev Containers > Stop Container**. It asks first when VS Code has
the container open.

## Reaching the app from the host

The app runs inside the container, so two settings decide whether Windows can reach it. The
[sample API](../samples/dotnet-webapi/README.md) has both:

- **The app listens on every interface in the container** (`http://0.0.0.0:5080`), not on `localhost`, which
  inside the container is the container's own loopback. The `http` profile's `applicationUrl` sets it.
- **The port is published** with `appPort` in devcontainer.json. The devcontainer CLI publishes it on the host's
  `127.0.0.1` when the container is created, so `http://localhost:5080` on Windows reaches the app. Rebuild the
  container after changing `appPort`.

## Limits for now

- **No breakpoints yet.** The profile runs the app in the container, but the Visual Studio debugger is not
  attached to it. Use **Debug > Start Without Debugging** (Ctrl+F5). Debugging inside the container is the next
  part of step 2.
- **.NET projects only.** The script runs `dotnet run`. TypeScript apps come with step 3 of the plan.
- **Ports from `forwardPorts` are not forwarded yet**; use `appPort`.

## Tested

On Linux with PowerShell 7, against Docker 29 and Podman 4.9: the sample API started in the container with the
profile's exact command line and answered `GET /api/info` from the host on port 5080, with `inContainer: true`.
Stopping the launcher and running again replaced the old app instead of failing on the port. With a container
created beforehand under VS Code style labels (a differently spelled folder path), the launcher reused that
container and left it running after the app stopped. Running it from
Visual Studio on Windows has not been tested yet.
