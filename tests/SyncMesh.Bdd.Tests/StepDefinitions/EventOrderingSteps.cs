using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SyncMesh.EventStore;

namespace SyncMesh.Bdd.Tests.StepDefinitions;

// Reqnroll gives each binding class constructor-injected access to any
// plain class used as a parameter, scoped to one scenario — this is that
// shared per-scenario state for the "ordered replay" scenario.
public sealed class EventOrderingContext : IDisposable
{
    public EventRecord? SiteARecord { get; set; }
    public EventRecord? SiteCRecord { get; set; }
    public EventStoreDbContext? ServerBStore { get; set; }
    public string? DbPath { get; set; }

    public void Dispose()
    {
        ServerBStore?.Dispose();
        if (DbPath is not null)
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(DbPath))
            {
                File.Delete(DbPath);
            }
        }
    }
}

// docs/bdd/features/event-ordering-and-idempotency.feature —
// "Events from two sites are ordered correctly on replay despite
// out-of-order arrival". Testable in isolation (no network): this proves
// the schema/query design (Phase 0) plus HLC values (Phase 1) produce
// correct replay order regardless of insertion order.
[Binding]
public sealed class EventOrderingSteps(EventOrderingContext context)
{
    [Given("Server B receives an event from Site A with HLC value earlier than an event from Site C")]
    public void GivenServerBReceivesAnEventFromSiteAWithHlcValueEarlierThanAnEventFromSiteC()
    {
        var baseTicks = DateTimeOffset.UtcNow.UtcTicks;

        context.SiteARecord = new EventRecord
        {
            GlobalEventId = Guid.NewGuid(),
            StreamId = Guid.NewGuid(),
            StreamVersion = 1,
            OriginSiteId = "Site A",
            HlcPhysicalTicks = baseTicks,
            HlcLogicalCounter = 0,
            RecordedAtUtc = DateTimeOffset.UtcNow,
            EventType = "TestEvent",
            PayloadJson = "{}",
            PayloadSchemaVersion = 1,
        };

        context.SiteCRecord = new EventRecord
        {
            GlobalEventId = Guid.NewGuid(),
            StreamId = Guid.NewGuid(),
            StreamVersion = 1,
            OriginSiteId = "Site C",
            HlcPhysicalTicks = baseTicks + TimeSpan.FromSeconds(1).Ticks,
            HlcLogicalCounter = 0,
            RecordedAtUtc = DateTimeOffset.UtcNow,
            EventType = "TestEvent",
            PayloadJson = "{}",
            PayloadSchemaVersion = 1,
        };
    }

    [When("Server B receives the Site C event before the Site A event")]
    public async Task WhenServerBReceivesTheSiteCEventBeforeTheSiteAEvent()
    {
        context.DbPath = Path.Combine(Path.GetTempPath(), $"syncmesh-ordering-{Guid.NewGuid():N}.db");
        var optionsBuilder = new DbContextOptionsBuilder<EventStoreDbContext>();
        optionsBuilder.UseSqlite(
            $"Data Source={context.DbPath}",
            sqlite => sqlite.MigrationsAssembly(SqliteEventStoreServiceCollectionExtensions.MigrationsAssembly));
        context.ServerBStore = new EventStoreDbContext(optionsBuilder.Options);
        await context.ServerBStore.Database.MigrateAsync();

        // Arrival order: Site C first, then Site A — out of HLC order.
        context.ServerBStore.Events.Add(context.SiteCRecord!);
        await context.ServerBStore.SaveChangesAsync();

        context.ServerBStore.Events.Add(context.SiteARecord!);
        await context.ServerBStore.SaveChangesAsync();
    }

    [Then("replaying Server B's event store produces the events in HLC order, not arrival order")]
    public async Task ThenReplayingServerBsEventStoreProducesTheEventsInHlcOrderNotArrivalOrder()
    {
        var replayed = await context.ServerBStore!.Events
            .OrderBy(e => e.HlcPhysicalTicks)
            .ThenBy(e => e.HlcLogicalCounter)
            .Select(e => e.OriginSiteId)
            .ToListAsync();

        CollectionAssert.AreEqual(new[] { "Site A", "Site C" }, replayed);
    }
}
