# Port forwarding

The extension forwards the ports listed in `forwardPorts` of devcontainer.json to `localhost` on Windows while the
dev container runs, the way VS Code does. The devcontainer CLI does not forward ports, so the extension does it
itself.

```jsonc
{
  "forwardPorts": [5080, "db:5432"]
}
```

- `5080` forwards `localhost:5080` on Windows to port 5080 of the dev container.
- `"db:5432"` forwards `localhost:5432` to port 5432 of the host `db` as the dev container reaches it, such as
  another service of a Docker Compose setup.

Ports the app opens are forwarded too, without listing them, the way VS Code does it: every 5 seconds the
extension reads which TCP ports something listens on in the container (`/proc/net/tcp` and `/proc/net/tcp6`) and
forwards each one to the same port on Windows. The Blazor template's `http://localhost:5049`, a Vite dev server on
5173 or the Node.js inspector on 9229 are then reachable on Windows while the app runs, and stop being forwarded
when it stops. Detected ports are marked in the Output window:

```
Forwarding port 5173 (detected) of the dev container to http://localhost:5173
```

Ports in `forwardPorts` are forwarded from the start, whether something listens on them yet or not.

### Leaving ports out

`portsAttributes` and `otherPortsAttributes` from devcontainer.json decide which detected ports are left out:

```jsonc
{
  "portsAttributes": {
    "9229": { "onAutoForward": "ignore" },          // never forward this port when it is detected
    "40000-40100": { "onAutoForward": "ignore" }    // nor this range
  },
  "otherPortsAttributes": { "onAutoForward": "ignore" } // forward only what forwardPorts lists
}
```

Other `onAutoForward` values (`notify`, `openBrowser`, `silent`) forward the port; no notification or browser is
shown yet.

## When ports are forwarded

The extension loads with every solution and checks every 5 seconds whether the solution's dev container runs. It
forwards while the container runs, whoever started it: **Open in Container**, the **Dev Container** launch profile,
or VS Code. It stops when the container stops, when another solution is opened, or when Visual Studio closes. The
**Dev Containers** pane of the Output window lists what is forwarded:

```
Forwarding port 5080 of the dev container to http://localhost:5080
```

Editing `forwardPorts` or `portsAttributes` takes effect within a few seconds, without rebuilding the container.

## How it works

For each port, the extension listens on `127.0.0.1` and `::1`. Each connection runs a small relay inside the
container through `docker exec -i` (or `podman exec -i`) that connects to the port there, and the extension copies
bytes both ways. Nothing is published when the container is created, so it works the same with Docker Desktop,
Podman machines on WSL2 and remote engines.

The relay uses the first tool the container has: `socat`, `bash` (its `/dev/tcp`), `nc`, `python3` or `node`. The
images from `mcr.microsoft.com/devcontainers` have `bash`; Alpine has BusyBox `nc`. A container with none of them
cannot be forwarded to, and the Output window says so.

The relay connects to `127.0.0.1` in the container when the app listens there or on every interface, and to
`::1` when the app listens only on IPv6 loopback, as Node.js 17 and later do for `localhost` on some images. An
app that listens only on another address of the container is reached on that address.

## Ports that are in use

A port already in use on Windows is skipped, and the Output window says why. Usually it is one of these:

- **VS Code has the same container open** and forwarded the port first. The app is reachable through VS Code's
  forwarding, so nothing is lost.
- **`appPort`** published the port when the container was created. `appPort` still works, but it is fixed at
  creation; prefer `forwardPorts`.
- **Another program** uses the port. Stop it, or change the port the app listens on.
- **The same project runs on Windows** with one of its own profiles while it also runs in the container. Whichever
  starts first has the port. Stop one of them.

The extension uses the same port number on Windows as in the container; it does not pick another free port.

## Limits

- Every new connection starts one `exec`, which adds some latency to the first request on that connection.
  Browsers keep connections open, so later requests on it are not slowed down.
- Only TCP is forwarded.
- Detection runs every 5 seconds, so a port is reachable on Windows up to 5 seconds after the app opens it.
- Ports below 1024 are detected too; an app on port 80 in the container is forwarded to port 80 on Windows,
  which needs that port to be free there.
- Labels, `requireLocalPort` and `elevateIfNeeded` from `portsAttributes` are not used.
