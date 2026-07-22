using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SyncMesh.EventStore.Migrations.SqlServer;

// Design-time only: lets `dotnet ef migrations add` run against this
// project in isolation, without a running ServerHost to supply DI.
public class SqlServerEventStoreDbContextFactory : IDesignTimeDbContextFactory<EventStoreDbContext>
{
    public EventStoreDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EventStoreDbContext>();
        optionsBuilder.UseSqlServer(
            "Server=localhost;Database=design_time;TrustServerCertificate=True;Integrated Security=True",
            sqlServer => sqlServer.MigrationsAssembly(SqlServerEventStoreServiceCollectionExtensions.MigrationsAssembly));

        return new EventStoreDbContext(optionsBuilder.Options);
    }
}
