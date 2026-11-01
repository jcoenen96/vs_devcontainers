using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DevContainers.Core.Runtime;

namespace DevContainers.Core.Forwarding;

/// <summary>
/// A <c>forwardPorts</c> entry: a port of the dev container itself ("3000"), or of another host it can reach
/// ("db:5432", a Compose service).
/// </summary>
public sealed record PortSpec(string Host, int Port)
{
    public static bool TryParse(string value, out PortSpec spec)
    {
        spec = null!;
        var text = value.Trim();
        var colon = text.LastIndexOf(':');
        var host = colon > 0 ? text[..colon] : "localhost";
        if (colon == 0 || !int.TryParse(colon > 0 ? text[(colon + 1)..] : text, out var port) || port is < 1 or > 65535)
        {
            return false;
        }

        spec = new PortSpec(host, port);
        return true;
    }

    public override string ToString() => this.Host == "localhost" ? this.Port.ToString() : $"{this.Host}:{this.Port}";
}

/// <summary>How a <see cref="PortSpec"/> ended up after <see cref="PortForwarder.Start"/>.</summary>
public sealed record ForwardedPort(PortSpec Spec, bool IsForwarded, string? Problem);

/// <summary>
/// Forwards ports of a running dev container to <c>localhost</c> on this computer while the container runs, as VS
/// Code does for <c>forwardPorts</c> (the devcontainer CLI does not). Every connection runs a small relay inside the
/// container through <c>docker exec</c> / <c>podman exec</c>, so nothing has to be published when the container is
/// created and it works with Docker Desktop, Podman machines and remote engines alike.
/// </summary>
public sealed class PortForwarder(ContainerRuntime runtime, string containerId, Action<string> log) : IDisposable
{
    /// <summary>
    /// Connects stdin and stdout to host:port, with the first tool the container has: socat, bash (its /dev/tcp),
    /// nc (BusyBox on Alpine), python3 or node. Arguments: $1 = host, $2 = port.
    /// </summary>
    internal const string RelayScript =
        "h=\"$1\"; p=\"$2\"; " +
        "if command -v socat >/dev/null 2>&1; then exec socat - \"TCP:$h:$p\"; fi; " +
        "if command -v bash >/dev/null 2>&1; then exec bash -c 'exec 3<>\"/dev/tcp/$0/$1\" || exit 1; cat <&3 & cat >&3; kill $! 2>/dev/null' \"$h\" \"$p\"; fi; " +
        "if command -v nc >/dev/null 2>&1; then exec nc \"$h\" \"$p\"; fi; " +
        "if command -v python3 >/dev/null 2>&1; then exec python3 -c 'import socket,sys,threading\ns=socket.create_connection((sys.argv[1],int(sys.argv[2])))\ndef up():\n  while True:\n    d=sys.stdin.buffer.read1(65536)\n    if not d: break\n    s.sendall(d)\n  s.shutdown(socket.SHUT_WR)\nthreading.Thread(target=up,daemon=True).start()\nwhile True:\n  d=s.recv(65536)\n  if not d: break\n  sys.stdout.buffer.write(d); sys.stdout.buffer.flush()' \"$h\" \"$p\"; fi; " +
        "if command -v node >/dev/null 2>&1; then exec node -e 'const s=require(\"net\").connect(+process.argv[2],process.argv[1]);process.stdin.pipe(s);s.pipe(process.stdout);s.on(\"error\",()=>process.exit(1))' \"$h\" \"$p\"; fi; " +
        "echo 'No socat, bash, nc, python3 or node in the container to forward the port with.' >&2; exit 127";

    private readonly Dictionary<PortSpec, List<TcpListener>> listeners = [];
    private readonly CancellationTokenSource stopping = new();
    private IReadOnlyList<ContainerListener> containerListeners = [];

    public string ContainerId => containerId;

    /// <summary>The ports being forwarded now.</summary>
    public IReadOnlyCollection<PortSpec> Forwarded
    {
        get
        {
            lock (this.listeners)
            {
                return this.listeners.Keys.ToList();
            }
        }
    }

    /// <summary>
    /// What listens in the container (from <see cref="ContainerPortScanner"/>), so a <c>localhost</c> port is
    /// relayed to the address its program listens on: <c>127.0.0.1</c>, or <c>::1</c> for one that listens only there.
    /// </summary>
    public void SetContainerListeners(IReadOnlyList<ContainerListener> listeners) => this.containerListeners = listeners;

