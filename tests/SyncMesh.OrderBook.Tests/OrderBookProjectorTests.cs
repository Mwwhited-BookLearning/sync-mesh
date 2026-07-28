using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SyncMesh.Contracts.OrderBook;
using SyncMesh.EventStore;
using SyncMesh.OrderBook.Api;

namespace SyncMesh.OrderBook.Tests;

// Regression coverage for the 2026-07-28 review finding: the projector
// used to advance a strict watermark on the event's own (origin) HLC, so
// a replicated event that physically lands in this server's database
// later — but carries an earlier HLC than one already applied — was
// skipped forever. See OrderBookProjector's doc comment for the fix
// (trailing RecordedAtUtc lookback window instead of a single HLC
// cursor).
public sealed class OrderBookProjectorTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _serviceProvider;

    public OrderBookProjectorTests()
    {
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<EventStoreDbContext>(options => options.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<EventStoreDbContext>().Database.EnsureCreated();
    }

    public async ValueTask DisposeAsync()
    {
        await _serviceProvider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private static EventRecord MakePlacedRecord(
        Guid orderId, DateTimeOffset recordedAtUtc, long hlcTicks, string originSiteId = "site-a") =>
        new()
        {
            GlobalEventId = Guid.NewGuid(),
            StreamId = orderId,
            StreamVersion = 1,
            OriginSiteId = originSiteId,
            HlcPhysicalTicks = hlcTicks,
            HlcLogicalCounter = 0,
            RecordedAtUtc = recordedAtUtc,
            EventType = OrderBookEventTypes.OrderPlaced,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new OrderPlaced
            {
                Symbol = "ACME",
                Side = OrderSide.Buy,
                Price = 100m,
                Quantity = 10m,
                TraderId = "trader-1",
            }),
            PayloadSchemaVersion = 1,
        };

    private OrderBookProjector CreateProjector(IOrderBookStore store) =>
        new(_serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            store,
            Options.Create(new OrderBookApiOptions { ProjectionLookbackWindow = TimeSpan.FromMinutes(5) }),
            NullLogger<OrderBookProjector>.Instance);

    [Fact]
    public async Task WatermarkNoLongerSkipsAnEventWithALowerOriginHlc()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EventStoreDbContext>();
        var store = new OrderBookStore();
        var projector = CreateProjector(store);

        var now = DateTimeOffset.UtcNow;

        var fastOrderId = Guid.NewGuid();
        db.Events.Add(MakePlacedRecord(fastOrderId, now, hlcTicks: 1_000_000, originSiteId: "site-a"));
        await db.SaveChangesAsync();
        await projector.PollOnceAsync(CancellationToken.None);

        Assert.Single(store.Snapshot("ACME").Bids);

        // A second event lands a moment later in THIS server's database
        // (later RecordedAtUtc, so a real re-scan will see it) but carries
        // a LOWER origin HLC than the one just applied — e.g. it was
        // appended at its origin site before the fast one was, but took
        // longer to replicate here. The old strict-HLC watermark would
        // have queried "> 1_000_000" and skipped this row forever.
        var slowOrderId = Guid.NewGuid();
        db.Events.Add(MakePlacedRecord(slowOrderId, now.AddSeconds(1), hlcTicks: 500_000, originSiteId: "site-b"));
        await db.SaveChangesAsync();
        await projector.PollOnceAsync(CancellationToken.None);

        Assert.Equal(2, store.Snapshot("ACME").Bids.Count);
        Assert.Contains(store.Snapshot("ACME").Bids, o => o.OrderId == slowOrderId);
    }

    [Fact]
    public async Task ReapplyingAnEventAlreadySeenInTheWindow_DoesNotDuplicateIt()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EventStoreDbContext>();
        var store = new OrderBookStore();
        var projector = CreateProjector(store);

        var orderId = Guid.NewGuid();
        db.Events.Add(MakePlacedRecord(orderId, DateTimeOffset.UtcNow, hlcTicks: 1));
        await db.SaveChangesAsync();

        // Poll several times without any new events landing — the same
        // row stays inside the lookback window every time.
        await projector.PollOnceAsync(CancellationToken.None);
        await projector.PollOnceAsync(CancellationToken.None);
        await projector.PollOnceAsync(CancellationToken.None);

        Assert.Single(store.Snapshot("ACME").Bids);
    }
}
