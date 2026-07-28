using System.Diagnostics;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using SyncMesh.Contracts;
using SyncMesh.Daemon;
using SyncMesh.Daemon.Nats;
using SyncMesh.Daemon.Tunnel;
using SyncMesh.EventStore;
using SyncMesh.ServerHost.Nats;
using SyncMesh.ServerHost.Tunnel;
using SyncMesh.TunnelClient;

namespace SyncMesh.Bdd.Tests.StepDefinitions;

// docs/bdd/features/remote-monitoring-tunnel.feature — the 4 Phase 5
// tunnel scenarios (direct connection, relay fallback, and both
// cross-failure-isolation scenarios). Real TunnelAgent/TunnelRelay/
// EchoServer always; real NATS hub+leaf + daemon/server event-sync stack
// added only for the two cross-failure scenarios, which specifically need
// to prove independence from event-sync. See
// docs/adr/0007-custom-reverse-tunnel-mechanism.md.
public sealed class TunnelContext : IAsyncDisposable
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

    private ServiceProvider? _daemonProvider;
    private ServiceProvider? _serverProvider;
    private EchoServer? _echo;
    private string? _daemonDbPath;
    private string? _serverDbPath;
    private readonly CancellationTokenSource _tunnelAgentCts = new();
    private readonly CancellationTokenSource _tunnelRelayCts = new();
    private CancellationTokenSource? _forwarderCts;
    private CancellationTokenSource? _responderCts;

    private INetwork? _network;
    private IContainer? _hub;
    private IContainer? _leaf;

    private TunnelAgent _tunnelAgent = null!;
    private TunnelRelay _tunnelRelay = null!;
    private int _relayClientPort;
    private int _directListenPort;

    public string SiteId { get; private set; } = null!;
    public string InstanceId { get; private set; } = null!;
    public bool LastUsedRelay { get; private set; }
    public TimeSpan LastConnectElapsed { get; private set; }
    public TimeSpan DirectAttemptTimeout { get; } = TimeSpan.FromSeconds(1);

    private System.Net.Sockets.NetworkStream? _lastStream;

    // Tunnel mechanism only — no NATS/event-sync. Used by the direct and
    // relay-fallback scenarios.
    public async Task StartTunnelOnlyAsync()
    {
        var testId = Guid.NewGuid().ToString("N")[..8];
        SiteId = $"bdd-tunnel-site-{testId}";
        InstanceId = $"bdd-tunnel-instance-{testId}";

        _echo = EchoServer.Start();
        _directListenPort = GetFreeTcpPort();
        var agentPort = GetFreeTcpPort();
        _relayClientPort = GetFreeTcpPort();

        var daemonServices = new ServiceCollection();
        daemonServices.AddLogging();
        daemonServices.Configure<DaemonOptions>(o => { o.SiteId = SiteId; o.InstanceId = InstanceId; });
        daemonServices.Configure<TunnelAgentOptions>(o =>
        {
            o.DirectListenPort = _directListenPort;
            o.LocalTargetEndpoint = $"localhost:{_echo.Port}";
            o.RelayUrl = $"localhost:{agentPort}";
        });
        daemonServices.AddSingleton<TunnelAgent>();
        var daemonProvider = daemonServices.BuildServiceProvider();
        _daemonProvider = daemonProvider;
        _tunnelAgent = daemonProvider.GetRequiredService<TunnelAgent>();

        var serverServices = new ServiceCollection();
        serverServices.AddLogging();
        serverServices.Configure<TunnelRelayOptions>(o =>
        {
            o.AgentListenPort = agentPort;
            o.ClientListenPort = _relayClientPort;
        });
        serverServices.AddSingleton<TunnelRelay>();
        var serverProvider = serverServices.BuildServiceProvider();
        _serverProvider = serverProvider;
        _tunnelRelay = serverProvider.GetRequiredService<TunnelRelay>();

        _ = _tunnelAgent.StartAsync(_tunnelAgentCts.Token);
        _ = _tunnelRelay.StartAsync(_tunnelRelayCts.Token);

        await WaitUntilAsync(() => _tunnelAgent.ConnectedToRelay);
    }

    // Full combined stack — NATS hub+leaf, daemon event-sync, server
    // event-sync, and the tunnel mechanism. Used by the two cross-failure
    // scenarios, which need both mechanisms running side by side
    // specifically to prove their independence.
    public async Task StartFullStackAsync()
    {
        _network = new NetworkBuilder().Build();
        await _network.CreateAsync();

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

        var testId = Guid.NewGuid().ToString("N")[..8];
        SiteId = $"bdd-tunnel-site-{testId}";
        InstanceId = $"bdd-tunnel-instance-{testId}";

        _echo = EchoServer.Start();
        _directListenPort = GetFreeTcpPort();
        var agentPort = GetFreeTcpPort();
        _relayClientPort = GetFreeTcpPort();

        _daemonDbPath = Path.Combine(Path.GetTempPath(), $"syncmesh-bdd-tunnel-daemon-{testId}.db");
        var daemonServices = new ServiceCollection();
        daemonServices.AddLogging();
        daemonServices.AddSqliteEventStore($"Data Source={_daemonDbPath}");
        daemonServices.AddSingleton<HlcGenerator>();
        daemonServices.Configure<DaemonOptions>(o => { o.SiteId = SiteId; o.InstanceId = InstanceId; });
        daemonServices.Configure<DaemonNatsOptions>(o =>
        {
            o.Url = LeafClientUrl;
            o.StreamName = $"DAEMON_EVENTS_{testId}";
            o.ConsumerName = $"FORWARDER_{testId}";
            o.SubjectPrefix = $"events{testId}";
            o.ApplyRequestSubject = $"server.apply.request.{testId}";
        });
        daemonServices.Configure<TunnelAgentOptions>(o =>
        {
            o.DirectListenPort = _directListenPort;
            o.LocalTargetEndpoint = $"localhost:{_echo.Port}";
            o.RelayUrl = $"localhost:{agentPort}";
        });
        daemonServices.AddSingleton(sp => new NatsConnection(new NatsOpts { Url = sp.GetRequiredService<IOptions<DaemonNatsOptions>>().Value.Url }));
        daemonServices.AddSingleton(sp => new NatsJSContext(sp.GetRequiredService<NatsConnection>()));
        daemonServices.AddScoped<SyncMesh.Daemon.Ipc.LocalEventWriter>();
        daemonServices.AddSingleton<DaemonJetStreamSetup>();
        daemonServices.AddSingleton<EventForwarder>();
        daemonServices.AddSingleton<TunnelAgent>();

        var daemonProvider = daemonServices.BuildServiceProvider();
        _daemonProvider = daemonProvider;
        using (var scope = daemonProvider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<EventStoreDbContext>().Database.MigrateAsync();
        }
        _tunnelAgent = daemonProvider.GetRequiredService<TunnelAgent>();

        _serverDbPath = Path.Combine(Path.GetTempPath(), $"syncmesh-bdd-tunnel-server-{testId}.db");
        var serverServices = new ServiceCollection();
        serverServices.AddLogging();
        serverServices.AddSqliteEventStore($"Data Source={_serverDbPath}");
        serverServices.Configure<ServerNatsOptions>(o =>
        {
            o.Url = HubClientUrl;
            o.ApplyRequestSubject = $"server.apply.request.{testId}";
        });
        serverServices.Configure<TunnelRelayOptions>(o =>
        {
            o.AgentListenPort = agentPort;
            o.ClientListenPort = _relayClientPort;
        });
        serverServices.AddSingleton(sp => new NatsConnection(new NatsOpts { Url = sp.GetRequiredService<IOptions<ServerNatsOptions>>().Value.Url }));
        serverServices.AddSingleton(sp => new NatsJSContext(sp.GetRequiredService<NatsConnection>()));
        serverServices.AddSingleton<ApplyResponder>();
        serverServices.AddSingleton<TunnelRelay>();

        var serverProvider = serverServices.BuildServiceProvider();
        _serverProvider = serverProvider;
        using (var scope = serverProvider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<EventStoreDbContext>().Database.MigrateAsync();
        }
        _tunnelRelay = serverProvider.GetRequiredService<TunnelRelay>();

        await daemonProvider.GetRequiredService<DaemonJetStreamSetup>().StartAsync(CancellationToken.None);
        _forwarderCts = new CancellationTokenSource();
        _ = daemonProvider.GetRequiredService<EventForwarder>().StartAsync(_forwarderCts.Token);
        _responderCts = new CancellationTokenSource();
        _ = serverProvider.GetRequiredService<ApplyResponder>().StartAsync(_responderCts.Token);
        _ = _tunnelAgent.StartAsync(_tunnelAgentCts.Token);
        _ = _tunnelRelay.StartAsync(_tunnelRelayCts.Token);

        await WaitUntilAsync(() => _tunnelAgent.ConnectedToRelay);
    }

    public Task StopTunnelRelayAsync() => _tunnelRelayCts.CancelAsync();

    public Task StopEventHubAsync() => _hub!.StopAsync();

    public async Task<Guid> AppendEventAsync()
    {
        using var scope = _daemonProvider!.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<SyncMesh.Daemon.Ipc.LocalEventWriter>();
        var response = await writer.AppendAsync(new SyncMesh.Contracts.Ipc.AppendEventRequest
        {
            StreamId = Guid.NewGuid(),
            EventType = "BddTunnelScenario",
            PayloadJson = "{}",
        }, CancellationToken.None);
        return response.GlobalEventId;
    }

    public async Task<bool> WasAppliedAsync(Guid globalEventId, TimeSpan timeout)
    {
        var db = _serverProvider!.GetRequiredService<EventStoreDbContext>();
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await db.Events.AnyAsync(e => e.GlobalEventId == globalEventId))
            {
                return true;
            }
            await Task.Delay(200);
        }
        return false;
    }

    // Attempts a tunnel connection. When simulateBlockedDirect is true, the
    // "direct" target is a deliberately closed port — representing
    // "blocked by firewall/NAT" from the remote client's point of view.
    public async Task ConnectAsync(bool simulateBlockedDirect)
    {
        var directPort = simulateBlockedDirect ? GetFreeTcpPort() /* nothing listens here */ : _directListenPort;
        var stopwatch = Stopwatch.StartNew();
        var result = await TunnelConnector.ConnectAsync(
            "localhost", directPort,
            "localhost", _relayClientPort,
            SiteId, InstanceId,
            DirectAttemptTimeout, CancellationToken.None);
        stopwatch.Stop();

        LastUsedRelay = result.UsedRelay;
        LastConnectElapsed = stopwatch.Elapsed;
        _lastStream = result.Stream;
    }

    public async Task AssertRoundTripsAsync()
    {
        var payload = Guid.NewGuid().ToByteArray();
        await _lastStream!.WriteAsync(payload);
        var echoed = new byte[payload.Length];
        var offset = 0;
        while (offset < echoed.Length)
        {
            var read = await _lastStream.ReadAsync(echoed.AsMemory(offset));
            Assert.IsTrue(read > 0, "Tunnel connection closed before the full echo was received.");
            offset += read;
        }
        Assert.IsTrue(payload.AsSpan().SequenceEqual(echoed), "Echoed bytes did not match what was sent through the tunnel.");
    }

    private string HubClientUrl => $"nats://{_hub!.Hostname}:{_hub.GetMappedPublicPort(4222)}";
    private string LeafClientUrl => $"nats://{_leaf!.Hostname}:{_leaf.GetMappedPublicPort(4222)}";

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
        Assert.IsTrue(condition(), "Tunnel agent did not connect to its relay within the timeout.");
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await _tunnelAgentCts.CancelAsync();
        if (!_tunnelRelayCts.IsCancellationRequested)
        {
            await _tunnelRelayCts.CancelAsync();
        }
        if (_forwarderCts is not null)
        {
            await _forwarderCts.CancelAsync();
        }
        if (_responderCts is not null)
        {
            await _responderCts.CancelAsync();
        }

        if (_echo is not null)
        {
            await _echo.DisposeAsync();
        }
        if (_daemonProvider is not null)
        {
            await _daemonProvider.DisposeAsync();
        }
        if (_serverProvider is not null)
        {
            await _serverProvider.DisposeAsync();
        }

        SqliteConnection.ClearAllPools();
        if (_daemonDbPath is not null && File.Exists(_daemonDbPath))
        {
            File.Delete(_daemonDbPath);
        }
        if (_serverDbPath is not null && File.Exists(_serverDbPath))
        {
            File.Delete(_serverDbPath);
        }

        if (_leaf is not null)
        {
            await _leaf.DisposeAsync();
        }
        if (_hub is not null)
        {
            await _hub.DisposeAsync();
        }
        if (_network is not null)
        {
            await _network.DeleteAsync();
        }
    }
}