    /// <summary>
    /// Forwards exactly <paramref name="ports"/>: starts the new ones and stops the ones no longer wanted, leaving
    /// the others (and their open connections) alone. Returns how each newly started port ended up.
    /// </summary>
    public IReadOnlyList<ForwardedPort> Update(IEnumerable<PortSpec> ports)
    {
        var wanted = ports.ToHashSet();
        lock (this.listeners)
        {
            foreach (var spec in this.listeners.Keys.Where(k => !wanted.Contains(k)).ToList())
            {
                this.listeners[spec].ForEach(l => l.Stop());
                this.listeners.Remove(spec);
            }

            wanted.ExceptWith(this.listeners.Keys);
        }

        return this.Start(wanted);
    }

    /// <summary>
    /// Starts listening on <c>localhost</c> for each port. A port that is already in use on this computer is
    /// skipped: it is usually published by <c>appPort</c> or forwarded by VS Code, so the app is reachable already.
    /// </summary>
    public IReadOnlyList<ForwardedPort> Start(IEnumerable<PortSpec> ports)
    {
        var result = new List<ForwardedPort>();
        foreach (var spec in ports)
        {
            var started = new List<TcpListener>();
            string? problem = null;

            // Browsers try ::1 first for "localhost"; listen on both loopbacks where the machine has them.
            foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
            {
                if (address.AddressFamily == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6)
                {
                    continue;
                }

                var listener = new TcpListener(address, spec.Port);
                try
                {
                    listener.Start();
                    started.Add(listener);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    problem = $"port {spec.Port} is already in use on this computer";
                    break;
                }
                catch (SocketException) when (address.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    // No IPv6 loopback on this machine; IPv4 is enough.
                }
            }

            if (problem is not null || started.Count == 0)
            {
                started.ForEach(l => l.Stop());
                result.Add(new ForwardedPort(spec, false, problem ?? $"could not listen on port {spec.Port}"));
                continue;
            }

            lock (this.listeners)
            {
                this.listeners[spec] = started;
            }

            foreach (var listener in started)
            {
                _ = this.AcceptLoopAsync(listener, spec);
            }

            result.Add(new ForwardedPort(spec, true, null));
        }

        return result;
    }

    public void Dispose()
    {
        this.stopping.Cancel();
        lock (this.listeners)
        {
            foreach (var started in this.listeners.Values)
            {
                started.ForEach(l => l.Stop());
            }

            this.listeners.Clear();
        }

        this.stopping.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener listener, PortSpec spec)
    {
        while (!this.stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(this.stopping.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = this.RelayAsync(client, spec);
        }
    }

    private async Task RelayAsync(TcpClient client, PortSpec spec)
    {
        using (client)
        {
            var startInfo = new ProcessStartInfo(runtime.ExecutablePath)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            // "localhost" in the container may resolve to ::1 first, where servers that bind 0.0.0.0 do not listen, so
            // connect to the address the program listens on: 127.0.0.1, or ::1 for one that listens only there.
            var host = spec.Host == "localhost" ? ContainerPortScanner.ConnectAddress(this.containerListeners, spec.Port) ?? "127.0.0.1" : spec.Host;
            foreach (var arg in new[] { "exec", "-i", containerId, "sh", "-c", RelayScript, "sh", host, spec.Port.ToString() })
            {
                startInfo.ArgumentList.Add(arg);
            }

            Process process;
            try
            {
                process = Process.Start(startInfo) ?? throw new InvalidOperationException("The process did not start.");
            }
            catch (Exception ex)
            {
                log($"Port {spec}: could not start the relay: {ex.Message}");
                return;
            }

            using (process)
            {
                var network = client.GetStream();
                var stderr = process.StandardError.ReadToEndAsync();

                var upstream = Task.Run(async () =>
                {
                    try
                    {
                        await network.CopyToAsync(process.StandardInput.BaseStream, this.stopping.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                    {
                        // The browser or the relay went away.
                    }
                    finally
                    {
                        // Tells the relay the client is done sending (it then finishes the response and exits).
                        try
                        {
                            process.StandardInput.Close();
                        }
                        catch (IOException)
                        {
                        }
                    }
                });

                try
                {
                    await process.StandardOutput.BaseStream.CopyToAsync(network, this.stopping.Token).ConfigureAwait(false);
                    client.Client.Shutdown(SocketShutdown.Send);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                {
                    // The browser or the relay went away.
                }

                // The app closed the connection; stop sending to it too.
                client.Close();
                await upstream.ConfigureAwait(false);
                if (!process.WaitForExit(5000))
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }

                var error = (await stderr.ConfigureAwait(false)).Trim();
                if (process.HasExited && process.ExitCode != 0 && error.Length > 0)
                {
                    log($"Port {spec}: {error}");
                }
            }
        }
    }
}
