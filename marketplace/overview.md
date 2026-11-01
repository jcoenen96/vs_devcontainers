# Dev Containers for Visual Studio

Open, build, run, debug and test your solution inside the dev container its repository defines in
`devcontainer.json`, the same container VS Code and Codespaces use, with **Docker Desktop** or **Podman**.

> **Preview.** Versions before 1.0 are previews. Known limitations are listed in the
> [changelog](https://github.com/jcoenen96/vs_devcontainers/blob/main/CHANGELOG.md).

## What it does

- **Open in Container** starts the folder's dev container through the official devcontainer CLI: images,
  Dockerfiles, Docker Compose, features and lifecycle commands all work as in VS Code, and Visual Studio and VS Code
  share one container.
- **Run button:** a **Dev Container** launch profile builds and runs a .NET project inside the container.
- **Debugging:** **Start Debugging in Container** debugs a .NET project in the container with Visual Studio's
  debugger. Breakpoints work as usual.
- **Tests:** **Run Tests in Container** runs `dotnet test` there.
- **TypeScript and Node.js:** run and debug `package.json` scripts in the container.
- **Port forwarding:** `forwardPorts` and the ports the app opens reach `localhost` on Windows.
- **Your settings come along:** user secrets, NuGet feeds, `.npmrc`, the HTTPS development certificate and Git
  settings are copied into the container on every run.
- **Hot Reload** with `dotnet watch`, a status window, and `customizations.visualstudio` for per-repository settings.
- When something fails, it says what to do next, and offers to start the engine or rebuild the container.

## Requirements

- Visual Studio 2026 (18.0) or newer on Windows.
- Docker Desktop or Podman, running Linux containers (WSL 2).
- The devcontainer CLI and Node.js. The extension offers to install them the first time.

## Get started

[Getting started](https://github.com/jcoenen96/vs_devcontainers/blob/main/docs/getting-started.md) ·
[Troubleshooting](https://github.com/jcoenen96/vs_devcontainers/blob/main/docs/troubleshooting.md) ·
[Report a problem](https://github.com/jcoenen96/vs_devcontainers/issues)

No telemetry. Security notes, including what is copied into containers, are in
[SECURITY.md](https://github.com/jcoenen96/vs_devcontainers/blob/main/SECURITY.md).
