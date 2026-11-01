# Getting started

Dev Containers for Visual Studio opens, builds, runs, debugs and tests a .NET or TypeScript solution inside the dev
container its repository defines in `devcontainer.json`, the same container VS Code uses. This page takes you from
installing the extension to running an app in a container in about ten minutes (most of it the first image download).

## 1. What you need

- **Windows 10 or 11** with **Visual Studio 2026** (version 18.0 or newer), any edition.
- **A container engine running Linux containers:**
  - [Docker Desktop](https://www.docker.com/products/docker-desktop/) with the WSL 2 backend (check its license
    terms for your company), or
  - [Podman Desktop](https://podman-desktop.io/), or Podman with a machine (`podman machine init --now`).
- **The devcontainer CLI** and Node.js. The extension offers to install both (with winget and npm) the first time it
  needs them, so you can skip this. By hand:

  ```
  winget install --id OpenJS.NodeJS.LTS --exact --source winget
  npm install -g @devcontainers/cli
  ```

- For .NET projects, the .NET SDK on Windows for the projects' target frameworks, which IntelliSense uses
  ([details](intellisense.md)). Visual Studio's .NET workloads install one.

## 2. Install the extension

From the Visual Studio Marketplace: **Extensions > Manage Extensions**, search for **Dev Containers**, install, and
restart Visual Studio. Or download the `.vsix` from the
[GitHub releases](https://github.com/jcoenen96/vs_devcontainers/releases) and open it.

## 3. Open a repository with a dev container

Open a solution (or, for TypeScript apps, a folder) whose folder or a parent folder up to the repository root has
`.devcontainer/devcontainer.json`, `.devcontainer.json` or `.devcontainer/<name>/devcontainer.json`.

To try it, clone this repository and open `samples/dotnet-webapi/HelloApi.slnx`, or the folder
`samples/typescript-node`.

No dev container yet? A minimal one for .NET:

```jsonc
// .devcontainer/devcontainer.json
{
  "image": "mcr.microsoft.com/devcontainers/dotnet:10.0",
  "forwardPorts": [5080]
}
```

## 4. Start the container

**Extensions > Dev Containers > Open in Container**. The first time, the engine downloads the image, which takes a
few minutes; the progress shows in the status bar and in the **Dev Containers** pane of the Output window. When the
container runs, a message says where your folder is mounted and lists anything about the SDKs that needs attention.

## 5. Run, debug and test

| To | Use |
| --- | --- |
| Run a .NET project with the Run button | **Add Run Profile for Dev Container** once, commit the two files it writes, then pick the **Dev Container** profile and press **Start Without Debugging** (Ctrl+F5). See [run-in-dev-container.md](run-in-dev-container.md). |
| Debug a .NET project | **Start Debugging in Container**. Breakpoints work as usual. See [debugging.md](debugging.md). |
| Run the tests | **Run Tests in Container**. |
| Run or debug a TypeScript or Node.js app | **Run npm Script in Container** or **Debug npm Script in Container**. See [typescript.md](typescript.md). |
| Get a shell in the container | **Open Container Terminal**. |

Ports in `forwardPorts`, and the ports the app opens, are forwarded to `localhost` on Windows: open
`http://localhost:<port>` in your browser as usual ([port-forwarding.md](port-forwarding.md)). Your user secrets,
NuGet feeds, `.npmrc`, HTTPS development certificate and Git settings are copied into the container on every run.

## 6. When you are done

**Stop Container** stops it (it asks first if VS Code has it open). After changing `devcontainer.json` or the
Dockerfile, use **Rebuild Container**.

## When something fails

The message says what went wrong and what to do next, and offers a button when the extension can fix it (start the
engine, open or rebuild the container). The full log is in **View > Output > Dev Containers**.
[troubleshooting.md](troubleshooting.md) has the common problems. Report a bug at
[GitHub issues](https://github.com/jcoenen96/vs_devcontainers/issues) with that log.
