# hello-node: a TypeScript sample

A small TypeScript HTTP server in a dev container, for trying the TypeScript commands of the extension.

- `src/server.ts` listens on `localhost:3000`. `GET /api/info` returns the machine it runs on, whether it runs in
  a container, and the Node.js inspector URL when a debugger can attach.
- `.devcontainer/devcontainer.json` uses `mcr.microsoft.com/devcontainers/typescript-node:22` and keeps
  `node_modules` in a volume of the container.

## Try it

1. Open this folder in Visual Studio (**File > Open > Folder**).
2. **Extensions > Dev Containers > Run npm Script in Container**, script `dev`. The window shows `npm ci` the first
   time, then `Listening on http://localhost:3000`.
3. Open `http://localhost:3000/api/info` on Windows: `"inContainer": true`.
4. **Debug npm Script in Container**, script `dev`: `tsc` runs first (`predev`), then the server under the
   debugger. `"inspector"` in `/api/info` is then set.

Without Visual Studio:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .devcontainer\vs-run.ps1 -Npm dev
```

See [docs/typescript.md](../../docs/typescript.md).
