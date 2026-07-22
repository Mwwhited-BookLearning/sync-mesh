using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SyncMesh.EventStore;

public static class SqlServerEventStoreServiceCollectionExtensions
{
    public const string MigrationsAssembly = "SyncMesh.EventStore.Migrations.SqlServer";

    // Server tier (Tier 2/3): system of record, one config-selected provider.
    public static IServiceCollection AddSqlServerEventStore(this IServiceCollection services, string connectionString) =>
        services.AddDbContext<EventStoreDbContext>(options =>
            options.UseSqlServer(connectionString, sqlServer => sqlServer.MigrationsAssembly(MigrationsAssembly)));
}
