using DevContainers.Core.Configuration;
using DevContainers.Core.Forwarding;
using DevContainers.Core.Node;

namespace DevContainers.Core.Tests;

public class PortDetectionTests
{
    // Taken from a node:22 container: the app on 127.0.0.1:3000 (tcp), the inspector on 0.0.0.0:9229 (tcp), an
    // established connection, and in tcp6 a listener on :: port 5173, one on ::1 port 4200 and a v4-mapped 8080.
    private const string ProcNetTcp = """
          sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
           0: 0100007F:0BB8 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 41021 1 0000000000000000 100 0 0 10 0
           1: 00000000:240D 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 41020 1 0000000000000000 100 0 0 10 0
           2: 0100007F:0BB8 0100007F:D2F4 01 00000000:00000000 00:00000000 00000000  1000        0 41500 1 0000000000000000 20 4 30 10 -1
          sl  local_address                         remote_address                        st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
           0: 00000000000000000000000000000000:1645 00000000000000000000000000000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 41100 1 0000000000000000 100 0 0 10 0
           1: 00000000000000000000000001000000:1068 00000000000000000000000000000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 41101 1 0000000000000000 100 0 0 10 0
           2: 0000000000000000FFFF00000100007F:1F90 00000000000000000000000000000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 41102 1 0000000000000000 100 0 0 10 0
        """;

    [Fact]
    public void Parse_ReadsListeningSocketsOnly()
    {
        var listeners = ContainerPortScanner.Parse(ProcNetTcp);

        Assert.Equal(
            [
                new ContainerListener(3000, "127.0.0.1"),
                new ContainerListener(9229, "0.0.0.0"),
                new ContainerListener(5701, "::"),
                new ContainerListener(4200, "::1"),
                new ContainerListener(8080, "127.0.0.1"),
            ],
            listeners);
    }

    [Fact]
    public void Parse_IgnoresGarbage() =>
        Assert.Empty(ContainerPortScanner.Parse("cat: /proc/net/tcp6: No such file or directory\n   0: zz:0BB8 00000000:0000 0A\n"));

    [Theory]
    [InlineData(3000, "127.0.0.1")]
    [InlineData(9229, "127.0.0.1")]
    [InlineData(5701, "127.0.0.1")]
    [InlineData(4200, "::1")]
    [InlineData(1234, null)]
    public void ConnectAddress_PrefersIPv4Loopback(int port, string? expected) =>
        Assert.Equal(expected, ContainerPortScanner.ConnectAddress(ContainerPortScanner.Parse(ProcNetTcp), port));

    [Fact]
    public void ConnectAddress_UsesTheOnlyAddressItListensOn() =>
        Assert.Equal("172.17.0.2", ContainerPortScanner.ConnectAddress([new ContainerListener(80, "172.17.0.2")], 80));

    [Fact]
    public void Config_ReadsPortsToIgnore()
    {
        var config = DevContainerConfig.Parse("""
            {
              "image": "node",
              "portsAttributes": {
                "9229": { "onAutoForward": "ignore" },
                "40000-40002": { "onAutoForward": "ignore" },
                "3000": { "label": "App", "onAutoForward": "notify" }
              }
            }
            """);

        Assert.Equal([9229, 40000, 40001, 40002], config.IgnoredPorts.Order());
        Assert.True(config.AutoForwardPorts);
    }

    [Fact]
    public void Config_OtherPortsAttributesIgnore_TurnsOffDetection()
    {
        var config = DevContainerConfig.Parse("""{ "image": "node", "otherPortsAttributes": { "onAutoForward": "ignore" } }""");

        Assert.False(config.AutoForwardPorts);
        Assert.Empty(config.IgnoredPorts);
    }

    [Fact]
    public void NpmPackages_FindsAppsWithScripts_SkippingDependencies()
    {
        var root = Directory.CreateTempSubdirectory("npm-packages-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "package.json"), """{ "name": "root", "scripts": { "build": "tsc", "start": "node .", "dev": "tsx watch" } }""");
            Directory.CreateDirectory(Path.Combine(root, "web"));
            File.WriteAllText(Path.Combine(root, "web", "package.json"), """{ "scripts": { "serve": "vite" } }""");
            Directory.CreateDirectory(Path.Combine(root, "lib"));
            File.WriteAllText(Path.Combine(root, "lib", "package.json"), """{ "name": "lib" }""");
            Directory.CreateDirectory(Path.Combine(root, "node_modules", "dep"));
            File.WriteAllText(Path.Combine(root, "node_modules", "dep", "package.json"), """{ "scripts": { "test": "x" } }""");

            var packages = NpmPackages.Find(root);

            Assert.Equal(["root", "web (web)"], packages.Select(p => p.DisplayName(root)));
            Assert.Equal(["dev", "start", "build"], packages[0].OrderedScripts());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NpmPackages_TheSample_StartsWithDev()
    {
        var sample = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "typescript-node");
        var package = NpmPackages.Read(sample);

        Assert.NotNull(package);
        Assert.Equal("hello-node", package.Name);
        Assert.Equal("dev", package.OrderedScripts()[0]);
    }
}
