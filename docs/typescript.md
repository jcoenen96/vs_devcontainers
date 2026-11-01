# TypeScript and Node.js apps

A TypeScript or Node.js app runs inside the dev container with **Extensions > Dev Containers > Run npm Script in
Container**, and runs under Visual Studio's JavaScript debugger with **Debug npm Script in Container**. Both work
for any folder with a `package.json` that has scripts: a plain Node.js server, an Express or Nest API, a Vite, React,
Angular or Vue dev server. [`samples/typescript-node`](../samples/typescript-node/README.md) is a small TypeScript
server to try them on.

## Running an npm script

1. Open the solution or folder that has the dev container configuration.
2. **Extensions > Dev Containers > Run npm Script in Container**.
3. Pick the app when there are several `package.json` files (folders such as `node_modules`, `dist` and `bin` are
   skipped), then the script. `dev`, `start`, `serve` and `watch` are listed first.

A console window opens and runs the same launcher as the .NET **Dev Container** launch profile
(`vs-run.ps1`, see [run-in-dev-container.md](run-in-dev-container.md)):

1. It starts the container, or reuses the one that runs (also one VS Code started).
2. It copies your `.npmrc` from Windows into the container user's home folder, so private registries and their
   tokens work. Settings with Windows paths (`prefix`, `cache`, `script-shell`) are left out. Your NuGet.Config and
   Git settings are copied too, as for .NET.
3. It stops the previous run of the same app, so its port is free.
4. **It installs the packages in the container** when `node_modules` is missing or older than `package.json` or the
   lock file: `npm ci` with `package-lock.json`, `pnpm install --frozen-lockfile` with `pnpm-lock.yaml`,
   `yarn install --immutable` with `yarn.lock` (pnpm and Yarn through Corepack), else `npm install`.
5. It runs `npm run <script>` in the container, as the container's user, with `HOST=0.0.0.0` set (dev servers that
   read `HOST`, such as Create React App and Nuxt, then listen on every interface).

The window shows the output; closing it stops the app. The ports the app opens are forwarded to `localhost` on
Windows within a few seconds, without listing them; see [port-forwarding.md](port-forwarding.md). Open
`http://localhost:<port>` in a browser on Windows as you would without a container.

You can also start the launcher yourself, from the app's folder:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .devcontainer\vs-run.ps1 -Npm dev
```

## node_modules in a volume

Packages are installed by the container's npm, for Linux. Packages with native code (esbuild, sharp, SWC, bcrypt and
many more) installed on Windows do not run in the container, and the other way around. Put `node_modules` in a
volume of the container, so the container and Windows each keep their own:

```jsonc
"mounts": [
  "source=${localWorkspaceFolderBasename}-node_modules,target=${containerWorkspaceFolder}/node_modules,type=volume"
]
```

This is also much faster: tens of thousands of small files through a Windows bind mount make `npm install` take
minutes. The engine creates the volume owned by root; the launcher hands it to the container's user before the
first install. Without the volume, the container installs into the `node_modules` folder on Windows, which works
for packages without native code.

Name the volume after the app (`hello-node-node_modules` in the sample) when several apps of one repository have
dev containers, so they do not share one.

## Debugging

**Debug npm Script in Container** runs the script the same way, with the Node.js inspector on port 9229 of the
container, and attaches Visual Studio's JavaScript debugger to it:

1. The launcher runs the script's `pre` script first (`predev` before `dev`), without the inspector. A build step
   such as `tsc` belongs there; otherwise the debugger would attach to the compiler.
2. It then runs the script's command directly, not through `npm run` (npm is a Node.js program too and would take the
   inspector port), with `node_modules/.bin` on `PATH` and `NODE_OPTIONS=--inspect=0.0.0.0:9229`.
3. The port detection forwards 9229 to `localhost:9229` on Windows, and the extension attaches the debugger to it,
   as **Debug > Attach to Process** does with the connection type **JavaScript and TypeScript (Chrome
   DevTools/V8 Inspector)**.

The attach runs in the background, so the other commands work while the app starts. When the debugger cannot attach
by itself (that connection type is missing, or the app did not start within two minutes), the Output window says why,
and the extension says how to attach by hand: **Debug > Attach to Process**, the connection type above,
`localhost:9229` as the target. `chrome://inspect` or `edge://inspect` in a browser on Windows works too.

Turn on source maps (`"sourceMap": true` in tsconfig.json, `--enable-source-maps` for Node.js) so the debugger shows
TypeScript instead of the compiled JavaScript.

A script whose command starts several Node.js processes (a watcher that restarts the server, `concurrently`) gives
the inspector port to the first one only; the others fail to start with "address already in use". Add a script
that starts only the server, such as `"debug": "node --enable-source-maps dist/server.js"` with `"predebug": "tsc"`,
and debug that one.

## Limits for now

- **Not yet run from Visual Studio.** The launcher, the package install, the inspector and the port detection are
  checked on Linux (and in CI on every pull request); the commands and the automatic attach have not been tried in
  Visual Studio on Windows yet.
- **Breakpoints in files on Windows** need the debugger to map the container's paths (`/workspaces/<folder>/...`) to
  the Windows ones. Whether Visual Studio's attach does that by itself is not checked yet; `debugger;` statements
  and breakpoints set in the sources the debugger loaded work either way.
- **The Run button** does not run npm scripts yet: Visual Studio's JavaScript project system (`.esproj`) starts
  scripts on Windows. Use the commands above.
- Only the inspector port 9229 is used; a second app debugged at the same time needs another port.
- Node.js has to be in the container: a `typescript-node` or `javascript-node` image, or the
  `ghcr.io/devcontainers/features/node:1` feature. The launcher says so when it is missing.
