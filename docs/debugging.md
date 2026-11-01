# Debugging in the dev container

**Extensions > Dev Containers > Start Debugging in Container** builds a .NET project inside the dev container and
starts it under Visual Studio's debugger. The app and the .NET debugger both run in the container; Visual Studio
on Windows shows breakpoints, the call stack, locals and the Immediate window as usual.

## What it does

1. Asks which project to debug when the solution has several. Projects with a **Dev Container** launch profile
   are offered first.
2. Starts the dev container when it is not running, the same way as **Open in Container**, and copies the
   project's user secrets, your NuGet.Config, the HTTPS development certificate and your Git settings into it.
3. Stops a run of the same project that the Run button's **Dev Container** profile started, so its ports are free.
4. Installs the .NET debugger (`vsdbg`) into `~/.vsdbg` in the container the first time. The container needs
   `curl` or `wget` and access to `aka.ms` for that; the images from `mcr.microsoft.com/devcontainers` have both.
   The debugger stays in the container, so later sessions start right away until the container is rebuilt.
5. Builds the project in Debug inside the container, into `~/.vs-devcontainer/build` (the same output the Run
   button uses, so the two share one build).
6. Starts the app under the debugger with the settings of a launch profile: the profile the **Dev Container**
   profile runs (its `-LaunchProfile`), or else the first profile with `commandName` `Project`. Its
   `environmentVariables` and `commandLineArgs` apply, and `applicationUrl` becomes `ASPNETCORE_URLS`, as with
   `dotnet run`.

Ports in `forwardPorts` are forwarded while the container runs, so `http://localhost:5080` reaches the debugged
app from Windows. Stop it with **Debug > Stop Debugging** (Shift+F5); the container keeps running.

## How it works

Visual Studio can talk to any debugger that speaks the Debug Adapter Protocol. The extension writes a launch file
to `%TEMP%\vs-devcontainers\launch-<project>.json` that tells Visual Studio to start the debugger with
`docker exec -i` (or `podman exec -i`) and talk to it over standard input and output:

```json
{
  "adapter": "C:\\Program Files\\Docker\\Docker\\resources\\bin\\docker.exe",
  "adapterArgs": "exec -i -u vscode -w /workspaces/app/HelloApi <container> /home/vscode/.vsdbg/vsdbg --interpreter=vscode",
  "configurations": [
    {
      "type": "coreclr",
      "request": "launch",
      "program": "/home/vscode/.vs-devcontainer/build/bin/HelloApi/debug/HelloApi.dll",
      "sourceFileMap": { "/workspaces/app": "C:\\src\\app" }
    }
  ]
}
```

It then runs Visual Studio's `DebugAdapterHost.Launch` command with that file. `sourceFileMap` maps the paths in
the container back to the files on Windows, so breakpoints set in the editor bind and stepping opens the right
files. The extension runs out of process and VisualStudio.Extensibility has no API to start a debug session, so
it runs the command through Visual Studio's automation object, the way external tools do.

You can start the same session yourself from the Command Window (**View > Other Windows > Command Window**):

```
DebugAdapterHost.Launch /LaunchJson:"%TEMP%\vs-devcontainers\launch-HelloApi.json" /EngineGuid:541B8A8A-6081-4506-9F0A-1CE771DEBC04
```

## Limits

- **F5 on the Dev Container profile still runs without the debugger.** The Run button's profile starts the app
  through the launcher script, which Visual Studio cannot attach to. Use **Start Debugging in Container** to
  debug; use the Run button (Ctrl+F5) for a quick run.
- **.NET only.** Debugging TypeScript and Node.js apps comes with the TypeScript inner loop.
- **Hot Reload** and **Edit and Continue** do not work for an app in the container.
- **Attach to a running process** in the container is not offered yet; start the app under the debugger instead.
- Keep the solution open in one Visual Studio instance only; with two, the debugger starts in the first one found.
