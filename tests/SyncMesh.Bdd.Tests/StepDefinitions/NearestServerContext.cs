using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using NATS.Client.Core;

namespace SyncMesh.Bdd.Tests.StepDefinitions;

// docs/bdd/features/nearest-neighbor-sync.feature — proves the daemon's
// leaf-node connection to whatever "nearest server" is configured (on-prem
// or cloud are the same mechanism, just a label/URL), and that switching
// between them is a config change with no daemon code touched. Reuses the
// real hub+leaf container pattern validated in SyncMesh.Sync.Tests, scaled
// down to what these scenarios need: proof of a working leaf connection,
// not the forwarder/responder round trip.
public sealed class NearestServerContext : IAsyncDisposable
{
    private INetwork? _network;
    private readonly Dictionary<string, IContainer> _hubs = [];
    private IContainer? _currentLeaf;

    public string? LastStartedHubLabel { get; private set; }

    public async Task<string> StartNearestServerAsync(string label)
    {
        _network ??= await CreateNetworkAsync();

        var config = $$"""
            port: 4222
            server_name: {{label}}
            jetstream {
                store_dir: "/data"
            }
            leafnodes {
                port: 7422
            }
            """;

        var hub = new ContainerBuilder("nats:2-alpine")
            .WithNetwork(_network)
            .WithNetworkAliases(label)
            .WithResourceMapping(Encoding.UTF8.GetBytes(config), "/etc/nats/nats-server.conf")
            .WithCommand("-c", "/etc/nats/nats-server.conf")
            .WithPortBinding(4222, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server is ready"))
            .Build();
        await hub.StartAsync();
        _hubs.Add(label, hub);
        LastStartedHubLabel = label;

        return label;
    }

    // Connects (or reconnects) the daemon's leaf node to the named nearest
    // server. Tearing down and rebuilding the leaf container simulates a
    // config change + restart — no daemon *code* is involved either way,
    // which is exactly the property "no code changes" is testing.
    public async Task ConnectDaemonLeafToNearestServerAsync(string hubLabel)
    {
        if (_currentLeaf is not null)
        {
            await _currentLeaf.DisposeAsync();
        }

        // No inbound `leafnodes { port: ... }` block here at all — only an
        // outbound `remotes:` entry. The daemon's leaf never listens for
        // inbound leaf connections, only dials out — see the "firewall/NAT"
        // scenario, which is an architectural property of this config, not
        // something to prove at runtime.
        var config = $$"""
            port: 4222
            server_name: daemon-leaf
            jetstream {
                store_dir: "/data"
            }
            leafnodes {
                remotes: [
                    { url: "nats-leaf://{{hubLabel}}:7422" }
                ]
            }
            """;

        var leaf = new ContainerBuilder("nats:2-alpine")
            .WithNetwork(_network)
            .WithResourceMapping(Encoding.UTF8.GetBytes(config), "/etc/nats/nats-server.conf")
            .WithCommand("-c", "/etc/nats/nats-server.conf")
            .WithPortBinding(4222, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server is ready"))
            .Build();
        await leaf.StartAsync();
        _currentLeaf = leaf;
    }

    // Proves the leaf connection is real (not just "both containers are
    // running"): publish on the leaf, subscribe on the hub, confirm
    // receipt — exactly the technique validated manually for ADR-0002.
    public async Task<bool> EventForwardingWorksAsync(string hubLabel)
    {
        var hub = _hubs[hubLabel];
        var hubUrl = $"nats://{hub.Hostname}:{hub.GetMappedPublicPort(4222)}";
        var leafUrl = $"nats://{_currentLeaf!.Hostname}:{_currentLeaf.GetMappedPublicPort(4222)}";

        await using var hubConn = new NatsConnection(new NatsOpts { Url = hubUrl });
        var subject = $"probe.{Guid.NewGuid():N}";
        var received = false;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var subscribeTask = Task.Run(async () =>
        {
            await foreach (var msg in hubConn.SubscribeAsync<string>(subject, cancellationToken: cts.Token))
            {
                received = true;
                break;
            }
        }, cts.Token);

        await Task.Delay(500, CancellationToken.None); // let the subscription land
        await using var leafConn = new NatsConnection(new NatsOpts { Url = leafUrl });
        await leafConn.PublishAsync(subject, "probe");

        try
        {
            await subscribeTask;
        }
        catch (OperationCanceledException)
        {
        }

        return received;
    }

    private static async Task<INetwork> CreateNetworkAsync()
    {
        var network = new NetworkBuilder().Build();
        await network.CreateAsync();
        return network;
    }

    public async ValueTask DisposeAsync()
    {
        if (_currentLeaf is not null)
        {
            await _currentLeaf.DisposeAsync();
        }

        foreach (var hub in _hubs.Values)
        {
            await hub.DisposeAsync();
        }

        if (_network is not null)
        {
            await _network.DeleteAsync();
        }
    }
}
