using Microsoft.EntityFrameworkCore;
using SyncMesh.EventStore;
using Testcontainers.PostgreSql;

namespace SyncMesh.EventStore.Tests.Postgres;

// Phase 0 exit criterion: EventStoreDbContext can migrate against
// PostgreSQL in isolation. See docs/05-implementation-guide.md, Phase 0.
// Requires a running Docker daemon.
public sealed class EventStoreDbContextMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18.3").Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private EventStoreDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<EventStoreDbContext>();
        optionsBuilder.UseNpgsql(
            _container.GetConnectionString(),
            npgsql => npgsql.MigrationsAssembly(PostgresEventStoreServiceCollectionExtensions.MigrationsAssembly));
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

    [Fact]
    public async Task Migrate_EventLineage_RoundTripsAndRejectsDuplicatePair()
    {
        var childId = Guid.NewGuid();
        var parentId = Guid.NewGuid();

        await using (var context = CreateContext())
        {
            await context.Database.MigrateAsync();
            context.EventLineages.Add(new EventLineage { ChildEventId = childId, ParentEventId = parentId });
            await context.SaveChangesAsync();

            var stored = await context.EventLineages.SingleAsync(l => l.ChildEventId == childId && l.ParentEventId == parentId);
            Assert.Equal(parentId, stored.ParentEventId);
        }

        // A fresh context (a different DbContext instance, as a separate
        // request would use) — the duplicate must be rejected by the DB's
        // composite PK, not merely by the change tracker's identity map.
        await using var duplicateContext = CreateContext();
        duplicateContext.EventLineages.Add(new EventLineage { ChildEventId = childId, ParentEventId = parentId });
        await Assert.ThrowsAsync<DbUpdateException>(() => duplicateContext.SaveChangesAsync());
    }

    [Fact]
    public async Task Migrate_EventLineage_DoesNotEnforceForeignKeyToEventRecord()
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();

        // ParentEventId intentionally matches no row in Events — lineage is
        // descriptive-only and deliberately has no DB-enforced FK (see
        // docs/adr/0006-event-lineage-descriptive-provenance.md).
        context.EventLineages.Add(new EventLineage { ChildEventId = Guid.NewGuid(), ParentEventId = Guid.NewGuid() });
        await context.SaveChangesAsync(); // must not throw
    }
}
