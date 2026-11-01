# HelloApi: a Web API in a dev container

A minimal ASP.NET Core Web API (.NET 10) for checking that an app running inside the dev container, also under the
debugger, is reachable from Windows. It listens on port 5080 (HTTP) and 5443 (HTTPS).

| Endpoint | Returns |
| --- | --- |
| `GET /` | A short text, handy as a first check in a browser. |
| `GET /api/info` | Where the API runs: OS, machine name, whether it is in a container, whether a debugger is attached, the URLs Kestrel listens on, the `HelloApi:Greeting` user secret, and the caller's address. |
| `GET /api/echo/{text}` | The text back, as JSON. |

## How it is reachable from the host

The launch profiles use the usual `localhost` URLs, as the project template makes them, and two things make
them reachable from Windows:

1. **Kestrel listens on every interface in the container.** The extension runs the profile with `localhost`
   replaced by `0.0.0.0`, so `https://localhost:5443` listens as `https://0.0.0.0:5443`, and `appPort` or another
   container can reach it too, not only the extension's port forwarding.
   `ASPNETCORE_URLS=http://0.0.0.0:5080` in `containerEnv` covers launches without a profile, such as
   `dotnet run --no-launch-profile` in the container terminal.
2. **The ports are forwarded to the host.** The extension forwards the ports of the profile that the Dev Container
   profile runs (5443 and 5080), and `forwardPorts: [5080]`, from `localhost` on Windows into the container while
   the container runs, as VS Code does. The Output window's **Dev Containers** pane says `Forwarding port 5080`
   once it does. Visual Studio and VS Code can both have the container open: whichever forwards first gets the
   port, and the other one leaves it.

## Test it

1. Make sure Docker Desktop or Podman is running, and open `samples\dotnet-webapi\HelloApi.slnx` in Visual Studio.
2. Check that **Dev Container** is selected in the list next to the Run button. It is the first profile in
   `HelloApi\Properties\launchSettings.json`, so it is selected after a fresh clone.
3. Start it with **Debug > Start Without Debugging** (Ctrl+F5). A console window opens and:
   - offers to install Node.js and the devcontainer CLI if they are missing,
   - builds and starts the dev container (the first time takes a few minutes),
   - copies the user secrets, your NuGet.Config and the HTTPS development certificate into the container,
   - runs `dotnet run --project HelloApi --launch-profile https` inside the container.

   Wait for `Now listening on: https://0.0.0.0:5443`.
4. From Windows, open <http://localhost:5080/api/info> in a browser, or open `HelloApi\HelloApi.http` in Visual
   Studio and choose **Send request**. You should see JSON with a Linux `os`, `inContainer: true` and the
   container's `machineName`. `curl.exe http://localhost:5080/api/info` from PowerShell gives the same.
   <https://localhost:5443/api/info> works too, with no certificate warning once Windows trusts the development
   certificate (`dotnet dev-certs https --trust`).
5. To see a user secret reach the container, run `dotnet user-secrets set HelloApi:Greeting "Hi"` in the
   `HelloApi` folder on Windows and run again: `greeting` in `/api/info` shows it.
6. Stop it from Visual Studio or close the window. The next run stops the previous app in the container first, so
   the port is free again.

To run on Windows instead, pick the **https** or **http** profile. [docs/run-in-dev-container.md](../../docs/run-in-dev-container.md)
explains how the profile works.

To debug it, set a breakpoint in `Program.cs` (in the `/api/info` handler) and use **Extensions > Dev Containers >
Start Debugging in Container**. The first time installs the .NET debugger in the container. Open
<http://localhost:5080/api/info>: the breakpoint is hit, and once you continue, `debuggerAttached` is `true`.
[docs/debugging.md](../../docs/debugging.md) explains how it works.

`HelloApi.Tests` has two tests that start the API in memory. **Extensions > Dev Containers > Run Tests in
Container** runs them in the container (`dotnet test HelloApi.slnx`), with the result in the Output window; Test
Explorer runs them on Windows.

## When the host cannot reach it

- **Check from inside the container first.** In the container terminal, `curl http://localhost:5080/api/info`.
  If that fails, the API is not running or listens on another port; the startup log says which.
- **Check the port is forwarded.** The **Dev Containers** pane of the Output window says `Forwarding port 5080`.
  If it says the port is in use, another program has port 5080 on Windows; VS Code's Ports view shows whether it
  is VS Code.
- **Listening on localhost only.** If the startup log says `http://localhost:5080`, the app sets its own URL
  (for example with `UseUrls` or `Kestrel:Endpoints` in appsettings.json), which the profile cannot change. Use
  `0.0.0.0` there, or leave the URL to the profile.
- **Without Visual Studio.** Only the extension and VS Code forward ports. To reach the app with just the
  devcontainer CLI, add `"appPort": [5080]`, which publishes the port when the container is created, and rebuild
  the container.
