# Changelog

All notable changes to Dev Containers for Visual Studio. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/):
until 1.0, a minor version can change behavior.

## [Unreleased]

## [0.9.0] - Unreleased

The first public preview.

### Added

- **Open, rebuild and stop** the dev container of a solution from **Extensions > Dev Containers**, with Docker or
  Podman, through the devcontainer CLI. Shares the container with VS Code, and asks before stopping or rebuilding
  one VS Code has open.
- **Run button:** **Add Run Profile for Dev Container** gives a .NET project a **Dev Container** launch profile that
  builds and runs it in the container, with the project's own launch profile settings.
- **Debugging:** **Start Debugging in Container** builds the project in the container and debugs it there with
  Visual Studio's debugger (vsdbg through exec).
- **Tests:** **Run Tests in Container** runs `dotnet test` in the container.
- **TypeScript and Node.js:** **Run npm Script in Container** and **Debug npm Script in Container**, which installs
  the packages with the container's npm, pnpm or Yarn and attaches Visual Studio's JavaScript debugger.
- **Port forwarding** of `forwardPorts` and of the ports the app opens (detected), honoring `portsAttributes`
  `onAutoForward: ignore`. No ports are published, so it works the same with every engine.
- **Settings from Windows** copied into the container on every run: user secrets, NuGet.Config and its feeds,
  `.npmrc`, the HTTPS development certificate Windows trusts, and Git name, email and line ending settings.
- **SDK check:** Open in Container compares the .NET SDKs on Windows (IntelliSense) and in the container with the
  projects' target frameworks and global.json, and says what to install.
- **Error handling:** failures say what went wrong and what to do, with buttons to start Docker Desktop or the
  Podman machine, open or rebuild the container, and a troubleshooting guide.
- Every .NET project type `dotnet run` starts: Web API, Blazor, worker, console, and C#, F# and Visual Basic.

### Known limitations

- Visual Studio's JavaScript debugger attach and breakpoint path mapping are not yet verified on every setup;
  see [docs/typescript.md](docs/typescript.md#limits-for-now).
- Aspire app hosts are not supported yet.
- Requires Visual Studio 2026 (18.0) or newer, Windows, and Linux containers.

[Unreleased]: https://github.com/jcoenen96/vs_devcontainers/compare/v0.9.0...HEAD
[0.9.0]: https://github.com/jcoenen96/vs_devcontainers/releases/tag/v0.9.0
