# Troubleshooting

When a command fails, the extension says what went wrong and what to do next, and offers to fix it when it can:
start Docker Desktop or the Podman machine, open the container again, or rebuild it. The full log is always in
the **Dev Containers** pane of the Output window (**View > Output**, then pick **Dev Containers** in the list).
The Run button's launcher prints the same advice in its console window.

Each heading below is a topic the error message names.

## No container engine

Neither `docker` nor `podman` was found on `PATH`.

- Install [Docker Desktop](https://www.docker.com/products/docker-desktop/) or
  [Podman Desktop](https://podman-desktop.io/), start it, and try again.
- Installed somewhere else? Set **Tools > Options > Dev Containers > Container engine path**.
- Just installed it? Restart Visual Studio, so it sees the new `PATH`.

## The engine is not running

Docker or Podman is installed but does not answer.

- **Docker Desktop:** the extension offers **Start Docker Desktop** and waits up to 3 minutes for it. Or start it
  from the Start menu and wait until it says *Engine running*.
- **Podman:** the extension offers **Start the Podman machine**, which runs `podman machine start` (and
  `podman machine init` first when there is no machine yet). Or start it from Podman Desktop.
- **Still starting:** right after signing in to Windows, Docker Desktop and the Podman machine take a minute. The
  extension notices that and waits for them instead of failing.

## Docker runs Windows containers

Dev containers are Linux containers. Right-click the Docker Desktop icon in the notification area and choose
**Switch to Linux containers**.

## WSL is not working

Docker Desktop and Podman on Windows run their containers in WSL 2. When WSL fails, so does the engine.

```
wsl --update
wsl --shutdown
```

Then start the engine again. WSL needs virtualization enabled in the BIOS (Intel VT-x or AMD-V) and the *Virtual
Machine Platform* Windows feature.

## Out of disk space

Images and build cache fill the engine's virtual disk over time.

```
docker system prune        # or: podman system prune
docker builder prune       # build cache
```

Docker Desktop: **Settings > Resources > Disk image size**. Podman: `podman machine set --disk-size <GB>` while
the machine is stopped.

## A port is already in use

A port that the container *publishes* when it is created (`appPort`, or `ports` in a Docker Compose file) is taken
on Windows. Stop the program that uses it (`netstat -ano | findstr :<port>` shows its process id), or change the
port. Ports in `forwardPorts` never stop the container from starting; a taken one is skipped, and the Output
window says so. See [port-forwarding.md](port-forwarding.md#ports-that-are-in-use).

## The image cannot be pulled

The registry refused access. Check the image name in devcontainer.json or the Dockerfile, and sign in:
`docker login <registry>` or `podman login <registry>`.

## No network

The engine could not download an image or a feature. Check the internet connection, and a proxy or VPN: the
engine runs in its own VM and needs the proxy configured there (Docker Desktop: **Settings > Resources >
Proxies**; Podman: `HTTP_PROXY` and `HTTPS_PROXY` in the machine).

## The container was removed

The container was stopped or removed outside Visual Studio: with `docker rm`, the engine's own window, VS Code's
**Rebuild Container**, or a restart of the engine. The extension notices it on the next command and offers
**Open in Container**, which creates it again from devcontainer.json. Nothing in your folder is lost: it is
mounted, not copied.

## The devcontainer CLI fails

The devcontainer CLI or Node.js is broken or too old.

```
node --version                       # 20 or newer
npm install -g @devcontainers/cli    # reinstall
```

If the CLI is installed somewhere npm does not put it, set **Tools > Options > Dev Containers > devcontainer
CLI path** to its `devcontainer.js`.

## The image build fails

A Dockerfile step or a feature failed. The Output window shows the failing step. Fix it, then use **Rebuild
Container**; **Rebuild Container Without Cache** helps when a cached layer is stale (for example an old
`apt-get update`).

## A lifecycle command fails

`onCreateCommand`, `postCreateCommand`, `postStartCommand` and the other commands of devcontainer.json run inside
the container, and their output is in the Output window. `onCreateCommand` and `postCreateCommand` run only when
the container is created, so after fixing one use **Rebuild Container**.

## More

- **Cancel a long build:** click the progress notification at the bottom of Visual Studio and cancel it. The
  devcontainer CLI and the build it started are stopped; the next **Open in Container** starts over.
- **The Run button's window closes too fast to read:** set the environment variable
  `DEVCONTAINERS_RUN_LOG` to a file path, restart Visual Studio, and run again. The launcher writes everything
  it prints to that file.
- **Debugging does not start:** see [debugging.md](debugging.md). The Output window shows each step: installing
  the debugger, building, and starting the session.
