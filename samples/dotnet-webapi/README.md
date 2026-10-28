# HelloApi: a Web API in a dev container

A minimal ASP.NET Core Web API (.NET 10) for checking that an app running inside the dev container, also under the
debugger, is reachable from Windows. It listens on port 5080.

| Endpoint | Returns |
| --- | --- |
| `GET /` | A short text, handy as a first check in a browser. |
| `GET /api/info` | Where the API runs: OS, machine name, whether it is in a container, whether a debugger is attached, the URLs Kestrel listens on, and the caller's address. |
| `GET /api/echo/{text}` | The text back, as JSON. |

## How it is reachable from the host

Two things have to be right, and the sample sets both:

1. **Kestrel listens on every interface in the container.** By default ASP.NET Core listens on `localhost` only,
   which inside a container is the container's own loopback: a request from the host never arrives.
   `ASPNETCORE_URLS=http://0.0.0.0:5080` in `containerEnv` covers launches without a profile, and the `http`
   profile in `Properties/launchSettings.json` uses the same URL, because a launch profile's `applicationUrl`
   overrides `ASPNETCORE_URLS`.
2. **The port is published to the host.** `appPort: [5080]` makes Docker or Podman publish the port when the
   container is created (`-p 5080:5080`), so it works now, while the extension does not forward ports yet.
   `forwardPorts: [5080]` is there too: VS Code forwards it, and the extension will once port forwarding is in
   place (step 4 of the plan). In VS Code, port 5080 on the host is already taken by `appPort`, so VS Code forwards
   to the next free port; use the address it shows in the Ports view.

## Test it

1. Open `samples\dotnet-webapi\HelloApi.slnx` in Visual Studio and choose **Extensions > Dev Containers > Open in
   Container**. If you created this container before, choose **Rebuild Container** so the port is published.
2. Start the API in the container.
   - **From the container terminal** (works now): **Extensions > Dev Containers > Open Container Terminal**, then
     `dotnet run --project HelloApi`. Wait for `Now listening on: http://0.0.0.0:5080`.
   - **With F5 in the container** (once build, run and debug in the container is in place, step 2 of the plan):
     pick the `http` profile and press F5.
3. From Windows, open <http://localhost:5080/api/info> in a browser, or open `HelloApi\HelloApi.http` in Visual
   Studio and choose **Send request**. You should see JSON with a Linux `os`, `inContainer: true` and the container's
   `machineName`. With `curl.exe http://localhost:5080/api/info` from PowerShell you get the same.
4. Under the debugger, `debuggerAttached` is `true`. Set a breakpoint in the `/api/info` handler in `Program.cs`
   and send the request again from the host: the breakpoint is hit.

## When the host cannot reach it

- **Check from inside the container first.** In the container terminal, `curl http://localhost:5080/api/info`.
  If that fails, the API is not running or listens on another port; the startup log says which.
- **Check the port is published.** On the host, `docker port <container>` (or `podman port <container>`) should
  list `5080/tcp -> 0.0.0.0:5080`. If it lists nothing, the container was created before `appPort` was set:
  rebuild it.
- **Listening on localhost only.** If the startup log says `http://localhost:5080`, a launch profile or setting
  overrode the URL. Use the `http` profile, or set `ASPNETCORE_URLS` back to `http://0.0.0.0:5080`.
- **Port 5080 is in use on the host.** Change `appPort` to `["5081:5080"]`, rebuild the container and browse to
  port 5081.
