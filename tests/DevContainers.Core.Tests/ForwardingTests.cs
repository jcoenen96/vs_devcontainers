using System.Net;
using System.Net.Sockets;
using System.Text;
using DevContainers.Core.Forwarding;
using DevContainers.Core.Processes;
using DevContainers.Core.Runtime;

namespace DevContainers.Core.Tests;

public class ForwardingTests
{
    [Theory]
    [InlineData("3000", "localhost", 3000)]
    [InlineData(" 5080 ", "localhost", 5080)]
    [InlineData("db:5432", "db", 5432)]
    [InlineData("localhost:8080", "localhost", 8080)]
    public void PortSpec_Parses(string value, string host, int port)
    {
        Assert.True(PortSpec.TryParse(value, out var spec));
        Assert.Equal(new PortSpec(host, port), spec);
    }

    [Theory]
    [InlineData("")]
    [InlineData("web")]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData(":80")]
    public void PortSpec_RejectsInvalid(string value) => Assert.False(PortSpec.TryParse(value, out _));

    [Fact]
    public void Start_SkipsPortInUse()
    {
        using var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Start();
        var port = ((IPEndPoint)busy.LocalEndpoint).Port;

        using var forwarder = new PortForwarder(new ContainerRuntime(ContainerRuntimeKind.Docker, "docker", "27.0"), "abc", _ => { });
        var result = Assert.Single(forwarder.Start([new PortSpec("localhost", port)]));

        Assert.False(result.IsForwarded);
        Assert.Contains("already in use", result.Problem);
    }

    /// <summary>
    /// Forwards port 8080 of real containers: Alpine (BusyBox nc as the relay) and Debian (bash). Runs only with
    /// DEVCONTAINERS_INTEGRATION=1.
    /// </summary>
    [Theory]
    [InlineData("docker.io/library/alpine:3.20", "nc -lk -p 8080 -e cat", "nc")]
    [InlineData("docker.io/library/python:3.12-slim", "mkdir /www && echo hello > /www/index.html && exec python3 -m http.server 8080 -d /www", "bash")]
    public async Task Forwards_ToRealContainer(string image, string server, string relay)
    {
        if (Environment.GetEnvironmentVariable("DEVCONTAINERS_INTEGRATION") != "1")
        {
            return;
        }

        var runtime = await new ContainerRuntimeDetector(new ProcessRunner()).DetectAsync(
            Enum.TryParse<ContainerRuntimePreference>(Environment.GetEnvironmentVariable("DEVCONTAINERS_RUNTIME"), ignoreCase: true, out var preference) ? preference : ContainerRuntimePreference.Auto,
            null,
            default);
        var run = await new ProcessRunner().RunAsync(new ProcessSpec(runtime.ExecutablePath, ["run", "-d", "--rm", image, "sh", "-c", server]), null, default);
        Assert.True(run.Succeeded, run.StandardError);
        var id = run.StandardOutput.Trim();

        try
        {
            var log = new List<string>();
            using var forwarder = new PortForwarder(runtime, id, log.Add);
            var forwarded = Assert.Single(forwarder.Start([new PortSpec("localhost", 8080)]));
            Assert.True(forwarded.IsForwarded, forwarded.Problem);

            string answer;
            if (relay == "nc")
            {
                // An echo server: what goes in comes back.
                answer = await EchoAsync(8080, "ping");
                Assert.Equal("ping", answer);
                log.Clear();
                Assert.Equal("pong", await EchoAsync(8080, "pong"));
            }
            else
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                answer = await RetryAsync(() => http.GetStringAsync("http://localhost:8080/"));
                Assert.Equal("hello", answer.Trim());
                log.Clear();

                // Several requests in a row, each over its own connection.
                for (var i = 0; i < 3; i++)
                {
                    using var fresh = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                    Assert.Equal("hello", (await fresh.GetStringAsync("http://127.0.0.1:8080/")).Trim());
                }
            }

            Assert.Empty(log);
        }
        finally
        {
            await new ProcessRunner().RunAsync(new ProcessSpec(runtime.ExecutablePath, ["rm", "-f", id]), null, default);
        }
    }

    private static async Task<string> EchoAsync(int port, string text)
    {
        return await RetryAsync(async () =>
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(text + "\n"));
            var buffer = new byte[64];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var read = await stream.ReadAsync(buffer, timeout.Token);
            if (read == 0)
            {
                throw new IOException("The connection closed without an answer.");
            }

            return Encoding.ASCII.GetString(buffer, 0, read).Trim();
        });
    }

    private static async Task<T> RetryAsync<T>(Func<Task<T>> action)
    {
        // The server in the container may still be starting.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception) when (attempt < 10)
            {
                await Task.Delay(1000);
            }
        }
    }
}
