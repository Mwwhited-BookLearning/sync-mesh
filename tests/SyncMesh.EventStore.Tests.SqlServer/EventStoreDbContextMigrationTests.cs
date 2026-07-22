using Microsoft.EntityFrameworkCore;
using SyncMesh.EventStore;
using Testcontainers.MsSql;

namespace SyncMesh.EventStore.Tests.SqlServer;

// Phase 0 exit criterion: EventStoreDbContext can migrate against
// SQL Server in isolation. See docs/05-implementation-guide.md, Phase 0.
// Requires a running Docker daemon.
public sealed class EventStoreDbContextMigrationTests : IAsyncLifetime
{
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private EventStoreDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<EventStoreDbContext>();
        optionsBuilder.UseSqlServer(
            _container.GetConnectionString(),
            sqlServer => sqlServer.MigrationsAssembly(SqlServerEventStoreServiceCollectionExtensions.MigrationsAssembly));
        return new EventStoreDbContext(optionsBuilder.Options);
    }

    [Fact]
    public async Task Migrate_CreatesSchema_AllowsInsertAndQuery()
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();

        var record = new EventRecord
        {
            GlobalEventId = Guid.NewGuid(),
            StreamId = Guid.NewGuid(),
            StreamVersion = 1,
            OriginSiteId = "site-a",
            HlcPhysicalTicks = DateTimeOffset.UtcNow.UtcTicks,
            HlcLogicalCounter = 0,
            RecordedAtUtc = DateTimeOffset.UtcNow,
            EventType = "TestEvent",
            PayloadJson = "{}",
            PayloadSchemaVersion = 1,
        };

        context.Events.Add(record);
        await context.SaveChangesAsync();

        var stored = await context.Events.SingleAsync(e => e.GlobalEventId == record.GlobalEventId);
        Assert.Equal(record.StreamId, stored.StreamId);
    }

    [Fact]
    public async Task Migrate_EnforcesUniqueStreamIdAndStreamVersion()
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();

        var streamId = Guid.NewGuid();
        context.Events.Add(new EventRecord
        {
            GlobalEventId = Guid.NewGuid(),
            StreamId = streamId,
            StreamVersion = 1,
            OriginSiteId = "site-a",
            HlcPhysicalTicks = 1,
            HlcLogicalCounter = 0,
            RecordedAtUtc = DateTimeOffset.UtcNow,
            EventType = "TestEvent",
            PayloadJson = "{}",
            PayloadSchemaVersion = 1,
        });
        await context.SaveChangesAsync();

        context.Events.Add(new EventRecord
        {
            GlobalEventId = Guid.NewGuid(),
            StreamId = streamId,
            StreamVersion = 1, // duplicate (StreamId, StreamVersion) — must be rejected
            OriginSiteId = "site-a",
            HlcPhysicalTicks = 2,
            HlcLogicalCounter = 0,
            RecordedAtUtc = DateTimeOffset.UtcNow,
            EventType = "TestEvent",
            PayloadJson = "{}",
            PayloadSchemaVersion = 1,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }
}
