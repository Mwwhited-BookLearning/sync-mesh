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
using SyncMesh.Daemon.Ipc;
using SyncMesh.Daemon.Nats;
using SyncMesh.EventStore;
using SyncMesh.ServerHost.Nats;

namespace SyncMesh.Bdd.Tests.StepDefinitions;

// docs/bdd/features/event-ordering-and-idempotency.feature — "Leaf node
// reconnect-sync gap is explicitly tested, not assumed safe." Full
// daemon-side (LocalEventWriter + DaemonJetStreamSetup + EventForwarder)
// and server-side (ApplyResponder) stacks against a real hub+leaf NATS
// pair, mirroring SyncMesh.Sync.Tests.DaemonToServerSyncTests — the same
// property, bound here as an executable BDD acceptance criterion per
// CLAUDE.md rather than only referenced from another test project.
public sealed class LeafReconnectContext : IAsyncDisposable
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
    private ServiceProvider _daemonProvider = null!;
    private ServiceProvider _serverProvider = null!;
    private readonly List<string> _dbPaths = [];
    private readonly CancellationTokenSource _forwarderCts = new();
    private readonly CancellationTokenSource _responderCts = new();

    public LocalEventWriter Writer { get; private set; } = null!;
    public NatsJSContext DaemonJetStream { get; private set; } = null!;
    public DaemonNatsOptions DaemonNatsOptions { get; private set; } = null!;
    public EventStoreDbContext ServerDb { get; private set; } = null!;

    public async Task StartAsync()
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
        var applySubject = $"server.apply.request.{testId}";

        var daemonDbPath = Path.Combine(Path.GetTempPath(), $"syncmesh-bdd-leafreconnect-daemon-{testId}.db");
        _dbPaths.Add(daemonDbPath);
        var daemonServices = new ServiceCollection();
        daemonServices.AddLogging();
        daemonServices.AddSqliteEventStore($"Data Source={daemonDbPath}");
        daemonServices.AddSingleton<HlcGenerator>();
        daemonServices.Configure<DaemonOptions>(o => o.SiteId = $"bdd-leaf-{testId}");
        daemonServices.Configure<DaemonNatsOptions>(o =>
        {
            o.Url = LeafClientUrl;
            o.StreamName = $"DAEMON_EVENTS_{testId}";
            o.ConsumerName = $"FORWARDER_{testId}";
            o.SubjectPrefix = $"events{testId}";
            o.ApplyRequestSubject = applySubject;
        });
        daemonServices.AddSingleton(sp => new NatsConnection(new NatsOpts { Url = sp.GetRequiredService<IOptions<DaemonNatsOptions>>().Value.Url }));
        daemonServices.AddSingleton(sp => new NatsJSContext(sp.GetRequiredService<NatsConnection>()));
        daemonServices.AddScoped<LocalEventWriter>();
        daemonServices.AddSingleton<DaemonJetStreamSetup>();
        daemonServices.AddSingleton<EventForwarder>();

        _daemonProvider = daemonServices.BuildServiceProvider();
        using (var scope = _daemonProvider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<EventStoreDbContext>().Database.MigrateAsync();
        }

        var serverDbPath = Path.Combine(Path.GetTempPath(), $"syncmesh-bdd-leafreconnect-server-{testId}.db");
        _dbPaths.Add(serverDbPath);
        var serverServices = new ServiceCollection();
        serverServices.AddLogging();
        serverServices.AddSqliteEventStore($"Data Source={serverDbPath}");
        serverServices.Configure<ServerNatsOptions>(o =>
        {
            o.Url = HubClientUrl;
            o.ApplyRequestSubject = applySubject;
        });
        serverServices.AddSingleton(sp => new NatsConnection(new NatsOpts { Url = sp.GetRequiredService<IOptions<ServerNatsOptions>>().Value.Url }));
        serverServices.AddSingleton(sp => new NatsJSContext(sp.GetRequiredService<NatsConnection>()));
        serverServices.AddSingleton<ApplyResponder>();

        _serverProvider = serverServices.BuildServiceProvider();
        using (var scope = _serverProvider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<EventStoreDbContext>().Database.MigrateAsync();
        }

        Writer = _daemonProvider.GetRequiredService<LocalEventWriter>();
        DaemonJetStream = _daemonProvider.GetRequiredService<NatsJSContext>();
        DaemonNatsOptions = _daemonProvider.GetRequiredService<IOptions<DaemonNatsOptions>>().Value;
        ServerDb = _serverProvider.GetRequiredService<EventStoreDbContext>();

        await _daemonProvider.GetRequiredService<DaemonJetStreamSetup>().StartAsync(CancellationToken.None);
        _ = _daemonProvider.GetRequiredService<EventForwarder>().StartAsync(_forwarderCts.Token);
        _ = _serverProvider.GetRequiredService<ApplyResponder>().StartAsync(_responderCts.Token);
    }

    private string HubClientUrl => $"nats://{_hub.Hostname}:{_hub.GetMappedPublicPort(4222)}";
    private string LeafClientUrl => $"nats://{_leaf.Hostname}:{_leaf.GetMappedPublicPort(4222)}";

    public Task StopHubAsync() => _hub.StopAsync();

    public Task StartHubAsync() => _hub.StartAsync();

    public async Task<EventRecord?> WaitUntilAppliedAsync(Guid globalEventId, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        EventRecord? applied = null;
        while (applied is null && DateTimeOffset.UtcNow < deadline)
        {
            applied = await ServerDb.Events.FirstOrDefaultAsync(e => e.GlobalEventId == globalEventId);
            if (applied is null)
            {
                await Task.Delay(200);
            }
        }
        return applied;
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
        await _forwarderCts.CancelAsync();
        await _responderCts.CancelAsync();

        await _daemonProvider.DisposeAsync();
        await _serverProvider.DisposeAsync();

        SqliteConnection.ClearAllPools();
        foreach (var path in _dbPaths)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        await _leaf.DisposeAsync();
        await _hub.DisposeAsync();
        await _network.DeleteAsync();
    }
}
