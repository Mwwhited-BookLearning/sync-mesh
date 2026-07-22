using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SyncMesh.EventStore;

namespace SyncMesh.EventStore.Tests.Sqlite;

// Phase 0 exit criterion: EventStoreDbContext can migrate against SQLite in
// isolation. See docs/05-implementation-guide.md, Phase 0.
public sealed class EventStoreDbContextMigrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"syncmesh-sqlite-{Guid.NewGuid():N}.db");

    private EventStoreDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<EventStoreDbContext>();
        optionsBuilder.UseSqlite(
            $"Data Source={_dbPath}",
            sqlite => sqlite.MigrationsAssembly(SqliteEventStoreServiceCollectionExtensions.MigrationsAssembly));
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

    public void Dispose()
    {
        // Microsoft.Data.Sqlite pools connections by default, which keeps a
        // file handle open past the DbContext's disposal.
        SqliteConnection.ClearAllPools();

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}
