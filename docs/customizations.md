# customizations.visualstudio

devcontainer.json has a `customizations` section where each tool keeps its own settings: VS Code reads
`customizations.vscode`, Codespaces `customizations.codespaces`. This extension reads
`customizations.visualstudio`; other tools ignore it, so it is safe to commit next to theirs.

```jsonc
{
  "image": "mcr.microsoft.com/devcontainers/dotnet:10.0",
  "customizations": {
    "vscode": { "extensions": ["ms-dotnettools.csdevkit"] },
    "visualstudio": {
      "startupProject": "src/Api/Api.csproj",
      "launchProfile": "https",
      "hotReload": true,
      "npmScript": "dev",
      "inspectPort": 9229,
      "openOnLoad": "prompt"
    }
  }
}
```

| Setting | Type | Default | What it does |
| --- | --- | --- | --- |
| `startupProject` | string | (ask) | The project **Start Debugging in Container** and **Add Run Profile for Dev Container** use without asking: a project file path relative to the workspace folder (`src/Api/Api.csproj`), or a project name (`Api`). |
| `launchProfile` | string | the first `Project` profile | Which of the project's own launch profiles applies in the container (its environment variables, `applicationUrl`, arguments). **Add Run Profile** writes it into the **Dev Container** profile as `-LaunchProfile`. |
| `hotReload` | boolean | `false` | **Add Run Profile** writes `-Watch` into the **Dev Container** profile, so the Run button starts the app with `dotnet watch`: code changes you save in Visual Studio apply to the running app (Hot Reload), and it restarts for changes Hot Reload cannot apply. |
| `npmScript` | string | (ask) | The `package.json` script **Run npm Script** and **Debug npm Script in Container** run without asking, when the package has it. |
| `inspectPort` | number | `9229` | The Node.js inspector port for **Debug npm Script in Container**. Change it when 9229 is taken on Windows. |
| `openOnLoad` | `"never"`, `"prompt"`, `"always"` | `"never"` | When a solution loads and its dev container does not run: `"prompt"` asks whether to open it (like VS Code's *Reopen in Container*), `"always"` opens it. Once per Visual Studio session. |

Unknown settings and wrong types are reported in the **Dev Containers** pane of the Output window when the
container opens, and otherwise ignored.

After changing `launchProfile` or `hotReload`, run **Add Run Profile for Dev Container** again to update the
profile. The other settings apply the next time a command runs.

## Hot Reload

With `hotReload` (or `-Watch` added to the profile's `commandLineArgs` by hand), the launcher runs
`dotnet watch run` in the container instead of `dotnet build` and `dotnet run`, with the launch profile's settings as
before. Changes saved on Windows reach the container without file system events through WSL 2 and Podman machine
mounts, so `dotnet watch` polls for them (`DOTNET_USE_POLLING_FILE_WATCHER=1`); a change shows up within a second
or two. Visual Studio's own **Hot Reload** button applies to apps it runs on Windows and does not reach the
container; use `dotnet watch` there.

Node.js watchers (Vite, webpack, nodemon and others built on chokidar or watchpack) poll too: the npm commands set
`CHOKIDAR_USEPOLLING` and `WATCHPACK_POLLING`.
