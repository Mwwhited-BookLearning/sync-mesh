using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SyncMesh.EventStore;

public static class PostgresEventStoreServiceCollectionExtensions
{
    public const string MigrationsAssembly = "SyncMesh.EventStore.Migrations.Postgres";

    // Server tier (Tier 2/3): system of record, one config-selected provider.
    public static IServiceCollection AddPostgresEventStore(this IServiceCollection services, string connectionString) =>
        services.AddDbContext<EventStoreDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(MigrationsAssembly)));
}
