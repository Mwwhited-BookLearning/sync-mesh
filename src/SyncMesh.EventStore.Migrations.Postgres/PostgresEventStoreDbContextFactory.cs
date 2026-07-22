using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SyncMesh.EventStore.Migrations.Postgres;

// Design-time only: lets `dotnet ef migrations add` run against this
// project in isolation, without a running ServerHost to supply DI.
public class PostgresEventStoreDbContextFactory : IDesignTimeDbContextFactory<EventStoreDbContext>
{
    public EventStoreDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EventStoreDbContext>();
        optionsBuilder.UseNpgsql(
            "Host=localhost;Database=design_time;Username=design_time;Password=design_time",
            npgsql => npgsql.MigrationsAssembly(PostgresEventStoreServiceCollectionExtensions.MigrationsAssembly));

        return new EventStoreDbContext(optionsBuilder.Options);
    }
}
