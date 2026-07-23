using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace SyncMesh.Sync.Tests;

// N independent, standalone nats-server containers — one per simulated
// server-tier node. Deliberately NOT leaf/gateway-connected to each other at
// the NATS server-config level: Phase 3's server-mesh replication is
// point-to-point at the application layer (MeshForwarder dialing each
// configured peer's own URL directly), not native NATS gateway clustering —
// see docs/adr/0002-nats-leaf-nodes-for-transport.md's 2026-07-23 (Phase 3)
// Amendment. Each node just needs to be independently reachable on a stable
// port from every other node's test-harness client.
public sealed class ServerMeshFixture : IAsyncLifetime
{
    private INetwork _network = null!;
    private readonly List<IContainer> _nodes = [];

    public async Task InitializeAsync()
    {
        _network = new NetworkBuilder().Build();
        await _network.CreateAsync();
    }

    // Fixed host port (not random) for every node — some tests stop/start a
    // node mid-test to simulate an outage, and its address must stay stable
    // across that, exactly like NatsLeafHubFixture's hub.
    public async Task<MeshNode> CreateNodeAsync(string alias)
    {
        var config = $$"""
            port: 4222
            server_name: {{alias}}
            jetstream {
                store_dir: "/data"
            }
            """;

        var hostPort = GetFreeTcpPort();
        var container = new ContainerBuilder("nats:2-alpine")
            .WithNetwork(_network)
            .WithNetworkAliases(alias)
            .WithResourceMapping(Encoding.UTF8.GetBytes(config), "/etc/nats/nats-server.conf")
            .WithCommand("-c", "/etc/nats/nats-server.conf")
            .WithPortBinding(hostPort, 4222)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server is ready"))
            .Build();
        await container.StartAsync();
        _nodes.Add(container);

        return new MeshNode(container);
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async Task DisposeAsync()
    {
        foreach (var node in _nodes)
        {
            await node.DisposeAsync();
        }

        await _network.DeleteAsync();
    }
}

public sealed class MeshNode(IContainer container)
{
    // Computed, not cached — Testcontainers can remap the host port across
    // a Stop/Start cycle on the same container (see NatsLeafHubFixture).
    // Irrelevant here since we always use a fixed host port, but kept
    // computed for consistency/safety.
    public string ClientUrl => $"nats://{container.Hostname}:{container.GetMappedPublicPort(4222)}";

    public Task StopAsync() => container.StopAsync();

    public Task StartAsync() => container.StartAsync();
}
