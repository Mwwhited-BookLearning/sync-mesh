using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NATS.Client.JetStream;
using SyncMesh.Contracts;
using SyncMesh.Daemon;
using SyncMesh.Daemon.Ipc;
using SyncMesh.Daemon.Nats;
using SyncMesh.EventStore;

namespace SyncMesh.Bdd.Tests.StepDefinitions;

// Per-scenario state for local-durability.feature. A single real
// nats-server container (JetStream, no leaf/hub split needed here — the
// leaf-to-hub topology itself is proven in SyncMesh.Sync.Tests) stands in
// for "the daemon's embedded NATS leaf node." "The nearest server
// acknowledges receipt" is simulated by acking the JetStream consumer
// directly, since this feature is about the local buffer's own behavior,
// not the forwarder/responder round trip.
public sealed class LocalDurabilityContext : IAsyncDisposable
{
    private IContainer? _nats;
    private ServiceProvider? _provider;
    private string? _dbPath;
    private INatsJSConsumer? _consumer;

    public LocalEventWriter Writer { get; private set; } = null!;
    public LocalEventReader Reader { get; private set; } = null!;
    public NatsJSContext JetStream { get; private set; } = null!;
    public DaemonNatsOptions NatsOptions { get; private set; } = null!;
    public AppendEventResponse? LastAppendResponse { get; set; }
    public ReadEventsResponse? LastReadResponse { get; set; }
    public Guid StreamId { get; } = Guid.NewGuid();

    public async Task StartAsync()
    {
        _nats = new ContainerBuilder("nats:2-alpine")
            .WithResourceMapping(Encoding.UTF8.GetBytes("""
                port: 4222
                jetstream {
                    store_dir: "/data"
                }
                """), "/etc/nats/nats-server.conf")
            .WithCommand("-c", "/etc/nats/nats-server.conf")
            .WithPortBinding(4222, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server is ready"))
            .Build();
        await _nats.StartAsync();

        var natsUrl = $"nats://{_nats.Hostname}:{_nats.GetMappedPublicPort(4222)}";
        var testId = Guid.NewGuid().ToString("N")[..8];
        _dbPath = Path.Combine(Path.GetTempPath(), $"syncmesh-bdd-durability-{testId}.db");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqliteEventStore($"Data Source={_dbPath}");
        services.AddSingleton<HlcGenerator>();
        services.Configure<DaemonOptions>(o => o.SiteId = $"bdd-{testId}");
        services.Configure<DaemonNatsOptions>(o =>
        {
            o.Url = natsUrl;
            o.StreamName = $"DAEMON_EVENTS_{testId}";
            o.ConsumerName = $"CONSUMER_{testId}";
            o.SubjectPrefix = $"events{testId}";
        });
        services.AddSingleton(sp => new NatsConnection(new NatsOpts { Url = natsUrl }));
        services.AddSingleton(sp => new NatsJSContext(sp.GetRequiredService<NatsConnection>()));
        services.AddScoped<LocalEventWriter>();
        services.AddScoped<LocalEventReader>();
        services.AddSingleton<DaemonJetStreamSetup>();

        _provider = services.BuildServiceProvider();

        using (var scope = _provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<EventStoreDbContext>().Database.MigrateAsync();
        }

        await _provider.GetRequiredService<DaemonJetStreamSetup>().StartAsync(CancellationToken.None);

        JetStream = _provider.GetRequiredService<NatsJSContext>();
        NatsOptions = _provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DaemonNatsOptions>>().Value;
        Writer = _provider.GetRequiredService<LocalEventWriter>();
        Reader = _provider.GetRequiredService<LocalEventReader>();
        _consumer = await JetStream.GetConsumerAsync(NatsOptions.StreamName, NatsOptions.ConsumerName);
    }

    public async Task<long> GetBufferedMessageCountAsync()
    {
        var info = await JetStream.GetStreamAsync(NatsOptions.StreamName);
        return (long)info.Info.State.Messages;
    }

    public async Task<(long MaxBytes, long MaxMsgs, TimeSpan MaxAge)> GetStreamLimitsAsync()
    {
        var info = await JetStream.GetStreamAsync(NatsOptions.StreamName);
        return (info.Info.Config.MaxBytes, info.Info.Config.MaxMsgs, info.Info.Config.MaxAge);
    }

    // Stands in for "the nearest server acknowledges receipt": fetches the
    // next buffered message and acks it, exactly as the real forwarder
    // would once the hub confirms idempotent apply.
    public async Task SimulateUpstreamAckAsync()
    {
        await foreach (var msg in _consumer!.ConsumeAsync<byte[]>())
        {
            await msg.AckAsync();
            break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        if (_nats is not null)
        {
            await _nats.DisposeAsync();
        }

        SqliteConnection.ClearAllPools();
        if (_dbPath is not null && File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}
