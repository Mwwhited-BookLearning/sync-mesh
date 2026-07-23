using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace SyncMesh.Sync.Tests;

// Real nats-server containers (hub + leaf), wired with an actual
// leafnodes config — the same topology validated manually for
// docs/adr/0002-nats-leaf-nodes-for-transport.md's 2026-07-23 Amendment.
// Deliberately not the Aspire.Hosting.NATS package or a mocked NATS client
// — this is the one place Phase 2's correctness actually gets proven.
public sealed class NatsLeafHubFixture : IAsyncLifetime
{
    private const string HubConfig = """
        port: 4222
        server_name: nats-hub

        jetstream {
            store_dir: "/data"
        }

        leafnodes {
            port: 7422
        }
        """;

    private const string LeafConfig = """
        port: 4222
        server_name: nats-leaf

        jetstream {
            store_dir: "/data"
        }

        leafnodes {
            remotes: [
                { url: "nats-leaf://nats-hub:7422" }
            ]
        }
        """;

    private INetwork _network = null!;
    private IContainer _hub = null!;
    private IContainer _leaf = null!;

    // Computed, not cached: Testcontainers can remap the host port across
    // a Stop/Start cycle on the same container, so these must always
    // reflect current reality. A real production hub's address wouldn't
    // change like this — it's purely a test-harness consideration.
    public string HubClientUrl => $"nats://{_hub.Hostname}:{_hub.GetMappedPublicPort(4222)}";
    public string LeafClientUrl => $"nats://{_leaf.Hostname}:{_leaf.GetMappedPublicPort(4222)}";

    public async Task InitializeAsync()
    {
        _network = new NetworkBuilder().Build();
        await _network.CreateAsync();

        // Fixed host port (not random) for the hub specifically — it gets
        // stopped/started to simulate an outage, and its address must stay
        // stable across that, exactly like a real hub's would.
        var hubHostPort = GetFreeTcpPort();
        _hub = new ContainerBuilder("nats:2-alpine")
            .WithNetwork(_network)
            .WithNetworkAliases("nats-hub")
            .WithResourceMapping(Encoding.UTF8.GetBytes(HubConfig), "/etc/nats/nats-server.conf")
            .WithCommand("-c", "/etc/nats/nats-server.conf")
            .WithPortBinding(hubHostPort, 4222)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server is ready"))
            .Build();
        await _hub.StartAsync();

        _leaf = new ContainerBuilder("nats:2-alpine")
            .WithNetwork(_network)
            .WithNetworkAliases("nats-leaf")
            .WithResourceMapping(Encoding.UTF8.GetBytes(LeafConfig), "/etc/nats/nats-server.conf")
            .WithCommand("-c", "/etc/nats/nats-server.conf")
            .WithPortBinding(4222, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server is ready"))
            .Build();
        await _leaf.StartAsync();
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // Simulates an extended outage: the hub is stopped (not just
    // disconnected) so the leaf's leaf-node connection actually drops and
    // must reconnect from scratch, exactly like the manual smoke test.
    public Task StopHubAsync() => _hub.StopAsync();

    public Task StartHubAsync() => _hub.StartAsync();

    public async Task DisposeAsync()
    {
        await _leaf.DisposeAsync();
        await _hub.DisposeAsync();
        await _network.DeleteAsync();
    }
}
