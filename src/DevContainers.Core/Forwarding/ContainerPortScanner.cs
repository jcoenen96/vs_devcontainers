using System.Globalization;
using DevContainers.Core.Processes;
using DevContainers.Core.Runtime;

namespace DevContainers.Core.Forwarding;

/// <summary>A TCP port something listens on inside the container, and the address it listens on.</summary>
/// <param name="Port">The port.</param>
/// <param name="Address">The address, such as <c>0.0.0.0</c>, <c>127.0.0.1</c>, <c>::</c> or <c>::1</c>.</param>
public sealed record ContainerListener(int Port, string Address)
{
    public bool IsAnyAddress => this.Address is "0.0.0.0" or "::";

    public bool IsIPv4Loopback => this.Address.StartsWith("127.", StringComparison.Ordinal);

    public bool IsIPv6Loopback => this.Address == "::1";
}

/// <summary>
/// Finds the ports that programs in a running container listen on, from <c>/proc/net/tcp</c> and
/// <c>/proc/net/tcp6</c> (the container's own network), so they can be forwarded without being listed, as VS Code
/// does, and so the relay connects to the address the program actually listens on.
/// </summary>
public static class ContainerPortScanner
{
    // tcp6 is missing where IPv6 is off; cat then fails, so end with true.
    public const string Command = "cat /proc/net/tcp /proc/net/tcp6 2>/dev/null; true";

    private const string Listen = "0A";

    public static async Task<IReadOnlyList<ContainerListener>> ScanAsync(IProcessRunner runner, ContainerRuntime runtime, string containerId, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessSpec(runtime.ExecutablePath, ["exec", containerId, "sh", "-c", Command]), null, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? Parse(result.StandardOutput) : [];
    }

    /// <summary>The listening sockets in the text of /proc/net/tcp and /proc/net/tcp6.</summary>
    public static IReadOnlyList<ContainerListener> Parse(string text)
    {
        var listeners = new List<ContainerListener>();
        foreach (var line in text.Split('\n'))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4 || fields[3] != Listen)
            {
                continue;
            }

            var local = fields[1].Split(':');
            if (local.Length != 2 || !int.TryParse(local[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var port) || port == 0)
            {
                continue;
            }

            var address = DecodeAddress(local[0]);
            if (address is not null)
            {
                listeners.Add(new ContainerListener(port, address));
            }
        }

        return listeners.Distinct().ToList();
    }

    /// <summary>
    /// The address to connect to inside the container for a port: <c>127.0.0.1</c> when something listens there or
    /// on every address, <c>::1</c> when only IPv6 loopback (Node's "localhost" on newer versions), else the address
    /// it listens on. Null when nothing listens on the port.
    /// </summary>
    public static string? ConnectAddress(IEnumerable<ContainerListener> listeners, int port)
    {
        var onPort = listeners.Where(l => l.Port == port).ToList();
        if (onPort.Count == 0)
        {
            return null;
        }

        if (onPort.Any(l => l.IsAnyAddress || l.IsIPv4Loopback))
        {
            return "127.0.0.1";
        }

        return onPort.Any(l => l.IsIPv6Loopback) ? "::1" : onPort[0].Address;
    }

    // IPv4 is one little-endian 32-bit word; IPv6 is four of them.
    private static string? DecodeAddress(string hex)
    {
        if (hex.Length != 8 && hex.Length != 32)
        {
            return null;
        }

        var bytes = new byte[hex.Length / 2];
        for (var word = 0; word < hex.Length / 8; word++)
        {
            if (!uint.TryParse(hex.AsSpan(word * 8, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            BitConverter.TryWriteBytes(bytes.AsSpan(word * 4, 4), value);
            if (!BitConverter.IsLittleEndian)
            {
                bytes.AsSpan(word * 4, 4).Reverse();
            }
        }

        var address = new System.Net.IPAddress(bytes);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.ToString();
    }
}
