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

## When ports are forwarded

The extension loads with every solution and checks every 5 seconds whether the solution's dev container runs. It
forwards while the container runs, whoever started it: **Open in Container**, the **Dev Container** launch profile,
or VS Code. It stops when the container stops, when another solution is opened, or when Visual Studio closes. The
**Dev Containers** pane of the Output window lists what is forwarded:

```
Forwarding port 5080 of the dev container to http://localhost:5080
```

Editing `forwardPorts` takes effect within a few seconds, without rebuilding the container.

## How it works

For each port, the extension listens on `127.0.0.1` and `::1`. Each connection runs a small relay inside the
container through `docker exec -i` (or `podman exec -i`) that connects to the port there, and the extension copies
bytes both ways. Nothing is published when the container is created, so it works the same with Docker Desktop,
Podman machines on WSL2 and remote engines.

The relay uses the first tool the container has: `socat`, `bash` (its `/dev/tcp`), `nc`, `python3` or `node`. The
images from `mcr.microsoft.com/devcontainers` have `bash`; Alpine has BusyBox `nc`. A container with none of them
cannot be forwarded to, and the Output window says so.

`localhost` in the container means `127.0.0.1`, so the app has to listen there or on every interface
(`0.0.0.0`). An app that listens only on `::1` in the container is not reached.

## Ports that are in use

A port already in use on Windows is skipped, and the Output window says why. Usually it is one of these:

- **VS Code has the same container open** and forwarded the port first. The app is reachable through VS Code's
  forwarding, so nothing is lost.
- **`appPort`** published the port when the container was created. `appPort` still works, but it is fixed at
  creation; prefer `forwardPorts`.
- **Another program** uses the port. Stop it, or change the port the app listens on.

The extension uses the same port number on Windows as in the container; it does not pick another free port.

## Limits

- Every new connection starts one `exec`, which adds some latency to the first request on that connection.
  Browsers keep connections open, so later requests on it are not slowed down.
- Only TCP is forwarded.
- `portsAttributes` (labels, `onAutoForward`) and `otherPortsAttributes` are not used yet, and ports the app opens
  that are not listed in `forwardPorts` are not detected automatically.
