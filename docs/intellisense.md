# IntelliSense and the SDKs on both sides

Visual Studio's editor runs on Windows. IntelliSense, the error list, Solution Explorer and refactorings load the
projects with the **.NET SDK on Windows**, and restore their packages there (into `obj/` of the project, as always).
The dev container builds, runs, tests and debugs them with **its own SDK**. The two are separate installs, so:

| When | What happens | What to do |
| --- | --- | --- |
| The container's SDK is older than a project's target framework | The build in the container fails with NETSDK1045. | Use a .NET image of that version, or set the dotnet feature's `version`, then rebuild the container. |
| The SDK on Windows is older than a project's target framework | The projects build in the container, but the editor cannot load them: no IntelliSense, errors in the error list. | Install that SDK on Windows (`winget install Microsoft.DotNet.SDK.10`, or the Visual Studio Installer). |
| A global.json asks for an SDK one side does not have | That side fails with *A compatible .NET SDK was not found*. | Install it there, or loosen `rollForward` in global.json. |
| Both have the target's SDK, but different feature bands (10.0.1xx and 10.0.2xx) | Everything works. Compiler and analyzer warnings can differ a little between the error list and the build. | Pin one feature band in global.json (below), if you want them identical. |

**Open in Container** compares both sides every time it opens the container: the target frameworks of the
projects in the folder, the global.json, and `dotnet --list-sdks` on Windows and in the container. It lists what
does not match in its message and in the **Dev Containers** pane of the Output window:

```
Warning: The projects target net10.0, but the newest .NET SDK on Windows is 9.0.304. The projects build in the
container, but Visual Studio's editor cannot load them: no IntelliSense and errors in the error list. Install
the .NET 10 SDK on Windows ('winget install Microsoft.DotNet.SDK.10').
```

The Run button's launcher checks the container side before it builds, and says what to change instead of leaving
you with NETSDK1045 alone.

## Keeping both sides on one SDK

A global.json at the root of the repository keeps both on the same feature band:

```json
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestFeature"
  }
}
```

- `latestPatch` (the default) wants the same feature band (10.0.1xx); `latestFeature` the same major and minor
  (10.0.x); `latestMajor` anything at least as new.
- Install the same SDK in the container with the dotnet feature: `"ghcr.io/devcontainers/features/dotnet:2":
  { "version": "10.0.100" }`.

## Packages

Both sides restore the same `PackageReference`s from the same feeds: the launcher copies your NuGet.Config and its
credentials into the container (see [run-in-dev-container.md](run-in-dev-container.md#what-is-copied-from-windows)).
They do not share the restore output. The container builds into `~/.vs-devcontainer/build` inside the container
(MSBuild's artifacts output), and Windows keeps `obj/` and `bin/` in the project folder, so neither side's restore
or build breaks the other's IntelliSense. A package that exists for only one operating system (a Windows-only
native package) can restore on Windows and fail in the container, or the other way around.

## What Windows still needs

- **.NET projects:** the .NET SDK for the projects' target frameworks, for the editor. Visual Studio's .NET desktop
  or ASP.NET workload installs a current one. Nothing else: the build, the runtime, the debugger (vsdbg) and the
  tests run in the container.
- **TypeScript apps:** nothing for running and debugging. The editor's TypeScript service can use the `typescript`
  package in `node_modules` when there is one on Windows, else the TypeScript that comes with Visual Studio. With
  `node_modules` in a volume of the container (see [typescript.md](typescript.md#node_modules-in-a-volume)), Windows
  has no `node_modules`: run `npm install` on Windows too if you want the editor to see the packages' types, which
  installs Windows' own copy next to the container's. When Node.js on Windows and in the container have different
  major versions, the extension notes it; keep `@types/node` at the container's version.
- **The devcontainer CLI** and its Node.js, which the extension offers to install.
