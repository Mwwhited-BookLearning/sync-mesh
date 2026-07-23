using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using SyncMesh.Contracts;
using SyncMesh.Daemon;
using SyncMesh.Daemon.Ipc;
using SyncMesh.Daemon.Nats;
using SyncMesh.EventStore;

namespace SyncMesh.Daemon.Tests;

// Minimal in-process host wiring the same services Program.cs registers,
// pointed at a unique temp SQLite file, pipe name, and NATS container per
// instance so tests can run in parallel without interfering with each
// other. A real nats-server container (not a mock) — see
// SyncMesh.Sync.Tests for the full leaf/hub topology; this is a single
// JetStream-enabled server standing in for "the daemon's local leaf node,"
// since these tests are about the IPC/write/read path, not forwarding.
internal sealed class DaemonTestHost : IAsyncDisposable
{
    public string PipeName { get; }
    public string DbPath { get; }

    private readonly IContainer _nats;
    private readonly ServiceProvider _provider;
    private readonly LocalIpcListener _listener;

    private DaemonTestHost(string pipeName, string dbPath, IContainer nats, ServiceProvider provider, LocalIpcListener listener)
    {
        PipeName = pipeName;
        DbPath = dbPath;
        _nats = nats;
        _provider = provider;
        _listener = listener;
    }

    public static async Task<DaemonTestHost> CreateAsync(Action<DaemonNatsOptions>? configureNats = null)
    {
        var nats = new ContainerBuilder("nats:2-alpine")
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
        await nats.StartAsync();
        var natsUrl = $"nats://{nats.Hostname}:{nats.GetMappedPublicPort(4222)}";

        var pipeName = $"syncmesh-test-{Guid.NewGuid():N}";
        var dbPath = Path.Combine(Path.GetTempPath(), $"syncmesh-daemon-test-{Guid.NewGuid():N}.db");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqliteEventStore($"Data Source={dbPath}");
        services.AddSingleton<HlcGenerator>();
        services.AddScoped<LocalEventWriter>();
        services.AddScoped<LocalEventReader>();
        services.Configure<DaemonOptions>(o =>
        {
            o.SiteId = "test-site";
            o.IpcPipeName = pipeName;
        });
        services.Configure<DaemonNatsOptions>(o =>
        {
            o.Url = natsUrl;
            configureNats?.Invoke(o);
        });
        services.AddSingleton(sp => new NatsConnection(new NatsOpts { Url = sp.GetRequiredService<IOptions<DaemonNatsOptions>>().Value.Url }));
        services.AddSingleton(sp => new NatsJSContext(sp.GetRequiredService<NatsConnection>()));
        services.AddSingleton<DaemonJetStreamSetup>();

        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<EventStoreDbContext>().Database.MigrateAsync();
        }

        await provider.GetRequiredService<DaemonJetStreamSetup>().StartAsync(CancellationToken.None);

        var listener = new LocalIpcListener(
            provider.GetRequiredService<IOptions<DaemonOptions>>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ILogger<LocalIpcListener>>());

        return new DaemonTestHost(pipeName, dbPath, nats, provider, listener);
    }

    public Task StartAsync() => _listener.StartAsync(CancellationToken.None);

    public LocalIpcClient CreateClient() => new(PipeName);

    // Simulates "daemon restarts": a fresh DbContext against the same
    // SQLite file, independent of anything this host's provider is holding.
    public EventStoreDbContext OpenFreshDbContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<EventStoreDbContext>();
        optionsBuilder.UseSqlite(
            $"Data Source={DbPath}",
            sqlite => sqlite.MigrationsAssembly(SqliteEventStoreServiceCollectionExtensions.MigrationsAssembly));
        return new EventStoreDbContext(optionsBuilder.Options);
    }

    public async ValueTask DisposeAsync()
    {
        await _listener.StopAsync(CancellationToken.None);
        await _provider.DisposeAsync();
        await _nats.DisposeAsync();

        SqliteConnection.ClearAllPools();
        if (File.Exists(DbPath))
        {
            File.Delete(DbPath);
        }
    }
}
