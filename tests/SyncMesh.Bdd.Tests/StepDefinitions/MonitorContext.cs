using System.Text;
using System.Text.Json;
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
using SyncMesh.EventStore;

namespace SyncMesh.Bdd.Tests.StepDefinitions;

// docs/bdd/features/remote-monitoring-tunnel.feature — "Passive monitoring
// works regardless of tunnel path availability." Real daemon stack (just
// enough of it: JetStream setup + MonitorPublisher, no IPC/writer needed
// for this scenario) against a real hub+leaf NATS pair, with a plain
// subscriber standing in for "the remote user" connected on the hub side —
// exactly where a real monitoring client would connect. See
// docs/00-design-document.md §4.5: monitoring rides ordinary NATS pub/sub,
// the same interest-graph routing already validated for leaf/gateway
// connections, with no separate infrastructure.
public sealed class MonitorContext : IAsyncDisposable
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
    private string _dbPath = null!;
    private readonly CancellationTokenSource _publisherCts = new();

    public string SiteId { get; private set; } = null!;
    public string InstanceId { get; private set; } = null!;

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
        SiteId = $"bdd-monitor-site-{testId}";
        InstanceId = $"bdd-monitor-instance-{testId}";

        _dbPath = Path.Combine(Path.GetTempPath(), $"syncmesh-bdd-monitor-{testId}.db");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqliteEventStore($"Data Source={_dbPath}");
        services.Configure<DaemonOptions>(o =>
        {
            o.SiteId = SiteId;
            o.InstanceId = InstanceId;
        });
        services.Configure<DaemonNatsOptions>(o =>
        {
            o.Url = LeafClientUrl;
            o.StreamName = $"DAEMON_EVENTS_{testId}";
            o.ConsumerName = $"FORWARDER_{testId}";
            o.SubjectPrefix = $"events{testId}";
        });
        services.Configure<DaemonMonitorOptions>(o =>
        {
            o.PublishInterval = TimeSpan.FromSeconds(1);
        });
        services.AddSingleton(sp => new NatsConnection(new NatsOpts { Url = sp.GetRequiredService<IOptions<DaemonNatsOptions>>().Value.Url }));
        services.AddSingleton(sp => new NatsJSContext(sp.GetRequiredService<NatsConnection>()));
        services.AddSingleton<DaemonJetStreamSetup>();
        services.AddSingleton<EventForwarder>();
        services.AddSingleton<MonitorPublisher>();

        _daemonProvider = services.BuildServiceProvider();
        using (var scope = _daemonProvider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<EventStoreDbContext>().Database.MigrateAsync();
        }

        await _daemonProvider.GetRequiredService<DaemonJetStreamSetup>().StartAsync(CancellationToken.None);
        _ = _daemonProvider.GetRequiredService<MonitorPublisher>().StartAsync(_publisherCts.Token);
    }

    private string HubClientUrl => $"nats://{_hub.Hostname}:{_hub.GetMappedPublicPort(4222)}";
    private string LeafClientUrl => $"nats://{_leaf.Hostname}:{_leaf.GetMappedPublicPort(4222)}";

    // Connects on the HUB side — exactly where a real remote-monitoring
    // client would reach in, never the daemon's leaf directly — and waits
    // for at least one status message to arrive.
    public async Task<DaemonStatus?> WaitForStatusAsync(TimeSpan timeout)
    {
        await using var connection = new NatsConnection(new NatsOpts { Url = HubClientUrl });
        var subject = $"monitor.{SiteId}.{InstanceId}.status";
        using var cts = new CancellationTokenSource(timeout);

        try
        {
            await foreach (var msg in connection.SubscribeAsync<byte[]>(subject, cancellationToken: cts.Token))
            {
                return JsonSerializer.Deserialize<DaemonStatus>(msg.Data!);
            }
        }
        catch (OperationCanceledException)
        {
        }

        return null;
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
        await _publisherCts.CancelAsync();
        await _daemonProvider.DisposeAsync();

        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        await _leaf.DisposeAsync();
        await _hub.DisposeAsync();
        await _network.DeleteAsync();
    }
}
