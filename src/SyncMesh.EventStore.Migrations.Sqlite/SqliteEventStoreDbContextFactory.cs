using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SyncMesh.EventStore.Migrations.Sqlite;

// Design-time only: lets `dotnet ef migrations add` run against this
// project in isolation, without a running Daemon host to supply DI.
public class SqliteEventStoreDbContextFactory : IDesignTimeDbContextFactory<EventStoreDbContext>
{
    public EventStoreDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EventStoreDbContext>();
        optionsBuilder.UseSqlite(
            "Data Source=design-time.db",
            sqlite => sqlite.MigrationsAssembly(SqliteEventStoreServiceCollectionExtensions.MigrationsAssembly));

        return new EventStoreDbContext(optionsBuilder.Options);
    }
}
