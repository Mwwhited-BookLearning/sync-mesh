using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SyncMesh.Contracts;
using SyncMesh.Daemon;
using SyncMesh.Daemon.Ipc;
using SyncMesh.EventStore;

namespace SyncMesh.Daemon.Tests;

// Minimal in-process host wiring the same services Program.cs registers,
// pointed at a unique temp SQLite file and pipe name per instance so tests
// can run in parallel without interfering with each other.
internal sealed class DaemonTestHost : IAsyncDisposable
{
    public string PipeName { get; } = $"syncmesh-test-{Guid.NewGuid():N}";
    public string DbPath { get; } = Path.Combine(Path.GetTempPath(), $"syncmesh-daemon-test-{Guid.NewGuid():N}.db");

    private readonly ServiceProvider _provider;
    private readonly LocalIpcListener _listener;

    public DaemonTestHost()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqliteEventStore($"Data Source={DbPath}");
        services.AddSingleton<HlcGenerator>();
        services.AddScoped<LocalEventWriter>();
        services.AddScoped<LocalEventReader>();
        services.Configure<DaemonOptions>(o =>
        {
            o.SiteId = "test-site";
            o.IpcPipeName = PipeName;
        });

        _provider = services.BuildServiceProvider();

        using (var scope = _provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<EventStoreDbContext>().Database.Migrate();
        }

        _listener = new LocalIpcListener(
            _provider.GetRequiredService<IOptions<DaemonOptions>>(),
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _provider.GetRequiredService<ILogger<LocalIpcListener>>());
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

        SqliteConnection.ClearAllPools();
        if (File.Exists(DbPath))
        {
            File.Delete(DbPath);
        }
    }
}
