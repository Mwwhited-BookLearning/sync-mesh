using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SyncMesh.EventStore;

public static class SqliteEventStoreServiceCollectionExtensions
{
    public const string MigrationsAssembly = "SyncMesh.EventStore.Migrations.Sqlite";

    // Daemon tier (Tier 1): local, durable-only-while-recording buffer.
    public static IServiceCollection AddSqliteEventStore(this IServiceCollection services, string connectionString) =>
        services.AddDbContext<EventStoreDbContext>(options =>
            options.UseSqlite(connectionString, sqlite => sqlite.MigrationsAssembly(MigrationsAssembly)));
}
