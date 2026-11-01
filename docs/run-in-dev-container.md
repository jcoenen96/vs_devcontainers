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
  "commandLineArgs": "-NoProfile -ExecutionPolicy Bypass -File \"$(ProjectDir)..\\.devcontainer\\vs-run.ps1\" -LaunchProfile \"https\"",
  "workingDirectory": "$(ProjectDir)"
}
```

- `commandName: Executable` makes Visual Studio start a program instead of the project itself.
- `$(ProjectDir)` is expanded by Visual Studio to the project folder, so the paths work wherever the repository
  is cloned.
- `-LaunchProfile "https"` names a regular profile of the project (`commandName: Project`) that `dotnet run` uses
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
4. **Copy what the app needs from Windows** into the container user's home folder (details below): the
   project's user secrets, your NuGet.Config, the HTTPS development certificate and your Git settings.
5. **Stop the previous run.** Stopping the app in Visual Studio closes the window on Windows, but the app inside
   the container keeps running and would hold its port. Each run records its process group in
   `/tmp/vs-run-<project>.pid` inside the container; the next run stops that group first.
6. **Run the project** inside the container with `devcontainer exec`, as the container's user and with its
   environment: `dotnet run --project <project> --launch-profile <profile>`. The output shows in the window, and
   the window's exit code is the app's. The build output goes to `~/.vs-devcontainer/build` inside the container
   (MSBuild's artifacts output), not to `obj/` and `bin/` in your folder: Windows bind mounts (Podman, and Docker
   through WSL) do not let the container's user set file times there, which fails the build with MSB3374, and the
   host's Visual Studio builds use those folders too.

## What is copied from Windows

The app should run in the container as it does on Windows, so the launcher copies these in on every run, and
**Open in Container** does the same (without user secrets, which belong to a project). They are copied, not
mounted, so the container keeps working when Windows changes them, and the next run copies the latest version.

| What | From (Windows) | To (container) | Notes |
| --- | --- | --- | --- |
| User secrets | `%APPDATA%\Microsoft\UserSecrets\<UserSecretsId>\secrets.json` | `~/.microsoft/usersecrets/<UserSecretsId>/secrets.json` | Only for a project with a `UserSecretsId`. Secrets set inside the container are replaced on the next run; set them on Windows. |
| NuGet feeds and credentials | `%APPDATA%\NuGet\NuGet.Config` | `~/.nuget/NuGet/NuGet.Config` | Local and network folder feeds and Windows paths are left out. Passwords that Windows encrypted cannot be read on Linux and are left out with a message; use `ClearTextPassword` with a personal access token, or a credential provider in the image. |
| HTTPS development certificate | The certificate `dotnet dev-certs https --trust` made, in `Cert:\CurrentUser\My` | The container user's certificate store, with `dotnet dev-certs https --clean --import` | Kestrel serves HTTPS with the certificate Windows already trusts, so browsers on Windows accept `https://localhost:<port>` without a warning. Imported only when the container does not have it yet. |

| Git settings | `git config --global` on Windows, and `core.autocrlf` as this repository uses it | `git config --global` in the container | `user.name`, `user.email`, aliases, `init.defaultBranch`, `pull.rebase`, `push.autoSetupRemote`. Credential helpers, editors and paths are Windows specific and are not copied. `safe.directory` is set, because Git otherwise refuses a folder mounted from Windows ("dubious ownership"). |

Each file is readable by the container user only (`chmod 600`). Nothing is written into your project folder.

### Git inside the container

Visual Studio's own Git tools run on Windows, against the same files, with your Windows credentials: commit,
pull and push from Visual Studio as usual. Git in the container is there for the terminal and for builds that
read Git (versioning tools, Source Link). It can read and commit, and it sees the same line endings as Git on
Windows, because `core.autocrlf` is copied. Pushing from the container terminal needs credentials in the
container, which are not forwarded from Windows; push from Visual Studio, or set up a token in the container.

Shell scripts that run in the container must have LF line endings. With `core.autocrlf=true` Git checks them out
with CRLF on Windows, and Linux then fails with `/bin/sh^M: bad interpreter`. Add this to `.gitattributes`:

```
*.sh text eol=lf
```

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
- **The port is forwarded** with `forwardPorts` in devcontainer.json. While the container runs, the extension
  listens on `localhost` on Windows and relays each connection into the container, so `http://localhost:5080`
  reaches the app. See [port-forwarding.md](port-forwarding.md).

## Limits for now

- **No breakpoints yet.** The profile runs the app in the container, but the Visual Studio debugger is not
  attached to it. Use **Debug > Start Without Debugging** (Ctrl+F5). Debugging inside the container is the next
  part of step 2.
- **.NET projects only.** The script runs `dotnet run`. TypeScript apps come with step 3 of the plan.

## Tested

On Linux with PowerShell 7, against Docker 29 and Podman 4.9: the sample API started in the container with the
profile's exact command line and answered `GET /api/info` from the host on port 5080, with `inContainer: true`.
Stopping the launcher and running again replaced the old app instead of failing on the port. With a container
created beforehand under VS Code style labels (a differently spelled folder path), the launcher reused that
container and left it running after the app stopped. Running it from
Visual Studio on Windows has not been tested yet.
